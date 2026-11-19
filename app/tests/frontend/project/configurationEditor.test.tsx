// @vitest-environment jsdom
/**
 * ConfigurationEditor component tests (WP-09-07, UI-04): 13 verbatim tabs,
 * default Scope tab >3 inputs, cookie-jar toggle bound to the engine DTO
 * field, unlimited-by-default limits with explicit never-clamped override,
 * AIMD validation, disabled-with-note later-phase tabs, and the real REST
 * DTO round-trip.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useState } from 'react';
import { ConfigurationEditor } from '../../../src/frontend/src/surfaces/project/ConfigurationEditor';
import {
  CONFIGURATION_TABS,
  TAB_IDS,
  configurationFromWire,
  configurationToCreateRequest,
  configurationToUpdateRequest,
  defaultConfigurationForm,
  type ConfigurationFormValue
} from '../../../src/frontend/src/surfaces/project/configurationModel';
import {
  byTestId,
  byTestIdAll,
  click,
  clickTab,
  configurationWireFixture,
  flush,
  renderProject,
  textOf,
  typeInto,
  waitFor,
  type Mounted
} from './helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

/** Controlled harness capturing the latest emitted form value. */
function Harness({ initial, latest }: { initial: ConfigurationFormValue; latest: { current: ConfigurationFormValue } }) {
  const [value, setValue] = useState(initial);
  return (
    <ConfigurationEditor
      value={value}
      onChange={(next) => {
        latest.current = next;
        setValue(next);
      }}
    />
  );
}

async function renderEditor(initial: ConfigurationFormValue = defaultConfigurationForm('http://127.0.0.1:47311/')) {
  const latest = { current: initial };
  mounted = await renderProject(<Harness initial={initial} latest={latest} />);
  await flush();
  return { container: mounted.container, latest };
}

describe('tab contract (walker UI-04)', () => {
  it('renders all 13 tab labels VERBATIM', async () => {
    const { container } = await renderEditor();
    const tabs = Array.from(container.querySelectorAll('[role="tab"]')).map((tab) => textOf(tab));
    expect(tabs).toEqual([...CONFIGURATION_TABS]);
    expect(tabs).toHaveLength(13);
  });

  it('default tab is Scope and renders >3 inputs', async () => {
    const { container } = await renderEditor();
    const panel = byTestId(container, 'config-panel-scope')!;
    expect(panel).not.toBeNull();
    const inputs = panel.querySelectorAll('input,textarea,select');
    expect(inputs.length).toBeGreaterThan(3);
    // Real, enabled controls — not placeholders.
    const enabled = Array.from(inputs).filter((input) => !(input as HTMLInputElement).disabled);
    expect(enabled.length).toBeGreaterThan(3);
  });

  it('every tab opens its panel', async () => {
    const { container } = await renderEditor();
    for (let index = 0; index < CONFIGURATION_TABS.length; index += 1) {
      await clickTab(container, CONFIGURATION_TABS[index]);
      expect(byTestId(container, `config-panel-${TAB_IDS[index]}`)).not.toBeNull();
    }
  });
});

describe('cookie-jar toggle (criterion #56)', () => {
  it('binds to scopeRules.cookieJar.enabled and flips both ways', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Authentication and cookies');
    const toggle = byTestId(container, 'config-cookie-jar') as HTMLInputElement;
    expect(toggle.checked).toBe(true); // engine baseline: jar ON
    click(toggle);
    await flush();
    expect(latest.current.scopeRules.cookieJar.enabled).toBe(false);
    expect((byTestId(container, 'config-cookie-jar') as HTMLInputElement).checked).toBe(false);
    click(byTestId(container, 'config-cookie-jar')!);
    await flush();
    expect(latest.current.scopeRules.cookieJar.enabled).toBe(true);
  });

  it('user cookies and custom headers edit the engine documents', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Authentication and cookies');
    click(byTestId(container, 'config-user-cookies-add')!);
    await flush();
    typeInto(byTestId(container, 'config-user-cookies-name-0') as HTMLInputElement, 'session');
    typeInto(byTestId(container, 'config-user-cookies-value-0') as HTMLInputElement, 'abc123');
    await flush();
    expect(latest.current.scopeRules.cookies).toEqual({ session: 'abc123' });
    click(byTestId(container, 'config-custom-headers-add')!);
    await flush();
    typeInto(byTestId(container, 'config-custom-headers-name-0') as HTMLInputElement, 'X-Test');
    typeInto(byTestId(container, 'config-custom-headers-value-0') as HTMLInputElement, '1');
    await flush();
    expect(latest.current.scopeRules.headers).toEqual({ 'X-Test': '1' });
  });
});

describe('limits tab', () => {
  it('is unlimited-by-default with the null sentinel', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Limits');
    for (const key of ['max-urls', 'max-depth', 'max-duration', 'max-response-bytes']) {
      const unlimited = byTestId(container, `config-limit-${key}-unlimited`) as HTMLInputElement;
      expect(unlimited.checked).toBe(true);
      expect((byTestId(container, `config-limit-${key}-override`) as HTMLInputElement).disabled).toBe(true);
    }
    expect(latest.current.limits).toEqual({ maxUrls: null, maxDepth: null, maxDurationSeconds: null, maxResponseBytes: null });
  });

  it('explicit override replaces the sentinel and is never clamped', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Limits');
    click(byTestId(container, 'config-limit-max-urls-unlimited')!);
    await flush();
    const override = byTestId(container, 'config-limit-max-urls-override') as HTMLInputElement;
    expect(override.disabled).toBe(false);
    expect(latest.current.limits.maxUrls).toBe(1000); // suggested starting override
    typeInto(override, '500');
    await flush();
    expect(latest.current.limits.maxUrls).toBe(500);
    typeInto(override, '999999999');
    await flush();
    expect(latest.current.limits.maxUrls).toBe(999999999); // never clamped
    typeInto(override, '');
    await flush();
    expect(latest.current.limits.maxUrls).toBeNull(); // back to the unlimited sentinel
    expect((byTestId(container, 'config-limit-max-urls-unlimited') as HTMLInputElement).checked).toBe(true);
  });

  it('validates AIMD parameters client-side (03/01 §4.3 mirror)', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Limits');
    typeInto(byTestId(container, 'config-aimd-beta') as HTMLInputElement, '1.5');
    await flush();
    expect(latest.current.aimd.beta).toBe(1.5);
    expect(container.textContent).toContain('beta must be in (0, 1)');
    typeInto(byTestId(container, 'config-aimd-beta') as HTMLInputElement, '0.5');
    await flush();
    expect(container.textContent).not.toContain('beta must be in (0, 1)');
  });

  it('renders the AIMD parameter group with the wire defaults', async () => {
    const { container } = await renderEditor();
    await clickTab(container, 'Limits');
    expect((byTestId(container, 'config-aimd-r0') as HTMLInputElement).value).toBe('2');
    expect((byTestId(container, 'config-aimd-kappa') as HTMLInputElement).value).toBe('4');
    expect((byTestId(container, 'config-aimd-ewma') as HTMLInputElement).value).toBe('0.2');
  });
});

describe('later-phase controls (CR-022 disabled-with-note)', () => {
  it('click discovery and site types are disabled with a phase-11 note', async () => {
    const { container } = await renderEditor();
    await clickTab(container, 'Click discovery and site types');
    expect(byTestId(container, 'config-click-discovery-section-note')!.textContent).toContain('engine lands in phase 11');
    expect((byTestId(container, 'config-click-inventory') as HTMLInputElement).disabled).toBe(true);
    expect((byTestId(container, 'config-site-hash-routes') as HTMLInputElement).disabled).toBe(true);
  });

  it('consent states are disabled with a phase-11 note', async () => {
    const { container } = await renderEditor();
    await clickTab(container, 'Consent states');
    expect(byTestId(container, 'config-consent-section-note')!.textContent).toContain('engine lands in phase 11');
    expect((byTestId(container, 'config-consent-mode-v2') as HTMLInputElement).disabled).toBe(true);
  });

  it('thresholds and extraction are disabled with phase-12 notes', async () => {
    const { container } = await renderEditor();
    await clickTab(container, 'Thresholds');
    expect(byTestId(container, 'config-thresholds-section-note')!.textContent).toContain('engine lands in phase 12');
    expect((byTestId(container, 'config-threshold-error') as HTMLInputElement).disabled).toBe(true);
    await clickTab(container, 'Extraction and search');
    expect(byTestId(container, 'config-extraction-section-note')!.textContent).toContain('engine lands in phase 12');
    expect((byTestId(container, 'config-extraction-rules') as HTMLTextAreaElement).disabled).toBe(true);
  });

  it('rendering controls are disabled with a phase-10 note while UA controls work', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Crawl as and capture');
    expect(byTestId(container, 'config-rendering-section-note')!.textContent).toContain('engine lands in phase 10');
    expect((byTestId(container, 'config-rendering-enabled') as HTMLInputElement).disabled).toBe(true);
    // UA controls are engine-delivered and functional.
    const simulate = byTestId(container, 'config-simulate-googlebot') as HTMLInputElement;
    expect(simulate.disabled).toBe(false);
    click(simulate);
    await flush();
    expect(latest.current.scopeRules.userAgent.simulateGooglebot).toBe(true);
  });
});

describe('scope + directives + categories bindings', () => {
  it('edits start URLs and include patterns', async () => {
    const { container, latest } = await renderEditor();
    typeInto(byTestId(container, 'config-start-urls') as HTMLTextAreaElement, 'https://a.example/\nhttps://b.example/');
    await flush();
    expect(latest.current.startUrls).toEqual(['https://a.example/', 'https://b.example/']);
    typeInto(byTestId(container, 'config-include-patterns') as HTMLTextAreaElement, '/products/.*');
    await flush();
    expect(latest.current.scopeRules.scope.includePatterns).toEqual(['/products/.*']);
  });

  it('directive toggles bind to the engine document with Googlebot defaults', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Directives');
    expect((byTestId(container, 'config-crawl-noindex') as HTMLInputElement).checked).toBe(true);
    expect((byTestId(container, 'config-follow-link-nofollow') as HTMLInputElement).checked).toBe(false);
    click(byTestId(container, 'config-follow-link-nofollow')!);
    await flush();
    expect(latest.current.scopeRules.directives.followLinkNofollow).toBe(true);
  });

  it('analysis categories toggle per configuration with the null all-enabled convention', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Analysis categories');
    expect(latest.current.enabledCategories).toBeNull();
    expect((byTestId(container, 'config-category-Content') as HTMLInputElement).checked).toBe(true);
    // WP-10-A1 (D-021): the three additive §7.4 categories render checked by
    // default under the same null = all-enabled convention.
    for (const category of ['Images', 'SecurityAndProtocol', 'Internationalization']) {
      expect((byTestId(container, `config-category-${category}`) as HTMLInputElement).checked).toBe(true);
    }
    click(byTestId(container, 'config-category-Content')!);
    await flush();
    expect(latest.current.enabledCategories).not.toBeNull();
    expect(latest.current.enabledCategories).not.toContain('Content');
    expect(latest.current.enabledCategories).toHaveLength(13);
    click(byTestId(container, 'config-category-Content')!);
    await flush();
    expect(latest.current.enabledCategories).toBeNull();
  });
});

describe('real REST DTO round-trip (producer → consumer)', () => {
  it('loads the Api wire shape and emits it back with the cookie flip', async () => {
    const form = configurationFromWire(configurationWireFixture);
    const { container, latest } = await renderEditor(form);
    // Wire fields surface in the form.
    expect((byTestId(container, 'config-name') as HTMLInputElement).value).toBe('Full crawl');
    expect((byTestId(container, 'config-start-urls') as HTMLTextAreaElement).value).toBe('http://127.0.0.1:47311/');
    await clickTab(container, 'Limits');
    expect((byTestId(container, 'config-limit-max-urls-unlimited') as HTMLInputElement).checked).toBe(false);
    expect((byTestId(container, 'config-limit-max-urls-override') as HTMLInputElement).value).toBe('2');
    // Flip the cookie jar and emit the update payload.
    await clickTab(container, 'Authentication and cookies');
    click(byTestId(container, 'config-cookie-jar')!);
    await flush();
    const payload = configurationToUpdateRequest(latest.current);
    expect(payload.scopeRules.cookieJar.enabled).toBe(false);
    expect(payload.limits).toEqual({ maxUrls: 2, maxDepth: 1, maxDurationSeconds: 30, maxResponseBytes: null });
    expect(payload.aimd).toEqual(configurationWireFixture.aimd);
    expect(payload.name).toBe('Full crawl');
    expect(payload.startUrls).toEqual(['http://127.0.0.1:47311/']);
    // JSON survives the wire (camelCase, additive members present).
    const parsed = JSON.parse(JSON.stringify(payload)) as Record<string, unknown>;
    expect((parsed.scopeRules as Record<string, unknown>).cookieJar).toEqual({ enabled: false });
    expect(parsed.enabledCategories).toBeNull();
  });

  it('create payload carries the engine baselines for a fresh configuration', async () => {
    const { container, latest } = await renderEditor();
    await clickTab(container, 'Authentication and cookies');
    await waitFor(() => byTestId(container, 'config-cookie-jar') !== null);
    const payload = configurationToCreateRequest(latest.current);
    expect(payload.scopeRules.cookieJar.enabled).toBe(true);
    expect(payload.scopeRules.directives.crawlNoindexPages).toBe(true);
    expect(payload.scopeRules.canonicalization.trailingSlash).toBe('preserve');
    expect(payload.limits.maxUrls).toBeNull();
    expect(payload.startUrls).toEqual(['http://127.0.0.1:47311/']);
    expect(byTestIdAll(container, 'config-tab-scope').length).toBe(1);
  });
});
