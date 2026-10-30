use super::*;

#[test]
fn include_path_insertion_preserves_exact_target_but_omits_tex_suffix() {
    let root = "\\include{chap}";
    let (v, b) = request(
        &[
            ("main", "main.tex", root),
            ("chapter", "chapter.tex", "Text"),
            ("bib", "chapter.bib", "@book{x,}"),
        ],
        "main",
        "inputPath",
        "chap",
        root.len() - 1,
    );
    let p = successful(&v, &b);
    assert_eq!(labels(&p), vec!["chapter.tex"]);
    assert_eq!(p["candidates"][0]["insertText"], "chapter");
    assert_eq!(p["candidates"][0]["definition"]["fileId"], "chapter");
    let root = "\\label{sec}\\ref{first, s}";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "reference",
        "s",
        root.len() - 1,
    );
    assert_eq!(labels(&successful(&v, &b)), vec!["sec"]);
    let root = "\\begin{\\dynamic}\\section{Literal}";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "outline",
        "",
        root.len(),
    );
    let p = successful(&v, &b);
    assert_eq!(p["complete"], false);
    assert!(p["limitations"]
        .as_array()
        .unwrap()
        .iter()
        .any(|v| v == "dynamicEnvironment"));
}

#[test]
fn depth_sixteen_is_admitted_without_following_a_seventeenth_edge() {
    let chain: Vec<_> = (0..16)
        .map(|i| {
            (
                format!("f{i}"),
                format!("f{i}.tex"),
                if i == 15 {
                    "Text".to_string()
                } else {
                    format!("\\input{{f{}}}", i + 1)
                },
            )
        })
        .collect();
    let mut files = vec![("main", "main.tex", "\\input{f0}")];
    files.extend(
        chain
            .iter()
            .map(|(id, path, text)| (id.as_str(), path.as_str(), text.as_str())),
    );
    let (v, b) = request(&files, "main", "outline", "", 0);
    let p = successful(&v, &b);
    assert_eq!(p["complete"], true);
}

#[test]
fn bounded_malformed_source_corpus_never_panics_or_returns_invalid_json() {
    let pieces = [
        "\\def",
        "\\newcommand",
        "\\newenvironment",
        "\\input",
        "\\cite",
        "\\begin",
        "\\csname",
        "\\usepackage",
        "{",
        "}",
        "[",
        "]",
        "#1",
        "% comment\r\n",
        "\\verb|",
        "'",
        "\"",
        "Київ",
        "😀",
        "е\u{301}",
        "\\",
        " ",
    ];
    let mut state = 43u64;
    for i in 0..256 {
        let mut text = String::new();
        for _ in 0..30 {
            state = state.wrapping_mul(6364136223846793005).wrapping_add(1);
            text.push_str(pieces[(state >> 32) as usize % pieces.len()]);
        }
        let context = [
            "command",
            "environment",
            "reference",
            "citation",
            "inputPath",
            "definition",
            "outline",
        ][i % 7];
        let (v, b) = request(
            &[("main", "main.tex", &text)],
            "main",
            context,
            "",
            text.len(),
        );
        let r = assist(&v.to_string(), &b);
        let decoded: Value = serde_json::from_str(&r).unwrap();
        assert!(matches!(
            decoded["status"].as_str(),
            Some("completed" | "failed")
        ));
        assert!(r.len() <= 262144);
    }
}
