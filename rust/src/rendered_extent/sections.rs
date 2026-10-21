//! Author-labelled page ranges, never automatic document-structure detection.
//! PDF page count and revision authority are supplied by the existing C# gate.
use super::{Observation, Request, INVALID_INPUT, RESPONSE_MAX_BYTES};
use crate::analysis::{knu_dto::WorkType, profiles::SHA256};
use serde::{Deserialize, Deserializer, Serialize};

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct PageRange {
    start: u16,
    end: u16,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct Ranges {
    introduction: Option<PageRange>,
    main_body: Option<PageRange>,
    conclusions: Option<PageRange>,
    appendix_start_page: Option<u16>,
    no_appendices: bool,
}

pub(super) fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<Ranges>, D::Error> {
    Ranges::deserialize(d).map(Some)
}

impl Ranges {
    fn valid(&self, pages: u16) -> bool {
        if self.no_appendices && self.appendix_start_page.is_some()
            || self.appendix_start_page.is_some_and(|p| p < 1 || p > pages)
        {
            return false;
        }
        let mut previous = 0;
        for range in [&self.introduction, &self.main_body, &self.conclusions]
            .into_iter()
            .flatten()
        {
            if range.start < 1
                || range.start <= previous
                || range.end < range.start
                || range.end > pages
                || self.appendix_start_page.is_some_and(|p| range.end >= p)
            {
                return false;
            }
            previous = range.end;
        }
        true
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Section {
    rule_id: &'static str,
    pages: Option<u16>,
    minimum: Option<u16>,
    maximum: Option<u16>,
    status: &'static str,
}

fn section(
    id: &'static str,
    range: &Option<PageRange>,
    min: Option<u16>,
    max: Option<u16>,
) -> Section {
    let pages = range.as_ref().map(|r| r.end - r.start + 1);
    let status = match pages {
        None => "indeterminate",
        Some(_) if min.is_none() && max.is_none() => "indeterminate",
        Some(n) if min.is_some_and(|m| n < m) => "belowMinimum",
        Some(n) if max.is_some_and(|m| n > m) => "aboveMaximum",
        Some(_) => "withinLimit",
    };
    Section {
        rule_id: id,
        pages,
        minimum: min,
        maximum: max,
        status,
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report<'a> {
    labels_source: &'static str,
    ranges: &'a Ranges,
    introduction: Section,
    main_body: Section,
    conclusions: Section,
}

#[derive(Serialize)]
struct Response<'a> {
    #[serde(flatten)]
    extent: Observation<'a>,
    sections: Report<'a>,
}

pub(super) fn evaluate(request: &Request, json: &str) -> String {
    let Some(ranges) = &request.sections else {
        return INVALID_INPUT.into();
    };
    // Derived structs also accept positional arrays. This second bounded shape
    // check requires named objects and every nullable field to be explicit.
    // Duplicate keys were already rejected by the derived structs above.
    let Ok(value) = serde_json::from_str::<serde_json::Value>(json) else {
        return INVALID_INPUT.into();
    };
    let Some(object) = value.get("sections").and_then(|v| v.as_object()) else {
        return INVALID_INPUT.into();
    };
    if object.len() != 5
        || [
            "introduction",
            "mainBody",
            "conclusions",
            "appendixStartPage",
            "noAppendices",
        ]
        .iter()
        .any(|k| !object.contains_key(*k))
        || ["introduction", "mainBody", "conclusions"].iter().any(|k| {
            let v = &object[*k];
            !v.is_null()
                && !v.as_object().is_some_and(|o| {
                    o.len() == 2 && o.contains_key("start") && o.contains_key("end")
                })
        })
        || !ranges.valid(request.page_count)
    {
        return INVALID_INPUT.into();
    }
    let band = match request.work_type {
        WorkType::Unspecified => None,
        WorkType::Coursework => Some((20, 40, 15)),
        WorkType::BachelorQualification => Some((40, 60, 25)),
        WorkType::MasterQualification => Some((50, 70, 35)),
    };
    let non_appendix = ranges
        .appendix_start_page
        .map(|p| p - 1)
        .or_else(|| ranges.no_appendices.then_some(request.page_count));
    let (status, reason) = match (band, non_appendix) {
        (None, _) => ("indeterminate", "workTypeRequired"),
        (_, None) => ("indeterminate", "nonAppendixBoundaryUnknown"),
        (Some((min, _, _)), Some(n)) if n < min => (
            "belowUsualMinimum",
            "classifiedNonAppendixBelowUsualMinimum",
        ),
        (Some((_, max, _)), Some(n)) if n > max => (
            "aboveUsualMaximum",
            "classifiedNonAppendixAboveUsualMaximum",
        ),
        _ => ("withinUsualRange", "classifiedNonAppendixWithinUsualRange"),
    };
    let response = Response {
        extent: Observation {
            schema_version: 2,
            kind: "observation",
            request_id: &request.request_id,
            pdf_sha256: &request.pdf_sha256,
            page_count: request.page_count,
            profile_id: "knu-csc-2025-rendered-extent",
            profile_version: "2.0.0",
            rule_id: "KNU-CSC-2025-PDF-EXTENT-001",
            evaluation_domain: "renderedObservation",
            work_type: request.work_type,
            status,
            reason,
            usual_minimum: band.map(|b| b.0),
            usual_maximum: band.map(|b| b.1),
            non_appendix_pages: non_appendix,
            main_body_pages: ranges.main_body.as_ref().map(|r| r.end - r.start + 1),
            compliance: "notAssessed",
            guideline_sha256: SHA256,
            source_section: "2",
            printed_page: 11,
            source_url: "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/",
        },
        sections: Report {
            labels_source: "userProvidedPageRanges",
            ranges,
            introduction: section(
                "KNU-CSC-2025-PDF-INTRODUCTION-001",
                &ranges.introduction,
                None,
                Some(3),
            ),
            main_body: section(
                "KNU-CSC-2025-PDF-BODY-001",
                &ranges.main_body,
                band.map(|b| b.2),
                None,
            ),
            conclusions: section(
                "KNU-CSC-2025-PDF-CONCLUSIONS-001",
                &ranges.conclusions,
                None,
                Some(5),
            ),
        },
    };
    match serde_json::to_string(&response) {
        Ok(json) if json.len() <= RESPONSE_MAX_BYTES => json,
        _ => INVALID_INPUT.into(),
    }
}

#[cfg(test)]
mod tests {
    use crate::evaluate_rendered_extent;
    use serde_json::{json, Value};
    fn input(work: &str) -> Value {
        json!({"schemaVersion":2,"requestId":"a".repeat(32),"pdfSha256":"b".repeat(64),"pageCount":80,"workType":work,
            "sections":{"introduction":{"start":3,"end":5},"mainBody":{"start":6,"end":40},
                "conclusions":{"start":41,"end":45},"appendixStartPage":61,"noAppendices":false}})
    }
    fn run(v: &Value) -> Value {
        serde_json::from_str(&evaluate_rendered_extent(&v.to_string())).unwrap()
    }
    #[test]
    fn work_type_and_actual_labelled_ranges_select_distinct_limits_without_compliance_claim() {
        for (work, min, usual_max) in [
            ("coursework", 15, 40),
            ("bachelorQualification", 25, 60),
            ("masterQualification", 35, 70),
        ] {
            let v = run(&input(work));
            assert_eq!(v["sections"]["mainBody"]["minimum"], min);
            assert_eq!(v["sections"]["mainBody"]["pages"], 35);
            assert_eq!(v["sections"]["mainBody"]["status"], "withinLimit");
            assert_eq!(v["sections"]["introduction"]["maximum"], 3);
            assert_eq!(v["sections"]["conclusions"]["maximum"], 5);
            assert_eq!(v["usualMaximum"], usual_max);
            assert_eq!(v["nonAppendixPages"], 60);
            assert_eq!(v["compliance"], "notAssessed");
            assert_eq!(v["sections"]["labelsSource"], "userProvidedPageRanges");
        }
    }
    #[test]
    fn each_boundary_and_unknown_labels_remain_honest() {
        let mut q = input("masterQualification");
        q["sections"]["introduction"]["end"] = json!(6);
        q["sections"]["mainBody"]["start"] = json!(7);
        q["sections"]["conclusions"]["end"] = json!(46);
        let v = run(&q);
        assert_eq!(v["sections"]["introduction"]["status"], "aboveMaximum");
        assert_eq!(v["sections"]["mainBody"]["status"], "belowMinimum");
        assert_eq!(v["sections"]["conclusions"]["status"], "aboveMaximum");
        q["sections"]["introduction"] = Value::Null;
        q["sections"]["appendixStartPage"] = Value::Null;
        assert_eq!(run(&q)["status"], "indeterminate");
        assert_eq!(
            run(&q)["sections"]["introduction"]["status"],
            "indeterminate"
        );
        q["sections"]["noAppendices"] = json!(true);
        assert_eq!(run(&q)["nonAppendixPages"], 80);
        q["workType"] = json!("unspecified");
        assert_eq!(run(&q)["sections"]["mainBody"]["status"], "indeterminate");
    }
    #[test]
    fn contradictory_outside_missing_extra_array_null_and_duplicate_shapes_are_rejected() {
        for fault in 0..8 {
            let mut q = input("coursework");
            match fault {
                0 => q["sections"]["noAppendices"] = json!(true),
                1 => q["sections"]["mainBody"]["start"] = json!(5),
                2 => q["sections"]["conclusions"]["end"] = json!(81),
                3 => {
                    q["sections"]
                        .as_object_mut()
                        .unwrap()
                        .remove("introduction");
                }
                4 => q["sections"]["extra"] = json!(true),
                5 => q["sections"]["introduction"] = json!([3, 5]),
                6 => q["sections"] = Value::Null,
                _ => q["schemaVersion"] = json!(1),
            }
            assert_eq!(run(&q)["kind"], "error");
        }
        let q = input("coursework").to_string().replace(
            "\"noAppendices\":false",
            "\"noAppendices\":false,\"noAppendices\":false",
        );
        assert_eq!(
            serde_json::from_str::<Value>(&evaluate_rendered_extent(&q)).unwrap()["kind"],
            "error"
        );
    }
    #[test]
    fn actual_section_fixture_export_is_explicit_fresh_and_bounded() {
        let mut cases = vec![];
        for (name, work) in [
            ("coursework", "coursework"),
            ("bachelor", "bachelorQualification"),
            ("master", "masterQualification"),
            ("unspecified", "unspecified"),
        ] {
            cases.push((name, input(work)));
        }
        let mut exceeded = input("masterQualification");
        exceeded["sections"]["introduction"]["end"] = json!(6);
        exceeded["sections"]["mainBody"]["start"] = json!(7);
        exceeded["sections"]["conclusions"]["end"] = json!(46);
        cases.push(("limits", exceeded));
        let mut unknown = input("bachelorQualification");
        unknown["sections"]["appendixStartPage"] = Value::Null;
        unknown["sections"]["introduction"] = Value::Null;
        cases.push(("unknown", unknown));
        let mut none = input("coursework");
        none["sections"]["appendixStartPage"] = Value::Null;
        none["sections"]["noAppendices"] = json!(true);
        cases.push(("no-appendices", none));
        let data:Vec<_>=cases.into_iter().map(|(name,q)|{
            let response=run(&q);assert_eq!(response["kind"],"observation");
            let bytes=serde_json::to_vec_pretty(&json!({"request":q,"response":response,"scope":"synthetic counts/author labels through actual Rust export, not PDF inspection"})).unwrap();
            assert!(bytes.len()<16384);(name,bytes)
        }).collect();
        if let Some(dir) = std::env::var_os("RENDERED_SECTION_FIXTURE_DIR") {
            use std::io::Write;
            let path = std::path::PathBuf::from(dir);
            assert!(path.is_dir());
            assert_eq!(std::fs::read_dir(&path).unwrap().count(), 0);
            for (name, bytes) in data {
                let mut file = std::fs::OpenOptions::new()
                    .create_new(true)
                    .write(true)
                    .open(path.join(format!("{name}.json")))
                    .unwrap();
                file.write_all(&bytes).unwrap();
            }
        }
    }
}
