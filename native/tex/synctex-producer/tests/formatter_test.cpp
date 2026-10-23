/* TEST ONLY: include implementation in THIS TU for private byte/lifetime audit.
 * Never link this driver into a product or export these internal fields. Each
 * selected case needs a fresh process/module; no public or fake reset exists.
 */
#include "oc_synctex_memory_sink.cpp"
#include <cstdlib>
#include <limits>
#include <string>
#include <type_traits>
#include <utility>

namespace {
using Callback = int (*)(void *, const char *, ...);
static_assert(std::is_same<decltype(&oc_mem_printf), Callback>::value, "Exact historical callback ABI");
template <typename T, typename = void> struct can_stage : std::false_type {};
template <typename T> struct can_stage<T, std::void_t<decltype(oc_mem_stage_checked(std::declval<T>()))>> : std::true_type {};
static_assert(can_stage<int>::value && !can_stage<bool>::value, "No C++ bool-as-seal shortcut");
static_assert(OC_MEM_STORAGE_BYTES == 1048576U && OC_MEM_MAX_BYTES == 1048575U, "Physical ceiling includes NUL");
static_assert(static_cast<uintmax_t>(std::numeric_limits<int>::max()) >= OC_MEM_MAX_BYTES, "Test count fits callback int");

void require(bool condition)
{
    if (!condition) { std::fputs("FAIL: byte/state/lifetime contract\n", stderr); std::abort(); }
}
void wiped()
{
    require(sink.used == 0U && sink.active_pass == 0U);
    for (char byte : sink.bytes) require(byte == 0);
}
void *start(uint32_t cap = 4096U)
{
    require(oc_mem_configure(1U, cap, 71U) == 1);
    void *handle = oc_mem_open(); require(handle != nullptr); return handle;
}
void record(int count, const char *expected)
{
    const std::size_t length = std::strlen(expected);
    require(count > 0 && static_cast<std::size_t>(count) == length);
    require(sink.used >= length);
    require(std::memcmp(sink.bytes.data() + sink.used - length, expected, length) == 0);
    require(sink.bytes[sink.used] == '\0');
}
void existing(void *handle)
{
    record(oc_mem_printf(handle, "Content:\n"), "Content:\n");
    // Test-only canary beyond configured cap: proves wipe covers ALL storage,
    // not merely previous used bytes or the first truncated record's prefix.
    sink.bytes.back() = 'Z';
}
void invalid_config(uint32_t mode, uint32_t cap, uint32_t pass)
{
    require(oc_mem_configure(mode, cap, pass) == 0); wiped();
    require(oc_mem_configure(1U, 64U, 1U) == 0); require(oc_mem_open() == nullptr); wiped();
}
void default_off()
{
    require(oc_mem_open() == nullptr); wiped();
    require(oc_mem_configure(1U, 64U, 1U) == 0); // Invalid use cannot be reset.
}
void mode_off()
{
    require(oc_mem_configure(0U, 0U, 71U) == 1);
    require(oc_mem_open() == nullptr); require(oc_mem_printf(nullptr, "Content:\n") == -1); wiped();
    require(oc_mem_configure(1U, 64U, 72U) == 0);
}
void reconfigure_armed()
{
    require(oc_mem_configure(1U, 64U, 71U) == 1);
    require(oc_mem_configure(1U, 64U, 71U) == 0); wiped(); require(oc_mem_open() == nullptr);
}
void reconfigure_writing()
{
    void *h = start(); existing(h);
    require(oc_mem_configure(0U, 0U, 72U) == 0); wiped();
    require(oc_mem_printf(h, "Content:\n") == -1); wiped();
}
void without_open()
{
    require(oc_mem_configure(1U, 64U, 71U) == 1);
    // Even a test-known correct private address cannot bypass open/state.
    require(oc_mem_printf(&sink.private_handle, "Content:\n") == -1); wiped();
}
void wrong_handle()
{
    void *h = start(); existing(h); unsigned char fake = 0;
    require(oc_mem_printf(&fake, "Content:\n") == -1); wiped();
}
void null_handle()
{
    void *h = start(); existing(h);
    require(oc_mem_printf(nullptr, "Content:\n") == -1); wiped();
}
void null_format()
{
    void *h = start(); existing(h);
    require(oc_mem_printf(h, nullptr) == -1); wiped();
}
void noncanonical_format()
{
    void *h = start(); existing(h);
    // Safe, correctly typed ordinary format, NOT an unsafe varargs experiment.
    require(oc_mem_printf(h, "Not canonical:%i\n", 1) == -1); wiped();
}
void empty_format()
{
    void *h = start(); existing(h);
    require(oc_mem_printf(h, "") == -1); wiped(); // No zero-byte success.
}
void repeat_open()
{
    void *h = start(); record(oc_mem_printf(h, "Content:\n"), "Content:\n");
    require(oc_mem_open() == h); record(oc_mem_printf(h, ">\n"), ">\n");
    require(sink.used == 11U); require(oc_mem_stage_checked(0) == 1);
    require(std::memcmp(sink.bytes.data(), "Content:\n>\n", 11U) == 0);
    oc_mem_discard(); wiped();
}
void stage_before_open()
{
    require(oc_mem_configure(1U, 64U, 71U) == 1);
    require(oc_mem_stage_checked(0) == 0); wiped(); require(oc_mem_open() == nullptr);
}
void stage_before_write()
{
    (void)start(); require(oc_mem_stage_checked(0) == 0); wiped();
}
void postamble_error(int actual_status)
{
    void *h = start(); existing(h);
    require(oc_mem_stage_checked(actual_status) == 0); wiped();
    require(oc_mem_printf(h, "Content:\n") == -1); require(oc_mem_stage_checked(0) == 0); wiped();
}
void after_stage_write()
{
    void *h = start(); existing(h);
    require(oc_mem_stage_checked(0) == 1);
    require(oc_mem_printf(h, ">\n") == -1); wiped(); require(oc_mem_stage_checked(0) == 0);
}
void after_stage_open()
{
    void *h = start(); existing(h); require(oc_mem_stage_checked(0) == 1);
    require(oc_mem_open() == nullptr); wiped();
}
void stage_twice()
{
    void *h = start(); existing(h); require(oc_mem_stage_checked(0) == 1);
    require(oc_mem_stage_checked(0) == 0); wiped();
}
void abort_terminal()
{
    void *h = start(); existing(h); oc_mem_abort(); wiped();
    require(oc_mem_open() == nullptr); require(oc_mem_stage_checked(0) == 0);
    require(oc_mem_printf(h, "Content:\n") == -1);
    oc_mem_abort(); require(oc_mem_configure(1U, 64U, 72U) == 0); wiped();
}
void discard_terminal()
{
    void *h = start(); existing(h); require(oc_mem_stage_checked(0) == 1);
    oc_mem_discard(); wiped(); require(sink.state == SinkState::discarded);
    require(oc_mem_printf(h, "Content:\n") == -1); // Stale previously real handle.
    require(oc_mem_open() == nullptr); require(oc_mem_stage_checked(0) == 0);
    require(oc_mem_configure(1U, 64U, 72U) == 0);
    oc_mem_abort(); oc_mem_discard(); wiped(); require(sink.state == SinkState::discarded);
}
void discard_armed()
{
    require(oc_mem_configure(1U, 64U, 71U) == 1); oc_mem_discard(); wiped();
    require(oc_mem_open() == nullptr); require(oc_mem_configure(1U, 64U, 72U) == 0);
}
void exact_cap()
{
    void *h = start(10U); record(oc_mem_printf(h, "Input:%i:%s\n", 1, "x"), "Input:1:x\n");
    require(sink.used == 10U && sink.bytes[10] == 0); require(oc_mem_stage_checked(0) == 1);
    oc_mem_discard(); wiped();
}
void one_byte_over()
{
    void *h = start(9U); sink.bytes.back() = 'Z';
    require(oc_mem_printf(h, "Input:%i:%s\n", 1, "x") == -1); wiped();
    require(oc_mem_printf(h, ">\n") == -1); wiped();
}
void cumulative_overflow()
{
    void *h = start(13U); existing(h); record(oc_mem_printf(h, ">\n"), ">\n");
    require(oc_mem_printf(h, "Count:%i\n", 1) == -1); wiped();
}
void full_then_write()
{
    void *h = start(2U); record(oc_mem_printf(h, ">\n"), ">\n");
    require(oc_mem_printf(h, ">\n") == -1); wiped();
}
void minimum_cap()
{
    void *h = start(1U); require(oc_mem_printf(h, ">\n") == -1); wiped();
}
void maximum_cap()
{
    void *h = start(OC_MEM_MAX_BYTES);
    const std::string name(OC_MEM_MAX_BYTES - 9U, 'a'); // Test fixture <=1MiB.
    const int n = oc_mem_printf(h, "Input:%i:%s\n", 1, name.c_str());
    require(n == static_cast<int>(OC_MEM_MAX_BYTES) && sink.used == OC_MEM_MAX_BYTES);
    require(std::memcmp(sink.bytes.data(), "Input:1:", 8U) == 0);
    require(std::memcmp(sink.bytes.data() + 8U, name.data(), name.size()) == 0);
    require(sink.bytes[OC_MEM_MAX_BYTES - 1U] == '\n' && sink.bytes[OC_MEM_MAX_BYTES] == 0);
    require(oc_mem_stage_checked(0) == 1); oc_mem_discard(); wiped();
}
void maximum_overflow()
{
    void *h = start(OC_MEM_MAX_BYTES);
    const std::string name(OC_MEM_MAX_BYTES - 8U, 'b'); // Required output cap+1.
    require(oc_mem_printf(h, "Input:%i:%s\n", 1, name.c_str()) == -1); wiped();
}
void utf8_byte_anchors()
{
    Callback callback = &oc_mem_printf; void *h = start();
    const char name[] = "a_\xD0\x96\xF0\x9F\x99\x82.tex"; // Ж + emoji,12 UTF8 bytes total.
    require(std::strlen(name) == 12U);
    const int preamble = callback(h, "SyncTeX Version:%i\n", 1);
    record(preamble, "SyncTeX Version:1\n"); require(preamble == 18);
    const int input = callback(h, "Input:%i:%s\n", 7, name);
    record(input, "Input:7:a_\xD0\x96\xF0\x9F\x99\x82.tex\n"); require(input == 21);
    const int first_anchor = callback(h, "!%i\n", preamble + input);
    record(first_anchor, "!39\n"); require(first_anchor == 4);
    const int content = callback(h, "Content:\n"); record(content, "Content:\n");
    record(callback(h, "!%i\n", first_anchor + content), "!13\n");
    const char expected[] = "SyncTeX Version:1\nInput:7:a_\xD0\x96\xF0\x9F\x99\x82.tex\n!39\nContent:\n!13\n";
    require(sizeof(expected) - 1U == 56U && sink.used == 56U);
    require(std::memcmp(sink.bytes.data(), expected, sizeof(expected)) == 0);
    require(oc_mem_stage_checked(0) == 1); oc_mem_discard(); wiped();
}
void all_canonical_formats()
{
    void *h = start();
    // Typed byte formatter fixtures only, not a coherent/generated SyncTeX map.
    record(oc_mem_printf(h, "}%i\n", 1), "}1\n");
    record(oc_mem_printf(h, "<%i\n", 1), "<1\n");
    record(oc_mem_printf(h, ">\n"), ">\n");
    record(oc_mem_printf(h, "f%i:%i,=\n", 1, 2), "f1:2,=\n");
    record(oc_mem_printf(h, "f%i:%i,%i\n", 1, 2, 3), "f1:2,3\n");
    record(oc_mem_printf(h, "x%i,%i:%i,=\n", 1, 2, 3), "x1,2:3,=\n");
    record(oc_mem_printf(h, "x%i,%i:%i,%i\n", 1, 2, 3, 4), "x1,2:3,4\n");
    record(oc_mem_printf(h, "Output:%s\nMagnification:%i\nUnit:%i\nX Offset:%i\nY Offset:%i\n", "pdf", 1000, 1, 0, 0), "Output:pdf\nMagnification:1000\nUnit:1\nX Offset:0\nY Offset:0\n");
    record(oc_mem_printf(h, "SyncTeX Version:%i\n", 1), "SyncTeX Version:1\n");
    record(oc_mem_printf(h, "Input:%i:%s\n", 1, "file.tex"), "Input:1:file.tex\n");
    record(oc_mem_printf(h, "!%i\n", 1), "!1\n");
    record(oc_mem_printf(h, "Content:\n"), "Content:\n");
    record(oc_mem_printf(h, "{%i\n", 1), "{1\n");
    record(oc_mem_printf(h, "v%i,%i:%i,=:%i,%i,%i\n", 1, 2, 3, 4, 5, 6), "v1,2:3,=:4,5,6\n");
    record(oc_mem_printf(h, "v%i,%i:%i,%i:%i,%i,%i\n", 1, 2, 3, 4, 5, 6, 7), "v1,2:3,4:5,6,7\n");
    record(oc_mem_printf(h, "[%i,%i:%i,=:%i,%i,%i\n", 1, 2, 3, 4, 5, 6), "[1,2:3,=:4,5,6\n");
    record(oc_mem_printf(h, "[%i,%i:%i,%i:%i,%i,%i\n", 1, 2, 3, 4, 5, 6, 7), "[1,2:3,4:5,6,7\n");
    record(oc_mem_printf(h, "]\n"), "]\n");
    record(oc_mem_printf(h, "h%i,%i:%i,=:%i,%i,%i\n", 1, 2, 3, 4, 5, 6), "h1,2:3,=:4,5,6\n");
    record(oc_mem_printf(h, "h%i,%i:%i,%i:%i,%i,%i\n", 1, 2, 3, 4, 5, 6, 7), "h1,2:3,4:5,6,7\n");
    record(oc_mem_printf(h, "(%i,%i:%i,=:%i,%i,%i\n", 1, 2, 3, 4, 5, 6), "(1,2:3,=:4,5,6\n");
    record(oc_mem_printf(h, "(%i,%i:%i,%i:%i,%i,%i\n", 1, 2, 3, 4, 5, 6, 7), "(1,2:3,4:5,6,7\n");
    record(oc_mem_printf(h, ")\n"), ")\n");
    record(oc_mem_printf(h, "Count:%i\n", 1), "Count:1\n");
    record(oc_mem_printf(h, "Postamble:\n"), "Postamble:\n");
    record(oc_mem_printf(h, "Post scriptum:\n"), "Post scriptum:\n");
    record(oc_mem_printf(h, "g%i,%i:%i,=\n", 1, 2, 3), "g1,2:3,=\n");
    record(oc_mem_printf(h, "g%i,%i:%i,%i\n", 1, 2, 3, 4), "g1,2:3,4\n");
    record(oc_mem_printf(h, "k%i,%i:%i,=:%i\n", 1, 2, 3, 4), "k1,2:3,=:4\n");
    record(oc_mem_printf(h, "k%i,%i:%i,%i:%i\n", 1, 2, 3, 4, 5), "k1,2:3,4:5\n");
    record(oc_mem_printf(h, "r%i,%i:%i,=:%i,%i,%i\n", 1, 2, 3, 4, 5, 6), "r1,2:3,=:4,5,6\n");
    record(oc_mem_printf(h, "r%i,%i:%i,%i:%i,%i,%i\n", 1, 2, 3, 4, 5, 6, 7), "r1,2:3,4:5,6,7\n");
    record(oc_mem_printf(h, "$%i,%i:%i,=\n", 1, 2, 3), "$1,2:3,=\n");
    record(oc_mem_printf(h, "$%i,%i:%i,%i\n", 1, 2, 3, 4), "$1,2:3,4\n");
    record(oc_mem_printf(h, "c%i,=\n", 1), "c1,=\n");
    record(oc_mem_printf(h, "c%i,%i\n", 1, 2), "c1,2\n");
    record(oc_mem_printf(h, "?%i,=:%i,%i\n", 1, 2, 3), "?1,=:2,3\n");
    record(oc_mem_printf(h, "?%i,%i:%i,%i\n", 1, 2, 3, 4), "?1,2:3,4\n");
    require(oc_mem_stage_checked(0) == 1); oc_mem_discard(); wiped();
}
struct Case { const char *name; void (*run)(); };
const Case cases[] = {
    {"default-off", default_off}, {"mode-off", mode_off},
    {"invalid-mode", [] { invalid_config(2U, 64U, 1U); }},
    {"zero-pass", [] { invalid_config(1U, 64U, 0U); }},
    {"zero-on-cap", [] { invalid_config(1U, 0U, 1U); }},
    {"off-invalid-pass", [] { invalid_config(0U, 0U, 0U); }},
    {"physical-cap-includes-nul", [] { invalid_config(1U, OC_MEM_STORAGE_BYTES, 1U); }},
    {"max-1mib-plus-one", [] { invalid_config(1U, OC_MEM_STORAGE_BYTES + 1U, 1U); }},
    {"off-over-cap", [] { invalid_config(0U, OC_MEM_STORAGE_BYTES + 1U, 1U); }},
    {"reconfigure-armed", reconfigure_armed}, {"reconfigure-writing", reconfigure_writing},
    {"without-open", without_open}, {"wrong-handle", wrong_handle}, {"null-handle", null_handle},
    {"null-format", null_format}, {"noncanonical-format", noncanonical_format}, {"empty-format", empty_format},
    {"repeat-open", repeat_open}, {"stage-before-open", stage_before_open}, {"stage-before-write", stage_before_write},
    {"postamble-negative", [] { postamble_error(-1); }}, {"postamble-positive", [] { postamble_error(1); }},
    {"after-stage-write", after_stage_write}, {"after-stage-open", after_stage_open}, {"stage-twice", stage_twice},
    {"abort-terminal", abort_terminal}, {"discard-terminal-stale-handle", discard_terminal}, {"discard-armed", discard_armed},
    {"exact-cap", exact_cap}, {"one-byte-over", one_byte_over}, {"cumulative-overflow", cumulative_overflow},
    {"full-then-write", full_then_write}, {"minimum-cap", minimum_cap}, {"maximum-cap", maximum_cap},
    {"maximum-overflow", maximum_overflow}, {"utf8-byte-anchors", utf8_byte_anchors}, {"canonical-38", all_canonical_formats}
};
} // namespace
int main(int argc, char **argv)
{
    if (argc != 2) { std::fputs("Select exactly one case per fresh process, or --list\n", stderr); return 2; }
    if (std::strcmp(argv[1], "--list") == 0) {
        for (const auto &test : cases) std::puts(test.name);
        return 0;
    }
    for (const auto &test : cases) {
        if (std::strcmp(argv[1], test.name) == 0) {
            test.run(); std::printf("PASS %s\n", test.name); return 0;
        }
    }
    std::fputs("Unknown test case\n", stderr); return 2;
}
