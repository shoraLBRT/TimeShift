using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Timelines;

/// <summary>
/// Построение ленты из слоёв (spec §3.2, §3.3). Чистая функция: время «сейчас» передаётся явно.
/// Приоритет слоёв сверху вниз: материализованные отрезки → продление открытого занятия → блоки шаблона → фон.
/// Результат упорядочен, непрерывен, покрывает окно ровно; соседние куски с одинаковым содержимым слиты.
/// </summary>
public static class TimelineBuilder
{
    public const string BackgroundTitle = "Залипание";

    public static readonly TimeSpan DefaultUnknownDurationFallback = TimeSpan.FromMinutes(60);

    public static IReadOnlyList<TimelinePiece> Build(TimelineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Template);
        ArgumentNullException.ThrowIfNull(request.Segments);
        ArgumentNullException.ThrowIfNull(request.Policy);
        Utc.Ensure(request.NowUtc, nameof(request.NowUtc));
        if (request.UnknownDurationFallback <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "UnknownDurationFallback должен быть > 0.");
        }

        var window = request.Window;
        var now = request.NowUtc;
        var segments = PrepareSegments(request.Segments, window);
        var occurrences = request.Template.Occurrences(window);
        var extension = Extension(request);

        var points = new SortedSet<DateTime> { window.StartUtc, window.EndUtc };
        AddInside(points, window, now);
        foreach (var s in segments)
        {
            AddInside(points, window, s.StartUtc);
            AddInside(points, window, s.EndUtc!.Value);
        }

        foreach (var o in occurrences)
        {
            AddInside(points, window, o.Range.StartUtc);
            AddInside(points, window, o.Range.EndUtc);
        }

        foreach (var (range, _) in extension)
        {
            AddInside(points, window, range.StartUtc);
            AddInside(points, window, range.EndUtc);
        }

        var result = new List<TimelinePiece>();
        var bounds = points.ToList();
        int segIndex = 0, occIndex = 0;
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var start = bounds[i];
            var end = bounds[i + 1];

            // Отрезки и вхождения отсортированы и не пересекаются — идём указателями.
            while (segIndex < segments.Count && segments[segIndex].EndUtc <= start)
            {
                segIndex++;
            }

            while (occIndex < occurrences.Count && occurrences[occIndex].Range.EndUtc <= start)
            {
                occIndex++;
            }

            var segment = segIndex < segments.Count && segments[segIndex].StartUtc <= start ? segments[segIndex] : null;
            var occurrence = occIndex < occurrences.Count && occurrences[occIndex].Range.StartUtc <= start
                ? occurrences[occIndex]
                : null;

            Append(result, Resolve(start, end, now, segment, occurrence, extension, request.OpenActivity));
        }

        return result;
    }

    private static TimelinePiece Resolve(
        DateTime start,
        DateTime end,
        DateTime now,
        Segment? segment,
        BlockOccurrence? occurrence,
        IReadOnlyList<(TimeRange Range, SegmentStatus Status)> extension,
        OpenActivity? open)
    {
        var isFuture = start >= now;

        if (segment is not null)
        {
            return new TimelinePiece(
                start, end, segment.Title, segment.ActivityId, segment.CategoryKey, segment.Status, segment.Source, isFuture);
        }

        foreach (var (range, status) in extension)
        {
            if (range.StartUtc <= start && end <= range.EndUtc)
            {
                // Продление внутри блока шаблона или сна — всегда под вопросом (spec §3.3, последний абзац).
                var effective = occurrence is null ? status : SegmentStatus.Questionable;
                return new TimelinePiece(
                    start, end, open!.Title, open.ActivityId, open.CategoryKey, effective, SegmentSource.Extension, isFuture);
            }
        }

        if (occurrence is not null)
        {
            var block = occurrence.Block;
            return new TimelinePiece(
                start, end, block.Title, block.ActivityId, block.CategoryKey, SegmentStatus.Assumed, SegmentSource.Schedule, isFuture);
        }

        return new TimelinePiece(
            start, end, BackgroundTitle, null, DefaultCategories.Wasted, SegmentStatus.Questionable, SegmentSource.Background, isFuture);
    }

    /// <summary>Куски продления: <c>[S, S+T·norm)</c> ⚪, <c>[S+T·norm, S+T·max)</c> 🟡; никогда не позже «сейчас».</summary>
    private static List<(TimeRange Range, SegmentStatus Status)> Extension(TimelineRequest request)
    {
        var result = new List<(TimeRange, SegmentStatus)>();
        var open = request.OpenActivity;
        if (open is null)
        {
            return result;
        }

        Utc.Ensure(open.StartUtc, nameof(open.StartUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(open.Title);
        var now = request.NowUtc;
        var start = open.StartUtc;
        if (start >= now)
        {
            return result;
        }

        var policy = request.Policy;
        if (open.TypicalDuration is not { } typical)
        {
            // T неизвестна: берём запасную длительность, но всё продление — под вопросом.
            var end = Cap(start, request.UnknownDurationFallback, policy.MaxFactor, now);
            result.Add((new TimeRange(start, end), SegmentStatus.Questionable));
            return result;
        }

        if (typical <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Типичная длительность должна быть > 0.");
        }

        var normEnd = Cap(start, typical, policy.NormFactor, now);
        var maxEnd = Cap(start, typical, policy.MaxFactor, now);
        if (normEnd > start)
        {
            result.Add((new TimeRange(start, normEnd), SegmentStatus.Assumed));
        }

        if (maxEnd > normEnd)
        {
            result.Add((new TimeRange(normEnd, maxEnd), SegmentStatus.Questionable));
        }

        return result;
    }

    private static DateTime Cap(DateTime start, TimeSpan typical, double factor, DateTime now)
    {
        var available = (now - start).Ticks;
        if (double.IsPositiveInfinity(factor))
        {
            return now;
        }

        var ticks = typical.Ticks * factor;
        return ticks >= available ? now : start.AddTicks((long)ticks);
    }

    private static List<Segment> PrepareSegments(IReadOnlyList<Segment> segments, TimeRange window)
    {
        if (segments.Any(s => s.IsOpen))
        {
            throw new ArgumentException(
                "Открытый отрезок передаётся как OpenActivity, а не среди материализованных.", nameof(segments));
        }

        var sorted = segments.OrderBy(s => s.StartUtc).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].StartUtc < sorted[i - 1].EndUtc)
            {
                throw new ArgumentException("Материализованные отрезки пересекаются.", nameof(segments));
            }
        }

        return sorted.Where(s => s.StartUtc < window.EndUtc && s.EndUtc > window.StartUtc).ToList();
    }

    private static void AddInside(SortedSet<DateTime> points, TimeRange window, DateTime point)
    {
        if (window.StartUtc < point && point < window.EndUtc)
        {
            points.Add(point);
        }
    }

    private static void Append(List<TimelinePiece> result, TimelinePiece piece)
    {
        if (result.Count > 0 && result[^1].HasSameContent(piece))
        {
            result[^1] = result[^1] with { EndUtc = piece.EndUtc };
        }
        else
        {
            result.Add(piece);
        }
    }
}
