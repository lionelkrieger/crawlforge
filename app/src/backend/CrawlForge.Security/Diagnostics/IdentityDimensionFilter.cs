namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Enforces the privacy invariant (FS-CRAWL-001 §3 rows 1/6, §10.5): no user,
/// machine, or account dimension may appear in any local log event, metric, or
/// diagnostic surface. Identity-bearing property keys are dropped before an
/// event is written.
/// </summary>
/// <remarks>
/// Local technical dimensions remain permitted — for example <c>auditId</c>,
/// <c>phase</c>, <c>status</c>, and crawl-target <c>host</c> (FS-CRAWL-002 §3.7
/// explicitly allows host as a local technical dimension). Keys beginning with
/// <c>user</c>, <c>account</c>, <c>machine</c>, or <c>identity</c> are treated as
/// identity-bearing, as are well-known machine/account identifiers such as
/// <c>hostname</c>, <c>computername</c>, <c>sid</c>, <c>login</c>, and
/// <c>owner</c>. Filtering is conservative by design.
/// </remarks>
public static class IdentityDimensionFilter
{
    private static readonly string[] DeniedPrefixes = { "user", "account", "machine", "identity" };

    private static readonly string[] DeniedExact =
    {
        "hostname",
        "computername",
        "machinename",
        "sid",
        "login",
        "loginid",
        "owner",
        "ownerid",
        "osuser",
        "environmentusername",
        "userdomainname",
    };

    /// <summary>Returns true when a property key is an identity-bearing dimension.</summary>
    /// <param name="key">Property key to classify.</param>
    /// <returns>True when the key must be dropped from diagnostic surfaces.</returns>
    public static bool IsIdentityDimension(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string trimmed = key.Trim();
        foreach (string prefix in DeniedPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string denied in DeniedExact)
        {
            if (string.Equals(trimmed, denied, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a copy of the properties with every identity-bearing key removed.
    /// Null input yields an empty dictionary.
    /// </summary>
    /// <param name="properties">Caller-supplied event properties (may be null).</param>
    /// <returns>Sanitized properties safe for local diagnostic surfaces.</returns>
    public static IReadOnlyDictionary<string, object?> Sanitize(IReadOnlyDictionary<string, object?>? properties)
    {
        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (properties is null)
        {
            return sanitized;
        }

        foreach (KeyValuePair<string, object?> pair in properties)
        {
            if (!IsIdentityDimension(pair.Key))
            {
                sanitized[pair.Key] = pair.Value;
            }
        }

        return sanitized;
    }
}