using System.Security.Cryptography;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Local plugin registry (FS-CRAWL-002 §6.7). Plugins are local user-authored
/// artifacts only: no remote registry, no auto-update, no remote code loading.
/// Grants are read-only data + UI contributions; the wire key "commands" has
/// no locked grant flag and is rejected. Rescan discovers plugin.json
/// manifests under the local plugins directory beside the application and
/// records integrity hashes; the Host may replace this minimal discovery with
/// a richer loader (handoff note).
/// </summary>
[Route("api/plugins")]
public sealed class PluginsController : ApiControllerBase
{
    private readonly IReadRepository<UiPluginDto> _reads;
    private readonly IWriteRepository<UiPluginDto> _writes;

    /// <summary>Creates the controller.</summary>
    /// <param name="reads">Plugin read repository.</param>
    /// <param name="writes">Plugin write repository.</param>
    public PluginsController(IReadRepository<UiPluginDto> reads, IWriteRepository<UiPluginDto> writes)
    {
        _reads = reads;
        _writes = writes;
    }

    /// <summary>Local plugin discovery root (beside the application).</summary>
    public static string PluginRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Lists the plugin registry.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All registry entries.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListAsync(CancellationToken cancellationToken)
    {
        var page = await _reads.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        return OkEnvelope(page.Items.Select(ToWire).ToList());
    }

    /// <summary>Updates enablement and permission grants.</summary>
    /// <param name="id">Plugin identifier.</param>
    /// <param name="request">Enablement and grant changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated entry.</returns>
    [HttpPut("{id:long}")]
    public async Task<ObjectResult> UpdateAsync(long id, [FromBody] PluginUpdateRequest request, CancellationToken cancellationToken)
    {
        var existing = await _reads.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFoundError($"Plugin {id} does not exist.");
        }

        var grants = existing.Grants;
        if (request.GrantPermissions is not null)
        {
            if (!TryParsePermissions(request.GrantPermissions, out var granted, out var grantError))
            {
                return ValidationError(grantError!, [new ApiFieldError("grantPermissions", "Use read-data or ui-contributions.")]);
            }

            grants |= granted;
        }

        if (request.RevokePermissions is not null)
        {
            if (!TryParsePermissions(request.RevokePermissions, out var revoked, out var revokeError))
            {
                return ValidationError(revokeError!, [new ApiFieldError("revokePermissions", "Use read-data or ui-contributions.")]);
            }

            grants &= ~revoked;
        }

        var updated = existing with
        {
            IsEnabled = request.Enabled ?? existing.IsEnabled,
            Grants = grants,
        };
        await _writes.UpdateAsync(updated, cancellationToken);
        return OkEnvelope(ToWire(updated));
    }

    /// <summary>Rescans the local plugin directory.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scan verdict and effective registry.</returns>
    [HttpPost("rescan")]
    public async Task<ObjectResult> RescanAsync(CancellationToken cancellationToken)
    {
        var root = PluginRoot;
        var registry = await _reads.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        var byName = registry.Items.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var removed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifestPath = Path.Combine(directory, "plugin.json");
                if (!System.IO.File.Exists(manifestPath))
                {
                    continue;
                }

                if (!TryReadManifest(manifestPath, out var name, out var version, out var permissions))
                {
                    continue;
                }

                seen.Add(name);
                var hash = Convert.ToHexString(SHA256.HashData(await System.IO.File.ReadAllBytesAsync(manifestPath, cancellationToken)));
                if (byName.TryGetValue(name, out var existing))
                {
                    if (existing.IntegrityHash != hash || existing.DirectoryPath != directory)
                    {
                        await _writes.UpdateAsync(existing with
                        {
                            DirectoryPath = directory,
                            Version = version,
                            IntegrityHash = hash,
                            LoadState = "pending",
                        }, cancellationToken);
                    }
                }
                else
                {
                    await _writes.CreateAsync(new UiPluginDto(
                        0, name, directory, version, true, permissions, hash, "pending", DateTimeOffset.UtcNow), cancellationToken);
                    added++;
                }
            }
        }

        foreach (var entry in registry.Items)
        {
            var underRoot = entry.DirectoryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            if (underRoot && (!seen.Contains(entry.Name) || !Directory.Exists(entry.DirectoryPath)))
            {
                await _writes.DeleteAsync(entry.Id, cancellationToken);
                removed++;
            }
        }

        var effective = await _reads.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        return OkEnvelope(new PluginRescanResponse(added, removed, effective.Items.Select(ToWire).ToList()));
    }

    /// <summary>Removes a plugin registry entry.</summary>
    /// <param name="id">Plugin identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion confirmation.</returns>
    [HttpDelete("{id:long}")]
    public async Task<ObjectResult> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Plugin {id} does not exist.");
        }

        await _writes.DeleteAsync(id, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(id.ToString(), true));
    }

    private static PluginRegistryEntryWire ToWire(UiPluginDto plugin) =>
        new(
            plugin.Id.ToString(),
            plugin.Name,
            plugin.Version,
            null,
            plugin.DirectoryPath,
            plugin.IsEnabled ? plugin.LoadState ?? "pending" : "disabled",
            plugin.IsEnabled,
            PermissionWire(plugin.Grants),
            null,
            plugin.RegisteredAt);

    private static IReadOnlyList<string> PermissionWire(PluginPermission grants)
    {
        var keys = new List<string>();
        if (grants.HasFlag(PluginPermission.ReadData))
        {
            keys.Add("read-data");
        }

        if (grants.HasFlag(PluginPermission.ContributeUi))
        {
            keys.Add("ui-contributions");
        }

        return keys;
    }

    private static bool TryParsePermissions(IReadOnlyList<string> keys, out PluginPermission permissions, out string? error)
    {
        permissions = PluginPermission.None;
        error = null;
        foreach (var key in keys)
        {
            switch (key)
            {
                case "read-data":
                    permissions |= PluginPermission.ReadData;
                    break;
                case "ui-contributions":
                    permissions |= PluginPermission.ContributeUi;
                    break;
                default:
                    error = $"Permission '{key}' is not grantable by the local registry.";
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadManifest(string manifestPath, out string name, out string version, out PluginPermission permissions)
    {
        name = string.Empty;
        version = string.Empty;
        permissions = PluginPermission.None;
        try
        {
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));
            var root = document.RootElement;
            name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
            if (name.Length == 0 || version.Length == 0)
            {
                return false;
            }

            if (root.TryGetProperty("permissions", out var permissionsElement) && permissionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in permissionsElement.EnumerateArray())
                {
                    switch (entry.GetString())
                    {
                        case "read-data":
                            permissions |= PluginPermission.ReadData;
                            break;
                        case "ui-contributions":
                            permissions |= PluginPermission.ContributeUi;
                            break;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }
}
