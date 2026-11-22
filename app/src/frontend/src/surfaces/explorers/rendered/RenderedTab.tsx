/**
 * Rendered tab (WP-10-03, UI-13 URL detail rendered tab; FS-CRAWL-001 §5.6.2 /
 * §7.18 / §8.2, CAP-A-004 / CAP-A-035 / CAP-A-X09 / CAP-A-X10 / CAP-C-086).
 *
 * The URL-detail tab that shows the rendered capture for one page:
 *  - the raw-vs-rendered DIFF (title / canonical / noindex / word count + every
 *    other delta field) with dual evidence, read from the stored `render.*`
 *    findings on the locked `audits.getUrl` detail wire;
 *  - the rendered DOM viewer, screenshot, console log and network waterfall,
 *    bound to the per-URL rendered artifacts through the injected
 *    `RenderedArtifactLoader` (the frozen REST catalog has no artifact read
 *    route yet — coordinator wiring handoff). When no loader/artifacts are
 *    present the viewers render honest "not captured" states; the diff still
 *    works from finding evidence, which is reachable today.
 *
 * Mounting (a tab inside the URL detail surface) is coordinator wiring; this
 * component is the exact export with props + testids documented in the handoff.
 *
 * @module surfaces/explorers/rendered/RenderedTab
 */

import { useEffect, useMemo, useState } from 'react';
import { Group, Loader, Paper, Stack, Text } from '@mantine/core';
import type { LoopbackApiClient } from '../../../contracts/api/client';
import type { UrlDetail } from '../../../contracts/api/audits';
import type { ApiEnvelope } from '../../../contracts/common/envelope';
import { extractRenderedDiff } from './renderedDiff';
import { RenderedDiffPanel } from './RenderedDiffPanel';
import { ScreenshotViewer } from './ScreenshotViewer';
import { RenderedDomViewer } from './RenderedDomViewer';
import { ConsoleLogViewer } from './ConsoleLogViewer';
import { NetworkWaterfallViewer } from './NetworkWaterfallViewer';
import { CaptureModeIndicator } from './CaptureModeIndicator';
import { EMPTY_RENDERED_ARTIFACTS, type RenderedArtifactLoader, type RenderedUrlArtifacts } from './renderedArtifacts';

/** Props for the rendered tab. */
export interface RenderedTabProps {
  readonly api: LoopbackApiClient | null;
  readonly auditId: string | null;
  readonly urlId: string | null;
  /** Optional artifact loader (coordinator wires the artifact endpoint). */
  readonly loadArtifacts?: RenderedArtifactLoader;
  readonly height?: number;
}

/** Section heading. */
function Heading({ children }: { children: string }) {
  return (
    <Text size="xs" fw={700} tt="uppercase" lts={0.6} c="dimmed">
      {children}
    </Text>
  );
}

/** The UI-13 rendered tab. */
export function RenderedTab({ api, auditId, urlId, loadArtifacts }: RenderedTabProps) {
  const [detail, setDetail] = useState<UrlDetail | null>(null);
  const [artifacts, setArtifacts] = useState<RenderedUrlArtifacts | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!api || !auditId || !urlId) {
      setDetail(null);
      setArtifacts(null);
      setError(null);
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    (async () => {
      try {
        const envelope = (await api.request('audits.getUrl', {
          params: { id: auditId, urlId },
          signal: controller.signal
        })) as ApiEnvelope<UrlDetail>;
        if (controller.signal.aborted) return;
        if (envelope.error || !envelope.data) {
          setError(envelope.error?.message ?? 'URL detail unavailable');
          setDetail(null);
        } else {
          setDetail(envelope.data);
        }
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : 'Backend unreachable');
          setDetail(null);
        }
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    })();
    return () => controller.abort();
  }, [api, auditId, urlId]);

  useEffect(() => {
    if (!loadArtifacts || !auditId || !urlId) {
      setArtifacts(null);
      return;
    }
    const controller = new AbortController();
    (async () => {
      try {
        const loaded = await loadArtifacts(auditId, urlId, controller.signal);
        if (!controller.signal.aborted) setArtifacts(loaded ?? EMPTY_RENDERED_ARTIFACTS);
      } catch {
        if (!controller.signal.aborted) setArtifacts(EMPTY_RENDERED_ARTIFACTS);
      }
    })();
    return () => controller.abort();
  }, [loadArtifacts, auditId, urlId]);

  const diff = useMemo(() => extractRenderedDiff(detail?.findings ?? []), [detail]);
  const arts = artifacts ?? EMPTY_RENDERED_ARTIFACTS;

  if (!api) {
    return (
      <Text size="sm" c="dimmed" data-testid="rendered-tab-offline">
        Backend disconnected — the rendered tab is unavailable offline.
      </Text>
    );
  }
  if (!auditId || !urlId) {
    return (
      <Text size="sm" c="dimmed" data-testid="rendered-tab-empty">
        Select a URL to inspect its rendered capture.
      </Text>
    );
  }
  if (loading) {
    return (
      <Group gap="xs" data-testid="rendered-tab-loading">
        <Loader size="xs" /> <Text size="sm">Loading rendered capture…</Text>
      </Group>
    );
  }
  if (error) {
    return (
      <Text size="sm" c="red" data-testid="rendered-tab-error">
        {error}
      </Text>
    );
  }

  return (
    <Stack gap="sm" data-testid="rendered-tab" data-url-id={urlId} style={{ overflowY: 'auto' }}>
      <Group justify="space-between" wrap="nowrap">
        <Heading>Rendered capture</Heading>
        <CaptureModeIndicator captureSource={detail?.captureSource ?? null} testId="rendered-tab-capture" />
      </Group>

      <RenderedDiffPanel diff={diff} testId="rendered-diff" />

      <Paper withBorder p="sm">
        <Stack gap="sm">
          <Heading>Screenshot</Heading>
          <ScreenshotViewer screenshotBase64={arts.screenshotBase64} testId="rendered-screenshot" />
          <Heading>Rendered DOM</Heading>
          <RenderedDomViewer html={arts.renderedDom} testId="rendered-dom" />
          <Heading>Console log</Heading>
          <ConsoleLogViewer messages={arts.consoleMessages} testId="rendered-console" />
          <Heading>Network waterfall</Heading>
          <NetworkWaterfallViewer entries={arts.networkWaterfall} testId="rendered-waterfall" />
        </Stack>
      </Paper>
    </Stack>
  );
}
