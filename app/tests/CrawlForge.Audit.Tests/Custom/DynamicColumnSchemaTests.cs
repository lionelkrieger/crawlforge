using CrawlForge.Audit.Custom;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Custom;

/// <summary>
/// Dynamic-column schema contract (phase-05 consumer surface): naming,
/// multi-value expansion, types, snippet column union.
/// </summary>
public sealed class DynamicColumnSchemaTests
{
    [Fact]
    public void SingleValuedRules_ProduceOneColumnPerRule_InOrder()
    {
        var rules = new[]
        {
            new CustomExtractionRule("price", ".price", ExtractedValueType.Number),
            new CustomExtractionRule("title", "h1", ExtractedValueType.Text),
        };
        var values = new[]
        {
            new ExtractedValueDraft("price", 0, "19.99", ExtractedValueType.Number, false, false),
            new ExtractedValueDraft("title", 0, "\"Widget\"", ExtractedValueType.Text, false, false),
        };

        IReadOnlyList<DynamicColumnDescriptor> columns = DynamicColumnSchema.BuildExtractionColumns(rules, values);

        Assert.Equal(2, columns.Count);
        Assert.Equal("custom.price", columns[0].Key);
        Assert.Equal("number", columns[0].ValueType);
        Assert.Equal("custom.title", columns[1].Key);
        Assert.Equal("text", columns[1].ValueType);
    }

    [Fact]
    public void MultiValuedRule_ExpandsToBusiestPage()
    {
        var rules = new[] { new CustomExtractionRule("tags", ".tag", ExtractedValueType.Text, AllowMultiple: true) };
        var values = new[]
        {
            new ExtractedValueDraft("tags", 0, "\"a\"", ExtractedValueType.Text, false, false),
            new ExtractedValueDraft("tags", 1, "\"b\"", ExtractedValueType.Text, false, false),
            new ExtractedValueDraft("tags", 2, "\"c\"", ExtractedValueType.Text, false, false),
        };

        IReadOnlyList<DynamicColumnDescriptor> columns = DynamicColumnSchema.BuildExtractionColumns(rules, values);

        Assert.Equal(3, columns.Count);
        Assert.Equal("custom.tags.0", columns[0].Key);
        Assert.Equal("custom.tags.2", columns[2].Key);
        Assert.Equal(2, columns[2].Ordinal);
    }

    [Fact]
    public void RuleWithNoValues_StillProducesOneColumn()
    {
        var rules = new[] { new CustomExtractionRule("empty", ".none", ExtractedValueType.Text) };

        IReadOnlyList<DynamicColumnDescriptor> columns = DynamicColumnSchema.BuildExtractionColumns(rules, Array.Empty<ExtractedValueDraft>());

        Assert.Equal("custom.empty", Assert.Single(columns).Key);
    }

    [Fact]
    public void SnippetColumns_UnionAcrossPages_FirstSeenOrder()
    {
        var results = new[]
        {
            new SnippetResultDraft("s1", "[{\"key\":\"a\",\"label\":\"a\",\"valueType\":\"number\"}]", "[{\"a\":1}]", false),
            new SnippetResultDraft("s1", "[{\"key\":\"b\",\"label\":\"b\",\"valueType\":\"string\"},{\"key\":\"a\",\"label\":\"a\",\"valueType\":\"number\"}]", "[{\"a\":2,\"b\":\"x\"}]", false),
        };

        IReadOnlyList<DynamicColumnDescriptor> columns = DynamicColumnSchema.BuildSnippetColumns(results);

        Assert.Equal(2, columns.Count);
        Assert.Equal("snippet.s1.a", columns[0].Key);
        Assert.Equal("snippet.s1.b", columns[1].Key);
        Assert.Equal("number", columns[0].ValueType);
    }

    [Fact]
    public void ValueTypeMapping_CoversLockedEnum()
    {
        Assert.Equal("text", DynamicColumnSchema.MapValueType(ExtractedValueType.Text));
        Assert.Equal("number", DynamicColumnSchema.MapValueType(ExtractedValueType.Number));
        Assert.Equal("boolean", DynamicColumnSchema.MapValueType(ExtractedValueType.Boolean));
        Assert.Equal("datetime", DynamicColumnSchema.MapValueType(ExtractedValueType.DateTime));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => DynamicColumnSchema.BuildExtractionColumns(null!, Array.Empty<ExtractedValueDraft>()));
        Assert.Throws<ArgumentNullException>(() => DynamicColumnSchema.BuildSnippetColumns(null!));
    }
}