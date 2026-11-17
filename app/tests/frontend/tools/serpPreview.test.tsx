// @vitest-environment jsdom
/**
 * Live SERP & Social Card preview UI tests (criterion #48): seeded render,
 * per-keystroke truncation verdicts, device budget swap, editable budgets
 * with reset, and the offline data:-only card image.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SerpPreview } from '../../../src/frontend/src/tools/serp/SerpPreview';
import { byTestId, click, fire, flush, offlineServices, renderTool, textOf, typeInto } from './helpers';
import type { Mounted } from '../shell/helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  localStorage.clear();
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

async function renderSerp() {
  const services = offlineServices();
  mounted = await renderTool(<SerpPreview />, services);
  await flush();
  return mounted.container;
}

function inputOf(container: HTMLElement, testId: string): HTMLInputElement {
  // Mantine 8 puts data-testid on the form control itself.
  return byTestId(container, testId) as HTMLInputElement;
}

describe('seeded render', () => {
  it('opens populated with the snippet and both social cards', async () => {
    const container = await renderSerp();
    expect(byTestId(container, 'tool-serp')).not.toBeNull();
    expect(byTestId(container, 'serp-snippet')).not.toBeNull();
    expect(byTestId(container, 'social-card-twitter')).not.toBeNull();
    expect(byTestId(container, 'social-card-facebook')).not.toBeNull();
    expect(inputOf(container, 'serp-title-input').value).toContain('CrawlForge');
    expect(textOf(byTestId(container, 'serp-url'))).toContain('crawlforge.local');
  });

  it('shows the default long title and description as truncated', async () => {
    const container = await renderSerp();
    expect(byTestId(container, 'serp-title-truncated')).not.toBeNull();
    expect(byTestId(container, 'serp-description-truncated')).not.toBeNull();
    expect(byTestId(container, 'serp-title-render-overflow')).not.toBeNull();
  });
});

describe('live truncation', () => {
  it('re-renders verdicts on every keystroke', async () => {
    const container = await renderSerp();
    typeInto(inputOf(container, 'serp-title-input'), 'Hi');
    await flush();
    expect(byTestId(container, 'serp-title-fits')).not.toBeNull();
    expect(byTestId(container, 'serp-title-truncated')).toBeNull();
    expect(textOf(byTestId(container, 'serp-title-render'))).toBe('Hi');

    typeInto(inputOf(container, 'serp-title-input'), 'X'.repeat(120));
    await flush();
    expect(byTestId(container, 'serp-title-truncated')).not.toBeNull();
    expect(byTestId(container, 'serp-title-render-overflow')).not.toBeNull();
  });

  it('updates the description verdict and social cards together', async () => {
    const container = await renderSerp();
    typeInto(inputOf(container, 'serp-description-input'), 'Short.');
    await flush();
    expect(byTestId(container, 'serp-description-fits')).not.toBeNull();
    expect(textOf(byTestId(container, 'social-description-verdict-twitter'))).toBe('Description fits');
    expect(textOf(byTestId(container, 'social-description-verdict-facebook'))).toBe('Description fits');
  });

  it('reports used/budget pixels in the meters', async () => {
    const container = await renderSerp();
    expect(textOf(byTestId(container, 'serp-title-meter'))).toContain('/ 580 px');
    expect(textOf(byTestId(container, 'serp-description-meter'))).toContain('/ 920 px');
  });
});

describe('device + budgets', () => {
  it('swaps to the narrower mobile budget set', async () => {
    const container = await renderSerp();
    const mobile = byTestId(container, 'serp-device')!.querySelector('input[value="mobile"]')!;
    fire(mobile, 'click');
    await flush();
    expect(textOf(byTestId(container, 'serp-title-meter'))).toContain('/ 400 px');
    expect(textOf(byTestId(container, 'serp-description-meter'))).toContain('/ 600 px');
  });

  it('edits a budget live and resets to defaults', async () => {
    const container = await renderSerp();
    typeInto(inputOf(container, 'serp-budget-title'), '300');
    await flush();
    expect(textOf(byTestId(container, 'serp-title-meter'))).toContain('/ 300 px');
    click(byTestId(container, 'serp-budget-reset')!);
    await flush();
    expect(textOf(byTestId(container, 'serp-title-meter'))).toContain('/ 580 px');
  });
});

describe('offline card image', () => {
  it('accepts a data: URL and rejects remote images', async () => {
    const container = await renderSerp();
    const imageInput = inputOf(container, 'serp-image-input');
    typeInto(imageInput, 'data:image/png;base64,AAA');
    await flush();
    expect(byTestId(container, 'social-card-twitter')!.querySelector('img')).not.toBeNull();

    typeInto(imageInput, 'https://evil.example/x.png');
    await flush();
    expect(byTestId(container, 'social-card-twitter')!.querySelector('img')).toBeNull();
    expect(textOf(byTestId(container, 'social-card-twitter'))).toContain('og:image (data: URL)');
  });
});