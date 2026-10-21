//! SYNTHETIC source-intent fixtures through the REAL public entry. No compiler,
//! expansion, counter/PDF mapping, browser, native, provider or filesystem proof.
use serde_json::{json, Value};

use super::analyze_source;
use super::references::{LABEL, REFERENCE};

fn article(body: &str) -> String {
    format!("\\documentclass{{article}}\n\\begin{{document}}\n{body}\n\\end{{document}}\n")
}
fn request(files: &[(&str, &str, Vec<u8>)], root: usize) -> (Value, Vec<u8>) {
    let mut packed = Vec::new();
    let descriptors: Vec<_> = files.iter().enumerate().map(|(i, (path, kind, bytes))| {
        let offset = packed.len(); packed.extend(bytes);
        json!({"fileId":format!("file-{i}"),"relativePath":path,"mediaKind":kind,
            "hasBom":bytes.starts_with(&[239,187,191]),"sha256":"","byteOffset":offset,"byteLength":bytes.len()})
    }).collect();
    (
        json!({"protocolVersion":1,"requestId":"source-reference-fixture","operation":"analyzeSource",
        "payload":{"snapshotId":"snapshot-ref","projectId":"project-ref","sessionId":"session-ref","workingGeneration":7,
            "baseHeadRevisionId":"head-ref","profile":{"id":"engine-source-references","version":"1.0.0"},
            "referenceConfiguration":{"rootFileId":format!("file-{root}")},"limits":{},"files":descriptors}}),
        packed,
    )
}
fn wire(req: &Value, bytes: &[u8]) -> Value {
    serde_json::from_str(&analyze_source(&req.to_string(), bytes)).unwrap()
}
fn completed(req: &Value, bytes: &[u8]) -> Value {
    let r = wire(req, bytes);
    assert_eq!(r["status"], "completed", "{r}");
    assert_eq!(r["requestId"], req["requestId"]);
    r["payload"].clone()
}
fn root(body: &str) -> (Value, Vec<u8>) {
    request(&[("main.tex", "text", article(body).into_bytes())], 0)
}
fn rule<'a>(p: &'a Value, id: &str) -> &'a Value {
    p["ruleResults"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == id)
        .unwrap()
}
fn new_diags(p: &Value) -> Vec<&Value> {
    p["diagnostics"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|d| d["ruleId"] == LABEL || d["ruleId"] == REFERENCE)
        .collect()
}
fn invariant(p: &Value, req: &Value) {
    assert_eq!(
        p["referenceConfiguration"],
        req["payload"]["referenceConfiguration"]
    );
    assert!(p.get("configuration").is_none());
    assert!(p.get("knu").is_none());
    assert!(p.get("referenceReport").is_none());
    assert_eq!(p["ruleResults"].as_array().unwrap().len(), 3);
    assert_eq!(p["ruleResults"][0]["ruleId"], "ENGINE-STRUCT-001");
    for id in [LABEL, REFERENCE] {
        let r = rule(p, id);
        assert_eq!(r["origin"], "engine");
        assert!(r.get("source").is_none());
        assert!(r.get("evidenceIds").is_none());
        let limits = r["limitations"].as_array().unwrap();
        assert!(limits.len() <= 10);
        assert!(limits.contains(&json!("sourceIntentOnly")));
        assert!(limits.contains(&json!("macroExpansionUnsupported")));
        let mut unique = std::collections::HashSet::new();
        assert!(limits.iter().all(|l| unique.insert(l.as_str().unwrap())));
        for f in r["perFile"].as_array().unwrap() {
            for did in f["diagnosticIds"].as_array().unwrap() {
                assert_eq!(
                    p["diagnostics"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .filter(|d| d["id"] == *did
                            && d["fileId"] == f["fileId"]
                            && d["ruleId"] == id)
                        .count(),
                    1
                );
            }
        }
    }
    for d in new_diags(p) {
        assert_eq!(d["origin"], "engine");
        assert_eq!(d["severity"], "warning");
        assert_eq!(
            d["message"],
            if d["ruleId"] == LABEL {
                "Repeated literal label key in the selected source closure."
            } else {
                "No matching literal label was found in the selected source closure."
            }
        );
        assert!(d["id"].as_str().unwrap().len() <= super::MAX_ID_BYTES + 42);
    }
}

#[test]
fn full_closure_precedes_forward_cross_file_matching_and_ignores_unreachable() {
    let files = [
        (
            "unreachable.tex",
            "text",
            b"\\label{x}\\label{x}\\ref{never}".to_vec(),
        ),
        (
            "main.tex",
            "text",
            article("\\ref{x}\\input{parts/one}\\pageref{y}").into_bytes(),
        ),
        ("parts/one.tex", "text", b"\\label{x}\\input{two}".to_vec()),
        ("two.tex", "text", b"\\label{y}".to_vec()),
    ];
    let (req, bytes) = request(&files, 1);
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    assert_eq!(rule(&p, LABEL)["outcome"], "pass");
    assert_eq!(rule(&p, REFERENCE)["outcome"], "pass");
    assert!(new_diags(&p).is_empty());
    for id in [LABEL, REFERENCE] {
        let ids: Vec<_> = rule(&p, id)["perFile"]
            .as_array()
            .unwrap()
            .iter()
            .map(|f| f["fileId"].as_str().unwrap())
            .collect();
        assert_eq!(ids, ["file-1", "file-2", "file-3"]);
    }
}

#[test]
fn uppercase_tex_extensions_keep_exact_path_lookup_and_lowercase_append() {
    let files = [
        (
            "MAIN.TEX",
            "text",
            article("\\ref{x}\\input{CHAPTER.TEX}").into_bytes(),
        ),
        ("CHAPTER.TEX", "text", b"\\label{x}".to_vec()),
    ];
    let (req, bytes) = request(&files, 0);
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    assert_eq!(rule(&p, LABEL)["outcome"], "pass");
    assert_eq!(rule(&p, REFERENCE)["outcome"], "pass");
    assert!(new_diags(&p).is_empty());
    for id in [LABEL, REFERENCE] {
        assert_eq!(rule(&p, id)["perFile"].as_array().unwrap().len(), 2);
    }

    // Extension admission is insensitive; actual captured path lookup is NOT.
    let mut differing_case = req.clone();
    differing_case["payload"]["files"][1]["relativePath"] = json!("chapter.TEX");
    let p = completed(&differing_case, &bytes);
    invariant(&p, &differing_case);
    assert_eq!(rule(&p, REFERENCE)["outcome"], "indeterminate");
    assert!(rule(&p, REFERENCE)["limitations"]
        .as_array()
        .unwrap()
        .contains(&json!("missingInclude")));
    assert!(new_diags(&p).is_empty());

    // Extensionless input still appends EXACTLY .tex, not .TEX or a repair.
    let (req, bytes) = request(
        &[
            (
                "MAIN.TEX",
                "text",
                article("\\input{CHAPTER}\\ref{x}").into_bytes(),
            ),
            ("CHAPTER.TEX", "text", b"\\label{wrong}".to_vec()),
            ("CHAPTER.tex", "text", b"\\label{x}".to_vec()),
        ],
        0,
    );
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    assert_eq!(rule(&p, REFERENCE)["outcome"], "pass");
    assert!(new_diags(&p).is_empty());
    for id in [LABEL, REFERENCE] {
        let ids: Vec<_> = rule(&p, id)["perFile"]
            .as_array()
            .unwrap()
            .iter()
            .map(|f| f["fileId"].as_str().unwrap())
            .collect();
        assert_eq!(ids, ["file-0", "file-2"]);
    }
}

#[test]
fn duplicate_includes_first_occurrence_and_missing_span_is_argument_interior() {
    let source = article("\\label{same}\\input{chapter}\\ref{missing}");
    let (req, bytes) = request(
        &[
            ("main.tex", "text", source.clone().into_bytes()),
            ("chapter.tex", "text", b"\\label{same}".to_vec()),
        ],
        0,
    );
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    let ds = new_diags(&p);
    assert_eq!(ds.iter().filter(|d| d["ruleId"] == LABEL).count(), 2);
    assert_eq!(ds.iter().filter(|d| d["ruleId"] == REFERENCE).count(), 1);
    assert_eq!(rule(&p, LABEL)["outcome"], "fail");
    assert_eq!(rule(&p, REFERENCE)["outcome"], "fail");
    let missing = ds.iter().find(|d| d["ruleId"] == REFERENCE).unwrap();
    let at = source.find("missing").unwrap();
    assert_eq!(missing["span"], json!({"startByte":at,"endByte":at+7}));
    assert!(!missing["message"].as_str().unwrap().contains("missing"));
}

#[test]
fn exact_unicode_case_and_normalization_are_not_repaired() {
    let source = format!(
        "\u{feff}{}",
        article(
            "% Україна 😀\r\n\\label{Київ🙂}\\ref{Київ🙂}\\ref{київ🙂}\\label{é}\\ref{e\u{301}}"
        )
        .replace('\n', "\r\n")
    );
    let (req, bytes) = request(&[("main.tex", "text", source.clone().into_bytes())], 0);
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    let ds: Vec<_> = new_diags(&p)
        .into_iter()
        .filter(|d| d["ruleId"] == REFERENCE)
        .collect();
    assert_eq!(ds.len(), 2);
    for (d, key) in ds.iter().zip(["київ🙂", "e\u{301}"]) {
        let at = source.find(key).unwrap();
        assert_eq!(d["span"], json!({"startByte":at,"endByte":at+key.len()}));
        let (line, column) = super::mapping::line_column(&bytes, at);
        assert_eq!(d["primaryLocation"], json!({"line":line,"column":column}));
    }
}

#[test]
fn project_root_cwd_and_dot_segments_not_including_directory() {
    let (req, bytes) = request(
        &[
            (
                "dir/main.tex",
                "text",
                article("\\input{./parts/../child}\\ref{root}").into_bytes(),
            ),
            ("child.tex", "text", b"\\label{root}".to_vec()),
            ("dir/child.tex", "text", b"\\label{wrong}".to_vec()),
        ],
        0,
    );
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    assert_eq!(rule(&p, REFERENCE)["outcome"], "pass");
    assert_eq!(rule(&p, LABEL)["perFile"].as_array().unwrap().len(), 2);
}

#[test]
fn include_exact_policy_and_all_uncertainty_suppresses_missing() {
    for (command, limitation) in [
        ("\\input{absent}", "missingInclude"),
        ("\\input{../escape}", "unsupportedIncludeSyntax"),
        ("\\input{/absolute}", "unsupportedIncludeSyntax"),
        ("\\input{C:/drive}", "unsupportedIncludeSyntax"),
        ("\\input{https://host/a}", "unsupportedIncludeSyntax"),
        ("\\input{a\\b}", "unsupportedIncludeSyntax"),
        ("\\include{chapter.tex}", "unsupportedIncludeSyntax"),
        ("\\input{\\macro}", "unsupportedIncludeSyntax"),
        ("\\input chapter", "unsupportedIncludeSyntax"),
        ("\\includeonly{chapter}", "unsupportedIncludeSyntax"),
        ("\\import{dir}{chapter}", "unsupportedIncludeSyntax"),
        ("\\input{asset.bin}", "missingInclude"),
    ] {
        let (req, bytes) = request(
            &[
                (
                    "main.tex",
                    "text",
                    article(&format!("{command}\\ref{{unknown}}")).into_bytes(),
                ),
                ("asset.bin", "binary", vec![255, 0]),
            ],
            0,
        );
        let p = completed(&req, &bytes);
        invariant(&p, &req);
        assert_eq!(
            rule(&p, REFERENCE)["outcome"],
            "indeterminate",
            "{command}: {p}"
        );
        assert!(
            rule(&p, REFERENCE)["limitations"]
                .as_array()
                .unwrap()
                .contains(&json!(limitation)),
            "{command}: {p}"
        );
        assert!(!new_diags(&p).iter().any(|d| d["ruleId"] == REFERENCE));
    }
    let (req, bytes) = request(
        &[
            (
                "main.tex",
                "text",
                article("\\include{chapter}\\ref{x}").into_bytes(),
            ),
            ("chapter.tex", "text", b"\\label{x}".to_vec()),
        ],
        0,
    );
    assert_eq!(rule(&completed(&req, &bytes), REFERENCE)["outcome"], "pass");
}

#[test]
fn cycle_and_repeat_are_iterative_uncertain_without_duplicate_execution_claim() {
    for (body, child, code) in [
        ("\\input{a}", "\\input{main}", "includeCycle"),
        ("\\input{a}\\input{a}", "\\label{x}", "repeatedInclude"),
    ] {
        let (req, bytes) = request(
            &[
                (
                    "main.tex",
                    "text",
                    article(&format!("{body}\\ref{{unknown}}")).into_bytes(),
                ),
                ("a.tex", "text", child.as_bytes().to_vec()),
            ],
            0,
        );
        let p = completed(&req, &bytes);
        invariant(&p, &req);
        assert_eq!(rule(&p, REFERENCE)["outcome"], "indeterminate");
        assert!(rule(&p, REFERENCE)["limitations"]
            .as_array()
            .unwrap()
            .contains(&json!(code)));
        assert!(new_diags(&p).is_empty());
    }
}

#[test]
fn owned_context_not_every_label_fact_is_a_declaration() {
    for body in [
        "\\newcommand{\\foo}{\\label{x}}\\ref{x}",
        "\\def\\foo{\\label{x}}\\ref{x}",
        "\\iffalse\\label{x}\\fi\\ref{x}",
        "\\begin{unknown}\\label{x}\\end{unknown}\\ref{x}",
        "\\section{moving \\label{x}}\\ref{x}",
        "{$\\label{x}$}\\ref{x}",
        "\\usepackage{unknown}\\label{x}\\ref{x}",
        "\\unknown\\label{x}\\label{x}\\ref{y}",
    ] {
        let (req, bytes) = root(body);
        let p = completed(&req, &bytes);
        invariant(&p, &req);
        assert_eq!(rule(&p, LABEL)["outcome"], "indeterminate", "{body}: {p}");
        assert!(new_diags(&p).is_empty(), "{body}: {p}");
    }
}

#[test]
fn comments_and_verbatim_do_not_declare_or_reference_keys() {
    let (req,bytes)=root("% \\label{ghost}\\ref{bad}\n\\begin{verbatim}\n\\label{ghost}\\ref{bad}\n\\end{verbatim}\n\\verb|\\label{ghost}|\\ref{ghost}");
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    let ds = new_diags(&p);
    assert_eq!(ds.len(), 1);
    assert_eq!(ds[0]["ruleId"], REFERENCE);
}

#[test]
fn nonliteral_keys_are_not_trimmed_or_expanded_and_known_duplicates_can_survive_uncertainty() {
    for key in [
        " x",
        "x ",
        "x y",
        "\\macro",
        "x{y}",
        "x%comment\ny",
        "x\t",
        "",
    ] {
        let (req, bytes) = root(&format!("\\label{{{key}}}\\ref{{unresolved}}"));
        let p = completed(&req, &bytes);
        invariant(&p, &req);
        assert_eq!(
            rule(&p, REFERENCE)["outcome"],
            "indeterminate",
            "{key:?}: {p}"
        );
        assert!(new_diags(&p).is_empty());
    }
    let (req, bytes) = root("\\label{x}\\label{x}\\unknown\\ref{missing}");
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    assert_eq!(new_diags(&p).len(), 2);
    assert!(new_diags(&p).iter().all(|d| d["ruleId"] == LABEL));
    assert_eq!(rule(&p, LABEL)["outcome"], "indeterminate");
}

#[test]
fn zero_token_graph_diagnostic_result_and_incomplete_limits_never_claim_clean_missing() {
    for limits in [
        json!({"maxTokensPerProject":0}),
        json!({"maxTokensPerFile":1}),
        json!({"maxBraceDepth":0}),
        json!({"maxDiagnosticsPerProject":0}),
        json!({"maxDiagnosticsPerProject":1}),
    ] {
        let (mut req, bytes) = root("\\ref{a}\\ref{b}\\label{x}\\label{x}");
        req["payload"]["limits"] = limits;
        let p = completed(&req, &bytes);
        invariant(&p, &req);
        assert_eq!(rule(&p, REFERENCE)["outcome"], "indeterminate", "{p}");
        assert!(!new_diags(&p).iter().any(|d| d["ruleId"] == REFERENCE));
    }
    let (mut req, bytes) = root("\\ref{x}");
    req["payload"]["limits"] = json!({"maxResultBytes":1});
    let r = wire(&req, &bytes);
    assert_eq!(r["status"], "failed");
    assert_eq!(r["error"]["code"], "QuotaExceeded");
    assert_eq!(r["requestId"], req["requestId"]);
    let (mut req, bytes) = root("\\ref{x}");
    req["payload"]["limits"] = json!({"maxTotalBytes":0});
    assert_eq!(wire(&req, &bytes)["error"]["code"], "QuotaExceeded");
    let (req, bytes) = request(
        &[("main.tex", "text", article("\\ref{x}{").into_bytes())],
        0,
    );
    let p = completed(&req, &bytes);
    assert_eq!(rule(&p, REFERENCE)["outcome"], "indeterminate");
}

#[test]
fn invalid_root_config_paths_and_unknown_keys_are_correlated_before_analysis() {
    for variant in [
        "absent",
        "null",
        "empty",
        "unknown",
        "binary",
        "nontex",
        "path",
        "duplicatepath",
        "unknownconfig",
        "unknownpayload",
        "unknownfile",
        "wrongprofile",
        "knu",
    ] {
        let (mut req, mut bytes) = root("\\label{x}");
        match variant {
            "absent" => {
                req["payload"]
                    .as_object_mut()
                    .unwrap()
                    .remove("referenceConfiguration");
            }
            "null" => req["payload"]["referenceConfiguration"] = Value::Null,
            "empty" => req["payload"]["referenceConfiguration"]["rootFileId"] = json!(""),
            "unknown" => {
                req["payload"]["referenceConfiguration"]["rootFileId"] = json!("not-a-member")
            }
            "binary" => req["payload"]["files"][0]["mediaKind"] = json!("binary"),
            "nontex" => req["payload"]["files"][0]["relativePath"] = json!("root.txt"),
            "path" => req["payload"]["files"][0]["relativePath"] = json!("./main.tex"),
            "duplicatepath" => {
                let mut other = req["payload"]["files"][0].clone();
                other["fileId"] = json!("other");
                req["payload"]["files"].as_array_mut().unwrap().push(other);
            }
            "unknownconfig" => req["payload"]["referenceConfiguration"]["other"] = json!(1),
            "unknownpayload" => req["payload"]["unexpected"] = json!(1),
            "unknownfile" => req["payload"]["files"][0]["unexpected"] = json!(1),
            "wrongprofile" => {
                req["payload"]["profile"] = json!({"id":"engine-structural","version":"0"})
            }
            _ => req["payload"]["knu"] = json!({}),
        }
        // Even invalid bytes cannot make a malformed configuration reach scans.
        if matches!(variant, "absent" | "null" | "empty" | "unknown" | "path") {
            bytes[0] = 255;
        }
        let r = wire(&req, &bytes);
        assert_eq!(r["status"], "failed", "{variant}: {r}");
        assert_eq!(r["error"]["code"], "InvalidInput");
        assert_eq!(r["requestId"], req["requestId"]);
    }
    let (req, bytes) = root("x");
    let raw = req.to_string().replace(
        "\"rootFileId\":\"file-0\"",
        "\"rootFileId\":\"file-0\",\"rootFileId\":\"file-0\"",
    );
    let r: Value = serde_json::from_str(&analyze_source(&raw, &bytes)).unwrap();
    assert_eq!(r["error"]["code"], "InvalidInput");
    assert_eq!(r["requestId"], req["requestId"]);
}

#[test]
fn profile_version_init_and_legacy_field_omission_are_preserved() {
    let (mut req, bytes) = root("Plain");
    req["payload"]["profile"]["version"] = json!("2.0.0");
    assert_eq!(wire(&req, &bytes)["error"]["code"], "UnsupportedCapability");
    req["payload"]["profile"] = json!({"id":"engine-structural","version":"0"});
    req["payload"]
        .as_object_mut()
        .unwrap()
        .remove("referenceConfiguration");
    let p = completed(&req, &bytes);
    assert!(p.get("referenceConfiguration").is_none());
    assert_eq!(p["ruleResults"].as_array().unwrap().len(), 1);
    let init: Value = serde_json::from_str(&super::initialize(
        r#"{"protocolVersion":1,"requestId":"init","operation":"initialize"}"#,
    ))
    .unwrap();
    assert_eq!(init["payload"]["engineVersion"], super::ENGINE_VERSION);
    assert_eq!(init["payload"]["operations"], json!(["analyzeSource"]));
}

#[test]
fn single_line_bom_mapping_long_opaque_id_and_capped_keys() {
    let source = "\u{feff}\\documentclass{article}\\begin{document}\\ref{absent}\\end{document}";
    let (mut req, bytes) = request(&[("main.tex", "text", source.as_bytes().to_vec())], 0);
    let id = "Я".repeat(super::MAX_ID_BYTES / 2);
    req["payload"]["files"][0]["fileId"] = json!(id);
    req["payload"]["referenceConfiguration"]["rootFileId"] =
        req["payload"]["files"][0]["fileId"].clone();
    let p = completed(&req, &bytes);
    invariant(&p, &req);
    let ds = new_diags(&p);
    assert_eq!(ds.len(), 1);
    let at = source.find("absent").unwrap();
    let (line, column) = super::mapping::line_column(&bytes, at);
    assert_eq!(
        ds[0]["primaryLocation"],
        json!({"line":line,"column":column})
    );
    for key in [
        "x".repeat(super::dto::MAX_TOKEN_TEXT_BYTES + 1),
        "x\0y".into(),
    ] {
        let (req, bytes) = root(&format!("\\label{{{key}}}\\ref{{missing}}"));
        let p = completed(&req, &bytes);
        assert_eq!(rule(&p, REFERENCE)["outcome"], "indeterminate");
        assert!(new_diags(&p).is_empty());
    }
}

#[test]
fn unused_labels_and_qualification_codes_do_not_create_observed_uncertainty() {
    for (body, labels, references) in [
        ("Plain", "notApplicable", "notApplicable"),
        ("\\label{unused}", "pass", "notApplicable"),
        (
            "\\section{Literal title}\\label{x}\\eqref{x}",
            "pass",
            "pass",
        ),
    ] {
        let (req, bytes) = root(body);
        let p = completed(&req, &bytes);
        invariant(&p, &req);
        assert_eq!(rule(&p, LABEL)["outcome"], labels);
        assert_eq!(rule(&p, REFERENCE)["outcome"], references);
        assert_eq!(p["aggregateOutcome"], "pass");
        assert_eq!(rule(&p, LABEL)["limitations"].as_array().unwrap().len(), 2);
        assert!(new_diags(&p).is_empty());
    }
}

#[test]
fn strict_nested_shape_duplicate_unknown_limits_and_no_root_identity_repair() {
    let (req, bytes) = root("Plain");
    for raw in [
        req.to_string().replace(
            "\"limits\":{}",
            "\"limits\":{\"maxFiles\":1,\"maxFiles\":1}",
        ),
        req.to_string()
            .replace("\"limits\":{}", "\"limits\":{\"unknown\":1}"),
        req.to_string()
            .replace("\"rootFileId\":\"file-0\"", "\"rootFileId\":1"),
        req.to_string()
            .replace("\"rootFileId\":\"file-0\"", "\"rootFileId\":null"),
        req.to_string()
            .replace("\"rootFileId\":\"file-0\"", "\"rootFileId\":\"FILE-0\""),
        req.to_string()
            .replace("\"fileId\":\"file-0\"", "\"fileId\":\"file-0\",\"extra\":0"),
    ] {
        let response: Value = serde_json::from_str(&analyze_source(&raw, &bytes)).unwrap();
        assert_eq!(response["requestId"], req["requestId"]);
        assert_eq!(response["error"]["code"], "InvalidInput");
    }
    for path in [
        "a/../main.tex",
        "a//main.tex",
        "/main.tex",
        "C:/main.tex",
        "a\\main.tex",
        " main.tex",
        "a\0.tex",
    ] {
        let mut q = req.clone();
        q["payload"]["files"][0]["relativePath"] = json!(path);
        assert_eq!(wire(&q, &bytes)["error"]["code"], "InvalidInput");
    }
    let mut q = req.clone();
    let mut second = q["payload"]["files"][0].clone();
    second["relativePath"] = json!("other.tex");
    q["payload"]["files"].as_array_mut().unwrap().push(second);
    assert_eq!(wire(&q, &bytes)["error"]["code"], "InvalidInput");
}

fn fixture_cases() -> Vec<(&'static str, Value, Vec<u8>)> {
    let mut cases = Vec::new();
    for (name, body) in [
        ("forward", "\\ref{x}\\label{x}"),
        ("missing", "\\ref{missing}"),
        ("duplicate", "\\label{x}\\label{x}\\ref{x}"),
        (
            "unicode",
            "% Україна 😀\n\\label{Київ🙂}\\ref{Київ🙂}\\ref{київ🙂}",
        ),
        ("macro", "\\newcommand{\\foo}{\\label{x}}\\ref{x}"),
        ("conditional", "\\iffalse\\label{x}\\fi\\ref{x}"),
        (
            "unknown-environment",
            "\\begin{custom}\\label{x}\\end{custom}\\ref{x}",
        ),
        (
            "comment-verbatim",
            "% \\label{x}\n\\begin{verbatim}\n\\label{x}\n\\end{verbatim}\n\\ref{x}",
        ),
        ("nonliteral", "\\label{ x }\\ref{unknown}"),
        ("unbraced", "\\input chapter\\ref{x}"),
        ("dynamic", "\\input{\\chapter}\\ref{x}"),
        ("unsafe", "\\input{../escape}\\ref{x}"),
        ("include-extension", "\\include{chapter.tex}\\ref{x}"),
    ] {
        let (q, b) = if name == "unicode" {
            let text = format!("\u{feff}{}", article(body).replace('\n', "\r\n"));
            request(&[("main.tex", "text", text.into_bytes())], 0)
        } else {
            root(body)
        };
        cases.push((name, q, b));
    }
    for (name, body, extra) in [
        (
            "multifile",
            "\\ref{x}\\input{chapter}",
            vec![("chapter.tex", "text", b"\\label{x}".to_vec())],
        ),
        (
            "unreachable",
            "\\ref{x}",
            vec![("chapter.tex", "text", b"\\label{x}".to_vec())],
        ),
        (
            "root-cwd",
            "\\input{dir/first}\\ref{x}",
            vec![
                ("dir/first.tex", "text", b"\\input{chapter}".to_vec()),
                ("chapter.tex", "text", b"\\label{x}".to_vec()),
                ("dir/chapter.tex", "text", b"\\label{wrong}".to_vec()),
            ],
        ),
        (
            "cycle",
            "\\input{chapter}\\ref{x}",
            vec![("chapter.tex", "text", b"\\input{main}".to_vec())],
        ),
        (
            "repeat",
            "\\input{chapter}\\input{chapter}\\ref{unknown}",
            vec![("chapter.tex", "text", b"\\label{x}".to_vec())],
        ),
        (
            "binary-target",
            "\\input{chapter}\\ref{x}",
            vec![("chapter.tex", "binary", vec![255, 0])],
        ),
        ("missing-include", "\\input{absent}\\ref{x}", vec![]),
    ] {
        let mut files = vec![("main.tex", "text", article(body).into_bytes())];
        files.extend(extra);
        let (q, b) = request(&files, 0);
        cases.push((name, q, b));
    }
    cases
}

#[test]
fn twenty_small_real_entry_cases_and_explicit_fresh_fixture_export() {
    let cases = fixture_cases();
    assert_eq!(cases.len(), 20);
    let output = std::env::var_os("SOURCE_REFERENCE_FIXTURE_DIR").map(std::path::PathBuf::from);
    if let Some(dir) = &output {
        assert!(
            dir.is_dir(),
            "root must supply an existing fresh output directory"
        );
        assert_eq!(
            std::fs::read_dir(dir).unwrap().count(),
            0,
            "refuse nonempty fixture output; never overwrite"
        );
    }
    let mut exports = Vec::new();
    let mut total = 0usize;
    // Complete every public-entry assertion and serialize/size-check every
    // synthetic export BEFORE creating any entry in the caller directory.
    for (name, req, bytes) in cases {
        let response = wire(&req, &bytes);
        assert_eq!(response["status"], "completed", "{name}: {response}");
        invariant(&response["payload"], &req);
        let expected = match name {
            "forward" | "multifile" | "root-cwd" | "duplicate" => "pass",
            "missing" | "unreachable" | "comment-verbatim" | "unicode" => "fail",
            _ => "indeterminate",
        };
        assert_eq!(
            rule(&response["payload"], REFERENCE)["outcome"],
            expected,
            "{name}: {response}"
        );
        if name == "duplicate" {
            assert_eq!(rule(&response["payload"], LABEL)["outcome"], "fail");
        }
        if output.is_some() {
            let source_hex: String = bytes.iter().map(|b| format!("{b:02x}")).collect();
            let value = json!({"description":"SYNTHETIC source-intent analysis, not compiler execution","request":req,"response":response,"sourceHex":source_hex});
            let mut encoded = serde_json::to_vec_pretty(&value).unwrap();
            encoded.push(b'\n');
            assert!(encoded.len() <= 256 * 1024);
            total += encoded.len();
            assert!(total <= 8 * 1024 * 1024);
            exports.push((name, encoded));
        }
    }
    if let Some(dir) = &output {
        use std::io::Write;
        for (name, encoded) in exports {
            let mut file = std::fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(dir.join(format!("{name}.json")))
                .unwrap();
            file.write_all(&encoded).unwrap();
        }
    }
}
