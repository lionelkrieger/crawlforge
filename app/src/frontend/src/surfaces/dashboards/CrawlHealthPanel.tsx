/**
 * Crawl-health panel (FS-CRAWL-001 §8.2, FS-CRAWL-002 §3.7/§2.7): host
 * throttle timelines (AIMD adjustment history), current rate vs baseline,
 * WAF provider breakdown and block-rate gauge. Aggregates only; provider
 * clicks cross-filter the WAF block list / URL explorer.
 *
 * @module surfaces/dashboards/CrawlHealthPanel
 */

import type { HostState } from '../../contracts/api/hosts';
import type { WafBlocksSummary } from '../../contracts/api/waf';
import type { ChartRendererFactory } from '../../charts/chartPorts';
import type { ChartSelection } from '../../charts/crossFilter';
import { EChart } from '../../charts/EChart';
import {
  hostRateBarOption,
  hostThrottleTimelineOption,
  wafBlockRateGaugeOption,
  wafProviderOption
} from '../../charts/chartTransforms';

/** Crawl-health panel props. */
export interface CrawlHealthPanelProps {
  readonly hosts: readonly HostState[] | null;
  readonly waf: WafBlocksSummary | null;
  readonly factory?: ChartRendererFactory;
  readonly onCrossFilter?: (selection: ChartSelection) => void;
}

/** Host throttle + WAF health charts. */
export function CrawlHealthPanel({ hosts, waf, factory, onCrossFilter }: CrawlHealthPanelProps) {
  return (
    <div data-testid="cf-crawl-health-panel" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
      {hosts && hosts.length > 0 ? (
        <>
          <EChart
            factory={factory}
            option={hostThrottleTimelineOption(hosts)}
            ariaLabel="Host throttle timelines"
            exportName="host-throttle-timelines"
            height={300}
          />
          <EChart
            factory={factory}
            option={hostRateBarOption(hosts)}
            ariaLabel="Host rates versus baseline"
            exportName="host-rates"
            height={300}
          />
        </>
      ) : (
        <div data-testid="cf-hosts-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
          No host throttle state — hosts appear once the crawl starts.
        </div>
      )}

      {waf ? (
        <>
          <EChart
            factory={factory}
            option={wafProviderOption(waf)}
            ariaLabel="WAF blocks by provider"
            exportName="waf-providers"
            height={300}
            onChartClick={(params) => onCrossFilter?.({ kind: 'waf-provider', provider: params.name })}
          />
          <EChart
            factory={factory}
            option={wafBlockRateGaugeOption(waf.blockRate)}
            ariaLabel="WAF block rate"
            exportName="waf-block-rate"
            height={300}
          />
        </>
      ) : (
        <div data-testid="cf-waf-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
          No WAF detections recorded for this audit.
        </div>
      )}
    </div>
  );
}
