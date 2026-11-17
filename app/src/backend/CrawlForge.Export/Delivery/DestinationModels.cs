using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Export.Delivery;

/// <summary>
/// Parsed endpoint details of a user-configured export destination
/// (schema catalog: export_destinations.endpoint_details_json). Shapes:
/// LocalFolder <c>{"path"}</c>; Ftp/Sftp <c>{"host","port","remotePath"}</c>;
/// Webhook/Other <c>{"url"}</c>. Every endpoint is user-owned — never a vendor
/// service (FS-CRAWL-002 exclusion row 7).
/// </summary>
/// <param name="Path">Local folder path (LocalFolder).</param>
/// <param name="Host">Server host (Ftp/Sftp).</param>
/// <param name="Port">Server port (Ftp/Sftp).</param>
/// <param name="RemotePath">Remote directory (Ftp/Sftp).</param>
/// <param name="Url">Absolute http(s) URL (Webhook/Other).</param>
public sealed record DestinationEndpoint(
    string? Path,
    string? Host,
    int? Port,
    string? RemotePath,
    string? Url)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Parses endpoint details JSON.</summary>
    /// <param name="json">Stored endpoint payload.</param>
    /// <returns>The parsed endpoint.</returns>
    /// <exception cref="FormatException">The payload is not valid JSON.</exception>
    public static DestinationEndpoint Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonSerializer.Deserialize<DestinationEndpoint>(json, JsonOptions)
                ?? throw new FormatException("The endpoint details payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The endpoint details payload is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Validates the endpoint for its destination class. Remote paths are
    /// normalized and rejected when they contain parent traversal — the
    /// transfer must stay inside the user-configured target (safe path
    /// handling per the SSH.NET advisory guidance).
    /// </summary>
    /// <param name="destinationClass">Destination class.</param>
    /// <exception cref="FormatException">The endpoint is invalid for the class.</exception>
    public void Validate(ExportDestinationClass destinationClass)
    {
        switch (destinationClass)
        {
            case ExportDestinationClass.LocalFolder:
                if (string.IsNullOrWhiteSpace(Path))
                {
                    throw new FormatException("A local-folder destination requires a 'path'.");
                }

                break;

            case ExportDestinationClass.Ftp:
            case ExportDestinationClass.Sftp:
                if (string.IsNullOrWhiteSpace(Host))
                {
                    throw new FormatException("An FTP/SFTP destination requires a 'host'.");
                }

                if (RemotePath is not null && RemotePath.Split('/').Contains(".."))
                {
                    throw new FormatException("The remote path must not contain parent traversal.");
                }

                break;

            case ExportDestinationClass.Webhook:
            case ExportDestinationClass.Other:
                if (string.IsNullOrWhiteSpace(Url)
                    || !Uri.TryCreate(Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new FormatException("A webhook destination requires an absolute http(s) 'url' configured by the user.");
                }

                break;

            default:
                throw new FormatException($"Unknown destination class '{destinationClass}'.");
        }
    }
}

/// <summary>
/// User-supplied destination credentials (never seeded, never vendor-owned).
/// Stored only in protected form via <see cref="ISecretProtector"/>.
/// </summary>
/// <param name="Username">Username (FTP/SFTP).</param>
/// <param name="Password">Password (FTP/SFTP).</param>
/// <param name="Token">Bearer token (Webhook).</param>
public sealed record DestinationCredentials(string? Username, string? Password, string? Token)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Protects credentials for storage (FS-CRAWL-001 §9.1 mandatory encryption
    /// at rest through the locked secret protector).
    /// </summary>
    /// <param name="protector">Locked secret protector.</param>
    /// <param name="credentials">Plaintext credentials.</param>
    /// <returns>The protected payload for encrypted_credentials_json.</returns>
    public static string Protect(ISecretProtector protector, DestinationCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(credentials);
        return protector.Protect(JsonSerializer.Serialize(credentials, JsonOptions));
    }

    /// <summary>
    /// Unprotects stored credentials. Returns null when no payload exists.
    /// </summary>
    /// <param name="protector">Locked secret protector.</param>
    /// <param name="protectedJson">Protected payload, or null.</param>
    /// <returns>The plaintext credentials, or null.</returns>
    public static DestinationCredentials? Unprotect(ISecretProtector protector, string? protectedJson)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (string.IsNullOrWhiteSpace(protectedJson))
        {
            return null;
        }

        var json = protector.Unprotect(protectedJson);
        return JsonSerializer.Deserialize<DestinationCredentials>(json, JsonOptions);
    }
}

/// <summary>
/// One entry of the in-memory delivery ledger. Restart semantics: the ledger
/// is runtime state — it is lost on process restart; exported files and the
/// durable destination rows survive (documented handoff, WP-05-03).
/// </summary>
/// <param name="ExportJobId">Delivered export job.</param>
/// <param name="DestinationId">Target destination.</param>
/// <param name="State">delivering, delivered, retrying, or failed.</param>
/// <param name="Attempt">Last attempt number (1-based).</param>
/// <param name="Message">Outcome detail.</param>
/// <param name="UpdatedAt">Last transition timestamp (UTC).</param>
public sealed record DeliveryLedgerEntry(
    long ExportJobId,
    long DestinationId,
    string State,
    int Attempt,
    string? Message,
    DateTimeOffset UpdatedAt);
