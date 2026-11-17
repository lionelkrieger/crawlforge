using System.Text;
using System.Text.RegularExpressions;

namespace CrawlForge.Tools.Governance;

internal sealed record TextImportFact(string Module, bool IsDynamic, int Line, int Column);

internal sealed record TextCallFact(
    string Name, string Receiver, string ArgumentsText, bool IsConstructor, int Line, int Column);

internal sealed record TextDeclarationFact(string Kind, string Name, int Line, int Column);

internal sealed record TextLiteralFact(string Value, int Line, int Column);

internal sealed record TextObjectKeyFact(string Key, int Line, int Column);

internal sealed record TextHostConfigFact(string Key, string Value, int Line, int Column);

internal sealed class TextFileFacts
{
    public string Code { get; init; } = string.Empty;

    public List<TextImportFact> Imports { get; } = [];

    public List<TextCallFact> Calls { get; } = [];

    public List<TextDeclarationFact> Declarations { get; } = [];

    public List<TextLiteralFact> Literals { get; } = [];

    public List<TextObjectKeyFact> ObjectKeys { get; } = [];

    public List<TextHostConfigFact> HostConfigs { get; } = [];
}

/// <summary>
/// Comment-aware TS/JS/JSON scanner. Comments are blanked (positions preserved) BEFORE any
/// pattern matching, so guardrail vocabulary in comments never fires a rule. String literals
/// are tracked so comment markers inside strings are not mistaken for comments. This is a
/// lexical heuristic (documented per rule), not a full TypeScript parse.
/// </summary>
internal static class TextScanner
{
    private static readonly Regex ImportRegex = new(
        @"\bimport\s+(?:type\s+)?(?:[A-Za-z0-9_$,{}\s*]+\s+from\s+)?['""]([^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex RequireRegex = new(
        @"\brequire\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled);

    private static readonly Regex DynamicImportRegex = new(
        @"\bimport\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled);

    private static readonly Regex CallRegex = new(
        @"\b([A-Za-z_$][A-Za-z0-9_$]*(?:\s*\.\s*[A-Za-z_$][A-Za-z0-9_$]*)*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex DeclarationRegex = new(
        @"\b(const|let|var|function|class|interface|type|enum)\s+([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    private static readonly Regex LiteralRegex = new(
        @"(['""`])((?:\\.|(?!\1)[^\\])*)\1", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ObjectKeyRegex = new(
        @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:", RegexOptions.Compiled);

    private static readonly Regex HostConfigRegex = new(
        @"\b(host|hostname)\s*:\s*['""]([^'""]+)['""]", RegexOptions.Compiled);

    private static readonly HashSet<string> KeywordCallNames = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "function", "typeof", "new", "delete",
        "void", "in", "of", "do", "else", "try", "finally", "throw", "case", "await", "yield",
    };

    public static TextFileFacts Scan(string text)
    {
        var code = StripComments(text);
        var lineStarts = BuildLineStarts(code);
        var facts = new TextFileFacts { Code = code };

        foreach (Match match in ImportRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.Imports.Add(new TextImportFact(match.Groups[1].Value, false, line, col));
        }

        foreach (Match match in RequireRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.Imports.Add(new TextImportFact(match.Groups[1].Value, false, line, col));
        }

        foreach (Match match in DynamicImportRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.Imports.Add(new TextImportFact(match.Groups[1].Value, true, line, col));
        }

        foreach (Match match in CallRegex.Matches(code))
        {
            var fullName = match.Groups[1].Value;
            var segments = fullName.Split('.').Select(s => s.Trim()).ToList();
            var name = segments[^1];
            if (KeywordCallNames.Contains(name))
            {
                continue;
            }

            var receiver = segments.Count > 1 ? string.Join(".", segments.Take(segments.Count - 1)) : string.Empty;
            var openParen = match.Index + match.Length - 1;
            var arguments = ExtractBalancedArguments(code, openParen);
            var (line, col) = Position(lineStarts, match.Index);
            var isConstructor = IsPrecededByNew(code, match.Index);
            facts.Calls.Add(new TextCallFact(name, receiver, arguments, isConstructor, line, col));
        }

        foreach (Match match in DeclarationRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.Declarations.Add(new TextDeclarationFact(match.Groups[1].Value, match.Groups[2].Value, line, col));
        }

        foreach (Match match in LiteralRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.Literals.Add(new TextLiteralFact(match.Groups[2].Value, line, col));
        }

        foreach (Match match in ObjectKeyRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.ObjectKeys.Add(new TextObjectKeyFact(match.Groups[1].Value, line, col));
        }

        foreach (Match match in HostConfigRegex.Matches(code))
        {
            var (line, col) = Position(lineStarts, match.Index);
            facts.HostConfigs.Add(new TextHostConfigFact(match.Groups[1].Value, match.Groups[2].Value, line, col));
        }

        return facts;
    }

    /// <summary>Blank line/block comments while preserving every character position and newline.</summary>
    public static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`')
            {
                var quote = c;
                result.Append(c);
                i++;
                while (i < text.Length)
                {
                    var sc = text[i];
                    result.Append(sc);
                    if (sc == '\\' && i + 1 < text.Length)
                    {
                        result.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    i++;
                    if (sc == quote)
                    {
                        break;
                    }
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    result.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                result.Append("  ");
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    result.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < text.Length)
                {
                    result.Append("  ");
                    i += 2;
                }

                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static (int Line, int Column) Position(int[] lineStarts, int index)
    {
        var line = Array.BinarySearch(lineStarts, index);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return (line + 1, index - lineStarts[line] + 1);
    }

    private static string ExtractBalancedArguments(string code, int openParenIndex)
    {
        var depth = 0;
        var i = openParenIndex;
        var start = openParenIndex + 1;
        while (i < code.Length && i - openParenIndex < 8000)
        {
            var c = code[i];
            if (c is '\'' or '"' or '`')
            {
                var quote = c;
                i++;
                while (i < code.Length)
                {
                    if (code[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (code[i] == quote)
                    {
                        break;
                    }

                    i++;
                }

                i++;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    var length = i - start;
                    return length <= 0 ? string.Empty : code.Substring(start, Math.Min(length, 4000));
                }
            }

            i++;
        }

        return code.Length - start > 0 ? code.Substring(start, Math.Min(code.Length - start, 4000)) : string.Empty;
    }

    private static bool IsPrecededByNew(string code, int matchIndex)
    {
        var i = matchIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i]))
        {
            i--;
        }

        return i >= 2 && code[i] == 'w' && code[i - 1] == 'e' && code[i - 2] == 'n'
            && (i - 3 < 0 || !char.IsLetterOrDigit(code[i - 3]) && code[i - 3] != '_' && code[i - 3] != '$');
    }
}
