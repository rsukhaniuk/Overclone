//! Opt-in1.2 source-only fixes, through the REAL Rust public entry.
use super::{analyze_source, profiles};
use serde_json::{json, Value};
const GOOD: &str = r"\geometry{left=25mm,right=10mm,top=20mm,bottom=20mm}";
fn request(files: &[&str]) -> (Value, Vec<u8>) {
    let mut bytes = Vec::new();
    let descriptors: Vec<_> = files.iter().enumerate().map(|(i, s)| {
        let d = json!({"fileId":format!("f{i}"),"relativePath":format!("f{i}.tex"),"mediaKind":"text","hasBom":s.starts_with('\u{feff}'),"sha256":"0".repeat(64),"byteOffset":bytes.len(),"byteLength":s.len()});
        bytes.extend_from_slice(s.as_bytes()); d
    }).collect();
    (
        json!({"protocolVersion":1,"requestId":"margin-fix","operation":"analyzeSource","payload":{"snapshotId":"snapshot","projectId":"project","sessionId":"session","workingGeneration":3,"baseHeadRevisionId":"head","profile":{"id":profiles::KNU_ID,"version":profiles::MARGIN_FIX_VERSION},"configuration":{"workType":"unspecified"},"limits":{},"files":descriptors}}),
        bytes,
    )
}
fn reply(req: &Value, bytes: &[u8]) -> Value {
    let value: Value = serde_json::from_str(&analyze_source(&req.to_string(), bytes)).unwrap();
    assert_eq!(value["status"], "completed", "{value}");
    value
}
fn one(src: &str) -> Value {
    let (req, bytes) = request(&[src]);
    reply(&req, &bytes)
}
fn fixes(r: &Value) -> &Vec<Value> {
    r["payload"]["knu"]["fixDescriptors"].as_array().unwrap()
}
fn repair(src: &str, f: &Value) -> String {
    let a = f["span"]["startByte"].as_u64().unwrap() as usize;
    let b = f["span"]["endByte"].as_u64().unwrap() as usize;
    assert_eq!(&src[a..b], f["expectedOriginal"].as_str().unwrap());
    format!(
        "{}{}{}",
        &src[..a],
        f["replacement"].as_str().unwrap(),
        &src[b..]
    )
}
#[test]
fn all_sides_units_exact_span_and_idempotence() {
    for (index, side, minimum) in [
        (0, "left", "25"),
        (1, "right", "10"),
        (2, "top", "20"),
        (3, "bottom", "20"),
    ] {
        for literal in ["1mm", "0.9cm", "  0.000 cm  "] {
            let src = format!(
                "\u{feff}% Україна🙂\r\n{}\r\n\\setlength{{\\parindent}}{{1.27cm}}",
                GOOD.replace(&format!("{side}={minimum}mm"), &format!("{side}={literal}"))
            );
            let r = one(&src);
            let f = &fixes(&r)[0];
            assert_eq!(fixes(&r).len(), 1);
            assert_eq!(f["id"], profiles::MARGIN_FIXES[index].0);
            assert_eq!(f["replacement"], profiles::MARGIN_FIXES[index].1);
            assert_eq!(f["ruleId"], profiles::MARGINS);
            assert_eq!(f["ruleVersion"], "1.1.0");
            assert_eq!(f["version"], "1.0.0");
            let repaired = repair(&src, f);
            assert!(fixes(&one(&repaired)).is_empty());
            assert!(repaired.starts_with("\u{feff}% Україна🙂\r\n"));
            assert!(repaired.ends_with("\\setlength{\\parindent}{1.27cm}"));
        }
    }
    for src in [
        GOOD.to_string(),
        GOOD.replace("mm", "0mm"),
        r"\geometry{left=3cm,right=2cm,top=4cm,bottom=9cm}".into(),
    ] {
        assert!(fixes(&one(&src)).is_empty());
    }
}
#[test]
fn priority_indent_then_left_right_top_bottom_and_sequential_reanalysis() {
    let mut src = "\\setlength{\\parindent}{1cm}\\geometry{bottom=1mm,top=1mm,right=1mm,left=1mm}"
        .to_string();
    for id in std::iter::once(profiles::FIX).chain(profiles::MARGIN_FIXES.iter().map(|x| x.0)) {
        let r = one(&src);
        assert_eq!(fixes(&r).len(), 1);
        assert_eq!(fixes(&r)[0]["id"], id);
        src = repair(&src, &fixes(&r)[0]);
        assert!(fixes(&one(&src)).iter().all(|f| f["id"] != id));
    }
    assert!(fixes(&one(&src)).is_empty());
}
#[test]
fn strict_unknown_context_and_incomplete_coverage_refuse_without_new_parser() {
    let bad = GOOD.replace("left=25mm", "left=20mm");
    for src in [
        format!("{bad}\\input{{other}}"),
        format!("{bad}{bad}"),
        bad.replace("left=20mm", "left=20mm,left=1mm"),
        bad.replace("top=20mm", "top=\\x"),
        bad.replace("top=20mm", "top=1pt"),
        bad.replace("bottom=20mm", "unknown=20mm"),
        format!("{{{bad}}}"),
        format!("\\iftrue{bad}\\fi"),
        format!("\\newcommand{{\\x}}{{{bad}}}"),
        format!("{bad}\\unknown"),
        bad.replace("right=10mm,", ""),
    ] {
        assert!(fixes(&one(&src)).is_empty(), "{src}");
    }
    let (req, bytes) = request(&[&bad, &bad]);
    assert!(fixes(&reply(&req, &bytes)).is_empty());
    for limits in [
        json!({"maxDiagnosticsPerProject":1}),
        json!({"maxTokensPerFile":1}),
        json!({"maxBytesPerFile":10}),
        json!({"maxTokensPerProject":1}),
    ] {
        let src = bad.replace("top=20mm", "top=1mm");
        let (mut req, bytes) = request(&[&src]);
        req["payload"]["limits"] = limits;
        let raw: Value = serde_json::from_str(&analyze_source(&req.to_string(), &bytes)).unwrap();
        if raw["status"] == "completed" {
            assert!(fixes(&raw).is_empty());
        } else {
            assert!(raw["payload"].is_null());
        }
    }
}
#[test]
fn old_profiles_metadata_and_f1_margin_only_exemption_remain() {
    let src = "\\setlength{\\parindent}{1cm}\\geometry{left=1mm,unknown=20mm}";
    for version in [
        profiles::VERSION,
        profiles::LATEST_VERSION,
        profiles::MARGIN_FIX_VERSION,
    ] {
        let (mut req, bytes) = request(&[src]);
        req["payload"]["profile"]["version"] = json!(version);
        let r = reply(&req, &bytes);
        if version == profiles::VERSION {
            // Legacy1.0 does not recognize geometry at all: its global unknown
            // context suppresses F1, unlike1.1's margin-only exemption.
            assert!(fixes(&r).is_empty());
        } else {
            assert_eq!(fixes(&r)[0]["id"], profiles::FIX);
        }
        let rules = r["payload"]["knu"]["metadata"]["rules"].as_array().unwrap();
        if version != profiles::VERSION {
            let m = rules
                .iter()
                .find(|r| r["ruleId"] == profiles::MARGINS)
                .unwrap();
            assert_eq!(
                m["version"],
                if version == profiles::MARGIN_FIX_VERSION {
                    "1.1.0"
                } else {
                    "1.0.0"
                }
            );
        }
    }
    for version in [profiles::VERSION, profiles::LATEST_VERSION] {
        let src = GOOD.replace("left=25mm", "left=20mm");
        let (mut req, bytes) = request(&[&src]);
        req["payload"]["profile"]["version"] = json!(version);
        assert!(fixes(&reply(&req, &bytes)).is_empty());
    }
    // Replay every accepted legacy golden through the actual public entry.
    for entry in std::fs::read_dir("../tests/Workspace.Tests/Fixtures/Knu").unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_none_or(|x| x != "json") {
            continue;
        }
        let fixture: Value = serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
        if let (Some(req), Some(hex), Some(expected)) = (
            fixture.get("request"),
            fixture["sourceHex"].as_str(),
            fixture.get("response"),
        ) {
            let bytes = unhex(hex);
            assert_eq!(reply(req, &bytes), *expected);
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
fn export_actual_public_fixtures_only_when_requested() {
    let Ok(dir) = std::env::var("MARGIN_FIXTURE_DIR") else {
        return;
    };
    let dir = std::path::Path::new(&dir);
    let mut metadata = None;
    for entry in std::fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_none_or(|x| x != "seed") {
            continue;
        }
        let mut fixture: Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        let bytes = unhex(fixture["sourceHex"].as_str().unwrap());
        fixture["response"] = reply(&fixture["request"], &bytes);
        metadata = Some(fixture["response"]["payload"]["knu"]["metadata"].clone());
        std::fs::write(
            path.with_extension("json"),
            serde_json::to_vec_pretty(&fixture).unwrap(),
        )
        .unwrap();
    }
    std::fs::write(
        dir.join("knu-profile-v1_2-metadata.json"),
        serde_json::to_vec_pretty(&metadata.unwrap()).unwrap(),
    )
    .unwrap();
}
