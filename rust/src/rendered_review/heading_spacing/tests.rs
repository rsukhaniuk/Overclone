use crate::rendered_review::evaluate;
use serde_json::{json, Value};

const FACTS: &str = include_str!("../fixtures/heading_spacing/p17-native-v2.json");
const LINEAGE: &str = include_str!("../fixtures/heading_spacing/lineage.json");

fn request() -> Value {
    let lineage: Value = serde_json::from_str(LINEAGE).unwrap();
    let label = |id, role, start, end| {
        json!({
            "annotationId":id,"role":role,"pageIndex":0,"characterStart":start,"characterEnd":end,
            "provenance":"authorLabel","certainty":"confirmedObservation","sourceFileId":null,"sourceSpan":null
        })
    };
    json!({
        "protocolVersion":1,"requestId":"1".repeat(32),"operation":"evaluateRenderedReview",
        "payload":{
            "pdfSha256":lineage["pdf"]["sha256"],"compileRequestId":"2".repeat(32),"compileEpoch":5,"inputDigest":"3".repeat(64),
            "captureId":"df3c8bb6-11c5-4a4e-aea9-8b79e9fa2575","profile":{"id":"knu-csc-2025-rendered-review","version":"1.6.0"},
            "workType":"bachelorQualification","annotationVersion":2,"pageCount":3,"requestedPages":[0],
            "annotations":[label(1,"headingMinor",0,53),label(2,"paragraph",55,156)],
            "factsSha256":lineage["retainedFactsSha256"]
        }
    })
}
fn run(request: &Value, facts: &str) -> Value {
    serde_json::from_str(&evaluate(&request.to_string(), facts)).unwrap()
}

fn spacing(report: &Value) -> &Value {
    assert_eq!(report["status"], "completed", "{report}");
    report["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|row| row["ruleId"] == "KNU-HEADINGS-RENDERED-001")
        .unwrap()
}

#[test]
fn unequal_confirmed_heading_and_local_paragraph_intervals_are_measured_on_real_e1_facts() {
    let request = request();
    let report = run(&request, FACTS);
    let row = spacing(&report);
    assert_eq!(row["ruleVersion"], "1.3.0");
    assert_eq!(row["citation"]["pdfPages"], json!([19]));
    assert_eq!(row["outcome"], "indeterminate");
    assert_eq!(row["reason"], super::DIFFERENT);
    let evidence = &row["evidence"][0];
    assert!((evidence["measured"].as_f64().unwrap() - 8.435).abs() < 0.01);
    assert_eq!(evidence["roleProvenance"]["annotationId"], 1);
    assert_eq!(evidence["observation"]["bodyAnnotationId"], 2);
    assert_eq!(
        evidence["observation"]["comparison"],
        "outsideBothBodyIntervals"
    );
    for interval in evidence["observation"]["bodyIntervals"].as_array().unwrap() {
        assert!((interval["measuredMm"].as_f64().unwrap() - 4.921).abs() < 0.01);
        assert!(interval["absoluteErrorMm"].as_f64().unwrap() > 0.0);
    }
    let mut legacy = request;
    legacy["payload"]["profile"]["version"] = json!("1.0.0");
    let before = run(&legacy, FACTS);
    for (earlier, later) in before["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .zip(report["payload"]["results"].as_array().unwrap())
    {
        if earlier["ruleId"] != "KNU-HEADINGS-RENDERED-001" {
            assert_eq!(earlier, later);
        }
    }
}

#[test]
fn incomplete_or_unconfirmed_ranges_and_ambiguous_frame_never_yield_evidence() {
    let mut partial = request();
    partial["payload"]["annotations"][0]["characterStart"] = json!(1);
    assert_eq!(spacing(&run(&partial, FACTS))["reason"], super::ADJACENT);
    let mut tentative = request();
    tentative["payload"]["annotations"][1]["certainty"] = json!("tentative");
    assert_eq!(spacing(&run(&tentative, FACTS))["reason"], super::REQUIRED);
    let mut rotated: Value = serde_json::from_str(FACTS).unwrap();
    rotated["pages"][0]["facts"]["rotation"] = json!(90);
    let report = run(&request(), &rotated.to_string());
    let row = spacing(&report);
    assert_eq!(row["reason"], super::GEOMETRY);
    assert_eq!(row["evidence"], json!([]));
    assert_eq!(row["outcome"], "indeterminate");
}

#[test]
fn new_version_requires_one_page_and_disallows_other_profile_confirmation_flag() {
    let mut confirmed_other_check = request();
    confirmed_other_check["payload"]["authorConfirmedPageTextCoverage"] = json!(true);
    assert_eq!(
        run(&confirmed_other_check, FACTS)["error"]["code"],
        "InvalidInput"
    );
    let mut multi_page = request();
    multi_page["payload"]["requestedPages"] = json!([0, 1]);
    let mut facts: Value = serde_json::from_str(FACTS).unwrap();
    let mut another = facts["pages"][0].clone();
    another["facts"]["pageIndex"] = json!(1);
    facts["pages"].as_array_mut().unwrap().push(another);
    assert_eq!(
        run(&multi_page, &facts.to_string())["error"]["code"],
        "InvalidInput"
    );
}
