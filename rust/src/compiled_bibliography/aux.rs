//! Recognize closed classic numeric controls emitted by the supported TeX path.
use super::{groups, key, keys, path, Files, MAX_KEYS};
use std::collections::{BTreeMap, BTreeSet};

type Issue = &'static str;
const UNSUPPORTED: Issue = "unsupported-aux-control";

#[derive(Default)]
pub struct Controls<'a> {
    pub citations: Vec<&'a str>,
    pub labels: BTreeMap<&'a str, u32>,
    pub includes: BTreeSet<String>,
    pub style: &'a str,
    pub database: &'a str,
}

#[derive(Default)]
struct Reader<'a> {
    controls: Controls<'a>,
    visited: BTreeSet<&'a str>,
    cited: BTreeSet<&'a str>,
}

pub fn inspect<'a>(root: &str, files: &Files<'a>) -> Result<Controls<'a>, Issue> {
    let mut reader = Reader::default();
    reader.visit(root, files, 0)?;
    if reader.visited.len() != files.len()
        || reader.controls.style.is_empty()
        || reader.controls.database.is_empty()
    {
        return Err(UNSUPPORTED);
    }
    Ok(reader.controls)
}

impl<'a> Reader<'a> {
    fn visit(&mut self, name: &str, files: &Files<'a>, depth: usize) -> Result<(), Issue> {
        let (&name, &text) = files.get_key_value(name).ok_or("missing-captured-aux")?;
        if depth >= 16 || !self.visited.insert(name) {
            return Err("ambiguous-aux-include");
        }
        let mut checkpoint = false;
        for line in text.lines().map(str::trim) {
            if line.is_empty() {
                continue;
            }
            if checkpoint {
                if line == "}" {
                    checkpoint = false;
                    continue;
                }
                let fields = line
                    .strip_prefix("\\setcounter")
                    .and_then(|s| groups(s, 2))
                    .ok_or(UNSUPPORTED)?;
                if !fields[0].bytes().all(|b| b.is_ascii_alphabetic())
                    || fields[1].parse::<i32>().is_err()
                {
                    return Err(UNSUPPORTED);
                }
                continue;
            }
            if matches!(
                line,
                "\\relax"
                    | "\\providecommand\\babel@aux[2]{}"
                    | "\\@nameuse{bbl@beforestart}"
                    | "\\catcode \u{60}\"\\active"
            ) {
                continue;
            }
            let boundary = line.find(['{', ' ']).unwrap_or(line.len());
            let (command, value) = line.split_at(boundary);
            match command {
                "\\citation" => {
                    for key in keys(groups(value, 1).ok_or(UNSUPPORTED)?[0]) {
                        let key = key?;
                        if self.cited.insert(key) {
                            if self.cited.len() > MAX_KEYS {
                                return Err("key-limit");
                            }
                            self.controls.citations.push(key);
                        }
                    }
                }
                "\\bibcite" if depth == 0 => {
                    let fields = groups(value, 2).ok_or(UNSUPPORTED)?;
                    let number = positive(fields[1]).ok_or("unsupported-bibcite-label")?;
                    if !key(fields[0]) || self.controls.labels.insert(fields[0], number).is_some() {
                        return Err(UNSUPPORTED);
                    }
                    if self.controls.labels.len() > MAX_KEYS {
                        return Err("key-limit");
                    }
                }
                "\\bibstyle" if depth == 0 && self.controls.style.is_empty() => {
                    self.controls.style = groups(value, 1).ok_or(UNSUPPORTED)?[0]
                }
                "\\bibdata" if depth == 0 && self.controls.database.is_empty() => {
                    self.controls.database = groups(value, 1).ok_or(UNSUPPORTED)?[0]
                }
                "\\@input" => {
                    let include = groups(value, 1).ok_or(UNSUPPORTED)?[0];
                    if !path(include)
                        || !include.ends_with(".aux")
                        || !self.controls.includes.insert(include.to_owned())
                    {
                        return Err(UNSUPPORTED);
                    }
                    self.visit(include, files, depth + 1)?;
                }
                "\\@setckpt" if depth > 0 => {
                    let child = value
                        .strip_suffix('{')
                        .and_then(|s| groups(s, 1))
                        .ok_or(UNSUPPORTED)?[0];
                    if name.strip_suffix(".aux") != Some(child) {
                        return Err(UNSUPPORTED);
                    }
                    checkpoint = true;
                }
                "\\babel@aux" => {
                    let fields = groups(value, 2).ok_or(UNSUPPORTED)?;
                    if !matches!(fields[0], "english" | "ukrainian") || !fields[1].is_empty() {
                        return Err(UNSUPPORTED);
                    }
                }
                "\\gdef" if depth == 0 => {
                    let last_page = value
                        .trim_start()
                        .strip_prefix("\\@abspage@last")
                        .and_then(|s| groups(s, 1))
                        .ok_or(UNSUPPORTED)?;
                    if last_page[0].parse::<u32>().is_err() {
                        return Err(UNSUPPORTED);
                    }
                }
                "\\@writefile" | "\\newlabel" => {
                    let fields = groups(value, 2).ok_or(UNSUPPORTED)?;
                    if (command == "\\@writefile" && !matches!(fields[0], "toc" | "lof" | "lot"))
                        || (command == "\\newlabel" && !key(fields[0]))
                        || ["\\citation", "\\bib", "\\@input", "\\csname", "\\write"]
                            .iter()
                            .any(|command| fields[1].contains(command))
                    {
                        return Err(UNSUPPORTED);
                    }
                }
                _ => return Err(UNSUPPORTED),
            }
        }
        if checkpoint {
            return Err(UNSUPPORTED);
        }
        Ok(())
    }
}

fn positive(value: &str) -> Option<u32> {
    if value.is_empty()
        || value.len() > 6
        || value.starts_with('0')
        || !value.bytes().all(|b| b.is_ascii_digit())
    {
        return None;
    }
    value.parse().ok()
}

pub fn read_bbl(text: &str) -> Result<Vec<&str>, Issue> {
    let mut items = Vec::new();
    let mut seen = BTreeSet::new();
    let mut begun = false;
    let mut ended = false;
    for line in text.lines().map(str::trim) {
        if line.is_empty() {
            continue;
        }
        if !begun {
            let marker = line
                .strip_prefix("\\begin{thebibliography}")
                .and_then(|s| groups(s, 1))
                .ok_or("unsupported-bbl-control")?;
            if positive(marker[0]).is_none() {
                return Err("unsupported-bbl-control");
            }
            begun = true;
            continue;
        }
        if ended {
            return Err("unsupported-bbl-control");
        }
        if line == "\\end{thebibliography}" {
            ended = true;
            continue;
        }
        if let Some(argument) = line.strip_prefix("\\bibitem") {
            let item = groups(argument, 1).ok_or("unsupported-bbl-control")?[0];
            if !key(item) || !seen.insert(item) {
                return Err("unsupported-bbl-control");
            }
            if seen.len() > MAX_KEYS {
                return Err("key-limit");
            }
            items.push(item);
            continue;
        }
        if items.is_empty() || !body_line(line) {
            return Err("unsupported-bbl-control");
        }
    }
    if !begun || !ended {
        return Err("unsupported-bbl-control");
    }
    Ok(items)
}

fn body_line(mut value: &str) -> bool {
    if value.contains(['%', '^', '#']) {
        return false;
    }
    while let Some((_, rest)) = value.split_once('\\') {
        let n = rest
            .bytes()
            .take_while(|b| b.is_ascii_alphabetic() || *b == b'@')
            .count();
        if n == 0 {
            if !rest
                .as_bytes()
                .first()
                .is_some_and(|b| *b == 96 || b"'\"^~.\\&_${}".contains(b))
            {
                return false;
            }
            value = &rest[1..];
        } else {
            if !matches!(
                &rest[..n],
                "newblock"
                    | "em"
                    | "emph"
                    | "textbf"
                    | "textit"
                    | "texttt"
                    | "url"
                    | "doi"
                    | "v"
                    | "c"
                    | "H"
                    | "u"
                    | "ss"
                    | "ae"
                    | "o"
                    | "aa"
                    | "i"
            ) {
                return false;
            }
            value = &rest[n..];
        }
    }
    true
}
