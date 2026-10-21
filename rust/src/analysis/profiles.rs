//! Finite offline registry. Guideline bytes are not bundled or fetched.
use super::dto::ProfileRef;
use super::knu_dto::{Citation, EvaluationDomain, ProfileMetadata, SourceRule, WorkType};

pub const KNU_ID: &str = "knu-csc-2025-source";
pub const VERSION: &str = "1.0.0";
pub const LATEST_VERSION: &str = "1.1.0";
pub const MARGIN_FIX_VERSION: &str = "1.2.0";
pub const MARGIN_RULE_VERSION: &str = "1.1.0";
pub const GEOMETRY_PACKAGE_VERSION: &str = "1.3.0";
pub const FORMATTING_VERSION: &str = "1.4.0";
pub const PAPER: &str = "KNU-SRC-PAPER-001";
pub const LINE_SPACING: &str = "KNU-SRC-LINE-SPACING-001";
pub const CAPTION: &str = "KNU-SRC-CAPTION-DOT-001";
pub const GEOMETRY_PACKAGE_RULE_VERSION: &str = "1.2.0";
pub const MARGIN_FIXES: [(&str, &str); 4] = [
    ("KNU-FIX-MARGIN-LEFT-LITERAL-001", "25mm"),
    ("KNU-FIX-MARGIN-RIGHT-LITERAL-001", "10mm"),
    ("KNU-FIX-MARGIN-TOP-LITERAL-001", "20mm"),
    ("KNU-FIX-MARGIN-BOTTOM-LITERAL-001", "20mm"),
];
pub const MARGINS: &str = "KNU-SRC-MARGINS-001";
pub const INDENT: &str = "KNU-SRC-PARINDENT-001";
pub const HEADING: &str = "KNU-SRC-HEADING-DOT-001";
pub const FIX: &str = "KNU-FIX-PARINDENT-LITERAL-001";
pub const SHA256: &str = "8be4d724ac108488e6b1766322e7fd6b3646412aa2b6de12b6945f48fb380714";

#[derive(Debug, PartialEq, Eq)]
pub enum Profile {
    Engine,
    SourceReferences,
    Knu,
    KnuLatest,
    KnuWithMarginFixes,
    KnuWithGeometryPackage,
    KnuWithFormatting,
}

impl Profile {
    pub fn is_knu(&self) -> bool {
        matches!(
            self,
            Self::Knu
                | Self::KnuLatest
                | Self::KnuWithMarginFixes
                | Self::KnuWithGeometryPackage
                | Self::KnuWithFormatting
        )
    }
}

pub fn resolve(p: &ProfileRef) -> Option<Profile> {
    match (p.id.as_str(), p.version.as_str()) {
        ("engine-structural", "0") => Some(Profile::Engine),
        ("engine-source-references", "1.0.0") => Some(Profile::SourceReferences),
        (KNU_ID, VERSION) => Some(Profile::Knu),
        (KNU_ID, LATEST_VERSION) => Some(Profile::KnuLatest),
        (KNU_ID, MARGIN_FIX_VERSION) => Some(Profile::KnuWithMarginFixes),
        (KNU_ID, GEOMETRY_PACKAGE_VERSION) => Some(Profile::KnuWithGeometryPackage),
        (KNU_ID, FORMATTING_VERSION) => Some(Profile::KnuWithFormatting),
        _ => None,
    }
}

pub fn rule(id: &str) -> SourceRule {
    if id == MARGINS {
        return margin_rule();
    }
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

fn margin_rule() -> SourceRule {
    SourceRule {
        rule_id: MARGINS.into(),
        version: VERSION.into(),
        origin: "knu".into(),
        evaluation_domain: EvaluationDomain::SourceObservation,
        citation: Citation { section: "3.1".into(), printed_pages: vec![18], pdf_pages: vec![18] },
        severity: "warning".into(),
        severity_rationale: "Mandatory minimum margins, but literal source intent is not effective TeX state or rendered compliance".into(),
        applicable_work_types: vec![WorkType::Unspecified, WorkType::Coursework, WorkType::BachelorQualification, WorkType::MasterQualification],
        expected: "At least top/bottom 20 mm, left 25 mm, right 10 mm; no upper bound; rendered geometry unverified".into(),
        supported_syntax: "Top-level unstarred geometry with comma-separated left/right/top/bottom=nonnegative decimal mm/cm; ASCII whitespace; <=9 digits and <=3 fractional digits; <=4096 argument bytes".into(),
        limitations: vec!["Finite syntax/context allowlist; no macro expansion or include resolution".into(), "All four explicit sides required; unknown options, duplicate sides and multiple declarations are indeterminate; no precedence inference".into(), "Source observation only; rendered layout unverified; no margin fix".into()],
    }
}

pub fn rule_for(id: &str, version: &str) -> SourceRule {
    if matches!(id, PAPER | LINE_SPACING | CAPTION) {
        return formatting_rule(id);
    }
    // 1.4 retains the exact existing three rule metadata/semantics.
    let version = if version == FORMATTING_VERSION {
        GEOMETRY_PACKAGE_VERSION
    } else {
        version
    };
    let mut result = rule(id);
    if id == MARGINS && matches!(version, MARGIN_FIX_VERSION | GEOMETRY_PACKAGE_VERSION) {
        result.version = MARGIN_RULE_VERSION.into();
        result.limitations[2] = "Source observation only; rendered layout unverified; one eligible literal side may be proposed for explicit review".into();
    }
    if id == MARGINS && version == GEOMETRY_PACKAGE_VERSION {
        result.version = GEOMETRY_PACKAGE_RULE_VERSION.into();
        result.supported_syntax = "Top-level unstarred geometry with four explicit literal side options; or one preamble usepackage{geometry} followed by one geometry declaration; or one usepackage[left/right/top/bottom options]{geometry}. Same nonnegative decimal mm/cm grammar, <=9 digits, <=3 fractional digits and <=4096 argument bytes; immediate lexer-owned optional argument only".into();
        result.limitations[1] = "All four explicit unique sides required; unknown packages/options, dynamic or nested syntax, duplicate imports/declarations and mixed option/command declarations are indeterminate; no precedence inference".into();
    }
    result
}

pub fn metadata_for(version: &str) -> ProfileMetadata {
    let mut result = metadata();
    if matches!(
        version,
        LATEST_VERSION | MARGIN_FIX_VERSION | GEOMETRY_PACKAGE_VERSION | FORMATTING_VERSION
    ) {
        result.profile.version = version.into();
        result.rules.push(rule_for(MARGINS, version));
    }
    if version == FORMATTING_VERSION {
        result
            .rules
            .extend([PAPER, LINE_SPACING, CAPTION].map(formatting_rule));
    }
    result
}

fn formatting_rule(id: &str) -> SourceRule {
    let (section, pages, expected, syntax, limitations) = match id {
        PAPER => ("3.1", vec![18], "Declared A4 intent; A3 requires illustration purpose that source size alone cannot establish",
            "Standard article/report/book immediate documentclass options a4paper/a3paper/a5paper/b5paper/letterpaper/legalpaper/executivepaper; geometry command or geometry package options with those names or paper=a4/a3/a5/b5/letter/legal/executive. Example: documentclass[a4paper]{article}",
            "Missing/dynamic/unknown paper syntax, repeated declarations and custom classes are indeterminate; A3 illustration purpose is never inferred; physical sheet size is unverified"),
        LINE_SPACING => ("3.1", vec![18], "Declared one-and-a-half spacing intent, not measured line/baseline geometry",
            "Top-level unstarred onehalfspacing; setstretch{1.5}; linespread{1.5}; renewcommand{\\baselinestretch}{1.5}. Literal nonnegative decimal scalar, <=9 digits and <=3 fractional digits; no units. Example: usepackage{setspace} followed by onehalfspacing",
            "onehalfspacing expresses conventional setspace intent (requires one plain preamble setspace import); setstretch also requires that import. linespread/baselinestretch declare nominal factors, not equivalent measured geometry. Missing/custom/dynamic/repeated declarations are indeterminate; fonts, glyph heights and effective baselines are not measured"),
        _ => ("3.7; 3.8", vec![21,22,23], "No terminal U+002E in a supported literal figure/table caption title",
            "Unstarred caption{literal title} directly inside one ordinary figure or table in document, without placement arguments. Example: begin{figure} caption{Назва.} end{figure}; exact terminal dot only",
            "Starred captions, optional short titles and float placement arguments are indeterminate; nested/custom environments, dynamic titles, comments inside titles and macro/conditional context are not interpreted. No numbering, prefix, placement, caption presence or rendered layout inference"),
    };
    SourceRule {
        rule_id: id.into(), version: VERSION.into(), origin: "knu".into(),
        evaluation_domain: EvaluationDomain::SourceObservation,
        citation: Citation { section: section.into(), printed_pages: pages.clone(), pdf_pages: pages },
        severity: "warning".into(),
        severity_rationale: "Mandatory guideline category, but declared literal source intent is not effective TeX state or physical PDF compliance".into(),
        applicable_work_types: vec![WorkType::Unspecified, WorkType::Coursework, WorkType::BachelorQualification, WorkType::MasterQualification],
        expected: expected.into(), supported_syntax: syntax.into(),
        limitations: vec![limitations.into(), "Finite bounded syntax and ownership only; unknown commands, conditions and unresolved includes preserve uncertainty. No automatic fix".into()],
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
