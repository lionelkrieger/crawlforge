# Loopback Handshake — Supervisor Discovery Contract

WP-00-04 (`CrawlForge.Security.Handshake.LoopbackPortHandshake`, implementing
`CrawlForge.Contracts.Security.ILoopbackPortHandshake`).

The backend binds **loopback only** (FS-CRAWL-001 §2.2). The Electron supervisor
discovers the actual port and the per-launch session token by reading a local
handshake file — no network discovery, no remote service, no identity material.

## File location

Default: `loopback-handshake.json` inside the per-user CrawlForge data directory:

| Platform | Path |
|---|---|
| Windows | `%APPDATA%\CrawlForge\loopback-handshake.json` |
| macOS | `~/Library/Application Support/CrawlForge/loopback-handshake.json` |
| Linux | `~/.config/CrawlForge/loopback-handshake.json` |

The path is configurable via the `LoopbackPortHandshake` constructor; the
`LoopbackHandshake.HandshakeFilePath` returned by `StartAsync` is authoritative.

## File schema (version 1, UTF-8 JSON, camelCase)

```json
{
  "version": 1,
  "port": 54321,
  "sessionToken": "<base64url, 32 random bytes>",
  "createdAtUtc": "2026-11-14T06:58:26.1234567+00:00"
}
```

- `port` — loopback port the backend listens on. Preferred port first; when
  occupied, an OS-assigned ephemeral port (spec §2.2 port strategy).
- `sessionToken` — per-launch random credential, generated locally from
  app entropy (`RandomNumberGenerator`). Never hardware/OS-derived, never
  transmitted off-machine, no remote issuance/rotation/revocation.
- `createdAtUtc` — publish timestamp (UTC).
- **No other fields exist.** No machine, user, or account dimensions
  (privacy invariant).

## Lifecycle

1. Backend `StartAsync` atomically publishes the file (temp write + move), so
   the supervisor never reads a partial descriptor.
2. Supervisor polls/reads the file, connects to `http://127.0.0.1:{port}`,
   and presents `sessionToken` (header convention is defined by the later
   Host/Electron integration phase; validation is
   `ILoopbackPortHandshake.ValidateSessionToken`, constant-time).
3. Backend `StopAsync` deletes the file and invalidates the token. A stale
   file from a crashed launch is superseded by the next `StartAsync`
   (overwrite) — the supervisor should treat `createdAtUtc` freshness and a
   successful health probe as readiness confirmation (spec §2.3 step 4).

The probe socket is released before publishing; the backend host binds the
published port immediately after `StartAsync` (Host wiring is a later phase).