using TimeShift.Domain.Periods;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Periods.ClarificationReason;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

public class PeriodResolverTests
{
    private const string RouteGymHome = "route:зал→дом";

    private static PeriodResolution Resolve(
        TimeRange window, IReadOnlyList<PeriodItem> items, Dictionary<string, int>? facts = null, int rounding = 5) =>
        PeriodResolver.Resolve(new PeriodRequest
        {
            Window = window,
            Items = items,
            Facts = facts ?? [],
            TimeZone = Moscow,
            RoundingMinutes = rounding,
        });

    private static PeriodProposal Proposal(PeriodResolution resolution, TimeRange window)
    {
        var proposal = Assert.IsType<PeriodProposal>(resolution);
        AssertExactCover(proposal, window);
        return proposal;
    }

    /// <summary>Отрезки и хвосты вместе ровно покрывают окно, без пустых кусков.</summary>
    internal static void AssertExactCover(PeriodProposal proposal, TimeRange window)
    {
        var all = proposal.Segments.Select(s => (s.StartUtc, End: s.EndUtc!.Value))
            .Concat(proposal.UnaccountedTails.Select(t => (t.StartUtc, End: t.EndUtc)))
            .OrderBy(x => x.StartUtc)
            .ToList();
        Assert.NotEmpty(all);
        Assert.Equal(window.StartUtc, all[0].StartUtc);
        Assert.Equal(window.EndUtc, all[^1].End);
        for (var i = 0; i < all.Count; i++)
        {
            Assert.True(all[i].End > all[i].StartUtc);
            if (i > 0)
            {
                Assert.Equal(all[i - 1].End, all[i].StartUtc);
            }
        }

        Assert.All(proposal.Segments, s => Assert.Equal((SegmentStatus.Confirmed, SegmentSource.User), (s.Status, s.Source)));
    }

    private static List<string> Lines(PeriodProposal p) => p.Segments.Select(s => Describe(s, Moscow)).ToList();

    private static string Tail(TimeRange r) => $"{Local(r.StartUtc, Moscow)[3..]}-{Local(r.EndUtc, Moscow)[3..]}";

    private static PeriodItem Item(string title, DurationSpec duration, string? category = null) => new(title, duration, category);

    private static DurationSpec.Minutes Min(int n) => new(n);

    [Fact]
    public void Spec_3_4_resolution_with_route_fact()
    {
        var window = new TimeRange(Msk(14), Msk(17));
        var result = Resolve(
            window,
            [Item("Тренировка", Min(60), "sport"), Item("Дорога зал → дом", new DurationSpec.FromFact(RouteGymHome), "commute"), Item("YouTube", new DurationSpec.Rest())],
            new() { [RouteGymHome] = 25 });

        var proposal = Proposal(result, window);
        Assert.Equal(
            [
                "14:00-15:00 Тренировка Confirmed",
                "15:00-15:25 Дорога зал → дом Confirmed",
                "15:25-17:00 YouTube Confirmed",
            ],
            Lines(proposal));
        Assert.Empty(proposal.UnaccountedTails);
        Assert.Null(proposal.Segments[2].CategoryKey); // категорию YouTube бот спросит отдельно
        Assert.Equal("commute", proposal.Segments[1].CategoryKey);
    }

    [Fact]
    public void Missing_route_fact_asks_for_it()
    {
        var result = Resolve(
            new TimeRange(Msk(14), Msk(17)),
            [Item("Тренировка", Min(60)), Item("Дорога зал → дом", new DurationSpec.FromFact(RouteGymHome)), Item("YouTube", new DurationSpec.Rest())]);

        var clarification = Assert.IsType<NeedsClarification>(result);
        Assert.Equal([new MissingFact(RouteGymHome)], clarification.Reasons);
    }

    [Fact]
    public void Every_missing_fact_is_reported_once()
    {
        var result = Resolve(
            new TimeRange(Msk(14), Msk(17)),
            [
                Item("Туда", new DurationSpec.FromFact("route:дом→зал")),
                Item("Обратно", new DurationSpec.FromFact(RouteGymHome)),
                Item("Ещё раз туда", new DurationSpec.FromFact("route:дом→зал")),
            ]);

        var clarification = Assert.IsType<NeedsClarification>(result);
        Assert.Equal([new MissingFact("route:дом→зал"), new MissingFact(RouteGymHome)], clarification.Reasons);
    }

    [Fact]
    public void Range_items_are_anchors_that_split_the_window()
    {
        var window = new TimeRange(Msk(12), Msk(18));
        var result = Resolve(
            window,
            [
                Item("Обед", Min(30)),
                Item("Встреча", new DurationSpec.Range(T(14), T(15))),
                Item("Дорога", Min(20)),
                Item("Работа", new DurationSpec.Rest()),
            ]);

        var proposal = Proposal(result, window);
        Assert.Equal(
            [
                "12:00-12:30 Обед Confirmed",
                "14:00-15:00 Встреча Confirmed",
                "15:00-15:20 Дорога Confirmed",
                "15:20-18:00 Работа Confirmed",
            ],
            Lines(proposal));
        Assert.Equal(["12:30-14:00"], proposal.UnaccountedTails.Select(Tail));
    }

    [Fact]
    public void Range_crossing_midnight_is_placed_on_the_right_day()
    {
        var window = new TimeRange(Msk(22), At(Moscow, 2026, 11, 14, 2));
        var result = Resolve(
            window,
            [Item("Ужин", new DurationSpec.Unspecified()), Item("Фильм", new DurationSpec.Range(T(23), T(1)))]);

        var proposal = Proposal(result, window);
        Assert.Equal(["22:00-23:00 Ужин Confirmed", "23:00-01:00 Фильм Confirmed"], Lines(proposal));
        Assert.Equal(["01:00-02:00"], proposal.UnaccountedTails.Select(Tail));
    }

    [Fact]
    public void Range_within_one_rounding_step_of_window_edge_is_clamped()
    {
        var window = new TimeRange(Msk(14, 3), Msk(17));
        var result = Resolve(window, [Item("Встреча", new DurationSpec.Range(T(14), T(15))), Item("Дом", new DurationSpec.Rest())]);

        var proposal = Proposal(result, window);
        Assert.Equal(["14:03-15:00 Встреча Confirmed", "15:00-17:00 Дом Confirmed"], Lines(proposal));
    }

    [Fact]
    public void Range_outside_window_needs_clarification()
    {
        var result = Resolve(
            new TimeRange(Msk(14), Msk(17)),
            [Item("Кино", new DurationSpec.Range(T(16), T(18)))]);

        Assert.Equal([new RangeOutsideWindow(0)], Assert.IsType<NeedsClarification>(result).Reasons);
    }

    [Fact]
    public void Conflicting_ranges_need_clarification()
    {
        var result = Resolve(
            new TimeRange(Msk(14), Msk(17)),
            [Item("A", new DurationSpec.Range(T(15), T(16))), Item("B", new DurationSpec.Range(T(14), T(15, 30)))]);

        Assert.Equal([new ConflictingRanges(1)], Assert.IsType<NeedsClarification>(result).Reasons);
    }

    [Fact]
    public void Overflow_beyond_rounding_step_needs_clarification()
    {
        var result = Resolve(new TimeRange(Msk(14), Msk(15)), [Item("A", Min(40)), Item("B", Min(40))]);

        Assert.Equal([new Overflow(80, 60)], Assert.IsType<NeedsClarification>(result).Reasons);
    }

    [Fact]
    public void Overflow_is_checked_per_sub_window()
    {
        var result = Resolve(
            new TimeRange(Msk(14), Msk(17)),
            [Item("A", Min(90)), Item("Встреча", new DurationSpec.Range(T(15), T(16)))]);

        Assert.Equal([new Overflow(90, 60)], Assert.IsType<NeedsClarification>(result).Reasons);
    }

    [Fact]
    public void Overflow_within_rounding_step_shrinks_the_last_item()
    {
        var window = new TimeRange(Msk(14), Msk(15));
        var proposal = Proposal(Resolve(window, [Item("A", Min(40)), Item("B", Min(23))]), window);

        Assert.Equal(["14:00-14:40 A Confirmed", "14:40-15:00 B Confirmed"], Lines(proposal));
        Assert.Empty(proposal.UnaccountedTails);
    }

    [Fact]
    public void Two_unspecified_items_in_one_sub_window_need_clarification()
    {
        var result = Resolve(
            new TimeRange(Msk(14), Msk(17)),
            [Item("A", new DurationSpec.Unspecified()), Item("B", Min(30)), Item("C", new DurationSpec.Rest())]);

        Assert.Equal([new TooManyUnspecified(2)], Assert.IsType<NeedsClarification>(result).Reasons);
    }

    [Fact]
    public void Unspecified_items_in_different_sub_windows_are_fine()
    {
        var window = new TimeRange(Msk(14), Msk(17));
        var result = Resolve(
            window,
            [Item("A", new DurationSpec.Unspecified()), Item("Встреча", new DurationSpec.Range(T(15), T(16))), Item("C", new DurationSpec.Unspecified())]);

        Assert.Equal(
            ["14:00-15:00 A Confirmed", "15:00-16:00 Встреча Confirmed", "16:00-17:00 C Confirmed"],
            Lines(Proposal(result, window)));
    }

    [Fact]
    public void Unfilled_window_without_rest_returns_unaccounted_tail()
    {
        var window = new TimeRange(Msk(14), Msk(17));
        var proposal = Proposal(Resolve(window, [Item("A", Min(60)), Item("B", Min(30))]), window);

        Assert.Equal(["14:00-15:00 A Confirmed", "15:00-15:30 B Confirmed"], Lines(proposal));
        Assert.Equal(["15:30-17:00"], proposal.UnaccountedTails.Select(Tail));
    }

    [Fact]
    public void Rest_item_in_the_middle_takes_the_remainder_in_place()
    {
        var window = new TimeRange(Msk(14), Msk(17));
        var proposal = Proposal(Resolve(window, [Item("A", Min(30)), Item("B", new DurationSpec.Rest()), Item("C", Min(30))]), window);

        Assert.Equal(["14:00-14:30 A Confirmed", "14:30-16:30 B Confirmed", "16:30-17:00 C Confirmed"], Lines(proposal));
    }

    [Fact]
    public void Interior_boundaries_round_to_grid_but_window_edges_stay()
    {
        var window = new TimeRange(Msk(14, 3), Msk(16, 58));
        var proposal = Proposal(Resolve(window, [Item("A", Min(46)), Item("B", new DurationSpec.Rest())]), window);

        // 14:03 + 46 = 14:49 → 14:50; A=14:03 и B=16:58 не двигаются.
        Assert.Equal(["14:03-14:50 A Confirmed", "14:50-16:58 B Confirmed"], Lines(proposal));
    }

    [Fact]
    public void Rounding_step_of_fifteen_minutes()
    {
        var window = new TimeRange(Msk(14), Msk(16));
        var proposal = Proposal(
            Resolve(window, [Item("A", Min(20)), Item("B", Min(20)), Item("C", new DurationSpec.Rest())], rounding: 15),
            window);

        Assert.Equal(["14:00-14:15 A Confirmed", "14:15-14:45 B Confirmed", "14:45-16:00 C Confirmed"], Lines(proposal));
    }

    [Fact]
    public void Piece_that_rounds_to_zero_is_dropped()
    {
        var window = new TimeRange(Msk(14), Msk(15));
        var proposal = Proposal(
            Resolve(window, [Item("Кофе", Min(5)), Item("Работа", new DurationSpec.Rest())], rounding: 15),
            window);

        Assert.Equal(["14:00-15:00 Работа Confirmed"], Lines(proposal));
    }

    [Fact]
    public void Tiny_tail_that_rounds_to_zero_is_dropped()
    {
        var window = new TimeRange(Msk(14), Msk(15));
        var proposal = Proposal(Resolve(window, [Item("A", Min(58))]), window);

        Assert.Equal(["14:00-15:00 A Confirmed"], Lines(proposal));
        Assert.Empty(proposal.UnaccountedTails);
    }

    [Fact]
    public void Invalid_input_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurationSpec.Minutes(0));
        Assert.Throws<ArgumentException>(() => new DurationSpec.Range(T(10), T(10)));
        Assert.Throws<ArgumentException>(() => Resolve(new TimeRange(Msk(14), Msk(15)), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(new TimeRange(Msk(14), Msk(15)), [Item("A", Min(5))], rounding: 0));
    }
}
