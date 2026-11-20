# Coordinator reply to cf-wp-10-05 early findings #2 (queued — send on completion)

## 1. Ownership ruling: GRANTED — scoped one-file extension

You MAY update `app\tests\CrawlForge.Audit.Tests\ContentQuality\ImageRuleAnalyzerTests.cs`
— ONLY that file, ONLY the five itemized assertions you listed, minimal
changes, no deletions, no weakening beyond the spec-mandated behavior:
- RealFixtureImages_EmitLegacyFormatNotices: Performance → Images;
  DoesNotContain(Oversized) → the 2 not-measured Notices (CR-031 fixes the
  exact defect that assertion encoded — this is a behavior correction, not
  a weakening; cite CR-031 in a comment).
- OversizedBytes_EmitWarning_WithMeasurement: Performance → Images.
- ModernFormats_EmitNothing / UnknownFormat_EmitsNothing: Assert.Empty →
  one image.oversized Notice measurement="not-measured" per unknown-size
  image (CR-031: never a silent pass).
- SmallDataUri_EmitsFormatOnly: account for the alt-takeover
  accessibility.image-alt (Error, Images) on the alt-less img.
Document each change with its spec/CR rationale in your report; the
coordinator reviews the diff against this itemized list. The extension is
recorded in DISPATCH-TRACKING.md (the YAML owned_paths stay as written —
coordinator ruling, not a plan edit). Your stand-in tests stay in your
owned folders as planned.

## 2. Alt takeover: APPROVED as implemented

Reusing the internal ImageAltRule + AccessibilityRuleContext (same
assembly) with identical camelCase AccessibilityEvidence, Error severity,
Category=Images is the zero-drift design — better than reimplementation.
DI handoff carries the RuleOverrides disable (+ SinglePageTester path) —
already in the coordinator wiring plan §4.

## 3. CR-031: NOT a contract gap — no amendment needed

Your not-measured Notice / measured Warning + bytesSource design is
approved. On http-mode proxy sizes: ProxyCapturedRequest (the locked
pipeline-context record) indeed has no byte field — but the ARTIFACT path
covers CR-031 without any contract change: the coordinator is instructing
WP-10-01 to persist proxy subresource captures as
ContentArtifactKind.NetworkWaterfall artifacts using the LOCKED
RenderedNetworkEntry shape (TransferredBytes/EncodedDataLength filled from
the proxy's byte counting). Your analyzer already reads that shape
(JSON array of RenderedNetworkEntry or the object wrapper) — so http-mode
images with proxy capture become "measured" through the same read path,
and not-measured remains the honest fallback when no artifact exists.
Reframe your report note: "ProxyCapturedRequest lacks bytes (locked);
CR-031 http-mode measurement flows via NetworkWaterfall-kind proxy
artifacts (coordinator wiring convention), not via the context record."
Do NOT add contract members. The coordinator verifies 01's storage shape
against your reader at wiring (array form expected — System.Text.Json
serializes IReadOnlyList<RenderedNetworkEntry> as a JSON array).

## 4. Redirects: acknowledged

Legacy redirects.inappropriate constant retained for compile compat, no
longer emitted, four locked case keys emitted — correct. The coordinator
reseeds the catalog at wiring (your handoff list) and drops the legacy
seed. Keep the constant until the reseed lands.

## 5. Crawler.Tests compile blocker (CS0050 in 01's FakeRenderHarness)

Acknowledged — correctly NOT your code and correctly left untouched. No
routing needed: 01's own filtered Rendering verification runs through the
same CrawlForge.Crawler.Tests project, so they hit and fix this directly
(CS0050 is a declaration-side accessibility error; InternalsVisibleTo
cannot cure it — the harness member must be internal or BrowserLease
public). The item is queued in 01's completion-round note as a
must-fix-before-finish. Retry your Waf/RedirectFinding run before
finishing as planned; if it is STILL broken when you complete, say so
explicitly in your report — the coordinator will sequence 01's acceptance
first and re-run your verification in the review round rather than let you
touch their lane.