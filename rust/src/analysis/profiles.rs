//! Finite offline registry. Guideline bytes are not bundled or fetched.
use super::dto::ProfileRef;
use super::knu_dto::{Citation, EvaluationDomain, ProfileMetadata, SourceRule, WorkType};

pub const KNU_ID: &str = "knu-csc-2025-source";
pub const VERSION: &str = "1.0.0";
pub const INDENT: &str = "KNU-SRC-PARINDENT-001";
pub const HEADING: &str = "KNU-SRC-HEADING-DOT-001";
pub const FIX: &str = "KNU-FIX-PARINDENT-LITERAL-001";
pub const SHA256: &str = "8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714";

#[derive(Debug, PartialEq, Eq)]
pub enum Profile {
    Engine,
    Knu,
}

pub fn resolve(p: &ProfileRef) -> Option<Profile> {
    match (p.id.as_str(), p.version.as_str()) {
        ("engine-structural", "0") => Some(Profile::Engine),
        (KNU_ID, VERSION) => Some(Profile::Knu),
        _ => None,
    }
}

pub fn rule(id: &str) -> SourceRule {
    let indent = id == INDENT;
    SourceRule {
        rule_id: id.into(),
        version: VERSION.into(),
        origin: "knu".into(),
        evaluation_domain: EvaluationDomain::SourceObservation,
        citation: Citation {
            section: if indent { "3.1" } else { "3.2" }.into(),
            printed_pages: if indent { vec![18] } else { vec![18, 19] },
            pdf_pages: if indent { vec![18] } else { vec![18, 19] },
        },
        severity: "warning".into(),
        severity_rationale: "Mandatory requirement, but literal source intent is not effective TeX state or rendered compliance".into(),
        applicable_work_types: vec![WorkType::Unspecified, WorkType::Coursework, WorkType::BachelorQualification, WorkType::MasterQualification],
        expected: if indent { "12.7 mm (1.27 cm); rendered uniformity unverified" } else { "No terminal U+002E in a literal heading title" }.into(),
        supported_syntax: if indent {
            "Top-level unstarred setlength with parindent target and nonnegative decimal mm/cm; <=9 digits and <=3 fractional digits"
        } else {
            "Top-level chapter/section/subsection/subsubsection/paragraph/subparagraph, optional one star, immediate literal short title and literal display title"
        }.into(),
        limitations: vec!["Finite syntax/context allowlist; no macro expansion or include resolution".into(), "Source observation only; rendered layout unverified".into()],
    }
}

pub fn metadata() -> ProfileMetadata {
    ProfileMetadata {
        profile: ProfileRef { id: KNU_ID.into(), version: VERSION.into() },
        guideline_title: "Методичні вказівки з підготовки та оформлення кваліфікаційних та курсових робіт для студентів факультету комп’ютерних наук та кібернетики. Т. О. Карнаух, Л. Л. Омельчук, А. Б. Ставровський. Київ, 2025. 58 с.".into(),
        approved_url: "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/".into(),
        guideline_sha256: SHA256.into(),
        rules: vec![rule(INDENT), rule(HEADING)],
        limitations: vec!["Faculty-specific, not university-wide certification".into(), "PDF page 1 is the unnumbered title; PDF pages 2–58 match printed numbers".into(), "Pass covers observed literals only, never rendered compliance or real-thesis acceptance".into()],
    }
}
