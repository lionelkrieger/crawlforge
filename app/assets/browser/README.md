# Bundled browser asset (pinned Chrome for Testing)

FS-CRAWL-001 §5.6.3 · D-003 / CR-007 · WP-10-01

This directory is the packaged-assets root for the **pinned Chrome for Testing**
rendering browser. Resolution order (§5.6.3):

1. **Bundled browser** — this directory (`browser-pin.json` + the platform bundle).
2. **User-configured executable path** (Settings).
3. **Locally installed Chrome**, then **Edge**.
4. None found → rendering-dependent capture modes fail at **pre-crawl
   validation** with a clear local error; non-JS capture remains available.

**Runtime download of any browser or driver is prohibited** (offline-first).
The resolver (`CrawlForge.Crawler.Rendering.LocalBrowserResolver`) probes the
local filesystem only.

## Current state

- `browser-pin.json` records the pin: **Chrome for Testing 131.0.6778.85**
  (stable), with per-platform bundle layouts and optional SHA-256 slots.
- The browser **binary is not yet physically bundled** — that is owner
  packaging item **O-006**. Until it ships, resolution falls through to the
  user path / local Chrome / Edge chain. Component tests exercise the resolver
  against fake bundle layouts.

## Bundle layout (O-006)

Place the official Chrome for Testing archives' extracted trees here:

| Platform key | Executable (relative to this directory)                    |
|--------------|-------------------------------------------------------------|
| `win64`      | `chrome-win64/chrome.exe`                                   |
| `linux64`    | `chrome-linux64/chrome`                                     |
| `mac-x64`    | `chrome-mac-x64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing` |
| `mac-arm64`  | `chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing` |

Update `browser-pin.json` (`version`, `pinnedAtUtc`, `sha256`) with each product
release; the pinned version is recorded in the release notes and shown in the
UI-21 browser section. Redistribution terms are confirmed by the licence audit
(`architecture/09-distribution`) before public release.
