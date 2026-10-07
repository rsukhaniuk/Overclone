//! Focused contract tests for the public `analyze_source` / `initialize`
//! entries and the canonical serializer. Each endpoint span is checked
//! against the standard library `str::is_char_boundary` as an INDEPENDENT
//! oracle, not the crate's own helper.

use crate::analysis::analyze_source;
use crate::analysis::dto::{AnalysisResult, NewlineKind, Outcome, SourceFileFacts, TokenKind};
use serde::Deserialize;

const RID: &str = "req-1";
const SID: &str = "snap-1";
const PID: &str = "proj-1";
const SESS: &str = "sess-1";
const PROFILE_ID: &str = "engine-structural";
const PROFILE_VER: &str = "0";

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
struct TestResponse {
    #[serde(rename = "protocolVersion")]
    protocol_version: u32,
    #[serde(rename = "requestId", default)]
    request_id: Option<String>,
    status: String,
    #[serde(default)]
    payload: Option<serde_json::Value>,
    #[serde(default)]
    error: Option<TestError>,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
struct TestError {
    code: String,
    safe_message: String,
    retryable: bool,
}

/// Independent scalar-boundary oracle: the standard library.
fn std_boundary(bytes: &[u8], offset: usize) -> bool {
    std::str::from_utf8(bytes)
        .map(|s| s.is_char_boundary(offset))
        .unwrap_or(false)
}

/// Build a canonical analyzeSource request envelope + one packed buffer.
fn build_request(files: &[(&str, &str, &[u8])], limits: Option<&str>) -> (String, Vec<u8>) {
    build_request_with(
        &files
            .iter()
            .map(|(id, p, b)| (*id, *p, *b, false, "text"))
            .collect::<Vec<_>>(),
        limits,
    )
}

fn build_request_with(
    files: &[(&str, &str, &[u8], bool, &str)],
    limits: Option<&str>,
) -> (String, Vec<u8>) {
    let mut packed = Vec::new();
    let mut descriptors = Vec::new();
    for &(id, path, bytes, has_bom, media) in files {
        let off = packed.len() as u64;
        let len = bytes.len() as u64;
        packed.extend_from_slice(bytes);
        descriptors.push(serde_json::json!({
            "fileId": id,
            "relativePath": path,
            "mediaKind": media,
            "hasBom": has_bom,
            "sha256": "",
            "byteOffset": off,
            "byteLength": len,
        }));
    }
    let limits_json = if let Some(l) = limits {
        serde_json::json!(serde_json::from_str::<serde_json::Value>(l).unwrap_or_default())
    } else {
        serde_json::json!({})
    };
    let req = serde_json::json!({
        "protocolVersion": 1,
        "requestId": RID,
        "operation": "analyzeSource",
        "payload": {
            "snapshotId": SID,
            "projectId": PID,
            "sessionId": SESS,
            "workingGeneration": 7,
            "baseHeadRevisionId": null,
            "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
            "limits": limits_json,
            "files": descriptors,
        }
    });
    (serde_json::to_string(&req).unwrap(), packed)
}

fn run(req_json: &str, packed: &[u8]) -> TestResponse {
    let resp = analyze_source(req_json, packed);
    serde_json::from_str(&resp).expect("response must be valid canonical JSON")
}

/// Public-result helper assertions: every completed/failed envelope must
/// carry protocolVersion 1; a failed envelope must have a nonempty
/// `safeMessage` and a known `retryable` value.
fn assert_public_invariants(resp: &TestResponse) {
    assert_eq!(resp.protocol_version, 1, "protocolVersion must be 1");
    if resp.status == "failed" {
        let e = resp
            .error
            .as_ref()
            .expect("failed envelope carries an error");
        assert!(!e.safe_message.is_empty(), "safeMessage is nonempty");
        // retryable is a known bool; assert it is false for the codes we emit.
        assert!(!e.retryable, "retryable is false for these failures");
    }
}

fn result_of(resp: &TestResponse) -> AnalysisResult {
    assert_public_invariants(resp);
    assert_eq!(
        resp.status, "completed",
        "unexpected status: {:?}",
        resp.error
    );
    let payload = resp.payload.as_ref().expect("completed payload");
    serde_json::from_value(payload.clone()).expect("payload is a canonical AnalysisResult")
}

fn failed(resp: &TestResponse) -> &TestError {
    assert_public_invariants(resp);
    assert_eq!(resp.status, "failed");
    resp.error.as_ref().unwrap()
}

// ---------- outcome helpers ----------

fn file<'a>(r: &'a AnalysisResult, fid: &str) -> &'a SourceFileFacts {
    r.facts
        .files
        .iter()
        .find(|f| f.file_id == fid)
        .expect("file present")
}

// ============================================================
// Defect 1: scalar handling — whole-scalar escapes/verbatim delimiters
// ============================================================

/// Table of (source, expected unmatched-open count, expected aggregate).
/// Spans are checked against the stdlib oracle.
const SCALAR_FIXTURES: &[(&str, usize, Outcome)] = &[
    // `\é{`: escape consumes the WHOLE é scalar after backslash.
    (r"\é{", 1, Outcome::Fail),
    // `\verbé{é`: \verb with a whole-scalar Unicode delimiter.
    (r"\verbé{é", 1, Outcome::Fail),
    // 2-byte scalar after backslash, plus a normal brace.
    (r"\é{x}", 1, Outcome::Fail),
    // 3-byte scalar (CJK) beside a delimiter.
    ("\\中{", 1, Outcome::Fail),
    // 4-byte scalar (emoji) after backslash.
    ("\\🙂{", 1, Outcome::Fail),
];

#[test]
fn scalar_endpoints_match_stdlib_oracle() {
    for &(src, _opens, _agg) in SCALAR_FIXTURES {
        let bytes = src.as_bytes();
        let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
        let r = result_of(&run(&req, &packed));
        for f in &r.facts.files {
            for t in &f.tokens {
                assert!(
                    std_boundary(bytes, t.start_byte as usize),
                    "start not scalar boundary (src {src:?})"
                );
                assert!(
                    std_boundary(bytes, t.end_byte as usize),
                    "end not scalar boundary (src {src:?})"
                );
                assert!(t.start_byte <= t.end_byte);
            }
        }
        for d in &r.diagnostics {
            if let Some(s) = &d.span {
                assert!(std_boundary(bytes, s.start_byte as usize));
                assert!(std_boundary(bytes, s.end_byte as usize));
            }
        }
    }
}

#[test]
fn escape_consumes_whole_scalar_and_finds_open_brace() {
    // `\é{` → escape [0,3), then `{` BraceOpen [3,4); one unmatched open → Fail.
    let src = r"\é{";
    let bytes = src.as_bytes();
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Fail);
    assert_eq!(r.diagnostics.len(), 1);
    let d = &r.diagnostics[0];
    assert_eq!(d.file_id, "f1");
    let s = d.span.as_ref().unwrap();
    assert_eq!(s.start_byte, 3);
    assert_eq!(s.end_byte, 4); // the `{` scalar, half-open
}

#[test]
fn verb_unicode_delimiter_whole_scalar() {
    // `\verbé{é` — delimiter is é (2 bytes), searched as a whole scalar;
    // the body is `{é` and the second é closes. The `{` inside is skipped.
    let src = r"\verbé{é";
    let bytes = src.as_bytes();
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::Verbatim));
    for t in &f.tokens {
        assert!(std_boundary(bytes, t.start_byte as usize));
        assert!(std_boundary(bytes, t.end_byte as usize));
    }
}

// ============================================================
// Defect 2: full control-word name first; \verbatim is not \verb
// ============================================================

#[test]
fn verbatim_prefix_is_ordinary_command_not_verb() {
    // `\verbatim{` is a control word "verbatim" (not "verb") + an unmatched `{`.
    let src = r"\verbatim{";
    let bytes = src.as_bytes();
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Fail);
    assert_eq!(r.diagnostics.len(), 1);
    let d = &r.diagnostics[0];
    assert_eq!(d.span.as_ref().unwrap().start_byte, 9); // the `{` after \verbatim
}

#[test]
fn verbatimfoo_and_verbatiminput_are_ordinary_commands() {
    for src in [r"\verbatimfoo{x}", r"\verbatiminput{x}"] {
        let bytes = src.as_bytes();
        let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
        let r = result_of(&run(&req, &packed));
        // matched braces, ordinary command tokens — no \verb, no diagnostic.
        assert_eq!(r.aggregate_outcome, Outcome::Pass);
        assert!(r.diagnostics.is_empty(), "src: {src:?}");
    }
}

#[test]
fn escaped_braces_and_comments_inside_verb() {
    // `\verb|x\{y\}z|` — escapes/braces inside \verb are literal; one verbatim token.
    let src = r"\verb|x\{y\}z|";
    let bytes = src.as_bytes();
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::Verbatim));
}

#[test]
fn verb_star_delimiter() {
    // `\verb*|x{y|` — the `*` variant with a `|` delimiter; the `{` inside is literal.
    let src = r"\verb*|x{y|";
    let bytes = src.as_bytes();
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
}

// ============================================================
// Defect 3: per-file counter reset; caps enforced at each emission
// ============================================================

#[test]
fn per_file_counter_resets_between_files() {
    // Two one-token files, maxTokensPerFile=1: both must be Pass (each emits exactly
    // one token). Previously b was truncated with zero tokens.
    let (req, packed) = build_request(
        &[("a", "a.tex", b"x"), ("b", "b.tex", b"y")],
        Some(r#"{"maxTokensPerFile": 1}"#),
    );
    let r = result_of(&run(&req, &packed));
    let fa = file(&r, "a");
    let fb = file(&r, "b");
    assert_eq!(fa.token_count, 1);
    assert_eq!(fb.token_count, 1);
    assert_eq!(fa.engine_outcome, Outcome::Pass);
    assert_eq!(fb.engine_outcome, Outcome::Pass);
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
}

#[test]
fn caps_enforced_in_multi_token_branches() {
    // `\begin{verbatim}x\end{verbatim}` with maxTokensPerFile=1: the `\begin{verbatim}`
    // command is the single allowed token; the verbatim body emission hits
    // the cap and stops the file → Indeterminate, tokenCount <= 1.
    let src = r"\begin{verbatim}x\end{verbatim}";
    let bytes = src.as_bytes();
    let (req, packed) = build_request(
        &[("f1", "main.tex", bytes)],
        Some(r#"{"maxTokensPerFile": 1}"#),
    );
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    assert!(f.truncated);
    assert!(f.token_count <= 1);
    assert_eq!(f.engine_outcome, Outcome::Indeterminate);
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
}

#[test]
fn project_exhaustion_marks_unvisited_files_indeterminate() {
    // Two files; project token cap = 1: first file (1 token) fills it; the
    // second file is unvisited → Indeterminate.
    let (req, packed) = build_request(
        &[("a", "a.tex", b"x"), ("b", "b.tex", b"y")],
        Some(r#"{"maxTokensPerProject": 1}"#),
    );
    let r = result_of(&run(&req, &packed));
    assert!(file(&r, "a").token_count <= 1);
    assert!(file(&r, "b").truncated);
    assert_eq!(file(&r, "b").engine_outcome, Outcome::Indeterminate);
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
}

// ============================================================
// Defect 4: diagnose unmatched openings only on a FULL scan
// ============================================================

#[test]
fn balanced_truncated_after_open_is_indeterminate_no_diagnostic() {
    // `{x}` with maxTokensPerFile=1: the `{` is the single token, scanning
    // stops before `x`/`}` → Indeterminate, ZERO unmatched-open diagnostics.
    let src = b"{x}";
    let (req, packed) = build_request(
        &[("f1", "main.tex", src)],
        Some(r#"{"maxTokensPerFile": 1}"#),
    );
    let r = result_of(&run(&req, &packed));
    assert!(file(&r, "f1").truncated);
    assert!(r.diagnostics.is_empty());
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
}

#[test]
fn balanced_full_scan_is_pass() {
    let (req, packed) = build_request(&[("f1", "main.tex", b"{x}")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty());
}

#[test]
fn unmatched_open_full_scan_is_exactly_one_fail() {
    let (req, packed) = build_request(&[("f1", "main.tex", b"{x")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Fail);
    assert_eq!(r.diagnostics.len(), 1);
    assert_eq!(r.diagnostics[0].span.as_ref().unwrap().start_byte, 0);
}

#[test]
fn brace_depth_breach_stops_scan_no_diagnostic() {
    // `{{{` with maxBraceDepth=2: depth breach stops scanning → Indeterminate,
    // no unmatched-open diagnostics from the uncertain remainder.
    let src = b"{{{";
    let (req, packed) = build_request(&[("f1", "main.tex", src)], Some(r#"{"maxBraceDepth": 2}"#));
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    assert!(f.brace_depth_exceeded);
    assert!(r.diagnostics.is_empty());
    assert_eq!(f.engine_outcome, Outcome::Indeterminate);
}

// ============================================================
// Defect 5: optional brackets + literal-argument facts
// ============================================================

#[test]
fn optional_arg_does_not_silently_pass_unmatched_brace() {
    // `\foo[{]` — the optional arg is unterminated (the `]` sits inside the
    // unmatched `{` group); must NOT be Pass.
    let src = br"\foo[{]";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_ne!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty()); // no confirmed unmatched open
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
}

#[test]
fn optional_arg_normal_with_inner_braces() {
    // `\foo[a{b}c]` — optionalArg span recorded, inner braces tracked by
    // the main scan (balanced), so Pass.
    let src = br"\foo[a{b}c]";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::OptionalArg));
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::BraceOpen));
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::BraceClose));
}

#[test]
fn include_literal_argument_span_and_text() {
    // `\input{chapter}` → includePath [7,14) text='chapter' (the argument interior).
    let src = br"\input{chapter}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.start_byte, 7);
    assert_eq!(inc.end_byte, 14);
    assert_eq!(inc.text.as_deref(), Some("chapter"));
    // Compare the fact's source-byte slice to token.text.
    assert_eq!(&bytes[7..14], "chapter".as_bytes());
}

#[test]
fn label_literal_argument_span_and_text() {
    // `\label{item}` → label [7,11) text='item'.
    let src = br"\label{item}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    let lbl = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::Label)
        .expect("label fact");
    assert_eq!(lbl.start_byte, 7);
    assert_eq!(lbl.end_byte, 11);
    assert_eq!(lbl.text.as_deref(), Some("item"));
    assert_eq!(&bytes[7..11], "item".as_bytes());
}

#[test]
fn ordinary_command_does_not_carry_argument_text() {
    // `\textbf{abc}` is an ordinary command — no copied argument text, no
    // includePath/label/reference fact, and not subject to the 4096-byte
    // fact-text limit.
    let src = br"\textbf{abc}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    // The command token itself; no fact with copied text.
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::Command));
    assert!(!f.tokens.iter().any(|t| {
        matches!(
            t.kind,
            TokenKind::IncludePath | TokenKind::Label | TokenKind::Reference
        ) && t.text.is_some()
    }));
}

#[test]
fn literal_extraction_rejects_nonliteral_content() {
    // `\input{a\{b}` — escaped brace inside the argument → nonliteral.
    let src = br"\input{a\{b}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact emitted");
    assert!(
        inc.text.is_none(),
        "nonliteral argument must not carry text"
    );
    assert_eq!(inc.note.as_deref(), Some("nonliteral-argument"));
    assert_eq!(f.engine_outcome, Outcome::Indeterminate);
}

// ============================================================
// Defect 6: per-file diagnosticIds built from actual stored diagnostics
// ============================================================

#[test]
fn per_file_diagnostic_ids_match_stored_diagnostics() {
    // Two files with identical local offsets, each with an unmatched open.
    let (req, packed) = build_request(&[("a", "a.tex", b"{x"), ("b", "b.tex", b"{y")], None);
    let r = result_of(&run(&req, &packed));
    let rr = &r.rule_results[0];
    assert_eq!(rr.rule_id, "ENGINE-STRUCT-001");
    assert_eq!(rr.per_file.len(), 2);
    // Union of per-file diagnosticIds equals the top-level stored IDs.
    let mut union_ids: Vec<String> = rr
        .per_file
        .iter()
        .flat_map(|pf| pf.diagnostic_ids.iter().cloned())
        .collect();
    union_ids.sort();
    let mut top_ids: Vec<String> = r.diagnostics.iter().map(|d| d.id.clone()).collect();
    top_ids.sort();
    assert_eq!(union_ids, top_ids);
    // Each per-file entry lists its file's stored diagnostic IDs.
    for pf in &rr.per_file {
        let expected: Vec<String> = r
            .diagnostics
            .iter()
            .filter(|d| d.file_id == pf.file_id)
            .map(|d| d.id.clone())
            .collect();
        assert_eq!(pf.diagnostic_ids, expected);
    }
}

#[test]
fn diagnostic_cap_keeps_ids_consistent() {
    // Many unmatched opens; diagnostic cap = 1 stores one, the rest are dropped.
    let src: Vec<u8> = (0..5).map(|_| b'{').collect();
    let (req, packed) = build_request(
        &[("f1", "a.tex", &src)],
        Some(r#"{"maxDiagnosticsPerProject": 1}"#),
    );
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.diagnostics.len(), 1);
    assert!(r.coverage.diagnostics_truncated);
    // per-file diagnosticIds list only the ACTUAL stored diagnostic.
    let rr = &r.rule_results[0];
    assert_eq!(rr.per_file.len(), 1);
    assert_eq!(rr.per_file[0].diagnostic_ids.len(), 1);
    assert_eq!(rr.per_file[0].diagnostic_ids[0], r.diagnostics[0].id);
}

// ============================================================
// Defect 7: identity/bound hardening
// ============================================================

#[test]
fn missing_request_id_rejected() {
    let req = serde_json::json!({
        "protocolVersion": 1, "operation": "analyzeSource",
        "payload": {"snapshotId": SID, "projectId": PID, "sessionId": SESS,
        "workingGeneration": 0, "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
        "limits": {}, "files": []}
    });
    let resp = run(&serde_json::to_string(&req).unwrap(), b"");
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn empty_file_id_rejected() {
    let (req, packed) = build_request(&[("", "a.tex", b"x"), ("f2", "b.tex", b"y")], None);
    let resp = run(&req, &packed);
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn oversize_id_rejected_with_small_response() {
    let big = "x".repeat(200);
    let req = serde_json::json!({
        "protocolVersion": 1, "requestId": RID, "operation": "analyzeSource",
        "payload": {"snapshotId": big, "projectId": PID, "sessionId": SESS,
        "workingGeneration": 0, "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
        "limits": {}, "files": []}
    });
    let resp = run(&serde_json::to_string(&req).unwrap(), b"");
    assert_eq!(failed(&resp).code, "InvalidInput");
    // The failed envelope stays small (no oversize requestId echoed).
    let resp_json = serde_json::to_string(&req).unwrap();
    let raw = analyze_source(&resp_json, b"");
    assert!(raw.len() < 4096);
}

#[test]
fn oversize_relative_path_rejected() {
    let big_path = "p/".to_string() + &"x".repeat(2000);
    let (req, packed) = build_request(&[("f1", &big_path, b"x")], None);
    let resp = run(&req, &packed);
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn packed_buffer_over_total_cap_rejected_even_with_small_descriptor() {
    // maxTotalBytes=4 (a request-lowered cap); a 5-byte packed buffer is
    // rejected even though the file descriptor references a small prefix.
    let req = serde_json::json!({
        "protocolVersion": 1, "requestId": RID, "operation": "analyzeSource",
        "payload": {"snapshotId": SID, "projectId": PID, "sessionId": SESS,
        "workingGeneration": 0, "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
        "limits": {"maxTotalBytes": 4},
        "files": [{"fileId": "f1", "relativePath": "a.tex", "mediaKind": "text",
            "hasBom": false, "sha256": "", "byteOffset": 0, "byteLength": 3}]}
    });
    // 5-byte buffer; descriptor references only bytes 0..3 (<= cap) but the buffer itself > cap.
    let packed = b"abcde";
    let resp = run(&serde_json::to_string(&req).unwrap(), packed);
    assert_eq!(failed(&resp).code, "QuotaExceeded");
}

#[test]
fn initialize_metadata_bound_rejects_oversize() {
    let huge = "x".repeat(5 * 1024 * 1024);
    let req = format!(
        r#"{{"protocolVersion":1,"requestId":"{RID}","operation":"initialize","payload":{{}}{}"#,
        huge
    );
    let resp = crate::analysis::initialize(&req);
    assert_eq!(failed(&deserialize(&resp)).code, "QuotaExceeded");
}

fn deserialize(resp: &str) -> TestResponse {
    serde_json::from_str(resp).expect("initialize response is valid canonical JSON")
}

// ============================================================
// Defect 8: no malformed fallback output
// ============================================================

#[test]
fn result_byte_overflow_returns_only_small_valid_envelope() {
    // Lower the result-byte cap so a modest result overflows. The response
    // must be a small VALID canonical failed envelope (no appended partial JSON).
    let src = b"a {b {c} d {e";
    let (req, packed) = build_request(&[("f1", "a.tex", src)], Some(r#"{"maxResultBytes": 64}"#));
    let resp_json = analyze_source(&req, &packed);
    let resp: TestResponse = serde_json::from_str(&resp_json).expect("valid JSON");
    assert_eq!(resp.status, "failed");
    assert_eq!(resp.error.unwrap().code, "QuotaExceeded");
    // The failed envelope is small AND valid (parses round-trip).
    assert!(resp_json.len() < 4096);
    serde_json::from_str::<serde_json::Value>(&resp_json).expect("round-trip valid JSON");
}

// ============================================================
// Defect 10: line_column CRLF boundaries
// ============================================================

#[test]
fn line_column_crlf_boundaries() {
    use crate::analysis::mapping::line_column;
    let b = b"a\r\nb";
    // before CR: line 1, column = offset
    assert_eq!(line_column(b, 0), (1, 0));
    // at the CR (between \r and \n): CRLF not complete -> still line 1
    assert_eq!(line_column(b, 1), (1, 1));
    // between \r and \n (offset = position of \n): not a completed newline
    assert_eq!(line_column(b, 2), (1, 2));
    // after \n: CRLF complete -> line 2
    assert_eq!(line_column(b, 3), (2, 0));
}

#[test]
fn line_column_lone_cr() {
    use crate::analysis::mapping::line_column;
    let b = b"a\rb";
    assert_eq!(line_column(b, 0), (1, 0));
    assert_eq!(line_column(b, 1), (1, 1));
    // lone CR is a completed newline at the \r
    assert_eq!(line_column(b, 2), (2, 0));
}

#[test]
fn line_column_multilingual_byte_offsets() {
    use crate::analysis::mapping::line_column;
    // BOM + CRLF + Ukrainian/emoji/combining then a `{`.
    let mut b = b"\xEF\xBB\xBF".to_vec();
    b.extend_from_slice(b"\r\n");
    b.extend_from_slice("Привіт 🇺🇦 e\u{0301}".as_bytes());
    b.extend_from_slice(b" {");
    let off = b.len() - 1; // the `{`
    let (line, column) = line_column(&b, off);
    assert_eq!(line, 2); // after the first CRLF
                         // column is the byte offset from the start of line 2
    assert_eq!(column as usize, off - ("\r\n".len() + 3)); // 3 = BOM
}

// ============================================================
// Prior coverage kept meaningful (regression net)
// ============================================================

#[test]
fn empty_project_is_not_applicable() {
    let (req, packed) = build_request(&[], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::NotApplicable);
    assert_eq!(r.rule_results.len(), 1);
    assert!(r.unavailable_capabilities.contains(&"pdfFacts".to_string()));
    assert!(r
        .unavailable_capabilities
        .contains(&"pdfChecks".to_string()));
}

#[test]
fn empty_text_file_is_pass() {
    let (req, packed) = build_request(&[("f1", "main.tex", b"")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert_eq!(f.engine_outcome, Outcome::Pass);
    assert_eq!(f.token_count, 0);
}

#[test]
fn matched_braces_no_diagnostic() {
    let (req, packed) = build_request(&[("f1", "main.tex", b"plain {a} {b} text")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty());
}

#[test]
fn unmatched_close_is_fact_only_no_diagnostic() {
    let (req, packed) = build_request(&[("f1", "main.tex", b"ab }")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty());
    let f = file(&r, "f1");
    assert!(f
        .tokens
        .iter()
        .any(|t| t.note.as_deref() == Some("unpaired-close")));
}

#[test]
fn two_files_identical_local_offsets_keep_attribution() {
    let (req, packed) = build_request(&[("a", "a.tex", b"{x"), ("b", "b.tex", b"{y")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.diagnostics.len(), 2);
    let ids: Vec<&str> = r.diagnostics.iter().map(|d| d.file_id.as_str()).collect();
    assert!(ids.contains(&"a") && ids.contains(&"b"));
    for d in &r.diagnostics {
        let s = d.span.as_ref().unwrap();
        assert_eq!(s.start_byte, 0);
        assert_eq!(s.end_byte, 1);
    }
    assert_eq!(r.aggregate_outcome, Outcome::Fail);
}

#[test]
fn bom_inclusive_span_after_first_crlf_with_multilingual() {
    let mut src = b"\xEF\xBB\xBF".to_vec();
    src.extend_from_slice(b"\r\n");
    src.extend_from_slice("Привіт 🇺🇦 e\u{0301}".as_bytes());
    src.extend_from_slice(b" {");
    let (req, packed) = build_request_with(&[("f1", "main.tex", &src, true, "text")], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Fail);
    let d = &r.diagnostics[0];
    let s = d.span.as_ref().unwrap();
    assert_eq!(s.start_byte as usize, src.len() - 1);
    assert_eq!(s.end_byte as usize, src.len());
    assert!(std_boundary(&src, s.start_byte as usize));
    assert!(std_boundary(&src, s.end_byte as usize));
    assert_eq!(file(&r, "f1").newline_kind, NewlineKind::Crlf);
}

#[test]
fn every_token_span_is_scalar_boundary() {
    let src = "Привіт 🇺🇦 e\u{0301} {group} %comment\n".as_bytes();
    let (req, packed) = build_request(&[("f1", "main.tex", src)], None);
    let r = result_of(&run(&req, &packed));
    for f in &r.facts.files {
        for t in &f.tokens {
            assert!(std_boundary(src, t.start_byte as usize));
            assert!(std_boundary(src, t.end_byte as usize));
            assert!(t.start_byte <= t.end_byte);
        }
    }
}

#[test]
fn comments_and_escapes_are_literal() {
    let src = br"a\%b \{c\} d\\e %comment
f";
    let (req, packed) = build_request(&[("f1", "main.tex", src)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty());
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::Comment));
    assert_eq!(
        f.tokens
            .iter()
            .filter(|t| t.kind == TokenKind::Escape)
            .count(),
        4
    );
}

#[test]
fn binary_file_is_not_applicable() {
    let (req, packed) = build_request_with(
        &[("bin", "asset.png", b"\xFF\xFE\x00\x01", false, "binary")],
        None,
    );
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "bin");
    assert_eq!(f.engine_outcome, Outcome::NotApplicable);
    assert_eq!(r.aggregate_outcome, Outcome::NotApplicable);
    assert!(r.coverage.unsupported.iter().any(|s| s == "binary-file"));
}

#[test]
fn malformed_metadata_returns_invalid_input() {
    let resp_json = analyze_source("not json", b"");
    let resp: TestResponse = serde_json::from_str(&resp_json).expect("valid JSON");
    assert_public_invariants(&resp);
    assert_eq!(resp.status, "failed");
    assert_eq!(resp.error.unwrap().code, "InvalidInput");
    assert_eq!(resp.request_id, None);
}

#[test]
fn wrong_protocol_version_returns_unsupported_protocol() {
    let req = serde_json::json!({
        "protocolVersion": 2, "requestId": RID, "operation": "analyzeSource",
        "payload": {"snapshotId": SID, "projectId": PID, "sessionId": SESS,
        "workingGeneration": 0, "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
        "limits": {}, "files": []}
    });
    let resp = run(&serde_json::to_string(&req).unwrap(), b"");
    assert_eq!(failed(&resp).code, "UnsupportedProtocol");
}

#[test]
fn wrong_operation_returns_unsupported_capability() {
    let req = serde_json::json!({
        "protocolVersion": 1, "requestId": RID, "operation": "frobnicate",
        "payload": {"snapshotId": SID, "projectId": PID, "sessionId": SESS,
        "workingGeneration": 0, "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
        "limits": {}, "files": []}
    });
    let resp = run(&serde_json::to_string(&req).unwrap(), b"");
    assert_eq!(failed(&resp).code, "UnsupportedCapability");
}

#[test]
fn duplicate_file_id_returns_invalid_input() {
    let (req, packed) = build_request(&[("f1", "a.tex", b"x"), ("f1", "b.tex", b"y")], None);
    let resp = run(&req, &packed);
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn out_of_bounds_slice_returns_invalid_input() {
    let req = serde_json::json!({
        "protocolVersion": 1, "requestId": RID, "operation": "analyzeSource",
        "payload": {"snapshotId": SID, "projectId": PID, "sessionId": SESS,
        "workingGeneration": 0, "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
        "limits": {}, "files": [{"fileId": "f1", "relativePath": "a.tex",
        "mediaKind": "text", "hasBom": false, "sha256": "", "byteOffset": 0,
        "byteLength": 10}]}
    });
    let resp = run(&serde_json::to_string(&req).unwrap(), b"short");
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn invalid_utf8_text_file_returns_invalid_input() {
    let bad = b"\xFF\xFE";
    let (req, packed) = build_request_with(&[("f1", "a.tex", bad, false, "text")], None);
    let resp = run(&req, &packed);
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn bom_mismatch_returns_invalid_input() {
    let bytes = b"\xEF\xBB\xBFhello";
    let (req, packed) = build_request_with(&[("f1", "a.tex", bytes, false, "text")], None);
    let resp = run(&req, &packed);
    assert_eq!(failed(&resp).code, "InvalidInput");
}

#[test]
fn request_limits_cannot_elevate_defaults() {
    let big = vec![b'{'; 9 * 1024 * 1024];
    let limits = r#"{"maxBytesPerFile": 999000000}"#;
    let (req, packed) = build_request_with(&[("f1", "a.tex", &big, false, "text")], Some(limits));
    let resp = run(&req, &packed);
    assert_eq!(failed(&resp).code, "QuotaExceeded");
}

#[test]
fn unknown_constructs_do_not_invent_knu_or_tex_claims() {
    let src = br"\unknownmacro{x} \[weird]";
    let (req, packed) = build_request(&[("f1", "main.tex", src)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.rule_results.len(), 1);
    assert_eq!(r.rule_results[0].rule_id, "ENGINE-STRUCT-001");
    assert!(r.unavailable_capabilities.contains(&"pdfFacts".to_string()));
    assert!(r
        .unavailable_capabilities
        .contains(&"pdfChecks".to_string()));
}

#[test]
fn deterministic_diagnostics_for_same_input() {
    let src = b"a {b {c} d";
    let (req, packed) = build_request(&[("f1", "main.tex", src)], None);
    let r1 = result_of(&run(&req, &packed));
    let r2 = result_of(&run(&req, &packed));
    assert_eq!(r1.diagnostics.len(), r2.diagnostics.len());
    assert_eq!(r1.diagnostics[0].id, r2.diagnostics[0].id);
    assert_eq!(r1.diagnostics[0].span, r2.diagnostics[0].span);
    assert_eq!(r1.aggregate_outcome, r2.aggregate_outcome);
}

#[test]
fn large_text_is_run_collected_not_one_token_per_byte() {
    let src: Vec<u8> = (0..100_000).map(|_| b'x').collect();
    let (req, packed) = build_request(&[("f1", "a.tex", &src)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    assert_eq!(f.token_count, 1, "ordinary text is a single run token");
    assert_eq!(f.engine_outcome, Outcome::Pass);
}

#[test]
fn initialize_advertises_capabilities_and_echoes_only_request_id() {
    let req = serde_json::json!({
        "protocolVersion": 1, "requestId": "init-1", "operation": "initialize", "payload": {}
    });
    let resp = deserialize(&crate::analysis::initialize(
        &serde_json::to_string(&req).unwrap(),
    ));
    assert_public_invariants(&resp);
    assert_eq!(resp.status, "completed");
    assert_eq!(resp.request_id.as_deref(), Some("init-1"));
    let payload = resp.payload.unwrap();
    let ops = payload.get("operations").unwrap().as_array().unwrap();
    assert!(ops.iter().any(|v| v.as_str() == Some("analyzeSource")));
    assert!(payload
        .get("limits")
        .unwrap()
        .get("maxBytesPerFile")
        .is_some());
    assert!(payload.get("snapshotId").is_none());
    assert!(payload.get("profile").is_none());
}

#[test]
fn initialize_rejects_unsupported_protocol() {
    let req = serde_json::json!({
        "protocolVersion": 9, "requestId": "init-2", "operation": "initialize", "payload": {}
    });
    let resp = deserialize(&crate::analysis::initialize(
        &serde_json::to_string(&req).unwrap(),
    ));
    assert_eq!(failed(&resp).code, "UnsupportedProtocol");
}

// ============================================================
// Round 2 — Defect 1: starred \verb delimiter consumed exactly once
// ============================================================

#[test]
fn verb_star_double_star_delimiter_consumed_once() {
    // `\verb**{*` (9 bytes): \verb* with delimiter '*', body '{', closing '*'.
    // The optional `*` was consumed during control-word parsing; the \verb
    // branch must NOT re-check for `*` and treat '{' as the delimiter.
    // Expected: one Verbatim token covering the whole input, no diagnostic, Pass.
    let src = br"\verb**{*";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty());
    let f = file(&r, "f1");
    let verbs: Vec<_> = f
        .tokens
        .iter()
        .filter(|t| t.kind == TokenKind::Verbatim)
        .collect();
    assert_eq!(verbs.len(), 1, "exactly one verbatim token");
    assert_eq!(verbs[0].start_byte, 0);
    assert_eq!(verbs[0].end_byte, 9); // covers the whole 9-byte input
    assert!(std_boundary(bytes, verbs[0].start_byte as usize));
    assert!(std_boundary(bytes, verbs[0].end_byte as usize));
}

#[test]
fn verb_star_pipe_delimiter_with_brace_body() {
    // `\verb*|{x}|` — the `*` variant with a `|` delimiter; `{`/`}` are literal.
    let src = br"\verb*|{x}|";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert_eq!(
        f.tokens
            .iter()
            .filter(|t| t.kind == TokenKind::Verbatim)
            .count(),
        1
    );
}

#[test]
fn verb_unstarred_pipe_delimiter_with_brace_body() {
    // `\verb|{x}|` — no `*`; the `|` delimiter; `{`/`}` are literal.
    let src = br"\verb|{x}|";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert_eq!(
        f.tokens
            .iter()
            .filter(|t| t.kind == TokenKind::Verbatim)
            .count(),
        1
    );
}

#[test]
fn verb_whitespace_delimiter_falls_through_to_command() {
    // `\verb ` — the only char after \verb is whitespace, so it cannot be a
    // delimiter; \verb falls through to an ordinary command. No verbatim token.
    let src = br"\verb ";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().all(|t| t.kind != TokenKind::Verbatim));
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::Command));
}

#[test]
fn verb_forms_without_closing_delimiter_are_indeterminate() {
    // The same delimiter forms as the Pass fixtures, but WITHOUT a closing
    // delimiter => a verbatim-to-EOF token with `verbatim-unterminated`, and
    // the file is Indeterminate. The first `*` after `\verb` is always the
    // optional star (never an unstarred `*` delimiter); the NEXT scalar is the
    // delimiter. So `\verb**{` is `\verb*` (star) + `*` delimiter + body `{`
    // with no closing `*`.
    let cases: &[&[u8]] = &[
        r"\verb|x".as_bytes(),  // unstarred, `|` delimiter, no close
        r"\verb*|x".as_bytes(), // starred, `|` delimiter, no close
        r"\verb**{".as_bytes(), // first `*` = optional star, `*` delimiter, no close
        r"\verbé{x".as_bytes(), // Unicode (whole-scalar) delimiter, no close
    ];
    for &src in cases {
        let (req, packed) = build_request(&[("f1", "main.tex", src)], None);
        let r = result_of(&run(&req, &packed));
        assert_eq!(
            r.aggregate_outcome,
            Outcome::Indeterminate,
            "unterminated \\verb should be Indeterminate (src {:?})",
            std::str::from_utf8(src).unwrap_or("<non-utf8>")
        );
        let f = file(&r, "f1");
        assert!(
            f.tokens.iter().any(|t| t.kind == TokenKind::Verbatim),
            "a verbatim-to-EOF token is present (src {:?})",
            std::str::from_utf8(src).unwrap_or("<non-utf8>")
        );
        assert!(r.diagnostics.is_empty(), "no unmatched-open diagnostic");
    }
}

// ============================================================
// Round 2 — Defect 2: whitespace/comment before the mandatory group
// ============================================================

#[test]
fn input_space_before_group_extracts_fact() {
    // `\input {chapter}` — a space between the command and the mandatory group.
    let src = br"\input {chapter}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.text.as_deref(), Some("chapter"));
    // interior span = bytes[8..15) == "chapter"
    assert_eq!(
        &bytes[inc.start_byte as usize..inc.end_byte as usize],
        "chapter".as_bytes()
    );
    assert!(std_boundary(bytes, inc.start_byte as usize));
    assert!(std_boundary(bytes, inc.end_byte as usize));
}

#[test]
fn label_newline_before_group_extracts_fact() {
    // `\label` then a REAL newline (0x0A) then `{item}` — a newline between the
    // command and the group. (Use a regular byte string, not a raw `br"..."`, so
    // `\n` is a newline, not a literal backslash-n control symbol.)
    let src = b"\\label\n{item}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    let lbl = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::Label)
        .expect("label fact");
    assert_eq!(lbl.text.as_deref(), Some("item"));
    assert_eq!(
        &bytes[lbl.start_byte as usize..lbl.end_byte as usize],
        "item".as_bytes()
    );
}

#[test]
fn input_crlf_before_group_extracts_fact() {
    // `\input\r\n{chapter}` — a CRLF between the command and the group.
    let src = b"\\input\r\n{chapter}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.text.as_deref(), Some("chapter"));
    assert_eq!(
        &bytes[inc.start_byte as usize..inc.end_byte as usize],
        "chapter".as_bytes()
    );
}

#[test]
fn input_space_and_comment_before_group_extracts_fact() {
    // `\input % c\n{chapter}` — a space, a comment to end of line, then the group.
    let src = b"\\input % c\n{chapter}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.text.as_deref(), Some("chapter"));
    assert_eq!(
        &bytes[inc.start_byte as usize..inc.end_byte as usize],
        "chapter".as_bytes()
    );
    // The main scan still visited the comment (one Comment token) and balanced braces.
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::Comment));
}

#[test]
fn input_no_group_emits_no_fact() {
    // `\input \foo` — a non-group command follows; no mandatory group, no fact.
    let src = br"\input \foo";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().all(|t| t.kind != TokenKind::IncludePath));
}

#[test]
fn input_nested_group_is_nonliteral_indeterminate() {
    // `\input{a{b}c}` — nested braces in the argument => nonliteral/Indeterminate.
    let src = br"\input{a{b}c}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact emitted");
    assert!(inc.text.is_none());
    assert_eq!(inc.note.as_deref(), Some("nonliteral-argument"));
}

#[test]
fn input_escaped_brace_in_group_is_nonliteral_indeterminate() {
    // `\input{a\{b}` — an escaped brace => nonliteral/Indeterminate.
    let src = br"\input{a\{b}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact emitted");
    assert!(inc.text.is_none());
    assert_eq!(inc.note.as_deref(), Some("nonliteral-argument"));
}

#[test]
fn input_unicode_argument_text_preserves_scalar_boundaries() {
    // `\input{Привіт}` — Unicode argument text, literal, scalar-boundary spans.
    let src = "\\input{Привіт}".as_bytes();
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.text.as_deref(), Some("Привіт"));
    assert_eq!(
        &bytes[inc.start_byte as usize..inc.end_byte as usize],
        "Привіт".as_bytes()
    );
    assert!(std_boundary(bytes, inc.start_byte as usize));
    assert!(std_boundary(bytes, inc.end_byte as usize));
}

#[test]
fn input_and_label_with_whitespace_both_extract_facts() {
    // The independently reproduced case: `\input {chapter}\label {item}`.
    let src = br"\input {chapter}\label {item}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.text.as_deref(), Some("chapter"));
    let lbl = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::Label)
        .expect("label fact");
    assert_eq!(lbl.text.as_deref(), Some("item"));
}

// ============================================================
// Round 2 — Defect 3: both argument lookaheads honor comments
// ============================================================

#[test]
fn optional_close_only_in_eof_comment_is_unterminated() {
    // `\foo[%]` (7 bytes): the only `]` is inside a comment that runs to EOF;
    // there is no actual optional close => Indeterminate, no OptionalArg fact.
    let src = br"\foo[%]";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().all(|t| t.kind != TokenKind::OptionalArg));
    assert!(
        r.diagnostics.is_empty(),
        "the EOF comment itself is not an error"
    );
}

#[test]
fn optional_close_after_comment_line_uses_real_close() {
    // `\foo[%]\n]` — a fake `]` in a comment, then the real `]` on the next line.
    let src = b"\\foo[%]\n]";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::OptionalArg));
}

#[test]
fn braces_in_comment_do_not_change_depth() {
    // `{a%{b}\nc}` — the `%{b}` comment hides a `{`/`}`; the group closes at the final `}`.
    let src = b"{a%{b}\nc}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(
        r.diagnostics.is_empty(),
        "braces in the comment do not count"
    );
}

#[test]
fn escaped_percent_in_group_is_not_a_comment() {
    // `{a\%b}` — `\%` is a literal percent (escape), not a comment; the group closes.
    let src = br"{a\%b}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    assert!(r.diagnostics.is_empty(), "\\% is not a comment");
}

#[test]
fn escaped_bracket_is_not_an_optional_close() {
    // `\foo[\]a]` — `\]` is an escaped bracket (not a close); the final `]` closes.
    let src = br"\foo[\]a]";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::OptionalArg));
}

#[test]
fn bracket_nested_in_brace_group_is_not_optional_close() {
    // `\foo[a{b]c}d]` — the `]` inside `{b]c}` is at brace depth 1, not the close;
    // the final `]` at depth 0 closes.
    let src = br"\foo[a{b]c}d]";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let f = file(&r, "f1");
    assert!(f.tokens.iter().any(|t| t.kind == TokenKind::OptionalArg));
}

// ============================================================
// Round 2 — Defect 4: bounded lookahead byte-work budget
// ============================================================

#[test]
fn small_ordinary_fixture_stays_complete() {
    // A small ordinary fixture must remain complete (Pass) with facts intact.
    let src = br"\foo{bar}\input{x}";
    let bytes: &[u8] = src;
    let (req, packed) = build_request(&[("f1", "main.tex", bytes)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    assert!(!f.truncated);
    assert_eq!(f.engine_outcome, Outcome::Pass);
    assert_eq!(r.aggregate_outcome, Outcome::Pass);
    let inc = f
        .tokens
        .iter()
        .find(|t| t.kind == TokenKind::IncludePath)
        .expect("includePath fact");
    assert_eq!(inc.text.as_deref(), Some("x"));
}

#[test]
fn repeated_incomplete_optional_commands_are_bounded() {
    // Repeated `\foo[` (unterminated optional) + a long plain tail would rescan
    // the tail thousands of times without the shared lookahead budget. The
    // budget bounds total lookahead work to ~bytes.len()*4; on exhaustion the
    // file stops (truncated/Indeterminate) with NO hypothetical unmatched warnings.
    let mut src = Vec::new();
    for _ in 0..200 {
        src.extend_from_slice(br"\foo[");
    }
    src.extend_from_slice(&vec![b'x'; 8000]);
    let (req, packed) = build_request(&[("f1", "a.tex", &src)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    assert!(f.truncated, "lookahead exhaustion truncates the file");
    assert_eq!(f.engine_outcome, Outcome::Indeterminate);
    assert_eq!(r.aggregate_outcome, Outcome::Indeterminate);
    assert!(
        r.diagnostics.is_empty(),
        "no guessed unmatched braces on exhaustion"
    );
    assert!(
        r.rule_results[0]
            .limitations
            .iter()
            .any(|l| l.contains("lookahead byte-work budget exhausted")),
        "explicit limitation metadata present"
    );
}

#[test]
fn repeated_incomplete_group_commands_are_bounded() {
    // Repeated `\input{` (unterminated mandatory group) + a long plain tail.
    let mut src = Vec::new();
    for _ in 0..200 {
        src.extend_from_slice(br"\input{");
    }
    src.extend_from_slice(&vec![b'x'; 8000]);
    let (req, packed) = build_request(&[("f1", "a.tex", &src)], None);
    let r = result_of(&run(&req, &packed));
    let f = file(&r, "f1");
    assert!(f.truncated, "lookahead exhaustion truncates the file");
    assert_eq!(f.engine_outcome, Outcome::Indeterminate);
    assert!(
        r.diagnostics.is_empty(),
        "no guessed unmatched braces on exhaustion"
    );
    assert!(
        r.rule_results[0]
            .limitations
            .iter()
            .any(|l| l.contains("lookahead byte-work budget exhausted")),
        "explicit limitation metadata present"
    );
}

// ============================================================
// Round 2 — Defect 5: uniform request-id validation/correlation
// ============================================================

/// Build an analyzeSource envelope JSON value with a custom requestId and the
/// base payload (no files). Used to exercise the request-id boundary.
fn analyze_env_with_rid(rid: serde_json::Value) -> String {
    serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1,
        "requestId": rid,
        "operation": "analyzeSource",
        "payload": {
            "snapshotId": SID, "projectId": PID, "sessionId": SESS,
            "workingGeneration": 0,
            "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
            "limits": {}, "files": []
        }
    }))
    .unwrap()
}

/// Build an analyzeSource envelope JSON value WITHOUT a requestId key.
fn analyze_env_without_rid() -> String {
    serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1,
        "operation": "analyzeSource",
        "payload": {
            "snapshotId": SID, "projectId": PID, "sessionId": SESS,
            "workingGeneration": 0,
            "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
            "limits": {}, "files": []
        }
    }))
    .unwrap()
}

fn init_env_with_rid(rid: serde_json::Value) -> String {
    serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1, "requestId": rid, "operation": "initialize", "payload": {}
    }))
    .unwrap()
}

fn init_env_without_rid() -> String {
    serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1, "operation": "initialize", "payload": {}
    }))
    .unwrap()
}

#[test]
fn initialize_rejects_missing_request_id() {
    let resp = deserialize(&crate::analysis::initialize(&init_env_without_rid()));
    let e = failed(&resp);
    assert_eq!(e.code, "InvalidInput");
    assert_eq!(resp.request_id, None, "no trusted requestId echoed");
}

#[test]
fn initialize_rejects_empty_request_id() {
    let resp = deserialize(&crate::analysis::initialize(&init_env_with_rid(
        serde_json::json!(""),
    )));
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None);
}

#[test]
fn initialize_rejects_non_string_request_id() {
    let resp = deserialize(&crate::analysis::initialize(&init_env_with_rid(
        serde_json::json!(7),
    )));
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None);
}

#[test]
fn initialize_rejects_oversize_request_id_small_envelope() {
    let big = "x".repeat(200);
    let raw = crate::analysis::initialize(&init_env_with_rid(serde_json::json!(big)));
    let resp = deserialize(&raw);
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None, "oversize id is not echoed");
    assert!(raw.len() < 4096, "envelope stays small");
}

#[test]
fn analyze_rejects_missing_request_id() {
    let resp = run(&analyze_env_without_rid(), b"");
    let e = failed(&resp);
    assert_eq!(e.code, "InvalidInput");
    assert_eq!(resp.request_id, None);
}

#[test]
fn analyze_rejects_empty_request_id() {
    let resp = run(&analyze_env_with_rid(serde_json::json!("")), b"");
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None);
}

#[test]
fn analyze_rejects_non_string_request_id() {
    let resp = run(&analyze_env_with_rid(serde_json::json!(7)), b"");
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None);
}

#[test]
fn analyze_rejects_oversize_request_id_small_envelope() {
    let big = "x".repeat(200);
    let raw = analyze_source(&analyze_env_with_rid(serde_json::json!(big)), b"");
    let resp: TestResponse = serde_json::from_str(&raw).expect("valid JSON");
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None, "oversize id is not echoed");
    assert!(raw.len() < 4096, "envelope stays small");
}

#[test]
fn unsupported_protocol_with_oversize_request_id_is_small_failure() {
    // requestId is validated FIRST: even though protocolVersion is also wrong,
    // the oversize id wins => small InvalidInput with no trusted id.
    let big = "x".repeat(200);
    let req = serde_json::to_string(&serde_json::json!({
        "protocolVersion": 2,
        "requestId": big,
        "operation": "analyzeSource",
        "payload": {
            "snapshotId": SID, "projectId": PID, "sessionId": SESS,
            "workingGeneration": 0,
            "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
            "limits": {}, "files": []
        }
    }))
    .unwrap();
    let raw = analyze_source(&req, b"");
    let resp: TestResponse = serde_json::from_str(&raw).expect("valid JSON");
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id, None);
    assert!(raw.len() < 4096, "stays small");
}

#[test]
fn valid_request_id_with_bad_file_id_echoes_original() {
    // A valid requestId plus an empty fileId echoes the original id on failure.
    let req = serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1, "requestId": RID, "operation": "analyzeSource",
        "payload": {
            "snapshotId": SID, "projectId": PID, "sessionId": SESS,
            "workingGeneration": 0,
            "profile": {"id": PROFILE_ID, "version": PROFILE_VER},
            "limits": {},
            "files": [{"fileId": "", "relativePath": "a.tex", "mediaKind": "text",
                "hasBom": false, "sha256": "", "byteOffset": 0, "byteLength": 1}]
        }
    }))
    .unwrap();
    let resp = run(&req, b"x");
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id.as_deref(), Some(RID), "original id is kept");
}

#[test]
fn valid_request_id_with_bad_profile_echoes_original() {
    // A valid requestId plus an empty profile.id echoes the original id.
    let req = serde_json::to_string(&serde_json::json!({
        "protocolVersion": 1, "requestId": RID, "operation": "analyzeSource",
        "payload": {
            "snapshotId": SID, "projectId": PID, "sessionId": SESS,
            "workingGeneration": 0,
            "profile": {"id": "", "version": PROFILE_VER},
            "limits": {}, "files": []
        }
    }))
    .unwrap();
    let resp = run(&req, b"");
    assert_eq!(failed(&resp).code, "InvalidInput");
    assert_eq!(resp.request_id.as_deref(), Some(RID), "original id is kept");
}
