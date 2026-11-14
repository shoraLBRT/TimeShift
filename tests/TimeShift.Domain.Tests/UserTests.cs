using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Domain.Tests;

public class UserTests
{
    [Fact]
    public void New_user_gets_default_settings_from_spec()
    {
        var user = new User(42, "Test", User.DefaultTimeZone, DateTime.UtcNow);

        var s = user.Settings;
        Assert.Equal(30, s.PingIntervalMin);
        Assert.Equal(1.5, s.NormFactor);
        Assert.Equal(2.0, s.MaxFactor);
        Assert.Equal(2, s.MaxUnansweredPings);
        Assert.False(s.PingDuringBlocks);
        Assert.Equal(30, s.EveningReviewOffsetMin);
        Assert.Equal(5, s.RoundingMin);
    }

    [Fact]
    public void Created_at_must_be_utc()
    {
        Assert.Throws<ArgumentException>(() =>
            new User(42, "Test", User.DefaultTimeZone, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    private static User NewUser() => new(42, "Test", User.DefaultTimeZone, DateTime.UtcNow);

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [Fact]
    public void Sleep_nights_start_on_the_evening_before_or_the_wake_day()
    {
        Assert.Equal(
            [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday],
            ScheduleBlock.SleepDays(T(23), beforeWeekend: false));
        Assert.Equal(DaysOfWeek.Weekdays, ScheduleBlock.SleepDays(T(1), beforeWeekend: false));
        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Saturday], ScheduleBlock.SleepDays(T(23, 30), beforeWeekend: true));
        Assert.Equal(DaysOfWeek.Weekend, ScheduleBlock.SleepDays(T(2), beforeWeekend: true));
    }

    [Fact]
    public void Weekday_and_weekend_sleep_cover_every_night_once()
    {
        var user = NewUser();

        Assert.Null(user.TrySetSleep((T(23), T(7)), (T(1), T(10))));

        var sleep = user.Blocks.Where(b => b.Kind == ScheduleBlockKind.Sleep).ToList();
        Assert.Equal(2, sleep.Count);
        Assert.Equal(7, sleep.Sum(b => b.Days.Count));
    }

    [Fact]
    public void Overlapping_block_is_rejected_and_the_other_block_is_reported()
    {
        var user = NewUser();
        user.TrySetSleep((T(1), T(9)));
        var work = new ScheduleBlock(ScheduleBlockKind.Fixed, "Работа", "work", DaysOfWeek.Weekdays, T(9), T(18));
        Assert.Null(user.TryAddBlock(work));

        var early = new ScheduleBlock(ScheduleBlockKind.Custom, "Пробежка", "sport", [DayOfWeek.Monday], T(8), T(9, 30));
        var conflict = user.TryAddBlock(early);

        Assert.Equal(ScheduleBlockKind.Sleep, conflict!.Kind);
        Assert.Equal(2, user.Blocks.Count);

        // Новый сон, который заходит на работу, тоже не ставится — старый остаётся.
        Assert.Same(work, user.TrySetSleep((T(2), T(10))));
        Assert.Equal(T(1), user.Blocks.Single(b => b.Kind == ScheduleBlockKind.Sleep).Start);

        Assert.True(user.RemoveBlock(work.Id));
        Assert.False(user.RemoveBlock(work.Id));
    }

    [Fact]
    public void Extension_policy_round_trips_through_settings_with_infinity_as_null()
    {
        var settings = NewUser().Settings;
        Assert.Equal(ExtensionPolicy.Default, settings.Extension);

        settings.Extension = ExtensionPolicy.Infinite;
        Assert.Null(settings.NormFactor);
        Assert.Null(settings.MaxFactor);
        Assert.Equal(ExtensionPolicy.Infinite, settings.Extension);

        settings.Extension = ExtensionPolicy.Custom(1.2, double.PositiveInfinity);
        Assert.Equal((1.2, (double?)null), (settings.NormFactor, settings.MaxFactor));
        Assert.Equal(ExtensionPolicy.Custom(1.2, double.PositiveInfinity), settings.Extension);
    }
}
