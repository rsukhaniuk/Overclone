//! Opt-in1.3 public-entry integration. No package/TeX/PDF execution claims.
use super::{analyze_source, profiles};
use serde_json::{json, Value};

const OPTIONS: &str = "left=20mm,right=10mm,top=20mm,bottom=20mm";
fn article(preamble: &str) -> String {
    format!("\u{feff}% Україна🙂\r\n\\documentclass{{article}}\r\n{preamble}\r\n\\setlength{{\\parindent}}{{1.27cm}}\r\n\\begin{{document}}\r\n\\section{{Introduction}}\r\nPlain body text.\r\n\\end{{document}}\r\n")
}
fn forms(options: &str) -> [String; 2] {
    [
        article(&format!(
            "\\usepackage{{geometry}}\r\n\\geometry{{{options}}}"
        )),
        article(&format!("\\usepackage[{options}]{{geometry}}")),
    ]
}
fn request(files: &[&str], version: &str) -> (Value, Vec<u8>) {
    let mut packed = Vec::new();
    let descriptors: Vec<_> = files.iter().enumerate().map(|(i,s)| {
        let f = json!({"fileId":format!("f{i}"),"relativePath":format!("f{i}.tex"),"mediaKind":"text","hasBom":s.starts_with('\u{feff}'),"sha256":"0".repeat(64),"byteOffset":packed.len(),"byteLength":s.len()});
        packed.extend_from_slice(s.as_bytes()); f
    }).collect();
    (
        json!({"protocolVersion":1,"requestId":"geometry-package","operation":"analyzeSource","payload":{"snapshotId":"s","projectId":"p","sessionId":"session","workingGeneration":7,"baseHeadRevisionId":"head","profile":{"id":profiles::KNU_ID,"version":version},"configuration":{"workType":"unspecified"},"limits":{},"files":descriptors}}),
        packed,
    )
}
fn wire(req: &Value, bytes: &[u8]) -> Value {
    serde_json::from_str(&analyze_source(&req.to_string(), bytes)).unwrap()
}
fn reply(req: &Value, bytes: &[u8]) -> Value {
    let r = wire(req, bytes);
    assert_eq!(r["status"], "completed", "{r}");
    r
}
fn one(src: &str) -> Value {
    let (r, b) = request(&[src], profiles::GEOMETRY_PACKAGE_VERSION);
    reply(&r, &b)
}
fn fixes(r: &Value) -> &Vec<Value> {
    r["payload"]["knu"]["fixDescriptors"].as_array().unwrap()
}
fn repair(src: &str, f: &Value) -> String {
    let a = f["span"]["startByte"].as_u64().unwrap() as usize;
    let b = f["span"]["endByte"].as_u64().unwrap() as usize;
    assert!(src.is_char_boundary(a) && src.is_char_boundary(b));
    assert_eq!(&src[a..b], f["expectedOriginal"].as_str().unwrap());
    format!(
        "{}{}{}",
        &src[..a],
        f["replacement"].as_str().unwrap(),
        &src[b..]
    )
}
#[test]
fn clean_complete_articles_both_forms_exact_rule_citation_spans_and_idempotence() {
    for src in forms(OPTIONS) {
        let r = one(&src);
        let k = &r["payload"]["knu"];
        assert!(k["files"][0]["limitations"].as_array().unwrap().is_empty());
        assert_eq!(fixes(&r).len(), 1);
        let f = &fixes(&r)[0];
        assert_eq!(f["id"], profiles::MARGIN_FIXES[0].0);
        assert_eq!(f["ruleVersion"], "1.2.0");
        assert_eq!(f["version"], "1.0.0");
        assert_eq!(f["expectedOriginal"], "20mm");
        assert_eq!(f["replacement"], "25mm");
        let rule = k["metadata"]["rules"]
            .as_array()
            .unwrap()
            .iter()
            .find(|v| v["ruleId"] == profiles::MARGINS)
            .unwrap();
        assert_eq!(rule["citation"]["section"], "3.1");
        assert_eq!(rule["citation"]["printedPages"], json!([18]));
        let fixed = repair(&src, f);
        assert_eq!(fixed, src.replacen("left=20mm", "left=25mm", 1));
        assert!(fixes(&one(&fixed)).is_empty());
        assert!(fixed.starts_with("\u{feff}% Україна🙂\r\n"));
        assert!(fixed.ends_with("\\end{document}\r\n"));
        for o in k["files"][0]["observations"].as_array().unwrap() {
            for s in ["commandSpan", "declarationSpan", "valueSpan"] {
                let a = o[s]["startByte"].as_u64().unwrap() as usize;
                let b = o[s]["endByte"].as_u64().unwrap() as usize;
                assert!(src.is_char_boundary(a) && src.is_char_boundary(b));
            }
        }
    }
}
#[test]
fn all_sides_units_thresholds_larger_and_priority() {
    let good = "left=25mm,right=10mm,top=20mm,bottom=20mm";
    for (index, side, min) in [
        (0, "left", "25"),
        (1, "right", "10"),
        (2, "top", "20"),
        (3, "bottom", "20"),
    ] {
        for literal in ["0mm", "0.9cm", "  1.000 cm  "] {
            //1cm is the right minimum, so only the strictly below cases repair.
            let options = good.replace(&format!("{side}={min}mm"), &format!("{side}={literal}"));
            for src in forms(&options) {
                let r = one(&src);
                if side == "right" && literal.contains("1.000") {
                    assert!(fixes(&r).is_empty());
                    continue;
                }
                assert_eq!(fixes(&r)[0]["id"], profiles::MARGIN_FIXES[index].0);
                let next = repair(&src, &fixes(&r)[0]);
                assert!(fixes(&one(&next)).is_empty());
            }
        }
    }
    for options in [
        good,
        "left=2.5cm,right=1cm,top=2cm,bottom=2cm",
        "left=99mm,right=4cm,top=300mm,bottom=9cm",
    ] {
        for src in forms(options) {
            assert!(fixes(&one(&src)).is_empty());
        }
    }
    for mut src in forms("bottom=1mm,top=1mm,right=1mm,left=1mm") {
        src = src.replace("1.27cm", "1cm");
        for id in std::iter::once(profiles::FIX).chain(profiles::MARGIN_FIXES.iter().map(|x| x.0)) {
            let r = one(&src);
            assert_eq!(fixes(&r).len(), 1);
            assert_eq!(fixes(&r)[0]["id"], id);
            src = repair(&src, &fixes(&r)[0]);
            assert!(fixes(&one(&src)).iter().all(|f| f["id"] != id));
        }
        assert!(fixes(&one(&src)).is_empty());
    }
}
#[test]
fn duplicates_mixed_unknown_dynamic_nested_and_math_stay_unfixed() {
    let bad = format!("\\geometry{{{OPTIONS}}}");
    let optional = format!("\\usepackage[{OPTIONS}]{{geometry}}");
    for preamble in [
        format!("\\usepackage{{geometry}}\\usepackage{{geometry}}{bad}"),
        format!("{optional}{optional}"),
        format!("{optional}{bad}"),
        format!("{bad}\\usepackage{{geometry}}"),
        format!("\\usepackage{{geometry}}{bad}{bad}"),
        optional.replace("right=10mm", "right=10mm,right=1mm"),
        optional.replace("right=10mm", "margin=10mm"),
        optional.replace("right=10mm,", ""),
        optional.replace("20mm", "1pt"),
        optional.replace("geometry}", "amsmath}"),
        optional.replace("geometry}", "geometry,amsmath}"),
        optional.replace("geometry}", "\\geometryname}"),
        optional.replace("left=20mm", "left=\\x"),
        optional.replace("left=20mm", "left={20mm}"),
        optional.replace("left=20mm", "left=20%comment\nmm"),
        format!("{{{optional}}}"),
        format!("\\newcommand{{\\x}}{{{optional}}}"),
        format!("\\iftrue{optional}\\fi"),
        format!("{optional}\\unknownmacro"),
        format!("\\usepackage{{amsmath}}{optional}"),
        "\\usepackage{geometry}".into(),
        optional.replacen("usepackage", "usepackage*", 1),
    ] {
        // Invalid syntax must not become a back door to F1 either.
        for src in [
            article(&preamble),
            article(&preamble).replace("1.27cm", "1cm"),
        ] {
            assert!(fixes(&one(&src)).is_empty(), "{src}");
        }
    }
    let body = forms(OPTIONS)[1].replace("Plain body text.", "Plain $x^2+y^2=z^2$.");
    assert!(fixes(&one(&body)).is_empty());
    let in_body = article("").replace("Plain body text.", &optional);
    assert!(fixes(&one(&in_body)).is_empty());
    let src = forms(OPTIONS)[1].clone();
    let (req, bytes) = request(&[&src, &src], profiles::GEOMETRY_PACKAGE_VERSION);
    assert!(fixes(&reply(&req, &bytes)).is_empty());
}
#[test]
fn argument_and_shared_emission_budgets_refuse_no_hidden_pass() {
    for src in [
        article(&format!(
            "\\usepackage[left={},right=10mm,top=20mm,bottom=20mm]{{geometry}}",
            "1".repeat(5000)
        )),
        article(&format!(
            "\\usepackage[{}]{{geometry}}",
            "left=1mm,".repeat(80)
        )),
        article("\\usepackage[left=1mm{geometry}"),
    ] {
        let r = one(&src);
        assert!(fixes(&r).is_empty());
    }
    let source = forms("left=1mm,right=1mm,top=1mm,bottom=1mm")[1].clone();
    for limit in [
        json!({"maxTokensPerFile":4}),
        json!({"maxTokensPerProject":4}),
        json!({"maxDiagnosticsPerProject":1}),
        json!({"maxBytesPerFile":10}),
        json!({"maxBraceDepth":1}),
    ] {
        let bounded_source = if limit.get("maxBraceDepth").is_some() {
            source.replace("Plain body text.", "{{plain body}}")
        } else {
            source.clone()
        };
        let (mut req, bytes) = request(&[&bounded_source], profiles::GEOMETRY_PACKAGE_VERSION);
        req["payload"]["limits"] = limit;
        let r = wire(&req, &bytes);
        if r["status"] == "completed" {
            assert!(fixes(&r).is_empty(), "{r}");
        } else {
            assert!(r["payload"].is_null());
        }
    }
}
#[test]
fn old_three_profiles_and_all_old_goldens_are_identical() {
    for dir in [
        "../tests/Workspace.Tests/Fixtures/Knu",
        "../tests/Workspace.Tests/Fixtures/KnuMarginFixes",
    ] {
        for entry in std::fs::read_dir(dir).unwrap() {
            let p = entry.unwrap().path();
            if p.extension().is_none_or(|s| s != "json") {
                continue;
            }
            let fixture: Value = serde_json::from_slice(&std::fs::read(p).unwrap()).unwrap();
            let bytes = unhex(fixture["sourceHex"].as_str().unwrap());
            assert_eq!(wire(&fixture["request"], &bytes), fixture["response"]);
        }
    }
    for version in [
        profiles::VERSION,
        profiles::LATEST_VERSION,
        profiles::MARGIN_FIX_VERSION,
    ] {
        for src in forms(OPTIONS) {
            let (req, bytes) = request(&[&src], version);
            let r = reply(&req, &bytes);
            assert!(fixes(&r).is_empty());
        }
    }
}
fn unhex(hex: &str) -> Vec<u8> {
    hex.as_bytes()
        .chunks_exact(2)
        .map(|p| u8::from_str_radix(std::str::from_utf8(p).unwrap(), 16).unwrap())
        .collect()
}
#[test]
fn export_actual_package_fixtures_when_requested() {
    let Ok(dir) = std::env::var("GEOMETRY_PACKAGE_FIXTURE_DIR") else {
        return;
    };
    let dir = std::path::Path::new(&dir);
    let mut metadata = None;
    for entry in std::fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_none_or(|p| p != "seed") {
            continue;
        }
        let mut f: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        let bytes = unhex(f["sourceHex"].as_str().unwrap());
        f["response"] = reply(&f["request"], &bytes);
        metadata = Some(f["response"]["payload"]["knu"]["metadata"].clone());
        std::fs::write(
            path.with_extension("json"),
            serde_json::to_vec_pretty(&f).unwrap(),
        )
        .unwrap();
    }
    std::fs::write(
        dir.join("knu-profile-v1_3-metadata.json"),
        serde_json::to_vec_pretty(&metadata.unwrap()).unwrap(),
    )
    .unwrap();
}
