using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Periods;

/// <summary>
/// Детерминированный разбор периода (spec §3.5). Чистая функция.
/// <list type="number">
/// <item>Пункты <see cref="DurationSpec.Range"/> — якоря, делят окно на под-окна; прочие пункты живут в своём под-окне.</item>
/// <item>Пункты с минутами и фактами расставляются по порядку.</item>
/// <item>Один пункт «остаток» / без длительности на под-окно забирает остаток; больше одного — уточнение.</item>
/// <item>Нет факта → <see cref="ClarificationReason.MissingFact"/>.</item>
/// <item>Перебор больше шага округления → <see cref="ClarificationReason.Overflow"/>; в пределах шага — ужимаем последние пункты.</item>
/// <item>Недобор без «остатка» → явный <see cref="PeriodProposal.UnaccountedTails"/>, ничего не выдумываем.</item>
/// <item>Внутренние границы округляются к ближайшему кратному шагу в местном времени; A и B не двигаются;
/// куски нулевой длины после округления выбрасываются.</item>
/// </list>
/// </summary>
public static class PeriodResolver
{
    public static PeriodResolution Resolve(PeriodRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Items);
        ArgumentNullException.ThrowIfNull(request.Facts);
        ArgumentNullException.ThrowIfNull(request.TimeZone);
        if (request.RoundingMinutes is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "RoundingMinutes должен быть от 1 до 60.");
        }

        if (request.Items.Count == 0)
        {
            throw new ArgumentException("Нет пунктов для разбора.", nameof(request));
        }

        var items = request.Items;
        var window = request.Window;
        var step = TimeSpan.FromMinutes(request.RoundingMinutes);
        var reasons = new List<ClarificationReason>();

        // Длительности известных пунктов; отсутствующие факты собираем все сразу.
        var known = new Dictionary<int, TimeSpan>();
        var missing = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            switch (items[i].Duration)
            {
                case DurationSpec.Minutes m:
                    known[i] = TimeSpan.FromMinutes(m.Value);
                    break;
                case DurationSpec.FromFact f when request.Facts.TryGetValue(f.Key, out var minutes):
                    if (minutes <= 0)
                    {
                        throw new ArgumentException($"Факт '{f.Key}' должен быть > 0 минут.", nameof(request));
                    }

                    known[i] = TimeSpan.FromMinutes(minutes);
                    break;
                case DurationSpec.FromFact f:
                    missing.Add(i);
                    if (!reasons.OfType<ClarificationReason.MissingFact>().Any(r => r.Key == f.Key))
                    {
                        reasons.Add(new ClarificationReason.MissingFact(f.Key));
                    }

                    break;
            }
        }

        // Якоря.
        var anchors = new Dictionary<int, TimeRange>();
        var previousEnd = window.StartUtc;
        var anchorsOk = true;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Duration is not DurationSpec.Range range)
            {
                continue;
            }

            var placed = PlaceRange(range, window, request.TimeZone, step, previousEnd, out var fitsWindow);
            if (placed is { } anchor)
            {
                anchors[i] = anchor;
                previousEnd = anchor.EndUtc;
            }
            else
            {
                anchorsOk = false;
                reasons.Add(fitsWindow
                    ? new ClarificationReason.ConflictingRanges(i)
                    : new ClarificationReason.RangeOutsideWindow(i));
            }
        }

        if (!anchorsOk)
        {
            return new NeedsClarification(reasons);
        }

        var subWindows = SplitIntoSubWindows(items, anchors, window);

        // Проверки под-окон.
        foreach (var sub in subWindows)
        {
            var restCount = sub.Items.Count(i => IsRest(items[i]));
            if (restCount > 1)
            {
                reasons.Add(new ClarificationReason.TooManyUnspecified(restCount));
            }

            if (sub.Items.Any(missing.Contains))
            {
                continue;
            }

            var total = Sum(sub.Items.Where(known.ContainsKey).Select(i => known[i]));
            var length = sub.End - sub.Start;
            if (total > length + step)
            {
                reasons.Add(new ClarificationReason.Overflow(
                    (int)Math.Round(total.TotalMinutes), (int)Math.Round(length.TotalMinutes)));
            }
        }

        if (reasons.Count > 0)
        {
            return new NeedsClarification(reasons);
        }

        // Раскладка без округления: непрерывная цепочка кусков от A до B (null — неучтённый хвост).
        var raw = new List<(int? Item, DateTime Start, DateTime End)>();
        foreach (var sub in subWindows)
        {
            LayOut(sub, items, known, raw);
            if (sub.AnchorAfter is { } anchorIndex)
            {
                raw.Add((anchorIndex, anchors[anchorIndex].StartUtc, anchors[anchorIndex].EndUtc));
            }
        }

        return Round(raw, items, window, request.TimeZone, step);
    }

    private static bool IsRest(PeriodItem item) => item.Duration is DurationSpec.Rest or DurationSpec.Unspecified;

    private static List<SubWindow> SplitIntoSubWindows(
        IReadOnlyList<PeriodItem> items, Dictionary<int, TimeRange> anchors, TimeRange window)
    {
        var result = new List<SubWindow>();
        var cursor = window.StartUtc;
        var current = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (anchors.TryGetValue(i, out var anchor))
            {
                result.Add(new SubWindow(cursor, anchor.StartUtc, current, i));
                current = [];
                cursor = anchor.EndUtc;
            }
            else
            {
                current.Add(i);
            }
        }

        result.Add(new SubWindow(cursor, window.EndUtc, current, null));
        return result;
    }

    private static void LayOut(
        SubWindow sub,
        IReadOnlyList<PeriodItem> items,
        Dictionary<int, TimeSpan> known,
        List<(int? Item, DateTime Start, DateTime End)> raw)
    {
        var length = sub.End - sub.Start;
        var durations = sub.Items.Where(known.ContainsKey).ToDictionary(i => i, i => known[i]);

        // Перебор в пределах шага: ужимаем известные пункты с конца.
        var excess = Sum(durations.Values) - length;
        for (var k = sub.Items.Count - 1; k >= 0 && excess > TimeSpan.Zero; k--)
        {
            var index = sub.Items[k];
            if (!durations.TryGetValue(index, out var duration))
            {
                continue;
            }

            var cut = duration < excess ? duration : excess;
            durations[index] = duration - cut;
            excess -= cut;
        }

        var rest = length - Sum(durations.Values);
        var hasRest = sub.Items.Any(i => IsRest(items[i]));
        var cursor = sub.Start;
        foreach (var index in sub.Items)
        {
            var duration = durations.TryGetValue(index, out var d) ? d : rest;
            raw.Add((index, cursor, cursor + duration));
            cursor += duration;
        }

        if (!hasRest && cursor < sub.End)
        {
            raw.Add((null, cursor, sub.End));
        }
    }

    private static PeriodProposal Round(
        List<(int? Item, DateTime Start, DateTime End)> raw,
        IReadOnlyList<PeriodItem> items,
        TimeRange window,
        TimeZoneInfo timeZone,
        TimeSpan step)
    {
        // Границы: A, начала кусков 1..n-1, B. Внутренние — к сетке, монотонно, в пределах [A, B].
        var bounds = new DateTime[raw.Count + 1];
        bounds[0] = window.StartUtc;
        bounds[raw.Count] = window.EndUtc;
        for (var i = 1; i < raw.Count; i++)
        {
            var rounded = RoundToLocalGrid(raw[i].Start, timeZone, step);
            if (rounded < bounds[i - 1])
            {
                rounded = bounds[i - 1];
            }

            if (rounded > window.EndUtc)
            {
                rounded = window.EndUtc;
            }

            bounds[i] = rounded;
        }

        var segments = new List<Segment>();
        var tails = new List<TimeRange>();
        for (var i = 0; i < raw.Count; i++)
        {
            var start = bounds[i];
            var end = bounds[i + 1];
            if (end <= start)
            {
                continue;
            }

            if (raw[i].Item is { } index)
            {
                var item = items[index];
                segments.Add(new Segment(
                    start, end, item.Title, SegmentStatus.Confirmed, SegmentSource.User, item.ActivityId, item.CategoryHint));
            }
            else if (tails.Count > 0 && tails[^1].EndUtc == start)
            {
                tails[^1] = new TimeRange(tails[^1].StartUtc, end);
            }
            else
            {
                tails.Add(new TimeRange(start, end));
            }
        }

        return new PeriodProposal(segments, tails);
    }

    /// <summary>Ближайшее кратное шагу в местном времени (половина — вверх), обратно — по смещению исходного момента.</summary>
    private static DateTime RoundToLocalGrid(DateTime utc, TimeZoneInfo timeZone, TimeSpan step)
    {
        var local = LocalTime.ToLocal(utc, timeZone);
        var offset = local - DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        var ticks = (local.Ticks + (step.Ticks / 2)) / step.Ticks * step.Ticks;
        return new DateTime(ticks - offset.Ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Переводит «с … до …» в UTC: самая ранняя дата, при которой интервал целиком в окне (допуск — шаг округления
    /// у краёв окна, с прижатием к A/B) и не раньше конца предыдущего якоря.
    /// </summary>
    private static TimeRange? PlaceRange(
        DurationSpec.Range range,
        TimeRange window,
        TimeZoneInfo timeZone,
        TimeSpan step,
        DateTime previousEnd,
        out bool fitsWindow)
    {
        fitsWindow = false;
        var firstDate = DateOnly.FromDateTime(LocalTime.ToLocal(window.StartUtc, timeZone)).AddDays(-1);
        var lastDate = DateOnly.FromDateTime(LocalTime.ToLocal(window.EndUtc, timeZone));
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var endDate = range.End < range.Start ? date.AddDays(1) : date;
            var start = LocalTime.ToUtc(date.ToDateTime(range.Start), timeZone);
            var end = LocalTime.ToUtc(endDate.ToDateTime(range.End), timeZone);

            if (start < window.StartUtc && window.StartUtc - start <= step)
            {
                start = window.StartUtc;
            }

            if (end > window.EndUtc && end - window.EndUtc <= step)
            {
                end = window.EndUtc;
            }

            if (start < window.StartUtc || end > window.EndUtc || end <= start)
            {
                continue;
            }

            fitsWindow = true;
            if (start >= previousEnd)
            {
                return new TimeRange(start, end);
            }
        }

        return null;
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan> values) =>
        values.Aggregate(TimeSpan.Zero, (acc, v) => acc + v);

    private sealed record SubWindow(DateTime Start, DateTime End, List<int> Items, int? AnchorAfter);
}
