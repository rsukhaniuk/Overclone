//! Recognize literal citation-bearing source in the same captured file closure.
use super::{keys, path, Files, MAX_KEYS};
use std::collections::BTreeSet;

type Issue = &'static str;
const UNSUPPORTED: Issue = "unsupported-source-form";

#[derive(Default)]
pub struct Source<'a> {
    pub citations: Vec<&'a str>,
    pub includes: BTreeSet<String>,
    pub style: &'a str,
    pub database: &'a str,
}

#[derive(Default)]
struct Reader<'a> {
    result: Source<'a>,
    visited: BTreeSet<&'a str>,
    cited: BTreeSet<&'a str>,
    environments: Vec<&'a str>,
    class: bool,
    main: bool,
    ended: bool,
}

pub fn inspect<'a>(root: &str, files: &Files<'a>) -> Result<Source<'a>, Issue> {
    let mut reader = Reader::default();
    reader.visit(root, files, 0)?;
    if !reader.class
        || !reader.ended
        || !reader.environments.is_empty()
        || reader.result.style.is_empty()
        || reader.result.database.is_empty()
        || reader.visited.len() != files.len()
    {
        return Err(UNSUPPORTED);
    }
    Ok(reader.result)
}

impl<'a> Reader<'a> {
    fn visit(&mut self, name: &str, files: &Files<'a>, depth: usize) -> Result<(), Issue> {
        let (&name, &text) = files.get_key_value(name).ok_or("missing-captured-source")?;
        if depth >= 16 || !self.visited.insert(name) {
            return Err("ambiguous-source-include");
        }
        let mut input = text;
        let initial_environments = self.environments.len();
        while {
            input = spaces(input);
            !input.is_empty()
        } {
            let Some(rest) = input.strip_prefix('\\') else {
                let end = input.find(['\\', '%']).unwrap_or(input.len());
                let text = &input[..end];
                if !self.main || text.contains(['{', '}', '#', '^']) {
                    return Err(UNSUPPORTED);
                }
                input = &input[end..];
                continue;
            };
            let letters = rest
                .bytes()
                .take_while(|b| b.is_ascii_alphabetic() || *b == b'@')
                .count();
            if letters == 0 {
                let escaped = rest.as_bytes().first().ok_or(UNSUPPORTED)?;
                if !self.main || !b"\\%&#_{}$~ ".contains(escaped) {
                    return Err(UNSUPPORTED);
                }
                input = &rest[1..];
                continue;
            }
            let command = &rest[..letters];
            input = &rest[letters..];
            match command {
                "documentclass" if depth == 0 && !self.class && !self.main => {
                    optional(&mut input)?;
                    if !matches!(required(&mut input)?, "article" | "report" | "book") {
                        return Err(UNSUPPORTED);
                    }
                    self.class = true;
                }
                "usepackage" if depth == 0 && self.class && !self.main && !self.ended => {
                    optional(&mut input)?;
                    if !required(&mut input)?.split(',').map(str::trim).all(|name| {
                        matches!(
                            name,
                            "inputenc"
                                | "fontenc"
                                | "babel"
                                | "amsmath"
                                | "amssymb"
                                | "graphicx"
                                | "booktabs"
                                | "geometry"
                        )
                    }) {
                        return Err(UNSUPPORTED);
                    }
                }
                "begin" | "end" => {
                    let environment = required(&mut input)?;
                    if environment == "document" {
                        if depth != 0 {
                            return Err(UNSUPPORTED);
                        }
                        if command == "begin" && self.class && !self.main && !self.ended {
                            self.main = true;
                        } else if command == "end" && self.main && self.environments.is_empty() {
                            self.main = false;
                            self.ended = true;
                        } else {
                            return Err(UNSUPPORTED);
                        }
                    } else if self.main
                        && matches!(environment, "abstract" | "figure" | "table" | "center")
                    {
                        if command == "begin" {
                            optional(&mut input)?;
                            if self.environments.len() >= 16 {
                                return Err(UNSUPPORTED);
                            }
                            self.environments.push(environment);
                        } else if self.environments.len() <= initial_environments
                            || self.environments.pop() != Some(environment)
                        {
                            return Err(UNSUPPORTED);
                        }
                    } else {
                        return Err(UNSUPPORTED);
                    }
                }
                "cite" if self.main && self.environments.is_empty() => {
                    optional(&mut input)?;
                    for key in keys(required(&mut input)?) {
                        let key = key?;
                        if self.cited.insert(key) {
                            if self.cited.len() > MAX_KEYS {
                                return Err("key-limit");
                            }
                            self.result.citations.push(key);
                        }
                    }
                }
                "include" | "input" if self.main && self.environments.is_empty() => {
                    let target = required(&mut input)?;
                    if !path(target) {
                        return Err(UNSUPPORTED);
                    }
                    let tex = if target.ends_with(".tex") {
                        target.to_owned()
                    } else {
                        format!("{target}.tex")
                    };
                    if command == "include"
                        && (target.ends_with(".tex")
                            || !self.result.includes.insert(format!("{target}.aux")))
                    {
                        return Err(UNSUPPORTED);
                    }
                    self.visit(&tex, files, depth + 1)?;
                }
                "bibliographystyle"
                    if depth == 0
                        && self.main
                        && self.environments.is_empty()
                        && self.result.style.is_empty() =>
                {
                    let style = required(&mut input)?;
                    if !matches!(style, "plain" | "abbrv" | "unsrt") {
                        return Err("unsupported-bibliography-style");
                    }
                    self.result.style = style;
                }
                "bibliography"
                    if depth == 0
                        && self.main
                        && self.environments.is_empty()
                        && self.result.database.is_empty() =>
                {
                    let database = required(&mut input)?;
                    if !path(database) {
                        return Err(UNSUPPORTED);
                    }
                    self.result.database = database;
                }
                "title" | "author" | "date"
                    if depth == 0 && self.class && !self.main && !self.ended =>
                {
                    required(&mut input)?;
                }
                "section" | "subsection" | "subsubsection" | "chapter" | "caption" | "label"
                | "ref" | "pageref" | "textbf" | "textit" | "emph" | "includegraphics"
                    if self.main =>
                {
                    input = spaces(input);
                    if let Some(rest) = input.strip_prefix('*') {
                        input = rest;
                    }
                    optional(&mut input)?;
                    required(&mut input)?;
                }
                "maketitle" | "tableofcontents" | "listoffigures" | "listoftables" | "par"
                | "centering" | "noindent" | "newpage" | "clearpage" | "smallskip" | "medskip"
                | "bigskip"
                    if self.main => {}
                _ => return Err(UNSUPPORTED),
            }
        }
        if self.environments.len() != initial_environments {
            return Err(UNSUPPORTED);
        }
        Ok(())
    }
}

fn spaces(mut input: &str) -> &str {
    loop {
        input = input.trim_start();
        if !input.starts_with('%') {
            return input;
        }
        input = input.split_once('\n').map_or("", |(_, rest)| rest);
    }
}

fn required<'a>(input: &mut &'a str) -> Result<&'a str, Issue> {
    argument(input, '{', '}')
}

fn optional(input: &mut &str) -> Result<(), Issue> {
    if spaces(input).starts_with('[') {
        argument(input, '[', ']')?;
    }
    Ok(())
}

fn argument<'a>(input: &mut &'a str, open: char, close: char) -> Result<&'a str, Issue> {
    let (value, rest) = spaces(input)
        .strip_prefix(open)
        .and_then(|s| s.split_once(close))
        .ok_or(UNSUPPORTED)?;
    if value.len() > 1024 || value.contains(['\\', '{', '}', '[', ']', '%', '^', '#']) {
        return Err(UNSUPPORTED);
    }
    *input = rest;
    Ok(value.trim())
}
