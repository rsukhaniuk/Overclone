#include "document.hpp"

extern "C" int count_words(const char* text)
{
    return count_document_words(text);
}
