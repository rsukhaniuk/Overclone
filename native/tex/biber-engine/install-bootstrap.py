#!/usr/bin/env python3
"""Docker build-only exact package installation, without APT/network access."""
import hashlib
import json
from pathlib import Path
import subprocess

root = Path("/bootstrap")
lock = json.loads((root/"bootstrap-lock.json").read_text())
for group, rows in [("base", lock["baseBuildPackages"]), ("biber", lock["files"])]:
    files = []
    for row in rows:
        package = root/group/row["name"]; data = package.read_bytes()
        if len(data) != row["length"] or hashlib.sha256(data).hexdigest() != row["sha256"]:
            raise SystemExit("bootstrap package integrity mismatch")
        files.append(str(package))
    subprocess.run(["dpkg", "--unpack", *files], check=True)
    subprocess.run(["dpkg", "--configure", "--pending"], check=True)
