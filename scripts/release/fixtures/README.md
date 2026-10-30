# Synthetic A1 fixtures

`source.txt` and `NOTICE.txt` are authored synthetic test data, not acquired
compiler/font/license material. The notice grants nothing about the real product.
`invalid-json.json` deliberately repeats a JSON field.

`test_release_materials.py` constructs a complete tiny material DAG in a temporary
directory from these bytes: two independent A/B native/Rust/managed output trees,
canonical source/build/component/selected-member/Work/notice/source-access facts,
a real ZIP over the synthetic runtime bytes, six stage receipts and their comparison.
All lengths and hashes are calculated from the bytes actually written. Nothing
executes as native code. The synthetic decision is explicitly not a real license
or provenance approval. Tests mutate independent associations, not expected hashes
to excuse failures. Use `--fixture-out "$RESULT_DIR/synthetic-inputs"` to retain one
complete generated graph for independent inspection (destination must not exist).

Expected positive outcome: `verify-inputs` returns 0 without creating `--out`.
Expected negative outcomes: stable nonzero refusal, with input bytes unchanged.
These fixtures do not prove real rebuild reproducibility, license clearance,
engine/PDF behavior, runtime isolation, browser offline support or publication.
