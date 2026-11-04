#!/usr/bin/env python3
"""Create an offline Docker build context from already acquired exact packages."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--packages", type=Path, required=True)
parser.add_argument("--base-packages", type=Path, required=True)
parser.add_argument("--out", type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parent
lock = json.loads((root/"bootstrap-lock.json").read_text())
if args.out.exists():
    raise SystemExit("fresh bootstrap context required")
args.out.mkdir(parents=True)
for group, source, rows in [("base", args.base_packages, lock["baseBuildPackages"]), ("biber", args.packages, lock["files"])]:
    (args.out/group).mkdir()
    for row in rows:
        package = source/row["name"]; data = package.read_bytes()
        if len(data) != row["length"] or hashlib.sha256(data).hexdigest() != row["sha256"]:
            raise SystemExit("bootstrap integrity mismatch")
        shutil.copyfile(package, args.out/group/row["name"])
shutil.copyfile(root/"bootstrap-lock.json", args.out/"bootstrap-lock.json")
shutil.copyfile(root/"install-bootstrap.py", args.out/"install-bootstrap.py")
(args.out/"Dockerfile").write_text("FROM "+lock["toolchainImage"]+"\nCOPY base/ /bootstrap/base/\nCOPY biber/ /bootstrap/biber/\nCOPY bootstrap-lock.json install-bootstrap.py /bootstrap/\nRUN [\"python3\", \"/bootstrap/install-bootstrap.py\"]\n")
print("Verified bootstrap context prepared; build with --network none.")
