using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;

namespace CrawlForge.Crawler.Tests.Waf.Support;

/// <summary>
/// Test-local catalogue double with full control over signature markers and
/// weights, used to prove the corroboration guard and confidence threshold
/// independently of the bundled asset.
/// </summary>
public sealed class InMemoryWafCatalogue : IWafSignatureCatalogue
{
    private readonly List<WafSignature> _signatures;

    /// <summary>Creates a catalogue double holding the given signatures.</summary>
    /// <param name="signatures">Effective signatures.</param>
    public InMemoryWafCatalogue(params WafSignature[] signatures)
    {
        _signatures = signatures.ToList();
    }

    /// <inheritdoc />
    public Task<WafCatalogueSnapshot> GetEffectiveCatalogueAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WafCatalogueSnapshot("test-version", "test-revision", _signatures.ToList()));

    /// <inheritdoc />
    public Task<WafUserSignatureDto> UpsertUserSignatureAsync(WafUserSignatureDto signature, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The in-memory double does not persist user entries.");

    /// <inheritdoc />
    public Task<bool> DeleteUserSignatureAsync(string key, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The in-memory double does not persist user entries.");
}
