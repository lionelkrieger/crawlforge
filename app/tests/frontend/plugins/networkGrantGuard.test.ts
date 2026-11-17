/**
 * Network grant guard tests (FS-CRAWL-002 §6.4, criterion #39, §2.6):
 * zero network without a grant; with a grant, only user destination rules;
 * CSP connect origins follow the grant exactly.
 */

import { describe, expect, it } from 'vitest';
import {
  grantedConnectOrigins,
  isNetworkAllowed,
  NO_NETWORK_GRANT,
  type DestinationRule,
  type NetworkGrant
} from '../../../src/frontend/src/plugins/networkGrantGuard';

const destinations: readonly DestinationRule[] = [
  { ref: 'warehouse', host: 'warehouse.internal.example', port: 8443, scheme: 'https' },
  { ref: 'allowlist-host', host: 'metrics.user.example' }
];

const granted: NetworkGrant = { mode: 'destinations', destinationRefs: ['warehouse', 'allowlist-host'], granted: true };

describe('zero network without a grant', () => {
  it('denies everything under the default grant', () => {
    for (const url of ['https://warehouse.internal.example:8443/push', 'https://anywhere.test', 'http://127.0.0.1:5000/api/health']) {
      const decision = isNetworkAllowed(url, NO_NETWORK_GRANT, destinations);
      expect(decision.allowed, url).toBe(false);
      expect(decision.reason).toContain('grant');
    }
  });

  it('denies when mode is destinations but consent is missing', () => {
    const ungranted: NetworkGrant = { ...granted, granted: false };
    expect(isNetworkAllowed('https://warehouse.internal.example:8443/push', ungranted, destinations).allowed).toBe(false);
  });

  it('denies when the grant references no destinations', () => {
    const empty: NetworkGrant = { mode: 'destinations', destinationRefs: [], granted: true };
    expect(isNetworkAllowed('https://warehouse.internal.example:8443/', empty, destinations).allowed).toBe(false);
  });
});

describe('granted destinations only', () => {
  it('allows exact host/port/scheme matches', () => {
    expect(isNetworkAllowed('https://warehouse.internal.example:8443/push', granted, destinations).allowed).toBe(true);
    expect(isNetworkAllowed('https://metrics.user.example/collect', granted, destinations).allowed).toBe(true);
  });

  it('rejects hosts outside the granted rules', () => {
    for (const url of [
      'https://evil.test/steal',
      'https://sub.warehouse.internal.example:8443/',
      'https://warehouse.internal.example:9999/',
      'http://warehouse.internal.example:8443/',
      'https://metrics.user.example:8443/'
    ]) {
      const decision = isNetworkAllowed(url, granted, destinations);
      expect(decision.allowed, url).toBe(false);
    }
  });

  it('rejects non-http schemes and invalid URLs', () => {
    expect(isNetworkAllowed('ftp://warehouse.internal.example:8443/', granted, destinations).allowed).toBe(false);
    expect(isNetworkAllowed('file:///etc/passwd', granted, destinations).allowed).toBe(false);
    expect(isNetworkAllowed('not a url', granted, destinations).allowed).toBe(false);
  });

  it('ignores refs with no matching user rule', () => {
    const dangling: NetworkGrant = { mode: 'destinations', destinationRefs: ['no-such-rule'], granted: true };
    expect(isNetworkAllowed('https://warehouse.internal.example:8443/', dangling, destinations).allowed).toBe(false);
  });

  it('matches hosts case-insensitively', () => {
    expect(isNetworkAllowed('https://WAREHOUSE.INTERNAL.EXAMPLE:8443/', granted, destinations).allowed).toBe(true);
  });
});

describe('CSP connect origins', () => {
  it('is empty without a grant (connect-src none)', () => {
    expect(grantedConnectOrigins(NO_NETWORK_GRANT, destinations)).toEqual([]);
    expect(grantedConnectOrigins({ ...granted, granted: false }, destinations)).toEqual([]);
  });

  it('lists only granted destination origins, sorted and deduplicated', () => {
    const origins = grantedConnectOrigins(granted, destinations);
    expect(origins).toEqual(['https://metrics.user.example', 'https://warehouse.internal.example:8443']);
  });
});