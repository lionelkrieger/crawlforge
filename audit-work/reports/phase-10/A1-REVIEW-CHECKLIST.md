# WP-10-A1 review checklist (coordinator working note, phase-10 step 1)

Applied at review time. Items 1–6 are the brief/YAML scope; 7–10 are the
contract surfaces the wave-10A builders need that ONLY A1 can add (Contracts
locked after acceptance; AGENT-INSTRUCTIONS §11). Subagents are not steerable
mid-flight, so gaps found here go back as one specific send-back round.

## Brief/YAML scope
1. AuditCategory += Images=11, SecurityAndProtocol=12, Internationalization=13.
   Wire (default ApiEnumConverter convention, no hyphen insertion):
   "images", "securityandprotocol", "internationalization". §7.4 gives prose
   labels only ("Security & protocol" is a display label, frontend concern).
2. WafChallengeClass += ManagedChallenge=4, RateLimitSoftBlock=5.
   Spec 03/02 §2.2 (~line 205) locks HYPHENATED tokens: `managed-challenge`,
   `rate-limit-soft-block`. ApiEnumConverter.WireName default lowercases without
   hyphens; the CatchUp→"catch-up" special case is the precedent. WireName lives
   in CrawlForge.Api (outside A1 scope) → A1 must lock the intended hyphenated
   tokens in EnumVocabularyTests + frontend mirror and hand off the two Api
   special cases. Existing members/tokens ("captcha"/"interstitial"/"botblock")
   unchanged — the pre-existing vs-spec "js-interstitial"/"bot-block" tension is
   a loop-§8 owner observation, NOT an additive fix.
3. IndexabilityFlags += CanonicalElsewhere = 16 (YAML contract_tests name;
   WIRING-NOTES gap 4 called it CanonicalConflict — YAML wins).
4. FindingRuleKeys += gap-6 exact local keys (read builder folders:
   crawler-trap.detected, canonicalization.session-segment-removed,
   crawl.cookieless-redirect-loop, ua-comparison.material-difference,
   redirects.chain, content.duplicate-*, url.*, image.*,
   structured-data.missing-recommended) + CR-029 four case keys + CR-030
   deprecated-term key.
5. Frontend mirrors + guards accept every new value; guard tests outside A1
   owned paths that lock exact lists → handoff item (coordinator extends).
6. AdditivePolicyTests: every new DTO member [AdditiveMember], optional, null on
   baseline construction; KnownAdditiveMembersExistOnParentDtos extended.
   EnumVocabularyTests lists EXTENDED, never weakened. No renames/renumbers.

## Additional contract surfaces required by wave 10A (dispatch-note items)
7. CaptureMode enum NonJs=0/Js=1/Dual=2/Adaptive=3; intended wire "non-js"
   (Api special case handoff) / "js" / "dual" / "adaptive".
   CrawlConfigurationDto += [AdditiveMember] CaptureMode? CaptureMode (null =
   baseline non-JS) + additive capture-settings document with: adaptive sample
   size (default in 3–5), material word-count threshold (default 20%), dual
   storage warning threshold (no hard cap), per-URL-pattern overrides
   (pattern + rendered/non-rendered, win over adaptive), wait quiet-ms (500) /
   hard-cap-ms (20000), pool size (half logical procs, min 2).
8. CR-029 redirect-check toggle document on CrawlConfigurationDto: four bools
   (cross-host, temporary 302/303/307, non-indexable final target, HTTPS→HTTP
   downgrade), default TRUE when absent.
9. RenderedPage (Contracts\Crawl\RenderingContracts.cs) += additive
   NetworkWaterfall member + RenderedNetworkEntry record (url, resourceType,
   method, statusCode, mimeType, transferredBytes, encodedDataLength, timing,
   fromCache, fromServiceWorker) — WP-10-01 contract_tests require the waterfall
   in the render result; CR-031 needs transferredBytes for rendered images.
   ContentArtifactKind += NetworkWaterfall = 3 (RenderedDom=2/Screenshot=1
   already exist; waterfall storage per WP-10-01 contract_tests).
10. Phase-10 finding keys (spec names the findings, not the key strings —
    §7.18/§5.6.1/§7.19): lock the acceptance harness's ratified measurement
    vocabulary so the gate verdicts measure the delivered keys unchanged:
    render.links-delta, render.text-delta, render.metadata-delta,
    render.title-delta, render.canonical-delta, render.noindex-delta
    (rendered-only noindex = CRITICAL per §7.18), plus
    content.javascript-dependent-template (adaptive group switch, §5.6.1) and
    a service-worker informational key containing "service-worker"
    (t_service_worker matches the substring). Soft-404 client route reuses the
    phase-09 soft-404 key family (t_soft_404_client_route matches "soft-404").
    Rendered-only link discovery: additive DiscoverySource.Render = 5 (wire
    "render") or an additive link/URL flag — A1's choice, documented in handoff
    (WP-10-02 deliverable "recorded as discovered-by-render").

## Verification re-run (coordinator)
- dotnet test tests/CrawlForge.Contracts.Tests (CF_AGENT_ID unset, canonical)
- dotnet build src/backend/CrawlForge.Host -c Debug
- cd src/frontend; npm run build; npm run test -- --run (contract guards)
- git diff --stat: ONLY Contracts/, Contracts.Tests/, frontend/src/contracts/
- additive-only review: no existing member renamed/renumbered/removed; no
  finding moved category (WP-10-05's job).