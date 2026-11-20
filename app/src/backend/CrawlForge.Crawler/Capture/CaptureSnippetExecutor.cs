using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Capture;

/// <summary>
/// Capture-time custom JS snippet execution (FS-CRAWL-001 §7.17; §5.6.1 source
/// selection). Runs the locked <see cref="IJsSnippetRunner"/> — the isolated Jint
/// wrapper (no CLR interop, no network, scoped <c>cf.submit</c>/<c>cf.error</c>/
/// <c>cf.loadScript</c> host API, per-snippet timeout + statement/memory bounds)
/// — against the selected extraction source: raw, rendered, or both. An outer
/// execution bound isolates a hung runner so a capture worker can never stall;
/// a snippet failure on one page never fails the audit or other pages.
/// <para>
/// Snippets require rendering mode (§7.17). Capture modes carry rendering even
/// when the legacy <c>RenderingMode</c> field is <c>None</c>, so the executor
/// bridges the runner's rendering gate on the local context it passes; the
/// stored configuration is never mutated. A rendered-source snippet with no
/// rendered capture yields an empty result (it never silently falls back to raw).
/// </para>
/// <para>
/// Results are grouped by source: a <see cref="CaptureSource.Both"/> snippet
/// appears in both the raw and the rendered outcome. §5.6.2 places snippet
/// execution on the rendering control layer; when the pool exposes an in-page
/// evaluation surface the coordinator can route rendered-source snippets through
/// it (WP-10-02 handoff). Until then the isolated interpreter evaluates the
/// captured rendered DOM offline-safely (D-003, no runtime download).
/// </para>
/// </summary>
public sealed class CaptureSnippetExecutor : ICaptureSnippetExecutor
{
    private readonly IJsSnippetRunner _runner;
    private readonly TimeSpan _outerTimeout;

    /// <summary>Creates the executor.</summary>
    /// <param name="runner">Locked snippet runner (Jint isolated wrapper in production).</param>
    /// <param name="outerTimeout">Outer per-source execution bound; null uses the documented default.</param>
    public CaptureSnippetExecutor(IJsSnippetRunner runner, TimeSpan? outerTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _outerTimeout = outerTimeout ?? CaptureDefaults.SnippetOuterTimeout;
        if (_outerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(outerTimeout), "The outer snippet timeout must be positive.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaptureSnippetOutcome>> RunAsync(
        PageAuditContext context,
        IReadOnlyList<CaptureSnippetDefinition> snippets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snippets);
        cancellationToken.ThrowIfCancellationRequested();
        if (snippets.Count == 0)
        {
            return [];
        }

        // §7.17 rendering gate: capture modes carry rendering; a non-JS audit
        // with no rendered capture and no legacy rendering mode is out of scope.
        bool renderingActive =
            (context.Configuration.CaptureMode is { } mode && mode != CaptureMode.NonJs)
            || context.RenderedDocument is not null
            || context.Configuration.RenderingMode != RenderMode.None;
        if (!renderingActive)
        {
            return [];
        }

        var rawBatch = snippets
            .Where(s => s.Source is CaptureSource.Raw or CaptureSource.Both)
            .Select(s => s.Snippet)
            .ToList();
        var renderedBatch = snippets
            .Where(s => s.Source is CaptureSource.Rendered or CaptureSource.Both)
            .Select(s => s.Snippet)
            .ToList();

        var outcomes = new List<CaptureSnippetOutcome>();
        if (rawBatch.Count > 0 && context.Document is not null)
        {
            PageAuditContext rawContext = context with
            {
                RenderedDocument = null,
                Configuration = WithRenderingEnabled(context.Configuration),
            };
            outcomes.Add(await RunBatchAsync(CaptureSource.Raw, rawContext, rawBatch, cancellationToken).ConfigureAwait(false));
        }

        if (renderedBatch.Count > 0)
        {
            if (context.RenderedDocument is { } rendered)
            {
                PageAuditContext renderedContext = context with
                {
                    Document = rendered,
                    RenderedDocument = rendered,
                    Configuration = WithRenderingEnabled(context.Configuration),
                };
                outcomes.Add(await RunBatchAsync(CaptureSource.Rendered, renderedContext, renderedBatch, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                // No rendered capture: rendered-source snippets yield empty
                // results (never a silent raw fallback).
                outcomes.Add(new CaptureSnippetOutcome(
                    CaptureSource.Rendered,
                    renderedBatch.Select(s => Empty(s.Key)).ToList(),
                    TimedOutKeys: []));
            }
        }

        return outcomes;
    }

    private async Task<CaptureSnippetOutcome> RunBatchAsync(
        CaptureSource source,
        PageAuditContext context,
        IReadOnlyList<JsSnippetDefinition> batch,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_outerTimeout);
        try
        {
            IReadOnlyList<SnippetResultDraft> results = await _runner.RunAsync(context, batch, linked.Token).ConfigureAwait(false);
            return new CaptureSnippetOutcome(source, results, TimedOutKeys: []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Outer bound tripped: isolate the whole batch, never fail the crawl.
            return new CaptureSnippetOutcome(source, batch.Select(s => Empty(s.Key)).ToList(), batch.Select(s => s.Key).ToList());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new CaptureSnippetOutcome(source, batch.Select(s => Empty(s.Key)).ToList(), TimedOutKeys: []);
        }
    }

    private static CrawlConfigurationDto WithRenderingEnabled(CrawlConfigurationDto configuration) =>
        configuration.RenderingMode == RenderMode.None
            ? configuration with { RenderingMode = RenderMode.Always }
            : configuration;

    private static SnippetResultDraft Empty(string key) => new(key, "[]", "[]", IsTruncated: false);
}
