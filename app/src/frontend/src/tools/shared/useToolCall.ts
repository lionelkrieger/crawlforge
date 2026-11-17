/**
 * Tool call hook: one envelope-bridged async call surface for every
 * standalone tool (FS-CRAWL-001 §8.7). Wraps the typed loopback client with
 * pending/error/fieldErrors state; offline-first (a null API degrades to a
 * message, never a throw). Wire facts (WP-05-01 handoff): envelope errors
 * carry code/message/fieldErrors (field → reasons map); null data on error.
 *
 * @module tools/shared/useToolCall
 */

import { useCallback, useState } from 'react';
import type { ApiEnvelope } from '../../contracts/common/envelope';
import type { ApiCallArgs } from '../../contracts/api/client';
import type { ApiRouteKey } from '../../contracts/api/routes';
import type { ApiRouteResponse } from '../../contracts/api/contracts-map';
import { useShell } from '../../shell/ShellContext';

/** Observable state of the most recent tool call. */
export interface ToolCallState {
  /** True while a call is in flight. */
  readonly pending: boolean;
  /** Envelope or transport error message; null when the last call succeeded. */
  readonly error: string | null;
  /** Field → reasons map from the envelope error (validation surfaces). */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>> | null;
}

/** The hook surface: state plus a typed run function and a reset. */
export interface ToolCall extends ToolCallState {
  /**
   * Performs one typed route call. Returns the response payload on success,
   * null on any failure (state carries the reason). Only enveloped routes
   * are used by the tools layer.
   */
  run<K extends ApiRouteKey>(route: K, ...args: ApiCallArgs<K>): Promise<ApiRouteResponse<K> | null>;
  /** Clears error/fieldErrors (keeps pending false). */
  reset(): void;
}

/** Creates the tool-call hook bound to the shell's API client. */
export function useToolCall(): ToolCall {
  const { services } = useShell();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Readonly<Record<string, readonly string[]>> | null>(null);

  const run = useCallback(
    async <K extends ApiRouteKey>(route: K, ...args: ApiCallArgs<K>): Promise<ApiRouteResponse<K> | null> => {
      if (!services.api) {
        setError('Backend disconnected — this tool needs the local backend.');
        setFieldErrors(null);
        return null;
      }
      setPending(true);
      setError(null);
      setFieldErrors(null);
      try {
        // Every tools-layer route is enveloped (frozen catalog); the cast
        // narrows the conditional ApiCallResult for envelope access.
        const envelope = (await services.api.request(route, ...args)) as ApiEnvelope<ApiRouteResponse<K>>;
        if (envelope.error) {
          setError(envelope.error.message);
          setFieldErrors(envelope.error.fieldErrors ?? null);
          return null;
        }
        return envelope.data;
      } catch {
        setError('Transport failure — the local backend did not answer.');
        return null;
      } finally {
        setPending(false);
      }
    },
    [services]
  );

  const reset = useCallback(() => {
    setError(null);
    setFieldErrors(null);
  }, []);

  return { pending, error, fieldErrors, run, reset };
}