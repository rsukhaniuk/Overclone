//! Bundled source-backed support catalog; no source document, IO or PDF facts.
//! Unsupported required cells stay open. Source observations are not compliance.
pub const VERSION: &str = "1.1.0";
pub const SOURCE_PROFILE_VERSION: &str = "1.6.0";
pub const JSON: &str = include_str!("thesis_catalog/catalog.json");
pub(crate) const CAPTION_ROLES_JSON: &str = include_str!("thesis_catalog/catalog_v1_7.json");

#[cfg(test)]
mod tests {
    #[test]
    fn synthetic_cited_qualifier_vectors_preserve_source_interpretation() {
        let catalog: serde_json::Value = serde_json::from_str(super::JSON).unwrap();
        let vectors: serde_json::Value = serde_json::from_str(include_str!(
            "../tests/fixtures/r2/catalog/cited-qualifiers.json"
        ))
        .unwrap();
        for case in vectors["cases"].as_array().unwrap() {
            let rule = catalog["rules"]
                .as_array()
                .unwrap()
                .iter()
                .find(|r| r["ruleId"] == case["ruleId"])
                .unwrap();
            assert_eq!(
                rule.pointer(case["pointer"].as_str().unwrap()).unwrap(),
                &case["expected"],
                "{}: {}",
                case["ruleId"],
                case["qualifier"]
            );
        }
    }

    #[test]
    fn catalog_is_cited_complete_metadata_not_fake_rendered_support() {
        let v: serde_json::Value = serde_json::from_str(super::JSON).unwrap();
        let mut ids = std::collections::HashSet::new();
        for r in v["rules"].as_array().unwrap() {
            for field in [
                "ruleId",
                "ruleVersion",
                "domain",
                "citation",
                "guidelineSha256",
                "requirementStrength",
                "applicableWorkTypes",
                "preconditions",
                "requiredCapabilities",
                "supportedSyntax",
                "limitations",
                "examples",
                "fixPolicy",
                "supportState",
                "predicates",
            ] {
                assert!(!r[field].is_null(), "{field}: {}", r["ruleId"]);
            }
            assert!(ids.insert(r["ruleId"].as_str().unwrap()));
            assert_eq!(r["guidelineSha256"], crate::analysis::profiles::SHA256);
            assert_eq!(r["citation"]["printedPages"], r["citation"]["pdfPages"]);
            if r["domain"] == "renderedObservation" && r["ruleId"] != "KNU-LENGTH-001" {
                assert_eq!(r["supportState"], "unsupported");
            }
        }
        assert!(ids.contains("KNU-CAPTION-RENDERED-001"));
        assert!(ids.contains("KNU-BIBLIOGRAPHY-RENDERED-001"));
        assert_eq!(v["globalCoverage"], "incomplete");
    }
}
