/**
 * Real Sigma.js adapter behind the GraphRendererFactory port (WebGL graph
 * rendering, FS-CRAWL-002 §8.3). Dark-first defaults; kill() releases the
 * WebGL context and all listeners.
 *
 * LAZY BY CONSTRUCTION: sigma's module initialization touches WebGL globals
 * (WebGL2RenderingContext) that jsdom does not provide, so the runtime
 * `import('sigma')` happens INSIDE the async factory creation path — never
 * at module scope. jsdom suites inject the mock factory and never trigger
 * this import; production lazy-loads sigma on the first real graph mount.
 * The GraphRendererFactory port itself stays synchronous.
 *
 * @module graphs/sigmaRenderer
 */

import type { Settings } from 'sigma/settings';
import type { CrawlGraph } from './graphModel';
import type { GraphRendererFactory, GraphRendererPort } from './graphPorts';

/** Dark-first sigma settings (node/edge colors come from graph attributes). */
const baseSettings: Partial<Settings> = {
  renderLabels: true,
  renderEdgeLabels: false,
  labelRenderedSizeThreshold: 8,
  stagePadding: 24,
  zoomDuration: 180
};

/**
 * Creates the production WebGL renderer factory. Async because the sigma
 * module (and its DEFAULT_STYLES) is imported lazily here — evaluating this
 * module never touches WebGL globals.
 */
export async function createSigmaRendererFactory(settings: Partial<Settings> = {}): Promise<GraphRendererFactory> {
  const { default: Sigma, DEFAULT_STYLES } = await import('sigma');

  return (container: HTMLElement, graph: CrawlGraph): GraphRendererPort => {
    const sigma = new Sigma(graph, container, {
      settings: { ...baseSettings, ...settings },
      styles: {
        nodes: { ...DEFAULT_STYLES.nodes, labelColor: '#aab4c8' },
        edges: DEFAULT_STYLES.edges
      }
    });
    let killed = false;

    return {
      refresh(): void {
        if (!killed) sigma.refresh();
      },
      resize(): void {
        if (!killed) sigma.resize();
      },
      setSetting(key: string, value: unknown): void {
        if (killed) return;
        sigma.setSetting(key as keyof Settings, value as Settings[keyof Settings]);
      },
      onNodeClick(handler: (nodeId: string) => void): () => void {
        const listener = ({ node }: { node: string }): void => handler(node);
        sigma.on('clickNode', listener);
        return () => sigma.removeListener('clickNode', listener);
      },
      onNodeHover(handler: (nodeId: string | null) => void): () => void {
        const enter = ({ node }: { node: string }): void => handler(node);
        const leave = (): void => handler(null);
        sigma.on('enterNode', enter);
        sigma.on('leaveNode', leave);
        return () => {
          sigma.removeListener('enterNode', enter);
          sigma.removeListener('leaveNode', leave);
        };
      },
      kill(): void {
        if (killed) return;
        killed = true;
        sigma.kill();
      }
    };
  };
}
