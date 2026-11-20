use crate::rendered_review::evaluate;
use serde_json::{json, Value};

const FACTS: &str = include_str!("../fixtures/heading_page_end/p17-native-v2.json");
const DOCUMENT: &str = "bef3ed1d25b203683a171572ba2190a23863d229927477767a7153c49faedeeb";
const FACTS_SHA: &str = "dec8602ef7584e73392440dd6f8fb20a169407935668ca2b381ba4cfd30b3b93";

fn request(start: usize, end: usize) -> Value {
    json!({
        "protocolVersion":1,"requestId":"1".repeat(32),"operation":"evaluateRenderedReview",
        "payload":{
            "pdfSha256":DOCUMENT,"compileRequestId":"2".repeat(32),"compileEpoch":5,"inputDigest":"3".repeat(64),
            "captureId":"df3c8bb6-11c5-4a4e-aea9-8b79e9fa2575","profile":{"id":"knu-csc-2025-rendered-review","version":"1.5.0"},
            "workType":"bachelorQualification","annotationVersion":2,"pageCount":1,"requestedPages":[0],
            "annotations":[{"annotationId":1,"role":"headingMinor","pageIndex":0,"characterStart":start,"characterEnd":end,
                "sourceFileId":null,"sourceSpan":null,"provenance":"authorLabel","certainty":"confirmedObservation"}],
            "factsSha256":FACTS_SHA, "authorConfirmedPageTextCoverage":true
        }
    })
}

fn run(request: &Value, facts: &str) -> Value {
    serde_json::from_str(&evaluate(&request.to_string(), facts)).unwrap()
}

fn heading(report: &Value) -> &Value {
    assert_eq!(report["status"], "completed", "{report}");
    assert_eq!(report["payload"]["coverage"]["standardsComplete"], false);
    report["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|row| row["ruleId"] == "KNU-HEADINGS-RENDERED-001")
        .unwrap()
}

fn indeterminate(request: &Value, facts: &str, reason: &str) {
    let report = run(request, facts);
    let row = heading(&report);
    assert_eq!(row["reason"], reason);
    assert_eq!(row["outcome"], "indeterminate");
    assert_eq!(row["evidence"], json!([]));
}

#[test]
fn real_minor_heading_at_page_end_and_earlier_heading_on_the_same_pdf() {
    for (start, end, outcome, reason, remaining, neighbor, side) in [
        (
            89,
            108,
            "fail",
            "confirmed-minor-heading-is-final-page-text-line",
            0,
            (56, 87),
            "preceding",
        ),
        (
            33,
            54,
            "indeterminate",
            "conditional-minor-heading-has-following-page-text",
            2,
            (56, 87),
            "following",
        ),
    ] {
        let request = request(start, end);
        let report = run(&request, FACTS);
        let row = heading(&report);
        assert_eq!(row["ruleVersion"], "1.2.0");
        assert_eq!(row["citation"]["pdfPages"], json!([18, 19]));
        assert_eq!(row["outcome"], outcome);
        assert_eq!(row["reason"], reason);
        let item = &row["evidence"][0];
        let observation = &item["observation"];
        assert_eq!(item["measured"], remaining);
        assert_eq!(observation["textLineCount"], 4);
        assert_eq!(observation["authorConfirmedPageTextCoverage"], true);
        assert_eq!(observation["neighbor"]["side"], side);
        assert_eq!(observation["neighbor"]["characterStart"], neighbor.0);
        assert_eq!(observation["neighbor"]["characterEnd"], neighbor.1);
        assert!(observation["headingInkErrorRaw"].as_f64().unwrap() > 0.0);

        let mut legacy = request.clone();
        legacy["payload"]["profile"]["version"] = json!("1.0.0");
        legacy["payload"]
            .as_object_mut()
            .unwrap()
            .remove("authorConfirmedPageTextCoverage");
        let before = run(&legacy, FACTS);
        assert_eq!(before["status"], "completed");
        for (left, right) in before["payload"]["results"]
            .as_array()
            .unwrap()
            .iter()
            .zip(report["payload"]["results"].as_array().unwrap())
        {
            if left["ruleId"] != "KNU-HEADINGS-RENDERED-001" {
                assert_eq!(left, right);
            }
        }
    }
}

#[test]
fn explicit_coverage_and_version_are_required() {
    for value in [Value::Null, json!(false), json!("true")] {
        let mut unconfirmed = request(89, 108);
        unconfirmed["payload"]["authorConfirmedPageTextCoverage"] = value;
        assert_eq!(run(&unconfirmed, FACTS)["error"]["code"], "InvalidInput");
    }
    let mut missing = request(89, 108);
    missing["payload"]
        .as_object_mut()
        .unwrap()
        .remove("authorConfirmedPageTextCoverage");
    assert_eq!(run(&missing, FACTS)["error"]["code"], "InvalidInput");
    for prior in ["1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0"] {
        let mut legacy = request(89, 108);
        legacy["payload"]["profile"]["version"] = json!(prior);
        assert_eq!(run(&legacy, FACTS)["error"]["code"], "InvalidInput");
    }
}

#[test]
fn partial_tentative_and_ambiguous_labels_never_fail() {
    indeterminate(
        &request(90, 108),
        FACTS,
        "minor-heading-not-complete-single-line",
    );
    let mut tentative = request(89, 108);
    tentative["payload"]["annotations"][0]["certainty"] = json!("tentative");
    indeterminate(&tentative, FACTS, "confirmed-minor-heading-range-required");
    let mut ambiguous = request(89, 108);
    let mut other = request(33, 54)["payload"]["annotations"][0].clone();
    other["annotationId"] = json!(2);
    ambiguous["payload"]["annotations"]
        .as_array_mut()
        .unwrap()
        .push(other);
    indeterminate(&ambiguous, FACTS, "ambiguous-minor-heading-ranges");
}

#[test]
fn native_facts_without_a_complete_upright_single_column_never_fail() {
    let mut rotated: Value = serde_json::from_str(FACTS).unwrap();
    rotated["pages"][0]["facts"]["rotation"] = json!(90);
    indeterminate(
        &request(89, 108),
        &rotated.to_string(),
        "minor-heading-coverage-unestablished",
    );
    let mut broken: Value = serde_json::from_str(FACTS).unwrap();
    broken["pages"][0]["facts"]["characters"][31]["unicode"] = json!(10);
    indeterminate(
        &request(89, 108),
        &broken.to_string(),
        "minor-heading-coverage-unestablished",
    );
    let mut columns: Value = serde_json::from_str(FACTS).unwrap();
    let chars = columns["pages"][0]["facts"]["characters"]
        .as_array_mut()
        .unwrap();
    for ch in &mut chars[56..87] {
        for (key, x_coordinates) in [("origin", &[0][..]), ("inkBox", &[0, 2][..])] {
            if let Some(coordinates) = ch[key].as_array_mut() {
                for &x in x_coordinates {
                    coordinates[x] = json!(coordinates[x].as_f64().unwrap() + 250.0);
                }
            }
        }
    }
    indeterminate(
        &request(89, 108),
        &columns.to_string(),
        "minor-heading-page-order-unestablished",
    );
}
