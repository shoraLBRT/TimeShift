using SkiaSharp;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Timelines;
using TimeShift.Rendering;

namespace TimeShift.Infrastructure.Tests;

/// <summary>Картинки лент рисуются и цвета стоят там, где должны.</summary>
public class RenderingTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly DateTime Day = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

    private static TimelinePiece Piece(int from, int to, string title, string? category, SegmentStatus status, bool future = false) =>
        new(Day.AddHours(from), Day.AddHours(to), title, null, category, status, SegmentSource.User, future);

    private static (int X, int Y) At(int hour) =>
        (300, 90 + (int)(hour / 24.0 * (1370 - 90)));

    [Fact]
    public void Day_image_has_phone_size_and_category_colors()
    {
        var renderer = new SkiaDayRenderer();
        var pieces = new[]
        {
            Piece(0, 8, "Сон", DefaultCategories.Sleep, SegmentStatus.Assumed),
            Piece(8, 17, "Работа", DefaultCategories.Work, SegmentStatus.Confirmed),
            Piece(17, 20, "Залипание", DefaultCategories.Wasted, SegmentStatus.Questionable),
            Piece(20, 24, "Сон", DefaultCategories.Sleep, SegmentStatus.Assumed, future: true),
        };

        var png = renderer.RenderDay(new DayImage("Сегодня, 13 ноября", Day, Day.AddDays(1), Day.AddHours(20), Utc, pieces,
            [Piece(12, 13, "Обед", DefaultCategories.Household, SegmentStatus.Confirmed)]));

        Assert.NotNull(png);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((720, 1400), (bitmap.Width, bitmap.Height));

        // Внутри «Работы» (15:00 — между линиями сетки) — цвет работы, сплошной.
        var (x, y) = At(15);
        Assert.Equal(SkiaDayRenderer.ColorOf(DefaultCategories.Work), bitmap.GetPixel(x + 200, y));

        // Сон ⚪ — бледнее сплошного цвета сна.
        var sleep = bitmap.GetPixel(At(4).X + 200, At(4).Y);
        Assert.NotEqual(SkiaDayRenderer.ColorOf(DefaultCategories.Sleep), sleep);

        // Параллельный обед — в узкой колонке справа.
        Assert.Equal(SkiaDayRenderer.ColorOf(DefaultCategories.Household), bitmap.GetPixel(608, At(12).Y + 20));
    }

    [Fact]
    public void Week_image_has_day_strips_category_bars_and_fits_a_phone()
    {
        var renderer = new SkiaDayRenderer();
        IReadOnlyList<TimelinePiece> Pieces(int i) =>
        [
            new(Day.AddDays(i), Day.AddDays(i).AddHours(8), "Сон", null, DefaultCategories.Sleep, SegmentStatus.Assumed, SegmentSource.User, false),
            new(Day.AddDays(i).AddHours(8), Day.AddDays(i).AddHours(17), "Работа", null, DefaultCategories.Work, SegmentStatus.Confirmed, SegmentSource.User, false),
            new(Day.AddDays(i).AddHours(17), Day.AddDays(i + 1), "Залипание", null, DefaultCategories.Wasted, SegmentStatus.Questionable, SegmentSource.User, false),
        ];

        // Пять дней с записями, два последних — будущее.
        var days = Enumerable.Range(0, 7)
            .Select(i => new WeekDay(DateOnly.FromDateTime(Day).AddDays(i), Day.AddDays(i), Day.AddDays(i + 1), i < 5 ? Pieces(i) : []))
            .ToList();
        var main = days.SelectMany(d => d.Pieces).ToList();
        var stats = WeekStats.Of(main, [Piece(12, 13, "Обед", DefaultCategories.Household, SegmentStatus.Confirmed)], main);

        var png = renderer.RenderWeek(new WeekImage("Эта неделя, 13–19 ноября", Utc, days, stats));

        using var bitmap = SKBitmap.Decode(png);
        var categoriesTop = SkiaDayRenderer.WeekTop + (SkiaDayRenderer.WeekRow * 7) + 26;
        Assert.Equal(720, bitmap.Width);
        Assert.Equal(categoriesTop + (SkiaDayRenderer.CategoryRow * 3) + 14 + 86 + 34 + 30 + 14, bitmap.Height);
        Assert.True(bitmap.Height <= SkiaDayRenderer.Height); // не выше картинки дня — помещается на экран телефона

        // Полоса третьего дня в 13:00 — работа, сплошным цветом; в 4:00 — сон ⚪, бледнее.
        int X(double hour) => SkiaDayRenderer.WeekStripLeft + (int)((SkiaDayRenderer.WeekRight - SkiaDayRenderer.WeekStripLeft) * hour / 24);
        var row = SkiaDayRenderer.WeekTop + (SkiaDayRenderer.WeekRow * 2) + 20;
        Assert.Equal(SkiaDayRenderer.ColorOf(DefaultCategories.Work), bitmap.GetPixel(X(13), row));
        Assert.NotEqual(SkiaDayRenderer.ColorOf(DefaultCategories.Sleep), bitmap.GetPixel(X(4), row));

        // Будущий день пуст — на том же месте нет цвета работы.
        var empty = bitmap.GetPixel(X(13), SkiaDayRenderer.WeekTop + (SkiaDayRenderer.WeekRow * 6) + 20);
        Assert.NotEqual(SkiaDayRenderer.ColorOf(DefaultCategories.Work), empty);

        // Самая большая категория (работа, 45 ч) — первой строкой, её полоса во всю ширину.
        Assert.Equal(SkiaDayRenderer.ColorOf(DefaultCategories.Work), bitmap.GetPixel(SkiaDayRenderer.WeekRight - 10, categoriesTop + 41));
        Assert.NotEqual(SkiaDayRenderer.ColorOf(DefaultCategories.Sleep), bitmap.GetPixel(SkiaDayRenderer.WeekRight - 10, categoriesTop + SkiaDayRenderer.CategoryRow + 41));
    }

    [Fact]
    public void Own_categories_are_drawn_in_the_colour_of_their_parent_or_group()
    {
        var user = new Domain.Users.User(1, "Тест", Domain.Users.User.DefaultTimeZone, Day);
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, Day);
        var guitar = user.AddCategory("Гитара", CategoryGroup.Rest, music.Key, Day);
        var calls = user.AddCategory("Созвоны", CategoryGroup.Useful, DefaultCategories.Work, Day);
        user.RemoveCategory(music.Key, Day);
        var set = user.Categories;
        var grey = SkiaDayRenderer.ColorOf("nope");

        // Подвид системной категории — её цвет; своя категория и её подвид — цвет группы; удалённые — как были.
        Assert.Equal(SkiaDayRenderer.ColorOf(DefaultCategories.Work), SkiaDayRenderer.ColorOf(calls.Key, set));
        Assert.NotEqual(grey, SkiaDayRenderer.ColorOf(music.Key, set));
        Assert.Equal(SkiaDayRenderer.ColorOf(music.Key, set), SkiaDayRenderer.ColorOf(guitar.Key, set));
        Assert.Equal(grey, SkiaDayRenderer.ColorOf(music.Key));

        // И на картинке дня: кусок своей категории закрашен её цветом, а не серым «без категории».
        var png = new SkiaDayRenderer().RenderDay(new DayImage(
            "Сегодня", Day, Day.AddDays(1), Day.AddHours(20), Utc, [Piece(0, 20, "Гитара", guitar.Key, SegmentStatus.Confirmed)], [], set));
        using var bitmap = SKBitmap.Decode(png);
        var (x, y) = At(15); // между линиями сетки
        Assert.Equal(SkiaDayRenderer.ColorOf(guitar.Key, set), bitmap.GetPixel(x + 200, y));
    }
}
