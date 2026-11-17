using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

public class TimelineBuilderTests
{
    private static readonly WeekTemplate NightOnly =
        new([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9))], Moscow);

    private static List<string> Build(
        TimeRange window,
        DateTime now,
        OpenActivity? open = null,
        ExtensionPolicy? policy = null,
        WeekTemplate? template = null,
        IReadOnlyList<Segment>? segments = null)
    {
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = window,
            NowUtc = now,
            Template = template ?? NightOnly,
            OpenActivity = open,
            Policy = policy ?? ExtensionPolicy.Default,
            Segments = segments ?? [],
        });
        AssertCovers(pieces, window);
        return pieces.Select(p => Describe(p, (template ?? NightOnly).TimeZone)).ToList();
    }

    private static OpenActivity Training(int minutes = 60) =>
        new(Msk(14), "Тренировка", minutes == 0 ? null : TimeSpan.FromMinutes(minutes), CategoryKey: "sport");

    [Fact]
    public void Spec_3_4_extension_then_background()
    {
        var result = Build(new TimeRange(Msk(14), Msk(17)), Msk(17), Training());

        Assert.Equal(
            [
                "14:00-15:30 Тренировка Assumed",
                "15:30-16:00 Тренировка Questionable",
                "16:00-17:00 Залипание Questionable",
            ],
            result);
    }

    [Fact]
    public void Preset_up_to_typical()
    {
        var result = Build(new TimeRange(Msk(14), Msk(17)), Msk(17), Training(), ExtensionPolicy.UpToTypical);

        Assert.Equal(["14:00-15:00 Тренировка Assumed", "15:00-17:00 Залипание Questionable"], result);
    }

    [Fact]
    public void Preset_infinite_extends_to_now()
    {
        var result = Build(new TimeRange(Msk(14), Msk(18)), Msk(17), Training(), ExtensionPolicy.Infinite);

        Assert.Equal(["14:00-17:00 Тренировка Assumed", "17:00-18:00 Залипание Questionable"], result);
    }

    [Fact]
    public void Preset_custom_with_infinite_max()
    {
        var result = Build(new TimeRange(Msk(14), Msk(17)), Msk(17), Training(), ExtensionPolicy.Custom(1.25, double.PositiveInfinity));

        Assert.Equal(["14:00-15:15 Тренировка Assumed", "15:15-17:00 Тренировка Questionable"], result);
    }

    [Fact]
    public void Unknown_typical_duration_uses_fallback_and_is_questionable()
    {
        var result = Build(new TimeRange(Msk(14), Msk(17)), Msk(17), Training(minutes: 0));

        // Запасные 60 мин × max 2.0 = 2 часа, всё 🟡.
        Assert.Equal(["14:00-16:00 Тренировка Questionable", "16:00-17:00 Залипание Questionable"], result);
    }

    [Fact]
    public void Extension_never_goes_beyond_now_and_future_is_flagged()
    {
        var window = new TimeRange(Msk(14), Msk(17));
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = window,
            NowUtc = Msk(15),
            Template = NightOnly,
            OpenActivity = Training(),
        });

        AssertCovers(pieces, window);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(("Тренировка", Msk(15), false), (pieces[0].Title, pieces[0].EndUtc, pieces[0].IsFuture));
        Assert.Equal(("Залипание", Msk(15), true), (pieces[1].Title, pieces[1].StartUtc, pieces[1].IsFuture));
    }

    [Fact]
    public void Extension_into_sleep_across_midnight_is_questionable_inside_block()
    {
        var template = new WeekTemplate([ScheduleBlock.Sleep(DaysOfWeek.All, T(23), T(7))], Moscow);
        var dota = new OpenActivity(Msk(21), "Dota 2", TimeSpan.FromHours(2));
        var window = new TimeRange(Msk(20), At(Moscow, 2026, 11, 14, 3));
        var now = At(Moscow, 2026, 11, 14, 2);

        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = window, NowUtc = now, Template = template, OpenActivity = dota,
        });

        AssertCovers(pieces, window);
        Assert.Equal(
            [
                "20:00-21:00 Залипание Questionable",
                "21:00-23:00 Dota 2 Assumed",
                "23:00-01:00 Dota 2 Questionable", // норма до 00:00 попала в сон → 🟡, плюс превышение до 01:00
                "01:00-02:00 Сон Assumed",
                "02:00-03:00 Сон Assumed",
            ],
            pieces.Select(p => Describe(p, Moscow)));
        Assert.Equal([false, false, false, false, true], pieces.Select(p => p.IsFuture));
        Assert.Equal(SegmentSource.Extension, pieces[2].Source);
        Assert.Equal(SegmentSource.Schedule, pieces[3].Source);
    }

    [Fact]
    public void Extension_into_fixed_block_is_questionable_then_block_resumes()
    {
        var template = new WeekTemplate(
            [new ScheduleBlock(ScheduleBlockKind.Fixed, "Учёба", "study", DaysOfWeek.Weekdays, T(9), T(15))], Moscow);
        var open = new OpenActivity(Msk(8), "Дорога", TimeSpan.FromMinutes(60));

        var result = Build(new TimeRange(Msk(7), Msk(16)), Msk(16), open, template: template);

        Assert.Equal(
            [
                "07:00-08:00 Залипание Questionable",
                "08:00-09:00 Дорога Assumed",
                "09:00-10:00 Дорога Questionable",
                "10:00-15:00 Учёба Assumed",
                "15:00-16:00 Залипание Questionable",
            ],
            result);
    }

    [Fact]
    public void Materialized_segments_win_over_every_layer()
    {
        var segments = new[]
        {
            new Segment(Msk(14, 30), Msk(15), "Звонок маме", SegmentStatus.Confirmed, SegmentSource.User, categoryKey: "family"),
            new Segment(Msk(16, 30), Msk(17, 30), "Ужин", SegmentStatus.Assumed, SegmentSource.User, categoryKey: "household"),
        };

        var result = Build(new TimeRange(Msk(14), Msk(18)), Msk(18), Training(), segments: segments);

        Assert.Equal(
            [
                "14:00-14:30 Тренировка Assumed",
                "14:30-15:00 Звонок маме Confirmed",
                "15:00-15:30 Тренировка Assumed",
                "15:30-16:00 Тренировка Questionable",
                "16:00-16:30 Залипание Questionable",
                "16:30-17:30 Ужин Assumed",
                "17:30-18:00 Залипание Questionable",
            ],
            result);
    }

    [Fact]
    public void Adjacent_identical_segments_are_merged()
    {
        var segments = new[]
        {
            new Segment(Msk(10), Msk(11), "Работа", SegmentStatus.Confirmed, SegmentSource.User, categoryKey: "work"),
            new Segment(Msk(11), Msk(12), "Работа", SegmentStatus.Confirmed, SegmentSource.User, categoryKey: "work"),
        };

        var result = Build(new TimeRange(Msk(10), Msk(12)), Msk(12), segments: segments);

        Assert.Equal(["10:00-12:00 Работа Confirmed"], result);
    }

    [Fact]
    public void Open_segment_must_be_passed_as_open_activity()
    {
        var open = new Segment(Msk(14), null, "Тренировка", SegmentStatus.Confirmed, SegmentSource.User);

        Assert.Throws<ArgumentException>(() => Build(new TimeRange(Msk(14), Msk(17)), Msk(17), segments: [open]));
        Assert.Equal("Тренировка", OpenActivity.FromSegment(open, TimeSpan.FromHours(1)).Title);
    }

    [Fact]
    public void Overlapping_segments_are_rejected()
    {
        var segments = new[]
        {
            new Segment(Msk(10), Msk(11), "A", SegmentStatus.Confirmed, SegmentSource.User),
            new Segment(Msk(10, 30), Msk(12), "B", SegmentStatus.Confirmed, SegmentSource.User),
        };

        Assert.Throws<ArgumentException>(() => Build(new TimeRange(Msk(10), Msk(12)), Msk(12), segments: segments));
    }

    [Fact]
    public void Sleep_block_crossing_midnight_over_a_full_day()
    {
        var template = new WeekTemplate([ScheduleBlock.Sleep(DaysOfWeek.All, T(23, 30), T(7, 30))], Moscow);
        var day = new TimeRange(At(Moscow, 2026, 11, 13, 0), At(Moscow, 2026, 11, 14, 0));

        var result = Build(day, day.EndUtc, template: template);

        Assert.Equal(
            [
                "00:00-07:30 Сон Assumed",
                "07:30-23:30 Залипание Questionable",
                "23:30-00:00 Сон Assumed",
            ],
            result);
    }

    [Fact]
    public void Dst_spring_day_in_berlin_has_23_hours_without_gaps()
    {
        var template = new WeekTemplate(
            [
                ScheduleBlock.Sleep(DaysOfWeek.All, T(23, 30), T(1, 30)),
                new ScheduleBlock(ScheduleBlockKind.Custom, "Призрак", null, [DayOfWeek.Sunday], T(2, 10), T(2, 50)),
                new ScheduleBlock(ScheduleBlockKind.Custom, "Пробежка", "sport", [DayOfWeek.Sunday], T(2, 50), T(4)),
            ],
            Berlin);
        var day = new TimeRange(At(Berlin, 2026, 3, 29, 0), At(Berlin, 2026, 3, 30, 0));

        var pieces = TimelineBuilder.Build(new TimelineRequest { Window = day, NowUtc = day.EndUtc, Template = template });

        AssertCovers(pieces, day);
        Assert.Equal(TimeSpan.FromHours(23), day.Duration);
        Assert.Equal(
            [
                "00:00-01:30 Сон Assumed",
                "01:30-03:00 Залипание Questionable",
                "03:00-04:00 Пробежка Assumed", // 02:50 не существует → начало сдвинуто на момент перехода
                "04:00-23:30 Залипание Questionable",
                "23:30-00:00 Сон Assumed",
            ],
            pieces.Select(p => Describe(p, Berlin)));
        Assert.DoesNotContain(pieces, p => p.Title == "Призрак"); // целиком в «дыре» 02:00–03:00
        Assert.Equal(TimeSpan.FromHours(1), pieces[2].Duration);
    }

    [Fact]
    public void Dst_autumn_day_in_berlin_has_25_hours_and_longer_sleep()
    {
        var template = new WeekTemplate([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9))], Berlin);
        var day = new TimeRange(At(Berlin, 2026, 10, 25, 0), At(Berlin, 2026, 10, 26, 0));

        var pieces = TimelineBuilder.Build(new TimelineRequest { Window = day, NowUtc = day.EndUtc, Template = template });

        AssertCovers(pieces, day);
        Assert.Equal(TimeSpan.FromHours(25), day.Duration);
        Assert.Equal(3, pieces.Count);
        Assert.Equal(("Сон", TimeSpan.FromHours(9)), (pieces[1].Title, pieces[1].Duration));
        Assert.Equal(TimeSpan.FromHours(25), TimeSpan.FromTicks(pieces.Sum(p => p.Duration.Ticks)));
    }

    [Fact]
    public void Window_entirely_in_the_future_is_forecast()
    {
        var window = new TimeRange(Msk(14), Msk(17));
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = window, NowUtc = Msk(10), Template = NightOnly, OpenActivity = new OpenActivity(Msk(9), "X", TimeSpan.FromHours(10)),
        });

        AssertCovers(pieces, window);
        var piece = Assert.Single(pieces);
        Assert.True(piece.IsFuture);
        Assert.Equal(SegmentSource.Background, piece.Source);
    }

    private static IReadOnlyList<string> Running(WeekTemplate template, DateTime start, DateTime now, TimeSpan? typical, TimeSpan elapsedBefore = default) =>
        TimelineBuilder.Build(new TimelineRequest
        {
            Window = new TimeRange(Msk(0), Msk(0).AddDays(1)),
            NowUtc = now,
            Template = template,
            OpenActivity = new OpenActivity(start, "Созвон", typical, ElapsedBefore: elapsedBefore),
        }).Where(p => p.Title == "Созвон").Select(p => Describe(p, Moscow)).ToList();

    private static readonly WeekTemplate Work = new(
        [new ScheduleBlock(ScheduleBlockKind.Fixed, "Работа", "work", DaysOfWeek.All, T(10), T(19))], Moscow);

    [Fact]
    public void Activity_started_inside_a_block_is_assumed_within_the_norm_and_questionable_beyond()
    {
        // 14:35, норма 1 ч × 1.5 = до 16:05, предел до 16:35.
        var result = Running(Work, Msk(14, 35), Msk(17), TimeSpan.FromHours(1));

        Assert.Equal(["14:35-16:05 Созвон Assumed", "16:05-16:35 Созвон Questionable"], result);
    }

    [Fact]
    public void Activity_started_before_a_block_and_running_into_it_is_questionable_inside()
    {
        var result = Running(Work, Msk(9), Msk(12), TimeSpan.FromHours(4));

        Assert.Equal(["09:00-10:00 Созвон Assumed", "10:00-12:00 Созвон Questionable"], result);
    }

    [Fact]
    public void Activity_started_inside_sleep_follows_the_same_rule()
    {
        var result = Running(NightOnly, Msk(3), Msk(5), TimeSpan.FromHours(4));

        Assert.Equal(["03:00-05:00 Созвон Assumed"], result);
    }

    [Fact]
    public void Activity_resumed_inside_a_block_behaves_like_one_started_there()
    {
        // Начат в 09:00, подтверждён и возобновлён в 11:00 (уже прошло 2 ч): блок начался до возобновления.
        var result = Running(Work, Msk(11), Msk(13), TimeSpan.FromHours(4), TimeSpan.FromHours(2));

        Assert.Equal(["11:00-13:00 Созвон Assumed"], result);
    }
}
