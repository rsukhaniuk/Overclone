"""Guarded, copy-only optional object-facts overlay; no acquisition or old edits."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil

WRAPPER = {
    "e1.cpp": "16fbe6833bdff747b73deb5e42bb74af763df12a7d0b4865d1986e5d282782c4",
    "e1.h": "f89ec2273343aaafd37f1a62e9b6e54edf0f21554a1130d6c2f5edba811a8043",
    "layout.inc": "550b952b21a3c2743fdb749b321362d486922e25cb64e129c99251c8ee890944",
    "ink.inc": "1775f9f9ce88308e048b1616aee4bdc467dfb305ad1c317fbaaeb4fef63917e5",
    "actions.inc": "c6af6a2ed0cdcb9483b592b31bb2e864ac11bd3fe405c2becdfdfd09f2df943a",
    "pdfium_wasm_platform.cpp": "9ab0d120909bce63be6c005e99ef34829696ccc91d2ee89556e1178ae5be1fae",
    "build.sh": "e85f7a68b96e4f55bedea96bab8137a0909466a0880b31f0be553bed8cd84a38",
}
SOURCE = {
    "core/fpdfapi/page/cpdf_image.h": "d765c6b1e912d5da7acafe038bd895cb61ef6ee00a8f5d3e1c449365ed87dea8",
    "core/fpdfapi/page/cpdf_imageobject.h": "8670bf097aa6cbb5dc3e3a629ee94d7a4d6c9697bcdaae7ce271274747b6f66a",
    "core/fpdfapi/page/cpdf_pageobject.h": "3fd11559b0a369d524de0fcbf371535fe564b600040ddc863df36ab6e21735bf",
    "core/fpdfapi/page/cpdf_generalstate.h": "716ebbab81df699578a3d31f820a092a25ecb253f92b0365afefd3678e87cddf",
    "core/fpdfapi/page/cpdf_page.h": "01c54699fd61b08a5c395b359be7c5205041d30d55d530cdfe4028db4fe9e8e4",
    "core/fpdfapi/parser/cpdf_dictionary.h": "aca0bb659732d55dcec1a4641551e99b1a83e8e0b862a1407f8829c98358b91a",
    "core/fpdfapi/parser/cpdf_object.h": "5705f528bd538c25d7c9004cb5a4ddc4d2d2cd11c0a8e96e82c6898a5ec3d141",
    "core/fxcrt/fx_coordinates.h": "254cdd35f88427259dc418a29741d0eea0e37810966123a401b57ea3a11949a4",
    "out/r2/obj/libpdfium.a": "a2512bf85e29ea765dffb79aeed34beef2b52d1a439ced40692360309bf9df19",
}


def pin(path):
    data = path.read_bytes()
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def checked(root, expected):
    rows = []
    for relative, digest in expected.items():
        path = root / relative
        actual = pin(path)
        if actual["sha256"] != digest:
            raise SystemExit(f"unexpected frozen input: {relative}")
        rows.append({"path": relative, **actual})
    return rows


def prepare(pdfium_source, destination):
    here = Path(__file__).resolve().parent
    wrapper = here.parent
    pdfium_source = pdfium_source.resolve()
    destination = destination.resolve()
    if destination.exists():
        raise SystemExit("destination must not exist; never replace a build")
    wrapper_rows = checked(wrapper, WRAPPER)
    source_rows = checked(pdfium_source, SOURCE)
    if not (pdfium_source / "out/r2/gen").is_dir():
        raise SystemExit("matching generated header directory is missing")
    destination.mkdir(parents=True)
    for name in ("parent-wrapper", "candidate-wrapper"):
        target = destination / name
        target.mkdir()
        for relative in WRAPPER:
            shutil.copyfile(wrapper / relative, target / relative)
    overlay = destination / "candidate-wrapper/object-layout"
    overlay.mkdir()
    for name in ("e1_object_layout.h", "object-layout.inc"):
        shutil.copyfile(here / name, overlay / name)
    entry = destination / "candidate-wrapper/e1.cpp"
    data = entry.read_text()
    anchor = '#include "layout.inc"\n'
    if data.count(anchor) != 1 or 'object-layout/' in data:
        raise SystemExit("unexpected wrapper append context")
    entry.write_text(data.replace(anchor, anchor + '#include "object-layout/object-layout.inc"\n'))
    receipt = {
        "schemaVersion": 1,
        "pdfiumRevision": "6688c576fd851f4159c6fae39396da50939de110",
        "wrapper": wrapper_rows,
        "pdfiumSourceHeadersAndAssociatedArchive": source_rows,
        "candidateOverlay": [
            {"path": str(p.relative_to(destination)), **pin(p)}
            for p in sorted((destination / "candidate-wrapper").rglob("*")) if p.is_file()
        ],
    }
    (destination / "inputs.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    args = argparse.ArgumentParser()
    args.add_argument("--pdfium-source", type=Path, required=True)
    args.add_argument("--destination", type=Path, required=True)
    opts = args.parse_args()
    prepare(opts.pdfium_source, opts.destination)
