using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

/// <summary>spec §3.6: перерыв внутри открытого занятия и параллельные занятия.</summary>
public class InterruptionAndParallelTests
{
    private static readonly WeekTemplate NightOnly = new([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9))], Moscow);

    private static Segment Primary(int fromHour, int fromMinute, int? toHour, int toMinute, string title) =>
        new(Msk(fromHour, fromMinute), toHour is { } h ? Msk(h, toMinute) : null, title, SegmentStatus.Confirmed, SegmentSource.User);

    private static Segment Parallel(int fromHour, int fromMinute, int toHour, int toMinute, string title) =>
        new(Msk(fromHour, fromMinute), Msk(toHour, toMinute), title, SegmentStatus.Confirmed, SegmentSource.User, isParallel: true);

    private static List<string> BuildDay(Timeline timeline, TimeRange window, DateTime now, TimeSpan typical)
    {
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = window,
            NowUtc = now,
            Template = NightOnly,
            Segments = timeline.Segments.Where(s => !s.IsOpen).ToList(),
            OpenActivity = timeline.OpenSegment is { } open ? OpenActivity.FromSegment(open, typical) : null,
        });
        AssertCovers(pieces, window);
        return pieces.Select(p => Describe(p, Moscow)).ToList();
    }

    [Fact]
    public void Extension_after_a_break_counts_time_already_spent()
    {
        // Тренировка (T = 60 мин) с 14:00, в 14:30 позвонили на 30 минут.
        var timeline = new Timeline([Primary(14, 0, null, 0, "Тренировка")]);
        timeline.Apply([Primary(14, 30, 15, 0, "Звонок")], Msk(17));

        // Норма 90 мин и предел 120 мин считаются по сумме: 30 мин уже было до звонка.
        Assert.Equal(
            [
                "14:00-14:30 Тренировка Confirmed",
                "14:30-15:00 Звонок Confirmed",
                "15:00-16:00 Тренировка Assumed",
                "16:00-16:30 Тренировка Questionable",
                "16:30-17:00 Не записано Questionable",
            ],
            BuildDay(timeline, new TimeRange(Msk(14), Msk(17)), Msk(17), TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void Elapsed_time_beyond_the_limit_leaves_no_extension()
    {
        var open = new OpenActivity(Msk(16), "Тренировка", TimeSpan.FromMinutes(60), ElapsedBefore: TimeSpan.FromHours(3));

        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = new TimeRange(Msk(16), Msk(17)),
            NowUtc = Msk(17),
            Template = NightOnly,
            OpenActivity = open,
        });

        Assert.Equal(["16:00-17:00 Не записано Questionable"], pieces.Select(p => Describe(p, Moscow)));
    }

    [Fact]
    public void Parallel_activity_does_not_displace_the_main_one()
    {
        // «Работаю» с 10:00; «обедал за работой с 13 до 13:30».
        var timeline = new Timeline([Primary(10, 0, null, 0, "Работа")]);

        var change = timeline.Apply([Parallel(13, 0, 13, 30, "Обед")], Msk(15));

        Assert.Equal(["10:00-… Работа Confirmed"], timeline.Segments.Select(s => Describe(s, Moscow)));
        Assert.Equal(["13:00-13:30 Обед Confirmed"], timeline.ParallelSegments.Select(s => Describe(s, Moscow)));
        Assert.Empty(change.Before);

        timeline.Undo(change);
        Assert.Empty(timeline.ParallelSegments);
        Assert.Single(timeline.Segments);
    }

    [Fact]
    public void Parallel_activities_may_overlap_each_other_and_survive_main_edits()
    {
        var timeline = new Timeline();
        timeline.Apply(
            [
                Primary(19, 0, 20, 0, "YouTube"),
                Parallel(19, 0, 19, 30, "Ужин"),
                Parallel(19, 15, 19, 45, "Переписка"),
            ],
            Msk(21));

        // Основная дорожка переписана — параллельные остаются.
        timeline.Apply([Primary(19, 0, 20, 0, "Сериал")], Msk(21));

        Assert.Equal(["19:00-20:00 Сериал Confirmed"], timeline.Segments.Select(s => Describe(s, Moscow)));
        Assert.Equal(2, timeline.ParallelSegments.Count);
    }

    [Fact]
    public void Parallel_activity_must_be_closed()
    {
        Assert.Throws<ArgumentException>(() =>
            new Segment(Msk(13), null, "Обед", SegmentStatus.Confirmed, SegmentSource.User, isParallel: true));
    }

    [Fact]
    public void Main_builder_rejects_parallel_segments()
    {
        Assert.Throws<ArgumentException>(() => TimelineBuilder.Build(new TimelineRequest
        {
            Window = new TimeRange(Msk(12), Msk(14)),
            NowUtc = Msk(14),
            Template = NightOnly,
            Segments = [Parallel(13, 0, 13, 30, "Обед")],
        }));
    }

    [Fact]
    public void Parallel_track_is_clipped_to_the_window()
    {
        var pieces = TimelineBuilder.BuildParallel(
            new TimeRange(Msk(13), Msk(14)),
            [Parallel(12, 30, 13, 15, "Обед"), Parallel(13, 30, 13, 45, "Кофе"), Parallel(15, 0, 15, 10, "Чай")],
            Msk(16));

        Assert.Equal(
            ["13:00-13:15 Обед Confirmed", "13:30-13:45 Кофе Confirmed"],
            pieces.Select(p => Describe(p, Moscow)));
    }
}
