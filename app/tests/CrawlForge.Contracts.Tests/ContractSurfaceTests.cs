using System.Reflection;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using Xunit;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Contract surface completeness: every interface and entity DTO demanded by
/// the WP-00-02 deliverables exists in the locked assembly, and the uniform
/// REST envelope/pagination/error shapes match FS-CRAWL-001 §10.1.
/// </summary>
public class ContractSurfaceTests
{
    [Fact]
    public void AllCrawlEngineInterfacesExist()
    {
        Type[] expected =
        {
            typeof(ILimitsResolver),
            typeof(IRobotsTxtParser),
            typeof(IAimdPolitenessController),
            typeof(IUrlCanonicalizer),
            typeof(IUrlRewriteEngine),
            typeof(IPriorityFrontier),
            typeof(ICrawlCheckpointService),
            typeof(IHttpFetcher),
            typeof(IRedirectChainRecorder),
            typeof(ISignedRequestHandler),
            typeof(IPreCrawlValidator),
            typeof(IWafChallengeEvaluator),
            typeof(IWafSignatureCatalogue),
            typeof(IListModeIngestor),
            typeof(IRedirectMatrixValidator),
            typeof(IRenderingPool),
            typeof(ILoopbackInterceptingProxy),
            typeof(IScheduledAuditService),
        };

        Assert.Equal(18, expected.Length);
        AssertAllInContractAssembly(expected);
    }

    [Fact]
    public void AllAuditInterfacesExist()
    {
        Type[] expected =
        {
            typeof(IAuditPipeline),
            typeof(ITolerantDomParser),
            typeof(IElementInventoryExtractor),
            typeof(IContentScopeResolver),
            typeof(ISinglePageAnalyzer),
            typeof(IAuditRuleAnalyzer),
            typeof(IIndexabilityAnalyzer),
            typeof(IHeaderHtmlEquivalencyAnalyzer),
            typeof(IResponseRenderedComparer),
            typeof(ISoft404Detector),
            typeof(IContentQualityAnalyzer),
            typeof(ISpellingChecker),
            typeof(IReadabilityCalculator),
            typeof(IShingleNearDuplicateDetector),
            typeof(ISerpPixelMeasurer),
            typeof(ILinkZoneClassifier),
            typeof(IWeightedLinkEquityCalculator),
            typeof(IStructuredDataValidator),
            typeof(II18nAnalyzer),
            typeof(IMobileFriendlinessAnalyzer),
            typeof(ITechnologyDetector),
            typeof(IAccessibilityAnalyzer),
            typeof(ICoreWebVitalsAnalyzer),
            typeof(IPerformanceBudgetEvaluator),
            typeof(ICodeCoverageAnalyzer),
            typeof(ICustomContentExtractor),
            typeof(ICustomContentSearch),
            typeof(IJsSnippetRunner),
            typeof(IAuditScoringService),
            typeof(IFindingSuppressionService),
        };

        Assert.Equal(30, expected.Length);
        AssertAllInContractAssembly(expected);
    }

    [Fact]
    public void AllIntelligenceInterfacesExist()
    {
        Type[] expected =
        {
            typeof(INgramAnalyzer),
            typeof(IStopWordFilter),
            typeof(ILocalEmbeddingProvider),
            typeof(ISemanticLinkAnalyzer),
            typeof(IGeoReadinessAnalyzer),
            typeof(IAiBotGovernanceMatrix),
            typeof(ILlmsTxtGenerator),
            typeof(IMarTechAuditor),
            typeof(IDataLayerInspector),
            typeof(IConsentModeAnalyzer),
            typeof(ILogFileParser),
            typeof(IBotVerifier),
            typeof(ILogCorrelationEngine),
        };

        Assert.Equal(13, expected.Length);
        AssertAllInContractAssembly(expected);
    }

    [Fact]
    public void AllPersistenceSecurityApiInterfacesExist()
    {
        Type[] expected =
        {
            typeof(IDbProvider),
            typeof(ISchemaMigrator),
            typeof(IReadRepository<>),
            typeof(IWriteRepository<>),
            typeof(IContentStore),
            typeof(IRetentionPruner),
            typeof(IProjectArchiveService),
            typeof(ISecretProtector),
            typeof(ILoopbackPortHandshake),
            typeof(ICrashDumpHandler),
            typeof(IRealtimeHub),
            typeof(IMetricsExposition),
            typeof(IExportEngine),
            typeof(IExportDestinationDelivery),
            typeof(IMcpToolSurface),
            typeof(IMcpResourceSurface),
            typeof(IMcpPromptSurface),
            typeof(IAutomationActionLog),
        };

        Assert.Equal(18, expected.Length);
        AssertAllInContractAssembly(expected);
    }

    [Fact]
    public void AllFortyEntityDtosExist()
    {
        Type[] expected =
        {
            // 25 baseline tables (FS-CRAWL-001 §9.2).
            typeof(ApplicationSettingsDto),
            typeof(ProjectDto),
            typeof(CrawlConfigurationDto),
            typeof(AuditDto),
            typeof(UrlRecordDto),
            typeof(UrlElementDto),
            typeof(LinkEdgeDto),
            typeof(FindingDto),
            typeof(MetricSummaryDto),
            typeof(CrawlCheckpointDto),
            typeof(SchemaMigrationDto),
            typeof(SavedViewDto),
            typeof(ScheduleDefinitionDto),
            typeof(ScheduleRunDto),
            typeof(ExportDestinationDto),
            typeof(UserDictionaryEntryDto),
            typeof(ContentArtifactDto),
            typeof(UrlEmbeddingDto),
            typeof(ExtractedContentDto),
            typeof(ContentSearchResultDto),
            typeof(SnippetResultDto),
            typeof(TechnologyDetectionDto),
            typeof(AuditScoreDto),
            typeof(FindingSuppressionDto),
            typeof(AutomationActionLogDto),
            // 15 additive objects (FS-CRAWL-002 Appendix A).
            typeof(WafDetectionDto),
            typeof(WafUserSignatureDto),
            typeof(HostThrottleEventDto),
            typeof(ContentScopeProfileDto),
            typeof(NgramFrequencyDto),
            typeof(NgramSiteSummaryDto),
            typeof(UserStopwordDto),
            typeof(UiPluginDto),
            typeof(RedirectMappingDto),
            typeof(LogImportDto),
            typeof(LogUrlAggregateDto),
            typeof(SemanticLinkOpportunityDto),
            typeof(SemanticClusterDto),
            typeof(GovernanceMatrixRowDto),
            typeof(AnalyticsDetectionDto),
        };

        Assert.Equal(40, expected.Length);
        Assert.Equal(40, ContractAssembly.EntityTypes.Length);
        foreach (Type type in expected)
        {
            Assert.Contains(type, ContractAssembly.EntityTypes);
        }
    }

    [Fact]
    public void UniformEnvelopeHasDataPaginationAndError()
    {
        Type envelope = typeof(ApiEnvelope<>);
        Assert.NotNull(envelope.GetProperty(nameof(ApiEnvelope<object>.Data)));
        Assert.NotNull(envelope.GetProperty(nameof(ApiEnvelope<object>.Pagination)));
        Assert.NotNull(envelope.GetProperty(nameof(ApiEnvelope<object>.Error)));

        Type pagination = typeof(PaginationMetadata);
        Assert.NotNull(pagination.GetProperty(nameof(PaginationMetadata.Offset)));
        Assert.NotNull(pagination.GetProperty(nameof(PaginationMetadata.Limit)));
        Assert.NotNull(pagination.GetProperty(nameof(PaginationMetadata.TotalCount)));

        Type error = typeof(ApiError);
        Assert.NotNull(error.GetProperty(nameof(ApiError.Code)));
        Assert.NotNull(error.GetProperty(nameof(ApiError.Message)));
        Assert.NotNull(error.GetProperty(nameof(ApiError.FieldErrors)));

        Type fieldError = typeof(ApiFieldError);
        Assert.NotNull(fieldError.GetProperty(nameof(ApiFieldError.Field)));
        Assert.NotNull(fieldError.GetProperty(nameof(ApiFieldError.Reason)));
    }

    [Fact]
    public void AuditExecutionServiceHasTheAmendedStartAuditShape()
    {
        // Amendment WP-02-A1: the scheduler's additive audit-start port.
        Type service = typeof(IAuditExecutionService);
        Assert.True(service.IsInterface);
        Assert.Equal(ContractAssembly.Assembly, service.Assembly);

        MethodInfo method = Assert.Single(service.GetMethods());
        Assert.Equal(nameof(IAuditExecutionService.StartAuditAsync), method.Name);
        Assert.Equal(typeof(Task<long>), method.ReturnType);

        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(5, parameters.Length);
        Assert.Equal(typeof(long), parameters[0].ParameterType);
        Assert.Equal(typeof(long), parameters[1].ParameterType);
        Assert.Equal(typeof(ScheduleTriggerType), parameters[2].ParameterType);
        Assert.Equal(typeof(CrawlLimitOverrides), parameters[3].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[4].ParameterType);

        // The additive override payload stays optional forever (precedence rule 3).
        NullabilityInfo overrides = new NullabilityInfoContext().Create(parameters[3]);
        Assert.Equal(NullabilityState.Nullable, overrides.ReadState);
        Assert.NotNull(parameters[3].GetCustomAttribute<AdditiveMemberAttribute>());
        Assert.True(parameters[4].HasDefaultValue);
    }

    private static void AssertAllInContractAssembly(Type[] expected)
    {
        foreach (Type type in expected)
        {
            Assert.True(type.IsInterface, type.Name + " must be an interface.");
            Assert.Equal(ContractAssembly.Assembly, type.Assembly);
        }
    }
}
