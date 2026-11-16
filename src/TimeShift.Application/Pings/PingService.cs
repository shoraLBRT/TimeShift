using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Pings;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Pings;

/// <summary>Итог одного прохода: сколько пингов ушло и какие пользователи упали с ошибкой.</summary>
public sealed record PingRunResult(int Sent, IReadOnlyList<(Guid UserId, Exception Error)> Failures);

/// <summary>
/// Проход по пользователям (spec §5.2): кому пора — отправить пинг, предыдущий неотвеченный удалить.
/// Вызывается фоновым циклом раз в минуту; ошибка одного пользователя не мешает остальным.
/// </summary>
public sealed class PingService(
    IUserRepository users,
    TrackingService tracking,
    Llm.IntentApplier intents,
    DailyReview daily,
    INotifier notifier,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<PingRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sent = 0;
        var failures = new List<(Guid, Exception)>();
        foreach (var user in await users.ListOnboardedAsync(cancellationToken))
        {
            // Пользователю без Telegram пинги слать некуда (для него будет Web Push).
            if (user.TelegramChatId is null)
            {
                continue;
            }

            try
            {
                if (await PingIfDueAsync(user, now, cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add((user.Id, e));
            }
        }

        return new PingRunResult(sent, failures);
    }

    /// <summary>Когда пользователю следующий пинг (<c>null</c> — пауза до его сообщения).</summary>
    public static DateTime? NextDue(User user, CurrentActivity? current, DateTime nowUtc) => PingSchedule.NextDue(new PingContext
    {
        NowUtc = nowUtc,
        Interval = TimeSpan.FromMinutes(user.Settings.PingIntervalMin),
        LastPingAtUtc = user.LastPingAtUtc,
        LastSeenAtUtc = user.LastSeenAtUtc,
        UnansweredPings = user.UnansweredPings,
        MaxUnansweredPings = user.Settings.MaxUnansweredPings,
        OpenActivity = current?.ToOpenActivity(),
        Template = user.Template(),
        PingDuringBlocks = user.Settings.PingDuringBlocks,
        QuietUntilUtc = user.QuietUntilUtc,
    });

    private async Task<bool> PingIfDueAsync(User user, DateTime now, CancellationToken cancellationToken)
    {
        // Предложение периода без ответа — записать как ⚪ (spec §3.5 п. 7).
        if (await intents.FinalizeExpiredAsync(user, now, cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Вечерний разбор — за N минут до сна по шаблону (spec §5.4), не в тихие часы.
        if (daily.IsEveningDue(user, now) && !(user.QuietUntilUtc > now))
        {
            await notifier.SendAsync(user, await daily.EveningAsync(user, now, cancellationToken), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        var current = await tracking.CurrentAsync(user, cancellationToken);
        if (NextDue(user, current, now) is not { } due || due > now)
        {
            return false;
        }

        var recent = await tracking.RecentAsync(user, TrackingReplies.RecentCount + 1, cancellationToken);
        var reply = TrackingReplies.Ping(current, recent, now, user.TimeZoneInfo);
        var messageRef = await notifier.SendAsync(user, reply, cancellationToken);
        var previous = user.RegisterPing(now, messageRef);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (previous is not null)
        {
            await notifier.DeleteAsync(user, previous, cancellationToken);
        }

        return true;
    }
}
