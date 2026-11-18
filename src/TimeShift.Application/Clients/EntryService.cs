using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>Начать занятие: <paramref name="ActivityId"/> или <paramref name="Title"/>, с местного времени <paramref name="At"/> (по умолчанию — сейчас).</summary>
public sealed record StartRequest(Guid? ActivityId, string? Title, DateTime? At);

/// <summary>Записать прошедший период: <paramref name="Start"/> и <paramref name="End"/> — местное время.</summary>
public sealed record AddEntryRequest(DateTime Start, DateTime End, string? Title, Guid? ActivityId, string? Category, bool Parallel);

/// <summary>Правка отрезка: что не задано — не меняется.</summary>
public sealed record EditEntryRequest(DateTime? Start, DateTime? End, string? Title, string? Category);

/// <summary>
/// Операции клиента над лентой (то же, что бот делает кнопками и текстом, но по явным полям): начать, закончить,
/// продолжить, записать прошедшее, поправить, удалить, подтвердить, откатить, тихие часы. Каждая операция считается
/// «пользователь ответил» (непрочитанные пинги сбрасываются), пишется одним изменением журнала для отката и сохраняется.
/// </summary>
public sealed class EntryService(TrackingService tracking, ITimelineRepository timelines, IUnitOfWork unitOfWork, IClock clock)
{
    public const int MinQuietMinutes = 5;

    public const int MaxQuietMinutes = 1440;

    private const string FutureText = "Будущее не записывается.";

    public async Task StartAsync(User user, StartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var at = request.At is { } local ? FromLocal(user, local) : (DateTime?)null;
        if (at > TrackingService.Minute(now))
        {
            throw ClientException.Invalid("at", FutureText);
        }

        var activity = await ActivityForAsync(user, request.ActivityId, request.Title, cancellationToken);
        user.RegisterActivity(now);
        await tracking.StartAsync(user, activity, now, cancellationToken, at);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task StopAsync(User user, DateTime? atLocal, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var at = atLocal is { } local ? FromLocal(user, local) : (DateTime?)null;
        if (at > TrackingService.Minute(now))
        {
            throw ClientException.Invalid("at", FutureText);
        }

        user.RegisterActivity(now);
        if (await tracking.StopAsync(user, now, cancellationToken, at) is null)
        {
            throw ClientException.Conflict("Сейчас ничего не идёт.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>«Всё ещё занят»: идущее занятие подтверждается до «сейчас».</summary>
    public async Task ContinueAsync(User user, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        user.RegisterActivity(now);
        if (await tracking.ContinueAsync(user, now, cancellationToken) is null)
        {
            throw ClientException.Conflict("Сейчас ничего не идёт.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Записывает прошедший период как подтверждённый; вытесняет то, что было (параллельное — ничего не вытесняет).</summary>
    public async Task AddAsync(User user, AddEntryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var start = FromLocal(user, request.Start);
        var end = FromLocal(user, request.End);
        if (end <= start)
        {
            throw ClientException.Invalid("end", "Конец должен быть позже начала.");
        }

        if (end > TrackingService.Minute(now))
        {
            throw ClientException.Invalid("end", FutureText);
        }

        var category = ValidCategory(user, request.Category);
        var activity = await ActivityForAsync(user, request.ActivityId, request.Title, cancellationToken);
        if (category is not null)
        {
            activity.CategoryKey ??= category;
        }

        user.RegisterActivity(now);
        var segment = new Segment(
            start, end, activity.Name, SegmentStatus.Confirmed, SegmentSource.User, activity.Id,
            category ?? activity.CategoryKey, isParallel: request.Parallel);
        await tracking.ApplyAsync(user, [segment], now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Правит хранимый отрезок; освободившееся время возвращается шаблону и фону.</summary>
    public async Task EditAsync(User user, Guid segmentId, EditEntryRequest patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var now = clock.UtcNow;
        var nowMinute = TrackingService.Minute(now);
        var existing = await timelines.FindSegmentAsync(user.Id, segmentId, cancellationToken)
            ?? throw ClientException.NotFound("Такой записи нет.");

        var start = patch.Start is { } s ? FromLocal(user, s) : existing.StartUtc;
        var end = patch.End is { } e ? FromLocal(user, e) : existing.EndUtc;
        if (start > nowMinute)
        {
            throw ClientException.Invalid("start", FutureText);
        }

        if (end > nowMinute)
        {
            throw ClientException.Invalid("end", FutureText);
        }

        if (end <= start)
        {
            throw ClientException.Invalid("end", "Конец должен быть позже начала.");
        }

        var title = existing.Title;
        var activityId = existing.ActivityId;
        var category = existing.CategoryKey;
        if (patch.Title is { } rawTitle)
        {
            var name = ValidTitle(rawTitle);
            if (Activity.Normalize(name) != Activity.Normalize(existing.Title))
            {
                var activity = await tracking.ResolveActivityAsync(user, name, cancellationToken);
                (title, activityId, category) = (activity.Name, activity.Id, activity.CategoryKey ?? category);
            }
        }

        category = ValidCategory(user, patch.Category) ?? category;
        var replacement = new Segment(
            start, end, title, SegmentStatus.Confirmed, SegmentSource.User, activityId, category,
            existing.IsOpen && end is null ? existing.ElapsedBefore : default, existing.IsParallel);

        user.RegisterActivity(now);
        try
        {
            if (await tracking.EditAsync(user, existing, replacement, now, cancellationToken) is null)
            {
                throw ClientException.NotFound("Такой записи нет.");
            }
        }
        catch (Exception e2) when (e2 is ArgumentException or InvalidOperationException)
        {
            throw ClientException.Conflict("Так поправить запись нельзя — она конфликтует с соседними.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(User user, Guid segmentId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var existing = await timelines.FindSegmentAsync(user.Id, segmentId, cancellationToken)
            ?? throw ClientException.NotFound("Такой записи нет.");
        user.RegisterActivity(now);
        if (await tracking.DeleteAsync(user, existing, now, cancellationToken) is null)
        {
            throw ClientException.NotFound("Такой записи нет.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Подтверждает вычисленные куски диапазона как они показаны: ⚪ → ✅, 🟡 — только при <paramref name="includeQuestionable"/>.
    /// Продление идущего занятия подтверждается целиком до «сейчас» (как «продолжаю»), поэтому при 🟡 в нём — только
    /// вместе с <paramref name="includeQuestionable"/>. Куски, лежащие в времени идущего занятия, кроме его продления, не трогаются.
    /// </summary>
    public async Task ConfirmAsync(User user, DateTime startLocal, DateTime endLocal, bool includeQuestionable, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var nowMinute = TrackingService.Minute(now);
        var start = FromLocal(user, startLocal);
        var end = FromLocal(user, endLocal);
        if (end <= start)
        {
            throw ClientException.Invalid("end", "Конец должен быть позже начала.");
        }

        if (end > nowMinute)
        {
            throw ClientException.Invalid("end", FutureText);
        }

        var timeline = await tracking.LoadAsync(user, start, cancellationToken);
        var current = await tracking.CurrentAsync(user, cancellationToken);
        var pieces = DayBuilder.Build(user, timeline, current, new TimeRange(start, end), nowMinute);
        bool Eligible(TimelinePiece p) =>
            !p.IsFuture && (p.Status == SegmentStatus.Assumed || (includeQuestionable && p.Status == SegmentStatus.Questionable));

        var openStart = current?.Segment.StartUtc;
        var fresh = pieces
            .Where(m => m.Segment is null && !m.IsOpen && Eligible(m.Piece) && !(openStart is { } o && m.Piece.StartUtc >= o))
            .Select(m => new Segment(
                m.Piece.StartUtc, m.Piece.EndUtc, m.Piece.Title, SegmentStatus.Confirmed, SegmentSource.User,
                m.Piece.ActivityId, m.Piece.CategoryKey))
            .ToList();

        var extension = pieces.Where(m => m.IsOpen && !m.Piece.IsFuture).Select(m => m.Piece).ToList();
        var continueOpen = extension.Any(Eligible)
            && (includeQuestionable || extension.All(p => p.Status != SegmentStatus.Questionable));

        user.RegisterActivity(now);
        if (fresh.Count > 0)
        {
            await tracking.ApplyAsync(user, fresh, now, cancellationToken);
        }

        if (continueOpen)
        {
            // Повторная загрузка ленты читает базу — сначала сохраняем уже записанное.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tracking.ContinueAsync(user, now, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<UndoResult> UndoAsync(User user, CancellationToken cancellationToken)
    {
        user.RegisterActivity(clock.UtcNow);
        var result = await tracking.UndoAsync(user, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>Тихие часы: <paramref name="minutes"/> = 0 — снять; иначе от 5 минут до суток.</summary>
    public async Task SetQuietAsync(User user, int minutes, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (minutes != 0 && (minutes < MinQuietMinutes || minutes > MaxQuietMinutes))
        {
            throw ClientException.Invalid("minutes", $"Тихие часы — от {MinQuietMinutes} минут до суток, 0 — снять.");
        }

        user.RegisterActivity(now);
        user.SetQuietUntil(minutes == 0 ? null : TrackingService.Minute(now + TimeSpan.FromMinutes(minutes)));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static DateTime FromLocal(User user, DateTime local) =>
        TrackingService.Minute(LocalTime.ToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), user.TimeZoneInfo));

    private static string ValidTitle(string? title)
    {
        var name = title?.Trim() ?? string.Empty;
        if (name.Length is 0 or > Activity.MaxNameLength)
        {
            throw ClientException.Invalid("title", $"Название — от 1 до {Activity.MaxNameLength} символов.");
        }

        return name;
    }

    private static string? ValidCategory(User user, string? key)
    {
        if (key is null)
        {
            return null;
        }

        return user.Categories.FindActive(key) is not null ? key : throw ClientException.Invalid("category", "Нет такой категории.");
    }

    /// <summary>Занятие по Id или по названию (нового заводит); ровно одно из двух.</summary>
    private async Task<Activity> ActivityForAsync(User user, Guid? activityId, string? title, CancellationToken cancellationToken)
    {
        if (activityId is { } id)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                throw ClientException.Invalid("title", "Нужно что-то одно: название или занятие.");
            }

            return await tracking.GetActivityAsync(user, id, cancellationToken)
                ?? throw ClientException.NotFound("Такого занятия нет.");
        }

        return await tracking.ResolveActivityAsync(user, ValidTitle(title), cancellationToken);
    }
}
