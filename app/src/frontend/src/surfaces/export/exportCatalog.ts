/**
 * Export surface catalog (WP-09-10, UI-20; FS-CRAWL-001 §8.4/§11.8).
 *
 * Local additive catalog for the export dialog: the frozen shared
 * `contracts/api/exports.ts` ExportFormat union carries only the four
 * baseline keys; the additive formats (xml-sitemap, json, jsonl, text,
 * markdown) and the column-selection affordance live here until the lead's
 * additive contract amendment lands (handoff). Wire keys match the backend
 * exactly (ExportsController.TryParseFormat + AdditiveExportFormats).
 *
 * @module surfaces/export/exportCatalog
 */

import type { EntityId } from '../../contracts/common/primitives';
import type { ExportFormat } from '../../contracts/api/exports';

/** Additive format keys beyond the frozen shared union (WP-09-10 handoff). */
export type AdditiveExportFormat = 'xml-sitemap' | 'json' | 'jsonl' | 'text' | 'markdown';

/** Every format the export dialog offers. */
export type ExportFormatKey = ExportFormat | AdditiveExportFormat;

/** One offered export format. */
export interface ExportFormatOption {
  readonly key: ExportFormatKey;
  readonly label: string;
  /** Local artifact extension (csv/text/markdown are zip bundles). */
  readonly extension: string;
  readonly description: string;
}

/** Format catalog — baseline first, additive appended (CR-011/CR-017). */
export const EXPORT_FORMATS: readonly ExportFormatOption[] = [
  { key: 'xlsx', label: 'Excel workbook (.xlsx)', extension: '.xlsx', description: 'Multi-sheet workbook: summary, URLs, findings, links, extraction.' },
  { key: 'csv', label: 'CSV per dataset (.zip)', extension: '.zip', description: 'One RFC-4180 CSV per selected dataset inside a zip archive.' },
  { key: 'html', label: 'HTML report (.html)', extension: '.html', description: 'Self-contained offline HTML report.' },
  { key: 'pdf', label: 'PDF summary (.pdf)', extension: '.pdf', description: 'Paginated summary with findings severity rollup.' },
  { key: 'xml-sitemap', label: 'XML sitemap (.xml)', extension: '.xml', description: 'Standards-conformant sitemap of eligible URLs (index when split).' },
  { key: 'json', label: 'JSON — agent dataset (.json)', extension: '.json', description: 'Summary-first compact JSON with stable identifiers (§11.8).' },
  { key: 'jsonl', label: 'JSON Lines — agent bulk (.jsonl)', extension: '.jsonl', description: 'One JSON object per row for agents working in code (§11.8).' },
  { key: 'text', label: 'Full page text (.zip)', extension: '.zip', description: 'Plain visible text per stored page, with identifier manifest.' },
  { key: 'markdown', label: 'Full page Markdown (.zip)', extension: '.zip', description: 'Markdown content per stored page, with identifier manifest.' }
];

/** One offered dataset (baseline + FS-CRAWL-002 Appendix B additive keys). */
export interface ExportDatasetOption {
  readonly key: string;
  readonly label: string;
}

/** Dataset catalog mirroring ExportDatasetKeys on the backend. */
export const EXPORT_DATASETS: readonly ExportDatasetOption[] = [
  { key: 'summary', label: 'Summary' },
  { key: 'urls', label: 'URLs' },
  { key: 'findings', label: 'Findings' },
  { key: 'links', label: 'Links' },
  { key: 'custom-extraction', label: 'Custom extraction' },
  { key: 'snippets', label: 'Snippet results' },
  { key: 'ngrams', label: 'N-grams' },
  { key: 'redirect-matrix', label: 'Redirect matrix' },
  { key: 'waf-blocks', label: 'WAF blocks' },
  { key: 'semantic-opportunity', label: 'Semantic opportunity' },
  { key: 'analytics', label: 'Analytics' }
];

/** Default dataset selection (ExportDatasetKeys.Default). */
export const DEFAULT_DATASETS: readonly string[] = ['summary', 'urls', 'findings', 'links'];

/** Formats whose payload is a per-page content bundle (dataset selection N/A). */
export const PAGE_CONTENT_FORMATS: readonly ExportFormatKey[] = ['text', 'markdown'];

/** One selectable column of a dataset. */
export interface ExportColumnOption {
  readonly key: string;
  readonly label: string;
}

/**
 * Static column catalog for the fixed datasets (mirrors ExportDatasetReader
 * baseline columns; identifier columns are added server-side for agent
 * formats). Dynamic datasets (custom-extraction, snippets) fetch their
 * columns from the REST surface at dialog time.
 */
export const STATIC_DATASET_COLUMNS: Readonly<Record<string, readonly ExportColumnOption[]>> = {
  summary: [
    { key: 'section', label: 'Section' },
    { key: 'key', label: 'Key' },
    { key: 'dimensions', label: 'Dimensions' },
    { key: 'value', label: 'Value' },
    { key: 'detail', label: 'Detail' }
  ],
  urls: [
    { key: 'url', label: 'URL' },
    { key: 'host', label: 'Host' },
    { key: 'path', label: 'Path' },
    { key: 'depth', label: 'Depth' },
    { key: 'discovery_source', label: 'Discovery Source' },
    { key: 'status_class', label: 'Status Class' },
    { key: 'final_status_code', label: 'Final Status Code' },
    { key: 'redirect_target_url', label: 'Redirect Target' },
    { key: 'content_type', label: 'Content Type' },
    { key: 'size_bytes', label: 'Size (bytes)' },
    { key: 'robots_state', label: 'Robots State' },
    { key: 'indexability_flags', label: 'Indexability Flags' },
    { key: 'is_indexable', label: 'Indexable' },
    { key: 'timing_json', label: 'Timing' }
  ],
  findings: [
    { key: 'rule_key', label: 'Rule' },
    { key: 'category', label: 'Category' },
    { key: 'severity', label: 'Severity' },
    { key: 'url', label: 'URL' },
    { key: 'evidence_json', label: 'Evidence' },
    { key: 'recommendation_key', label: 'Recommendation' },
    { key: 'is_suppressed', label: 'Suppressed' }
  ],
  links: [
    { key: 'source_url', label: 'Source URL' },
    { key: 'canonical_target_url', label: 'Target URL' },
    { key: 'target_url', label: 'Resolved Target' },
    { key: 'anchor_text', label: 'Anchor Text' },
    { key: 'relation_flags', label: 'Relation Flags' },
    { key: 'is_internal', label: 'Internal' }
  ]
};

/** Additive export job wire record (mirrors Api ExportJobWire). */
export interface ExportJobWire {
  readonly id: EntityId;
  readonly auditId: EntityId;
  readonly format: string;
  readonly state: 'queued' | 'running' | 'completed' | 'failed';
  readonly createdAt: string;
  readonly filePath: string | null;
  readonly datasets: readonly string[];
}

/** Extension for a format key (unknown keys fall back to .bin). */
export function extensionFor(format: ExportFormatKey): string {
  return EXPORT_FORMATS.find((option) => option.key === format)?.extension ?? '.bin';
}