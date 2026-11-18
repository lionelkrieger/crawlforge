"""Python driver for node/cdp.mjs --serve (raw CDP, no dependencies).

One persistent Node child per session; JSON-lines request/response over
stdin/stdout with a reader thread. Events (console, exceptions, network) are
buffered in the child and polled with events().
"""

from __future__ import annotations

import json
import queue
import subprocess
import threading
import time
from pathlib import Path

HARNESS_DIR = Path(__file__).resolve().parents[1]
CDP_SCRIPT = HARNESS_DIR / "node" / "cdp.mjs"


class CdpError(RuntimeError):
    pass


class CdpSession:
    def __init__(self, port: int, target_hint: str = "index.html", node: str = "node"):
        self.port = port
        self.proc = subprocess.Popen(
            [node, str(CDP_SCRIPT), "--port", str(port), "--serve", "--target", target_hint],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
        )
        self._responses: queue.Queue = queue.Queue()
        self._next_id = 1
        self._lock = threading.Lock()
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()
        ready = self._responses.get(timeout=30)
        if not ready.get("ok"):
            raise CdpError(f"CDP session not ready: {ready}")
        self.page_url = ready["result"].get("pageUrl")
        self.page_id = ready["result"].get("pageId")

    def _read_loop(self):
        assert self.proc.stdout is not None
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                self._responses.put(json.loads(line))
            except json.JSONDecodeError:
                self._responses.put({"id": None, "ok": False, "error": f"non-JSON line: {line[:200]}"})
        self._responses.put({"id": None, "ok": False, "error": "CDP child stdout closed"})

    def _stderr_tail(self) -> str:
        try:
            if self.proc.stderr is not None and self.proc.poll() is not None:
                return (self.proc.stderr.read() or "")[-800:]
        except (OSError, ValueError):
            pass
        return ""

    def call(self, cmd: str, timeout: float = 60.0, **args):
        if self.proc.poll() is not None:
            raise CdpError(f"CDP child exited (code {self.proc.returncode}): {self._stderr_tail()}")
        with self._lock:
            req_id = self._next_id
            self._next_id += 1
        assert self.proc.stdin is not None
        self.proc.stdin.write(json.dumps({"id": req_id, "cmd": cmd, **args}) + "\n")
        self.proc.stdin.flush()
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise CdpError(f"CDP {cmd} timed out after {timeout}s")
            try:
                resp = self._responses.get(timeout=min(remaining, 5.0))
            except queue.Empty:
                if self.proc.poll() is not None:
                    raise CdpError(f"CDP child died during {cmd}: {self._stderr_tail()}")
                continue
            if resp.get("id") == req_id:
                if not resp.get("ok"):
                    raise CdpError(f"CDP {cmd} failed: {resp.get('error')}")
                return resp.get("result")
            # stray response (e.g. late timeout) — keep waiting for ours

    # -- convenience --------------------------------------------------------

    def ping(self):
        return self.call("ping")

    def dump(self) -> dict:
        return self.call("dump")

    def eval(self, expression: str):
        return self.call("eval", expression=expression)

    def shot(self, path: Path | str) -> str:
        p = Path(path)
        p.parent.mkdir(parents=True, exist_ok=True)
        self.call("shot", timeout=90.0, path=str(p))
        return str(p)

    def _retry_action(self, cmd: str, args: dict, ok_key: str, timeout: float) -> dict:
        """Assert success; retry with short backoff until timeout; raise
        CdpError on persistent failure. A missed click/fill (e.g. a Mantine
        modal animation moving the target) is NEVER silently swallowed
        (lead send-back 2026-11-18, UI-entry determinism finding)."""
        deadline = time.monotonic() + timeout
        last = None
        attempt = 0
        while True:
            attempt += 1
            last = self.call(cmd, **args)
            if isinstance(last, dict) and last.get(ok_key):
                if attempt > 1:
                    last = {**last, "attempts": attempt}
                return last
            if time.monotonic() >= deadline:
                raise CdpError(f"CDP {cmd} did not succeed within {timeout}s "
                               f"(attempts={attempt}): {json.dumps(last)[:400]}")
            time.sleep(0.4)

    def click(self, *, testid: str | None = None, text: str | None = None,
              selector: str | None = None, placeholder: str | None = None,
              double: bool = False, timeout: float = 10.0) -> dict:
        args: dict = {"double": double}
        if testid is not None:
            args["testid"] = testid
        elif text is not None:
            args["text"] = text
        elif selector is not None:
            args["selector"] = selector
        elif placeholder is not None:
            args["placeholder"] = placeholder
        else:
            raise ValueError("click needs testid/text/selector/placeholder")
        return self._retry_action("click", args, "clicked", timeout)

    def fill(self, value: str, *, testid: str | None = None, selector: str | None = None,
             placeholder: str | None = None, timeout: float = 10.0) -> dict:
        args: dict = {"value": value}
        if testid is not None:
            args["testid"] = testid
        elif selector is not None:
            args["selector"] = selector
        elif placeholder is not None:
            args["placeholder"] = placeholder
        else:
            raise ValueError("fill needs testid/selector/placeholder")
        return self._retry_action("fill", args, "ok", timeout)

    def key(self, name: str, modifiers: int = 0):
        return self.call("key", key=name, modifiers=modifiers)

    def ctrl_k(self):
        return self.key("k", modifiers=2)

    def type_text(self, text: str):
        return self.call("type", text=text)

    def wait(self, ms: int):
        return self.call("wait", timeout=max(60.0, ms / 1000 + 10), ms=ms)

    def wait_for(self, *, testid: str | None = None, selector: str | None = None,
                 timeout_ms: int = 15000) -> dict:
        args: dict = {"timeoutMs": timeout_ms}
        if testid is not None:
            args["testid"] = testid
        else:
            args["selector"] = selector
        return self.call("waitFor", timeout=timeout_ms / 1000 + 15, **args)

    def must_wait_for(self, *, testid: str | None = None, selector: str | None = None,
                      timeout_ms: int = 15000) -> dict:
        r = self.wait_for(testid=testid, selector=selector, timeout_ms=timeout_ms)
        if not r.get("found"):
            raise CdpError(f"wait_for timed out after {timeout_ms}ms: testid={testid} selector={selector}")
        return r

    def count(self, selector: str) -> int:
        return self.call("count", selector=selector)

    def events(self, clear: bool = False) -> dict:
        return self.call("events", clear=clear)

    def targets(self) -> list:
        return self.call("targets")

    def navigate(self, url: str):
        return self.call("navigate", url=url)

    def reload(self):
        return self.call("reload")

    def close_browser(self):
        return self.call("closeBrowser", timeout=30.0)

    def close(self):
        """Drop the CDP socket; the app keeps running."""
        try:
            if self.proc.stdin is not None:
                self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()