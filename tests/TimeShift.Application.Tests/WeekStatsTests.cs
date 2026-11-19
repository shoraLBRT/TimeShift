using TimeShift.Application.Tracking;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Tests;

/// <summary>Цифры недели: суммы сходятся с лентой, параллельные отдельно, сравнение с прошлой неделей.</summary>
public class WeekStatsTests
{
    private static readonly DateTime Day = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);

    private static TimelinePiece Piece(double from, double to, string title, string? category, SegmentStatus status, bool future = false) =>
        new(Day.AddHours(from), Day.AddHours(to), title, null, category, status, SegmentSource.User, future);

    [Fact]
    public void Categories_and_statuses_add_up_to_the_past_part_of_the_timeline()
    {
        var stats = WeekStats.Of(
            [
                Piece(0, 8, "Сон", DefaultCategories.Sleep, SegmentStatus.Assumed),
                Piece(8, 12, "Работа", DefaultCategories.Work, SegmentStatus.Confirmed),
                Piece(12, 13, "YouTube", DefaultCategories.WastedYouTube, SegmentStatus.Confirmed),
                Piece(13, 15, "Залипание", DefaultCategories.Wasted, SegmentStatus.Questionable),
                Piece(15, 15.5, "Что-то", null, SegmentStatus.Confirmed),
                Piece(15.5, 24, "Сон", DefaultCategories.Sleep, SegmentStatus.Assumed, future: true),
            ],
            []);

        Assert.Equal(TimeSpan.FromHours(15.5), stats.Total);
        Assert.Equal(
            ["Сон 8", "Работа 4", "Залипание 3", "Без категории 0,5"],
            stats.Categories.Select(c => $"{c.Name} {c.Total.TotalHours.ToString(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}").ToList());
        Assert.Equal(stats.Total, TimeSpan.FromTicks(stats.Categories.Sum(c => c.Total.Ticks)));
        Assert.Equal(stats.Total, stats.Confirmed + stats.Assumed + stats.Questionable);
        Assert.Equal(TimeSpan.FromHours(5.5), stats.Confirmed);
        Assert.False(stats.IsCompared);
        Assert.All(stats.Categories, c => Assert.Null(c.Change));
    }

    [Fact]
    public void Unknown_time_is_a_last_line_of_its_own_and_not_lost_time()
    {
        TimelinePiece Background(double from, double to) =>
            new(Day.AddHours(from), Day.AddHours(to), TimelineBuilder.BackgroundTitle, null, null, SegmentStatus.Questionable, SegmentSource.Background, false);
        var previous = new[] { Background(0, 10), Piece(10, 12, "Без названия", null, SegmentStatus.Confirmed) };

        var stats = WeekStats.Of(
            [
                Background(0, 6),
                Piece(6, 7, "YouTube", DefaultCategories.WastedYouTube, SegmentStatus.Confirmed),
                Piece(7, 8, "Что-то", null, SegmentStatus.Confirmed),
                Piece(8, 12, "Работа", DefaultCategories.Work, SegmentStatus.Confirmed),
            ],
            [],
            previous);

        // Фон (#47) — не «Залипание» и не «Без категории»: отдельная последняя строка, хотя она и самая большая.
        Assert.Equal(
            ["Работа 4 (+4)", "Без категории 1 (-1)", "Залипание 1 (+1)", "Не записано 6 (-4)"],
            stats.Categories.Select(c => $"{c.Name} {c.Total.TotalHours} ({(c.Change!.Value.TotalHours > 0 ? "+" : string.Empty)}{c.Change.Value.TotalHours})").ToList());
        var unknown = stats.Categories[^1];
        Assert.True(unknown.IsUnknown);
        Assert.Null(unknown.Key);
        Assert.Equal(stats.Total, TimeSpan.FromTicks(stats.Categories.Sum(c => c.Total.Ticks)));
    }

    [Fact]
    public void Shares_are_whole_percents_that_sum_to_100()
    {
        var stats = WeekStats.Of(
            [
                Piece(0, 1, "А", DefaultCategories.Work, SegmentStatus.Confirmed),
                Piece(1, 2, "Б", DefaultCategories.Sleep, SegmentStatus.Assumed),
                Piece(2, 3, "В", DefaultCategories.Wasted, SegmentStatus.Questionable),
            ],
            []);

        var (confirmed, assumed, questionable) = stats.Shares();

        Assert.Equal(100, confirmed + assumed + questionable);
        Assert.Equal([33, 33, 34], new[] { confirmed, assumed, questionable }.Order().ToList());
        Assert.Equal((0, 0, 0), WeekStats.Of([], []).Shares());
    }

    [Fact]
    public void Parallel_activities_are_counted_apart_from_the_main_track()
    {
        var stats = WeekStats.Of(
            [Piece(0, 24, "Работа", DefaultCategories.Work, SegmentStatus.Confirmed)],
            [
                Piece(12, 13, "Обед", DefaultCategories.Household, SegmentStatus.Confirmed),
                Piece(18, 18.5, "Обед", DefaultCategories.Household, SegmentStatus.Assumed),
                Piece(12.5, 13, "Переписка", null, SegmentStatus.Confirmed),
                Piece(22, 23, "Ужин", DefaultCategories.Household, SegmentStatus.Assumed, future: true),
            ]);

        Assert.Equal(TimeSpan.FromHours(24), stats.Total);
        Assert.Equal(["Обед 90", "Переписка 30"], stats.Parallel.Select(p => $"{p.Title} {p.Total.TotalMinutes}").ToList());
        Assert.Equal(TimeSpan.FromHours(2), stats.ParallelSum);
    }

    [Fact]
    public void Change_is_the_difference_with_the_previous_week_and_keeps_vanished_categories()
    {
        var stats = WeekStats.Of(
            [
                Piece(0, 8, "Сон", DefaultCategories.Sleep, SegmentStatus.Confirmed),
                Piece(8, 14, "Работа", DefaultCategories.Work, SegmentStatus.Confirmed),
            ],
            [],
            [
                Piece(0, 8, "Сон", DefaultCategories.Sleep, SegmentStatus.Assumed),
                Piece(8, 12, "Работа", DefaultCategories.Work, SegmentStatus.Confirmed),
                Piece(12, 14, "Зал", DefaultCategories.Sport, SegmentStatus.Confirmed),
            ]);

        Assert.True(stats.IsCompared);
        Assert.Equal(
            ["Сон 8 ч (=)", "Работа 6 ч (+2 ч)", "Спорт 0 мин (−2 ч)"],
            stats.Categories.Select(c => $"{c.Name} {TrackingReplies.Duration(c.Total)} ({TrackingReplies.Change(c.Change!.Value)})").ToList());
    }

    [Fact]
    public void Week_title_names_this_and_the_previous_week()
    {
        var thisMonday = new DateOnly(2026, 11, 30);

        Assert.Equal("Эта неделя, 30 ноября – 6 декабря", TrackingReplies.WeekTitle(thisMonday, thisMonday));
        Assert.Equal("Прошлая неделя, 23–29 ноября", TrackingReplies.WeekTitle(thisMonday.AddDays(-7), thisMonday));
        Assert.Equal("Неделя 16–22 ноября", TrackingReplies.WeekTitle(thisMonday.AddDays(-14), thisMonday));
    }
}
