#include "document.hpp"

int count_document_words(const char* text)
{
    if (!text) return 0;
    int words = 0;
    bool inside_word = false;
    for (const unsigned char* cursor = reinterpret_cast<const unsigned char*>(text);
         *cursor; ++cursor) {
        const bool whitespace = *cursor == ' ' || (*cursor >= '\t' && *cursor <= '\r');
        if (!whitespace && !inside_word) ++words;
        inside_word = !whitespace;
    }
    return words;
}
