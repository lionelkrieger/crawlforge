/**
 * Finding detail panel with evidence drill-down (WP-09-08, FS-CRAWL-001
 * §8.2 issue explorer: "drill-down to affected URLs"; FS-CRAWL-002 §8.3).
 *
 * Renders the locked finding wire shape (`Finding` + `FindingEvidence`):
 * the message, severity/category badges, the affected URL record, and a
 * GENERIC key/value drill-down over `evidence.details` — every evidence key
 * the producer emitted (wave-9A shapes such as structured-data.invalid's
 * missingRequired/errors/nestedEntityTypes, canonical.header-html-conflict's
 * headerValue/htmlValue/diff, hreflang reciprocity's pageUrl/targetUrl/
 * targetAnnotations, …). URL-valued strings render as links; `*UrlId`
 * fields render as drill-down buttons handed off through `onOpenUrl` (the
 * shell opens the URL explorer / URL detail for that record).
 *
 * Suppression actions live here too: finding-scoped and rule-scoped
 * suppress, and the reversible unsuppress when a suppression covers the row.
 *
 * @module surfaces/explorers/issues/FindingDetail
 */

import type { Finding } from '../../../contracts/api/audits';
import type { JsonValue } from '../../../contracts/common/primitives';
import type { SuppressionState } from '../../../contracts/api/projects';
import { Badge, severityTone, TextCell } from '../cells';
import { findingSuppression, suppressionLabel } from './suppression';

/** Detail panel props. */
export interface FindingDetailProps {
  readonly finding: Finding;
  readonly suppressions: readonly SuppressionState[];
  /** Suppressions are project-scoped; absent project disables the actions. */
  readonly projectId: string | null;
  readonly busy: boolean;
  readonly onOpenUrl?: (urlId: string) => void;
  readonly onSuppress: (finding: Finding, scope: 'finding' | 'rule') => void;
  readonly onUnsuppress: (finding: Finding) => void;
  readonly onClose: () => void;
}

/** True for evidence keys that reference a URL record id (urlId, pageUrlId, targetUrlId…). */
function isUrlIdKey(key: string): boolean {
  return /urlid$/i.test(key);
}

/** True for absolute http(s) URL strings. */
function isUrlValue(value: string): boolean {
  return value.startsWith('http://') || value.startsWith('https://');
}

/** Renders one evidence value generically (key/value drill-down). */
function EvidenceValue({ value }: { value: JsonValue }) {
  if (value === null) {
    return <span className="cf-cell-empty">null</span>;
  }
  if (typeof value === 'boolean') {
    return <Badge tone={value ? 'success' : 'danger'}>{value ? 'true' : 'false'}</Badge>;
  }
  if (typeof value === 'number') {
    return <span className="cf-number-cell">{value}</span>;
  }
  if (typeof value === 'string') {
    if (isUrlValue(value)) {
      return (
        <a className="cf-evidence-link" href={value} target="_blank" rel="noreferrer" title={value}>
          {value}
        </a>
      );
    }
    return <TextCell value={value} />;
  }
  // Arrays / objects: readable structured JSON (bounded scroll region).
  return (
    <pre className="cf-evidence-json" data-testid="cf-evidence-json">
      {JSON.stringify(value, null, 2)}
    </pre>
  );
}

/** The finding detail drawer body. */
export function FindingDetail({
  finding,
  suppressions,
  projectId,
  busy,
  onOpenUrl,
  onSuppress,
  onUnsuppress,
  onClose
}: FindingDetailProps) {
  const covering = findingSuppression(finding, suppressions);
  const details = finding.evidence?.details ?? null;
  const detailKeys = details ? Object.keys(details) : [];
  const actionsDisabled = busy || projectId === null;

  return (
    <aside className="cf-drilldown" role="dialog" aria-label="Finding detail" data-testid="issues-detail">
      <div className="cf-drilldown-header">
        <h3 data-testid="issues-detail-title">{finding.ruleKey}</h3>
        <button
          type="button"
          className="cf-drilldown-close"
          onClick={onClose}
          aria-label="Close detail"
          data-testid="issues-detail-close"
        >
          ✕
        </button>
      </div>
      <p className="cf-drilldown-subtitle">{finding.message}</p>

      <div className="cf-drilldown-fields">
        <div className="cf-drilldown-field">
          <span className="cf-drilldown-label">Severity</span>
          <span className="cf-drilldown-value">
            <Badge tone={severityTone(finding.severity)}>{finding.severity}</Badge>
          </span>
        </div>
        <div className="cf-drilldown-field">
          <span className="cf-drilldown-label">Category</span>
          <span className="cf-drilldown-value"><Badge tone="info">{finding.category}</Badge></span>
        </div>
        <div className="cf-drilldown-field">
          <span className="cf-drilldown-label">Status</span>
          <span className="cf-drilldown-value" data-testid="issues-detail-status">
            {covering ? (
              <Badge tone="neutral" title={suppressionLabel(covering)}>suppressed</Badge>
            ) : (
              <Badge tone="success">active</Badge>
            )}
          </span>
        </div>
        <div className="cf-drilldown-field">
          <span className="cf-drilldown-label">Affected URL</span>
          <span className="cf-drilldown-value">
            {finding.urlId !== null ? (
              <button
                type="button"
                className="cf-evidence-open-url"
                data-testid="issues-detail-open-url"
                onClick={() => onOpenUrl?.(finding.urlId as string)}
                title={`Open URL record ${finding.urlId}`}
              >
                Open URL record {finding.urlId}
              </button>
            ) : (
              <Badge tone="ember">site-wide</Badge>
            )}
          </span>
        </div>
      </div>

      {finding.evidence?.snippet ? (
        <div className="cf-drilldown-section">
          <div className="cf-drilldown-section-heading">Snippet</div>
          <pre className="cf-evidence-json" data-testid="issues-detail-snippet">{finding.evidence.snippet}</pre>
        </div>
      ) : null}
      {finding.evidence?.selector ? (
        <div className="cf-drilldown-section">
          <div className="cf-drilldown-section-heading">Selector</div>
          <TextCell value={finding.evidence.selector} mono />
        </div>
      ) : null}

      <div className="cf-drilldown-section" data-testid="issues-detail-evidence">
        <div className="cf-drilldown-section-heading">Evidence</div>
        {detailKeys.length === 0 ? (
          <div className="cf-explorer-note" data-testid="issues-detail-evidence-empty">
            No evidence payload was recorded for this finding.
          </div>
        ) : (
          detailKeys.map((key) => {
            const value = (details as Record<string, JsonValue>)[key];
            const urlIdDrill =
              isUrlIdKey(key) && typeof value === 'string' && value.length > 0 && onOpenUrl;
            return (
              <div className="cf-drilldown-field" key={key} data-testid={`issues-evidence-${key}`}>
                <span className="cf-drilldown-label">{key}</span>
                <span className="cf-drilldown-value">
                  <EvidenceValue value={value} />
                  {urlIdDrill ? (
                    <button
                      type="button"
                      className="cf-evidence-open-url"
                      data-testid={`issues-evidence-open-${key}`}
                      onClick={() => onOpenUrl?.(value as string)}
                      title={`Open URL record ${value}`}
                    >
                      Open URL record
                    </button>
                  ) : null}
                </span>
              </div>
            );
          })
        )}
      </div>

      <div className="cf-drilldown-section">
        <div className="cf-drilldown-section-heading">Suppression</div>
        {projectId === null ? (
          <div className="cf-explorer-note" data-testid="issues-detail-no-project">
            Suppression is project-scoped — open this audit from a project to manage suppressions.
          </div>
        ) : covering ? (
          <button
            type="button"
            className="cf-suppression-button"
            data-testid="issues-detail-unsuppress"
            disabled={busy}
            onClick={() => onUnsuppress(finding)}
            title={`Reverse suppression ${covering.key}`}
          >
            Unsuppress ({suppressionLabel(covering)})
          </button>
        ) : (
          <div className="cf-suppression-actions">
            <button
              type="button"
              className="cf-suppression-button"
              data-testid="issues-detail-suppress"
              disabled={actionsDisabled}
              onClick={() => onSuppress(finding, 'finding')}
              title="Suppress this rule on the affected URL"
            >
              Suppress this finding
            </button>
            <button
              type="button"
              className="cf-suppression-button"
              data-testid="issues-detail-suppress-rule"
              disabled={actionsDisabled}
              onClick={() => onSuppress(finding, 'rule')}
              title="Suppress this rule across every URL"
            >
              Suppress rule (all URLs)
            </button>
          </div>
        )}
      </div>
    </aside>
  );
}
