using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// Outcome of loading the bundled extension surface onto one connection: the managed
/// capability families that were registered plus any locally bundled native extensions
/// that were loaded (or failed to load). Native load failures are reported, never
/// silently swallowed and never fatal to the managed families.
/// </summary>
/// <param name="ManagedFamilies">Managed capability families registered on the connection.</param>
/// <param name="NativeExtensionsLoaded">Absolute paths of native extensions loaded from the local bundle.</param>
/// <param name="NativeExtensionErrors">Per-file native load error descriptions, when any.</param>
public sealed record SqliteExtensionLoadReport(
    IReadOnlyList<SqliteExtensionFamily> ManagedFamilies,
    IReadOnlyList<string> NativeExtensionsLoaded,
    IReadOnlyList<string> NativeExtensionErrors);

/// <summary>
/// Loads the four required capability families (FS-CRAWL-001 §2.4) onto every opened
/// connection. The families are bundled locally as managed SQL functions/aggregates
/// (regex, delimited-text, vector-similarity, math/stats) so the default provider is
/// fully self-contained with zero native dependencies beyond the SQLite engine itself.
/// When a local native bundle directory is configured, platform-specific native
/// extensions are additionally loaded from it at connection open — per-platform
/// packaging layout: <c>&lt;bundle&gt;/&lt;runtime-identifier&gt;/</c> with fallback to
/// <c>&lt;bundle&gt;/</c>. Binaries are only ever loaded from the local bundle; nothing
/// is downloaded at build or run time (clean-room network boundary).
/// </summary>
public sealed class SqliteExtensionLoader
{
    private static readonly SqliteExtensionFamily[] AllFamilies =
    [
        SqliteExtensionFamily.Regex,
        SqliteExtensionFamily.DelimitedText,
        SqliteExtensionFamily.VectorSimilarity,
        SqliteExtensionFamily.MathStatistics,
    ];

    private readonly string? _nativeDirectory;

    /// <summary>Creates a loader.</summary>
    /// <param name="nativeExtensionDirectory">
    /// Optional local directory with bundled native extensions; <see langword="null"/>
    /// means managed families only.
    /// </param>
    public SqliteExtensionLoader(string? nativeExtensionDirectory = null)
    {
        _nativeDirectory = nativeExtensionDirectory;
    }

    /// <summary>
    /// Registers all managed capability families and loads any locally bundled native
    /// extensions onto an open connection.
    /// </summary>
    /// <param name="connection">The open connection to extend.</param>
    /// <returns>The load report for this connection.</returns>
    public SqliteExtensionLoadReport Load(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        RegexFunctions.Register(connection);
        DelimitedTextFunctions.Register(connection);
        VectorFunctions.Register(connection);
        MathStatisticsFunctions.Register(connection);

        var loaded = new List<string>();
        var errors = new List<string>();
        var directory = ResolveNativeDirectory();
        if (directory is not null)
        {
            connection.EnableExtensions(true);
            foreach (var file in Directory.EnumerateFiles(directory)
                .Where(IsNativeLibrary)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    connection.LoadExtension(file);
                    loaded.Add(file);
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
                {
                    errors.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{file}: {ex.Message}"));
                }
            }
        }

        return new SqliteExtensionLoadReport(AllFamilies, loaded, errors);
    }

    private string? ResolveNativeDirectory()
    {
        if (_nativeDirectory is null)
        {
            return null;
        }

        var platformDirectory = Path.Combine(_nativeDirectory, RuntimeInformation.RuntimeIdentifier);
        if (Directory.Exists(platformDirectory))
        {
            return platformDirectory;
        }

        return Directory.Exists(_nativeDirectory) ? _nativeDirectory : null;
    }

    private static bool IsNativeLibrary(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
    }
}
