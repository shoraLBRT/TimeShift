using TimeShift.Application.Parsing;

namespace TimeShift.Application.Tests;

public class ScheduleTextTests
{
    [Theory]
    [InlineData("Работа пн-пт 9-18", "Работа", "пн–пт", "09:00–18:00")]
    [InlineData("учёба пн, ср, пт 9:30-15", "Учёба", "пн, ср, пт", "09:30–15:00")]
    [InlineData("Тренировка вт чт 19:00–21:00", "Тренировка", "вт, чт", "19:00–21:00")]
    [InlineData("Учёба по субботам с 10 до 13", "Учёба", "сб", "10:00–13:00")]
    [InlineData("работа по будням 10-19", "Работа", "пн–пт", "10:00–19:00")]
    [InlineData("Йога каждый день 7-7:30", "Йога", "каждый день", "07:00–07:30")]
    [InlineData("Смена пт-вс 20-4", "Смена", "пт–вс", "20:00–04:00")]
    [InlineData("Футбол выходные 12.00-14.00", "Футбол", "сб, вс", "12:00–14:00")]
    [InlineData("Ночная работа вс-чт 22-24", "Ночная работа", "вс–чт", "22:00–00:00")]
    public void Parses_blocks(string text, string title, string days, string time)
    {
        Assert.True(ScheduleText.TryParseBlock(text, out var block, out var error), error);

        Assert.Equal(title, block!.Title);
        Assert.Equal(days, ScheduleText.FormatDays(block.Days));
        Assert.Equal(time, ScheduleText.FormatTime(block.Start, block.End));
    }

    [Theory]
    [InlineData("Работа пн-пт", "время")]
    [InlineData("Работа 9-18", "дни")]
    [InlineData("пн-пт 9-18", "назвать")]
    [InlineData("Работа пн 9-9", "время")]
    [InlineData("Работа пн 9-25", "время")]
    public void Explains_what_is_missing(string text, string hint)
    {
        Assert.False(ScheduleText.TryParseBlock(text, out _, out var error));
        Assert.Contains(hint, error);
    }

    [Theory]
    [InlineData("1-9", 1, 0, 9, 0)]
    [InlineData("23:30-7:30", 23, 30, 7, 30)]
    [InlineData("с 0 до 8", 0, 0, 8, 0)]
    [InlineData("01:00–09:00", 1, 0, 9, 0)]
    public void Parses_time_ranges(string text, int h1, int m1, int h2, int m2)
    {
        Assert.True(ScheduleText.TryParseTimeRange(text, out var start, out var end));
        Assert.Equal((new TimeOnly(h1, m1), new TimeOnly(h2, m2)), (start, end));
    }

    [Theory]
    [InlineData("ложусь в 1")]
    [InlineData("1-9 и ещё")]
    [InlineData("5-5")]
    public void Rejects_non_ranges(string text)
    {
        Assert.False(ScheduleText.TryParseTimeRange(text, out _, out _));
    }

    [Theory]
    [InlineData("Работа", "work")]
    [InlineData("Пары в универе", "study")]
    [InlineData("Тренировка", "sport")]
    [InlineData("Свой проект", "projects")]
    [InlineData("Гитара", null)]
    public void Guesses_obvious_categories(string title, string? category)
    {
        Assert.Equal(category, ScheduleText.GuessCategory(title));
    }

    [Theory]
    [InlineData("", "2026-11-14")]
    [InlineData("сегодня", "2026-11-14")]
    [InlineData("вчера", "2026-11-13")]
    [InlineData("позавчера", "2026-11-12")]
    [InlineData("сб", "2026-11-14")]
    [InlineData("в пятницу", "2026-11-13")]
    [InlineData("понедельник", "2026-11-09")]
    [InlineData("вс", "2026-11-08")]
    [InlineData("13.11", "2026-11-13")]
    [InlineData("1.1.2026", "2026-01-01")]
    [InlineData("31.12", "2025-12-31")]
    public void Parses_day_arguments(string text, string expected)
    {
        var today = new DateOnly(2026, 11, 14); // суббота
        Assert.True(DayArgument.TryParse(text, today, out var day, out var error), error);
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), day);
    }

    [Theory]
    [InlineData("завтра")]
    [InlineData("15.11")]
    [InlineData("32.11")]
    public void Rejects_future_and_unknown_days(string text)
    {
        Assert.False(DayArgument.TryParse(text, new DateOnly(2026, 11, 14), out _, out var error));
        Assert.NotNull(error);
    }
}
