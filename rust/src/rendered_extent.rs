//! Pure usual-extent observation for the KNU Faculty of Computer Science and
//! Cybernetics 2025 guideline, section 2, printed page 11. The caller supplies
//! total PDF pages; this module neither measures a PDF nor proves its revision.
//! Usual non-appendix ranges are contextual, not hard compliance requirements.
//! Appendix/main-body boundaries remain unknown. No fixes or physical metrics.

use crate::analysis::{knu_dto::WorkType, profiles::SHA256};
use serde::{Deserialize, Deserializer, Serialize};

mod sections;

const REQUEST_MAX_BYTES: usize = 1024;
const RESPONSE_MAX_BYTES: usize = 8192;
const INVALID_INPUT: &str = r#"{"schemaVersion":1,"kind":"error","code":"invalidInput"}"#;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    schema_version: u8,
    request_id: String,
    pdf_sha256: String,
    page_count: u16,
    #[serde(deserialize_with = "deserialize_work_type")]
    work_type: WorkType,
    #[serde(default, deserialize_with = "sections::deserialize")]
    sections: Option<sections::Ranges>,
}

// Serde unit enums can also accept externally tagged objects. The shared wire
// requires a string, so constrain the input before reusing the source enum.
fn deserialize_work_type<'de, D>(deserializer: D) -> Result<WorkType, D::Error>
where
    D: Deserializer<'de>,
{
    let value = String::deserialize(deserializer)?;
    match value.as_str() {
        "unspecified" => Ok(WorkType::Unspecified),
        "coursework" => Ok(WorkType::Coursework),
        "bachelorQualification" => Ok(WorkType::BachelorQualification),
        "masterQualification" => Ok(WorkType::MasterQualification),
        _ => Err(serde::de::Error::custom("invalid work type")),
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Observation<'a> {
    schema_version: u8,
    kind: &'static str,
    request_id: &'a str,
    pdf_sha256: &'a str,
    page_count: u16,
    profile_id: &'static str,
    profile_version: &'static str,
    rule_id: &'static str,
    evaluation_domain: &'static str,
    work_type: WorkType,
    status: &'static str,
    reason: &'static str,
    usual_minimum: Option<u16>,
    usual_maximum: Option<u16>,
    non_appendix_pages: Option<u16>,
    main_body_pages: Option<u16>,
    compliance: &'static str,
    guideline_sha256: &'static str,
    source_section: &'static str,
    printed_page: u8,
    source_url: &'static str,
}

fn lowercase_hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'))
}

/// Evaluate only the fixed, bounded wire contract. Invalid requests return one
/// fixed error with no input echo. This function has no clock, IO or state;
/// trusted page-count acquisition and stale-result rejection belong to C#.
pub fn evaluate(request_json: &str) -> String {
    // str::len counts UTF8 bytes. Bound parsing/allocation before touching JSON.
    if request_json.len() > REQUEST_MAX_BYTES {
        return INVALID_INPUT.into();
    }
    // Derived Serde structs can accept positional arrays. Require an object;
    // inspect only JSON whitespace, without rewriting any input or identity.
    let first = request_json
        .bytes()
        .find(|&byte| !matches!(byte, b' ' | b'\t' | b'\r' | b'\n'));
    if first != Some(b'{') {
        return INVALID_INPUT.into();
    }
    let Ok(request) = serde_json::from_str::<Request>(request_json) else {
        return INVALID_INPUT.into();
    };
    if !lowercase_hex(&request.request_id, 32)
        || !lowercase_hex(&request.pdf_sha256, 64)
        || !(1..=512).contains(&request.page_count)
    {
        return INVALID_INPUT.into();
    }
    if request.schema_version == 2 {
        return sections::evaluate(&request, request_json);
    }
    if request.schema_version != 1 || request.sections.is_some() {
        return INVALID_INPUT.into();
    }

    let range = match request.work_type {
        WorkType::Unspecified => None,
        WorkType::Coursework => Some((20, 40)),
        WorkType::BachelorQualification => Some((40, 60)),
        WorkType::MasterQualification => Some((50, 70)),
    };
    let (status, reason) = match range {
        None => ("indeterminate", "workTypeRequired"),
        Some((minimum, _)) if request.page_count < minimum => {
            ("belowUsualMinimum", "totalBelowUsualNonAppendixMinimum")
        }
        Some(_) => ("indeterminate", "nonAppendixBoundaryUnknown"),
    };
    let response = Observation {
        schema_version: 1,
        kind: "observation",
        request_id: &request.request_id,
        pdf_sha256: &request.pdf_sha256,
        page_count: request.page_count,
        profile_id: "knu-csc-2025-rendered-extent",
        profile_version: "1.0.0",
        rule_id: "KNU-CSC-2025-PDF-EXTENT-001",
        evaluation_domain: "renderedObservation",
        work_type: request.work_type,
        status,
        reason,
        usual_minimum: range.map(|(minimum, _)| minimum),
        usual_maximum: range.map(|(_, maximum)| maximum),
        non_appendix_pages: None,
        main_body_pages: None,
        compliance: "notAssessed",
        guideline_sha256: SHA256,
        source_section: "2",
        printed_page: 11,
        source_url: "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/",
    };
    // Only fixed strings, bounded integers and the validated ASCII identities
    // are serialized. No raw input, parse errors or arbitrary messages escape.
    match serde_json::to_string(&response) {
        Ok(json) if json.len() <= RESPONSE_MAX_BYTES => json,
        _ => INVALID_INPUT.into(),
    }
}

#[cfg(test)]
mod tests {
    use crate::evaluate_rendered_extent;
    use serde_json::{json, Value};

    const REQUEST_ID: &str = "0123456789abcdef0123456789abcdef";
    const PDF_HASH: &str = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    // Independent shared-contract examples, not generated by policy constants.
    const GOLDEN_REQUEST: &str = r#"{"schemaVersion":1,"requestId":"0123456789abcdef0123456789abcdef","pdfSha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","pageCount":1,"workType":"bachelorQualification"}"#;
    const GOLDEN_RESPONSE: &str = r#"{
        "schemaVersion":1,"kind":"observation",
        "requestId":"0123456789abcdef0123456789abcdef",
        "pdfSha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "pageCount":1,"profileId":"knu-csc-2025-rendered-extent",
        "profileVersion":"1.0.0","ruleId":"KNU-CSC-2025-PDF-EXTENT-001",
        "evaluationDomain":"renderedObservation","workType":"bachelorQualification",
        "status":"belowUsualMinimum","reason":"totalBelowUsualNonAppendixMinimum",
        "usualMinimum":40,"usualMaximum":60,"nonAppendixPages":null,
        "mainBodyPages":null,"compliance":"notAssessed",
        "guidelineSha256":"8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714",
        "sourceSection":"2","printedPage":11,
        "sourceUrl":"https://csc.knu.ua/uk/filer/canonical/1740300611/2680/"
    }"#;
    const SAFE_ERROR: &str = r#"{"schemaVersion":1,"kind":"error","code":"invalidInput"}"#;

    fn request(work_type: &str, count: u16) -> String {
        json!({
            "schemaVersion": 1,
            "requestId": REQUEST_ID,
            "pdfSha256": PDF_HASH,
            "pageCount": count,
            "workType": work_type
        })
        .to_string()
    }

    fn observe(input: &str) -> Value {
        let output = evaluate_rendered_extent(input);
        assert!(output.len() <= 8192);
        serde_json::from_str(&output).expect("response is JSON")
    }

    fn assert_invalid(input: &str) {
        let output = evaluate_rendered_extent(input);
        assert_eq!(output, SAFE_ERROR);
        assert_eq!(
            serde_json::from_str::<Value>(&output).expect("safe error JSON"),
            json!({"schemaVersion": 1, "kind": "error", "code": "invalidInput"})
        );
    }

    fn expected(
        work_type: &str,
        count: u16,
        range: Option<(u16, u16)>,
        status: &str,
        reason: &str,
    ) -> Value {
        let mut value: Value = serde_json::from_str(GOLDEN_RESPONSE).expect("golden JSON");
        value["workType"] = json!(work_type);
        value["pageCount"] = json!(count);
        value["usualMinimum"] = json!(range.map(|(minimum, _)| minimum));
        value["usualMaximum"] = json!(range.map(|(_, maximum)| maximum));
        value["status"] = json!(status);
        value["reason"] = json!(reason);
        value
    }

    #[test]
    fn independent_golden_matches_export_and_exact_schema() {
        let expected: Value = serde_json::from_str(GOLDEN_RESPONSE).expect("golden JSON");
        assert_eq!(observe(GOLDEN_REQUEST), expected);
        assert_eq!(expected.as_object().expect("object").len(), 21);
        // Exact semantic equality also forbids pass/fail, edit, sourceSpan,
        // physical fields or skipped-null coverage in the exported response.
    }

    #[test]
    fn known_work_types_cover_each_usual_range_boundary() {
        for (work_type, minimum, maximum) in [
            ("coursework", 20, 40),
            ("bachelorQualification", 40, 60),
            ("masterQualification", 50, 70),
        ] {
            for count in [1, minimum - 1, minimum, maximum, maximum + 1, 512] {
                let (status, reason) = if count < minimum {
                    ("belowUsualMinimum", "totalBelowUsualNonAppendixMinimum")
                } else {
                    ("indeterminate", "nonAppendixBoundaryUnknown")
                };
                assert_eq!(
                    observe(&request(work_type, count)),
                    expected(work_type, count, Some((minimum, maximum)), status, reason)
                );
            }
        }
    }

    #[test]
    fn unspecified_requires_work_type_for_every_valid_count() {
        for count in 1..=512 {
            assert_eq!(
                observe(&request("unspecified", count)),
                expected(
                    "unspecified",
                    count,
                    None,
                    "indeterminate",
                    "workTypeRequired"
                )
            );
        }
    }

    #[test]
    fn validated_identity_echo_and_repeated_results_are_exact() {
        let id = "f".repeat(32);
        let hash = "a1".repeat(32);
        let input = request("masterQualification", 512)
            .replace(PDF_HASH, &hash)
            .replace(REQUEST_ID, &id);
        let first = evaluate_rendered_extent(&input);
        let result = observe(&input);
        assert_eq!(result["requestId"], id);
        assert_eq!(result["pdfSha256"], hash);
        assert_eq!(result["pageCount"], 512);
        assert_eq!(result["workType"], "masterQualification");
        for _ in 0..8 {
            assert_eq!(evaluate_rendered_extent(&input), first);
        }
    }

    #[test]
    fn page_count_rejects_non_integer_out_of_range_and_wrong_types() {
        let base = request("coursework", 1);
        for token in [
            "0",
            "513",
            "-1",
            "-0",
            "1.0",
            "1.5",
            "1e0",
            "1E0",
            "1e2",
            "1e9999",
            "65536",
            "18446744073709551616",
            "null",
            "true",
            "false",
            "\"1\"",
            "[]",
            "{}",
            "+1",
            "01",
            "NaN",
            "Infinity",
        ] {
            assert_invalid(&base.replace("\"pageCount\":1", &format!("\"pageCount\":{token}")));
        }
    }

    #[test]
    fn schema_version_requires_integer_one() {
        let base = request("coursework", 1);
        for token in [
            "0",
            "2",
            "-1",
            "256",
            "18446744073709551616",
            "1.0",
            "1e0",
            "null",
            "true",
            "\"1\"",
            "[]",
            "{}",
        ] {
            assert_invalid(
                &base.replace("\"schemaVersion\":1", &format!("\"schemaVersion\":{token}")),
            );
        }
    }

    #[test]
    fn every_field_is_required_non_null_and_unknown_fields_are_rejected() {
        let base: Value = serde_json::from_str(GOLDEN_REQUEST).expect("request JSON");
        for key in [
            "schemaVersion",
            "requestId",
            "pdfSha256",
            "pageCount",
            "workType",
        ] {
            let mut missing = base.clone();
            missing.as_object_mut().expect("object").remove(key);
            assert_invalid(&missing.to_string());
            let mut null = base.clone();
            null[key] = Value::Null;
            assert_invalid(&null.to_string());
        }
        for key in [
            "extra",
            "SchemaVersion",
            "bodyPages",
            "nonAppendixPages",
            "policy",
        ] {
            let mut extra = base.clone();
            extra[key] = json!("DO_NOT_ECHO_INPUT");
            assert_invalid(&extra.to_string());
        }
    }

    #[test]
    fn duplicate_keys_are_rejected_even_when_values_agree() {
        let base: Value = serde_json::from_str(GOLDEN_REQUEST).expect("request JSON");
        for (key, value) in base.as_object().expect("object") {
            let duplicate = format!("{{\"{key}\":{value},{}", &GOLDEN_REQUEST[1..]);
            assert_invalid(&duplicate);
        }
        let escaped_duplicate = format!(
            "{{\"request\\u0049d\":\"{REQUEST_ID}\",{}",
            &GOLDEN_REQUEST[1..]
        );
        assert_invalid(&escaped_duplicate);
    }

    #[test]
    fn identities_require_exact_lowercase_ascii_hex_without_normalization() {
        for (field, length) in [("requestId", 32), ("pdfSha256", 64)] {
            for bad in [
                String::new(),
                "a".repeat(length - 1),
                "a".repeat(length + 1),
                "A".repeat(length),
                "g".repeat(length),
                "і".repeat(length),
                "0".repeat(length - 1) + " ",
                " ".to_owned() + &"a".repeat(length),
                "a".repeat(length) + "\n",
                "０".repeat(length),
            ] {
                let mut input: Value = serde_json::from_str(GOLDEN_REQUEST).expect("request JSON");
                input[field] = json!(bad);
                assert_invalid(&input.to_string());
            }
            for bad in [json!(1), json!(true), json!([]), json!({})] {
                let mut input: Value = serde_json::from_str(GOLDEN_REQUEST).expect("request JSON");
                input[field] = bad;
                assert_invalid(&input.to_string());
            }
        }
    }

    #[test]
    fn work_type_is_only_one_of_four_exact_strings_not_a_tagged_enum_object() {
        for bad in [
            json!("Coursework"),
            json!("coursework "),
            json!(" coursework"),
            json!("bachelorqualification"),
            json!("master"),
            json!(""),
            json!(1),
            json!(true),
            json!([]),
            json!({"coursework": null}),
            json!({"unspecified": null}),
            json!({"bachelorQualification": null}),
            json!({"masterQualification": null}),
        ] {
            let mut input: Value = serde_json::from_str(GOLDEN_REQUEST).expect("request JSON");
            input["workType"] = bad;
            assert_invalid(&input.to_string());
        }
    }

    #[test]
    fn malformed_deep_and_unpaired_unicode_have_only_fixed_safe_errors() {
        for bad in ["", "null", "[]", "true", "{", "{\"", "{}", "\u{feff}{}"] {
            assert_invalid(bad);
        }
        // Serde's positional struct representation must not bypass exact keys.
        assert_invalid(&json!([1, REQUEST_ID, PDF_HASH, 1, "coursework"]).to_string());
        assert_invalid(&format!("{GOLDEN_REQUEST} {{}}"));
        assert_invalid(&format!("{GOLDEN_REQUEST} trailing"));
        assert_invalid(&GOLDEN_REQUEST.replace(REQUEST_ID, r"\ud800"));
        assert_invalid(&GOLDEN_REQUEST.replace(PDF_HASH, r"\udc00"));
        assert_invalid(&GOLDEN_REQUEST.replace("bachelorQualification", r"\ud800"));
        assert_invalid(&format!("{}0{}", "[".repeat(160), "]".repeat(160)));
        let deep = format!("{}0{}", "[".repeat(160), "]".repeat(160));
        assert_invalid(&GOLDEN_REQUEST.replace("\"bachelorQualification\"", &deep));
        assert_invalid(&GOLDEN_REQUEST.replace("\"pageCount\":1", "\"pageCount\":1,"));
    }

    #[test]
    fn byte_preflight_includes_utf8_and_accepts_exact_limit_with_json_whitespace() {
        let padding = " ".repeat(1024 - GOLDEN_REQUEST.len());
        let exact = format!("{GOLDEN_REQUEST}{padding}");
        assert_eq!(exact.len(), 1024);
        assert_eq!(observe(&exact), observe(GOLDEN_REQUEST));
        assert_eq!(
            observe(&format!(" \t\r\n{GOLDEN_REQUEST}")),
            observe(GOLDEN_REQUEST)
        );
        assert_invalid(&(exact + " "));
        let unicode = format!("{{\"DO_NOT_ECHO_INPUT\":\"{}\"}}", "і".repeat(512));
        assert!(unicode.chars().count() < 1024);
        assert!(unicode.len() > 1024);
        assert_invalid(&unicode);
        assert_invalid(&"x".repeat(8193));
    }
}
