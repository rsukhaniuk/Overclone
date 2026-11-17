"""Prepare exact synthetic CRLF inputs; invoke the existing public Rust entry separately."""
import hashlib
import json
from pathlib import Path
import sys

destination = Path(sys.argv[1])
if destination.exists():
    raise SystemExit('Use a fresh fixture directory.')
destination.mkdir(parents=True)
source = Path(__file__).resolve().parent / 'Fixtures'
inputs = {}
for filename, relative in [('main.tex', 'тези/my thesis.tex'), ('chapter.tex', 'chapter.tex')]:
    data = (source / filename).read_bytes()
    assert b'\r' not in data
    inputs[relative] = data.replace(b'\n', b'\r\n')


def prepare(folder, content, request_id):
    packed = bytearray()
    descriptors = []
    for index, (name, data) in enumerate(content.items()):
        target = folder / 'project' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        descriptors.append({'fileId': f'f{index}', 'relativePath': name, 'mediaKind': 'text', 'hasBom': False,
                            'byteOffset': len(packed), 'byteLength': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        packed.extend(data)
    request = {'protocolVersion': 1, 'requestId': request_id, 'operation': 'analyzeSource', 'payload': {
        'snapshotId': 'roles-snapshot', 'projectId': 'roles-project', 'sessionId': 'roles-session', 'workingGeneration': 1,
        'baseHeadRevisionId': None, 'profile': {'id': 'knu-csc-2025-source', 'version': '1.5.0'},
        'configuration': {'workType': 'bachelorQualification'}, 'referenceConfiguration': {'rootFileId': 'f0'},
        'limits': {}, 'files': descriptors}}
    (folder / 'request.json').write_text(json.dumps(request, ensure_ascii=False))
    (folder / 'packed.bin').write_bytes(packed)


prepare(destination, inputs, 'roles-native')
root = inputs['тези/my thesis.tex']
variants = {
    'duplicate-source-text': root.replace(b'Engine comparison', b'Orbit comparison'),
    'unknown-macro': root.replace(b'Control body text.', b'Control body text. \\unknownrole'),
    'included-generic-heading': root.replace(b'Control body text.', b'Control body text.\\input{chapter}'),
    'unknown-package': root.replace(b'\\begin{document}', b'\\usepackage{fontspec}\r\n\\begin{document}'),
}
for name, data in variants.items():
    prepare(destination / 'variants' / name, {**inputs, 'тези/my thesis.tex': data}, name)
(destination / 'analyze.rs').write_text('''fn main() {
    let folder = std::env::args().nth(1).expect("fixture folder");
    let request = std::fs::read_to_string(format!("{folder}/request.json")).unwrap();
    let packed = std::fs::read(format!("{folder}/packed.bin")).unwrap();
    std::fs::write(format!("{folder}/response.json"), document_engine::analyze_source(&request, &packed)).unwrap();
}
''')
print(destination)
