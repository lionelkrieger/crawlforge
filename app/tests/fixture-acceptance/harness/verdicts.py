"""Per-trap verdict logic — MEASURED against pulled audit data, never hardcoded.

Expectations come from app/tests/fixture-site/expected-results.json (expect +
capture + flags). Verdict semantics (probe-2 §5 evidence conventions):
  pass         expected finding/state present
  partial      some required evidence present but a required finding/classification missing
  fail         expected detection absent though the trap was exercised (or a page
               that should be discovered is absent, e.g. the sitemap-only orphan)
  not_testable capture mode cannot exercise the trap (js/dual traps in http mode;
               js traps when rendering is DETECTED inert from evidence;
               mixed-content without TLS; safety trap vacuous when click
               discovery is inert; dual/adaptive modes have no wire support)

Rule-key matching uses the product's stable taxonomy (IndexabilityRuleKeys,
StructuredDataRuleKeys, AccessibilityRuleKeys, FindingRuleKeys) plus tolerant
substring probes so a future detector with a conforming key is picked up.
"""

from __future__ import annotations

import json

VERDICT_ENUM = ("pass", "partial", "fail", "not_testable")
BASE = "http://127.0.0.1:47311"


class Eval:
    """Measured-data accessor for one audit."""

    def __init__(self, data: dict, base_url: str = BASE):
        self.data = data or {}
        self.base = base_url.rstrip("/")
        self.urls = self.data.get("urls") or []
        self.links = self.data.get("links") or []
        self.findings = self.data.get("findings") or []
        self.details = self.data.get("trapUrlDetails") or {}
        self.audit_id = str(self.data.get("id") or (self.data.get("audit") or {}).get("id") or "?")

    def row(self, path: str):
        full = self.base + path
        for u in self.urls:
            if u.get("canonicalUrl") == full:
                return u
        for u in self.urls:
            if str(u.get("canonicalUrl") or "").startswith(full):
                return u
        return None

    def rows_prefix(self, prefix: str) -> list:
        full = self.base + prefix
        return [u for u in self.urls if str(u.get("canonicalUrl") or "").startswith(full)]

    def detail(self, path: str) -> dict:
        entry = self.details.get(path) or {}
        return entry.get("detail") or {}

    def url_findings(self, path: str) -> list:
        d = self.detail(path)
        if d.get("findings") is not None:
            return d.get("findings") or []
        row = self.row(path)
        if row is None:
            return []
        return [f for f in self.findings if str(f.get("urlId")) == str(row.get("id"))]

    def rule(self, findings: list, *needles: str, any_of: bool = False) -> list:
        out = []
        for f in findings:
            key = str(f.get("ruleKey") or "").lower()
            msg = str(f.get("message") or "").lower()
            hits = [n.lower() in key or n.lower() in msg for n in needles]
            if (any(hits) if any_of else all(hits)):
                out.append(f)
        return out

    @staticmethod
    def fbrief(findings: list, cap: int = 4) -> str:
        return "; ".join(
            f"{f.get('ruleKey')}[{f.get('severity')}] {str(f.get('message') or '')[:110]}"
            for f in findings[:cap]
        ) or "none"

    def words(self, path: str):
        scope = self.detail(path).get("scopeDiagnostics") or {}
        return scope.get("totalWordCount")

    def status(self, path: str):
        row = self.row(path)
        return None if row is None else row.get("statusCode")


def _nt(reason: str) -> tuple[str, str]:
    return "not_testable", reason


def _absent(ev: Eval, path: str, expectation: str) -> tuple[str, str]:
    return "fail", (
        f"audit {ev.audit_id}: {path} is absent from the URL inventory; {expectation} "
        "(the page should have been discovered through normal crawling)."
    )


# --- individual trap evaluators ---------------------------------------------

def t_redirect_chain(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/redirect/chain-1"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the three-hop chain seed was never crawled")
    d = ev.detail(path)
    chain = d.get("redirectChain") or []
    findings = ev.url_findings(path)
    chain_findings = ev.rule(findings, "redirect", "chain", any_of=False) or ev.rule(ev.findings, "redirect-matrix.chain")
    statuses = [h.get("statusCode") for h in chain]
    hop_ok = len(chain) >= 3 and statuses[:3] == [301, 302, 301]
    target_ok = any("/pages/redirect-target.html" in str(h.get("url") or h.get("location") or "") for h in chain) or \
        str(row.get("title") or "") == "Redirect chain target"
    if hop_ok and chain_findings:
        return "pass", f"audit {ev.audit_id}: redirectChain {json.dumps(chain)[:300]}; finding {ev.fbrief(chain_findings)}"
    return "partial", (
        f"audit {ev.audit_id}: {path} stored as statusCode={row.get('statusCode')} (title={row.get('title')!r}), "
        f"redirectChain length={len(chain)} statuses={statuses}; the required hop-by-hop 301>302>301 record "
        f"and a redirect-chain finding are absent (findings on URL: {ev.fbrief(findings, 3)}). "
        f"Final target reached={target_ok}."
    )


def t_redirect_loop(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/redirect/loop-a"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the loop seed was never crawled")
    findings = ev.url_findings(path)
    loop_findings = ev.rule(findings, "loop", any_of=True) + ev.rule(ev.findings, "redirect", "loop")
    completed = str(ctx.get("audit_state") or "").lower() == "completed"
    if loop_findings and completed:
        return "pass", f"audit {ev.audit_id}: loop reported ({ev.fbrief(loop_findings)}); audit completed without hanging."
    if completed:
        return "partial", (
            f"audit {ev.audit_id}: {path} stored as statusCode={row.get('statusCode')} "
            f"(statusClass={row.get('statusClass')}); the crawl did not hang (audit completed), "
            f"but no loop finding or loop classification was emitted (findings on URL: {ev.fbrief(findings, 3)})."
        )
    return "fail", f"audit {ev.audit_id}: audit did not complete cleanly (state={ctx.get('audit_state')}); loop handling unproven."


def t_noindex(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/noindex.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the noindex page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "indexability.noindex")
    if row.get("statusCode") == 200 and hits:
        directives = []
        for f in hits:
            det = ((f.get("evidence") or {}).get("details") or {})
            directives = det.get("directives") or det.get("robotsDirectives") or directives
        return "pass", (
            f"audit {ev.audit_id}: crawled 200 (words={ev.words(path)}); indexability.noindex emitted "
            f"(directives={directives}); {ev.fbrief(hits, 2)}"
        )
    if row.get("statusCode") == 200:
        return "fail", f"audit {ev.audit_id}: page crawled 200 but no indexability.noindex finding (findings: {ev.fbrief(findings, 4)})."
    return "fail", f"audit {ev.audit_id}: page not crawled successfully (statusCode={row.get('statusCode')})."


def t_robots_blocked(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/private/blocked.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the robots-blocked URL never entered the inventory")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "robots", any_of=True)
    block_reason = row.get("blockReason")
    not_fetched = row.get("statusCode") in (0, None)
    if not_fetched and (block_reason or hits):
        return "pass", f"audit {ev.audit_id}: not fetched (statusCode={row.get('statusCode')}), blockReason={block_reason!r}, finding {ev.fbrief(hits)}."
    if row.get("statusCode") == 200:
        return "fail", f"audit {ev.audit_id}: robots-disallowed URL was fetched (200) — robots.txt not honored."
    return "partial", (
        f"audit {ev.audit_id}: {path} was not fetched (statusCode={row.get('statusCode')}) but blockReason={block_reason!r} "
        f"and no robots finding was emitted (findings on URL: {ev.fbrief(findings, 3)}); "
        "the URL is not affirmatively reported as blocked by robots.txt."
    )


def t_canonical_conflict(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/canonical-conflict.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the canonical-conflict page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "canonical.header-html-conflict")
    mismatch = ev.rule(findings, "canonical.mismatch")
    # Real wire shape: headerConflicts is a LIST of conflict records
    # [{headerName, headerValue, htmlValue, conflict}] (measured audit 16).
    hc = ev.detail(path).get("headerConflicts")
    if isinstance(hc, list):
        conflict_flag = any(isinstance(c, dict) and c.get("conflict") for c in hc)
    elif isinstance(hc, dict):
        conflict_flag = hc.get("conflict")
    else:
        conflict_flag = None
    if hits:
        det = ((hits[0].get("evidence") or {}).get("details") or {})
        return "pass", (
            f"audit {ev.audit_id}: canonical.header-html-conflict emitted with headerTarget={det.get('headerTarget')!r} "
            f"htmlTarget={det.get('htmlTarget')!r}; canonical.mismatch count={len(mismatch)}; headerConflicts.conflict={conflict_flag}."
        )
    if row.get("statusCode") == 200:
        return "fail", f"audit {ev.audit_id}: page crawled 200 but no header-vs-HTML canonical conflict finding (findings: {ev.fbrief(findings, 4)})."
    return "fail", f"audit {ev.audit_id}: page not crawled successfully (statusCode={row.get('statusCode')})."


def t_broken_links(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/broken-links.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the broken-links page was never crawled")
    internal = ev.row("/pages/does-not-exist.html")
    internal_404 = internal is not None and internal.get("statusCode") == 404
    external_rows = [u for u in ev.urls if "broken.example.invalid" in str(u.get("canonicalUrl") or "")]
    external_checked = len(external_rows) > 0
    findings = ev.url_findings(path) + ev.findings
    broken_findings = ev.rule(findings, "broken", any_of=True)
    if internal_404 and external_checked and broken_findings:
        return "pass", f"audit {ev.audit_id}: internal 404 recorded, external checked, findings {ev.fbrief(broken_findings)}."
    if internal_404:
        return "partial", (
            f"audit {ev.audit_id}: internal 404 recorded for /pages/does-not-exist.html "
            f"(statusCode={internal.get('statusCode') if internal else None}), but no broken-internal/broken-external finding "
            f"(rule probe: {ev.fbrief(broken_findings, 2)}); broken.example.invalid checked={external_checked} "
            f"(rows={len(external_rows)})."
        )
    return "fail", (
        f"audit {ev.audit_id}: internal 404 not recorded (row present={internal is not None}, "
        f"statusCode={internal.get('statusCode') if internal else None}); external checked={external_checked}."
    )


def t_orphan(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/orphan.html"
    row = ev.row(path)
    if row is None:
        return "fail", (
            f"audit {ev.audit_id}: {path} is absent from the URL inventory although it is listed in /sitemap.xml; "
            "sitemap-only discovery does not exist, so the orphan can never be reported."
        )
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "orphan", any_of=True)
    inbound = ev.detail(path).get("inboundLinks") or []
    if hits:
        return "pass", f"audit {ev.audit_id}: orphan discovered and reported ({ev.fbrief(hits)}); inboundLinks={len(inbound)}."
    return "partial", (
        f"audit {ev.audit_id}: {path} discovered (statusCode={row.get('statusCode')}) but no orphan finding "
        f"(inboundLinks={len(inbound)}; findings: {ev.fbrief(findings, 3)})."
    )


def t_js_only_link(ev: Eval, ctx: dict) -> tuple[str, str]:
    target = ev.row("/pages/js-only-target.html")
    source = ev.row("/pages/js-link.html")
    if target is not None:
        findings = ev.url_findings("/pages/js-link.html") + ev.url_findings("/pages/js-only-target.html")
        delta = ev.rule(ev.findings, "render.links-delta") or ev.rule(findings, "render", "link", any_of=True)
        if delta:
            return "pass", f"audit {ev.audit_id}: /pages/js-only-target.html discovered under JS capture; rendered-link reporting {ev.fbrief(delta, 2)}."
        return "partial", f"audit {ev.audit_id}: JS-only target discovered but no rendered-only-link reporting (findings: {ev.fbrief(findings, 3)})."
    return "fail", (
        f"audit {ev.audit_id}: rendering was exercised but /pages/js-only-target.html was not discovered "
        f"(source /pages/js-link.html present={source is not None})."
    )


def t_script_injected_content(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/js-content.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the script-injected-content page was never crawled")
    words = ev.words(path)
    findings = ev.url_findings(path)
    delta = ev.rule(findings, "render.text-delta") or ev.rule(ev.findings, "render.text-delta")
    if words is not None and words >= 100 and delta:
        return "pass", f"audit {ev.audit_id}: rendered word count={words} with render.text-delta reporting."
    if words is not None and words >= 100:
        return "partial", f"audit {ev.audit_id}: rendered word count={words} but no raw-vs-rendered difference reporting."
    return "fail", f"audit {ev.audit_id}: rendered content absent (totalWordCount={words}); script-injected paragraph not captured."


def t_late_meta_change(ev: Eval, ctx: dict) -> tuple[str, str]:
    # Only reachable when a real dual-capture flow exists; gating above handles
    # http/js. Kept for completeness when dual support lands.
    delta = ev.rule(ev.findings, "render.metadata-delta") + ev.rule(ev.findings, "render.title-delta")
    if delta:
        return "pass", f"audit {ev.audit_id}: raw-vs-rendered metadata deltas reported ({ev.fbrief(delta, 3)})."
    return "fail", f"audit {ev.audit_id}: no raw-vs-rendered title/canonical/robots delta findings."


def t_soft_404_server(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/soft-404.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the soft-404 page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "indexability.soft-404")
    if hits:
        return "pass", f"audit {ev.audit_id}: 200 page classified soft-404 ({ev.fbrief(hits, 2)})."
    return "fail", (
        f"audit {ev.audit_id}: {path} crawled statusCode={row.get('statusCode')} with 'Page not found' body "
        f"but no indexability.soft-404 finding (findings: {ev.fbrief(findings, 4)})."
    )


def t_soft_404_client_route(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/spa/does-not-exist"
    row = ev.row(path)
    if row is None:
        return "fail", f"audit {ev.audit_id}: SPA unknown route {path} absent; client-side soft-404 not evaluated."
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "soft-404", any_of=True)
    if hits:
        return "pass", f"audit {ev.audit_id}: client-rendered 'Page not found' classified soft-404 ({ev.fbrief(hits)})."
    return "fail", f"audit {ev.audit_id}: SPA route crawled but no soft-404 classification (findings: {ev.fbrief(findings, 3)})."


def t_hash_route(ev: Eval, ctx: dict) -> tuple[str, str]:
    rows = ev.rows_prefix("/hash-app/")
    views = [u for u in rows if "#" in str(u.get("canonicalUrl") or "")]
    findings = ev.rule(ev.findings, "hash", any_of=True)
    if views and findings:
        return "pass", f"audit {ev.audit_id}: hash views captured as distinct states ({len(views)}); findings {ev.fbrief(findings, 2)}."
    if views:
        return "partial", f"audit {ev.audit_id}: {len(views)} hash-view rows but no hash-route handling report."
    return "fail", f"audit {ev.audit_id}: no /hash-app/#/... view states discovered (rows under /hash-app/: {len(rows)})."


def t_client_side_redirect(ev: Eval, ctx: dict) -> tuple[str, str]:
    target = ev.row("/pages/js-redirect-target.html")
    findings = ev.rule(ev.findings, "redirect", any_of=True)
    render_redirect = [f for f in findings if "render" in str(f.get("ruleKey") or "") or "client" in str(f.get("message") or "").lower()]
    if target is not None and render_redirect:
        return "pass", f"audit {ev.audit_id}: JS redirect target reached and reported ({ev.fbrief(render_redirect, 2)})."
    if target is not None:
        return "partial", f"audit {ev.audit_id}: target discovered but the client-side redirect itself is not reported."
    return "fail", f"audit {ev.audit_id}: /pages/js-redirect-target.html not discovered; client-side redirect not followed."


def t_postback_links(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/postback.aspx"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the postback page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "postback", any_of=True) + ev.rule(findings, "uncrawlable", any_of=True) + \
        ev.rule(ev.findings, "postback", any_of=True)
    if hits:
        return "pass", f"audit {ev.audit_id}: postback links reported as uncrawlable navigation ({ev.fbrief(hits, 2)})."
    return "fail", (
        f"audit {ev.audit_id}: {path} crawled statusCode={row.get('statusCode')} but the two javascript:__doPostBack "
        f"links are not reported as uncrawlable navigation (findings: {ev.fbrief(findings, 4)})."
    )


def t_cookieless_session(ev: Eval, ctx: dict) -> tuple[str, str]:
    normalized = ev.row("/pages/session.aspx")
    raw_rows = [u for u in ev.urls if "/(S(" in str(u.get("canonicalUrl") or "") or "/(A(" in str(u.get("canonicalUrl") or "") or "/(F(" in str(u.get("canonicalUrl") or "")]
    findings = ev.rule(ev.findings, "cookieless", any_of=True) + ev.rule(ev.findings, "session", "normaliz", any_of=False)
    norm_ok = normalized is not None and str(normalized.get("canonicalUrl")) == ev.base + "/pages/session.aspx" and not raw_rows
    if norm_ok and findings:
        return "pass", f"audit {ev.audit_id}: session segment stripped and reported ({ev.fbrief(findings, 2)})."
    if norm_ok:
        return "partial", f"audit {ev.audit_id}: URL normalized to /pages/session.aspx but no normalization report."
    return "fail", (
        f"audit {ev.audit_id}: cookieless-session URL stored unchanged "
        f"(raw session-segment rows={len(raw_rows)}, e.g. {raw_rows[0].get('canonicalUrl') if raw_rows else 'n/a'}); "
        f"no session-segment normalization or finding."
    )


def t_large_viewstate(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/viewstate-large.aspx"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the large-ViewState page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "viewstate", any_of=True) + ev.rule(ev.findings, "viewstate", any_of=True)
    if hits:
        return "pass", f"audit {ev.audit_id}: ViewState size flagged ({ev.fbrief(hits, 2)})."
    return "fail", (
        f"audit {ev.audit_id}: {path} crawled statusCode={row.get('statusCode')} (~152 KB __VIEWSTATE hidden input) "
        f"but no ViewState size measurement or >100 KB finding (findings: {ev.fbrief(findings, 4)})."
    )


def _trap_family(ev: Eval, ctx: dict, prefix: str, label: str) -> tuple[str, str]:
    rows = ev.rows_prefix(prefix)
    findings = ev.rule(ev.findings, "trap", any_of=True)
    throttled = [u for u in rows if u.get("blockReason") and "trap" in str(u.get("blockReason")).lower()]
    if findings or throttled:
        return "pass", (
            f"audit {ev.audit_id}: {label} combination explosion detected/reported "
            f"(trap findings={len(findings)}, throttled rows={len(throttled)}, crawled rows={len(rows)}); audit finished."
        )
    return "fail", (
        f"audit {ev.audit_id}: {len(rows)} {label} combination URLs crawled with no trap detection or throttling "
        f"(trap findings={len(findings)}); the run was bounded only by the explicit limit overrides "
        f"{json.dumps(ctx.get('limit_overrides') or {})} — the cap, not the product, ended the run."
    )


def t_crawler_trap_filter(ev: Eval, ctx: dict) -> tuple[str, str]:
    return _trap_family(ev, ctx, "/trap/filter", "faceted-filter")


def t_crawler_trap_calendar(ev: Eval, ctx: dict) -> tuple[str, str]:
    return _trap_family(ev, ctx, "/trap/calendar/", "calendar")


def t_cookie_gate(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/cookie-gate/"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the cookie-gate URL never entered the inventory")
    findings = ev.url_findings(path)
    if row.get("statusCode") == 200:
        return "pass", f"audit {ev.audit_id}: cookie jar honored; /cookie-gate/ reached 200 after the Set-Cookie redirect."
    chain = ev.detail(path).get("redirectChain") or []
    if chain:
        return "partial", f"audit {ev.audit_id}: redirect recorded ({len(chain)} hops) but gate not passed (statusCode={row.get('statusCode')})."
    return "fail", (
        f"audit {ev.audit_id}: {path} stored statusCode={row.get('statusCode')} with no successful cookie-gated navigation "
        f"and no loop finding (findings: {ev.fbrief(findings, 3)})."
    )


def t_consent_banner(ev: Eval, ctx: dict) -> tuple[str, str]:
    tags = ctx.get("tag_hits") or {}
    findings = ev.rule(ev.findings, "consent", any_of=True)
    before = tags.get("beforeConsent", 0)
    if before > 0 and findings:
        return "pass", f"audit {ev.audit_id}: {before} tag hit(s) fired before consent and are reported ({ev.fbrief(findings, 2)})."
    if before > 0:
        return "partial", f"audit {ev.audit_id}: {before} pre-consent tag hit(s) recorded by the fixture but no consent-violation finding."
    return "fail", f"audit {ev.audit_id}: no pre-consent tag hits observed although rendering was exercised (tagHits={tags.get('total', 0)})."


def t_service_worker(ev: Eval, ctx: dict) -> tuple[str, str]:
    findings = ev.rule(ev.findings, "service-worker", any_of=True) + ev.rule(ev.findings, "service worker", any_of=True)
    row = ev.row("/sw-app/")
    if findings:
        return "pass", f"audit {ev.audit_id}: service worker detected/reported ({ev.fbrief(findings, 2)})."
    return "fail", f"audit {ev.audit_id}: /sw-app/ crawled={row is not None} but no service-worker detection or bypass reporting."


def t_structured_data_errors(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/structured-data.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the structured-data page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "structured-data.malformed-block") + ev.rule(findings, "structured-data.invalid") + \
        ev.rule(findings, "missing-required", any_of=True)
    if hits:
        return "pass", f"audit {ev.audit_id}: structured-data validation errors reported ({ev.fbrief(hits, 3)})."
    return "fail", (
        f"audit {ev.audit_id}: {path} crawled statusCode={row.get('statusCode')} (Article missing required properties + "
        f"malformed JSON-LD block) but no structured-data validation finding (findings: {ev.fbrief(findings, 4)})."
    )


def t_missing_alt(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/images.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the images page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "accessibility.image-alt")
    if hits:
        return "pass", f"audit {ev.audit_id}: accessibility.image-alt emitted ({ev.fbrief(hits, 2)})."
    return "fail", f"audit {ev.audit_id}: {path} crawled statusCode={row.get('statusCode')} but no missing-alt finding (findings: {ev.fbrief(findings, 4)})."


def t_mixed_content(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/mixed-content.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the mixed-content page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "mixed-content", any_of=True) + ev.rule(findings, "insecure", any_of=True)
    if hits:
        return "pass", f"audit {ev.audit_id}: insecure http image on https page flagged ({ev.fbrief(hits, 2)})."
    return "fail", f"audit {ev.audit_id}: HTTPS page crawled but the http:// image is not flagged as mixed content."


def t_hreflang_no_return(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/hreflang-en.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the hreflang page was never crawled")
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "hreflang-missing-reciprocity") + ev.rule(findings, "return-tag", any_of=True) + \
        ev.rule(findings, "hreflang", "return", any_of=False)
    valid_pass = ev.rule(findings, "i18n.hreflang-valid")
    if hits:
        return "pass", f"audit {ev.audit_id}: missing return tag reported ({ev.fbrief(hits, 2)})."
    return "fail", (
        f"audit {ev.audit_id}: {path} crawled but no missing-return-tag finding; "
        f"i18n.hreflang-valid pass findings={len(valid_pass)} (emitting a pass while reciprocity is missing)."
    )


def t_duplicate_titles(ev: Eval, ctx: dict) -> tuple[str, str]:
    a = ev.row("/pages/dup-title-a.html")
    b = ev.row("/pages/dup-title-b.html")
    if a is None or b is None:
        return "fail", f"audit {ev.audit_id}: duplicate-title pages not both crawled (a={a is not None}, b={b is not None})."
    findings = ev.url_findings("/pages/dup-title-a.html") + ev.url_findings("/pages/dup-title-b.html")
    hits = ev.rule(findings, "duplicate-title", any_of=True) + ev.rule(ev.findings, "duplicate-title", any_of=True)
    if hits:
        return "pass", f"audit {ev.audit_id}: both pages share the identical title and a duplicate-title finding is emitted ({ev.fbrief(hits, 2)})."
    # content.keyword-cannibalization is an audit-level finding (urlId=null);
    # the affected pages live in evidence.details.members[].url.
    related = []
    pair = {ev.base + "/pages/dup-title-a.html", ev.base + "/pages/dup-title-b.html"}
    for f in ev.findings:
        if str(f.get("ruleKey") or "") != "content.keyword-cannibalization":
            continue
        members = ((f.get("evidence") or {}).get("details") or {}).get("members") or []
        if pair.issubset({str(m.get("url") or "") for m in members}):
            related.append(f)
    if related:
        return "partial", (
            f"audit {ev.audit_id}: both pages crawled with identical title {a.get('title')!r}; "
            f"content.keyword-cannibalization emitted ({len(related)}) but no duplicate-title finding."
        )
    return "fail", f"audit {ev.audit_id}: identical titles on both pages but no duplicate-title or related finding."


def t_thin_page(ev: Eval, ctx: dict) -> tuple[str, str]:
    path = "/pages/thin.html"
    row = ev.row(path)
    if row is None:
        return _absent(ev, path, "the thin page was never crawled")
    words = ev.words(path)
    findings = ev.url_findings(path)
    hits = ev.rule(findings, "content.thin-text")
    if hits:
        return "pass", f"audit {ev.audit_id}: content.thin-text emitted on {path} (wordCount={words})."
    return "fail", (
        f"audit {ev.audit_id}: {path} has totalWordCount={words} but no content.thin-text finding on this URL "
        f"(findings on URL: {ev.fbrief(findings, 4)}); thin-content detection missed the named fixture page."
    )


def t_live_websocket(ev: Eval, ctx: dict) -> tuple[str, str]:
    findings = ev.rule(ev.findings, "websocket", any_of=True) + ev.rule(ev.findings, "network-quiet", any_of=True) + \
        ev.rule(ev.findings, "never quiet", any_of=True)
    row = ev.row("/live/odds.html")
    if findings:
        return "pass", f"audit {ev.audit_id}: continuous websocket activity detected/reported ({ev.fbrief(findings, 2)})."
    return "fail", f"audit {ev.audit_id}: /live/odds.html crawled={row is not None} but no never-quiet/websocket detection."


def t_click_only_navigation(ev: Eval, ctx: dict) -> tuple[str, str]:
    views = [p for p in ("/spa/markets", "/spa/promotions", "/spa/results") if ev.row(p) is not None]
    findings = ev.rule(ev.findings, "discovered by click", any_of=True) + ev.rule(ev.findings, "non-crawlable", any_of=True) + \
        ev.rule(ev.findings, "uncrawlable", any_of=True)
    if len(views) == 3 and findings:
        return "pass", f"audit {ev.audit_id}: click-only views discovered ({views}) and classified ({ev.fbrief(findings, 2)})."
    if views:
        return "partial", f"audit {ev.audit_id}: some click-only views discovered ({views}) but no click/non-crawlable classification."
    return "fail", f"audit {ev.audit_id}: no click-only SPA views discovered although click discovery was exercised."


def t_never_place_bet(ev: Eval, ctx: dict) -> tuple[str, str]:
    req = ctx.get("fixture_requests") or {}
    non_get = req.get("nonGet") or []
    if non_get:
        return "fail", f"audit {ev.audit_id}: SAFETY VIOLATION — nonGet={json.dumps(non_get)[:400]} (a Place bet/Deposit/Logout control was triggered)."
    return "pass", f"audit {ev.audit_id}: click discovery exercised and nonGet == [] — no betting/deposit/logout control triggered."


EVALUATORS = {
    "redirect-chain": t_redirect_chain,
    "redirect-loop": t_redirect_loop,
    "noindex": t_noindex,
    "robots-blocked": t_robots_blocked,
    "canonical-conflict": t_canonical_conflict,
    "broken-links": t_broken_links,
    "orphan": t_orphan,
    "js-only-link": t_js_only_link,
    "script-injected-content": t_script_injected_content,
    "late-meta-change": t_late_meta_change,
    "soft-404-client-route": t_soft_404_client_route,
    "soft-404-server": t_soft_404_server,
    "hash-route": t_hash_route,
    "client-side-redirect": t_client_side_redirect,
    "postback-links": t_postback_links,
    "cookieless-session": t_cookieless_session,
    "large-viewstate": t_large_viewstate,
    "crawler-trap-filter": t_crawler_trap_filter,
    "crawler-trap-calendar": t_crawler_trap_calendar,
    "cookie-gate": t_cookie_gate,
    "consent-banner": t_consent_banner,
    "service-worker": t_service_worker,
    "structured-data-errors": t_structured_data_errors,
    "missing-alt": t_missing_alt,
    "mixed-content": t_mixed_content,
    "hreflang-no-return": t_hreflang_no_return,
    "duplicate-titles": t_duplicate_titles,
    "thin-page": t_thin_page,
    "live-websocket": t_live_websocket,
    "click-only-navigation": t_click_only_navigation,
    "never-place-bet": t_never_place_bet,
}


def evaluate_trap(trap: dict, mode: str, ctx: dict, data: dict) -> dict:
    """Gate by capture mode / flags / detected inertness, then measure."""
    tid = trap["id"]
    capture = trap.get("capture", "any")
    flags = trap.get("flags") or []
    ev = Eval(data, ctx.get("base_url") or BASE)

    def nt(reason: str) -> dict:
        return {"result": "not_testable", "evidence": reason}

    # 1. Modes with no wire-contract support cannot be exercised at all.
    if mode in ("dual", "adaptive"):
        wire = ctx.get("wire_probe_evidence") or "no dual/adaptive capture field exists in RenderingSettingsWire"
        return nt(f"capture mode '{mode}' has no wire-contract support on this build ({wire}); the mode cannot be exercised through any normal entry point, so no trap of this mode is testable.")

    # 2. Capture gating.
    if capture == "dual" and mode in ("http", "js"):
        return nt(f"trap requires dual capture; --mode {mode} cannot exercise it (expected-results.json capture='dual').")
    if capture == "js" and mode == "http":
        return nt(f"trap requires JS capture; --mode http runs with rendering.enabled=false, so it cannot be exercised.")
    if "requires_tls" in flags and not ctx.get("tls_active"):
        return nt("trap flags requires_tls; the fixture is served over plain HTTP (no --tls-cert/--tls-key), so mixed content cannot exist.")

    # 3. Detected rendering inertness (js mode only — evidence-driven).
    inert = ctx.get("rendering_inert") or {}
    if capture == "js" and mode == "js" and inert.get("inert"):
        specifics = {
            "js-only-link": "/pages/js-only-target.html is absent although rendering.enabled=true",
            "script-injected-content": f"/pages/js-content.html totalWordCount={ev.words('/pages/js-content.html')} (raw markup only)",
            "late-meta-change": "no rendered DOM exists to compare against raw metadata",
            "soft-404-client-route": "/spa/does-not-exist is absent; the SPA shell is not rendered",
            "hash-route": "hash views are not rendered or captured",
            "client-side-redirect": "the meta-refresh JS redirect is not executed in a rendered tree",
            "consent-banner": f"fixture tagHits={len((ctx.get('fixture_requests') or {}).get('tagHits') or [])}; the banner script never runs",
            "service-worker": "service-worker registration requires a rendered page",
            "live-websocket": "the websocket feed requires a rendered page and network-quiet detection",
            "click-only-navigation": "click discovery is inert; SPA views were not discovered",
            "never-place-bet": "click discovery is inert, so the safety assertion is vacuous rather than an exercised click-safety pass",
        }
        extra = specifics.get(tid, "rendering produced no rendered tree")
        non_get_note = ""
        if tid == "never-place-bet":
            req = ctx.get("fixture_requests") or {}
            non_get_note = f" nonGet={json.dumps(req.get('nonGet') or [])}, tagHits={len(req.get('tagHits') or [])} (global safety assertion still checked separately)."
        return nt(f"rendering is DETECTED inert in this js run ({inert.get('evidence')}); {extra}.{non_get_note}")

    # 4. Click-discovery gating for the safety trap when rendering works but
    #    click discovery produced nothing (vacuous pass is not a pass).
    if tid == "never-place-bet":
        click = ctx.get("click_activity") or {}
        if not click.get("active"):
            return nt(f"click discovery produced no exercised navigation ({click.get('evidence')}); nonGet==[] is vacuous, not an exercised click-safety pass.")

    evaluator = EVALUATORS.get(tid)
    if evaluator is None:
        return nt(f"no expectation logic implemented for trap '{tid}' (harness gap — report to lead).")
    result, evidence = evaluator(ev, ctx)
    return {"result": result, "evidence": evidence}


def merge_audit_data(datas: list[dict], base_url: str = BASE) -> dict:
    """Pool the session's bulk runs into one evidence view (probe-2 convention:
    its §5 trap table cites 'Audit 11/12' jointly for both mode columns).
    Per-run measurements are still recorded separately in each run record, so
    nothing is hidden — e.g. the js run's time-cap truncation stays visible."""
    merged: dict = {
        "id": "+".join(str(d.get("id") or "?") for d in datas),
        "urls": [], "links": [], "findings": [], "trapUrlDetails": {},
    }
    seen_urls: set = set()
    seen_findings: set = set()
    for d in datas:
        for u in d.get("urls") or []:
            key = u.get("canonicalUrl")
            if key not in seen_urls:
                seen_urls.add(key)
                merged["urls"].append(u)
        merged["links"].extend(d.get("links") or [])
        for f in d.get("findings") or []:
            key = (f.get("ruleKey"), str(f.get("urlId")), str(f.get("message") or "")[:120])
            if key not in seen_findings:
                seen_findings.add(key)
                merged["findings"].append(f)
        for path, entry in (d.get("trapUrlDetails") or {}).items():
            cur = merged["trapUrlDetails"].get(path)
            if cur is None or (not cur.get("found") and entry.get("found")):
                merged["trapUrlDetails"][path] = entry
    return merged


def evaluate_matrix(traps: list[dict], mode: str, ctx: dict, data: dict, selected: list[str] | None = None) -> dict:
    """Evaluate every trap (or the selected subset) for one run."""
    out: dict = {}
    for trap in traps:
        if selected is not None and trap["id"] not in selected:
            continue
        out[trap["id"]] = evaluate_trap(trap, mode, ctx, data)
    return out


def count_verdicts(trap_verdicts: dict) -> dict:
    counts = {v: 0 for v in VERDICT_ENUM}
    for v in trap_verdicts.values():
        result = v.get("result")
        if result in counts:  # invalid enums are flagged by the shape validator, not counted
            counts[result] += 1
    return counts