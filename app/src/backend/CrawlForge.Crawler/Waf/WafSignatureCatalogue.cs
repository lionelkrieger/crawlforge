using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Effective WAF signature catalogue (FS-CRAWL-002 §2.6): the bundled JSON
/// asset plus user-local entries persisted in <c>waf_user_signatures</c>.
/// Bundled entries are packaged assets read from the local install only —
/// there is no runtime fetch of any kind (exclusions E2/E4). User entries
/// shadow bundled entries with the same key; deleting a user entry restores
/// the bundled default.
/// </summary>
public sealed class WafSignatureCatalogue : IWafSignatureCatalogue
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    private static readonly JsonSerializerOptions AssetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IDbProvider _provider;
    private readonly string _bundledAssetPath;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<BundledCatalogueAsset> _bundledAsset;

    /// <summary>Creates the catalogue over a database provider and the bundled asset file.</summary>
    /// <param name="provider">Database provider holding user-local signature entries.</param>
    /// <param name="bundledAssetPath">Local path of the bundled catalogue JSON asset.</param>
    /// <param name="timeProvider">Clock for entry timestamps; defaults to the system clock.</param>
    public WafSignatureCatalogue(IDbProvider provider, string bundledAssetPath, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledAssetPath);
        _provider = provider;
        _bundledAssetPath = bundledAssetPath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bundledAsset = new Lazy<BundledCatalogueAsset>(LoadBundledAsset);
    }

    /// <inheritdoc />
    public async Task<WafCatalogueSnapshot> GetEffectiveCatalogueAsync(CancellationToken cancellationToken = default)
    {
        var asset = _bundledAsset.Value;
        var effective = new SortedDictionary<string, WafSignature>(StringComparer.Ordinal);
        foreach (var signature in ConvertBundled(asset))
        {
            effective[signature.Key] = signature;
        }

        var userCount = 0;
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT signature_key, provider, challenge_class, header_markers_json, body_markers_json " +
            "FROM waf_user_signatures WHERE is_enabled = 1 ORDER BY signature_key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            effective[key] = new WafSignature(
                key,
                (WafProvider)reader.GetInt32(1),
                (WafChallengeClass)reader.GetInt32(2),
                ParseMarkerArray(reader.GetString(3)),
                ParseMarkerArray(reader.GetString(4)),
                IsBundled: false);
            userCount++;
        }

        var revision = userCount == 0
            ? asset.Revision
            : string.Create(CultureInfo.InvariantCulture, $"{asset.Revision}+u{userCount}");
        return new WafCatalogueSnapshot(asset.Version, revision, effective.Values.ToList());
    }

    /// <inheritdoc />
    public async Task<WafUserSignatureDto> UpsertUserSignatureAsync(WafUserSignatureDto signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature.Key);
        ValidateMarkerJson(signature.HeaderMarkersJson);
        ValidateMarkerJson(signature.BodyMarkersJson);

        var now = FormatTimestamp(_timeProvider.GetUtcNow());
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        long? existingId = null;
        string? createdAt = null;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT id, created_at FROM waf_user_signatures WHERE signature_key = @key;";
            probe.Parameters.Add(CreateParameter(probe, "@key", signature.Key));
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingId = reader.GetInt64(0);
                createdAt = reader.GetString(1);
            }
        }

        if (existingId is { } id)
        {
            await using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE waf_user_signatures SET provider = @provider, challenge_class = @class, " +
                "header_markers_json = @headers, body_markers_json = @body, is_enabled = @enabled, updated_at = @updated " +
                "WHERE id = @id;";
            update.Parameters.Add(CreateParameter(update, "@provider", (int)signature.Provider));
            update.Parameters.Add(CreateParameter(update, "@class", (int)signature.ChallengeClass));
            update.Parameters.Add(CreateParameter(update, "@headers", signature.HeaderMarkersJson));
            update.Parameters.Add(CreateParameter(update, "@body", signature.BodyMarkersJson));
            update.Parameters.Add(CreateParameter(update, "@enabled", signature.IsEnabled ? 1 : 0));
            update.Parameters.Add(CreateParameter(update, "@updated", now));
            update.Parameters.Add(CreateParameter(update, "@id", id));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            createdAt = now;
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO waf_user_signatures (signature_key, provider, challenge_class, header_markers_json, body_markers_json, is_enabled, created_at, updated_at) " +
                "VALUES (@key, @provider, @class, @headers, @body, @enabled, @created, @updated);";
            insert.Parameters.Add(CreateParameter(insert, "@key", signature.Key));
            insert.Parameters.Add(CreateParameter(insert, "@provider", (int)signature.Provider));
            insert.Parameters.Add(CreateParameter(insert, "@class", (int)signature.ChallengeClass));
            insert.Parameters.Add(CreateParameter(insert, "@headers", signature.HeaderMarkersJson));
            insert.Parameters.Add(CreateParameter(insert, "@body", signature.BodyMarkersJson));
            insert.Parameters.Add(CreateParameter(insert, "@enabled", signature.IsEnabled ? 1 : 0));
            insert.Parameters.Add(CreateParameter(insert, "@created", createdAt));
            insert.Parameters.Add(CreateParameter(insert, "@updated", now));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadUserSignatureAsync(connection, signature.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"User signature '{signature.Key}' was not persisted.");
    }

    /// <inheritdoc />
    public async Task<bool> DeleteUserSignatureAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM waf_user_signatures WHERE signature_key = @key;";
        command.Parameters.Add(CreateParameter(command, "@key", key));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    private static string FormatTimestamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static IReadOnlyList<string> ParseMarkerArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Marker payload must be a JSON array of strings.");
        }

        return document.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
    }

    private static void ValidateMarkerJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        _ = ParseMarkerArray(json);
    }

    private static async Task<WafUserSignatureDto?> ReadUserSignatureAsync(DbConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, signature_key, provider, challenge_class, header_markers_json, body_markers_json, is_enabled, created_at, updated_at " +
            "FROM waf_user_signatures WHERE signature_key = @key;";
        command.Parameters.Add(CreateParameter(command, "@key", key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new WafUserSignatureDto(
            reader.GetInt64(0),
            reader.GetString(1),
            (WafProvider)reader.GetInt32(2),
            (WafChallengeClass)reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6) == 1,
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)));
    }

    private BundledCatalogueAsset LoadBundledAsset()
    {
        // Bundled packaged asset: local file read only, never a runtime fetch (E2/E4).
        var json = File.ReadAllText(_bundledAssetPath);
        var asset = JsonSerializer.Deserialize<BundledCatalogueAsset>(json, AssetJsonOptions)
            ?? throw new FormatException($"Bundled WAF catalogue asset '{_bundledAssetPath}' is empty.");
        if (string.IsNullOrWhiteSpace(asset.Version) || string.IsNullOrWhiteSpace(asset.Revision) || asset.Signatures is null)
        {
            throw new FormatException($"Bundled WAF catalogue asset '{_bundledAssetPath}' lacks version/revision/signatures.");
        }

        return asset;
    }

    private static IEnumerable<WafSignature> ConvertBundled(BundledCatalogueAsset asset)
    {
        foreach (var entry in asset.Signatures)
        {
            if (!Enum.TryParse<WafProvider>(entry.Provider, ignoreCase: true, out var provider))
            {
                throw new FormatException($"Unknown WAF provider label '{entry.Provider}' in bundled asset.");
            }

            // D-021 five-class policy: challenge classes parse through the
            // single wire authority (EnumWireNames), so the hyphenated new
            // tokens "managed-challenge" / "rate-limit-soft-block" resolve
            // alongside the locked lowercase wires (interstitial, botblock).
            // Case-insensitive: existing PascalCase asset labels keep working.
            if (!EnumWireNames.TryParse<WafChallengeClass>(entry.ChallengeClass, out var challengeClass))
            {
                throw new FormatException($"Unknown challenge class '{entry.ChallengeClass}' in bundled asset.");
            }

            var headerMarkers = (entry.HeaderMarkers ?? []).ToList();
            foreach (var cookieMarker in entry.CookieMarkers ?? [])
            {
                headerMarkers.Add(cookieMarker.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)
                    ? cookieMarker
                    : "cookie:" + cookieMarker);
            }

            // Five-facet set (§2.3.1): optional title/dom/script marker arrays
            // ride the locked WafSignature.BodyMarkers list through the facet
            // prefix grammar (title:/dom:/script:), additive to the asset shape.
            var bodyMarkers = (entry.BodyMarkers ?? []).ToList();
            foreach (var titleMarker in entry.TitleMarkers ?? [])
            {
                bodyMarkers.Add(titleMarker.StartsWith("title:", StringComparison.OrdinalIgnoreCase)
                    ? titleMarker
                    : "title:" + titleMarker);
            }

            foreach (var domMarker in entry.DomMarkers ?? [])
            {
                bodyMarkers.Add(domMarker.StartsWith("dom:", StringComparison.OrdinalIgnoreCase)
                    ? domMarker
                    : "dom:" + domMarker);
            }

            foreach (var scriptMarker in entry.ScriptMarkers ?? [])
            {
                bodyMarkers.Add(scriptMarker.StartsWith("script:", StringComparison.OrdinalIgnoreCase)
                    ? scriptMarker
                    : "script:" + scriptMarker);
            }

            yield return new WafSignature(
                entry.Key,
                provider,
                challengeClass,
                headerMarkers,
                bodyMarkers,
                IsBundled: true);
        }
    }

    private sealed record BundledCatalogueAsset(
        [property: JsonRequired] string Version,
        [property: JsonRequired] string Revision,
        [property: JsonRequired] IReadOnlyList<BundledSignatureAsset> Signatures);

    private sealed record BundledSignatureAsset(
        [property: JsonRequired] string Key,
        [property: JsonRequired] string Provider,
        [property: JsonRequired] string ChallengeClass,
        IReadOnlyList<string>? HeaderMarkers,
        IReadOnlyList<string>? CookieMarkers,
        IReadOnlyList<string>? BodyMarkers,
        IReadOnlyList<string>? TitleMarkers,
        IReadOnlyList<string>? DomMarkers,
        IReadOnlyList<string>? ScriptMarkers);
}
