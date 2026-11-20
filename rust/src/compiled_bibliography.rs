//! Bounded classic BibTeX compiler evidence. The caller owns capture, opened-file
//! provenance and freshness. Indeterminate TeX never contributes a partial result.
mod aux;
mod source;

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

const MAX_BYTES: usize = 512 * 1024;
const MAX_FILES: usize = 24;
const MAX_KEYS: usize = 256;
type Files<'a> = BTreeMap<&'a str, &'a str>;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    protocol_version: u8,
    request_id: String,
    operation: String,
    root: String,
    root_aux: String,
    source_files: Vec<File>,
    aux_files: Vec<File>,
    bbl_bytes: usize,
    confirm_main_work: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct File {
    name: String,
    length: usize,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ResultRow<'a> {
    key: &'a str,
    first_use: Option<usize>,
    list_position: Option<usize>,
    assigned_number: Option<u32>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report<'a> {
    protocol_version: u8,
    request_id: &'a str,
    status: &'static str,
    reason: Option<&'static str>,
    membership_matches: Option<bool>,
    order_matches: Option<bool>,
    numbers_match: Option<bool>,
    rows: Vec<ResultRow<'a>>,
}

pub fn evaluate(metadata: &str, packed: &[u8]) -> String {
    if metadata.len() > 16 * 1024 || packed.len() > MAX_BYTES {
        return String::new();
    }
    let Ok(request) = serde_json::from_str::<Request>(metadata) else {
        return String::new();
    };
    if request.protocol_version != 1
        || request.operation != "compiledBibliography"
        || request.request_id.len() != 32
        || !request
            .request_id
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return String::new();
    }
    let mut report = Report {
        protocol_version: 1,
        request_id: &request.request_id,
        status: "unavailable",
        reason: None,
        membership_matches: None,
        order_matches: None,
        numbers_match: None,
        rows: Vec::new(),
    };
    if let Err(reason) = evaluate_inner(&request, packed, &mut report) {
        report.reason = Some(reason);
    }
    serde_json::to_string(&report).unwrap_or_default()
}

fn evaluate_inner<'a>(
    request: &'a Request,
    packed: &'a [u8],
    report: &mut Report<'a>,
) -> Result<(), &'static str> {
    if !request.confirm_main_work {
        return Err("main-work-scope-unconfirmed");
    }
    let mut tail = packed;
    let sources = read_files(&request.source_files, &mut tail, ".tex")?;
    let aux = read_files(&request.aux_files, &mut tail, ".aux")?;
    if request.bbl_bytes == 0 || request.bbl_bytes != tail.len() {
        return Err("invalid-capture");
    }
    let bibliographic_text = text(tail)?;
    let source = source::inspect(&request.root, &sources)?;
    let controls = aux::inspect(&request.root_aux, &aux)?;
    if source.style != controls.style
        || source.database != controls.database
        || source.includes != controls.includes
        || source.citations != controls.citations
    {
        return Err("source-and-aux-disagree");
    }
    let items = aux::read_bbl(bibliographic_text)?;
    if items.len() != controls.labels.len()
        || items.iter().any(|key| !controls.labels.contains_key(key))
    {
        return Err("bbl-and-labels-disagree");
    }
    let first: BTreeMap<_, _> = controls
        .citations
        .iter()
        .enumerate()
        .map(|(i, key)| (*key, i + 1))
        .collect();
    let positions: BTreeMap<_, _> = items
        .iter()
        .enumerate()
        .map(|(i, key)| (*key, i + 1))
        .collect();
    let keys: BTreeSet<_> = first.keys().chain(positions.keys()).copied().collect();
    if keys.len() > MAX_KEYS {
        return Err("key-limit");
    }
    let mut rows: Vec<_> = keys
        .into_iter()
        .map(|key| ResultRow {
            key,
            first_use: first.get(key).copied(),
            list_position: positions.get(key).copied(),
            assigned_number: controls.labels.get(key).copied(),
        })
        .collect();
    rows.sort_by_key(|row| {
        (
            row.first_use.unwrap_or(usize::MAX),
            row.list_position.unwrap_or(usize::MAX),
        )
    });
    let membership = first.keys().eq(positions.keys());
    // Order among shared keys remains observable even when membership differs.
    let ordered = controls
        .citations
        .iter()
        .filter(|key| positions.contains_key(*key))
        .eq(items.iter().filter(|key| first.contains_key(*key)));
    let numbers = items
        .iter()
        .enumerate()
        .all(|(i, key)| controls.labels.get(key) == Some(&((i + 1) as u32)));
    report.status = if membership && ordered && numbers {
        "match"
    } else {
        "difference"
    };
    report.membership_matches = Some(membership);
    report.order_matches = Some(ordered);
    report.numbers_match = Some(numbers);
    report.rows = rows;
    Ok(())
}

fn read_files<'a>(
    items: &'a [File],
    tail: &mut &'a [u8],
    extension: &str,
) -> Result<Files<'a>, &'static str> {
    if items.is_empty() || items.len() > MAX_FILES {
        return Err("invalid-capture");
    }
    let mut result = Files::new();
    for item in items {
        if !path(&item.name)
            || !item.name.ends_with(extension)
            || item.length == 0
            || item.length > tail.len()
        {
            return Err("invalid-capture");
        }
        let (body, rest) = tail.split_at(item.length);
        *tail = rest;
        if result.insert(item.name.as_str(), text(body)?).is_some() {
            return Err("invalid-capture");
        }
    }
    Ok(result)
}

fn text(bytes: &[u8]) -> Result<&str, &'static str> {
    if bytes.contains(&0) {
        return Err("invalid-capture");
    }
    let value = std::str::from_utf8(bytes).map_err(|_| "invalid-capture")?;
    Ok(value.trim_start_matches('\u{feff}'))
}

fn path(path: &str) -> bool {
    !path.is_empty()
        && path.len() <= 249
        && path.split('/').count() <= 16
        && path.split('/').all(|segment| {
            !segment.is_empty()
                && segment != "."
                && segment != ".."
                && segment.trim() == segment
                && segment
                    .chars()
                    .all(|c| c.is_alphanumeric() || matches!(c, '.' | '_' | '-' | ' '))
        })
}

fn key(key: &str) -> bool {
    !key.is_empty()
        && key.len() <= 96
        && key
            .chars()
            .all(|c| c.is_alphanumeric() || matches!(c, ':' | '.' | '_' | '-' | '/' | '+'))
}

fn keys(text: &str) -> impl Iterator<Item = Result<&str, &'static str>> {
    text.split(',').map(|value| {
        let value = value.trim();
        if key(value) {
            Ok(value)
        } else {
            Err("unsupported-citation-key")
        }
    })
}

fn groups(mut value: &str, count: usize) -> Option<Vec<&str>> {
    let mut result = Vec::with_capacity(count);
    for _ in 0..count {
        value = value.trim_start().strip_prefix('{')?;
        let mut depth = 1;
        let mut escaped = false;
        let end = value.bytes().position(|b| {
            if escaped {
                escaped = false;
                return false;
            }
            match b {
                b'\\' => escaped = true,
                b'{' => depth += 1,
                b'}' => depth -= 1,
                _ => {}
            }
            depth == 0
        })?;
        result.push(&value[..end]);
        value = &value[end + 1..];
    }
    value.trim().is_empty().then_some(result)
}

#[cfg(test)]
mod tests;
