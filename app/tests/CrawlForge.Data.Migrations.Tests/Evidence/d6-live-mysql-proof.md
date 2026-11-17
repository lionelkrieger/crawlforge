# DEFECT-11 (D6) — Live MySQL Migration Proof

Builder: cf-wp-00-05 · Task-45 · Criterion #18 (MySQL parity) unblocked.

## Defect

MySQL backend startup died in `SchemaMigrator.MigrateAsync`:
`MySqlException: Identifier name 'ux_semantic_link_opportunities_audit_id_source_url_id_target_url_id' is too long`
(MySQL 64-char identifier cap). Two catalog indexes synthesized over-long names:

| Location | Synthesized name | Length |
|---|---|---|
| SchemaCatalog.Additive.cs:180 | ux_semantic_link_opportunities_audit_id_source_url_id_target_url_id | 67 |
| SchemaCatalog.Additive.cs:256 | ix_ngram_site_summaries_audit_id_zone_ngram_order_document_frequency | 68 |

The MySQL logical schema (`MySqlLogicalSchema`) already declared short names —
catalog and logical schema were out of sync.

## Fix (index naming only; no column/table structure changes)

- `SchemaCatalog.MaxIdentifierLength = 64` + `ValidateIdentifierName` guard:
  any synthesized OR explicit name > 64 throws a clear `InvalidOperationException`
  at catalog construction (fails fast, prevents the whole class).
- Explicit-name `Idx`/`Ux` overloads; the two indexes now use the exact
  `MySqlLogicalSchema` short names (consistent across SQLite + MySQL):
  - `ux_semantic_link_opportunities_audit_source_target` (50 chars)
  - `ix_ngram_site_summaries_audit_zone_order_df` (43 chars)
- Neighbors verified under the limit: :181 = 54, :182 = 51, :255 = 55 chars.
- Pre-release rename is safe; migration idempotency is ledger-fenced and the
  first-run seed is `WHERE NOT EXISTS`-guarded (both proven live below).

## Live proof (captured from test output)

```
MySQL VERSION(): 8.0.28
Scratch database: crawlforge_d6
Schema version after first apply: 4
Physical tables: 40
Idempotent re-run: version, ledger, tables, and seed row unchanged (no-op).
D6 LIVE PROOF PASS — MySQL 8.0.28, scratch crawlforge_d6, version 4, 40 tables,
both short indexes present, 0 over-long identifiers.
```

Server: 127.0.0.1:3306, root / empty password (user-configured test server).
Flow: DROP/CREATE scratch `crawlforge_d6` → real `SchemaMigrator` + `MySqlDialect`
full-catalog `MigrateAsync` → version 4, 40 tables, ledger rows = migration count,
both D6 short indexes present in `information_schema.statistics`, zero index
names > 64 chars, exactly 1 seeded settings row → second `MigrateAsync` is a
complete no-op → scratch database dropped.

## Verification results

| Command | Result |
|---|---|
| `dotnet test ...\CrawlForge.Data.Migrations.Tests` (CF_MYSQL_TEST_* set) | **56/56 PASS, 0 skipped, exit 0** |
| Live fact isolated (detailed output above) | **1/1 PASS, exit 0** |
| Same suite without server config | live fact SKIPS gracefully (repo convention), exit 0 |
| `governance-analyzer --root ...\CrawlForge.Data.Migrations --fail-on-violation` | **13 files, 0 violations, PASS, exit 0** |

New regression facts (`IndexNameLengthTests`): every catalog index name ≤ 64
(both dialects — one provider-agnostic name set), every table name ≤ 64, names
unique, D6 indexes carry the exact MySQL logical-schema short names, embedded
dialect physically creates the short names, guard throws > 64 / accepts 64.

## Incidental repair (accepted by orchestrator, separate from D6)

`AdditiveVocabularyTests.AllSixDomZonesPersistOnLinkEdges` hardcoded 6 DOM zones
but the locked `DomZone` enum has 7 (WP-03-A1 Breadcrumb amendment) — stale test,
failing before D6. Repaired contract-driven (`Enum.GetValues<DomZone>().Length`),
renamed `AllDomZonesPersistOnLinkEdges`.
