/**
 * Dockable split panes (FS-CRAWL-002 §8.2 workspace). Custom dependency-free
 * implementation: draggable divider (pointer capture), keyboard resizing,
 * double-click collapse/restore, orientation switch, clamped fractions.
 * Layout persists through the shell window-state surface.
 *
 * @module shell/SplitPanes
 */

import { useCallback, useRef, useState, type ReactNode, type PointerEvent, type KeyboardEvent } from 'react';

/** Split orientation. */
export type SplitDirection = 'horizontal' | 'vertical';

/** SplitPanes props. */
export interface SplitPanesProps {
  readonly direction: SplitDirection;
  /** First-pane fraction (clamped 0.15..0.85). */
  readonly split: number;
  readonly collapsed: boolean;
  readonly onSplitChange: (split: number) => void;
  readonly onCollapsedChange: (collapsed: boolean) => void;
  readonly first: ReactNode;
  readonly second: ReactNode;
  /** Accessible labels for the two panes. */
  readonly firstLabel?: string;
  readonly secondLabel?: string;
}

const MIN_FRACTION = 0.15;
const MAX_FRACTION = 0.85;
const KEY_STEP = 0.03;

/** Clamps a fraction to the dockable range. */
export function clampFraction(value: number): number {
  if (!Number.isFinite(value)) return 0.5;
  return Math.max(MIN_FRACTION, Math.min(MAX_FRACTION, value));
}

/**
 * Two dockable panes separated by an interactive divider. The divider is a
 * focusable separator with full keyboard support (arrows resize, Enter
 * toggles collapse) and pointer-capture dragging.
 */
export function SplitPanes({
  direction,
  split,
  collapsed,
  onSplitChange,
  onCollapsedChange,
  first,
  second,
  firstLabel = 'Primary pane',
  secondLabel = 'Secondary pane'
}: SplitPanesProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [dragging, setDragging] = useState(false);

  const fractionFromPointer = useCallback(
    (clientX: number, clientY: number): number => {
      const container = containerRef.current;
      if (!container) return split;
      const rect = container.getBoundingClientRect();
      const raw =
        direction === 'horizontal'
          ? (clientX - rect.left) / Math.max(1, rect.width)
          : (clientY - rect.top) / Math.max(1, rect.height);
      return clampFraction(raw);
    },
    [direction, split]
  );

  const handlePointerDown = useCallback(
    (event: PointerEvent<HTMLButtonElement>) => {
      event.preventDefault();
      event.currentTarget.setPointerCapture(event.pointerId);
      setDragging(true);
    },
    []
  );

  const handlePointerMove = useCallback(
    (event: PointerEvent<HTMLButtonElement>) => {
      if (!dragging) return;
      onSplitChange(fractionFromPointer(event.clientX, event.clientY));
    },
    [dragging, fractionFromPointer, onSplitChange]
  );

  const handlePointerUp = useCallback(
    (event: PointerEvent<HTMLButtonElement>) => {
      if (!dragging) return;
      event.currentTarget.releasePointerCapture(event.pointerId);
      setDragging(false);
    },
    [dragging]
  );

  const handleKeyDown = useCallback(
    (event: KeyboardEvent<HTMLButtonElement>) => {
      const decrease = direction === 'horizontal' ? 'ArrowLeft' : 'ArrowUp';
      const increase = direction === 'horizontal' ? 'ArrowRight' : 'ArrowDown';
      if (event.key === decrease) {
        event.preventDefault();
        onCollapsedChange(false);
        onSplitChange(clampFraction(split - KEY_STEP));
      } else if (event.key === increase) {
        event.preventDefault();
        onCollapsedChange(false);
        onSplitChange(clampFraction(split + KEY_STEP));
      } else if (event.key === 'Home') {
        event.preventDefault();
        onSplitChange(MIN_FRACTION);
      } else if (event.key === 'End') {
        event.preventDefault();
        onSplitChange(MAX_FRACTION);
      } else if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        onCollapsedChange(!collapsed);
      }
    },
    [direction, split, collapsed, onSplitChange, onCollapsedChange]
  );

  const firstSize = collapsed ? '100%' : `${(split * 100).toFixed(2)}%`;
  const secondSize = collapsed ? '0%' : `${((1 - split) * 100).toFixed(2)}%`;
  const sizeProperty = direction === 'horizontal' ? 'width' : 'height';

  return (
    <div className="cf-split" data-direction={direction} ref={containerRef} data-testid="cf-split">
      <div
        className="cf-split-pane"
        style={{ [sizeProperty]: firstSize, flex: `0 0 ${firstSize}` }}
        role="region"
        aria-label={firstLabel}
        data-testid="cf-split-first"
      >
        {first}
      </div>
      <button
        type="button"
        className="cf-split-divider"
        data-dragging={dragging}
        data-testid="cf-split-divider"
        role="separator"
        aria-orientation={direction}
        aria-valuenow={Math.round(split * 100)}
        aria-valuemin={MIN_FRACTION * 100}
        aria-valuemax={MAX_FRACTION * 100}
        aria-label={`Resize ${firstLabel} and ${secondLabel} (Enter toggles collapse)`}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onPointerCancel={handlePointerUp}
        onDoubleClick={() => onCollapsedChange(!collapsed)}
        onKeyDown={handleKeyDown}
      />
      <div
        className="cf-split-pane"
        style={{
          [sizeProperty]: secondSize,
          flex: `0 0 ${secondSize}`,
          display: collapsed ? 'none' : undefined
        }}
        role="region"
        aria-label={secondLabel}
        data-testid="cf-split-second"
      >
        {second}
      </div>
    </div>
  );
}
