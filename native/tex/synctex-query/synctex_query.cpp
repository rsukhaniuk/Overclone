#include "synctex_query.h"
#include "synctex_parser.h"

#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

namespace {
constexpr uint32_t map_limit = 1048575;
constexpr size_t name_limit = 1024;
constexpr size_t result_limit = 32768;
constexpr double coordinate_limit = 1000000;
constexpr char directory[] = ".synctex-query-private";
constexpr char sidecar[] = ".synctex-query-private/map.synctex";
constexpr char output[] = ".synctex-query-private/map.pdf";
constexpr char magic[] = "SyncTeX Version:1\n";

synctex_scanner_p scanner = nullptr;
bool attempted = false;
bool closed = false;
int32_t status = SQ_OK;
std::array<char, result_limit> result{};
size_t result_size = 0;

void clear_result() {
    // Erase even bytes beyond the published length, including partial assembly.
    result.fill(0);
    result_size = 0;
    if (scanner) {
        synctex_scanner_reset_result(scanner);
    }
}

int32_t fail(int32_t code) {
    clear_result();
    status = code;
    return code;
}

// Strict Unicode scalar decoding, rejecting overlong encodings, surrogates,
// C0/C1 controls, DEL, and Unicode line/paragraph separators in filenames.
bool valid_name(const uint8_t *s, size_t n) {
    if (!s || n == 0 || n > name_limit) { return false; }
    size_t i = 0;
    while (i < n) {
        uint32_t cp = s[i++];
        unsigned more = 0;
        uint32_t minimum = 0;
        if (cp >= 0xc2 && cp <= 0xdf) { cp &= 0x1f; more = 1; minimum = 0x80; }
        else if (cp >= 0xe0 && cp <= 0xef) { cp &= 0x0f; more = 2; minimum = 0x800; }
        else if (cp >= 0xf0 && cp <= 0xf4) { cp &= 7; more = 3; minimum = 0x10000; }
        else if (cp >= 0x80) { return false; }
        if (more > n - i) { return false; }
        for (unsigned j = 0; j < more; ++j) {
            const uint8_t c = s[i++];
            if ((c & 0xc0) != 0x80) { return false; }
            cp = (cp << 6) | (c & 0x3f);
        }
        if (cp < minimum || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)
            || cp < 0x20 || (cp >= 0x7f && cp <= 0x9f)
            || cp == 0x2028 || cp == 0x2029) { return false; }
    }
    return true;
}

bool relative_path(const uint8_t *s, size_t n) {
    if (!valid_name(s, n)) { return false; }
    size_t start = 0;
    for (size_t i = 0; i <= n; ++i) {
        if (i < n && (s[i] == '\\' || s[i] == ':')) { return false; }
        if (i == n || s[i] == '/') {
            const size_t size = i - start;
            if (size == 0 || (size == 1 && s[start] == '.')
                || (size == 2 && s[start] == '.' && s[start + 1] == '.')) { return false; }
            start = i + 1;
        }
    }
    return true;
}

bool bounded(double x) { return std::isfinite(x) && std::abs(x) <= coordinate_limit; }

struct Builder {
    size_t size = 0;
    bool ok = true;
    void append(const char *s, size_t n) {
        if (!ok || n > result_limit - size) { ok = false; return; }
        std::memcpy(result.data() + size, s, n);
        size += n;
    }
    void text(const char *s) { append(s, std::strlen(s)); }
    void integer(int n) {
        char b[32];
        const int count = std::snprintf(b, sizeof b, "%d", n);
        if (count <= 0 || static_cast<size_t>(count) >= sizeof b) { ok = false; return; }
        append(b, static_cast<size_t>(count));
    }
    bool number(float n) {
        char b[64];
        const int count = std::snprintf(b, sizeof b, "%.9g", static_cast<double>(n));
        if (count <= 0 || static_cast<size_t>(count) >= sizeof b) { return false; }
        // A non-C decimal locale must fail safely, never emit invalid JSON.
        for (int i = 0; i < count; ++i) {
            const char c = b[i];
            if (!(c >= '0' && c <= '9') && c != '-' && c != '+' && c != '.'
                && c != 'e' && c != 'E') { return false; }
        }
        append(b, static_cast<size_t>(count));
        return true;
    }
    void quoted(const char *s, size_t n) {
        text("\"");
        for (size_t i = 0; i < n; ++i) {
            if (s[i] == '\\' || s[i] == '"') { text("\\"); }
            append(s + i, 1);
        }
        text("\"");
    }
};

// This fixed directory is acquired exclusively; preexisting files/directories/
// symlinks are never adopted or deleted. Dedicated single-owner CWD is required.
struct Sidecar {
    bool owns_directory = false;
    bool owns_file = false;
    struct stat directory_identity{};
    struct stat file_identity{};
    static bool same(const char *path, const struct stat &identity) {
        struct stat now{};
        return lstat(path, &now) == 0 && now.st_dev == identity.st_dev
            && now.st_ino == identity.st_ino && now.st_mode == identity.st_mode;
    }
    bool write_map(const uint8_t *bytes, uint32_t length) {
        if (mkdir(directory, 0700) != 0) { return false; }
        if (lstat(directory, &directory_identity) != 0) { return false; }
        owns_directory = true;
        const int fd = open(sidecar, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0600);
        if (fd < 0) { return false; }
        if (fstat(fd, &file_identity) != 0) { ::close(fd); return false; }
        owns_file = true;
        size_t done = 0;
        while (done < length) {
            const ssize_t count = write(fd, bytes + done, length - done);
            if (count <= 0) { ::close(fd); return false; }
            done += static_cast<size_t>(count);
        }
        return ::close(fd) == 0;
    }
    ~Sidecar() {
        if (owns_directory && same(directory, directory_identity)) {
            if (owns_file && same(sidecar, file_identity)) { unlink(sidecar); }
            rmdir(directory); // Fails rather than recursively deleting unknown entries.
        }
    }
};

int32_t collect(const char *direction, synctex_status_t native_status) {
    if (native_status < 0) { return fail(SQ_NATIVE); }
    if (native_status == 0) { return fail(SQ_EMPTY); }
    // Assemble records first; reserve prefix space by moving once at publication.
    Builder b;
    int retained = 0;
    bool truncated = false;
    while (synctex_node_p node = synctex_scanner_next_result(scanner)) {
        if (retained == 32) { truncated = true; break; } // Inspect only a 33rd handle.
        const char *name = synctex_node_get_name(node);
        const size_t length = name ? strnlen(name, name_limit + 1) : 0;
        const int tag = synctex_node_tag(node);
        const int line = synctex_node_line(node);
        const int column = synctex_node_column(node);
        const int page = synctex_node_page(node);
        if (!valid_name(reinterpret_cast<const uint8_t *>(name), length)
            || tag <= 0 || line <= 0 || column < -1 || page < 1 || page > 512) {
            return fail(length > name_limit ? SQ_BUDGET : SQ_NATIVE);
        }
        const float values[] = {
            synctex_node_visible_h(node), synctex_node_visible_v(node),
            synctex_node_box_visible_h(node), synctex_node_box_visible_v(node),
            synctex_node_box_visible_width(node), synctex_node_box_visible_height(node),
            synctex_node_box_visible_depth(node)
        };
        for (size_t i = 0; i < 7; ++i) {
            if (!bounded(values[i]) || (i >= 4 && values[i] < 0)) { return fail(SQ_NATIVE); }
        }
        if (retained) { b.text(","); }
        b.text("{\"file\":"); b.quoted(name, length);
        b.text(",\"tag\":"); b.integer(tag);
        b.text(",\"line\":"); b.integer(line);
        b.text(",\"column\":");
        // Header describes 0; this pinned implementation also uses
        // SYNCTEX_DFLT_COLUMN=-1 for missing engine column information.
        if (column <= 0) { b.text("null"); } else { b.integer(column); }
        b.text(",\"page\":"); b.integer(page);
        const char *keys[] = {",\"point\":{\"h\":", ",\"v\":", "},\"box\":{\"h\":", ",\"v\":",
                              ",\"width\":", ",\"height\":", ",\"depth\":"};
        for (size_t i = 0; i < 7; ++i) {
            b.text(keys[i]);
            if (!b.number(values[i])) { return fail(SQ_NATIVE); }
        }
        b.text("}}");
        if (!b.ok) { return fail(SQ_BUDGET); }
        ++retained;
    }
    if (retained == 0) { return fail(SQ_EMPTY); }
    char prefix[256];
    const int count = std::snprintf(prefix, sizeof prefix,
        "{\"schema\":1,\"direction\":\"%s\",\"coordinateSpace\":\"synctex-visible-bp-top-left\","
        "\"retainedCount\":%d,\"truncated\":%s,\"matches\":[", direction, retained,
        truncated ? "true" : "false");
    if (count <= 0 || static_cast<size_t>(count) >= sizeof prefix
        || b.size + static_cast<size_t>(count) + 2 > result_limit) { return fail(SQ_BUDGET); }
    std::memmove(result.data() + count, result.data(), b.size);
    std::memcpy(result.data(), prefix, static_cast<size_t>(count));
    result_size = static_cast<size_t>(count) + b.size;
    result[result_size++] = ']'; result[result_size++] = '}';
    synctex_scanner_reset_result(scanner);
    return status = SQ_OK;
}
} // namespace

extern "C" {
uint32_t sq_version(void) { return 1; }
int32_t sq_status_code(void) { return status; }
const char *sq_error_message(void) {
    switch (status) {
        case SQ_OK: return "ok";
        case SQ_EMPTY: return "no matches";
        case SQ_INVALID: return "invalid input";
        case SQ_STATE: return "module state refuses operation";
        case SQ_IO: return "private sidecar unavailable";
        case SQ_PARSE: return "upstream map parse failed";
        case SQ_NATIVE: return "unsupported upstream query data";
        case SQ_BUDGET: return "result budget exceeded";
        case SQ_COPY: return "result copy refused";
        default: return "internal error";
    }
}
int32_t sq_load(const uint8_t *bytes, uint32_t length) {
    clear_result();
    if (attempted || closed) { return fail(SQ_STATE); }
    attempted = true;
    if (!bytes || length == 0 || length > map_limit || std::memchr(bytes, 0, length)
        || length < sizeof magic - 1 || std::memcmp(bytes, magic, sizeof magic - 1) != 0) {
        return fail(SQ_INVALID);
    }
    Sidecar owned;
    if (!owned.write_map(bytes, length)) { return fail(SQ_IO); }
    scanner = synctex_scanner_new_with_output_file(output, nullptr, 1);
    if (!scanner) { return fail(SQ_PARSE); }
    // parse=1 consumed the complete file. No query needs a project/source/PDF file.
    return status = SQ_OK;
}
int32_t sq_forward(const uint8_t *filename, uint32_t length,
                   int32_t line, int32_t column, int32_t page_hint) {
    clear_result();
    if (!scanner || closed) { return fail(SQ_STATE); }
    if (!relative_path(filename, length) || line <= 0 || column < 0
        || (page_hint != -1 && (page_hint < 1 || page_hint > 512))) { return fail(SQ_INVALID); }
    std::array<char, name_limit + 1> name{};
    std::memcpy(name.data(), filename, length);
    return collect("forward", synctex_display_query(scanner, name.data(), line, column, page_hint));
}
int32_t sq_inverse(int32_t page, double h, double v) {
    clear_result();
    if (!scanner || closed) { return fail(SQ_STATE); }
    if (page < 1 || page > 512 || !bounded(h) || !bounded(v)) { return fail(SQ_INVALID); }
    return collect("inverse", synctex_edit_query(scanner, page, static_cast<float>(h), static_cast<float>(v)));
}
uint32_t sq_result_length(void) { return static_cast<uint32_t>(result_size); }
int32_t sq_result_copy(uint8_t *destination, uint32_t capacity) {
    if (!destination || result_size == 0 || capacity < result_size) { return fail(SQ_COPY); }
    std::memcpy(destination, result.data(), result_size);
    return status = SQ_OK;
}
int32_t sq_close(void) {
    clear_result();
    if (scanner) { synctex_scanner_free(scanner); scanner = nullptr; }
    closed = true;
    return status = SQ_OK;
}
}
