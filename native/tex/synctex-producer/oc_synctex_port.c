/* Owned glue only: no record generation, synthetic node layouts or parser. */
#define EXTERN extern
#include "pdftexd.h"
#include "oc_synctex_memory_sink.h"
char *oc_synctex_current_name(void)
{
    const char *name = makecstring(fullsourcefilenamestack[inopen]);
    const size_t n = strlen(name);
    char *copy = (char *)malloc(n + 1);
    if (!copy) { oc_mem_abort(); uexit(1); return NULL; }
    memcpy(copy, name, n + 1);
    return copy;
}
