# Dynamic-Column Schema Contract — Custom Extraction & JS Snippets

**Owner:** WP-03-07 (`CrawlForge.Audit\Custom`). **Consumers:** phase-05 query
builder (§8.7), custom extraction explorer (§8.2), spreadsheet/CSV exports
(§8.4), automation interface. **Programmatic surface:**
`CrawlForge.Audit.Custom.DynamicColumnSchema` (+ `DynamicColumnDescriptor`).

This document is the normative companion to the XML-documented public surface.
It fixes column naming, types, and caps so downstream surfaces never re-derive
them from raw rows.

## 1. Extraction columns (`extracted_content`)

- **One column per rule**, in configured rule order. Column label = the
  user-chosen rule name (verbatim casing).
- **Key:** `custom.{ruleName}`. When a rule is multi-valued (`AllowMultiple`)
  and any page captured more than one value, the column expands to
  `custom.{ruleName}.{ordinal}` — one column per ordinal up to the **busiest
  page's** value count. Ordinals are zero-based and stable.
- **Types** (from the rule's configured `ExtractedValueType` — locked enum:
  Text, Number, Boolean, DateTime):

  | Configured type | Column type | JSON payload shape |
  |---|---|---|
  | Text | `text` | JSON string |
  | Number | `number` | JSON number (decimal; covers integer and decimal captures) |
  | Boolean | `boolean` | JSON true/false |
  | DateTime | `datetime` | JSON string (ISO-8601 round-trip) |

  > Harmonization note (deferred, recorded with the orchestrator): the spec's
  > distinct integer and URL value kinds have no locked enum members yet;
  > Number covers integer/decimal captures. Additive enum members would be a
  > serialized contract amendment if phase-05 requires them.

- **Invalid values:** `TypedValueJson = null` with `IsInvalid = true`. The
  column stays present and typed; explorers render an invalid marker, exports
  emit an empty cell. Conversion failures never fail the audit (criterion #24).
- **Missing required values:** a single invalid draft at ordinal 0
  (`Required = true` rules).

## 2. Snippet columns (`snippet_results`)

- **One column per declared result column** submitted through `cf.submit`,
  unioned across pages in first-seen order.
- **Key:** `snippet.{snippetKey}.{columnKey}`; label = the submitted column
  name. A snippet that only returns a value (no `cf.submit`) produces a single
  `result` column.
- **Types:** inferred from submitted values — `number`, `boolean`, `string`
  (first non-null value wins; all-null columns default to `string`).
- `ColumnsJson` is the typed column definition array
  (`[{ "key", "label", "valueType" }]`); `RowsJson` is the row array of
  column-keyed objects. Both are produced by `JintSnippetRunner` and persisted
  verbatim.

## 3. Caps (schema-level bounds, not data errors)

| Bound | Default | Configurable via | Behavior at the bound |
|---|---|---|---|
| Values per rule per page | 100 | `CustomExtractionOptions.MaxValuesPerPage` | Excess discarded + counted (`discarded` diagnostic) |
| Value length | 10 000 chars (rule `MaxLength` overrides) | `CustomExtractionOptions.DefaultValueMaxLength` | Surrogate-safe truncation + `truncated` flag/diagnostic |
| Rule evaluation time | 2 s | `CustomExtractionOptions.RuleTimeout` | Aborted + `timeout` diagnostic; audit continues |
| Search matches per item per page | 1 000 | `CustomSearchOptions.MaxMatchesPerPage` | Count clamped + `discarded` diagnostic |
| Search snippets per item per page | 20 | `CustomSearchOptions.MaxSnippetsPerPage` | Excess snippets dropped |
| Regex execution | 1 s | `CustomSearchOptions.RegexTimeout` | Aborted + `timeout` diagnostic; item isolated |
| Snippet rows | `JsSnippetDefinition.MaxRows` (100), ceiling 1 000 | `JsSnippetOptions.MaxRowsCeiling` | Excess rows discarded + `IsTruncated` flag |
| Snippet execution | 2 s | `JsSnippetOptions.DefaultTimeout` / per-key overrides | Aborted + `timeout` diagnostic; snippet isolated |

## 4. Invariants for consumers

1. **Stability:** keys and ordinals are deterministic for identical stored
   artifacts + rules — re-audit reproduces the same schema (offline purity).
2. **Isolation:** a failing rule/item/snippet yields diagnostics and (where
   applicable) empty/invalid rows; it never removes other columns or fails the
   audit (criteria #24/#30/#31).
3. **Privacy:** no identity dimensions appear in keys, labels, values, or
   diagnostics.
4. **Provider agnosticism:** the schema is identical on SQLite and MySQL;
   dynamic columns live in JSON payloads, not physical DDL.