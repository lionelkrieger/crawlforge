using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// User-triggered DNS verification tool (FS-CRAWL-002 §11.3/§11.7, E8). The
/// ONLY path that may resolve DNS: explicit per-import consent is mandatory,
/// the sample is bounded, and results are logged locally into the import's
/// counters payload. Import-time classification never invokes this tool.
/// REST mapping: POST /api/logs/{id}/verify-dns (phase-05).
/// </summary>
public sealed class LogDnsVerificationTool : ILogDnsVerificationTool
{
    /// <summary>Hard upper bound for one verification sample.</summary>
    public const int MaxSampleSize = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbProvider _provider;
    private readonly IBotVerifier _verifier;

    /// <summary>Creates the tool.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="verifier">The bot verifier (DNS path).</param>
    public LogDnsVerificationTool(IDbProvider provider, IBotVerifier verifier)
    {
        _provider = provider;
        _verifier = verifier;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DnsVerificationResult>> VerifySampleAsync(
        long importId,
        bool consent,
        int sampleSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!consent)
        {
            throw new InvalidOperationException(
                "DNS verification requires explicit per-import user consent (FS-CRAWL-002 §11.3).");
        }

        if (sampleSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize), "The sample size must be positive.");
        }

        sampleSize = Math.Min(sampleSize, MaxSampleSize);
        var countersJson = await LoadCountersJsonAsync(importId, cancellationToken);
        var counters = JsonSerializer.Deserialize<LogImportCountersPayload>(countersJson, JsonOptions);
        var samples = (counters?.BotIpSamples ?? []).Take(sampleSize).ToList();

        var results = new List<DnsVerificationResult>(samples.Count);
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _verifier.VerifyDnsAsync(sample.Ip, cancellationToken));
        }

        await LogResultsLocallyAsync(importId, countersJson, results, cancellationToken);
        return results;
    }

    private async Task<string> LoadCountersJsonAsync(long importId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT counters_json FROM log_imports WHERE id = @id";
        AddParameter(command, "@id", importId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string ?? throw new InvalidOperationException($"Log import {importId} does not exist.");
    }

    private async Task LogResultsLocallyAsync(long importId, string countersJson, IReadOnlyList<DnsVerificationResult> results, CancellationToken cancellationToken)
    {
        // Local logging only: merge a dnsVerification section into counters_json.
        var node = JsonNode.Parse(countersJson) ?? new JsonObject();
        node["dnsVerification"] = JsonSerializer.SerializeToNode(new
        {
            verifiedAt = DateTimeOffset.UtcNow,
            results = results.Select(r => new { ip = r.ClientIp, verified = r.Verified, hostname = r.Hostname })
        }, JsonOptions);

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE log_imports SET counters_json = @counters WHERE id = @id";
        AddParameter(command, "@counters", node.ToJsonString());
        AddParameter(command, "@id", importId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
