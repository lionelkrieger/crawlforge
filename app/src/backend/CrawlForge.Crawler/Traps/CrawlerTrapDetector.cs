using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CrawlForge.Crawler.Traps;

/// <summary>
/// Crawler-trap detection (FS-CRAWL-001 §5.14, added by CR-008; criterion #55).
/// Flags URL patterns whose count grows without new content — parameter-combination
/// explosion, repeating path segments, incrementing date paths, and runs of
/// near-duplicate content hashes. By default a pattern is flagged once it
/// exceeds <c>500</c> URLs while mostly producing near-duplicate content
/// (configurable via <see cref="CrawlerTrapOptions"/>); a long enough run of
/// near-duplicate content hashes flags a pattern even below the budget.
/// <para>
/// The detector is a standalone component the frontier consults — it never
/// fetches, enqueues, or persists anything itself:
/// </para>
/// <list type="bullet">
/// <item><see cref="Admit"/> is called once per distinct discovered URL (the
/// §5.2 seen-set deduplicates upstream), BEFORE frontier enqueue. A flagged
/// pattern's URLs are throttled: the decision carries skip reason
/// <see cref="ThrottleSkipReason"/> and the URL is recorded as skipped, never
/// fetched. This bounds the crawled URL set of every flagged pattern, so the
/// audit finishes.</item>
/// <item><see cref="ObserveContent"/> is called AFTER each successful fetch
/// with a content hash of the page's normalized visible text (the producer
/// normalizes; repeated hashes are the near-duplicate evidence). Feeding
/// hashes is what raises detections.</item>
/// <item><see cref="DrainDetections"/> / <see cref="DrainThrottleSignals"/>
/// hand new findings and throttle signals to the engine for persistence and
/// hub emission.</item>
/// </list>
/// <para>
/// Thread-safe: all state is guarded by one lock; classification is pure.
/// Memory is bounded (tracked patterns, per-pattern hashes, examples). All
/// work is local — no endpoint is contacted and no identity dimension is
/// carried (privacy invariant).
/// </para>
/// </summary>
public sealed class CrawlerTrapDetector
{
    /// <summary>Skip reason recorded for URLs of a flagged (throttled) pattern.</summary>
    public const string ThrottleSkipReason = "crawler-trap";

    private readonly CrawlerTrapOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, PatternState> _patterns = new(StringComparer.Ordinal);
    private readonly List<TrapDetection> _detections = [];
    private readonly List<TrapDetection> _pendingDetections = [];
    private readonly List<TrapThrottleSignal> _pendingSignals = [];

    /// <summary>Creates a detector over local thresholds.</summary>
    /// <param name="options">Detection thresholds; null uses the §5.14 defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A threshold is invalid.</exception>
    public CrawlerTrapDetector(CrawlerTrapOptions? options = null)
    {
        _options = options ?? CrawlerTrapOptions.Default;
        _options.Validate();
    }

    /// <summary>Number of distinct patterns currently tracked.</summary>
    public int TrackedPatternCount
    {
        get
        {
            lock (_gate)
            {
                return _patterns.Count;
            }
        }
    }

    /// <summary>All detections raised so far, in detection order.</summary>
    /// <returns>A snapshot of the cumulative detections.</returns>
    public IReadOnlyList<TrapDetection> Detections()
    {
        lock (_gate)
        {
            return _detections.ToList();
        }
    }

    /// <summary>
    /// Decides whether a discovered URL may enter the frontier. URLs of a
    /// flagged pattern are throttled (skip reason <see cref="ThrottleSkipReason"/>);
    /// every other URL is admitted and counted against its pattern.
    /// </summary>
    /// <param name="url">Canonical discovered URL (absolute http(s)).</param>
    /// <returns>The admission decision.</returns>
    public TrapAdmission Admit(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!_options.Enabled)
        {
            return TrapAdmission.Allowed;
        }

        var classification = Classify(url);
        if (classification is null)
        {
            return TrapAdmission.Allowed;
        }

        lock (_gate)
        {
            if (!_patterns.TryGetValue(classification.Value.Key, out var state))
            {
                if (_patterns.Count >= _options.MaxTrackedPatterns)
                {
                    // Memory bound reached: admit untracked; global crawl
                    // limits still bound the audit.
                    return TrapAdmission.Allowed;
                }

                state = new PatternState(classification.Value.Key, classification.Value.Family, classification.Value.Host);
                _patterns.Add(state.Key, state);
            }

            state.DiscoveredCount++;
            if (state.Examples.Count < _options.MaxExamples)
            {
                state.Examples.Add(url);
            }

            if (state.Flagged)
            {
                return TrapAdmission.Throttled(state.Key);
            }

            state.AdmittedCount++;
            return new TrapAdmission(true, null, state.Key);
        }
    }

    /// <summary>
    /// Feeds one content observation for a fetched URL. A hash that repeats a
    /// hash already seen within the URL's pattern is near-duplicate evidence;
    /// consecutive repeats form a run. When the pattern's URL count exceeds
    /// the budget while mostly near-duplicate, or the near-duplicate run
    /// reaches the configured length, the pattern is flagged: further URLs
    /// are throttled and a detection + throttle signal become drainable.
    /// </summary>
    /// <param name="url">Canonical URL of the fetched page.</param>
    /// <param name="contentHash">
    /// Content hash of the page's normalized visible text (see
    /// <see cref="HashText"/>). Null/empty observations are ignored.
    /// </param>
    public void ObserveContent(string url, string? contentHash)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!_options.Enabled || string.IsNullOrEmpty(contentHash))
        {
            return;
        }

        var classification = Classify(url);
        if (classification is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_patterns.TryGetValue(classification.Value.Key, out var state))
            {
                return;
            }

            state.ObservedCount++;
            if (state.Hashes.Contains(contentHash))
            {
                state.NearDuplicateCount++;
                state.CurrentRun++;
            }
            else
            {
                state.CurrentRun = 0;
                if (state.Hashes.Count < _options.MaxDistinctHashesPerPattern)
                {
                    state.Hashes.Add(contentHash);
                }
            }

            TryFlag(state);
        }
    }

    /// <summary>Returns and clears detections raised since the previous drain.</summary>
    /// <returns>New detections in detection order (empty when none).</returns>
    public IReadOnlyList<TrapDetection> DrainDetections()
    {
        lock (_gate)
        {
            if (_pendingDetections.Count == 0)
            {
                return [];
            }

            var drained = _pendingDetections.ToList();
            _pendingDetections.Clear();
            return drained;
        }
    }

    /// <summary>Returns and clears throttle signals raised since the previous drain.</summary>
    /// <returns>New throttle signals in detection order (empty when none).</returns>
    public IReadOnlyList<TrapThrottleSignal> DrainThrottleSignals()
    {
        lock (_gate)
        {
            if (_pendingSignals.Count == 0)
            {
                return [];
            }

            var drained = _pendingSignals.ToList();
            _pendingSignals.Clear();
            return drained;
        }
    }

    /// <summary>
    /// Returns a diagnostic snapshot of one URL's pattern, or null when the
    /// URL is untracked (detection disabled, unparseable, or memory bound).
    /// </summary>
    /// <param name="url">Canonical URL.</param>
    /// <returns>The pattern snapshot, or null.</returns>
    public TrapPatternSnapshot? GetPattern(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var classification = Classify(url);
        if (classification is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _patterns.TryGetValue(classification.Value.Key, out var state)
                ? new TrapPatternSnapshot(
                    state.Key,
                    state.Family,
                    state.DiscoveredCount,
                    state.AdmittedCount,
                    state.ObservedCount,
                    state.NearDuplicateCount,
                    state.Flagged)
                : null;
        }
    }

    /// <summary>
    /// Stable content hash of normalized visible text (SHA-256, lowercase hex).
    /// Producers hash the page's normalized body text — not raw markup — so
    /// navigation/link differences between near-duplicate pages do not defeat
    /// detection. Purely local.
    /// </summary>
    /// <param name="normalizedText">Normalized visible text of the page.</param>
    /// <returns>The lowercase hex SHA-256 of the UTF-8 text.</returns>
    public static string HashText(string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Classifies a canonical URL into its structural trap pattern. Pure and
    /// deterministic: date paths win over repeating segments, repeating
    /// segments win over query-parameter combinations, and every other URL
    /// maps to its query-stripped path pattern.
    /// </summary>
    /// <param name="url">Candidate URL.</param>
    /// <returns>The classification, or null when the URL is not absolute http(s).</returns>
    public static TrapPatternClassification? Classify(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var origin = string.Concat(uri.Scheme, "://", uri.Host);

        // Family 1: incrementing date paths — a year segment followed by a
        // month (and optional day) segment, e.g. /trap/calendar/2026/11.
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (!IsYear(segments[i]) || !IsMonth(segments[i + 1]))
            {
                continue;
            }

            var masked = segments.ToList();
            var windowLength = i + 2 < segments.Length && IsDay(segments[i + 2]) ? 3 : 2;
            masked.RemoveRange(i, windowLength);
            masked.Insert(i, "{date}");
            return new TrapPatternClassification(
                BuildKey(origin, masked),
                TrapPatternFamily.IncrementingDatePath,
                uri.Host);
        }

        // Family 2: repeating path segments — a single segment repeated at
        // least three times consecutively, or a two-segment unit repeated at
        // least three times (e.g. /a/b/a/b/a/b).
        var repeat = FindRepeatingRun(segments);
        if (repeat is { } run)
        {
            var masked = segments.ToList();
            masked.RemoveRange(run.Start, run.Length);
            masked.Insert(run.Start, "{repeat}");
            return new TrapPatternClassification(
                BuildKey(origin, masked),
                TrapPatternFamily.RepeatingPathSegment,
                uri.Host);
        }

        // Family 3: parameter-combination explosion — same path, varying
        // query-parameter combinations (faceted navigation).
        if (uri.Query.Length > 1)
        {
            return new TrapPatternClassification(
                origin + uri.AbsolutePath,
                TrapPatternFamily.ParameterCombination,
                uri.Host);
        }

        // Family 4: plain path pattern tracked for near-duplicate hash runs.
        return new TrapPatternClassification(
            origin + uri.AbsolutePath,
            TrapPatternFamily.NearDuplicateRun,
            uri.Host);
    }

    private static string BuildKey(string origin, List<string> maskedSegments) =>
        maskedSegments.Count == 0 ? origin + "/" : origin + "/" + string.Join("/", maskedSegments);

    private static bool IsYear(string segment) =>
        segment.Length == 4 &&
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
        year is >= 1000 and <= 2999;

    private static bool IsMonth(string segment) =>
        segment.Length is 1 or 2 &&
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
        month is >= 1 and <= 12;

    private static bool IsDay(string segment) =>
        segment.Length is 1 or 2 &&
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var day) &&
        day is >= 1 and <= 31;

    private static (int Start, int Length)? FindRepeatingRun(string[] segments)
    {
        // Single-segment runs of length >= 3.
        var runStart = 0;
        for (var i = 1; i <= segments.Length; i++)
        {
            if (i < segments.Length && string.Equals(segments[i], segments[runStart], StringComparison.Ordinal))
            {
                continue;
            }

            if (i - runStart >= 3)
            {
                return (runStart, i - runStart);
            }

            runStart = i;
        }

        // Two-segment units repeated >= 3 times consecutively.
        for (var i = 0; i + 5 < segments.Length; i++)
        {
            if (!string.Equals(segments[i], segments[i + 2], StringComparison.Ordinal) ||
                !string.Equals(segments[i + 1], segments[i + 3], StringComparison.Ordinal))
            {
                continue;
            }

            var repetitions = 2;
            var j = i + 4;
            while (j + 1 < segments.Length &&
                   string.Equals(segments[j], segments[i], StringComparison.Ordinal) &&
                   string.Equals(segments[j + 1], segments[i + 1], StringComparison.Ordinal))
            {
                repetitions++;
                j += 2;
            }

            if (repetitions >= 3)
            {
                return (i, repetitions * 2);
            }
        }

        return null;
    }

    private void TryFlag(PatternState state)
    {
        if (state.Flagged)
        {
            return;
        }

        var ratio = state.ObservedCount > 0
            ? (double)state.NearDuplicateCount / state.ObservedCount
            : 0d;
        var budgetExceeded = state.DiscoveredCount > _options.PatternUrlBudget &&
                             ratio >= _options.NearDuplicateRatio;
        var runReached = _options.NearDuplicateRunLength > 0 &&
                         state.CurrentRun >= _options.NearDuplicateRunLength;
        if (!budgetExceeded && !runReached)
        {
            return;
        }

        state.Flagged = true;
        var detection = new TrapDetection(
            state.Key,
            state.Family,
            state.DiscoveredCount,
            state.AdmittedCount,
            state.ObservedCount,
            state.NearDuplicateCount,
            ratio,
            state.Examples.ToList(),
            DateTimeOffset.UtcNow);
        _detections.Add(detection);
        _pendingDetections.Add(detection);
        _pendingSignals.Add(new TrapThrottleSignal(state.Key, state.Host, state.DiscoveredCount, _options.PatternUrlBudget));
    }

    private sealed class PatternState(string key, TrapPatternFamily family, string host)
    {
        public string Key { get; } = key;

        public TrapPatternFamily Family { get; } = family;

        public string Host { get; } = host;

        public int DiscoveredCount { get; set; }

        public int AdmittedCount { get; set; }

        public int ObservedCount { get; set; }

        public int NearDuplicateCount { get; set; }

        public int CurrentRun { get; set; }

        public bool Flagged { get; set; }

        public HashSet<string> Hashes { get; } = new(StringComparer.Ordinal);

        public List<string> Examples { get; } = [];
    }
}

/// <summary>
/// Structural classification of one URL into a trap pattern.
/// </summary>
/// <param name="Key">Stable pattern key shared by every URL of the pattern.</param>
/// <param name="Family">Structural pattern family.</param>
/// <param name="Host">Host of the URL (for throttle signals).</param>
public readonly record struct TrapPatternClassification(string Key, TrapPatternFamily Family, string Host);

/// <summary>
/// Diagnostic snapshot of one tracked pattern.
/// </summary>
/// <param name="Key">Pattern key.</param>
/// <param name="Family">Structural pattern family.</param>
/// <param name="DiscoveredCount">URLs discovered for the pattern.</param>
/// <param name="AdmittedCount">URLs admitted before any throttle.</param>
/// <param name="ObservedCount">Content observations fed.</param>
/// <param name="NearDuplicateCount">Near-duplicate observations.</param>
/// <param name="Flagged">Whether the pattern is flagged and throttled.</param>
public sealed record TrapPatternSnapshot(
    string Key,
    TrapPatternFamily Family,
    int DiscoveredCount,
    int AdmittedCount,
    int ObservedCount,
    int NearDuplicateCount,
    bool Flagged);
