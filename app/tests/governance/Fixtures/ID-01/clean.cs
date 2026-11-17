using Microsoft.Extensions.Logging;

namespace Fixture.Id01;

public sealed class PrivacySafeLogger
{
    private readonly ILogger<PrivacySafeLogger> _logger;

    public PrivacySafeLogger(ILogger<PrivacySafeLogger> logger) => _logger = logger;

    public void LogAuditStarted(string auditId, int pageCount)
    {
        _logger.LogInformation("Audit {AuditId} started with {PageCount} pages.", auditId, pageCount);
    }
}
