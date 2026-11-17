/**
 * Shared test helpers for the shell suites: fake fetch, fake hub
 * connection, service construction, and a jsdom render harness (React 19
 * act + createRoot; no testing-library dependency).
 */

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import type { ReactNode } from 'react';
import type { ApiEnvelope } from '../../../src/frontend/src/contracts/common/envelope';
import type {
  HubConnectionContract,
  HubConnectionState,
  HubHandlerDisposer
} from '../../../src/frontend/src/contracts/hub/client';
import type {
  HubClientMessageName,
  HubClientPayloadOf,
  HubServerEventName,
  HubServerPayloadOf
} from '../../../src/frontend/src/contracts/hub/events';
import { createShellServices, type ShellServices } from '../../../src/frontend/src/shell/ShellContext';
import { createCrawlForgeStore, createStoreActions } from '../../../src/frontend/src/store/crawlForgeStore';
import { createLiveProgressStore } from '../../../src/frontend/src/store/liveProgress';
import { createPluginHookBus } from '../../../src/frontend/src/store/pluginBus';
import { createHubDemux } from '../../../src/frontend/src/store/hubDemux';
import { createHubFacade } from '../../../src/frontend/src/store/hubClient';
import type { LoopbackApiClient } from '../../../src/frontend/src/contracts/api/client';
import { createLoopbackApiClient } from '../../../src/frontend/src/store/apiClient';

/** Builds a successful envelope. */
export function ok<T>(data: T, pagination?: { offset: number; limit: number; total: number }): ApiEnvelope<T> {
  return pagination ? { data, pagination } : { data };
}

/** Builds an error envelope. */
export function err(code: string, message: string, fieldErrors?: Record<string, string[]>): ApiEnvelope<null> {
  return { data: null, error: { code, message, ...(fieldErrors ? { fieldErrors } : {}) } };
}

/** Recorded fetch call. */
export interface FetchCall {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string | null;
}

/** Fake fetch routing by URL substring → responder. */
export function fakeFetch(routes: Record<string, (call: FetchCall) => unknown>) {
  const calls: FetchCall[] = [];
  const fetchLike = async (input: string, init?: RequestInit): Promise<Response> => {
    const headers: Record<string, string> = {};
    const rawHeaders = init?.headers as Record<string, string> | undefined;
    if (rawHeaders) Object.assign(headers, rawHeaders);
    const call: FetchCall = {
      url: input,
      method: init?.method ?? 'GET',
      headers,
      body: typeof init?.body === 'string' ? init.body : null
    };
    calls.push(call);
    for (const [fragment, responder] of Object.entries(routes)) {
      if (input.includes(fragment)) {
        const payload = responder(call);
        if (payload instanceof Response) return payload;
        return new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json' }
        });
      }
    }
    return new Response(JSON.stringify(err('not-found', `No fake route for ${input}`)), {
      status: 404,
      headers: { 'Content-Type': 'application/json' }
    });
  };
  return { fetchLike, calls };
}

/** In-memory fake hub connection implementing the locked contract. */
export class FakeHubConnection implements HubConnectionContract {
  state: HubConnectionState = 'Disconnected';
  connectionId: string | null = null;
  readonly baseUrl = 'http://127.0.0.1:5000';
  readonly invocations: { message: string; payload: unknown }[] = [];
  private handlers = new Map<string, Set<(payload: never) => void>>();
  private closeHandlers: ((error?: Error) => void)[] = [];
  private reconnectingHandlers: ((error?: Error) => void)[] = [];
  private reconnectedHandlers: ((connectionId?: string) => void)[] = [];
  startCalls = 0;
  stopCalls = 0;

  async start(): Promise<void> {
    this.startCalls += 1;
    this.state = 'Connected';
    this.connectionId = 'fake-connection';
  }

  async stop(): Promise<void> {
    this.stopCalls += 1;
    this.state = 'Disconnected';
    this.connectionId = null;
    for (const handler of this.closeHandlers) handler();
  }

  on<E extends HubServerEventName>(event: E, handler: (payload: HubServerPayloadOf<E>) => void): HubHandlerDisposer {
    let set = this.handlers.get(event);
    if (!set) {
      set = new Set();
      this.handlers.set(event, set);
    }
    set.add(handler as (payload: never) => void);
    return () => set.delete(handler as (payload: never) => void);
  }

  off<E extends HubServerEventName>(event: E, handler?: (payload: HubServerPayloadOf<E>) => void): void {
    if (!handler) {
      this.handlers.delete(event);
      return;
    }
    this.handlers.get(event)?.delete(handler as (payload: never) => void);
  }

  async invoke<M extends HubClientMessageName>(message: M, payload: HubClientPayloadOf<M>): Promise<void> {
    this.invocations.push({ message, payload });
  }

  onclose(handler: (error?: Error) => void): HubHandlerDisposer {
    this.closeHandlers.push(handler);
    return () => undefined;
  }

  onreconnecting(handler: (error?: Error) => void): HubHandlerDisposer {
    this.reconnectingHandlers.push(handler);
    return () => undefined;
  }

  onreconnected(handler: (connectionId?: string) => void): HubHandlerDisposer {
    this.reconnectedHandlers.push(handler);
    return () => undefined;
  }

  /** Test driver: emit one server event to all registered handlers. */
  emit<E extends HubServerEventName>(event: E, payload: HubServerPayloadOf<E>): void {
    for (const handler of this.handlers.get(event) ?? []) {
      (handler as (payload: HubServerPayloadOf<E>) => void)(payload);
    }
  }

  /** Test driver: count handlers registered for an event. */
  handlerCount(event: HubServerEventName): number {
    return this.handlers.get(event)?.size ?? 0;
  }

  /** Test driver: simulate a transport reconnect. */
  simulateReconnected(): void {
    this.state = 'Connected';
    for (const handler of this.reconnectedHandlers) handler('fake-connection-2');
  }
}

/** Minimal offline service graph (no API, no hub). */
export function offlineServices(): ShellServices {
  return createShellServices({ baseUrl: null });
}

/** Service graph with a fake-fetch API client and a fake hub connection. */
export function fakeServices(routes: Record<string, (call: FetchCall) => unknown> = {}) {
  const { fetchLike, calls } = fakeFetch(routes);
  const services = createShellServices({
    baseUrl: 'http://127.0.0.1:5000',
    getSessionToken: () => 'test-session-token',
    fetch: fetchLike
  });
  return { services, calls };
}

/** Standalone demux graph for unit tests (no React). */
export function demuxGraph() {
  const store = createCrawlForgeStore();
  const actions = createStoreActions(store, null);
  const live = createLiveProgressStore();
  const bus = createPluginHookBus(store);
  const demux = createHubDemux({ store, actions, live, bus, now: () => '2026-11-14T12:00:00Z' });
  const connection = new FakeHubConnection();
  const facade = createHubFacade(connection, { store, actions, demux });
  return { store, actions, live, bus, demux, connection, facade };
}

/** Typed API client over a fake fetch (for client unit tests). */
export function fakeApiClient(routes: Record<string, (call: FetchCall) => unknown>, token = 'test-session-token'): { api: LoopbackApiClient; calls: FetchCall[] } {
  const { fetchLike, calls } = fakeFetch(routes);
  const api = createLoopbackApiClient({
    baseUrl: 'http://127.0.0.1:5000',
    fetch: fetchLike,
    getSessionToken: () => token
  });
  return { api, calls };
}

// ── jsdom render harness ───────────────────────────────────────────────────

/** Installs the browser APIs Mantine needs under jsdom. */
export function installDomStubs(): void {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  if (!window.matchMedia) {
    window.matchMedia = (query: string) =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => undefined,
        removeListener: () => undefined,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
        dispatchEvent: () => false
      }) as MediaQueryList;
  }
  if (!globalThis.ResizeObserver) {
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  }
  if (!HTMLElement.prototype.setPointerCapture) {
    HTMLElement.prototype.setPointerCapture = () => undefined;
    HTMLElement.prototype.releasePointerCapture = () => undefined;
  }
  if (!HTMLElement.prototype.scrollIntoView) {
    HTMLElement.prototype.scrollIntoView = () => undefined;
  }
}

/** Mounted root handle. */
export interface Mounted {
  container: HTMLElement;
  unmount: () => void;
  rerender: (node: ReactNode) => void;
}

/** Renders a node into a fresh container under act. */
export async function render(node: ReactNode): Promise<Mounted> {
  installDomStubs();
  const container = document.createElement('div');
  document.body.appendChild(container);
  let root: Root | null = null;
  await act(async () => {
    root = createRoot(container);
    root.render(node);
  });
  return {
    container,
    unmount: () => {
      act(() => root?.unmount());
      container.remove();
    },
    rerender: (next: ReactNode) => {
      act(() => root?.render(next));
    }
  };
}

/** Flushes pending promises/timers under act. */
export async function flush(ms = 0): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

/** Query helper by data-testid. */
export function byTestId(container: HTMLElement, id: string): HTMLElement | null {
  return container.querySelector(`[data-testid="${id}"]`);
}

/**
 * Dispatches a native DOM event under act (no testing-library dependency).
 * Pointer/keyboard/mouse events are constructed with the matching ctor;
 * pointerId is attached defensively for jsdom builds lacking PointerEvent.
 */
export function fire(element: Element, type: string, init: Record<string, unknown> = {}): void {
  act(() => {
    let event: Event;
    const base = { bubbles: true, cancelable: true, ...init };
    if (type.startsWith('pointer') && typeof PointerEvent !== 'undefined') {
      event = new PointerEvent(type, base as PointerEventInit);
    } else if (type.startsWith('key')) {
      event = new KeyboardEvent(type, base as KeyboardEventInit);
    } else {
      event = new MouseEvent(type, base as MouseEventInit);
    }
    if (type.startsWith('pointer') && !('pointerId' in event)) {
      Object.defineProperty(event, 'pointerId', { value: init.pointerId ?? 1 });
    }
    element.dispatchEvent(event);
  });
}

/** Click helper. */
export function click(element: Element): void {
  fire(element, 'click');
}

/** Types into a controlled input via the native value setter + input event. */
export function type(input: HTMLInputElement, value: string): void {
  act(() => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (setter) {
      setter.call(input, value);
    } else {
      input.value = value;
    }
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

/** Keyboard helper on document (hotkeys). */
export function keyDown(target: Element | Document, key: string, modifiers: { ctrlKey?: boolean; metaKey?: boolean; shiftKey?: boolean } = {}): void {
  act(() => {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...modifiers }));
  });
}
