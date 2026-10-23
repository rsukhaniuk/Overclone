/* Owned bounded formatter plus internal success sealing. The upstream producer
 * and canonical WEB hooks generate records; this component does not model them. */
#include "oc_synctex_memory_sink.h"
#include "oc_synctex_producer.h"
#include "oc_synctex_internal.h"
#include <array>
#include <cstdarg>
#include <cstddef>
#include <cstdio>
#include <cstring>

namespace {
enum class SinkState { fresh, off, armed, writing, staged, sealed, failed, discarded };
bool attempted = false;
bool pdf_closed_ok = false;
struct Sink {
    std::array<char, OC_MEM_STORAGE_BYTES> bytes{}; // Exactly1MiB static/BSS.
    uint32_t capacity = 0;
    uint32_t used = 0;
    uint32_t active_pass = 0;
    SinkState state = SinkState::fresh; // Disabled until explicit configuration.
    unsigned char private_handle = 0;
};
Sink sink;

// Fixed literals ONLY, from the38 historical writer sites. This is a format
// spelling guard, NOT origin/argument-type authentication or record generation.
constexpr const char *canonical_formats[] = {
    "}%i\n", "<%i\n", ">\n", "f%i:%i,=\n", "f%i:%i,%i\n",
    "x%i,%i:%i,=\n", "x%i,%i:%i,%i\n",
    "Output:%s\nMagnification:%i\nUnit:%i\nX Offset:%i\nY Offset:%i\n",
    "SyncTeX Version:%i\n", "Input:%i:%s\n", "!%i\n", "Content:\n", "{%i\n",
    "v%i,%i:%i,=:%i,%i,%i\n", "v%i,%i:%i,%i:%i,%i,%i\n",
    "[%i,%i:%i,=:%i,%i,%i\n", "[%i,%i:%i,%i:%i,%i,%i\n", "]\n",
    "h%i,%i:%i,=:%i,%i,%i\n", "h%i,%i:%i,%i:%i,%i,%i\n",
    "(%i,%i:%i,=:%i,%i,%i\n", "(%i,%i:%i,%i:%i,%i,%i\n", ")\n", "Count:%i\n",
    "Postamble:\n", "Post scriptum:\n", "g%i,%i:%i,=\n", "g%i,%i:%i,%i\n",
    "k%i,%i:%i,=:%i\n", "k%i,%i:%i,%i:%i\n",
    "r%i,%i:%i,=:%i,%i,%i\n", "r%i,%i:%i,%i:%i,%i,%i\n",
    "$%i,%i:%i,=\n", "$%i,%i:%i,%i\n", "c%i,=\n", "c%i,%i\n",
    "?%i,=:%i,%i\n", "?%i,%i:%i,%i\n"
};
static_assert(sizeof(canonical_formats) / sizeof(canonical_formats[0]) == 38,
              "Historical format-site correspondence changed");
static_assert(sizeof(sink.bytes) == OC_MEM_STORAGE_BYTES, "Physical byte ceiling");

void wipe() noexcept
{
    // Volatile stores retain wiping, including partially formatted/truncated data
    // and bytes outside current used/capacity. No dynamic formatting allocation.
    volatile char *p = sink.bytes.data();
    for (std::size_t i = 0; i < sink.bytes.size(); ++i) p[i] = 0;
    sink.used = 0;
    sink.active_pass = 0;
}
void fail() noexcept
{
    wipe();
    if (sink.state != SinkState::discarded) sink.state = SinkState::failed;
}
bool canonical(const char *format) noexcept
{
    for (const char *allowed : canonical_formats)
        if (std::strcmp(format, allowed) == 0) return true;
    return false;
}
} // namespace; internal state is visible ONLY to a test TU including this file.

extern "C" int oc_mem_configure(uint32_t raw_mode, uint32_t cap, uint32_t pass_token)
{
    if (attempted || sink.state != SinkState::fresh || raw_mode > 1U || cap > OC_MEM_MAX_BYTES ||
        pass_token == 0U || (raw_mode == 1U && cap == 0U)) {
        fail(); return 0;
    }
    sink.capacity = cap;
    sink.active_pass = pass_token;
    sink.state = raw_mode == 0U ? SinkState::off : SinkState::armed;
    return 1;
}
extern "C" void *oc_mem_open(void)
{
    if ((sink.state != SinkState::armed && sink.state != SinkState::writing) || sink.active_pass == 0U) {
        fail(); return nullptr;
    }
    sink.state = SinkState::writing;
    return &sink.private_handle;
}
extern "C" int oc_mem_printf(void *token, const char *format, ...)
{
    if (sink.state != SinkState::writing || sink.active_pass == 0U ||
        token != &sink.private_handle || format == nullptr || sink.used > sink.capacity ||
        !canonical(format)) {
        fail(); return -1;
    }
    const uint32_t remaining = sink.capacity - sink.used;
    std::va_list args;
    va_start(args, format);
    const int count = std::vsnprintf(sink.bytes.data() + sink.used,
                                    static_cast<std::size_t>(remaining) + 1U, format, args);
    va_end(args);
    // Required-but-unwritten counts MUST NOT escape. Negative/zero, truncation
    // and cumulative overflow all invalidate the pass and wipe even partial data.
    if (count <= 0 || static_cast<uintmax_t>(count) > remaining) {
        fail(); return -1;
    }
    sink.used += static_cast<uint32_t>(count); // Proven <=configured cap<1MiB.
    return count;
}
extern "C" void oc_mem_abort(void) { fail(); }
extern "C" int oc_mem_stage_checked(int actual_postamble_return)
{
    if (actual_postamble_return != 0 || sink.state != SinkState::writing ||
        sink.active_pass == 0U || sink.used == 0U) {
        fail(); return 0;
    }
    sink.state = SinkState::staged;
    return 1; // No PDF/native-success check and NO read/publication permission.
}
extern "C" void oc_mem_discard(void)
{
    wipe(); sink.state = SinkState::discarded;
}

extern "C" int oc_synctex_begin(void)
{
    if (attempted) { fail(); return 0; }
    attempted = true;
    pdf_closed_ok = false;
    // No configuration means PDF-only. Still consumes the module's attempt.
    if (sink.state == SinkState::fresh) sink.state = SinkState::off;
    return 1;
}
extern "C" int oc_synctex_enabled(void)
{
    return sink.state == SinkState::armed || sink.state == SinkState::writing;
}
extern "C" void oc_synctex_pdf_close(FILE *stream)
{
    if (!attempted || !stream || pdf_closed_ok) {
        fail();
        if (stream) std::fclose(stream);
        return;
    }
    const int prior_error = std::ferror(stream);
    const int flushed = std::fflush(stream);
    const int final_error = std::ferror(stream);
    const int closed = std::fclose(stream);
    pdf_closed_ok = prior_error == 0 && flushed == 0 && final_error == 0 && closed == 0;
    if (!pdf_closed_ok) fail();
}
extern "C" void oc_synctex_finish(int actual_native_result)
{
    if (!attempted || actual_native_result != 0) { fail(); return; }
    if (sink.state == SinkState::off) return;
    if (sink.state != SinkState::staged || !pdf_closed_ok) { fail(); return; }
    sink.state = SinkState::sealed;
}
extern "C" uint32_t oc_synctex_abi(void) { return OC_SYNCTEX_ABI; }
extern "C" uint32_t oc_synctex_status(void)
{
    if (sink.state == SinkState::sealed) return 1;
    if (sink.state == SinkState::failed || sink.state == SinkState::discarded) return 2;
    return 0;
}
extern "C" uint32_t oc_synctex_length(uint32_t pass_tag)
{
    return sink.state == SinkState::sealed && pass_tag != 0 && pass_tag == sink.active_pass
        ? sink.used : 0;
}
extern "C" uint32_t oc_synctex_copy(uint32_t pass_tag, unsigned char *dst, uint32_t capacity)
{
    const uint32_t n = oc_synctex_length(pass_tag);
    if (!n || !dst || capacity < n || capacity > OC_MEM_MAX_BYTES) return 0;
    std::memcpy(dst, sink.bytes.data(), n);
    return n;
}
