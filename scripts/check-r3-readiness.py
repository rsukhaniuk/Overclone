#!/usr/bin/env python3
"""Operator preflight only: no OAuth, provider generation, uploads or app writes.

The deployment worksheet stays outside product assets. Values are never echoed.
Network probes require explicit switches; HTTP checks are not browser acceptance.
"""

import argparse
import base64
import hashlib
from html.parser import HTMLParser
import ipaddress
import json
from pathlib import Path
import re
import sys
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class Refusal(Exception):
    pass


def require(condition):
    if not condition:
        raise Refusal("invalid-input")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def strict_json(data):
    try:
        return json.loads(data.decode("utf-8", errors="strict"),
                          object_pairs_hook=unique_object,
                          parse_constant=lambda _: require(False))
    except (ValueError, UnicodeError, RecursionError) as error:
        raise Refusal("invalid-input") from error


def fields(value, expected):
    require(isinstance(value, dict) and set(value) == set(expected))


def canonical_origin(value, *, local=False):
    require(isinstance(value, str) and 0 < len(value) <= 256)
    require(not any(ord(char) <= 32 or ord(char) >= 127 for char in value))
    try:
        parsed = urlsplit(value)
        host, port = parsed.hostname, parsed.port
        require(host and not parsed.username and not parsed.password)
        require(not parsed.path and not parsed.query and not parsed.fragment)
        require(parsed.scheme in (("http", "https") if local else ("https",)))
        authority = f"[{host}]" if ":" in host else host
        default = 80 if parsed.scheme == "http" else 443
        if port is not None and port != default:
            authority += f":{port}"
        require(value == f"{parsed.scheme}://{authority}")
        if local and host != "localhost":
            address = ipaddress.ip_address(host)
            require(address.is_loopback or (address.version == 4 and (
                address in ipaddress.ip_network("10.0.0.0/8") or
                address in ipaddress.ip_network("172.16.0.0/12") or
                address in ipaddress.ip_network("192.168.0.0/16"))))
        return value
    except (ValueError, TypeError) as error:
        raise Refusal("invalid-input") from error


def identifier(value, pattern, limit):
    require(isinstance(value, str) and len(value) <= limit)
    require(value == "" or re.fullmatch(pattern, value, flags=re.ASCII))
    return bool(value)


def validate(document):
    fields(document, ("schemaVersion", "deploymentOrigin", "applicationBasePath", "drive", "ai"))
    require(type(document["schemaVersion"]) is int and document["schemaVersion"] == 1)
    require(document["applicationBasePath"] == "/")
    require(isinstance(document["deploymentOrigin"], str))
    if document["deploymentOrigin"]:
        canonical_origin(document["deploymentOrigin"])
    drive, ai = document["drive"], document["ai"]
    fields(drive, ("clientId", "pickerApiKey", "projectNumber"))
    fields(ai, ("ollama", "gemini", "claude"))
    identifier(drive["clientId"], r"[A-Za-z0-9._-]+\.apps\.googleusercontent\.com", 256)
    identifier(drive["pickerApiKey"], r"[A-Za-z0-9_-]+", 256)
    identifier(drive["projectNumber"], r"[0-9]+", 30)
    for provider in ("ollama", "gemini", "claude"):
        fields(ai[provider], ("modelId", "baseUrl") if provider == "ollama" else ("modelId",))
        model = ai[provider]["modelId"]
        identifier(model, r"[A-Za-z0-9_.:/-]+", 128)
        require(not model or ("://" not in model and not model.startswith("/")))
        require(provider != "gemini" or not re.search(r"[/:]", model))
    if ai["ollama"]["baseUrl"]:
        canonical_origin(ai["ollama"]["baseUrl"], local=True)
    return document


def row(identifier, passed, failure="missing-operator-input"):
    return {"id": identifier, "status": "pass" if passed else "blocked",
            "code": "checked" if passed else failure}


def configured(document):
    ai = document["ai"]
    return [row("deployment-origin", bool(document["deploymentOrigin"])),
            row("drive-public-identifiers", all(document["drive"].values())),
            row("ollama-selected-model-endpoint", all(ai["ollama"].values())),
            row("gemini-selected-model", bool(ai["gemini"]["modelId"])),
            row("claude-selected-model", bool(ai["claude"]["modelId"]))]


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        fp.close()
        raise Refusal("redirect-refused")


def http_read(url, limit, *, method="GET", headers=None):
    request = Request(url, method=method, headers={
        "Cache-Control": "no-store", "Accept-Encoding": "identity", **(headers or {})})
    try:
        with build_opener(NoRedirect).open(request, timeout=10) as response:
            require(response.geturl() == url)
            declared = response.headers.get("Content-Length")
            require(declared is None or (declared.isascii() and declared.isdecimal() and int(declared) <= limit))
            body = response.read(limit + 1)
            require(len(body) <= limit)
            return body, response.headers
    except HTTPError as error:
        # Never read/echo an authenticated/service error body or raw exception.
        error.close()
        raise Refusal("http-rejected") from error


class MetaPolicies(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.policies = []

    def handle_starttag(self, tag, attributes):
        if tag == "meta":
            values = dict(attributes)
            if values.get("http-equiv", "").lower() == "content-security-policy":
                self.policies.append(values.get("content", ""))


def directives(policy):
    result = {}
    for directive in policy.split(";"):
        words = directive.split()
        if words:
            # CSP honors only the first occurrence; do not accidentally merge.
            result.setdefault(words[0].lower(), words[1:])
    return result


def permits(policy, directive, target, own_origin):
    sources = policy.get(directive, policy.get("default-src", []))
    if not sources or "'none'" in sources or any(source in ("http:", "https:") or "*" in source for source in sources):
        return False
    parsed = urlsplit(target)
    for source in sources:
        if source == "'self'" and target.startswith(own_origin + "/"):
            return True
        # Deliberately conservative exact-source subset: broad schemes,
        # wildcards and exotic CSP expressions are never a readiness pass.
        if source.startswith(("https://", "http://")) and "*" not in source:
            allowed = urlsplit(source)
            if (allowed.scheme, allowed.netloc) == (parsed.scheme, parsed.netloc):
                if not allowed.path or (parsed.path.startswith(allowed.path) if allowed.path.endswith("/") else parsed.path == allowed.path):
                    return True
    return False


def deployment(document, expected, fetcher=http_read):
    origin = document["deploymentOrigin"]
    require(bool(origin))
    fields(expected, ("indexSha256", "assetsSha256"))
    require(all(isinstance(value, str) and re.fullmatch("[a-f0-9]{64}", value) for value in expected.values()))
    index, headers = fetcher(origin + "/", 1024 * 1024)
    assets, _ = fetcher(origin + "/service-worker-assets.js", 4 * 1024 * 1024)
    actual = {"indexSha256": hashlib.sha256(index).hexdigest(),
              "assetsSha256": hashlib.sha256(assets).hexdigest()}
    require(actual == expected)
    match = re.fullmatch(rb"\s*self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;?\s*", assets)
    require(match)
    manifest = strict_json(match[1])
    require(isinstance(manifest, dict) and isinstance(manifest.get("assets"), list))
    entries = [entry for entry in manifest["assets"] if isinstance(entry, dict) and entry.get("url") == "index.html"]
    require(len(entries) == 1 and entries[0].get("hash") == "sha256-" + base64.b64encode(hashlib.sha256(index).digest()).decode("ascii"))
    parser = MetaPolicies()
    parser.feed(index.decode("utf-8", errors="strict"))
    header_policies = [directives(value) for value in headers.get_all("Content-Security-Policy", [])]
    policies = header_policies + [directives(value) for value in parser.policies]
    require(bool(policies))
    rows = [row("approved-static-publication", True),
            row("header-denies-embedding", any(policy.get("frame-ancestors") == ["'none'"] for policy in header_policies), "static-header-required")]
    targets = {"drive": "https://www.googleapis.com/drive/v3/files/selected?alt=media",
               "gemini": "https://generativelanguage.googleapis.com/v1beta/models/selected:generateContent",
               "claude": "https://api.anthropic.com/v1/messages"}
    local = document["ai"]["ollama"]["baseUrl"]
    if local:
        targets["ollama"] = local + "/api/chat"
    for name, target in targets.items():
        rows.append(row(name + "-effective-connect-policy", all(permits(policy, "connect-src", target, origin) for policy in policies), "effective-csp-refuses-endpoint"))
    return rows, actual


def ollama(document, fetcher=http_read):
    origin = document["deploymentOrigin"]
    selection = document["ai"]["ollama"]
    require(origin and all(selection.values()))
    _, headers = fetcher(selection["baseUrl"] + "/api/chat", 4096, method="OPTIONS", headers={
        "Origin": origin, "Access-Control-Request-Method": "POST",
        "Access-Control-Request-Headers": "content-type"})
    cors = (headers.get("Access-Control-Allow-Origin") in (origin, "*") and
            "post" in [value.strip().lower() for value in headers.get("Access-Control-Allow-Methods", "").split(",")] and
            "content-type" in [value.strip().lower() for value in headers.get("Access-Control-Allow-Headers", "").split(",")])
    tags, _ = fetcher(selection["baseUrl"] + "/api/tags", 1024 * 1024)
    data = strict_json(tags)
    require(isinstance(data, dict) and isinstance(data.get("models"), list))
    installed = any(isinstance(model, dict) and model.get("model") == selection["modelId"] for model in data["models"])
    return [row("ollama-http-preflight", cors, "cors-preflight-refused"),
            row("ollama-selected-model-installed", installed, "selected-model-not-installed")]


def read_json(path):
    with Path(path).open("rb") as stream:
        data = stream.read(32769)
    require(len(data) <= 32768)
    return strict_json(data)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", required=True, help="private operator worksheet; values are never echoed")
    parser.add_argument("--probe-deployment", action="store_true", help="explicitly GET public HTTPS index/manifest; no auth")
    parser.add_argument("--expected-publication", help="root-approved indexSha256/assetsSha256 JSON; required for deployment probe")
    parser.add_argument("--probe-ollama", action="store_true", help="explicitly OPTIONS local chat and GET tags; no generation")
    args = parser.parse_args(argv)
    result = {"schemaVersion": 1, "evidenceClass": "operator-preflight",
              "milestoneComplete": False, "checks": []}
    try:
        require(args.probe_deployment == bool(args.expected_publication))
        document = validate(read_json(args.configuration))
        result["checks"] = configured(document)
        for enabled, name, probe in (
                (args.probe_deployment, "deployment-http-probe", lambda: deployment(document, read_json(args.expected_publication))),
                (args.probe_ollama, "ollama-http-probe", lambda: (ollama(document), None))):
            if enabled:
                try:
                    rows, publication = probe()
                    result["checks"].extend(rows)
                    if publication:
                        result["publication"] = publication
                except Exception:
                    result["checks"].append(row(name, False, "probe-refused-or-unreachable"))
        result["checks"].extend({"id": name, "status": "not-run", "code": "actual-browser-and-operator-input-required"}
                                for name in ("drive-live-Chrome", "drive-live-Edge", "ollama-live-Chrome", "ollama-live-Edge", "gemini-live-Chrome", "gemini-live-Edge", "claude-live-Chrome", "claude-live-Edge"))
        result["inputsReady"] = all(item["status"] == "pass" for item in result["checks"] if not item["id"].endswith(("-Chrome", "-Edge")))
        print(json.dumps(result, separators=(",", ":")))
        return 0 if result["inputsReady"] else 1
    except Exception:
        print(json.dumps({"schemaVersion": 1, "evidenceClass": "operator-preflight", "milestoneComplete": False, "inputsReady": False, "code": "invalid-input"}, separators=(",", ":")))
        return 2


if __name__ == "__main__":
    sys.exit(main())
