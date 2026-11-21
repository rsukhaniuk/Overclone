/* GPL-2.0-or-later. Read-only geometry from the actual direct PDF writer. */
#include "oc_pdftex_origin_writer.h"
#include "oc_synctex_producer.h"
#include <cmath>
#include <cstdio>
#include <cstring>

#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#define OC_READ_API EMSCRIPTEN_KEEPALIVE
#else
#define OC_READ_API
#endif

namespace {
constexpr int MaxPages = 512;
constexpr uint32_t MaxBytes = 131072;
constexpr size_t MaxAttributeBytes = 4096;

// Pinned SyncTeX query ABI1 normalizes a raw sp coordinate by this divisor.
constexpr double QuerySpPerBp = 65781.76;

struct Box {
    double left, bottom, right, top;
};

struct Page {
    int mag;
    double bpPerSp, e, f;
    Box media;
    bool dictionary, explicitMedia;
    unsigned builtinCoordinates;
};

struct Tokens {
    const unsigned char *current;
    const unsigned char *end;

    static bool space(unsigned char c)
    {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    }

    void skip()
    {
        while (current != end && space(*current)) ++current;
    }

    bool finished()
    {
        skip();
        return current == end;
    }

    bool take(const char *token)
    {
        skip();
        const auto length = std::strlen(token);
        if (static_cast<size_t>(end - current) < length ||
            std::memcmp(current, token, length) != 0) return false;
        const auto *after = current + length;
        if (*token == '/' && after != end && !space(*after) && *after != '[') return false;
        current = after;
        return true;
    }

    bool number(double &value, bool integral = false)
    {
        skip();
        const auto *start = current;
        const bool negative = current != end && *current == '-';
        if (current != end && (*current == '+' || *current == '-')) ++current;

        auto decimal = [this] { return current != end && *current >= '0' && *current <= '9'; };
        value = 0;
        bool anyDigit = false;
        while (decimal()) {
            anyDigit = true;
            value = value * 10 + (*current++ - '0');
            if (value > 1000000) { current = start; return false; }
        }
        if (current != end && *current == '.') {
            if (integral) { current = start; return false; }
            ++current;
            double place = 0.1;
            while (decimal()) {
                anyDigit = true;
                value += (*current++ - '0') * place;
                place *= 0.1;
            }
        }
        if (negative) value = -value;
        const bool valid = anyDigit && std::abs(value) <= 1000000 &&
            (current == end || space(*current) || *current == ']' || *current == '/');
        if (!valid) current = start;
        return valid;
    }

    bool box(Box &box)
    {
        return take("[") && number(box.left) && number(box.bottom) &&
            number(box.right) && number(box.top) && take("]") &&
            box.left < box.right && box.bottom < box.top;
    }
};

Page pages[MaxPages]{};
int count, headerMag, rootPages;
bool headerSeen, rootSeen, active, invalidSequence, invalidFrame, opaqueContent;
char encoded[MaxBytes]{};
uint32_t encodedLength;

bool bounded(double value)
{
    return std::isfinite(value) && std::abs(value) <= 1000000;
}

bool sealed(uint32_t token)
{
    return oc_synctex_length(token) && headerSeen && rootSeen && count &&
        count == rootPages && !active && !invalidSequence;
}

bool pageAttributes(Page &page, const unsigned char *bytes, size_t length, bool suppressBuiltin)
{
    if (!length) return !suppressBuiltin;
    if (!bytes || length > MaxAttributeBytes) return false;

    Tokens tokens{bytes, bytes + length};
    unsigned keys = 0;
    Box crop{};
    while (!tokens.finished()) {
        unsigned key;
        if (tokens.take("/MediaBox")) {
            key = 1;
            if (!tokens.box(page.media)) return false;
        } else if (tokens.take("/CropBox")) {
            key = 2;
            if (!tokens.box(crop)) return false;
        } else if (tokens.take("/Rotate")) {
            key = 4;
            double rotation;
            if (!tokens.number(rotation, true) || (rotation != 0 && rotation != 90 &&
                rotation != 180 && rotation != 270)) return false;
        } else return false;
        if (keys & key) return false;
        keys |= key;
    }

    page.explicitMedia = (keys & 1) != 0;
    if (page.explicitMedia != suppressBuiltin) return false;
    // The explicitly admitted page must keep the complete CropBox in its
    // declared MediaBox. For built-in pages the renderer clips its usual view.
    return !page.explicitMedia || !(keys & 2) ||
        (page.media.left <= crop.left && page.media.bottom <= crop.bottom &&
         page.media.right >= crop.right && page.media.top >= crop.top);
}

bool neutralColors(const unsigned char *bytes, size_t length)
{
    if (!length) return true;
    if (!bytes || length > MaxAttributeBytes) return false;
    Tokens tokens{bytes, bytes + length};
    while (!tokens.finished()) {
        unsigned operands = 0;
        double value;
        while (tokens.number(value)) {
            if (++operands > 4 || value < 0 || value > 1) return false;
        }
        const bool colorOperator = (operands == 1 && (tokens.take("g") || tokens.take("G"))) ||
            (operands == 3 && (tokens.take("rg") || tokens.take("RG"))) ||
            (operands == 4 && (tokens.take("k") || tokens.take("K")));
        if (!colorOperator) return false;
        if (tokens.current != tokens.end && !Tokens::space(*tokens.current)) return false;
    }
    return true;
}
}

void ocpdftexoriginmap(int pdf, int mag, int unit, int x, int y)
{
    if (!pdf) return;
    if (headerSeen || mag <= 0 || unit != 1 || x != 0 || y != 0) {
        invalidSequence = true;
        return;
    }
    headerSeen = true;
    headerMag = mag;
}

void ocpdftexoriginbegin(int page, int origin_h, int origin_v, int mag, int hundred_bp)
{
    if (active || page != count + 1 || count >= MaxPages || mag <= 0 || hundred_bp <= 0) {
        invalidSequence = true;
        return;
    }
    active = true;
    const double bpPerSp = (100.0 / hundred_bp) * (mag / 1000.0);
    pages[count] = {mag, bpPerSp, -origin_h * bpPerSp, origin_v * bpPerSp, {}, false, false, 0};
}

void oc_pdftex_origin_page_bytes(int page, const unsigned char *bytes, size_t length, int suppress_builtin)
{
    if (!active || page != count + 1 || pages[count].dictionary) {
        invalidSequence = true;
        return;
    }
    auto &p = pages[count];
    p.dictionary = true;
    if (!pageAttributes(p, bytes, length, suppress_builtin != 0)) invalidFrame = true;
}

void ocpdftexoriginbp(int printed, int decimal_digits)
{
    // The two observations are taken from the same fixed-point values sent to
    // the actual built-in MediaBox printer. Other bp prints have no effect.
    if (!active) return;
    auto &p = pages[count];
    if (!p.dictionary || p.explicitMedia || p.builtinCoordinates >= 2) return;
    if (decimal_digits < 0 || decimal_digits > 5 || printed <= 0) {
        invalidFrame = true;
        return;
    }
    const double value = printed / std::pow(10.0, decimal_digits);
    if (++p.builtinCoordinates == 1) p.media.right = value;
    else p.media.top = value;
}

void ocpdftexoriginend(int page)
{
    if (!active || page != count + 1 || !pages[count].dictionary) {
        invalidSequence = true;
        return;
    }
    const auto &p = pages[count];
    if (!p.explicitMedia && p.builtinCoordinates != 2) invalidFrame = true;
    active = false;
    ++count;
}

void ocpdftexoriginroot(int total_pages, size_t attributes_length)
{
    if (active || rootSeen || total_pages != count) {
        invalidSequence = true;
        return;
    }
    rootSeen = true;
    rootPages = total_pages;
    if (attributes_length) invalidFrame = true;
}

void oc_pdftex_origin_literal_bytes(const unsigned char *bytes, size_t length)
{
    if (active && !neutralColors(bytes, length)) opaqueContent = true;
}

void ocpdftexoriginunsafe(void)
{
    opaqueContent = true;
}

extern "C" OC_READ_API uint32_t oc_pdftex_origin_abi(void)
{
    return 1;
}

extern "C" OC_READ_API uint32_t oc_pdftex_legacy_zero_origin(uint32_t token)
{
    // Preserve the previous zero-origin map mode for existing opaque package
    // channels only when every actual page keeps its built-in geometry. Any
    // explicitly selected or ambiguous MediaBox forbids this admission.
    if (!opaqueContent || invalidFrame || !sealed(token)) return 0;
    for (int index = 0; index < count; ++index) {
        const auto &p = pages[index];
        if (p.explicitMedia || p.builtinCoordinates != 2 || p.mag != headerMag ||
            p.e != 0 || !bounded(p.media.right) || !bounded(p.media.top) ||
            std::abs(p.f - p.media.top) > .501) return 0;
    }
    return 1;
}

extern "C" OC_READ_API uint32_t oc_synctex_geometry_length(uint32_t token)
{
    if (!sealed(token) || oc_pdftex_legacy_zero_origin(token)) return 0;
    if (encodedLength) return encodedLength;

    int written = std::snprintf(encoded, sizeof(encoded), "{\"schemaVersion\":1,\"pages\":[");
    if (written <= 0) return 0;
    uint32_t used = static_cast<uint32_t>(written);
    const double queryBpPerSp = (headerMag / 1000.0) / QuerySpPerBp;

    for (int index = 0; index < count; ++index) {
        const auto &p = pages[index];
        // The origin and units come solely from native PDF and SyncTeX stream
        // state. The dictionary contributes only the inverse-mapped extent.
        const double scale = p.bpPerSp / queryBpPerSp;
        const Box box{(p.media.left - p.e) / scale, (p.f - p.media.top) / scale,
                      (p.media.right - p.e) / scale, (p.f - p.media.bottom) / scale};
        if (!opaqueContent && !invalidFrame && p.mag == headerMag && bounded(scale) && scale > 0 &&
            bounded(p.e) && bounded(p.f) && bounded(box.left) && bounded(box.bottom) &&
            bounded(box.right) && bounded(box.top) &&
            box.left < box.right && box.bottom < box.top) {
            written = std::snprintf(encoded + used, sizeof(encoded) - used,
                "%s{\"page\":%d,\"status\":\"available\",\"reason\":\"none\","
                "\"writerToPdfRaw\":[%.17g,0,0,%.17g,%.17g,%.17g],"
                "\"writerPageBoxBp\":[%.17g,%.17g,%.17g,%.17g]}", index ? "," : "",
                index + 1, scale, -scale, p.e, p.f, box.left, box.bottom, box.right, box.top);
        } else {
            written = std::snprintf(encoded + used, sizeof(encoded) - used,
                "%s{\"page\":%d,\"status\":\"unsupported\",\"reason\":\"out-of-domain\","
                "\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}", index ? "," : "", index + 1);
        }
        if (written <= 0 || static_cast<uint32_t>(written) >= sizeof(encoded) - used) return 0;
        used += static_cast<uint32_t>(written);
    }
    if (used + 2 >= sizeof(encoded)) return 0;
    std::memcpy(encoded + used, "]}", 3);
    encodedLength = used + 2;
    return encodedLength;
}

extern "C" OC_READ_API uint32_t oc_synctex_geometry_copy(
    uint32_t token, unsigned char *destination, uint32_t capacity)
{
    const uint32_t size = oc_synctex_geometry_length(token);
    if (!size || !destination || capacity < size || capacity > MaxBytes) return 0;
    std::memcpy(destination, encoded, size);
    return size;
}
