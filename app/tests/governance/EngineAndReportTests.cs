using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Tools.Governance;
using Xunit;

namespace CrawlForge.Tests.Governance;

/// <summary>Report shape, determinism, CLI behavior, and classifier unit tests.</summary>
public class EngineAndReportTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _scratch = TestPaths.CreateScratchDirectory();

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Report_lists_all_rules_with_detection_basis()
    {
        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(TestPaths.FixturesRoot));
        Assert.Equal(20, report.Rules.Count);
        Assert.All(report.Rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.DetectionBasis));
            Assert.False(string.IsNullOrWhiteSpace(rule.SpecReference));
        });
        Assert.Equal("crawlforge-governance-analyzer", report.Tool);
    }

    [Fact]
    public void Report_is_byte_deterministic_for_identical_inputs()
    {
        var engine = new GovernanceAnalyzerEngine();
        var first = JsonSerializer.Serialize(
            engine.Analyze(new GovernanceAnalysisRequest(TestPaths.FixturesRoot)), JsonOptions);
        var second = JsonSerializer.Serialize(
            engine.Analyze(new GovernanceAnalysisRequest(TestPaths.FixturesRoot)), JsonOptions);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Violations_are_deterministically_ordered()
    {
        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(TestPaths.FixturesRoot));
        Assert.NotEmpty(report.Violations);
        for (var i = 1; i < report.Violations.Count; i++)
        {
            Assert.True(
                GovernanceViolation.Compare(report.Violations[i - 1], report.Violations[i]) <= 0,
                $"Violations out of order at index {i}.");
        }
    }

    [Fact]
    public void Rule_filter_restricts_violations_and_catalog()
    {
        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(
            TestPaths.FixturesRoot, RuleFilter: ["NR-01"]));
        Assert.All(report.Violations, v => Assert.Equal("NR-01", v.RuleId));
        Assert.Single(report.Rules);
        Assert.NotEmpty(report.Violations);
    }

    [Fact]
    public void Comments_never_fire_rules()
    {
        File.WriteAllText(Path.Combine(_scratch, "commented.cs"), """
            // This file only TALKS about prohibited things: Registry.GetValue, WMI,
            // MachineGuid, license keys, api.openai.com, telemetry, CAPTCHA solving.
            /* Registry.CurrentUser.GetValue("MachineGuid"); new HttpClient(); */
            namespace Scratch;
            public static class Quiet
            {
                public static int Add(int a, int b) => a + b;
            }
            """);
        File.WriteAllText(Path.Combine(_scratch, "commented.ts"), """
            // fetch('https://api.openai.com/v1/embeddings'); solveCaptcha(); machineId
            /* const licenseKey = 'x'; new WebSocket('wss://relay.example.com'); */
            export function add(a: number, b: number): number {
              return a + b;
            }
            """);
        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(_scratch));
        Assert.True(report.Passed, string.Join("; ", report.Violations.Select(v => $"{v.RuleId}:{v.Line} {v.Evidence}")));
    }

    [Fact]
    public void Enumerator_excludes_generated_dirs_and_governance_self_paths()
    {
        const string evil = "using Microsoft.Win32; public class Evil { public object X => Registry.CurrentUser; }";
        foreach (var relative in new[]
        {
            "obj/evil.cs", "bin/evil.cs", "node_modules/evil.cs", "dist/evil.cs",
            "tools/governance-analyzer/evil.cs", "tests/governance/evil.cs",
            "src/Generated.g.cs", "src/Widget.Designer.cs",
        })
        {
            var path = Path.Combine(_scratch, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, evil);
        }

        Directory.CreateDirectory(Path.Combine(_scratch, "src"));
        File.WriteAllText(Path.Combine(_scratch, "src", "real.cs"), "namespace Scratch; public static class Real { }");

        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(_scratch));
        Assert.Equal(1, report.FilesScanned);
        Assert.True(report.Passed);
    }

    [Fact]
    public void Cli_requires_root_and_rejects_unknown_arguments()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--root"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--root", "x", "--bogus"]));
        var options = CliOptions.Parse(["--root", "x", "--out", "r.json", "--fail-on-violation", "--rules", "NR-01, NET-02"]);
        Assert.Equal("x", options.Root);
        Assert.Equal("r.json", options.OutputPath);
        Assert.True(options.FailOnViolation);
        Assert.Equal(["NR-01", "NET-02"], options.RuleFilter);
    }

    [Fact]
    public void Cli_exit_codes_follow_the_gate_contract()
    {
        var violatingRoot = Path.Combine(TestPaths.FixturesRoot, "NR-01");
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);

            Assert.Equal(1, Program.Main(["--root", violatingRoot, "--fail-on-violation"]));
            Assert.Equal(0, Program.Main(["--root", violatingRoot]));
            Assert.Equal(0, Program.Main(["--root", violatingRoot, "--rules", "NET-02", "--fail-on-violation"]));
            Assert.Equal(2, Program.Main(["--root", Path.Combine(_scratch, "does-not-exist"), "--fail-on-violation"]));
            Assert.Equal(2, Program.Main([]));

            var outPath = Path.Combine(_scratch, "report.json");
            Assert.Equal(1, Program.Main(["--root", violatingRoot, "--out", outPath, "--fail-on-violation"]));
            Assert.True(File.Exists(outPath));
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
            Assert.True(document.RootElement.GetProperty("violations").GetArrayLength() > 0);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/api", true)]
    [InlineData("http://localhost:5173/", true)]
    [InlineData("http://sub.localhost:5173/", true)]
    [InlineData("http://[::1]:8080/", true)]
    [InlineData("http://127.5.5.5/", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://192.168.1.50/", false)]
    [InlineData("https://localhost.evil.com/", false)]
    [InlineData("https://10.0.0.1/", false)]
    public void Loopback_classification_is_precise(string url, bool expected)
    {
        Assert.Equal(expected, EndpointClassifier.IsLoopbackUrl(url));
    }

    [Fact]
    public void Identifier_tokenization_avoids_substring_false_positives()
    {
        Assert.False(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("industrial"), ["trial"]));
        Assert.False(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("Activator"), ["activation"]));
        Assert.False(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("LogInformation"), ["login"]));
        Assert.True(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("GetRemoteFlagValue"), ["remote", "flag"]));
        Assert.True(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("machine_id"), ["machine", "id"]));
        Assert.True(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("MachineGUID"), ["machine", "guid"]));
        Assert.True(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("FetchSignaturesAsync"), ["fetch", "signature"]));
        Assert.True(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("DownloadStopWordsAsync"), ["download", "stop", "word"]));
        Assert.True(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("PhysicalAddress"), ["physical", "address"]));
        Assert.False(IdentifierTokens.ContainsSequence(IdentifierTokens.Tokenize("SubscriptionPayload"), ["license"]));
    }

    [Fact]
    public void Comment_stripping_preserves_positions_and_strings()
    {
        const string code = "const a = 1; // fetch('https://evil.example.com')\nconst url = 'http://127.0.0.1'; /* x */";
        var stripped = TextScanner.StripComments(code);
        Assert.Equal(code.Length, stripped.Length);
        Assert.DoesNotContain("evil.example.com", stripped);
        Assert.Contains("http://127.0.0.1", stripped);
        Assert.Equal(code.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
    }

    [Fact]
    public void Boundary_allowlist_honors_custom_config()
    {
        var configPath = Path.Combine(_scratch, "custom-boundary.json");
        File.WriteAllText(configPath, """
            {
              "namespacePrefixes": ["Fixture.Custom"],
              "typeNamePatterns": ["*CustomTarget*"],
              "pathFragments": ["custom-area/"]
            }
            """);
        var allowlist = NetworkBoundaryAllowlist.Load(configPath);
        Assert.True(allowlist.IsApprovedCSharpScope("Fixture.Custom.Fetching", "Anything", "x/y.cs"));
        Assert.True(allowlist.IsApprovedCSharpScope("Other", "MyCustomTargetClient", "x/y.cs"));
        Assert.True(allowlist.IsApprovedTextScope("src/custom-area/client.ts"));
        Assert.False(allowlist.IsApprovedCSharpScope("CrawlForge.Crawler", "Fetcher", "x/y.cs"));
    }
}
