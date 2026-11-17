"""Project-authored guarded patch for the exact BSD-licensed PDFium input.

Keep corresponding original sources and notices. Full PDFium must be rebuilt
with these headers/sources before linking; mixed-definition archives are invalid.
"""
from pathlib import Path
import hashlib

PATCHES = [('core/fxge/cfx_face.h', '185f2e6fd6ee11198e9eeb3cbb27cc743cede2043fe1f152337670377a2575b7', '046a3b503fe4e9bdd23966d6e3d492365c4c25d5b2139dc79fc67c460931e642', [('  bool IsBold() const;\n\n', '  bool IsBold() const;\n\n  // Overclone read-only pinned-source measurement guard; no layout change.\n  bool E1StableOutlineFace() const {\n#if defined(PDF_ENABLE_FREETYPE)\n    const auto* face = GetRec();\n    return face && !IsTricky() && !FT_HAS_MULTIPLE_MASTERS(face) && !FT_HAS_COLOR(face) && !(face->face_flags & FT_FACE_FLAG_SVG);\n#else\n    return false;\n#endif\n  }\n\n')]), ('core/fpdfapi/page/cpdf_generalstate.h', '7f14b956313e80bb421f17f2133f36f0ee9fbfe8b73f1e12f54432a6aacd9c33', '716ebbab81df699578a3d31f820a092a25ecb253f92b0365afefd3678e87cddf', [('  bool HasRef() const { return !!ref_; }\n\n', '  bool HasRef() const { return !!ref_; }\n\n  // Overclone read-only pinned-source guard; no data-layout change.\n  bool E1DefaultGeometryState() const {\n    const auto* d = ref_.GetObject();\n    return !d || (d->blend_type_ == BlendMode::kNormal && !d->soft_mask_ &&\n      d->stroke_alpha_ == 1 && d->fill_alpha_ == 1 && !d->tr_ && !d->transfer_func_ &&\n      d->render_intent_ == 0 && !d->stroke_adjust_ && !d->alpha_source_ && !d->text_knockout_ &&\n      !d->stroke_op_ && !d->fill_op_ && d->opmode_ == 0 && !d->bg_ && !d->ucr_ && !d->ht_ &&\n      d->flatness_ == 1 && d->smoothness_ == 0 && d->graphics_resource_names_.empty());\n  }\n\n')])]

def apply(source: Path) -> None:
    for relative, before_sha, after_sha, replacements in PATCHES:
        path = source / relative
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest == after_sha:
            continue
        if digest != before_sha:
            raise SystemExit(f"unexpected PDFium source for guarded patch: {relative}")
        text = data.decode("utf-8")
        for before, after in replacements:
            if text.count(before) != 1:
                raise SystemExit(f"unexpected PDFium patch context: {relative}")
            text = text.replace(before, after, 1)
        if hashlib.sha256(text.encode("utf-8")).hexdigest() != after_sha:
            raise SystemExit(f"patched PDFium source digest mismatch: {relative}")
        path.write_text(text)
