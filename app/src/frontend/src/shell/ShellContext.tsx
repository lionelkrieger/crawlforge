/**
 * Shell service container + React context (dependency injection for the
 * whole shell). Services are constructed once per mount: normalized store,
 * typed API client (loopback + injectable session token), live telemetry,
 * plugin hook bus, hub demultiplexer, and the SignalR facade.
 *
 * Offline-first: with no backend origin the shell renders fully in a
 * disconnected state — every surface degrades gracefully, never throws.
 *
 * @module shell/ShellContext
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode
} from 'react';
import type { LoopbackApiClient, FetchLike } from '../contracts/api/client';
import type { CrawlForgeStore, CrawlForgeStoreHook } from '../contracts/store/selectors';
import type { CrawlForgeHubClient } from '../contracts/hub/client';
import {
  createCrawlForgeStore,
  createStoreActions,
  createStoreHook,
  type CrawlForgeStoreActions
} from '../store/crawlForgeStore';
import { createLoopbackApiClient, type SessionTokenProvider } from '../store/apiClient';
import { createLiveProgressStore, type LiveProgressStore } from '../store/liveProgress';
import { createPluginHookBus, type CrawlForgePluginBus } from '../store/pluginBus';
import { createHubDemux } from '../store/hubDemux';
import { createSignalRHubClient } from '../store/hubClient';
import {
  DEFAULT_WINDOW_STATE,
  loadWindowState,
  saveWindowState,
  type PaneLayoutState,
  type ShellView,
  type ShellWindowState
} from './windowState';

/** The complete shell service graph. */
export interface ShellServices {
  readonly store: CrawlForgeStore;
  readonly hook: CrawlForgeStoreHook;
  readonly actions: CrawlForgeStoreActions;
  readonly api: LoopbackApiClient | null;
  readonly live: LiveProgressStore;
  readonly bus: CrawlForgePluginBus;
  readonly hub: CrawlForgeHubClient | null;
  readonly baseUrl: string | null;
}

/** Service construction options. */
export interface ShellServicesOptions {
  /** Loopback backend origin (preload bridge); null = offline mode. */
  readonly baseUrl: string | null;
  readonly getSessionToken?: SessionTokenProvider;
  readonly fetch?: FetchLike;
}

/** Constructs the full service graph (pure; no React, no side effects). */
export function createShellServices(options: ShellServicesOptions): ShellServices {
  const store = createCrawlForgeStore();
  const hook = createStoreHook(store);
  const api = options.baseUrl
    ? createLoopbackApiClient({
        baseUrl: options.baseUrl,
        fetch: options.fetch,
        getSessionToken: options.getSessionToken
      })
    : null;
  const actions = createStoreActions(store, api);
  const live = createLiveProgressStore();
  const bus = createPluginHookBus(store);
  const demux = createHubDemux({ store, actions, live, bus });
  const hub = options.baseUrl
    ? createSignalRHubClient({
        baseUrl: options.baseUrl,
        getSessionToken: options.getSessionToken,
        store,
        actions,
        demux
      })
    : null;
  return { store, hook, actions, api, live, bus, hub, baseUrl: options.baseUrl };
}

/** Navigation + layout surface exposed to shell components. */
export interface ShellNavigation {
  readonly view: ShellView;
  readonly layout: PaneLayoutState;
  setView(view: ShellView): void;
  setLayout(layout: PaneLayoutState): void;
  openAudit(auditId: string): void;
}

interface ShellContextValue {
  readonly services: ShellServices;
  readonly navigation: ShellNavigation;
}

const ShellContext = createContext<ShellContextValue | null>(null);

/** Provider props. */
export interface ShellProviderProps {
  readonly services: ShellServices;
  readonly children: ReactNode;
  /** Initial window state override (tests). */
  readonly initialWindowState?: ShellWindowState;
}

/** Provides services + persisted navigation/layout to the shell tree. */
export function ShellProvider({ services, children, initialWindowState }: ShellProviderProps) {
  const [windowState, setWindowState] = useState<ShellWindowState>(
    () => initialWindowState ?? loadWindowState()
  );
  const stateRef = useRef(windowState);
  stateRef.current = windowState;

  const update = useCallback((next: ShellWindowState) => {
    setWindowState(next);
    saveWindowState(next);
  }, []);

  const navigation = useMemo<ShellNavigation>(
    () => ({
      view: windowState.view,
      layout: windowState.layout,
      setView: (view) => update({ ...stateRef.current, view }),
      setLayout: (layout) => update({ ...stateRef.current, layout }),
      openAudit: (auditId) => {
        services.actions.setActiveAudit(auditId);
        update({ ...stateRef.current, view: 'report' });
        if (services.hub) {
          void services.hub.subscribe([auditId]).catch(() => undefined);
        }
      }
    }),
    [windowState, update, services]
  );

  const value = useMemo(() => ({ services, navigation }), [services, navigation]);
  return <ShellContext.Provider value={value}>{children}</ShellContext.Provider>;
}

/** Accesses the shell context (throws outside the provider — a wiring bug). */
export function useShell(): ShellContextValue {
  const value = useContext(ShellContext);
  if (!value) throw new Error('useShell must be used within ShellProvider.');
  return value;
}

/** Convenience: the store-bound selector hook. */
export function useStoreSelector<T>(
  selector: (state: ReturnType<CrawlForgeStore['getState']>) => T
): T {
  const { services } = useShell();
  return services.hook(selector);
}

/**
 * Hub + bus lifecycle: starts the connection and relay timer on mount,
 * performs the initial local-aggregate loads, and tears everything down on
 * unmount (subscription cleanup, §7.4). Safe to mount twice (StrictMode).
 */
export function useShellLifecycle(services: ShellServices): void {
  useEffect(() => {
    let cancelled = false;
    services.bus.start();

    async function bootstrap() {
      if (services.api) {
        // Offline-first: loads degrade to error outcomes, never throw.
        await Promise.all([
          services.actions.loadSettings(),
          services.actions.loadProjects(),
          services.actions.loadAudits(),
          services.actions.loadPlugins()
        ]);
      }
      if (cancelled || !services.hub) return;
      try {
        await services.hub.start();
        // Re-subscribe the active audit, if any.
        const active = services.store.getState().audits.activeAuditId;
        if (active) await services.hub.subscribe([active]);
      } catch {
        // Connection failure is surfaced by the connection pill; the shell
        // stays usable offline (reconnect is automatic when configured).
      }
    }

    void bootstrap();

    return () => {
      cancelled = true;
      services.bus.dispose();
      if (services.hub) {
        void services.hub.stop().catch(() => undefined);
      }
    };
  }, [services]);
}

/** Default window state re-export for consumers. */
export { DEFAULT_WINDOW_STATE };
