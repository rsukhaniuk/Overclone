use super::*;
use serde_json::{json, Value};
mod edges;
mod regressions;
mod vectors;

fn request(
    files: &[(&str, &str, &str)],
    file: &str,
    context: &str,
    query: &str,
    caret: usize,
) -> (Value, Vec<u8>) {
    let mut bytes = vec![];
    let descriptors:Vec<_>=files.iter().map(|(id,path,text)|{
        let offset=bytes.len(); bytes.extend_from_slice(text.as_bytes());
        json!({"fileId":id,"relativePath":path,"mediaKind":"text","hasBom":text.starts_with('\u{feff}'),
            "sha256":"","byteOffset":offset,"byteLength":text.len()})
    }).collect();
    (
        json!({"protocolVersion":1,"requestId":"r1","operation":"authoringAssist","payload":{
        "snapshotId":"snapshot","projectId":"project","sessionId":"session","workingGeneration":1,
        "baseHeadRevisionId":null,"rootFileId":"main","fileId":file,"caretByte":caret,
        "context":context,"query":query,"catalogVersion":"1.0","files":descriptors,"limits":{}}}),
        bytes,
    )
}
fn run_request(v: &Value, b: &[u8]) -> Value {
    serde_json::from_str(&assist(&v.to_string(), b)).unwrap()
}
fn successful(v: &Value, b: &[u8]) -> Value {
    let r = run_request(v, b);
    assert_eq!(r["status"], "completed", "{r}");
    r["payload"].clone()
}
fn labels(p: &Value) -> Vec<&str> {
    p["candidates"]
        .as_array()
        .unwrap()
        .iter()
        .map(|c| c["label"].as_str().unwrap())
        .collect()
}

#[test]
fn command_catalog_is_package_gated_and_offline() {
    let text = "\\documentclass{article}\n\\usepackage{graphicx}\n\\incl";
    let (v, b) = request(
        &[("main", "main.tex", text)],
        "main",
        "command",
        "incl",
        text.len(),
    );
    let p = successful(&v, &b);
    assert_eq!(labels(&p), vec!["include", "includegraphics"]);
    let graphics = &p["candidates"][1];
    assert!(graphics["detail"]
        .as_str()
        .unwrap()
        .contains("resources unverified"));
    assert!(graphics["detail"]
        .as_str()
        .unwrap()
        .contains("https://ctan.org/pkg/graphicx"));
    assert_eq!(
        graphics["replaceSpan"],
        json!({"startByte":text.len()-5,"endByte":text.len()})
    );
    assert_eq!(graphics["caretOffsetUtf16"], 17);
    let text = "\\incl";
    let (v, b) = request(
        &[("main", "main.tex", text)],
        "main",
        "command",
        "incl",
        text.len(),
    );
    assert_eq!(labels(&successful(&v, &b)), vec!["include"]);
}

#[test]
fn every_context_has_a_useful_literal_result() {
    let main="\\documentclass{report}\n\\usepackage{amsmath,biblatex}\n\\newcommand{\\hello}[2]{#1 #2}\n\\newenvironment{proofish}{}{}\n\\label{sec:one}\n\\addbibresource{refs.bib}\n\\section{Title}\n\\subsection{Child}\n\\input{chapter}\n";
    let bib = "@book{knu2025, title={Kyiv {nested}}, author=\"A {B}\"}\n";
    for (context, suffix, query, want) in [
        ("command", "\\hel", "hel", "hello"),
        ("environment", "\\begin{pro}", "pro", "proofish"),
        ("reference", "\\ref{sec:}", "sec:", "sec:one"),
        ("citation", "\\cite{knu}", "knu", "knu2025"),
        ("inputPath", "\\input{chap}", "chap", "chapter.tex"),
        ("definition", "\\hello", "hello", "hello"),
        ("outline", "", "", ""),
    ] {
        let text = format!("{main}{suffix}");
        let caret = text.len() - usize::from(suffix.ends_with('}'));
        let (v, b) = request(
            &[
                ("main", "main.tex", &text),
                (
                    "chapter",
                    "chapter.tex",
                    "\\section{Included}\\label{sec:two}",
                ),
                ("bib", "refs.bib", bib),
            ],
            "main",
            context,
            query,
            caret,
        );
        let p = successful(&v, &b);
        if context != "outline" {
            assert!(labels(&p).contains(&want), "{context}: {p}");
        }
        assert_eq!(p["outline"].as_array().unwrap().len(), 3);
        assert_eq!(p["outline"][1]["parentIndex"], 0);
        assert_eq!(p["outline"][2]["parentIndex"], Value::Null);
        if context == "command" {
            assert_eq!(p["candidates"][0]["insertText"], "\\hello{}{}");
        }
    }
}

#[test]
fn malformed_wire_versions_and_budgets_fail_closed() {
    let text = "\\sec";
    let (base, b) = request(
        &[("main", "main.tex", text)],
        "main",
        "command",
        "sec",
        text.len(),
    );
    for (pointer, value, code) in [
        ("/protocolVersion", json!(2), "UnsupportedProtocol"),
        ("/operation", json!("execute"), "UnsupportedCapability"),
        (
            "/payload/catalogVersion",
            json!("2.0"),
            "UnsupportedCapability",
        ),
        ("/payload/context", json!("Command"), "InvalidInput"),
        ("/payload/caretByte", json!(999), "InvalidInput"),
        ("/payload/query", json!("x".repeat(1025)), "QuotaExceeded"),
        ("/payload/workingGeneration", json!(-1), "InvalidInput"),
        ("/payload/files/0/byteLength", json!(999), "InvalidInput"),
        (
            "/payload/files/0/relativePath",
            json!("../bad.tex"),
            "InvalidInput",
        ),
    ] {
        let mut v = base.clone();
        *v.pointer_mut(pointer).unwrap() = value;
        let r = run_request(&v, &b);
        assert_eq!(r["error"]["code"], code, "{pointer}: {r}");
        assert!(r.get("payload").is_none());
    }
    for limits in [
        json!({"maxFiles":0}),
        json!({"maxFiles":5001}),
        json!({"maxResultBytes":30}),
        json!({"maxTokensPerFile":1}),
        json!({"maxBraceDepth":1,"maxTotalBytes":1}),
    ] {
        let mut v = base.clone();
        v["payload"]["limits"] = limits;
        assert_eq!(run_request(&v, &b)["error"]["code"], "QuotaExceeded");
    }
    for json in [
        base.to_string().replace(
            "\"context\":\"command\"",
            "\"context\":\"command\",\"context\":\"outline\"",
        ),
        base.to_string()
            .replace("\"hasBom\":false", "\"hasBom\":false,\"unexpected\":true"),
        base.to_string()
            .replace("\"limits\":{}", "\"limits\":{\"unexpected\":1}"),
    ] {
        let r: Value = serde_json::from_str(&assist(&json, &b)).unwrap();
        assert_eq!(r["status"], "failed", "{r}");
    }
}
