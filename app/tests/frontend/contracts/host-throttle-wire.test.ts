/**
 * HOTFIX-01 contract conformance, frontend half.
 *
 * The C# test CrawlForge.Host.Tests.Contracts.WireShapeContractTests
 * serialises the real HubHostThrottleSink payload and asserts these exact
 * field names and values. This test feeds the same payload to the real hub
 * guard and demultiplexer, and checks the run-view formatter tolerates a
 * missing numeric field. Both halves fail on the old wire shape.
 */
import { describe, expect, it } from 'vitest';
import { isHubServerEvent } from '../../../src/frontend/src/contracts';
import { formatMs } from '../../../src/frontend/src/shell/format';
import { demuxGraph } from '../shell/helpers';

/** Mirrors the producer values asserted by the C# contract test. */
const producerPayload = {
  auditId: '7',
  host: 'example.test',
  trigger: 'additive-increase',
  rateBefore: 1.0,
  rateAfter: 1.5,
  concurrencyBefore: 1,
  concurrencyAfter: 2,
  ttfbSampleMs: 120.5,
  rollingBaselineRate: 110.25,
  timestamp: '2026-11-17T10:00:00Z'
};

describe('host-throttle wire contract (backend -> frontend)', () => {
  it('the real producer payload passes the real hub guard', () => {
    expect(isHubServerEvent({ event: 'host-throttle', payload: producerPayload })).toBe(true);
  });

  it('the demultiplexer maps producer fields into the run-view host state', () => {
    const { store, demux } = demuxGraph();
    demux.ingest({ event: 'host-throttle', payload: producerPayload });

    const host = store.getState().hostThrottle.byHostKey['7:example.test'];
    expect(host.effectiveRate).toBe(1.5);
    expect(host.concurrency).toBe(2);
    expect(host.ttfbEwmaMs).toBe(120.5);
    expect(host.throttled).toBe(false);
  });

  it('a missing numeric field renders a dash rather than crashing', () => {
    expect(formatMs(undefined)).toBe('—');
    expect(formatMs(null)).toBe('—');
    expect(formatMs(Number.NaN)).toBe('—');
    expect(formatMs(120.5)).toBe('121 ms');
  });
});
