using CrawlForge.Host;

// CrawlForge backend entry point (WP-00-01 skeleton).
// The Electron shell spawns this process and discovers the actual loopback port
// through the local port-descriptor handshake (process-model spec §2.2 / §2.3).
await using var backend = await CrawlForgeBackend.StartAsync(args);
await backend.WaitForShutdownAsync();
