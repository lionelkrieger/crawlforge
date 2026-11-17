using System.Text.Json;
using CrawlForge.Host.Configuration;

namespace CrawlForge.Host.Handshake;

/// <summary>
/// Spawn-time port handshake (process-model spec §2.2): the backend writes the actual
/// loopback port to a descriptor in the per-user data directory so the shell can
/// discover it. Local file only; contains no secrets and no machine identity.
/// The Security workpiece (wave 0C) hardens this channel later.
/// </summary>
public sealed record PortDescriptor(int SchemaVersion, int Port, string BaseAddress, DateTimeOffset StartedAtUtc)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task WriteAsync(CrawlForgePaths paths, int port, CancellationToken cancellationToken)
    {
        var descriptor = new PortDescriptor(1, port, $"http://127.0.0.1:{port}", DateTimeOffset.UtcNow);
        await using var stream = File.Create(paths.PortDescriptorPath);
        await JsonSerializer.SerializeAsync(stream, descriptor, SerializerOptions, cancellationToken);
    }
}
