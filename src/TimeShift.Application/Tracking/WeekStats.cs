using TimeShift.Domain.Categories;
using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Tracking;

/// <summary>Сумма по категории верхнего уровня. <paramref name="Change"/> — разница с прошлой неделей; <c>null</c> — сравнивать не с чем.</summary>
public sealed record CategoryTotal(string? Key, string Name, TimeSpan Total, TimeSpan? Change);

/// <summary>Сумма параллельных занятий с одним названием.</summary>
public sealed record ParallelTotal(string Title, TimeSpan Total);

/// <summary>
/// Цифры недели (spec §5.7: /week): основная дорожка по категориям и по статусам — их суммы равны
/// <see cref="Total"/>, то есть прошедшему времени окна; параллельные занятия отдельно и в эту сумму не входят (§3.6).
/// </summary>
public sealed record WeekStats(
    TimeSpan Total,
    IReadOnlyList<CategoryTotal> Categories,
    TimeSpan Confirmed,
    TimeSpan Assumed,
    TimeSpan Questionable,
    IReadOnlyList<ParallelTotal> Parallel)
{
    /// <summary>Есть сравнение с прошлой неделей.</summary>
    public bool IsCompared => Categories.Any(c => c.Change is not null);

    /// <summary>Всего параллельных занятий (пересекающиеся считаются каждое).</summary>
    public TimeSpan ParallelSum => TimeSpan.FromTicks(Parallel.Sum(p => p.Total.Ticks));

    /// <summary>
    /// Считает прошедшую часть лент. Категории — как в «Итого» дня: подвиды сложены в свою категорию («Залипание»);
    /// <paramref name="categories"/> — набор пользователя (без него — только системные).
    /// <paramref name="previous"/> — основная дорожка того же окна неделей раньше; категория, которая была только
    /// там, остаётся в списке с нулём.
    /// </summary>
    public static WeekStats Of(
        IEnumerable<TimelinePiece> main,
        IEnumerable<TimelinePiece> parallel,
        IEnumerable<TimelinePiece>? previous = null,
        CategorySet? categories = null)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(parallel);
        var set = categories ?? CategorySet.System;
        var past = main.Where(p => !p.IsFuture).ToList();
        var now = ByCategory(past, set);
        var before = previous is null ? null : ByCategory(previous.Where(p => !p.IsFuture), set);
        var keys = before is null ? now.Keys : now.Keys.Union(before.Keys);
        var totals = keys
            .Select(key => new CategoryTotal(
                key.Length == 0 ? null : key,
                key.Length == 0 ? "Без категории" : set.Name(key),
                now.GetValueOrDefault(key),
                before is null ? null : now.GetValueOrDefault(key) - before.GetValueOrDefault(key)))
            .OrderByDescending(c => c.Total)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        TimeSpan Sum(SegmentStatus status) => Ticks(past.Where(p => p.Status == status));
        var parallelTotals = parallel
            .Where(p => !p.IsFuture)
            .GroupBy(p => p.Title)
            .Select(g => new ParallelTotal(g.Key, Ticks(g)))
            .OrderByDescending(p => p.Total)
            .ThenBy(p => p.Title, StringComparer.Ordinal)
            .ToList();
        return new WeekStats(
            Ticks(past),
            totals,
            Sum(SegmentStatus.Confirmed),
            Sum(SegmentStatus.Assumed),
            Sum(SegmentStatus.Questionable),
            parallelTotals);
    }

    /// <summary>Доли ✅/⚪/🟡 в целых процентах; в сумме ровно 100 (остаток — самым большим дробным частям).</summary>
    public (int Confirmed, int Assumed, int Questionable) Shares()
    {
        if (Total <= TimeSpan.Zero)
        {
            return (0, 0, 0);
        }

        double[] exact = [.. new[] { Confirmed, Assumed, Questionable }.Select(t => 100.0 * t.Ticks / Total.Ticks)];
        int[] whole = [.. exact.Select(e => (int)Math.Floor(e))];
        foreach (var i in Enumerable.Range(0, 3).OrderByDescending(i => exact[i] - whole[i]).Take(100 - whole.Sum()))
        {
            whole[i]++;
        }

        return (whole[0], whole[1], whole[2]);
    }

    private static Dictionary<string, TimeSpan> ByCategory(IEnumerable<TimelinePiece> pieces, CategorySet set) => pieces
        .GroupBy(p => set.TotalsKey(p.CategoryKey) ?? string.Empty)
        .ToDictionary(g => g.Key, Ticks, StringComparer.Ordinal);

    private static TimeSpan Ticks(IEnumerable<TimelinePiece> pieces) => TimeSpan.FromTicks(pieces.Sum(p => p.Duration.Ticks));
}
