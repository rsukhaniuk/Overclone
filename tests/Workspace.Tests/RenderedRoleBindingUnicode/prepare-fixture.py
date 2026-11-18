"""Prepare original synthetic CRLF inputs, never edit captured native sources."""
from pathlib import Path
import hashlib
import json
import sys

destination = Path(sys.argv[1])
if destination.exists():
    raise SystemExit("Use a fresh private fixture directory.")
source = Path(__file__).resolve().parent / "Fixtures"
inputs = {}
for filename, relative in [("main.tex", "тези/my thesis.tex"), ("chapter.tex", "chapter.tex")]:
    data = (source / filename).read_bytes()
    assert b"\r" not in data
    inputs[relative] = data.replace(b"\n", b"\r\n")


def prepare(folder, files, request_id):
    folder.mkdir(parents=True)
    descriptors, packed = [], bytearray()
    for index, (name, data) in enumerate(files.items()):
        target = folder / "project" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        descriptors.append({"fileId": f"f{index}", "relativePath": name, "mediaKind": "text", "hasBom": False,
                            "byteOffset": len(packed), "byteLength": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        packed.extend(data)
    request = {"protocolVersion": 1, "requestId": request_id, "operation": "analyzeSource", "payload": {
        "snapshotId": "unicode-role-snapshot", "projectId": "unicode-role-project", "sessionId": "unicode-role-session",
        "workingGeneration": 1, "baseHeadRevisionId": None, "profile": {"id": "knu-csc-2025-source", "version": "1.6.0"},
        "configuration": {"workType": "bachelorQualification"}, "referenceConfiguration": {"rootFileId": "f0"},
        "limits": {}, "files": descriptors}}
    (folder / "request.json").write_text(json.dumps(request, ensure_ascii=False))
    (folder / "packed.bin").write_bytes(packed)


prepare(destination, inputs, "unicode-executed-include")
# These are separately declared uncertainty-isolation controls, never source
# substitutions for the original native Ukrainian/include acceptance target.
root_only = dict(inputs)
root_only["тези/my thesis.tex"] = root_only["тези/my thesis.tex"].replace(b"\\input{chapter}\r\n", b"")
prepare(destination / "controls/root-only-packages", root_only, "unicode-root-only-packages")
ascii_files = {
    "тези/my thesis.tex": b"\\documentclass{article}\r\n\\begin{document}\r\n\\begin{figure}\r\n\\caption{Root comparison}\r\n\\end{figure}\r\n\\input{chapter}\r\n\\end{document}\r\n",
    "chapter.tex": b"\\begin{table}\r\n\\caption{Included comparison}\r\n\\end{table}\r\n",
}
prepare(destination / "controls/ascii-executed-include", ascii_files, "ascii-executed-include")
print(destination)
