# Packaging and Release

> **CrawlForge architecture v2.0 — area of concern: `09-distribution`.**
> New domain added by change record CR-014 (decisions D-009, D-010, D-011). Not derived from the baseline documents. Release-time requirements: specified now, built when CrawlForge is first made available to others.

| Source | Version | Sections |
|---|---|---|
| CR-014 (`changes/CR-014.md`) | 2026-11-17 | complete file |

---

### 1. Installer
> Decisions: D-009 · Change records: CR-014

- A per-user Windows installer installs the shell, backend, bundled assets (including the ONNX model) and the pinned rendering browser (§5.6.3). It uninstalls cleanly and keeps user data unless the user chooses to remove it.

### 2. Code signing
> Decisions: D-009, D-010 · Change records: CR-014

- Release binaries and the installer are code-signed so operating-system reputation checks do not warn users. A free signing programme for open-source projects may be used.

### 3. Release channel
> Decisions: D-010 · Change records: CR-014

- Releases are published with release notes on **GitHub Releases**. The user-initiated release check (§2.6) compares the installed version with the latest release. Installation is manual or user-confirmed; there is no automatic checking.

### 4. Crash reporting
> Decisions: D-009 · Change records: CR-014

- Crash reporting stays local (minidumps; §3 row 6). Opt-in crash submission is not in scope; it may be revisited if other users ask for it, which would require amending §3 row 6.

### 5. Licence audit
> Decisions: D-010, D-011 · Change records: CR-014

- Before every public release, a **required release-checklist step** checks every bundled dependency and data asset (dictionaries, ONNX model, technology fingerprints, WAF signatures, bot IP lists, AI agent profiles, font metrics, stop-word lists, the pinned browser) for redistribution terms and compatibility with AGPL-3.0, automated where tooling allows. Incompatible items are replaced or removed before release.

### 6. Platforms
> Decisions: D-009 · Change records: CR-014

- Windows is the release platform. macOS support is deferred. Nothing is hard-coded to Windows where a portable choice exists.

### 7. Large assets
> Decisions: D-009 · Change records: CR-014

- Large bundled assets, including the 87 MB ONNX model, ship inside the installer. Nothing is downloaded on demand, so offline use is preserved and no further network-boundary change is needed.
