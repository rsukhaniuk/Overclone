/* ABI5 only. Derivation and supported domain: writer-geometry.md. */
#include "writer_geometry.h"
#include "lua_maps_api.h"
#include <cmath>
#include <cstdio>
#include <cstring>

namespace {
constexpr uint32_t MaxPages = 512, MaxBytes = 131072;
struct Page {
    int64_t width, height, synctexHeight;
    double spToRaw, spToBp;
    bool available;
};
Page pages[MaxPages] = {};
uint32_t count = 0;
bool active = false, sequenceValid = true, unsafe = false;
char geometry[MaxBytes] = {};
uint32_t geometrySize = 0;
bool bounded(double x) { return std::isfinite(x) && std::abs(x) <= 1000000; }
bool space(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f'; }
/* Deliberately restrictive grammar. No names with escapes, references,
 * comments, nested objects, duplicate keys, operators or exponent notation.
 * Inspect at most 4096 bytes; never allocate document-controlled memory. */
struct Attributes {
    const char *p, *end;
    void skip() { while (p != end && space(*p)) ++p; }
    bool literal(const char *s) {
        skip();
        size_t n = std::strlen(s);
        if (static_cast<size_t>(end - p) < n || std::memcmp(p, s, n)) return false;
        // PDF names end at whitespace or a delimiter, not at our prefix.
        // Do not mistake /Rotate90 or /CropBoxExtra for whitelisted keys.
        if (s[0] == '/' && p + n != end && !space(p[n]) &&
            !std::strchr("()<>[]{}/%", p[n])) return false;
        p += n;
        return true;
    }
    bool number(double &value) {
        skip();
        bool negative = false, digit = false;
        if (p != end && (*p == '-' || *p == '+')) negative = *p++ == '-';
        value = 0;
        while (p != end && *p >= '0' && *p <= '9') {
            digit = true;
            value = value * 10 + (*p++ - '0');
            if (value > 1000000) return false;
        }
        if (p != end && *p == '.') {
            ++p;
            double factor = 0.1;
            while (p != end && *p >= '0' && *p <= '9') {
                digit = true;
                value += (*p++ - '0') * factor;
                factor *= 0.1;
            }
        }
        if (negative) value = -value;
        return digit && bounded(value) && (p == end || space(*p) || *p == ']' || *p == '/');
    }
    bool valid() {
        bool crop = false, rotation = false;
        for (;;) {
            skip();
            if (p == end) return true;
            if (*p != '/') return false;
            if (literal("/CropBox") && !crop) {
                crop = true;
                double box[4];
                if (!literal("[")) return false;
                for (double &n : box) if (!number(n)) return false;
                if (!literal("]") || box[0] >= box[2] || box[1] >= box[3]) return false;
            } else if (literal("/Rotate") && !rotation) {
                rotation = true;
                double n;
                if (!number(n) || (n != 0 && n != 90 && n != 180 && n != 270)) return false;
            } else return false;
        }
    }
};
}
extern "C" void oc_lua_geometry_unsafe(void) { unsafe = true; }
extern "C" void oc_lua_geometry_attributes(const char *bytes, size_t length)
{
    if (!length) return;
    if (!bytes || length > 4096) { unsafe = true; return; }
    Attributes a{bytes, bytes + length};
    if (!a.valid()) unsafe = true;
}
extern "C" void oc_lua_geometry_begin(int page, int64_t width, int64_t height,
    int64_t synctex_height, double sp_to_raw, double sp_to_bp, int supported_direction)
{
    if (active || page <= 0 || page > static_cast<int>(MaxPages) ||
        static_cast<uint32_t>(page) != count + 1) {
        sequenceValid = false;
        return;
    }
    active = true;
    Page &p = pages[count];
    p = {width, height, synctex_height, sp_to_raw, sp_to_bp, false};
    const double scale = sp_to_raw / sp_to_bp;
    p.available = supported_direction && width > 0 && height > 0 && sp_to_raw > 0 &&
        sp_to_bp > 0 && bounded(scale) && scale > 0 &&
        bounded(width * sp_to_bp) && bounded(height * sp_to_bp) &&
        bounded(synctex_height * sp_to_raw) && bounded(synctex_height * sp_to_bp) &&
        bounded((synctex_height - height) * sp_to_bp);
}
extern "C" void oc_lua_geometry_observe(int64_t synctex_height, int supported_direction)
{
    if (!active) { unsafe = true; return; }
    Page &p = pages[count];
    if (p.synctexHeight != synctex_height || !supported_direction) p.available = false;
}
extern "C" void oc_lua_geometry_end(int page, int64_t width, int64_t height)
{
    if (!active || page <= 0 || static_cast<uint32_t>(page) != count + 1) {
        sequenceValid = false;
        return;
    }
    Page &p = pages[count];
    if (p.width != width || p.height != height) p.available = false;
    active = false;
    ++count;
}
extern "C" uint32_t oc_synctex_geometry_length(uint32_t token)
{
    // The unchanged raw seal is the ONLY authority, after actual native close,
    // postamble, native success and output quota checks. Never seal independently.
    if (!oc_synctex_length(token) || !sequenceValid || active || !count) return 0;
    if (geometrySize) return geometrySize;
    int n = std::snprintf(geometry, sizeof(geometry), "{\"schemaVersion\":1,\"pages\":[");
    if (n <= 0) return 0;
    uint32_t used = static_cast<uint32_t>(n);
    for (uint32_t i = 0; i < count; ++i) {
        const Page &p = pages[i];
        if (p.available && !unsafe) {
            double s = p.spToRaw / p.spToBp;
            n = std::snprintf(geometry + used, sizeof(geometry) - used,
                "%s{\"page\":%u,\"status\":\"available\",\"reason\":\"none\","
                "\"writerToPdfRaw\":[%.17g,0,0,%.17g,0,%.17g],"
                "\"writerPageBoxBp\":[0,%.17g,%.17g,%.17g]}", i ? "," : "", i + 1,
                s, -s, p.synctexHeight * p.spToRaw,
                (p.synctexHeight - p.height) * p.spToBp, p.width * p.spToBp,
                p.synctexHeight * p.spToBp);
        } else {
            n = std::snprintf(geometry + used, sizeof(geometry) - used,
                "%s{\"page\":%u,\"status\":\"unsupported\",\"reason\":\"out-of-domain\","
                "\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}", i ? "," : "", i + 1);
        }
        if (n <= 0 || static_cast<uint32_t>(n) >= sizeof(geometry) - used) return 0;
        used += static_cast<uint32_t>(n);
    }
    if (used + 2 >= sizeof(geometry)) return 0;
    std::memcpy(geometry + used, "]}", 3);
    geometrySize = used + 2;
    return geometrySize;
}
extern "C" uint32_t oc_synctex_geometry_copy(uint32_t token, unsigned char *dst, uint32_t capacity)
{
    uint32_t n = oc_synctex_geometry_length(token);
    if (!n || !dst || capacity < n || capacity > MaxBytes) return 0;
    std::memcpy(dst, geometry, n);
    return n;
}
