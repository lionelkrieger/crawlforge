using System.Diagnostics;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.XPath;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Scoping;

/// <summary>Per-selector match count for scope diagnostics and preview.</summary>
/// <param name="Expression">Selector expression.</param>
/// <param name="Language">Detected selector language.</param>
/// <param name="MatchCount">Number of matched elements.</param>
public sealed record SelectorMatchCount(string Expression, SelectorLanguage Language, int MatchCount);

/// <summary>
/// Full scope-resolution outcome: the locked scoped visible text plus the
/// FS-CRAWL-002 §4.4 content_scope diagnostics (include/exclude match counts,
/// full/scoped/removed word counts, per-selector counts).
/// </summary>
/// <param name="VisibleText">Locked scoped visible text surface.</param>
/// <param name="IncludeMatchCount">Total include-selector matches (raw union).</param>
/// <param name="ExcludeMatchCount">Total exclude-selector matches.</param>
/// <param name="FullWordCount">Visible word count without scoping.</param>
/// <param name="ScopedWordCount">Visible word count after scoping.</param>
/// <param name="RemovedWordCount">Words removed by scoping.</param>
/// <param name="PerSelectorCounts">Per-entry match counts in list order.</param>
public sealed record ScopeResolution(
    ScopedVisibleText VisibleText,
    int IncludeMatchCount,
    int ExcludeMatchCount,
    int FullWordCount,
    int ScopedWordCount,
    int RemovedWordCount,
    IReadOnlyList<SelectorMatchCount> PerSelectorCounts);

/// <summary>
/// Resolves the Scoped Visible Text Tree (FS-CRAWL-002 §4.3) from the contract
/// DOM. Deterministic evaluation semantics (§4.2): empty include list → whole
/// body; non-empty include list → union of matched subtrees in document order;
/// excludes applied after inclusion with exclude-wins; nested matches
/// deduplicated (ancestors subsume descendants); document order preserved.
/// With no scope configured the full visible text is returned, reproducing the
/// baseline exactly (criterion #37, precedence rule 4). Selector evaluation is
/// time-bounded per page and failures are isolated as diagnostics — the audit
/// never fails on a scope error.
/// </summary>
public sealed class ContentScopeResolver : IContentScopeResolver
{
    private static readonly HashSet<string> NonVisibleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "title", "meta", "link", "base",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "body", "div", "p", "section", "article", "header", "footer", "nav", "aside",
        "main", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "blockquote", "pre", "figure",
        "figcaption", "form", "fieldset", "address", "details", "summary", "hgroup", "br", "hr",
    };

    private readonly TimeSpan _evaluationBudget;

    /// <summary>Creates a resolver with the default 2-second per-page evaluation budget.</summary>
    public ContentScopeResolver()
        : this(TimeSpan.FromSeconds(2))
    {
    }

    /// <summary>Creates a resolver with a custom evaluation budget.</summary>
    /// <param name="evaluationBudget">Per-page selector-evaluation time bound.</param>
    public ContentScopeResolver(TimeSpan evaluationBudget)
    {
        _evaluationBudget = evaluationBudget;
    }

    /// <inheritdoc />
    public ScopedVisibleText Resolve(DomDocument document, ContentScopeDefinition? scope) =>
        ResolveDetailed(document, scope).VisibleText;

    /// <summary>Resolves scoped visible text with full §4.4 diagnostics.</summary>
    /// <param name="document">Parsed contract document.</param>
    /// <param name="scope">Include/exclude scope; null means baseline full text.</param>
    /// <returns>The resolution with counts and per-selector diagnostics.</returns>
    public ScopeResolution ResolveDetailed(DomDocument document, ContentScopeDefinition? scope)
    {
        var scratch = DomRehydrator.Rehydrate(document, out var nodes);
        var parents = BuildParentMap(document.Root);
        var bodyIndex = FindBodyIndex(nodes);

        var fullText = ExtractText(nodes, parents, excluded: null, roots: [bodyIndex]);
        var fullWordCount = CountWords(fullText);

        if (scope is null || (scope.IncludeSelectors.Count == 0 && scope.ExcludeSelectors.Count == 0))
        {
            var baseline = new ScopedVisibleText(
                fullText,
                fullWordCount,
                ["scope: none (baseline full visible text)"]);
            return new ScopeResolution(baseline, 0, 0, fullWordCount, fullWordCount, 0, []);
        }

        var stopwatch = Stopwatch.StartNew();
        var perSelector = new List<SelectorMatchCount>();
        var diagnostics = new List<string>();

        var includeSet = new HashSet<int>();
        var includeMatchCount = EvaluateList(scratch, scope.IncludeSelectors, "include", includeSet, perSelector, diagnostics, stopwatch);

        var excludeSet = new HashSet<int>();
        var excludeMatchCount = EvaluateList(scratch, scope.ExcludeSelectors, "exclude", excludeSet, perSelector, diagnostics, stopwatch);

        // Exclude wins: flag every node inside an excluded subtree.
        var excluded = new bool[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            excluded[i] = excludeSet.Contains(i) || (parents[i] >= 0 && excluded[parents[i]]);
        }

        // Candidate region: include union (nested-deduplicated) or the whole body.
        int[] roots;
        if (scope.IncludeSelectors.Count == 0)
        {
            roots = [bodyIndex];
        }
        else
        {
            var kept = includeSet.Where(i => !HasAncestorIn(i, parents, includeSet)).OrderBy(i => i).ToArray();
            roots = kept.Where(i => !excluded[i]).ToArray();
        }

        var scopedText = ExtractText(nodes, parents, excluded, roots);
        var scopedWordCount = CountWords(scopedText);
        var removedWordCount = Math.Max(0, fullWordCount - scopedWordCount);

        diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"include-matches: {includeMatchCount}"));
        diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"exclude-matches: {excludeMatchCount}"));
        diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"full-word-count: {fullWordCount}"));
        diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"scoped-word-count: {scopedWordCount}"));
        diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"removed-word-count: {removedWordCount}"));

        var visible = new ScopedVisibleText(scopedText, scopedWordCount, diagnostics);
        return new ScopeResolution(
            visible,
            includeMatchCount,
            excludeMatchCount,
            fullWordCount,
            scopedWordCount,
            removedWordCount,
            perSelector);
    }

    private int EvaluateList(
        IHtmlDocument scratch,
        IReadOnlyList<string> selectors,
        string listName,
        HashSet<int> target,
        List<SelectorMatchCount> perSelector,
        List<string> diagnostics,
        Stopwatch stopwatch)
    {
        var total = 0;
        for (var i = 0; i < selectors.Count; i++)
        {
            var expression = selectors[i];
            var language = SelectorLanguageDetector.Detect(expression);

            if (stopwatch.Elapsed > _evaluationBudget)
            {
                diagnostics.Add($"evaluation-budget-exceeded: {listName}[{i}] and later entries skipped");
                break;
            }

            try
            {
                var matches = Evaluate(scratch, expression, language);
                total += matches.Count;
                foreach (var index in matches)
                {
                    target.Add(index);
                }

                perSelector.Add(new SelectorMatchCount(expression, language, matches.Count));
            }
            catch (Exception ex)
            {
                // Isolated rule-level diagnostic; never fails the audit (§4.2).
                diagnostics.Add($"selector-failure {listName}[{i}] '{expression}': {ex.Message}");
                perSelector.Add(new SelectorMatchCount(expression, language, 0));
            }
        }

        return total;
    }

    private static List<int> Evaluate(IHtmlDocument scratch, string expression, SelectorLanguage language)
    {
        var matches = new List<int>();
        if (language == SelectorLanguage.XPath)
        {
            foreach (var node in scratch.DocumentElement!.SelectNodes(expression, ignoreNamespaces: true))
            {
                if (node is IElement element)
                {
                    var index = DomRehydrator.IndexOf(element);
                    if (index >= 0)
                    {
                        matches.Add(index);
                    }
                }
            }
        }
        else
        {
            foreach (var element in scratch.QuerySelectorAll(expression))
            {
                var index = DomRehydrator.IndexOf(element);
                if (index >= 0)
                {
                    matches.Add(index);
                }
            }
        }

        return matches;
    }

    private static int[] BuildParentMap(DomNode root)
    {
        var parents = new List<int>();
        var stack = new Stack<(DomNode Node, int Parent)>();
        stack.Push((root, -1));
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            var index = parents.Count;
            parents.Add(parent);
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((node.Children[i], index));
            }
        }

        return parents.ToArray();
    }

    private static bool HasAncestorIn(int index, int[] parents, HashSet<int> set)
    {
        var current = parents[index];
        while (current >= 0)
        {
            if (set.Contains(current))
            {
                return true;
            }

            current = parents[current];
        }

        return false;
    }

    private static int FindBodyIndex(IReadOnlyList<DomNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].TagName, "body", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static string ExtractText(IReadOnlyList<DomNode> nodes, int[] parents, bool[]? excluded, int[] roots)
    {
        // Precompute child adjacency once (pre-order layout): linear-time traversal.
        var children = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            children[i] = [];
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (parents[i] >= 0)
            {
                children[parents[i]].Add(i);
            }
        }

        var builder = new StringBuilder();
        foreach (var root in roots)
        {
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                if (excluded is not null && excluded[index])
                {
                    continue;
                }

                var node = nodes[index];
                if (!IsVisible(node))
                {
                    continue;
                }

                var isBlock = BlockTags.Contains(node.TagName);
                if (isBlock)
                {
                    builder.Append(' ');
                }

                if (!string.IsNullOrEmpty(node.TextContent))
                {
                    builder.Append(node.TextContent);
                    if (isBlock)
                    {
                        builder.Append(' ');
                    }
                }

                var childIndices = children[index];
                for (var i = childIndices.Count - 1; i >= 0; i--)
                {
                    stack.Push(childIndices[i]);
                }
            }
        }

        return NormalizeWhitespace(builder.ToString());
    }

    private static bool IsVisible(DomNode node)
    {
        if (NonVisibleTags.Contains(node.TagName))
        {
            return false;
        }

        if (node.Attributes.ContainsKey("hidden"))
        {
            return false;
        }

        if (node.Attributes.TryGetValue("aria-hidden", out var ariaHidden) &&
            string.Equals(ariaHidden.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (node.Attributes.TryGetValue("style", out var style))
        {
            var compact = style.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (compact.Contains("display:none", StringComparison.Ordinal) ||
                compact.Contains("visibility:hidden", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static int CountWords(string text) =>
        text.Length == 0 ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
