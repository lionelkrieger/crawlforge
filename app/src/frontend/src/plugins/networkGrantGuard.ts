/**
 * Network grant guard (FS-CRAWL-002 §6.4, criterion #39; §2.6 boundary).
 *
 * Zero plugin network traffic without an explicit user grant. With a grant,
 * requests reach ONLY user destination rules (export destinations or explicit
 * host-allowlist entries) — never arbitrary hosts, never vendor services.
 * Pure functions; no I/O here.
 *
 * @module plugins/networkGrantGuard
 */

/** Local grant state for one plugin's network permission. */
export interface NetworkGrant {
  readonly mode: 'none' | 'destinations';
  readonly destinationRefs: readonly string[];
  /** Explicit user consent; false until granted in the management surface. */
  readonly granted: boolean;
}

/** The default grant: no network at all. */
export const NO_NETWORK_GRANT: NetworkGrant = { mode: 'none', destinationRefs: [], granted: false };

/** One user-configured destination rule (export destination / allowlist entry). */
export interface DestinationRule {
  readonly ref: string;
  readonly host: string;
  readonly port?: number;
  readonly scheme?: 'http' | 'https';
}

/** Guard decision with a human-readable reason for diagnostics. */
export interface NetworkDecision {
  readonly allowed: boolean;
  readonly reason: string;
}

/**
 * Decides whether a plugin request URL may proceed under the grant and the
 * user's destination rules. Denies by default; grants never widen the §2.6
 * boundary beyond user-configured destinations.
 */
export function isNetworkAllowed(
  url: string,
  grant: NetworkGrant,
  destinations: readonly DestinationRule[]
): NetworkDecision {
  if (grant.mode !== 'destinations' || !grant.granted) {
    return { allowed: false, reason: 'network access requires an explicit user grant' };
  }
  if (grant.destinationRefs.length === 0) {
    return { allowed: false, reason: 'grant has no destination references' };
  }

  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return { allowed: false, reason: 'invalid request URL' };
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return { allowed: false, reason: 'only http(s) destinations are permitted' };
  }

  const host = parsed.hostname.toLowerCase();
  const port = parsed.port.length > 0 ? Number(parsed.port) : parsed.protocol === 'https:' ? 443 : 80;
  const scheme = parsed.protocol === 'https:' ? 'https' : 'http';

  for (const ref of grant.destinationRefs) {
    const rule = destinations.find((destination) => destination.ref === ref);
    if (!rule) continue;
    if (rule.host.toLowerCase() !== host) continue;
    if (rule.scheme !== undefined && rule.scheme !== scheme) continue;
    // An unspecified port pins the scheme default — grants never widen to
    // arbitrary ports on a destination host.
    const expectedPort = rule.port ?? (scheme === 'https' ? 443 : 80);
    if (expectedPort !== port) continue;
    return { allowed: true, reason: `matched destination rule ${rule.ref}` };
  }
  return { allowed: false, reason: 'URL does not match any granted user destination rule' };
}

/**
 * The connect-src origins a sandboxed frame may receive under a grant:
 * empty (CSP 'none') without a grant, otherwise only granted destination
 * origins. Origins are deduplicated and sorted for determinism.
 */
export function grantedConnectOrigins(
  grant: NetworkGrant,
  destinations: readonly DestinationRule[]
): readonly string[] {
  if (grant.mode !== 'destinations' || !grant.granted) return [];
  const origins = new Set<string>();
  for (const ref of grant.destinationRefs) {
    const rule = destinations.find((destination) => destination.ref === ref);
    if (!rule) continue;
    const scheme = rule.scheme ?? 'https';
    const port = rule.port !== undefined ? `:${rule.port}` : '';
    origins.add(`${scheme}://${rule.host.toLowerCase()}${port}`);
  }
  return [...origins].sort();
}