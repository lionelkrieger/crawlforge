using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;
using CrawlForge.Security.Diagnostics;
using CrawlForge.Security.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Security.Tests.Diagnostics;

/// <summary>
/// Artifact registry (FS-CRAWL-001 §10.5): persistent metadata, bounded
/// retention with oldest-first reclamation, self-healing listing, and the
/// user-initiated purge capability consumed by the settings surface.
/// </summary>
public sealed class DiagnosticArtifactRegistryTests
{
    [Fact]
    public async Task Register_List_Purge_RoundTrip()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        string file = CreateFile(temp, "artifact.log", 64);

        DiagnosticArtifactDto dto = await registry.RegisterAsync(file, DiagnosticArtifactKind.Log);

        Assert.True(dto.Id > 0);
        Assert.Equal(DiagnosticArtifactKind.Log, dto.Kind);
        Assert.Equal(64, dto.SizeBytes);
        Assert.Equal(Path.GetFullPath(file), dto.FilePath);

        DiagnosticArtifactDto row = Assert.Single(await registry.ListAsync(null));
        Assert.Equal(dto.Id, row.Id);

        Assert.Equal(1, await registry.PurgeAsync(null));
        Assert.False(File.Exists(file));
        Assert.Empty(await registry.ListAsync(null));
    }

    [Fact]
    public async Task Registry_PersistsAcrossInstances_WithStableIds()
    {
        using var temp = new TempDirectory();
        var options = new DiagnosticsOptions(temp.RootPath);
        string file = CreateFile(temp, "persistent.log", 16);

        var first = new DiagnosticArtifactRegistry(options);
        DiagnosticArtifactDto dto = await first.RegisterAsync(file, DiagnosticArtifactKind.Log);

        var second = new DiagnosticArtifactRegistry(options);
        DiagnosticArtifactDto row = Assert.Single(await second.ListAsync(null));

        Assert.Equal(dto.Id, row.Id);
        Assert.Equal(dto.Kind, row.Kind);
        Assert.Equal(dto.FilePath, row.FilePath);

        // Surrogate ids keep increasing across restarts.
        string nextFile = CreateFile(temp, "next.log", 16);
        DiagnosticArtifactDto next = await second.RegisterAsync(nextFile, DiagnosticArtifactKind.Log);
        Assert.True(next.Id > dto.Id);
    }

    [Fact]
    public async Task Retention_ReclaimsOldestFirst_WhenCountBoundExceeded()
    {
        using var temp = new TempDirectory();
        var options = new DiagnosticsOptions(temp.RootPath, maxArtifactCount: 3);
        var registry = new DiagnosticArtifactRegistry(options);

        var files = new List<string>();
        var ids = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            string file = CreateFile(temp, $"artifact-{i}.log", 32);
            files.Add(file);
            ids.Add((await registry.RegisterAsync(file, DiagnosticArtifactKind.Log)).Id);
        }

        IReadOnlyList<DiagnosticArtifactDto> remaining = await registry.ListAsync(null);
        Assert.Equal(3, remaining.Count);

        // The three newest survive; the two oldest are reclaimed from disk.
        Assert.Equal(new[] { ids[2], ids[3], ids[4] }, remaining.Select(r => r.Id).OrderBy(id => id).ToArray());
        Assert.False(File.Exists(files[0]));
        Assert.False(File.Exists(files[1]));
        Assert.True(File.Exists(files[2]));
        Assert.True(File.Exists(files[3]));
        Assert.True(File.Exists(files[4]));
    }

    [Fact]
    public async Task Retention_ReclaimsOldestFirst_WhenTotalSizeBoundExceeded()
    {
        using var temp = new TempDirectory();
        var options = new DiagnosticsOptions(temp.RootPath, maxArtifactCount: 100, maxTotalBytes: 250);
        var registry = new DiagnosticArtifactRegistry(options);

        string first = CreateFile(temp, "a.log", 100);
        string second = CreateFile(temp, "b.log", 100);
        string third = CreateFile(temp, "c.log", 100);

        await registry.RegisterAsync(first, DiagnosticArtifactKind.Log);
        await registry.RegisterAsync(second, DiagnosticArtifactKind.Log);
        await registry.RegisterAsync(third, DiagnosticArtifactKind.Log);

        IReadOnlyList<DiagnosticArtifactDto> remaining = await registry.ListAsync(null);
        Assert.Equal(2, remaining.Count);
        Assert.True(remaining.Sum(r => r.SizeBytes) <= 250);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
    }

    [Fact]
    public async Task List_RefreshesSizesFromDisk()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        string file = CreateFile(temp, "growing.log", 10);

        DiagnosticArtifactDto dto = await registry.RegisterAsync(file, DiagnosticArtifactKind.Log);
        Assert.Equal(10, dto.SizeBytes);

        File.AppendAllText(file, "0123456789");

        DiagnosticArtifactDto row = Assert.Single(await registry.ListAsync(null));
        Assert.Equal(20, row.SizeBytes);
    }

    [Fact]
    public async Task List_DropsEntriesWhoseFilesVanished()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        string file = CreateFile(temp, "vanished.log", 8);
        await registry.RegisterAsync(file, DiagnosticArtifactKind.Log);

        File.Delete(file);

        Assert.Empty(await registry.ListAsync(null));
    }

    [Fact]
    public async Task Purge_RemovesEntry_EvenWhenFileAlreadyGone()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        string file = CreateFile(temp, "gone.log", 8);
        await registry.RegisterAsync(file, DiagnosticArtifactKind.Log);
        File.Delete(file);

        Assert.Equal(1, await registry.PurgeAsync(null));
        Assert.Empty(await registry.ListAsync(null));
    }

    [Fact]
    public async Task CorruptRegistryFile_RecoversToEmptyRegistry()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.RootPath, "artifacts.json"), "{{{ this is not valid JSON");

        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));

        Assert.Empty(await registry.ListAsync(null));
        string file = CreateFile(temp, "after-recovery.log", 8);
        DiagnosticArtifactDto dto = await registry.RegisterAsync(file, DiagnosticArtifactKind.Log);
        Assert.True(dto.Id > 0);
    }

    [Fact]
    public async Task Register_MissingFile_Throws()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => registry.RegisterAsync(Path.Combine(temp.RootPath, "missing.log"), DiagnosticArtifactKind.Log));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DiagnosticArtifactRegistry(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RejectNonPositiveCountBound(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticsOptions(maxArtifactCount: count));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Options_RejectNonPositiveSizeBound(long bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticsOptions(maxTotalBytes: bytes));
    }

    private static string CreateFile(TempDirectory temp, string name, int size)
    {
        string path = Path.Combine(temp.RootPath, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
}