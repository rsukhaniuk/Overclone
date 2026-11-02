#ifndef OC_DVI_ABI
#define OC_DVI_ABI 1
#endif
#include "oc_dvi.h"
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
#include <cmath>
#endif
extern "C" {
#include "core-bridge.h"
#include "md5.h"
}
#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <map>
#include <string>
#include <vector>

namespace {
constexpr size_t cap = 8 * 1024 * 1024, resource_cap = 96 * 1024 * 1024;
constexpr size_t file_cap = 32 * 1024 * 1024, log_cap = 256 * 1024;
struct File { std::vector<uint8_t> bytes; };
struct Handle { const File *file; size_t pos; int pushed; bool closed; };
std::map<std::string, File> project, resources;
std::deque<Handle> handles;
std::vector<uint8_t> pdf, log_bytes;
size_t project_bytes = 0, resource_bytes = 0;
bool used = false, limited = false, truncated = false, output_opened = false;
int status = 0, native_return = -1;
std::string missing;
int pdf_token, log_token;
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
constexpr uint32_t geo_cap=131072, geo_pages=512;
struct GeoPage { double m[6], box[4]; bool available; };
GeoPage measured[geo_pages] = {};
char geo_bytes[geo_cap] = {};
uint32_t geo_count=0, geo_size=0, geo_tag=0;
bool geo_configured=false, geo_enabled=false, geo_invalid=false;
bool geo_active=false, geo_sequence=true, geo_unsafe=false, pdf_closed=false;
bool geo_pending_put=false, geo_put_checked=false;
double geo_unit=0, geo_header_mag=0;
int geo_kind=0;
void geo_revoke() { geo_size=0; geo_enabled=false; }
bool coord(double n) { return std::isfinite(n) && std::abs(n)<=1000000; }
bool geo_authority() { return used && status==0 && native_return==0 && pdf_closed && !limited && !pdf.empty() && geo_enabled && !geo_invalid; }
uint32_t geo_serialize() {
    if (!geo_authority() || geo_active || !geo_sequence || !geo_count || geo_count>geo_pages) return 0;
    if (geo_size) return geo_size;
    int n=std::snprintf(geo_bytes,sizeof(geo_bytes),"{\"schemaVersion\":1,\"pages\":[");
    if (n<=0) return 0;
    uint32_t used_bytes=static_cast<uint32_t>(n);
    for (uint32_t i=0;i<geo_count;++i) {
        const GeoPage &p=measured[i];
        if (p.available && !geo_unsafe) n=std::snprintf(geo_bytes+used_bytes,sizeof(geo_bytes)-used_bytes,
            "%s{\"page\":%u,\"status\":\"available\",\"reason\":\"none\",\"writerToPdfRaw\":[%.17g,%.17g,%.17g,%.17g,%.17g,%.17g],\"writerPageBoxBp\":[%.17g,%.17g,%.17g,%.17g]}",
            i?",":"",i+1,p.m[0],p.m[1],p.m[2],p.m[3],p.m[4],p.m[5],p.box[0],p.box[1],p.box[2],p.box[3]);
        else n=std::snprintf(geo_bytes+used_bytes,sizeof(geo_bytes)-used_bytes,
            "%s{\"page\":%u,\"status\":\"unsupported\",\"reason\":\"out-of-domain\",\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}",i?",":"",i+1);
        if (n<=0 || static_cast<uint32_t>(n)>=sizeof(geo_bytes)-used_bytes) return 0;
        used_bytes+=static_cast<uint32_t>(n);
    }
    if (used_bytes+2>=sizeof(geo_bytes)) return 0;
    std::memcpy(geo_bytes+used_bytes,"]}",3);geo_size=used_bytes+2;
    return geo_size;
}
#endif

bool path_ok(const char *s, bool flat = false) {
    if (!s) return false;
    size_t n = strnlen(s, 250);
    if (!n || n > 249) return false;
    std::string part;
    for (size_t i = 0; i <= n; ++i) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        if (i == n || c == '/') {
            if (part.empty() || part == "." || part == "..") return false;
            if (flat && i != n) return false;
            part.clear();
        } else {
            if (c < 32 || c == 127 || c == '\\' || c == ':' || c == '|') return false;
            part += static_cast<char>(c);
        }
    }
    // Validate UTF-8 scalar sequences; names are not normalized or rewritten.
    for (size_t i = 0; i < n;) {
        unsigned char c = static_cast<unsigned char>(s[i++]);
        if (c < 128) continue;
        unsigned count = c >= 0xc2 && c <= 0xdf ? 1 : c >= 0xe0 && c <= 0xef ? 2 : c >= 0xf0 && c <= 0xf4 ? 3 : 0;
        if (!count || i + count > n) return false;
        uint32_t cp = c & ((1u << (6 - count)) - 1);
        for (unsigned j = 0; j < count; ++j) {
            unsigned char b = static_cast<unsigned char>(s[i++]);
            if ((b & 0xc0) != 0x80) return false;
            cp = (cp << 6) | (b & 63);
        }
        if ((count == 1 && cp < 0x80) || (count == 2 && cp < 0x800) ||
            (count == 3 && cp < 0x10000) || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) return false;
    }
    return true;
}
void append_log(const char *data, size_t n) {
    size_t take = std::min(n, log_cap - log_bytes.size());
    log_bytes.insert(log_bytes.end(), data, data + take);
    truncated |= take != n;
}
void message(void *, const char *text) {
    append_log(text, strnlen(text, 4096));
    append_log("\n", 1);
}
const File *lookup(const char *name, tt_input_format_type format) {
    // The only absolute spelling is the agreed virtual runtime font root.
    // It never falls back to project files or the host filesystem.
    if (name && !strncmp(name, "/tex/", 5)) {
        if (!path_ok(name + 5, true)) return nullptr;
        auto r = resources.find(name + 5);
        return r == resources.end() ? nullptr : &r->second;
    }
    if (!path_ok(name)) return nullptr;
    auto p = project.find(name);
    if (p != project.end()) return &p->second;
    auto r = resources.find(name);
    if (r != resources.end()) return &r->second;
    const char *suffix = nullptr;
    switch (format) {
    case TTIF_TFM: suffix = ".tfm"; break;
    case TTIF_OFM: suffix = ".ofm"; break;
    case TTIF_TYPE1: suffix = ".pfb"; break;
    case TTIF_VF: suffix = ".vf"; break;
    case TTIF_ENC: suffix = ".enc"; break;
    case TTIF_FONTMAP: suffix = ".map"; break;
    case TTIF_TRUETYPE: suffix = ".ttf"; break;
    case TTIF_OPENTYPE: suffix = ".otf"; break;
    default: break;
    }
    if (suffix && !strchr(name, '.')) {
        std::string extended = std::string(name) + suffix;
        p = project.find(extended); if (p != project.end()) return &p->second;
        r = resources.find(extended); if (r != resources.end()) return &r->second;
    }
    return nullptr;
}
void *input_open(void *, const char *name, tt_input_format_type format, int gz) {
    const File *f = gz ? nullptr : lookup(name, format);
    if (!f) {
        if (missing.empty()) missing = path_ok(name) || (name && !strncmp(name, "/tex/", 5) && path_ok(name + 5, true)) ? name : "[rejected path]";
        return nullptr;
    }
    if (handles.size() >= 8192) { limited = true; return nullptr; }
    handles.push_back({f, 0, -1, false});
    return &handles.back();
}
Handle *handle(void *h) { return static_cast<Handle *>(h); }
size_t input_size(void *, void *h) { return handle(h)->file->bytes.size(); }
size_t input_seek(void *, void *h, ssize_t off, int whence, int *err) {
    Handle &v = *handle(h);
    int64_t base = whence == SEEK_SET ? 0 : whence == SEEK_CUR ? static_cast<int64_t>(v.pos) : whence == SEEK_END ? static_cast<int64_t>(v.file->bytes.size()) : -1;
    if (v.closed || base < 0 || off < -base || off > static_cast<int64_t>(v.file->bytes.size()) - base) { *err = 1; return 0; }
    v.pos = static_cast<size_t>(base + off); v.pushed = -1; *err = 0;
    return v.pos;
}
ssize_t input_read(void *, void *h, char *data, size_t n) {
    Handle &v = *handle(h);
    if (v.closed) return -1;
    size_t done = 0;
    if (n && v.pushed >= 0) { *data++ = static_cast<char>(v.pushed); v.pushed = -1; ++v.pos; --n; ++done; }
    size_t take = std::min(n, v.file->bytes.size() - v.pos);
    if (take) memcpy(data, v.file->bytes.data() + v.pos, take);
    v.pos += take; return static_cast<ssize_t>(done + take);
}
int input_getc(void *ctx, void *h) { unsigned char c; return input_read(ctx, h, reinterpret_cast<char *>(&c), 1) == 1 ? c : EOF; }
int input_ungetc(void *, void *h, int c) {
    Handle &v = *handle(h);
    if (v.closed || c == EOF || v.pos == 0 || v.pushed >= 0) return EOF;
    --v.pos; v.pushed = static_cast<unsigned char>(c); return v.pushed;
}
int input_close(void *, void *h) { handle(h)->closed = true; return 0; }
void *output_open(void *, const char *name, int gz) {
    if (gz || strcmp(name, "result.pdf") || output_opened) return nullptr;
    output_opened = true; return &pdf_token;
}
void *output_stdout(void *) { return &log_token; }
size_t output_write(void *, void *h, const char *data, size_t n) {
    if (h == &log_token) { append_log(data, n); return n; }
    if (h != &pdf_token || limited) return 0;
    if (n > cap - pdf.size()) { limited = true; return 0; }
    pdf.insert(pdf.end(), data, data + n); return n;
}
int output_putc(void *ctx, void *h, int c) { char b = static_cast<char>(c); return output_write(ctx, h, &b, 1) == 1 ? static_cast<unsigned char>(c) : EOF; }
int output_flush(void *, void *) { return limited ? -1 : 0; }
int output_close(void *, void *h) {
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
    if (h==&pdf_token && !limited) pdf_closed=true;
#endif
    return limited ? -1 : 0;
}
int data_md5(void *, const char *data, size_t n, char *digest) {
    md5_state_t m; md5_init(&m);
    md5_append(&m, reinterpret_cast<const md5_byte_t *>(data), static_cast<int>(n));
    md5_finish(&m, reinterpret_cast<md5_byte_t *>(digest)); return 0;
}
int file_md5(void *ctx, const char *name, char *digest) {
    const File *f = lookup(name, TTIF_BINARY);
    if (!f) return -1;
    return data_md5(ctx, reinterpret_cast<const char *>(f->bytes.data()), f->bytes.size(), digest);
}
}
// All retained printf calls are redirected here at compile time. No unbounded
// native stdout, and no native file/process/network hooks are provided.
extern "C" int oc_dvi_printf(const char *fmt, ...) {
    char buf[1024]; va_list args; va_start(args, fmt);
    int n = vsnprintf(buf, sizeof(buf), fmt, args); va_end(args);
    if (n > 0) { append_log(buf, std::min(static_cast<size_t>(n), sizeof(buf) - 1)); truncated |= n >= static_cast<int>(sizeof(buf)); }
    return n;
}
extern "C" {
uint32_t oc_dvi_abi_version(void) { return OC_DVI_ABI; }
#if OC_DVI_ABI == 3
const char *oc_dvi_engine_version(void) { return "oc-dvi-3/swiftlatex-tectonic-dvipdfmx/xdv-geometry1"; }
#elif OC_DVI_ABI == 2
const char *oc_dvi_engine_version(void) { return "oc-dvi-2/swiftlatex-tectonic-dvipdfmx/geometry1"; }
#else
const char *oc_dvi_engine_version(void) { return "oc-dvi-1/swiftlatex-tectonic-dvipdfmx"; }
#endif
int oc_dvi_add_file(const char *name, const uint8_t *bytes, uint32_t n, uint32_t resource) {
    if (used) {
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
        geo_revoke(); pdf.clear(); status=3;
#endif
        return 3;
    }
    if (resource > 1 || !path_ok(name, resource == 1) || (!bytes && n)) return 1;
    size_t &total = resource ? resource_bytes : project_bytes;
    const size_t limit = resource ? resource_cap : cap;
    if (n > file_cap || n > limit - total || project.size() + resources.size() >= 4096) return 2;
    auto &files = resource ? resources : project;
    if (files.count(name)) return 1;
    File f;
    if (n) f.bytes.assign(bytes, bytes + n);
    files.emplace(name, std::move(f)); total += n;
    return 0;
}
int oc_dvi_run(const char *entry) {
    if (used) {
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
        geo_revoke();
#endif
        pdf.clear(); return status = 3;
    }
    used = true; pdf.clear();
    if (!path_ok(entry) || !project.count(entry) || project.at(entry).bytes.empty()) return status = 1;
    // A single fixed output name cannot select or reuse caller-provided output.
    tt_bridge_api_t api{};
    api.issue_warning = message; api.issue_error = message;
    api.get_file_md5 = file_md5; api.get_data_md5 = data_md5;
    api.output_open = output_open; api.output_open_stdout = output_stdout;
    api.output_putc = output_putc; api.output_write = output_write;
    api.output_flush = output_flush; api.output_close = output_close;
    api.input_open = input_open; api.input_get_size = input_size;
    api.input_seek = input_seek; api.input_read = input_read;
    api.input_getc = input_getc; api.input_ungetc = input_ungetc; api.input_close = input_close;
    char output[] = "result.pdf";
    native_return = dvipdfmx_simple_main(&api, const_cast<char *>(entry), output, true, true, 0);
    status = limited ? 2 : native_return == 99 ? 5 : native_return ? 4 :
        pdf.size() < 16 || memcmp(pdf.data(), "%PDF-", 5) ? 6 : 0;
    if (status) pdf.clear();
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
    if (status || !pdf_closed) geo_revoke();
#endif
    return status;
}
int oc_dvi_status(void) { return status; }
int oc_dvi_native_return(void) { return native_return; }
const char *oc_dvi_error(void) {
    static const char *messages[] = {"", "Invalid converter input.", "Converter resource limit exceeded.", "Converter instance already used.", "Converter returned failure.", "Converter aborted.", "Converter did not produce a valid PDF header."};
    return messages[status >= 0 && status <= 6 ? status : 4];
}
const uint8_t *oc_dvi_pdf_data(void) { return pdf.empty() ? nullptr : pdf.data(); }
uint32_t oc_dvi_pdf_size(void) { return static_cast<uint32_t>(pdf.size()); }
const uint8_t *oc_dvi_log_data(void) { return log_bytes.empty() ? nullptr : log_bytes.data(); }
uint32_t oc_dvi_log_size(void) { return static_cast<uint32_t>(log_bytes.size()); }
uint32_t oc_dvi_log_truncated(void) { return truncated; }
const char *oc_dvi_missing_file(void) { return missing.c_str(); }
#if OC_DVI_ABI == 2 || OC_DVI_ABI == 3
int oc_dvi_geometry_configure(uint32_t mode,uint32_t token) {
    if (used || geo_configured || mode>1 || !token) {
        geo_invalid=true;geo_revoke();
        if (used) { pdf.clear();status=3; }
        return 0;
    }
    geo_configured=true;geo_enabled=mode==1;geo_tag=token;return 1;
}
const uint8_t *oc_dvi_geometry_data(void) { return geo_serialize()?reinterpret_cast<const uint8_t *>(geo_bytes):nullptr; }
uint32_t oc_dvi_geometry_size(void) { return geo_serialize(); }
uint32_t oc_dvi_geometry_length(uint32_t token) { return token && token==geo_tag?geo_serialize():0; }
uint32_t oc_dvi_geometry_copy(uint32_t token,uint8_t *dst,uint32_t capacity) {
    uint32_t n=oc_dvi_geometry_length(token);
    if (!n || !dst || capacity<n || capacity>geo_cap) return 0;
    std::memcpy(dst,geo_bytes,n);return n;
}
void oc_dvi_geo_units(double unit,double header_mag,int kind) { geo_unit=unit;geo_header_mag=header_mag;geo_kind=kind; }
void oc_dvi_geo_begin(uint32_t page,double a,double b,double c,double d,double e,double f,
                      double llx,double lly,double urx,double ury) {
    if (geo_active || geo_count>=geo_pages || page!=geo_count+1) { geo_sequence=false;return; }
    geo_active=true;GeoPage &p=measured[geo_count];p.available=false;
    // Both coordinate relations come from actual pinned DVI/SyncTeX state:
    // Pinned query parser multiplies coordinates, NOT preamble offsets, by mag:
    // visible=raw_sp*bp_per_sp*header_mag + 4736287*bp_per_sp;
    // PDF=M*(dvi_sp*unit_bp,-dvi_sp*unit_bp). Never infer M from paper boxes.
    constexpr double bp_per_sp=72.0/72.27/65536.0;
#if OC_DVI_ABI == 3
    constexpr int required_kind=7; // Explicit new closed XDV7 geometry domain.
#else
    constexpr int required_kind=2; // Accepted ABI2 remains DVI-only.
#endif
    if (geo_kind!=required_kind || !std::isfinite(geo_unit) || geo_unit<=0 ||
        !std::isfinite(geo_header_mag) || geo_header_mag<=0) return;
    double r=geo_unit/(bp_per_sp*geo_header_mag),o=4736287.0*bp_per_sp;
    p.m[0]=a*r;p.m[1]=b*r;p.m[2]=-c*r;p.m[3]=-d*r;
    p.m[4]=e-a*r*o+c*r*o;p.m[5]=f-b*r*o+d*r*o;
    for (double v:p.m) if (!coord(v)) return;
    double det=p.m[0]*p.m[3]-p.m[1]*p.m[2];
    if (!std::isfinite(det) || det==0 || !coord(llx) || !coord(lly) || !coord(urx) || !coord(ury) || llx>=urx || lly>=ury) return;
    p.box[0]=p.box[1]=1000000;p.box[2]=p.box[3]=-1000000;
    for (double x:{llx,urx}) for (double y:{lly,ury}) {
        double h=(p.m[3]*(x-p.m[4])-p.m[2]*(y-p.m[5]))/det;
        double v=(-p.m[1]*(x-p.m[4])+p.m[0]*(y-p.m[5]))/det;
        if (!coord(h) || !coord(v)) return;
        p.box[0]=std::min(p.box[0],h);p.box[1]=std::min(p.box[1],v);
        p.box[2]=std::max(p.box[2],h);p.box[3]=std::max(p.box[3],v);
    }
    p.available=p.box[0]<p.box[2] && p.box[1]<p.box[3];
}
void oc_dvi_geo_end(uint32_t page) {
    if (!geo_active || page!=geo_count+1) { geo_sequence=false;return; }
    geo_active=false;++geo_count;
}
void oc_dvi_geo_special(const char *data,int32_t length) {
    geo_pending_put=false;geo_put_checked=false;
    if (!data || length<0 || length>4096) { geo_unsafe=true;return; }
    size_t n=static_cast<size_t>(length);
    auto white=[](char c){return c==' ' || c=='\r' || c=='\n' || c=='\t' || c=='\f';};
    while (n && white(*data)) {++data;--n;}
    auto prefix=[&](const char *s){size_t k=std::strlen(s);return n>=k && !std::memcmp(data,s,k) && (n==k || s[k-1]=='=' || white(data[k]));};
    if (prefix("papersize=") || prefix("landscape") || prefix("pdf:pagesize")) return;
    if (prefix("pdf:put")) {geo_pending_put=true;return;}
    // Arbitrary specials may install persistent BOP/EOP programs or mutate
    // future page objects: deliberately conservative run-wide fallback.
    geo_unsafe=true;
}
void oc_dvi_geo_put(int safe) { geo_put_checked=true;if (!safe) geo_unsafe=true; }
void oc_dvi_geo_special_end(int actual_result) {
    if (actual_result || (geo_pending_put && !geo_put_checked)) geo_unsafe=true;
    geo_pending_put=false;
}
#endif
}
