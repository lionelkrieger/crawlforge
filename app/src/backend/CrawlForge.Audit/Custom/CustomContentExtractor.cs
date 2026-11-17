using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.XPath;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// Custom content extractor (FS-CRAWL-001 §7.9, criterion #24): user-defined
/// rules capture typed values from the stored parsed document with mandatory
/// bounds — per-page value caps (excess discarded and counted), surrogate-safe
/// length caps (truncations counted), and time-bounded evaluation. Conversion
/// failures become invalid markers; extraction errors NEVER fail the audit.
/// </summary>
/// <remarks>
/// <para>
/// Selector encoding (locked <c>CustomExtractionRule.Selector</c> string):
/// plain = CSS selector; <c>xpath:</c> prefix = XPath expression;
/// <c>regex:</c> prefix = pattern over raw markup (first capture group when
/// defined, else the whole match). Capture-target suffixes on selector rules:
/// <c>@attribute</c> captures a named attribute, <c>::markup</c> captures inner
/// markup; the default target is element text.
/// </para>
/// <para>
/// Pure and offline: evaluates only the stored context documents (post-render
/// DOM in rendering mode), so changed rules re-extract from stored bodies on
/// re-audit without network access.
/// </para>
/// </remarks>
public sealed class CustomContentExtractor : ICustomContentExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CustomExtractionOptions _options;

    /// <summary>Creates an extractor with optional local bounds configuration.</summary>
    /// <param name="options">Bounds and path scoping; null uses documented defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option bound is invalid.</exception>
    public CustomContentExtractor(CustomExtractionOptions? options = null)
    {
        _options = options ?? new CustomExtractionOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtractedValueDraft>> ExtractAsync(
        PageAuditContext context,
        IReadOnlyList<CustomExtractionRule> rules,
        CancellationToken cancellationToken = default)
    {
        CustomExtractionResult result = await ExtractWithDiagnosticsAsync(context, rules, cancellationToken).ConfigureAwait(false);
        return result.Values;
    }

    /// <summary>
    /// Additive diagnostics surface: extracted values plus local rule-level
    /// diagnostics (invalid/truncated/discarded/timeout/error). The interface
    /// method returns <see cref="CustomExtractionResult.Values"/>.
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="rules">User-defined extraction rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Values and diagnostics; never throws for rule failures.</returns>
    /// <exception cref="ArgumentNullException">Context or rules is null.</exception>
    public async Task<CustomExtractionResult> ExtractWithDiagnosticsAsync(
        PageAuditContext context,
        IReadOnlyList<CustomExtractionRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        cancellationToken.ThrowIfCancellationRequested();

        var values = new List<ExtractedValueDraft>();
        var diagnostics = new List<CustomDiagnostic>();

        DomDocument? effective = context.Configuration.RenderingMode != RenderMode.None && context.RenderedDocument is not null
            ? context.RenderedDocument
            : context.Document;
        string html = DomHtmlSerializer.Serialize(effective);
        string baseUrl = context.Fetch?.FinalUrl ?? context.Url.CanonicalUrl;

        IDocument? document = null;
        if (effective is not null)
        {
            IBrowsingContext browsing = BrowsingContext.New(Configuration.Default);
            document = await browsing.OpenAsync(request => request.Content(html), cancellationToken).ConfigureAwait(false);
        }

        foreach (CustomExtractionRule rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ExtractRule(context, rule, document, html, baseUrl, values, diagnostics, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Timeout,
                    $"Rule exceeded the {_options.RuleTimeout.TotalMilliseconds:0} ms evaluation bound and was aborted."));
            }
            catch (RegexMatchTimeoutException)
            {
                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Timeout,
                    $"Pattern exceeded the {_options.RuleTimeout.TotalMilliseconds:0} ms evaluation bound and was aborted."));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Error,
                    "Rule evaluation failed and was isolated: " + exception.Message));
            }
        }

        return new CustomExtractionResult(values, diagnostics);
    }

    private void ExtractRule(
        PageAuditContext context,
        CustomExtractionRule rule,
        IDocument? document,
        string html,
        string baseUrl,
        List<ExtractedValueDraft> values,
        List<CustomDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (_options.PathScopes.TryGetValue(rule.Name, out PathScope? scope) && !scope.Matches(context.Url.Path))
        {
            diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.OutOfScope, "Path scope excluded this page."));
            return;
        }

        using var ruleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ruleCts.CancelAfter(_options.RuleTimeout);
        CancellationToken token = ruleCts.Token;

        List<string> captures = CaptureValues(rule, document, html, token);
        int cap = rule.AllowMultiple ? _options.MaxValuesPerPage : 1;
        int discarded = Math.Max(0, captures.Count - cap);
        if (discarded > 0)
        {
            diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Discarded,
                "Excess matches discarded at the per-page value cap.", discarded));
        }

        if (captures.Count == 0)
        {
            if (rule.Required)
            {
                values.Add(new ExtractedValueDraft(rule.Name, 0, null, rule.ValueType, IsInvalid: true, IsTruncated: false));
                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Missing, "Required rule captured no value."));
            }

            return;
        }

        int ordinal = 0;
        foreach (string capture in captures.Take(cap))
        {
            token.ThrowIfCancellationRequested();
            values.Add(CoerceValue(rule, capture, baseUrl, diagnostics, ref ordinal));
        }
    }

    private List<string> CaptureValues(CustomExtractionRule rule, IDocument? document, string html, CancellationToken token)
    {
        var captures = new List<string>();
        string selector = rule.Selector.Trim();

        if (selector.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            string pattern = selector.Substring("regex:".Length);
            var regex = new Regex(pattern, RegexOptions.None, _options.RuleTimeout);
            foreach (Match match in regex.Matches(html))
            {
                token.ThrowIfCancellationRequested();
                captures.Add(match.Groups.Count > 1 ? match.Groups[1].Value : match.Value);
            }

            return captures;
        }

        if (document is null)
        {
            return captures;
        }

        if (selector.StartsWith("xpath:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (INode node in document.DocumentElement.SelectNodes(selector.Substring("xpath:".Length)))
            {
                token.ThrowIfCancellationRequested();
                captures.Add(node.TextContent);
            }

            return captures;
        }

        (string css, string captureTarget) = SplitCaptureTarget(selector);
        foreach (IElement element in document.QuerySelectorAll(css))
        {
            token.ThrowIfCancellationRequested();
            captures.Add(captureTarget switch
            {
                "markup" => element.InnerHtml,
                string attribute when attribute.StartsWith('@') => element.GetAttribute(attribute.Substring(1)) ?? string.Empty,
                _ => element.TextContent,
            });
        }

        return captures;
    }

    private static (string Selector, string CaptureTarget) SplitCaptureTarget(string selector)
    {
        int markupIndex = selector.LastIndexOf("::markup", StringComparison.OrdinalIgnoreCase);
        if (markupIndex >= 0)
        {
            return (selector.Substring(0, markupIndex).Trim(), "markup");
        }

        int attributeIndex = selector.LastIndexOf(" @", StringComparison.Ordinal);
        if (attributeIndex >= 0)
        {
            return (selector.Substring(0, attributeIndex).Trim(), selector.Substring(attributeIndex + 1).Trim());
        }

        return (selector, "text");
    }

    private ExtractedValueDraft CoerceValue(
        CustomExtractionRule rule,
        string capture,
        string baseUrl,
        List<CustomDiagnostic> diagnostics,
        ref int ordinal)
    {
        int currentOrdinal = ordinal;
        ordinal++;

        switch (rule.ValueType)
        {
            case ExtractedValueType.Number:
                if (decimal.TryParse(capture.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
                {
                    return new ExtractedValueDraft(rule.Name, currentOrdinal, JsonSerializer.Serialize(number, JsonOptions), rule.ValueType, false, false);
                }

                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Invalid, "Value failed numeric coercion."));
                return new ExtractedValueDraft(rule.Name, currentOrdinal, null, rule.ValueType, true, false);

            case ExtractedValueType.Boolean:
                if (bool.TryParse(capture.Trim(), out bool flag))
                {
                    return new ExtractedValueDraft(rule.Name, currentOrdinal, JsonSerializer.Serialize(flag, JsonOptions), rule.ValueType, false, false);
                }

                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Invalid, "Value failed boolean coercion."));
                return new ExtractedValueDraft(rule.Name, currentOrdinal, null, rule.ValueType, true, false);

            case ExtractedValueType.DateTime:
                if (DateTimeOffset.TryParse(capture.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset timestamp))
                {
                    return new ExtractedValueDraft(rule.Name, currentOrdinal, JsonSerializer.Serialize(timestamp, JsonOptions), rule.ValueType, false, false);
                }

                diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Invalid, "Value failed date/time coercion."));
                return new ExtractedValueDraft(rule.Name, currentOrdinal, null, rule.ValueType, true, false);

            default:
                return TruncateText(rule, capture, currentOrdinal, diagnostics);
        }
    }

    private ExtractedValueDraft TruncateText(CustomExtractionRule rule, string value, int ordinal, List<CustomDiagnostic> diagnostics)
    {
        int maxLength = rule.MaxLength ?? _options.DefaultValueMaxLength;
        string truncated = CustomTextUtility.TruncateSafe(value, maxLength);
        bool wasTruncated = truncated.Length < value.Length;
        if (wasTruncated)
        {
            diagnostics.Add(new CustomDiagnostic(rule.Name, CustomDiagnosticKinds.Truncated, "Value truncated at the length cap."));
        }

        return new ExtractedValueDraft(rule.Name, ordinal, JsonSerializer.Serialize(truncated, JsonOptions), rule.ValueType, false, wasTruncated);
    }
}