use super::*;
use serde_json::{json, Value};

const BBL: &str = include_str!("fixtures/plain.bbl");
const POSITIVE: (&str, &str, &str, &str) = (
    include_str!("fixtures/positive/main.tex"),
    include_str!("fixtures/positive/part.tex"),
    include_str!("fixtures/positive/main.aux"),
    include_str!("fixtures/positive/part.aux"),
);
const NEGATIVE: (&str, &str, &str, &str) = (
    include_str!("fixtures/plain/main.tex"),
    include_str!("fixtures/plain/part.tex"),
    include_str!("fixtures/plain/main.aux"),
    include_str!("fixtures/plain/part.aux"),
);

fn run((main, part, aux, part_aux): (&str, &str, &str, &str), bbl: &str, confirmed: bool) -> Value {
    let request = json!({
        "protocolVersion":1, "requestId":"1".repeat(32), "operation":"compiledBibliography",
        "root":"main.tex", "rootAux":"main.aux", "confirmMainWork":confirmed,
        "sourceFiles":[{"name":"main.tex","length":main.len()},{"name":"chapters/part.tex","length":part.len()}],
        "auxFiles": if part_aux.is_empty() { json!([{"name":"main.aux","length":aux.len()}]) }
            else { json!([{"name":"main.aux","length":aux.len()},{"name":"chapters/part.aux","length":part_aux.len()}]) },
        "bblBytes":bbl.len()
    });
    let packed = [main, part, aux, part_aux, bbl].concat();
    serde_json::from_str(&evaluate(&request.to_string(), packed.as_bytes())).unwrap()
}

#[test]
fn actual_classic_bibtex_positive_and_alphabetized_difference() {
    let positive = run(POSITIVE, BBL, true);
    assert_eq!(positive["status"], "match");
    assert_eq!(
        positive["rows"][0],
        json!({"key":"first","firstUse":1,"listPosition":1,"assignedNumber":1})
    );
    let negative = run(NEGATIVE, BBL, true);
    assert_eq!(negative["status"], "difference");
    assert_eq!(negative["membershipMatches"], true);
    assert_eq!(negative["numbersMatch"], true);
    assert_eq!(negative["orderMatches"], false);
    assert_eq!(
        negative["rows"][0],
        json!({"key":"third","firstUse":1,"listPosition":3,"assignedNumber":3})
    );
}

#[test]
fn membership_and_number_differences_are_attributed_to_keys() {
    let (main, part, aux, part_aux) = POSITIVE;
    let main = main.replace("first,second", "first,missing");
    let aux = aux
        .replace("\\citation{second}", "\\citation{missing}")
        .replace("\\bibcite{third}{3}", "\\bibcite{third}{5}");
    let result = run((&main, part, &aux, part_aux), BBL, true);
    assert_eq!(result["status"], "difference");
    assert_eq!(result["membershipMatches"], false);
    assert_eq!(result["orderMatches"], true);
    assert_eq!(result["numbersMatch"], false);
    assert_eq!(
        result["rows"][1],
        json!({"key":"missing","firstUse":2,"listPosition":null,"assignedNumber":null})
    );
    assert_eq!(
        result["rows"][3],
        json!({"key":"second","firstUse":null,"listPosition":2,"assignedNumber":2})
    );
}

#[test]
fn missing_include_uncertain_scope_and_unknown_control_refuse_wholly() {
    let (main, part, aux, part_aux) = POSITIVE;
    let biber = aux.replace("\\citation{first}", "\\abx@aux@cite{first}");
    let labelled = aux.replace("{first}{1}", "{first}{{1}{2025}{{Author}}{{}}}");
    let macro_source = main.replace("\\cite{first,second}", "\\someMacro{first,second}");
    let nested_bbl = format!("{BBL}\n\\begin{{thebibliography}}{{1}}\n\\end{{thebibliography}}");
    for (result, reason) in [
        (run(POSITIVE, BBL, false), "main-work-scope-unconfirmed"),
        (
            run((main, part, aux, ""), BBL, true),
            "missing-captured-aux",
        ),
        (
            run((main, part, &biber, part_aux), BBL, true),
            "unsupported-aux-control",
        ),
        (
            run((main, part, &labelled, part_aux), BBL, true),
            "unsupported-bibcite-label",
        ),
        (
            run((&macro_source, part, aux, part_aux), BBL, true),
            "unsupported-source-form",
        ),
        (run(POSITIVE, &nested_bbl, true), "unsupported-bbl-control"),
    ] {
        assert_eq!(result["status"], "unavailable");
        assert_eq!(result["reason"], reason);
        assert_eq!(result["rows"], json!([]));
    }
}
