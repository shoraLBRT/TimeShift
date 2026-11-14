using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Schedule;

/// <summary>
/// Шаблон недели пользователя: блоки в местном времени + часовой пояс (spec §2, §3.2).
/// Блоки не пересекаются — с учётом перехода через полночь и с воскресенья на понедельник.
/// </summary>
public sealed class WeekTemplate
{
    private const long TicksPerWeek = TimeSpan.TicksPerDay * 7;

    public WeekTemplate(IEnumerable<ScheduleBlock> blocks, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(timeZone);
        Blocks = blocks.ToList();
        TimeZone = timeZone;
        EnsureNoOverlaps(Blocks);
    }

    public IReadOnlyList<ScheduleBlock> Blocks { get; }

    public TimeZoneInfo TimeZone { get; }

    public static WeekTemplate Empty(TimeZoneInfo timeZone) => new([], timeZone);

    /// <summary>Вхождения блоков, пересекающие окно (не обрезаны по окну), упорядочены по началу.</summary>
    public IReadOnlyList<BlockOccurrence> Occurrences(TimeRange window)
    {
        var firstDate = DateOnly.FromDateTime(LocalTime.ToLocal(window.StartUtc, TimeZone)).AddDays(-1);
        var lastDate = DateOnly.FromDateTime(LocalTime.ToLocal(window.EndUtc, TimeZone));

        var result = new List<BlockOccurrence>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            foreach (var block in Blocks)
            {
                if (!block.Days.Contains(date.DayOfWeek))
                {
                    continue;
                }

                var endDate = block.CrossesMidnight ? date.AddDays(1) : date;
                var start = LocalTime.ToUtc(date.ToDateTime(block.Start), TimeZone);
                var end = LocalTime.ToUtc(endDate.ToDateTime(block.End), TimeZone);

                // Блок целиком внутри «дыры» перевода часов схлопывается — пропускаем.
                if (end <= start || end <= window.StartUtc || start >= window.EndUtc)
                {
                    continue;
                }

                result.Add(new BlockOccurrence(block, new TimeRange(start, end)));
            }
        }

        result.Sort((a, b) => a.Range.StartUtc.CompareTo(b.Range.StartUtc));
        return result;
    }

    /// <summary>Первая пара пересекающихся блоков (с учётом полуночи и конца недели) или <c>null</c>.</summary>
    public static (ScheduleBlock First, ScheduleBlock Second)? FindOverlap(IReadOnlyList<ScheduleBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        // Каждое вхождение — интервал на «кольце» недели в тиках; переход через конец недели режем на два.
        var intervals = new List<(long Start, long End, int Block)>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            foreach (var day in block.Days)
            {
                var start = ((int)day * TimeSpan.TicksPerDay) + block.Start.Ticks;
                var end = start + block.Length.Ticks;
                if (end <= TicksPerWeek)
                {
                    intervals.Add((start, end, i));
                }
                else
                {
                    intervals.Add((start, TicksPerWeek, i));
                    intervals.Add((0, end - TicksPerWeek, i));
                }
            }
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < intervals.Count; i++)
        {
            if (intervals[i].Start < intervals[i - 1].End)
            {
                return (blocks[intervals[i - 1].Block], blocks[intervals[i].Block]);
            }
        }

        return null;
    }

    private static void EnsureNoOverlaps(IReadOnlyList<ScheduleBlock> blocks)
    {
        if (FindOverlap(blocks) is var (first, second))
        {
            throw new ArgumentException($"Блоки шаблона пересекаются: {first} и {second}.", nameof(blocks));
        }
    }
}
