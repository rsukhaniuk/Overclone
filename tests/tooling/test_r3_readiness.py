"""Synthetic preflight checks; no browser, OAuth or provider-generation proof."""

import base64
import copy
from email.message import Message
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from contextlib import redirect_stdout


SOURCE = Path(__file__).resolve().parents[2] / "scripts/check-r3-readiness.py"
SPEC = importlib.util.spec_from_file_location("r3_readiness", SOURCE)
R = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(R)


def worksheet():
    return {"schemaVersion": 1, "deploymentOrigin": "https://editor.example.invalid",
            "applicationBasePath": "/",
            "drive": {"clientId": "synthetic.apps.googleusercontent.com",
                      "pickerApiKey": "synthetic-public-picker-key", "projectNumber": "123"},
            "ai": {"ollama": {"modelId": "selected:local", "baseUrl": "http://localhost:11434"},
                   "gemini": {"modelId": "selected-gemini"}, "claude": {"modelId": "selected-claude"}}}


def headers(**values):
    result = Message()
    for key, value in values.items():
        result[key.replace("_", "-")] = value
    return result


def publication(policy=None, extra_header=None):
    connect = "connect-src 'self' http://localhost:11434 https://www.googleapis.com https://generativelanguage.googleapis.com https://api.anthropic.com"
    index = ('<meta http-equiv="Content-Security-Policy" content="' + connect + '">').encode()
    asset = {"version": "synthetic", "assets": [{"url": "index.html", "hash": "sha256-" + base64.b64encode(hashlib.sha256(index).digest()).decode()}]}
    assets = ("self.assetsManifest = " + json.dumps(asset) + ";").encode()
    response_headers = headers(Content_Security_Policy=policy or connect + "; frame-ancestors 'none'")
    if extra_header:
        response_headers["Content-Security-Policy"] = extra_header
    expected = {"indexSha256": hashlib.sha256(index).hexdigest(), "assetsSha256": hashlib.sha256(assets).hexdigest()}
    calls = []

    def fetch(url, limit):
        calls.append((url, limit))
        return (index, response_headers) if url.endswith("/") else (assets, headers())

    return expected, fetch, calls


class ReadinessTests(unittest.TestCase):
    def test_frozen_public_worksheet_has_no_secret_fields(self):
        self.assertEqual(R.validate(worksheet()), worksheet())
        for nested in (None, "ai", "drive"):
            value = worksheet()
            (value if nested is None else value[nested])["apiKey"] = "synthetic-secret"
            with self.subTest(nested=nested), self.assertRaises(R.Refusal):
                R.validate(value)

    def test_empty_inputs_are_blockers_without_invented_defaults(self):
        value = worksheet()
        value["deploymentOrigin"] = ""
        for key in value["drive"]:
            value["drive"][key] = ""
        for provider in value["ai"].values():
            for key in provider:
                provider[key] = ""
        self.assertEqual(len(R.configured(R.validate(value))), 5)
        self.assertTrue(all(row["status"] == "blocked" for row in R.configured(value)))

    def test_exact_origin_refuses_credentials_paths_query_fragment_and_noncanonical_values(self):
        for origin in ("http://editor.example.invalid", "https://user:password@editor.example.invalid", "https://editor.example.invalid/", "https://editor.example.invalid?", "https://editor.example.invalid#", "https://editor.example.invalid:443", "https://EDITOR.example.invalid", "https://editor.example.invalid:", "https://editor.example.invalid\n"):
            with self.subTest(origin=origin), self.assertRaises(R.Refusal):
                R.canonical_origin(origin)

    def test_local_origin_uses_only_accepted_loopback_or_private_ipv4(self):
        for origin in ("http://localhost:11434", "http://127.0.0.1:11434", "http://[::1]:11434", "https://10.0.0.2:11434", "http://192.168.1.2:11434", "http://172.20.1.2:11434"):
            self.assertEqual(R.canonical_origin(origin, local=True), origin)
        for origin in ("http://model.lan:11434", "http://8.8.8.8:11434", "http://169.254.1.2:11434", "http://172.32.1.2:11434", "http://[fd00::1]:11434"):
            with self.subTest(origin=origin), self.assertRaises(R.Refusal):
                R.canonical_origin(origin, local=True)

    def test_invalid_public_identifier_suffixes_never_look_configured(self):
        for field in ("clientId", "pickerApiKey", "projectNumber"):
            value = worksheet()
            value["drive"][field] += "\n"
            with self.subTest(field=field), self.assertRaises(R.Refusal):
                R.validate(value)

    def test_strict_json_rejects_duplicate_escaped_keys_invalid_utf8_and_constants(self):
        for data in (b'{"id":1,"id":2}', b'{"id":1,"i\\u0064":2}', b'{"id":"\xff"}', b'{"id":NaN}', b'{"id":Infinity}'):
            with self.subTest(data=data), self.assertRaises(R.Refusal):
                R.strict_json(data)

    def test_model_url_and_gemini_resource_paths_refuse(self):
        for provider, model in (("gemini", "models/model"), ("gemini", "model:generate"), ("claude", "https://other.invalid"), ("ollama", "/model")):
            value = worksheet()
            value["ai"][provider]["modelId"] = model
            with self.subTest(provider=provider, model=model), self.assertRaises(R.Refusal):
                R.validate(value)

    def test_root_publication_pin_and_sdk_index_integrity_are_required(self):
        expected, fetch, calls = publication()
        rows, actual = R.deployment(worksheet(), expected, fetch)
        self.assertTrue(all(row["status"] == "pass" for row in rows))
        self.assertEqual(actual, expected)
        self.assertEqual(len(calls), 2)
        drift = copy.copy(expected)
        drift["indexSha256"] = "0" * 64
        with self.assertRaises(R.Refusal):
            R.deployment(worksheet(), drift, fetch)

    def test_effective_csp_intersects_every_header_and_meta_policy(self):
        expected, fetch, _ = publication(extra_header="default-src 'self'; frame-ancestors 'none'")
        rows, _ = R.deployment(worksheet(), expected, fetch)
        self.assertTrue(all(row["status"] == "blocked" for row in rows if "connect-policy" in row["id"]))

    def test_repeated_csp_directive_uses_first_occurrence(self):
        policy = R.directives("default-src 'none'; connect-src 'self'; connect-src https://api.anthropic.com")
        self.assertFalse(R.permits(policy, "connect-src", "https://api.anthropic.com/v1/messages", "https://editor.example.invalid"))

    def test_meta_frame_ancestors_cannot_replace_required_response_header(self):
        expected, fetch, _ = publication(policy="connect-src https://api.anthropic.com")
        rows, _ = R.deployment(worksheet(), expected, fetch)
        self.assertEqual(next(row["status"] for row in rows if row["id"] == "header-denies-embedding"), "blocked")

    def test_conservative_connect_check_refuses_wildcards_and_nonmatching_path(self):
        for source in ("https:", "*", "https://*.anthropic.com", "https://api.anthropic.com/other/"):
            with self.subTest(source=source):
                self.assertFalse(R.permits({"connect-src": [source]}, "connect-src", "https://api.anthropic.com/v1/messages", "https://editor.example.invalid"))
        self.assertFalse(R.permits({"connect-src": ["https:", "https://api.anthropic.com"]}, "connect-src", "https://api.anthropic.com/v1/messages", "https://editor.example.invalid"))

    def test_default_has_no_network_no_identifier_echo_and_no_live_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "operator.json"
            config.write_text(json.dumps(worksheet()), encoding="utf-8")
            output = io.StringIO()
            with patch.object(R, "build_opener", side_effect=AssertionError("network forbidden")) as network, redirect_stdout(output):
                self.assertEqual(R.main(["--configuration", str(config)]), 0)
            network.assert_not_called()
            report = json.loads(output.getvalue())
            self.assertFalse(report["milestoneComplete"])
            self.assertTrue(all(row["status"] == "not-run" for row in report["checks"] if row["id"].endswith(("-Chrome", "-Edge"))))
            for value in (worksheet()["deploymentOrigin"], *worksheet()["drive"].values()):
                self.assertNotIn(value, output.getvalue())

    def test_probe_without_root_publication_pin_refuses_before_network(self):
        output = io.StringIO()
        with patch.object(R, "build_opener", side_effect=AssertionError("network forbidden")) as network, redirect_stdout(output):
            self.assertEqual(R.main(["--configuration", "/unread", "--probe-deployment"]), 2)
        network.assert_not_called()
        self.assertEqual(json.loads(output.getvalue())["code"], "invalid-input")

    def test_ollama_probe_never_generates_or_selects_another_model(self):
        calls = []

        def fetch(url, limit, **kwargs):
            calls.append((url, kwargs))
            if kwargs.get("method") == "OPTIONS":
                self.assertEqual(kwargs["headers"]["Origin"], worksheet()["deploymentOrigin"])
                return b"", headers(Access_Control_Allow_Origin=worksheet()["deploymentOrigin"], Access_Control_Allow_Methods="GET, POST", Access_Control_Allow_Headers="Content-Type")
            return b'{"models":[{"model":"another-model"}]}', headers()

        rows = R.ollama(worksheet(), fetch)
        self.assertEqual(rows[0]["status"], "pass")
        self.assertEqual(rows[1]["status"], "blocked")
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0][1]["method"], "OPTIONS")
        self.assertTrue(calls[1][0].endswith("/api/tags"))
        self.assertNotIn("data", calls[0][1])


if __name__ == "__main__":
    unittest.main()
