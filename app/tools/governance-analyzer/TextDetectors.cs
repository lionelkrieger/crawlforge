using System.Text.RegularExpressions;

namespace CrawlForge.Tools.Governance;

/// <summary>
/// Evaluates governance rules against comment-stripped TS/JS lexical facts.
/// Detection is heuristic (documented per rule in the report): call sites, imports,
/// declarations, object keys and transmission-context URLs — never comment text.
/// </summary>
internal static class TextDetectors
{
    private static readonly string[] TrackingCalls =
    [
        "track", "trackEvent", "trackPageView", "gtag", "fbq", "ga", "captureException", "captureMessage",
    ];

    private static readonly string[] AnalyticsReceivers =
    [
        "analytics", "segment", "mixpanel", "amplitude", "posthog", "telemetry",
    ];

    private static readonly string[] MetricReceivers =
    [
        "counter", "histogram", "gauge", "meter", "metric", "telemetry", "stats",
    ];

    private static readonly string[] ConsoleMethods = ["log", "info", "warn", "error", "debug", "trace"];

    private static readonly string[] OutboundCallNames =
    [
        "fetch", "request", "get", "post", "put", "delete", "patch", "connect", "createConnection",
    ];

    private static readonly string[] OutboundReceivers =
    [
        "axios", "http", "https", "net", "tls", "got", "ky", "superagent", "undici", "needle",
    ];

    private static readonly Regex UrlLiteralRegex = new(
        @"['""`]((?:https?|wss?)://[^'""`]+)['""`]", RegexOptions.Compiled);

    private static readonly Regex FirstArgLiteralRegex = new(
        @"^\s*['""`]([^'""`]*)['""`]", RegexOptions.Compiled);

    private static readonly Regex HostInArgsRegex = new(
        @"\b(?:host|hostname)\s*:\s*['""`]([^'""`]+)['""`]", RegexOptions.Compiled);

    private static readonly Regex TypeSignatureRegex = new(
        @":\s*(number|string|boolean|any|unknown|void|Buffer)\b", RegexOptions.Compiled);

    private static readonly Regex PositionalStringRegex = new(
        @"['""`]([^'""`]+)['""`]", RegexOptions.Compiled);

    private static readonly Regex EndpointAssignmentRegex = new(
        @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*(?:endpoint|url|baseurl|baseaddress)[A-Za-z0-9_$]*)\s*=\s*['""`]([^'""`]+)['""`]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IdentityPlaceholderRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static List<GovernanceViolation> Evaluate(
        SourceFile file, TextFileFacts facts, NetworkBoundaryAllowlist boundary)
    {
        var violations = new List<GovernanceViolation>();
        var inBoundary = boundary.IsApprovedTextScope(file.RelativePath);
        var isPlugin = DetectionHelpers.IsPluginPath(file.RelativePath);

        EvaluateIdentifierPatterns(file, facts, violations);
        EvaluateImports(file, facts, inBoundary, violations);
        EvaluateMachineIdentity(file, facts, violations);
        EvaluateVendorHeaders(file, facts, violations);
        EvaluateTrackingCalls(file, facts, violations);
        EvaluatePluginMutations(file, facts, isPlugin, violations);
        EvaluateCloudLlmLiterals(file, facts, violations);
        EvaluateTransmissionEndpoints(file, facts, inBoundary, violations);
        EvaluateListeners(file, facts, violations);
        EvaluateOutboundCalls(file, facts, inBoundary, violations);
        EvaluateIdentityDimensions(file, facts, isPlugin, violations);

        return violations
            .GroupBy(v => (v.RuleId, v.Line, v.Column))
            .Select(g => g.First())
            .ToList();
    }

    private static void EvaluateIdentifierPatterns(SourceFile file, TextFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var (ruleId, patterns) in PatternLists.IdentifierPatterns)
        {
            foreach (var declaration in facts.Declarations)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(declaration.Name), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, declaration.Line, declaration.Column,
                        $"{declaration.Kind} {declaration.Name}",
                        $"Identifier '{declaration.Name}' matches a prohibited concept for {ruleId}."));
                }
            }

            foreach (var call in facts.Calls)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(call.Name), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, call.Line, call.Column,
                        $"{call.Name}({call.ArgumentsText})",
                        $"Call '{call.Name}' matches a prohibited concept for {ruleId}."));
                }
            }

            foreach (var key in facts.ObjectKeys)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(key.Key), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, key.Line, key.Column,
                        $"{key.Key}:",
                        $"Property '{key.Key}' matches a prohibited concept for {ruleId}."));
                }
            }
        }
    }

    private static void EvaluateImports(
        SourceFile file, TextFileFacts facts, bool inBoundary, List<GovernanceViolation> violations)
    {
        foreach (var import in facts.Imports)
        {
            if (import.IsDynamic
                && (import.Module.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || import.Module.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.NrE1, file, import.Line, import.Column,
                    $"import('{import.Module}')",
                    "Remote code loading is prohibited; plugins are local artifacts only."));
            }

            foreach (var (name, ruleId, isPrefix) in PatternLists.BannedNpmPackages)
            {
                var matches = isPrefix
                    ? import.Module.StartsWith(name, StringComparison.Ordinal)
                    : string.Equals(import.Module, name, StringComparison.Ordinal);
                if (matches)
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, import.Line, import.Column,
                        $"import '{import.Module}'",
                        $"Package '{import.Module}' is a prohibited SDK surface for {ruleId}."));
                }
            }

            if (!inBoundary && PatternLists.OutboundTextModules.Contains(import.Module, StringComparer.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net02, file, import.Line, import.Column,
                    $"import '{import.Module}'",
                    "Outbound network module imported outside approved boundary scopes."));
            }
        }
    }

    private static void EvaluateMachineIdentity(SourceFile file, TextFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var literal in facts.Literals)
        {
            if (DetectionHelpers.ContainsAny(literal.Value, PatternLists.MachineIdentityLiterals)
                || DetectionHelpers.ContainsAny(literal.Value, PatternLists.OsIdentityToolingLiterals))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, literal.Line, literal.Column,
                    literal.Value,
                    "Literal references an OS identity store or hardware identifier."));
            }
        }

        foreach (var call in facts.Calls)
        {
            if (string.Equals(call.Name, "networkInterfaces", StringComparison.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, call.Line, call.Column,
                    $"{call.Receiver}.{call.Name}()",
                    "Network interface enumeration reads hardware (MAC) identifiers."));
            }
        }
    }

    private static void EvaluateVendorHeaders(SourceFile file, TextFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var literal in facts.Literals)
        {
            if (DetectionHelpers.ContainsAny(literal.Value, PatternLists.VendorHeaderLiterals))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr02, file, literal.Line, literal.Column,
                    literal.Value,
                    "Identity-bearing vendor header is prohibited."));
            }
        }
    }

    private static void EvaluateTrackingCalls(SourceFile file, TextFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var call in facts.Calls)
        {
            var isTracking = TrackingCalls.Contains(call.Name, StringComparer.Ordinal)
                || (string.Equals(call.Name, "identify", StringComparison.Ordinal)
                    && DetectionHelpers.ContainsAny(call.Receiver, AnalyticsReceivers))
                || (string.Equals(call.Name, "push", StringComparison.Ordinal)
                    && call.Receiver.EndsWith("dataLayer", StringComparison.OrdinalIgnoreCase));
            if (isTracking)
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr06, file, call.Line, call.Column,
                    $"{call.Receiver}.{call.Name}({call.ArgumentsText})",
                    "Analytics/tracking invocation is prohibited (no telemetry)."));
            }
        }
    }

    private static void EvaluatePluginMutations(
        SourceFile file, TextFileFacts facts, bool isPlugin, List<GovernanceViolation> violations)
    {
        if (!isPlugin)
        {
            return;
        }

        foreach (var call in facts.Calls)
        {
            if (IdentifierTokens.MatchesAnyPattern(
                    IdentifierTokens.Tokenize(call.Name), PatternLists.PluginMutationPatterns))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.NrE5, file, call.Line, call.Column,
                    $"{call.Name}({call.ArgumentsText})",
                    "Plugin surfaces must never invoke crawl-lifecycle or configuration mutations."));
            }
        }
    }

    private static void EvaluateCloudLlmLiterals(SourceFile file, TextFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var literal in facts.Literals)
        {
            if (!EndpointClassifier.TryParse(literal.Value, out var endpoint) || endpoint is null)
            {
                continue;
            }

            foreach (var vendor in PatternLists.CloudLlmVendorHosts)
            {
                if (EndpointClassifier.HostMatches(endpoint.Host, vendor))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.NrE7, file, literal.Line, literal.Column,
                        literal.Value,
                        $"Cloud LLM/embedding/vector vendor endpoint '{vendor}' is prohibited."));
                }
            }
        }
    }

    /// <summary>
    /// Transmission-context endpoint rules for TS: URLs inside outbound call arguments and
    /// endpoint-named assignments. Loopback and approved boundary scopes are exempt.
    /// </summary>
    private static void EvaluateTransmissionEndpoints(
        SourceFile file, TextFileFacts facts, bool inBoundary, List<GovernanceViolation> violations)
    {
        var candidates = new List<(string Url, int Line, int Column)>();
        foreach (var call in facts.Calls)
        {
            if (!IsOutboundCall(call))
            {
                continue;
            }

            foreach (Match match in UrlLiteralRegex.Matches(call.ArgumentsText))
            {
                candidates.Add((match.Groups[1].Value, call.Line, call.Column));
            }
        }

        foreach (Match match in EndpointAssignmentRegex.Matches(facts.Code))
        {
            var line = 1;
            var column = 1;
            for (var i = 0; i < match.Index; i++)
            {
                if (facts.Code[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            candidates.Add((match.Groups[2].Value, line, column));
        }

        foreach (var (url, line, column) in candidates)
        {
            if (!EndpointClassifier.TryParse(url, out var endpoint) || endpoint is null)
            {
                continue;
            }

            foreach (var (ruleId, hosts) in PatternLists.ContextualVendorHosts)
            {
                foreach (var vendor in hosts)
                {
                    if (EndpointClassifier.HostMatches(endpoint.Host, vendor))
                    {
                        violations.Add(DetectionHelpers.Violation(
                            ruleId, file, line, column,
                            url,
                            $"Vendor service endpoint '{vendor}' in transmission context is prohibited."));
                    }
                }
            }

            if (EndpointClassifier.IsLoopbackHost(endpoint.Host) || inBoundary)
            {
                continue;
            }

            foreach (var (ruleId, patterns) in PatternLists.EndpointPathPatterns)
            {
                foreach (var pattern in patterns)
                {
                    if (EndpointClassifier.PathContainsSequence(endpoint.PathSegments, pattern))
                    {
                        violations.Add(DetectionHelpers.Violation(
                            ruleId, file, line, column,
                            url,
                            $"Endpoint path '{string.Join('/', pattern)}' in transmission context is prohibited."));
                    }
                }
            }

            foreach (var pattern in PatternLists.EmbeddingLlmPathPatterns)
            {
                if (EndpointClassifier.PathContainsSequence(endpoint.PathSegments, pattern))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.NrE7, file, line, column,
                        url,
                        "Non-loopback embedding/LLM API endpoint is prohibited (loopback daemon or bundled ONNX only)."));
                }
            }
        }
    }

    private static void EvaluateListeners(SourceFile file, TextFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var config in facts.HostConfigs)
        {
            if (config.Value is "0.0.0.0" or "::" or "*")
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net01, file, config.Line, config.Column,
                    $"{config.Key}: '{config.Value}'",
                    "Wildcard host configuration opens a non-loopback listener."));
            }
        }

        foreach (var call in facts.Calls)
        {
            if (!string.Equals(call.Name, "listen", StringComparison.Ordinal))
            {
                continue;
            }

            var args = call.ArgumentsText;

            // Type signatures in interface/type declarations are not call sites.
            if (TypeSignatureRegex.IsMatch(args))
            {
                continue;
            }

            if (args.Contains("'0.0.0.0'", StringComparison.Ordinal)
                || args.Contains("\"0.0.0.0\"", StringComparison.Ordinal)
                || args.Contains("'::'", StringComparison.Ordinal)
                || args.Contains("\"::\"", StringComparison.Ordinal)
                || args.Contains("'*'", StringComparison.Ordinal)
                || args.Contains("\"*\"", StringComparison.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net01, file, call.Line, call.Column,
                    $"listen({args})",
                    "Listener binds a non-loopback address."));
                continue;
            }

            var hostMatch = HostInArgsRegex.Match(args);
            if (hostMatch.Success)
            {
                if (!EndpointClassifier.IsLoopbackHost(hostMatch.Groups[1].Value))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.Net01, file, call.Line, call.Column,
                        $"listen({args})",
                        "Listener binds a non-loopback host."));
                }

                continue;
            }

            var positionalHost = PositionalStringRegex.Match(args);
            if (positionalHost.Success)
            {
                var value = positionalHost.Groups[1].Value;
                var isLocalIpc = value.StartsWith('/') || value.StartsWith("\\\\", StringComparison.Ordinal)
                    || value.Contains(".sock", StringComparison.Ordinal);
                if (!isLocalIpc && !EndpointClassifier.IsLoopbackHost(value))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.Net01, file, call.Line, call.Column,
                        $"listen({args})",
                        "Listener binds a non-loopback host."));
                }

                continue;
            }

            // No host argument at all: Node's listen() defaults to all interfaces (::).
            violations.Add(DetectionHelpers.Violation(
                RuleCatalog.Net01, file, call.Line, call.Column,
                $"listen({args})",
                "listen() without a provable loopback host binds all interfaces (Node default)."));
        }
    }

    private static void EvaluateOutboundCalls(
        SourceFile file, TextFileFacts facts, bool inBoundary, List<GovernanceViolation> violations)
    {
        foreach (var call in facts.Calls)
        {
            if (call.IsConstructor && call.Name is "WebSocket" or "EventSource" or "XMLHttpRequest")
            {
                var urlMatch = FirstArgLiteralRegex.Match(call.ArgumentsText);
                var provenLocal = urlMatch.Success
                    && (EndpointClassifier.IsLoopbackUrl(urlMatch.Groups[1].Value)
                        || EndpointClassifier.IsRelativeUrl(urlMatch.Groups[1].Value));
                if (provenLocal || inBoundary)
                {
                    continue;
                }

                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net02, file, call.Line, call.Column,
                    $"new {call.Name}({call.ArgumentsText})",
                    "Outbound client outside approved boundary scopes must target loopback only."));
                continue;
            }

            if (!IsOutboundCall(call))
            {
                continue;
            }

            if (call.Receiver is "http" or "https" && call.Name is "request" or "get")
            {
                var hostMatch = HostInArgsRegex.Match(call.ArgumentsText);
                if (!hostMatch.Success || EndpointClassifier.IsLoopbackHost(hostMatch.Groups[1].Value) || inBoundary)
                {
                    continue;
                }

                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net02, file, call.Line, call.Column,
                    $"{call.Receiver}.{call.Name}({call.ArgumentsText})",
                    "Outbound request to a non-loopback host outside approved boundary scopes."));
                continue;
            }

            var firstArg = FirstArgLiteralRegex.Match(call.ArgumentsText);
            if (firstArg.Success)
            {
                var target = firstArg.Groups[1].Value;
                if (EndpointClassifier.IsLoopbackUrl(target) || EndpointClassifier.IsRelativeUrl(target) || inBoundary)
                {
                    continue;
                }

                if (EndpointClassifier.TryParse(target, out _))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.Net02, file, call.Line, call.Column,
                        $"{call.Name}('{target}')",
                        "Outbound call to a non-loopback target outside approved boundary scopes."));
                }

                continue;
            }

            if (!inBoundary)
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net02, file, call.Line, call.Column,
                    $"{call.Name}({call.ArgumentsText})",
                    "Outbound call with dynamic target outside approved boundary scopes."));
            }
        }
    }

    private static void EvaluateIdentityDimensions(
        SourceFile file, TextFileFacts facts, bool isPlugin, List<GovernanceViolation> violations)
    {
        foreach (var call in facts.Calls)
        {
            var isConsole = string.Equals(call.Receiver, "console", StringComparison.Ordinal)
                && ConsoleMethods.Contains(call.Name, StringComparer.Ordinal);
            var isTelemetry = PatternLists.TextTelemetryCallNames.Contains(call.Name, StringComparer.Ordinal)
                || isConsole
                || (call.Name is "record" or "observe" or "increment"
                    && IdentifierTokens.TokenizeText(call.Receiver).Any(t => MetricReceivers.Contains(t, StringComparer.Ordinal)))
                || (string.Equals(call.Name, "identify", StringComparison.Ordinal)
                    && DetectionHelpers.ContainsAny(call.Receiver, AnalyticsReceivers));
            if (!isTelemetry)
            {
                continue;
            }

            var matched = FindIdentityDimension(call.ArgumentsText);
            if (matched is not null)
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Id01, file, call.Line, call.Column,
                    $"{call.Name}({call.ArgumentsText})",
                    $"Identity dimension '{matched}' in event/metric/log payload violates the privacy invariant."));
            }
        }

        if (isPlugin)
        {
            foreach (var key in facts.ObjectKeys)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(key.Key), PatternLists.IdentityDimensions))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.Id01, file, key.Line, key.Column,
                        $"{key.Key}:",
                        "Identity dimension on a plugin surface violates the privacy invariant."));
                }
            }

            foreach (var declaration in facts.Declarations)
            {
                if (IdentifierTokens.MatchesAnyPattern(
                        IdentifierTokens.Tokenize(declaration.Name), PatternLists.IdentityDimensions))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.Id01, file, declaration.Line, declaration.Column,
                        $"{declaration.Kind} {declaration.Name}",
                        "Identity dimension on a plugin surface violates the privacy invariant."));
                }
            }
        }
    }

    private static string? FindIdentityDimension(string argumentsText)
    {
        var tokens = IdentifierTokens.TokenizeText(argumentsText);
        foreach (var pattern in PatternLists.IdentityDimensions)
        {
            if (IdentifierTokens.ContainsSequence(tokens, pattern))
            {
                return string.Concat(pattern);
            }
        }

        foreach (Match match in IdentityPlaceholderRegex.Matches(argumentsText))
        {
            var placeholderTokens = IdentifierTokens.TokenizeText(match.Groups[1].Value);
            foreach (var pattern in PatternLists.IdentityDimensions)
            {
                if (IdentifierTokens.ContainsSequence(placeholderTokens, pattern))
                {
                    return string.Concat(pattern);
                }
            }
        }

        return null;
    }

    private static bool IsOutboundCall(TextCallFact call)
    {
        if (string.Equals(call.Name, "fetch", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(call.Receiver, "axios", StringComparison.Ordinal)
            || string.Equals(call.Name, "axios", StringComparison.Ordinal))
        {
            return true;
        }

        return OutboundReceivers.Contains(call.Receiver, StringComparer.Ordinal)
            && OutboundCallNames.Contains(call.Name, StringComparer.Ordinal);
    }
}
