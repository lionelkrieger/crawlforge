using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// content_scope_profiles persistence on the real completed stack
/// (FS-CRAWL-002 §4.4): configuration data with ordered language/expression
/// entries, validated at configuration time.
/// </summary>
public class ProfileStoreTests : IClassFixture<AuditSqliteStackFixture>
{
    private readonly AuditSqliteStackFixture _stack;

    public ProfileStoreTests(AuditSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private ContentScopeProfileStore CreateStore() => new(_stack.Profiles, _stack.Profiles);

    [Fact]
    public async Task CreateAndResolveRoundTripsOrderedEntries()
    {
        var (auditId, projectId, _) = await _stack.CreateAuditAsync();
        Assert.True(auditId > 0);
        var store = CreateStore();
        var scope = new ContentScopeDefinition(
            ["main", "//article[@id='post']"],
            ["nav", ".cookie-banner"]);

        var profile = await store.CreateAsync(projectId, "article-only", scope);
        Assert.True(profile.Id > 0);
        Assert.Equal(projectId, profile.ProjectId);

        var resolved = await store.ResolveDefinitionAsync(profile.Id);
        Assert.NotNull(resolved);
        Assert.Equal(scope.IncludeSelectors, resolved.IncludeSelectors);
        Assert.Equal(scope.ExcludeSelectors, resolved.ExcludeSelectors);
    }

    [Fact]
    public async Task InvalidSelectorsAreRejectedBeforePersistence()
    {
        var (_, projectId, _) = await _stack.CreateAuditAsync();
        var store = CreateStore();
        await Assert.ThrowsAsync<ContentScopeValidationException>(() =>
            store.CreateAsync(projectId, "bad", new ContentScopeDefinition(["???>>>"], [])));

        var listed = await store.ListByProjectAsync(projectId);
        Assert.Empty(listed);
    }

    [Fact]
    public async Task UpdateValidatesAndReplacesScope()
    {
        var (_, projectId, _) = await _stack.CreateAuditAsync();
        var store = CreateStore();
        var profile = await store.CreateAsync(projectId, "v1", new ContentScopeDefinition(["main"], []));

        var updated = await store.UpdateAsync(profile.Id, "v2", new ContentScopeDefinition(["#content"], ["footer"]));
        Assert.NotNull(updated);
        Assert.Equal("v2", updated.Name);

        var resolved = await store.ResolveDefinitionAsync(profile.Id);
        Assert.NotNull(resolved);
        Assert.Equal(["#content"], resolved.IncludeSelectors);
        Assert.Equal(["footer"], resolved.ExcludeSelectors);

        await Assert.ThrowsAsync<ContentScopeValidationException>(() =>
            store.UpdateAsync(profile.Id, "bad", new ContentScopeDefinition([], ["//["])));
    }

    [Fact]
    public async Task ListIsProjectScopedAndDeleteRemoves()
    {
        var (_, projectA, _) = await _stack.CreateAuditAsync();
        var (_, projectB, _) = await _stack.CreateAuditAsync();
        var store = CreateStore();
        await store.CreateAsync(projectA, "a-scope", new ContentScopeDefinition(["main"], []));
        await store.CreateAsync(projectB, "b-scope", new ContentScopeDefinition(["body"], []));

        var listedA = await store.ListByProjectAsync(projectA);
        Assert.Single(listedA);
        Assert.Equal("a-scope", listedA[0].Name);

        Assert.True(await store.DeleteAsync(listedA[0].Id));
        Assert.Empty(await store.ListByProjectAsync(projectA));
        Assert.Null(await store.ResolveDefinitionAsync(listedA[0].Id));
        Assert.Single(await store.ListByProjectAsync(projectB));
    }

    [Fact]
    public async Task MissingProfileResolvesToNull()
    {
        var store = CreateStore();
        Assert.Null(await store.ResolveDefinitionAsync(999999));
        Assert.Null(await store.UpdateAsync(999999, "x", new ContentScopeDefinition([], [])));
    }
}
