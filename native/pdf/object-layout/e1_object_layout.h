#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
// Optional independent object-facts schema 1; E1 ABI 1/budget remain unchanged.
// Direct-image placement relative to parsed native render matrices, never ink,
// role/association or ideal source-CTM proof. Discover before calling old E1.
// Existing immutable result/copy/release ownership and worker-reset cancellation.
uint32_t e1_object_layout_facts(uint32_t page);
#ifdef __cplusplus
}
#endif
