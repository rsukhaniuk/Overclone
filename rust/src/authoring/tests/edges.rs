use super::*;

fn has_limit(p: &Value, code: &str) -> bool {
    p["limitations"]
        .as_array()
        .unwrap()
        .iter()
        .any(|v| v == code)
}

#[test]
fn catalog_has_every_allocated_package_and_discloses_engine_and_resources() {
    for package in [
        "amsmath",
        "graphicx",
        "hyperref",
        "cleveref",
        "booktabs",
        "caption",
        "subcaption",
        "listings",
        "enumitem",
        "setspace",
        "geometry",
        "babel",
        "fontspec",
        "biblatex",
        "article",
        "report",
        "book",
    ] {
        assert!(catalog::known_package(package), "{package}");
        for entry in catalog::ENTRIES
            .iter()
            .filter(|e| e.package == Some(package))
        {
            let suffix = if entry.kind == "environment" {
                format!("\\begin{{{}}}", entry.name)
            } else {
                format!("\\{}", entry.name)
            };
            let loader = if matches!(package, "article" | "report" | "book") {
                "documentclass"
            } else {
                "usepackage"
            };
            let root = format!("\\{loader}{{{package}}}{suffix}");
            let (v, b) = request(
                &[("main", "main.tex", &root)],
                "main",
                if entry.kind == "environment" {
                    "environment"
                } else {
                    "command"
                },
                entry.name,
                root.len() - usize::from(entry.kind == "environment"),
            );
            let p = successful(&v, &b);
            let found = p["candidates"]
                .as_array()
                .unwrap()
                .iter()
                .find(|c| c["label"] == entry.name)
                .unwrap();
            assert!(found["detail"].as_str().unwrap().contains(entry.provenance));
            assert!(found["detail"].as_str().unwrap().contains(entry.engines));
            assert_eq!(
                p["loadedPackages"][0]["status"],
                "catalogKnownResourcesUnverified"
            );
        }
    }
}

#[test]
fn selected_root_only_and_literal_include_grammar_are_reused() {
    let root = "\\input{dir/../chapter}\\ref{sec}";
    let (mut v, b) = request(
        &[
            ("main", "main.tex", root),
            ("included", "chapter.tex", "\\label{section}"),
            ("other", "other.tex", "\\label{secret}"),
        ],
        "main",
        "reference",
        "sec",
        root.len() - 1,
    );
    assert_eq!(labels(&successful(&v, &b)), vec!["section"]);
    v["payload"]["rootFileId"] = json!("other");
    let p = successful(&v, &b);
    assert!(labels(&p).is_empty());
    assert!(has_limit(&p, "fileOutsideRootClosure"));
    assert_eq!(p["complete"], false);
}

#[test]
fn closure_reports_missing_dynamic_cycle_and_repeated_edges() {
    for (root, child, code) in [
        ("\\input{missing}", "", "missingInclude"),
        ("\\input{\\macro}", "", "dynamicOrUnsafeInclude"),
        ("\\input{a}", "\\input{main}", "cyclicInclude"),
        ("\\input{a}\\input{a}", "", "repeatedInclude"),
        ("\\input{../escape}", "", "dynamicOrUnsafeInclude"),
    ] {
        let (v, b) = request(
            &[("main", "main.tex", root), ("a", "a.tex", child)],
            "main",
            "outline",
            "",
            root.len(),
        );
        let p = successful(&v, &b);
        assert!(has_limit(&p, code), "{code}: {p}");
        assert_eq!(p["complete"], false);
    }
}

#[test]
fn custom_declarations_from_sty_cls_and_simple_def_are_navigable() {
    let root = "\\documentclass{custom}\\usepackage{local}\\input{macros.tex}\\loc";
    let cls = "\\LoadClass{report}\\newcommand{\\classmacro}[1]{#1}";
    let sty = "\\newcommand{\\localmacro}[2]{#1 #2}\\newenvironment{localenv}{}{}";
    let macros = "\\def\\localdef#1#2{#1#2}";
    let (v, b) = request(
        &[
            ("main", "main.tex", root),
            ("cls", "custom.cls", cls),
            ("sty", "local.sty", sty),
            ("mac", "macros.tex", macros),
        ],
        "main",
        "command",
        "loc",
        root.len(),
    );
    let p = successful(&v, &b);
    assert_eq!(labels(&p), vec!["localmacro", "localdef"]);
    assert_eq!(p["candidates"][0]["definition"]["fileId"], "sty");
    let start = sty.find("localmacro").unwrap();
    assert_eq!(
        p["candidates"][0]["definition"]["span"],
        json!({"startByte":start,"endByte":start+10})
    );
    assert_eq!(p["candidates"][1]["insertText"], "\\localdef{}{}");
    assert!(has_limit(&p, "projectPackageExecutionUnsupported"));
}

#[test]
fn declarations_never_execute_their_bodies_or_conditionals() {
    let root="\\newcommand{\\x}{\\label{hidden}\\section{Hidden}\\input{absent}}\n\\iffalse\\label{conditional}\\newcommand{\\no}{}\\fi\n\\label{visible}\\ref{}";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "reference",
        "",
        root.len() - 1,
    );
    let p = successful(&v, &b);
    assert_eq!(labels(&p), vec!["visible"]);
    assert_eq!(p["outline"], json!([]));
    assert!(!has_limit(&p, "missingInclude"));
    assert!(has_limit(&p, "conditionalExecutionUnsupported"));
    for (declaration, code) in [
        (
            "\\newcommand{\\foo}[1][x]{#1}",
            "declarationDefaultUnsupported",
        ),
        ("\\renewcommand{\\foo}[1]{#1}", "declarationEffectUncertain"),
        ("\\providecommand{\\foo}{x}", "declarationEffectUncertain"),
        (
            "\\newcommand{\\foo}{}\\newcommand{\\foo}{}",
            "redefinitionUncertain",
        ),
        ("\\def\\foo#1x{#1}", "dynamicDeclaration"),
        ("\\catcode`\\@=11\\newcommand{\\foo}{}", "dynamicConstruct"),
    ] {
        let (v, b) = request(
            &[("main", "main.tex", declaration)],
            "main",
            "outline",
            "",
            declaration.len(),
        );
        let p = successful(&v, &b);
        assert!(has_limit(&p, code), "{code}: {p}");
    }
}

#[test]
fn comments_verbatim_and_midtoken_or_wrong_context_produce_no_candidates() {
    for (text, context, query, caret) in [
        ("% \\sec", "command", "sec", 6),
        ("\\verb|\\sec|", "command", "sec", 10),
        (
            "\\begin{verbatim}\\sec\\end{verbatim}",
            "command",
            "sec",
            20,
        ),
        ("\\section", "command", "sec", 4),
        ("\\\\sec", "command", "sec", 5),
        ("plain sec", "command", "sec", 9),
        ("\\other{ali}", "environment", "ali", 10),
        ("\\begin{ali}", "reference", "ali", 10),
        ("\\ref{sec}", "citation", "sec", 8),
        ("\\cite{key}", "inputPath", "key", 9),
        ("\\input{main}", "environment", "main", 11),
        ("\\ref{x} prose,sec", "reference", "sec", 17),
    ] {
        let (v, b) = request(
            &[("main", "main.tex", text)],
            "main",
            context,
            query,
            caret.min(text.len()),
        );
        let p = successful(&v, &b);
        assert!(labels(&p).is_empty(), "{text}: {p}");
    }
}

#[test]
fn definition_and_outline_miss_are_not_fabricated() {
    let root = "\\newcommand{\\foo}{}\\section{Real}\\notfound";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "definition",
        "notfound",
        root.len(),
    );
    let p = successful(&v, &b);
    assert!(labels(&p).is_empty());
    assert_eq!(p["outline"][0]["label"], "Real");
    let root = "% \\section{Not real}\n\\newcommand{\\x}{\\section{Hidden}}\\section{\\macro}";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "outline",
        "",
        root.len(),
    );
    let p = successful(&v, &b);
    assert_eq!(p["outline"], json!([]));
    assert!(has_limit(&p, "dynamicHeading"));
}

#[test]
fn unknown_and_dynamic_packages_do_not_grant_catalog_capabilities() {
    for (packages, code) in [
        ("\\usepackage{unknown}", "unknownPackage"),
        ("\\usepackage{\\chosen}", "dynamicPackage"),
        ("\\usepackage[option=\\value]{graphicx}", "dynamicPackage"),
    ] {
        let root = format!("{packages}\\incl");
        let (v, b) = request(
            &[("main", "main.tex", &root)],
            "main",
            "command",
            "incl",
            root.len(),
        );
        let p = successful(&v, &b);
        assert_eq!(labels(&p), vec!["include"]);
        assert!(has_limit(&p, code));
    }
}

#[test]
fn bibliography_reader_handles_braces_quotes_escapes_and_exact_selected_resources() {
    let root = "\\bibliography{refs}\\cite{a}";
    let bib="% @book{fake, title={No}}\n@comment{nested {ignored} @book{fake,{}}}\n@book{alpha,title={A {B} @literal},author=\"Quoted \\\" text\"}\n@article{another,title=\"Braces {inside} and )\"}\n";
    let (v, b) = request(
        &[
            ("main", "main.tex", root),
            ("bib", "refs.bib", bib),
            (
                "outside",
                "not-selected.bib",
                "@book{absent,title={Not selected}}",
            ),
        ],
        "main",
        "citation",
        "a",
        root.len() - 1,
    );
    let p = successful(&v, &b);
    assert_eq!(labels(&p), vec!["alpha", "another"]);
    assert_eq!(
        p["candidates"][0]["definition"]["span"],
        json!({"startByte":bib.find("alpha").unwrap(),"endByte":bib.find("alpha").unwrap()+5})
    );
    assert!(!has_limit(&p, "malformedBibliography"), "{p}");
}

#[test]
fn duplicate_dynamic_and_malformed_bibliography_are_explicitly_incomplete() {
    for (bib, code) in [
        (
            "@book{a,title={A}} @book{a,title={B}}",
            "duplicateBibliographyKey",
        ),
        (
            "@string{journal=\"Name\"}\n@book{a,title=journal}",
            "dynamicBibliography",
        ),
        ("@book{a,title=\"A\" # \"B\"}", "dynamicBibliography"),
        ("@book{a,title={unclosed}", "malformedBibliography"),
        ("@book{\\dynamic,title={A}}", "malformedBibliography"),
    ] {
        let root = "\\addbibresource{refs.bib}\\cite{}";
        let (v, b) = request(
            &[("main", "main.tex", root), ("bib", "refs.bib", bib)],
            "main",
            "citation",
            "",
            root.len() - 1,
        );
        let p = successful(&v, &b);
        assert!(has_limit(&p, code), "{code}: {p}");
        assert_eq!(p["complete"], false);
    }
    for root in [
        "\\bibliography{missing}",
        "\\addbibresource{../escape.bib}",
        "\\bibliography{\\dynamic}",
    ] {
        let (v, b) = request(
            &[("main", "main.tex", root)],
            "main",
            "outline",
            "",
            root.len(),
        );
        let p = successful(&v, &b);
        assert_eq!(p["complete"], false);
    }
}

#[test]
fn unicode_bom_crlf_combining_spans_and_caret_utf16_are_exact() {
    let key = "Київ😀е\u{301}";
    let root = format!("\u{feff}% Україна😀\r\n\\label{{{key}}}\r\n\\ref{{Ки}}\r\n");
    let caret = root.rfind("Ки").unwrap() + "Ки".len();
    let (v, b) = request(
        &[("main", "розділ/main.tex", &root)],
        "main",
        "reference",
        "Ки",
        caret,
    );
    let p = successful(&v, &b);
    let c = &p["candidates"][0];
    assert_eq!(c["insertText"], key);
    assert_eq!(c["caretOffsetUtf16"], key.encode_utf16().count());
    let a = root.find(key).unwrap();
    assert_eq!(
        c["definition"]["span"],
        json!({"startByte":a,"endByte":a+key.len()})
    );
    assert_eq!(
        c["replaceSpan"],
        json!({"startByte":caret-"Ки".len(),"endByte":caret})
    );
    assert_eq!(&b[a..a + key.len()], key.as_bytes());
    let mut bad = v.clone();
    bad["payload"]["caretByte"] = json!(root.find('😀').unwrap() + 1);
    assert_eq!(run_request(&bad, &b)["error"]["code"], "InvalidInput");
    let mut bad = v;
    bad["payload"]["files"][0]["hasBom"] = json!(false);
    assert_eq!(run_request(&bad, &b)["error"]["code"], "InvalidInput");
}

#[test]
fn canonically_equivalent_keys_are_not_normalized() {
    let root = "\\label{е\u{301}}\\ref{é}";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "reference",
        "é",
        root.len() - 1,
    );
    assert!(labels(&successful(&v, &b)).is_empty());
}

#[test]
fn hard_caps_never_publish_a_completed_truncated_prefix() {
    let root = format!(
        "{}\\ref{{}}",
        (0..101)
            .map(|i| format!("\\label{{key{i}}}"))
            .collect::<String>()
    );
    let (v, b) = request(
        &[("main", "main.tex", &root)],
        "main",
        "reference",
        "",
        root.len() - 1,
    );
    let r = run_request(&v, &b);
    assert_eq!(r["error"]["code"], "QuotaExceeded");
    assert!(r.get("payload").is_none());
    let root = format!("\\label{{{}}}\\ref{{}}", "a".repeat(513));
    let (v, b) = request(
        &[("main", "main.tex", &root)],
        "main",
        "reference",
        "",
        root.len() - 1,
    );
    assert_eq!(run_request(&v, &b)["error"]["code"], "QuotaExceeded");
    let root = "\\input{a}";
    let (mut v, b) = request(
        &[("main", "main.tex", root), ("a", "a.tex", "\\section{A}")],
        "main",
        "outline",
        "",
        root.len(),
    );
    v["payload"]["limits"] = json!({"maxTokensPerProject":5});
    assert_eq!(run_request(&v, &b)["error"]["code"], "QuotaExceeded");
    let chain: Vec<_> = (0..18)
        .map(|i| {
            (
                format!("f{i}"),
                format!("f{i}.tex"),
                format!("\\input{{f{}}}", i + 1),
            )
        })
        .collect();
    let mut files = vec![("main", "main.tex", "\\input{f0}")];
    files.extend(
        chain
            .iter()
            .map(|(id, path, body)| (id.as_str(), path.as_str(), body.as_str())),
    );
    let (v, b) = request(&files, "main", "outline", "", 0);
    assert_eq!(run_request(&v, &b)["error"]["code"], "QuotaExceeded");
}

#[test]
fn stable_ids_and_request_correlation_do_not_create_authority() {
    let root = "\\label{sec}\\ref{s}";
    let (mut v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "reference",
        "s",
        root.len() - 1,
    );
    let first = successful(&v, &b);
    v["requestId"] = json!("new-request");
    let r = run_request(&v, &b);
    assert_eq!(r["requestId"], "new-request");
    assert_eq!(r["payload"], first);
    assert_eq!(first["snapshotId"], "snapshot");
    assert_eq!(first["rootFileId"], "main");
    assert_eq!(first["fileId"], "main");
    assert_eq!(first["caretByte"], root.len() - 1);
    assert!(first.get("projectId").is_none());
}

#[test]
fn classes_and_packages_cannot_grant_each_others_catalog_applicability() {
    for (loader, name, query) in [
        ("usepackage", "report", "chap"),
        ("documentclass", "graphicx", "includegr"),
    ] {
        let root = format!("\\{loader}{{{name}}}\\{query}");
        let (v, b) = request(
            &[("main", "main.tex", &root)],
            "main",
            "command",
            query,
            root.len(),
        );
        let p = successful(&v, &b);
        assert!(labels(&p).is_empty(), "{root}: {p}");
        assert!(has_limit(&p, "unknownPackage"));
        assert_eq!(
            p["loadedPackages"][0]["status"],
            "unknownResourcesUnverified"
        );
    }
}
