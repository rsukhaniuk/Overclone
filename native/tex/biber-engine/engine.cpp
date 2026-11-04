// Project-owned ABI around genuine, statically linked Perl/Biber execution.
// Upstream program/source notices remain with the separately pinned runtime.
#include "engine.h"

#include <emscripten.h>
#include <emscripten/heap.h>
#include <cerrno>
#include <climits>
#include <csetjmp>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

#define API extern "C" EMSCRIPTEN_KEEPALIVE
extern "C" int oc_genuine_perl_main(int, char **, char **);
extern "C" int emperl_end_perl(void);

// Minimum browser/WASM glue: the same Unicode alias convention as other ports.
// Syntax, UTF-8 validation, path boundaries and workflow remain in C++.
EM_JS(int, oc_biber_path_key, (const char *name, char *out, int cap), {
  const key = UTF8ToString(name).normalize('NFC').toUpperCase().normalize('NFC');
  if (lengthBytesUTF8(key) >= cap) return 0;
  stringToUTF8(key, out, cap); return 1;
});
EM_JS(int, oc_biber_guard_begin, (uint32_t log, uint32_t output, const char *stem), {
  try {
    if (typeof ocInstallBiberVfsBudget !== 'function' || ocBiberVfsBudget) return 0;
    ocBiberVfsBudget = ocInstallBiberVfsBudget(log, output, UTF8ToString(stem));
    return ocBiberVfsBudget && !ocBiberVfsBudget.failed ? 1 : 0;
  } catch (_) { return 0; }
});
EM_JS(int, oc_biber_guard_failed, (), {
  return !ocBiberVfsBudget || ocBiberVfsBudget.failed || ocBiberVfsBudget.snapshot().failed ? 1 : 0;
});

namespace {
constexpr uint32_t MaxPath = 1024, MaxContext = 8192;
constexpr uint32_t MaxFiles = 64, MaxInput = 8u * 1024u * 1024u;
constexpr uint32_t MaxLog = 256u * 1024u, MaxOutput = 8u * 1024u * 1024u;
constexpr const char *Program = "/work/runtime/perl/bin/biber";
enum class State { Fresh, Configured, Running, Failed, Complete };
State state = State::Fresh;
const char *lastError = "";
std::string entry, entryKey, stem, context;
std::map<std::string, std::string> inputs;
std::map<std::string, std::string> inputDirectories;
uint32_t inputBytes = 0, logLimit = 0, outputLimit = 0;
bool bcfSeeded = false, active = false, forbidden = false, mainReturned = false, cleanupStarted = false;
int actualExit = 0;
std::jmp_buf exitTarget;
std::vector<uint8_t> outputs[3];

int error(int code) {
  lastError = code == -1 ? "invalid-input" : code == -2 ? "invalid-state" : code == -3 ? "quota" : "native-failure";
  return code;
}
int stop(int code) {
  state = State::Failed;
  for (auto &value : outputs) value.clear();
  return error(code);
}
bool range(const void *ptr, uint32_t length) {
  if (!length) return true;
  const auto start = reinterpret_cast<uintptr_t>(ptr);
  const auto size = emscripten_get_heap_size();
  return start != 0 && start < size && length <= size - start;
}
bool utf8(const uint8_t *bytes, uint32_t length) {
  if (!range(bytes, length)) return false;
  for (uint32_t i = 0; i < length;) {
    const uint32_t first = bytes[i++];
    if (first == 0) return false;
    if (first < 128) continue;
    uint32_t scalar = 0, extra = 0, minimum = 0;
    if (first >= 0xc2 && first <= 0xdf) { scalar = first & 31; extra = 1; minimum = 0x80; }
    else if (first >= 0xe0 && first <= 0xef) { scalar = first & 15; extra = 2; minimum = 0x800; }
    else if (first >= 0xf0 && first <= 0xf4) { scalar = first & 7; extra = 3; minimum = 0x10000; }
    else return false;
    if (extra > length - i) return false;
    for (uint32_t n = 0; n < extra; ++n) {
      const auto b = bytes[i++]; if ((b & 0xc0) != 0x80) return false;
      scalar = (scalar << 6) | (b & 63);
    }
    if (scalar < minimum || scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff)) return false;
  }
  return true;
}
bool path(const uint8_t *bytes, uint32_t length, std::string &value, std::string &key) {
  if (!length || length > MaxPath || !utf8(bytes, length)) return false;
  value.assign(reinterpret_cast<const char *>(bytes), length);
  if (value.front() == '/' || value.back() == '/') return false;
  for (const unsigned char c : value) if (c < 32 || c == 127 || c == '\\' || c == ':') return false;
  size_t start = 0;
  while (start < value.size()) {
    const auto end = value.find('/', start);
    const auto part = value.substr(start, end == std::string::npos ? end : end - start);
    if (part.empty() || part == "." || part == "..") return false;
    if (end == std::string::npos) break;
    start = end + 1;
  }
  char normalized[4097];
  if (!oc_biber_path_key(value.c_str(), normalized, sizeof normalized)) return false;
  key.assign(normalized);
  return true;
}
bool conflict(const std::string &key) {
  for (const auto &item : inputs) {
    const auto &old = item.first;
    if (old == key || (old.size() < key.size() && key.compare(0, old.size() + 1, old + '/') == 0)
        || (key.size() < old.size() && old.compare(0, key.size() + 1, key + '/') == 0)) return true;
  }
  return false;
}
bool directoryAliases(const std::string &value, bool remember) {
  for (auto at = value.find('/'); at != std::string::npos; at = value.find('/', at + 1)) {
    const auto prefix = value.substr(0, at);
    char rawKey[4097]; if (!oc_biber_path_key(prefix.c_str(), rawKey, sizeof rawKey)) return false;
    const auto found = inputDirectories.find(rawKey);
    if (found != inputDirectories.end() && found->second != prefix) return false;
    if (inputs.count(rawKey)) return false;
    if (remember) inputDirectories.emplace(rawKey, prefix);
  }
  return true;
}
bool directory(const std::string &name) {
  struct stat info{};
  if (lstat(name.c_str(), &info) == 0) return S_ISDIR(info.st_mode) && !S_ISLNK(info.st_mode);
  return errno == ENOENT && mkdir(name.c_str(), 0700) == 0;
}
bool parents(const std::string &name) {
  for (auto at = name.find('/', 1); at != std::string::npos; at = name.find('/', at + 1)) {
    if (!directory(name.substr(0, at))) return false;
  }
  return true;
}
bool regular(const std::string &name) {
  struct stat info{};
  return lstat(name.c_str(), &info) == 0 && S_ISREG(info.st_mode) && !S_ISLNK(info.st_mode);
}
int readOutput(uint32_t kind, const std::string &file, uint32_t cap, bool optional) {
  struct stat info{};
  if (lstat(file.c_str(), &info) != 0) return optional && errno == ENOENT ? 0 : -4;
  if (!S_ISREG(info.st_mode) || S_ISLNK(info.st_mode) || info.st_size < 0) return -4;
  if (uint64_t(info.st_size) > cap) return -3;
  auto &out = outputs[kind]; out.resize(static_cast<size_t>(info.st_size));
  FILE *stream = std::fopen(file.c_str(), "rb");
  if (!stream) return -4;
  const bool ok = (out.empty() || std::fread(out.data(), 1, out.size(), stream) == out.size()) && !std::ferror(stream);
  return std::fclose(stream) == 0 && ok ? 0 : -4;
}
int ready(uint32_t kind) {
  if (kind > 2) return error(-1);
  if (state == State::Failed) return -2; // Preserve the actual run failure code.
  if (state != State::Complete) return error(-2);
  if (forbidden) return stop(-4);
  if (oc_biber_guard_failed()) return stop(-3);
  return 0;
}
}

// Internal hook used by the builder's denied process/network imports. A caught
// Perl exception must not turn a forbidden operation into an eligible result.
extern "C" void oc_biber_latch_forbidden(void) { forbidden = true; }
extern "C" void oc_biber_cleanup_started(void) { cleanupStarted = true; }
extern "C" __attribute__((noreturn)) void __wrap_exit(int code) {
  if (!active) __builtin_trap();
  if (!mainReturned || actualExit == 0) actualExit = code;
  std::longjmp(exitTarget, 1);
}
extern "C" __attribute__((noreturn)) void __wrap__exit(int code) { __wrap_exit(code); }

API uint32_t oc_biber_abi() { return 1; }
API int32_t oc_biber_configure(const uint8_t *bcf, uint32_t bcfLength,
                             const uint8_t *operation, uint32_t operationLength,
                             uint32_t maxLog, uint32_t maxOutput) {
  if (state != State::Fresh) return error(-2);
  std::string value, key;
  if (!path(bcf, bcfLength, value, key) || operationLength > MaxContext || !utf8(operation, operationLength)
      || !maxLog || maxLog > MaxLog || !maxOutput || maxOutput > MaxOutput) return error(-1);
  const auto leaf = value.substr(value.find_last_of('/') == std::string::npos ? 0 : value.find_last_of('/') + 1);
  if (leaf.size() <= 4 || leaf.compare(leaf.size() - 4, 4, ".bcf") != 0) return error(-1);
  const auto job = leaf.substr(0, leaf.size() - 4);
  if (job == "." || job == "..") return error(-1);
  if (!directory("/work") || !directory("/work/project") || !directory("/work/output") || !directory("/work/cache")) return stop(-4);
  if (!regular(Program)) return stop(-4);
  entry = value; entryKey = key; stem = job;
  if (!directoryAliases(entry, true)) return stop(-1);
  if (operationLength) context.assign(reinterpret_cast<const char *>(operation), operationLength);
  logLimit = maxLog; outputLimit = maxOutput; state = State::Configured; lastError = "";
  return 0;
}
API int32_t oc_biber_add_file(uint32_t role, const uint8_t *name, uint32_t nameLength,
                            const uint8_t *bytes, uint32_t length) {
  if (state != State::Configured) return error(-2);
  std::string value, key;
  if (role == 0 || role > 2 || !path(name, nameLength, value, key)
      || (role == 2 && value != entry) || (role == 1 && key == entryKey) || conflict(key)
      || inputDirectories.count(key) || !directoryAliases(value, false)) return error(-1);
  if (length > MaxInput || length > MaxInput - inputBytes || inputs.size() >= MaxFiles) return stop(-3);
  if (!range(bytes, length)) return error(-1);
  const auto target = "/work/project/" + value;
  struct stat info{};
  if (lstat(target.c_str(), &info) == 0 || errno != ENOENT || !parents(target)) return error(-1);
  FILE *file = std::fopen(target.c_str(), "wb");
  if (!file) return stop(-4);
  const bool written = !length || std::fwrite(bytes, 1, length, file) == length;
  const bool closed = std::fclose(file) == 0;
  if (!written || !closed) { unlink(target.c_str()); return stop(-4); }
  inputs.emplace(key, value); inputBytes += length; directoryAliases(value, true);
  if (role == 2) bcfSeeded = length > 0;
  lastError = ""; return 0;
}
API int32_t oc_biber_run() {
  if (state != State::Configured) return error(-2);
  state = State::Running;
  if (!bcfSeeded) return stop(-1);
  if (chdir("/work/project") != 0) return stop(-4);
  // Do not inherit origin/process/provider credentials or ambient Perl options.
  if (clearenv() != 0) return stop(-4);
  const char *env[] = {"HOME=/work/cache", "TMPDIR=/work/cache", "TEMP=/work/cache", "TMP=/work/cache",
    "PATH=/work/runtime/perl/bin", "LANG=C.UTF-8", "LC_ALL=C.UTF-8", "TZ=UTC", "PERL_USE_UNSAFE_INC=0",
    "PERL_HASH_SEED=0", "PERL_PERTURB_KEYS=0", nullptr};
  std::vector<std::string> environment;
  for (const char **p = env; *p; ++p) {
    environment.emplace_back(*p);
    const auto split = environment.back().find('=');
    if (setenv(environment.back().substr(0, split).c_str(), environment.back().c_str() + split + 1, 1) != 0) return stop(-4);
  }
  char *nativeEnv[16] = {}; size_t envCount = 0;
  for (auto &item : environment) nativeEnv[envCount++] = item.data();
  FILE *empty = std::fopen("/work/cache/stdin", "wb");
  if (!empty || std::fclose(empty) != 0) return stop(-4);
  FILE *log = std::fopen("/work/output/terminal.log", "wb");
  if (!log || std::fclose(log) != 0) return stop(-4);
  if (!std::freopen("/work/cache/stdin", "rb", stdin)
      || !std::freopen("/work/output/terminal.log", "ab", stdout)
      || !std::freopen("/work/output/terminal.log", "ab", stderr)) return stop(-4);
  setvbuf(stdout, nullptr, _IONBF, 0); setvbuf(stderr, nullptr, _IONBF, 0);
  if (!oc_biber_guard_begin(logLimit, outputLimit, stem.c_str())) return stop(-4);
  // The ABI supplies UTF-8 argv bytes. Perl's A flag decodes only @ARGV;
  // it does not change file contents or the standard I/O encoding layers.
  std::string arguments[] = {"biber", "-CA", Program, "--noconf", "--input-directory=/work/project",
    "--output-directory=/work/output", "--", entry};
  char *argv[9] = {}; for (size_t i = 0; i < 8; ++i) argv[i] = arguments[i].data();
  active = true; mainReturned = false; cleanupStarted = false; actualExit = 0;
  if (setjmp(exitTarget) == 0) {
    actualExit = oc_genuine_perl_main(8, argv, nativeEnv);
    mainReturned = true;
    // The genuine early-error main path already cleans up before exit(); that
    // branch longjmps above and must never enter cleanup a second time.
    if (!cleanupStarted) {
      const int cleanup = emperl_end_perl();
      if (actualExit == 0 && cleanup != 0) actualExit = cleanup;
    }
  }
  active = false;
  std::fflush(nullptr);
  if (forbidden) return stop(-4);
  if (oc_biber_guard_failed()) return stop(-3);
  if (actualExit != 0) { const int code = actualExit; stop(-4); return code > 0 ? code : -4; }
  const auto prefix = std::string("/work/output/") + stem;
  const int bbl = readOutput(0, prefix + ".bbl", outputLimit, false);
  if (bbl) return stop(bbl);
  if (outputs[0].empty()) return stop(-4);
  const int blg = readOutput(1, prefix + ".blg", outputLimit - static_cast<uint32_t>(outputs[0].size()), true);
  if (blg) return stop(blg);
  const int terminal = readOutput(2, "/work/output/terminal.log", logLimit, true);
  if (terminal) return stop(terminal);
  if (forbidden) return stop(-4);
  if (oc_biber_guard_failed()) return stop(-3);
  state = State::Complete; lastError = ""; return 0;
}
API int32_t oc_biber_output_size(uint32_t kind) {
  const auto status = ready(kind); return status ? status : static_cast<int32_t>(outputs[kind].size());
}
API int32_t oc_biber_copy_output(uint32_t kind, uint8_t *destination, uint32_t capacity) {
  const auto status = ready(kind); if (status) return status;
  const auto &out = outputs[kind];
  if (capacity < out.size() || !range(destination, static_cast<uint32_t>(out.size()))) return error(-1);
  if (!out.empty()) std::memcpy(destination, out.data(), out.size());
  return static_cast<int32_t>(out.size());
}
API int32_t oc_biber_error(uint8_t *destination, uint32_t capacity) {
  const auto length = static_cast<uint32_t>(std::strlen(lastError));
  if (capacity < length || !range(destination, length)) return -1;
  if (length) std::memcpy(destination, lastError, length);
  return static_cast<int32_t>(length);
}
