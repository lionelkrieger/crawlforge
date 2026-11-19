using System.Collections.Concurrent;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-09-11 (G05): the backend-lifecycle producer emits the frozen
/// {kind:"ready"|"shutdown"} payload — ready after startup, shutdown BEFORE
/// the lifecycle drain (hub still up). Live producer proof over an
/// in-process backend + recording hub channel (the conformance harness
/// connects its hub after startup and captures the shutdown notice at
/// fixture dispose, so its mid-run lock is producer-side).
/// </summary>
public sealed class BackendLifecycleTests
{
    [Fact]
    public async Task Backend_EmitsReadyThenShutdown_WithFrozenKindShape()
    {
        var channel = new RecordingHubChannel();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"));
        var backend = await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"],
            builder => builder.Services.AddSingleton<IHubClientChannel>(channel));
        try
        {
            var ready = channel.Events.Where(e => e.Name == HubEventCatalog.BackendLifecycle).ToList();
            Assert.True(ready.Count > 0, "no backend-lifecycle ready event emitted at startup");
            using (var payload = JsonDocument.Parse(ready[0].PayloadJson))
            {
                Assert.Equal("ready", payload.RootElement.GetProperty("kind").GetString());
            }

            await backend.StopAsync();

            var kinds = channel.Events
                .Where(e => e.Name == HubEventCatalog.BackendLifecycle)
                .Select(e => JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("kind").GetString())
                .ToList();
            Assert.Equal(new[] { "ready", "shutdown" }, kinds);
        }
        finally
        {
            await backend.StopAsync();
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private sealed class RecordingHubChannel : IHubClientChannel
    {
        public ConcurrentQueue<(string Name, string PayloadJson)> Events { get; } = new();

        public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
