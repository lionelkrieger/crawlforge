using CrawlForge.Api.Hubs;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// In-memory transport double: records every send and group membership so
/// tests can assert routing, coalescing, and cadence deterministically.
/// </summary>
public sealed class FakeHubChannel : IHubClientChannel
{
    private readonly object _gate = new();

    /// <summary>Recorded sends in dispatch order.</summary>
    public List<SentEvent> Sends { get; } = [];

    /// <summary>Group memberships by connection id.</summary>
    public Dictionary<string, HashSet<string>> Groups { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Sends.Add(new SentEvent(Target: null, eventName, payloadJson));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Sends.Add(new SentEvent(groupName, eventName, payloadJson));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!Groups.TryGetValue(connectionId, out var groups))
            {
                groups = new HashSet<string>(StringComparer.Ordinal);
                Groups[connectionId] = groups;
            }

            groups.Add(groupName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (Groups.TryGetValue(connectionId, out var groups))
            {
                groups.Remove(groupName);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Snapshot of recorded sends.</summary>
    public IReadOnlyList<SentEvent> Snapshot()
    {
        lock (_gate)
        {
            return Sends.ToList();
        }
    }
}

/// <summary>One recorded send; null target means broadcast.</summary>
/// <param name="Target">Group name, or null for all clients.</param>
/// <param name="EventName">Hub event name.</param>
/// <param name="PayloadJson">JSON payload.</param>
public sealed record SentEvent(string? Target, string EventName, string PayloadJson);
