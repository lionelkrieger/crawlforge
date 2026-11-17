using System.Text.Json;
using CrawlForge.Host.Configuration;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Initialization;

/// <summary>Outcome of the first-launch initialization path.</summary>
public sealed record FirstLaunchResult(bool FirstRun, string DataDirectory, DateTimeOffset CompletedAtUtc);

/// <summary>
/// First-launch initialization (process-model spec §2.3 step 3, skeleton form): creates
/// the per-user data directory, seeds default local settings with every feature enabled,
/// and records completion. Completes with NO network access — local filesystem only.
/// Schema migrations and deterministic data seeding arrive with the persistence phase.
/// </summary>
public sealed class FirstLaunchInitializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly CrawlForgePaths _paths;
    private readonly ILogger<FirstLaunchInitializer> _logger;

    public FirstLaunchInitializer(CrawlForgePaths paths, ILogger<FirstLaunchInitializer> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<FirstLaunchResult> InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        Directory.CreateDirectory(_paths.DiagnosticsDirectory);

        var firstRun = !File.Exists(_paths.SettingsFilePath);
        if (firstRun)
        {
            var settings = new LocalSettings { FirstLaunchCompletedUtc = DateTimeOffset.UtcNow };
            await using var stream = File.Create(_paths.SettingsFilePath);
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
            _logger.LogInformation(
                "First launch: local settings seeded at {Path} (all features enabled by default).",
                _paths.SettingsFilePath);
        }

        return new FirstLaunchResult(firstRun, _paths.DataDirectory, DateTimeOffset.UtcNow);
    }
}
