//! Opt-in thesis source observations using unchanged 1.4 results and the same
//! bounded lexer and argument ownership. Edition 1.6 observes literal citation
//! postnotes. Edition 1.7 adds the pinned project caption context.
use super::{
    dto::*,
    lexer::{LookaheadBudget, LookaheadResult},
    ownership, profiles, references,
};
use serde::Serialize;
use serde_json::{json, Value};
use std::collections::{BTreeSet, HashMap, HashSet};

mod project_caption_roles;
use project_caption_roles::{CaptionContext, Environment as CaptionEnvironment};

pub(super) const CITATION_NOTES_VERSION: &str = "1.6.0";
const LEGACY_VERSION: &str = "1.5.0";
const LEGACY_CATALOG_VERSION: &str = "1.0.0";
const CITATION_NOTES_CATALOG_VERSION: &str = "1.1.0";
// An observation bound, not a guideline limit on page/citation notation.
const MAX_CITATION_NOTE_BYTES: usize = 512;

pub(super) fn supports_version(version: &str) -> bool {
    matches!(
        version,
        LEGACY_VERSION | CITATION_NOTES_VERSION | project_caption_roles::VERSION
    )
}

const IDS: [&str; 5] = [
    "KNU-SRC-STRUCTURE-001",
    "KNU-SRC-CITATIONS-001",
    "KNU-SRC-BIBLIOGRAPHY-001",
    "KNU-SRC-APPENDIX-001",
    "KNU-SRC-CAPTION-PRESENCE-001",
];

pub(super) fn analyze(metadata: &str, packed: &[u8], p: AnalyzePayload, id: String) -> String {
    match run(metadata, packed, p) {
        Ok((value, cap)) => {
            let mut writer = super::CappedWriter::new(cap);
            if serde_json::to_writer(&mut writer, &value).is_err() {
                super::failed_response(Some(id), "QuotaExceeded", "thesis result limit exceeded")
            } else {
                writer.into_string()
            }
        }
        Err(code) => super::failed_response(
            Some(id),
            code,
            "thesis source request could not be completed",
        ),
    }
}

fn run(metadata: &str, packed: &[u8], p: AnalyzePayload) -> Result<(Value, usize), &'static str> {
    let mut input = super::reference_wire::unique_value(metadata).ok_or("InvalidInput")?;
    // 1.5 adds only referenceConfiguration (selected root) to existing KNU input.
    let keys = super::reference_wire::keys;
    if !keys(
        &input,
        &["protocolVersion", "requestId", "operation", "payload"],
    ) || !keys(
        &input["payload"],
        &[
            "snapshotId",
            "projectId",
            "sessionId",
            "workingGeneration",
            "baseHeadRevisionId",
            "profile",
            "configuration",
            "referenceConfiguration",
            "limits",
            "files",
        ],
    ) || !keys(&input["payload"]["profile"], &["id", "version"])
        || !keys(&input["payload"]["configuration"], &["workType"])
        || !keys(&input["payload"]["referenceConfiguration"], &["rootFileId"])
        || !keys(
            &input["payload"]["limits"],
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
        )
        || p.configuration.is_none()
        || p.working_generation < 0
        || p.working_generation > 9_007_199_254_740_991
        || !p.files.iter().all(|f| {
            !f.relative_path.starts_with("__oop_runtime__/")
                && matches!(f.media_kind.as_str(), "text" | "binary")
        })
    {
        return Err("InvalidInput");
    }
    if !input["payload"]["files"].as_array().is_some_and(|files| {
        files.iter().all(|f| {
            keys(
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
    }) {
        return Err("InvalidInput");
    }
    let root = references::validate(&p).map_err(|_| "InvalidInput")?;
    let limits = super::resolve_limits(&p.limits);
    // New profile refuses attempted raised/zero limits, unlike legacy clamping.
    macro_rules! check { ($($f:ident),*) => { $(if p.limits.$f.is_some_and(|n| n == 0 || n > limits.$f) {
        return Err("QuotaExceeded");
    })* }; }
    check!(
        max_bytes_per_file,
        max_files,
        max_total_bytes,
        max_result_bytes,
        max_tokens_per_file,
        max_tokens_per_project,
        max_diagnostics_per_project,
        max_brace_depth
    );
    input["payload"]["profile"]["version"] = json!(profiles::FORMATTING_VERSION);
    input["payload"]
        .as_object_mut()
        .ok_or("InvalidInput")?
        .remove("referenceConfiguration");
    let legacy = super::analyze_source(&input.to_string(), packed);
    let mut value: Value = serde_json::from_str(&legacy).map_err(|_| "InternalError")?;
    if value["status"] != "completed" {
        return Err(match value["error"]["code"].as_str() {
            Some("QuotaExceeded") => "QuotaExceeded",
            Some("InvalidInput") => "InvalidInput",
            Some("UnsupportedCapability") => "UnsupportedCapability",
            _ => "InternalError",
        });
    }
    let result: AnalysisResult =
        serde_json::from_value(value["payload"].clone()).map_err(|_| "InternalError")?;
    let bytes = p
        .files
        .iter()
        .map(|f| {
            super::checked_slice(f, packed.len())
                .map(|(off, len)| &packed[off..off + len as usize])
                .map_err(|_| "InvalidInput")
        })
        .collect::<Result<Vec<_>, _>>()?;
    // The accepted project producer owns include closure and the sole bounded
    // brace/quote-aware .bib reader. This profile adds observations, not a parser.
    let mut project = crate::authoring::project::Project::new(&p.files, bytes.clone(), limits);
    project.visit(root, 0)?;
    let caption_context = if p.profile.version == project_caption_roles::VERSION {
        CaptionContext::recognize(root, &project)?
    } else {
        None
    };
    let mut pass = Pass {
        p: &p,
        result: &result,
        project: &project,
        bytes,
        visited: vec![0; p.files.len()],
        paths: p
            .files
            .iter()
            .enumerate()
            .map(|(i, f)| (f.relative_path.as_str(), i))
            .collect(),
        limitations: BTreeSet::new(),
        events: vec![],
        envs: vec![],
        remaining: limits.max_tokens_per_project as usize,
        per_file: limits.max_tokens_per_file as usize,
        file_units: vec![0; p.files.len()],
        max_evidence: (limits.max_diagnostics_per_project as usize)
            .saturating_sub(result.diagnostics.len()),
        class_seen: false,
        document_seen: false,
        document_closed: false,
        caption_context,
    };
    for code in &project.limitations {
        let resolved = match *code {
            // The thesis pass observes manual bibitems, captions and duplicate keys.
            "macroExecutionUnsupported" | "duplicateBibliographyKey" => true,
            // For 1.7 the exact preamble recognizes the one unlisted package
            // in this closure. Every other package/scope limitation still applies.
            "unknownPackage" => pass.caption_context.is_some(),
            _ => false,
        };
        if !resolved {
            pass.limitations.insert(*code);
        }
    }
    pass.charge(project.facts.len())?;
    for (i, tokens) in project.tokens.iter().enumerate() {
        pass.charge(tokens.len())?;
        pass.file_units[i] = tokens.len();
    }
    // Charge existing token stream plus new ownership/index/event work together.
    for (i, f) in result.facts.files.iter().enumerate() {
        let observations = result
            .knu
            .as_ref()
            .and_then(|k| k.files.get(i))
            .map_or(0, |f| f.observation_count as usize);
        let used = f
            .tokens
            .len()
            .checked_add(observations)
            .ok_or("QuotaExceeded")?;
        pass.charge(used)?;
        pass.file_units[i] = pass.file_units[i]
            .checked_add(used)
            .ok_or("QuotaExceeded")?;
    }
    pass.visit(root, 0)?;
    for fact in project.facts.iter().filter(|f| f.kind == "citation") {
        let file = p
            .files
            .iter()
            .position(|f| f.file_id == fact.definition.file_id)
            .ok_or("InternalError")?;
        pass.emit(
            file,
            "bibliographyKey",
            fact.definition.span.start_byte as usize,
            fact.definition.span.end_byte as usize,
            &fact.label,
        )?;
    }
    if !pass.envs.is_empty() {
        pass.limitations.insert("unclosedEnvironment");
    }
    if !pass.class_seen || !pass.document_seen || !pass.document_closed {
        pass.limitations.insert("incompleteDocumentBoundaries");
    }
    let report = pass.report()?;
    value["payload"]["profile"]["version"] = json!(p.profile.version);
    value["payload"]["knu"]["metadata"]["profile"]["version"] = json!(p.profile.version);
    // Existing fixes retain exact preconditions and are bound to requested edition.
    if let Some(fixes) = value["payload"]["knu"]["fixDescriptors"].as_array_mut() {
        for fix in fixes {
            fix["profile"]["version"] = json!(p.profile.version);
        }
    }
    value["payload"]["referenceConfiguration"] =
        serde_json::to_value(&p.reference_configuration).map_err(|_| "InternalError")?;
    value["payload"]["thesis"] = report;
    // Legacy aggregate remains legacy source coverage; never a global compliance flag.
    value["payload"]["unavailableCapabilities"]
        .as_array_mut()
        .ok_or("InternalError")?
        .push(json!("thesisRenderedChecks"));
    Ok((value, limits.max_result_bytes as usize))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Event {
    kind: &'static str,
    file_id: String,
    span: Span,
    text: String,
    appendix: bool,
}
struct Pass<'a> {
    p: &'a AnalyzePayload,
    result: &'a AnalysisResult,
    project: &'a crate::authoring::project::Project<'a>,
    bytes: Vec<&'a [u8]>,
    visited: Vec<u8>,
    paths: HashMap<&'a str, usize>,
    limitations: BTreeSet<&'static str>,
    events: Vec<Event>,
    envs: Vec<(String, usize)>,
    remaining: usize,
    per_file: usize,
    file_units: Vec<usize>,
    max_evidence: usize,
    class_seen: bool,
    document_seen: bool,
    document_closed: bool,
    caption_context: Option<CaptionContext>,
}
impl Pass<'_> {
    fn charge(&mut self, n: usize) -> Result<(), &'static str> {
        self.remaining = self.remaining.checked_sub(n).ok_or("QuotaExceeded")?;
        Ok(())
    }
    fn emit(
        &mut self,
        file: usize,
        kind: &'static str,
        a: usize,
        b: usize,
        text: &str,
    ) -> Result<(), &'static str> {
        self.charge(1)?;
        self.file_units[file] += 1;
        if self.file_units[file] > self.per_file {
            return Err("QuotaExceeded");
        }
        if self.events.len() >= self.max_evidence || text.len() > 512 {
            return Err("QuotaExceeded");
        }
        self.events.push(Event {
            kind,
            file_id: self.p.files[file].file_id.clone(),
            span: Span {
                start_byte: a as u64,
                end_byte: b as u64,
            },
            text: text.into(),
            appendix: self
                .events
                .iter()
                .any(|e| e.kind == "appendixMode" || e.kind == "appendix"),
        });
        Ok(())
    }
    fn visit(&mut self, file: usize, depth: usize) -> Result<(), &'static str> {
        if depth > 16 {
            return Err("QuotaExceeded");
        }
        if self.visited[file] != 0 {
            self.limitations.insert(if self.visited[file] == 1 {
                "cyclicInclude"
            } else {
                "repeatedInclude"
            });
            return Ok(());
        }
        self.visited[file] = 1;
        let inherited_env_count = self.envs.len();
        if let Some(context) = self.caption_context.as_mut() {
            if depth > 0 && (inherited_env_count != 1 || self.envs[0].0 != "document") {
                context.refuse_scope();
            }
        }
        let bytes = self.bytes[file];
        let facts = &self.result.facts.files[file];
        if facts.truncated || facts.brace_depth_exceeded {
            return Err("QuotaExceeded");
        }
        if facts.engine_outcome != Outcome::Pass {
            self.limitations.insert("incompleteScan");
        }
        let tokens = &self.project.tokens[file];
        self.charge(tokens.len())?;
        self.file_units[file] = self.file_units[file]
            .checked_add(tokens.len())
            .ok_or("QuotaExceeded")?;
        if self.file_units[file] > self.per_file {
            return Err("QuotaExceeded");
        }
        let (closes, depths, unmatched) = ownership::index(tokens);
        if unmatched {
            self.limitations.insert("incompleteScan");
        }
        let mut work = LookaheadBudget::new(bytes.len().saturating_mul(8).saturating_add(64));
        let mut owned_end = 0;
        for (i, t) in tokens.iter().enumerate() {
            if t.start_byte < owned_end {
                continue;
            }
            if t.kind == TokenKind::Text {
                let raw = &bytes[t.start_byte as usize..t.end_byte as usize];
                if raw
                    .iter()
                    .any(|b| matches!(b, b'$' | b'#' | b'&' | b'^' | b'_' | b'~'))
                {
                    self.limitations.insert("unsupportedContext");
                }
                continue;
            }
            if t.kind != TokenKind::Command {
                continue;
            }
            if depths[i] != 0 {
                self.limitations.insert("groupedExecutionUnsupported");
                continue;
            }
            let raw = &bytes[t.start_byte as usize..t.end_byte as usize];
            if raw.starts_with(b"\\begin{") {
                continue;
            } // lexer-owned verbatim
            let name = std::str::from_utf8(&raw[1..]).map_err(|_| "InvalidInput")?;
            // This is the same root traversal. Only the two exact initial
            // package declarations validated by the 1.7 capability own their
            // optional and mandatory groups here.
            if let Some(end) = self
                .caption_context
                .as_ref()
                .and_then(|c| c.package_end(file, t.start_byte))
            {
                owned_end = end;
                continue;
            }
            if name != "documentclass"
                && name != "begin"
                && (!self.document_seen || self.document_closed)
            {
                self.limitations.insert("outsideDocumentExecution");
            }
            let mut next = i + 1;
            if name == "cite" && self.p.profile.version != LEGACY_VERSION {
                let (after_notes, end, literal) =
                    citation_notes(tokens, bytes, next, t.end_byte as usize, &mut work)?;
                next = after_notes;
                owned_end = end as u64;
                if !literal {
                    self.limitations.insert("dynamicCitation");
                }
            }
            let mut arg = || -> Result<Option<(usize, usize, usize)>, &'static str> {
                match ownership::group(tokens, bytes, &closes, next, &mut work) {
                    LookaheadResult::Found(a) => {
                        next = a.next;
                        Ok(Some((a.start, a.end, a.owned_end)))
                    }
                    LookaheadResult::NotFound => Ok(None),
                    LookaheadResult::Exhausted => Err("QuotaExceeded"),
                }
            };
            match name {
                "input" | "include" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        if let Some(path) =
                            references::include_path(&bytes[a..b], name == "include")
                        {
                            if let Some(&child) = self.paths.get(path.as_str()) {
                                if self.project.visited[child] == 2
                                    && self.p.files[child].media_kind == "text"
                                    && references::tex(&self.p.files[child].relative_path)
                                {
                                    self.visit(child, depth + 1)?;
                                } else {
                                    self.limitations.insert("nonTextOrNonTexInclude");
                                }
                            } else {
                                self.limitations.insert("missingInclude");
                            }
                        } else {
                            self.limitations.insert("dynamicOrUnsafeInclude");
                        }
                    } else {
                        self.limitations.insert("dynamicOrUnsafeInclude");
                    }
                }
                "documentclass" => {
                    if self.class_seen || self.document_seen {
                        self.limitations.insert("classContextUncertain");
                    }
                    self.class_seen = true;
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        if !matches!(&bytes[a..b], b"article" | b"report" | b"book") {
                            self.limitations.insert("customClass");
                        }
                    } else {
                        self.limitations.insert("customClass");
                    }
                }
                "begin" | "end" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        let env = std::str::from_utf8(&bytes[a..b]).map_err(|_| "InvalidInput")?;
                        if !matches!(
                            env,
                            "document" | "abstract" | "figure" | "table" | "thebibliography"
                        ) {
                            self.limitations.insert("unsupportedEnvironment");
                        }
                        if name == "begin" {
                            if bytes[end..].trim_ascii_start().starts_with(b"[") {
                                self.limitations.insert("optionalEnvironmentSyntax");
                            }
                            if env == "document" {
                                if self.document_seen || !self.envs.is_empty() {
                                    self.limitations.insert("documentContextUncertain");
                                }
                                self.document_seen = true;
                            } else if !self.document_seen || self.document_closed {
                                self.limitations.insert("outsideDocumentExecution");
                            }
                            if self.envs.len() >= 16 {
                                return Err("QuotaExceeded");
                            }
                            if matches!(env, "figure" | "table")
                                && self.envs.last().is_some_and(|(e, _)| e != "document")
                            {
                                self.limitations.insert("nestedFloat");
                            }
                            self.envs.push((env.into(), 0));
                            if env == "abstract" {
                                self.emit(file, "abstract", a, b, env)?;
                            }
                            if env == "thebibliography" {
                                if let Some((_, _, end)) = arg()? {
                                    owned_end = end as u64;
                                } else {
                                    self.limitations.insert("malformedBibliography");
                                }
                                self.emit(file, "bibliographyStart", a, b, env)?;
                            }
                        } else if let Some((open, count)) = self.envs.pop() {
                            if env == "document" {
                                self.document_closed = true;
                            }
                            if open != env {
                                self.limitations.insert("unbalancedEnvironment");
                            }
                            if matches!(env, "figure" | "table") {
                                self.emit(file, "floatCaptionCount", a, b, &count.to_string())?;
                            }
                        } else {
                            self.limitations.insert("unbalancedEnvironment");
                        }
                    } else {
                        self.limitations.insert("dynamicEnvironment");
                    }
                }
                "chapter" | "chapter*" | "section" | "section*" | "subsection" | "subsection*" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        if literal_title(&bytes[a..b]) {
                            let title = std::str::from_utf8(&bytes[a..b])
                                .map_err(|_| "InvalidInput")?
                                .trim();
                            let kind = match title {
                                "РЕФЕРАТ" | "Реферат" => "abstract",
                                "ЗМІСТ" | "Зміст" => "contents",
                                "ПЕРЕЛІК СКОРОЧЕНЬ ТА УМОВНИХ ПОЗНАЧЕНЬ"
                                | "Перелік скорочень та умовних позначень"
                                | "СКОРОЧЕННЯ ТА УМОВНІ ПОЗНАКИ"
                                | "Скорочення та умовні познаки" => {
                                    "abbreviations"
                                }
                                "ВСТУП" | "Вступ" => "introduction",
                                "ВИСНОВКИ" | "Висновки" => "conclusions",
                                "ПЕРЕЛІК ДЖЕРЕЛ ПОСИЛАННЯ" | "Перелік джерел посилання" => {
                                    "references"
                                }
                                _ if title.starts_with("ДОДАТОК ") => "appendix",
                                _ if !name.ends_with('*') => "body",
                                _ => "unknownHeadingRole",
                            };
                            if kind == "unknownHeadingRole" {
                                self.limitations.insert("unknownHeadingRole");
                            }
                            if title
                                .strip_prefix("ДОДАТОК ")
                                .is_some_and(|s| s.split_whitespace().count() > 1)
                            {
                                self.limitations.insert("ambiguousAppendixHeading");
                            }
                            self.emit(file, kind, a, b, title)?;
                        } else {
                            self.limitations.insert("dynamicHeading");
                        }
                    } else {
                        self.limitations.insert("dynamicHeading");
                    }
                }
                "cite" | "bibitem" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        if name == "bibitem"
                            && self.envs.last().is_none_or(|(e, _)| e != "thebibliography")
                        {
                            self.limitations.insert("bibitemOutsideBibliography");
                        }
                        if name == "cite" {
                            let mut start = a;
                            for part in bytes[a..b].split(|b| *b == b',') {
                                let leading =
                                    part.iter().take_while(|b| b.is_ascii_whitespace()).count();
                                let trimmed = part.trim_ascii();
                                if references::literal_key(trimmed) {
                                    self.emit(
                                        file,
                                        "cite",
                                        start + leading,
                                        start + leading + trimmed.len(),
                                        std::str::from_utf8(trimmed).map_err(|_| "InvalidInput")?,
                                    )?;
                                } else {
                                    self.limitations.insert("dynamicCitation");
                                }
                                start += part.len() + 1;
                            }
                        } else if references::literal_key(&bytes[a..b]) {
                            self.emit(
                                file,
                                "bibitem",
                                a,
                                b,
                                std::str::from_utf8(&bytes[a..b]).map_err(|_| "InvalidInput")?,
                            )?;
                        } else {
                            self.limitations.insert("malformedBibliography");
                        }
                    } else {
                        self.limitations.insert("dynamicCitationOrBibliography");
                    }
                }
                "caption" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        if let Some((env, count)) = self.envs.last_mut() {
                            if let Some(environment) =
                                CaptionEnvironment::from_name(env).filter(|_| {
                                    bytes[a..b].trim_ascii().is_empty()
                                        || literal_title(&bytes[a..b])
                                })
                            {
                                *count += 1;
                                if let Some(context) = self.caption_context.as_mut() {
                                    context.record(self.events.len(), environment, *count);
                                }
                                self.emit(
                                    file,
                                    "captionTitle",
                                    a,
                                    b,
                                    std::str::from_utf8(&bytes[a..b])
                                        .map_err(|_| "InvalidInput")?,
                                )?;
                            } else {
                                self.limitations.insert("dynamicCaption");
                            }
                        } else {
                            self.limitations.insert("captionOutsideFloat");
                        }
                    } else {
                        self.limitations.insert("dynamicCaption");
                    }
                }
                "maketitle" | "tableofcontents" | "appendix" => {
                    self.emit(
                        file,
                        match name {
                            "maketitle" => "title",
                            "tableofcontents" => "contents",
                            _ => "appendixMode",
                        },
                        t.start_byte as usize,
                        t.end_byte as usize,
                        name,
                    )?;
                }
                "bibliography" | "addbibresource" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        self.emit(
                            file,
                            "externalBibliography",
                            a,
                            b,
                            std::str::from_utf8(&bytes[a..b]).map_err(|_| "InvalidInput")?,
                        )?;
                    } else {
                        self.limitations.insert("dynamicOrUnsafeBibliography");
                    }
                }
                "bibliographystyle" => {
                    if let Some((a, b, end)) = arg()? {
                        owned_end = end as u64;
                        if !matches!(&bytes[a..b], b"plain" | b"unsrt" | b"abbrv") {
                            self.limitations
                                .insert("bibliographyStyleExecutionUnsupported");
                        }
                    } else {
                        self.limitations
                            .insert("bibliographyStyleExecutionUnsupported");
                    }
                }
                "printbibliography" => {
                    self.limitations.insert("bibliographyBackendNotEvaluated");
                }
                "label" | "ref" | "pageref" | "eqref" => {
                    if let Some((_, _, end)) = arg()? {
                        owned_end = end as u64;
                    } else {
                        self.limitations.insert("dynamicReference");
                    }
                }
                "newpage" | "clearpage" | "par" => {}
                _ => {
                    self.limitations
                        .insert("macroOrPackageExecutionUnsupported");
                }
            }
        }
        if depth > 0 && self.envs.len() != inherited_env_count {
            if let Some(context) = self.caption_context.as_mut() {
                context.refuse_scope();
            }
        }
        self.visited[file] = 2;
        Ok(())
    }
    fn report(&self) -> Result<Value, &'static str> {
        let mut results = vec![];
        let mut last = 0;
        let mut roles = HashSet::new();
        let mut order_bad = false;
        for e in &self.events {
            let rank = match e.kind {
                "title" => 1,
                "abstract" => 2,
                "contents" | "abbreviations" => 3,
                "introduction" => 4,
                "body" => 5,
                "conclusions" => 6,
                "references" | "bibliographyStart" | "externalBibliography" => 7,
                "appendix" | "appendixMode" => 8,
                _ => continue,
            };
            // Appendix-local headings and references are not main-structure inversions.
            if e.appendix {
                continue;
            }
            order_bad |= rank < last;
            last = rank;
            if e.kind != "abbreviations" {
                roles.insert(rank);
            }
        }
        let mut counts = HashMap::<&str, usize>::new();
        let mut bib = vec![];
        let mut cites = vec![];
        let mut seen = HashSet::new();
        for e in &self.events {
            if e.appendix && matches!(e.kind, "cite" | "bibitem") {
                continue;
            }
            if matches!(e.kind, "bibitem" | "bibliographyKey") {
                *counts.entry(&e.text).or_default() += 1;
                if e.kind == "bibitem" {
                    bib.push(e.text.as_str());
                }
            }
            if e.kind == "cite" && seen.insert(e.text.as_str()) {
                cites.push(e.text.as_str());
            }
        }
        let appendix_citations = self
            .events
            .iter()
            .any(|e| e.appendix && matches!(e.kind, "cite" | "bibitem"));
        let external = self.events.iter().any(|e| e.kind == "externalBibliography");
        let missing = cites.iter().any(|k| counts.get(k) != Some(&1));
        let duplicate = counts.values().any(|n| *n != 1);
        let appendices = self
            .events
            .iter()
            .filter(|e| e.kind == "appendix")
            .collect::<Vec<_>>();
        let letters_ok = appendices.iter().all(|e| {
            e.text
                .strip_prefix("ДОДАТОК ")
                .is_some_and(|s| s.chars().count() == 1 && "АБВГДЕЖИКЛМНПРСТУФХЦШЩЮЯ".contains(s))
        });
        let captions = self
            .events
            .iter()
            .filter(|e| e.kind == "captionTitle")
            .collect::<Vec<_>>();
        let conditions = [
            (
                order_bad,
                (1..=7).all(|r| roles.contains(&r)),
                "literal-structure-order",
            ),
            (
                missing || duplicate,
                !cites.is_empty() && !counts.is_empty(),
                "literal-citation-key-resolution",
            ),
            (
                !external && bib != cites,
                !external && !bib.is_empty() && !cites.is_empty(),
                "literal-manual-first-use-order",
            ),
            (
                !letters_ok,
                !appendices.is_empty(),
                "literal-ukrainian-appendix-letter",
            ),
            (
                captions.iter().any(|e| e.text.trim().is_empty()),
                !captions.is_empty(),
                "literal-caption-title-present",
            ),
        ];
        let (catalog_version, catalog_json) = match self.p.profile.version.as_str() {
            project_caption_roles::VERSION => (
                project_caption_roles::CATALOG_VERSION,
                crate::thesis_catalog::CAPTION_ROLES_JSON,
            ),
            CITATION_NOTES_VERSION => (CITATION_NOTES_CATALOG_VERSION, crate::thesis_catalog::JSON),
            _ => (LEGACY_CATALOG_VERSION, crate::thesis_catalog::JSON),
        };
        let catalog: Value = serde_json::from_str(catalog_json).map_err(|_| "InternalError")?;
        for (id, (bad, observed, method)) in IDS.iter().zip(conditions) {
            let appendix_scope = appendix_citations
                && matches!(*id, "KNU-SRC-CITATIONS-001" | "KNU-SRC-BIBLIOGRAPHY-001");
            let backend_order = external && *id == "KNU-SRC-BIBLIOGRAPHY-001";
            let outcome = if !self.limitations.is_empty() || appendix_scope {
                "indeterminate"
            } else if backend_order {
                "unavailable"
            } else if bad {
                "fail"
            } else if observed {
                "pass"
            } else {
                "unavailable"
            };
            results.push(json!({"ruleId":id,"ruleVersion":"1.0.0","domain":"sourceObservation","supportState":"partial",
                "outcome":outcome,"reason":if appendix_scope {"appendix-citation-scope-unsupported"} else if !self.limitations.is_empty(){"literal-context-uncertain"}else if backend_order{"compiled-bibliography-order-unavailable"}else if bad {"literal-counterexample"}else if observed {"observed-literals-only"}else{"no-supported-observation"},
                "method":method,"knownCounterexample":bad,"fixPolicy":"none","severity":"warning",
                "citation":catalog["rules"].as_array().ok_or("InternalError")?.iter().find(|r|r["ruleId"]==*id).ok_or("InternalError")?["citation"],
                "evidenceIndexes":(0..self.events.len()).collect::<Vec<_>>()}));
        }
        let root_file_id = &self
            .p
            .reference_configuration
            .as_ref()
            .ok_or("InvalidInput")?
            .root_file_id;
        let mut report = json!({"schemaVersion":1,"catalogId":"knu-csc-2025-thesis-support","catalogVersion":catalog_version,
            "guidelineSha256":profiles::SHA256,"rootFileId":root_file_id,
            "workType":self.p.configuration.as_ref().ok_or("InvalidInput")?.work_type,
            "domain":"sourceObservation","globalCoverage":"incomplete","renderedCompliance":"notAssessed",
            "completeLiteralScan":self.limitations.is_empty(),"limitations":self.limitations,
            "closureFileIds":self.visited.iter().enumerate().filter(|(_,v)|**v==2).map(|(i,_)|&self.p.files[i].file_id).collect::<Vec<_>>(),
            "events":self.events,"ruleResults":results,"bibliographyMode":if external {if bib.is_empty(){"external"}else{"mixed"}}else if bib.is_empty(){"none"}else{"manual"},
            "firstLiteralCitationKeys":cites,
            "unavailableCells":[{"ruleId":"KNU-STRUCTURE-001","reason":"semantic-content-and-signatures-unavailable"},
                {"ruleId":"KNU-ABSTRACT-001","reason":"semantic-abstract-classification-unavailable"}],
            "workTypeDependentTitle":if self.p.configuration.as_ref().is_some_and(|c|c.work_type==super::knu_dto::WorkType::Unspecified){"unavailable-work-type"}else if self.p.configuration.as_ref().is_some_and(|c|c.work_type==super::knu_dto::WorkType::Coursework){"not-applicable-coursework"}else{"unavailable-title-metadata"}});
        if self.p.profile.version == project_caption_roles::VERSION {
            CaptionContext::add_report_fields(
                self.caption_context.as_ref(),
                &mut report,
                &self.events,
                self.limitations.is_empty(),
                root_file_id,
            );
        }
        Ok(report)
    }
}

/// One ordinary cite postnote, using the existing lexer's bounded optional-group
/// reader. Additional groups are unsupported and owned only to keep their text
/// from becoming executed thesis events. Token text can span adjacent brackets,
/// so raw mandatory-group adjacency is checked before token association.
fn citation_notes(
    tokens: &[Token],
    bytes: &[u8],
    mut next: usize,
    mut pos: usize,
    work: &mut LookaheadBudget,
) -> Result<(usize, usize, bool), &'static str> {
    let mut seen_note = false;
    let mut literal = true;
    loop {
        // Match the existing Project argument observer's inter-argument trivia.
        loop {
            match bytes.get(pos) {
                Some(b) if b.is_ascii_whitespace() => {
                    if !work.consume(1) {
                        return Err("QuotaExceeded");
                    }
                    pos += 1;
                }
                Some(b'%') => {
                    while pos < bytes.len() && !matches!(bytes[pos], b'\r' | b'\n') {
                        if !work.consume(1) {
                            return Err("QuotaExceeded");
                        }
                        pos += 1;
                    }
                }
                _ => break,
            }
        }
        if bytes.get(pos) != Some(&b'[') {
            break;
        }
        // Two-note forms require package command semantics not established here.
        // Owning them is conservative skipping, never positive syntax support.
        literal &= !seen_note;
        seen_note = true;
        let close = match super::lexer::find_optional_close(bytes, pos + 1, work) {
            LookaheadResult::Found(close) => close,
            LookaheadResult::NotFound => return Ok((next, pos, false)),
            LookaheadResult::Exhausted => return Err("QuotaExceeded"),
        };
        let note = &bytes[pos + 1..close];
        if !work.consume(note.len()) {
            return Err("QuotaExceeded");
        }
        literal &= note.len() <= MAX_CITATION_NOTE_BYTES
            && note.iter().all(|b| {
                (!b.is_ascii_control() || matches!(b, b'\t' | b'\r' | b'\n'))
                    && !matches!(
                        b,
                        b'\\' | b'{' | b'}' | b'%' | b'#' | b'$' | b'&' | b'^' | b'_' | b'~' | b'['
                    )
            });
        pos = close + 1;
        while next < tokens.len() && tokens[next].start_byte < pos as u64 {
            if !work.consume(1) {
                return Err("QuotaExceeded");
            }
            next += 1;
        }
    }
    if seen_note {
        match super::lexer::skip_to_group_open(bytes, pos, work) {
            LookaheadResult::Found(_) => {}
            LookaheadResult::NotFound => literal = false,
            LookaheadResult::Exhausted => return Err("QuotaExceeded"),
        }
    }
    Ok((next, pos, literal))
}

fn literal_title(bytes: &[u8]) -> bool {
    !bytes.is_empty()
        && !bytes.iter().any(|b| {
            matches!(
                b,
                b'\\' | b'{' | b'}' | b'%' | b'#' | b'$' | b'&' | b'^' | b'_' | b'~'
            )
        })
}

#[cfg(test)]
mod tests;
