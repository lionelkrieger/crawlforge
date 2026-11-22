// @vitest-environment jsdom
/**
 * Capture-mode indicator proofs (WP-10-03, UI-10; §5.6.1, CAP-A-X09).
 * Binds the locked CAPTURE_MODE / CAPTURE_SOURCE wire tokens; absent/unknown
 * values render an honest baseline state, never a fabricated mode.
 */

import { afterEach, describe, expect, it } from 'vitest';
import { CaptureModeIndicator } from '../../../src/frontend/src/surfaces/explorers/rendered/CaptureModeIndicator';
import { renderTool, byTestId, textOf, fakeServices } from '../tools/helpers';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('CaptureModeIndicator — locked capture-mode tokens', () => {
  it.each([
    ['non-js', 'Non-JS (raw HTTP)'],
    ['js', 'JavaScript rendered'],
    ['dual', 'Dual (raw + rendered)'],
    ['adaptive', 'Adaptive sampling']
  ] as const)('renders the %s mode label', async (mode, label) => {
    const view = await renderTool(<CaptureModeIndicator mode={mode} />, fakeServices().services);
    const root = byTestId(view.container, 'capture-mode-indicator');
    expect(root?.getAttribute('data-mode')).toBe(mode);
    expect(textOf(byTestId(view.container, 'capture-mode-indicator-value'))).toBe(label);
  });

  it('renders a baseline state for an absent mode', async () => {
    const view = await renderTool(<CaptureModeIndicator />, fakeServices().services);
    expect(byTestId(view.container, 'capture-mode-indicator')?.getAttribute('data-mode')).toBe('baseline');
    expect(textOf(byTestId(view.container, 'capture-mode-indicator-value'))).toContain('Baseline');
  });

  it('renders a baseline state for an unknown mode token (guard)', async () => {
    const view = await renderTool(<CaptureModeIndicator mode={'bogus' as never} />, fakeServices().services);
    expect(byTestId(view.container, 'capture-mode-indicator')?.getAttribute('data-mode')).toBe('baseline');
  });
});

describe('CaptureModeIndicator — per-URL capture source', () => {
  it.each([
    ['raw', 'Raw response'],
    ['rendered', 'Rendered DOM'],
    ['both', 'Raw + rendered']
  ] as const)('renders the %s capture source', async (source, label) => {
    const view = await renderTool(<CaptureModeIndicator mode="dual" captureSource={source} />, fakeServices().services);
    const badge = byTestId(view.container, 'capture-mode-indicator-source');
    expect(badge?.getAttribute('data-source')).toBe(source);
    expect(textOf(badge)).toBe(label);
  });

  it('omits the source badge when no capture source is present', async () => {
    const view = await renderTool(<CaptureModeIndicator mode="js" />, fakeServices().services);
    expect(byTestId(view.container, 'capture-mode-indicator-source')).toBeNull();
  });
});
