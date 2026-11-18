use crate::rendered_review::evaluate;
use serde_json::{json, Value};

const BATCH: &str = include_str!("../fixtures/caption-placement/paired-batch.json");
const LINEAGE: &str = include_str!("../fixtures/caption-placement/lineage.json");

fn request(object: u16) -> Value {
    let lineage: Value = serde_json::from_str(LINEAGE).unwrap();
    json!({
        "protocolVersion": 1, "requestId": "0".repeat(32), "operation": "evaluateRenderedReview",
        "payload": {
            "pdfSha256": lineage["pdf"]["sha256"], "compileRequestId": "1".repeat(32), "compileEpoch": 2,
            "inputDigest": "2".repeat(64), "captureId": "342572e0-50c1-4a4f-ba24-67828b942ed6",
            "profile": {"id": "knu-csc-2025-rendered-review", "version": "1.1.0"},
            "workType": "unspecified", "annotationVersion": 4, "pageCount": 1, "requestedPages": [0],
            "annotations": [{"annotationId": 9, "role": "figureCaption", "pageIndex": 0,
                "characterStart": 0, "characterEnd": 4, "sourceFileId": null, "sourceSpan": null,
                "provenance": "authorLabel", "certainty": "confirmedObservation"}],
            "factsSha256": lineage["batch"]["sha256"],
            "captionImagePair": {
                "captionAnnotationId": 9, "objectIndex": object, "inspectionRequestId": "3".repeat(32),
                "inspectionEpoch": 6, "rendererLoaderSha256": lineage["runtime"]["loader"]["sha256"],
                "rendererWasmSha256": lineage["runtime"]["wasm"]["sha256"],
                "layoutFactsSha256": lineage["layout"]["sha256"], "objectFactsSha256": lineage["objects"]["sha256"],
            },
        },
    })
}

fn batch() -> Value {
    serde_json::from_str(BATCH).unwrap()
}

fn report(request: &Value, bytes: &str) -> Value {
    serde_json::from_str(&evaluate(&request.to_string(), bytes)).unwrap()
}

fn assert_error(request: &Value, facts: &Value, code: &str) {
    assert_eq!(
        report(request, &facts.to_string()),
        json!({
            "protocolVersion": 1, "status": "failed", "error": {"code": code},
        })
    );
}

fn assert_indeterminate(facts: &Value, reason: &str) {
    let response = report(&request(0), &facts.to_string());
    assert_eq!(response["status"], "completed", "{response}");
    assert_eq!(response["payload"]["relation"], "indeterminate");
    assert_eq!(response["payload"]["reason"], reason);
    assert_eq!(response["payload"]["geometry"], Value::Null);
}

#[test]
fn actual_shared_native_page_measures_each_explicit_occurrence_in_upright_physical_frame() {
    for (index, relation, gap) in [
        (0, "captionAbove", 36.0),
        (1, "captionBelow", -20.3994140625),
    ] {
        let request = request(index);
        let response = report(&request, BATCH);
        assert_eq!(response["status"], "completed", "{response}");
        assert_eq!(response["requestId"], request["requestId"]);
        let payload = &response["payload"];
        for key in [
            "pdfSha256",
            "compileRequestId",
            "compileEpoch",
            "inputDigest",
            "captureId",
            "profile",
            "workType",
            "annotationVersion",
            "pageCount",
            "requestedPages",
            "factsSha256",
            "captionImagePair",
        ] {
            assert_eq!(payload[key], request["payload"][key]);
        }
        assert_eq!(payload["pageIndex"], 0);
        assert_eq!(payload["relation"], relation);
        assert_eq!(payload["reason"], "measured-separation");
        assert_eq!(
            payload["method"],
            "conditional-native-caption-image-vertical-gap"
        );
        assert_eq!(payload["limitations"], json!(super::LIMITATIONS));
        assert!(payload.get("results").is_none());
        let geometry = &payload["geometry"];
        assert_eq!(geometry["rawSignedGap"], gap);
        let gap_mm = geometry["signedGapMm"].as_f64().unwrap();
        assert!((gap_mm - gap * 2.0 * 25.4 / 72.0).abs() < 1e-12);
        assert_eq!(
            geometry["captionInkBox"],
            json!({"left":40.0,"bottom":80.0,"right":68.8011703491211,"top":89.6005859375})
        );
        let actual = batch();
        let image = &actual["objectLayout"]["images"][usize::from(index)];
        let bounds = &image["rawBox"];
        for (key, i) in ["left", "bottom", "right", "top"].iter().zip(0..4) {
            assert_eq!(
                geometry["imagePlacementBox"][key].as_f64(),
                bounds[i].as_f64()
            );
        }
        let outline_error = actual["pages"][0]["facts"]["characters"][0]["inkUncertainty"]
            ["absoluteErrorRaw"]
            .as_f64()
            .unwrap();
        let placement_error = image["uncertainty"]["absoluteErrorRaw"].as_f64().unwrap();
        let raw_error = geometry["absoluteErrorRaw"].as_f64().unwrap();
        assert!(raw_error > outline_error + placement_error && raw_error < 0.12);
        let mm_error = geometry["absoluteErrorMm"].as_f64().unwrap();
        assert!(mm_error > raw_error * 2.0 * 25.4 / 72.0 && mm_error < 0.085);
    }
}

#[test]
fn refuses_unknown_unconfirmed_ambiguous_or_cross_page_selections() {
    let facts = batch();
    assert_error(&request(2), &facts, "InvalidInput"); // Direct text object, not an image.
    let mut bad = request(0);
    bad["payload"]["captionImagePair"]["captionAnnotationId"] = json!(1);
    assert_error(&bad, &facts, "InvalidAnnotations");
    for (key, value) in [
        ("role", json!("footnote")),
        ("certainty", json!("tentative")),
        ("characterEnd", Value::Null),
    ] {
        let mut bad = request(0);
        bad["payload"]["annotations"][0][key] = value;
        assert_error(&bad, &facts, "InvalidAnnotations");
    }
    let mut bad = request(0);
    let duplicate = bad["payload"]["annotations"][0].clone();
    bad["payload"]["annotations"]
        .as_array_mut()
        .unwrap()
        .push(duplicate);
    assert_error(&bad, &facts, "InvalidAnnotations");
    let mut bad = request(0);
    bad["payload"]["pageCount"] = json!(2);
    bad["payload"]["requestedPages"] = json!([0, 1]);
    assert_error(&bad, &facts, "InvalidInput");
    bad["payload"]["requestedPages"] = json!([0]);
    bad["payload"]["annotations"][0]["pageIndex"] = json!(1);
    let mut facts = facts;
    facts["pages"][0]["facts"]["pageCount"] = json!(2);
    facts["objectLayout"]["pageCount"] = json!(2);
    assert_error(&bad, &facts, "InvalidAnnotations");
}

#[test]
fn verifies_exact_fact_versions_page_frame_and_completed_inventory() {
    for (key, value) in [
        ("schemaVersion", json!(2)),
        ("pageIndex", json!(1)),
        ("pageCount", json!(2)),
        ("media", json!([0, 0, 220, 220])),
        ("crop", Value::Null),
        ("effective", json!([0, 0, 220, 220])),
        ("coordinateSpace", json!("device")),
        ("scope", json!("recursive")),
        ("rotation", json!(90)),
        (
            "userUnit",
            json!({"status":"available","value":1,"method":"default-userunit"}),
        ),
        ("scanComplete", json!(false)),
        ("directObjectCount", json!(10001)),
        ("limitations", json!([])),
    ] {
        let mut facts = batch();
        facts["objectLayout"][key] = value;
        assert_error(&request(0), &facts, "InvalidFacts");
    }
    for key in ["schemaVersion", "documentSha256"] {
        let mut facts = batch();
        facts[key] = if key == "schemaVersion" {
            json!(1)
        } else {
            json!("f".repeat(64))
        };
        assert_error(&request(0), &facts, "InvalidFacts");
    }
    let mut facts = batch();
    let duplicate = facts["pages"][0].clone();
    facts["pages"].as_array_mut().unwrap().push(duplicate);
    assert_error(&request(0), &facts, "InvalidFacts");
    let mut facts = batch();
    facts["pages"][0]["facts"]["complete"] = json!(false);
    assert_error(&request(0), &facts, "InvalidFacts");
    let mut facts = batch();
    facts["objectLayout"]["objectTypeCounts"]["image"] = json!(1);
    assert_error(&request(0), &facts, "InvalidFacts");
}

#[test]
fn verifies_native_geometry_even_for_unselected_objects_and_rejects_duplicate_fields() {
    for (key, value) in [
        ("objectIndex", json!(0)),
        ("active", json!(false)),
        ("limitations", json!([])),
        ("method", json!("ink")),
        ("matrix", json!([80.000001, 0, 0, 22, 24, 110])),
        (
            "quad",
            json!([[24, 110], [104, 110], [104, 133], [24, 132]]),
        ),
        ("rawBox", json!([24, 110, 104, 131])),
        (
            "uncertainty",
            json!({"absoluteErrorRaw": 0,"method":"pdfium-float32-matrix-bound"}),
        ),
    ] {
        let mut facts = batch();
        facts["objectLayout"]["images"][1][key] = value;
        assert_error(&request(0), &facts, "InvalidFacts");
    }
    let mut facts = batch();
    facts["objectLayout"]["images"][1]
        .as_object_mut()
        .unwrap()
        .remove("uncertainty");
    assert_error(&request(0), &facts, "InvalidFacts");
    let duplicated = BATCH.replacen(
        "\"scanComplete\":true",
        "\"scanComplete\":true,\"scanComplete\":true",
        1,
    );
    assert_eq!(
        report(&request(0), &duplicated)["error"]["code"],
        "InvalidFacts"
    );
    let mut bad = request(0);
    for (key, value) in [
        ("inspectionEpoch", json!(0)),
        ("inspectionRequestId", json!("bad")),
        ("rendererLoaderSha256", json!("a")),
        ("objectFactsSha256", json!("A".repeat(64))),
    ] {
        bad["payload"]["captionImagePair"][key] = value;
        assert_error(&bad, &batch(), "InvalidInput");
        bad = request(0);
    }
    bad["payload"]["captionImagePair"] = json!([]);
    assert_error(&bad, &batch(), "InvalidInput");
}

#[test]
fn domain_refusals_have_no_numeric_or_normative_outcome() {
    let mut facts = batch();
    facts["pages"][0]["facts"]["rotation"] = json!(90);
    facts["objectLayout"]["rotation"] = json!(90);
    assert_indeterminate(&facts, "unsupported-page-rotation");

    let mut facts = batch();
    let unit = json!({"status":"indeterminate","value":null,"method":"invalid"});
    facts["pages"][0]["capabilities"]["physicalScale"] = json!("indeterminate");
    facts["pages"][0]["facts"]["userUnit"] = unit.clone();
    facts["objectLayout"]["userUnit"] = unit;
    assert_indeterminate(&facts, "unavailable-physical-scale");

    let mut facts = batch();
    let row = &mut facts["objectLayout"]["images"][0];
    row["status"] = json!("indeterminate");
    row["reason"] = json!("clip-path-present");
    for key in ["matrix", "quad", "rawBox", "uncertainty"] {
        row[key] = Value::Null;
    }
    assert_indeterminate(&facts, "unavailable-image-placement");

    let mut facts = batch();
    let glyph = &mut facts["pages"][0]["facts"]["characters"][0];
    glyph["inkStatus"] = json!("indeterminate");
    glyph["inkBox"] = Value::Null;
    glyph["inkUncertainty"] = Value::Null;
    assert_indeterminate(&facts, "unavailable-caption-ink");

    let mut facts = batch();
    facts["pages"][0]["facts"]["characters"][0]["inkBox"][1] = json!(2.05);
    assert_indeterminate(&facts, "caption-ink-crosses-effective-page");
}

fn with_image([left, bottom, right, top]: [f64; 4]) -> Value {
    let mut facts = batch();
    let row = &mut facts["objectLayout"]["images"][0];
    row["matrix"] = json!([right - left, 0.0, 0.0, top - bottom, left, bottom]);
    row["quad"] = json!([[left, bottom], [right, bottom], [right, top], [left, top]]);
    row["rawBox"] = json!([left, bottom, right, top]);
    row["uncertainty"]["absoluteErrorRaw"] =
        json!((8.0 * f64::from(f32::EPSILON) + 16.0 * f64::EPSILON) * right.max(top));
    facts
}

#[test]
fn refuses_horizontal_nonoverlap_and_any_vertical_gap_explained_by_uncertainty() {
    assert_indeterminate(
        &with_image([125.0, 20.0, 150.0, 44.0]),
        "horizontal-overlap-unestablished",
    );
    assert_indeterminate(
        &with_image([68.75, 20.0, 150.0, 44.0]),
        "horizontal-overlap-unestablished",
    );
    for top in [79.9375, 80.0, 85.0] {
        assert_indeterminate(
            &with_image([18.0, 20.0, 118.0, top]),
            "vertical-separation-unestablished",
        );
    }
}

#[test]
fn placement_must_be_opted_into_and_never_changes_legacy_shape() {
    let mut facts = batch();
    facts["schemaVersion"] = json!(1);
    facts.as_object_mut().unwrap().remove("objectLayout");
    let mut legacy = request(0);
    legacy["payload"]["profile"]["version"] = json!("1.0.0");
    assert_error(&legacy, &facts, "InvalidInput");
    legacy["payload"]["captionImagePair"] = Value::Null;
    assert_error(&legacy, &facts, "InvalidInput");
    legacy["payload"]
        .as_object_mut()
        .unwrap()
        .remove("captionImagePair");
    let report = report(&legacy, &facts.to_string());
    assert_eq!(report["status"], "completed");
    assert!(report["payload"].get("results").is_some());
    assert!(report["payload"].get("captionImagePair").is_none());
    assert!(report["payload"].get("geometry").is_none());
}
