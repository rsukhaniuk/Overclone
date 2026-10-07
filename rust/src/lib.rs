use wasm_bindgen::prelude::*;

/// Counts words separated by ASCII whitespace, matching the C++ example.
/// Non-ASCII whitespace remains word content; this is not a tokenizer.
#[wasm_bindgen]
pub fn count_words(text: &str) -> u32 {
    text.split(|c: char| c == ' ' || ('\t'..='\r').contains(&c))
        .filter(|word| !word.is_empty())
        .count() as u32
}

#[cfg(test)]
mod tests {
    use super::count_words;

    #[test]
    fn counts_empty_ascii_and_utf8_input() {
        for (text, expected) in [
            ("", 0),
            (" \t\r\n", 0),
            ("one", 1),
            (" one  two\nthree\t", 3),
            ("one\u{000b}two\u{000c}three", 3),
            ("Привіт світе", 2),
            ("one\u{00a0}two", 1),
        ] {
            assert_eq!(count_words(text), expected, "input: {text:?}");
        }
    }
}
