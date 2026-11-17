using Microsoft.Extensions.Logging;

namespace Fixture.Id01;

public sealed class IdentityEnrichedLogger
{
    private readonly ILogger<IdentityEnrichedLogger> _logger;

    public IdentityEnrichedLogger(ILogger<IdentityEnrichedLogger> logger) => _logger = logger;

    public void LogAuditStarted(string auditId, string machineId)
    {
        _logger.LogInformation("Audit {AuditId} started on {MachineId}.", auditId, machineId);
    }
}
