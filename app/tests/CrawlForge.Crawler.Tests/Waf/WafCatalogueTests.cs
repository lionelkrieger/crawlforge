using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// Effective catalogue behavior: the bundled local asset (no runtime fetch),
/// user-local entries persisted in waf_user_signatures, key shadowing, and
/// bundled-default restoration on delete.
/// </summary>
public class WafCatalogueTests : IAsyncLifetime
{
    private readonly WafTestDatabase _db = new();

    /// <summary>Creates the per-test fixture.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public Task InitializeAsync() => _db.InitializeAsync();

    /// <summary>Tears down the per-test fixture.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>The bundled asset loads locally with version, revision, and all six provider labels.</summary>
    [Fact]
    public async Task BundledCatalogueLoadsAllSixProviders()
    {
        Assert.True(File.Exists(WafTestDatabase.BundledAssetPath));
        var snapshot = await _db.Catalogue.GetEffectiveCatalogueAsync();
        Assert.Equal("2026.1", snapshot.BundledVersion);
        Assert.Equal("r1", snapshot.Revision);
        Assert.All(snapshot.Signatures, s => Assert.True(s.IsBundled));
        var providers = snapshot.Signatures.Select(s => s.Provider).Distinct().ToList();
        Assert.Contains(WafProvider.Cloudflare, providers);
        Assert.Contains(WafProvider.DataDome, providers);
        Assert.Contains(WafProvider.Akamai, providers);
        Assert.Contains(WafProvider.Imperva, providers);
        Assert.Contains(WafProvider.PerimeterX, providers);
        Assert.Contains(WafProvider.Other, providers);
    }

    /// <summary>A user entry with a bundled key shadows the bundled default and bumps the revision.</summary>
    [Fact]
    public async Task UserSignatureShadowsBundledKey()
    {
        var user = NewUserSignature("cloudflare-interstitial", WafProvider.Other, WafChallengeClass.BotBlock);
        await _db.Catalogue.UpsertUserSignatureAsync(user);

        var snapshot = await _db.Catalogue.GetEffectiveCatalogueAsync();
        var shadowed = snapshot.Signatures.Single(s => s.Key == "cloudflare-interstitial");
        Assert.False(shadowed.IsBundled);
        Assert.Equal(WafProvider.Other, shadowed.Provider);
        Assert.Equal(WafChallengeClass.BotBlock, shadowed.ChallengeClass);
        Assert.Equal("r1+u1", snapshot.Revision);
    }

    /// <summary>Deleting a user entry restores the bundled default with the same key.</summary>
    [Fact]
    public async Task DeleteRestoresBundledDefault()
    {
        await _db.Catalogue.UpsertUserSignatureAsync(NewUserSignature("cloudflare-interstitial", WafProvider.Other, WafChallengeClass.BotBlock));
        var deleted = await _db.Catalogue.DeleteUserSignatureAsync("cloudflare-interstitial");
        Assert.True(deleted);

        var snapshot = await _db.Catalogue.GetEffectiveCatalogueAsync();
        var restored = snapshot.Signatures.Single(s => s.Key == "cloudflare-interstitial");
        Assert.True(restored.IsBundled);
        Assert.Equal(WafProvider.Cloudflare, restored.Provider);
        Assert.Equal("r1", snapshot.Revision);

        Assert.False(await _db.Catalogue.DeleteUserSignatureAsync("cloudflare-interstitial"));
    }

    /// <summary>Disabled user entries are excluded from the effective catalogue.</summary>
    [Fact]
    public async Task DisabledUserEntryIsExcluded()
    {
        var user = NewUserSignature("user-disabled", WafProvider.Other, WafChallengeClass.Interstitial) with { IsEnabled = false };
        await _db.Catalogue.UpsertUserSignatureAsync(user);
        var snapshot = await _db.Catalogue.GetEffectiveCatalogueAsync();
        Assert.DoesNotContain(snapshot.Signatures, s => s.Key == "user-disabled");
        Assert.Equal("r1", snapshot.Revision);
    }

    /// <summary>User entries round-trip through the database with stable identity and timestamps.</summary>
    [Fact]
    public async Task UserSignatureRoundTripsThroughDatabase()
    {
        var catalogue = new WafSignatureCatalogue(_db.Provider, WafTestDatabase.BundledAssetPath, new FixedTimeProvider());
        var first = await catalogue.UpsertUserSignatureAsync(NewUserSignature("user-roundtrip", WafProvider.Other, WafChallengeClass.Captcha));
        Assert.True(first.Id > 0);
        Assert.Equal("user-roundtrip", first.Key);
        Assert.Equal(FixedTimeProvider.FixedInstant, first.CreatedAt);
        Assert.Equal(FixedTimeProvider.FixedInstant, first.UpdatedAt);
        Assert.Equal(1, await _db.CountRowsAsync("waf_user_signatures"));

        var updated = await catalogue.UpsertUserSignatureAsync(first with { ChallengeClass = WafChallengeClass.BotBlock });
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(WafChallengeClass.BotBlock, updated.ChallengeClass);
        Assert.Equal(1, await _db.CountRowsAsync("waf_user_signatures"));
    }

    /// <summary>A new user key extends the effective catalogue without touching bundled entries.</summary>
    [Fact]
    public async Task NewUserKeyExtendsCatalogue()
    {
        var bundled = await _db.Catalogue.GetEffectiveCatalogueAsync();
        await _db.Catalogue.UpsertUserSignatureAsync(NewUserSignature("user-custom", WafProvider.Other, WafChallengeClass.Interstitial));
        var extended = await _db.Catalogue.GetEffectiveCatalogueAsync();
        Assert.Equal(bundled.Signatures.Count + 1, extended.Signatures.Count);
        var custom = extended.Signatures.Single(s => s.Key == "user-custom");
        Assert.False(custom.IsBundled);
        Assert.Contains("x-custom-waf|0.6", custom.HeaderMarkers);
    }

    /// <summary>Invalid marker payloads are rejected before anything is persisted.</summary>
    [Fact]
    public async Task UpsertRejectsInvalidMarkerJson()
    {
        var invalid = NewUserSignature("user-invalid", WafProvider.Other, WafChallengeClass.Interstitial) with
        {
            HeaderMarkersJson = "not-json",
        };
        await Assert.ThrowsAnyAsync<Exception>(() => _db.Catalogue.UpsertUserSignatureAsync(invalid));
        Assert.Equal(0, await _db.CountRowsAsync("waf_user_signatures"));
    }

    private static WafUserSignatureDto NewUserSignature(string key, WafProvider provider, WafChallengeClass challengeClass) => new(
        Id: 0,
        Key: key,
        Provider: provider,
        ChallengeClass: challengeClass,
        HeaderMarkersJson: "[\"x-custom-waf|0.6\"]",
        BodyMarkersJson: "[\"custom-challenge-marker|0.6\"]",
        IsEnabled: true,
        CreatedAt: FixedTimeProvider.FixedInstant,
        UpdatedAt: FixedTimeProvider.FixedInstant);
}
