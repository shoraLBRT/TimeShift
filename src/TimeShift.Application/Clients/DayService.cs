using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>Запросы ленты для клиента: день целиком (с прогнозом по шаблону) и итоги за период.</summary>
public sealed class DayService(TrackingService tracking, IClock clock)
{
    public const int MaxSummaryDays = 31;

    private static readonly DateOnly MinDate = new(2000, 1, 1);

    private static readonly DateOnly MaxDate = new(2100, 12, 31);

    /// <summary>День пользователя «сегодня» по часам сервера.</summary>
    public DateOnly Today(User user) => DateOnly.FromDateTime(LocalTime.ToLocal(clock.UtcNow, user.TimeZoneInfo));

    /// <summary>Лента дня; <paramref name="date"/> = <c>null</c> — сегодня. Ничего не меняет.</summary>
    public async Task<DayModel> GetAsync(User user, DateOnly? date, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var tz = user.TimeZoneInfo;
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, tz));
        var day = date ?? today;
        if (day < MinDate || day > MaxDate)
        {
            throw ClientException.Invalid("date", "Дата вне допустимого диапазона.");
        }

        var (dayStart, dayEnd) = Bounds(day, tz);
        var nowMinute = TrackingService.Minute(now);
        var timeline = await tracking.LoadAsync(user, dayStart, cancellationToken);
        var current = await tracking.CurrentAsync(user, cancellationToken);

        var window = new TimeRange(dayStart, dayEnd);
        var pieces = DayBuilder.Build(user, timeline, current, window, nowMinute);
        var parallel = ParallelPieces(window, timeline, nowMinute);
        var (totals, status, parallelMin) = Analyze(pieces.Select(p => p.Piece), parallel, nowMinute);

        var isToday = day == today;
        return new DayModel(
            day,
            user.TimeZone,
            isToday,
            dayStart,
            dayEnd,
            Minutes(dayEnd - dayStart),
            now,
            isToday ? Minutes(nowMinute - dayStart) : null,
            pieces.Select(p => ToDayPiece(p, dayStart, tz)).ToList(),
            parallel.Select(p => ToParallelPiece(p.Piece, p.Segment, dayStart, tz)).ToList(),
            current is null ? null : ToCurrent(current, now, tz),
            totals,
            status,
            parallelMin,
            new AttentionModel(user.UnansweredPings > 0, user.QuietUntilUtc > now ? user.QuietUntilUtc : null));
    }

    /// <summary>Итоги по дням и за весь период (не больше <see cref="MaxSummaryDays"/> дней); считается только прошедшее.</summary>
    public async Task<SummaryModel> SummaryAsync(User user, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (from < MinDate || to > MaxDate)
        {
            throw ClientException.Invalid("from", "Дата вне допустимого диапазона.");
        }

        if (to < from)
        {
            throw ClientException.Invalid("to", "Конец периода раньше начала.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxSummaryDays)
        {
            throw ClientException.Invalid("to", $"Период — не больше {MaxSummaryDays} дней.");
        }

        var now = clock.UtcNow;
        var tz = user.TimeZoneInfo;
        var nowMinute = TrackingService.Minute(now);
        var (firstStart, _) = Bounds(from, tz);
        var timeline = await tracking.LoadAsync(user, firstStart, cancellationToken);
        var current = await tracking.CurrentAsync(user, cancellationToken);

        var days = new List<SummaryDay>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var (dayStart, dayEnd) = Bounds(day, tz);
            if (dayStart >= nowMinute)
            {
                days.Add(new SummaryDay(day, [], new StatusMinutes(0, 0, 0), 0));
                continue;
            }

            var window = new TimeRange(dayStart, dayEnd);
            var pieces = DayBuilder.Build(user, timeline, current, window, nowMinute);
            var (totals, status, parallelMin) = Analyze(
                pieces.Select(p => p.Piece), ParallelPieces(window, timeline, nowMinute), nowMinute);
            days.Add(new SummaryDay(day, totals, status, parallelMin));
        }

        var all = days
            .SelectMany(d => d.Totals)
            .GroupBy(t => t.Category)
            .Select(g => new CategoryMinutes(g.Key, g.Sum(t => t.Minutes)))
            .OrderByDescending(t => t.Minutes)
            .ThenBy(t => t.Category, StringComparer.Ordinal)
            .ToList();
        return new SummaryModel(
            from,
            to,
            days,
            all,
            new StatusMinutes(days.Sum(d => d.Status.ConfirmedMin), days.Sum(d => d.Status.AssumedMin), days.Sum(d => d.Status.QuestionableMin)));
    }

    internal static (DateTime Start, DateTime End) Bounds(DateOnly day, TimeZoneInfo tz) =>
        (LocalTime.ToUtc(day.ToDateTime(TimeOnly.MinValue), tz), LocalTime.ToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));

    private static int Minutes(TimeSpan span) => (int)Math.Round(span.TotalMinutes);

    /// <summary>Параллельная дорожка: отрезки, обрезанные по окну, по порядку начала; пробелы не заполняются.</summary>
    private static List<(TimelinePiece Piece, Segment Segment)> ParallelPieces(TimeRange window, Timeline timeline, DateTime nowMinute) =>
        timeline.ParallelSegments
            .Where(s => s.StartUtc < window.EndUtc && s.EndUtc > window.StartUtc)
            .OrderBy(s => s.StartUtc)
            .Select(s =>
            {
                var start = s.StartUtc > window.StartUtc ? s.StartUtc : window.StartUtc;
                var end = s.EndUtc!.Value < window.EndUtc ? s.EndUtc.Value : window.EndUtc;
                return (new TimelinePiece(start, end, s.Title, s.ActivityId, s.CategoryKey, s.Status, s.Source, start >= nowMinute), s);
            })
            .ToList();

    /// <summary>Итоги прошедшей части основной дорожки: по категориям (по убыванию), по статусам и минуты параллельных.</summary>
    private static (IReadOnlyList<CategoryMinutes> Totals, StatusMinutes Status, int ParallelMin) Analyze(
        IEnumerable<TimelinePiece> main, IEnumerable<(TimelinePiece Piece, Segment Segment)> parallel, DateTime nowMinute)
    {
        var past = main.Where(p => !p.IsFuture).ToList();
        var totals = past
            .GroupBy(p => p.CategoryKey)
            .Select(g => new CategoryMinutes(g.Key, g.Sum(p => Minutes(p.Duration))))
            .OrderByDescending(t => t.Minutes)
            .ThenBy(t => t.Category, StringComparer.Ordinal)
            .ToList();
        int Sum(SegmentStatus status) => past.Where(p => p.Status == status).Sum(p => Minutes(p.Duration));
        var parallelMin = parallel
            .Where(p => p.Piece.StartUtc < nowMinute)
            .Sum(p => Minutes((p.Piece.EndUtc < nowMinute ? p.Piece.EndUtc : nowMinute) - p.Piece.StartUtc));
        return (totals, new StatusMinutes(Sum(SegmentStatus.Confirmed), Sum(SegmentStatus.Assumed), Sum(SegmentStatus.Questionable)), parallelMin);
    }

    private static DayPiece ToDayPiece(MappedPiece mapped, DateTime dayStart, TimeZoneInfo tz)
    {
        var piece = mapped.Piece;
        return new DayPiece(
            Minutes(piece.StartUtc - dayStart),
            Minutes(piece.EndUtc - dayStart),
            piece.StartUtc,
            piece.EndUtc,
            piece.Title,
            piece.CategoryKey,
            piece.Status,
            piece.Source,
            piece.IsFuture,
            mapped.IsOpen,
            piece.ActivityId,
            mapped.Segment is { } segment ? Link(segment, tz) : null);
    }

    private static DayPiece ToParallelPiece(TimelinePiece piece, Segment segment, DateTime dayStart, TimeZoneInfo tz) => new(
        Minutes(piece.StartUtc - dayStart),
        Minutes(piece.EndUtc - dayStart),
        piece.StartUtc,
        piece.EndUtc,
        piece.Title,
        piece.CategoryKey,
        piece.Status,
        piece.Source,
        piece.IsFuture,
        false,
        piece.ActivityId,
        Link(segment, tz));

    internal static PieceSegment Link(Segment segment, TimeZoneInfo tz) => new(
        segment.Id,
        LocalTime.ToLocal(segment.StartUtc, tz),
        segment.EndUtc is { } end ? LocalTime.ToLocal(end, tz) : null);

    private static CurrentModel ToCurrent(CurrentActivity current, DateTime now, TimeZoneInfo tz) => new(
        current.Segment.Id,
        current.Segment.ActivityId,
        current.Segment.Title,
        // Категория самой записи, как в pieces и totals: категория занятия — это настройка для будущих записей.
        current.Segment.CategoryKey,
        current.Segment.StartUtc,
        LocalTime.ToLocal(current.Segment.StartUtc, tz),
        Math.Max(0, Minutes(current.Elapsed(TrackingService.Minute(now)))),
        current.Activity?.TypicalDuration is { } typical ? Minutes(typical) : null);
}
