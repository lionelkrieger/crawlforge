# DEFECT D12 — MySQL Archive Streaming Export Transaction Fix (criterion #18 revert)

Builder: cf-wp-00-05 · Task-49 · MySQL archive revert unblocked.

## Defect

`ProjectArchiveService.ExportAsync` from MySQL threw MySqlConnector
"The transaction associated with this command is not the connection's active
transaction" at `RepositoryOps.StreamAsync` (RepositoryOps.cs:98 via
ProjectArchiveService.cs:121). Forward switch + report parity + body fidelity
passed; only the MySQL streaming export (the revert path) failed.

## Root cause

`ExportAsync` opens one connection and begins an ambient transaction, then
streams every table through `RepositoryOps.StreamAsync(..., connection, ct)`.
`StreamAsync` created its command on the shared connection but NEVER set
`command.Transaction`. MySqlConnector requires every command on a connection
with an active transaction to be enlisted in that exact transaction;
Microsoft.Data.Sqlite is lenient (implicit enlistment), which masked the
defect on the embedded provider.

## Fix (product, owned scope — provider-neutral, fidelity-preserving)

- `RepositoryOps.StreamAsync`: new optional `DbTransaction? transaction`
  parameter; the streaming command is enlisted (`command.Transaction =
  transaction`). Guards reject a transaction from a different connection and a
  transaction without its shared connection (clear ArgumentException). The
  streaming read stays inside the caller's consistent snapshot on every
  provider — archive fidelity preserved; no separate connection, no ambient
  suppression. Additive optional parameter: existing callers unaffected.
- `ProjectArchiveService.ExportAsync`: both streaming call sites (table plans
  + content-artifact index) now pass the ambient transaction.
- Import path unchanged (already enlisted every command correctly).

## Facts added (CrawlForge.Data.Repositories.Tests)

`ArchiveTransactionTests`:
1. `StreamAsync_EnlistedInAmbientTransaction_ReadsTransactionRows` — streaming
   before commit sees the transaction's own row (embedded provider).
2. `StreamAsync_TransactionFromDifferentConnection_Throws` — guard.
3. `StreamAsync_TransactionWithoutConnection_Throws` — guard.
4. `MySqlArchiveExport_StreamsInTransaction_RevertRoundTripPreservesBodies`
   [LiveMySqlFact] — the EXACT D12 failure path: rich project seeded on live
   MySQL (scratch crawlforge_d12, real migrator schema), ExportAsync streams
   every table inside the ambient transaction, bundle imports into a fresh
   embedded database; asserts per-table row fidelity, identical artifact
   storage keys + compressed bytes, and original payload readability.
   Harness: MySqlTestProvider double + CF_MYSQL_TEST_* gating (graceful skip),
   MySqlConnector 2.6.2 pinned locally (project CPM disabled).

Sibling coordination note: ProjectArchiveTests.cs was updated by its owner
mid-turn (ContentArtifactDto slimmed to ContentHash; store.PutAsync writes the
index row); the D12 seeder mirrors the current shape. No sibling file edited.

## Verification results (live server 127.0.0.1:3306 root/empty)

| Command | Result |
|---|---|
| `dotnet test CrawlForge.Data.Repositories.Tests` (live env) | **146/146 PASS, 0 skipped, exit 0** |
| D12 facts isolated (detailed) | 4/4 PASS exit 0 — MySQL VERSION() 8.0.28; streaming export 8091 bytes; revert round trip full row + body fidelity |
| `dotnet test CrawlForge.Data.Sqlite.Tests` | **75/75 PASS, exit 0** |
| `dotnet test CrawlForge.Data.MySql.Tests` (live env) | **163/163 PASS, 0 skipped, exit 0** |
| `dotnet test CrawlForge.Data.Migrations.Tests` (live env) | **58/58 PASS, 0 skipped, exit 0** |
| `dotnet test CrawlForge.Data.ContentStore.Tests` | exit 0 (placeholder project, no discoverable tests) |
| `governance-analyzer --root CrawlForge.Data.Repositories --fail-on-violation` | **passed, exit 0** |
| `governance-analyzer --root app --fail-on-violation` | **637 files, passed, exit 0** (no drift) |

E2E confirmation (cf-wp-01-01, 2026-11-14): **80/80 GREEN, exit 0** (4m43s
full consolidated suite). ArchiveSwitch_ToMySql_AndExplicitRevert flip GREEN:
switch → MySQL parity IDENTICAL → body fidelity → explicit revert into fresh
embedded DB → parity IDENTICAL → bodies survive round trip. All 4 MySQL tests
green on live 8.0.28. Criterion #18 fully re-proven (D10 parity + D12 revert).
