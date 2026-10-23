/* Owned browser-port bindings; node access stays in upstream pdfTeX header. */
#include "synctexdir/synctex-pdftex.h"
#include "oc_synctex_memory_sink.h"
#include "oc_synctex_internal.h"
/* Upstream expects a newly allocated current input name; makecstring is a
 * reusable port buffer, so the bridge copies that exact recorded source name. */
extern char *oc_synctex_current_name(void);
#define SYNCTEX_GET_CURRENT_NAME() oc_synctex_current_name()
