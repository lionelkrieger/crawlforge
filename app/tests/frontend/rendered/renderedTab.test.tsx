// @vitest-environment jsdom
/**
 * Rendered tab component proofs (WP-10-03, UI-13; probe-2 G01 render-testing
 * inert → the tab now binds real rendered capture + dual-evidence diff).
 *
 * Acceptance stand-ins (phase gate drives these testids over CDP):
 *  - /pages/late-meta.html shows raw title 'Raw title before script' vs
 *    rendered 'Rendered title after script' + the canonical change + the
 *    critical rendered-only noindex (dual evidence);
 *  - /pages/js-content.html shows ~0 vs ~200 word counts;
 *  - the rendered DOM / screenshot / console / network waterfall viewers bind
 *    the captured artifacts; absent artifacts render honest empty states.
 * All data flows through the typed loopback client over real wire JSON.
 */

import { afterEach, describe, expect, it } from 'vitest';
import { RenderedTab } from '../../../src/frontend/src/surfaces/explorers/rendered/RenderedTab';
import { renderTool, fakeServices } from '../tools/helpers';
import {
  AUDIT,
  renderedClient,
  flush,
  byTestId,
  textOf,
  installDomStubs,
  makeArtifactLoader,
  lateMetaArtifacts,
  type Mounted
} from './helpers';

afterEach(() => {
  document.body.innerHTML = '';
});

/** Mounts the rendered tab over the fake wire. */
async function mountTab(options: {
  urlId?: string;
  api?: ReturnType<typeof renderedClient>['api'] | null;
  loadArtifacts?: ReturnType<typeof makeArtifactLoader>;
} = {}): Promise<{ view: Mounted; calls: ReturnType<typeof renderedClient>['calls'] }> {
  installDomStubs();
  const { api, calls } = renderedClient();
  const view = await renderTool(
    <RenderedTab
      api={options.api === undefined ? api : options.api}
      auditId={AUDIT}
      urlId={options.urlId ?? 'url-late-meta'}
      loadArtifacts={options.loadArtifacts}
    />,
    fakeServices().services
  );
  await flush(20);
  return { view, calls };
}

describe('RenderedTab — late-meta dual-evidence diff (UI-13 acceptance)', () => {
  it('shows the raw vs rendered title change', async () => {
    const { view } = await mountTab();
    expect(byTestId(view.container, 'rendered-tab')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-diff-title-raw'))).toBe('Raw title before script');
    expect(textOf(byTestId(view.container, 'rendered-diff-title-rendered'))).toBe('Rendered title after script');
  });

  it('shows the canonical change with dual evidence', async () => {
    const { view } = await mountTab();
    expect(textOf(byTestId(view.container, 'rendered-diff-canonical-raw'))).toBe('http://127.0.0.1:47311/pages/late-meta.html');
    expect(textOf(byTestId(view.container, 'rendered-diff-canonical-rendered'))).toBe('http://127.0.0.1:47311/pages/late-meta-other.html');
  });

  it('shows the critical rendered-only noindex with dual evidence + direction', async () => {
    const { view } = await mountTab();
    expect(textOf(byTestId(view.container, 'rendered-diff-noindex-raw'))).toBe('index');
    expect(textOf(byTestId(view.container, 'rendered-diff-noindex-rendered'))).toBe('noindex');
    expect(textOf(byTestId(view.container, 'rendered-diff-noindex-direction'))).toBe('rendered-only');
    expect(byTestId(view.container, 'rendered-diff-noindex-critical')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-diff-critical')).not.toBeNull();
  });

  it('badges the per-URL capture source', async () => {
    const { view } = await mountTab();
    const source = byTestId(view.container, 'rendered-tab-capture-source');
    expect(source?.getAttribute('data-source')).toBe('both');
  });
});

describe('RenderedTab — js-content word counts (UI-13 acceptance)', () => {
  it('shows ~0 raw vs ~200 rendered word counts', async () => {
    const { view } = await mountTab({ urlId: 'url-js-content' });
    expect(textOf(byTestId(view.container, 'rendered-diff-wordcount-raw'))).toBe('3');
    expect(textOf(byTestId(view.container, 'rendered-diff-wordcount-rendered'))).toBe('203');
    expect(textOf(byTestId(view.container, 'rendered-diff-wordcount-delta'))).toContain('+200');
  });
});

describe('RenderedTab — rendered artifact viewers', () => {
  it('binds the screenshot, rendered DOM, console log and network waterfall', async () => {
    const { view } = await mountTab({ loadArtifacts: makeArtifactLoader(lateMetaArtifacts()) });
    await flush(10);
    const img = byTestId(view.container, 'rendered-screenshot-img') as HTMLImageElement;
    expect(img.getAttribute('src')).toContain('data:image/png;base64,');
    expect(textOf(byTestId(view.container, 'rendered-dom-pre'))).toContain('Rendered title after script');
    expect(byTestId(view.container, 'rendered-console-row-0')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-console-text-0'))).toBe('late-meta script ran');
    expect(byTestId(view.container, 'rendered-waterfall-row-0')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-waterfall-row-2')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-waterfall-total'))).toContain('KB');
  });

  it('renders honest empty artifact states when nothing was captured (diff still works)', async () => {
    const { view } = await mountTab(); // no artifact loader
    expect(byTestId(view.container, 'rendered-screenshot-empty')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-dom-empty')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-console-empty')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-waterfall-empty')).not.toBeNull();
    // The diff is independent of artifacts and still renders.
    expect(textOf(byTestId(view.container, 'rendered-diff-title-rendered'))).toBe('Rendered title after script');
  });

  it('treats a null artifact load as baseline (no fabricated viewers)', async () => {
    const { view } = await mountTab({ loadArtifacts: makeArtifactLoader(null) });
    await flush(10);
    expect(byTestId(view.container, 'rendered-waterfall-empty')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-screenshot-empty')).not.toBeNull();
  });
});

describe('RenderedTab — empty / offline / error states', () => {
  it('shows no differences for a clean page', async () => {
    const { view } = await mountTab({ urlId: 'url-clean' });
    expect(byTestId(view.container, 'rendered-diff-empty')).not.toBeNull();
  });

  it('degrades offline without throwing', async () => {
    const { view } = await mountTab({ api: null });
    expect(byTestId(view.container, 'rendered-tab-offline')).not.toBeNull();
  });

  it('surfaces a detail load error honestly', async () => {
    installDomStubs();
    const { api } = renderedClient({ failWith: { code: 'internal', message: 'detail boom' } });
    const view = await renderTool(<RenderedTab api={api} auditId={AUDIT} urlId="url-late-meta" />, fakeServices().services);
    await flush(20);
    expect(textOf(byTestId(view.container, 'rendered-tab-error'))).toBe('detail boom');
  });
});
