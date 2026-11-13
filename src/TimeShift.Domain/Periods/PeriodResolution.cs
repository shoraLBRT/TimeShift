using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Periods;

/// <summary>Итог разбора периода: предложение или список уточнений.</summary>
public abstract record PeriodResolution;

/// <summary>
/// Предложение (spec §3.5, п. 7): непрерывные ✅-отрезки по описанию. <see cref="UnaccountedTails"/> — участки окна,
/// о которых пользователь ничего не сказал (лента покажет их фоном). Отрезки и хвосты вместе покрывают окно ровно.
/// </summary>
public sealed record PeriodProposal(IReadOnlyList<Segment> Segments, IReadOnlyList<TimeRange> UnaccountedTails)
    : PeriodResolution;

/// <summary>Разбор не может завершиться без ответа пользователя.</summary>
public sealed record NeedsClarification(IReadOnlyList<ClarificationReason> Reasons) : PeriodResolution;

/// <summary>Почему нужно уточнение.</summary>
public abstract record ClarificationReason
{
    private ClarificationReason()
    {
    }

    /// <summary>Нужен факт, а его нет (например, <c>route:зал→дом</c>).</summary>
    public sealed record MissingFact(string Key) : ClarificationReason;

    /// <summary>В одном под-окне больше одного пункта без длительности / «остаток».</summary>
    public sealed record TooManyUnspecified(int Count) : ClarificationReason;

    /// <summary>Известные длительности не влезают в (под-)окно больше чем на шаг округления.</summary>
    public sealed record Overflow(int TotalMinutes, int WindowMinutes) : ClarificationReason;

    /// <summary>Интервал «с … до …» пункта <paramref name="ItemIndex"/> не помещается в окно.</summary>
    public sealed record RangeOutsideWindow(int ItemIndex) : ClarificationReason;

    /// <summary>Интервал пункта <paramref name="ItemIndex"/> пересекается с предыдущим якорем или идёт раньше него.</summary>
    public sealed record ConflictingRanges(int ItemIndex) : ClarificationReason;
}
