using TimeShift.Application.Pings;

namespace TimeShift.Host;

/// <summary>Раз в минуту — проход <see cref="PingService"/> (spec §5.2). Работает, только если есть клиент для отправки.</summary>
internal sealed partial class PingWorker(IServiceScopeFactory scopes, ILogger<PingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<PingService>().RunAsync(stoppingToken);
                if (result.Sent > 0)
                {
                    LogSent(result.Sent);
                }

                foreach (var (userId, error) in result.Failures)
                {
                    LogUserFailed(error, userId);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogRunFailed(e);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} ping(s)")]
    private partial void LogSent(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ping failed for user {UserId}")]
    private partial void LogUserFailed(Exception exception, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ping run failed")]
    private partial void LogRunFailed(Exception exception);
}
