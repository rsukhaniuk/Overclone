use super::*;

fn pair(role: &str, literal: &str, version: u8) -> (Value, Value) {
    let mut facts: Value =
        serde_json::from_str(include_str!("fixtures/native-v2-rect-batch.json")).unwrap();
    let template = facts["pages"][0]["facts"]["characters"][0].clone();
    let chars: Vec<_> = literal
        .chars()
        .enumerate()
        .map(|(i, c)| {
            let mut ch = template.clone();
            ch["index"] = json!(i);
            ch["unicode"] = json!(c as u32);
            let x = 10.0 + i as f64 * 8.0;
            ch["origin"] = json!([x, 20]);
            ch["matrix"] = json!([1, 0, 0, 1, x, 20]);
            ch["inkBox"] = json!([x, 20, x + 7.201171875, 29.6005859375]);
            if version == 1 {
                ch.as_object_mut().unwrap().remove("inkUncertainty");
                ch["inkStatus"] = json!("unsupported");
                ch["inkBox"] = Value::Null;
            }
            ch
        })
        .collect();
    facts["pages"][0]["facts"]["characters"] = json!(chars);
    if version == 1 {
        facts["pages"][0]["capabilities"]["schemaVersion"] = json!(1);
        facts["pages"][0]["capabilities"]["glyphInk"] = json!("unsupported");
        facts["pages"][0]["capabilities"]
            .as_object_mut()
            .unwrap()
            .remove("glyphInkMethod");
        facts["pages"][0]["facts"]["schemaVersion"] = json!(1);
    }
    let request = json!({"protocolVersion":1,"requestId":"b".repeat(32),"operation":"evaluateRenderedReview","payload":{
        "pdfSha256":facts["documentSha256"],"compileRequestId":"c".repeat(32),"compileEpoch":3,"inputDigest":"d".repeat(64),
        "captureId":"12345678-1234-1234-1234-123456789abc","profile":{"id":"knu-csc-2025-rendered-review","version":"1.0.0"},
        "workType":"masterQualification","annotationVersion":2,"pageCount":1,"requestedPages":[0],"annotations":[{
            "annotationId":1,"role":role,"pageIndex":0,"characterStart":0,"characterEnd":chars.len(),"sourceFileId":null,"sourceSpan":null,
            "provenance":"authorLabel","certainty":"confirmedObservation"}],"factsSha256":"e".repeat(64)}});
    (request, facts)
}
fn response(r: &Value, f: &Value) -> Value {
    serde_json::from_str(&evaluate(&r.to_string(), &f.to_string())).unwrap()
}
fn rule<'a>(v: &'a Value, id: &str) -> &'a Value {
    v["payload"]["results"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == id)
        .unwrap()
}
fn item<'a>(v: &'a Value, id: &str) -> &'a Value {
    &rule(v, id)["evidence"][0]
}

#[test]
fn extracted_unicode_case_prefix_and_fullstop_work_without_ink_or_visual_style() {
    for (role, literal, id, case, prefix, dot) in [
        (
            "headingMajor",
            "ВСТУП🙂",
            "KNU-HEADINGS-RENDERED-001",
            "upper",
            "none",
            false,
        ),
        (
            "headingMinor",
            "1.1 Їжа ґрунт",
            "KNU-HEADINGS-RENDERED-001",
            "mixed",
            "none",
            false,
        ),
        (
            "figureCaption",
            "Рисунок А.1 — Їжа.",
            "KNU-CAPTION-RENDERED-001",
            "mixed",
            "figure",
            true,
        ),
        (
            "tableCaption",
            "Таблиця 2.2 — Дані",
            "KNU-CAPTION-RENDERED-001",
            "mixed",
            "table",
            false,
        ),
    ] {
        let (r, f) = pair(role, literal, 1);
        let v = response(&r, &f);
        let e = item(&v, id);
        assert_eq!(rule(&v, id)["outcome"], "indeterminate");
        assert_eq!(e["measured"], literal);
        assert_eq!(e["observation"]["unicodeCase"], case);
        assert_eq!(e["observation"]["captionPrefix"], prefix);
        assert_eq!(e["observation"]["finalFullStop"], dot);
        assert!(e["observation"]["placement"].is_null());
        assert_eq!(e["observation"]["textOrder"], "native-index");
        assert_eq!(e["method"], "annotation-scoped-native-text");
        if let Ok(path) = std::env::var("RENDERED_ROLES_VECTOR_PATH") {
            std::fs::write(
                format!(
                    "{path}.{}.json",
                    if role.starts_with("heading") {
                        "heading"
                    } else {
                        "caption"
                    }
                ),
                serde_json::to_vec_pretty(&json!({"request":r,"facts":f,"response":v})).unwrap(),
            )
            .unwrap();
        }
    }
}
#[test]
fn number_literal_placement_is_actual_pdf_ordinal_and_uncertain_native_ink() {
    let (r, f) = pair("pageNumber", "12", 2);
    let v = response(&r, &f);
    let e = item(&v, "KNU-PAGINATION-001");
    let o = &e["observation"];
    assert_eq!(o["asciiDigitValue"], 12);
    assert_eq!(o["oneBasedPdfPage"], 1);
    assert_eq!(o["textOrder"], "validated-horizontal");
    assert_eq!(o["placement"]["lineCount"], 1);
    assert!(o["placement"]["absoluteErrorMm"].as_f64().unwrap() > 0.0);
    assert!(
        (o["placement"]["topClearanceMm"].as_f64().unwrap()
            - (144.0 - 29.6005859375) * 25.4 / 72.0)
            .abs()
            < 1e-12
    );
    assert_eq!(rule(&v, "KNU-PAGINATION-001")["outcome"], "indeterminate");
    // Pure-Rust vector with synthetic digits; genuine unchanged A-glyph source
    // facts are pinned separately by the existing metrics fixture manifest.
    if let Ok(path) = std::env::var("RENDERED_ROLES_VECTOR_PATH") {
        std::fs::write(
            path,
            serde_json::to_vec_pretty(&json!({"request":r,"facts":f,"response":v})).unwrap(),
        )
        .unwrap();
    }
}
#[test]
fn caption_order_stays_unknown_even_with_labelled_illustration_or_claimed_machine_provenance() {
    for provenance in [
        "authorLabel",
        "literalSourceAndNativeMap",
        "validatedNativeClassification",
    ] {
        let (mut r, f) = pair("figureCaption", "Рисунок 1 — Назва", 2);
        r["payload"]["annotations"][0]["provenance"] = json!(provenance);
        r["payload"]["annotations"][0]["certainty"] = json!("tentative");
        let v = response(&r, &f);
        let e = item(&v, "KNU-CAPTION-RENDERED-001");
        assert_eq!(
            rule(&v, "KNU-CAPTION-RENDERED-001")["outcome"],
            "indeterminate"
        );
        assert_eq!(
            e["observation"]["relativeOrder"],
            "illustration-geometry-unavailable"
        );
        assert!(e["sourceLocation"].is_null());
    }
}
#[test]
fn unknown_scalars_and_long_or_empty_ranges_never_silently_drop_characters() {
    for literal in ["\0", "\u{fffd}", "\u{0001}", " \n\t "] {
        let (r, f) = pair("headingMajor", literal, 1);
        let v = response(&r, &f);
        assert!(rule(&v, "KNU-HEADINGS-RENDERED-001")["evidence"]
            .as_array()
            .unwrap()
            .is_empty());
    }
    let (r, f) = pair("headingMajor", &"Ї".repeat(129), 1);
    assert_eq!(
        rule(&response(&r, &f), "KNU-HEADINGS-RENDERED-001")["reason"],
        "role-text-quota"
    );
}
#[test]
fn native_index_text_is_not_reordered_or_claimed_as_visual_case() {
    let (r, mut f) = pair("headingMajor", "Ab", 1);
    f["pages"][0]["facts"]["characters"][0]["origin"][0] = json!(90);
    let v = response(&r, &f);
    let e = item(&v, "KNU-HEADINGS-RENDERED-001");
    assert_eq!(e["measured"], "Ab");
    assert_eq!(e["observation"]["textOrder"], "native-index");
    assert!(e["uncertainty"]["limitations"]
        .as_array()
        .unwrap()
        .contains(&json!("extracted-text-not-visual-glyph-proof")));
}
#[test]
fn missing_ink_rotation_and_columns_do_not_produce_measured_alignment() {
    for mode in 0..3 {
        let (r, mut f) = pair("headingMajor", "ABCD", 2);
        match mode {
            0 => f["pages"][0]["facts"]["rotation"] = json!(90),
            1 => {
                f["pages"][0]["facts"]["characters"][0]["inkStatus"] = json!("indeterminate");
                f["pages"][0]["facts"]["characters"][0]["inkBox"] = Value::Null;
                f["pages"][0]["facts"]["characters"][0]["inkUncertainty"] = Value::Null;
            }
            _ => f["pages"][0]["facts"]["characters"][0]["origin"][0] = json!(60),
        }
        let v = response(&r, &f);
        let e = item(&v, "KNU-HEADINGS-RENDERED-001");
        assert_eq!(e["observation"]["textOrder"], "native-index");
        assert!(e["observation"]["placement"].is_null());
    }
}
#[test]
fn duplicate_page_numbers_and_stale_range_pdf_are_rejected_or_unknown() {
    let (mut r, f) = pair("pageNumber", "12", 1);
    r["payload"]["annotations"][0]["characterEnd"] = json!(1);
    let mut second = r["payload"]["annotations"][0].clone();
    second["annotationId"] = json!(2);
    second["characterStart"] = json!(1);
    second["characterEnd"] = json!(2);
    r["payload"]["annotations"]
        .as_array_mut()
        .unwrap()
        .push(second);
    let v = response(&r, &f);
    assert_eq!(
        rule(&v, "KNU-PAGINATION-001")["reason"],
        "role-ambiguous-duplicates"
    );
    r["payload"]["pdfSha256"] = json!("0".repeat(64));
    assert_eq!(response(&r, &f)["status"], "failed");
}
#[test]
fn genuine_unchanged_native_single_glyph_can_scope_literal_heading_without_normative_proof() {
    let (mut r, _) = pair("headingMajor", "A", 2);
    let f: Value =
        serde_json::from_str(include_str!("fixtures/native-v2-rect-batch.json")).unwrap();
    r["payload"]["factsSha256"] =
        json!("7e33123840008f9809cbd8a53d7cddee8851947275c0c58160fd8be195bc164b");
    let v = response(&r, &f);
    let e = item(&v, "KNU-HEADINGS-RENDERED-001");
    assert_eq!(e["measured"], "A");
    assert_eq!(e["observation"]["unicodeCase"], "upper");
    assert_eq!(e["observation"]["textOrder"], "validated-horizontal");
    assert_eq!(
        rule(&v, "KNU-HEADINGS-RENDERED-001")["outcome"],
        "indeterminate"
    );
    if let Ok(path) = std::env::var("RENDERED_ROLES_VECTOR_PATH") {
        std::fs::write(
            format!("{path}.native-heading.json"),
            serde_json::to_vec_pretty(&json!({"request":r,"facts":f,"response":v})).unwrap(),
        )
        .unwrap();
    }
}

#[test]
fn printed_macro_like_text_and_ambiguous_columns_cannot_become_page_or_alignment_proof() {
    let (r, f) = pair("pageNumber", "\\thepage", 1);
    let v = response(&r, &f);
    let e = item(&v, "KNU-PAGINATION-001");
    assert_eq!(e["measured"], "\\thepage");
    assert!(e["observation"]["asciiDigitValue"].is_null());
    assert_eq!(rule(&v, "KNU-PAGINATION-001")["outcome"], "indeterminate");

    let (r, mut f) = pair("headingMajor", "ABCDEFGH", 2);
    for (i, ch) in f["pages"][0]["facts"]["characters"]
        .as_array_mut()
        .unwrap()
        .iter_mut()
        .enumerate()
    {
        let x = [10.0, 18.0, 100.0, 108.0][i % 4];
        let y = if i < 4 { 40.0 } else { 20.0 };
        ch["origin"] = json!([x, y]);
        ch["matrix"] = json!([1, 0, 0, 1, x, y]);
        ch["inkBox"] = json!([x, y, x + 7.201171875, y + 9.6005859375]);
    }
    let v = response(&r, &f);
    let e = item(&v, "KNU-HEADINGS-RENDERED-001");
    assert_eq!(e["measured"], "ABCDEFGH");
    assert!(e["observation"]["placement"].is_null());
    assert_eq!(
        e["observation"]["placementReason"],
        "metric-ambiguous-columns"
    );
}
