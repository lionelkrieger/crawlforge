using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// Regular-expression capability family (FS-CRAWL-001 §2.4: regular-expression
/// predicates in queries). Registers <c>regexp</c> (the function SQLite invokes for
/// the <c>REGEXP</c> operator), <c>regexp_replace</c>, and <c>regexp_extract</c>.
/// Patterns use invariant-culture .NET semantics with a bounded match timeout.
/// </summary>
internal static class RegexFunctions
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Registers the regex family on an open connection.</summary>
    /// <param name="connection">The connection to extend.</param>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // SQLite evaluates "input REGEXP pattern" as regexp(pattern, input).
        connection.CreateFunction(
            "regexp",
            (string? pattern, string? input) =>
            {
                if (pattern is null || input is null)
                {
                    return null;
                }

                return (bool?)Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant, MatchTimeout);
            },
            isDeterministic: true);

        connection.CreateFunction(
            "regexp_replace",
            (string? input, string? pattern, string? replacement) =>
            {
                if (input is null || pattern is null || replacement is null)
                {
                    return null;
                }

                return Regex.Replace(input, pattern, replacement, RegexOptions.CultureInvariant, MatchTimeout);
            },
            isDeterministic: true);

        connection.CreateFunction(
            "regexp_extract",
            (string? input, string? pattern, long group) =>
            {
                if (input is null || pattern is null)
                {
                    return null;
                }

                if (group < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(group), group, "The capture group index must not be negative.");
                }

                var match = Regex.Match(input, pattern, RegexOptions.CultureInvariant, MatchTimeout);
                if (!match.Success || group >= match.Groups.Count)
                {
                    return null;
                }

                return match.Groups[(int)group].Value;
            },
            isDeterministic: true);
    }
}
