/**
 * SignalR hub client implementation (contracts/hub/client).
 *
 * One WebSocket connection to the loopback hub feeds the demultiplexer
 * (FS-CRAWL-001 §10.3, FS-CRAWL-002 §8.1). All 18 server events are wired;
 * subscribe/unsubscribe join/leave per-audit channels (server methods take
 * a string[] — the facade bridges the locked AuditChannelPayload shape).
 * system-health flows independently of per-audit subscriptions (§7.2).
 *
 * Session convention (WP-06-01): the token source is INJECTABLE; the
 * preload bridge supplies it in production. It rides as both the SignalR
 * access token (WebSocket query) and the X-CrawlForge-Session header
 * (negotiate/long-polling). The renderer never reads the handshake file.
 *
 * @module store/hubClient
 */

import {
  HubConnectionBuilder,
  LogLevel,
  type HubConnection,
  type IHttpConnectionOptions
} from '@microsoft/signalr';
import type {
  CrawlForgeHubClient,
  HubConnectionContract,
  HubConnectionState,
  HubHandlerDisposer
} from '../contracts/hub/client';
import type {
  HubClientMessageName,
  HubClientPayloadOf,
  HubServerEventName,
  HubServerPayloadOf
} from '../contracts/hub/events';
import { HUB_SERVER_EVENT_NAMES } from '../contracts/hub/events';
import type { CrawlForgeStore } from '../contracts/store/selectors';
import type { CrawlForgeStoreActions } from './crawlForgeStore';
import type { createHubDemux } from './hubDemux';
import { SESSION_HEADER, assertLoopbackBaseUrl, type SessionTokenProvider } from './apiClient';

/** Hub route path (accepted phase-05 CrawlForgeHub.HubRoutePath). */
export const HUB_ROUTE_PATH = '/hubs/realtime';

/** Options for the real SignalR connection. */
export interface SignalRHubClientOptions {
  /** Loopback origin discovered via the preload bridge. */
  readonly baseUrl: string;
  /** Automatic reconnect (default true; local transport only). */
  readonly reconnect?: boolean;
  /** Injectable session-token provider. */
  readonly getSessionToken?: SessionTokenProvider;
  /** Injectable connection factory (tests supply fakes). */
  readonly connectionFactory?: (url: string, options: IHttpConnectionOptions) => HubConnection;
}

/** Maps the SignalR connection state enum to the locked contract strings. */
function mapState(state: string): HubConnectionState {
  switch (state) {
    case 'Connecting':
      return 'Connecting';
    case 'Connected':
      return 'Connected';
    case 'Disconnecting':
      return 'Disconnecting';
    case 'Reconnecting':
      return 'Reconnecting';
    default:
      return 'Disconnected';
  }
}

/** Adapts a real SignalR HubConnection to the locked low-level contract. */
export function adaptSignalRConnection(
  connection: HubConnection,
  baseUrl: string
): HubConnectionContract {
  return {
    get state() {
      return mapState(connection.state);
    },
    get connectionId() {
      return connection.connectionId;
    },
    baseUrl,
    start: () => connection.start(),
    stop: () => connection.stop(),
    on<E extends HubServerEventName>(
      event: E,
      handler: (payload: HubServerPayloadOf<E>) => void
    ): HubHandlerDisposer {
      connection.on(event, handler as (...args: unknown[]) => void);
      return () => connection.off(event, handler as (...args: unknown[]) => void);
    },
    off<E extends HubServerEventName>(
      event: E,
      handler?: (payload: HubServerPayloadOf<E>) => void
    ): void {
      if (handler) {
        connection.off(event, handler as (...args: unknown[]) => void);
      } else {
        connection.off(event);
      }
    },
    invoke<M extends HubClientMessageName>(message: M, payload: HubClientPayloadOf<M>): Promise<void> {
      // Server hub methods: Subscribe(string[] auditIds) / Unsubscribe(string[]).
      // SignalR method lookup is case-insensitive; the payload bridges to the
      // single array argument the accepted hub expects.
      return connection.invoke(message, payload.auditIds);
    },
    onclose(handler: (error?: Error) => void): HubHandlerDisposer {
      connection.onclose(handler);
      return () => undefined;
    },
    onreconnecting(handler: (error?: Error) => void): HubHandlerDisposer {
      connection.onreconnecting(handler);
      return () => undefined;
    },
    onreconnected(handler: (connectionId?: string) => void): HubHandlerDisposer {
      connection.onreconnected(handler);
      return () => undefined;
    }
  };
}

/** Options for the facade over any (real or fake) connection contract. */
export interface HubFacadeOptions {
  readonly store: CrawlForgeStore;
  readonly actions: CrawlForgeStoreActions;
  readonly demux: ReturnType<typeof createHubDemux>;
}

/**
 * Builds the high-level facade over a connection contract: wires all 18
 * event handlers into the demultiplexer, manages per-audit subscriptions
 * (re-subscribed after reconnect), and mirrors connection state into the
 * store. Idempotent start; graceful stop with full handler cleanup.
 */
export function createHubFacade(
  connection: HubConnectionContract,
  options: HubFacadeOptions
): CrawlForgeHubClient {
  const { store, actions, demux } = options;
  const disposers: HubHandlerDisposer[] = [];
  let started = false;
  let stopped = false;

  // Wire every server event exactly once.
  for (const name of HUB_SERVER_EVENT_NAMES) {
    const handler = demux.handlers[name] as (payload: HubServerPayloadOf<HubServerEventName>) => void;
    disposers.push(connection.on(name, handler));
  }

  disposers.push(
    connection.onclose(() => {
      actions.setConnectionState('Disconnected');
    }),
    connection.onreconnecting(() => {
      actions.setConnectionState('Reconnecting');
    }),
    connection.onreconnected(() => {
      actions.setConnectionState('Connected');
      // Re-join per-audit channels after a transport reconnect.
      const ids = store.getState().hub.subscribedAuditIds;
      if (ids.length > 0) {
        void connection.invoke('subscribe', { auditIds: ids }).catch(() => undefined);
      }
    })
  );

  const client: CrawlForgeHubClient = {
    connection,

    async subscribe(auditIds: readonly string[]): Promise<void> {
      if (auditIds.length === 0) return;
      await connection.invoke('subscribe', { auditIds });
      const current = store.getState().hub.subscribedAuditIds;
      const merged = [...new Set([...current, ...auditIds])];
      actions.setSubscribedAuditIds(merged);
    },

    async unsubscribe(auditIds: readonly string[]): Promise<void> {
      if (auditIds.length === 0) return;
      await connection.invoke('unsubscribe', { auditIds });
      const removing = new Set(auditIds);
      const remaining = store.getState().hub.subscribedAuditIds.filter((id) => !removing.has(id));
      actions.setSubscribedAuditIds(remaining);
    },

    onEvent<E extends HubServerEventName>(
      event: E,
      handler: (payload: HubServerPayloadOf<E>) => void
    ): HubHandlerDisposer {
      return connection.on(event, handler);
    },

    async start(): Promise<void> {
      if (started || stopped) return;
      actions.setConnectionState('Connecting');
      try {
        await connection.start();
        started = true;
        actions.setConnectionState(mapState(connection.state));
      } catch (error) {
        actions.setConnectionState('Disconnected');
        throw error;
      }
    },

    async stop(): Promise<void> {
      if (stopped) return;
      stopped = true;
      actions.setConnectionState('Disconnecting');
      try {
        await connection.stop();
      } finally {
        for (const dispose of disposers) dispose();
        disposers.length = 0;
        actions.setConnectionState('Disconnected');
        actions.setSubscribedAuditIds([]);
      }
    }
  };

  return client;
}

/**
 * Creates the production SignalR hub client: loopback-only URL, WebSocket
 * transport, automatic reconnect, injectable session token, and the full
 * demultiplexer wiring.
 */
export function createSignalRHubClient(
  options: SignalRHubClientOptions & HubFacadeOptions
): CrawlForgeHubClient {
  assertLoopbackBaseUrl(options.baseUrl);
  const baseUrl = options.baseUrl.replace(/\/+$/, '');
  const url = baseUrl + HUB_ROUTE_PATH;

  const connectionOptions: IHttpConnectionOptions = {
    transport: 1 /* HttpTransportType.WebSockets */,
    accessTokenFactory: () => options.getSessionToken?.() ?? ''
  };
  const token = options.getSessionToken?.();
  if (token) {
    connectionOptions.headers = { [SESSION_HEADER]: token };
  }

  const builder = new HubConnectionBuilder()
    .withUrl(url, connectionOptions)
    .configureLogging(LogLevel.Warning);
  if (options.reconnect !== false) {
    builder.withAutomaticReconnect();
  }

  const raw = options.connectionFactory
    ? options.connectionFactory(url, connectionOptions)
    : builder.build();

  return createHubFacade(adaptSignalRConnection(raw, baseUrl), options);
}
