using CrawlForge.Crawler.Traps;
using Xunit;

namespace CrawlForge.Crawler.Tests.Traps;

/// <summary>
/// FS-CRAWL-001 §5.14 crawler-trap detection (criterion #55): pattern-family
/// classification, per-pattern URL budgets, near-duplicate hash runs, throttle
/// admission, detection payloads, memory bounds, thread safety — and the
/// proving simulations that the audit FINISHES (bounded URL set) on both
/// fixture trap shapes (crawler-trap-filter, crawler-trap-calendar).
/// </summary>
public class CrawlerTrapDetectorTests
{
    private const string FilterPath = "https://fixture.example/trap/filter";
    private const string CalendarRoot = "https://fixture.example/trap/calendar";

    // ----- Classification (pure, deterministic) -----

    [Fact]
    public void Classify_ParameterCombination_SharedKeyAcrossQueryVariants()
    {
        var a = CrawlerTrapDetector.Classify(FilterPath + "?color=red");
        var b = CrawlerTrapDetector.Classify(FilterPath + "?color=blue&size=m");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(TrapPatternFamily.ParameterCombination, a!.Value.Family);
        Assert.Equal(a.Value.Key, b!.Value.Key);
        Assert.Equal(FilterPath, a.Value.Key);
    }

    [Fact]
    public void Classify_IncrementingDatePath_MasksDateWindow()
    {
        var a = CrawlerTrapDetector.Classify(CalendarRoot + "/2026/11");
        var b = CrawlerTrapDetector.Classify(CalendarRoot + "/2027/3");
        var c = CrawlerTrapDetector.Classify(CalendarRoot + "/2026/11/19");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.Equal(TrapPatternFamily.IncrementingDatePath, a!.Value.Family);
        Assert.Equal(a.Value.Key, b!.Value.Key);
        Assert.Equal(a.Value.Key, c!.Value.Key);
        Assert.Contains("{date}", a.Value.Key);
        Assert.DoesNotContain("2026", a.Value.Key);
    }

    [Theory]
    [InlineData("https://h.example/a/b/a/b/a/b")]
    [InlineData("https://h.example/x/x/x")]
    [InlineData("https://h.example/tag/tag/tag/tag")]
    public void Classify_RepeatingPathSegment_MasksRun(string url)
    {
        var classification = CrawlerTrapDetector.Classify(url);

        Assert.NotNull(classification);
        Assert.Equal(TrapPatternFamily.RepeatingPathSegment, classification!.Value.Family);
        Assert.Contains("{repeat}", classification.Value.Key);
    }

    [Fact]
    public void Classify_PlainPath_TracksNearDuplicateRunFamily()
    {
        var classification = CrawlerTrapDetector.Classify("https://h.example/pages/about.html");

        Assert.NotNull(classification);
        Assert.Equal(TrapPatternFamily.NearDuplicateRun, classification!.Value.Family);
        Assert.Equal("https://h.example/pages/about.html", classification.Value.Key);
    }

    [Theory]
    [InlineData("ftp://h.example/file")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    public void Classify_NonAbsoluteHttp_ReturnsNull(string url)
    {
        Assert.Null(CrawlerTrapDetector.Classify(url));
    }

    [Fact]
    public void Classify_DatePathWinsOverQuery()
    {
        var classification = CrawlerTrapDetector.Classify(CalendarRoot + "/2026/11?view=grid");

        Assert.NotNull(classification);
        Assert.Equal(TrapPatternFamily.IncrementingDatePath, classification!.Value.Family);
    }

    // ----- Admission and budgets -----

    [Fact]
    public void Admit_DisabledDetector_AlwaysAllows()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(Enabled: false));

        for (var i = 0; i < 1000; i++)
        {
            var decision = detector.Admit($"{FilterPath}?page={i}");
            Assert.True(decision.Admit);
        }

        Assert.Empty(detector.Detections());
        Assert.Equal(0, detector.TrackedPatternCount);
    }

    [Fact]
    public void BudgetExceeded_WithMostlyNearDuplicateContent_FlagsThrottlesAndDetects()
    {
        // Run-based flagging disabled so the budget path is the only trigger.
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(
            PatternUrlBudget: 10, NearDuplicateRatio: 0.8, NearDuplicateRunLength: 0));

        // Discover 15 URLs of one faceted pattern (all admitted pre-flag).
        var urls = Enumerable.Range(0, 15).Select(i => $"{FilterPath}?color={i}").ToList();
        foreach (var url in urls)
        {
            Assert.True(detector.Admit(url).Admit);
        }

        // Feed 10 observations: 9 repeat the first hash → ratio 0.9 ≥ 0.8.
        var firstHash = CrawlerTrapDetector.HashText("same products");
        detector.ObserveContent(urls[0], firstHash);
        for (var i = 1; i < 10; i++)
        {
            detector.ObserveContent(urls[i], firstHash);
        }

        var detection = Assert.Single(detector.Detections());
        Assert.Equal(TrapPatternFamily.ParameterCombination, detection.Family);
        Assert.Equal(FilterPath, detection.PatternKey);
        Assert.True(detection.UrlCount > 10, $"URL count {detection.UrlCount} must exceed the budget 10.");
        Assert.True(detection.NearDuplicateRatio >= 0.8);
        Assert.InRange(detection.Examples.Count, 1, 5);

        // Every further URL of the flagged pattern is throttled.
        var throttled = detector.Admit(FilterPath + "?color=never-before-seen");
        Assert.False(throttled.Admit);
        Assert.Equal(CrawlerTrapDetector.ThrottleSkipReason, throttled.SkipReason);
        Assert.Equal(FilterPath, throttled.PatternKey);

        // Other patterns are unaffected.
        Assert.True(detector.Admit("https://fixture.example/pages/about.html").Admit);

        var signal = Assert.Single(detector.DrainThrottleSignals());
        Assert.Equal(FilterPath, signal.PatternKey);
        Assert.Equal("fixture.example", signal.Host);
        Assert.Equal(10, signal.Budget);
        Assert.Equal("throttled", signal.Action);
        Assert.Empty(detector.DrainThrottleSignals());

        var drained = detector.DrainDetections();
        Assert.Single(drained);
        Assert.Empty(detector.DrainDetections());
    }

    [Fact]
    public void NearDuplicateRun_FlagsBelowBudget_WithDefaultOptions()
    {
        var detector = new CrawlerTrapDetector(); // §5.14 defaults: budget 500, run 25.

        var urls = Enumerable.Range(0, 40).Select(i => $"{CalendarRoot}/2026/{(i % 12) + 1}?v={i}").ToList();
        foreach (var url in urls)
        {
            Assert.True(detector.Admit(url).Admit);
        }

        // Identical visible text on every calendar page → run of repeats.
        var hash = CrawlerTrapDetector.HashText("Events calendar No events this month.");
        for (var i = 0; i < 26; i++)
        {
            detector.ObserveContent(urls[i], hash);
        }

        var detection = Assert.Single(detector.Detections());
        Assert.Equal(TrapPatternFamily.IncrementingDatePath, detection.Family);
        Assert.True(detection.UrlCount <= 500, "Run-based flagging must fire below the URL budget.");
        Assert.False(detector.Admit(CalendarRoot + "/2030/1").Admit);
    }

    [Fact]
    public void UniqueContent_LargePattern_IsNotFlagged()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(
            PatternUrlBudget: 10, NearDuplicateRatio: 0.8, NearDuplicateRunLength: 0));

        for (var i = 0; i < 60; i++)
        {
            var url = $"{FilterPath}?product={i}";
            Assert.True(detector.Admit(url).Admit);
            detector.ObserveContent(url, CrawlerTrapDetector.HashText($"unique product {i}"));
        }

        Assert.Empty(detector.Detections());
        Assert.True(detector.Admit(FilterPath + "?product=60").Admit);
    }

    [Fact]
    public void RunLengthZero_DisablesRunBasedFlagging()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(NearDuplicateRunLength: 0));
        var hash = CrawlerTrapDetector.HashText("dup");

        for (var i = 0; i < 100; i++)
        {
            var url = $"{FilterPath}?p={i}";
            detector.Admit(url);
            detector.ObserveContent(url, hash);
        }

        // 100 URLs < default budget 500 and runs disabled → no flag.
        Assert.Empty(detector.Detections());
    }

    [Fact]
    public void Examples_AreBoundedByOptions()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(
            PatternUrlBudget: 1, NearDuplicateRatio: 0.5, NearDuplicateRunLength: 0, MaxExamples: 3));

        for (var i = 0; i < 10; i++)
        {
            detector.Admit($"{FilterPath}?x={i}");
        }

        var hash = CrawlerTrapDetector.HashText("dup");
        detector.ObserveContent(FilterPath + "?x=0", hash);
        detector.ObserveContent(FilterPath + "?x=1", hash);
        detector.ObserveContent(FilterPath + "?x=2", hash);

        var detection = Assert.Single(detector.Detections());
        Assert.Equal(3, detection.Examples.Count);
    }

    [Fact]
    public void TrackedPatterns_AreMemoryBounded()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(MaxTrackedPatterns: 2));

        Assert.True(detector.Admit("https://h.example/a").Admit);
        Assert.True(detector.Admit("https://h.example/b").Admit);
        var untracked = detector.Admit("https://h.example/c");

        Assert.True(untracked.Admit);
        Assert.Equal(2, detector.TrackedPatternCount);
        Assert.Null(detector.GetPattern("https://h.example/c"));
        Assert.NotNull(detector.GetPattern("https://h.example/a"));
    }

    [Fact]
    public void ObserveContent_UntrackedOrEmpty_IsIgnored()
    {
        var detector = new CrawlerTrapDetector();

        detector.ObserveContent("https://h.example/never-admitted", "hash");
        detector.ObserveContent("https://h.example/never-admitted", null);
        detector.ObserveContent("https://h.example/never-admitted", string.Empty);

        Assert.Equal(0, detector.TrackedPatternCount);
        Assert.Empty(detector.Detections());
    }

    [Fact]
    public void Options_InvalidBounds_ThrowAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerTrapDetector(new CrawlerTrapOptions(PatternUrlBudget: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerTrapDetector(new CrawlerTrapOptions(NearDuplicateRatio: 1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerTrapDetector(new CrawlerTrapOptions(NearDuplicateRunLength: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerTrapDetector(new CrawlerTrapOptions(MaxExamples: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerTrapDetector(new CrawlerTrapOptions(MaxTrackedPatterns: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerTrapDetector(new CrawlerTrapOptions(MaxDistinctHashesPerPattern: 0)));
    }

    [Fact]
    public void HashText_IsDeterministicAndContentSensitive()
    {
        Assert.Equal(CrawlerTrapDetector.HashText("abc"), CrawlerTrapDetector.HashText("abc"));
        Assert.NotEqual(CrawlerTrapDetector.HashText("abc"), CrawlerTrapDetector.HashText("abd"));
        Assert.Equal(64, CrawlerTrapDetector.HashText("abc").Length);
    }

    // ----- Thread safety -----

    [Fact]
    public void ParallelAdmitAndObserve_AreThreadSafe()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(
            PatternUrlBudget: 50, NearDuplicateRatio: 0.8, NearDuplicateRunLength: 25));
        var hash = CrawlerTrapDetector.HashText("near-duplicate body");
        var urls = Enumerable.Range(0, 400).Select(i => $"{FilterPath}?c={i}").ToList();

        Parallel.For(0, 8, worker =>
        {
            for (var i = worker * 50; i < (worker + 1) * 50; i++)
            {
                detector.Admit(urls[i]);
                detector.ObserveContent(urls[i], hash);
            }
        });

        var pattern = detector.GetPattern(urls[0]);
        Assert.NotNull(pattern);
        Assert.Equal(400, pattern!.DiscoveredCount);
        Assert.Equal(400, pattern.ObservedCount);
        Assert.True(pattern.Flagged);
        Assert.Single(detector.Detections());
        Assert.False(detector.Admit(FilterPath + "?c=after").Admit);
    }

    // ----- Proving simulations: the audit FINISHES (bounded URL set) -----

    /// <summary>
    /// Fixture crawler-trap-filter shape: /trap/filter with faceted parameters
    /// color(3) × size(3) × sort(2) × page(3); every page links to all
    /// single-parameter variants (adding or replacing one parameter) and serves
    /// identical visible text. The reachable space is every partial and full
    /// parameter assignment. The simulated crawl consults the detector at
    /// enqueue AND dequeue (the engine recipe) and must terminate with a
    /// bounded fetched set, a detection, and throttled skips — not crawl the
    /// whole combination space.
    /// </summary>
    [Fact]
    public void FilterTrap_SimulatedCrawl_FinishesBounded_WithDetection()
    {
        var detector = new CrawlerTrapDetector(); // §5.14 defaults.
        var facets = new (string Key, string[] Values)[]
        {
            ("color", ["red", "blue", "green"]),
            ("size", ["s", "m", "l"]),
            ("sort", ["price", "name"]),
            ("page", ["1", "2", "3"]),
        };

        // Canonical URL builder: assigned parameters in fixed key order, so
        // every generated URL has one stable spelling.
        string FilterUrl(IReadOnlyDictionary<string, string> assigned) =>
            FilterPath + "?" + string.Join("&", facets
                .Where(f => assigned.ContainsKey(f.Key))
                .Select(f => $"{f.Key}={assigned[f.Key]}"));

        // The full reachable space: each parameter unset or one of its values
        // (4 × 4 × 3 × 4 assignments, minus the empty-query state).
        var space = new HashSet<string>(StringComparer.Ordinal);
        foreach (var color in new string?[] { null, "red", "blue", "green" })
        {
            foreach (var size in new string?[] { null, "s", "m", "l" })
            {
                foreach (var sort in new string?[] { null, "price", "name" })
                {
                    foreach (var page in new string?[] { null, "1", "2", "3" })
                    {
                        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (color is not null) assigned["color"] = color;
                        if (size is not null) assigned["size"] = size;
                        if (sort is not null) assigned["sort"] = sort;
                        if (page is not null) assigned["page"] = page;
                        if (assigned.Count > 0)
                        {
                            space.Add(FilterUrl(assigned));
                        }
                    }
                }
            }
        }

        Assert.Equal(191, space.Count);

        var bodyHash = CrawlerTrapDetector.HashText(
            "Products The same eight products in a different order. color=red color=blue color=green size=s size=m size=l sort=price sort=name page=1 page=2 page=3");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var fetched = new List<string>();
        var skipped = 0;

        void Enqueue(string url)
        {
            if (!seen.Add(url))
            {
                return;
            }

            if (!detector.Admit(url).Admit)
            {
                skipped++;
                return;
            }

            queue.Enqueue(url);
        }

        Enqueue(FilterPath + "?color=red"); // the fixture seed link.

        var guard = 0;
        while (queue.Count > 0)
        {
            Assert.True(++guard < 10_000, "the simulated crawl must finish");
            var url = queue.Dequeue();

            // Dequeue re-consult: URLs queued before the flag are throttled too.
            if (!detector.Admit(url).Admit)
            {
                skipped++;
                continue;
            }

            fetched.Add(url);
            detector.ObserveContent(url, bodyHash);

            // The page links to every single-parameter variant (fixture shape).
            var current = ParseQuery(url);
            foreach (var (key, values) in facets)
            {
                foreach (var value in values)
                {
                    var variant = new Dictionary<string, string>(current, StringComparer.Ordinal) { [key] = value };
                    Enqueue(FilterUrl(variant));
                }
            }
        }

        var detection = Assert.Single(detector.Detections());
        Assert.Equal(TrapPatternFamily.ParameterCombination, detection.Family);
        Assert.True(fetched.Count < space.Count, $"fetched {fetched.Count} must be bounded below the full space {space.Count}");
        Assert.InRange(fetched.Count, 1, 40);
        Assert.True(skipped > 0, "the flagged pattern must throttle URLs");
        Assert.All(fetched, url => Assert.Contains(url, space));
        Assert.True(detection.UrlCount > fetched.Count, "discovery continues past the throttle");
    }

    /// <summary>
    /// Fixture crawler-trap-calendar shape: /trap/calendar/YYYY/M with
    /// previous/next month links — an INFINITE walk. Without the detector the
    /// simulated crawl grows without bound (control); with the detector it
    /// finishes with a bounded fetched set and an incrementing-date-path
    /// detection.
    /// </summary>
    [Fact]
    public void CalendarTrap_SimulatedCrawl_FinishesBounded_WithDetection()
    {
        var detector = new CrawlerTrapDetector(); // §5.14 defaults.
        var bodyHash = CrawlerTrapDetector.HashText("Events calendar No events this month. Previous month Next month");

        var (fetched, skipped, guard) = RunCalendarCrawl(detector, bodyHash, maxIterations: 10_000);

        Assert.True(guard < 10_000, "the calendar crawl must finish, not hit the iteration cap");
        Assert.InRange(fetched, 1, 40);
        Assert.True(skipped > 0, "the flagged calendar pattern must throttle URLs");
        var detection = Assert.Single(detector.Detections());
        Assert.Equal(TrapPatternFamily.IncrementingDatePath, detection.Family);
        Assert.Contains("{date}", detection.PatternKey);

        // Control: without detection the same walk is unbounded.
        var (controlFetched, _, controlGuard) = RunCalendarCrawl(null, bodyHash, maxIterations: 2_000);
        Assert.Equal(2_000, controlGuard);
        Assert.True(controlFetched > 1_000, "the calendar trap is infinite without detection");
    }

    private static (int Fetched, int Skipped, int Iterations) RunCalendarCrawl(
        CrawlerTrapDetector? detector, string bodyHash, int maxIterations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(int Year, int Month)>();
        var fetched = 0;
        var skipped = 0;

        void Enqueue(int year, int month)
        {
            var url = $"{CalendarRoot}/{year}/{month}";
            if (!seen.Add(url))
            {
                return;
            }

            if (detector is not null && !detector.Admit(url).Admit)
            {
                skipped++;
                return;
            }

            queue.Enqueue((year, month));
        }

        Enqueue(2026, 11); // the fixture seed link.

        var iterations = 0;
        while (queue.Count > 0 && iterations < maxIterations)
        {
            iterations++;
            var (year, month) = queue.Dequeue();
            var url = $"{CalendarRoot}/{year}/{month}";
            if (detector is not null && !detector.Admit(url).Admit)
            {
                skipped++;
                continue;
            }

            fetched++;
            detector?.ObserveContent(url, bodyHash);

            // Fixture: previous month and next month links.
            var previous = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
            var next = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            Enqueue(previous.Year, previous.Month);
            Enqueue(next.Year, next.Month);
        }

        return (fetched, skipped, iterations);
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var query = new Uri(url).Query.TrimStart('?');
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            result[pair[..separator]] = pair[(separator + 1)..];
        }

        return result;
    }
}
