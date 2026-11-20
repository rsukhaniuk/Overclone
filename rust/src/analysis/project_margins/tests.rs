use super::*;
use crate::analysis::analyze_source;
use serde_json::{json, Value};

const ROOT: &str = include_str!("fixtures/root.tex");
const CHILD: &str = include_str!("fixtures/chapter.tex");
const ROOT_PATH: &str = "тези/my thesis.tex";
const LITERAL_INPUT: &str = "\\input{chapter.tex}";

fn request(files: &[(&str, &str, &str)], root: &str) -> (Value, Vec<u8>) {
    let mut packed = Vec::new();
    let descriptors: Vec<_> = files
        .iter()
        .enumerate()
        .map(|(i, (path, media, text))| {
            let offset = packed.len();
            packed.extend_from_slice(text.as_bytes());
            json!({"fileId":format!("file-{i}"),"relativePath":path,"mediaKind":media,
            "hasBom":false,"sha256":"a".repeat(64),"byteOffset":offset,"byteLength":text.len()})
        })
        .collect();
    (
        json!({"protocolVersion":1,"requestId":"project-margin-fixture","operation":"analyzeSource","payload":{
            "snapshotId":"snapshot-layout","projectId":"project-layout","sessionId":"session-layout",
            "workingGeneration":7,"baseHeadRevisionId":"head-layout",
            "profile":{"id":profiles::KNU_ID,"version":VERSION},"configuration":{"workType":"bachelorQualification"},
            "referenceConfiguration":{"rootFileId":root},"limits":{},"files":descriptors
        }}),
        packed,
    )
}

fn pair(root: &str, child: &str) -> (Value, Vec<u8>) {
    // Physical packing order deliberately differs from selected execution order.
    request(
        &[("chapter.tex", "text", child), (ROOT_PATH, "text", root)],
        "file-1",
    )
}

fn run(request: &Value, packed: &[u8]) -> Value {
    serde_json::from_str(&analyze_source(&request.to_string(), packed)).unwrap()
}

fn completed(request: &Value, packed: &[u8]) -> Value {
    let reply = run(request, packed);
    assert_eq!(reply["status"], "completed", "{reply}");
    reply["payload"].clone()
}

fn fixes(report: &Value) -> &[Value] {
    report["knu"]["fixDescriptors"].as_array().unwrap()
}

#[test]
fn exact_two_file_literal_margin_patch_and_idempotence() {
    let (request, packed) = pair(ROOT, CHILD);
    let report = completed(&request, &packed);
    let offset = ROOT.find(LITERAL_INPUT).unwrap();
    assert_eq!(
        report["projectMarginContext"],
        json!({"rootFileId":"file-1","includedFileId":"file-0",
        "inputSpan":{"startByte":offset,"endByte":offset+LITERAL_INPUT.len()}})
    );
    assert_eq!(
        report["referenceConfiguration"],
        request["payload"]["referenceConfiguration"]
    );
    assert_eq!(report["knu"]["files"][0]["observations"], json!([]));
    assert_eq!(report["knu"]["files"][0]["limitations"], json!([]));
    assert_eq!(report["knu"]["files"][1]["limitations"], json!([]));
    let margins = report["ruleResults"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["ruleId"] == profiles::MARGINS)
        .unwrap();
    assert_eq!(margins["perFile"][0]["outcome"], "unavailable");
    assert_eq!(margins["perFile"][1]["outcome"], "fail");
    assert_eq!(fixes(&report).len(), 1);
    let fix = &fixes(&report)[0];
    assert_eq!(fix["id"], profiles::MARGIN_FIXES[0].0);
    assert_eq!(fix["fileId"], "file-1");
    assert_eq!(fix["relativePath"], ROOT_PATH);
    assert_eq!(fix["expectedOriginal"], "12mm");
    assert_eq!(fix["replacement"], "25mm");
    assert_eq!(fix["ruleVersion"], profiles::GEOMETRY_PACKAGE_RULE_VERSION);
    assert_eq!(
        fix["expectedFileSha256"],
        request["payload"]["files"][1]["sha256"]
    );
    let start = fix["span"]["startByte"].as_u64().unwrap() as usize;
    let finish = fix["span"]["endByte"].as_u64().unwrap() as usize;
    assert_eq!(&ROOT[start..finish], "12mm");
    let fixed = format!("{}25mm{}", &ROOT[..start], &ROOT[finish..]);
    assert_eq!(fixed, ROOT.replacen("left=12mm", "left=25mm", 1));
    let (after, new_bytes) = pair(&fixed, CHILD);
    let result = completed(&after, &new_bytes);
    assert!(result["projectMarginContext"].is_object());
    assert!(fixes(&result).is_empty());
    assert_eq!(&packed[..CHILD.len()], &new_bytes[..CHILD.len()]);
}

#[test]
fn dynamic_or_duplicated_and_unresolved_inputs_cannot_authorize_fix() {
    let alternatives = [
        ROOT.replace(LITERAL_INPUT, "\\input{missing.tex}"),
        ROOT.replace(LITERAL_INPUT, "\\input{./chapter.tex}"),
        ROOT.replace(LITERAL_INPUT, "\\input{chapter}"),
        ROOT.replace(LITERAL_INPUT, "\\include{chapter}"),
        ROOT.replace(LITERAL_INPUT, "\\input{chapter.tex}\\input{chapter.tex}"),
        ROOT.replace(LITERAL_INPUT, "\\input{\\piece}"),
        ROOT.replace(LITERAL_INPUT, "% optional include\n\\input{chapter.tex}"),
        ROOT.replace(LITERAL_INPUT, "\\iftrue\\input{chapter.tex}\\fi"),
        ROOT.replace(LITERAL_INPUT, "\\input{chapter.tex}\\geometry{left=5mm}"),
        ROOT.replace(LITERAL_INPUT, "Unincluded printed words."),
        ROOT.replace("right=10mm", "left=8mm"),
        ROOT.replace("right=10mm", "paper=a4"),
        ROOT.replace("right=10mm", "right=\\other"),
        ROOT.replace(
            "\\documentclass{article}",
            "\\documentclass[twoside]{article}",
        ),
        ROOT.replace("\\documentclass{article}", "\\documentclass{book}"),
    ];
    for text in alternatives {
        let (request, bytes) = pair(&text, CHILD);
        let report = completed(&request, &bytes);
        assert!(
            report.get("projectMarginContext").is_none(),
            "{text}: {report}"
        );
        assert!(fixes(&report).is_empty(), "{text}");
    }
    for child in [
        "",
        "   \n",
        "Missing final newline",
        "\\input{chapter.tex}\n",
        "\\usepackage{geometry}\n",
        "\\setlength{\\parindent}{5mm}\n",
        "Hidden ^^5cinput text.\n",
        "\\end{document}\n",
        "A TeX~active character.\n",
    ] {
        let (request, bytes) = pair(ROOT, child);
        let report = completed(&request, &bytes);
        assert!(report.get("projectMarginContext").is_none(), "{child:?}");
        assert!(fixes(&report).is_empty(), "{child:?}");
    }
}

#[test]
fn extra_override_inputs_wrong_root_and_truncated_observations_fail_closed() {
    for extra in [
        ("geometry.cfg", "binary", "override"),
        ("geometry.sty", "text", ""),
        ("more.tex", "text", "extra\n"),
    ] {
        let (request, bytes) = request(
            &[
                ("chapter.tex", "text", CHILD),
                (ROOT_PATH, "text", ROOT),
                extra,
            ],
            "file-1",
        );
        let report = completed(&request, &bytes);
        assert!(report.get("projectMarginContext").is_none());
        assert!(fixes(&report).is_empty());
    }
    let (mut request, packed) = pair(ROOT, CHILD);
    request["payload"]["referenceConfiguration"]["rootFileId"] = json!("file-0");
    let report = completed(&request, &packed);
    assert!(report.get("projectMarginContext").is_none());
    assert!(fixes(&report).is_empty());
    request["payload"]["referenceConfiguration"]["rootFileId"] = json!("file-1");
    for limits in [
        json!({"maxDiagnosticsPerProject":0}),
        json!({"maxTokensPerProject":5}),
    ] {
        request["payload"]["limits"] = limits;
        let report = completed(&request, &packed);
        assert!(report.get("projectMarginContext").is_none());
        assert!(fixes(&report).is_empty());
    }
}

#[test]
fn multiple_deficiencies_are_measured_but_have_no_single_idempotent_repair() {
    let several = ROOT.replace("right=10mm", "right=5mm");
    let (request, bytes) = pair(&several, CHILD);
    let report = completed(&request, &bytes);
    assert!(report["projectMarginContext"].is_object());
    assert!(fixes(&report).is_empty());
}

#[test]
fn previous_geometry_edition_still_refuses_the_same_include() {
    let (mut request, bytes) = pair(ROOT, CHILD);
    let payload = request["payload"].as_object_mut().unwrap();
    payload.remove("referenceConfiguration");
    payload["profile"]["version"] = json!(profiles::GEOMETRY_PACKAGE_VERSION);
    let legacy = completed(&request, &bytes);
    assert!(legacy.get("projectMarginContext").is_none());
    assert!(legacy.get("referenceConfiguration").is_none());
    assert!(fixes(&legacy).is_empty());
    assert!(legacy["knu"]["files"][1]["limitations"]
        .as_array()
        .unwrap()
        .contains(&json!("includeNotResolved")));
}

#[test]
fn selected_root_and_wire_identity_are_required() {
    let (mut request, bytes) = pair(ROOT, CHILD);
    request["payload"]
        .as_object_mut()
        .unwrap()
        .remove("referenceConfiguration");
    assert_eq!(run(&request, &bytes)["error"]["code"], "InvalidInput");
    request["payload"]["referenceConfiguration"] =
        json!({"rootFileId":"file-1","userOverride":true});
    assert_eq!(run(&request, &bytes)["error"]["code"], "InvalidInput");
    request["payload"]["referenceConfiguration"] = json!({"rootFileId":"missing"});
    assert_eq!(run(&request, &bytes)["error"]["code"], "InvalidInput");
    let (mut request, mut bytes) = pair(ROOT, CHILD);
    request["payload"]["files"][0]["sha256"] = json!("");
    let report = completed(&request, &bytes);
    assert!(report.get("projectMarginContext").is_none());
    assert!(fixes(&report).is_empty());

    let (request, _) = pair(ROOT, CHILD);
    bytes.push(b'X');
    let report = completed(&request, &bytes);
    assert!(report.get("projectMarginContext").is_none());
    assert!(fixes(&report).is_empty());
}
