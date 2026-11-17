using Xunit;

namespace CrawlForge.Data.Sqlite.Tests;

/// <summary>Unit tests for provider option defaults and validation (baseline-preserving).</summary>
public class SqliteProviderOptionsTests
{
    [Fact(DisplayName = "Unconfigured options reproduce the baseline profile")]
    public void UnconfiguredOptionsReproduceBaseline()
    {
        var options = new SqliteProviderOptions();

        Assert.Null(options.DatabasePath);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), options.BusyTimeout);
        Assert.Equal(SqliteSynchronousMode.Normal, options.Synchronous);
        Assert.Null(options.NativeExtensionDirectory);
        options.Validate();
    }

    [Fact(DisplayName = "Default path lives in the per-user application data directory")]
    public void DefaultPathLivesInPerUserApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrawlForge",
            "crawlforge.db");

        Assert.Equal(expected, SqliteProviderOptions.GetDefaultDatabasePath());
        Assert.Equal(expected, new SqliteProviderOptions().ResolveDatabasePath());
    }

    [Fact(DisplayName = "Explicit database path overrides the default")]
    public void ExplicitDatabasePathOverridesDefault()
    {
        var options = new SqliteProviderOptions { DatabasePath = @"E:\data\custom.db" };

        Assert.Equal(@"E:\data\custom.db", options.ResolveDatabasePath());
        options.Validate();
    }

    [Fact(DisplayName = "Negative busy timeout is rejected")]
    public void NegativeBusyTimeoutRejected()
    {
        var options = new SqliteProviderOptions { BusyTimeout = TimeSpan.FromMilliseconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact(DisplayName = "Zero busy timeout is allowed")]
    public void ZeroBusyTimeoutAllowed()
    {
        var options = new SqliteProviderOptions { BusyTimeout = TimeSpan.Zero };

        options.Validate();
    }

    [Fact(DisplayName = "Empty or whitespace database path is rejected")]
    public void EmptyOrWhitespaceDatabasePathRejected()
    {
        Assert.Throws<ArgumentException>(() => new SqliteProviderOptions { DatabasePath = string.Empty }.Validate());
        Assert.Throws<ArgumentException>(() => new SqliteProviderOptions { DatabasePath = "   " }.Validate());
    }

    [Fact(DisplayName = "Synchronous modes map to the SQLite pragma encoding")]
    public void SynchronousModesMapToPragmaEncoding()
    {
        Assert.Equal(0, (int)SqliteSynchronousMode.Off);
        Assert.Equal(1, (int)SqliteSynchronousMode.Normal);
        Assert.Equal(2, (int)SqliteSynchronousMode.Full);
        Assert.Equal(3, (int)SqliteSynchronousMode.Extra);
    }
}
