/**
 * Graph renderer ports (dependency-injection boundary). Sigma.js needs
 * WebGL, which jsdom does not provide — mounting therefore sits behind
 * these ports and jsdom suites inject a mock renderer. The real adapter
 * (sigmaRenderer.ts) is the only runtime sigma import in the graphs area.
 *
 * @module graphs/graphPorts
 */

import type { CrawlGraph } from './graphModel';

/**
 * Minimal sigma-like surface consumed by graph components. kill() MUST
 * release the WebGL context, canvases, and every listener (§7.4: graphs
 * dispose on unmount; no leaked WebGL contexts).
 */
export interface GraphRendererPort {
  /** Redraw after graph attribute changes. */
  refresh(): void;
  /** Re-measure the container. */
  resize(): void;
  /** Update one renderer setting (e.g. label threshold, edge colors). */
  setSetting(key: string, value: unknown): void;
  /** Node click subscription; returns a disposer. */
  onNodeClick(handler: (nodeId: string) => void): () => void;
  /** Node hover subscription (null on leave); returns a disposer. */
  onNodeHover(handler: (nodeId: string | null) => void): () => void;
  /** Release everything. Idempotent. */
  kill(): void;
}

/** Factory port: mounts a renderer for a graph into a container. */
export type GraphRendererFactory = (container: HTMLElement, graph: CrawlGraph) => GraphRendererPort;
