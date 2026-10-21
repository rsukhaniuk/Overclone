//! Synthetic inputs, REAL public entry; explicit fresh-dir export only under cfg(test).
use super::{
    analyze_source,
    profiles::{CAPTION, LINE_SPACING, PAPER},
};
use serde_json::{json, Value};

fn document(options: &str, spacing: &str, body: &str) -> String {
    format!("\\documentclass{options}{{article}}\n\\usepackage{{setspace}}\n\\setlength{{\\parindent}}{{1.27cm}}\n\\geometry{{left=25mm,right=10mm,top=20mm,bottom=20mm}}\n{spacing}\n\\begin{{document}}\n{body}\n\\end{{document}}\n")
}
fn request(source: &str) -> Value {
    json!({"protocolVersion":1,"requestId":"formatting-fixture","operation":"analyzeSource",
        "payload":{"snapshotId":"formatting-snapshot","projectId":"formatting-project","sessionId":"formatting-session",
        "workingGeneration":7,"baseHeadRevisionId":null,"profile":{"id":"knu-csc-2025-source","version":"1.4.0"},
        "configuration":{"workType":"bachelorQualification"},"limits":{},"files":[{"fileId":"formatting-file",
        "relativePath":"main.tex","mediaKind":"text","hasBom":source.starts_with('\u{feff}'),"sha256":"",
        "byteOffset":0,"byteLength":source.len()}]}})
}
fn wire(req: &Value, source: &[u8]) -> Value {
    serde_json::from_str(&analyze_source(&req.to_string(), source)).unwrap()
}
fn result(source: &str) -> Value {
    let r = wire(&request(source), source.as_bytes());
    assert_eq!(r["status"], "completed", "{r}");
    r["payload"].clone()
}
fn rule<'a>(p: &'a Value, id: &str) -> &'a Value {
    p["ruleResults"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == id)
        .unwrap()
}
fn obs<'a>(p: &'a Value, id: &str) -> Vec<&'a Value> {
    p["knu"]["files"][0]["observations"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|o| o["ruleId"] == id)
        .collect()
}
fn invariant(p: &Value, source: &[u8]) {
    assert_eq!(p["profile"]["version"], "1.4.0");
    assert_eq!(p["ruleResults"].as_array().unwrap().len(), 7);
    let token_count = p["facts"]["files"][0]["tokenCount"].as_u64().unwrap();
    let count = p["knu"]["files"][0]["observations"]
        .as_array()
        .unwrap()
        .len() as u64;
    assert_eq!(p["knu"]["files"][0]["observationCount"], count);
    assert!(token_count + count <= 20000);
    assert!(p["knu"]["fixDescriptors"].as_array().unwrap().is_empty());
    for id in [PAPER, LINE_SPACING, CAPTION] {
        let r = rule(p, id);
        assert_eq!(r["source"]["version"], "1.0.0");
        assert_eq!(r["source"]["severity"], "warning");
        assert_eq!(r["source"]["evaluationDomain"], "sourceObservation");
        for o in obs(p, id) {
            let s = o["valueSpan"]["startByte"].as_u64().unwrap() as usize;
            let e = o["valueSpan"]["endByte"].as_u64().unwrap() as usize;
            let raw = std::str::from_utf8(&source[s..e]).unwrap();
            if !o["excerptTruncated"].as_bool().unwrap() {
                assert_eq!(o["excerpt"], raw);
            }
            if o["agrees"] == false {
                let d = p["diagnostics"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .find(|d| d["id"] == o["id"]);
                if !p["coverage"]["diagnosticsTruncated"].as_bool().unwrap() {
                    let d = d.unwrap();
                    if id == CAPTION {
                        assert_eq!(d["span"]["startByte"], e - 1);
                        assert_eq!(&source[e - 1..e], b".");
                    } else {
                        assert_eq!(d["span"], o["valueSpan"]);
                    }
                }
            }
        }
    }
}

#[test]
fn formatting_literal_positives_negatives_and_unicode_spans() {
    let good = document("[a4paper]", "\\onehalfspacing", "\\begin{figure}\\caption{Версія 1.2 😀}\\end{figure}\n\\begin{table}\\caption{Таблиця}\\end{table}");
    let p = result(&good);
    invariant(&p, good.as_bytes());
    for id in [PAPER, LINE_SPACING, CAPTION] {
        assert_eq!(rule(&p, id)["outcome"], "pass", "{p}");
    }
    assert_eq!(
        obs(&p, LINE_SPACING)[0]["valueSpan"],
        obs(&p, LINE_SPACING)[0]["commandSpan"]
    );
    for declaration in [
        "\\setstretch{1.5}",
        "\\linespread{1.500}",
        "\\renewcommand{\\baselinestretch}{1.50}",
    ] {
        let p = result(&document("[a4paper]", declaration, ""));
        assert_eq!(
            rule(&p, LINE_SPACING)["outcome"],
            "pass",
            "{declaration}: {p}"
        );
    }
    let bad = format!("\u{feff}{}", document("[letterpaper]", "\\linespread{1.2}", "\\begin{figure}\\caption{Україна 😀.  }\\end{figure}\n\\begin{table}\\caption{Внутр. крапка.}\\end{table}").replace('\n', "\r\n"));
    let p = result(&bad);
    invariant(&p, bad.as_bytes());
    for id in [PAPER, LINE_SPACING, CAPTION] {
        assert_eq!(rule(&p, id)["outcome"], "fail", "{p}");
    }
    assert_eq!(
        p["diagnostics"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|d| d["ruleId"] == CAPTION)
            .count(),
        2
    );
}

#[test]
fn formatting_uncertainty_and_no_promoted_macro_or_environment_contents() {
    for paper in [
        "",
        "[a3paper]",
        "[a4paper,a4paper]",
        "[a4paper,letterpaper]",
        "[\\custom]",
        "[paperwidth=210mm]",
        "[a4paper,landscape]",
        "[paper=a4]",
    ] {
        let p = result(&document(paper, "\\onehalfspacing", ""));
        assert_eq!(rule(&p, PAPER)["outcome"], "indeterminate", "{paper}: {p}");
    }
    for spacing in [
        "",
        "\\linespread{1.5pt}",
        "\\linespread{.5}",
        "\\linespread{1.5000}",
        "\\setstretch{\\factor}",
        "\\renewcommand\\baselinestretch{1.5}",
        "\\linespread{1.5}\\linespread{1.5}",
        "\\linespread{1.5}\\setstretch{1.2}",
    ] {
        let p = result(&document("[a4paper]", spacing, ""));
        assert_eq!(
            rule(&p, LINE_SPACING)["outcome"],
            "indeterminate",
            "{spacing}: {p}"
        );
    }
    let illustrated_a3 = result(&document(
        "[a3paper]",
        "\\onehalfspacing",
        "\\begin{figure}\\caption{Illustration}\\end{figure}",
    ));
    assert_eq!(rule(&illustrated_a3, PAPER)["outcome"], "indeterminate");
    for body in ["\\newcommand{\\mine}{\\begin{figure}\\caption{Not live.}\\end{figure}}",
        "\\iffalse\\begin{figure}\\caption{Not live.}\\end{figure}\\fi",
        "\\begin{custom}\\begin{figure}\\caption{Not owned.}\\end{figure}\\end{custom}",
        "% \\begin{figure}\\caption{Comment.}\\end{figure}\n\\begin{verbatim}\\begin{table}\\caption{Verbatim.}\\end{table}\\end{verbatim}",
        "\\caption{Outside.}", "\\begin{figure}[ht]\\caption{Unsupported placement.}\\end{figure}"] {
        let p = result(&document("[a4paper]","\\onehalfspacing",body));
        assert!(obs(&p,CAPTION).is_empty(), "{body}: {p}");
    }
    for title in [
        "\\caption*{Title.}",
        "\\caption[Short.]{Title.}",
        "\\caption{\\macro.}",
        "\\caption{Title.%comment\n}",
        "\\caption",
    ] {
        let p = result(&document(
            "[a4paper]",
            "\\onehalfspacing",
            &format!("\\begin{{figure}}{title}\\end{{figure}}"),
        ));
        assert_eq!(
            rule(&p, CAPTION)["outcome"],
            "indeterminate",
            "{title}: {p}"
        );
        assert!(obs(&p, CAPTION).iter().all(|o| o["agrees"].is_null()));
    }
    let mismatched = result(&document(
        "[a4paper]",
        "\\onehalfspacing",
        "\\begin{figure}\\caption{Known.}\\end{table}",
    ));
    assert_eq!(rule(&mismatched, CAPTION)["outcome"], "indeterminate");
    assert_eq!(obs(&mismatched, CAPTION)[0]["agrees"], false);
    for spacing in ["\\onehalfspacing", "\\setstretch{1.5}"] {
        let p = result(&document("[a4paper]", spacing, "").replace("\\usepackage{setspace}\n", ""));
        assert_eq!(rule(&p, LINE_SPACING)["outcome"], "indeterminate");
    }
}

#[test]
fn formatting_all_existing_profile_goldens_remain_identical() {
    for directory in [
        "Knu",
        "KnuMarginFixes",
        "GeometryPackage",
        "SourceReferences",
    ] {
        let path = std::path::Path::new("../tests/Workspace.Tests/Fixtures").join(directory);
        for entry in std::fs::read_dir(path).unwrap() {
            let path = entry.unwrap().path();
            if path.extension().is_none_or(|extension| extension != "json") {
                continue;
            }
            let fixture: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
            let hex = fixture["sourceHex"].as_str().unwrap();
            let bytes: Vec<u8> = hex
                .as_bytes()
                .chunks_exact(2)
                .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).unwrap(), 16).unwrap())
                .collect();
            assert_eq!(
                wire(&fixture["request"], &bytes),
                fixture["response"],
                "{}",
                path.display()
            );
        }
    }
}

#[test]
fn formatting_cross_file_declarations_remain_uncertain() {
    let source = document("[a4paper]", "\\onehalfspacing", "");
    let mut req = request(&source);
    let mut second = req["payload"]["files"][0].clone();
    second["fileId"] = json!("second-file");
    second["relativePath"] = json!("second.tex");
    second["byteOffset"] = json!(source.len());
    req["payload"]["files"].as_array_mut().unwrap().push(second);
    let mut packed = source.as_bytes().to_vec();
    packed.extend_from_slice(source.as_bytes());
    let r = wire(&req, &packed);
    assert_eq!(r["status"], "completed");
    for id in [PAPER, LINE_SPACING] {
        assert_eq!(rule(&r["payload"], id)["outcome"], "indeterminate");
        assert!(rule(&r["payload"], id)["perFile"]
            .as_array()
            .unwrap()
            .iter()
            .all(|f| f["outcome"] == "indeterminate"));
    }
}

#[test]
fn formatting_geometry_paths_and_resource_limits() {
    for source in [
        "\\documentclass{article}\n\\geometry{a4paper,left=25mm,right=10mm,top=20mm,bottom=20mm}",
        "\\documentclass{article}\n\\usepackage[paper=a4,left=25mm,right=10mm,top=20mm,bottom=20mm]{geometry}",
    ] {
        let p = result(source); assert_eq!(obs(&p,PAPER).len(), 1); assert_eq!(obs(&p,PAPER)[0]["agrees"],true);
    }
    let source = document(
        "[a4paper]",
        "\\onehalfspacing",
        "\\begin{figure}\\caption{Title.}\\end{figure}",
    );
    let mut req = request(&source);
    req["payload"]["limits"] = json!({"maxResultBytes":1});
    let r = wire(&req, source.as_bytes());
    assert_eq!(r["status"], "failed");
    assert_eq!(r["error"]["code"], "QuotaExceeded");
    for limits in [
        json!({"maxTokensPerFile":8}),
        json!({"maxTokensPerProject":8}),
        json!({"maxDiagnosticsPerProject":0}),
    ] {
        req["payload"]["limits"] = limits;
        let r = wire(&req, source.as_bytes());
        assert_eq!(r["status"], "completed", "{r}");
        assert_eq!(r["payload"]["aggregateOutcome"], "indeterminate", "{r}");
    }
    let overlong = document(
        "[a4paper]",
        "\\onehalfspacing",
        &format!(
            "\\begin{{figure}}\\caption{{{}}}\\end{{figure}}",
            "я".repeat(2200)
        ),
    );
    let p = result(&overlong);
    assert_eq!(rule(&p, CAPTION)["outcome"], "indeterminate");
    let heavy = format!(
        "\\geometry{{{}right=10mm,top=20mm,bottom=20mm}}",
        "left=25mm,".repeat(300)
    );
    let bounded = result(&heavy);
    assert!(bounded["knu"]["files"][0]["limitations"]
        .as_array()
        .unwrap()
        .contains(&json!("lookaheadBudget")));
    assert_eq!(bounded["aggregateOutcome"], "indeterminate");
    let titles = "\\caption{Title.}".repeat(1100);
    let p = result(&document(
        "[a4paper]",
        "\\onehalfspacing",
        &format!("\\begin{{figure}}{titles}\\end{{figure}}"),
    ));
    assert_eq!(rule(&p, CAPTION)["outcome"], "indeterminate");
    assert!(p["diagnostics"].as_array().unwrap().len() <= 1000);
}

fn cases() -> Vec<(&'static str, String)> {
    vec![
        ("a4-half", document("[a4paper]","\\onehalfspacing","\\begin{figure}\\caption{Версія 1.2 😀}\\end{figure}")),
        ("letter-dots", format!("\u{feff}{}", document("[letterpaper]","\\linespread{1.2}","\\begin{figure}\\caption{Україна 😀.}\\end{figure}\n\\begin{table}\\caption{Таблиця.}\\end{table}").replace('\n',"\r\n"))),
        ("a3-purpose", document("[a3paper]","\\onehalfspacing","")),
        ("geometry-command", "\\documentclass{article}\\geometry{a4paper,left=25mm,right=10mm,top=20mm,bottom=20mm}".into()),
        ("geometry-package", "\\documentclass{article}\\usepackage[paper=a4,left=25mm,right=10mm,top=20mm,bottom=20mm]{geometry}".into()),
        ("setstretch", document("[a4paper]","\\setstretch{1.5}","")),
        ("linespread", document("[a4paper]","\\linespread{1.500}","")),
        ("renewal", document("[a4paper]","\\renewcommand{\\baselinestretch}{1.50}","")),
        ("missing", document("","","")),
        ("dynamic", document("[\\paper]","\\linespread{\\factor}","")),
        ("repeat", document("[a4paper,a4paper]","\\linespread{1.5}\\linespread{1.5}","")),
        ("conflict", document("[a4paper,a5paper]","\\linespread{1.5}\\setstretch{1.2}","")),
        ("caption-optional", document("[a4paper]","\\onehalfspacing","\\begin{table}\\caption[Short.]{Display.}\\end{table}")),
        ("caption-star", document("[a4paper]","\\onehalfspacing","\\begin{figure}\\caption*{Display.}\\end{figure}")),
        ("macro-condition", document("[a4paper]","\\onehalfspacing","\\newcommand{\\x}{\\begin{figure}\\caption{No.}\\end{figure}}\\iffalse\\begin{table}\\caption{No.}\\end{table}\\fi")),
        ("unknown-environment", document("[a4paper]","\\onehalfspacing","\\begin{custom}\\begin{figure}\\caption{No.}\\end{figure}\\end{custom}")),
        ("comment-verbatim", document("[a4paper]","\\onehalfspacing","% \\caption{Comment.}\n\\begin{verbatim}\\begin{figure}\\caption{Not live.}\\end{figure}\\end{verbatim}")),
        ("include", document("[a4paper]","\\onehalfspacing","\\input{chapter}")),
        ("unsupported-units", document("[a4paper]","\\linespread{1.5pt}","\\begin{figure}\\caption{\\title.}\\end{figure}")),
        ("table-unicode", document("[a4paper]","\\onehalfspacing","\\begin{table}\\caption{Табл. 1 😀}\\end{table}")),
    ]
}

#[test]
fn twenty_formatting_real_entry_cases_and_explicit_fresh_export() {
    let output = std::env::var_os("THESIS_FORMATTING_FIXTURE_DIR").map(std::path::PathBuf::from);
    if let Some(dir) = &output {
        assert!(dir.is_dir(), "root supplies an existing fresh directory");
        assert_eq!(
            std::fs::read_dir(dir).unwrap().count(),
            0,
            "refuse nonempty directory"
        );
    }
    let cases = cases();
    assert_eq!(cases.len(), 20);
    let mut exports = Vec::new();
    let mut total = 0;
    for (name, source) in cases {
        let req = request(&source);
        let r = wire(&req, source.as_bytes());
        assert_eq!(r["status"], "completed", "{name}: {r}");
        invariant(&r["payload"], source.as_bytes());
        let p = &r["payload"];
        match name {
            "a4-half" | "table-unicode" => {
                for id in [PAPER, LINE_SPACING, CAPTION] {
                    assert_eq!(rule(p, id)["outcome"], "pass", "{name}: {p}");
                }
            }
            "letter-dots" => {
                for id in [PAPER, LINE_SPACING, CAPTION] {
                    assert_eq!(rule(p, id)["outcome"], "fail");
                }
            }
            "a3-purpose" | "repeat" | "conflict" | "dynamic" | "missing" => {
                assert_eq!(rule(p, PAPER)["outcome"], "indeterminate")
            }
            "setstretch" | "linespread" | "renewal" => {
                assert_eq!(rule(p, LINE_SPACING)["outcome"], "pass")
            }
            "caption-optional" | "caption-star" | "unsupported-units" | "include" => {
                assert_eq!(rule(p, CAPTION)["outcome"], "indeterminate")
            }
            "macro-condition" | "unknown-environment" | "comment-verbatim" => {
                assert!(obs(p, CAPTION).is_empty())
            }
            _ => assert_eq!(obs(p, PAPER)[0]["agrees"], true),
        }
        if output.is_some() {
            let source_hex: String = source
                .as_bytes()
                .iter()
                .map(|b| format!("{b:02x}"))
                .collect();
            let mut encoded = serde_json::to_vec_pretty(&json!({"description":"SYNTHETIC formatting source intent, real Rust public output, no PDF compliance", "request":req,"response":r,"sourceHex":source_hex})).unwrap();
            encoded.push(b'\n');
            assert!(encoded.len() <= 256 * 1024);
            total += encoded.len();
            assert!(total <= 8 * 1024 * 1024);
            exports.push((name, encoded));
        }
    }
    if let Some(dir) = output {
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
