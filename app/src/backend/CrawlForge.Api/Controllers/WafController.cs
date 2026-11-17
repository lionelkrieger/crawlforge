using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// WAF/challenge explorer and local signature catalogue (FS-CRAWL-002 §2.7):
/// per-audit block list and summary, plus the effective signature catalogue
/// with user-local CRUD. Detection and reporting only (E3); verdicts derive
/// from the bundled local catalogue plus observed responses (E4).
/// </summary>
[Route("api")]
public sealed class WafController : ApiControllerBase
{
    private readonly IReadRepository<WafDetectionDto> _detections;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IReadRepository<AuditDto> _audits;
    private readonly IWafSignatureCatalogue _catalogue;
    private readonly IReadRepository<WafUserSignatureDto> _userSignatures;
    private readonly IWriteRepository<WafUserSignatureDto> _userSignatureWrites;

    /// <summary>Creates the controller.</summary>
    /// <param name="detections">WAF detection read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="catalogue">Effective signature catalogue.</param>
    /// <param name="userSignatures">User signature read repository.</param>
    /// <param name="userSignatureWrites">User signature write repository.</param>
    public WafController(
        IReadRepository<WafDetectionDto> detections,
        IReadRepository<UrlRecordDto> urls,
        IReadRepository<AuditDto> audits,
        IWafSignatureCatalogue catalogue,
        IReadRepository<WafUserSignatureDto> userSignatures,
        IWriteRepository<WafUserSignatureDto> userSignatureWrites)
    {
        _detections = detections;
        _urls = urls;
        _audits = audits;
        _catalogue = catalogue;
        _userSignatures = userSignatures;
        _userSignatureWrites = userSignatureWrites;
    }

    /// <summary>Lists WAF-blocked URLs of an audit.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="provider">Provider filter.</param>
    /// <param name="challengeClass">Challenge class filter.</param>
    /// <param name="urlId">URL filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged block rows.</returns>
    [HttpGet("audits/{id:long}/waf-blocks")]
    public async Task<IActionResult> ListBlocks(
        long id,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? challengeClass = null,
        [FromQuery] string? urlId = null,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (!TryBindSort(sort, order, new[] { "detectedAt", "provider", "confidence" }, out var sortBy, out var descending, out var error))
        {
            return ValidationError(error!);
        }

        var filters = new Dictionary<string, string> { ["auditId"] = id.ToString() };
        if (provider is not null)
        {
            if (!ApiEnumConverter<WafProvider>.TryParse(provider, out var providerValue))
            {
                return ValidationError($"Unknown provider '{provider}'.", [new ApiFieldError("provider", "Use a known provider key.")]);
            }

            filters["provider"] = ((int)providerValue).ToString();
        }

        if (challengeClass is not null)
        {
            if (!ApiEnumConverter<WafChallengeClass>.TryParse(challengeClass, out var classValue))
            {
                return ValidationError($"Unknown challenge class '{challengeClass}'.", [new ApiFieldError("challengeClass", "Use a known challenge class key.")]);
            }

            filters["challengeClass"] = ((int)classValue).ToString();
        }

        if (urlId is not null)
        {
            filters["urlId"] = urlId;
        }

        var repositorySort = sortBy switch
        {
            "provider" => "Provider",
            "confidence" => "Id",
            _ => "DetectedAt",
        };
        var page = await _detections.ListAsync(BindListQuery(offset, limit, repositorySort, descending, filters), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var items = page.Items.Select(d => ToBlock(d, urlMap)).ToList();
        return OkEnvelope(items, PaginationOf(page));
    }

    /// <summary>Summarizes WAF blocks of an audit (provider/class counts, block rate).</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block summary.</returns>
    [HttpGet("audits/{id:long}/waf-blocks/summary")]
    public async Task<IActionResult> BlocksSummary(long id, CancellationToken cancellationToken)
    {
        var audit = await _audits.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var detections = await _detections.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var counters = AuditWireMapper.ParseCounters(audit.CountersJson);
        var fetched = counters.Fetched;
        var blockRate = fetched > 0 ? Math.Min(1.0, (double)detections.TotalCount / fetched) : 0.0;

        var byProvider = detections.Items
            .GroupBy(d => ApiEnumConverter<WafProvider>.ToWireValue(d.Provider), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        var byClass = detections.Items
            .GroupBy(d => ApiEnumConverter<WafChallengeClass>.ToWireValue(d.ChallengeClass), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

        return OkEnvelope(new WafBlocksSummaryWire(detections.TotalCount, blockRate, byProvider, byClass));
    }

    /// <summary>Lists the effective signature catalogue (bundled + user entries).</summary>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="provider">Provider filter.</param>
    /// <param name="source">Source filter (bundled/user).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catalogue with paged signatures.</returns>
    [HttpGet("waf-signatures")]
    public async Task<IActionResult> ListSignatures(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? provider = null,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _catalogue.GetEffectiveCatalogueAsync(cancellationToken);
        var userEntries = await _userSignatures.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        var enabledByKey = userEntries.Items.ToDictionary(u => u.Key, u => u.IsEnabled, StringComparer.Ordinal);

        var signatures = snapshot.Signatures
            .Select(s => ToSignature(s, enabledByKey))
            .Where(s => provider is null || string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .Where(s => source is null || string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var page = signatures.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(
            new WafSignatureCatalogueWire(snapshot.BundledVersion, page),
            new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), signatures.Count));
    }

    /// <summary>Creates a local user signature.</summary>
    /// <param name="request">Create request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created signature.</returns>
    [HttpPost("waf-signatures")]
    public async Task<IActionResult> CreateSignature([FromBody] WafSignatureCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Pattern))
        {
            return ValidationError("Key and pattern are required.", [new ApiFieldError("key", "Provide a stable key and a detection pattern.")]);
        }

        if (!ApiEnumConverter<WafProvider>.TryParse(request.Provider, out var provider)
            || !ApiEnumConverter<WafChallengeClass>.TryParse(request.ChallengeClass, out var challengeClass))
        {
            return ValidationError("Unknown provider or challenge class.", [new ApiFieldError("provider", "Use known provider and challenge class keys.")]);
        }

        var existing = await FindUserSignatureAsync(request.Key, cancellationToken);
        if (existing is not null)
        {
            return ConflictError($"A user signature with key '{request.Key}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var created = new WafUserSignatureDto(
            0, request.Key, provider, challengeClass, "[]", JsonSerializer.Serialize(new[] { request.Pattern }), true, now, now);
        var stored = await _catalogue.UpsertUserSignatureAsync(created, cancellationToken);
        return CreatedEnvelope(ToUserSignature(stored));
    }

    /// <summary>Updates a local user signature (partial).</summary>
    /// <param name="key">Signature key.</param>
    /// <param name="request">Update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated signature.</returns>
    [HttpPut("waf-signatures/{key}")]
    public async Task<IActionResult> UpdateSignature(string key, [FromBody] WafSignatureUpdateRequest request, CancellationToken cancellationToken)
    {
        var existing = await FindUserSignatureAsync(key, cancellationToken);
        if (existing is null)
        {
            return NotFoundError($"User signature '{key}' was not found.");
        }

        var provider = existing.Provider;
        if (request.Provider is not null && !ApiEnumConverter<WafProvider>.TryParse(request.Provider, out provider))
        {
            return ValidationError($"Unknown provider '{request.Provider}'.", [new ApiFieldError("provider", "Use a known provider key.")]);
        }

        var challengeClass = existing.ChallengeClass;
        if (request.ChallengeClass is not null && !ApiEnumConverter<WafChallengeClass>.TryParse(request.ChallengeClass, out challengeClass))
        {
            return ValidationError($"Unknown challenge class '{request.ChallengeClass}'.", [new ApiFieldError("challengeClass", "Use a known challenge class key.")]);
        }

        var bodyMarkers = request.Pattern is not null
            ? JsonSerializer.Serialize(new[] { request.Pattern })
            : existing.BodyMarkersJson;
        var updated = existing with
        {
            Provider = provider,
            ChallengeClass = challengeClass,
            BodyMarkersJson = bodyMarkers,
            IsEnabled = request.Enabled ?? existing.IsEnabled,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var stored = await _catalogue.UpsertUserSignatureAsync(updated, cancellationToken);
        return OkEnvelope(ToUserSignature(stored));
    }

    /// <summary>Deletes a user signature, restoring any bundled default.</summary>
    /// <param name="key">Signature key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deletion verdict.</returns>
    [HttpDelete("waf-signatures/{key}")]
    public async Task<IActionResult> DeleteSignature(string key, CancellationToken cancellationToken)
    {
        var deleted = await _catalogue.DeleteUserSignatureAsync(key, cancellationToken);
        if (!deleted)
        {
            return NotFoundError($"User signature '{key}' was not found.");
        }

        var snapshot = await _catalogue.GetEffectiveCatalogueAsync(cancellationToken);
        var restored = snapshot.Signatures.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal) && s.IsBundled);
        return OkEnvelope(new WafSignatureDeleteResponse(key, true, restored));
    }

    private async Task<WafUserSignatureDto?> FindUserSignatureAsync(string key, CancellationToken cancellationToken)
    {
        var page = await _userSignatures.ListAsync(new ListQuery(0, 10_000), cancellationToken);
        return page.Items.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyDictionary<long, string>> LoadUrlMapAsync(long auditId, CancellationToken cancellationToken)
    {
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        return urls.Items.ToDictionary(u => u.Id, u => u.CanonicalUrl);
    }

    private static WafBlockWire ToBlock(WafDetectionDto detection, IReadOnlyDictionary<long, string> urlMap)
    {
        var evidence = ProjectWireMapper.ParseObject(detection.EvidenceJson);
        var headers = evidence["headers"] as System.Text.Json.Nodes.JsonObject;
        return new WafBlockWire(
            detection.UrlId.ToString(),
            urlMap.GetValueOrDefault(detection.UrlId, string.Empty),
            detection.DetectedAt,
            ApiEnumConverter<WafProvider>.ToWireValue(detection.Provider),
            ApiEnumConverter<WafChallengeClass>.ToWireValue(detection.ChallengeClass),
            evidence["confidence"]?.GetValue<double>() ?? 1.0,
            detection.SignatureKey,
            new WafEvidenceWire(
                evidence["snippet"]?.GetValue<string>(),
                headers?.ToDictionary(h => h.Key, h => h.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal),
                detection.SignatureKey));
    }

    private static WafSignatureWire ToSignature(WafSignature signature, IReadOnlyDictionary<string, bool> enabledByKey)
    {
        var pattern = string.Join(";", signature.HeaderMarkers.Concat(signature.BodyMarkers));
        return new WafSignatureWire(
            signature.Key,
            ApiEnumConverter<WafProvider>.ToWireValue(signature.Provider),
            ApiEnumConverter<WafChallengeClass>.ToWireValue(signature.ChallengeClass),
            pattern,
            signature.IsBundled ? "bundled" : "user",
            signature.IsBundled || enabledByKey.GetValueOrDefault(signature.Key, true),
            null);
    }

    private static WafSignatureWire ToUserSignature(WafUserSignatureDto signature)
    {
        var markers = ProjectWireMapper.ParseStringList(signature.BodyMarkersJson);
        var pattern = string.Join(";", markers);
        return new WafSignatureWire(
            signature.Key,
            ApiEnumConverter<WafProvider>.ToWireValue(signature.Provider),
            ApiEnumConverter<WafChallengeClass>.ToWireValue(signature.ChallengeClass),
            pattern,
            "user",
            signature.IsEnabled,
            null);
    }
}
