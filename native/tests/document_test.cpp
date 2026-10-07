#include "document.hpp"
#include <iostream>

int main()
{
    struct Case { const char* text; int expected; };
    const Case cases[] = {
        {nullptr, 0}, {"", 0}, {" \t\r\n", 0},
        {"one", 1}, {" one  two\nthree\t", 3}, {"Привіт світе", 2}
    };
    for (const auto& item : cases) {
        if (count_document_words(item.text) != item.expected) {
            std::cerr << "Word count mismatch\n";
            return 1;
        }
    }
}
