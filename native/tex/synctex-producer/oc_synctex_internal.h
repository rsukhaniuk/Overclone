/* Internal engine hooks. NOT WASM exports. Integers/pointers cannot authenticate
 * their caller: only reviewed patched native call sites establish provenance. */
#ifndef OC_SYNCTEX_INTERNAL_H
#define OC_SYNCTEX_INTERNAL_H
#include <stdio.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Called only by compileLaTeX before entering _compile. Returns 0 on reuse;
 * always consumes the single attempt, including empty-root failures. */
int oc_synctex_begin(void);
int oc_synctex_enabled(void);
/* WEB Finish PDF hook: checks ferror/fflush/fclose of that pass's actual stream. */
void oc_synctex_pdf_close(FILE *stream);
/* Called only AFTER actual _compile return; stages alone never suffice. */
void oc_synctex_finish(int actual_native_result);
#ifdef __cplusplus
}
#endif
#endif
