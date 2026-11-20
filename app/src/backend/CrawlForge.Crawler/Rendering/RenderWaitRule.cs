namespace CrawlForge.Crawler.Rendering;

/// <summary>The wait-rule outcome for one poll (§5.6.2, CR-009).</summary>
public enum RenderWaitDecision
{
    /// <summary>DOM and network have been quiet for the configured period.</summary>
    Settled = 0,

    /// <summary>Still waiting for quiescence.</summary>
    Waiting = 1,

    /// <summary>The hard cap elapsed; capture proceeds with the current state.</summary>
    HardCapReached = 2,
}

/// <summary>
/// Pure wait-rule state machine (FS-CRAWL-001 §5.6.2, CR-009): a page is
/// complete when the DOM and network have been quiet for
/// <see cref="QuietMs"/>, subject to a hard cap of <see cref="HardCapMs"/>.
/// Both values are configurable (CaptureSettings.renderWaitQuietMs /
/// renderWaitHardCapMs). Fixed delays are not used — the rule is evaluated
/// against observed activity ages, never a sleep-until-settled guess.
/// Streaming requests are excluded upstream by the waterfall recorder.
/// </summary>
public sealed class RenderWaitRule
{
    /// <summary>Creates the rule.</summary>
    /// <param name="quietMs">Required quiet period (default 500 ms).</param>
    /// <param name="hardCapMs">Hard cap (default 20 000 ms).</param>
    public RenderWaitRule(int quietMs = 500, int hardCapMs = 20_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quietMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(hardCapMs, 1);
        QuietMs = quietMs;
        HardCapMs = hardCapMs;
    }

    /// <summary>Required DOM-and-network quiet period in milliseconds.</summary>
    public int QuietMs { get; }

    /// <summary>Hard cap in milliseconds.</summary>
    public int HardCapMs { get; }

    /// <summary>
    /// Evaluates one observation.
    /// </summary>
    /// <param name="domReady">document.readyState is interactive or complete.</param>
    /// <param name="domQuietAgeMs">Milliseconds since the last DOM mutation.</param>
    /// <param name="inFlightNonStreaming">In-flight non-streaming request count.</param>
    /// <param name="networkQuietAgeMs">Milliseconds since the last non-streaming network activity.</param>
    /// <param name="elapsedMs">Milliseconds since navigation started.</param>
    /// <returns>The decision.</returns>
    public RenderWaitDecision Evaluate(
        bool domReady,
        long domQuietAgeMs,
        int inFlightNonStreaming,
        long networkQuietAgeMs,
        long elapsedMs)
    {
        if (domReady
            && domQuietAgeMs >= QuietMs
            && inFlightNonStreaming == 0
            && networkQuietAgeMs >= QuietMs)
        {
            return RenderWaitDecision.Settled;
        }

        return elapsedMs >= HardCapMs ? RenderWaitDecision.HardCapReached : RenderWaitDecision.Waiting;
    }
}
