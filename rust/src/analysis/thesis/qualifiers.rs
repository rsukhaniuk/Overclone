use super::{analyze, doc, request, rule};
use serde_json::{json, Value};

#[test]
fn work_types_are_explicit_and_qualification_only_fields_never_inferred() {
    for (work, expected) in [
        ("unspecified", "unavailable-work-type"),
        ("coursework", "not-applicable-coursework"),
        ("bachelorQualification", "unavailable-title-metadata"),
        ("masterQualification", "unavailable-title-metadata"),
    ] {
        let (mut r, bytes) = request(&[("main.tex", &doc("\\section*{ДОДАТОК А}"))], "1.5.0");
        r["payload"]["configuration"]["workType"] = json!(work);
        let v: Value =
            serde_json::from_str(&crate::analysis::analyze_source(&r.to_string(), &bytes)).unwrap();
        assert_eq!(v["payload"]["thesis"]["workType"], work);
        assert_eq!(v["payload"]["thesis"]["workTypeDependentTitle"], expected);
        assert_eq!(rule(&v, 3)["outcome"], "pass");
    }
}

#[test]
fn optional_appendix_and_abbreviation_do_not_replace_missing_required_roles() {
    let text=doc("\\maketitle\\tableofcontents\\section*{ВСТУП}\\section{Методи}\\section*{ВИСНОВКИ}\\section*{ПЕРЕЛІК ДЖЕРЕЛ ПОСИЛАННЯ}\\section*{ДОДАТОК А}");
    let v = analyze(&[("main.tex", &text)]);
    assert_eq!(rule(&v, 0)["outcome"], "unavailable"); // abstract absent, not proven missing
    let text=doc("\\maketitle\\section*{РЕФЕРАТ}\\section*{СКОРОЧЕННЯ ТА УМОВНІ ПОЗНАКИ}\\section*{ВСТУП}\\section{Методи}\\section*{ВИСНОВКИ}\\section*{ПЕРЕЛІК ДЖЕРЕЛ ПОСИЛАННЯ}");
    let v = analyze(&[("main.tex", &text)]);
    assert_eq!(rule(&v, 0)["outcome"], "unavailable"); // contents absent
}

#[test]
fn catalog_preserves_numeric_strength_and_cited_exceptions_as_data_only() {
    let c: Value = serde_json::from_str(crate::thesis_catalog::JSON).unwrap();
    let rule = |id: &str| {
        c["rules"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["ruleId"] == id)
            .unwrap()
    };
    let abstract_rule = rule("KNU-ABSTRACT-001");
    assert_eq!(abstract_rule["predicates"][1]["strength"], "usual");
    assert_eq!(abstract_rule["predicates"][1]["min"], 5);
    assert_eq!(abstract_rule["predicates"][1]["max"], 15);
    let length = rule("KNU-LENGTH-001");
    assert_eq!(
        length["predicates"][1]["bodyMinimumPages"],
        json!({"coursework":15,"bachelorQualification":25,"masterQualification":35})
    );
    assert_eq!(length["predicates"][2]["strength"], "usual");
    assert_eq!(
        rule("KNU-MARGINS-001")["predicates"][0]["comparison"],
        "atLeast"
    );
    assert_eq!(
        rule("KNU-PAPER-001")["predicates"][1]["condition"],
        "Necessary for illustrations"
    );
    let typography = rule("KNU-TYPOGRAPHY-001");
    assert_eq!(
        typography["predicates"][0]["decision"],
        "measurement-definition-unresolved"
    );
    assert_eq!(typography["predicates"][1]["strength"], "usual");
    assert_eq!(typography["predicates"][3]["nominalPoints"], 12);
    assert_eq!(
        rule("KNU-REFERENCES-001")["predicates"][0]["strength"],
        "recommended"
    );
    let caption = rule("KNU-CAPTION-RENDERED-001");
    assert_eq!(caption["citation"]["pdfPages"], json!([21, 22, 23, 24, 25]));
    assert_eq!(caption["predicates"][7]["minimumMm"], 8);
    assert!(caption["predicates"][1]["text"]
        .as_str()
        .unwrap()
        .contains("OR"));
    assert!(caption["predicates"][10]["text"]
        .as_str()
        .unwrap()
        .contains("without title"));
    assert!(
        rule("KNU-BIBLIOGRAPHY-RENDERED-001")["predicates"][1]["text"]
            .as_str()
            .unwrap()
            .contains("may be omitted")
    );
    // These checks validate transcription/qualifiers, not physical predicates.
    assert_eq!(caption["supportState"], "unsupported");
}
