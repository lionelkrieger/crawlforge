using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Export.Delivery;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// User-configured export destinations (FS-CRAWL-001 §10.2/§8.5).
/// Destinations are user-owned, off by default, never vendor services.
/// Credentials are protected at rest through the locked secret protector and
/// never returned in clear; the wire carries only a mask indicator.
/// Endpoint payloads use the locked <see cref="DestinationEndpoint"/> shapes
/// consumed by the delivery engine. Creation/update timestamps live in the
/// API-surface metadata sidecar (the locked schema has no timestamp columns).
/// </summary>
[Route("api/destinations")]
public sealed class DestinationsController : ApiControllerBase
{
    private const string SecretMask = "********";

    private readonly IReadRepository<ExportDestinationDto> _reads;
    private readonly IWriteRepository<ExportDestinationDto> _writes;
    private readonly ISecretProtector _protector;
    private readonly IExportDestinationDelivery _delivery;
    private readonly IExportEngine _exports;
    private readonly ApiSurfaceMetadataStore _metadata;

    /// <summary>Creates the controller.</summary>
    /// <param name="reads">Destination read repository.</param>
    /// <param name="writes">Destination write repository.</param>
    /// <param name="protector">Locked secret protector.</param>
    /// <param name="delivery">Locked delivery service.</param>
    /// <param name="exports">Locked export engine.</param>
    /// <param name="settingsReads">Settings read repository (metadata sidecar).</param>
    /// <param name="settingsWrites">Settings write repository (metadata sidecar).</param>
    public DestinationsController(
        IReadRepository<ExportDestinationDto> reads,
        IWriteRepository<ExportDestinationDto> writes,
        ISecretProtector protector,
        IExportDestinationDelivery delivery,
        IExportEngine exports,
        IReadRepository<ApplicationSettingsDto> settingsReads,
        IWriteRepository<ApplicationSettingsDto> settingsWrites)
    {
        _reads = reads;
        _writes = writes;
        _protector = protector;
        _delivery = delivery;
        _exports = exports;
        _metadata = new ApiSurfaceMetadataStore(settingsReads, settingsWrites);
    }

    /// <summary>Lists destinations with pagination.</summary>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged destinations.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListAsync(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBindSort(sort, order, ["id", "name"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var root = await _metadata.LoadAsync(cancellationToken);
        var page = await _reads.ListAsync(BindListQuery(offset, limit, sortBy, descending), cancellationToken);
        var items = page.Items.Select(d => ToWire(d, root)).ToList();
        return OkEnvelope(items, PaginationOf(page));
    }

    /// <summary>Creates a destination.</summary>
    /// <param name="request">Destination fields.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created destination.</returns>
    [HttpPost]
    public async Task<ObjectResult> CreateAsync([FromBody] DestinationCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationError("A destination name is required.", [new ApiFieldError("name", "Provide a name.")]);
        }

        if (!TryParseKind(request.Kind, out var kind))
        {
            return ValidationError($"Unknown destination kind '{request.Kind}'.", [new ApiFieldError("kind", "Use local-folder, ftp, sftp, webhook, or other.")]);
        }

        if (!TryBuildEndpoint(kind, request.Target, out var endpoint, out var endpointError))
        {
            return ValidationError(endpointError!, [new ApiFieldError("target", "Provide a valid target for the destination kind.")]);
        }

        var credentials = BuildCredentials(kind, request.Username, request.Secret);
        var now = DateTimeOffset.UtcNow;
        var created = new ExportDestinationDto(
            0,
            request.Name.Trim(),
            kind,
            JsonSerializer.Serialize(endpoint, ApiJsonOptions.Default),
            credentials is null ? null : DestinationCredentials.Protect(_protector, credentials),
            IsEnabled: true);
        var id = (await _writes.CreateAsync(created, cancellationToken)).Id;

        var root = await _metadata.LoadAsync(cancellationToken);
        ApiSurfaceMetadataStore.WriteEntry(root, "destinations", id.ToString(), new ApiSurfaceMetadataStore.Entry(null, now, now));
        await _metadata.SaveAsync(root, cancellationToken);

        return CreatedEnvelope(ToWire(created with { Id = id }, root));
    }

    /// <summary>Updates a destination (subset merge).</summary>
    /// <param name="id">Destination identifier.</param>
    /// <param name="request">Fields to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated destination.</returns>
    [HttpPut("{id:long}")]
    public async Task<ObjectResult> UpdateAsync(long id, [FromBody] DestinationUpdateRequest request, CancellationToken cancellationToken)
    {
        var existing = await _reads.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFoundError($"Destination {id} does not exist.");
        }

        var kind = existing.Class;
        if (request.Kind is not null)
        {
            if (!TryParseKind(request.Kind, out kind))
            {
                return ValidationError($"Unknown destination kind '{request.Kind}'.", [new ApiFieldError("kind", "Use local-folder, ftp, sftp, webhook, or other.")]);
            }
        }

        var endpointJson = existing.EndpointDetailsJson;
        if (request.Target is not null || request.Kind is not null)
        {
            var target = request.Target ?? ProjectTarget(existing);
            if (!TryBuildEndpoint(kind, target, out var endpoint, out var endpointError))
            {
                return ValidationError(endpointError!, [new ApiFieldError("target", "Provide a valid target for the destination kind.")]);
            }

            endpointJson = JsonSerializer.Serialize(endpoint, ApiJsonOptions.Default);
        }

        var storedCredentials = DestinationCredentials.Unprotect(_protector, existing.EncryptedCredentialsJson);
        var username = request.Username is null && request.Secret is null
            ? storedCredentials?.Username
            : request.Username;
        string? secret;
        if (request.Secret is null)
        {
            secret = kind == ExportDestinationClass.Webhook ? storedCredentials?.Token : storedCredentials?.Password;
        }
        else
        {
            secret = request.Secret.Length == 0 ? null : request.Secret;
        }

        var credentials = BuildCredentials(kind, username, secret);
        var updated = existing with
        {
            Name = request.Name is null ? existing.Name : request.Name.Trim(),
            Class = kind,
            EndpointDetailsJson = endpointJson,
            EncryptedCredentialsJson = credentials is null ? null : DestinationCredentials.Protect(_protector, credentials),
            IsEnabled = request.Enabled ?? existing.IsEnabled,
        };
        if (string.IsNullOrWhiteSpace(updated.Name))
        {
            return ValidationError("A destination name is required.", [new ApiFieldError("name", "Provide a name.")]);
        }

        await _writes.UpdateAsync(updated, cancellationToken);

        var root = await _metadata.LoadAsync(cancellationToken);
        var entry = ApiSurfaceMetadataStore.ReadEntry(root, "destinations", id.ToString());
        var now = DateTimeOffset.UtcNow;
        ApiSurfaceMetadataStore.WriteEntry(root, "destinations", id.ToString(), new ApiSurfaceMetadataStore.Entry(null, entry?.CreatedAt ?? now, now));
        await _metadata.SaveAsync(root, cancellationToken);

        return OkEnvelope(ToWire(updated, root));
    }

    /// <summary>Deletes a destination.</summary>
    /// <param name="id">Destination identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion confirmation.</returns>
    [HttpDelete("{id:long}")]
    public async Task<ObjectResult> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Destination {id} does not exist.");
        }

        await _writes.DeleteAsync(id, cancellationToken);
        var root = await _metadata.LoadAsync(cancellationToken);
        ApiSurfaceMetadataStore.RemoveEntry(root, "destinations", id.ToString());
        await _metadata.SaveAsync(root, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(id.ToString(), true));
    }

    /// <summary>Tests reachability and credentials of a destination.</summary>
    /// <param name="id">Destination identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Test verdict.</returns>
    [HttpPost("{id:long}/test")]
    public async Task<ObjectResult> TestAsync(long id, CancellationToken cancellationToken)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Destination {id} does not exist.");
        }

        var verdict = await _delivery.TestAsync(id, cancellationToken);
        return OkEnvelope(new DestinationTestResponse(verdict.IsReachable, verdict.CredentialsValid, verdict.Message));
    }

    /// <summary>Delivers a completed export to this destination (additive route).</summary>
    /// <param name="id">Destination identifier.</param>
    /// <param name="request">Export job reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Delivery verdict.</returns>
    [HttpPost("{id:long}/deliver")]
    public async Task<ObjectResult> DeliverAsync(long id, [FromBody] DestinationDeliverRequest request, CancellationToken cancellationToken)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Destination {id} does not exist.");
        }

        if (!long.TryParse(request.ExportId, out var exportId) || await _exports.GetExportAsync(exportId, cancellationToken) is null)
        {
            return NotFoundError($"Export job {request.ExportId} does not exist.");
        }

        try
        {
            await _delivery.DeliverAsync(exportId, id, cancellationToken);
            return OkEnvelope(new DestinationDeliverResponse(request.ExportId, id.ToString(), "delivered", null));
        }
        catch (InvalidOperationException ex)
        {
            return ConflictError(ex.Message);
        }
    }

    private DestinationWire ToWire(ExportDestinationDto destination, JsonObject root)
    {
        var credentials = DestinationCredentials.Unprotect(_protector, destination.EncryptedCredentialsJson);
        var entry = ApiSurfaceMetadataStore.ReadEntry(root, "destinations", destination.Id.ToString());
        var fallback = DateTimeOffset.UnixEpoch;
        return new DestinationWire(
            destination.Id.ToString(),
            destination.Name,
            KindWire(destination.Class),
            ProjectTarget(destination),
            credentials?.Username,
            destination.EncryptedCredentialsJson is null ? null : SecretMask,
            entry?.CreatedAt ?? fallback,
            entry?.UpdatedAt ?? fallback,
            destination.IsEnabled);
    }

    private static string ProjectTarget(ExportDestinationDto destination)
    {
        var endpoint = DestinationEndpoint.Parse(destination.EndpointDetailsJson);
        return destination.Class switch
        {
            ExportDestinationClass.LocalFolder => endpoint.Path ?? string.Empty,
            ExportDestinationClass.Ftp or ExportDestinationClass.Sftp =>
                $"{endpoint.Host}:{endpoint.Port ?? (destination.Class == ExportDestinationClass.Ftp ? 21 : 22)}{endpoint.RemotePath}",
            _ => endpoint.Url ?? string.Empty,
        };
    }

    private static bool TryBuildEndpoint(ExportDestinationClass kind, string? target, out DestinationEndpoint endpoint, out string? error)
    {
        endpoint = new DestinationEndpoint(null, null, null, null, null);
        error = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "A destination target is required.";
            return false;
        }

        try
        {
            endpoint = kind switch
            {
                ExportDestinationClass.LocalFolder => new DestinationEndpoint(target, null, null, null, null),
                ExportDestinationClass.Ftp or ExportDestinationClass.Sftp => ParseTransferTarget(kind, target),
                _ => new DestinationEndpoint(null, null, null, null, target),
            };
            endpoint.Validate(kind);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static DestinationEndpoint ParseTransferTarget(ExportDestinationClass kind, string target)
    {
        var defaultPort = kind == ExportDestinationClass.Ftp ? 21 : 22;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "ftp" or "sftp")
        {
            var remotePath = uri.AbsolutePath is "/" or "" ? null : uri.AbsolutePath;
            return new DestinationEndpoint(null, uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port, remotePath, null);
        }

        var pathIndex = target.IndexOf('/');
        var authority = pathIndex < 0 ? target : target[..pathIndex];
        var path = pathIndex < 0 ? null : target[pathIndex..];
        var portSeparator = authority.LastIndexOf(':');
        if (portSeparator > 0 && int.TryParse(authority[(portSeparator + 1)..], out var port))
        {
            return new DestinationEndpoint(null, authority[..portSeparator], port, path, null);
        }

        return new DestinationEndpoint(null, authority, defaultPort, path, null);
    }

    private static DestinationCredentials? BuildCredentials(ExportDestinationClass kind, string? username, string? secret)
    {
        if (username is null && secret is null)
        {
            return null;
        }

        return kind == ExportDestinationClass.Webhook
            ? new DestinationCredentials(null, null, secret)
            : new DestinationCredentials(username, secret, null);
    }

    private static bool TryParseKind(string? wire, out ExportDestinationClass kind)
    {
        switch (wire)
        {
            case "local-folder":
                kind = ExportDestinationClass.LocalFolder;
                return true;
            case "ftp":
                kind = ExportDestinationClass.Ftp;
                return true;
            case "sftp":
                kind = ExportDestinationClass.Sftp;
                return true;
            case "webhook":
                kind = ExportDestinationClass.Webhook;
                return true;
            case "other":
                kind = ExportDestinationClass.Other;
                return true;
            default:
                kind = ExportDestinationClass.LocalFolder;
                return false;
        }
    }

    private static string KindWire(ExportDestinationClass kind) => kind switch
    {
        ExportDestinationClass.LocalFolder => "local-folder",
        ExportDestinationClass.Ftp => "ftp",
        ExportDestinationClass.Sftp => "sftp",
        ExportDestinationClass.Webhook => "webhook",
        _ => "other",
    };
}
