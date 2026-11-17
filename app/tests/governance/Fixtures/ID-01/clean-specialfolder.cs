using Microsoft.Extensions.Logging;

namespace Fixture.Id01;

/// <summary>
/// Regression (wave-0C gate): SpecialFolder path-enum expressions inside telemetry
/// payloads are directory locations, never identity dimensions. Must pass ALL rules.
/// </summary>
public sealed class PathLogger
{
    private readonly ILogger<PathLogger> _logger;

    public PathLogger(ILogger<PathLogger> logger) => _logger = logger;

    public void LogDataLocation()
    {
        _logger.LogInformation(
            "Data root resolved under {ProfileRoot}.",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
