using TimeShift.Domain.Pings;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

/// <summary>spec §5.2: когда пинговать; «Всё ещё…» / «Закончил» в ленте.</summary>
public class PingAndTrackingRulesTests
{
    private static readonly WeekTemplate Template = new(
        [
            ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9)),
            new ScheduleBlock(ScheduleBlockKind.Fixed, "Работа", "work", DaysOfWeek.All, T(10), T(18)),
        ],
        Moscow);

    private static PingContext Context(DateTime now, DateTime lastSeen) => new()
    {
        NowUtc = now,
        Interval = TimeSpan.FromMinutes(30),
        LastSeenAtUtc = lastSeen,
        Template = Template,
    };

    [Fact]
    public void Next_ping_is_an_interval_after_the_latest_of_ping_and_message()
    {
        var context = Context(Msk(9, 10), Msk(9, 5));
        Assert.Equal(Msk(9, 35), PingSchedule.NextDue(context));

        var later = new PingContext { NowUtc = Msk(9, 10), Interval = context.Interval, LastSeenAtUtc = Msk(9, 5), LastPingAtUtc = Msk(9, 20), UnansweredPings = 1, Template = Template };
        Assert.Equal(Msk(9, 50), PingSchedule.NextDue(later));
    }

    [Fact]
    public void Blocks_and_sleep_push_the_ping_to_their_end()
    {
        // 9:40 + 30 = 10:10 — внутри «Работы» 10–18 → в 18:00.
        Assert.Equal(Msk(18), PingSchedule.NextDue(Context(Msk(9, 45), Msk(9, 40))));

        // С разрешением пинговать в блоках — в 10:10, но сон по-прежнему запрещён.
        var allowed = new PingContext { NowUtc = Msk(9, 45), Interval = TimeSpan.FromMinutes(30), LastSeenAtUtc = Msk(9, 40), Template = Template, PingDuringBlocks = true };
        Assert.Equal(Msk(10, 10), PingSchedule.NextDue(allowed));

        // Ночью: 00:50 + 30 = 01:20 — сон до 09:00.
        Assert.Equal(Msk(9), PingSchedule.NextDue(Context(Msk(0, 55), Msk(0, 50))));
    }

    [Fact]
    public void Overdue_ping_is_checked_against_now_not_the_past()
    {
        // Последнее сообщение в 19:00, сейчас 02:00 (сон) — не пингуем ночью, ждём 09:00.
        Assert.Equal(Msk(9), PingSchedule.NextDue(Context(Msk(2), Msk(19).AddDays(-1))));
    }

    [Fact]
    public void Open_activity_delays_the_first_ping_to_its_typical_duration_minus_elapsed()
    {
        var context = new PingContext
        {
            NowUtc = Msk(18, 30),
            Interval = TimeSpan.FromMinutes(30),
            LastSeenAtUtc = Msk(18, 30),
            Template = Template,
            OpenActivity = new OpenActivity(Msk(18, 30), "Тренировка", TimeSpan.FromMinutes(90), ElapsedBefore: TimeSpan.FromMinutes(30)),
        };

        Assert.Equal(Msk(19, 30), PingSchedule.NextDue(context));
    }

    [Fact]
    public void Quiet_hours_and_unanswered_limit()
    {
        var quiet = new PingContext { NowUtc = Msk(18, 30), Interval = TimeSpan.FromMinutes(30), LastSeenAtUtc = Msk(18, 30), Template = Template, QuietUntilUtc = Msk(21) };
        Assert.Equal(Msk(21), PingSchedule.NextDue(quiet));

        var paused = new PingContext { NowUtc = Msk(19), Interval = TimeSpan.FromMinutes(30), LastPingAtUtc = Msk(19), UnansweredPings = 2, MaxUnansweredPings = 2, Template = Template };
        Assert.Null(PingSchedule.NextDue(paused));
    }

    [Fact]
    public void Continue_confirms_time_so_far_and_keeps_the_activity_open()
    {
        var timeline = new Timeline([new Segment(Msk(14), null, "Тренировка", SegmentStatus.Confirmed, SegmentSource.User)]);

        var change = timeline.Continue(Msk(14, 40));

        Assert.Equal(["14:00-14:40 Тренировка Confirmed", "14:40-… Тренировка Confirmed"], timeline.Segments.Select(s => Describe(s, Moscow)));
        Assert.Equal(TimeSpan.FromMinutes(40), timeline.OpenSegment!.ElapsedBefore);

        timeline.Continue(Msk(15));
        Assert.Equal(TimeSpan.FromMinutes(60), timeline.OpenSegment!.ElapsedBefore);

        timeline.Stop(Msk(15, 10));
        Assert.Null(timeline.OpenSegment);
        Assert.Equal("15:00-15:10 Тренировка Confirmed", Describe(timeline.Segments[^1], Moscow));
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public void Stop_right_after_start_removes_the_activity_and_undo_restores_it()
    {
        var open = new Segment(Msk(14), null, "Тренировка", SegmentStatus.Confirmed, SegmentSource.User);
        var timeline = new Timeline([open]);

        var change = timeline.Stop(Msk(14));
        Assert.Empty(timeline.Segments);

        timeline.Undo(change);
        Assert.Same(open, timeline.OpenSegment);

        Assert.True(new Timeline().Stop(Msk(14)).IsEmpty);
        Assert.True(new Timeline().Continue(Msk(14)).IsEmpty);
    }
}
