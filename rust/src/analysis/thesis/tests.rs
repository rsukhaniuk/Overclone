//! Synthetic thesis fixtures only. Pure source/wire checks, not PDF/browser evidence.
#[path = "citation_notes.rs"]
mod citation_notes;
#[path = "project_caption_roles_tests.rs"]
mod project_caption_roles;
#[path = "qualifiers.rs"]
mod qualifiers;
#[path = "vectors.rs"]
mod vectors;
use super::*;
fn request(files: &[(&str, &str)], version: &str) -> (Value, Vec<u8>) {
    let mut packed = vec![];
    let descriptors=files.iter().enumerate().map(|(i,(path,text))| {
        let offset=packed.len();packed.extend_from_slice(text.as_bytes());
        json!({"fileId":format!("f{i}"),"relativePath":path,"mediaKind":"text","hasBom":text.starts_with('\u{feff}'),
            "byteOffset":offset,"byteLength":text.len(),"sha256":"a".repeat(64)})
    }).collect::<Vec<_>>();
    (
        json!({"protocolVersion":1,"requestId":"r","operation":"analyzeSource","payload":{
        "snapshotId":"snapshot","projectId":"project","sessionId":"session","workingGeneration":1,
        "baseHeadRevisionId":null,"profile":{"id":profiles::KNU_ID,"version":version},
        "configuration":{"workType":"unspecified"},"referenceConfiguration":{"rootFileId":"f0"},
        "limits":{},"files":descriptors}}),
        packed,
    )
}
fn analyze(files: &[(&str, &str)]) -> Value {
    let (r, b) = request(files, "1.5.0");
    serde_json::from_str(&super::super::analyze_source(&r.to_string(), &b)).unwrap()
}
fn rule(v: &Value, index: usize) -> &Value {
    &v["payload"]["thesis"]["ruleResults"][index]
}
fn doc(body: &str) -> String {
    format!("\\documentclass{{article}}\n\\begin{{document}}\n{body}\n\\end{{document}}")
}
#[test]
fn manual_citation_resolution_order_and_single_variable_negatives() {
    let text=doc("\\cite{а,б}\\cite{а}\\begin{thebibliography}{9}\\bibitem{а}Один\\bibitem{б}Два\\end{thebibliography}");
    let v = analyze(&[("main.tex", &text)]);
    assert_eq!(v["status"], "completed", "{v}");
    assert_eq!(rule(&v, 1)["outcome"], "pass", "{v}");
    assert_eq!(rule(&v, 2)["outcome"], "pass");
    for (from, to, index) in [
        ("cite{а,б}", "cite{а,в}", 1),
        ("bibitem{б}", "bibitem{а}", 1),
    ] {
        let v = analyze(&[("main.tex", &text.replacen(from, to, 1))]);
        assert_eq!(rule(&v, index)["outcome"], "fail", "{v}");
    }
    let v = analyze(&[("main.tex", &text.replacen("cite{а,б}", "cite{б,а}", 1))]);
    assert_eq!(rule(&v, 1)["outcome"], "pass");
    assert_eq!(rule(&v, 2)["outcome"], "fail");
}
#[test]
fn selected_root_include_order_is_not_physical_file_order() {
    let root=doc("\\maketitle\\section*{РЕФЕРАТ}\\tableofcontents\\section*{ВСТУП}\\input{chapter}\\section*{ВИСНОВКИ}\\section*{ПЕРЕЛІК ДЖЕРЕЛ ПОСИЛАННЯ}");
    let v = analyze(&[
        ("main.tex", &root),
        ("chapter.tex", "\\section{Методи}"),
        ("ignored.tex", "\\badmacro"),
    ]);
    assert_eq!(rule(&v, 0)["outcome"], "pass", "{v}");
    assert_eq!(
        v["payload"]["thesis"]["closureFileIds"],
        json!(["f0", "f1"])
    );
    let bad = root.replace(
        "\\section*{ВСТУП}\\input{chapter}",
        "\\input{chapter}\\section*{ВСТУП}",
    );
    let v = analyze(&[("main.tex", &bad), ("chapter.tex", "\\section{Методи}")]);
    assert_eq!(rule(&v, 0)["outcome"], "fail", "{v}");
}
#[test]
fn unicode_appendix_letters_no_normalization_and_optional_absence() {
    for letter in ["А", "Б", "В", "Г", "Д", "И", "Я"] {
        let v = analyze(&[("main.tex", &doc(&format!("\\section*{{ДОДАТОК {letter}}}")))]);
        assert_eq!(rule(&v, 3)["outcome"], "pass", "{letter}: {v}");
    }
    for letter in [
        "Ґ",
        "Є",
        "З",
        "І",
        "Ї",
        "Й",
        "О",
        "Ч",
        "Ь",
        "A",
        "B",
        "а",
        "АБ",
        "А\u{301}",
    ] {
        let v = analyze(&[("main.tex", &doc(&format!("\\section*{{ДОДАТОК {letter}}}")))]);
        assert_eq!(rule(&v, 3)["outcome"], "fail", "{letter}: {v}");
    }
    let v = analyze(&[("main.tex", &doc("Текст"))]);
    assert_eq!(rule(&v, 3)["outcome"], "unavailable");
    assert_eq!(
        v["payload"]["thesis"]["workTypeDependentTitle"],
        "unavailable-work-type"
    );
}
#[test]
fn caption_presence_positive_and_negative_no_physical_claim() {
    for env in ["figure", "table"] {
        let text = doc(&format!("\\begin{{{env}}}\\caption{{Назва}}\\end{{{env}}}"));
        let v = analyze(&[("main.tex", &text)]);
        assert_eq!(rule(&v, 4)["outcome"], "pass", "{v}");
        let v = analyze(&[("main.tex", &text.replace("\\caption{Назва}", ""))]);
        assert_eq!(rule(&v, 4)["outcome"], "unavailable", "{v}");
        let v = analyze(&[("main.tex", &text.replace("\\caption{Назва}", "\\caption{}"))]);
        assert_eq!(rule(&v, 4)["outcome"], "fail", "{v}");
        let v = analyze(&[(
            "main.tex",
            &text.replace("\\caption{Назва}", "\\caption{Назва}\\caption{Інша}"),
        )]);
        assert_eq!(rule(&v, 4)["outcome"], "pass", "{v}");
    }
}
#[test]
fn uncertainty_retains_known_counterexample_and_never_claims_pass() {
    for context in [
        "\\unknown",
        "\\newcommand{\\x}{\\section{Зайве}}",
        "\\iftrue",
        "\\input{missing}",
        "\\begin{figure}[h]\\caption{Назва}\\end{figure}",
        "\\cite[с. 2]{a}",
        "\\bibliography{refs}",
    ] {
        let v = analyze(&[(
            "main.tex",
            &doc(&format!("{context}\\section*{{ДОДАТОК A}}")),
        )]);
        assert_eq!(rule(&v, 3)["outcome"], "indeterminate", "{context}: {v}");
        assert_eq!(rule(&v, 3)["knownCounterexample"], true);
    }
}
#[test]
fn bom_crlf_emoji_combining_and_exact_key_spans() {
    let text=format!("\u{feff}{}",doc("😀 и\u{306}\n\\cite{ключ😀,a\u{301}}\\begin{thebibliography}{9}\\bibitem{ключ😀}Один\\bibitem{a\u{301}}Два\\end{thebibliography}").replace('\n',"\r\n"));
    let v = analyze(&[("main.tex", &text)]);
    assert_eq!(rule(&v, 1)["outcome"], "pass", "{v}");
    for e in v["payload"]["thesis"]["events"].as_array().unwrap() {
        let a = e["span"]["startByte"].as_u64().unwrap() as usize;
        let b = e["span"]["endByte"].as_u64().unwrap() as usize;
        assert!(text.is_char_boundary(a) && text.is_char_boundary(b));
        if e["kind"] == "cite" || e["kind"] == "bibitem" {
            assert_eq!(&text[a..b], e["text"].as_str().unwrap());
        }
    }
}
#[test]
fn strict_shape_root_worktype_and_quotas() {
    let (r, b) = request(&[("main.tex", &doc("\\section*{ДОДАТОК А}"))], "1.5.0");
    for pointer in [
        "/payload/configuration/workType",
        "/payload/referenceConfiguration/rootFileId",
    ] {
        let mut bad = r.clone();
        *bad.pointer_mut(pointer).unwrap() = json!("unknown");
        let v: Value =
            serde_json::from_str(&super::super::analyze_source(&bad.to_string(), &b)).unwrap();
        assert_eq!(v["error"]["code"], "InvalidInput");
    }
    let mut bad = r.clone();
    bad["payload"]["limits"]["maxTokensPerProject"] = json!(1);
    let v: Value =
        serde_json::from_str(&super::super::analyze_source(&bad.to_string(), &b)).unwrap();
    assert_eq!(v["status"], "failed");
    assert_eq!(v["error"]["code"], "QuotaExceeded");
    let mut bad = r.clone();
    bad["payload"]["unexpected"] = json!(true);
    let v: Value =
        serde_json::from_str(&super::super::analyze_source(&bad.to_string(), &b)).unwrap();
    assert_eq!(v["error"]["code"], "InvalidInput");
}
#[test]
fn legacy_edition_has_no_thesis_payload_and_keeps_fixes_idempotent() {
    let text = doc("\\setlength{\\parindent}{1cm}");
    for version in ["1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0"] {
        let fixture = if version == "1.0.0" {
            text.clone()
        } else {
            text.replace(
                "\\setlength",
                "\\geometry{left=25mm,right=10mm,top=20mm,bottom=20mm}\\setlength",
            )
        };
        let (mut r, b) = request(&[("main.tex", &fixture)], version);
        if version != "1.5.0" {
            r["payload"]
                .as_object_mut()
                .unwrap()
                .remove("referenceConfiguration");
        }
        let v: Value =
            serde_json::from_str(&super::super::analyze_source(&r.to_string(), &b)).unwrap();
        assert_eq!(v["status"], "completed", "{v}");
        if version != "1.5.0" {
            assert!(v["payload"]["thesis"].is_null());
        }
        let fixes = v["payload"]["knu"]["fixDescriptors"].as_array().unwrap();
        if matches!(version, "1.4.0" | "1.5.0") {
            assert!(
                fixes.is_empty(),
                "Formatting edition intentionally offers no fixes"
            );
            continue;
        }
        assert_eq!(fixes.len(), 1, "{v}");
        let f = &fixes[0];
        let a = f["span"]["startByte"].as_u64().unwrap() as usize;
        let z = f["span"]["endByte"].as_u64().unwrap() as usize;
        let mut changed = b.clone();
        changed.splice(a..z, f["replacement"].as_str().unwrap().bytes());
        r["payload"]["files"][0]["byteLength"] = json!(changed.len());
        let second: Value =
            serde_json::from_str(&super::super::analyze_source(&r.to_string(), &changed)).unwrap();
        assert_eq!(
            second["payload"]["knu"]["fixDescriptors"],
            json!([]),
            "{second}"
        );
    }
}
