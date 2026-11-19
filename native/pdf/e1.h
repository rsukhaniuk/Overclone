#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
// WASM32, C ABI v1. All handles uint32_t, never pointer identities. 0 is invalid.
// All lengths are bytes unless explicitly UTF-16 units or PDF character indexes.
// Native coordinates: unrotated RAW DEFAULT-USER-SPACE units, bottom-left.
// NOT universally physical 1/72-inch points: UserUnit is not exposed by these
// cached public headers. Legacy numeric fields/algorithms remain unchanged.
// Display coordinates: effective MediaBox/CropBox intersection, intrinsic clockwise
// rotation, top-left pixels at caller raw-unit scale; source mapping unsupported.
// Physical measurement is INDETERMINATE; do not treat this as KNU geometry proof.
enum E1Status { E1_OK=0, E1_ARGUMENT=1, E1_ABI=2, E1_LIMIT=3, E1_HANDLE=4,
 E1_PDF=5, E1_UNSUPPORTED=6, E1_MEMORY=7, E1_INDETERMINATE=8 };
typedef struct E1Budget { uint32_t version,struct_bytes,input_bytes,pages,text_units,
 search_hits,raster_pixels,output_bytes,live_handles; } E1Budget;
// Budget exact 36-byte LE struct, version=1. Every limit positive and <= hard caps.
// Inputs: allocated writable owned buffers. data() exposes ONLY input allocation;
// no operation accepts a raw payload pointer. Open copies input and budget. Caller
// mutation/release cannot change an open document. Immutable output handles need
// copy() into a caller input buffer; outputs survive page/document close until
// release/reset. No borrowed native pointers/text/bitmap handles cross the ABI.
uint32_t e1_version(void);
#if defined(E1_PDF_INPUT_CAPACITY_12_MIB)
// Optional compiled capacity for trusted variant selection. Existing budgets
// keep their caller-selected ceiling, including the default 8 MiB.
uint32_t e1_input_bytes_limit(void);
#endif
uint32_t e1_status(void);
uint32_t e1_native_error(void);
uint32_t e1_result(void);
uint32_t e1_live(void);
uint32_t e1_alloc(uint32_t bytes);
uintptr_t e1_data(uint32_t input);
uint32_t e1_size(uint32_t buffer);
uint32_t e1_copy(uint32_t output,uint32_t destination);
uint32_t e1_release(uint32_t buffer);
uint32_t e1_open(uint32_t input,uint32_t budget);
uint32_t e1_document_pages(uint32_t document);
uint32_t e1_page(uint32_t document,uint32_t index);
uint32_t e1_close(uint32_t document_or_page);
uint32_t e1_facts(uint32_t page);
uint32_t e1_text(uint32_t page);
uint32_t e1_search(uint32_t page,uint32_t utf16le_query);
// C1 coordinate policy: finite args/positive scale<=100; rounded viewport <=1e6.
// Signed32 device/viewport-endpoint overflow -> E1_LIMIT before native call/cast.
// Conservative precision domain: viewport endpoints and requested/transformed
// device coordinates (including rounding) within +/-1048576 pixels; otherwise
// E1_INDETERMINATE. Off-page/negative/fractional coordinates remain admissible.
// E1_OK point results additionally pass native agreement <=0.51 pixels per axis
// in device space (including inverse integer input quantization). Float native
// matrices are NOT promised equal to arbitrary double analytic transforms.
// Native nonfinite/excess error -> INDETERMINATE, result=0, no output allocation.
// Rectangles use the same device domain, with finite analytic corners; no native
// conversion/precision equivalence is claimed for their analytic-only output.
uint32_t e1_point(uint32_t page,uint32_t inverse,double x,double y,
 int32_t origin_x,int32_t origin_y,double scale);
uint32_t e1_rectangle(uint32_t page,uint32_t inverse,double left,double bottom,
 double right,double top,int32_t origin_x,int32_t origin_y,double scale);
// Immutable raster: LE uint32 magic=0x31424752,width,height,stride then tightly
// packed BGRA rows, top-first. White opaque background, render flags=0.
uint32_t e1_raster(uint32_t page,uint32_t width,uint32_t height);
// Optional additive v1 exports: discover symbols before calling on an old runtime.
// Both require a valid page and publish immutable JSON through the SAME owned
// result/copy/release mechanism and caller output/live-handle budgets.
uint32_t e1_capabilities(uint32_t page);
// Read-only passive metadata. No action execution/navigation/file opening.
// Max 128 links, max 4096 native bytes per string INCLUDING trailing NUL.
// Two bounded serialization passes preflight exact JSON bytes before allocation.
// Excess -> E1_LIMIT/result0, no partial output. Invalid UTF8 is explicit field
// status with null value, not truncation/replacement. Rectangles are raw units.
// Locality is action-kind gated: types2/5 are not-local/null regardless of raw
// getter integers. Only local GoTo1 or absent-action link destinations can be
// available locally; unrelated/unknown action kinds cannot supply local pages.
uint32_t e1_actions(uint32_t page);
// Optional additive layout schema1; legacy ABI/budget remain unchanged. These
// immutable outputs never reinterpret character boxes as painted glyph ink.
uint32_t e1_layout_capabilities(uint32_t page);
uint32_t e1_layout_facts(uint32_t page);
void e1_reset(void);
#ifdef __cplusplus
}
#endif
