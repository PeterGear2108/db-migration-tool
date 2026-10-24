using Microsoft.Extensions.Logging;

namespace DBMigrationTool.Utilities;

public class MicrosoftLoggerAdapter : ILoggerAdapter
{
    private readonly ILogger _logger;

    public MicrosoftLoggerAdapter(ILogger logger)
    {
        _logger = logger;
    }

    public void Info(string message) => _logger.LogInformation("{Message}", message);
    public void Warn(string message) => _logger.LogWarning("{Message}", message);
}
