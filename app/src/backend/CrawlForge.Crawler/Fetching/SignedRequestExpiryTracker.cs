namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Expiry state of user-configured signing material (FS-CRAWL-001 §5.10: the
/// engine tracks signature expiry and warns before and after it lapses).
/// </summary>
public enum SignedRequestExpiryState
{
    /// <summary>No expiry is registered for the key.</summary>
    Unknown = 0,

    /// <summary>The material is valid and outside the warning window.</summary>
    Active = 1,

    /// <summary>The material is valid but inside the pre-expiry warning window.</summary>
    ExpiringSoon = 2,

    /// <summary>The material has lapsed and must be renewed by the user.</summary>
    Expired = 3,
}

/// <summary>
/// Tracks expiry of user-supplied signing material (§5.10). The locked
/// <c>SignedRequestSecret</c> contract carries no expiry field, so expiry is
/// registered here alongside the key identifier; the settings and hub surfaces
/// consume the state to warn before and after lapse. Purely local state — no
/// remote issuance, rotation, or revocation exists (exclusion row 4).
/// </summary>
public sealed class SignedRequestExpiryTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExpiryEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) the expiry of a signing key.</summary>
    /// <param name="keyId">Signing key identifier.</param>
    /// <param name="expiresAtUtc">Expiry timestamp (UTC).</param>
    /// <param name="warningWindow">
    /// Pre-expiry warning window; defaults to 24 hours. Must be non-negative.
    /// </param>
    /// <exception cref="ArgumentException">The key identifier is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The warning window is negative.</exception>
    public void Register(string keyId, DateTimeOffset expiresAtUtc, TimeSpan? warningWindow = null)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("The key identifier must be non-empty.", nameof(keyId));
        }

        TimeSpan window = warningWindow ?? TimeSpan.FromHours(24);
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(warningWindow), window, "The warning window cannot be negative.");
        }

        _entries[keyId] = new ExpiryEntry(expiresAtUtc, window);
    }

    /// <summary>Removes the expiry registration of a signing key.</summary>
    /// <param name="keyId">Signing key identifier.</param>
    public void Unregister(string keyId)
    {
        if (!string.IsNullOrWhiteSpace(keyId))
        {
            _entries.TryRemove(keyId, out _);
        }
    }

    /// <summary>Evaluates the expiry state of a signing key at a point in time.</summary>
    /// <param name="keyId">Signing key identifier.</param>
    /// <param name="now">Evaluation timestamp (UTC).</param>
    /// <returns>The expiry state; Unknown when no expiry is registered.</returns>
    public SignedRequestExpiryState GetState(string keyId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(keyId) || !_entries.TryGetValue(keyId, out ExpiryEntry? entry))
        {
            return SignedRequestExpiryState.Unknown;
        }

        if (now >= entry.ExpiresAtUtc)
        {
            return SignedRequestExpiryState.Expired;
        }

        return now >= entry.ExpiresAtUtc - entry.WarningWindow
            ? SignedRequestExpiryState.ExpiringSoon
            : SignedRequestExpiryState.Active;
    }

    private sealed record ExpiryEntry(DateTimeOffset ExpiresAtUtc, TimeSpan WarningWindow);
}