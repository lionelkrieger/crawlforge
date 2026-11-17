using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// One merged tag/container detection (internal detail richer than the
/// locked <see cref="AnalyticsDetectionDraft"/>; extras ride evidence JSON).
/// DetectionSource vocabulary: dom | proxy | both (contract comment).
/// </summary>
internal sealed record TagDetection(
    string Vendor,
    string Kind,
    string? ContainerId,
    bool FromDom,
    bool FromNetwork,
    string? Placement,
    bool Async,
    bool Defer,
    bool FirstParty,
    string? EvidenceUrl,
    string? Excerpt)
{
    /// <summary>Merged detection source label.</summary>
    public string Source => (FromDom, FromNetwork) switch
    {
        (true, true) => "both",
        (true, false) => "dom",
        _ => "proxy",
    };

    /// <summary>Projects onto the locked detection draft.</summary>
    public AnalyticsDetectionDraft ToDraft() => new(Vendor, ContainerId, Source, Placement);
}

/// <summary>
/// Tag/container inventory detector (FS-CRAWL-002 §14.1): scans the stored
/// DOM (script sources + inline markers) and the proxy network capture
/// against the bundled catalogue plus user-defined local beacon patterns,
/// then merges detections by vendor + container ID. Offline: operates only
/// on stored artifacts; no fetches, no vendor lookups (E2). All pattern
/// matching is time-bounded (§7.16 discipline).
/// </summary>
internal sealed class TagInventoryDetector
{
    private readonly MarTechOptions _options;
    private readonly TimeSpan _patternTimeout;

    public TagInventoryDetector(MarTechOptions options)
    {
        _options = options;
        _patternTimeout = TimeSpan.FromMilliseconds(Math.Max(1, options.PatternTimeoutMs));
    }

    /// <summary>Detects tags from the effective document and captured requests.</summary>
    public IReadOnlyList<TagDetection> Detect(
        DomDocument? document,
        IReadOnlyList<ProxyCapturedRequest>? capturedRequests,
        string pageUrl)
    {
        var detections = new List<TagDetection>();
        var pageHost = Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ? pageUri.Host : null;

        if (document is not null)
        {
            DetectFromDom(document.Root, inHead: false, pageHost, detections);
        }

        if (capturedRequests is not null)
        {
            foreach (var request in capturedRequests)
            {
                DetectFromNetwork(request.Url, detections);
            }
        }

        DetectCustomBeacons(document, capturedRequests, detections);
        return Merge(detections);
    }

    private void DetectFromDom(DomNode node, bool inHead, string? pageHost, List<TagDetection> detections)
    {
        var nowInHead = inHead || node.TagName == "head";
        if (node.TagName == "script")
        {
            var src = Attr(node, "src");
            var inline = src is null ? node.TextContent : null;
            var placement = nowInHead ? "head" : "body";
            var async = node.Attributes.ContainsKey("async");
            var defer = node.Attributes.ContainsKey("defer");

            foreach (var signature in MarTechCatalogue.Signatures)
            {
                string? matchedText = null;
                if (src is not null && signature.ScriptSrcMarkers.Any(marker =>
                    src.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedText = src;
                }
                else if (inline is not null && signature.InlineMarkers.Any(marker =>
                    inline.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedText = inline;
                }

                if (matchedText is null)
                {
                    continue;
                }

                var firstParty = src is null
                    || pageHost is null
                    || !Uri.TryCreate(src, UriKind.Absolute, out var srcUri)
                    || string.Equals(srcUri.Host, pageHost, StringComparison.OrdinalIgnoreCase);

                detections.Add(new TagDetection(
                    signature.Vendor,
                    signature.Kind,
                    ExtractId(signature, matchedText),
                    FromDom: true,
                    FromNetwork: false,
                    placement,
                    async,
                    defer,
                    firstParty,
                    EvidenceUrl: src,
                    Excerpt: Excerpt(matchedText)));
            }
        }

        foreach (var child in node.Children)
        {
            DetectFromDom(child, nowInHead, pageHost, detections);
        }
    }

    private void DetectFromNetwork(string url, List<TagDetection> detections)
    {
        foreach (var signature in MarTechCatalogue.Signatures)
        {
            if (!signature.NetworkMarkers.Any(marker => url.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            detections.Add(new TagDetection(
                signature.Vendor,
                signature.Kind,
                ExtractId(signature, url) ?? NetworkHitClassifier.ContainerIdFromUrl(url),
                FromDom: false,
                FromNetwork: true,
                Placement: null,
                Async: false,
                Defer: false,
                FirstParty: false,
                EvidenceUrl: url,
                Excerpt: null));
        }
    }

    private void DetectCustomBeacons(
        DomDocument? document,
        IReadOnlyList<ProxyCapturedRequest>? capturedRequests,
        List<TagDetection> detections)
    {
        foreach (var beacon in _options.CustomBeacons)
        {
            Regex regex;
            try
            {
                regex = new Regex(beacon.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _patternTimeout);
            }
            catch (ArgumentException)
            {
                continue; // Invalid local pattern: isolated, never fatal.
            }

            var vendor = "custom:" + beacon.Name;
            try
            {
                switch (beacon.Target.ToLowerInvariant())
                {
                    case "script-src" when document is not null:
                        foreach (var script in EnumerateScripts(document.Root))
                        {
                            var src = Attr(script, "src");
                            if (src is not null && SafeIsMatch(regex, src))
                            {
                                detections.Add(new TagDetection(vendor, MarTechCatalogue.KindAnalytics,
                                    FirstGroup(regex, src), true, false, null, false, false, false, src, null));
                            }
                        }

                        break;

                    case "inline" when document is not null:
                        foreach (var script in EnumerateScripts(document.Root))
                        {
                            if (Attr(script, "src") is null && SafeIsMatch(regex, script.TextContent))
                            {
                                detections.Add(new TagDetection(vendor, MarTechCatalogue.KindAnalytics,
                                    FirstGroup(regex, script.TextContent), true, false, null, false, false, true, null,
                                    Excerpt(script.TextContent)));
                            }
                        }

                        break;

                    case "network" when capturedRequests is not null:
                        foreach (var request in capturedRequests)
                        {
                            if (SafeIsMatch(regex, request.Url))
                            {
                                detections.Add(new TagDetection(vendor, MarTechCatalogue.KindAnalytics,
                                    FirstGroup(regex, request.Url), false, true, null, false, false, false, request.Url, null));
                            }
                        }

                        break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Time-bounded discipline: an expensive local pattern is
                // skipped for this page, never blocks the audit.
            }
        }
    }

    private static IEnumerable<DomNode> EnumerateScripts(DomNode node)
    {
        if (node.TagName == "script")
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var script in EnumerateScripts(child))
            {
                yield return script;
            }
        }
    }

    private string? ExtractId(MarTechSignature signature, string text)
    {
        foreach (var pattern in signature.IdPatterns)
        {
            try
            {
                var match = Regex.Match(text, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _patternTimeout);
                if (match.Success)
                {
                    return match.Groups.Count > 1 && match.Groups[1].Success
                        ? match.Groups[1].Value
                        : match.Value;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Bounded: skip this identifier pattern.
            }
            catch (ArgumentException)
            {
                // Defensive: catalogue patterns are compile-checked constants.
            }
        }

        return null;
    }

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? FirstGroup(Regex regex, string input)
    {
        try
        {
            var match = regex.Match(input);
            return match.Success && match.Groups.Count > 1 && match.Groups[1].Success
                ? match.Groups[1].Value
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private string? Excerpt(string text) =>
        text.Length <= _options.EvidenceExcerptBound ? text : text[.._options.EvidenceExcerptBound];

    private static string? Attr(DomNode node, string name) =>
        node.Attributes.TryGetValue(name, out var value) ? value : null;

    private static IReadOnlyList<TagDetection> Merge(List<TagDetection> detections)
    {
        var merged = new List<TagDetection>();
        foreach (var group in detections.GroupBy(detection =>
            (detection.Vendor, detection.ContainerId ?? string.Empty)))
        {
            var members = group.ToList();
            var dom = members.FirstOrDefault(member => member.FromDom);
            var net = members.FirstOrDefault(member => member.FromNetwork);
            var representative = dom ?? net ?? members[0];
            merged.Add(representative with
            {
                FromDom = members.Any(member => member.FromDom),
                FromNetwork = members.Any(member => member.FromNetwork),
                Placement = dom?.Placement ?? representative.Placement,
                Async = members.Any(member => member.Async),
                Defer = members.Any(member => member.Defer),
                EvidenceUrl = representative.EvidenceUrl ?? net?.EvidenceUrl,
            });
        }

        // Absorb unidentified script-load observations into the vendor's
        // single identified container (a loader belongs to its pixel); when
        // several distinct IDs exist the unidentified rows stay separate.
        var final = new List<TagDetection>();
        foreach (var vendorGroup in merged.GroupBy(detection => detection.Vendor, StringComparer.Ordinal))
        {
            var members = vendorGroup.ToList();
            var identified = members.Where(member => !string.IsNullOrEmpty(member.ContainerId)).ToList();
            var unidentified = members.Where(member => string.IsNullOrEmpty(member.ContainerId)).ToList();
            if (identified.Count == 1 && unidentified.Count > 0)
            {
                var target = identified[0];
                foreach (var extra in unidentified)
                {
                    target = target with
                    {
                        FromDom = target.FromDom || extra.FromDom,
                        FromNetwork = target.FromNetwork || extra.FromNetwork,
                        Placement = target.Placement ?? extra.Placement,
                        Async = target.Async || extra.Async,
                        Defer = target.Defer || extra.Defer,
                        EvidenceUrl = target.EvidenceUrl ?? extra.EvidenceUrl,
                    };
                }

                final.Add(target);
            }
            else
            {
                final.AddRange(members);
            }
        }

        return final;
    }
}
