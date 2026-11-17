using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// User dictionary (FS-CRAWL-001 §10.2): locally accepted spellings that
/// suppress spelling findings. Local user data only — bundled dictionary
/// assets are never fetched remotely. Duplicates are rejected with 409;
/// removal matches word plus optional language.
/// </summary>
[Route("api/dictionary")]
public sealed class DictionaryController : ApiControllerBase
{
    private readonly IReadRepository<UserDictionaryEntryDto> _reads;
    private readonly IWriteRepository<UserDictionaryEntryDto> _writes;

    /// <summary>Creates the controller.</summary>
    /// <param name="reads">Dictionary read repository.</param>
    /// <param name="writes">Dictionary write repository.</param>
    public DictionaryController(IReadRepository<UserDictionaryEntryDto> reads, IWriteRepository<UserDictionaryEntryDto> writes)
    {
        _reads = reads;
        _writes = writes;
    }

    /// <summary>Lists dictionary entries with language filter and pagination.</summary>
    /// <param name="language">Optional language filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged entries.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListAsync(
        [FromQuery] string? language,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBindSort(sort, order, ["word", "language", "addedAt"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var repositorySort = sortBy == "addedAt" ? "AddedAt" : sortBy;
        var filters = language is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["language"] = language };
        var page = await _reads.ListAsync(BindListQuery(offset, limit, repositorySort, descending, filters), cancellationToken);
        var items = page.Items.Select(e => new DictionaryEntryWire(e.Word, e.Language, e.AddedAt)).ToList();
        return OkEnvelope(items, PaginationOf(page));
    }

    /// <summary>Accepts a word into the dictionary.</summary>
    /// <param name="request">Word and language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Mutation confirmation.</returns>
    [HttpPost]
    public async Task<ObjectResult> AddAsync([FromBody] DictionaryAddRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Word))
        {
            return ValidationError("A word is required.", [new ApiFieldError("word", "Provide a word.")]);
        }

        if (string.IsNullOrWhiteSpace(request.Language))
        {
            return ValidationError("A language tag is required.", [new ApiFieldError("language", "Provide a language tag.")]);
        }

        var word = request.Word.Trim();
        var language = request.Language.Trim();
        var existing = await _reads.ListAsync(new ListQuery(0, 1, new Dictionary<string, string>
        {
            ["word"] = word,
            ["language"] = language,
        }), cancellationToken);
        if (existing.Items.Count > 0)
        {
            return ConflictError($"'{word}' is already accepted for language '{language}'.");
        }

        await _writes.CreateAsync(new UserDictionaryEntryDto(0, language, word, DateTimeOffset.UtcNow), cancellationToken);
        return CreatedEnvelope(new DictionaryMutationResponse(word, language, true, null));
    }

    /// <summary>Removes a word (optionally restricted to one language).</summary>
    /// <param name="request">Word and optional language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Mutation confirmation.</returns>
    [HttpDelete]
    public async Task<ObjectResult> RemoveAsync([FromBody] DictionaryRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Word))
        {
            return ValidationError("A word is required.", [new ApiFieldError("word", "Provide a word.")]);
        }

        var word = request.Word.Trim();
        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["word"] = word };
        if (request.Language is not null)
        {
            filters["language"] = request.Language.Trim();
        }

        var matches = await _reads.ListAsync(new ListQuery(0, 10_000, filters), cancellationToken);
        if (matches.Items.Count == 0)
        {
            return NotFoundError($"'{word}' is not in the dictionary.");
        }

        foreach (var entry in matches.Items)
        {
            await _writes.DeleteAsync(entry.Id, cancellationToken);
        }

        return OkEnvelope(new DictionaryMutationResponse(word, matches.Items[0].Language, null, true));
    }
}
