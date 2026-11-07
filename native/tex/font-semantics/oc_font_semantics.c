/* GPL-2.0-or-later, like the pdfTeX source this helper extends.
 * No source-string correlation, changed character fields, or input whatsits.
 * The 5,000,001-byte table matches the pinned main_memory=5,000,000 port.
 */
#include "ptexlib.h"
#include "oc_font_semantics.h"
#define OC_NODE_LIMIT 5000001
#define OC_UPPER 2031689 /* distinct char_given meaning: 0x1f0049 */
#define OC_LOWER 2031721 /* 0x1f0069; never accepted by scan_char_num */
static unsigned char semantic[OC_NODE_LIMIT], hyphen[64], saved[2], current;
static uint32_t live, failures, spans, copies, reconstructions;
static char span[640], special[680];
static int span_length, special_length;
static int node_index(int n) { return n >= 0 && n < OC_NODE_LIMIT; }
static int char_node(int n) {
    return node_index(n) && n >= himemmin && n <= memend;
}
static int tag_matches(int n, unsigned char tag) {
    return char_node(n) && ((tag == 1 && zmem[n].hh.b1 == 73) ||
                               (tag == 2 && zmem[n].hh.b1 == 105));
}
static void put(int n, unsigned char tag) {
    if (!node_index(n)) { ++failures; return; }
    if (tag && !tag_matches(n, tag)) { ++failures; tag = 0; }
    if (semantic[n]) --live;
    semantic[n] = tag;
    if (tag) ++live;
}
int oc_sem_decode(int command, int value) {
    current = 0;
    if (command == 68 && value == OC_UPPER) { current = 1; return 73; }
    if (command == 68 && value == OC_LOWER) { current = 2; return 105; }
    return value;
}
int oc_sem_meaning(int command, int value) {
    if (command == 68 && current == 1 && value == 73) return OC_UPPER;
    if (command == 68 && current == 2 && value == 105) return OC_LOWER;
    return value;
}
void oc_sem_clear(int n) { put(n, 0); }
void oc_sem_attach(int n) { put(n, current); }
void oc_sem_copy(int target, int source) {
    if (!char_node(source)) { ++failures; put(target, 0); return; }
    unsigned char tag = semantic[source];
    put(target, tag);
    if (tag) ++copies;
}
void oc_sem_hsave(int i, int n) {
    if (i < 0 || i > 63 || !char_node(n)) { ++failures; return; }
    hyphen[i] = semantic[n];
}
void oc_sem_hrestore(int n, int i) {
    if (i < 0 || i > 63) { ++failures; return; }
    put(n, hyphen[i]);
    if (hyphen[i]) ++reconstructions;
}
void oc_sem_hclear(int i) {
    if (i < 0 || i > 63) { ++failures; return; }
    hyphen[i] = 0;
}
void oc_sem_hstash(int i, int slot) {
    if (i < 0 || i > 63 || slot < 0 || slot > 1) { ++failures; return; }
    saved[slot] = hyphen[i]; hyphen[i] = 0;
}
void oc_sem_hunstash(int i, int slot) {
    if (i < 0 || i > 63 || slot < 0 || slot > 1) { ++failures; return; }
    hyphen[i] = saved[slot]; saved[slot] = 0;
}
/* TeXXeT reversal discards ligature components. Refuse an unsupported semantic
 * claim while preserving the exact original TeX operation and glyph layout. */
void oc_sem_reverse(int n) {
    for (int k = 0; n > -268435455 && k < 64; ++k) {
        if (!char_node(n)) { ++failures; return; }
        if (semantic[n]) { ++failures; return; }
        n = zmem[n].hh.v.RH;
    }
    if (n > -268435455) ++failures;
}
int oc_sem_prepare(int n, int components) {
    span_length = 0;
    if (!char_node(n)) { ++failures; return 0; }
    int first = components > -268435455 ? components : n;
    int cursor = first, count = 0, marked = 0, same_font = 1;
    unsigned int values[64];
    do {
        if (count == 64 || !char_node(cursor)) { ++failures; return 0; }
        same_font &= zmem[cursor].hh.b0 == zmem[n].hh.b0;
        const unsigned char tag = semantic[cursor];
        const unsigned int ch = zmem[cursor].hh.b1;
        if (tag && !tag_matches(cursor, tag)) { ++failures; return 0; }
        values[count++] = tag == 1 ? 0x0406 : tag == 2 ? 0x0456 : ch;
        marked |= tag;
        if (components <= -268435455) break;
        cursor = zmem[cursor].hh.v.RH;
    } while (cursor > -268435455);
    if (!marked) return 0;
    if (!same_font) { ++failures; return 0; }
    /* Untagged ligature components can be proven directly only for ASCII.
     * Refuse unsupported custom-font combinations; never guess their Unicode. */
    for (int k = 0; k < count; ++k)
        if (!(values[k] >= 32 && values[k] <= 126) &&
            values[k] != 0x0406 && values[k] != 0x0456) {
            ++failures; return 0;
        }
    span_length = snprintf(span, sizeof span, "/Span << /ActualText <FEFF");
    for (int k = 0; k < count; ++k)
        span_length += snprintf(span + span_length, sizeof span - span_length,
                                "%04X", values[k]);
    span_length += snprintf(span + span_length, sizeof span - span_length, "> >> BDC");
    ++spans;
    return 1;
}
void oc_sem_pdf_begin(void) { pdf_puts("\n"); pdf_puts(span); pdf_puts("\n"); }
void oc_sem_pdf_end(void) { pdf_puts("\nEMC\n"); }
int oc_sem_dvi_length(int ending) {
    special_length = snprintf(special, sizeof special, "pdf:literal direct %s",
                              ending ? "EMC" : span);
    return special_length;
}
int oc_sem_dvi_byte(int i) {
    if (i < 0 || i >= special_length) { ++failures; return 0; }
    return (unsigned char)special[i];
}
int oc_sem_dump_safe(void) { return live == 0; }
uint32_t oc_font_semantics_abi(void) { return 1; }
uint32_t oc_font_semantics_status(void) { return failures ? 2 : 1; }
uint32_t oc_font_semantics_spans(void) { return spans; }
uint32_t oc_font_semantics_copies(void) { return copies; }
uint32_t oc_font_semantics_reconstructions(void) { return reconstructions; }
uint32_t oc_font_semantics_current(void) { return current; }
