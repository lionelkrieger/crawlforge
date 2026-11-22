using System.Text.Json;
using CrawlForge.ContractConformance.Tests.Harness;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// REST uniform-envelope conformance (FS-CRAWL-001 §10.1/§10.4) against the
/// LIVE loopback backend: every one of the 108 frozen catalog operations
/// (88 route groups) is driven with real scratch entities and asserted for
/// envelope shape — data always present, pagination {offset,limit,total},
/// error {code,message,fieldErrors?} with the stable code set, camelCase,
/// lowercase enums. Binary/text routes (metrics, export file) carry the
/// catalog's documented envelope exemption.
/// </summary>
[Collection(LiveBackendCollection.Name)]
public sealed class RestEnvelopeConformanceTests
{
    private readonly LiveBackendFixture _fixture;

    /// <summary>Creates the suite over the shared live backend.</summary>
    /// <param name="fixture">Shared live-backend fixture.</param>
    public RestEnvelopeConformanceTests(LiveBackendFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Drives every frozen catalog operation against the live backend and
    /// asserts the uniform envelope on each JSON response. The per-route
    /// results are committed as captured/rest-envelope-sweep.json.
    /// </summary>
    [Fact]
    public async Task EveryCatalogOperation_ConformsToUniformEnvelope()
    {
        var catalog = RouteCatalog.ParseFrozenCatalog();
        // WP-10-04 (UI-21 amendment): +settings.browser (GET /api/settings/browser).
        Assert.Equal(109, catalog.Count);

        // Disposable entities created BY this sweep (only these are deleted).
        var sweepProject = await Rest.PostDataAsync(_fixture.Http, "/api/projects", new
        {
            name = "Conformance sweep disposable",
            description = "WP-08-03 envelope sweep scratch project."
        });
        var sweepProjectId = IdOf(sweepProject);
        var sweepConfig = await Rest.PostDataAsync(_fixture.Http, $"/api/projects/{sweepProjectId}/configurations", new
        {
            name = "sweep-config",
            startUrls = new[] { FixtureSite.BaseUrl + "/" },
            limits = new { maxUrls = 2, maxDepth = 1, maxDurationSeconds = 30 },
            rendering = new { enabled = false, infiniteScroll = false, captureConsole = false }
        });
        var sweepConfigId = IdOf(sweepConfig);
        var sweepSchedule = await Rest.PostDataAsync(_fixture.Http, $"/api/projects/{_fixture.ProjectId}/schedules", new
        {
            configurationId = _fixture.BoundedConfigurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            name = "Conformance sweep schedule",
            enabled = false,
            cron = "interval:1.00:00:00",
            completionActions = Array.Empty<object>()
        });
        var sweepScheduleId = IdOf(sweepSchedule);
        var sweepDestinationDir = Path.Combine(_fixture.DataDirectory, "sweep-delivery");
        Directory.CreateDirectory(sweepDestinationDir);
        var sweepDestination = await Rest.PostDataAsync(_fixture.Http, "/api/destinations", new
        {
            name = "Conformance sweep destination",
            kind = "local-folder",
            target = sweepDestinationDir
        });
        var sweepDestinationId = IdOf(sweepDestination);
        var sweepQuery = await Rest.PostDataAsync(_fixture.Http, "/api/queries", new
        {
            name = "Conformance sweep query",
            ruleTree = new
            {
                kind = "group",
                @operator = "and",
                children = new[] { new { kind = "condition", field = "severity", @operator = "eq", value = "error" } }
            }
        });
        var sweepQueryId = IdOf(sweepQuery);
        var sweepImport = await Rest.PostDataAsync(_fixture.Http, $"/api/projects/{_fixture.ProjectId}/logs/import", new
        {
            filePath = Path.Combine(_fixture.DataDirectory, "fixture-access.log"),
            format = "combined"
        });
        var sweepImportId = long.Parse(sweepImport.GetProperty("importId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var results = new List<object>();
        var violations = new List<string>();
        var createdProjectIds = new List<long>();
        var createdConfigurationIds = new List<long>();
        var createdScheduleIds = new List<long>();
        var createdDestinationIds = new List<long>();
        var createdQueryIds = new List<long>();
        // Drive reads/mutations before deletes: catalog order places
        // projects.delete before configuration/schedule/log reads that need
        // the sweep project. Deletes run last, children before parents
        // (projects.delete absolutely last). OrderBy is stable, so catalog
        // order is preserved within each rank.
        foreach (var operation in catalog.OrderBy(op =>
        {
            var isDelete = op.Method == "DELETE"
                || op.Key.EndsWith(".delete", StringComparison.Ordinal)
                || op.Key.EndsWith(".remove", StringComparison.Ordinal);
            return isDelete ? (op.Key == "projects.delete" ? 2 : 1) : 0;
        }))
        {
            var plan = DrivePlan(operation, sweepProjectId, sweepConfigId, sweepScheduleId, sweepDestinationId, sweepQueryId, sweepImportId, sweepDestinationDir);

            // FK hygiene before parent deletes: the case-created schedule
            // references the sweep configuration (RESTRICT — a dangling
            // schedule 500s the config delete), so it goes first. The setup
            // sweep schedule references the FIXTURE bounded configuration and
            // is left for its own schedules.delete drive. Case-created
            // configurations are cleared before the project delete as well.
            if (operation.Key == "configurations.delete")
            {
                foreach (var id in createdScheduleIds)
                {
                    await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/schedules/{id}");
                }
            }

            if (operation.Key == "projects.delete")
            {
                foreach (var id in createdConfigurationIds)
                {
                    await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/configurations/{id}");
                }
            }

            var exchange = await Rest.SendAsync(_fixture.Http, operation.Method, plan.Path, plan.Body);

            // Track sweep-created entities so cleanup deletes only our own rows.
            if (exchange.Status is >= 200 and < 300 && exchange.IsJsonObject
                && exchange.Root.TryGetProperty("data", out var created) && created.ValueKind == JsonValueKind.Object)
            {
                switch (operation.Key)
                {
                    case "projects.create" when created.TryGetProperty("id", out var pid):
                        createdProjectIds.Add(long.Parse(pid.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "configurations.create" when created.TryGetProperty("id", out var cid):
                        createdConfigurationIds.Add(long.Parse(cid.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "schedules.create" when created.TryGetProperty("id", out var sid):
                        createdScheduleIds.Add(long.Parse(sid.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "destinations.create" when created.TryGetProperty("id", out var did):
                        createdDestinationIds.Add(long.Parse(did.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "queries.create" when created.TryGetProperty("id", out var qid):
                        createdQueryIds.Add(long.Parse(qid.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }

            string outcome;
            if (!operation.Enveloped)
            {
                // Documented binary/text exemption: must NOT be a JSON envelope.
                var isJson = exchange.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
                outcome = isJson ? "VIOLATION: exempt route returned JSON" : "exempt-non-json";
                if (isJson)
                {
                    violations.Add($"{operation.Key}: envelope-exempt route returned JSON ({exchange.ContentType})");
                }
            }
            else
            {
                var expectSuccess = (plan.ExpectSuccess || plan.OutcomeAgnostic) && exchange.Status is >= 200 and < 300;
                var routeViolations = Rest.CollectEnvelopeViolations(exchange, expectSuccess);
                if (plan.ExpectSuccess && !plan.OutcomeAgnostic && exchange.Status is not (>= 200 and < 300))
                {
                    routeViolations.Add($"expected success, got HTTP {exchange.Status}");
                }

                if (plan.ExpectedErrorCode is not null)
                {
                    var code = exchange.IsJsonObject && exchange.Root.TryGetProperty("error", out var error)
                        && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetString()
                        : null;
                    if (code != plan.ExpectedErrorCode)
                    {
                        routeViolations.Add($"expected error.code '{plan.ExpectedErrorCode}', got '{code}'");
                    }
                }

                outcome = routeViolations.Count == 0 ? "conformant" : "VIOLATION";
                foreach (var violation in routeViolations)
                {
                    violations.Add($"{operation.Key} ({operation.Method} {plan.Path}): {violation}");
                }
            }

            results.Add(new
            {
                key = operation.Key,
                method = operation.Method,
                path = plan.Path,
                status = exchange.Status,
                contentType = exchange.ContentType,
                enveloped = operation.Enveloped,
                expectedSuccess = plan.ExpectSuccess,
                outcome
            });
        }

        await _fixture.SaveCaptureAsync("rest-envelope-sweep.json", JsonSerializer.SerializeToElement(new
        {
            operations = results.Count,
            routeGroups = catalog.Select(o => o.Group).Distinct().Count(),
            violations,
            results
        }));

        // Cleanup: delete ONLY the sweep's own disposable entities.
        await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/logs/{sweepImportId}");
        await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/queries?id={sweepQueryId}");
        foreach (var id in createdQueryIds)
        {
            await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/queries?id={id}");
        }

        foreach (var id in createdDestinationIds)
        {
            await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/destinations/{id}");
        }

        await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/destinations/{sweepDestinationId}");
        foreach (var id in createdScheduleIds)
        {
            await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/schedules/{id}");
        }

        await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/schedules/{sweepScheduleId}");
        foreach (var id in createdConfigurationIds)
        {
            await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/configurations/{id}");
        }

        await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/configurations/{sweepConfigId}");
        foreach (var id in createdProjectIds)
        {
            await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/projects/{id}");
        }

        await Rest.SendAsync(_fixture.Http, "DELETE", $"/api/projects/{sweepProjectId}");

        Assert.True(violations.Count == 0,
            $"{violations.Count} envelope violation(s) across {results.Count} operations:\n" + string.Join("\n", violations));
    }

    /// <summary>Pagination follows offset/limit/total on a real paged route.</summary>
    [Fact]
    public async Task Pagination_OffsetLimitTotal_BehavePerContract()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "GET", "/api/audits?offset=0&limit=2");
        Rest.AssertSuccessEnvelope(exchange);
        var pagination = exchange.Root.GetProperty("pagination");
        Assert.Equal(0, pagination.GetProperty("offset").GetInt32());
        Assert.Equal(2, pagination.GetProperty("limit").GetInt32());
        Assert.True(pagination.GetProperty("total").GetInt64() >= 1);
        var data = exchange.Root.GetProperty("data");
        Assert.True(data.GetArrayLength() <= 2);

        // A page beyond the total returns an empty data array, not an error.
        var beyond = await Rest.SendAsync(_fixture.Http, "GET", "/api/audits?offset=10000&limit=2");
        Rest.AssertSuccessEnvelope(beyond);
        Assert.Equal(0, beyond.Root.GetProperty("data").GetArrayLength());
    }

    /// <summary>Model-validation failures carry the fieldErrors map (§10.4, D8).</summary>
    [Fact]
    public async Task ValidationError_CarriesFieldErrorsMap()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "POST", "/api/projects", new { });
        Assert.Equal(400, exchange.Status);
        Rest.AssertErrorEnvelope(exchange, "validation");
        var error = exchange.Root.GetProperty("error");
        Assert.True(error.TryGetProperty("fieldErrors", out var fieldErrors), "validation error must carry fieldErrors");
        Assert.Equal(JsonValueKind.Object, fieldErrors.ValueKind);
        Assert.True(fieldErrors.EnumerateObject().Any(), "fieldErrors must name at least one invalid field");
    }

    /// <summary>Unknown identifiers produce the not-found envelope, not a raw 404 body.</summary>
    [Fact]
    public async Task NotFoundError_ConformsToEnvelope()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "GET", "/api/projects/999999999");
        Assert.Equal(404, exchange.Status);
        Rest.AssertErrorEnvelope(exchange, "not-found");
    }

    /// <summary>Invalid lifecycle transitions produce the invalid-state envelope (§10.4).</summary>
    [Fact]
    public async Task InvalidStateTransition_ConformsToEnvelope()
    {
        // The fixture audit is Completed: pause/resume/cancel are invalid transitions.
        foreach (var action in new[] { "pause", "resume", "cancel" })
        {
            var exchange = await Rest.SendAsync(_fixture.Http, "POST", $"/api/audits/{_fixture.AuditId}/{action}");
            Assert.Equal(409, exchange.Status);
            Rest.AssertErrorEnvelope(exchange, "invalid-state");
        }
    }

    /// <summary>Health and version are enveloped like every other JSON route.</summary>
    [Fact]
    public async Task HealthAndVersion_AreEnveloped()
    {
        var health = await Rest.SendAsync(_fixture.Http, "GET", "/api/health");
        Rest.AssertSuccessEnvelope(health);
        Assert.True(health.Root.GetProperty("data").GetProperty("ready").GetBoolean());

        var version = await Rest.SendAsync(_fixture.Http, "GET", "/api/version");
        Rest.AssertSuccessEnvelope(version);
        var data = version.Root.GetProperty("data");
        Assert.False(string.IsNullOrEmpty(data.GetProperty("productVersion").GetString()));
        // schemaVersion is a numeric migration version on the wire.
        Assert.Equal(JsonValueKind.Number, data.GetProperty("schemaVersion").ValueKind);
        Assert.True(data.GetProperty("schemaVersion").GetInt32() > 0);
    }

    /// <summary>/metrics is Prometheus text (catalog exemption), not a JSON envelope.</summary>
    [Fact]
    public async Task Metrics_IsPrometheusText_NotEnveloped()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "GET", "/metrics");
        Assert.Equal(200, exchange.Status);
        Assert.DoesNotContain("json", exchange.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# TYPE", exchange.Body, StringComparison.Ordinal);
    }

    /// <summary>The export file download is binary (catalog exemption), not a JSON envelope.</summary>
    [Fact]
    public async Task ExportDownload_IsBinaryExemptFromEnvelope()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "GET", $"/api/exports/{_fixture.ExportId}/file");
        Assert.True(exchange.Status is >= 200 and < 300, $"export download returned {exchange.Status}");
        Assert.DoesNotContain("json", exchange.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.True(exchange.Body.Length > 0, "export file must not be empty");
    }

    /// <summary>
    /// G-REST-IDEMPOTENCY RESOLVED (WP-09-11): §10.4 idempotency — the engine's
    /// StartCoreAsync returns the existing active audit (Queued/Running/Paused)
    /// for the same configuration instead of creating a second run.
    /// </summary>
    [Fact]
    public async Task DuplicateAuditStart_IsIdempotentWhileActive()
    {
        var first = await Rest.PostDataAsync(_fixture.Http, "/api/audits", new
        {
            configurationId = _fixture.BoundedConfigurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limitOverrides = new { maxUrls = 220, maxDepth = 6, maxDurationSeconds = 180 }
        });
        var second = await Rest.PostDataAsync(_fixture.Http, "/api/audits", new
        {
            configurationId = _fixture.BoundedConfigurationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limitOverrides = new { maxUrls = 220, maxDepth = 6, maxDurationSeconds = 180 }
        });
        Assert.Equal(first.GetProperty("auditId").GetString(), second.GetProperty("auditId").GetString());
    }

    /// <summary>The session gate rejects unauthenticated data requests with 401.</summary>
    [Fact]
    public async Task SessionGate_RejectsMissingToken()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        using var response = await anonymous.GetAsync("/api/projects");
        Assert.Equal(401, (int)response.StatusCode);

        // The readiness probe is exempt (composition-layer gate, not an API route).
        using var health = await anonymous.GetAsync("/api/health");
        Assert.Equal(200, (int)health.StatusCode);
    }

    /// <summary>
    /// G-REAUDIT-SNAPSHOT (contract side): the frozen re-audit wire contract —
    /// ReauditRequest {refresh?} and ReauditResponse {auditId, sourceAuditId,
    /// state} as string ids — is intact at the API boundary. This shape check
    /// passes regardless of the execution-layer defect below.
    /// </summary>
    [Fact]
    public void ReauditWireContract_Shape()
    {
        var request = typeof(CrawlForge.Api.Controllers.ReauditRequest);
        Assert.NotNull(request.GetProperty("Refresh"));
        Assert.Equal(typeof(bool?), request.GetProperty("Refresh")!.PropertyType);

        var response = typeof(CrawlForge.Api.Controllers.ReauditResponse);
        foreach (var property in new[] { "AuditId", "SourceAuditId", "State" })
        {
            var member = response.GetProperty(property);
            Assert.NotNull(member);
            Assert.Equal(typeof(string), member!.PropertyType);
        }
    }

    /// <summary>
    /// G-REAUDIT-SNAPSHOT RESOLVED (WP-09-11): REST start now merges
    /// enabledCategories into the engine's DTO-shaped storage snapshot
    /// (AuditsController.ParseSnapshotObject) instead of overwriting it with
    /// the wire projection, so re-audit of a REST-started audit deserializes
    /// cleanly and returns the created envelope with a new audit id.
    /// </summary>
    [Fact]
    public async Task Reaudit_RestStartedAudit_SpecShape()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "POST", $"/api/audits/{_fixture.AuditId}/reaudit", new { refresh = false });
        Rest.AssertSuccessEnvelope(exchange);
        Assert.Equal(201, exchange.Status);
        var data = exchange.Root.GetProperty("data");
        Assert.Equal(JsonValueKind.String, data.GetProperty("auditId").ValueKind);
        Assert.Equal(_fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture), data.GetProperty("sourceAuditId").GetString());
    }

    /// <summary>
    /// G-MATRIX-SEED-RACE RESOLVED (WP-09-11): the crawl loop reuses pre-seeded
    /// url_records rows (GetOrCreateUrlRowAsync) and enqueues pre-seeded rows
    /// through the admission gate, so autoSeedAudit's concurrent crawl + ingest
    /// no longer collides on UNIQUE(audit_id, canonical_url); the seeded
    /// validation audit completes.
    /// </summary>
    [Fact]
    public async Task MatrixAutoSeed_StartsValidationAudit_SpecShape()
    {
        var exchange = await Rest.SendAsync(_fixture.Http, "POST", $"/api/audits/{_fixture.AuditId}/redirect-matrix", new
        {
            rows = new[] { new { sourceUrl = FixtureSite.BaseUrl + "/redirect/chain-1", targetUrl = FixtureSite.BaseUrl + "/pages/redirect-target.html" } },
            autoSeedAudit = true
        });
        Rest.AssertSuccessEnvelope(exchange);
        var seededAuditId = long.Parse(exchange.Root.GetProperty("data").GetProperty("auditId").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var terminal = await _fixture.WaitForTerminalAsync(seededAuditId, TimeSpan.FromSeconds(240));
        Assert.Equal("completed", terminal.GetProperty("state").GetString());
    }

    private static long IdOf(JsonElement data) =>
        long.Parse(data.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private sealed record RouteDrivePlan(string Path, object? Body, bool ExpectSuccess, string? ExpectedErrorCode = null, bool OutcomeAgnostic = false);

    private RouteDrivePlan DrivePlan(
        RouteOperation operation,
        long sweepProjectId,
        long sweepConfigId,
        long sweepScheduleId,
        long sweepDestinationId,
        long sweepQueryId,
        long sweepImportId,
        string sweepDestinationDir)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var auditId = _fixture.AuditId.ToString(inv);
        var projectId = _fixture.ProjectId.ToString(inv);
        var boundedConfigId = _fixture.BoundedConfigurationId.ToString(inv);
        var fixtureRoot = FixtureSite.BaseUrl + "/";

        // Context-sensitive {id} resolution by route family.
        string ResolveId(string path)
        {
            if (path.StartsWith("/api/projects/", StringComparison.Ordinal)) return sweepProjectId.ToString(inv);
            if (path.StartsWith("/api/configurations/", StringComparison.Ordinal)) return sweepConfigId.ToString(inv);
            if (path.StartsWith("/api/schedules/", StringComparison.Ordinal)) return sweepScheduleId.ToString(inv);
            if (path.StartsWith("/api/destinations/", StringComparison.Ordinal)) return sweepDestinationId.ToString(inv);
            if (path.StartsWith("/api/exports/", StringComparison.Ordinal)) return _fixture.ExportId.ToString(inv);
            if (path.StartsWith("/api/logs/", StringComparison.Ordinal)) return sweepImportId.ToString(inv);
            if (path.StartsWith("/api/plugins/", StringComparison.Ordinal)) return "999999"; // numeric :long constraint; missing id → enveloped 404
            return auditId; // audits family
        }

        var concrete = operation.Path
            .Replace("{urlId}", _fixture.NoindexUrlId, StringComparison.Ordinal)
            .Replace("{metric}", "status-classes", StringComparison.Ordinal)
            .Replace("{host}", Uri.EscapeDataString("127.0.0.1:" + FixtureSite.Port), StringComparison.Ordinal)
            .Replace("{key}", "conformance-missing-key", StringComparison.Ordinal)
            .Replace("{id}", ResolveId(operation.Path), StringComparison.Ordinal);

        // GET reads over real fixture data must succeed; mutations carry valid
        // bodies where the shape is locked, everything else asserts envelope
        // shape on whichever (success | error) outcome the live backend returns.
        switch (operation.Key)
        {
            case "health.get":
            case "version.get":
            case "settings.get":
            case "settings.browser":
            case "diagnostics.listArtifacts":
            case "projects.list":
            case "configurations.list":
            case "audits.list":
            case "audits.get":
            case "audits.listUrls":
            case "audits.getUrl":
            case "audits.listLinks":
            case "audits.listFindings":
            case "audits.summary":
            case "audits.chartSeries":
            case "audits.listExtractedContent":
            case "audits.extractionSummary":
            case "audits.listSearchResults":
            case "audits.listSnippetResults":
            case "audits.listTechnologies":
            case "audits.scores":
            case "schedules.list":
            case "schedules.get":
            case "schedules.listRuns":
            case "destinations.list":
            case "dictionary.list":
            case "queries.list":
            case "waf.listBlocks":
            case "waf.blocksSummary":
            case "waf.listSignatures":
            case "hosts.list":
            case "ngrams.list":
            case "ngrams.cannibalization":
            case "ngrams.gaps":
            case "plugins.list":
            case "system.health":
            case "matrix.results":
            case "logs.list":
            case "semantics.linkOpportunities":
            case "semantics.clusters":
            case "geo.llmsTxt":
            case "geo.governance":
            case "analytics.list":
            case "analytics.coverage":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);

            case "projects.get":
                return new RouteDrivePlan($"/api/projects/{projectId}", null, ExpectSuccess: true);
            case "audits.graph":
                // type is required (structure-tree | link-network | redirect-chains).
                return new RouteDrivePlan(concrete + "?type=link-network", null, ExpectSuccess: true);
            case "semantics.similar":
                // urlId is required (numeric reference URL record).
                return new RouteDrivePlan(concrete + $"?urlId={_fixture.NoindexUrlId}", null, ExpectSuccess: true);
            case "semantics.run":
                // Runs the local embedding job synchronously; {} uses defaults.
                return new RouteDrivePlan(concrete, new { }, ExpectSuccess: true);
            case "hosts.override":
                // rate:null clears the manual override; returns the host state.
                return new RouteDrivePlan(concrete, new { rate = (double?)null }, ExpectSuccess: true);
            case "configurations.get":
                return new RouteDrivePlan($"/api/configurations/{sweepConfigId}", null, ExpectSuccess: true);
            case "audits.compare":
                return new RouteDrivePlan($"/api/audits/compare?auditIdA={auditId}&auditIdB={_fixture.ReauditId}", null, ExpectSuccess: true);
            case "audits.compareContent":
                return new RouteDrivePlan($"/api/audits/compare/content?auditIdA={auditId}&auditIdB={_fixture.ReauditId}&canonicalUrl={Uri.EscapeDataString(FixtureSite.BaseUrl + "/")}", null, ExpectSuccess: true);
            case "logs.correlation":
                return new RouteDrivePlan($"/api/audits/{auditId}/logs/correlation?importId={_fixture.LogImportId}", null, ExpectSuccess: true);

            case "projects.create":
                return new RouteDrivePlan(concrete, new { name = "Conformance sweep create", description = "sweep" }, ExpectSuccess: true);
            case "projects.update":
                return new RouteDrivePlan(concrete, new { name = "Conformance sweep disposable (renamed)" }, ExpectSuccess: true);
            case "projects.delete":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);
            case "configurations.create":
                return new RouteDrivePlan(concrete, new
                {
                    name = "sweep-config-create",
                    startUrls = new[] { fixtureRoot },
                    limits = new { maxUrls = 2, maxDepth = 1, maxDurationSeconds = 30 },
                    rendering = new { enabled = false, infiniteScroll = false, captureConsole = false }
                }, ExpectSuccess: true);
            case "configurations.update":
                return new RouteDrivePlan(concrete, new { name = "sweep-config (renamed)" }, ExpectSuccess: true);
            case "configurations.delete":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);

            case "audits.start":
                return new RouteDrivePlan(concrete, new
                {
                    configurationId = boundedConfigId,
                    limitOverrides = new { maxUrls = 2, maxDepth = 1, maxDurationSeconds = 30 }
                }, ExpectSuccess: true);
            case "audits.pause":
            case "audits.resume":
            case "audits.cancel":
                return new RouteDrivePlan($"/api/audits/{auditId}/{operation.Key.Split('.')[1]}", null, ExpectSuccess: false, ExpectedErrorCode: "invalid-state");
            case "audits.reaudit":
                // G-REAUDIT-SNAPSHOT: re-audit of REST-started audits 500s
                // (wire-shaped snapshot vs DTO read). The MCP-started offline
                // source keeps the engine DTO snapshot and re-audits cleanly.
                return new RouteDrivePlan($"/api/audits/{_fixture.OfflineSourceAuditId}/reaudit", new { refresh = false }, ExpectSuccess: true);

            case "analysis.singlePage":
                return new RouteDrivePlan(concrete, new { url = FixtureSite.BaseUrl + "/pages/noindex.html", render = false }, ExpectSuccess: true);
            case "tools.urlCheck":
                return new RouteDrivePlan(concrete, new { url = fixtureRoot }, ExpectSuccess: true);

            case "exports.create":
                return new RouteDrivePlan(concrete, new { auditId, format = "csv", datasets = new[] { "summary" } }, ExpectSuccess: true);

            case "schedules.create":
                // The configuration must belong to the schedule's project
                // (SchedulesController validates the pairing) → sweep config.
                return new RouteDrivePlan(concrete, new
                {
                    configurationId = sweepConfigId.ToString(inv),
                    name = "Conformance sweep schedule create",
                    enabled = false,
                    cron = "interval:1.00:00:00",
                    completionActions = Array.Empty<object>()
                }, ExpectSuccess: true);
            case "schedules.update":
                return new RouteDrivePlan(concrete, new { name = "Conformance sweep schedule (renamed)" }, ExpectSuccess: true);
            case "schedules.delete":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);
            case "schedules.enable":
            case "schedules.disable":
                return new RouteDrivePlan($"/api/schedules/{_fixture.ScheduleId}/{operation.Key.Split('.')[1]}", null, ExpectSuccess: true);
            case "schedules.runNow":
                // The scheduler's no-overlap guard legitimately conflicts when
                // another run of the same schedule is active (hub/MCP tests
                // also fire run-now); envelope conformance holds for either.
                return new RouteDrivePlan($"/api/schedules/{_fixture.ScheduleId}/run-now", null, ExpectSuccess: false, OutcomeAgnostic: true);

            case "destinations.create":
                return new RouteDrivePlan(concrete, new { name = "Conformance sweep destination create", kind = "local-folder", target = sweepDestinationDir }, ExpectSuccess: true);
            case "destinations.update":
                return new RouteDrivePlan(concrete, new { name = "Conformance sweep destination (renamed)" }, ExpectSuccess: true);
            case "destinations.delete":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);
            case "destinations.test":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);
            case "destinations.deliver":
                return new RouteDrivePlan(concrete, new { exportId = _fixture.ExportId.ToString(inv) }, ExpectSuccess: true);

            case "dictionary.add":
                return new RouteDrivePlan(concrete, new { word = "conformanceword", language = "en" }, ExpectSuccess: true);
            case "dictionary.remove":
                return new RouteDrivePlan(concrete, new { word = "conformanceword" }, ExpectSuccess: true);

            case "queries.create":
                return new RouteDrivePlan(concrete, new
                {
                    name = "Conformance sweep query create",
                    ruleTree = new
                    {
                        kind = "group",
                        @operator = "and",
                        children = new[] { new { kind = "condition", field = "severity", @operator = "eq", value = "error" } }
                    }
                }, ExpectSuccess: true);
            case "queries.update":
                return new RouteDrivePlan(concrete, new
                {
                    id = sweepQueryId.ToString(inv),
                    name = "Conformance sweep query (renamed)",
                    ruleTree = new
                    {
                        kind = "group",
                        @operator = "and",
                        children = new[] { new { kind = "condition", field = "severity", @operator = "eq", value = "error" } }
                    }
                }, ExpectSuccess: true);
            case "queries.delete":
                return new RouteDrivePlan($"/api/queries?id={sweepQueryId}", null, ExpectSuccess: true);

            case "logs.import":
                return new RouteDrivePlan(concrete, new { filePath = Path.Combine(_fixture.DataDirectory, "fixture-access.log"), format = "combined" }, ExpectSuccess: true);
            case "logs.verifyDns":
                // Synchronous DNS verification of the import's client IPs.
                return new RouteDrivePlan(concrete, new { }, ExpectSuccess: true);
            case "logs.delete":
                return new RouteDrivePlan(concrete, null, ExpectSuccess: true);

            case "matrix.upload":
                return new RouteDrivePlan(concrete, new
                {
                    rows = new[] { new { sourceUrl = FixtureSite.BaseUrl + "/redirect/chain-1", targetUrl = FixtureSite.BaseUrl + "/pages/redirect-target.html" } },
                    autoSeedAudit = false
                }, ExpectSuccess: true);

            case "system.compact":
                // Compaction legitimately conflicts while an audit is active
                // (SystemController.cs:86-89); the sweep may run beside bounded
                // audits. Envelope conformance is asserted for EITHER outcome.
                return new RouteDrivePlan(concrete, null, ExpectSuccess: false, OutcomeAgnostic: true);
            case "diagnostics.purgeArtifacts":
            case "plugins.rescan":
            case "settings.update":
                return new RouteDrivePlan(concrete, operation.Method == "PUT" ? new { } : null, ExpectSuccess: true);

            default:
                // Shape-tolerant drives: POST/PUT carry an empty object so model
                // validation produces the documented validation envelope; the
                // assertion is envelope conformance, not functional success.
                var body = operation.Method is "POST" or "PUT" ? new { } : null;
                return new RouteDrivePlan(concrete, body, ExpectSuccess: false);
        }
    }
}
