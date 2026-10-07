#pragma once

// Counts words separated by ASCII whitespace. UTF-8 non-ASCII bytes are
// preserved as word content; this is not a linguistic tokenizer.
int count_document_words(const char* text);
