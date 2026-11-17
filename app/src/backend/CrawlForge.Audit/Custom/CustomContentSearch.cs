using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// Custom content search (FS-CRAWL-001 §7.16, criterion #30): user-defined
/// items combining plain terms or regular expressions, with negative terms
/// that must be absent for a match. Records per-URL match counts and bounded
/// matched snippets against scoped visible text (content-area-scoped when
/// configured) or raw markup. Mandatory bounds: regex execution timeout and a
/// matches-per-page cap; failures are isolated and never fail the audit.
/// </summary>
/// <remarks>
/// Pure and offline: evaluates only stored context artifacts, so the feature
/// re-runs on re-audit from stored bodies without network access. Negative
/// terms, targeting, and path scoping arrive through constructor-injected
/// <see cref="CustomSearchOptions"/> (the locked item contract carries
/// key/pattern/regex only).
/// </remarks>
public sealed class CustomContentSearch : ICustomContentSearch
{
    private readonly CustomSearchOptions _options;

    /// <summary>Creates a search surface with optional local configuration.</summary>
    /// <param name="options">Negative terms, bounds, targeting; null uses documented defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option bound is invalid.</exception>
    public CustomContentSearch(CustomSearchOptions? options = null)
    {
        _options = options ?? new CustomSearchOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentSearchMatch>> SearchAsync(
        PageAuditContext context,
        IReadOnlyList<CustomSearchItem> items,
        CancellationToken cancellationToken = default) =>
        SearchCoreAsync(context, items, cancellationToken);

    /// <summary>
    /// Additive diagnostics surface: per-item matches plus local item-level
    /// diagnostics (error/timeout/out-of-scope/discarded).
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="items">User-defined search items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matches and diagnostics; never throws for item failures.</returns>
    /// <exception cref="ArgumentNullException">Context or items is null.</exception>
    public async Task<CustomSearchResult> SearchWithDiagnosticsAsync(
        PageAuditContext context,
        IReadOnlyList<CustomSearchItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = new List<ContentSearchMatch>();
        var diagnostics = new List<CustomDiagnostic>();

        DomDocument? effective = context.Configuration.RenderingMode != RenderMode.None && context.RenderedDocument is not null
            ? context.RenderedDocument
            : context.Document;
        string visibleText = context.ScopedText?.Text ?? CustomTextUtility.VisibleText(effective);
        string markup = DomHtmlSerializer.Serialize(effective);

        foreach (CustomSearchItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                matches.Add(SearchItem(context, item, visibleText, markup, diagnostics, cancellationToken));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(new CustomDiagnostic(item.Key, CustomDiagnosticKinds.Timeout, "Item evaluation exceeded its time bound."));
                matches.Add(new ContentSearchMatch(item.Key, 0, Array.Empty<string>()));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                diagnostics.Add(new CustomDiagnostic(item.Key, CustomDiagnosticKinds.Error,
                    "Item evaluation failed and was isolated: " + exception.Message));
                matches.Add(new ContentSearchMatch(item.Key, 0, Array.Empty<string>()));
            }
        }

        return new CustomSearchResult(matches, diagnostics);
    }

    private async Task<IReadOnlyList<ContentSearchMatch>> SearchCoreAsync(
        PageAuditContext context,
        IReadOnlyList<CustomSearchItem> items,
        CancellationToken cancellationToken)
    {
        CustomSearchResult result = await SearchWithDiagnosticsAsync(context, items, cancellationToken).ConfigureAwait(false);
        return result.Matches;
    }

    private ContentSearchMatch SearchItem(
        PageAuditContext context,
        CustomSearchItem item,
        string visibleText,
        string markup,
        List<CustomDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (_options.PathScopes.TryGetValue(item.Key, out PathScope? scope) && !scope.Matches(context.Url.Path))
        {
            diagnostics.Add(new CustomDiagnostic(item.Key, CustomDiagnosticKinds.OutOfScope, "Path scope excluded this page."));
            return new ContentSearchMatch(item.Key, 0, Array.Empty<string>());
        }

        bool rawMarkup = _options.RawMarkupTargets.TryGetValue(item.Key, out bool target) && target;
        string text = rawMarkup ? markup : visibleText;

        if (_options.NegativeTerms.TryGetValue(item.Key, out IReadOnlyList<string>? negatives))
        {
            foreach (string negative in negatives)
            {
                if (negative.Length > 0 && text.Contains(negative, StringComparison.OrdinalIgnoreCase))
                {
                    // A negative term is present: the item must not match here.
                    return new ContentSearchMatch(item.Key, 0, Array.Empty<string>());
                }
            }
        }

        if (item.Pattern.Length == 0)
        {
            diagnostics.Add(new CustomDiagnostic(item.Key, CustomDiagnosticKinds.Error, "Empty search pattern."));
            return new ContentSearchMatch(item.Key, 0, Array.Empty<string>());
        }

        return item.IsRegex
            ? SearchRegex(item, text, diagnostics, cancellationToken)
            : SearchPlain(item, text, diagnostics, cancellationToken);
    }

    private ContentSearchMatch SearchPlain(
        CustomSearchItem item,
        string text,
        List<CustomDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var snippets = new List<string>();
        int count = 0;
        int index = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int found = text.IndexOf(item.Pattern, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            count++;
            if (snippets.Count < _options.MaxSnippetsPerPage)
            {
                snippets.Add(CustomTextUtility.BuildSnippet(text, found, item.Pattern.Length, _options.SnippetContextChars));
            }

            index = found + item.Pattern.Length;
        }

        return ClampCount(item.Key, count, snippets, diagnostics);
    }

    private ContentSearchMatch SearchRegex(
        CustomSearchItem item,
        string text,
        List<CustomDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = new Regex(item.Pattern, RegexOptions.None, _options.RegexTimeout);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new CustomDiagnostic(item.Key, CustomDiagnosticKinds.Error, "Invalid regular expression: " + exception.Message));
            return new ContentSearchMatch(item.Key, 0, Array.Empty<string>());
        }

        var snippets = new List<string>();
        int count = 0;
        try
        {
            foreach (Match match in regex.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                if (snippets.Count < _options.MaxSnippetsPerPage)
                {
                    snippets.Add(CustomTextUtility.BuildSnippet(text, match.Index, match.Length, _options.SnippetContextChars));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            diagnostics.Add(new CustomDiagnostic(item.Key, CustomDiagnosticKinds.Timeout,
                $"Regular expression exceeded the {_options.RegexTimeout.TotalMilliseconds:0} ms execution timeout."));
            return new ContentSearchMatch(item.Key, 0, Array.Empty<string>());
        }

        return ClampCount(item.Key, count, snippets, diagnostics);
    }

    private ContentSearchMatch ClampCount(string itemKey, int count, List<string> snippets, List<CustomDiagnostic> diagnostics)
    {
        if (count > _options.MaxMatchesPerPage)
        {
            diagnostics.Add(new CustomDiagnostic(itemKey, CustomDiagnosticKinds.Discarded,
                "Matches beyond the per-page cap were counted but not stored.", count - _options.MaxMatchesPerPage));
            count = _options.MaxMatchesPerPage;
        }

        return new ContentSearchMatch(itemKey, count, snippets);
    }
}