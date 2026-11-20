use super::*;
use crate::rendered_review::evaluate as rendered;
use serde_json::json;

const FACTS: &str = include_str!("../fixtures/paragraph/p17-paragraph-native-v2.json");
const LINEAGE: &str = include_str!("../fixtures/paragraph/lineage.json");

fn setup(start: usize, end: usize) -> (Value, Value) {
    let facts: Value = serde_json::from_str(FACTS).unwrap();
    let lineage: Value = serde_json::from_str(LINEAGE).unwrap();
    let request = json!({"protocolVersion":1,"requestId":"3".repeat(32),"operation":"evaluateRenderedReview","payload":{
        "pdfSha256":lineage["pdf"]["sha256"],"compileRequestId":"4".repeat(32),"compileEpoch":5,
        "inputDigest":"5".repeat(64),"captureId":"df3c8bb6-11c5-4a4e-aea9-8b79e9fa2575",
        "profile":{"id":"knu-csc-2025-rendered-review","version":PROFILE_VERSION},"workType":"bachelorQualification",
        "annotationVersion":1,"pageCount":1,"requestedPages":[0],"factsSha256":lineage["batch"]["sha256"],
        "annotations":[{"annotationId":1,"role":"paragraph","pageIndex":0,"characterStart":start,
            "characterEnd":end,"sourceFileId":null,"sourceSpan":null,"provenance":"authorLabel","certainty":"confirmedObservation"}]
    }});
    (request, facts)
}

fn result(request: &Value, facts: &Value) -> Value {
    let response: Value =
        serde_json::from_str(&rendered(&request.to_string(), &facts.to_string())).unwrap();
    response["payload"]["results"]
        .as_array()
        .unwrap_or_else(|| panic!("{response}"))
        .iter()
        .find(|r| r["ruleId"] == RULE_ID)
        .unwrap()
        .clone()
}

#[test]
fn actual_p17_paragraphs_measure_target_and_counterexample() {
    for (start, end, expected, relation, reason, outcome) in [
        (
            0,
            102,
            12.7,
            "targetWithinUncertainty",
            "conditional-paragraph-indent-target-in-interval",
            "indeterminate",
        ),
        (
            104,
            203,
            6.0,
            "outsideTarget",
            "confirmed-paragraph-indent-differs",
            "fail",
        ),
    ] {
        let (request, facts) = setup(start, end);
        let row = result(&request, &facts);
        assert_eq!(row["reason"], reason, "{row}");
        assert_eq!(row["outcome"], outcome);
        let e = &row["evidence"][0];
        assert_eq!(e["observation"]["comparison"], relation);
        assert_eq!(e["expected"], 12.7);
        let measured = e["measured"].as_f64().unwrap();
        let error = e["uncertainty"]["absoluteError"].as_f64().unwrap();
        assert!((measured - expected).abs() < 0.01, "{row}");
        assert!((0.0..0.1).contains(&error));
        assert_eq!(e["observation"]["starts"][0]["index"], start);
    }
}

#[test]
fn partial_tentative_and_missing_ranges_remain_indeterminate() {
    for (start, end) in [(1, 102), (0, 101), (0, 65), (33, 102), (0, 203)] {
        let (request, facts) = setup(start, end);
        let row = result(&request, &facts);
        assert_eq!(row["reason"], EXACT_RANGE, "{row}");
        assert_eq!(row["evidence"], json!([]));
    }
    let (mut request, facts) = setup(0, 102);
    request["payload"]["annotations"][0]["certainty"] = json!("tentative");
    assert_eq!(result(&request, &facts)["reason"], EXACT_RANGE);
    request["payload"]["annotations"] = json!([]);
    assert_eq!(result(&request, &facts)["reason"], EXACT_RANGE);
}

#[test]
fn out_of_scope_text_in_line_band_refuses_isolation() {
    let (request, mut facts) = setup(0, 102);
    let ch = &mut facts["pages"][0]["facts"]["characters"][104];
    ch["origin"][1] = json!(700.0);
    let row = result(&request, &facts);
    assert_eq!(row["reason"], ORDER);
    assert_eq!(row["evidence"], json!([]));
}

#[test]
fn unaligned_start_clipping_and_rotation_remain_unknown() {
    let (request, mut facts) = setup(104, 203);
    facts["pages"][0]["facts"]["characters"][138]["matrix"][4] = json!(100.0);
    assert_eq!(result(&request, &facts)["reason"], ALIGNMENT);

    let (_, mut facts) = setup(104, 203);
    facts["pages"][0]["facts"]["rotation"] = json!(90);
    assert_eq!(result(&request, &facts)["reason"], INK);

    let (_, mut facts) = setup(104, 203);
    let f = &mut facts["pages"][0]["facts"];
    let crop = json!([136.0, f["media"][1], f["media"][2], f["media"][3]]);
    f["crop"] = crop.clone();
    f["effective"] = crop;
    assert_eq!(result(&request, &facts)["reason"], INK);
}
