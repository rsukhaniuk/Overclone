//! Transcription of the approved faculty-specific 2025 source. Numeric
//! predicates are kept here in Rust, never duplicated in C#/worker glue.
use serde::Serialize;
pub const GUIDELINE_SHA256: &str =
    "8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714";
pub const SOURCE_URL: &str = "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/";
pub const A4: [f64; 2] = [210.0, 297.0];
pub const A3: [f64; 2] = [297.0, 420.0];
pub const MARGINS_MM: [f64; 4] = [25.0, 20.0, 10.0, 20.0];
pub const PARAGRAPH_INDENT_MM: f64 = 12.7;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Citation {
    pub section: &'static str,
    pub printed_pages: &'static [u8],
    pub pdf_pages: &'static [u8],
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Descriptor {
    pub rule_id: &'static str,
    pub rule_version: &'static str,
    pub domain: &'static str,
    pub citation: Citation,
    pub guideline_sha256: &'static str,
    pub requirement_strength: &'static str,
    pub applicable_work_types: &'static [&'static str],
    pub preconditions: &'static [&'static str],
    pub required_capabilities: &'static [&'static str],
    pub supported_syntax: &'static [&'static str],
    pub limitations: &'static [&'static str],
    pub examples: &'static [&'static str],
    pub fix_policy: &'static str,
    pub support_state: &'static str,
}
const ALL: &[&str] = &["coursework", "bachelorQualification", "masterQualification"];
macro_rules! rule {
    ($id:expr,$s:expr,$p:expr,$strength:expr,$cap:expr,$limits:expr,$example:expr) => {
        Descriptor {
            rule_id: $id,
            rule_version: "1.0.0",
            domain: "renderedObservation",
            citation: Citation {
                section: $s,
                printed_pages: $p,
                pdf_pages: $p,
            },
            guideline_sha256: GUIDELINE_SHA256,
            requirement_strength: $strength,
            applicable_work_types: ALL,
            preconditions: &[
                "current-compiled-authority",
                "matching-pdf-and-page-facts",
                "confirmed-required-role",
            ],
            required_capabilities: $cap,
            supported_syntax: &[
                "layout-facts-schema1",
                "layout-facts-schema2",
                "explicit-role-annotations-schema1",
            ],
            limitations: $limits,
            examples: $example,
            fix_policy: "none",
            support_state: "partial",
        }
    };
}
pub static RULES:&[Descriptor]=&[
 rule!("KNU-PAPER-001","3.1",&[18],"conditional",&["physicalScale"],&["single-sided-printing-not-observable"],&["A4 210x297 mm; A3 297x420 mm only when needed for illustrations"]),
 rule!("KNU-MARGINS-001","3.1",&[18],"mandatory",&["physicalScale","glyphInk","confirmedBody"],&["headers-and-illustrations-need-separate-roles"],&["left30 mm satisfies minimum25 mm; right9 mm is below minimum10 mm"]),
 rule!("KNU-TYPOGRAPHY-001","3.1;3.10;3.11",&[18,26,27],"conditional",&["glyphInk","textStyle","confirmedBody","baselines"],&["measurement-definition-unresolved","usual-font-is-advisory","notes-have-separate-policy"],&["1.8 mm minimum font height is not nominal14 pt; family variation alone does not fail"]),
 rule!("KNU-PARAGRAPH-RENDERED-001","3.1",&[18],"mandatory",&["physicalScale","glyphInk","confirmedParagraphs"],&["all-body-paragraphs-required-for-uniformity"],&["confirmed paragraph indent12.7 mm; first line of a continuation is not a paragraph start"]),
 rule!("KNU-HEADINGS-RENDERED-001","3.2;3.4",&[18,19,20],"conditional",&["nativeCharacterUnicode","explicitRoleScope"],&["subordinate-page-start-exception","source-case-is-not-rendered-case","extracted-text-not-visual-glyph-proof","visual-style-and-text-alignment-unestablished"],&["major heading bold uppercase centered; internal punctuation is allowed"]),
 rule!("KNU-PAGINATION-001","3.3",&[19],"conditional",&["nativeCharacterUnicode","explicitRoleScope"],&["empty-text-does-not-prove-number-absence","extracted-text-not-visual-glyph-proof","document-numbering-and-role-unestablished"],&["title counts as1 and displays no page number; later top-right Arabic numbers continue through appendices"]),
 rule!("KNU-STRUCTURE-001","2;2.1-2.9",&[11,12,13,14,15,16,17],"conditional",&["confirmedStructure","literalSourceNativeMap","workType"],&["semantic-content-needs-human-review","qualification-title-extra-signatures"],&["abbreviations and appendices are conditional, not mandatory in every work"]),
 rule!("KNU-ABSTRACT-001","2.2",&[12,13],"conditional",&["confirmedAbstract","readingOrder","renderedCase"],&["usual-keyword-count-advisory","semantic-content-needs-human-review"],&["usual5-15 uppercase alphabetized keywords; an inapplicable description aspect may be omitted"]),
 rule!("KNU-LENGTH-001","2",&[11],"conditional",&["renderedExtentEvidence","confirmedSectionRanges","workType"],&["delegates-existing-rendered-extent"],&["body minima15/25/35; usual totals20-40/40-60/50-70 excluding appendices; introduction<=3 and conclusions<=5"]),
 rule!("KNU-REFERENCES-001","2.8;3.15;3.16;appendixD",&[16,17,30,31,32,50,51,52,53,54],"conditional",&["literalCitationOrder","compiledBibliography","confirmedReferences"],&["end-text-placement-recommended","no-external-dstu-certification"],&["[3;6;15] and consecutive ranges are allowed; appendix-only citations use a separate list"]),
 rule!("KNU-APPENDIX-001","2.9;3.16",&[17,31,32],"conditional",&["confirmedAppendices","literalCitationOrder","confirmedPageNumbers"],&["optional-appendix-absence-not-failure","copied-document-cover-exception"],&["Ukrainian letters exceptҐ Є З І Ї Й О Ч Ь; do not normalize Latin A to Cyrillic А"]),
 rule!("KNU-CAPTION-RENDERED-001","3.7;3.8",&[21,22,23],"conditional",&["nativeCharacterUnicode","explicitRoleScope"],&["continued-figure-caption-exception","semantic-caption-content-needs-human-review","illustration-geometry-unavailable","extracted-text-not-visual-glyph-proof"],&["table caption paragraph-indented above table, no final dot, wrapped line under Таблиця; continuation figure label includes (продовження)"]),
 rule!("KNU-BIBLIOGRAPHY-RENDERED-001","2.8;3.15;3.16;appendixD",&[16,17,30,31,32,50,51,52,53,54],"conditional",&["compiledBibliography","literalCitationOrder","confirmedReferences"],&["delegates-reference-predicates"],&["shared reference ordering and syntax predicates; no duplicate independent bibliography compliance result"]),
];
pub fn json() -> String {
    serde_json::to_string(&serde_json::json!({"schemaVersion":1,"profileId":"knu-csc-2025-rendered-review","profileVersion":"1.0.0","guidelineSha256":GUIDELINE_SHA256,"sourceUrl":SOURCE_URL,"standardsComplete":false,"rules":RULES})).unwrap()
}
