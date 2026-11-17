/**
 * CrawlForge frontend contracts — the Phase-00 contract lock.
 *
 * Every frontend workpiece codes against these types:
 * - `common`  — uniform envelope, pagination, primitives, guards
 * - `api`     — all 88 REST route groups (51 baseline + 37 additive) as a
 *               frozen catalog plus per-route DTO contracts and the typed
 *               loopback client surface
 * - `hub`     — all 19 hub event contracts (11 baseline incl. the
 *               subscribe/unsubscribe channel + 8 additive) as discriminated
 *               unions, guards, and the SignalR client contract
 * - `store`   — normalized Zustand-class store shape, store API, selectors
 * - `plugins` — lifecycle hook bus (onLoad / onTabActivate / onDataUpdate /
 *               onCrawlComplete) and the read-only plugin bridge
 *
 * Spec surface: FS-CRAWL-001 §10.1–§10.4; FS-CRAWL-002 §7.2, §8.8,
 * Appendix B, Appendix C. Clean-room: no identity/account/license/
 * entitlement concepts appear in any contract (exclusions #1–#8, E1–E9).
 *
 * Runtime code is limited to type guards and frozen declarative catalogs.
 *
 * @module contracts
 */

export * from './common';
export * from './api';
export * from './hub';
export * from './store';
export * from './plugins';
