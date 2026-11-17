use super::*;

fn pair() -> (Value, Value) {
    let facts: Value =
        serde_json::from_str(include_str!("fixtures/typography/native-two-line-v2.json")).unwrap();
    let request = json!({"protocolVersion":1,"requestId":"b".repeat(32),"operation":"evaluateRenderedReview","payload":{
        "pdfSha256":facts["documentSha256"],"compileRequestId":"c".repeat(32),"compileEpoch":3,"inputDigest":"d".repeat(64),
        "captureId":"12345678-1234-1234-1234-123456789abc","profile":{"id":"knu-csc-2025-rendered-review","version":"1.0.0"},
        "workType":"masterQualification","annotationVersion":2,"pageCount":1,"requestedPages":[0],"annotations":[annotation(1,"body",None)],
        "factsSha256":serde_json::from_str::<Value>(include_str!("fixtures/typography/native-two-line-v2.json.lineage.json")).unwrap()["facts"]["sha256"]}});
    (request, facts)
}
fn annotation(id: u64, role: &str, range: Option<(usize, usize)>) -> Value {
    json!({"annotationId":id,"role":role,"pageIndex":0,"characterStart":range.map(|r|r.0),"characterEnd":range.map(|r|r.1),
        "sourceFileId":null,"sourceSpan":null,"provenance":"authorLabel","certainty":"confirmedObservation"})
}
fn reply(r: &Value, f: &Value) -> Value {
    serde_json::from_str(&evaluate(&r.to_string(), &f.to_string())).unwrap()
}
fn result(v: &Value) -> &Value {
    v["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == "KNU-TYPOGRAPHY-001")
        .unwrap()
}
fn metric<'a>(r: &'a Value, name: &str) -> &'a Value {
    r["evidence"]
        .as_array()
        .unwrap()
        .iter()
        .find(|e| e["metric"] == name)
        .unwrap()
}
fn synthetic(rows: &[(f64, Vec<(u32, f64, f64)>)]) -> (Value, Value) {
    // Labelled synthetic consumer geometry; only pair() is genuine native facts.
    let (r, mut f) = pair();
    let template = f["pages"][0]["facts"]["characters"][0].clone();
    let mut chars = Vec::new();
    for (y, columns) in rows {
        for (scalar, x, height) in columns {
            let mut c = template.clone();
            c["index"] = json!(chars.len());
            c["unicode"] = json!(scalar);
            c["origin"] = json!([x, y]);
            c["matrix"] = json!([1, 0, 0, 1, x, y]);
            c["inkBox"] = json!([x, y, x + 7.2, y + height]);
            c["inkUncertainty"]["absoluteErrorRaw"] = json!(0.1);
            chars.push(c);
        }
    }
    f["pages"][0]["facts"]["characters"] = json!(chars);
    (r, f)
}
#[test]
fn genuine_two_line_native_ink_and_baselines_are_numeric_conditional_only() {
    let (r, f) = pair();
    let v = reply(&r, &f);
    assert_eq!(v["status"], "completed");
    let t = result(&v);
    assert_eq!(t["supportState"], "partial");
    assert_eq!(t["outcome"], "indeterminate");
    assert_eq!(t["reason"], "conditional-typography-observations");
    assert_eq!(t["evidence"].as_array().unwrap().len(), 4);
    for name in ["letter-ink-height-min", "letter-ink-height-max"] {
        let e = metric(t, name);
        assert!((e["measured"].as_f64().unwrap() - 9.6 * 25.4 / 72.0).abs() < 0.001);
        assert_eq!(e["expected"], typography::HEIGHT_EXPECTED);
        assert_eq!(e["method"], "native-glyph-ink-height");
        assert!(e["uncertainty"]["absoluteError"].as_f64().unwrap() > 0.2 * 25.4 / 72.0);
    }
    for name in ["baseline-distance-min", "baseline-distance-max"] {
        let e = metric(t, name);
        assert!((e["measured"].as_f64().unwrap() - 20.0 * 25.4 / 72.0).abs() < 1e-12);
        assert_eq!(e["expected"], typography::BASELINE_EXPECTED);
        assert_eq!(e["method"], "classified-baseline-distance");
        assert_eq!(e["classification"]["lineCount"], 2);
        assert_eq!(e["sourceLocation"], Value::Null);
    }
    assert_eq!(v["payload"]["coverage"]["standardsComplete"], false);
    if let Ok(path) = std::env::var("RENDERED_TYPOGRAPHY_VECTOR_PATH") {
        std::fs::write(
            path,
            serde_json::to_vec_pretty(&json!({"request":r,"facts":f,"response":v})).unwrap(),
        )
        .unwrap();
    }
}
#[test]
fn unicode_alphabetic_heights_and_neighbors_have_independent_extrema() {
    let (r, f) = synthetic(&[
        (
            110.0,
            vec![
                (0x0416, 10.0, 8.0),
                (0x0457, 18.0, 9.0),
                (0x1f642, 26.0, 12.0),
            ],
        ),
        (90.0, vec![(0x4e00, 10.0, 6.0), (49, 18.0, 3.0)]),
        (50.0, vec![(0x0410, 10.0, 10.0), (0x0411, 18.0, 7.0)]),
    ]);
    let v = reply(&r, &f);
    let t = result(&v);
    for (name, raw) in [
        ("letter-ink-height-min", 6.0),
        ("letter-ink-height-max", 10.0),
        ("baseline-distance-min", 20.0),
        ("baseline-distance-max", 40.0),
    ] {
        assert!((metric(t, name)["measured"].as_f64().unwrap() - raw * 25.4 / 72.0).abs() < 1e-12);
    }
}
#[test]
fn font_metadata_never_changes_ink_height_or_invents_effective_style() {
    let (r, mut f) = pair();
    let before = reply(&r, &f);
    for c in f["pages"][0]["facts"]["characters"].as_array_mut().unwrap() {
        c["fontName"] = json!("Untrusted Bold Italic 1.8mm");
        c["fontFlags"] = json!(262144);
        c["nominalSize"] = json!(999);
    }
    assert_eq!(
        result(&reply(&r, &f))["evidence"],
        result(&before)["evidence"]
    );
}
#[test]
fn physical_scale_and_uncertainty_are_applied_once() {
    let (r, mut f) = pair();
    let v = reply(&r, &f);
    f["pages"][0]["facts"]["userUnit"] =
        json!({"status":"available","value":2,"method":"page-dictionary-userunit"});
    let scaled = reply(&r, &f);
    for name in [
        "letter-ink-height-min",
        "letter-ink-height-max",
        "baseline-distance-min",
        "baseline-distance-max",
    ] {
        let old = metric(result(&v), name);
        let new = metric(result(&scaled), name);
        assert_eq!(
            new["measured"].as_f64().unwrap(),
            old["measured"].as_f64().unwrap() * 2.0
        );
        assert!(
            new["uncertainty"]["absoluteError"].as_f64().unwrap()
                > old["uncertainty"]["absoluteError"].as_f64().unwrap() * 2.0
        );
    }
}
#[test]
fn missing_ink_rotation_columns_reorder_and_unknown_whitespace_refuse_metrics() {
    for mode in 0..8 {
        let (r, mut f) = synthetic(&[
            (100.0, vec![(65, 10.0, 8.0), (66, 18.0, 8.0)]),
            (80.0, vec![(67, 10.0, 8.0), (68, 18.0, 8.0)]),
        ]);
        let p = &mut f["pages"][0]["facts"];
        match mode {
            0 => p["rotation"] = json!(90),
            1 => p["characters"][0]["matrix"][1] = json!(0.1),
            2 => p["characters"][0]["matrix"][0] = json!(-1),
            3 => {
                p["characters"][0]["inkStatus"] = json!("indeterminate");
                p["characters"][0]["inkBox"] = Value::Null;
                p["characters"][0]["inkUncertainty"] = Value::Null;
            }
            4 => p["characters"][0]["origin"] = Value::Null,
            5 => {
                p["characters"][0]["unicode"] = json!(32);
                p["characters"][0]["inkStatus"] = json!("indeterminate");
                p["characters"][0]["inkBox"] = Value::Null;
                p["characters"][0]["inkUncertainty"] = Value::Null;
            }
            6 => p["characters"][1]["origin"][0] = json!(9),
            _ => p["characters"][0]["inkUncertainty"]["absoluteErrorRaw"] = json!(5),
        }
        let v = reply(&r, &f);
        assert_eq!(v["status"], "completed", "mode{mode}");
        assert!(
            result(&v)["evidence"].as_array().unwrap().is_empty(),
            "mode{mode}"
        );
    }
    let (r, f) = synthetic(&[
        (
            100.0,
            vec![
                (65, 10.0, 8.0),
                (66, 18.0, 8.0),
                (67, 60.0, 8.0),
                (68, 68.0, 8.0),
            ],
        ),
        (
            80.0,
            vec![
                (65, 10.0, 8.0),
                (66, 18.0, 8.0),
                (67, 60.0, 8.0),
                (68, 68.0, 8.0),
            ],
        ),
    ]);
    assert_eq!(result(&reply(&r, &f))["reason"], "metric-ambiguous-columns");
}
#[test]
fn overlap_and_role_scope_conflicts_are_indeterminate() {
    for y in [97.0, 91.9] {
        let (r, f) = synthetic(&[(100.0, vec![(65, 10.0, 8.0)]), (y, vec![(65, 10.0, 8.0)])]);
        assert!(result(&reply(&r, &f))["evidence"]
            .as_array()
            .unwrap()
            .is_empty());
    }
    let (mut r, f) = pair();
    r["payload"]["annotations"]
        .as_array_mut()
        .unwrap()
        .push(annotation(2, "headingMajor", Some((0, 2))));
    assert_eq!(
        result(&reply(&r, &f))["reason"],
        "metric-role-scope-overlap"
    );
}
#[test]
fn native_ranges_and_conditional_provenance_remain_exact() {
    let (mut r, f) = pair();
    r["payload"]["annotations"] = json!([annotation(1, "paragraph", Some((0, 2)))]);
    for provenance in [
        "authorLabel",
        "literalSourceAndNativeMap",
        "validatedNativeClassification",
    ] {
        r["payload"]["annotations"][0]["provenance"] = json!(provenance);
        r["payload"]["annotations"][0]["certainty"] = json!("tentative");
        let v = reply(&r, &f);
        let t = result(&v);
        assert_eq!(t["outcome"], "indeterminate");
        assert_eq!(t["evidence"].as_array().unwrap().len(), 2);
        for e in t["evidence"].as_array().unwrap() {
            assert_eq!(e["characterStart"], 0);
            assert_eq!(e["characterEnd"], 2);
            assert_eq!(e["roleProvenance"]["provenance"], provenance);
        }
    }
}
#[test]
fn bounded_character_and_annotation_quotas_never_truncate_success() {
    let (mut r, mut f) = synthetic(&[(
        100.0,
        (0..9).map(|i| (65, 10.0 + i as f64 * 8.0, 8.0)).collect(),
    )]);
    r["payload"]["annotations"] = json!((0..9)
        .map(|i| annotation(i + 1, "body", Some((i as usize, i as usize + 1))))
        .collect::<Vec<_>>());
    assert_eq!(result(&reply(&r, &f))["reason"], "metric-evidence-quota");
    r["payload"]["annotations"] = json!([annotation(1, "body", None)]);
    let template = f["pages"][0]["facts"]["characters"][0].clone();
    let mut chars = Vec::new();
    for index in 0..8193 {
        let mut c = template.clone();
        c["index"] = json!(index);
        chars.push(c);
    }
    f["pages"][0]["facts"]["characters"] = json!(chars);
    assert_eq!(
        result(&reply(&r, &f))["reason"],
        "metric-classification-quota"
    );
}
#[test]
fn old_v1_and_no_labels_keep_measurement_definition_unresolved() {
    let (mut r, mut f) = pair();
    r["payload"]["annotations"] = json!([]);
    assert_eq!(
        result(&reply(&r, &f))["reason"],
        "measurement-definition-unresolved"
    );
    r["payload"]["annotations"] = json!([annotation(1, "body", None)]);
    let page = &mut f["pages"][0];
    page["capabilities"]
        .as_object_mut()
        .unwrap()
        .remove("glyphInkMethod");
    page["capabilities"]["schemaVersion"] = json!(1);
    page["capabilities"]["glyphInk"] = json!("unsupported");
    page["capabilities"]["textStyle"] = json!("unsupported");
    page["facts"]["schemaVersion"] = json!(1);
    for c in page["facts"]["characters"].as_array_mut().unwrap() {
        c.as_object_mut().unwrap().remove("inkUncertainty");
        c["inkBox"] = Value::Null;
        c["inkStatus"] = json!("unsupported");
        c["styleStatus"] = json!("unsupported");
    }
    let v = reply(&r, &f);
    assert_eq!(v["status"], "completed");
    assert_eq!(result(&v)["reason"], "measurement-definition-unresolved");
    assert!(result(&v)["evidence"].as_array().unwrap().is_empty());
}
