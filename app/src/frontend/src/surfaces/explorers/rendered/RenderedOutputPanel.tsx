/**
 * Real rendered output panel (WP-10-03, UI-18 single-page analyzer + UI-19
 * render tester; FS-CRAWL-001 §8.7, CAP-G-X01 / CAP-A-004 / CAP-C-086).
 *
 * Binds the LOCKED `RenderTestResponse` wire shape (`contracts/api/tools`)
 * produced by the new direct-CDP rendering pool (WP-10-01): screenshot,
 * post-render DOM, console messages, network waterfall and the post-render WAF
 * verdict. This is the component that makes the render tester show REAL
 * rendered output (probe-2 G01: render testing was inert because the pool was
 * inert — the pool is now live and this panel binds its full output).
 *
 * `networkWaterfall` is additive (WP-10-A1): absent/null means a baseline
 * render that captured no waterfall, rendered as an honest empty state.
 *
 * Reuse: the single-page analyzer mounts this directly (owned path); the
 * standalone render tester (`tools/RenderTesterTool`, coordinator-owned) mounts
 * it via the wiring handoff.
 *
 * @module surfaces/explorers/rendered/RenderedOutputPanel
 */

import { Badge, Group, Stack } from '@mantine/core';
import type { RenderTestResponse } from '../../../contracts/api/tools';
import { ScreenshotViewer } from './ScreenshotViewer';
import { ConsoleLogViewer } from './ConsoleLogViewer';
import { NetworkWaterfallViewer } from './NetworkWaterfallViewer';
import { RenderedDomViewer } from './RenderedDomViewer';

/** Props for the rendered output panel. */
export interface RenderedOutputPanelProps {
  /** The locked render-test response (real CDP pool output). */
  readonly result: RenderTestResponse;
  /** Test id root (default `rendered-output`). */
  readonly testId?: string;
  /** Show the post-render DOM source block (default true). */
  readonly showDom?: boolean;
}

/** The full real rendered output for one render-test call. */
export function RenderedOutputPanel({ result, testId = 'rendered-output', showDom = true }: RenderedOutputPanelProps) {
  const waf = result.wafVerdict ?? null;
  const waterfall = result.networkWaterfall ?? null;
  return (
    <Stack gap="sm" data-testid={testId}>
      <Group gap={6} wrap="wrap">
        {waf ? (
          <Badge
            size="sm"
            variant="light"
            color="yellow"
            data-testid={`${testId}-waf`}
            data-detected="true"
            data-provider={waf.provider}
          >
            WAF: {waf.provider || 'unknown'} ({waf.challengeClass})
          </Badge>
        ) : (
          <Badge size="sm" variant="light" color="green" data-testid={`${testId}-waf`} data-detected="false">
            No WAF detected
          </Badge>
        )}
        <Badge size="sm" variant="outline" color="steel" data-testid={`${testId}-waterfall-count`}>
          {waterfall ? `${waterfall.length} network request${waterfall.length === 1 ? '' : 's'}` : 'No waterfall (baseline)'}
        </Badge>
        <Badge size="sm" variant="outline" color="steel" data-testid={`${testId}-console-count`}>
          {result.consoleMessages.length} console message{result.consoleMessages.length === 1 ? '' : 's'}
        </Badge>
      </Group>

      <ScreenshotViewer screenshotBase64={result.screenshotBase64} testId={`${testId}-screenshot`} />
      <NetworkWaterfallViewer entries={waterfall} testId={`${testId}-waterfall`} />
      <ConsoleLogViewer messages={result.consoleMessages} testId={`${testId}-console`} />
      {showDom ? <RenderedDomViewer html={result.renderedHtml} testId={`${testId}-dom`} /> : null}
    </Stack>
  );
}
