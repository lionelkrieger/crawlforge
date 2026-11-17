// @vitest-environment jsdom
/**
 * HOTFIX-01: a failing report/run panel must show an inline error and leave
 * its shell siblings mounted (no blank renderer).
 */
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { MantineProvider } from '@mantine/core';
import { describe, expect, it, vi } from 'vitest';
import { SurfaceErrorBoundary } from '../../../src/frontend/src/shell/ErrorBoundary';

function Boom(): never {
  throw new Error('panel exploded');
}

describe('SurfaceErrorBoundary', () => {
  it('isolates a throwing panel and keeps sibling surfaces mounted', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => undefined,
        removeListener: () => undefined,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        dispatchEvent: () => false
      })
    });
    const container = document.createElement('div');
    document.body.appendChild(container);
    const root = createRoot(container);
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);

    await act(async () => {
      root.render(
        <MantineProvider>
          <div>
            <span data-testid="sibling">shell stays</span>
            <SurfaceErrorBoundary label="Report">
              <Boom />
            </SurfaceErrorBoundary>
          </div>
        </MantineProvider>
      );
    });

    expect(container.querySelector('[data-testid=sibling]')?.textContent).toBe('shell stays');
    expect(container.querySelector('[data-testid=surface-error]')?.textContent).toContain('Report could not render');
    expect(container.querySelector('[data-testid=surface-error]')?.textContent).toContain('panel exploded');

    consoleError.mockRestore();
    await act(async () => root.unmount());
    container.remove();
  });
});
