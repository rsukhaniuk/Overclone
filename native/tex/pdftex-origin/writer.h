/* GPL-2.0-or-later. Private observed pdfTeX writer protocol. */
#pragma once
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

void ocpdftexoriginmap(int pdf, int mag, int unit, int x, int y);
void ocpdftexoriginbegin(int page, int origin_h, int origin_v, int mag, int hundred_bp);
void oc_pdftex_origin_page_bytes(int page, const unsigned char *bytes, size_t length, int suppress_builtin);
void ocpdftexoriginbp(int printed, int decimal_digits);
void ocpdftexoriginend(int page);
void ocpdftexoriginroot(int pages, size_t attributes_length);
void oc_pdftex_origin_literal_bytes(const unsigned char *bytes, size_t length);
void ocpdftexoriginunsafe(void);

uint32_t oc_pdftex_origin_abi(void);
uint32_t oc_pdftex_legacy_zero_origin(uint32_t token);
uint32_t oc_synctex_geometry_length(uint32_t token);
uint32_t oc_synctex_geometry_copy(uint32_t token, unsigned char *destination, uint32_t capacity);

#ifdef __cplusplus
}
#endif

/* The generated TeX sees constant integer offsets into its actual string pool.
 * Keep the pool itself out of the observer surface. */
#define ocpdftexoriginpage(page, start, length, suppress_builtin) \
    oc_pdftex_origin_page_bytes(page, (length) ? strpool + (start) : NULL, length, suppress_builtin)
#define ocpdftexoriginliteral(start, length) \
    oc_pdftex_origin_literal_bytes(strpool + (start), length)
