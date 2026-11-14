namespace TimeShift.Domain.Timelines;

/// <summary>
/// Материализованные отрезки одного пользователя (spec §3.2, §3.6, §7). Две дорожки:
/// <list type="bullet">
/// <item>основная (<see cref="Segments"/>): отсортирована по началу, без пересечений, открытый отрезок не больше
/// одного и он последний (открытый тянется в бесконечность);</item>
/// <item>параллельная (<see cref="ParallelSegments"/>): закрытые отрезки поверх основной; могут пересекаться с чем
/// угодно и не вытесняют основную ленту.</item>
/// </list>
/// </summary>
public sealed class Timeline
{
    private List<Segment> _segments;
    private List<Segment> _parallel;

    public Timeline(IEnumerable<Segment>? segments = null)
    {
        var all = (segments ?? []).ToList();
        if (all.Any(s => s is null))
        {
            throw new ArgumentException("Пустой отрезок.", nameof(segments));
        }

        _segments = Normalize(all.Where(s => !s.IsParallel), nameof(segments));
        _parallel = SortParallel(all.Where(s => s.IsParallel));
    }

    /// <summary>Основная дорожка.</summary>
    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>Параллельные занятия, отсортированы по началу.</summary>
    public IReadOnlyList<Segment> ParallelSegments => _parallel;

    public Segment? OpenSegment => _segments.Count > 0 && _segments[^1].IsOpen ? _segments[^1] : null;

    /// <summary>
    /// Вставляет входящие отрезки. Параллельные просто добавляются на свою дорожку. Основные обрезают и разрезают
    /// пересекающиеся существующие:
    /// <list type="bullet">
    /// <item>закрытый существующий остаётся кусками до и после входящих;</item>
    /// <item>открытый существующий закрывается в начале первого входящего и <b>продолжается</b> после последнего —
    /// если входящие кончились раньше <paramref name="nowUtc"/>, ни один из них не открытый и занятие сохранило своё
    /// начало (обед внутри работы → работа идёт дальше). Если входящие доходят до «сейчас» или накрывают начало
    /// занятия, оно не продолжается;</item>
    /// <item>входящий открытый отрезок занимает всё время от своего начала.</item>
    /// </list>
    /// </summary>
    public TimelineChange Apply(IReadOnlyList<Segment> incoming, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        Utc.Ensure(nowUtc, nameof(nowUtc));
        if (incoming.Any(s => s is null))
        {
            throw new ArgumentException("Пустой отрезок.", nameof(incoming));
        }

        var added = Normalize(incoming.Where(s => !s.IsParallel), nameof(incoming));
        var addedParallel = SortParallel(incoming.Where(s => s.IsParallel));
        if (added.Count == 0 && addedParallel.Count == 0)
        {
            return new TimelineChange([], []);
        }

        var before = new List<Segment>();
        var after = new List<Segment>();
        var kept = new List<Segment>();
        foreach (var existing in _segments)
        {
            var overlapping = added.Where(existing.Overlaps).ToList();
            if (overlapping.Count == 0)
            {
                kept.Add(existing);
                continue;
            }

            before.Add(existing);
            after.AddRange(existing.IsOpen
                ? CutOpen(existing, overlapping, nowUtc)
                : CutClosed(existing, overlapping));
        }

        after.AddRange(added);
        after.Sort(ByStart);
        _segments = Normalize(kept.Concat(after), nameof(incoming));
        _parallel = SortParallel(_parallel.Concat(addedParallel));
        after.AddRange(addedParallel);
        return new TimelineChange(before, after);
    }

    /// <summary>Откатывает изменение: убирает <see cref="TimelineChange.After"/>, возвращает <see cref="TimelineChange.Before"/>.</summary>
    public void Undo(TimelineChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var main = _segments.ToList();
        var parallel = _parallel.ToList();
        foreach (var segment in change.After)
        {
            if (!(segment.IsParallel ? parallel : main).Remove(segment))
            {
                throw new InvalidOperationException("Лента изменилась после этого изменения — откат невозможен.");
            }
        }

        main.AddRange(change.Before.Where(s => !s.IsParallel));
        parallel.AddRange(change.Before.Where(s => s.IsParallel));
        _segments = Normalize(main, nameof(change));
        _parallel = SortParallel(parallel);
    }

    private static IEnumerable<Segment> CutClosed(Segment existing, List<Segment> covers)
    {
        var cursor = existing.StartUtc;
        var end = existing.EndUtc!.Value;
        foreach (var cover in covers)
        {
            if (cover.StartUtc > cursor)
            {
                yield return existing.WithRange(cursor, cover.StartUtc);
            }

            if (cover.EffectiveEndUtc > cursor)
            {
                cursor = cover.EffectiveEndUtc;
            }

            if (cursor >= end)
            {
                yield break;
            }
        }

        yield return existing.WithRange(cursor, end);
    }

    private static IEnumerable<Segment> CutOpen(Segment existing, List<Segment> covers, DateTime nowUtc)
    {
        var keepsStart = covers[0].StartUtc > existing.StartUtc;
        var elapsed = existing.ElapsedBefore;
        var cursor = existing.StartUtc;
        foreach (var cover in covers)
        {
            if (cover.StartUtc > cursor)
            {
                elapsed += cover.StartUtc - cursor;
                yield return existing.WithRange(cursor, cover.StartUtc);
            }

            if (cover.EffectiveEndUtc > cursor)
            {
                cursor = cover.EffectiveEndUtc;
            }
        }

        // Входящий открытый даёт cursor = MaxValue, так что продолжения нет.
        if (keepsStart && cursor < nowUtc)
        {
            yield return existing.ResumedAt(cursor, elapsed);
        }
    }

    private static List<Segment> Normalize(IEnumerable<Segment> segments, string paramName)
    {
        var list = segments.ToList();
        list.Sort(ByStart);
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].StartUtc < list[i - 1].EffectiveEndUtc)
            {
                throw new ArgumentException(
                    "Отрезки пересекаются (или открытый отрезок не последний).", paramName);
            }
        }

        return list;
    }

    private static List<Segment> SortParallel(IEnumerable<Segment> segments)
    {
        var list = segments.ToList();
        list.Sort(ByStart);
        return list;
    }

    private static int ByStart(Segment a, Segment b) => a.StartUtc.CompareTo(b.StartUtc);
}
