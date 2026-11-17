// @vitest-environment jsdom
/**
 * Visual selector picker tests (criterion #42). Two layers:
 *  - static sandbox posture: the production iframe attribute + srcdoc
 *    construction are asserted directly (jsdom does not load srcdoc, so the
 *    sandbox contract is proven statically, per the orchestrator directive);
 *  - interaction: the inline mode exercises the identical delegated
 *    capture/send path in-process.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  PICKER_SANDBOX,
  SelectorPicker,
  buildSandboxSrcDoc
} from '../../../src/frontend/src/tools/picker/SelectorPicker';
import { ExtractionDraftsPanel, draftsToJson } from '../../../src/frontend/src/tools/picker/ExtractionDrafts';
import { ARTIFACT_HTML, byTestId, click, fire, flush, offlineServices, renderTool, textOf, typeInto } from './helpers';
import type { Mounted } from '../shell/helpers';

const MALICIOUS_HTML = `<html><body>
  <script>window.steal()</script>
  <img src="https://evil.example/a.png">
  <p onclick="exfil()">safe text</p>
  <a href="https://evil.example/phish">link</a>
</body></html>`;

let mounted: Mounted | null = null;

beforeEach(() => {
  localStorage.clear();
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.restoreAllMocks();
});

describe('production sandbox posture (static)', () => {
  it('locks the iframe to same-origin only — no scripts, forms, popups, or top-navigation', () => {
    expect(PICKER_SANDBOX).toBe('allow-same-origin');
    for (const forbidden of ['allow-scripts', 'allow-forms', 'allow-popups', 'allow-top-navigation', 'allow-modals']) {
      expect(PICKER_SANDBOX).not.toContain(forbidden);
    }
  });

  it('builds srcdoc from the sanitized artifact only — never a remote URL', () => {
    const srcdoc = buildSandboxSrcDoc(MALICIOUS_HTML);
    expect(srcdoc).not.toContain('<script');
    expect(srcdoc).not.toContain('onclick');
    // No LOADABLE remote reference survives (src/href/action are defanged);
    // the original URL is kept only as inert data-cf-href provenance. The
    // space boundary avoids matching the inert data-cf-href attribute.
    expect(srcdoc).not.toMatch(/\ssrc="https:/);
    expect(srcdoc).not.toMatch(/\shref="https:/);
    expect(srcdoc).toContain('href="#"');
    expect(srcdoc).toContain('data-cf-href="https://evil.example/phish"');
    expect(srcdoc).toContain('safe text');
  });

  it('renders the iframe with the locked sandbox attribute and inert srcdoc', async () => {
    const services = offlineServices();
    mounted = await renderTool(<SelectorPicker html={MALICIOUS_HTML} mode="iframe" testId="picker" />, services);
    const iframe = byTestId(mounted.container, 'picker-iframe') as HTMLIFrameElement;
    expect(iframe).not.toBeNull();
    expect(iframe.getAttribute('sandbox')).toBe(PICKER_SANDBOX);
    expect(iframe.getAttribute('srcdoc')).toBe(buildSandboxSrcDoc(MALICIOUS_HTML));
    expect(iframe.hasAttribute('src')).toBe(false); // loads ONLY the stored artifact
    expect(iframe.getAttribute('title')).toBe('Sandboxed rendered artifact');
    expect(textOf(byTestId(mounted.container, 'picker-mode'))).toContain('Sandboxed iframe');
  });
});

describe('inline interaction path', () => {
  async function renderPicker(callbacks: {
    onSendToExtraction?: (selector: string) => void;
    onSendToScope?: (selector: string, list: 'include' | 'exclude') => void;
    onCapture?: (capture: unknown) => void;
  } = {}) {
    const services = offlineServices();
    mounted = await renderTool(
      <SelectorPicker html={ARTIFACT_HTML} mode="inline" testId="picker" {...callbacks} />,
      services
    );
    await flush();
    return mounted.container;
  }

  it('captures a clicked element with CSS + XPath + match counts', async () => {
    const onCapture = vi.fn();
    const container = await renderPicker({ onCapture });
    const h2 = byTestId(container, 'picker-inline')!.querySelector('h2.post-title')!;
    fire(h2, 'click');
    await flush();

    expect(byTestId(container, 'picker-capture')).not.toBeNull();
    expect(textOf(byTestId(container, 'picker-tag'))).toContain('h2');
    expect(textOf(byTestId(container, 'picker-match-count'))).toBe('1 CSS match');
    expect(textOf(byTestId(container, 'picker-xpath-count'))).toBe('1 XPath match');
    expect(textOf(byTestId(container, 'picker-css-pre')).length).toBeGreaterThan(0);
    expect(textOf(byTestId(container, 'picker-xpath-pre'))).toContain('./');
    expect(onCapture).toHaveBeenCalledTimes(1);
    expect(h2.getAttribute('data-cf-picker-selected')).toBe('true');
  });

  it('sends the captured selector to extraction and scope lists', async () => {
    const onSendToExtraction = vi.fn();
    const onSendToScope = vi.fn();
    const container = await renderPicker({ onSendToExtraction, onSendToScope });
    fire(byTestId(container, 'picker-inline')!.querySelector('p.lead')!, 'click');
    await flush();
    const css = textOf(byTestId(container, 'picker-css-pre'));

    click(byTestId(container, 'picker-send-extraction')!);
    expect(onSendToExtraction).toHaveBeenCalledWith(css);
    click(byTestId(container, 'picker-send-include')!);
    expect(onSendToScope).toHaveBeenLastCalledWith(css, 'include');
    click(byTestId(container, 'picker-send-exclude')!);
    expect(onSendToScope).toHaveBeenLastCalledWith(css, 'exclude');
  });

  it('previews manual selector match counts live and flags invalid syntax', async () => {
    const container = await renderPicker();
    const manual = byTestId(container, 'picker-manual') as HTMLInputElement;
    typeInto(manual, 'p');
    await flush();
    expect(textOf(byTestId(container, 'picker-manual-count'))).toBe('2 matches');
    typeInto(manual, 'p[');
    await flush();
    expect(textOf(byTestId(container, 'picker-manual-count'))).toBe('invalid');
  });

  it('applies a manual selector as a capture', async () => {
    const container = await renderPicker();
    const manual = byTestId(container, 'picker-manual') as HTMLInputElement;
    typeInto(manual, 'aside.sidebar');
    click(byTestId(container, 'picker-manual-apply')!);
    await flush();
    expect(textOf(byTestId(container, 'picker-tag'))).toContain('aside');
    expect(textOf(byTestId(container, 'picker-css-pre'))).toBe('aside.sidebar');
  });

  it('marks hover state on pointer-over', async () => {
    const container = await renderPicker();
    const aside = byTestId(container, 'picker-inline')!.querySelector('aside.sidebar')!;
    fire(aside, 'mouseover');
    expect(aside.getAttribute('data-cf-picker-hover')).toBe('true');
    fire(aside, 'mouseout');
    expect(aside.hasAttribute('data-cf-picker-hover')).toBe(false);
  });
});

describe('ExtractionDraftsPanel', () => {
  it('stages editable rule drafts and emits configuration JSON', async () => {
    const onChange = vi.fn();
    const services = offlineServices();
    const drafts = [{ ruleKey: 'custom.rule-1', selector: 'h2.post-title' }];
    mounted = await renderTool(<ExtractionDraftsPanel drafts={drafts} onChange={onChange} testId="drafts" />, services);
    expect(textOf(byTestId(mounted.container, 'drafts-json-pre'))).toBe(draftsToJson(drafts));
    expect(JSON.parse(draftsToJson(drafts))).toEqual([{ ruleKey: 'custom.rule-1', selector: 'h2.post-title', valueType: 'text' }]);

    const keyInput = byTestId(mounted.container, 'drafts-key-0') as HTMLInputElement;
    typeInto(keyInput, 'seo.title');
    expect(onChange).toHaveBeenCalledWith([{ ruleKey: 'seo.title', selector: 'h2.post-title' }]);

    click(byTestId(mounted.container, 'drafts-clear')!);
    expect(onChange).toHaveBeenLastCalledWith([]);
  });

  it('renders nothing when there are no drafts', async () => {
    const services = offlineServices();
    mounted = await renderTool(<ExtractionDraftsPanel drafts={[]} onChange={() => {}} testId="drafts" />, services);
    expect(byTestId(mounted.container, 'drafts')).toBeNull();
  });
});