//! Literal selected-root closure and language facts. Uses the existing lexer,
//! brace ownership and root-CWD reference path grammar, not TeX execution.
use super::{catalog, dto::*};
use crate::analysis::{
    dto::{FileDescriptor, ResolvedLimits, Span, Token, TokenKind},
    lexer::{self, FileBudget, LookaheadBudget, LookaheadResult},
    ownership, references,
};
use std::collections::{BTreeSet, HashMap, HashSet};

#[derive(Clone)]
pub struct Fact {
    pub kind: &'static str,
    pub label: String,
    pub detail: String,
    pub definition: Definition,
    pub arguments: usize,
}

pub struct Project<'a> {
    pub files: &'a [FileDescriptor],
    pub bytes: Vec<&'a [u8]>,
    pub facts: Vec<Fact>,
    pub outline: Vec<Outline>,
    pub packages: Vec<Package>,
    pub catalog_packages: HashSet<String>,
    pub limitations: BTreeSet<&'static str>,
    pub visited: Vec<u8>,
    pub tokens: Vec<Vec<Token>>,
    fact_keys: HashSet<(&'static str, String)>,
    paths: HashMap<&'a str, usize>,
    budget: FileBudget,
    limits: ResolvedLimits,
    units: usize,
    file_units: Vec<usize>,
    heading_stack: Vec<(usize, usize)>,
    bib_seen: BTreeSet<usize>,
}

type Checked<T> = Result<T, &'static str>;

impl<'a> Project<'a> {
    pub fn new(files: &'a [FileDescriptor], bytes: Vec<&'a [u8]>, limits: ResolvedLimits) -> Self {
        Self {
            files,
            bytes,
            facts: vec![],
            outline: vec![],
            packages: vec![],
            catalog_packages: HashSet::new(),
            limitations: BTreeSet::new(),
            visited: vec![0; files.len()],
            tokens: vec![vec![]; files.len()],
            fact_keys: HashSet::new(),
            paths: files
                .iter()
                .enumerate()
                .map(|(i, f)| (f.relative_path.as_str(), i))
                .collect(),
            budget: FileBudget {
                tokens_per_file_cap: limits.max_tokens_per_file,
                tokens_per_file_used: 0,
                tokens_per_project_cap: limits.max_tokens_per_project,
                tokens_per_project_used: 0,
                brace_depth_cap: limits.max_brace_depth,
            },
            limits,
            units: 0,
            file_units: vec![0; files.len()],
            heading_stack: vec![],
            bib_seen: BTreeSet::new(),
        }
    }

    fn unit(&mut self, file: usize) -> Checked<()> {
        self.units += 1;
        self.file_units[file] += 1;
        if self.file_units[file] > self.limits.max_tokens_per_file as usize
            || self.units + self.budget.tokens_per_project_used as usize
                > self.limits.max_tokens_per_project as usize
        {
            Err("QuotaExceeded")
        } else {
            Ok(())
        }
    }

    pub fn visit(&mut self, file: usize, depth: usize) -> Checked<()> {
        if depth > 16 {
            return Err("QuotaExceeded");
        }
        if self.files[file].media_kind != "text" {
            self.limitations.insert("nonTextInclude");
            return Ok(());
        }
        if self.visited[file] == 1 {
            self.limitations.insert("cyclicInclude");
            return Ok(());
        }
        if self.visited[file] == 2 {
            self.limitations.insert("repeatedInclude");
            return Ok(());
        }
        self.visited[file] = 1;
        let bytes = self.bytes[file];
        let mut diagnostics = self.limits.max_diagnostics_per_project;
        let mut scan = lexer::scan_file(
            &self.files[file].file_id,
            bytes,
            bytes.len() as u64,
            self.files[file].has_bom,
            &mut self.budget,
            &mut diagnostics,
        );
        self.file_units[file] = scan.tokens.len();
        if scan.truncated
            || scan.brace_depth_exceeded
            || scan.lookahead_exhausted
            || self.units + self.budget.tokens_per_project_used as usize
                > self.limits.max_tokens_per_project as usize
        {
            return Err("QuotaExceeded");
        }
        if scan.had_unmatched_open
            || scan.nonliteral_argument
            || scan.verbatim_unterminated
            || scan.optional_unterminated
            || scan.argument_too_long
        {
            self.limitations.insert("incompleteScan");
        }
        let (_, depths, unmatched) = ownership::index(&scan.tokens);
        if unmatched {
            self.limitations.insert("incompleteScan");
        }
        let mut end = 0;
        let mut conditionals = 0usize;
        for (i, t) in scan.tokens.iter().enumerate() {
            if t.kind != TokenKind::Command || t.start_byte < end || depths[i] != 0 {
                continue;
            }
            self.unit(file)?;
            let raw = &bytes[t.start_byte as usize..t.end_byte as usize];
            // Verbatim openers include their brace group in the lexer token.
            if raw.starts_with(b"\\begin{") {
                continue;
            }
            let name = std::str::from_utf8(&raw[1..]).map_err(|_| "InvalidInput")?;
            if name.starts_with("if") {
                self.limitations.insert("conditionalExecutionUnsupported");
                conditionals += 1;
                if conditionals > self.limits.max_brace_depth as usize {
                    return Err("QuotaExceeded");
                }
                continue;
            }
            if name == "fi" && conditionals > 0 {
                conditionals -= 1;
                continue;
            }
            if conditionals > 0 {
                continue;
            }
            let mut args = Arguments {
                bytes,
                pos: t.end_byte as usize,
                work: &mut scan.lookahead,
            };
            match name.trim_end_matches('*') {
                "begin" | "end" => {
                    if let Some((a, b)) = args.group()? {
                        end = args.pos as u64;
                        if !references::literal_key(&bytes[a..b]) {
                            self.limitations.insert("dynamicEnvironment");
                        }
                    } else {
                        self.limitations.insert("dynamicEnvironment");
                    }
                }
                "input" | "include" => {
                    if let Some(a) = args.group()? {
                        end = args.pos as u64;
                        if let Some(path) =
                            references::include_path(&bytes[a.0..a.1], name == "include")
                        {
                            self.follow(&path, depth)?;
                        } else {
                            self.limitations.insert("dynamicOrUnsafeInclude");
                        }
                    } else {
                        self.limitations.insert("dynamicOrUnsafeInclude");
                    }
                }
                "usepackage" | "RequirePackage" | "documentclass" | "LoadClass" => {
                    let options = args
                        .optional()?
                        .map(|a| &bytes[a.0..a.1])
                        .unwrap_or_default();
                    if let Some(a) = args.group()? {
                        end = args.pos as u64;
                        let raw = &bytes[a.0..a.1];
                        if literal_list(raw) && literal_options(options) {
                            let class = matches!(name, "documentclass" | "LoadClass");
                            for part in std::str::from_utf8(raw).unwrap_or_default().split(',') {
                                let package = part.trim();
                                if !package_name(package) {
                                    self.limitations.insert("dynamicPackage");
                                    continue;
                                }
                                let standard_class =
                                    matches!(package, "article" | "report" | "book");
                                let known =
                                    catalog::known_package(package) && class == standard_class;
                                if known {
                                    self.catalog_packages.insert(package.to_string());
                                }
                                if !known {
                                    self.limitations.insert("unknownPackage");
                                }
                                let local =
                                    format!("{package}.{}", if class { "cls" } else { "sty" });
                                let local_index = self.paths.get(local.as_str()).copied();
                                let status = if local_index.is_some() {
                                    "projectLocalUnverified"
                                } else if known {
                                    "catalogKnownResourcesUnverified"
                                } else {
                                    "unknownResourcesUnverified"
                                };
                                let options = std::str::from_utf8(options).unwrap_or_default();
                                bounded(options)?;
                                self.packages.push(Package {
                                    name: package.into(),
                                    options: options.into(),
                                    status,
                                });
                                self.unit(file)?;
                                if let Some(index) = local_index {
                                    self.limitations
                                        .insert("projectPackageExecutionUnsupported");
                                    self.visit(index, depth + 1)?;
                                }
                            }
                        } else {
                            self.limitations.insert("dynamicPackage");
                        }
                    } else {
                        self.limitations.insert("dynamicPackage");
                    }
                }
                "newcommand" | "renewcommand" | "providecommand" | "newenvironment"
                | "renewenvironment" => {
                    let environment = name.contains("environment");
                    let decl = args.declaration_name(environment)?;
                    let count = args.optional()?;
                    let count = match count {
                        None => Some(0),
                        Some((a, b)) if b == a + 1 && bytes[a].is_ascii_digit() => {
                            Some((bytes[a] - b'0') as usize)
                        }
                        _ => None,
                    };
                    let has_default = args.optional()?.is_some();
                    if has_default {
                        self.limitations.insert("declarationDefaultUnsupported");
                    }
                    let body = args.group()?;
                    let second = if environment {
                        args.group()?
                    } else {
                        Some((0, 0))
                    };
                    end = args.pos as u64;
                    if let (Some((label, span)), Some(count), Some(_), Some(_)) =
                        (decl, count, body, second)
                    {
                        if name.starts_with("renew") || name == "providecommand" {
                            self.limitations.insert("declarationEffectUncertain");
                        }
                        self.add_fact(
                            file,
                            if environment { "environment" } else { "macro" },
                            label,
                            span,
                            if has_default { 0 } else { count },
                            format!(
                                "Literal {name}; {count} argument(s); not expanded{}",
                                if has_default {
                                    "; default observed, name-only insertion"
                                } else {
                                    ""
                                }
                            ),
                        )?;
                    } else {
                        self.limitations.insert("dynamicDeclaration");
                    }
                }
                "def" => {
                    if let Some((label, span)) = args.declaration_name(false)? {
                        let start = args.pos;
                        // Simple #1..#9 parameter text only, no delimiters/expansion.
                        while args.pos < bytes.len() && bytes[args.pos] != b'{' {
                            if !args.work.consume(1) {
                                return Err("QuotaExceeded");
                            }
                            args.pos += 1;
                        }
                        let params = &bytes[start..args.pos];
                        let params = trim_ascii(params);
                        let simple = params.len().is_multiple_of(2)
                            && params
                                .chunks(2)
                                .enumerate()
                                .all(|(i, p)| i < 9 && p[0] == b'#' && p[1] == b'1' + i as u8);
                        let body = args.group()?;
                        end = args.pos as u64;
                        if simple && body.is_some() {
                            self.add_fact(
                                file,
                                "macro",
                                label,
                                span,
                                params.len() / 2,
                                "Literal simple def; not expanded".into(),
                            )?;
                        } else {
                            self.limitations.insert("dynamicDeclaration");
                        }
                    } else {
                        self.limitations.insert("dynamicDeclaration");
                    }
                }
                "label" => {
                    if let Some((a, b)) = args.group()? {
                        end = args.pos as u64;
                        if references::literal_key(&bytes[a..b]) {
                            self.add_fact(
                                file,
                                "label",
                                text(bytes, a, b),
                                span(a, b),
                                0,
                                "Literal label; compiled counter unknown".into(),
                            )?;
                        } else {
                            self.limitations.insert("dynamicReference");
                        }
                    } else {
                        self.limitations.insert("dynamicReference");
                    }
                }
                "ref" | "pageref" | "eqref" | "cref" | "Cref" | "cite" | "parencite"
                | "textcite" | "autocite" => {
                    let _ = args.optional()?;
                    let _ = args.optional()?;
                    if let Some((a, b)) = args.group()? {
                        end = args.pos as u64;
                        if !literal_list(&bytes[a..b]) {
                            self.limitations.insert("dynamicReferenceOrCitation");
                        }
                    } else {
                        self.limitations.insert("dynamicReferenceOrCitation");
                    }
                }
                "bibliography" | "addbibresource" => {
                    let _ = args.optional()?;
                    if let Some((a, b)) = args.group()? {
                        end = args.pos as u64;
                        if literal_list(&bytes[a..b]) {
                            for raw in text(bytes, a, b).split(',') {
                                let raw = raw.trim();
                                let path = if raw.ends_with(".bib") {
                                    raw.to_string()
                                } else {
                                    format!("{raw}.bib")
                                };
                                if references::include_path(path.as_bytes(), false).as_deref()
                                    != Some(&path)
                                {
                                    self.limitations.insert("dynamicOrUnsafeBibliography");
                                    continue;
                                }
                                if let Some(&index) = self.paths.get(path.as_str()) {
                                    if self.bib_seen.insert(index) {
                                        self.read_bib(index)?;
                                    }
                                } else {
                                    self.limitations.insert("missingBibliography");
                                }
                            }
                        } else {
                            self.limitations.insert("dynamicOrUnsafeBibliography");
                        }
                    } else {
                        self.limitations.insert("dynamicOrUnsafeBibliography");
                    }
                }
                "chapter" | "section" | "subsection" | "subsubsection" | "paragraph"
                | "subparagraph" => {
                    let _ = args.optional()?;
                    if let Some((a, b)) = args.group()? {
                        end = args.pos as u64;
                        if literal_title(&bytes[a..b]) {
                            let label = text(bytes, a, b);
                            bounded(&label)?;
                            let level = match name.trim_end_matches('*') {
                                "chapter" => 0,
                                "section" => 1,
                                "subsection" => 2,
                                "subsubsection" => 3,
                                "paragraph" => 4,
                                _ => 5,
                            };
                            while self.heading_stack.last().is_some_and(|(l, _)| *l >= level) {
                                self.heading_stack.pop();
                            }
                            let parent_index = self.heading_stack.last().map(|(_, i)| *i);
                            self.outline.push(Outline {
                                kind: "heading",
                                label,
                                file_id: self.files[file].file_id.clone(),
                                span: span(a, b),
                                parent_index,
                            });
                            self.heading_stack.push((level, self.outline.len() - 1));
                            self.unit(file)?;
                        } else {
                            self.limitations.insert("dynamicHeading");
                        }
                    } else {
                        self.limitations.insert("dynamicHeading");
                    }
                }
                "catcode" | "csname" | "expandafter" | "edef" | "gdef" | "xdef" | "let" => {
                    self.limitations.insert("dynamicConstruct");
                    // Never infer declarations under altered tokenization.
                    break;
                }
                _ => {
                    if !catalog::known_command(name.trim_end_matches('*')) {
                        self.limitations.insert("macroExecutionUnsupported");
                    }
                }
            }
        }
        if conditionals != 0 {
            self.limitations.insert("incompleteConditional");
        }
        self.tokens[file] = scan.tokens;
        self.visited[file] = 2;
        Ok(())
    }

    fn follow(&mut self, path: &str, depth: usize) -> Checked<()> {
        if let Some(&index) = self.paths.get(path) {
            if self.files[index].media_kind == "text" {
                self.visit(index, depth + 1)?;
            } else {
                self.limitations.insert("nonTextInclude");
            }
        } else {
            self.limitations.insert("missingInclude");
        }
        Ok(())
    }

    fn add_fact(
        &mut self,
        file: usize,
        kind: &'static str,
        label: String,
        span: Span,
        arguments: usize,
        detail: String,
    ) -> Checked<()> {
        bounded(&label)?;
        self.unit(file)?;
        if !self.fact_keys.insert((kind, label.clone())) {
            self.limitations.insert(if kind == "citation" {
                "duplicateBibliographyKey"
            } else if kind == "label" {
                "duplicateLabel"
            } else {
                "redefinitionUncertain"
            });
        }
        self.facts.push(Fact {
            kind,
            label,
            detail,
            arguments,
            definition: Definition {
                file_id: self.files[file].file_id.clone(),
                span,
            },
        });
        Ok(())
    }

    fn read_bib(&mut self, file: usize) -> Checked<()> {
        if self.files[file].media_kind != "text" {
            self.limitations.insert("nonTextBibliography");
            return Ok(());
        }
        // A single bounded brace/quote/escape-aware pass. No regex, expansion,
        // string concatenation or interpretation of bibliography field values.
        let bytes = self.bytes[file];
        let mut i = if self.files[file].has_bom { 3 } else { 0 };
        let mut entries = 0;
        while i < bytes.len() {
            if bytes[i] == b'%' {
                while i < bytes.len() && !matches!(bytes[i], b'\r' | b'\n') {
                    i += 1;
                }
                continue;
            }
            if bytes[i] != b'@' {
                i += 1;
                continue;
            }
            i += 1;
            let start = i;
            while i < bytes.len() && bytes[i].is_ascii_alphabetic() {
                i += 1;
            }
            let ty = text(bytes, start, i).to_ascii_lowercase();
            while i < bytes.len() && bytes[i].is_ascii_whitespace() {
                i += 1;
            }
            if i == bytes.len() || !matches!(bytes[i], b'{' | b'(') {
                self.limitations.insert("malformedBibliography");
                break;
            }
            let close = if bytes[i] == b'{' { b'}' } else { b')' };
            i += 1;
            let key_start = i;
            while i < bytes.len() && !matches!(bytes[i], b',' | b'}' | b')' | b'\r' | b'\n') {
                i += 1;
            }
            let key_end = i;
            let regular = !matches!(ty.as_str(), "comment" | "preamble" | "string");
            if regular {
                entries += 1;
                if entries > self.limits.max_tokens_per_file {
                    return Err("QuotaExceeded");
                }
                if i < bytes.len()
                    && bytes[i] == b','
                    && references::literal_key(&bytes[key_start..key_end])
                {
                    self.add_fact(
                        file,
                        "citation",
                        text(bytes, key_start, key_end),
                        span(key_start, key_end),
                        0,
                        "Literal bibliography key; fields/backend not evaluated".into(),
                    )?;
                } else {
                    self.limitations.insert("malformedBibliography");
                }
            } else if ty != "comment" {
                self.limitations.insert("dynamicBibliography");
            }
            // Scan from immediately after opening delimiter, so comment/string
            // forms without a comma are balanced with the same reader.
            i = key_start;
            let mut depth = 0usize;
            let mut quote = false;
            let mut closed = false;
            let mut value_start = false;
            while i < bytes.len() {
                let b = bytes[i];
                if b == b'\\' {
                    i = (i + 2).min(bytes.len());
                    continue;
                }
                if b == b'%' && !quote && depth == 0 {
                    while i < bytes.len() && !matches!(bytes[i], b'\r' | b'\n') {
                        i += 1;
                    }
                    continue;
                }
                if b == b'"' && depth == 0 {
                    quote = !quote;
                }
                if !quote {
                    if b == b'{' {
                        depth += 1;
                        if depth > self.limits.max_brace_depth as usize {
                            return Err("QuotaExceeded");
                        }
                    } else if depth > 0 && b == b'}' {
                        depth -= 1;
                    } else if depth == 0 && b == close {
                        i += 1;
                        closed = true;
                        break;
                    } else if depth == 0 && b == b'#' {
                        self.limitations.insert("dynamicBibliography");
                    }
                    if depth == 0 && value_start && !b.is_ascii_whitespace() {
                        if b.is_ascii_alphabetic() {
                            self.limitations.insert("dynamicBibliography");
                        }
                        value_start = false;
                    }
                    if depth == 0 && b == b'=' {
                        value_start = true;
                    }
                }
                i += 1;
            }
            if !closed {
                self.limitations.insert("malformedBibliography");
                break;
            }
        }
        Ok(())
    }
}

pub fn span(a: usize, b: usize) -> Span {
    Span {
        start_byte: a as u64,
        end_byte: b as u64,
    }
}
fn text(bytes: &[u8], a: usize, b: usize) -> String {
    // All source bytes were UTF-8 validated; delimiter-derived spans are
    // scalar boundaries. Do not normalize or introduce replacement characters.
    std::str::from_utf8(&bytes[a..b])
        .unwrap_or_default()
        .to_owned()
}
fn bounded(s: &str) -> Checked<()> {
    if s.len() > 512 {
        Err("QuotaExceeded")
    } else {
        Ok(())
    }
}
fn trim_ascii(mut b: &[u8]) -> &[u8] {
    while b.first().is_some_and(u8::is_ascii_whitespace) {
        b = &b[1..];
    }
    while b.last().is_some_and(u8::is_ascii_whitespace) {
        b = &b[..b.len() - 1];
    }
    b
}
fn literal_list(b: &[u8]) -> bool {
    !b.is_empty()
        && b.split(|b| *b == b',')
            .all(|p| references::literal_key(trim_ascii(p)))
}
fn literal_title(b: &[u8]) -> bool {
    !b.is_empty()
        && !b
            .iter()
            .any(|b| matches!(b, b'\\' | b'{' | b'}' | b'%' | b'#' | b'$' | b'~'))
}
fn literal_options(b: &[u8]) -> bool {
    !b.iter()
        .any(|b| matches!(b, b'\\' | b'{' | b'}' | b'%' | b'#'))
}
fn package_name(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 128
        && s.bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
}

/// Argument extraction reuses the lexer's bounded balanced delimiter routines.
/// Command recognition and verbatim/comment exclusion belong to that lexer.
struct Arguments<'a, 'b> {
    bytes: &'a [u8],
    pos: usize,
    work: &'b mut LookaheadBudget,
}
impl Arguments<'_, '_> {
    fn group(&mut self) -> Checked<Option<(usize, usize)>> {
        match lexer::skip_to_group_open(self.bytes, self.pos, self.work) {
            LookaheadResult::Exhausted => Err("QuotaExceeded"),
            LookaheadResult::NotFound => Ok(None),
            LookaheadResult::Found(open) => {
                match lexer::find_group_close(self.bytes, open + 1, self.work) {
                    LookaheadResult::Exhausted => Err("QuotaExceeded"),
                    LookaheadResult::NotFound => Ok(None),
                    LookaheadResult::Found(close) => {
                        self.pos = close + 1;
                        Ok(Some((open + 1, close)))
                    }
                }
            }
        }
    }
    fn trivia(&mut self) -> Checked<()> {
        loop {
            if self.pos >= self.bytes.len() {
                return Ok(());
            }
            let b = self.bytes[self.pos];
            if b.is_ascii_whitespace() {
                if !self.work.consume(1) {
                    return Err("QuotaExceeded");
                }
                self.pos += 1;
            } else if b == b'%' {
                while self.pos < self.bytes.len() && !matches!(self.bytes[self.pos], b'\r' | b'\n')
                {
                    if !self.work.consume(1) {
                        return Err("QuotaExceeded");
                    }
                    self.pos += 1;
                }
            } else {
                return Ok(());
            }
        }
    }
    fn optional(&mut self) -> Checked<Option<(usize, usize)>> {
        self.trivia()?;
        if self.bytes.get(self.pos) != Some(&b'[') {
            return Ok(None);
        }
        let open = self.pos;
        match lexer::find_optional_close(self.bytes, open + 1, self.work) {
            LookaheadResult::Exhausted => Err("QuotaExceeded"),
            LookaheadResult::NotFound => Ok(None),
            LookaheadResult::Found(close) => {
                self.pos = close + 1;
                Ok(Some((open + 1, close)))
            }
        }
    }
    fn declaration_name(&mut self, environment: bool) -> Checked<Option<(String, Span)>> {
        self.trivia()?;
        if let Some((a, b)) = self.group()? {
            let label = if environment {
                &self.bytes[a..b]
            } else {
                self.bytes[a..b].strip_prefix(b"\\").unwrap_or_default()
            };
            if !label.is_empty() && label.iter().all(u8::is_ascii_alphabetic) {
                let start = if environment { a } else { a + 1 };
                return Ok(Some((text(self.bytes, start, b), span(start, b))));
            }
            return Ok(None);
        }
        if !environment && self.bytes.get(self.pos) == Some(&b'\\') {
            let a = self.pos + 1;
            let mut b = a;
            while b < self.bytes.len() && self.bytes[b].is_ascii_alphabetic() {
                if !self.work.consume(1) {
                    return Err("QuotaExceeded");
                }
                b += 1;
            }
            if b > a {
                self.pos = b;
                return Ok(Some((text(self.bytes, a, b), span(a, b))));
            }
        }
        Ok(None)
    }
}
