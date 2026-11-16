use super::*;
fn pair() -> (Value, Value) {
    let facts = json!({"schemaVersion":1,"documentSha256":"a".repeat(64),"pages":[{"capabilities":{
  "schemaVersion":1,"coordinateSpace":"pdf-raw-unrotated-bottom-left","physicalScale":"available","glyphInk":"unsupported","textStyle":"unsupported",
  "lineGrouping":"unsupported","maxCharacters":100000,"nominalSizeUnit":"raw-user-units","nominalSizeMethod":"pdfium-font-size"},"facts":{
   "schemaVersion":1,"pageIndex":0,"pageCount":1,"media":[0,0,210.0*72.0/25.4,297.0*72.0/25.4],"crop":null,
   "effective":[0,0,210.0*72.0/25.4,297.0*72.0/25.4],"rotation":0,"userUnit":{"status":"available","value":1,"method":"default-userunit"},
   "characters":[],"complete":true,"limitations":["glyph-ink-not-measured"]}}]});
    let request = json!({"protocolVersion":1,"requestId":"b".repeat(32),"operation":"evaluateRenderedReview","payload":{
  "pdfSha256":"a".repeat(64),"compileRequestId":"c".repeat(32),"compileEpoch":3,"inputDigest":"d".repeat(64),"captureId":"12345678-1234-1234-1234-123456789abc",
  "profile":{"id":"knu-csc-2025-rendered-review","version":"1.0.0"},"workType":"masterQualification","annotationVersion":2,"pageCount":1,
  "requestedPages":[0],"annotations":[],"factsSha256":"e".repeat(64)}});
    (request, facts)
}
fn response(r: &Value, f: &Value) -> Value {
    serde_json::from_str(&evaluate(&r.to_string(), &f.to_string())).unwrap()
}
fn result<'a>(v: &'a Value, id: &str) -> &'a Value {
    v["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == id)
        .unwrap()
}
#[test]
fn measured_a4_and_named_gaps() {
    let (r, f) = pair();
    let v = response(&r, &f);
    assert_eq!(v["status"], "completed");
    assert_eq!(result(&v, "KNU-PAPER-001")["outcome"], "pass");
    assert_eq!(
        result(&v, "KNU-MARGINS-001")["reason"],
        "glyph-ink-unavailable"
    );
    assert_eq!(
        result(&v, "KNU-TYPOGRAPHY-001")["reason"],
        "measurement-definition-unresolved"
    );
    assert_eq!(v["payload"]["coverage"]["standardsComplete"], false);
}
#[test]
fn native_float_roundoff_is_not_compliance_tolerance() {
    let (r, mut f) = pair();
    for key in ["media", "effective"] {
        for i in [2, 3] {
            let n = f["pages"][0]["facts"][key][i].as_f64().unwrap() as f32;
            f["pages"][0]["facts"][key][i] = json!(n as f64);
        }
    }
    assert_eq!(
        result(&response(&r, &f), "KNU-PAPER-001")["outcome"],
        "pass"
    );
    for key in ["media", "effective"] {
        f["pages"][0]["facts"][key][2] = json!(595.276);
    }
    assert_eq!(
        result(&response(&r, &f), "KNU-PAPER-001")["outcome"],
        "indeterminate"
    );
}
#[test]
fn scale_missing_and_a3_role_do_not_pass() {
    let (r, mut f) = pair();
    f["pages"][0]["capabilities"]["physicalScale"] = json!("unsupported");
    f["pages"][0]["facts"]["userUnit"] =
        json!({"status":"unsupported","value":null,"method":"unavailable"});
    assert_eq!(
        result(&response(&r, &f), "KNU-PAPER-001")["outcome"],
        "unavailable"
    );
    let (r, mut f) = pair();
    for k in ["media", "effective"] {
        f["pages"][0]["facts"][k] = json!([0, 0, 297.0 * 72.0 / 25.4, 420.0 * 72.0 / 25.4]);
    }
    assert_eq!(
        result(&response(&r, &f), "KNU-PAPER-001")["outcome"],
        "indeterminate"
    );
}
#[test]
fn identity_unknown_duplicate_and_shapes_rejected() {
    let (r, f) = pair();
    let mut wrong = f.clone();
    wrong["documentSha256"] = json!("f".repeat(64));
    assert_eq!(response(&r, &wrong)["status"], "failed");
    let mut wrong = r.clone();
    wrong["payload"]["fabricated"] = json!(1);
    assert_eq!(response(&wrong, &f)["status"], "failed");
    let duplicate = r.to_string().replacen(
        "\"protocolVersion\":1",
        "\"protocolVersion\":1,\"protocolVersion\":1",
        1,
    );
    assert_eq!(
        serde_json::from_str::<Value>(&evaluate(&duplicate, &f.to_string())).unwrap()["status"],
        "failed"
    );
    assert!(evaluate("[]", &f.to_string()).contains("failed"));
}
#[test]
fn invalid_char_and_overlapping_author_labels_rejected() {
    let (mut r, f) = pair();
    let a = json!({"annotationId":1,"role":"title","pageIndex":0,"characterStart":null,"characterEnd":null,"sourceFileId":null,"sourceSpan":null,"provenance":"authorLabel","certainty":"confirmedObservation"});
    let mut b = a.clone();
    b["annotationId"] = json!(2);
    b["role"] = json!("body");
    r["payload"]["annotations"] = json!([a, b]);
    assert_eq!(response(&r, &f)["error"]["code"], "InvalidAnnotations");
}
#[test]
fn catalog_cites_actual_source_and_has_no_layout_fixes() {
    let v: Value = serde_json::from_str(&catalog::json()).unwrap();
    assert_eq!(v["guidelineSha256"], catalog::GUIDELINE_SHA256);
    assert_eq!(v["rules"].as_array().unwrap().len(), 13);
    for d in v["rules"].as_array().unwrap() {
        assert_eq!(d["fixPolicy"], "none");
        assert!(!d["citation"]["printedPages"].as_array().unwrap().is_empty());
    }
}
