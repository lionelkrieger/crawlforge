// @vitest-environment jsdom
/**
 * Split-pane interaction tests: keyboard resizing, collapse/restore,
 * pointer dragging, and clamping.
 */

import { describe, expect, it, vi } from 'vitest';
import { SplitPanes, clampFraction } from '../../../src/frontend/src/shell/SplitPanes';
import { render, byTestId, installDomStubs, fire } from './helpers';

installDomStubs();

function setup(overrides: Partial<Parameters<typeof SplitPanes>[0]> = {}) {
  const onSplitChange = vi.fn();
  const onCollapsedChange = vi.fn();
  const props = {
    direction: 'horizontal' as const,
    split: 0.6,
    collapsed: false,
    onSplitChange,
    onCollapsedChange,
    first: <div>first</div>,
    second: <div>second</div>,
    ...overrides
  };
  return { onSplitChange, onCollapsedChange, props };
}

describe('clampFraction', () => {
  it('clamps to the dockable range', () => {
    expect(clampFraction(0.01)).toBe(0.15);
    expect(clampFraction(0.99)).toBe(0.85);
    expect(clampFraction(Number.NaN)).toBe(0.5);
    expect(clampFraction(0.4)).toBe(0.4);
  });
});

describe('SplitPanes', () => {
  it('renders both panes and a separator with ARIA values', async () => {
    const { props } = setup();
    const mounted = await render(<SplitPanes {...props} />);
    const divider = byTestId(mounted.container, 'cf-split-divider')!;
    expect(divider.getAttribute('role')).toBe('separator');
    expect(divider.getAttribute('aria-valuenow')).toBe('60');
    expect(byTestId(mounted.container, 'cf-split-first')!.textContent).toBe('first');
    expect(byTestId(mounted.container, 'cf-split-second')!.textContent).toBe('second');
    mounted.unmount();
  });

  it('resizes with arrow keys and clamps at the bounds', async () => {
    const { props, onSplitChange } = setup({ split: 0.16 });
    const mounted = await render(<SplitPanes {...props} />);
    const divider = byTestId(mounted.container, 'cf-split-divider')!;
    fire(divider, 'keydown', { key: 'ArrowLeft' });
    expect(onSplitChange).toHaveBeenCalledWith(0.15); // clamped
    fire(divider, 'keydown', { key: 'ArrowRight' });
    expect(onSplitChange).toHaveBeenLastCalledWith(0.19);
    fire(divider, 'keydown', { key: 'End' });
    expect(onSplitChange).toHaveBeenLastCalledWith(0.85);
    fire(divider, 'keydown', { key: 'Home' });
    expect(onSplitChange).toHaveBeenLastCalledWith(0.15);
    mounted.unmount();
  });

  it('toggles collapse with Enter and double-click', async () => {
    const { props, onCollapsedChange } = setup();
    const mounted = await render(<SplitPanes {...props} />);
    const divider = byTestId(mounted.container, 'cf-split-divider')!;
    fire(divider, 'keydown', { key: 'Enter' });
    expect(onCollapsedChange).toHaveBeenCalledWith(true);
    fire(divider, 'dblclick');
    expect(onCollapsedChange).toHaveBeenLastCalledWith(true);
    mounted.unmount();
  });

  it('hides the second pane when collapsed', async () => {
    const { props } = setup({ collapsed: true });
    const mounted = await render(<SplitPanes {...props} />);
    const second = byTestId(mounted.container, 'cf-split-second')!;
    expect(second.style.display).toBe('none');
    expect(byTestId(mounted.container, 'cf-split-first')!.style.width).toBe('100%');
    mounted.unmount();
  });

  it('drags the divider with pointer capture', async () => {
    const { props, onSplitChange } = setup();
    const mounted = await render(<SplitPanes {...props} />);
    const split = byTestId(mounted.container, 'cf-split')!;
    split.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 500, right: 1000, bottom: 500, x: 0, y: 0, toJSON: () => ({}) });
    const divider = byTestId(mounted.container, 'cf-split-divider')!;

    fire(divider, 'pointerdown', { pointerId: 1, clientX: 600 });
    expect(divider.getAttribute('data-dragging')).toBe('true');
    fire(divider, 'pointermove', { pointerId: 1, clientX: 300 });
    expect(onSplitChange).toHaveBeenCalledWith(0.3);
    fire(divider, 'pointermove', { pointerId: 1, clientX: 50 });
    expect(onSplitChange).toHaveBeenLastCalledWith(0.15); // clamped
    fire(divider, 'pointerup', { pointerId: 1, clientX: 300 });
    expect(divider.getAttribute('data-dragging')).toBe('false');
    mounted.unmount();
  });

  it('uses vertical geometry for vertical splits', async () => {
    const { props, onSplitChange } = setup({ direction: 'vertical' });
    const mounted = await render(<SplitPanes {...props} />);
    const split = byTestId(mounted.container, 'cf-split')!;
    expect(split.getAttribute('data-direction')).toBe('vertical');
    split.getBoundingClientRect = () => ({ left: 0, top: 0, width: 1000, height: 500, right: 1000, bottom: 500, x: 0, y: 0, toJSON: () => ({}) });
    const divider = byTestId(mounted.container, 'cf-split-divider')!;
    fire(divider, 'pointerdown', { pointerId: 1, clientY: 300 });
    fire(divider, 'pointermove', { pointerId: 1, clientY: 250 });
    expect(onSplitChange).toHaveBeenCalledWith(0.5);
    fire(divider, 'keydown', { key: 'ArrowUp' });
    expect(onSplitChange).toHaveBeenLastCalledWith(0.57);
    mounted.unmount();
  });
});
