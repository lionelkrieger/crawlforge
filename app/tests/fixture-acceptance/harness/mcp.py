"""MCP entry point driver: spawns node/mcp_client.mjs (stdio adapter + SDK)."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

HARNESS_DIR = Path(__file__).resolve().parents[1]
MCP_SCRIPT = HARNESS_DIR / "node" / "mcp_client.mjs"


class McpError(RuntimeError):
    pass


def mcp_call(args: list[str], timeout_s: float = 600.0) -> dict:
    r = subprocess.run(["node", str(MCP_SCRIPT), *args], capture_output=True, text=True,
                       encoding="utf-8", timeout=timeout_s)
    out = (r.stdout or "").strip()
    if r.returncode != 0:
        raise McpError(f"mcp_client.mjs {' '.join(args[:1])} exited {r.returncode}: "
                       f"{(r.stderr or out)[-800:]}")
    try:
        return json.loads(out)
    except json.JSONDecodeError as e:
        raise McpError(f"mcp_client.mjs produced non-JSON output: {out[:400]}") from e