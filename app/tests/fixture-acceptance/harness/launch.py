"""Product launch + lifecycle through the NORMAL entry point.

Runs launch-debug.ps1 (a sanctioned copy of app/launch-crawlforge.ps1 with
--remote-debugging-port appended) with CF_AGENT_ID UNSET: the launcher hardcodes
canonical bin\\Debug paths and IS the product's normal entry point. The harness
runs no direct dotnet commands otherwise. Shutdown prefers CDP Browser.close
(graceful before-quit drain), falling back to killing ONLY process trees it can
identify as its own launched instance.
"""

from __future__ import annotations

import json
import os
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

from .rest import wait_backend

APP_ROOT = Path("E:/Crawlforge/app")
HARNESS_DIR = Path(__file__).resolve().parents[1]
CDP_PORTS = (9222, 9223)


class LaunchError(RuntimeError):
    pass


def check_sdk() -> str:
    r = subprocess.run(["dotnet", "--version"], cwd=str(APP_ROOT), capture_output=True, text=True, timeout=120)
    version = (r.stdout or "").strip()
    if r.returncode != 0 or not version.startswith("10.0."):
        raise LaunchError(f"pinned .NET 10 SDK not resolved inside {APP_ROOT}: {version!r} {r.stderr.strip()!r}")
    return version


def _cdp_responds(port: int) -> bool:
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/version", timeout=2) as resp:
            json.loads(resp.read().decode("utf-8"))
            return True
    except (urllib.error.URLError, OSError, TimeoutError, json.JSONDecodeError, ValueError):
        return False


def _netstat_listening(port: int) -> list[int]:
    """PIDs holding a LISTENING socket on the port (kernel-level truth).
    A connect() probe alone is NOT sufficient: a ghost socket can refuse
    connects yet still make Electron's bind() fail (measured 2026-11-18:
    'Cannot start http server for devtools', tcp_socket_win 0x2740)."""
    try:
        r = subprocess.run(["netstat", "-ano", "-p", "TCP"], capture_output=True, text=True, timeout=30)
        pids: list[int] = []
        for line in (r.stdout or "").splitlines():
            parts = line.split()
            if len(parts) >= 5 and parts[0].upper() == "TCP" and parts[3].upper() == "LISTENING":
                local = parts[1]
                if local.rsplit(":", 1)[-1] == str(port):
                    try:
                        pids.append(int(parts[4]))
                    except ValueError:
                        pass
        return pids
    except (OSError, subprocess.TimeoutExpired):
        return []


def pick_cdp_port() -> tuple[int, list[str]]:
    """Prefer 9222, then 9223. A port with ANY LISTENING socket (even a ghost
    with no live process) is skipped — never killed, never bound over."""
    notes: list[str] = []
    for port in CDP_PORTS:
        holders = _netstat_listening(port)
        if not holders and not _port_listening(port):
            return port, notes
        if _cdp_responds(port):
            notes.append(f"port {port} occupied by a live CDP endpoint (pids={holders}); skipped")
        else:
            notes.append(f"port {port} held by an unresponsive ghost listener (netstat pids={holders}, no live process); skipped — binding over it fails (devtools 0x2740)")
    raise LaunchError("both CDP ports 9222 and 9223 are occupied; cannot launch")


def _port_listening(port: int) -> bool:
    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(1.0)
        return s.connect_ex(("127.0.0.1", port)) == 0


def cdp_targets(port: int) -> list[dict]:
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=5) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except (urllib.error.URLError, OSError, TimeoutError, json.JSONDecodeError, ValueError):
        return []


def launch_app(results_dir: Path, cdp_port: int, timeout_s: float = 1500.0) -> dict:
    """Run the debug launcher copy; return launch metadata."""
    env = dict(os.environ)
    env.pop("CF_AGENT_ID", None)  # sanctioned: launcher uses canonical build paths
    stdout_log = results_dir / "electron-stdout.log"
    stderr_log = results_dir / "electron-stderr.log"
    console_log = results_dir / "launch-console.log"
    args = [
        "pwsh", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", str(HARNESS_DIR / "launch-debug.ps1"),
        "-CdpPort", str(cdp_port),
        "-StdOutLog", str(stdout_log),
        "-StdErrLog", str(stderr_log),
    ]
    with open(console_log, "wb") as log:
        r = subprocess.run(args, cwd=str(APP_ROOT), env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout_s)
    if r.returncode != 0:
        raise LaunchError(f"launch-debug.ps1 exited {r.returncode}; see {console_log}")
    return {
        "cdp_port": cdp_port,
        "console_log": str(console_log),
        "electron_stdout_log": str(stdout_log),
        "electron_stderr_log": str(stderr_log),
        "cf_agent_id": "unset (sanctioned normal-entry-point launch)",
    }


def wait_app_ready(cdp_port: int, timeout_s: float = 240.0, stderr_log: Path | None = None):
    """Wait for the renderer page target, then for a healthy backend.
    Fails FAST if Electron's devtools HTTP server could not bind the port."""
    deadline = time.monotonic() + timeout_s
    page = None
    while time.monotonic() < deadline:
        if stderr_log is not None and stderr_log.exists():
            try:
                tail = stderr_log.read_text(encoding="utf-8", errors="replace")[-4000:]
                if "Cannot start http server for devtools" in tail:
                    raise LaunchError(
                        f"Electron could not bind CDP port {cdp_port} (devtools http server failed; "
                        f"see {stderr_log}) — the port is held by another socket")
            except OSError:
                pass
        for t in cdp_targets(cdp_port):
            if t.get("type") == "page" and "index.html" in (t.get("url") or ""):
                page = t
                break
        if page:
            break
        time.sleep(2)
    if page is None:
        raise LaunchError(f"no renderer page target on CDP {cdp_port} after {timeout_s}s")
    client = wait_backend(timeout_s=max(30.0, deadline - time.monotonic()))
    return client, page


def launch_and_wait(results_dir: Path, ports: list[int], launch_timeout_s: float = 1500.0):
    """Cold-start with port fallback: if the devtools bind fails on one port,
    shut down ONLY the tree we launched and retry on the next candidate port."""
    notes: list[str] = []
    last_err: Exception | None = None
    for i, port in enumerate(ports):
        stderr_log = results_dir / "electron-stderr.log"
        if i > 0 and stderr_log.exists():
            stderr_log.rename(results_dir / f"electron-stderr-attempt{i}.log")
        launch_app(results_dir, port, timeout_s=launch_timeout_s)
        try:
            client, page = wait_app_ready(port, stderr_log=stderr_log)
            return client, page, port, notes
        except LaunchError as e:
            last_err = e
            notes.append(f"launch attempt on CDP {port} failed: {e}")
            report = shutdown_app(None, port, launched=True)
            notes.append(f"shut down failed attempt: {json.dumps(report)}")
    raise LaunchError(f"all CDP launch attempts failed: {notes}") from last_err


def _process_snapshot() -> list[dict]:
    r = subprocess.run(
        ["pwsh", "-NoLogo", "-NoProfile", "-Command",
         "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine | ConvertTo-Json -Depth 3"],
        capture_output=True, text=True, timeout=120,
    )
    if r.returncode != 0:
        return []
    try:
        data = json.loads(r.stdout)
    except json.JSONDecodeError:
        return []
    if isinstance(data, dict):
        data = [data]
    return data or []


def find_app_pids(cdp_port: int) -> dict:
    """Identify OUR Electron instance (by the CDP port argument we passed) and
    its backend child. Never matches cf-wp-08-03's headless backend or the
    owner's other work."""
    procs = _process_snapshot()
    needle = f"--remote-debugging-port={cdp_port}"
    electrons = [p for p in procs if (p.get("Name") or "").lower() == "electron.exe" and needle in (p.get("CommandLine") or "")]
    electron_ids = {p["ProcessId"] for p in electrons}
    backends = [
        p for p in procs
        if "CrawlForge.Host.dll" in (p.get("CommandLine") or "")
        and (p.get("ParentProcessId") in electron_ids or _ancestor_in(p, electron_ids, procs))
    ]
    return {"electron": electrons, "backend": backends}


def _ancestor_in(proc: dict, ids: set, procs: list[dict], depth: int = 6) -> bool:
    by_id = {p["ProcessId"]: p for p in procs}
    cur = proc
    for _ in range(depth):
        parent = by_id.get(cur.get("ParentProcessId"))
        if parent is None:
            return False
        if parent["ProcessId"] in ids:
            return True
        cur = parent
    return False


def _pid_alive(pid: int) -> bool:
    r = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True, timeout=30)
    return str(pid) in (r.stdout or "")


def shutdown_app(cdp, cdp_port: int, launched: bool, timeout_s: float = 45.0) -> dict:
    """Graceful CDP Browser.close first; kill only our identified tree as fallback."""
    report: dict = {"method": None, "electron_pids": [], "backend_pids": [], "residual": []}
    if not launched:
        report["method"] = "not-launched-by-harness (left running)"
        return report
    pids = find_app_pids(cdp_port)
    report["electron_pids"] = [p["ProcessId"] for p in pids["electron"]]
    report["backend_pids"] = [p["ProcessId"] for p in pids["backend"]]
    try:
        if cdp is not None:
            cdp.close_browser()
            report["method"] = "cdp Browser.close"
    except Exception as e:  # noqa: BLE001 - record and fall back
        report["cdp_close_error"] = str(e)
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if not any(_pid_alive(pid) for pid in report["electron_pids"]):
            break
        time.sleep(1)
    alive = [pid for pid in report["electron_pids"] if _pid_alive(pid)]
    if alive:
        report["method"] = (report["method"] or "") + " + taskkill fallback"
        for pid in alive:
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, text=True, timeout=60)
    # Backend children should drain via before-quit; kill orphans of OUR tree only.
    time.sleep(2)
    for pid in report["backend_pids"]:
        if _pid_alive(pid):
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, text=True, timeout=60)
            report["residual"].append(pid)
    report["ports_free"] = not _port_listening(cdp_port)
    return report