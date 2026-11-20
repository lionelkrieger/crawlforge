# Coordinator instruction to cf-wp-10-01 (queued — send on completion)

## Proxy capture artifacts for CR-031 (http-mode transferred bytes)

CR-031 requires oversized-image findings to use transferred bytes WHERE
CAPTURED, else "not measured" (never pass). In rendered mode your CDP
Network waterfall covers this (RenderedPage.NetworkWaterfall → stored
NetworkWaterfall artifact). In NON-rendered (http) mode with the loopback
proxy active, the proxy also observes subresource transfers — persist
those captures so CR-031 works there too:

- Store proxy subresource captures per URL as
  ContentArtifactKind.NetworkWaterfall artifacts (the locked kind — do NOT
  add a new kind; contracts are re-locked).
- Use the LOCKED RenderedNetworkEntry shape: Url, ResourceType, Method,
  StatusCode, MimeType, TransferredBytes, EncodedDataLength, TimingJson,
  FromCache, FromServiceWorker. Fill TransferredBytes/EncodedDataLength
  from the proxy's actual byte counting (Content-Length where present,
  counted body bytes otherwise; note which in TimingJson or a conventions
  comment).
- Serialize as a JSON array of RenderedNetworkEntry (System.Text.Json
  default for IReadOnlyList) — WP-10-05's ImageRuleAnalyzer reader accepts
  the array form or an object wrapper ("networkWaterfall"/"entries"),
  case-insensitive; the array form is the convention.
- The locked ProxyCapturedRequest context record stays unchanged (no byte
  field — that is fine; the artifact path carries the bytes).

State the exact storage shape + artifact naming in your wiring handoff so
the coordinator can verify it against 05's reader. If your proxy design
cannot count bytes for some transfer (e.g. CONNECT tunnels), leave
TransferredBytes=0 and document it — 05's analyzer treats absence/zero as
not-measured, never pass.

## Compile blocker reported by cf-wp-10-05 (fix in your lane)

CrawlForge.Crawler.Tests currently fails to compile on your WIP:
`Rendering\Harness\FakeRenderHarness.cs(29,37): error CS0050: return type
'Task<BrowserLease>' is less accessible than method
'FakeCdpBrowserFactory.LaunchAsync(...)'` — BrowserLease is internal in
CdpBrowserFactory.cs; InternalsVisibleTo does NOT cure CS0050 (accessibility
is checked at the declaration, not the consumer). Make the harness member
internal (or the lease public) so the shared test project compiles. Your
own filtered Rendering verification runs through this same project, so you
will hit it directly — fix before finishing; sibling builders' Crawler-side
verification is blocked until then.