//! Synthetic fixtures only. Public-boundary and independent-byte expectations.
//! Most requests use a shape-valid UNVERIFIED hash to exercise proposal gates;
//! F1 byte-preservation fixtures below supply independently computed real hashes.
use super::dto::{AnalysisResult, Outcome};
use super::knu_dto::{Length, Limitation, ObservationValue, TitleKind};
use super::profiles::{HEADING, INDENT, KNU_ID, SHA256, VERSION};
use super::{analyze_source, initialize, observations, rules};
use serde_json::{json, Value};

fn request(files: &[(&str, &[u8])]) -> (Value, Vec<u8>) {
    let mut packed = Vec::new();
    let mut descriptors = Vec::new();
    for (n, (path, bytes)) in files.iter().enumerate() {
        descriptors.push(json!({"fileId":format!("f{n}"),"relativePath":path,"mediaKind":"text",
            "hasBom":bytes.starts_with(b"\xef\xbb\xbf"),"sha256":"0000000000000000000000000000000000000000000000000000000000000000","byteOffset":packed.len(),"byteLength":bytes.len()}));
        packed.extend_from_slice(bytes);
    }
    (
        json!({"protocolVersion":1,"requestId":"knu-request","operation":"analyzeSource","payload":{
        "snapshotId":"snapshot","projectId":"project","sessionId":"session","workingGeneration":7,
        "baseHeadRevisionId":"head","profile":{"id":KNU_ID,"version":VERSION},
        "configuration":{"workType":"unspecified"},"limits":{},"files":descriptors}}),
        packed,
    )
}
fn wire(req: &Value, packed: &[u8]) -> Value {
    serde_json::from_str(&analyze_source(&req.to_string(), packed)).expect("canonical JSON")
}
fn completed(req: &Value, packed: &[u8]) -> AnalysisResult {
    let out = wire(req, packed);
    assert_eq!(out["status"], "completed", "{out}");
    let result: AnalysisResult = serde_json::from_value(out["payload"].clone()).unwrap();
    assert_eq!(result.rule_results.len(), 3);
    assert_eq!(result.unavailable_capabilities, ["pdfFacts", "pdfChecks"]);
    for (f, source) in result
        .facts
        .files
        .iter()
        .zip(req["payload"]["files"].as_array().unwrap())
    {
        assert_eq!(f.token_count as usize, f.tokens.len());
        let off = source["byteOffset"].as_u64().unwrap() as usize;
        if source["mediaKind"] == "text" {
            let text = std::str::from_utf8(&packed[off..off + f.byte_length as usize]).unwrap();
            for t in &f.tokens {
                assert!(text.is_char_boundary(t.start_byte as usize));
                assert!(text.is_char_boundary(t.end_byte as usize));
            }
            let k = result
                .knu
                .as_ref()
                .unwrap()
                .files
                .iter()
                .find(|k| k.file_id == f.file_id)
                .unwrap();
            assert_eq!(k.observation_count as usize, k.observations.len());
            for o in &k.observations {
                for s in [&o.command_span, &o.declaration_span, &o.value_span] {
                    assert!(s.start_byte <= s.end_byte);
                    assert!(text.is_char_boundary(s.start_byte as usize));
                    assert!(text.is_char_boundary(s.end_byte as usize));
                }
                assert!(o.excerpt.len() <= observations::MAX_EXCERPT_BYTES);
            }
        }
    }
    // Every rule reference resolves to an ACTUALLY stored file/rule diagnostic.
    for r in &result.rule_results {
        for f in &r.per_file {
            for id in &f.diagnostic_ids {
                assert!(result
                    .diagnostics
                    .iter()
                    .any(|d| &d.id == id && d.file_id == f.file_id && d.rule_id == r.rule_id));
            }
        }
    }
    result
}
fn one(src: &str) -> AnalysisResult {
    let (r, b) = request(&[("main.tex", src.as_bytes())]);
    completed(&r, &b)
}
fn outcome(r: &AnalysisResult, id: &str) -> Outcome {
    r.rule_results
        .iter()
        .find(|r| r.rule_id == id)
        .unwrap()
        .outcome
}
fn assert_no_fix(r: &AnalysisResult) {
    assert!(r.knu.as_ref().unwrap().fix_descriptors.is_empty());
}
fn error(req: &Value, bytes: &[u8], code: &str) {
    let r = wire(req, bytes);
    assert_eq!(r["protocolVersion"], 1);
    assert_eq!(r["requestId"], "knu-request");
    assert_eq!(r["status"], "failed");
    assert_eq!(r["error"]["code"], code, "{r}");
    assert!(r.get("payload").is_none());
    assert!(r.to_string().len() < 1024);
}

#[test]
fn registry_configuration_and_trusted_request_id_are_strict() {
    let (base, bytes) = request(&[]);
    for (id, version) in [
        ("other", VERSION),
        (KNU_ID, "0"),
        ("engine-structural", "1"),
        ("engine-structural", VERSION),
    ] {
        let mut r = base.clone();
        r["payload"]["profile"] = json!({"id":id,"version":version});
        error(&r, &bytes, "UnsupportedCapability");
    }
    for cfg in [
        json!(null),
        json!({}),
        json!(false),
        json!(1),
        json!([]),
        json!({"workType":"bachelor"}),
        json!({"workType":0}),
        json!({"workType":"unspecified","extra":true}),
        json!({"workType":null}),
    ] {
        let mut r = base.clone();
        r["payload"]["configuration"] = cfg;
        error(&r, &bytes, "InvalidInput");
    }
    let mut r = base.clone();
    r["payload"]
        .as_object_mut()
        .unwrap()
        .remove("configuration");
    error(&r, &bytes, "InvalidInput");
    let raw = base.to_string().replace(
        "\"workType\":\"unspecified\"",
        "\"workType\":\"unspecified\",\"workType\":\"coursework\"",
    );
    let out: Value = serde_json::from_str(&analyze_source(&raw, &bytes)).unwrap();
    assert_eq!(out["error"]["code"], "InvalidInput");
    assert_eq!(out["requestId"], "knu-request");
}

#[test]
fn metadata_citations_origins_domain_and_all_work_types_round_trip() {
    for work in [
        "unspecified",
        "coursework",
        "bachelorQualification",
        "masterQualification",
    ] {
        let (mut req, b) = request(&[(
            "main.tex",
            r"\setlength{\parindent}{1.27cm}\section{Методи}".as_bytes(),
        )]);
        req["payload"]["configuration"]["workType"] = json!(work);
        let r = completed(&req, &b);
        let v = serde_json::to_value(&r).unwrap();
        assert_eq!(v["configuration"]["workType"], work);
        assert_eq!(v["knu"]["metadata"]["guidelineSha256"], SHA256);
        assert_eq!(v["knu"]["metadata"]["profile"]["version"], VERSION);
        for rr in &r.rule_results[1..] {
            let source = rr.source.as_ref().unwrap();
            assert_eq!(source.origin, "knu");
            assert_eq!(source.version, VERSION);
            assert_eq!(source.citation.printed_pages, source.citation.pdf_pages);
            assert_eq!(
                source.citation.printed_pages,
                if rr.rule_id == INDENT {
                    vec![18]
                } else {
                    vec![18, 19]
                }
            );
            assert_eq!(
                source.citation.section,
                if rr.rule_id == INDENT { "3.1" } else { "3.2" }
            );
            assert_eq!(source.applicable_work_types.len(), 4);
            assert_eq!(
                serde_json::to_value(&source.evaluation_domain).unwrap(),
                "sourceObservation"
            );
            assert_eq!(rr.outcome, Outcome::Pass);
            assert!(!rr.evidence_ids.as_ref().unwrap().is_empty());
        }
        assert_eq!(r.rule_results[0].origin, "engine");
        assert!(r.rule_results[0].source.is_none());
        let again: AnalysisResult = serde_json::from_value(v.clone()).unwrap();
        assert_eq!(serde_json::to_value(again).unwrap(), v);
    }
}

#[test]
fn decimal_grammar_exact_units_bounds_and_public_outcomes() {
    for s in [
        "1.27cm",
        "1.270cm",
        "12.700 mm",
        " 0012.700\tmm ",
        "0cm",
        "999999999cm",
        "999999.999mm",
    ] {
        let n = observations::parse_length(s.as_bytes()).unwrap();
        let r = one(&format!("\\setlength{{\\parindent}}{{{s}}}"));
        assert_eq!(
            outcome(&r, INDENT),
            if n.is_required() {
                Outcome::Pass
            } else {
                Outcome::Fail
            },
            "{s}"
        );
    }
    for s in [
        "-1cm",
        "+1cm",
        "1,27cm",
        "1e1mm",
        ".127cm",
        "1.cm",
        "1pt",
        "1in",
        "1em",
        "1MM",
        "1cm plus 1mm",
        "\\x",
        "{1cm}",
        "1% comment\ncm",
        "1234567890mm",
        "0000000001cm",
        "1.2700cm",
        "18446744073709551615cm",
        "",
    ] {
        assert!(observations::parse_length(s.as_bytes()).is_none(), "{s}");
        let r = one(&format!("\\setlength{{\\parindent}}{{{s}}}"));
        assert_eq!(outcome(&r, INDENT), Outcome::Indeterminate, "{s}");
        assert_no_fix(&r);
    }
    let zero = Length {
        numerator_mm: 0,
        denominator: 0,
        unit: "mm".into(),
    };
    assert!(!zero.is_required());
    assert!(!zero.equals(&zero));
    let huge = Length {
        numerator_mm: u64::MAX,
        denominator: u64::MAX,
        unit: "mm".into(),
    };
    assert!(!huge.is_required());
    assert!(!huge.equals(&huge));
}

#[test]
fn both_rules_have_five_distinct_outcomes() {
    let pass = one(r"\setlength{\parindent}{1.27cm}\section{Good}");
    let fail = one(r"\setlength{\parindent}{1cm}\section{Bad.}");
    let uncertain = one(r"\unknown\setlength{\parindent}{1cm}\section{Bad.}");
    let unavailable = one("Plain text");
    let (r, b) = request(&[("refs.bib", b"anything"), ("style.sty", b"anything")]);
    let na = completed(&r, &b);
    for id in [INDENT, HEADING] {
        assert_eq!(outcome(&pass, id), Outcome::Pass);
        assert_eq!(outcome(&fail, id), Outcome::Fail);
        assert_eq!(outcome(&uncertain, id), Outcome::Indeterminate);
        assert_eq!(outcome(&unavailable, id), Outcome::Unavailable);
        assert_eq!(outcome(&na, id), Outcome::NotApplicable);
    }
    assert_eq!(uncertain.diagnostics.len(), 2); // retain observed counterexamples
    assert_eq!(
        uncertain.knu.unwrap().engine_aggregate_outcome,
        Outcome::Pass
    );
    for files in [vec![], vec![("main.tex", b"\xff".as_slice())]] {
        let (mut r, b) = request(&files);
        if !files.is_empty() {
            r["payload"]["files"][0]["mediaKind"] = json!("binary");
        }
        let result = completed(&r, &b);
        assert_eq!(outcome(&result, INDENT), Outcome::NotApplicable);
        assert_eq!(outcome(&result, HEADING), Outcome::NotApplicable);
    }
}

#[test]
fn exact_priority_and_per_file_missing_prerequisites() {
    let values = [
        Outcome::NotApplicable,
        Outcome::Pass,
        Outcome::Unavailable,
        Outcome::Fail,
        Outcome::Indeterminate,
    ];
    for (i, a) in values.iter().enumerate() {
        for (j, b) in values.iter().enumerate() {
            assert_eq!(rules::aggregate([*a, *b]), values[i.max(j)]);
            assert_eq!(rules::aggregate([*b, *a]), values[i.max(j)]);
        }
    }
    for (src, expected) in [
        (r"\setlength{\parindent}{1cm}", Outcome::Fail),
        (r"\setlength{\parindent}{1.27cm}", Outcome::Unavailable),
        (r"\unknown", Outcome::Indeterminate),
    ] {
        let (r, b) = request(&[("a.tex", src.as_bytes()), ("b.TeX", b"plain")]);
        let out = completed(&r, &b);
        assert_eq!(outcome(&out, INDENT), expected);
        assert_eq!(
            out.rule_results[1].per_file[1].outcome,
            Outcome::Unavailable
        );
    }
}

#[test]
fn heading_commands_star_short_titles_internal_and_final_full_stops() {
    for name in [
        "chapter",
        "section",
        "subsection",
        "subsubsection",
        "paragraph",
        "subparagraph",
    ] {
        for star in ["", "*"] {
            for (args, expected) in [
                ("{Огляд. Методи}", Outcome::Pass),
                ("[Short]{Good}", Outcome::Pass),
                ("[Short.]{Good}", Outcome::Fail),
                ("[Short]{Bad.  }", Outcome::Fail),
                ("{Текст…}", Outcome::Pass),
                ("{Текст．}", Outcome::Pass),
                ("{\\textbf{Текст}}", Outcome::Indeterminate),
                ("{ }", Outcome::Indeterminate),
            ] {
                let r = one(&format!("\\{name}{star}{args}"));
                assert_eq!(outcome(&r, HEADING), expected, "{name}{star}{args}");
                assert_no_fix(&r);
            }
        }
    }
    let r = one(r"\section*[Short.]{Display}");
    let obs = &r.knu.as_ref().unwrap().files[0].observations;
    assert!(matches!(
        obs[0].value,
        ObservationValue::HeadingTitle {
            title_kind: TitleKind::Short,
            ..
        }
    ));
    assert!(matches!(
        obs[1].value,
        ObservationValue::HeadingTitle {
            title_kind: TitleKind::Display,
            ..
        }
    ));
    assert_eq!(
        obs[0].value_span,
        super::dto::Span {
            start_byte: 10,
            end_byte: 16
        }
    );
    assert_eq!(
        obs[1].value_span,
        super::dto::Span {
            start_byte: 18,
            end_byte: 25
        }
    );
    assert_eq!(
        r.diagnostics[0].span,
        Some(super::dto::Span {
            start_byte: 15,
            end_byte: 16
        })
    );
}

#[test]
fn comments_verbatim_escapes_and_argument_ownership() {
    let src = "% \\setlength{\\parindent}{1cm}\r\n\\begin{verbatim}\\section{Bad.}\\end{verbatim}\n\\begin{lstlisting}\\input{../bad}\\end{lstlisting}\n\\begin{Verbatim}\\setlength{\\parindent}{1cm}\\end{Verbatim}\n\\verb|\\section{Bad.}| \\verb*🙂{\\unknown}🙂 \\% \\{ \\} \\& \\# \\_ \\$ \\\\\n\\setlength % A\r\n { \\parindent } % B\r { 1.27 cm }\n\\section% C\n{Good}\n\\label{x}\\ref{x}\\eqref{x}\\pageref{x}";
    let r = one(src);
    assert_eq!(outcome(&r, INDENT), Outcome::Pass);
    assert_eq!(outcome(&r, HEADING), Outcome::Pass);
    assert_eq!(r.knu.as_ref().unwrap().files[0].observations.len(), 2);
    for src in [
        r"\newcommand{\x}{\section{Hidden.}\setlength{\parindent}{1cm}}",
        r"{\section{Hidden.}\setlength{\parindent}{1cm}}",
        r"\section{\section{Hidden.}}",
    ] {
        let r = one(src);
        assert_eq!(outcome(&r, INDENT), Outcome::Indeterminate);
        assert_eq!(outcome(&r, HEADING), Outcome::Indeterminate);
        assert!(!r.knu.as_ref().unwrap().files[0]
            .observations
            .iter()
            .any(|o| o.agrees == Some(false)));
        assert_no_fix(&r);
    }
}

#[test]
fn finite_context_allowlist_never_claims_dynamic_semantics() {
    for context in [
        r"\def\x{a}",
        r"\gdef\x{a}",
        r"\edef\x{a}",
        r"\xdef\x{a}",
        r"\let\x\y",
        r"\newcommand{\x}{a}",
        r"\renewcommand{\x}{a}",
        r"\csname x\endcsname",
        r"\iftrue",
        r"\else",
        r"\fi",
        r"\catcode",
        r"\makeatletter",
        r"\ExplSyntaxOn",
        r"\foo",
        r"\documentclass{custom}",
        r"\documentclass[12pt]{article}",
        r"\usepackage{geometry}",
        r"\parindent=10mm",
        r"\addtolength{\parindent}{1cm}",
        r"$x$",
        r"\(x\)",
        r"\[x\]",
        "~",
        "^",
        "_",
        "#",
        "&",
        r"\begin{custom}x\end{custom}",
    ] {
        let r = one(&format!(
            "{context}\\setlength{{\\parindent}}{{1cm}}\\section{{Bad.}}"
        ));
        assert_eq!(outcome(&r, INDENT), Outcome::Indeterminate, "{context}");
        assert_eq!(outcome(&r, HEADING), Outcome::Indeterminate, "{context}");
        assert_no_fix(&r);
    }
    for class in ["article", "report", "book"] {
        let r=one(&format!("\\documentclass{{{class}}}\\setlength{{\\parindent}}{{1.27cm}}\\begin{{document}}\\tableofcontents\\maketitle\\clearpage\\newpage\\section{{Good}}\\end{{document}}"));
        assert_eq!(outcome(&r, INDENT), Outcome::Pass);
        assert_eq!(outcome(&r, HEADING), Outcome::Pass);
    }
}

#[test]
fn malformed_and_local_syntax_cannot_offer_a_fix_or_false_pass() {
    for src in [
        r"\setlength*{\parindent}{1cm}",
        r"\setlength[foo]{\parindent}{1cm}",
        r"\setlength{\x}{1cm}",
        r"\setlength{\parindent}",
        r"\setlength{\parindent}{1cm",
        r"\section[{x}]",
        r"\section[title",
        r"\section [short]{Good}",
        r"\section**{Good}",
        r"\section{Good}",
        r"\section{a%inside
b}",
        r"\begin{document}\section{Good}",
        r"\end{document}\section{Good}",
        r"\begin{document}\begin{document}\section{Good}\end{document}",
        r"\begin{document}\end{document}\section{Good}",
        r"}\section{Good}",
        r"{\setlength{\parindent}{1cm}}",
    ] {
        // One plain valid title is a control for distinguishing missing indent.
        let r = one(src);
        if src == r"\section{Good}" {
            assert_eq!(outcome(&r, HEADING), Outcome::Pass);
            assert_eq!(outcome(&r, INDENT), Outcome::Unavailable);
        } else {
            assert!(
                outcome(&r, INDENT) == Outcome::Indeterminate
                    || outcome(&r, HEADING) == Outcome::Indeterminate,
                "{src}"
            );
        }
        assert_no_fix(&r);
    }
}

#[test]
fn repeated_equal_and_conflicting_declarations_are_distinct() {
    for (a, b, expected) in [
        ("1cm", "10mm", Outcome::Fail),
        ("1.27cm", "12.700 mm", Outcome::Pass),
        ("1cm", "2cm", Outcome::Indeterminate),
    ] {
        let r = one(&format!(
            "\\setlength{{\\parindent}}{{{a}}}\\setlength{{\\parindent}}{{{b}}}\\section{{Good}}"
        ));
        assert_eq!(outcome(&r, INDENT), expected);
        assert_eq!(outcome(&r, HEADING), Outcome::Pass);
        assert_no_fix(&r);
    }
}

#[test]
fn includes_missing_traversal_dynamic_and_cycles_are_never_resolved() {
    for include in [
        r"\input{missing}",
        r"\include{../escape}",
        r"\input{/etc/passwd}",
        r"\input{a}",
        r"\input{\dynamic}",
        r"\input chapter",
        r"\section{\input{a}}",
    ] {
        let (r, b) = request(&[
            ("a.tex", include.as_bytes()),
            ("b.tex", br"\setlength{\parindent}{1cm}\section{Bad.}"),
            ("c.tex", br"\input{a}"),
        ]);
        let out = completed(&r, &b);
        for f in out.rule_results[1..].iter().flat_map(|r| &r.per_file) {
            assert_eq!(f.outcome, Outcome::Indeterminate);
        }
        assert!(out
            .knu
            .as_ref()
            .unwrap()
            .files
            .iter()
            .all(|f| f.limitations.contains(&Limitation::IncludeNotResolved)));
        assert_no_fix(&out);
    }
    for path in [
        "../a.tex",
        "/a.tex",
        "a/../b.tex",
        "a\\b.tex",
        "C:/a.tex",
        "a//b.tex",
        "./a.tex",
        "a\0.tex",
        "",
    ] {
        let (r, b) = request(&[(path, b"plain")]);
        error(&r, &b, "InvalidInput");
    }
}
