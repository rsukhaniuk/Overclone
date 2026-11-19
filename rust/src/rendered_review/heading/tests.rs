use crate::rendered_review::evaluate;
use serde_json::{json, Value};

const FACTS: &str = include_str!("../fixtures/heading/p16-heading-native-v2.json");
const PDF: &str = "42433c0d90c94d675b4f339f2a4ae4fbe98a2d460ea57ca9f1adc5a752050beb";
const SHA: &str = "76b8b65bf8c431e86e4b6f5c69cb5bbd9ddd45e36484aca559c574e22888884f";

fn annotation(id: usize, role: &str, range: Option<(usize, usize)>) -> Value {
    json!({"annotationId":id,"role":role,"pageIndex":0,"characterStart":range.map(|r|r.0),
        "characterEnd":range.map(|r|r.1),"sourceFileId":null,"sourceSpan":null,
        "provenance":"authorLabel","certainty":"confirmedObservation"})
}

fn request(body: (usize, usize)) -> Value {
    json!({
        "protocolVersion":1,"requestId":"1".repeat(32),"operation":"evaluateRenderedReview",
        "payload":{
            "pdfSha256":PDF,"compileRequestId":"2".repeat(32),"compileEpoch":5,"inputDigest":"3".repeat(64),
            "captureId":"df3c8bb6-11c5-4a4e-aea9-8b79e9fa2575","profile":{"id":"knu-csc-2025-rendered-review","version":"1.3.0"},
            "workType":"bachelorQualification","annotationVersion":11,"pageCount":1,"requestedPages":[0],
            "annotations":[annotation(1,"headingMajor",Some((11,19))),annotation(2,"body",Some(body))],
            "factsSha256":SHA
        }
    })
}

fn report(request: &Value, facts: &str) -> Value {
    let reply: Value = serde_json::from_str(&evaluate(&request.to_string(), facts)).unwrap();
    assert_eq!(reply["status"], "completed", "{reply}");
    assert_eq!(reply["payload"]["coverage"]["standardsComplete"], false);
    reply
}

fn row<'a>(report: &'a Value, rule: &str) -> &'a Value {
    report["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|entry| entry["ruleId"] == rule)
        .unwrap()
}

#[test]
fn native_pdf_body_above_is_a_counterexample_but_the_same_page_body_below_is_only_observed() {
    for (body, relation, outcome, reason) in [
        (
            (0, 9),
            "body-above-heading",
            "fail",
            "confirmed-major-heading-follows-body-on-page",
        ),
        (
            (21, 30),
            "body-below-heading",
            "indeterminate",
            "conditional-major-heading-precedes-body-on-page",
        ),
    ] {
        let result = report(&request(body), FACTS);
        let heading = row(&result, super::RULE_ID);
        assert_eq!(heading["outcome"], outcome);
        assert_eq!(heading["reason"], reason);
        assert_eq!(heading["ruleVersion"], "1.1.0");
        assert_eq!(heading["citation"]["pdfPages"], json!([18, 19]));
        let item = &heading["evidence"][0];
        let observed = &item["observation"];
        assert_eq!(observed["relativeOrder"], relation);
        assert_eq!(observed["bodyAnnotationId"], 2);
        assert_eq!(observed["bodyCharacterStart"], body.0);
        assert_eq!(observed["bodyCharacterEnd"], body.1);
        assert_eq!(observed["userUnit"].as_f64(), Some(2.0));
        let gap = item["measured"].as_f64().unwrap();
        let err = item["uncertainty"]["absoluteError"].as_f64().unwrap();
        assert!(err > 0.0 && gap.abs() > err);
        assert_eq!(gap > 0.0, body.0 == 0);
        assert_eq!(row(&result, "KNU-PAGINATION-001")["ruleVersion"], "1.1.0");
    }
}

#[test]
fn roles_must_be_explicit_author_confirmed_unique_and_nonoverlapping() {
    for (labels, reason) in [
        (
            vec![annotation(1, "headingMajor", Some((11, 19)))],
            "confirmed-heading-body-range-required",
        ),
        (
            vec![
                annotation(1, "headingMinor", Some((11, 19))),
                annotation(2, "body", Some((0, 9))),
            ],
            "confirmed-major-heading-range-required",
        ),
        (
            vec![
                annotation(1, "headingMajor", Some((11, 19))),
                annotation(2, "body", Some((0, 9))),
                annotation(3, "body", Some((21, 30))),
            ],
            "ambiguous-heading-role-pair",
        ),
        (
            vec![
                annotation(1, "headingMajor", Some((11, 19))),
                annotation(2, "body", Some((0, 21))),
            ],
            "heading-native-and-vertical-order-unestablished",
        ),
    ] {
        let mut req = request((0, 9));
        req["payload"]["annotations"] = json!(labels);
        assert_refusal(&req, FACTS, reason);
    }
    let mut req = request((0, 9));
    req["payload"]["annotations"][0]["certainty"] = json!("tentative");
    assert_refusal(&req, FACTS, "confirmed-major-heading-range-required");
}

#[test]
fn no_counterexample_with_rotated_unmeasured_or_divergent_geometry() {
    let mut facts: Value = serde_json::from_str(FACTS).unwrap();
    facts["pages"][0]["facts"]["rotation"] = json!(90);
    assert_refusal(
        &request((0, 9)),
        &facts.to_string(),
        "heading-upright-layout-required",
    );
    facts["pages"][0]["facts"]["rotation"] = json!(0);
    for char in &mut facts["pages"][0]["facts"]["characters"]
        .as_array_mut()
        .unwrap()[0..9]
    {
        let origin = char["origin"][1].as_f64().unwrap();
        char["origin"][1] = json!(origin - 130.0);
        for coordinate in [1, 3] {
            let previous = char["inkBox"][coordinate].as_f64().unwrap();
            char["inkBox"][coordinate] = json!(previous - 130.0);
        }
    }
    assert_refusal(
        &request((0, 9)),
        &facts.to_string(),
        "heading-native-and-vertical-order-unestablished",
    );
    let character = &mut facts["pages"][0]["facts"]["characters"][0];
    character["inkBox"] = Value::Null;
    character["inkUncertainty"] = Value::Null;
    character["inkStatus"] = json!("indeterminate");
    assert_refusal(
        &request((0, 9)),
        &facts.to_string(),
        "heading-ink-unavailable",
    );
}

#[test]
fn cumulative_profile_keeps_exact_pagination_title_number_overlap_behavior() {
    let mut facts: Value = serde_json::from_str(include_str!(
        "../fixtures/pagination/p16-pagination-native-v2.json"
    ))
    .unwrap();
    facts["pages"].as_array_mut().unwrap().truncate(1);
    let mut request = request((0, 9));
    request["payload"]["pageCount"] = json!(4);
    request["payload"]["pdfSha256"] = facts["documentSha256"].clone();
    request["payload"]["annotations"] = json!([
        annotation(1, "pageNumber", Some((0, 1))),
        annotation(2, "title", None)
    ]);
    let selected = report(&request, &facts.to_string());
    request["payload"]["profile"]["version"] = json!("1.2.0");
    let previous = report(&request, &facts.to_string());
    assert_eq!(
        row(&selected, "KNU-PAGINATION-001"),
        row(&previous, "KNU-PAGINATION-001")
    );
    assert_eq!(row(&selected, "KNU-PAGINATION-001")["outcome"], "fail");
}

fn assert_refusal(request: &Value, facts: &str, reason: &str) {
    let reply = report(request, facts);
    let heading = row(&reply, super::RULE_ID);
    assert_eq!(heading["outcome"], "indeterminate");
    assert_eq!(heading["reason"], reason);
    assert_eq!(heading["evidence"], json!([]));
}
