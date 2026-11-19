/**
 * UrlDetailPanel — the URL drill-down surface (WP-09-09, FS-CRAWL-002 §8.2
 * + §15.6).
 *
 * Renders the full detail wire: status + CR-024 search-indexability verdict,
 * hop-by-hop redirect chain with loop/cap badges, element inventory (meta
 * robots / canonical / hreflang with HTML-vs-header provenance), the
 * header/HTML conflict diff panel, inlinks/outlinks with anchor text +
 * domZone, asset lists, findings, and the SERP pixel preview with live
 * truncation indicators (desktop/mobile budgets from the bundled measurer —
 * tools/serp/fontMetrics, never re-implemented here).
 *
 * Pure presentational: the parent fetches `audits.getUrl` and passes the
 * wire view; internal link rows drill further through onOpenUrl.
 *
 * @module surfaces/explorers/urls/UrlDetailPanel
 */

import { useMemo, useState } from 'react';
import type { LinkEdge } from '../../../contracts/api/audits';
import {
  analyzeSerp,
  DESKTOP_BUDGET,
  MOBILE_BUDGET,
  TITLE_FONT,
  measureTextPx,
  type SerpBudget
} from '../../../tools/serp/fontMetrics';
import { Badge, statusTone, severityTone, UrlCell, TextCell, NumberCell } from '../cells';
import { normalizeRedirectChain } from './redirectChain';
import { deriveAssets, deriveInventoryFacts } from './inventory';
import type { UrlDetailWireView, UrlOpenTarget } from './types';
import './urls.css';

/** Panel props. */
export interface UrlDetailPanelProps {
  /** The detail wire (locked UrlDetail + additive chain/element fields). */
  readonly detail: UrlDetailWireView;
  /** Owning audit (drill-down handoff identity). */
  readonly auditId: string;
  /** Drill-down callback for internal link targets. */
  readonly onOpenUrl?: (target: UrlOpenTarget) => void;
  /** Close affordance (drawer host). */
  readonly onClose?: () => void;
}

type SerpDevice = 'desktop' | 'mobile';

/** One link row (inlink or outlink) with anchor + domZone + drill-down. */
function LinkRow({ edge, auditId, onOpenUrl, direction }: {
  edge: LinkEdge;
  auditId: string;
  onOpenUrl?: (target: UrlOpenTarget) => void;
  direction: 'in' | 'out';
}) {
  const otherUrl = direction === 'in' ? edge.sourceUrl : edge.targetUrl;
  const otherId = direction === 'in' ? edge.sourceUrlId : edge.targetUrlId;
  const drillable = Boolean(onOpenUrl && edge.internal && otherId);
  return (
    <li
      className={`cf-url-link-row${drillable ? ' cf-url-link-drillable' : ''}`}
      data-testid="url-detail-link"
      data-direction={direction}
      data-dom-zone={edge.domZone ?? 'other'}
    >
      <button
        type="button"
        className="cf-url-link-target"
        disabled={!drillable}
        onClick={() => {
          if (drillable && onOpenUrl) onOpenUrl({ auditId, urlId: otherId, canonicalUrl: otherUrl });
        }}
        data-testid="url-detail-link-target"
        title={otherUrl}
      >
        {otherUrl}
      </button>
      <span className="cf-url-link-anchor" data-testid="url-detail-link-anchor">
        {edge.anchorText ? `“${edge.anchorText}”` : <span className="cf-cell-empty">no anchor</span>}
      </span>
      <Badge tone="neutral">{edge.domZone ?? 'other'}</Badge>
      <Badge tone={edge.internal ? 'info' : 'neutral'}>{edge.internal ? 'internal' : 'external'}</Badge>
    </li>
  );
}

/** The URL detail panel. */
export function UrlDetailPanel({ detail, auditId, onOpenUrl, onClose }: UrlDetailPanelProps) {
  const [device, setDevice] = useState<SerpDevice>('desktop');

  const chain = useMemo(
    () => normalizeRedirectChain(detail.redirectChain, {
      canonicalUrl: detail.canonicalUrl,
      statusCode: detail.statusCode,
      redirectTargetUrl: detail.redirectTargetUrl
    }),
    [detail.redirectChain, detail.canonicalUrl, detail.statusCode, detail.redirectTargetUrl]
  );
  const facts = useMemo(() => deriveInventoryFacts(detail), [detail]);
  const assets = useMemo(() => deriveAssets(detail), [detail]);

  const budget: SerpBudget = device === 'desktop' ? DESKTOP_BUDGET : MOBILE_BUDGET;
  const serpTitle = facts.title ?? '';
  const serpDescription = facts.metaDescription ?? '';
  const analysis = useMemo(() => analyzeSerp(serpTitle, serpDescription, budget), [serpTitle, serpDescription, budget]);
  const titleFullWidth = measureTextPx(serpTitle, TITLE_FONT);

  const findings = detail.findings ?? [];
  const inlinks = detail.inboundLinks ?? [];
  const outlinks = detail.outboundLinks ?? [];
  const conflicts = detail.headerConflicts ?? [];
  const elementCounts = Object.entries(detail.elementInventory?.counts ?? {});

  return (
    <section className="cf-url-detail" data-testid="url-detail" aria-label={`URL detail: ${detail.canonicalUrl}`}>
      <header className="cf-url-detail-header">
        <div className="cf-url-detail-heading">
          <h3 className="cf-url-detail-url" data-testid="url-detail-url" title={detail.canonicalUrl}>
            {detail.canonicalUrl}
          </h3>
          <div className="cf-url-detail-badges">
            <span data-testid="url-detail-status">
              <Badge tone={statusTone(detail.statusClass)}>{detail.statusCode} · {detail.statusClass}</Badge>
            </span>
            <span data-testid="url-detail-indexable">
              <Badge tone={detail.indexable ? 'success' : 'danger'}>
                {detail.indexable ? 'Search-indexable' : 'Not search-indexable'}
              </Badge>
            </span>
            {facts.noindex ? (
              <span data-testid="url-detail-noindex">
                <Badge tone="warning" title="CR-024: fetchable but excluded from search indexing">noindex</Badge>
              </span>
            ) : null}
            <span className="cf-url-detail-depth">depth <NumberCell value={detail.depth} /></span>
            {detail.contentType ? <TextCell value={detail.contentType} mono /> : null}
          </div>
        </div>
        {onClose ? (
          <button type="button" className="cf-url-detail-close" onClick={onClose} aria-label="Close URL detail" data-testid="url-detail-close">✕</button>
        ) : null}
      </header>

      {/* ── Redirect chain (hop-by-hop + loop/cap badges) ─────────────── */}
      <section className="cf-url-detail-section" data-testid="url-detail-redirect-chain" aria-label="Redirect chain">
        <h4>
          Redirect chain ({chain.hops.length} {chain.hops.length === 1 ? 'hop' : 'hops'})
          {chain.loopDetected ? (
            <span data-testid="url-detail-loop-badge"><Badge tone="danger">Redirect loop</Badge></span>
          ) : null}
          {chain.chainCapExceeded ? (
            <span data-testid="url-detail-cap-badge"><Badge tone="warning">Chain cap exceeded</Badge></span>
          ) : null}
        </h4>
        {chain.hops.length === 0 ? (
          <p className="cf-url-detail-empty" data-testid="url-detail-chain-empty">No redirects — direct fetch.</p>
        ) : (
          <ol className="cf-url-chain">
            {chain.hops.map((hop) => (
              <li key={`${hop.ordinal}-${hop.url}`} className="cf-url-chain-hop" data-testid="url-detail-hop" data-hop-ordinal={hop.ordinal}>
                <span className="cf-url-chain-ordinal">{hop.ordinal}</span>
                <span className="cf-url-chain-url" data-testid="url-detail-hop-url" title={hop.url}>{hop.url}</span>
                <span data-testid="url-detail-hop-status"><Badge tone={statusTone(`${Math.floor(hop.statusCode / 100)}xx`)}>{hop.statusCode}</Badge></span>
                <span className="cf-url-chain-arrow" aria-hidden="true">→</span>
                <span className="cf-url-chain-target" data-testid="url-detail-hop-target" title={hop.toUrl ?? undefined}>
                  {hop.toUrl ?? <span className="cf-cell-empty">no target</span>}
                </span>
              </li>
            ))}
          </ol>
        )}
        {chain.hops.length > 0 ? (
          <p className="cf-url-chain-final" data-testid="url-detail-chain-final">
            Final: <UrlCell url={chain.finalUrl} />{' '}
            {chain.finalStatusCode !== null ? <Badge tone={statusTone(`${Math.floor(chain.finalStatusCode / 100)}xx`)}>{chain.finalStatusCode}</Badge> : null}
          </p>
        ) : null}
      </section>

      {/* ── Element inventory (meta robots / canonical / hreflang) ────── */}
      <section className="cf-url-detail-section" data-testid="url-detail-inventory" aria-label="Element inventory">
        <h4>Element inventory</h4>
        <div className="cf-url-facts">
          <div className="cf-url-fact">
            <span className="cf-url-fact-label">Title</span>
            <span className="cf-url-fact-value" data-testid="url-detail-title">{facts.title ?? <span className="cf-cell-empty">—</span>}</span>
          </div>
          <div className="cf-url-fact">
            <span className="cf-url-fact-label">Meta description</span>
            <span className="cf-url-fact-value" data-testid="url-detail-meta-description">
              {facts.metaDescription ?? <span className="cf-cell-empty">—</span>}
            </span>
          </div>
          <div className="cf-url-fact">
            <span className="cf-url-fact-label">Meta robots</span>
            <span className="cf-url-fact-value" data-testid="url-detail-meta-robots">
              {facts.metaRobots ?? <span className="cf-cell-empty">—</span>}
            </span>
          </div>
          <div className="cf-url-fact">
            <span className="cf-url-fact-label">X-Robots-Tag</span>
            <span className="cf-url-fact-value" data-testid="url-detail-x-robots-tag">
              {facts.xRobotsTag ?? <span className="cf-cell-empty">—</span>}
            </span>
          </div>
          <div className="cf-url-fact">
            <span className="cf-url-fact-label">Canonical targets</span>
            <span className="cf-url-fact-value">
              {facts.canonicals.length === 0 ? (
                <span className="cf-cell-empty" data-testid="url-detail-canonicals-empty">self / absent</span>
              ) : (
                <ul className="cf-url-canonicals" data-testid="url-detail-canonicals">
                  {facts.canonicals.map((target) => (
                    <li key={`${target.source}-${target.value}`} data-testid="url-detail-canonical" data-source={target.source}>
                      <Badge tone={target.source === 'html' ? 'info' : target.source === 'header' ? 'ember' : 'warning'}>{target.source}</Badge>{' '}
                      <UrlCell url={target.value} />
                    </li>
                  ))}
                </ul>
              )}
            </span>
          </div>
          <div className="cf-url-fact">
            <span className="cf-url-fact-label">hreflang alternates</span>
            <span className="cf-url-fact-value">
              {facts.hreflang.length === 0 ? (
                <span className="cf-cell-empty">—</span>
              ) : (
                <ul className="cf-url-hreflang" data-testid="url-detail-hreflang">
                  {facts.hreflang.map((alternate) => (
                    <li key={`${alternate.source}-${alternate.hreflang}-${alternate.href}`} data-testid="url-detail-hreflang-entry" data-source={alternate.source}>
                      <Badge tone="neutral">{alternate.hreflang}</Badge> <UrlCell url={alternate.href} /> <span className="cf-url-fact-source">{alternate.source}</span>
                    </li>
                  ))}
                </ul>
              )}
            </span>
          </div>
          {elementCounts.length > 0 ? (
            <div className="cf-url-fact">
              <span className="cf-url-fact-label">Element groups</span>
              <span className="cf-url-fact-value cf-url-element-counts" data-testid="url-detail-element-counts">
                {elementCounts.map(([group, count]) => (
                  <span key={group} className="cf-url-element-count" data-element-group={group}>
                    {group}: <NumberCell value={count} />
                  </span>
                ))}
                <span className="cf-url-element-total">total: <NumberCell value={detail.elementInventory.totalElements} /></span>
              </span>
            </div>
          ) : null}
        </div>
      </section>

      {/* ── Header vs HTML conflict diff (§15.6) ──────────────────────── */}
      {conflicts.length > 0 ? (
        <section className="cf-url-detail-section" data-testid="url-detail-conflicts" aria-label="Header versus HTML conflicts">
          <h4>Header / HTML conflicts ({conflicts.length})</h4>
          <div className="cf-url-conflict-list">
            {conflicts.map((conflict, index) => (
              <div key={index} className="cf-url-conflict" data-testid="url-detail-conflict" data-conflict={conflict.conflict ? 'true' : 'false'}>
                <div className="cf-url-conflict-name">
                  <Badge tone={conflict.conflict ? 'danger' : 'success'}>{conflict.conflict ? 'conflict' : 'equivalent'}</Badge>{' '}
                  <span className="cf-url-conflict-header-name">{conflict.headerName}</span>
                </div>
                <div className="cf-url-conflict-diff">
                  <div className="cf-url-conflict-side" data-testid="url-detail-conflict-header">
                    <span className="cf-url-conflict-side-label">HTTP header</span>
                    <code>{conflict.headerValue || '—'}</code>
                  </div>
                  <div className="cf-url-conflict-side" data-testid="url-detail-conflict-html">
                    <span className="cf-url-conflict-side-label">HTML</span>
                    <code>{conflict.htmlValue ?? '—'}</code>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </section>
      ) : null}

      {/* ── Inlinks / outlinks (anchor + domZone) ─────────────────────── */}
      <section className="cf-url-detail-section cf-url-links" aria-label="Links">
        <div className="cf-url-link-column">
          <h4>Inlinks ({inlinks.length})</h4>
          {inlinks.length === 0 ? (
            <p className="cf-url-detail-empty" data-testid="url-detail-inlinks-empty">No inbound links recorded.</p>
          ) : (
            <ul className="cf-url-link-list" data-testid="url-detail-inlinks">
              {inlinks.map((edge, index) => (
                <LinkRow key={`in-${index}-${edge.sourceUrl}`} edge={edge} auditId={auditId} onOpenUrl={onOpenUrl} direction="in" />
              ))}
            </ul>
          )}
        </div>
        <div className="cf-url-link-column">
          <h4>Outlinks ({outlinks.length})</h4>
          {outlinks.length === 0 ? (
            <p className="cf-url-detail-empty" data-testid="url-detail-outlinks-empty">No outbound links recorded.</p>
          ) : (
            <ul className="cf-url-link-list" data-testid="url-detail-outlinks">
              {outlinks.map((edge, index) => (
                <LinkRow key={`out-${index}-${edge.targetUrl}`} edge={edge} auditId={auditId} onOpenUrl={onOpenUrl} direction="out" />
              ))}
            </ul>
          )}
        </div>
      </section>

      {/* ── Assets ────────────────────────────────────────────────────── */}
      <section className="cf-url-detail-section" data-testid="url-detail-assets" aria-label="Assets">
        <h4>Assets ({assets.length})</h4>
        {assets.length === 0 ? (
          <p className="cf-url-detail-empty" data-testid="url-detail-assets-empty">No assets recorded.</p>
        ) : (
          <ul className="cf-url-asset-list">
            {assets.map((asset) => (
              <li key={`${asset.kind}-${asset.url}`} className="cf-url-asset" data-testid="url-detail-asset" data-asset-kind={asset.kind}>
                <Badge tone="neutral">{asset.kind}</Badge> <UrlCell url={asset.url} />
                {asset.detail ? <span className="cf-url-asset-detail">{asset.detail}</span> : null}
              </li>
            ))}
          </ul>
        )}
      </section>

      {/* ── SERP pixel preview (§15.6, bundled measurer) ──────────────── */}
      <section className="cf-url-detail-section" data-testid="url-detail-serp" aria-label="SERP preview">
        <h4>
          SERP preview
          <span className="cf-url-serp-devices" role="group" aria-label="SERP device profile">
            <button
              type="button"
              className={`cf-url-serp-device${device === 'desktop' ? ' cf-url-serp-device-active' : ''}`}
              aria-pressed={device === 'desktop'}
              onClick={() => setDevice('desktop')}
              data-testid="url-detail-serp-desktop"
            >
              Desktop
            </button>
            <button
              type="button"
              className={`cf-url-serp-device${device === 'mobile' ? ' cf-url-serp-device-active' : ''}`}
              aria-pressed={device === 'mobile'}
              onClick={() => setDevice('mobile')}
              data-testid="url-detail-serp-mobile"
            >
              Mobile
            </button>
          </span>
        </h4>
        <div className={`cf-url-serp-card cf-url-serp-${device}`}>
          <div className="cf-url-serp-display-url" data-testid="url-detail-serp-url">{detail.canonicalUrl}</div>
          <div className="cf-url-serp-title" data-testid="url-detail-serp-title">
            {serpTitle.length > 0 ? analysis.title.text : <span className="cf-cell-empty">No title</span>}
          </div>
          <div className="cf-url-serp-indicators">
            {analysis.title.truncated ? (
              <span data-testid="url-detail-serp-title-truncated"><Badge tone="warning">Title truncated</Badge></span>
            ) : (
              <span data-testid="url-detail-serp-title-fits"><Badge tone="success">Title fits</Badge></span>
            )}
            <span className="cf-url-serp-meter" data-testid="url-detail-serp-title-meter">
              {Math.round(titleFullWidth)} / {budget.titleMaxPx} px
            </span>
            {analysis.title.overflow ? (
              <span className="cf-url-serp-overflow" data-testid="url-detail-serp-title-overflow" title={analysis.title.overflow}>
                cut: “{analysis.title.overflow}”
              </span>
            ) : null}
          </div>
          <div className="cf-url-serp-description" data-testid="url-detail-serp-description">
            {serpDescription.length > 0
              ? analysis.description.lines.map((line, index) => <span key={index} className="cf-url-serp-line">{line.text}</span>)
              : <span className="cf-cell-empty">No meta description</span>}
          </div>
          <div className="cf-url-serp-indicators">
            {analysis.description.truncated ? (
              <span data-testid="url-detail-serp-description-truncated"><Badge tone="warning">Description truncated</Badge></span>
            ) : (
              <span data-testid="url-detail-serp-description-fits"><Badge tone="success">Description fits</Badge></span>
            )}
            <span className="cf-url-serp-meter" data-testid="url-detail-serp-description-meter">
              {Math.round(analysis.description.totalWidthPx)} / {budget.descriptionMaxPx} px · {analysis.description.lines.length}/{budget.descriptionLines} lines
            </span>
          </div>
        </div>
        {detail.serpMetrics ? (
          <p className="cf-url-serp-stored" data-testid="url-detail-serp-stored">
            Stored measurement: title {Math.round(detail.serpMetrics.titlePixelWidth)} px
            ({detail.serpMetrics.titleTruncated ? 'truncated' : 'fits'}), description {Math.round(detail.serpMetrics.descriptionPixelWidth)} px
            ({detail.serpMetrics.descriptionTruncated ? 'truncated' : 'fits'}).
          </p>
        ) : null}
      </section>

      {/* ── Findings ──────────────────────────────────────────────────── */}
      <section className="cf-url-detail-section" data-testid="url-detail-findings" aria-label="Findings">
        <h4>Findings ({findings.length})</h4>
        {findings.length === 0 ? (
          <p className="cf-url-detail-empty" data-testid="url-detail-findings-empty">No findings for this URL.</p>
        ) : (
          <ul className="cf-url-finding-list">
            {findings.map((finding) => (
              <li key={finding.id} className="cf-url-finding" data-testid="url-detail-finding" data-rule-key={finding.ruleKey}>
                <Badge tone={severityTone(finding.severity)}>{finding.severity}</Badge>{' '}
                <span className="cf-url-finding-rule">{finding.ruleKey}</span>{' '}
                <span className="cf-url-finding-message">{finding.message}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}
