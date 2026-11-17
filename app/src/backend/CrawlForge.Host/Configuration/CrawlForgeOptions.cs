namespace CrawlForge.Host.Configuration;

/// <summary>
/// Local backend options bound from the "CrawlForge" configuration section.
/// All values are local; nothing is fetched from any service (clean-room guardrail #5).
/// </summary>
public sealed class CrawlForgeOptions
{
    public const string SectionName = "CrawlForge";

    /// <summary>
    /// Configured default loopback port. 0 (the default) asks the operating system for a
    /// free ephemeral port. A positive value is attempted first; if occupied, the backend
    /// falls back to an OS-assigned ephemeral port (process-model spec §2.2).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Optional override for the per-user data directory. When null, the backend uses
    /// %APPDATA%\CrawlForge (platform equivalents on macOS/Linux).
    /// </summary>
    public string? DataDirectory { get; set; }
}
