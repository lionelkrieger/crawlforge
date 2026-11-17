using System.Net;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// Verification tiers of FS-CRAWL-002 §11.3. Unverified bots are always labeled
/// (criterion #44) — a UA match alone never implies trust.
/// </summary>
public static class BotVerificationTiers
{
    /// <summary>User-agent pattern match only.</summary>
    public const string Claimed = "claimed";

    /// <summary>Offline match against bundled published CIDR ranges.</summary>
    public const string CidrVerified = "cidr-verified";

    /// <summary>User-triggered forward-confirmed reverse DNS.</summary>
    public const string DnsVerified = "dns-verified";

    /// <summary>Non-bot hits carry no verification tier.</summary>
    public const string None = "none";

    /// <summary>Derives the tier of a classification (DNS tier is assigned only by the explicit verify tool).</summary>
    /// <param name="classification">The classification.</param>
    /// <returns>The tier label.</returns>
    public static string FromClassification(BotClassification classification) =>
        !classification.IsBot ? None : classification.CidrMatched ? CidrVerified : Claimed;
}

/// <summary>
/// Local DNS resolver abstraction so the verification path is testable fully
/// offline. The system implementation is used ONLY from the explicit
/// user-triggered verify tool (E8: user-triggered, off by default, per-import
/// consent, locally logged). Import-time classification never resolves DNS.
/// </summary>
public interface IDnsResolver
{
    /// <summary>Resolves a hostname to addresses (forward lookup).</summary>
    /// <param name="host">Hostname to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved addresses.</returns>
    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>Resolves an address to its hostname (reverse lookup).</summary>
    /// <param name="address">Address to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hostname, or null when unavailable.</returns>
    Task<string?> GetHostNameAsync(IPAddress address, CancellationToken cancellationToken = default);
}

/// <summary>
/// System DNS resolver (the single optional network action, E8). Constructed
/// only by the user-triggered verification tool — never by the import path.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <inheritdoc />
    public async Task<string?> GetHostNameAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        // IP-literal string form performs the reverse lookup.
        var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken);
        return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
    }
}

/// <summary>
/// Bot classifier and verifier (FS-CRAWL-002 §11.3). Classification is 100%
/// offline: bundled UA patterns + bundled CIDR ranges only. Verification tiers:
/// claimed (UA match), cidr-verified (offline range match), dns-verified (only
/// via <see cref="VerifyDnsAsync"/>, which must be invoked exclusively from an
/// explicit user action with per-import consent). Unverified bots are labeled,
/// never silently trusted (criterion #44).
/// </summary>
public sealed class BotVerifier : IBotVerifier
{
    /// <summary>Family label for browser traffic without bot evidence.</summary>
    public const string HumanLabel = "human";

    /// <summary>Family label for hits without a user-agent.</summary>
    public const string UnknownLabel = "unknown";

    private readonly BotCatalogue _catalogue;
    private readonly IDnsResolver _resolver;

    /// <summary>Creates the verifier.</summary>
    /// <param name="catalogue">The bundled bot catalogue.</param>
    /// <param name="resolver">DNS resolver for the explicit verify path; null uses the system resolver.</param>
    public BotVerifier(BotCatalogue catalogue, IDnsResolver? resolver = null)
    {
        _catalogue = catalogue;
        _resolver = resolver ?? new SystemDnsResolver();
    }

    /// <inheritdoc />
    public BotClassification Classify(string? clientIp, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new BotClassification(false, null, UnknownLabel, false);
        }

        var family = _catalogue.MatchFamily(userAgent);
        if (family is null)
        {
            return new BotClassification(false, null, HumanLabel, false);
        }

        var cidrMatched = IPAddress.TryParse(clientIp, out var address) && family.MatchesCidr(address);
        return new BotClassification(true, family.Name, family.Name, cidrMatched);
    }

    /// <inheritdoc />
    public async Task<DnsVerificationResult> VerifyDnsAsync(string clientIp, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(clientIp, out var address))
        {
            return new DnsVerificationResult(clientIp, false, null);
        }

        try
        {
            // Reverse lookup, then forward confirmation of the same address.
            var hostname = await _resolver.GetHostNameAsync(address, cancellationToken);
            if (string.IsNullOrWhiteSpace(hostname))
            {
                return new DnsVerificationResult(clientIp, false, null);
            }

            var forward = await _resolver.GetHostAddressesAsync(hostname, cancellationToken);
            var forwardConfirmed = forward.Any(a => a.Equals(address));
            var suffixMatched = _catalogue.Families.Any(family =>
                family.DnsSuffixes.Any(suffix => hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

            return new DnsVerificationResult(clientIp, forwardConfirmed && suffixMatched, hostname);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            // Tolerant posture: resolution failures are unverified, never fatal.
            return new DnsVerificationResult(clientIp, false, null);
        }
    }
}
