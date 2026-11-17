# CrawlForge Governance Static Analyzer

**Workpiece WP-00-05 (phase-00, wave-0B)** — clean-room negative-requirement verifier and CI gate.

Enforces all **17 negative requirements** — FS-CRAWL-001 §3 rows 1–8 (`NR-01`…`NR-08`) and
FS-CRAWL-002 §1.6 E1–E9 (`NR-E1`…`NR-E9`) — plus the always-on **network boundary**
(`NET-01` non-loopback listeners, `NET-02` outbound call-site lint) and the **privacy-invariant
identity-dimension lint** (`ID-01`, FS-CRAWL-002 §1.4 precedence rule 8).

Source of truth: `architecture\01-foundation-and-governance\01-clean-room-and-exclusions.md`.

## CLI

```
dotnet run --project E:\Crawlforge\app\tools\governance-analyzer -- --root E:\Crawlforge\app\src --fail-on-violation
```

| Option | Meaning |
|---|---|
| `--root <dir>` | Source root to scan (required). |
| `--fail-on-violation` | Exit **1** when any violation is found (CI gate mode). |
| `--out <file>` | Write the JSON report to a file instead of stdout. |
| `--boundary-config <file>` | Override the approved network-boundary allowlist (see below). |
| `--rules <id,id,…>` | Only report the listed rule ids (debugging / focused gates). |
| `--help` | Usage. |

Exit codes: **0** pass (or violations without `--fail-on-violation`), **1** violations with
`--fail-on-violation`, **2** usage/IO error. The JSON report goes to stdout (or `--out`); a
one-line human summary goes to stderr so stdout stays pipe-safe.

### Report shape (machine-readable, deterministic)

```json
{
  "tool": "crawlforge-governance-analyzer",
  "toolVersion": "1.0.0",
  "root": "E:\\Crawlforge\\app\\src",
  "filesScanned": 34,
  "rules": [ { "id": "NR-01", "title": "...", "specReference": "FS-CRAWL-001 §3 row 1",
               "severity": "error", "detectionBasis": "..." } ],
  "violations": [ { "ruleId": "NR-01", "severity": "error", "file": "backend/.../X.cs",
                    "line": 12, "column": 9, "evidence": "...", "message": "..." } ],
  "violationsByRule": { "NR-01": 1 },
  "passed": false
}
```

Violations are ordered by (file, line, column, ruleId, evidence); the report contains no
timestamps, so identical inputs produce byte-identical reports. Every rule's **detection
basis** (including heuristic limits) is embedded in the report.

## How detection works

- **C#** — Roslyn *syntax-only* analysis (`Microsoft.CodeAnalysis.CSharp`, pinned in this
  csproj with CPM disabled locally). No compilation, no semantic model: fast, and independent
  of sibling workpieces' in-progress code. Facts collected: usings, declarations, invocations,
  object creations, member accesses, string literals (with transmission/data context).
- **TS/JS** — comment-aware lexical scanner (comments are blanked with positions preserved
  before matching; strings are tracked). Facts: imports, calls with balanced arguments,
  declarations, literals, object keys, host configs.
- **Dependencies** — banned NuGet `PackageReference` entries (`.csproj`) and banned npm
  dependency keys (`package.json`). Build scripts/tooling settings are not inspected.
- **Never flagged**: comments; diagnostic declarations such as `remoteFlagResolution = false`;
  local toggles (`LocalFeatureAvailability`); loopback binds/clients (`IPAddress.Loopback`,
  `127.0.0.1`, `::1`, `localhost`, relative renderer URLs); local file I/O; BCL path-enum reads
  (`Environment.SpecialFolder.UserProfile/ApplicationData/LocalApplicationData` — directory
  locations, never identity reads; genuine `Environment.UserName/UserDomainName/MachineName/OSVersion`
  reads still fire); local structured
  logs and `MiniDumpWriteDump`-class local minidumps; pull-only Prometheus exposition; local
  assembly discovery (`Assembly.Load`); bundled-asset loads; the consented DNS check
  (`System.Net.Dns`, `dns` module); WAF/MarTech *detection* vocabulary and catalogue data
  (vendor hosts as pure data literals are exempt outside transmission context).

### Identifier matching

CamelCase/snake_case token sequences, not substrings: `industrial` never matches `trial`,
`Activator` never matches `activation`, `LogInformation` never matches `login`. Patterns match
declarations, invocations, type references and member accesses — never anonymous diagnostic
object members.

### Network boundary (`NET-02`)

Outbound client construction/usage is flagged unless the target is provably loopback /
same-origin, or the call site sits in an **approved boundary scope** per
[`boundary-allowlist.json`](boundary-allowlist.json):

- namespace prefixes: `CrawlForge.Crawler` (crawl targets / configured proxy),
  `CrawlForge.Export` (user destinations), `CrawlForge.Data.MySql` (user external DB),
  `CrawlForge.Intelligence.LogAnalyzer` (consented DNS);
- type-name patterns: `*CrawlTarget*`, `*TargetFetcher*`, `*Proxy*`, `*Destination*`,
  `*ExternalDb*`, `*Dns*`;
- path fragments: `crawler/fetching`, `crawler/proxy`, `crawlforge.export/`, `data.mysql/`,
  `loganalyzer/`, `plugins/` (user-granted plugin destinations).

Later workpieces that introduce a new approved boundary class can be accommodated without
touching analyzer code: the orchestrator passes `--boundary-config <file>` (or amends the
allowlist through the serialized dependency protocol). Everything outside these scopes must
stay loopback — ever.

### Scan exclusions

`obj/`, `bin/`, `node_modules/`, `dist/`, `build/`, `TestResults/`, `.git/`, `.vs/`,
`artifacts/`, `packages/`, `coverage/`, `.next/`, `.vite/`; generated files (`*.g.cs`,
`*.Designer.cs`, `*.AssemblyInfo.cs`, `*.min.js`, …); and the analyzer's **own** rule data and
fixture suites (`tools/governance-analyzer/`, `tests/governance/`) when a broader root is
scanned — they contain detection signatures and planted violations, not product behavior.

## Fixture suite (criterion #2 static evidence)

`E:\Crawlforge\app\tests\governance\Fixtures\<rule>\` holds, for **every** rule:

- `violation.*` — planted violation that must fire the rule (C# and TS where both surfaces exist);
- `clean.*` — clean code that must pass **all** rules (loopback clients, local toggles,
  bundled-asset loads, consented DNS, detection-only WAF vocabulary, boundary classes, …).

Fixtures are data (excluded from compilation) and are never planted under `app\src`.

Run: `dotnet test E:\Crawlforge\app\tests\governance` (under `CF_AGENT_ID`, protocol §10.2).

## Wiring as a verification gate (every later workpiece)

```
dotnet run --project E:\Crawlforge\app\tools\governance-analyzer -- --root E:\Crawlforge\app\src --fail-on-violation
```

Exit 0 required. The orchestrator adds both projects to `CrawlForge.sln` at the wave-0B gate;
until then, verification uses direct project paths. The tool builds with the shared
`Directory.Build.props` (net10.0, nullable, warnings-as-errors) and honors `CF_AGENT_ID`
output isolation.
