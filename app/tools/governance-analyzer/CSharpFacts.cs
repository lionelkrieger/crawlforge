using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CrawlForge.Tools.Governance;

/// <summary>Where a string literal sits — determines whether endpoint rules treat it as behavior or data.</summary>
internal enum LiteralContext
{
    /// <summary>Plain data (catalogue entries, constants, collection initializers).</summary>
    Data,

    /// <summary>Argument of an HTTP transmission method (send context).</summary>
    SendArgument,

    /// <summary>Argument of any other invocation.</summary>
    Argument,

    /// <summary>Argument of new Uri(...).</summary>
    UriCreation,

    /// <summary>Assigned to an endpoint/url/address-shaped member.</summary>
    EndpointAssignment,

    /// <summary>Assigned to a listener host/bind-shaped member.</summary>
    BindingAssignment,
}

internal abstract record CSharpFact(int Line, int Column, string Namespace, string ContainingType);

internal sealed record UsingFact(string Name, int Line, int Column, string Namespace, string ContainingType)
    : CSharpFact(Line, Column, Namespace, ContainingType);

internal sealed record DeclarationFact(
    string Kind, string TypeName, string Name, int Line, int Column, string Namespace, string ContainingType)
    : CSharpFact(Line, Column, Namespace, ContainingType);

internal sealed record ArgumentFact(string Text, string? LiteralValue);

internal sealed record InvocationFact(
    string MethodName, string Receiver, string ArgumentsText, IReadOnlyList<ArgumentFact> Arguments,
    int Line, int Column, string Namespace, string ContainingType)
    : CSharpFact(Line, Column, Namespace, ContainingType);

internal sealed record ObjectCreationFact(
    string TypeName, string ArgumentsText, string? InitializerText, IReadOnlyList<ArgumentFact> Arguments,
    int Line, int Column, string Namespace, string ContainingType)
    : CSharpFact(Line, Column, Namespace, ContainingType);

internal sealed record MemberAccessFact(
    string Receiver, string Name, int Line, int Column, string Namespace, string ContainingType)
    : CSharpFact(Line, Column, Namespace, ContainingType);

internal sealed record LiteralFact(
    string Value, LiteralContext Context, string ContextName, int Line, int Column, string Namespace, string ContainingType)
    : CSharpFact(Line, Column, Namespace, ContainingType);

internal sealed class CSharpFileFacts
{
    public List<UsingFact> Usings { get; } = [];

    public List<DeclarationFact> Declarations { get; } = [];

    public List<InvocationFact> Invocations { get; } = [];

    public List<ObjectCreationFact> ObjectCreations { get; } = [];

    public List<MemberAccessFact> MemberAccesses { get; } = [];

    public List<LiteralFact> Literals { get; } = [];
}

/// <summary>
/// Roslyn SYNTAX-only fact collection (no compilation, no semantic model): fast, and independent
/// of any sibling workpiece's in-progress code. Trivia (comments) is never inspected, so
/// guardrail mentions in comments cannot produce false positives.
/// </summary>
internal sealed class CSharpFactCollector : CSharpSyntaxWalker
{
    private readonly SyntaxTree _tree;
    private readonly CSharpFileFacts _facts = new();
    private readonly Stack<string> _namespaces = new();
    private readonly Stack<string> _types = new();

    private CSharpFactCollector(SyntaxTree tree)
    {
        _tree = tree;
    }

    public static CSharpFileFacts Collect(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var collector = new CSharpFactCollector(tree);
        collector.Visit(tree.GetRoot());
        return collector._facts;
    }

    private string CurrentNamespace => _namespaces.Count > 0 ? string.Join(".", _namespaces.Reverse()) : string.Empty;

    private string CurrentType => _types.Count > 0 ? _types.Peek() : string.Empty;

    private (int Line, int Column) Position(SyntaxNode node)
    {
        var span = _tree.GetLineSpan(node.Span);
        return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max];

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var (line, col) = Position(node);
        _facts.Usings.Add(new UsingFact(node.Name?.ToString() ?? string.Empty, line, col, CurrentNamespace, CurrentType));
        base.VisitUsingDirective(node);
    }

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        _namespaces.Push(node.Name.ToString());
        base.VisitNamespaceDeclaration(node);
        _namespaces.Pop();
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        _namespaces.Push(node.Name.ToString());
        base.VisitFileScopedNamespaceDeclaration(node);
        _namespaces.Pop();
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        PushType(node.Identifier.Text, () => base.VisitClassDeclaration(node));
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        PushType(node.Identifier.Text, () => base.VisitRecordDeclaration(node));
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        PushType(node.Identifier.Text, () => base.VisitStructDeclaration(node));
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        PushType(node.Identifier.Text, () => base.VisitInterfaceDeclaration(node));
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        PushType(node.Identifier.Text, () => base.VisitEnumDeclaration(node));
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var (line, col) = Position(node);
        _facts.Declarations.Add(new DeclarationFact(
            "property", node.Type.ToString(), node.Identifier.Text, line, col, CurrentNamespace, CurrentType));
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            var (line, col) = Position(variable);
            _facts.Declarations.Add(new DeclarationFact(
                "field", node.Declaration.Type.ToString(), variable.Identifier.Text, line, col, CurrentNamespace, CurrentType));
        }

        base.VisitFieldDeclaration(node);
    }

    public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            var (line, col) = Position(variable);
            _facts.Declarations.Add(new DeclarationFact(
                "local", node.Declaration.Type.ToString(), variable.Identifier.Text, line, col, CurrentNamespace, CurrentType));
        }

        base.VisitLocalDeclarationStatement(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var (line, col) = Position(node);
        _facts.Declarations.Add(new DeclarationFact(
            "method", node.ReturnType.ToString(), node.Identifier.Text, line, col, CurrentNamespace, CurrentType));
        base.VisitMethodDeclaration(node);
    }

    public override void VisitParameter(ParameterSyntax node)
    {
        var (line, col) = Position(node);
        _facts.Declarations.Add(new DeclarationFact(
            "parameter", node.Type?.ToString() ?? string.Empty, node.Identifier.Text, line, col, CurrentNamespace, CurrentType));
        base.VisitParameter(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var (line, col) = Position(node);
        string methodName;
        string receiver;
        switch (node.Expression)
        {
            case MemberAccessExpressionSyntax memberAccess:
                methodName = memberAccess.Name.Identifier.Text;
                receiver = Cap(memberAccess.Expression.ToString(), 200);
                break;
            case IdentifierNameSyntax identifier:
                methodName = identifier.Identifier.Text;
                receiver = string.Empty;
                break;
            default:
                methodName = Cap(node.Expression.ToString(), 120);
                receiver = string.Empty;
                break;
        }

        var arguments = node.ArgumentList.Arguments
            .Select(a => new ArgumentFact(Cap(a.ToString(), 500), LiteralValueOf(a.Expression)))
            .ToList();
        var argsText = Cap(node.ArgumentList.ToString(), 2000);
        _facts.Invocations.Add(new InvocationFact(
            methodName, receiver, argsText, arguments, line, col, CurrentNamespace, CurrentType));
        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var (line, col) = Position(node);
        var typeName = node.Type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => Cap(node.Type.ToString(), 120),
        };

        var arguments = (node.ArgumentList?.Arguments ?? default)
            .Select(a => new ArgumentFact(Cap(a.ToString(), 500), LiteralValueOf(a.Expression)))
            .ToList();
        _facts.ObjectCreations.Add(new ObjectCreationFact(
            typeName,
            Cap(node.ArgumentList?.ToString() ?? "()", 2000),
            node.Initializer is null ? null : Cap(node.Initializer.ToString(), 2000),
            arguments,
            line, col, CurrentNamespace, CurrentType));
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var (line, col) = Position(node);
        _facts.MemberAccesses.Add(new MemberAccessFact(
            Cap(node.Expression.ToString(), 200), node.Name.Identifier.Text, line, col, CurrentNamespace, CurrentType));
        base.VisitMemberAccessExpression(node);
    }

    public override void VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.StringLiteralExpression) && node.Token.ValueText is string value)
        {
            AddLiteral(node, value);
        }

        base.VisitLiteralExpression(node);
    }

    public override void VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
        var value = string.Concat(node.Contents.Select(content => content switch
        {
            InterpolatedStringTextSyntax text => text.TextToken.ValueText,
            InterpolationSyntax interpolation => "{" + interpolation.Expression.ToString() + "}",
            _ => string.Empty,
        }));
        AddLiteral(node, value);
        base.VisitInterpolatedStringExpression(node);
    }

    private void AddLiteral(SyntaxNode node, string value)
    {
        var (line, col) = Position(node);
        var (context, contextName) = ClassifyLiteralContext(node);
        _facts.Literals.Add(new LiteralFact(value, context, contextName, line, col, CurrentNamespace, CurrentType));
    }

    private static (LiteralContext Context, string Name) ClassifyLiteralContext(SyntaxNode node)
    {
        var parent = node.Parent;
        switch (parent)
        {
            case ArgumentSyntax argument:
                var owner = argument.Parent?.Parent;
                if (owner is InvocationExpressionSyntax invocation)
                {
                    var name = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        IdentifierNameSyntax id => id.Identifier.Text,
                        _ => invocation.Expression.ToString(),
                    };
                    var isSend = PatternLists.HttpSendMethods.Contains(name, StringComparer.Ordinal);
                    return (isSend ? LiteralContext.SendArgument : LiteralContext.Argument, name);
                }

                if (owner is ObjectCreationExpressionSyntax creation)
                {
                    var typeName = creation.Type.ToString();
                    return (typeName.EndsWith("Uri", StringComparison.Ordinal) ? LiteralContext.UriCreation : LiteralContext.Argument, typeName);
                }

                return (LiteralContext.Argument, string.Empty);

            case EqualsValueClauseSyntax equals:
                var targetName = equals.Parent switch
                {
                    VariableDeclaratorSyntax variable => variable.Identifier.Text,
                    PropertyDeclarationSyntax property => property.Identifier.Text,
                    AssignmentExpressionSyntax assignment => assignment.Left.ToString(),
                    _ => string.Empty,
                };
                var tokens = IdentifierTokens.Tokenize(targetName);
                if (tokens.Contains("endpoint") || tokens.Contains("url") || tokens.Contains("baseaddress")
                    || tokens.Contains("uri") || tokens.Contains("address"))
                {
                    return (LiteralContext.EndpointAssignment, targetName);
                }

                if (tokens.Contains("host") || tokens.Contains("bind") || tokens.Contains("listen"))
                {
                    return (LiteralContext.BindingAssignment, targetName);
                }

                return (LiteralContext.Data, targetName);

            default:
                return (LiteralContext.Data, string.Empty);
        }
    }

    private static string? LiteralValueOf(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(content => content switch
        {
            InterpolatedStringTextSyntax text => text.TextToken.ValueText,
            InterpolationSyntax interpolation => "{" + interpolation.Expression.ToString() + "}",
            _ => string.Empty,
        })),
        _ => null,
    };

    private void PushType(string name, Action visit)
    {
        _types.Push(name);
        visit();
        _types.Pop();
    }
}
