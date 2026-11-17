# DEFECT D10 — Report-Row Contributing Counts Fix (criterion #18)

Builder: cf-wp-00-05 · Task-48 · MySQL parity unblocked.

## Defect

`ProviderSwitchOrchestrator.ReadReportRowsAsync` queried
`SELECT category, score, contributing_counts_json FROM audit_scores` — but the
locked catalog (SchemaCatalog.Baseline.cs:323-334) stores the contributing
counts as FOUR INTEGER COLUMNS (error_count, warning_count, notice_count,
pass_count); no contributing_counts_json column exists. SqliteException
"no such column" at ReadReportRowsAsync blocked both MySQL-parity E2E tests.
A second latent bug: `category` is a NULLABLE INT (AuditCategory ordinal),
but the reader used GetString — invalid on MySQL and on NULL (overall) rows.

## Investigation (locked-contract alignment)

- `AuditScoreDto` (locked Contracts): Category = AuditCategory?, four int counts.
  NO JSON column anywhere in the contract → no column added.
- `AuditScoreRecorder.RecordAsync` persists exactly:
  `INSERT INTO audit_scores (audit_id, category, score, error_count, warning_count, notice_count, pass_count)`
  with category = ordinal or DBNull.
- Fix: align the query to the actual schema and compose the payload.

## Fix (product, owned scope)

`ProviderSwitchOrchestrator.ReadReportRowsAsync`:
- Query: `SELECT category, score, error_count, warning_count, notice_count, pass_count
  FROM audit_scores WHERE audit_id = @auditId ORDER BY category` (provider-neutral).
- Category mapping: NULL → `"overall"` marker; ordinal → `AuditCategory` name
  (the product's category-key convention, matching the recorder's dimension keys).
- ContributingCountsJson: composed from the four columns with a deterministic
  camelCase shape `{"errorCount":N,"warningCount":N,"noticeCount":N,"passCount":N}`
  — identical on SQLite and MySQL (same ints, same serializer order).
- `ProviderSwitchContracts.AuditScoreRow` doc clarified (shape unchanged).

## Test updates (owned Data test projects)

- `FakeAdo` now serves RAW audit_scores rows (nullable int category + four counts)
  instead of emulating the nonexistent JSON column; the two reader tests assert
  the composed mapping + that the SQL never references contributing_counts_json.
- `LiveServerTests.Report_Rows_Read_And_Compare_Identical`: temp table corrected
  to the real catalog shape; asserts overall/Content mapping + exact count payloads.
- NEW `ReportRowParityTests` (Data.Migrations.Tests): rows inserted with the EXACT
  AuditScoreRecorder statement; SQLite fact + live MySQL fact (scratch
  crawlforge_d10) assert correct counts on BOTH providers and exact cross-provider
  CompareReports parity.

## Incidental harness repair (same category as the accepted DomZone repair)

`LiveServerTests.Ngram_Read_Model_Refresh_Is_Idempotent` used TEMPORARY tables,
but the product refresh self-joins ngram_site_summaries in one UPDATE — MySQL
forbids referencing a temporary table twice in one query ("Can't reopen table").
Product SQL is correct on real InnoDB tables; the test now uses real scratch
tables dropped in finally. Latent defect: the live suite always skipped before
a server existed.

## Environment prep

Created the empty `crawlforge_test` scratch database (the Data.MySql live suite
connects to it directly; it uses only session-scoped objects). `crawlforge_e2e`
already existed for the E2E parity suite.

## Verification results

| Command | Result |
|---|---|
| `dotnet test CrawlForge.Data.MySql.Tests` (live env) | **163/163 PASS, 0 skipped, exit 0** |
| `dotnet test CrawlForge.Data.Migrations.Tests` (live env) | **58/58 PASS, 0 skipped, exit 0** |
| D10 facts isolated (detailed) | 2/2 PASS exit 0 — MySQL VERSION() 8.0.28, scratch crawlforge_d10, counts correct both providers, cross-provider parity identical |
| `governance-analyzer --root CrawlForge.Data.MySql --fail-on-violation` | **15 files, 0 violations, exit 0** |
| E2E blocked parity tests (cf-wp-01-01 suite, read-only run) | see observation below |

## E2E observation — RESOLVED (concurrent-run race, not a product defect)

A read-only confirmation run of the two blocked E2E parity tests failed in the
E2E fixture before any report reading ("Unknown database" / "application_settings
already exists", post-run state: 27 tables, ledger v1 only). cf-wp-01-01
confirmed the root cause: concurrent builder runs racing on the same fresh
crawlforge_e2e database (DROP/CREATE per test). Their fixture now splits the
reset into separate commands and calls ClearAllPoolsAsync; the E2E re-run is
coordinated after the D12 fix lands. The D10 surface itself is proven green on
BOTH live providers at the reader level (ReportRowParityTests, exact
CompareReports parity, MySQL 8.0.28). Scratch-database isolation: D6/D10 proofs
use crawlforge_d6/crawlforge_d10, never crawlforge_e2e.
