//! Source observations only. Independently recorded native fixture evidence
//! checks actual compilation, PDF text and map preimages separately.
use super::*;

const VERSION: &str = "1.7.0";
const ROOT: &str = "тези/my thesis.tex";
const PREAMBLE: &str = "\\documentclass{article}\r\n\\usepackage[T2A]{fontenc}\r\n\\usepackage[ukrainian]{babel}\r\n\\begin{document}\r\n";
const CHAPTER: &str =
    "% Україна Іі 😀\r\n\\begin{table}\r\n\\caption{Інші вимірювання}\r\n\\end{table}\r\n";

fn main_source() -> String {
    format!("% Україна Іі 😀\r\n{PREAMBLE}\\begin{{figure}}\r\n\\caption{{Іван і Ірина}}\r\n\\end{{figure}}\r\n\\input{{chapter}}\r\n\\end{{document}}\r\n")
}

fn edition(files: &[(&str, &str)], root: usize, version: &str) -> Value {
    let (mut r, b) = request(files, version);
    r["payload"]["referenceConfiguration"]["rootFileId"] = json!(format!("f{root}"));
    serde_json::from_str(&crate::analysis::analyze_source(&r.to_string(), &b)).unwrap()
}

fn assert_refused(files: &[(&str, &str)]) -> Value {
    let value = edition(files, 0, VERSION);
    assert_eq!(value["status"], "completed", "{value}");
    let report = &value["payload"]["thesis"];
    assert_eq!(report["captionRoles"], json!([]), "{value}");
    assert_eq!(report["captionRoleCapability"], Value::Null, "{value}");
    value
}

#[test]
fn ukrainian_titles_use_executed_root_and_include_with_exact_crlf_byte_spans() {
    let source = main_source();
    for (files, root) in [
        ([(ROOT, source.as_str()), ("chapter.tex", CHAPTER)], 0),
        ([("chapter.tex", CHAPTER), (ROOT, source.as_str())], 1),
    ] {
        let v = edition(&files, root, VERSION);
        assert_eq!(v["status"], "completed", "{v}");
        let report = &v["payload"]["thesis"];
        assert_eq!(report["catalogVersion"], "1.2.0");
        assert_eq!(report["completeLiteralScan"], true, "{v}");
        assert_eq!(report["limitations"], json!([]));
        assert_eq!(report["renderedCompliance"], "notAssessed");
        assert_eq!(
            report["captionRoleCapability"],
            json!({
                "id": "p16-article-t2a-ukrainian-caption-v1",
                "runtimeProfile": 16,
                "runtimeClosureSha256": "8e3fcc0c3255613609a423a5440934b261feee32a5cc73510f4633648b4a135e",
                "sourcePinsSha256": "73d1e45bfccbc0048189e355a3a7989578d120e22eb1a95433fec1813bafa6d3"
            })
        );
        let roles = report["captionRoles"].as_array().unwrap();
        assert_eq!(roles.len(), 2);
        for (role, env, file, text) in [
            (&roles[0], "figure", root, "Іван і Ірина"),
            (&roles[1], "table", 1 - root, "Інші вимірювання"),
        ] {
            assert_eq!(role.as_object().unwrap().len(), 6);
            let idx = role["eventIndex"].as_u64().unwrap() as usize;
            let e = &report["events"][idx];
            let span = &role["span"];
            let a = span["startByte"].as_u64().unwrap() as usize;
            let b = span["endByte"].as_u64().unwrap() as usize;
            assert_eq!(e["kind"], "captionTitle");
            assert_eq!(e["text"], text);
            assert_eq!(role["method"], "literal-project-caption-title");
            assert_eq!(role["environment"], env);
            assert_eq!(role["fileId"], format!("f{file}"));
            assert_eq!(role["contextRootFileId"], format!("f{root}"));
            assert_eq!(e["span"], *span);
            assert_eq!(&files[file].1[a..b], text);
        }
        // Existing per-file KNU producer still discloses its own include limits.
        let file_facts = v["payload"]["knu"]["files"].as_array().unwrap();
        assert!(file_facts.iter().any(|f| f["limitations"]
            .as_array()
            .unwrap()
            .contains(&json!("includeNotResolved"))));
    }
}

#[test]
fn every_altered_preamble_package_or_project_shadow_refuses_role_issuance() {
    let source = main_source();
    for (from, to) in [
        ("{article}", "[10pt]{article}"),
        ("{article}", "{report}"),
        ("[T2A]", "[T1]"),
        ("[ukrainian]", "[english,ukrainian]"),
        ("\\usepackage[T2A]{fontenc}\r\n", ""),
        (
            "\\usepackage[T2A]{fontenc}\r\n\\usepackage[ukrainian]{babel}",
            "\\usepackage[ukrainian]{babel}\r\n\\usepackage[T2A]{fontenc}",
        ),
        (
            "\\usepackage[T2A]{fontenc}",
            "\\usepackage[T2A]{fontenc}\\usepackage[T2A]{fontenc}",
        ),
        (
            "\\begin{document}",
            "\\usepackage{graphicx}\\begin{document}",
        ),
        ("\\begin{document}", "[2020-01-01]\\begin{document}"),
        ("% Україна", "% ^^M Україна"),
    ] {
        assert!(source.contains(from));
        let change = source.replace(from, to);
        let v = assert_refused(&[(ROOT, &change), ("chapter.tex", CHAPTER)]);
        assert_eq!(
            v["payload"]["thesis"]["completeLiteralScan"], false,
            "{from} -> {to}"
        );
    }
    for name in [
        "fontenc.sty",
        "config/babel.cfg",
        "other/BABEL-UKRAINIAN.TEX",
    ] {
        assert_refused(&[
            (ROOT, &source),
            ("chapter.tex", CHAPTER),
            (name, "% shadow"),
        ]);
    }
}

#[test]
fn dynamic_missing_and_non_isolated_contexts_leave_no_role() {
    let source = main_source();
    for (from, to) in [
        ("\\input{chapter}", "\\input{chapter}\\input{chapter}"),
        ("\\input{chapter}", "\\input{missing}"),
        ("\\input{chapter}", "\\input{\\dynamic}"),
        ("\\input{chapter}", "\\iftrue\\input{chapter}\\fi"),
        (
            "\\input{chapter}",
            "\\newcommand{\\foo}{\\input{chapter}}\\foo",
        ),
        ("\\caption{Іван і Ірина}", "\\caption[Short]{Іван і Ірина}"),
        (
            "\\caption{Іван і Ірина}",
            "\\caption{Іван і Ірина}\\caption{Second}",
        ),
        (
            "\\end{figure}\r\n\\input{chapter}",
            "\\input{chapter}\r\n\\end{figure}",
        ),
        ("\\end{figure}\r\n\\input{chapter}", "\\input{chapter}"),
    ] {
        assert!(source.contains(from));
        assert_refused(&[(ROOT, &source.replace(from, to)), ("chapter.tex", CHAPTER)]);
    }
    assert_refused(&[(ROOT, &source), ("chapter.tex", "\\input{тези/my thesis}")]);
    assert_refused(&[
        (ROOT, &source),
        ("chapter.tex", "\\begin{table}\\caption{Open}"),
    ]);
}

#[test]
fn legacy_v16_still_refuses_preamble_and_omits_both_new_wire_fields() {
    let source = main_source();
    for version in ["1.5.0", "1.6.0"] {
        let v = edition(&[(ROOT, &source), ("chapter.tex", CHAPTER)], 0, version);
        assert_eq!(v["status"], "completed");
        let t = &v["payload"]["thesis"];
        assert_eq!(t["completeLiteralScan"], false);
        assert!(t["limitations"]
            .as_array()
            .unwrap()
            .contains(&json!("unknownPackage")));
        assert!(t.get("captionRoles").is_none());
        assert!(t.get("captionRoleCapability").is_none());
    }
}
