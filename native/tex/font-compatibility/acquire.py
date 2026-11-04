#!/usr/bin/env python3
"""Explicit, bounded acquisition of the reviewed T2A font input proposal.

Requires separate user approval before invocation. Never edits runtime profiles.
Archive extraction is a separate reviewed step; downloads are recorded verbatim.
"""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import re
import time
import urllib.error
import urllib.parse
import urllib.request


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != "https":
            raise ValueError("non-HTTPS redirect refused")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--approved-acquisition", action="store_true", required=True)
    p.add_argument("--proposal", type=Path, default=Path(__file__).with_name("coverage.lock.json"))
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--resume", action="store_true", help="verify/reuse completed files; retry only failed inputs")
    a = p.parse_args()
    if a.output.exists() and not a.resume:
        p.error("fresh private staging directory required")
    previous = {}
    if a.resume:
        old = a.output / "acquisition.json"
        if not old.is_file():
            old = a.output / "acquisition-progress.json"
        previous = {r["name"]: r for r in json.loads(old.read_text())["files"]}
    a.output.mkdir(parents=True, exist_ok=a.resume)
    (a.output / "downloads").mkdir(exist_ok=a.resume)
    proposal = json.loads(a.proposal.read_text())["acquisition"]
    items = []
    for x in proposal["outlines"]:
        assert re.fullmatch(r"[a-z0-9]+\.pfb", x["name"])
        items.append((x["name"], "https://mirrors.ibiblio.org/pub/mirrors/CTAN/fonts/ps-type1/cm-super/pfb/" + x["name"], x["indexLength"]))
    items += [
        ("lh.r15878.tar.xz", "https://ftp.math.utah.edu/pub/tex/historic/systems/texlive/2023/tlnet-final/archive/lh.r15878.tar.xz", 172080),
        ("lh-current.tar.xz", "https://mirrors.ibiblio.org/pub/mirrors/CTAN/systems/texlive/tlnet/archive/lh.tar.xz", 361316),
        ("lhfnt35g-source.zip", "https://mirrors.ibiblio.org/pub/mirrors/CTAN/fonts/cyrillic/lh/lhfnt35g-source.zip", 649571),
        ("lhfnt35g-gen-mf.zip", "https://mirrors.ibiblio.org/pub/mirrors/CTAN/fonts/cyrillic/lh/lhfnt35g-gen-mf.zip", 157010),
        ("lh-install.txt", "https://mirrors.ibiblio.org/pub/mirrors/CTAN/fonts/cyrillic/lh/install", 16384),
        ("lh-manifest.txt", "https://mirrors.ibiblio.org/pub/mirrors/CTAN/fonts/cyrillic/lh/manifest.txt", 16384),
    ]
    assert sum(n for _, _, n in items) < 40 * 1024 ** 2

    def fetch(item):
        name, url, cap = item
        old = previous.get(name)
        if old and "error" not in old:
            data = (a.output / "downloads" / name).read_bytes()
            assert len(data) == old["length"] and hashlib.sha256(data).hexdigest() == old["sha256"], name
            return old
        attempts = [] if not old else [{"url": old["url"], "error": old["error"]}]
        current_url = "https://mirrors.ctan.org/fonts/ps-type1/cm-super/pfb/" + name if old and name.endswith(".pfb") else url
        for attempt in range(3):
            try:
                opener = urllib.request.build_opener(HttpsRedirect())
                request = urllib.request.Request(current_url, headers={"User-Agent": "Overclone-R1-font-source-acquisition/1"})
                with opener.open(request, timeout=30) as response:
                    data = response.read(cap + 1)
                    if len(data) > cap:
                        raise ValueError("bounded input exceeded: " + name)
                    if name.endswith((".pfb", ".zip", ".xz")) and len(data) != cap:
                        raise ValueError("metadata length changed: " + name)
                    if name.endswith(".pfb") and data[:2] != b"\x80\x01":
                        raise ValueError("invalid PFB signature: " + name)
                    row = {"name": name, "url": current_url, "resolvedUrl": response.url, "previousFailures": attempts,
                           "length": len(data), "sha256": hashlib.sha256(data).hexdigest(), "status": response.status,
                           "contentType": response.headers.get("Content-Type"), "lastModified": response.headers.get("Last-Modified")}
                (a.output / "downloads" / name).write_bytes(data)
                return row
            except (OSError, ValueError) as error:
                attempts.append({"url": current_url, "error": str(error)})
                if isinstance(error, urllib.error.HTTPError) and error.code == 403 and name.endswith(".pfb"):
                    current_url = "https://mirrors.ctan.org/fonts/ps-type1/cm-super/pfb/" + name
                if attempt == 2:
                    return {"name": name, "url": current_url, "error": str(error), "previousFailures": attempts}
                time.sleep(attempt + 1)

    rows = []
    def state():
        return {"schemaVersion": 1, "authorized": True, "downloadCapBytes": 40 * 1024 ** 2,
                "expandedInputCapBytes": 96 * 1024 ** 2,
                "proposalSha256": hashlib.sha256(a.proposal.read_bytes()).hexdigest(), "files": rows}
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        for row in pool.map(fetch, items):
            rows.append(row)
            (a.output / "acquisition-progress.json").write_text(json.dumps(state(), indent=2) + "\n")
            if len(rows) % 12 == 0 or "error" in row:
                print(json.dumps({"completed": len(rows), "total": len(items), "last": row["name"], "error": row.get("error")}), flush=True)
    bad = [r for r in rows if "error" in r]
    temporary = a.output / "acquisition.json.tmp"
    temporary.write_text(json.dumps(state(), indent=2) + "\n")
    temporary.replace(a.output / "acquisition.json")
    print(json.dumps({"completed": len(rows), "errors": len(bad), "downloadedBytes": sum(r.get("length", 0) for r in rows)}))
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
