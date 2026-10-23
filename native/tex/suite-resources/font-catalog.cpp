// Offline build tool: read only explicitly supplied fonts with retained FreeType.
// Emits the existing accepted XeTeX WASM xetexfontlist.txt ABI; no font discovery.
#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_SFNT_NAMES_H
#include FT_TRUETYPE_TABLES_H
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

static std::string utf16be(const FT_Byte* p, unsigned n) {
    std::string result;
    if (n % 2) throw std::runtime_error("odd UTF-16 name length");
    for (unsigned i = 0; i < n; i += 2) {
        unsigned c = (p[i] << 8) | p[i + 1];
        if (c >= 0xd800 && c <= 0xdbff) {
            if (i + 3 >= n) throw std::runtime_error("truncated surrogate");
            unsigned low = (p[i + 2] << 8) | p[i + 3];
            if (low < 0xdc00 || low > 0xdfff) throw std::runtime_error("invalid surrogate");
            c = 0x10000 + ((c - 0xd800) << 10) + low - 0xdc00;
            i += 2;
        } else if (c >= 0xdc00 && c <= 0xdfff) throw std::runtime_error("unpaired surrogate");
        if (c < 32 || c == 127) throw std::runtime_error("control in font name");
        if (c < 0x80) result += char(c);
        else if (c < 0x800) { result += char(0xc0 | (c >> 6)); result += char(0x80 | (c & 63)); }
        else if (c < 0x10000) { result += char(0xe0 | (c >> 12)); result += char(0x80 | ((c >> 6) & 63)); result += char(0x80 | (c & 63)); }
        else { result += char(0xf0 | (c >> 18)); result += char(0x80 | ((c >> 12) & 63)); result += char(0x80 | ((c >> 6) & 63)); result += char(0x80 | (c & 63)); }
    }
    return result;
}

static std::string name(FT_Face face, unsigned id) {
    // Explicit English Windows Unicode names. No invented aliases or filename inference.
    for (unsigned i = 0; i < FT_Get_Sfnt_Name_Count(face); ++i) {
        FT_SfntName rec;
        if (FT_Get_Sfnt_Name(face, i, &rec)) throw std::runtime_error("SFNT read failed");
        if (rec.name_id == id && rec.platform_id == 3 && rec.language_id == 0x409
            && (rec.encoding_id == 1 || rec.encoding_id == 10)) return utf16be(rec.string, rec.string_len);
    }
    return "";
}

static unsigned u16(const std::vector<FT_Byte>& b, size_t offset) {
    if (offset + 2 > b.size()) throw std::runtime_error("GPOS size field out of range");
    return (b[offset] << 8) | b[offset + 1];
}

struct Optical { unsigned design = 0, family = 0, name = 0, min = 0, max = 0; };
static Optical optical(FT_Face face) {
    FT_ULong length = 0;
    const FT_ULong gpos = FT_MAKE_TAG('G', 'P', 'O', 'S');
    if (FT_Load_Sfnt_Table(face, gpos, 0, nullptr, &length)) return {};
    std::vector<FT_Byte> data(length);
    if (FT_Load_Sfnt_Table(face, gpos, 0, data.data(), &length)) throw std::runtime_error("GPOS read failed");
    size_t list = u16(data, 6);
    if (!list) return {};
    unsigned count = u16(data, list);
    for (unsigned i = 0; i < count; ++i) {
        size_t record = list + 2 + i * 6;
        if (record + 6 > data.size()) throw std::runtime_error("GPOS feature out of range");
        if (data[record] != 's' || data[record+1] != 'i' || data[record+2] != 'z' || data[record+3] != 'e') continue;
        size_t feature = list + u16(data, record + 4);
        unsigned params = u16(data, feature);
        if (!params) return {};
        size_t p = feature + params;
        return {u16(data,p),u16(data,p+2),u16(data,p+4),u16(data,p+6),u16(data,p+8)};
    }
    return {};
}

int main(int argc, char** argv) {
    try {
        FT_Library library;
        if (FT_Init_FreeType(&library)) throw std::runtime_error("FreeType init failed");
        for (int i = 1; i < argc; ++i) {
            std::string filename = argv[i];
            if (filename.find('/') != std::string::npos || filename.find('\\') != std::string::npos)
                throw std::runtime_error("expected a single explicit font basename");
            FT_Face face;
            if (FT_New_Face(library, ("/fonts/" + filename).c_str(), 0, &face)) throw std::runtime_error("font open failed");
            if (face->num_faces != 1 || !FT_IS_SFNT(face)) throw std::runtime_error("only supplied single-face SFNT fonts supported");
            auto family = name(face, 16); if (family.empty()) family = name(face, 1);
            auto style = name(face, 17); if (style.empty()) style = name(face, 2);
            auto full = name(face, 4);
            const char* ps = FT_Get_Postscript_Name(face);
            auto os2 = static_cast<TT_OS2*>(FT_Get_Sfnt_Table(face, ft_sfnt_os2));
            auto head = static_cast<TT_Header*>(FT_Get_Sfnt_Table(face, ft_sfnt_head));
            auto post = static_cast<TT_Postscript*>(FT_Get_Sfnt_Table(face, ft_sfnt_post));
            if (family.empty() || style.empty() || full.empty() || !ps || !os2 || !head || !post)
                throw std::runtime_error("required metadata missing");
            bool regular = os2->fsSelection & (1 << 6);
            bool bold = (os2->fsSelection & (1 << 5)) || (head->Mac_Style & 1);
            bool italic = (os2->fsSelection & 1) || (head->Mac_Style & 2);
            int slant = int(1000 * std::tan(-double(post->italicAngle) / 65536 * std::acos(-1) / 180));
            Optical size = optical(face);
            auto pt = [](unsigned n) { return n * 72.27 / 72 / 10; };
            std::cout << std::setprecision(12) << i << '\n' << "/tex/" << filename << "\n0\n1\n" << family
                << "\n1\n" << style << "\n1\n" << full << '\n' << ps << '\n' << style << '\n'
                << os2->usWeightClass << '\n' << os2->usWidthClass << '\n' << slant << '\n'
                << regular << '\n' << bold << '\n' << italic << '\n'
                << (size.design ? pt(size.design) : 10.0) << '\n' << pt(size.min) << '\n' << pt(size.max) << '\n'
                << size.family << '\n' << size.family << '\n';
            // The retained reader consumes subFamilyID twice (not nameCode). Keep both
            // ABI fields truthful; preserve actual nameCode in the metadata receipt.
            std::cerr << filename << '\t' << ps << '\t' << family << '\t' << style << '\t'
                << os2->usWeightClass << '\t' << os2->usWidthClass << '\t' << slant << '\t'
                << size.design << '\t' << size.min << '\t' << size.max << '\t' << size.family << '\t' << size.name << '\n';
            FT_Done_Face(face);
        }
        FT_Done_FreeType(library);
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
