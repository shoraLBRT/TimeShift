using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Tests;

internal static class TestTime
{
    public static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    public static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    /// <summary>Местное время в поясе → UTC.</summary>
    public static DateTime At(TimeZoneInfo tz, int year, int month, int day, int hour, int minute = 0) =>
        LocalTime.ToUtc(new DateTime(year, month, day, hour, minute, 0), tz);

    /// <summary>Москва, 13.11.2026 (пятница), hh:mm → UTC.</summary>
    public static DateTime Msk(int hour, int minute = 0) => At(Moscow, 2026, 11, 13, hour, minute);

    public static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    public static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    public static string Local(DateTime utc, TimeZoneInfo tz) => LocalTime.ToLocal(utc, tz).ToString("dd HH:mm");

    /// <summary>Компактная запись куска для сравнения: «14:00-15:30 Тренировка Assumed».</summary>
    public static string Describe(TimelinePiece p, TimeZoneInfo tz) =>
        $"{LocalTime.ToLocal(p.StartUtc, tz):HH:mm}-{LocalTime.ToLocal(p.EndUtc, tz):HH:mm} {p.Title} {p.Status}";

    public static string Describe(Segment s, TimeZoneInfo tz) =>
        $"{LocalTime.ToLocal(s.StartUtc, tz):HH:mm}-{(s.EndUtc is { } e ? LocalTime.ToLocal(e, tz).ToString("HH:mm") : "…")} {s.Title} {s.Status}";

    /// <summary>Проверка инвариантов ленты: упорядочена, непрерывна, ровно покрывает окно, без пустых кусков.</summary>
    public static void AssertCovers(IReadOnlyList<TimelinePiece> pieces, TimeRange window)
    {
        Assert.NotEmpty(pieces);
        Assert.Equal(window.StartUtc, pieces[0].StartUtc);
        Assert.Equal(window.EndUtc, pieces[^1].EndUtc);
        for (var i = 0; i < pieces.Count; i++)
        {
            Assert.True(pieces[i].EndUtc > pieces[i].StartUtc, $"empty piece #{i}");
            if (i > 0)
            {
                Assert.Equal(pieces[i - 1].EndUtc, pieces[i].StartUtc);
                Assert.False(pieces[i - 1].HasSameContent(pieces[i]), $"unmerged neighbours #{i}");
            }
        }
    }
}
