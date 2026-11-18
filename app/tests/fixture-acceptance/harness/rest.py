"""REST client for the CrawlForge loopback backend (normal entry point).

Discovery is the same as the shell's: %APPDATA%/CrawlForge/loopback-handshake.json
{port, sessionToken}; every /api/* call except /api/health carries
X-CrawlForge-Session. Envelope shape {data, pagination, error}.
"""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
from pathlib import Path


class RestError(RuntimeError):
    def __init__(self, method: str, path: str, status: int, body):
        self.status = status
        self.body = body
        super().__init__(f"{method} {path} -> HTTP {status}: {json.dumps(body)[:400]}")


def handshake_path() -> Path:
    return Path(os.environ["APPDATA"]) / "CrawlForge" / "loopback-handshake.json"


def read_handshake() -> dict | None:
    p = handshake_path()
    if not p.exists():
        return None
    try:
        return json.loads(p.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError):
        return None


class RestClient:
    def __init__(self, port: int, token: str):
        self.base = f"http://127.0.0.1:{port}"
        self.token = token

    def request(self, method: str, path: str, body=None, timeout: float = 60.0):
        url = self.base + path
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Accept", "application/json")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        if path != "/api/health":
            req.add_header("X-CrawlForge-Session", self.token)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8")
                status = resp.status
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", errors="replace")
            status = e.code
        try:
            parsed = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            parsed = raw
        return status, parsed

    def ok(self, method: str, path: str, body=None, timeout: float = 60.0):
        status, parsed = self.request(method, path, body, timeout)
        if status >= 400:
            raise RestError(method, path, status, parsed)
        return parsed

    def data(self, method: str, path: str, body=None, timeout: float = 60.0):
        parsed = self.ok(method, path, body, timeout)
        if isinstance(parsed, dict) and "data" in parsed:
            return parsed["data"]
        return parsed

    def paged(self, path: str, limit: int = 200, cap: int = 100000, params: dict | None = None) -> list:
        rows: list = []
        offset = 0
        extra = ""
        if params:
            from urllib.parse import urlencode
            extra = "&" + urlencode(params)
        while True:
            sep = "&" if "?" in path else "?"
            parsed = self.ok("GET", f"{path}{sep}offset={offset}&limit={limit}{extra}")
            page = parsed.get("data") if isinstance(parsed, dict) else parsed
            if not isinstance(page, list):
                break
            rows.extend(page)
            total = (parsed.get("pagination") or {}).get("total") if isinstance(parsed, dict) else None
            offset += limit
            if not page or (total is not None and offset >= total) or len(rows) >= cap:
                break
        return rows


def wait_backend(timeout_s: float = 180.0, poll_s: float = 2.0) -> RestClient:
    """Wait for a fresh handshake descriptor and a healthy backend."""
    deadline = time.monotonic() + timeout_s
    last_err = "handshake file never appeared"
    while time.monotonic() < deadline:
        hs = read_handshake()
        if hs and hs.get("port") and hs.get("sessionToken"):
            client = RestClient(int(hs["port"]), hs["sessionToken"])
            try:
                status, body = client.request("GET", "/api/health", timeout=5)
                if status == 200:
                    return client
                last_err = f"health HTTP {status}"
            except (urllib.error.URLError, OSError, TimeoutError) as e:
                last_err = str(e)
        time.sleep(poll_s)
    raise TimeoutError(f"backend not ready after {timeout_s}s: {last_err}")