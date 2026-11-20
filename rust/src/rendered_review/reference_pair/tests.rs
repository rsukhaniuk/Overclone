use super::*;
use crate::rendered_review::evaluate;

const NATIVE: &str = include_str!("../fixtures/reference_pair/p16-reference-pair-native-v2.json");
const LINEAGE: &str = include_str!("../fixtures/reference_pair/lineage.json");

fn input() -> (Value, Value) {
    let facts: Value = serde_json::from_str(NATIVE).unwrap();
    let lineage: Value = serde_json::from_str(LINEAGE).unwrap();
    let selection = |page: u16, start: usize, literal: &str| {
        json!({"selectionRequestId": "2".repeat(32), "selectionEpoch": 3 + page,
            "pageIndex": page, "characterStart": start, "characterEnd": start + literal.chars().count(), "literal": literal})
    };
    let request = json!({"protocolVersion":1,"requestId":"3".repeat(32),"operation":"evaluateRenderedReview","payload":{
        "pdfSha256":lineage["pdf"]["sha256"],"compileRequestId":"4".repeat(32),"compileEpoch":5,
        "inputDigest":"5".repeat(64),"captureId":"df3c8bb6-11c5-4a4e-aea9-8b79e9fa2575",
        "profile":{"id":PROFILE_ID,"version":PROFILE_VERSION},"workType":"unspecified","annotationVersion":0,
        "pageCount":2,"requestedPages":[0,1],"annotations":[],"factsSha256":lineage["batch"]["sha256"],
        "printedReferencePair":{"scope":"mainWork","rendererLoaderSha256":lineage["runtime"]["loader"]["sha256"],
            "rendererWasmSha256":lineage["runtime"]["wasm"]["sha256"],"citation":selection(0,17,"[1]"),"entry":selection(1,0,"[1]")}
    }});
    (request, facts)
}

fn review(request: &Value, facts: &Value) -> Value {
    serde_json::from_str(&evaluate(&request.to_string(), &facts.to_string())).unwrap()
}

fn indeterminate(request: &Value, facts: &Value, expected: &str) {
    let result = review(request, facts);
    let p = &result["payload"];
    assert_eq!(p["comparison"], "indeterminate", "{result}");
    assert_eq!(p["reason"], expected);
    assert!(p["numbers"].is_null());
}

#[test]
fn genuine_cross_page_pair_and_one_changed_entry() {
    let (mut request, facts) = input();
    let result = review(&request, &facts);
    let p = &result["payload"];
    assert_eq!(p["comparison"], "numbersMatch", "{result}");
    assert_eq!(p["numbers"], json!({"citation":1,"entry":1}));
    assert_eq!(p["printedReferencePair"], request["payload"]["printedReferencePair"]);
    assert!(p["sourceLocation"].is_null());
    let entry = &mut request["payload"]["printedReferencePair"]["entry"];
    entry["characterStart"] = json!(18);
    entry["characterEnd"] = json!(21);
    entry["literal"] = json!("[2]");
    let result = review(&request, &facts);
    assert_eq!(result["payload"]["comparison"], "numbersDiffer", "{result}");
    assert_eq!(result["payload"]["numbers"], json!({"citation":1,"entry":2}));
}

#[test]
fn same_page_roles_must_name_distinct_ranges() {
    let (mut request, mut facts) = input();
    let p = &mut request["payload"];
    p["requestedPages"] = json!([1]);
    facts["pages"].as_array_mut().unwrap().remove(0);
    let pair = &mut p["printedReferencePair"];
    pair["citation"] = pair["entry"].clone();
    indeterminate(&request, &facts, "overlapping-selection-ranges");

    let citation = &mut request["payload"]["printedReferencePair"]["citation"];
    citation["characterStart"] = json!(18);
    citation["characterEnd"] = json!(21);
    citation["literal"] = json!("[2]");
    assert_eq!(review(&request, &facts)["payload"]["comparison"], "numbersDiffer");
}

#[test]
fn exact_scalars_without_normalization_are_required() {
    let (mut request, mut facts) = input();
    let citation = &mut request["payload"]["printedReferencePair"]["citation"];
    citation["literal"] = json!("[１]");
    indeterminate(&request, &facts, "selection-glyph-mismatch");
    facts["pages"][0]["facts"]["characters"][18]["unicode"] = json!(u32::from('１'));
    indeterminate(&request, &facts, "unsupported-printed-number");
    request["payload"]["printedReferencePair"]["citation"]["literal"] = json!("[🐈]");
    facts["pages"][0]["facts"]["characters"][18]["unicode"] = json!(u32::from('🐈'));
    indeterminate(&request, &facts, "unsupported-printed-number");
}

#[test]
fn incomplete_ink_and_page_clipping_are_indeterminate() {
    let (request, mut facts) = input();
    let selected = &mut facts["pages"][1]["facts"]["characters"][0];
    selected["inkStatus"] = json!("indeterminate");
    selected["inkBox"] = Value::Null;
    selected["inkUncertainty"] = Value::Null;
    indeterminate(&request, &facts, "glyph-ink-unavailable");

    let (_, mut facts) = input();
    let selected_page = &mut facts["pages"][1]["facts"];
    let media = selected_page["media"].as_array().unwrap();
    let crop = json!([136.0, media[1], media[2], media[3]]);
    selected_page["crop"] = crop.clone();
    selected_page["effective"] = crop;
    indeterminate(&request, &facts, "glyph-ink-unavailable");
}

#[test]
fn compatibility_and_capture_identity_are_strict() {
    let (request, facts) = input();
    let mut legacy = request.clone();
    legacy["payload"]["profile"]["id"] = json!("knu-csc-2025-rendered-review");
    for version in ["1.0.0", "1.1.0", "1.2.0", "1.3.0"] {
        legacy["payload"]["profile"]["version"] = json!(version);
        assert_eq!(review(&legacy, &facts)["error"]["code"], "InvalidInput");
    }
    let mut different_document = facts.clone();
    different_document["documentSha256"] = json!("0".repeat(64));
    assert_eq!(review(&request, &different_document)["error"]["code"], "InvalidFacts");
    let mut bad_shape = request.clone();
    bad_shape["payload"]["printedReferencePair"]["entry"] = json!([]);
    assert_eq!(review(&bad_shape, &facts)["error"]["code"], "InvalidInput");
    let mut wrong_pages = request;
    wrong_pages["payload"]["requestedPages"] = json!([0]);
    assert_eq!(review(&wrong_pages, &facts)["error"]["code"], "InvalidFacts");
}
