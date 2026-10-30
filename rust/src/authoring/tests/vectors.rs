use super::*;

/// Expected responses are independently authored from the frozen contract and
/// source delimiters, not captured from this producer. Reusable managed vectors.
#[test]
fn canonical_metadata_packed_bytes_and_results() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/r2/assist");
    for name in [
        "command",
        "environment",
        "reference-unicode",
        "citation",
        "inputPath",
        "definition",
        "outline",
        "unknown-catalog",
    ] {
        let path = root.join(name);
        let metadata = std::fs::read_to_string(path.join("metadata.json")).unwrap();
        let packed = std::fs::read(path.join("packed.utf8")).unwrap();
        let expected: Value =
            serde_json::from_str(&std::fs::read_to_string(path.join("result.json")).unwrap())
                .unwrap();
        let actual: Value = serde_json::from_str(&assist(&metadata, &packed)).unwrap();
        assert_eq!(actual, expected, "{name}");
    }
}

#[test]
fn snippet_query_count_and_total_response_boundaries() {
    let key = "a".repeat(512);
    let text = format!("\\label{{{key}}}\\ref{{}}");
    let (v, b) = request(
        &[("main", "main.tex", &text)],
        "main",
        "reference",
        "",
        text.len() - 1,
    );
    let p = successful(&v, &b);
    assert_eq!(p["candidates"][0]["insertText"], key);
    let query = "a".repeat(1024);
    let text = format!("\\{query}");
    let (v, b) = request(
        &[("main", "main.tex", &text)],
        "main",
        "command",
        &query,
        text.len(),
    );
    assert!(labels(&successful(&v, &b)).is_empty());
    let text = format!(
        "{}\\ref{{}}",
        (0..100)
            .map(|i| format!("\\label{{key{i}}}"))
            .collect::<String>()
    );
    let (v, b) = request(
        &[("main", "main.tex", &text)],
        "main",
        "reference",
        "",
        text.len() - 1,
    );
    assert_eq!(
        successful(&v, &b)["candidates"].as_array().unwrap().len(),
        100
    );
    let text = (0..1700)
        .map(|i| format!("\\section{{{i}{}}}", "x".repeat(100)))
        .collect::<String>();
    let (v, b) = request(
        &[("main", "main.tex", &text)],
        "main",
        "outline",
        "",
        text.len(),
    );
    let r = run_request(&v, &b);
    assert_eq!(r["error"]["code"], "QuotaExceeded");
    assert!(r.get("payload").is_none());
}

#[test]
fn malformed_id_path_encoding_and_strict_budget_shapes() {
    let (base, b) = request(
        &[("main", "main.tex", "\\sec")],
        "main",
        "command",
        "sec",
        4,
    );
    for (pointer, value) in [
        ("/requestId", json!("")),
        ("/requestId", json!("a".repeat(129))),
        ("/payload/rootFileId", json!("absent")),
        ("/payload/fileId", json!("absent")),
        (
            "/payload/files/0/relativePath",
            json!("__oop_runtime__/main.tex"),
        ),
        ("/payload/files/0/mediaKind", json!("Text")),
        ("/payload/files/0/byteOffset", json!(u64::MAX)),
        ("/payload/limits", json!({"maxFiles":null})),
    ] {
        let mut v = base.clone();
        *v.pointer_mut(pointer).unwrap() = value;
        assert_eq!(run_request(&v, &b)["status"], "failed", "{pointer}");
    }
    let mut v = base.clone();
    v["payload"]["limits"] = json!({"maxResultBytes":262145});
    assert_eq!(run_request(&v, &b)["error"]["code"], "QuotaExceeded");
    let mut v = base.clone();
    v["payload"]["files"]
        .as_array_mut()
        .unwrap()
        .push(base["payload"]["files"][0].clone());
    assert_eq!(run_request(&v, &b)["error"]["code"], "InvalidInput");
    let mut v = base.clone();
    v["payload"]["files"][0]["byteLength"] = json!(1);
    assert_eq!(run_request(&v, &[0xff])["error"]["code"], "InvalidInput");
    for malformed in [
        "null",
        "[]",
        "{}",
        "{\"requestId\":false}",
        "{\"requestId\":\"r\",\"protocolVersion\":1,\"protocolVersion\":1}",
    ] {
        let r: Value = serde_json::from_str(&assist(malformed, &[])).unwrap();
        assert_eq!(r["status"], "failed");
    }
}

#[test]
fn included_bib_unicode_and_default_declaration_insertions_are_safe() {
    let root = "\\input{part}\\cite{Ки}";
    let part = "\\addbibresource{refs.bib}";
    let bib = "\u{feff}@article{Київ😀е\u{301},title={Назва}}\r\n";
    let (v, b) = request(
        &[
            ("main", "main.tex", root),
            ("part", "part.tex", part),
            ("bib", "refs.bib", bib),
        ],
        "main",
        "citation",
        "Ки",
        root.len() - 1,
    );
    let p = successful(&v, &b);
    let c = &p["candidates"][0];
    assert_eq!(c["label"], "Київ😀е\u{301}");
    let a = bib.find("Київ").unwrap();
    assert_eq!(c["definition"]["span"]["startByte"], a);
    assert_eq!(
        c["caretOffsetUtf16"],
        "Київ😀е\u{301}".encode_utf16().count()
    );
    let root = "\\newcommand{\\foo}[2][default]{#1#2}\\fo";
    let (v, b) = request(
        &[("main", "main.tex", root)],
        "main",
        "command",
        "fo",
        root.len(),
    );
    let p = successful(&v, &b);
    let custom = p["candidates"]
        .as_array()
        .unwrap()
        .iter()
        .find(|c| c["label"] == "foo")
        .unwrap();
    assert_eq!(custom["insertText"], "\\foo");
    assert!(custom["detail"].as_str().unwrap().contains("name-only"));
}

#[test]
fn binary_local_style_is_not_lexed_and_bom_path_definition_is_navigable() {
    let root = "\\usepackage{local}\\input{chap}";
    let (mut v, mut b) = request(
        &[
            ("main", "main.tex", root),
            ("style", "local.sty", "x"),
            ("chapter", "chapter.tex", "\u{feff}Text"),
        ],
        "main",
        "inputPath",
        "chap",
        root.len() - 1,
    );
    let off = v["payload"]["files"][1]["byteOffset"].as_u64().unwrap() as usize;
    b[off] = 0xff;
    v["payload"]["files"][1]["mediaKind"] = json!("binary");
    let p = successful(&v, &b);
    assert_eq!(
        p["candidates"][0]["definition"]["span"],
        json!({"startByte":3,"endByte":3})
    );
    assert!(p["limitations"]
        .as_array()
        .unwrap()
        .iter()
        .any(|v| v == "nonTextInclude"));
}
