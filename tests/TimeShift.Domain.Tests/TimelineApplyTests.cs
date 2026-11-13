using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

public class TimelineApplyTests
{
    private static Segment Seg(int fromHour, int fromMinute, int? toHour, int toMinute, string title) =>
        new(Msk(fromHour, fromMinute), toHour is { } h ? Msk(h, toMinute) : null, title, SegmentStatus.Confirmed, SegmentSource.User);

    private static Segment Seg(int fromHour, int? toHour, string title) => Seg(fromHour, 0, toHour, 0, title);

    private static List<string> Lines(Timeline t) => t.Segments.Select(s => Describe(s, Moscow)).ToList();

    [Fact]
    public void Constructor_enforces_invariants()
    {
        Assert.Throws<ArgumentException>(() => new Timeline([Seg(10, 12, "A"), Seg(11, 13, "B")]));
        Assert.Throws<ArgumentException>(() => new Timeline([Seg(10, null, "A"), Seg(11, 12, "B")]));
        Assert.Throws<ArgumentException>(() => new Timeline([Seg(10, null, "A"), Seg(11, null, "B")]));

        var timeline = new Timeline([Seg(12, 13, "B"), Seg(10, 11, "A")]);
        Assert.Equal(["10:00-11:00 A Confirmed", "12:00-13:00 B Confirmed"], Lines(timeline));
    }

    [Fact]
    public void Incoming_trims_overlapping_neighbours()
    {
        var timeline = new Timeline([Seg(10, 12, "A"), Seg(12, 14, "B")]);

        var change = timeline.Apply([Seg(11, 13, "C")]);

        Assert.Equal(
            ["10:00-11:00 A Confirmed", "11:00-13:00 C Confirmed", "13:00-14:00 B Confirmed"],
            Lines(timeline));
        Assert.Equal(["10:00-12:00 A Confirmed", "12:00-14:00 B Confirmed"], change.Before.Select(s => Describe(s, Moscow)));
        Assert.Equal(3, change.After.Count);
    }

    [Fact]
    public void Incoming_inside_a_segment_splits_it()
    {
        var timeline = new Timeline([Seg(10, 14, "Работа")]);

        timeline.Apply([Seg(11, 0, 12, 30, "Обед")]);

        Assert.Equal(
            ["10:00-11:00 Работа Confirmed", "11:00-12:30 Обед Confirmed", "12:30-14:00 Работа Confirmed"],
            Lines(timeline));
    }

    [Fact]
    public void Several_incoming_cut_one_segment_into_pieces_and_cover_others_fully()
    {
        var timeline = new Timeline([Seg(10, 16, "A"), Seg(16, 17, "B")]);

        timeline.Apply([Seg(11, 12, "X"), Seg(13, 14, "Y"), Seg(15, 18, "Z")]);

        Assert.Equal(
            [
                "10:00-11:00 A Confirmed", "11:00-12:00 X Confirmed", "12:00-13:00 A Confirmed",
                "13:00-14:00 Y Confirmed", "14:00-15:00 A Confirmed", "15:00-18:00 Z Confirmed",
            ],
            Lines(timeline));
    }

    [Fact]
    public void Non_overlapping_incoming_leaves_existing_untouched()
    {
        var timeline = new Timeline([Seg(10, 11, "A")]);

        var change = timeline.Apply([Seg(12, 13, "B")]);

        Assert.Empty(change.Before);
        Assert.Equal(2, timeline.Segments.Count);
    }

    [Fact]
    public void Overlapping_incoming_is_rejected()
    {
        var timeline = new Timeline();
        Assert.Throws<ArgumentException>(() => timeline.Apply([Seg(10, 12, "A"), Seg(11, 13, "B")]));
        Assert.Empty(timeline.Segments);
    }

    [Fact]
    public void Describing_a_period_inside_open_activity_closes_it()
    {
        var timeline = new Timeline([Seg(10, 12, "A"), Seg(14, null, "Тренировка")]);

        timeline.Apply([Seg(15, 16, "Дорога")]);

        Assert.Null(timeline.OpenSegment);
        Assert.Equal(
            ["10:00-12:00 A Confirmed", "14:00-15:00 Тренировка Confirmed", "15:00-16:00 Дорога Confirmed"],
            Lines(timeline));
    }

    [Fact]
    public void Starting_a_new_activity_closes_the_previous_open_one()
    {
        var timeline = new Timeline([Seg(14, null, "Тренировка")]);

        timeline.Apply([Seg(15, null, "Дорога")]);

        Assert.Equal(["14:00-15:00 Тренировка Confirmed", "15:00-… Дорога Confirmed"], Lines(timeline));
        Assert.Equal("Дорога", timeline.OpenSegment!.Title);
    }

    [Fact]
    public void Incoming_open_segment_replaces_everything_after_its_start()
    {
        var timeline = new Timeline([Seg(10, 12, "A"), Seg(13, 14, "B")]);

        timeline.Apply([Seg(11, null, "C")]);

        Assert.Equal(["10:00-11:00 A Confirmed", "11:00-… C Confirmed"], Lines(timeline));
    }

    [Fact]
    public void Open_segment_fully_covered_is_removed()
    {
        var timeline = new Timeline([Seg(14, null, "Тренировка")]);

        timeline.Apply([Seg(13, 15, "Другое")]);

        Assert.Equal(["13:00-15:00 Другое Confirmed"], Lines(timeline));
        Assert.Null(timeline.OpenSegment);
    }

    [Fact]
    public void Undo_restores_previous_state()
    {
        var original = new[] { Seg(10, 12, "A"), Seg(12, 14, "B"), Seg(15, null, "Open") };
        var timeline = new Timeline(original);

        var change = timeline.Apply([Seg(11, 16, "C")]);
        timeline.Undo(change);

        Assert.Equal(original, timeline.Segments);
    }

    [Fact]
    public void Undo_of_a_stale_change_fails()
    {
        var timeline = new Timeline([Seg(10, 12, "A")]);
        var first = timeline.Apply([Seg(11, 12, "B")]);
        timeline.Apply([Seg(11, 12, "C")]);

        Assert.Throws<InvalidOperationException>(() => timeline.Undo(first));
    }
}
