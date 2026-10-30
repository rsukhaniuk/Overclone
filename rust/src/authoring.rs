//! Offline, bounded literal authoring assistance. Pure Rust: no filesystem,
//! network, clock or editor mutation. Worker wall-time termination and consumer
//! snapshot/caret/lock authority are deliberately outside this producer.
pub mod catalog;
mod dto;
mod project;
#[cfg(test)]
mod tests;

use crate::analysis::{self, dto::Span, lexer, mapping, reference_wire};
use dto::*;
use serde::Serialize;
use std::collections::HashSet;

const MAX_RESPONSE: usize = 262_144;

/// Canonical authoringAssist v1 entrypoint, independent of compilation/profiles.
pub fn assist(metadata: &str, packed: &[u8]) -> String {
    if metadata.len() > analysis::dto::MAX_METADATA_JSON_BYTES {
        return analysis::failed_response(None, "QuotaExceeded", "metadata limit exceeded");
    }
    let id = match analysis::recover_request_id(metadata) {
        Ok(id) => id,
        Err(_) => {
            return analysis::failed_response(None, "InvalidInput", "invalid request identity")
        }
    };
    match run(metadata, packed) {
        Ok((payload, cap)) => {
            #[derive(Serialize)]
            #[serde(rename_all = "camelCase")]
            struct Completed<'a> {
                protocol_version: u32,
                request_id: &'a str,
                status: &'static str,
                payload: ResultPayload,
            }
            let mut writer = analysis::CappedWriter::new(cap);
            if serde_json::to_writer(
                &mut writer,
                &Completed {
                    protocol_version: 1,
                    request_id: &id,
                    status: "completed",
                    payload,
                },
            )
            .is_err()
            {
                analysis::failed_response(
                    Some(id),
                    "QuotaExceeded",
                    "authoring result limit exceeded",
                )
            } else {
                writer.into_string()
            }
        }
        Err(code) => {
            analysis::failed_response(Some(id), code, "authoring request could not be completed")
        }
    }
}

fn run(metadata: &str, packed: &[u8]) -> Result<(ResultPayload, usize), &'static str> {
    strict_shape(metadata)?;
    let request: Request = serde_json::from_str(metadata).map_err(|_| "InvalidInput")?;
    if request.protocol_version != 1 {
        return Err("UnsupportedProtocol");
    }
    if request.operation != "authoringAssist" {
        return Err("UnsupportedCapability");
    }
    if request.request_id.is_empty() {
        return Err("InvalidInput");
    }
    let p = request.payload;
    if p.catalog_version != catalog::VERSION {
        return Err("UnsupportedCapability");
    }
    if p.query.len() > 1024 {
        return Err("QuotaExceeded");
    }
    for id in [
        &p.snapshot_id,
        &p.project_id,
        &p.session_id,
        &p.root_file_id,
        &p.file_id,
    ] {
        if id.is_empty() || id.len() > analysis::MAX_ID_BYTES {
            return Err("InvalidInput");
        }
    }
    if p.base_head_revision_id
        .as_ref()
        .is_some_and(|s| s.is_empty() || s.len() > analysis::MAX_ID_BYTES)
        || p.working_generation < 0
        || p.working_generation > 9_007_199_254_740_991
    {
        return Err("InvalidInput");
    }
    if p.limits
        .max_result_bytes
        .is_some_and(|n| n > MAX_RESPONSE as u64)
    {
        return Err("QuotaExceeded");
    }
    let limits = analysis::resolve_limits(&p.limits);
    // Unlike legacy analysis's historical clamping, this new operation refuses
    // attempts to exceed a hard budget or use zero as unlimited.
    macro_rules! limit {
        ($($field:ident),*)=>{$(if p.limits.$field.is_some_and(|v| v==0 || v>limits.$field) {return Err("QuotaExceeded");})*};
    }
    limit!(
        max_bytes_per_file,
        max_files,
        max_total_bytes,
        max_result_bytes,
        max_tokens_per_file,
        max_tokens_per_project,
        max_diagnostics_per_project,
        max_brace_depth
    );
    if p.files.len() > limits.max_files as usize || packed.len() as u64 > limits.max_total_bytes {
        return Err("QuotaExceeded");
    }
    let mut ids = HashSet::new();
    let mut paths = HashSet::new();
    let mut bytes = Vec::with_capacity(p.files.len());
    let mut total = 0u64;
    for f in &p.files {
        if f.file_id.is_empty()
            || f.file_id.len() > 128
            || !ids.insert(&f.file_id)
            || f.relative_path.len() > analysis::MAX_PATH_BYTES
            || f.relative_path.trim() != f.relative_path
            || !analysis::valid_project_path(&f.relative_path)
            || f.relative_path.starts_with("__oop_runtime__/")
            || !paths.insert(&f.relative_path)
            || !matches!(f.media_kind.as_str(), "text" | "binary")
        {
            return Err("InvalidInput");
        }
        let (off, len) = analysis::checked_slice(f, packed.len()).map_err(|_| "InvalidInput")?;
        total = total.checked_add(len).ok_or("QuotaExceeded")?;
        if len > limits.max_bytes_per_file || total > limits.max_total_bytes {
            return Err("QuotaExceeded");
        }
        let b = &packed[off..off + len as usize];
        if f.media_kind == "text"
            && (std::str::from_utf8(b).is_err() || f.has_bom != mapping::has_bom(b))
        {
            return Err("InvalidInput");
        }
        bytes.push(b);
    }
    let root = p
        .files
        .iter()
        .position(|f| f.file_id == p.root_file_id)
        .ok_or("InvalidInput")?;
    let selected = p
        .files
        .iter()
        .position(|f| f.file_id == p.file_id)
        .ok_or("InvalidInput")?;
    if p.files[root].media_kind != "text"
        || !analysis::references::tex(&p.files[root].relative_path)
        || p.files[selected].media_kind != "text"
    {
        return Err("InvalidInput");
    }
    let caret = usize::try_from(p.caret_byte).map_err(|_| "InvalidInput")?;
    let source = std::str::from_utf8(bytes[selected]).map_err(|_| "InvalidInput")?;
    if caret > source.len()
        || !source.is_char_boundary(caret)
        || (p.files[selected].has_bom && caret < 3)
    {
        return Err("InvalidInput");
    }
    let mut project = project::Project::new(&p.files, bytes, limits);
    project.visit(root, 0)?;
    let mut result = ResultPayload {
        snapshot_id: p.snapshot_id.clone(),
        root_file_id: p.root_file_id.clone(),
        file_id: p.file_id.clone(),
        caret_byte: p.caret_byte,
        catalog_version: catalog::VERSION,
        complete: true,
        limitations: vec![
            "sourceIntentOnly",
            "macroExpansionUnsupported",
            "runtimeResourcesUnverified",
        ],
        candidates: vec![],
        outline: vec![],
        loaded_packages: vec![],
    };
    let replace = prefix(&p, source, caret, &project.tokens[selected])?;
    if project.visited[selected] != 2 {
        project.limitations.insert("fileOutsideRootClosure");
    } else if p.context != Context::Outline {
        if let Some(replace) = replace {
            candidates(&p, &project, &replace, &mut result.candidates)?;
        } else {
            project.limitations.insert("incompatibleCaretContext");
        }
    }
    result.complete = project.limitations.is_empty();
    result.limitations.extend(project.limitations);
    result.outline = project.outline;
    result.loaded_packages = project.packages;
    Ok((result, (limits.max_result_bytes as usize).min(MAX_RESPONSE)))
}

fn strict_shape(json: &str) -> Result<(), &'static str> {
    let v = reference_wire::unique_value(json).ok_or("InvalidInput")?;
    let p = &v["payload"];
    if !reference_wire::keys(
        &v,
        &["protocolVersion", "requestId", "operation", "payload"],
    ) || !reference_wire::keys(
        p,
        &[
            "snapshotId",
            "projectId",
            "sessionId",
            "workingGeneration",
            "baseHeadRevisionId",
            "rootFileId",
            "fileId",
            "caretByte",
            "context",
            "query",
            "catalogVersion",
            "files",
            "limits",
        ],
    ) || !reference_wire::keys(
        &p["limits"],
        &[
            "maxBytesPerFile",
            "maxFiles",
            "maxTotalBytes",
            "maxResultBytes",
            "maxTokensPerFile",
            "maxTokensPerProject",
            "maxDiagnosticsPerProject",
            "maxBraceDepth",
        ],
    ) || !p["files"].as_array().is_some_and(|files| {
        files.iter().all(|f| {
            reference_wire::keys(
                f,
                &[
                    "fileId",
                    "relativePath",
                    "mediaKind",
                    "hasBom",
                    "sha256",
                    "byteOffset",
                    "byteLength",
                ],
            )
        })
    }) || !p["limits"]
        .as_object()
        .is_some_and(|limits| limits.values().all(|v| v.as_u64().is_some()))
    {
        return Err("InvalidInput");
    }
    Ok(())
}

struct Prefix {
    span: Span,
    path_command: Option<&'static str>,
}
impl Prefix {
    fn observed(start: usize, end: usize) -> Self {
        Self {
            span: project::span(start, end),
            path_command: None,
        }
    }
}

/// Query must be the exact observed prefix, never an arbitrary replacement
/// range supplied by the caller. No proposals in comments/verbatim or at a
/// mid-token caret. Definition/outline are read-only navigation operations.
fn prefix(
    p: &Payload,
    source: &str,
    caret: usize,
    tokens: &[analysis::dto::Token],
) -> Result<Option<Prefix>, &'static str> {
    if p.context == Context::Outline {
        return Ok(Some(Prefix::observed(caret, caret)));
    }
    let Some(start) = caret.checked_sub(p.query.len()) else {
        return Ok(None);
    };
    if !source.is_char_boundary(start) || source.get(start..caret) != Some(p.query.as_str()) {
        return Ok(None);
    }
    use analysis::dto::TokenKind;
    if tokens.iter().any(|t| {
        matches!(t.kind, TokenKind::Comment | TokenKind::Verbatim)
            && start >= t.start_byte as usize
            && start < t.end_byte as usize
    }) {
        return Ok(None);
    }
    // The shared lexer represents a recognized verbatim opener as one whole
    // command token. Its environment-name interior is still an explicit,
    // observed replacement span; the body remains excluded above.
    if p.context == Context::Environment
        && tokens.iter().any(|t| {
            t.kind == TokenKind::Command
                && t.start_byte as usize + 7 == start
                && t.end_byte as usize == caret + 1
                && source[t.start_byte as usize..].starts_with("\\begin{")
                && source.as_bytes().get(caret) == Some(&b'}')
        })
    {
        return Ok(Some(Prefix::observed(start, caret)));
    }
    if p.context == Context::Command {
        if start == 0
            || source.as_bytes()[start - 1] != b'\\'
            || !p.query.bytes().all(|b| b.is_ascii_alphabetic())
        {
            return Ok(None);
        }
        if !tokens.iter().any(|t| {
            t.start_byte == start as u64 - 1
                && t.end_byte == caret as u64
                && matches!(t.kind, TokenKind::Command | TokenKind::Escape)
        }) {
            return Ok(None);
        }
        return Ok(Some(Prefix::observed(start - 1, caret)));
    }
    if p.context == Context::Definition {
        // A command name or literal key (without its backslash) selected by the
        // explicit caller. Only exact matches are returned, not fuzzy relocation.
        return Ok(Some(Prefix::observed(start, caret)));
    }
    // Verify this prefix belongs to the literal mandatory group of an actual
    // lexer command, not an arbitrary brace or a macro body. Match only the
    // last comma-delimited key segment for cite/ref lists.
    let (_, depths, _) = analysis::ownership::index(tokens);
    let mut work = lexer::LookaheadBudget::new(source.len());
    for (i, t) in tokens.iter().enumerate().rev() {
        if t.kind != TokenKind::Command || t.end_byte as usize > start || depths[i] != 0 {
            continue;
        }
        let name = &source[t.start_byte as usize + 1..t.end_byte as usize];
        let allowed = match p.context {
            Context::Environment => matches!(name, "begin" | "end"),
            Context::Reference => matches!(name, "ref" | "eqref" | "pageref" | "cref" | "Cref"),
            Context::Citation => matches!(name, "cite" | "parencite" | "textcite" | "autocite"),
            Context::InputPath => matches!(
                name,
                "input" | "include" | "includegraphics" | "bibliography" | "addbibresource"
            ),
            _ => false,
        };
        if !allowed {
            continue;
        }
        let mut pos = t.end_byte as usize;
        // Existing lexer OptionalArg facts, including both cite notes where
        // present, are already balanced; skip only contiguous literal options.
        loop {
            while source
                .as_bytes()
                .get(pos)
                .is_some_and(u8::is_ascii_whitespace)
            {
                pos += 1;
            }
            if source.as_bytes().get(pos) != Some(&b'[') {
                break;
            }
            match lexer::find_optional_close(source.as_bytes(), pos + 1, &mut work) {
                lexer::LookaheadResult::Found(end) => pos = end + 1,
                lexer::LookaheadResult::Exhausted => return Err("QuotaExceeded"),
                _ => return Ok(None),
            }
        }
        let open = match lexer::skip_to_group_open(source.as_bytes(), pos, &mut work) {
            lexer::LookaheadResult::Found(open) => open,
            lexer::LookaheadResult::Exhausted => return Err("QuotaExceeded"),
            lexer::LookaheadResult::NotFound => continue,
        };
        {
            // Completion also works while the group is not yet closed, but
            // cannot escape a previous group's actual closing delimiter.
            if open + 1 > start {
                continue;
            }
            match lexer::find_group_close(source.as_bytes(), open + 1, &mut work) {
                lexer::LookaheadResult::Found(close) if caret > close => continue,
                lexer::LookaheadResult::Exhausted => return Err("QuotaExceeded"),
                _ => {}
            }
            let before =
                source[open + 1..start].trim_end_matches(|c: char| c.is_ascii_whitespace());
            let segment_ok = if matches!(p.context, Context::Citation | Context::Reference) {
                before.is_empty() || before.ends_with(',')
            } else {
                before.is_empty()
            };
            if segment_ok
                && !p.query.contains(['\\', '{', '}', '%', '\r', '\n'])
                && (caret == source.len() || source[caret..].starts_with(['}', ',']))
            {
                let path_command = match name {
                    "input" => Some("input"),
                    "include" => Some("include"),
                    "bibliography" => Some("bibliography"),
                    "addbibresource" => Some("addbibresource"),
                    _ => None,
                };
                return Ok(Some(Prefix {
                    span: project::span(start, caret),
                    path_command,
                }));
            }
        }
    }
    Ok(None)
}

fn candidates(
    p: &Payload,
    project: &project::Project<'_>,
    prefix: &Prefix,
    out: &mut Vec<Candidate>,
) -> Result<(), &'static str> {
    let replace = &prefix.span;
    if matches!(p.context, Context::Command | Context::Environment) {
        for entry in catalog::ENTRIES {
            // Prefer the applicable package/class signature over the same
            // built-in name, without duplicating one literal insertion.
            if entry.package.is_none()
                && catalog::ENTRIES.iter().any(|e| {
                    e.name == entry.name
                        && e.kind == entry.kind
                        && e.package
                            .is_some_and(|pkg| project.catalog_packages.contains(pkg))
                })
            {
                continue;
            }
            if out
                .iter()
                .any(|c| c.kind == entry.kind && c.label == entry.name)
            {
                continue;
            }
            if (p.context == Context::Environment) != (entry.kind == "environment")
                || !entry.name.starts_with(&p.query)
            {
                continue;
            }
            if entry
                .package
                .is_some_and(|pkg| !project.catalog_packages.contains(pkg))
            {
                continue;
            }
            let detail = format!(
                "{}; engines: {}; resources unverified{}; {}",
                entry.signature,
                entry.engines,
                entry
                    .package
                    .map(|_| "; requires literal package/class")
                    .unwrap_or(""),
                entry.provenance
            );
            push(
                out,
                entry.kind,
                entry.name.into(),
                detail,
                entry.insertion.into(),
                replace,
                if entry.package.is_some() {
                    "packageCatalog"
                } else {
                    "builtin"
                },
                None,
            )?;
        }
    }
    for fact in &project.facts {
        let applies = match p.context {
            Context::Command => fact.kind == "macro",
            Context::Environment => fact.kind == "environment",
            Context::Reference => fact.kind == "label",
            Context::Citation => fact.kind == "citation",
            Context::Definition => fact.label == p.query,
            _ => false,
        };
        if !applies || !fact.label.starts_with(&p.query) {
            continue;
        }
        let insertion = if fact.kind == "macro" {
            format!("\\{}{}", fact.label, "{}".repeat(fact.arguments))
        } else {
            fact.label.clone()
        };
        let origin = match fact.kind {
            "label" => "projectLabel",
            "citation" => "projectBibliography",
            _ => "projectDeclaration",
        };
        push(
            out,
            fact.kind,
            fact.label.clone(),
            fact.detail.clone(),
            insertion,
            replace,
            origin,
            Some(fact.definition.clone()),
        )?;
    }
    if p.context == Context::InputPath {
        for file in project.files {
            if !file.relative_path.starts_with(&p.query) {
                continue;
            }
            let insertion = match prefix.path_command {
                Some("include")
                    if file.media_kind == "text" && file.relative_path.ends_with(".tex") =>
                {
                    file.relative_path[..file.relative_path.len() - 4].to_string()
                }
                Some("include") => continue,
                Some("input") if file.media_kind != "text" => continue,
                Some("bibliography" | "addbibresource")
                    if file.media_kind != "text" || !file.relative_path.ends_with(".bib") =>
                {
                    continue
                }
                _ => file.relative_path.clone(),
            };
            // Only captured exact project-root targets. Include's insertion
            // omits .tex because LaTeX itself appends it; the label/definition
            // still identify the exact captured file. No filesystem probes.
            push(
                out,
                "path",
                file.relative_path.clone(),
                "Captured project path; input command/format compatibility not verified".into(),
                insertion,
                replace,
                "projectPath",
                (file.media_kind == "text").then(|| Definition {
                    file_id: file.file_id.clone(),
                    span: project::span(
                        if file.has_bom { 3 } else { 0 },
                        if file.has_bom { 3 } else { 0 },
                    ),
                }),
            )?;
        }
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn push(
    out: &mut Vec<Candidate>,
    kind: &'static str,
    label: String,
    detail: String,
    insert_text: String,
    replace: &Span,
    origin: &'static str,
    definition: Option<Definition>,
) -> Result<(), &'static str> {
    if out.len() >= 100
        || [label.len(), detail.len(), insert_text.len()]
            .iter()
            .any(|n| *n > 512)
    {
        return Err("QuotaExceeded");
    }
    // First empty argument is the insertion caret; otherwise end. String-derived
    // UTF16 count is always a scalar boundary, including non-BMP project keys.
    let caret = insert_text
        .find("{}")
        .map(|i| i + 1)
        .unwrap_or(insert_text.len());
    let caret_offset_utf16 = insert_text[..caret].encode_utf16().count();
    out.push(Candidate {
        id: format!("candidate-{}", out.len()),
        kind,
        label,
        detail,
        insert_text,
        replace_span: replace.clone(),
        caret_offset_utf16,
        requires_review: false,
        origin,
        definition,
    });
    Ok(())
}
