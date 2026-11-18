"""Fixture-site lifecycle + recorder assertions (read-only fixture code).

Starts app/tests/fixture-site/server.mjs on 127.0.0.1:47311 when the port is
free; verifies identity before reusing an existing listener. GET /__fixture/reset
before EACH audit; GET /__fixture/requests after EACH audit; the global
assertion no-non-get-requests (criterion #54) requires nonGet == [].
"""

from __future__ import annotations

import json
import socket
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

FIXTURE_DIR = Path(__file__).resolve().parents[2] / "fixture-site"
DEFAULT_PORT = 47311


def port_listening(port: int, host: str = "127.0.0.1") -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(1.0)
        return s.connect_ex((host, port)) == 0


def http_get(url: str, timeout: float = 5.0):
    with urllib.request.urlopen(url, timeout=timeout) as resp:
        return resp.status, resp.read().decode("utf-8", errors="replace")


class FixtureServer:
    def __init__(self, port: int = DEFAULT_PORT, tls_cert: str | None = None, tls_key: str | None = None):
        self.port = port
        self.tls_cert = tls_cert
        self.tls_key = tls_key
        self.scheme = "https" if (tls_cert and tls_key) else "http"
        self.base_url = f"{self.scheme}://127.0.0.1:{port}"
        self.proc: subprocess.Popen | None = None
        self.started_by_us = False
        self.reused = False
        self.log_path: Path | None = None

    # -- lifecycle ----------------------------------------------------------

    def ensure_running(self, log_path: Path, ready_timeout_s: float = 30.0) -> None:
        if port_listening(self.port):
            # Identity check: must be the CrawlForge fixture, not some other service.
            try:
                status, home = http_get(f"http://127.0.0.1:{self.port}/")
                status2, rec = http_get(f"http://127.0.0.1:{self.port}/__fixture/requests")
                json.loads(rec)
                if status == 200 and "CrawlForge fixture site" in home and status2 == 200:
                    self.reused = True
                    return
            except (urllib.error.URLError, OSError, json.JSONDecodeError, TimeoutError):
                pass
            raise RuntimeError(
                f"port {self.port} is occupied by a service that is not the CrawlForge fixture; "
                "refusing to proceed (stop the stale fixture or choose another port)"
            )
        args = ["node", str(FIXTURE_DIR / "server.mjs"), "--port", str(self.port)]
        if self.tls_cert and self.tls_key:
            args += ["--tls-cert", self.tls_cert, "--tls-key", self.tls_key]
        log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path = log_path
        with open(log_path, "ab") as log:
            self.proc = subprocess.Popen(
                args, cwd=str(FIXTURE_DIR), stdout=log, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
            )
        self.started_by_us = True
        deadline = time.monotonic() + ready_timeout_s
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                raise RuntimeError(f"fixture server exited early (code {self.proc.returncode}); see {log_path}")
            try:
                status, _ = http_get(f"http://127.0.0.1:{self.port}/__fixture/requests", timeout=2)
                if status == 200:
                    return
            except (urllib.error.URLError, OSError, TimeoutError):
                time.sleep(0.3)
        raise TimeoutError(f"fixture server not ready after {ready_timeout_s}s")

    def stop(self) -> None:
        if self.proc is not None and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=10)
        self.proc = None

    # -- recorder -----------------------------------------------------------

    def reset(self) -> dict:
        status, body = http_get(f"http://127.0.0.1:{self.port}/__fixture/reset")
        if status != 200:
            raise RuntimeError(f"fixture reset failed: HTTP {status}")
        return json.loads(body)

    def requests(self) -> dict:
        status, body = http_get(f"http://127.0.0.1:{self.port}/__fixture/requests")
        if status != 200:
            raise RuntimeError(f"fixture requests failed: HTTP {status}")
        return json.loads(body)


def assert_no_non_get(requests_log: dict) -> tuple[bool, str]:
    """Global assertion no-non-get-requests (expected-results.json, criterion #54).

    Returns (ok, evidence). A non-empty nonGet list means the crawler triggered
    a control it must never trigger (Place bet / Deposit / Logout POSTs).
    """
    non_get = requests_log.get("nonGet")
    if non_get is None:
        return False, "recorder response has no nonGet field"
    if not isinstance(non_get, list):
        return False, f"nonGet is not a list: {type(non_get).__name__}"
    if len(non_get) == 0:
        return True, "nonGet == [] after audit"
    return False, f"nonGet has {len(non_get)} entries: {json.dumps(non_get)[:600]}"


def inspect_tag_hits(requests_log: dict) -> dict:
    """Consent-banner evidence: tag hits and whether any fired before consent."""
    hits = requests_log.get("tagHits") or []
    before_consent = [h for h in hits if "consent=none" in (h.get("query") or "")]
    granted = [h for h in hits if "consent=granted" in (h.get("query") or "")]
    return {
        "total": len(hits),
        "beforeConsent": len(before_consent),
        "granted": len(granted),
        "hits": hits[:50],
    }