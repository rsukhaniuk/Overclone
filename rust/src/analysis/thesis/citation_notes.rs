//! Synthetic source observations. Notes are owned text, never verified pages.
use super::*;

fn edition(files: &[(&str, &str)], version: &str) -> Value {
    let (r, b) = request(files, version);
    serde_json::from_str(&crate::analysis::analyze_source(&r.to_string(), &b)).unwrap()
}

fn notes(files: &[(&str, &str)]) -> Value {
    edition(files, CITATION_NOTES_VERSION)
}

fn bibliography() -> &'static str {
    "\\begin{thebibliography}{9}\\bibitem{а}Один\\bibitem{б}Два\\end{thebibliography}"
}

#[test]
fn literal_postnote_and_empty_note_keep_exact_keys_and_first_use() {
    for optional in ["", "[с. 2]", "[]"] {
        let text = doc(&format!(
            "\\cite{optional}{{ а,б }}\\cite[с. 3]{{а}}{}",
            bibliography()
        ));
        let v = notes(&[("main.tex", &text)]);
        assert_eq!(v["status"], "completed", "{optional}: {v}");
        assert_eq!(rule(&v, 1)["outcome"], "pass", "{optional}: {v}");
        assert_eq!(rule(&v, 2)["outcome"], "pass", "{optional}: {v}");
        let report = &v["payload"]["thesis"];
        assert_eq!(report["completeLiteralScan"], true, "{optional}: {v}");
        assert_eq!(report["firstLiteralCitationKeys"], json!(["а", "б"]));
        assert_eq!(report["catalogVersion"], CITATION_NOTES_CATALOG_VERSION);
        assert_eq!(report["renderedCompliance"], "notAssessed");
        assert_eq!(v["payload"]["profile"]["version"], CITATION_NOTES_VERSION);
        assert_eq!(v["payload"]["knu"]["fixDescriptors"], json!([]));
    }
}

#[test]
fn inter_argument_comments_whitespace_and_exact_unicode_bom_crlf_spans() {
    let text = format!(
        "\u{feff}{}",
        doc("😀 и\u{306}\n\\cite % command comment\n [с. 2–3 😀] % before key\n { ключ😀, a\u{301} }\\begin{thebibliography}{9}\\bibitem{ключ😀}Один\\bibitem{a\u{301}}Два\\end{thebibliography}")
            .replace('\n', "\r\n")
    );
    let v = notes(&[("main.tex", &text)]);
    assert_eq!(rule(&v, 1)["outcome"], "pass", "{v}");
    assert_eq!(rule(&v, 2)["outcome"], "pass", "{v}");
    let events = v["payload"]["thesis"]["events"].as_array().unwrap();
    let cites = events.iter().filter(|e| e["kind"] == "cite");
    assert_eq!(cites.clone().count(), 2);
    for event in cites {
        let a = event["span"]["startByte"].as_u64().unwrap() as usize;
        let b = event["span"]["endByte"].as_u64().unwrap() as usize;
        assert!(text.is_char_boundary(a) && text.is_char_boundary(b));
        assert_eq!(&text[a..b], event["text"].as_str().unwrap());
        assert_eq!(a, text.find(&format!(" {}", &text[a..b])).unwrap() + 1);
    }
    assert_eq!(
        v["payload"]["thesis"]["firstLiteralCitationKeys"],
        json!(["ключ😀", "a\u{301}"])
    );
}

#[test]
fn notes_preserve_duplicate_missing_uncited_and_first_use_counterexamples() {
    let text = doc(&format!("\\cite[с. 2]{{а,б}}{}", bibliography()));
    for (from, to, citation_outcome, order_outcome) in [
        ("{а,б}", "{а,в}", "fail", "fail"),
        ("bibitem{б}", "bibitem{а}", "fail", "fail"),
        ("{а,б}", "{б,а}", "pass", "fail"),
        ("{а,б}", "{а}", "pass", "fail"),
    ] {
        let changed = text.replacen(from, to, 1);
        let v = notes(&[("main.tex", &changed)]);
        assert_eq!(rule(&v, 1)["outcome"], citation_outcome, "{from}: {v}");
        assert_eq!(rule(&v, 2)["outcome"], order_outcome, "{from}: {v}");
    }
}

#[test]
fn external_bibliography_resolves_notes_but_never_claims_backend_order() {
    let text = doc("\\cite[с. 2]{ключ😀}\\bibliography{refs}");
    let v = notes(&[
        ("main.tex", &text),
        ("refs.bib", "@book{ключ😀,title={Книга}}"),
    ]);
    assert_eq!(rule(&v, 1)["outcome"], "pass", "{v}");
    assert_eq!(rule(&v, 2)["outcome"], "unavailable", "{v}");
    assert_eq!(
        rule(&v, 2)["reason"],
        "compiled-bibliography-order-unavailable"
    );
    let v = notes(&[
        ("main.tex", &text.replace("ключ😀", "missing")),
        ("refs.bib", "@book{ключ😀,title={Книга}}"),
    ]);
    assert_eq!(rule(&v, 1)["outcome"], "fail", "{v}");
}

#[test]
fn note_keys_follow_selected_root_include_order_and_exact_unicode_identity() {
    let text = doc(&format!(
        "\\input{{chapter}}\\cite[с. 3]{{б}}{}",
        bibliography()
    ));
    let v = notes(&[
        ("main.tex", &text),
        ("ignored.tex", "\\cite[\\dynamic]{missing}"),
        ("chapter.tex", "\\cite[с. 2]{а}"),
    ]);
    assert_eq!(rule(&v, 1)["outcome"], "pass", "{v}");
    assert_eq!(rule(&v, 2)["outcome"], "pass", "{v}");
    assert_eq!(
        v["payload"]["thesis"]["closureFileIds"],
        json!(["f0", "f2"])
    );
    let v = notes(&[(
        "main.tex",
        &doc("\\cite[с. 2]{á}\\begin{thebibliography}{9}\\bibitem{a\u{301}}Джерело\\end{thebibliography}"),
    )]);
    assert_eq!(rule(&v, 1)["outcome"], "fail", "{v}");
}

#[test]
fn dynamic_notes_remain_indeterminate_and_never_execute_owned_commands() {
    for note in [
        "\\section{Injected}",
        "с. 2][\\section{Injected}",
        "с. 2][с. 3][\\section{Injected}",
        "{с. 2}",
        "с. % hidden ]\n2",
        "$2$",
        "#2",
        "a&b",
        "a_b",
        "a^b",
        "с.~2",
        "[2",
        "\u{0001}",
    ] {
        let text = doc(&format!("\\cite[{note}]{{missing}}{}", bibliography()));
        let v = notes(&[("main.tex", &text)]);
        assert_eq!(v["status"], "completed", "{note}: {v}");
        assert_eq!(rule(&v, 1)["outcome"], "indeterminate", "{note}: {v}");
        assert_eq!(rule(&v, 1)["knownCounterexample"], true, "{note}: {v}");
        assert!(v["payload"]["thesis"]["limitations"]
            .as_array()
            .unwrap()
            .contains(&json!("dynamicCitation")));
        assert!(v["payload"]["thesis"]["events"]
            .as_array()
            .unwrap()
            .iter()
            .all(|event| event["kind"] != "body"));
    }
}

#[test]
fn malformed_extra_starred_and_dynamic_key_syntax_is_never_silently_accepted() {
    for citation in [
        "\\cite[с. 2{а}",
        "\\cite[с. 2]",
        "\\cite[one][two]{а}",
        "\\cite[с. 2]junk{а}",
        "\\cite[][с. 2]{а}",
        "\\cite[][]{а}",
        "\\cite[one][two][three]{а}",
        "\\cite*[с. 2]{а}",
        "\\cite[с. 2]{\\key}",
        "\\cite[с. 2]{а,}",
        "\\cite[с. 2]{{а}}",
        "\\parencite[с. 2]{а}",
    ] {
        let text = doc(&format!("{citation}{}", bibliography()));
        let v = notes(&[("main.tex", &text)]);
        assert_eq!(v["status"], "completed", "{citation}: {v}");
        assert_eq!(rule(&v, 1)["outcome"], "indeterminate", "{citation}: {v}");
        assert_eq!(v["payload"]["thesis"]["completeLiteralScan"], false);
    }
}

#[test]
fn note_bound_is_per_utf8_byte_and_overlong_notes_are_uncertain() {
    for (note, expected) in [
        ("x".repeat(MAX_CITATION_NOTE_BYTES), "pass"),
        ("x".repeat(MAX_CITATION_NOTE_BYTES + 1), "indeterminate"),
        ("я".repeat(MAX_CITATION_NOTE_BYTES / 2), "pass"),
        ("я".repeat(MAX_CITATION_NOTE_BYTES / 2 + 1), "indeterminate"),
    ] {
        let text = doc(&format!("\\cite[{note}]{{а,б}}{}", bibliography()));
        let v = notes(&[("main.tex", &text)]);
        assert_eq!(v["status"], "completed", "{v}");
        assert_eq!(rule(&v, 1)["outcome"], expected, "{v}");
        assert_eq!(
            v["payload"]["thesis"]["firstLiteralCitationKeys"],
            json!(["а", "б"])
        );
    }
}

#[test]
fn exhausted_optional_lookahead_is_explicit_failure() {
    let mut work = LookaheadBudget::new(0);
    assert_eq!(
        citation_notes(&[], "[с. 2]{key}".as_bytes(), 0, 0, &mut work),
        Err("QuotaExceeded")
    );
    let text = doc(&format!("\\cite[с. 2]{{а,б}}{}", bibliography()));
    let (mut r, b) = request(&[("main.tex", &text)], CITATION_NOTES_VERSION);
    r["payload"]["limits"]["maxTokensPerProject"] = json!(1);
    let v: Value =
        serde_json::from_str(&crate::analysis::analyze_source(&r.to_string(), &b)).unwrap();
    assert_eq!(v["status"], "failed", "{v}");
    assert_eq!(v["error"]["code"], "QuotaExceeded");
}

#[test]
fn legacy_1_5_keeps_note_uncertainty_profile_and_catalog_identity() {
    let text = doc(&format!("\\cite[с. 2]{{а,б}}{}", bibliography()));
    let v = edition(&[("main.tex", &text)], LEGACY_VERSION);
    assert_eq!(v["status"], "completed", "{v}");
    assert_eq!(v["payload"]["profile"]["version"], LEGACY_VERSION);
    assert_eq!(
        v["payload"]["knu"]["metadata"]["profile"]["version"],
        LEGACY_VERSION
    );
    assert_eq!(
        v["payload"]["thesis"]["catalogVersion"],
        LEGACY_CATALOG_VERSION
    );
    assert_eq!(
        v["payload"]["thesis"]["firstLiteralCitationKeys"],
        json!([])
    );
    assert!(v["payload"]["thesis"]["ruleResults"]
        .as_array()
        .unwrap()
        .iter()
        .all(|r| r["outcome"] == "indeterminate"));
}

#[test]
fn legacy_1_5_complete_public_outputs_match_retained_actual_wasm_goldens() {
    // Reuse the existing reviewed producer-output fixture; do not regenerate its
    // expected results from this implementation or relabel them as 1.6 evidence.
    let fixture: Value = serde_json::from_str(include_str!(
        "../../../../tests/Workspace.Tests/Fixtures/ThesisSource/actual-wasm-vectors.json"
    ))
    .unwrap();
    for vector in fixture["vectors"].as_array().unwrap() {
        let hex = vector["packedUtf8Hex"].as_str().unwrap();
        let packed = hex
            .as_bytes()
            .chunks_exact(2)
            .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap())
            .collect::<Vec<_>>();
        let response: Value = serde_json::from_str(&crate::analysis::analyze_source(
            &vector["request"].to_string(),
            &packed,
        ))
        .unwrap();
        assert_eq!(response, vector["response"], "{}", vector["id"]);
    }
}

#[test]
fn citation_note_catalog_is_versioned_cited_and_has_no_rendered_or_fix_claim() {
    let catalog: Value = serde_json::from_str(crate::thesis_catalog::JSON).unwrap();
    assert_eq!(catalog["catalogVersion"], CITATION_NOTES_CATALOG_VERSION);
    assert_eq!(catalog["profileVersion"], CITATION_NOTES_VERSION);
    let row = catalog["rules"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == IDS[1])
        .unwrap();
    assert_eq!(row["ruleVersion"], "1.0.0");
    assert_eq!(row["citation"]["section"], "2.8; 3.15");
    assert_eq!(row["citation"]["pdfPages"], json!([16, 17, 30]));
    assert_eq!(row["fixPolicy"], "none");
    assert_eq!(row["supportState"], "partial");
    assert!(row["supportedSyntax"]
        .as_str()
        .unwrap()
        .contains("512 UTF-8 bytes"));
    assert_eq!(catalog["globalCoverage"], "incomplete");
}
