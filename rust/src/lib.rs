use wasm_bindgen::prelude::*;

pub mod analysis;
pub mod authoring;
pub mod rendered_extent;
pub mod rendered_review;
pub mod thesis_catalog;

/// Counts words separated by ASCII whitespace, matching the C++ example.
/// Non-ASCII whitespace remains word content; this is not a tokenizer.
#[wasm_bindgen]
pub fn count_words(text: &str) -> u32 {
    text.split(|c: char| c == ' ' || ('\t'..='\r').contains(&c))
        .filter(|word| !word.is_empty())
        .count() as u32
}

/// Advertise engine capabilities for the v1 worker envelope. Only the
/// `requestId` of the canonical initialize envelope is echoed; initialize
/// payloads do NOT carry a snapshot/profile.
#[wasm_bindgen]
pub fn initialize(request_json: &str) -> String {
    analysis::initialize(request_json)
}

/// Canonical source analysis entry. `metadata_json` is the v1 request
/// envelope; `packed_bytes` is the single packed binary buffer the file
/// descriptors slice. Returns the canonical response as JSON text. Pure
/// Rust: no clock, no filesystem, no browser APIs. The 5000 ms whole-job
/// wall deadline and worker termination belong to the later JS bridge.
#[wasm_bindgen]
pub fn analyze_source(metadata_json: &str, packed_bytes: &[u8]) -> String {
    analysis::analyze_source(metadata_json, packed_bytes)
}

/// Literal source assistance over captured bytes. Request echoes are correlation
/// only; the host must validate current snapshot, caret and writer authority.
#[wasm_bindgen]
pub fn authoring_assist(metadata_json: &str, packed_bytes: &[u8]) -> String {
    authoring::assist(metadata_json, packed_bytes)
}

/// Pure, bounded usual-extent observation over caller-supplied PDF page count.
/// The caller owns measurement/revision authority; this is not compliance proof.
#[wasm_bindgen]
pub fn evaluate_rendered_extent(request_json: &str) -> String {
    rendered_extent::evaluate(request_json)
}

/// Bounded observations over measured PDF facts. The caller authenticates facts
/// and revision authority; unsupported measurements remain explicit.
#[wasm_bindgen]
pub fn evaluate_rendered_review(request_json: &str, facts_json: &str) -> String {
    rendered_review::evaluate(request_json, facts_json)
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
