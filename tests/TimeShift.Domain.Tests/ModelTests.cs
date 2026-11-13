using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

public class ModelTests
{
    [Fact]
    public void Segment_requires_utc_and_start_before_end()
    {
        var start = Utc(2026, 11, 13, 10);
        Assert.Throws<ArgumentException>(() =>
            new Segment(start, start, "x", SegmentStatus.Confirmed, SegmentSource.User));
        Assert.Throws<ArgumentException>(() =>
            new Segment(start, start.AddHours(-1), "x", SegmentStatus.Confirmed, SegmentSource.User));
        Assert.Throws<ArgumentException>(() =>
            new Segment(DateTime.SpecifyKind(start, DateTimeKind.Local), null, "x", SegmentStatus.Confirmed, SegmentSource.User));
        Assert.Throws<ArgumentException>(() =>
            new Segment(start, start.AddHours(1), " ", SegmentStatus.Confirmed, SegmentSource.User));

        var open = new Segment(start, null, "Тренировка", SegmentStatus.Confirmed, SegmentSource.User);
        Assert.True(open.IsOpen);
    }

    [Fact]
    public void Extension_presets_match_spec()
    {
        Assert.Equal((1.0, 1.0), (ExtensionPolicy.UpToTypical.NormFactor, ExtensionPolicy.UpToTypical.MaxFactor));
        Assert.Equal((1.5, 2.0), (ExtensionPolicy.Default.NormFactor, ExtensionPolicy.Default.MaxFactor));
        Assert.True(double.IsPositiveInfinity(ExtensionPolicy.Infinite.NormFactor));
        Assert.True(double.IsPositiveInfinity(ExtensionPolicy.Infinite.MaxFactor));

        var custom = ExtensionPolicy.Custom(1.2, double.PositiveInfinity);
        Assert.Equal(1.2, custom.NormFactor);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtensionPolicy.Custom(2, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtensionPolicy.Custom(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtensionPolicy.Custom(double.NaN, 1));
    }

    [Fact]
    public void Default_categories_follow_spec_groups()
    {
        var all = DefaultCategories.All;
        Assert.Equal(all.Count, all.Select(c => c.Key).Distinct().Count());
        Assert.All(all.Where(c => c.ParentKey is not null), c => Assert.NotNull(DefaultCategories.Find(c.ParentKey!)));

        Assert.Equal(CategoryGroup.Necessary, DefaultCategories.Find("sleep")!.Group);
        Assert.Equal("Залипание", DefaultCategories.Find(DefaultCategories.Wasted)!.Name);
        Assert.Equal(
            ["wasted.lying", "wasted.multiplayer", "wasted.other", "wasted.short_video", "wasted.youtube"],
            all.Where(c => c.ParentKey == "wasted").Select(c => c.Key).Order());
        Assert.Equal(4, all.Count(c => c.Group == CategoryGroup.Useful));
        Assert.Equal("Потерянное", DefaultCategories.GroupName(CategoryGroup.Lost));
    }

    [Fact]
    public void Schedule_block_validates_input()
    {
        Assert.Throws<ArgumentException>(() => ScheduleBlock.Sleep([], T(1), T(9)));
        Assert.Throws<ArgumentException>(() => ScheduleBlock.Sleep(DaysOfWeek.All, T(9), T(9)));

        var night = ScheduleBlock.Sleep(DaysOfWeek.All, T(23, 30), T(7, 30));
        Assert.True(night.CrossesMidnight);
        Assert.Equal(TimeSpan.FromHours(8), night.Length);
        Assert.Equal("Сон", night.Title);
        Assert.Equal("sleep", night.CategoryKey);
    }

    [Fact]
    public void Template_rejects_overlapping_blocks()
    {
        var study = new ScheduleBlock(ScheduleBlockKind.Fixed, "Учёба", "study", DaysOfWeek.Weekdays, T(9), T(15));

        // Сон после полуночи наезжает на учёбу.
        Assert.Throws<ArgumentException>(() =>
            new WeekTemplate([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9, 30)), study], Moscow));

        // Сон с вечера через полночь наезжает на утренний блок следующего дня.
        var morningRun = new ScheduleBlock(ScheduleBlockKind.Custom, "Пробежка", "sport", [DayOfWeek.Tuesday], T(7), T(8));
        Assert.Throws<ArgumentException>(() =>
            new WeekTemplate([ScheduleBlock.Sleep([DayOfWeek.Monday], T(23), T(7, 30)), morningRun], Moscow));

        // Воскресный сон уходит в понедельник (переход через конец недели).
        var mondayEarly = new ScheduleBlock(ScheduleBlockKind.Custom, "Ранний", null, [DayOfWeek.Monday], T(0, 30), T(1));
        Assert.Throws<ArgumentException>(() =>
            new WeekTemplate([ScheduleBlock.Sleep([DayOfWeek.Sunday], T(23), T(7)), mondayEarly], Moscow));

        // Стык без пересечения — допустим.
        var ok = new WeekTemplate([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9)), study], Moscow);
        Assert.Equal(2, ok.Blocks.Count);
    }

    [Fact]
    public void Local_time_conversion_handles_dst_gap_and_overlap()
    {
        // Berlin, 29.03.2026: 02:00 CET → 03:00 CEST. 02:30 не существует → момент перехода (01:00 UTC).
        Assert.Equal(Utc(2026, 3, 29, 1), LocalTime.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0), Berlin));
        Assert.Equal(Utc(2026, 3, 29, 1), LocalTime.ToUtc(new DateTime(2026, 3, 29, 3, 0, 0), Berlin));
        Assert.Equal(Utc(2026, 3, 29, 0, 59), LocalTime.ToUtc(new DateTime(2026, 3, 29, 1, 59, 0), Berlin));

        // Berlin, 25.10.2026: 03:00 CEST → 02:00 CET. 02:30 бывает дважды → более раннее (00:30 UTC).
        Assert.Equal(Utc(2026, 10, 25, 0, 30), LocalTime.ToUtc(new DateTime(2026, 10, 25, 2, 30, 0), Berlin));
        Assert.Equal(Utc(2026, 10, 25, 2), LocalTime.ToUtc(new DateTime(2026, 10, 25, 3, 0, 0), Berlin));

        // Москва без перевода часов: UTC+3.
        Assert.Equal(Utc(2026, 11, 13, 11), LocalTime.ToUtc(new DateTime(2026, 11, 13, 14, 0, 0), Moscow));
        Assert.Equal(new DateTime(2026, 11, 13, 14, 0, 0), LocalTime.ToLocal(Utc(2026, 11, 13, 11), Moscow));
    }

    [Fact]
    public void Occurrences_cross_midnight_and_week_boundary()
    {
        var template = new WeekTemplate([ScheduleBlock.Sleep([DayOfWeek.Sunday], T(23, 30), T(7, 30))], Moscow);

        // Понедельник 16.11.2026 00:00–12:00 МСК: виден хвост воскресного сна.
        var window = new TimeRange(At(Moscow, 2026, 11, 16, 0), At(Moscow, 2026, 11, 16, 12));
        var occ = Assert.Single(template.Occurrences(window));
        Assert.Equal(At(Moscow, 2026, 11, 15, 23, 30), occ.Range.StartUtc);
        Assert.Equal(At(Moscow, 2026, 11, 16, 7, 30), occ.Range.EndUtc);
    }

    [Fact]
    public void Sleep_through_dst_changes_has_real_length()
    {
        var template = new WeekTemplate([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9))], Berlin);

        var spring = template.Occurrences(new TimeRange(Utc(2026, 3, 29, 0), Utc(2026, 3, 29, 2)));
        Assert.Equal(TimeSpan.FromHours(7), Assert.Single(spring).Range.Duration);

        var autumn = template.Occurrences(new TimeRange(Utc(2026, 10, 25, 0), Utc(2026, 10, 25, 2)));
        Assert.Equal(TimeSpan.FromHours(9), Assert.Single(autumn).Range.Duration);
    }
}
