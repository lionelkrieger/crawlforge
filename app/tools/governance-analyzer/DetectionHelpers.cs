namespace CrawlForge.Tools.Governance;

/// <summary>Shared detection helpers for the C# and text rule runners.</summary>
internal static class DetectionHelpers
{
    public static GovernanceViolation Violation(
        string ruleId, SourceFile file, int line, int column, string evidence, string message)
    {
        var trimmed = evidence.Length <= 240 ? evidence : evidence[..240];
        return new GovernanceViolation(
            ruleId,
            RuleCatalog.Get(ruleId).Severity,
            file.RelativePath,
            line,
            column,
            trimmed.ReplaceLineEndings(" "),
            message);
    }

    /// <summary>Plugin surfaces are identified by path convention (plugin scanner UI, bridges, manifests).</summary>
    public static bool IsPluginPath(string relativePath) =>
        relativePath.Contains("plugin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Literal contexts in which endpoint rules treat a URL as transmission intent (behavior, not data).</summary>
    public static bool IsTransmissionContext(LiteralContext context) =>
        context is LiteralContext.SendArgument or LiteralContext.UriCreation or LiteralContext.EndpointAssignment;

    public static bool ContainsAny(string text, string[] values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
