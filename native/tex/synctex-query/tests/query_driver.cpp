#include "synctex_query.h"

#include <array>
#include <climits>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>
#include <vector>

namespace {
void require(bool ok) {
    if (!ok) { std::fprintf(stderr, "driver check failed\n"); std::exit(2); }
}
int forward(const std::string &name, int line = 100) {
    return sq_forward(reinterpret_cast<const uint8_t *>(name.data()),
                      static_cast<uint32_t>(name.size()), line, 0, -1);
}
void report(int code) {
    const uint32_t length = sq_result_length();
    std::array<uint8_t, 32769> bytes{};
    if (length) {
        require(length <= 32768);
        require(sq_result_copy(bytes.data(), length) == SQ_OK);
        require(bytes[length] == 0);
    }
    std::printf("SQ_RESULT %d %s\n", code, length ? reinterpret_cast<char *>(bytes.data()) : "null");
}
}

int main(int argc, char **argv) {
    require(argc >= 3);
    std::ifstream file(argv[1], std::ios::binary);
    require(file.good());
    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)), {});
    const std::string operation = argv[2];
    require(sq_version() == 1);
    if (operation == "close-first") {
        require(sq_close() == SQ_OK);
        require(sq_load(bytes.data(), static_cast<uint32_t>(bytes.size())) == SQ_STATE);
        require(sq_result_length() == 0);
        report(sq_close()); return 0;
    }
    const int loaded = sq_load(bytes.data(), static_cast<uint32_t>(bytes.size()));
    if (operation == "load") {
        report(loaded);
        require(sq_load(bytes.data(), static_cast<uint32_t>(bytes.size())) == SQ_STATE);
        require(sq_result_length() == 0);
        require(sq_close() == SQ_OK); return 0;
    }
    require(loaded == SQ_OK);
    if (operation == "forward") {
        require(argc == 5);
        report(forward(argv[3], std::atoi(argv[4])));
    } else if (operation == "inverse") {
        require(argc == 6);
        report(sq_inverse(std::atoi(argv[3]), std::strtod(argv[4], nullptr), std::strtod(argv[5], nullptr)));
    } else if (operation == "controls") {
        auto positive = []() { require(forward("test.tex") == SQ_OK); require(sq_result_length() > 0); };
        positive();
        std::array<uint8_t, 32768> dest{};
        dest.fill(0xa5);
        require(sq_result_copy(dest.data(), sq_result_length() - 1) == SQ_COPY);
        require(sq_result_length() == 0 && dest[0] == 0xa5 && dest.back() == 0xa5);
        positive();
        require(sq_result_copy(nullptr, 32768) == SQ_COPY && sq_result_length() == 0);
        const std::vector<std::string> bad = {
            "", "/test.tex", "../test.tex", "a/../test.tex", "a/./b.tex", "a//b.tex",
            "a/", "C:test.tex", "a\\b.tex", std::string("a\0b", 3),
            "a\n.tex", "a\x7f.tex", "a\xc0\xaf.tex", "a\xed\xa0\x80.tex",
            "a\xf4\x90\x80\x80.tex", "a\xe2\x80\xa8.tex", "a\xc2\x85.tex",
            "a\xe2\x82", std::string(1025, 'x')
        };
        for (const auto &name : bad) {
            positive(); require(forward(name) == SQ_INVALID && sq_result_length() == 0);
        }
        positive();
        require(sq_forward(nullptr, 5, 1, 0, -1) == SQ_INVALID && sq_result_length() == 0);
        const auto *name = reinterpret_cast<const uint8_t *>("test.tex");
        for (const auto &args : std::vector<std::array<int32_t, 3>>{{0,0,-1},{-1,0,-1},
                {1,-1,-1},{1,0,0},{1,0,513},{1,0,-2}}) {
            positive();
            require(sq_forward(name, 8, args[0], args[1], args[2]) == SQ_INVALID && sq_result_length() == 0);
        }
        for (double value : {std::numeric_limits<double>::quiet_NaN(),
                std::numeric_limits<double>::infinity(), -std::numeric_limits<double>::infinity(),
                1000001.0, -1000001.0}) {
            positive(); require(sq_inverse(1, value, 0) == SQ_INVALID && sq_result_length() == 0);
            positive(); require(sq_inverse(1, 0, value) == SQ_INVALID && sq_result_length() == 0);
        }
        for (int page : {0, -1, 513, INT_MAX}) {
            positive(); require(sq_inverse(page, 0, 0) == SQ_INVALID && sq_result_length() == 0);
        }
        positive(); require(forward("no-such-source.tex") == SQ_EMPTY && sq_result_length() == 0);
        positive(); require(sq_inverse(512, 0, 0) == SQ_EMPTY && sq_result_length() == 0);
        positive();
        require(sq_load(bytes.data(), static_cast<uint32_t>(bytes.size())) == SQ_STATE && sq_result_length() == 0);
        positive(); // A refused second load did not replace the original scanner.
        require(sq_close() == SQ_OK && sq_result_length() == 0);
        require(forward("test.tex") == SQ_STATE && sq_result_length() == 0);
        require(sq_inverse(1, 0, 0) == SQ_STATE && sq_result_length() == 0);
        require(sq_load(bytes.data(), static_cast<uint32_t>(bytes.size())) == SQ_STATE);
        require(std::strcmp(sq_error_message(), "module state refuses operation") == 0);
        report(sq_close());
    } else { require(false); }
    require(sq_close() == SQ_OK && sq_result_length() == 0);
    return 0;
}
