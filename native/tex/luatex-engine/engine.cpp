#include "engine.h"
#ifndef OC_LUATEX_ABI
#define OC_LUATEX_ABI 3
#endif
#ifndef OC_LUA_OUTPUT_DECLARATIONS
#define OC_LUA_OUTPUT_DECLARATIONS 128
#endif
#if OC_LUA_OUTPUT_DECLARATIONS != 128 && OC_LUA_OUTPUT_DECLARATIONS != 256
#error Unsupported Lua output declaration contract
#endif
#if OC_LUA_OUTPUT_DECLARATIONS == 256
extern "C" int oc_lua_output_declaration_limit() { return 256; }
#endif
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
#include "../synctex-producer/oc_synctex_internal.h"
#include "../synctex-producer/oc_synctex_memory_sink.h"
#endif
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <set>
#include <string>
#include <setjmp.h>
#include <sys/stat.h>
#include <unistd.h>
#include <emscripten.h>
EM_JS(int, oc_budget_begin, (int format), {
    try { return ocInstallVfsBudget(!!format) ? 1 : 0; }
    catch (error) { ocVfsShapeError = String(error).slice(0,180); return 0; }
});
EM_JS(int, oc_budget_failed, (), { return ocVfsBudget && ocVfsBudget.failed ? 1 : 0; });
EM_JS(void, oc_budget_reason, (char *destination, int capacity), {
    const text = ocVfsBudget ? ocVfsBudget.snapshot().reason : ocVfsShapeError;
    stringToUTF8(text || 'VFS guard unavailable', destination, capacity);
});
EM_JS(void, oc_budget_report, (char *destination, int capacity), {
    stringToUTF8(JSON.stringify(ocVfsSnapshot()), destination, capacity);
});
// Browser Unicode APIs only: normalize collision keys, never caller/FS bytes.
// JS full-uppercase comparison can reject extra aliases beyond .NET invariant
// casing; no general cross-runtime casing parity is claimed.
EM_JS(int, oc_path_key, (const char *name, char *key, int capacity), {
    const text = UTF8ToString(name);
    const folded = text.normalize('NFC').toUpperCase().normalize('NFC');
    if (lengthBytesUTF8(folded) >= capacity) return 0;
    stringToUTF8(folded, key, capacity);
    return 1;
});
extern "C" {
#include "lua.h"
#include "lauxlib.h"
int oc_engine_main(int, char **);
}

namespace {
constexpr uint32_t MaxFile = 32u * 1024u * 1024u;
#if defined(OC_LUA_FONT_CLOSURE) && OC_LUA_FONT_CLOSURE
constexpr uint32_t MaxInput = 160u * 1024u * 1024u;
constexpr uint32_t MaxRuntimeInput = 128u * 1024u * 1024u;
constexpr uint32_t MaxWorkInput = 32u * 1024u * 1024u;
uint32_t runtimeInputBytes = 0, workInputBytes = 0;
#else
constexpr uint32_t MaxInput = 96u * 1024u * 1024u;
#endif
constexpr uint32_t MaxOutput = 64u * 1024u * 1024u;
constexpr uint32_t MaxOutputs = OC_LUA_OUTPUT_DECLARATIONS;
char declared[MaxOutputs][241] = {};
uint32_t declaredCount = 0;
char root[250] = {}, context[129] = {}, message[256] = {};
bool configured = false, used = false, running = false, succeeded = false, formatAttempt = false;
uint32_t inputBytes = 0, inputFiles = 0;
std::set<std::string> inputKeys[3], projectKeys, outputKeys;
jmp_buf engineExit;
int engineStatus = 0;
const char *const outputs[] = {"result.pdf", "result.log", "lualatex.fmt", "result.aux", "result.toc", "result.out", "result.bbl", "result.blg", "result.lof", "result.lot"};
int fail(int code, const char *text) {
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
    oc_mem_abort();
#endif
    std::snprintf(message, sizeof(message), "%s", text);
    if (used) succeeded = false;
    return code;
}
bool validUtf8(const char *name) {
    const auto *p = reinterpret_cast<const unsigned char *>(name);
    while (*p) {
        uint32_t cp = *p++;
        uint32_t minimum = 0;
        int continuation = 0;
        if (cp < 0x80) { if (cp < 32 || cp == 127) return false; continue; }
        if (cp >= 0xc2 && cp <= 0xdf) { continuation = 1; minimum = 0x80; cp &= 0x1f; }
        else if (cp >= 0xe0 && cp <= 0xef) { continuation = 2; minimum = 0x800; cp &= 0x0f; }
        else if (cp >= 0xf0 && cp <= 0xf4) { continuation = 3; minimum = 0x10000; cp &= 0x07; }
        else return false;
        while (continuation--) {
            if ((*p & 0xc0) != 0x80) return false;
            cp = (cp << 6) | (*p++ & 0x3f);
        }
        if (cp < minimum || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff) || (cp >= 0x80 && cp <= 0x9f)) return false;
    }
    return true;
}
bool validPath(const char *name) {
    if (!name || !*name || std::strlen(name) > 240 || name[0] == '/' || !validUtf8(name)) return false;
    const char *component = name;
    for (const unsigned char *p = reinterpret_cast<const unsigned char *>(name); ; ++p) {
        if (*p && (*p < 32 || *p == 127 || *p == '\\' || *p == '"' || *p == ':' || *p == '|')) return false;
        if (*p == '/' || !*p) {
            size_t n = reinterpret_cast<const char *>(p) - component;
            if (!n || (n == 1 && component[0] == '.') || (n == 2 && component[0] == '.' && component[1] == '.')) return false;
            component = reinterpret_cast<const char *>(p) + 1;
        }
        if (!*p) break;
    }
    char key[1024];
    if (!oc_path_key(name, key, sizeof(key))) return false;
    const std::string folded(key);
    return folded != "__OOP_RUNTIME__" && folded.rfind("__OOP_RUNTIME__/", 0) != 0;
}
std::string pathKey(const char *name) {
    char key[1024];
    return oc_path_key(name, key, sizeof(key)) ? std::string(key) : std::string();
}
bool namespaceConflict(const std::set<std::string> &keys, const std::string &key) {
    if (keys.count(key)) return true;
    for (size_t at = key.find('/'); at != std::string::npos; at = key.find('/', at + 1))
        if (keys.count(key.substr(0, at))) return true;
    auto child = keys.lower_bound(key + "/");
    return child != keys.end() && child->compare(0, key.size() + 1, key + "/") == 0;
}
bool validRoot(const char *name) {
    if (!validPath(name)) return false;
    for (const auto *p = reinterpret_cast<const unsigned char *>(name); *p; ++p) {
        if (*p >= 0x80) continue; // Already checked exact UTF-8, never normalized.
        if ((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || (*p >= '0' && *p <= '9') || std::strchr("._-/ ", *p)) continue;
        return false; // In particular all TeX command syntax and quote characters.
    }
    return true;
}
bool isCarryName(const char *name) {
    // ABI3 additions are exact fixed-job basenames, never extension wildcards.
    for (const char *allowed : {"result-blx.bib", "result.bcf", "result.run.xml"})
        if (!std::strcmp(name, allowed)) return true;
    const char *extension = std::strrchr(name, '.');
    if (!extension) return false;
    for (const char *allowed : {".aux", ".bbl", ".blg", ".toc", ".lof", ".lot", ".out"})
        if (!std::strcmp(extension, allowed)) return true;
    return false;
}
bool isDeclared(const char *name) {
    for (uint32_t i = 0; i < declaredCount; ++i) if (!std::strcmp(name, declared[i])) return true;
    return false;
}
bool prefixConflict(const char *a, const char *b) {
    size_t length = std::strlen(a);
    return std::strlen(b) > length && !std::strncmp(a, b, length) && b[length] == '/';
}
bool makeParents(char *path) {
    for (char *p = path + 1; *p; ++p) if (*p == '/') {
        *p = 0;
        int status = mkdir(path, 0700);
        *p = '/';
        if (status && errno != EEXIST) return false;
    }
    return true;
}
const char *outputPath(int artifact, char *buffer, size_t length) {
    if (artifact < 0 || artifact >= 10) return nullptr;
    std::snprintf(buffer, length, "/work/output/%s", artifact == 1 && formatAttempt ? "lualatex.log" : outputs[artifact]);
    return buffer;
}
int fileSize(const char *path) {
    struct stat st{};
    if (lstat(path, &st)) return errno == ENOENT ? 0 : fail(-3, "cannot inspect output");
    if (!S_ISREG(st.st_mode)) return fail(-3, "selected output is not a regular file");
    if (st.st_size < 0 || st.st_size > MaxFile) return fail(-4, "output exceeds copy bound");
    return static_cast<int>(st.st_size);
}
int outputSize(int artifact) {
    char path[300];
    if (!outputPath(artifact, path, sizeof(path))) return fail(-1, "unknown artifact");
    return fileSize(path);
}
int selectedOutputBudget() {
    uint32_t total = 0;
    for (uint32_t i = 0; i < declaredCount; ++i) {
        char path[300];
        std::snprintf(path, sizeof(path), "/work/output/%s", i == 1 && formatAttempt ? "lualatex.log" : declared[i]);
        int size = fileSize(path);
        if (size < 0) return size;
        if (static_cast<uint32_t>(size) > MaxOutput - total) return fail(-4, "selected output budget exceeded");
        total += static_cast<uint32_t>(size);
    }
    return 0;
}
int copyFile(const char *path, uint8_t *destination, uint32_t capacity, int size) {
    if (!destination || capacity < static_cast<uint32_t>(size)) return fail(-4, "destination is too small");
    FILE *file = std::fopen(path, "rb");
    if (!file) return fail(-3, "output disappeared");
    bool ok = std::fread(destination, 1, size, file) == static_cast<size_t>(size);
    ok = std::fclose(file) == 0 && ok;
    return ok ? size : fail(-3, "output read failed");
}
void environment() {
    // No inherited host discovery: all search roots are supplied MEMFS bytes.
    const char *search = "/work/project//:/work/output//:/work/runtime//";
    for (const char *key : {"TEXINPUTS", "LUAINPUTS", "TEXMFSCRIPTS", "TFMFONTS", "VFFONTS", "T1FONTS", "TTFONTS", "OPENTYPEFONTS", "ENCFONTS", "TEXFONTMAPS", "CMAPFONTS"}) setenv(key, search, 1);
    setenv("TEXMF", "/work/runtime", 1);
    setenv("TEXMFCNF", "/work/config", 1);
    setenv("TEXFORMATS", "/work/runtime", 1);
    setenv("TEXMFVAR", "/work/cache", 1);
    setenv("TEXMFCACHE", "/work/cache", 1);
    setenv("TEXMFCONFIG", "/work/config", 1);
    setenv("HOME", "/work/cache", 1);
    setenv("OSFONTDIR", "/work/runtime//", 1);
    setenv("LC_ALL", "C", 1);
    setenv("shell_escape", "f", 1);
    setenv("MKTEXFMT", "0", 1); setenv("MKTEXTFM", "0", 1); setenv("MKTEXPK", "0", 1);
}
int compile(bool format) {
    if (used) {
        succeeded = false; // A rejected attempt must revoke the previous PDF/fmt.
        return fail(-2, "module is single-use; instantiate fresh module");
    }
    if (!configured) return fail(-1, "configure root and context first");
    used = true; succeeded = false; formatAttempt = format; message[0] = 0;
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
    if (!oc_synctex_begin()) return fail(-2, "map attempt already consumed");
    if (format) oc_mem_discard(); // INITEX/BibTeX/carry can never publish a map.
#endif
    unlink("/work/output/result.pdf");
    unlink("/work/output/lualatex.fmt");
    environment();
    if (chdir("/work/project")) return fail(-3, "project VFS unavailable");
    if (!oc_budget_begin(format ? 1 : 0)) {
        oc_budget_reason(message, sizeof(message));
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
        oc_mem_abort();
#endif
        return -4;
    }
    char argument[256];
    std::snprintf(argument, sizeof(argument), "\"./%s\"", root);
    // Kpathsea only needs a real VFS location, not a host executable. The
    // program identity is supplied separately and never discovered from PATH.
    char *args[] = {const_cast<char *>("/work/project"), const_cast<char *>("--progname=lualatex"), const_cast<char *>("--no-shell-escape"),
        const_cast<char *>("--nosocket"), const_cast<char *>("--interaction=nonstopmode"),
        const_cast<char *>("--halt-on-error"), const_cast<char *>("--file-line-error"),
        const_cast<char *>("--recorder"), const_cast<char *>("--output-directory=/work/output"),
        const_cast<char *>(format ? "--jobname=lualatex" : "--jobname=result"),
        const_cast<char *>(format ? "--ini" : "--fmt=/work/runtime/lualatex.fmt"),
        format ? const_cast<char *>("lualatex.ini") : argument, nullptr};
    engineStatus = 1;
    running = true;
    if (setjmp(engineExit) == 0) engineStatus = oc_engine_main(12, args);
    running = false;
    std::fflush(nullptr);
    if (oc_budget_failed()) {
        oc_budget_reason(message, sizeof(message));
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
        oc_mem_abort();
#endif
        return -4;
    }
    if (engineStatus != 0) return fail(engineStatus, "LuaTeX reported failure; no PDF is eligible");
    if (outputSize(format ? 2 : 0) <= 0) return fail(-5, "engine returned without expected output");
    int budget = selectedOutputBudget();
    if (budget < 0) return budget;
    succeeded = true;
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
    if (!format) oc_synctex_finish(engineStatus); // After real PDF close and every output/quota check.
#endif
    return 0;
}
}

extern "C" {
void __wrap_exit(int status) {
    if (running) { engineStatus = status; longjmp(engineExit, 1); }
    std::abort();
}
// Defense in depth, not the sandbox claim: dangerous modules are not registered,
// and actual untrusted Lua/TeX requests are covered by the WASM tests.
int __wrap_system(const char *) { errno = EPERM; return -1; }
FILE *__wrap_popen(const char *, const char *) { errno = EPERM; return nullptr; }
void oc_restrict_lua(lua_State *L) {
    const struct { const char *table; const char *field; } revoked[] = {
        {"os", "execute"}, {"os", "exec"}, {"os", "spawn"},
        {"os", "exit"}, {"io", "popen"}, {"package", "loadlib"}
    };
    for (const auto &item : revoked) {
        lua_getglobal(L, item.table);
        lua_pushnil(L); lua_setfield(L, -2, item.field); lua_pop(L, 1);
    }
    lua_getglobal(L, "package"); lua_getfield(L, -1, "searchers");
    lua_pushnil(L); lua_rawseti(L, -2, 3);
    lua_pushnil(L); lua_rawseti(L, -2, 4);
    lua_pop(L, 2);
}
uint32_t oc_luatex_abi() { return OC_LUATEX_ABI; }
#if OC_LUATEX_ABI == 4 || OC_LUATEX_ABI == 5
// Used only by the private adapter, not an exported success/seal operation.
int oc_lua_maps_eligible() {
    if (oc_budget_failed()) { succeeded = false; oc_mem_abort(); }
    return used && !running && succeeded && !formatAttempt;
}
#if OC_LUATEX_ABI == 5 && defined(OC_LUA_HARFBUZZ) && OC_LUA_HARFBUZZ
const char *oc_luatex_version() { return "LuaHBTeX 1.13.0 (TeX Live 2021, svn 7410); browser ABI 5; HarfBuzz 1"; }
#elif OC_LUATEX_ABI == 5
const char *oc_luatex_version() { return "LuaTeX 1.13.0 (TeX Live 2021, svn 7410); browser ABI 5"; }
#else
const char *oc_luatex_version() { return "LuaTeX 1.13.0 (TeX Live 2021, svn 7410); browser ABI 4"; }
#endif
#else
const char *oc_luatex_version() { return "LuaTeX 1.13.0 (TeX Live 2021, svn 7410); browser ABI 3"; }
#endif
const char *oc_luatex_error() { return message; }
const char *oc_luatex_budget_json() {
    static char report[2048];
    oc_budget_report(report, sizeof(report));
    return report;
}
const char *oc_luatex_context() { return context; }
int oc_luatex_configure(const char *name, const char *identity) {
    if (used || configured) return fail(-2, "configuration is immutable");
    if (!validRoot(name) || !identity || !*identity || std::strlen(identity) > 128) return fail(-1, "invalid root/context");
    for (const unsigned char *p = reinterpret_cast<const unsigned char *>(identity); *p; ++p)
        if (*p < 32 || *p > 126) return fail(-1, "context must be printable ASCII");
    std::snprintf(root, sizeof(root), "%s", name);
    std::snprintf(context, sizeof(context), "%s", identity);
    for (const char *path : {"/work", "/work/project", "/work/runtime", "/work/output", "/work/cache", "/work/config"}) {
        if (mkdir(path, 0700) && errno != EEXIST) return fail(-3, "cannot initialize private VFS");
    }
    // Dedicated source-owned policy. Imported texmf.cnf cannot override it.
    FILE *cnf = std::fopen("/work/config/texmf.cnf", "wb");
    // TeX Live 2021 texmf.cnf's normal macro/input limits, not LuaTeX's tiny
    // unconfigured fallback (param_size=60 cannot load fontspec/xparse).
    const char *policy = "shell_escape = f\nopenin_any = a\nopenout_any = a\nparse_first_line = f\nmax_print_line = 1000\nparam_size = 10000\nnest_size = 500\nsave_size = 200000\nstack_size = 5000\nbuf_size = 200000\n";
    if (!cnf) return fail(-3, "cannot write VFS policy");
    bool ok = std::fputs(policy, cnf) >= 0;
    ok = std::fclose(cnf) == 0 && ok;
    if (!ok) return fail(-3, "cannot close VFS policy");
    for (const char *output : outputs) {
        std::snprintf(declared[declaredCount++], sizeof(declared[0]), "%s", output);
        outputKeys.insert(pathKey(output));
    }
    configured = true;
    return 0;
}
int oc_luatex_declare_output(const char *name) {
    if (!configured || used) return fail(-2, "outputs are immutable after the attempt starts");
    if (!validPath(name)) return fail(-1, "invalid output name");
    if (isDeclared(name)) return 0;
    if (!isCarryName(name)) return fail(-1, "only selected generated names are permitted");
    const auto key = pathKey(name);
    if (namespaceConflict(projectKeys, key)) return fail(-1, "project/generated namespace collision");
    if (namespaceConflict(outputKeys, key)) return fail(-1, "output namespace collision");
    if (declaredCount >= MaxOutputs) return fail(-4, "selected output count exceeded");
    for (uint32_t i = 0; i < declaredCount; ++i)
        if (prefixConflict(name, declared[i]) || prefixConflict(declared[i], name)) return fail(-1, "output file/directory conflict");
    char path[300]; std::snprintf(path, sizeof(path), "/work/output/%s", name);
    if (!makeParents(path)) return fail(-3, "cannot predeclare output parents");
    std::snprintf(declared[declaredCount++], sizeof(declared[0]), "%s", name);
    outputKeys.insert(key);
    return 0;
}
int oc_luatex_add_file(int kind, const char *name, const uint8_t *bytes, uint32_t length) {
    if (!configured || used) return fail(-2, "files may only be seeded before compilation");
    if (!validPath(name) || (!bytes && length) || length > (kind == 0 ? MaxFile : 8u*1024u*1024u) || inputBytes > MaxInput - length || inputFiles >= 4608) return fail(-4, "invalid file or input budget exceeded");
    const char *base;
    if (kind == 0) base = "runtime";
    else if (kind == 1) base = "project";
    else if (kind == 2 && isCarryName(name) && isDeclared(name)) base = "output";
    else return fail(-1, "invalid input kind or carry name");
#if defined(OC_LUA_FONT_CLOSURE) && OC_LUA_FONT_CLOSURE
    if (kind == 0 ? length > MaxRuntimeInput || runtimeInputBytes > MaxRuntimeInput - length
                  : length > MaxWorkInput || workInputBytes > MaxWorkInput - length)
        return fail(-4, "input role budget exceeded");
#endif
    const auto key = pathKey(name);
    if (namespaceConflict(inputKeys[kind], key)) return fail(-1, "input namespace collision");
    if (kind == 1 && namespaceConflict(outputKeys, key)) return fail(-1, "project/generated namespace collision");
    char path[300]; std::snprintf(path, sizeof(path), "/work/%s/%s", base, name);
    struct stat st{};
    if (!stat(path, &st)) return fail(-1, "duplicate input path");
    if (!makeParents(path)) return fail(-3, "cannot create input parents");
    FILE *file = std::fopen(path, "wb");
    if (!file) return fail(-3, "cannot create input");
    bool ok = std::fwrite(bytes, 1, length, file) == length;
    ok = std::fclose(file) == 0 && ok;
    if (!ok) { unlink(path); return fail(-3, "input write failed"); }
    inputBytes += length; ++inputFiles;
#if defined(OC_LUA_FONT_CLOSURE) && OC_LUA_FONT_CLOSURE
    if (kind == 0) runtimeInputBytes += length;
    else workInputBytes += length;
#endif
    inputKeys[kind].insert(key);
    if (kind == 1) projectKeys.insert(key);
    return 0;
}
int oc_luatex_compile_format() { return compile(true); }
int oc_luatex_compile_latex() { return compile(false); }
int oc_luatex_output_size(int artifact) {
    if (!used || running) return fail(-2, "no completed attempt");
    if (oc_budget_failed()) succeeded = false;
    if (!succeeded && artifact != 1) return 0;
    return outputSize(artifact);
}
int oc_luatex_copy_output(int artifact, uint8_t *destination, uint32_t capacity) {
    int size = oc_luatex_output_size(artifact);
    if (size <= 0) return size;
    char path[300]; outputPath(artifact, path, sizeof(path));
    return copyFile(path, destination, capacity, size);
}
int oc_luatex_named_output_size(const char *name) {
    if (!used || running) return fail(-2, "no completed attempt");
    if (oc_budget_failed()) succeeded = false;
    if (!validPath(name) || !isDeclared(name)) return fail(-1, "output was not declared");
    if (!succeeded && std::strcmp(name, "result.log")) return 0;
    char path[300]; std::snprintf(path, sizeof(path), "/work/output/%s", name);
    return fileSize(path);
}
int oc_luatex_copy_named_output(const char *name, uint8_t *destination, uint32_t capacity) {
    int size = oc_luatex_named_output_size(name);
    if (size <= 0) return size;
    char path[300]; std::snprintf(path, sizeof(path), "/work/output/%s", name);
    return copyFile(path, destination, capacity, size);
}
}
