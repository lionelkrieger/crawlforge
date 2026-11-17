/**
 * SignalR hub client contract — structural types only (no runtime dependency
 * on a SignalR package; the implementation arrives with the phase-06 shell
 * workpiece). The low-level interface mirrors the SignalR HubConnection
 * surface so a real connection can satisfy it directly; the high-level
 * facade adds typed per-audit subscription semantics.
 *
 * Spec surface: FS-CRAWL-001 §10.3 (one hub connection; subscribe/unsubscribe
 * per-audit channels, no authorization beyond the local boundary),
 * FS-CRAWL-002 §8.1 (one WebSocket hub connection feeds an event
 * demultiplexer with bounded ring buffers).
 *
 * @module contracts/hub/client
 */

import type {
  HubClientMessageName,
  HubClientPayloadOf,
  HubServerEventName,
  HubServerPayloadOf
} from './events';

/**
 * Connection states (mirrors the SignalR HubConnectionState string values
 * for drop-in compatibility).
 */
export type HubConnectionState =
  | 'Disconnected'
  | 'Connecting'
  | 'Connected'
  | 'Disconnecting'
  | 'Reconnecting';

/** Handler registration disposer. */
export type HubHandlerDisposer = () => void;

/**
 * Low-level SignalR-style connection contract. Event names are the wire
 * names of the hub contract; payloads are typed per event.
 */
export interface HubConnectionContract {
  readonly state: HubConnectionState;
  readonly connectionId: string | null;
  /** Base URL of the loopback hub (never non-loopback). */
  readonly baseUrl: string;
  start(): Promise<void>;
  stop(): Promise<void>;
  /** Register a typed handler for one server event. */
  on<E extends HubServerEventName>(
    event: E,
    handler: (payload: HubServerPayloadOf<E>) => void
  ): HubHandlerDisposer;
  /** Remove a handler (or all handlers for the event when omitted). */
  off<E extends HubServerEventName>(
    event: E,
    handler?: (payload: HubServerPayloadOf<E>) => void
  ): void;
  /** Invoke a client -> server channel message (subscribe/unsubscribe). */
  invoke<M extends HubClientMessageName>(
    message: M,
    payload: HubClientPayloadOf<M>
  ): Promise<void>;
  onclose(handler: (error?: Error) => void): HubHandlerDisposer;
  onreconnecting(handler: (error?: Error) => void): HubHandlerDisposer;
  onreconnected(handler: (connectionId?: string) => void): HubHandlerDisposer;
}

/** Options for establishing the single hub connection. */
export interface HubClientOptions {
  /** Loopback hub URL discovered via the preload bridge. */
  readonly baseUrl: string;
  /** Automatic reconnect policy (local transport only). */
  readonly reconnect?: boolean;
}

/**
 * High-level hub client facade consumed by the store demultiplexer.
 * Subscriptions join/leaves per-audit channels; system-health flows
 * independently of per-audit subscriptions (FS-CRAWL-002 §7.2).
 */
export interface CrawlForgeHubClient {
  readonly connection: HubConnectionContract;
  /** Join per-audit channels for the given audit identifiers. */
  subscribe(auditIds: readonly string[]): Promise<void>;
  /** Leave per-audit channels. */
  unsubscribe(auditIds: readonly string[]): Promise<void>;
  /** Typed server-event subscription; returns a disposer. */
  onEvent<E extends HubServerEventName>(
    event: E,
    handler: (payload: HubServerPayloadOf<E>) => void
  ): HubHandlerDisposer;
  /** Start the connection (idempotent). */
  start(): Promise<void>;
  /** Gracefully stop the connection. */
  stop(): Promise<void>;
}
