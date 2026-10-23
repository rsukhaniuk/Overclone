// Project-owned, offline-only C ABI helpers for the retained XeTeX port.
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cerrno>
#include <string>
#include <dirent.h>
#include <sys/stat.h>
#include <cstdio>
#include <unistd.h>
#include <emscripten.h>
#include <unicode/putil.h>

// MEMFS is a browser API seam. This small hook enforces writes while the C/C++
// engine runs, rather than only rejecting oversized output after allocation.
EM_JS(void, oc_install_limits, (int full), {
    const original = FS.write;
    let growth = 0;
    let logBytes = 0;
    FS.write = function(stream, buffer, offset, length, position, canOwn) {
        if (stream.fd === 1 || stream.fd === 2) {
            logBytes += length;
            if (logBytes > 2097152) return length;
        } else {
            if (!stream.path.startsWith('/work/')) throw new FS.ErrnoError(full);
            const size = stream.node.node_ops.getattr(stream.node).size;
            const end = (position === undefined ? stream.position : position) + length;
            const added = Math.max(0, end - size);
            const maximum = stream.path.endsWith('.fmt') ? 67108864 : 33554432;
            if (end > maximum || growth + added > 100663296) throw new FS.ErrnoError(full);
            growth += added;
        }
        return original.call(FS, stream, buffer, offset, length, position, canOwn);
    };
});

// No host mounts, font discovery, environment lookups or network imports.
// UTF-8 path components are validated, never evaluated as TeX commands.
extern "C" int oc_path_ok(const char* name, int writing) {
    if (!name || !*name || std::strlen(name) > 240) return 0;
    const auto* p = reinterpret_cast<const unsigned char*>(name);
    if (*p == '/') {
        if (std::strncmp(name, "/work/", 6) == 0) p += 6;
        else if (!writing && std::strncmp(name, "/tex/", 5) == 0) p += 5;
        else return 0;
    }
    const auto* component = p;
    while (*p) {
        if (*p == '/') {
            if (p == component || (p-component == 2 && component[0] == '.' && component[1] == '.')) return 0;
            component = ++p;
            continue;
        }
        if (*p < 32 || *p == 127 || std::strchr("\\:\"|{}%#^~$&", *p)) return 0;
        if (*p < 128) { ++p; continue; }
        uint32_t value;
        int count;
        if (*p >= 0xc2 && *p <= 0xdf) { value = *p & 31; count = 1; }
        else if (*p >= 0xe0 && *p <= 0xef) { value = *p & 15; count = 2; }
        else if (*p >= 0xf0 && *p <= 0xf4) { value = *p & 7; count = 3; }
        else return 0;
        const int total = count;
        ++p;
        while (count--) {
            if ((*p & 0xc0) != 0x80) return 0;
            value = (value << 6) | (*p++ & 63);
        }
        if ((total == 2 && value < 0x800) || (total == 3 && value < 0x10000)
            || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return 0;
        // This retained engine's generated filename scanner accepts UTF-16 BMP
        // characters only. Reject unsupported main names instead of truncating.
        if (writing == 2 && value > 0xffff) return 0;
    }
    if (p == component || (p-component == 2 && component[0] == '.' && component[1] == '.')) return 0;
    return 1;
}

namespace {
bool scan(const std::string& root, int depth, unsigned& count, uint64_t& bytes,
          unsigned maximumFiles, uint64_t maximumFile, uint64_t maximumTotal) {
    if (depth > 16) return false;
    DIR* dir = opendir(root.c_str());
    if (!dir) return false;
    bool valid = true;
    while (auto* entry = readdir(dir)) {
        if (!std::strcmp(entry->d_name, ".") || !std::strcmp(entry->d_name, "..")) continue;
        std::string name = root + "/" + entry->d_name;
        struct stat info {};
        if (lstat(name.c_str(), &info) || !oc_path_ok(name.c_str(), 0)) { valid = false; break; }
        if (S_ISDIR(info.st_mode)) {
            if (!scan(name, depth+1, count, bytes, maximumFiles, maximumFile, maximumTotal)) { valid = false; break; }
        } else if (S_ISREG(info.st_mode)) {
            bytes += info.st_size;
            if (++count > maximumFiles || uint64_t(info.st_size) > maximumFile || bytes > maximumTotal) { valid = false; break; }
        } else { valid = false; break; }
    }
    closedir(dir);
    return valid;
}
}

namespace { std::string expected_output; }

extern "C" int oc_prepare_output(const char* main) {
    if (main) {
        const char* slash = std::strrchr(main, '/');
        const std::string filename = slash ? slash+1 : main;
        std::string base = filename;
        const auto dot = base.rfind('.');
        if (dot != std::string::npos && dot != 0) base.resize(dot);
        if (base+".xdv" == filename) return 0;
        expected_output = "/work/" + base + ".xdv";
    } else {
        expected_output = "/work/xelatex.fmt";
    }
    // A supplied/project artifact must never serve as this attempt's output.
    return unlink(expected_output.c_str()) == 0 || errno == ENOENT;
}

extern "C" int oc_output_ready(int format) {
    FILE* file = std::fopen(expected_output.c_str(), "rb");
    if (!file) return 0;
    unsigned char header[2] = {};
    const auto read = std::fread(header, 1, sizeof(header), file);
    std::fclose(file);
    return read == 2 && (format || (header[0] == 247 && header[1] == 7));
}

extern "C" int oc_check_inputs() {
    unsigned count = 0;
    uint64_t bytes = 0;
    // Combined VFS admission: C# separately enforces 64 captured + 128 state files.
    // This is not a 192-file project capture allowance.
    if (!scan("/work", 0, count, bytes, 192, 8388608, 33554432)) return 0;
    count = 0; bytes = 0;
    return scan("/tex", 0, count, bytes, 8192, 67108864, 167772160);
}

extern "C" void oc_initialize() {
    // Genuine ICU 68.2 data from the approved TeX Live source, provisioned in MEMFS.
    u_setDataDirectory("/tex");
    oc_install_limits(ENOSPC);
}
