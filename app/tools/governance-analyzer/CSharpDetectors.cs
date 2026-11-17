using System.Text.RegularExpressions;

namespace CrawlForge.Tools.Governance;

/// <summary>
/// Evaluates every governance rule against Roslyn syntax facts for one C# file.
/// All matching is behavioral (calls/declarations/imports/transmission-context endpoints);
/// comments and pure data literals are exempt unless a rule explicitly says otherwise.
/// </summary>
internal static class CSharpDetectors
{
    private static readonly string[] WmiRegistryTypes =
    [
        "Registry", "RegistryKey", "RegistryValueKind", "RegistryValueOptions",
        "ManagementObjectSearcher", "ManagementObject", "ManagementClass", "ManagementScope",
        "ManagementPath", "ManagementBaseObject", "ManagementQuery", "WqlObjectQuery",
        "ManagementObjectCollection",
    ];

    private static readonly string[] TrackingMethods =
    [
        "TrackEvent", "TrackPageView", "TrackMetric", "TrackException", "TrackDependency",
        "TrackTrace", "CaptureException", "CaptureMessage", "gtag", "fbq",
    ];

    private static readonly string[] AnalyticsReceivers =
    [
        "analytics", "segment", "mixpanel", "amplitude", "posthog", "telemetry",
    ];

    private static readonly string[] MetricReceivers =
    [
        "counter", "histogram", "gauge", "meter", "metric", "telemetry",
    ];

    private static readonly string[] MetricMethods = ["Add", "Record", "Observe", "Set", "Increment", "Decrement"];

    private static readonly string[] ListenerTypes = ["TcpListener", "IPEndPoint"];

    private static readonly string[] AnyAddressMarkers =
    [
        "IPAddress.Any", "IPAddress.IPv6Any", "IPAddress.None", "\"0.0.0.0\"", "\"::\"",
    ];

    private static readonly string[] LoopbackPrefixes =
    [
        "http://127.", "https://127.", "http://localhost", "https://localhost",
        "http://[::1]", "https://[::1]", "ws://127.", "ws://localhost", "ws://[::1]",
    ];

    private static readonly Regex PlaceholderRegex = new(@"\{@?([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static readonly Regex WildcardBindRegex = new(
        @"^https?://(\*|\+|0\.0\.0\.0|\[?::\]?)(:\d+)?/?$", RegexOptions.Compiled);

    public static List<GovernanceViolation> Evaluate(
        SourceFile file, CSharpFileFacts facts, NetworkBoundaryAllowlist boundary)
    {
        var violations = new List<GovernanceViolation>();
        EvaluateIdentifierPatterns(file, facts, violations);
        EvaluateUsings(file, facts, violations);
        EvaluateMachineIdentityLiterals(file, facts, violations);
        EvaluateEnvironmentIdentity(file, facts, violations);
        EvaluateWmiRegistryTypes(file, facts, violations);
        EvaluateVendorHeaders(file, facts, violations);
        EvaluateTrackingCalls(file, facts, violations);
        EvaluateRemoteCodeLoading(file, facts, violations);
        EvaluatePluginMutations(file, facts, violations);
        EvaluateEndpoints(file, facts, boundary, violations);
        EvaluateListeners(file, facts, violations);
        EvaluateOutboundBoundary(file, facts, boundary, violations);
        EvaluateIdentityDimensions(file, facts, violations);

        return violations
            .GroupBy(v => (v.RuleId, v.Line, v.Column))
            .Select(g => g.First())
            .ToList();
    }

    private static void EvaluateIdentifierPatterns(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var (ruleId, patterns) in PatternLists.IdentifierPatterns)
        {
            foreach (var declaration in facts.Declarations)
            {
                var nameTokens = IdentifierTokens.Tokenize(declaration.Name);
                var typeTokens = IdentifierTokens.Tokenize(LastSegment(declaration.TypeName));
                if (IdentifierTokens.MatchesAnyPattern(nameTokens, patterns)
                    || IdentifierTokens.MatchesAnyPattern(typeTokens, patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, declaration.Line, declaration.Column,
                        $"{declaration.Kind} {declaration.Name}",
                        $"Identifier '{declaration.Name}' matches a prohibited concept for {ruleId}."));
                }
            }

            foreach (var invocation in facts.Invocations)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(invocation.MethodName), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, invocation.Line, invocation.Column,
                        $"{invocation.MethodName}{invocation.ArgumentsText}",
                        $"Invocation '{invocation.MethodName}' matches a prohibited concept for {ruleId}."));
                }
            }

            foreach (var creation in facts.ObjectCreations)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(creation.TypeName), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, creation.Line, creation.Column,
                        $"new {creation.TypeName}{creation.ArgumentsText}",
                        $"Type '{creation.TypeName}' matches a prohibited concept for {ruleId}."));
                }
            }

            foreach (var access in facts.MemberAccesses)
            {
                // Environment.SpecialFolder.* is a BCL PATH enum (a location, never an
                // identity read): UserProfile/ApplicationData/... locate per-user app-data
                // directories and read no username, account, or OS identity. Exempt from
                // concept matching for every rule (wave-0C gate ruling).
                if (IsSpecialFolderAccess(access))
                {
                    continue;
                }

                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(access.Name), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, access.Line, access.Column,
                        $"{access.Receiver}.{access.Name}",
                        $"Member '{access.Name}' matches a prohibited concept for {ruleId}."));
                }
            }
        }
    }

    private static void EvaluateUsings(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var usingFact in facts.Usings)
        {
            foreach (var (prefix, ruleId) in PatternLists.BannedUsingPrefixes)
            {
                if (usingFact.Name.StartsWith(prefix, StringComparison.Ordinal)
                    && (usingFact.Name.Length == prefix.Length || usingFact.Name[prefix.Length] == '.'))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, usingFact.Line, usingFact.Column,
                        $"using {usingFact.Name};",
                        $"Namespace '{usingFact.Name}' is a prohibited SDK surface for {ruleId}."));
                }
            }
        }
    }

    private static void EvaluateMachineIdentityLiterals(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var literal in facts.Literals)
        {
            if (DetectionHelpers.ContainsAny(literal.Value, PatternLists.MachineIdentityLiterals))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, literal.Line, literal.Column,
                    literal.Value,
                    "Literal references an OS identity store, WMI class, or hardware identifier."));
            }
        }
    }

    private static void EvaluateEnvironmentIdentity(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var access in facts.MemberAccesses)
        {
            if (!string.Equals(access.Receiver, "Environment", StringComparison.Ordinal))
            {
                continue;
            }

            if (access.Name is "MachineName" or "OSVersion")
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, access.Line, access.Column,
                    $"Environment.{access.Name}",
                    "OS-level machine identity read is prohibited."));
            }
            else if (access.Name is "UserName" or "UserDomainName")
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr03, file, access.Line, access.Column,
                    $"Environment.{access.Name}",
                    "OS account identity read is prohibited (no account system)."));
            }
        }
    }

    private static void EvaluateWmiRegistryTypes(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var creation in facts.ObjectCreations)
        {
            if (WmiRegistryTypes.Contains(creation.TypeName, StringComparer.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, creation.Line, creation.Column,
                    $"new {creation.TypeName}{creation.ArgumentsText}",
                    $"Registry/WMI type '{creation.TypeName}' reads OS identity stores."));
            }
        }

        foreach (var declaration in facts.Declarations)
        {
            var typeName = LastSegment(declaration.TypeName);
            if (WmiRegistryTypes.Contains(typeName, StringComparer.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, declaration.Line, declaration.Column,
                    $"{declaration.Kind} {declaration.Name}: {typeName}",
                    $"Registry/WMI type '{typeName}' reads OS identity stores."));
            }
        }

        foreach (var access in facts.MemberAccesses)
        {
            if (WmiRegistryTypes.Contains(access.Receiver, StringComparer.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, access.Line, access.Column,
                    $"{access.Receiver}.{access.Name}",
                    $"Registry/WMI API '{access.Receiver}.{access.Name}' reads OS identity stores."));
            }
        }
    }

    private static void EvaluateVendorHeaders(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
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

    private static void EvaluateTrackingCalls(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var invocation in facts.Invocations)
        {
            var isTracking = TrackingMethods.Contains(invocation.MethodName, StringComparer.Ordinal)
                || (string.Equals(invocation.MethodName, "Identify", StringComparison.Ordinal)
                    && DetectionHelpers.ContainsAny(invocation.Receiver, AnalyticsReceivers))
                || (string.Equals(invocation.MethodName, "Push", StringComparison.Ordinal)
                    && invocation.Receiver.EndsWith("dataLayer", StringComparison.OrdinalIgnoreCase));
            if (isTracking)
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr06, file, invocation.Line, invocation.Column,
                    $"{invocation.Receiver}.{invocation.MethodName}{invocation.ArgumentsText}",
                    "Analytics/tracking invocation is prohibited (no telemetry)."));
            }
        }
    }

    private static void EvaluateRemoteCodeLoading(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var invocation in facts.Invocations)
        {
            if (invocation.MethodName is not ("LoadFrom" or "LoadFile"))
            {
                continue;
            }

            foreach (var argument in invocation.Arguments)
            {
                if (argument.LiteralValue is { } value
                    && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.NrE1, file, invocation.Line, invocation.Column,
                        $"{invocation.MethodName}(\"{value}\")",
                        "Remote code loading is prohibited; plugins are local artifacts only."));
                }
            }
        }
    }

    private static void EvaluatePluginMutations(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        if (!DetectionHelpers.IsPluginPath(file.RelativePath))
        {
            return;
        }

        foreach (var invocation in facts.Invocations)
        {
            if (IdentifierTokens.MatchesAnyPattern(
                    IdentifierTokens.Tokenize(invocation.MethodName), PatternLists.PluginMutationPatterns))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.NrE5, file, invocation.Line, invocation.Column,
                    $"{invocation.MethodName}{invocation.ArgumentsText}",
                    "Plugin surfaces must never invoke crawl-lifecycle or configuration mutations."));
            }
        }
    }

    private static void EvaluateEndpoints(
        SourceFile file, CSharpFileFacts facts, NetworkBoundaryAllowlist boundary, List<GovernanceViolation> violations)
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

            if (!DetectionHelpers.IsTransmissionContext(literal.Context))
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
                            ruleId, file, literal.Line, literal.Column,
                            literal.Value,
                            $"Vendor service endpoint '{vendor}' in transmission context is prohibited."));
                    }
                }
            }

            var isLoopback = EndpointClassifier.IsLoopbackHost(endpoint.Host);
            var inBoundary = boundary.IsApprovedCSharpScope(literal.Namespace, literal.ContainingType, file.RelativePath);
            if (isLoopback || inBoundary)
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
                            ruleId, file, literal.Line, literal.Column,
                            literal.Value,
                            $"Endpoint path '{string.Join('/', pattern)}' in transmission context is prohibited."));
                    }
                }
            }

            foreach (var pattern in PatternLists.EmbeddingLlmPathPatterns)
            {
                if (EndpointClassifier.PathContainsSequence(endpoint.PathSegments, pattern))
                {
                    violations.Add(DetectionHelpers.Violation(
                        RuleCatalog.NrE7, file, literal.Line, literal.Column,
                        literal.Value,
                        "Non-loopback embedding/LLM API endpoint is prohibited (loopback daemon or bundled ONNX only)."));
                }
            }
        }
    }

    private static void EvaluateListeners(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var creation in facts.ObjectCreations)
        {
            if (ListenerTypes.Contains(creation.TypeName, StringComparer.Ordinal)
                && DetectionHelpers.ContainsAny(creation.ArgumentsText, AnyAddressMarkers))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net01, file, creation.Line, creation.Column,
                    $"new {creation.TypeName}{creation.ArgumentsText}",
                    "Listener binds a non-loopback address; only loopback binds are permitted."));
            }
        }

        foreach (var invocation in facts.Invocations)
        {
            if (string.Equals(invocation.MethodName, "ListenAnyIP", StringComparison.Ordinal))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net01, file, invocation.Line, invocation.Column,
                    $"{invocation.MethodName}{invocation.ArgumentsText}",
                    "ListenAnyIP opens a non-loopback listener."));
                continue;
            }

            if (invocation.MethodName is "Listen" or "Bind"
                && DetectionHelpers.ContainsAny(invocation.ArgumentsText, AnyAddressMarkers))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net01, file, invocation.Line, invocation.Column,
                    $"{invocation.MethodName}{invocation.ArgumentsText}",
                    "Listener binds a non-loopback address; only loopback binds are permitted."));
            }

            if (invocation.MethodName is "UseUrls" or "UseSetting")
            {
                foreach (var argument in invocation.Arguments)
                {
                    if (argument.LiteralValue is { } value && WildcardBindRegex.IsMatch(value))
                    {
                        violations.Add(DetectionHelpers.Violation(
                            RuleCatalog.Net01, file, invocation.Line, invocation.Column,
                            value,
                            "Wildcard URL binding opens a non-loopback listener."));
                    }
                }
            }
        }

        foreach (var literal in facts.Literals)
        {
            if (literal.Context != LiteralContext.BindingAssignment)
            {
                continue;
            }

            if (literal.Value is "0.0.0.0" or "::" or "*" || WildcardBindRegex.IsMatch(literal.Value))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net01, file, literal.Line, literal.Column,
                    literal.Value,
                    "Non-loopback bind address is prohibited."));
            }
        }
    }

    private static void EvaluateOutboundBoundary(
        SourceFile file, CSharpFileFacts facts, NetworkBoundaryAllowlist boundary, List<GovernanceViolation> violations)
    {
        foreach (var creation in facts.ObjectCreations)
        {
            if (!PatternLists.OutboundClientTypes.Contains(creation.TypeName, StringComparer.Ordinal))
            {
                continue;
            }

            var loopbackProven = creation.Arguments.Any(a => a.LiteralValue is { } v && EndpointClassifier.IsLoopbackUrl(v))
                || (creation.InitializerText is { } initializer
                    && DetectionHelpers.ContainsAny(initializer, LoopbackPrefixes));
            if (loopbackProven
                || boundary.IsApprovedCSharpScope(creation.Namespace, creation.ContainingType, file.RelativePath))
            {
                continue;
            }

            violations.Add(DetectionHelpers.Violation(
                RuleCatalog.Net02, file, creation.Line, creation.Column,
                $"new {creation.TypeName}{creation.ArgumentsText}",
                "Outbound client outside approved boundary scopes (crawl target/proxy, user destination, external DB, consented DNS) must target loopback only."));
        }

        foreach (var invocation in facts.Invocations)
        {
            var isCreate = string.Equals(invocation.MethodName, "Create", StringComparison.Ordinal)
                && invocation.Receiver is "WebRequest" or "HttpWebRequest";
            var isSend = PatternLists.HttpSendMethods.Contains(invocation.MethodName, StringComparer.Ordinal);
            if (!isCreate && !isSend)
            {
                continue;
            }

            foreach (var argument in invocation.Arguments)
            {
                if (argument.LiteralValue is not { } value
                    || !EndpointClassifier.TryParse(value, out var endpoint)
                    || endpoint is null
                    || EndpointClassifier.IsLoopbackHost(endpoint.Host))
                {
                    continue;
                }

                if (boundary.IsApprovedCSharpScope(invocation.Namespace, invocation.ContainingType, file.RelativePath))
                {
                    continue;
                }

                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Net02, file, invocation.Line, invocation.Column,
                    $"{invocation.MethodName}(\"{value}\")",
                    "Outbound call to a non-loopback target outside approved boundary scopes."));
            }
        }
    }

    private static void EvaluateIdentityDimensions(SourceFile file, CSharpFileFacts facts, List<GovernanceViolation> violations)
    {
        foreach (var invocation in facts.Invocations)
        {
            var isTelemetry = PatternLists.TelemetryMethodNames.Contains(invocation.MethodName, StringComparer.Ordinal)
                || (MetricMethods.Contains(invocation.MethodName, StringComparer.Ordinal)
                    && IdentifierTokens.TokenizeText(invocation.Receiver)
                        .Any(t => MetricReceivers.Contains(t, StringComparer.Ordinal)))
                || (invocation.MethodName is "SendAsync" or "SendCoreAsync" or "InvokeAsync"
                    && DetectionHelpers.ContainsAny(invocation.Receiver, ["hub", "client", "signalr"]));
            if (!isTelemetry)
            {
                continue;
            }

            var matched = FindIdentityDimension(invocation.ArgumentsText, invocation.Arguments);
            if (matched is not null)
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Id01, file, invocation.Line, invocation.Column,
                    $"{invocation.MethodName}{invocation.ArgumentsText}",
                    $"Identity dimension '{matched}' in event/metric/log payload violates the privacy invariant."));
            }
        }

        if (DetectionHelpers.IsPluginPath(file.RelativePath))
        {
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

    private static string? FindIdentityDimension(string argumentsText, IReadOnlyList<ArgumentFact> arguments)
    {
        // SpecialFolder.<member> expressions are directory locations, never identity
        // dimensions; neutralize them before payload tokenization (defense in depth).
        var tokens = IdentifierTokens.TokenizeText(SpecialFolderExpressionRegex.Replace(argumentsText, "SpecialFolder"));
        foreach (var pattern in PatternLists.IdentityDimensions)
        {
            if (IdentifierTokens.ContainsSequence(tokens, pattern))
            {
                return string.Concat(pattern);
            }
        }

        foreach (var argument in arguments)
        {
            if (argument.LiteralValue is not { } literal)
            {
                continue;
            }

            foreach (Match match in PlaceholderRegex.Matches(literal))
            {
                var placeholderTokens = IdentifierTokens.Tokenize(match.Groups[1].Value);
                foreach (var pattern in PatternLists.IdentityDimensions)
                {
                    if (IdentifierTokens.ContainsSequence(placeholderTokens, pattern))
                    {
                        return string.Concat(pattern);
                    }
                }
            }
        }

        return null;
    }

    private static string LastSegment(string typeName)
    {
        var index = typeName.LastIndexOf('.');
        var segment = index < 0 ? typeName : typeName[(index + 1)..];
        var generic = segment.IndexOf('<');
        return generic < 0 ? segment : segment[..generic];
    }

    /// <summary>
    /// True for accesses of the BCL path enum <c>Environment.SpecialFolder</c>
    /// (also <c>System.Environment.SpecialFolder</c> / imported <c>SpecialFolder</c>).
    /// These locate per-user app-data directories; they read no identity.
    /// </summary>
    private static bool IsSpecialFolderAccess(MemberAccessFact access) =>
        string.Equals(LastSegment(access.Receiver), "SpecialFolder", StringComparison.Ordinal);

    private static readonly Regex SpecialFolderExpressionRegex = new(
        @"\bSpecialFolder\s*\.\s*[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
}
