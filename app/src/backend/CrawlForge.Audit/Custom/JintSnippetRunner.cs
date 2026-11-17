using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// Custom JavaScript snippet runner (FS-CRAWL-001 §7.17, criterion #31):
/// executes local user-authored snippets in an isolated Jint interpreter —
/// pure managed, no CLR type access, no network, no backend surface beyond
/// the scoped wrapper API (<c>cf.submit</c>, <c>cf.error</c>,
/// <c>cf.loadScript</c> from the local library, read-only <c>cf.url</c>,
/// <c>cf.text</c>, <c>cf.html</c>). Execution is time-bounded and
/// statement-bounded; a snippet failure on one page never fails the audit or
/// other pages. Typed results become dynamic columns with bounded rows.
/// </summary>
/// <remarks>
/// Requires rendering mode; evaluates the stored post-render DOM (falling
/// back to the stored response DOM), so re-audit re-runs snippets from stored
/// artifacts without network access. Snippets and loaded scripts are local
/// user artifacts only (exclusion E1) — no remote loading path exists.
/// </remarks>
public sealed class JintSnippetRunner : IJsSnippetRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly JsSnippetOptions _options;

    /// <summary>Creates a runner with optional local bounds configuration.</summary>
    /// <param name="options">Timeouts, row ceiling, local script library; null uses documented defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option bound is invalid.</exception>
    public JintSnippetRunner(JsSnippetOptions? options = null)
    {
        _options = options ?? new JsSnippetOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SnippetResultDraft>> RunAsync(
        PageAuditContext context,
        IReadOnlyList<JsSnippetDefinition> snippets,
        CancellationToken cancellationToken = default)
    {
        JsSnippetRunResult result = await RunWithDiagnosticsAsync(context, snippets, cancellationToken).ConfigureAwait(false);
        return result.Results;
    }

    /// <summary>
    /// Additive diagnostics surface: snippet result drafts plus local
    /// snippet-level diagnostics (error/timeout/out-of-scope/discarded).
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="snippets">User-defined snippets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results and diagnostics; never throws for snippet failures.</returns>
    /// <exception cref="ArgumentNullException">Context or snippets is null.</exception>
    public Task<JsSnippetRunResult> RunWithDiagnosticsAsync(
        PageAuditContext context,
        IReadOnlyList<JsSnippetDefinition> snippets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snippets);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<SnippetResultDraft>();
        var diagnostics = new List<CustomDiagnostic>();

        DomDocument? effective = context.RenderedDocument ?? context.Document;
        string html = DomHtmlSerializer.Serialize(effective);
        string text = context.ScopedText?.Text ?? CustomTextUtility.VisibleText(effective);
        string url = context.Fetch?.FinalUrl ?? context.Url.CanonicalUrl;

        foreach (JsSnippetDefinition snippet in snippets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(RunSnippet(context, snippet, url, text, html, diagnostics, cancellationToken));
        }

        return Task.FromResult(new JsSnippetRunResult(results, diagnostics));
    }

    private SnippetResultDraft RunSnippet(
        PageAuditContext context,
        JsSnippetDefinition snippet,
        string url,
        string text,
        string html,
        List<CustomDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (context.Configuration.RenderingMode == RenderMode.None)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.OutOfScope,
                "JavaScript snippets require rendering mode."));
            return Empty(snippet.Key);
        }

        if (_options.PathScopes.TryGetValue(snippet.Key, out PathScope? scope) && !scope.Matches(context.Url.Path))
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.OutOfScope, "Path scope excluded this page."));
            return Empty(snippet.Key);
        }

        TimeSpan timeout = _options.TimeoutOverrides.TryGetValue(snippet.Key, out TimeSpan overridden)
            ? overridden
            : _options.DefaultTimeout;

        var host = new SnippetHostApi(url, text, html);
        try
        {
            using var engine = new Engine(options =>
            {
                options.TimeoutInterval(timeout);
                options.MaxStatements(_options.MaxStatements);
                options.LimitMemory(8_000_000);
                options.LimitRecursion(100);
                // CLR interop stays disabled: the interpreter is fully sandboxed.
            });

            engine.SetValue("cf", host);
            host.AttachEngine(engine, _options.LocalScriptLibrary, diagnostics, snippet.Key);

            JsValue completion = engine.Evaluate(snippet.JavaScript);
            cancellationToken.ThrowIfCancellationRequested();

            if (host.ColumnCount == 0 && !completion.IsUndefined() && !completion.IsNull())
            {
                host.Submit("result", completion.ToObject());
            }

            return BuildDraft(snippet, host, diagnostics);
        }
        catch (TimeoutException)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Timeout,
                $"Snippet exceeded the {timeout.TotalMilliseconds:0} ms execution timeout and was aborted."));
            return Empty(snippet.Key);
        }
        catch (StatementsCountOverflowException)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Timeout,
                "Snippet exceeded the statement bound and was aborted."));
            return Empty(snippet.Key);
        }
        catch (MemoryLimitExceededException)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Error,
                "Snippet exceeded the memory bound and was aborted."));
            return Empty(snippet.Key);
        }
        catch (ExecutionCanceledException)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Timeout, "Snippet execution was canceled."));
            return Empty(snippet.Key);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Error,
                "Snippet failed and was isolated: " + exception.Message));
            return Empty(snippet.Key);
        }
    }

    private SnippetResultDraft BuildDraft(JsSnippetDefinition snippet, SnippetHostApi host, List<CustomDiagnostic> diagnostics)
    {
        int rowBound = Math.Min(Math.Max(1, snippet.MaxRows), _options.MaxRowsCeiling);
        int rowCount = host.RowCount;
        bool truncated = rowCount > rowBound;
        if (truncated)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Discarded,
                "Rows beyond the snippet bound were discarded.", rowCount - rowBound));
        }

        var columns = new List<object>();
        foreach (var (name, values) in host.Columns)
        {
            columns.Add(new { key = name, label = name, valueType = InferColumnType(values) });
        }

        var rows = new List<Dictionary<string, object?>>();
        for (int i = 0; i < Math.Min(rowCount, rowBound); i++)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, values) in host.Columns)
            {
                row[name] = i < values.Count ? values[i] : null;
            }

            rows.Add(row);
        }

        foreach (string error in host.Errors)
        {
            diagnostics.Add(new CustomDiagnostic(snippet.Key, CustomDiagnosticKinds.Error, "Snippet reported: " + error));
        }

        return new SnippetResultDraft(
            snippet.Key,
            JsonSerializer.Serialize(columns, JsonOptions),
            JsonSerializer.Serialize(rows, JsonOptions),
            truncated);
    }

    private static string InferColumnType(IReadOnlyList<object?> values)
    {
        foreach (object? value in values)
        {
            switch (value)
            {
                case double or long or int or decimal:
                    return "number";
                case bool:
                    return "boolean";
                case string:
                    return "string";
            }
        }

        return "string";
    }

    private static SnippetResultDraft Empty(string key) => new(key, "[]", "[]", false);

    /// <summary>
    /// The scoped wrapper host API: the only surface a snippet can reach.
    /// Column-major submission — each <c>cf.submit(column, value)</c> appends
    /// to a column; rows align by index.
    /// </summary>
    private sealed class SnippetHostApi
    {
        private readonly Dictionary<string, List<object?>> _columns = new(StringComparer.Ordinal);
        private readonly List<string> _errors = new();
        private Engine? _engine;
        private IReadOnlyDictionary<string, string> _library = new Dictionary<string, string>();
        private List<CustomDiagnostic>? _diagnostics;
        private string _snippetKey = string.Empty;

        public SnippetHostApi(string url, string text, string html)
        {
            Url = url;
            Text = text;
            Html = html;
        }

        /// <summary>Page URL (read-only).</summary>
        public string Url { get; }

        /// <summary>Scoped visible text (read-only).</summary>
        public string Text { get; }

        /// <summary>Serialized DOM markup (read-only).</summary>
        public string Html { get; }

        public IEnumerable<KeyValuePair<string, List<object?>>> Columns => _columns;

        public int ColumnCount => _columns.Count;

        public int RowCount => _columns.Count == 0 ? 0 : _columns.Values.Max(v => v.Count);

        public IReadOnlyList<string> Errors => _errors;

        public void AttachEngine(Engine engine, IReadOnlyDictionary<string, string> library, List<CustomDiagnostic> diagnostics, string snippetKey)
        {
            _engine = engine;
            _library = library;
            _diagnostics = diagnostics;
            _snippetKey = snippetKey;
        }

        /// <summary>Submits a typed data value into a named result column.</summary>
        /// <param name="column">Column name.</param>
        /// <param name="value">Typed value (string, number, boolean, or null).</param>
        public void Submit(string column, object? value)
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                _errors.Add("submit() requires a non-empty column name.");
                return;
            }

            if (!_columns.TryGetValue(column, out List<object?>? values))
            {
                values = new List<object?>();
                _columns[column] = values;
            }

            values.Add(value);
        }

        /// <summary>Reports a snippet error (isolated; never fails the audit).</summary>
        /// <param name="message">Error detail.</param>
        public void Error(string message) => _errors.Add(message ?? string.Empty);

        /// <summary>
        /// Loads a script from the LOCAL user library by name (exclusion E1:
        /// no remote loading). Unknown names report an error.
        /// </summary>
        /// <param name="name">Local library script name.</param>
        public void LoadScript(string name)
        {
            if (_engine is null)
            {
                return;
            }

            if (name is not null && _library.TryGetValue(name, out string? source))
            {
                _engine.Execute(source);
                return;
            }

            _errors.Add($"loadScript(): unknown local script '{name}'.");
            _diagnostics?.Add(new CustomDiagnostic(_snippetKey, CustomDiagnosticKinds.Error, $"Unknown local script '{name}'."));
        }
    }
}