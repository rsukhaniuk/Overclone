/* Component test ONLY, not real producer acceptance. Include the actual patched
 * upstream postamble/terminate implementation; linker drops unused engine hooks.
 * No node callbacks, invented engine structs, or counterfeit production map. */
#include "synctexdir/synctex.c"
#include "oc_synctex_producer.h"
#include <assert.h>
#include <string.h>

static void absent(void)
{
    unsigned char sentinel[64];
    memset(sentinel, 0xa5, sizeof sentinel);
    assert(oc_synctex_length(77) == 0);
    assert(oc_synctex_copy(77, sentinel, sizeof sentinel) == 0);
    for (size_t i = 0; i < sizeof sentinel; ++i) assert(sentinel[i] == 0xa5);
}

int main(int argc, char **argv)
{
    assert(argc == 2);
    const char *test = argv[1];
    const int disabled = strcmp(test, "disabled") == 0;
    const int limited = strcmp(test, "limit") == 0;
    assert(oc_synctex_abi() == OC_SYNCTEX_ABI);
    assert(oc_mem_configure(disabled ? 0 : 1, disabled ? 0 : limited ? 8 : 4096, 77));
    assert(oc_synctex_begin());
    absent();
    if (disabled) {
        oc_synctex_finish(0);
        absent();
        assert(oc_synctex_status() == 0);
        assert(!oc_synctex_begin());
        absent();
        return 0;
    }
    SYNCTEX_FILE = oc_mem_open();
    assert(SYNCTEX_FILE);
    synctex_ctxt.fprintf = oc_mem_printf;
    synctex_ctxt.flags.not_void = 1;
    assert(synctex_record_preamble() == (limited ? -1 : 0));
    if (limited) {
        synctexterminate(1);
        oc_synctex_finish(0);
        absent();
        assert(oc_synctex_status() == 2);
        return 0;
    }
    if (strcmp(test, "no-postamble") != 0) synctexterminate(1);
    absent(); /* actual successful postamble is insufficient */
    if (strcmp(test, "postamble-only") == 0) {
        oc_synctex_finish(0);
        absent();
        assert(oc_synctex_status() == 2);
        return 0;
    }
    FILE *pdf = strcmp(test, "pdf-error") == 0 ? fopen("/dev/full", "wb") : tmpfile();
    assert(pdf);
    /* This is a stream-close test, NOT a PDF validity/producer fixture. The WEB
     * hook provenance is checked separately on regenerated Finish PDF code. */
    assert(fputs("stream-test", pdf) >= 0);
    oc_synctex_pdf_close(pdf);
    absent();
    if (strcmp(test, "abort") == 0) oc_mem_abort();
    if (strcmp(test, "discard") == 0) oc_mem_discard();
    oc_synctex_finish(strcmp(test, "native-error") == 0 ? 1 : 0);
    if (!strcmp(test, "pdf-error") || !strcmp(test, "native-error") ||
        !strcmp(test, "no-postamble") || !strcmp(test, "abort") || !strcmp(test, "discard")) {
        absent();
        assert(oc_synctex_status() == 2);
        return 0;
    }
    assert(oc_synctex_status() == 1);
    unsigned char result[4096];
    memset(result, 0xa5, sizeof result);
    const uint32_t n = oc_synctex_length(77);
    assert(n > 0 && n < sizeof result);
    assert(oc_synctex_length(78) == 0);
    assert(oc_synctex_copy(78, result, sizeof result) == 0);
    assert(oc_synctex_copy(77, result, n - 1) == 0);
    assert(result[0] == 0xa5);
    assert(oc_synctex_copy(77, result, sizeof result) == n);
    assert(result[n] == 0xa5); /* no NUL or extra bytes copied */
    if (!strcmp(test, "stale-attempt")) {
        assert(!oc_synctex_begin());
        absent();
        assert(oc_synctex_status() == 2);
    }
    if (!strcmp(test, "reconfigure")) {
        assert(!oc_mem_configure(1, 4096, 78));
        absent();
        assert(oc_synctex_status() == 2);
    }
    return 0;
}
