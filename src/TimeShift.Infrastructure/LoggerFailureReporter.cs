using Microsoft.Extensions.Logging;
using TimeShift.Application.Abstractions;

namespace TimeShift.Infrastructure;

/// <summary>Сбои — в обычный лог приложения.</summary>
internal sealed partial class LoggerFailureReporter(ILogger<LoggerFailureReporter> logger) : IFailureReporter
{
    public void Report(Exception exception, string message) => LogFailure(exception, message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private partial void LogFailure(Exception exception, string message);
}
