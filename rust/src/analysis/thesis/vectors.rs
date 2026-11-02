use super::*;
use crate::analysis::analyze_source;

#[test]
fn exact_synthetic_json_vectors_and_optional_private_export() {
    let fixture: Value = serde_json::from_str(include_str!(
        "../../../tests/fixtures/r2/source/vectors.json"
    ))
    .unwrap();
    let mut exported = vec![];
    for vector in fixture["vectors"].as_array().unwrap() {
        let files = vector["files"]
            .as_array()
            .unwrap()
            .iter()
            .map(|f| (f["path"].as_str().unwrap(), f["text"].as_str().unwrap()))
            .collect::<Vec<_>>();
        let (request, packed) = request(&files, "1.5.0");
        let response: Value =
            serde_json::from_str(&analyze_source(&request.to_string(), &packed)).unwrap();
        assert_eq!(
            response["status"], "completed",
            "{}: {response}",
            vector["id"]
        );
        for (index, expected) in vector["expected"].as_object().unwrap() {
            assert_eq!(
                &rule(&response, index.parse().unwrap())["outcome"],
                expected,
                "{}: {response}",
                vector["id"]
            );
        }
        exported.push(json!({"id":vector["id"],"synthetic":true,"request":request,
            "packedUtf8Hex":packed.iter().map(|b|format!("{b:02x}")).collect::<String>(),"response":response}));
    }
    if let Ok(path) = std::env::var("R2_CATALOG_VECTOR_OUT") {
        std::fs::write(
            path,
            serde_json::to_vec_pretty(&json!({"schemaVersion":1,"vectors":exported})).unwrap(),
        )
        .unwrap();
    }
}

#[test]
fn duplicate_fields_invalid_utf8_missing_worktype_and_binary_root_fail_closed() {
    let (r, mut b) = request(&[("main.tex", &doc("Текст"))], "1.5.0");
    let duplicate = r.to_string().replace(
        "\"workType\":\"unspecified\"",
        "\"workType\":\"unspecified\",\"workType\":\"coursework\"",
    );
    let v: Value = serde_json::from_str(&analyze_source(&duplicate, &b)).unwrap();
    assert_eq!(v["error"]["code"], "InvalidInput");
    assert_eq!(v["requestId"], "r");
    for pointer in ["configuration", "referenceConfiguration"] {
        let mut bad = r.clone();
        bad["payload"].as_object_mut().unwrap().remove(pointer);
        let v: Value = serde_json::from_str(&analyze_source(&bad.to_string(), &b)).unwrap();
        assert_eq!(v["error"]["code"], "InvalidInput");
    }
    let mut bad = r.clone();
    bad["payload"]["files"][0]["mediaKind"] = json!("binary");
    let v: Value = serde_json::from_str(&analyze_source(&bad.to_string(), &b)).unwrap();
    assert_eq!(v["error"]["code"], "InvalidInput");
    b[0] = 255;
    let v: Value = serde_json::from_str(&analyze_source(&r.to_string(), &b)).unwrap();
    assert_eq!(v["error"]["code"], "InvalidInput");
}

#[test]
fn optional_abbreviation_structure_and_small_result_budget() {
    let text=doc("\\maketitle\\section*{РЕФЕРАТ}\\tableofcontents\\section*{СКОРОЧЕННЯ ТА УМОВНІ ПОЗНАКИ}\\section*{ВСТУП}\\section{Методи}\\section*{ВИСНОВКИ}\\section*{ПЕРЕЛІК ДЖЕРЕЛ ПОСИЛАННЯ}");
    let v = analyze(&[("main.tex", &text)]);
    assert_eq!(rule(&v, 0)["outcome"], "pass", "{v}");
    let (mut r, b) = request(&[("main.tex", &text)], "1.5.0");
    r["payload"]["limits"]["maxResultBytes"] = json!(128);
    let v: Value = serde_json::from_str(&analyze_source(&r.to_string(), &b)).unwrap();
    assert_eq!(v["status"], "failed");
    assert_eq!(v["error"]["code"], "QuotaExceeded");
}
