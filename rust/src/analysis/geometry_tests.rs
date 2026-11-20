//! 1.1.0 public-entry tests. No PDF/effective-TeX compliance assertions.
use super::{
    analyze_source,
    dto::{AnalysisResult, Outcome},
    knu_dto::{Limitation, ObservationValue},
    profiles::{self, HEADING, INDENT, MARGINS},
};
use serde_json::{json, Value};

fn request(files: &[(&str, &[u8])]) -> (Value, Vec<u8>) {
    let mut packed = Vec::new();
    let mut descriptors = Vec::new();
    for (n, (path, bytes)) in files.iter().enumerate() {
        descriptors.push(json!({"fileId":format!("f{n}"),"relativePath":path,"mediaKind":"text",
            "hasBom":bytes.starts_with(b"\xef\xbb\xbf"),"sha256":"0".repeat(64),"byteOffset":packed.len(),"byteLength":bytes.len()}));
        packed.extend_from_slice(bytes);
    }
    (
        json!({"protocolVersion":1,"requestId":"geometry-request","operation":"analyzeSource","payload":{
        "snapshotId":"snapshot","projectId":"project","sessionId":"session","workingGeneration":7,
        "baseHeadRevisionId":"head","profile":{"id":profiles::KNU_ID,"version":profiles::LATEST_VERSION},
        "configuration":{"workType":"unspecified"},"limits":{},"files":descriptors}}),
        packed,
    )
}
fn wire(req: &Value, bytes: &[u8]) -> Value {
    serde_json::from_str(&analyze_source(&req.to_string(), bytes)).unwrap()
}
fn completed(req: &Value, bytes: &[u8]) -> AnalysisResult {
    let value = wire(req, bytes);
    assert_eq!(value["status"], "completed", "{value}");
    let result: AnalysisResult = serde_json::from_value(value["payload"].clone()).unwrap();
    assert_eq!(result.rule_results.len(), 4);
    assert_eq!(result.knu.as_ref().unwrap().metadata.rules.len(), 3);
    assert_eq!(result.unavailable_capabilities, ["pdfFacts", "pdfChecks"]);
    for r in &result.rule_results {
        for f in &r.per_file {
            for id in &f.diagnostic_ids {
                assert!(result
                    .diagnostics
                    .iter()
                    .any(|d| d.id == *id && d.rule_id == r.rule_id && d.file_id == f.file_id));
            }
        }
    }
    result
}
fn one(s: &str) -> AnalysisResult {
    let (req, bytes) = request(&[("main.tex", s.as_bytes())]);
    completed(&req, &bytes)
}
fn outcome(r: &AnalysisResult, id: &str) -> Outcome {
    r.rule_results
        .iter()
        .find(|r| r.rule_id == id)
        .unwrap()
        .outcome
}
const GOOD: &str = r"\geometry{left=25mm,right=10mm,top=20mm,bottom=20mm}";

#[test]
fn margins_exact_boundaries_equivalent_units_and_no_upper_bound() {
    for s in [
        GOOD,
        r"\geometry{ left = 2.500 cm , right=1cm, top=2cm, bottom=0020.000 mm }",
        r"\geometry{left=999999999cm,right=999999999cm,top=999999.999mm,bottom=21mm}",
    ] {
        let r = one(s);
        assert_eq!(outcome(&r, MARGINS), Outcome::Pass, "{s}");
        assert_eq!(r.knu.as_ref().unwrap().files[0].observations.len(), 4);
        assert!(r.diagnostics.is_empty());
        assert!(r.knu.as_ref().unwrap().fix_descriptors.is_empty());
    }
    for (side, below, equal) in [
        ("left", "24.999", "25"),
        ("right", "9.999", "10"),
        ("top", "19.999", "20"),
        ("bottom", "0", "20"),
    ] {
        let src = GOOD.replace(&format!("{side}={equal}mm"), &format!("{side}={below}mm"));
        let r = one(&src);
        assert_eq!(outcome(&r, MARGINS), Outcome::Fail, "{src}");
        let d = r.diagnostics.iter().find(|d| d.rule_id == MARGINS).unwrap();
        let span = d.span.as_ref().unwrap();
        assert_eq!(
            &src.as_bytes()[span.start_byte as usize..span.end_byte as usize],
            format!("{below}mm").as_bytes()
        );
        assert_eq!(r.diagnostics.len(), 1);
        assert!(r.knu.as_ref().unwrap().fix_descriptors.is_empty());
    }
}

#[test]
fn missing_mixed_unknown_duplicate_and_multiple_are_indeterminate() {
    for src in [
        "",
        r"\geometry{}",
        r"\geometry{left=25mm}",
        r"\geometry{left=1mm,top=20mm,right=10mm,bottom=20mm,margin=1cm}",
        r"\geometry{left=25mm,left=25mm,top=20mm,right=10mm,bottom=20mm}",
        r"\geometry{left=25mm,left=1mm,top=20mm,right=10mm,bottom=20mm}",
        &format!("{GOOD}{GOOD}"),
    ] {
        let r = one(src);
        assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate, "{src}");
        assert!(r.knu.as_ref().unwrap().fix_descriptors.is_empty());
        if src.contains("left=1mm") {
            assert!(
                r.diagnostics.iter().any(|d| d.rule_id == MARGINS),
                "known violations remain visible"
            );
        }
    }
    let (req, bytes) = request(&[
        ("main.tex", GOOD.as_bytes()),
        ("chapter.tex", GOOD.as_bytes()),
    ]);
    let r = completed(&req, &bytes);
    assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate);
    for f in &r.knu.unwrap().files {
        assert!(f
            .limitations
            .contains(&Limitation::MultipleGeometryDeclarations));
    }
}

#[test]
fn literal_unsupported_units_and_dynamic_owned_arguments_do_not_invent_intent() {
    for value in [
        "-1mm",
        "+1mm",
        "1pt",
        "1em",
        "1in",
        "1MM",
        "1e2mm",
        ".2cm",
        "1.mm",
        "1.0000mm",
        "1234567890mm",
        "",
        "20mm=20mm",
        "20\u{a0}mm",
    ] {
        let src = GOOD.replace("top=20mm", &format!("top={value}"));
        let r = one(&src);
        assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate, "{src}");
        assert!(matches!(
            r.knu.as_ref().unwrap().files[0].observations[2].value,
            ObservationValue::MarginDeclaration { length: None, .. }
        ));
    }
    for value in [
        r"\x",
        r"\dimexpr20mm\relax",
        "{20mm}",
        "20%comment\nmm",
        r"\x{a,right=1mm}",
        "20~mm",
        "20#1mm",
    ] {
        let src = GOOD.replace("top=20mm", &format!("top={value}"));
        let r = one(&src);
        assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate, "{src}");
        assert!(
            r.knu.as_ref().unwrap().files[0].observations.is_empty(),
            "no nested options become live: {src}"
        );
    }
}

#[test]
fn context_comments_verbatim_scopes_conditionals_macros_includes_and_ownership() {
    for src in [
        format!("%{GOOD}\n"),
        format!("\\verb|{GOOD}|"),
        format!("\\begin{{verbatim}}{GOOD}\\end{{verbatim}}"),
        format!("{{{GOOD}}}"),
        format!("\\newcommand{{\\x}}{{{GOOD}}}"),
        format!("\\iftrue{GOOD}\\fi"),
        format!("\\begingroup{GOOD}\\endgroup"),
        format!("{GOOD}\\input{{other}}"),
        format!("\\begin{{custom}}{GOOD}\\end{{custom}}"),
        format!("\\section{{{GOOD}}}"),
        GOOD.replacen("geometry", "geometry*", 1),
    ] {
        let r = one(&src);
        assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate, "{src}");
        assert!(r.knu.as_ref().unwrap().fix_descriptors.is_empty());
    }
    // Trivia between command and group is lexer-owned and explicitly supported.
    let r = one(&GOOD.replace("geometry{", "geometry %comment\r\n {"));
    assert_eq!(outcome(&r, MARGINS), Outcome::Pass);
}

#[test]
fn margin_local_limits_do_not_rewrite_old_rules_or_indent_fix() {
    let src = r"\setlength{\parindent}{1cm}\section{Good}\geometry{left=1mm,unknown=20mm}";
    let r = one(src);
    assert_eq!(outcome(&r, INDENT), Outcome::Fail);
    assert_eq!(outcome(&r, HEADING), Outcome::Pass);
    assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate);
    let fixes = &r.knu.as_ref().unwrap().fix_descriptors;
    assert_eq!(fixes.len(), 1);
    assert_eq!(fixes[0].rule_id, INDENT);
    assert_eq!(fixes[0].rule_version, profiles::VERSION);
    assert_eq!(fixes[0].profile.version, profiles::LATEST_VERSION);
    let changed = src.replacen("1cm", &fixes[0].replacement, 1);
    assert!(one(&changed).knu.unwrap().fix_descriptors.is_empty());
    let r = one(&format!(r"\setlength{{\parindent}}{{1pt}}{GOOD}"));
    assert_eq!(outcome(&r, MARGINS), Outcome::Pass);
}

#[test]
fn utf8_bom_crlf_scalar_spans_and_deterministic_output() {
    let src = format!(
        "\u{feff}Україна🙂\r\n\\geometry{{left= 24.9 mm ,right=1cm,top=2cm,bottom=2cm}}\r\n"
    );
    let (req, bytes) = request(&[("main.tex", src.as_bytes())]);
    let a = analyze_source(&req.to_string(), &bytes);
    assert_eq!(a, analyze_source(&req.to_string(), &bytes));
    let r = completed(&req, &bytes);
    for o in &r.knu.unwrap().files[0].observations {
        for span in [&o.command_span, &o.declaration_span, &o.value_span] {
            assert!(src.is_char_boundary(span.start_byte as usize));
            assert!(src.is_char_boundary(span.end_byte as usize));
        }
        assert_eq!(
            &src[o.value_span.start_byte as usize..o.value_span.end_byte as usize],
            o.excerpt
        );
    }
    let (req, bytes) = request(&[("main.tex", b"\xff")]);
    assert_eq!(wire(&req, &bytes)["error"]["code"], "InvalidInput");
    let (mut req, bytes) = request(&[("main.tex", GOOD.as_bytes())]);
    req["payload"]["profile"]["version"] = json!("1.9.0");
    assert_eq!(wire(&req, &bytes)["error"]["code"], "UnsupportedCapability");
}

#[test]
fn budgets_bound_pathological_geometry_and_result_growth() {
    for src in [
        format!(
            "\\geometry{{left={},right=10mm,top=20mm,bottom=20mm}}",
            "0".repeat(5000)
        ),
        format!("\\geometry{{{}}}", "left=1mm,".repeat(2000)),
        GOOD.repeat(400),
    ] {
        let (mut req, bytes) = request(&[("main.tex", src.as_bytes())]);
        req["payload"]["limits"] =
            json!({"maxTokensPerFile":80,"maxTokensPerProject":90,"maxDiagnosticsPerProject":2});
        let r = completed(&req, &bytes);
        assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate);
        let f = &r.knu.as_ref().unwrap().files[0];
        assert!(r.facts.files[0].token_count + f.observation_count <= 80);
        assert!(r.diagnostics.len() <= 2);
        assert!(f.observations.iter().all(|o| o.excerpt.len() <= 256));
    }
    let (mut req, bytes) = request(&[("main.tex", GOOD.as_bytes())]);
    req["payload"]["limits"] = json!({"maxResultBytes":100});
    assert_eq!(wire(&req, &bytes)["error"]["code"], "QuotaExceeded");
    req["payload"]["limits"] = json!({"maxDiagnosticsPerProject":0});
    let bad = GOOD.replace("left=25mm", "left=1mm");
    let (mut req, bytes) = request(&[("main.tex", bad.as_bytes())]);
    req["payload"]["limits"] = json!({"maxDiagnosticsPerProject":0});
    let r = completed(&req, &bytes);
    assert!(r.coverage.diagnostics_truncated);
    assert_eq!(outcome(&r, MARGINS), Outcome::Indeterminate);
    let facts = &one(GOOD).facts.files[0];
    let mut remaining = 100;
    let f = super::observations::observe_version(
        facts,
        GOOD.as_bytes(),
        true,
        super::lexer::LookaheadBudget::new(0),
        100,
        &mut remaining,
        true,
    );
    assert!(!f.complete);
    assert!(f.limitations.contains(&Limitation::LookaheadBudget));
}

#[test]
fn legacy_metadata_and_actual_fixtures_are_byte_semantically_unchanged() {
    let expected: Value = serde_json::from_str(include_str!(
        "../../../src/Client/Analysis/knu-profile-metadata.json"
    ))
    .unwrap();
    assert_eq!(
        serde_json::to_value(profiles::metadata()).unwrap(),
        expected
    );
    for raw in [
        include_str!("../../../tests/Workspace.Tests/Fixtures/Knu/positive.json"),
        include_str!("../../../tests/Workspace.Tests/Fixtures/Knu/negative.json"),
        include_str!("../../../tests/Workspace.Tests/Fixtures/Knu/review-after.json"),
        include_str!("../../../tests/Workspace.Tests/Fixtures/Knu/uncertain.json"),
        include_str!("../../../tests/Workspace.Tests/Fixtures/Knu/argument-bounds.json"),
    ] {
        let fixture: Value = serde_json::from_str(raw).unwrap();
        let bytes = unhex(fixture["sourceHex"].as_str().unwrap());
        assert_eq!(wire(&fixture["request"], &bytes), fixture["response"]);
    }
}
#[test]
fn margin_observations_share_leftovers_and_latest_keeps_engine_facts() {
    // Many options share a single Text token. This exercises observation
    // exhaustion independently of lexer exhaustion, including project leftovers.
    let source = format!("\\geometry{{{}}}", "left=1mm,".repeat(40));
    let (mut req, bytes) = request(&[
        ("main.tex", source.as_bytes()),
        ("other.tex", GOOD.as_bytes()),
    ]);
    req["payload"]["limits"] = json!({"maxTokensPerFile":12,"maxTokensPerProject":18});
    let r = completed(&req, &bytes);
    let k = r.knu.as_ref().unwrap();
    assert!(k
        .files
        .iter()
        .any(|f| f.limitations.contains(&Limitation::ObservationBudget)));
    let emitted: u32 = r.facts.files.iter().map(|f| f.token_count).sum::<u32>()
        + k.files.iter().map(|f| f.observation_count).sum::<u32>();
    assert!(emitted <= 18);
    for (f, observations) in r.facts.files.iter().zip(&k.files) {
        assert!(f.token_count + observations.observation_count <= 12);
    }
    let mut engine_req = req.clone();
    engine_req["payload"]["profile"] = json!({"id":"engine-structural","version":"0"});
    engine_req["payload"]
        .as_object_mut()
        .unwrap()
        .remove("configuration");
    let engine = wire(&engine_req, &bytes);
    assert_eq!(
        serde_json::to_value(&r.facts).unwrap(),
        engine["payload"]["facts"]
    );
    assert_eq!(
        serde_json::to_value(&r.rule_results[0]).unwrap(),
        engine["payload"]["ruleResults"][0]
    );
    assert_eq!(
        serde_json::to_value(k.engine_aggregate_outcome).unwrap(),
        engine["payload"]["aggregateOutcome"]
    );
}

fn unhex(hex: &str) -> Vec<u8> {
    hex.as_bytes()
        .chunks_exact(2)
        .map(|p| u8::from_str_radix(std::str::from_utf8(p).unwrap(), 16).unwrap())
        .collect()
}

/// Optional private generation receipt. Inputs contain independently computed
/// hashes; outputs come from the real public entry, not a modeled response.
#[test]
fn export_private_public_entry_fixtures_when_requested() {
    let Ok(dir) = std::env::var("GEOMETRY_FIXTURE_DIR") else {
        return;
    };
    let dir = std::path::Path::new(&dir);
    for entry in std::fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_some_and(|e| e == "seed") {
            let mut fixture: Value =
                serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
            let bytes = unhex(fixture["sourceHex"].as_str().unwrap());
            fixture["response"] = wire(&fixture["request"], &bytes);
            assert_eq!(fixture["response"]["status"], "completed");
            std::fs::write(
                path.with_extension("json"),
                serde_json::to_vec_pretty(&fixture).unwrap(),
            )
            .unwrap();
        }
    }
    std::fs::write(
        dir.join("knu-profile-v1_1-metadata.json"),
        serde_json::to_vec_pretty(&profiles::metadata_for(profiles::LATEST_VERSION)).unwrap(),
    )
    .unwrap();
}
