using SkiaSharp;
using TimeShift.Application.Abstractions;
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
    public void Week_image_has_a_row_per_day()
    {
        var renderer = new SkiaDayRenderer();
        var days = Enumerable.Range(0, 7)
            .Select(i => (DateOnly.FromDateTime(Day).AddDays(i), Day.AddDays(i), (IReadOnlyList<TimelinePiece>)[
                new TimelinePiece(Day.AddDays(i), Day.AddDays(i).AddHours(8), "Сон", null, DefaultCategories.Sleep, SegmentStatus.Confirmed, SegmentSource.User, false)]))
            .ToList();

        var png = renderer.RenderWeek(new WeekImage("Неделя", Utc, days));

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(720, bitmap.Width);
        Assert.Equal(110 + (170 * 7) + 30, bitmap.Height);
    }
}
