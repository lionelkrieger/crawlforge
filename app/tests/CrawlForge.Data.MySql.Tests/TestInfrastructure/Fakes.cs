using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>Probe stub returning a configured outcome and recording every call.</summary>
internal sealed class RecordingProbe : IMySqlEndpointProbe
{
    public int Calls { get; private set; }

    public MySqlDatabaseSettings? LastSettings { get; private set; }

    public MySqlProbeOutcome Outcome { get; set; } = MySqlProbeOutcome.Success("8.0.36");

    public Exception? ThrowOnProbe { get; set; }

    public Task<MySqlProbeOutcome> ProbeAsync(MySqlDatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastSettings = settings;
        if (ThrowOnProbe is not null)
        {
            return Task.FromException<MySqlProbeOutcome>(ThrowOnProbe);
        }

        return Task.FromResult(Outcome);
    }
}

/// <summary>Factory stub recording calls and returning unopened connections.</summary>
internal sealed class CountingConnectionFactory : IMySqlConnectionFactory
{
    private readonly Func<MySqlDatabaseSettings, MySqlConnector.MySqlConnection> _creator;

    public CountingConnectionFactory(Func<MySqlDatabaseSettings, MySqlConnector.MySqlConnection>? creator = null)
    {
        _creator = creator ?? (settings => new MySqlConnector.MySqlConnection(MySqlConnectionFactory.BuildConnectionString(settings)));
    }

    public int Calls { get; private set; }

    public MySqlDatabaseSettings? LastSettings { get; private set; }

    public MySqlConnector.MySqlConnection Create(MySqlDatabaseSettings settings)
    {
        Calls++;
        LastSettings = settings;
        return _creator(settings);
    }
}

/// <summary>Provider stub with a configurable connection-test verdict.</summary>
internal sealed class FakeDbProvider : IDbProvider
{
    public List<string> Calls { get; } = [];

    public CallSequence? Sequence { get; set; }

    public string Name { get; set; } = "provider";

    public DatabaseProviderKind Kind { get; set; } = DatabaseProviderKind.MySql;

    public string Dialect { get; set; } = "mysql";

    public DbConnectionTestResult TestResult { get; set; } = new(true, true, true, "ok");

    public Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("OpenConnection");
        Sequence?.Record(Name + ".OpenConnection");
        throw new NotSupportedException("The fake provider does not open connections.");
    }

    public Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("TestConnection");
        Sequence?.Record(Name + ".TestConnection");
        return Task.FromResult(TestResult);
    }
}

/// <summary>Migrator stub with a configurable ledger, recording call order.</summary>
internal sealed class FakeMigrator : ISchemaMigrator
{
    public List<string> Calls { get; } = [];

    public CallSequence? Sequence { get; set; }

    public string Name { get; set; } = "migrator";

    public int Version { get; set; } = 40;

    public string Dialect { get; set; } = "mysql";

    public int LedgerRows { get; set; } = 1;

    public string? OverrideLedgerDialect { get; set; }

    public Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("GetCurrentVersion");
        Sequence?.Record(Name + ".GetCurrentVersion");
        return Task.FromResult(Version);
    }

    public Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("Migrate");
        Sequence?.Record(Name + ".Migrate");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CrawlForge.Contracts.Entities.SchemaMigrationDto>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("GetAppliedMigrations");
        Sequence?.Record(Name + ".GetAppliedMigrations");
        var rows = new List<CrawlForge.Contracts.Entities.SchemaMigrationDto>();
        for (int i = 0; i < LedgerRows; i++)
        {
            rows.Add(new CrawlForge.Contracts.Entities.SchemaMigrationDto(
                i + 1,
                Version - LedgerRows + 1 + i,
                OverrideLedgerDialect ?? Dialect,
                DateTimeOffset.UnixEpoch));
        }

        return Task.FromResult<IReadOnlyList<CrawlForge.Contracts.Entities.SchemaMigrationDto>>(rows);
    }
}

/// <summary>Archive service stub recording calls and payloads.</summary>
internal sealed class FakeArchiveService : IProjectArchiveService
{
    public List<string> Calls { get; } = [];

    public CallSequence? Sequence { get; set; }

    public string Name { get; set; } = "archive";

    public byte[] ExportPayload { get; set; } = [1, 2, 3];

    public ProjectArchiveImportResult ImportResult { get; set; } = new(7, 2, 10);

    public long LastExportProjectId { get; private set; }

    public byte[]? LastImportBytes { get; private set; }

    public Task ExportAsync(long projectId, Stream destination, CancellationToken cancellationToken = default)
    {
        Calls.Add("Export");
        Sequence?.Record(Name + ".Export");
        LastExportProjectId = projectId;
        return destination.WriteAsync(ExportPayload, cancellationToken).AsTask();
    }

    public Task<ProjectArchiveImportResult> ImportAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        Calls.Add("Import");
        Sequence?.Record(Name + ".Import");
        using var copy = new MemoryStream();
        archive.CopyTo(copy);
        LastImportBytes = copy.ToArray();
        return Task.FromResult(ImportResult);
    }
}

/// <summary>Shared call-order log so switch orchestration ordering is assertable.</summary>
internal sealed class CallSequence
{
    public List<string> Entries { get; } = [];

    public void Record(string entry) => Entries.Add(entry);
}
