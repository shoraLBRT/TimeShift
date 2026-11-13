namespace TimeShift.Domain.Timelines;

/// <summary>
/// Материализованные отрезки одного пользователя (spec §3.2, §7). Инварианты: отсортированы по началу,
/// не пересекаются, открытый отрезок не больше одного (и он последний — открытый тянется в бесконечность).
/// </summary>
public sealed class Timeline
{
    private List<Segment> _segments;

    public Timeline(IEnumerable<Segment>? segments = null)
    {
        _segments = Normalize(segments ?? [], nameof(segments));
    }

    public IReadOnlyList<Segment> Segments => _segments;

    public Segment? OpenSegment => _segments.Count > 0 && _segments[^1].IsOpen ? _segments[^1] : null;

    /// <summary>
    /// Вставляет входящие отрезки, обрезая и разрезая пересекающиеся существующие.
    /// Открытый существующий отрезок, задетый входящими, закрывается в начале первого из них; его продолжение
    /// после них не сохраняется. Входящий открытый отрезок занимает всё время от своего начала.
    /// </summary>
    public TimelineChange Apply(IReadOnlyList<Segment> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var added = Normalize(incoming, nameof(incoming));
        if (added.Count == 0)
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
            if (existing.IsOpen)
            {
                var cut = overlapping[0].StartUtc;
                if (cut > existing.StartUtc)
                {
                    after.Add(existing.WithRange(existing.StartUtc, cut));
                }

                continue;
            }

            var cursor = existing.StartUtc;
            var end = existing.EndUtc!.Value;
            foreach (var cover in overlapping)
            {
                if (cover.StartUtc > cursor)
                {
                    after.Add(existing.WithRange(cursor, cover.StartUtc));
                }

                if (cover.EffectiveEndUtc > cursor)
                {
                    cursor = cover.EffectiveEndUtc;
                }

                if (cursor >= end)
                {
                    break;
                }
            }

            if (cursor < end)
            {
                after.Add(existing.WithRange(cursor, end));
            }
        }

        after.AddRange(added);
        after.Sort(ByStart);
        _segments = Normalize(kept.Concat(after), nameof(incoming));
        return new TimelineChange(before, after);
    }

    /// <summary>Откатывает изменение: убирает <see cref="TimelineChange.After"/>, возвращает <see cref="TimelineChange.Before"/>.</summary>
    public void Undo(TimelineChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = _segments.ToList();
        foreach (var segment in change.After)
        {
            if (!current.Remove(segment))
            {
                throw new InvalidOperationException("Лента изменилась после этого изменения — откат невозможен.");
            }
        }

        current.AddRange(change.Before);
        _segments = Normalize(current, nameof(change));
    }

    private static List<Segment> Normalize(IEnumerable<Segment> segments, string paramName)
    {
        var list = segments.ToList();
        if (list.Any(s => s is null))
        {
            throw new ArgumentException("Пустой отрезок.", paramName);
        }

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

    private static int ByStart(Segment a, Segment b) => a.StartUtc.CompareTo(b.StartUtc);
}
