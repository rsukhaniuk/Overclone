use super::*;

fn pair() -> (Value, Value) {
    let facts: Value =
        serde_json::from_str(include_str!("fixtures/native-v2-rect-batch.json")).unwrap();
    let request = json!({"protocolVersion":1,"requestId":"b".repeat(32),"operation":"evaluateRenderedReview","payload":{
        "pdfSha256":facts["documentSha256"],"compileRequestId":"c".repeat(32),"compileEpoch":3,"inputDigest":"d".repeat(64),
        "captureId":"12345678-1234-1234-1234-123456789abc","profile":{"id":"knu-csc-2025-rendered-review","version":"1.0.0"},
        "workType":"masterQualification","annotationVersion":2,"pageCount":1,"requestedPages":[0],"annotations":[annotation("body",None)],"factsSha256":"7e33123840008f9809cbd8a53d7cddee8851947275c0c58160fd8be195bc164b"}});
    (request, facts)
}
fn annotation(role: &str, range: Option<(usize, usize)>) -> Value {
    json!({"annotationId":if role=="body" {1} else {2},"role":role,"pageIndex":0,
        "characterStart":range.map(|r|r.0),"characterEnd":range.map(|r|r.1),"sourceFileId":null,"sourceSpan":null,
        "provenance":"authorLabel","certainty":"confirmedObservation"})
}
fn response(r: &Value, f: &Value) -> Value {
    serde_json::from_str(&evaluate(&r.to_string(), &f.to_string())).unwrap()
}
fn rule<'a>(response: &'a Value, id: &str) -> &'a Value {
    response["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == id)
        .unwrap()
}
fn scene(lines: &[Vec<f64>]) -> (Value, Value) {
    // Synthetic consumer vectors built from the genuine native outline shape.
    // They exercise geometry/classification, not native execution of new PDFs.
    let (mut r, mut f) = pair();
    let template = f["pages"][0]["facts"]["characters"][0].clone();
    let mut chars = Vec::new();
    for (row, xs) in lines.iter().enumerate() {
        let y = 200.0 - row as f64 * 20.0;
        for x in xs {
            let mut ch = template.clone();
            ch["index"] = json!(chars.len());
            ch["origin"] = json!([x, y]);
            ch["matrix"] = json!([1, 0, 0, 1, x, y]);
            ch["inkBox"] = json!([x, y, x + 7.201171875, y + 9.6005859375]);
            chars.push(ch);
        }
    }
    f["pages"][0]["facts"]["characters"] = json!(chars);
    for k in ["media", "effective"] {
        f["pages"][0]["facts"][k] = json!([0, 0, 600, 800]);
    }
    r["payload"]["annotations"] = json!([
        annotation("body", None),
        annotation("paragraph", Some((0, chars.len())))
    ]);
    (r, f)
}
#[test]
fn genuine_outline_margins_are_numeric_conditional_and_include_native_uncertainty() {
    let (r, f) = pair();
    let v = response(&r, &f);
    assert_eq!(v["status"], "completed");
    let margins = rule(&v, "KNU-MARGINS-001");
    assert_eq!(margins["outcome"], "indeterminate");
    assert_eq!(margins["reason"], "conditional-body-ink-margins");
    let evidence = margins["evidence"].as_array().unwrap();
    assert_eq!(evidence.len(), 4);
    assert!((evidence[0]["measured"].as_f64().unwrap() - 10.0 * 25.4 / 72.0).abs() < 1e-12);
    assert!(
        evidence[0]["uncertainty"]["absoluteError"]
            .as_f64()
            .unwrap()
            > 0.11816494806855918 * 25.4 / 72.0
    );
    assert_eq!(evidence[0]["classification"]["lineCount"], 1);
    assert_eq!(evidence[0]["roleProvenance"]["provenance"], "authorLabel");
    assert_eq!(
        rule(&v, "KNU-TYPOGRAPHY-001")["reason"],
        "measurement-definition-unresolved"
    );
    assert_eq!(v["payload"]["coverage"]["standardsComplete"], false);
    if let Ok(path) = std::env::var("RENDERED_METRICS_VECTOR_PATH") {
        std::fs::write(
            path,
            serde_json::to_vec_pretty(&json!({"request":r,"facts":f,"response":v})).unwrap(),
        )
        .unwrap();
    }
}
#[test]
fn actual_userunit_and_nonzero_media_use_mm_and_scaled_error_once() {
    let (r, mut f) = pair();
    let base = response(&r, &f);
    f["pages"][0]["facts"]["userUnit"] =
        json!({"status":"available","value":2,"method":"page-dictionary-userunit"});
    let scaled = response(&r, &f);
    let e = |v: &Value| rule(v, "KNU-MARGINS-001")["evidence"][0].clone();
    assert_eq!(
        e(&scaled)["measured"].as_f64().unwrap(),
        e(&base)["measured"].as_f64().unwrap() * 2.0
    );
    assert!(
        e(&scaled)["uncertainty"]["absoluteError"].as_f64().unwrap()
            > e(&base)["uncertainty"]["absoluteError"].as_f64().unwrap() * 2.0
    );
    for key in ["media", "effective"] {
        f["pages"][0]["facts"][key] = json!([-5, 0, 72, 144]);
    }
    assert!(
        (e(&response(&r, &f))["measured"].as_f64().unwrap() - 15.0 * 2.0 * 25.4 / 72.0).abs()
            < 1e-12
    );
}
#[test]
fn labels_tentative_or_claimed_machine_provenance_never_mint_normative_proof() {
    for provenance in [
        "authorLabel",
        "literalSourceAndNativeMap",
        "validatedNativeClassification",
    ] {
        for certainty in ["confirmedObservation", "tentative"] {
            let (mut r, f) = pair();
            r["payload"]["annotations"][0]["provenance"] = json!(provenance);
            r["payload"]["annotations"][0]["certainty"] = json!(certainty);
            let v = response(&r, &f);
            assert_eq!(rule(&v, "KNU-MARGINS-001")["outcome"], "indeterminate");
        }
    }
}
#[test]
fn paragraph_indent_uses_first_and_unique_continuation_ink_not_nominal_font_size() {
    let (r, mut f) = scene(&[
        vec![136.0, 144.0, 152.0],
        vec![100.0, 108.0, 116.0, 124.0, 132.0],
    ]);
    let v = response(&r, &f);
    let result = rule(&v, "KNU-PARAGRAPH-RENDERED-001");
    assert_eq!(result["reason"], "conditional-paragraph-indent");
    assert_eq!(result["outcome"], "indeterminate");
    assert!((result["evidence"][0]["measured"].as_f64().unwrap() - 12.7).abs() < 1e-12);
    assert_eq!(result["evidence"][0]["expected"], 12.7);
    if let Ok(path) = std::env::var("RENDERED_METRICS_VECTOR_PATH") {
        std::fs::write(
            format!("{path}.paragraph.json"),
            serde_json::to_vec_pretty(&json!({"request":r,"facts":f,"response":v})).unwrap(),
        )
        .unwrap();
    }
    for ch in f["pages"][0]["facts"]["characters"].as_array_mut().unwrap() {
        ch["nominalSize"] = json!(999);
    }
    assert_eq!(
        rule(&response(&r, &f), "KNU-PARAGRAPH-RENDERED-001")["evidence"],
        result["evidence"]
    );
}
#[test]
fn columns_reordering_rotation_missing_ink_and_inconsistent_continuations_are_indeterminate() {
    let (r, f) = scene(&[
        vec![10.0, 18.0, 100.0, 108.0],
        vec![10.0, 18.0, 100.0, 108.0],
    ]);
    assert_eq!(
        rule(&response(&r, &f), "KNU-MARGINS-001")["reason"],
        "metric-ambiguous-columns"
    );
    for mode in 0..7 {
        let (r, mut f) = scene(&[
            vec![136.0, 144.0, 152.0],
            vec![100.0, 108.0, 116.0, 124.0, 132.0],
        ]);
        match mode {
            0 => f["pages"][0]["facts"]["rotation"] = json!(90),
            1 => f["pages"][0]["facts"]["characters"][0]["matrix"][1] = json!(0.1),
            2 => {
                f["pages"][0]["facts"]["characters"][0]["inkStatus"] = json!("indeterminate");
                f["pages"][0]["facts"]["characters"][0]["inkBox"] = Value::Null;
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"] = Value::Null;
            }
            3 => f["pages"][0]["facts"]["characters"][1]["origin"][0] = json!(120),
            4 => f["pages"][0]["facts"]["characters"][0]["matrix"][0] = json!(-1),
            5 => {
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"]["absoluteErrorRaw"] =
                    json!(100)
            }
            _ => f["pages"][0]["facts"]["characters"][0]["origin"] = Value::Null,
        }
        let v = response(&r, &f);
        let result = rule(&v, "KNU-PARAGRAPH-RENDERED-001");
        assert_eq!(result["outcome"], "indeterminate", "mode {mode}");
        assert!(
            result["evidence"].as_array().unwrap().is_empty(),
            "mode {mode}"
        );
    }
    let (r, f) = scene(&[
        vec![136.0, 144.0, 152.0],
        vec![100.0, 108.0, 116.0, 124.0, 132.0],
        vec![110.0, 118.0],
    ]);
    assert_eq!(
        rule(&response(&r, &f), "KNU-PARAGRAPH-RENDERED-001")["reason"],
        "metric-continuation-not-established"
    );
}
#[test]
fn strict_versions_uncertainty_bounds_and_stale_identity_are_refused() {
    for mode in 0..11 {
        let (mut r, mut f) = pair();
        match mode {
            0 => {
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"]["absoluteErrorRaw"] =
                    json!(-1)
            }
            1 => {
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"]["absoluteErrorRaw"] =
                    json!(1000001)
            }
            2 => {
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"]["method"] =
                    json!("guessed")
            }
            3 => {
                f["pages"][0]["facts"]["characters"][0]
                    .as_object_mut()
                    .unwrap()
                    .remove("inkUncertainty");
            }
            4 => f["pages"][0]["capabilities"]["schemaVersion"] = json!(1),
            5 => f["pages"][0]["facts"]["schemaVersion"] = json!(1),
            6 => f["pages"][0]["capabilities"]["glyphInkMethod"] = Value::Null,
            7 => r["payload"]["pdfSha256"] = json!("0".repeat(64)),
            8 => f["pages"][0]["facts"]["characters"][0]["unicode"] = json!(0xd800),
            9 => {
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"] =
                    json!([0.1, "native-outline-fill-witness"])
            }
            _ => f["pages"][0]["facts"]["characters"][0]["inkUncertainty"]["extra"] = json!(true),
        }
        assert_eq!(response(&r, &f)["status"], "failed", "mode {mode}");
    }
}
#[test]
fn unicode_native_indexes_and_explicit_ranges_remain_scalar_and_correlated() {
    let (r, mut f) = scene(&[
        vec![136.0, 144.0, 152.0],
        vec![100.0, 108.0, 116.0, 124.0, 132.0],
    ]);
    f["pages"][0]["facts"]["characters"][0]["unicode"] = json!('Ї' as u32);
    f["pages"][0]["facts"]["characters"][1]["unicode"] = json!('🙂' as u32);
    assert_eq!(
        rule(&response(&r, &f), "KNU-PARAGRAPH-RENDERED-001")["evidence"][0]["characterEnd"],
        8
    );
    let mut stale = r.clone();
    stale["payload"]["annotations"][1]["characterEnd"] = json!(9);
    assert_eq!(response(&stale, &f)["error"]["code"], "InvalidAnnotations");
}

#[test]
fn excluded_roles_quota_and_v1_null_extensions_stay_explicit() {
    let (mut r, f) = pair();
    let mut note = annotation("note", Some((0, 1)));
    note["annotationId"] = json!(3);
    r["payload"]["annotations"]
        .as_array_mut()
        .unwrap()
        .push(note);
    assert_eq!(
        rule(&response(&r, &f), "KNU-MARGINS-001")["reason"],
        "metric-role-scope-overlap"
    );

    let (mut r, f) = scene(&[vec![10.0; 9]]);
    let labels: Vec<_> = (0..9)
        .map(|i| {
            let mut a = annotation("body", Some((i, i + 1)));
            a["annotationId"] = json!(i + 1);
            a
        })
        .collect();
    r["payload"]["annotations"] = json!(labels);
    assert_eq!(
        rule(&response(&r, &f), "KNU-MARGINS-001")["reason"],
        "metric-evidence-quota"
    );

    let (r, mut f) = pair();
    f["pages"][0]["capabilities"]["schemaVersion"] = json!(1);
    f["pages"][0]["capabilities"]
        .as_object_mut()
        .unwrap()
        .remove("glyphInkMethod");
    f["pages"][0]["facts"]["schemaVersion"] = json!(1);
    f["pages"][0]["facts"]["characters"][0]["inkUncertainty"] = Value::Null;
    assert_eq!(response(&r, &f)["status"], "failed"); // v1 did not have this field, including null.
    f["pages"][0]["facts"]["characters"][0]
        .as_object_mut()
        .unwrap()
        .remove("inkUncertainty");
    assert_eq!(
        rule(&response(&r, &f), "KNU-MARGINS-001")["reason"],
        "glyph-ink-uncertainty-unavailable"
    );
}

#[test]
fn visible_ink_mapped_to_whitespace_cannot_disappear_from_margin_bounds() {
    let (r, mut f) = scene(&[vec![100.0, 108.0, 116.0]]);
    // A legal Unicode mapping is not proof that the underlying glyph is blank.
    f["pages"][0]["facts"]["characters"][0]["unicode"] = json!(32);
    let v = response(&r, &f);
    let margins = rule(&v, "KNU-MARGINS-001");
    assert_eq!(margins["outcome"], "indeterminate");
    assert_eq!(margins["reason"], "metric-painted-whitespace-ambiguous");
    assert!(margins["evidence"].as_array().unwrap().is_empty());
}
