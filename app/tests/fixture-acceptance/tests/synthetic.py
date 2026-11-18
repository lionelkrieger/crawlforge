"""Probe-2-shaped synthetic audit data for verdict-logic self-tests.

Shapes mirror the REAL REST explorer payloads measured in
audit-work/reports/runtime-probe-2/logs/audit-11-data.json (read-only
reference): url rows {id, canonicalUrl, statusCode, blockReason, title},
findings {ruleKey, severity, urlId, message, evidence.details}, URL details
{findings, redirectChain, scopeDiagnostics}. Audit-level findings such as
content.keyword-cannibalization carry urlId=null and members[].url.
"""

from __future__ import annotations

BASE = "http://127.0.0.1:47311"

# (id, path, statusCode, title, blockReason)
URLS = [
    (1, "/", 200, "CrawlForge fixture home", None),
    (2, "/redirect/chain-1", 200, "Redirect chain target", None),
    (3, "/redirect/loop-a", 0, None, None),
    (4, "/private/blocked.html", 0, None, None),
    (5, "/pages/noindex.html", 200, "Noindex page", None),
    (6, "/pages/canonical-conflict.html", 200, "Canonical conflict", None),
    (7, "/pages/does-not-exist.html", 404, "Not found", None),
    (8, "/pages/broken-links.html", 200, "Broken links", None),
    (9, "/pages/thin.html", 200, "Thin", None),
    (10, "/pages/dup-title-a.html", 200, "Duplicate fixture title", None),
    (11, "/pages/dup-title-b.html", 200, "Duplicate fixture title", None),
    (12, "/pages/images.html", 200, "Images", None),
    (13, "/pages/soft-404.html", 200, "Page not found", None),
    (14, "/pages/postback.aspx", 200, "Postback page", None),
    (15, "/pages/viewstate-large.aspx", 200, "Large ViewState", None),
    (16, "/(S(abc123))/pages/session.aspx", 200, "Cookieless session", None),
    (17, "/cookie-gate/", 0, None, None),
    (18, "/trap/filter?color=red", 200, "Product filter", None),
    (19, "/trap/calendar/2026/11", 200, "Events 2026-11", None),
    (20, "/pages/hreflang-en.html", 200, "Hreflang page (English)", None),
    (21, "/pages/structured-data.html", 200, "Structured data", None),
    (22, "/pages/js-link.html", 200, "JS link page", None),
    (23, "/pages/js-content.html", 200, "Script content", None),
    (24, "/pages/late-meta.html", 200, "Original title", None),
    (25, "/pages/js-redirect.html", 200, "Redirecting", None),
    (26, "/spa/", 200, "Fixture SPA", None),
    (27, "/hash-app/", 200, "Hash app", None),
    (28, "/sw-app/", 200, "Service worker app", None),
    (29, "/live/odds.html", 200, "Live odds", None),
    (30, "/pages/mixed-content.html", 200, "Mixed content", None),
    # /pages/orphan.html and /pages/js-only-target.html deliberately ABSENT.
]

WORDS = {"/pages/thin.html": 6, "/pages/js-content.html": 2, "/pages/noindex.html": 154}


def _f(fid, rule, url_id, message, severity="warning", details=None):
    return {"id": str(fid), "ruleKey": rule, "severity": severity, "urlId": None if url_id is None else str(url_id),
            "message": message, "evidence": {"snippet": None, "selector": None, "details": details or {}}}


def build_findings():
    return [
        _f(101, "indexability.noindex", 5, "noindex", "notice",
           {"source": "meta-robots", "directives": ["noindex", "follow"]}),
        _f(102, "indexability.soft-404", 13, "soft 404", "warning",
           {"url": BASE + "/pages/soft-404.html", "finalStatusCode": 200}),
        _f(103, "accessibility.image-alt", 12, "img missing alt", "error",
           {"selector": "img[src='/static/pixel.png']"}),
        _f(104, "canonical.header-html-conflict", 6, "canonical header html conflict", "error",
           {"headerTarget": "/pages/canonical-target-header.html", "htmlTarget": "/pages/canonical-target-html.html"}),
        _f(105, "canonical.mismatch", 6, "canonical mismatch", "warning",
           {"canonicalTarget": BASE + "/pages/canonical-target-html.html", "source": "html"}),
        _f(106, "content.keyword-cannibalization", None, "content keyword cannibalization", "warning",
           {"term": "duplicate title", "zone": "title",
            "members": [{"urlId": 10, "url": BASE + "/pages/dup-title-a.html"},
                        {"urlId": 11, "url": BASE + "/pages/dup-title-b.html"}]}),
        _f(107, "i18n.hreflang-valid", 20, "hreflang valid", "pass", {}),
        _f(108, "content.thin-text", 23, "thin text", "notice", {"wordCount": 2}),
        _f(109, "content.metrics", 9, "content metrics", "notice", {"wordCount": 6}),
        _f(110, "mobile.render-required", 23,
           "Check 'mobile.render-required' requires rendering and was not evaluated: no rendered tree is available.",
           "notice", {"requiresRendering": True}),
    ]


def build_data(extra_urls=(), extra_findings=(), words_override=None, redirect_chains=None):
    urls = [{"id": str(i), "canonicalUrl": BASE + p, "statusCode": sc, "statusClass": f"{sc // 100}xx" if sc else "5xx",
             "title": t, "blockReason": br, "depth": 0 if p == "/" else 1, "indexable": sc == 200}
            for (i, p, sc, t, br) in list(URLS) + list(extra_urls)]
    findings = build_findings() + list(extra_findings)
    words = dict(WORDS, **(words_override or {}))
    chains = redirect_chains or {}
    details = {}
    # every trap URL referenced by expected-results.json
    import json
    from pathlib import Path
    expected = json.loads((Path(__file__).resolve().parents[2] / "fixture-site" / "expected-results.json").read_text(encoding="utf-8"))
    for trap in expected["traps"]:
        for path in trap["urls"]:
            full = BASE + path
            row = next((u for u in urls if u["canonicalUrl"] == full), None)
            if row is None:
                row = next((u for u in urls if u["canonicalUrl"].startswith(full)), None)
            if row is None:
                details[path] = {"found": False}
                continue
            uf = [f for f in findings if f["urlId"] == row["id"]]
            details[path] = {
                "found": True, "summary": row,
                "detail": {
                    **row, "findings": uf,
                    "redirectChain": chains.get(path, [{"url": BASE + path, "statusCode": 200, "location": None}] if path == "/redirect/chain-1" else []),
                    "scopeDiagnostics": {"included": True, "totalWordCount": words.get(path, 150), "scopedWordCount": words.get(path, 150)},
                    "inboundLinks": [], "outboundLinks": [],
                    "headerConflicts": ([{"headerName": "x-robots-tag", "headerValue": "</pages/canonical-target-header.html>; rel=\"canonical\"",
                                          "htmlValue": "/pages/canonical-target-html.html", "conflict": True}]
                                        if path == "/pages/canonical-conflict.html" else []),
                },
            }
    return {"id": "900", "urls": urls, "links": [], "findings": findings, "trapUrlDetails": details}


def build_ctx(mode: str, inert: bool = False, tls: bool = False, non_get=None, tag_hits=None):
    return {
        "base_url": BASE, "tls_active": tls,
        "rendering_enabled": mode == "js",
        "rendering_inert": {"inert": inert, "evidence": "synthetic: no rendered tree is available; js-only-target absent"},
        "click_activity": {"active": False, "evidence": "synthetic: click discovery inert"},
        "fixture_requests": {"nonGet": non_get or [], "tagHits": tag_hits or []},
        "tag_hits": {"total": len(tag_hits or []), "beforeConsent": sum(1 for h in (tag_hits or []) if "consent=none" in h.get("query", ""))},
        "audit_state": "completed",
        "limit_overrides": {"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180},
    }