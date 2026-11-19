use crate::rendered_review::evaluate;
use serde_json::{json, Value};

const BATCH: &str = include_str!("../fixtures/pagination/p16-pagination-native-v2.json");
const LINEAGE: &str = include_str!("../fixtures/pagination/lineage.json");

fn batch() -> Value {
    serde_json::from_str(BATCH).unwrap()
}

fn annotation(id: u64, page: u16, role: &str, range: Option<(usize, usize)>) -> Value {
    json!({"annotationId":id,"pageIndex":page,"role":role,"provenance":"authorLabel","certainty":"confirmedObservation",
        "characterStart":range.map(|r|r.0),"characterEnd":range.map(|r|r.1),"sourceFileId":null,"sourceSpan":null})
}

fn number(id: u64, page: u16) -> Value {
    annotation(id, page, "pageNumber", Some((0, 1)))
}

fn request(facts: &Value, pages: &[u16], annotations: &[Value]) -> Value {
    let lineage: Value = serde_json::from_str(LINEAGE).unwrap();
    json!({"protocolVersion":1,"requestId":"b".repeat(32),"operation":"evaluateRenderedReview","payload":{
        "pdfSha256":facts["documentSha256"],"compileRequestId":"c".repeat(32),"compileEpoch":3,"inputDigest":"d".repeat(64),
        "captureId":"12345678-1234-1234-1234-123456789abc","profile":{"id":"knu-csc-2025-rendered-review","version":"1.2.0"},
        "workType":"masterQualification","annotationVersion":2,"pageCount":4,"requestedPages":pages,"annotations":annotations,
        "factsSha256":lineage["batch"]["sha256"]}})
}

fn single(page: u16, annotations: &[Value]) -> (Value, Value) {
    let mut facts = batch();
    facts["pages"] = json!([facts["pages"][usize::from(page)].clone()]);
    (request(&facts, &[page], annotations), facts)
}

fn report(r: &Value, f: &Value) -> Value {
    serde_json::from_str(&evaluate(&r.to_string(), &f.to_string())).unwrap()
}

fn results(report: &Value) -> Vec<&Value> {
    assert_eq!(report["status"], "completed", "{report}");
    report["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|row| row["ruleId"] == "KNU-PAGINATION-001")
        .collect()
}

fn unknown(request: &Value, facts: &Value, reason: &str) {
    let r = report(request, facts);
    let rows = results(&r);
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0]["reason"], reason);
    assert_eq!(rows[0]["supportState"], "partial");
    assert_eq!(rows[0]["outcome"], "indeterminate");
    assert_eq!(rows[0]["evidence"], json!([]));
}

#[test]
fn genuine_p16_pdf_title_body_and_appendices_produce_bounded_observations() {
    let facts = batch();
    let r = request(
        &facts,
        &[0, 1, 2, 3],
        &[
            annotation(99, 0, "title", None),
            number(1, 0),
            number(2, 1),
            annotation(100, 2, "appendix", None),
            number(3, 2),
            annotation(101, 3, "appendix", None),
            number(4, 3),
        ],
    );
    let reply: Value = serde_json::from_str(&evaluate(&r.to_string(), BATCH)).unwrap();
    assert_eq!(reply["payload"]["profile"]["version"], "1.2.0");
    let rows = results(&reply);
    assert_eq!(rows.len(), 4);
    for (index, (row, expected)) in rows
        .iter()
        .zip([
            (
                "1",
                "title-number-present",
                "fail",
                "confirmed-title-number-present",
            ),
            (
                "2",
                "index-match",
                "indeterminate",
                "confirmed-page-number-index-match",
            ),
            (
                "1",
                "index-mismatch",
                "indeterminate",
                "confirmed-page-number-index-mismatch",
            ),
            (
                "4",
                "index-match",
                "indeterminate",
                "confirmed-page-number-index-match",
            ),
        ])
        .enumerate()
    {
        let page = &facts["pages"][index]["facts"];
        assert_eq!(
            row["citation"],
            json!({"section":"3.3;3.16","printedPages":[19,31],"pdfPages":[19,31]})
        );
        assert_eq!(row["ruleVersion"], "1.1.0");
        assert_eq!(row["limitations"], json!(super::LIMITATIONS));
        assert_eq!(row["supportState"], "partial");
        assert_eq!(row["outcome"], expected.2);
        assert_eq!(row["reason"], expected.3);
        let evidence = row["evidence"].as_array().unwrap();
        assert_eq!(evidence.len(), 1);
        let e = &evidence[0];
        assert_eq!(e["pageIndex"], index);
        assert_eq!(e["characterStart"], 0);
        assert_eq!(e["characterEnd"], 1);
        assert_eq!(
            e["roleProvenance"],
            json!({"annotationId":index+1,"role":"pageNumber","provenance":"authorLabel","certainty":"confirmedObservation"})
        );
        assert_eq!(e["measured"], expected.0);
        assert_eq!(e["method"], "confirmed-page-number-native-ink");
        assert_eq!(e["sourceLocation"], Value::Null);
        assert_eq!(
            e["expected"],
            if index == 0 {
                Value::Null
            } else {
                json!(index + 1)
            }
        );
        let observation = &e["observation"];
        assert_eq!(observation["kind"], "confirmed-page-number");
        assert_eq!(observation["comparison"], expected.1);
        assert_eq!(observation["physicalPageNumber"], index + 1);
        assert_eq!(
            observation["asciiDigitValue"],
            expected.0.parse::<u64>().unwrap()
        );
        assert_eq!(
            observation["titleAnnotationId"],
            if index == 0 { json!(99) } else { Value::Null }
        );
        let p = &observation["placement"];
        let top = page["media"][3].as_f64().unwrap() - e["rawBox"][3].as_f64().unwrap();
        let right = page["media"][2].as_f64().unwrap() - e["rawBox"][2].as_f64().unwrap();
        assert_eq!(p["effective"][0].as_f64(), Some(8.0));
        assert_eq!(p["effective"][3].as_f64(), Some(780.0));
        assert!((p["topClearanceMm"].as_f64().unwrap() - top * 2.0 * 25.4 / 72.0).abs() < 1e-10);
        assert!(
            (p["rightClearanceMm"].as_f64().unwrap() - right * 2.0 * 25.4 / 72.0).abs() < 1e-10
        );
        let native_bound = page["characters"][0]["inkUncertainty"]["absoluteErrorRaw"]
            .as_f64()
            .unwrap();
        let bound = p["absoluteErrorMm"].as_f64().unwrap();
        assert!(bound > native_bound * 2.0 * 25.4 / 72.0 && bound < 0.06);
    }
}

#[test]
fn empty_or_unlabelled_pages_do_not_prove_absence() {
    for page in [0, 1, 2] {
        let (r, mut f) = single(page, &[]);
        unknown(&r, &f, "confirmed-page-number-range-required");
        f["pages"][0]["facts"]["characters"] = json!([]);
        unknown(&r, &f, "confirmed-page-number-range-required");
    }
    let (r, f) = single(0, &[annotation(1, 0, "title", None)]);
    unknown(&r, &f, "confirmed-page-number-range-required");
    let (r, f) = single(0, &[number(1, 0)]);
    unknown(&r, &f, "confirmed-title-role-required");
    let (r, f) = single(2, &[number(1, 2), annotation(2, 2, "title", Some((1, 2)))]);
    unknown(&r, &f, "conflicting-title-role");
}

#[test]
fn tentative_whole_page_or_duplicate_number_roles_do_not_establish_work_number() {
    let mut tentative = number(1, 3);
    tentative["certainty"] = json!("tentative");
    for label in [tentative.clone(), annotation(1, 3, "pageNumber", None)] {
        let (r, f) = single(3, &[label]);
        unknown(&r, &f, "confirmed-page-number-range-required");
    }
    let (r, f) = single(
        3,
        &[tentative, annotation(2, 3, "pageNumber", Some((20, 21)))],
    );
    unknown(&r, &f, "ambiguous-page-number-labels");
    let mut imported = number(1, 3);
    imported["provenance"] = json!("validatedNativeClassification");
    let (r, f) = single(3, &[imported]);
    unknown(&r, &f, "confirmed-page-number-range-required");
}

#[test]
fn unicode_digit_like_scalars_and_unknown_mappings_do_not_get_normalized() {
    for scalar in ['١', '۱', '２', 'Ⅱ', 'І', 'I', '🙂', '\u{fffd}', '\0', ' '] {
        let (r, mut f) = single(1, &[number(1, 1)]);
        f["pages"][0]["facts"]["characters"][0]["unicode"] = json!(scalar as u32);
        unknown(&r, &f, "page-number-non-ascii-or-out-of-range");
    }
    let (r, f) = synthetic_number("9007199254740992");
    unknown(&r, &f, "page-number-non-ascii-or-out-of-range");
    let (r, f) = synthetic_number(&"0".repeat(33));
    unknown(&r, &f, "page-number-non-ascii-or-out-of-range");
    for (literal, numeric, comparison) in [
        ("02", 2u64, "index-match"),
        ("9007199254740991", 9_007_199_254_740_991, "index-mismatch"),
    ] {
        let (r, f) = synthetic_number(literal);
        let result = report(&r, &f);
        let e = &results(&result)[0]["evidence"][0];
        assert_eq!(e["measured"], literal);
        assert_eq!(e["observation"]["asciiDigitValue"], numeric);
        assert_eq!(e["observation"]["comparison"], comparison);
    }
}

fn synthetic_number(literal: &str) -> (Value, Value) {
    let (r, mut f) = single(
        1,
        &[annotation(1, 1, "pageNumber", Some((0, literal.len())))],
    );
    let facts = &mut f["pages"][0]["facts"];
    let template = facts["characters"][0].clone();
    let mut glyphs = Vec::new();
    for (index, byte) in literal.bytes().enumerate() {
        let mut g = template.clone();
        let x = 40.0 + index as f64 * 5.0;
        g["unicode"] = json!(byte);
        g["index"] = json!(index);
        g["origin"][0] = json!(x);
        g["matrix"][4] = json!(x);
        g["inkBox"][0] = json!(x);
        g["inkBox"][2] = json!(x + 3.0);
        glyphs.push(g);
    }
    facts["characters"] = json!(glyphs);
    (r, f)
}

#[test]
fn incomplete_uncertain_or_non_horizontal_ink_and_unknown_scale_stay_indeterminate() {
    let (r, mut f) = single(1, &[number(1, 1)]);
    f["pages"][0]["facts"]["rotation"] = json!(90);
    unknown(&r, &f, "page-number-unavailable-ink");
    let (r, mut f) = single(1, &[number(1, 1)]);
    let g = &mut f["pages"][0]["facts"]["characters"][0];
    g["inkStatus"] = json!("indeterminate");
    g["inkBox"] = Value::Null;
    g["inkUncertainty"] = Value::Null;
    unknown(&r, &f, "page-number-unavailable-ink");
    let (r, mut f) = synthetic_number("12");
    f["pages"][0]["facts"]["characters"][1]["origin"][1] = json!(685);
    unknown(&r, &f, "page-number-unavailable-ink");
    let (r, mut f) = single(1, &[number(1, 1)]);
    f["pages"][0]["facts"]["characters"][0]["inkUncertainty"]["absoluteErrorRaw"] = json!(0);
    unknown(&r, &f, "page-number-unavailable-ink");
    let (r, mut f) = single(1, &[number(1, 1)]);
    f["pages"][0]["facts"]["characters"][0]["inkBox"][3] = json!(779.95);
    unknown(&r, &f, "page-number-ink-outside-effective-page");
    let (r, mut f) = single(1, &[number(1, 1)]);
    f["pages"][0]["facts"]["userUnit"] =
        json!({"status":"indeterminate","value":null,"method":"invalid"});
    f["pages"][0]["capabilities"]["physicalScale"] = json!("indeterminate");
    unknown(&r, &f, "page-number-physical-scale-unavailable");
}

#[test]
fn title_page_overlap_exception_only_applies_to_the_new_confirmed_explicit_pair() {
    let (mut r, f) = single(0, &[number(1, 0), annotation(2, 0, "title", None)]);
    assert_eq!(results(&report(&r, &f))[0]["outcome"], "fail");
    r["payload"]["profile"]["version"] = json!("1.0.0");
    assert_eq!(report(&r, &f)["error"]["code"], "InvalidAnnotations");
    r["payload"]["profile"]["version"] = json!("1.2.0");
    r["payload"]["annotations"][1]["certainty"] = json!("tentative");
    assert_eq!(report(&r, &f)["error"]["code"], "InvalidAnnotations");
    let (r, f) = single(1, &[number(1, 1), annotation(2, 1, "title", None)]);
    assert_eq!(report(&r, &f)["error"]["code"], "InvalidAnnotations");
    let (r, f) = single(
        0,
        &[
            annotation(1, 0, "pageNumber", None),
            annotation(2, 0, "title", None),
        ],
    );
    assert_eq!(report(&r, &f)["error"]["code"], "InvalidAnnotations");
}
