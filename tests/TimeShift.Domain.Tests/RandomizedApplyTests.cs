using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

/// <summary>Случайные ленты и вставки: инварианты <see cref="Timeline"/> держатся, откат возвращает исходное.</summary>
public class RandomizedApplyTests
{
    private static readonly DateTime Origin = Msk(8);

    private static DateTime At(int quarter) => Origin.AddMinutes(15 * quarter);

    /// <summary>Непересекающиеся отрезки на сетке по 15 минут; последний может быть открытым.</summary>
    private static List<Segment> RandomTrack(Random random, string prefix, bool allowOpen)
    {
        var result = new List<Segment>();
        var cursor = random.Next(0, 4);
        var count = random.Next(0, 5);
        for (var i = 0; i < count; i++)
        {
            var start = cursor + random.Next(0, 3);
            var end = start + random.Next(1, 6);
            var open = allowOpen && i == count - 1 && random.Next(2) == 0;
            result.Add(new Segment(At(start), open ? null : At(end), $"{prefix}{i}", SegmentStatus.Confirmed, SegmentSource.User));
            cursor = end;
        }

        return result;
    }

    [Fact]
    public void Apply_keeps_invariants_and_undo_restores()
    {
        var random = new Random(20261114);
        for (var run = 0; run < 400; run++)
        {
            var original = RandomTrack(random, "E", allowOpen: true);
            var timeline = new Timeline(original);
            var incoming = RandomTrack(random, "N", allowOpen: random.Next(4) == 0);
            if (random.Next(3) == 0)
            {
                incoming.Add(new Segment(At(2), At(5), "P", SegmentStatus.Confirmed, SegmentSource.User, isParallel: true));
            }

            var now = At(random.Next(0, 30));
            var change = timeline.Apply(incoming, now);

            var main = timeline.Segments;
            for (var i = 1; i < main.Count; i++)
            {
                Assert.True(main[i - 1].EndUtc is { } end && end <= main[i].StartUtc, $"run {run}: overlap");
            }

            // Каждый входящий основной отрезок лежит в ленте как есть.
            foreach (var segment in incoming.Where(s => !s.IsParallel))
            {
                Assert.Contains(segment, main);
            }

            // Продолжение открытого начинается не позже «сейчас» и несёт прожитое время.
            if (timeline.OpenSegment is { ElapsedBefore: var elapsed } open && elapsed > TimeSpan.Zero)
            {
                Assert.True(open.StartUtc < now, $"run {run}: resumed after now");
            }

            timeline.Undo(change);
            Assert.Equal(original, timeline.Segments);
            Assert.Empty(timeline.ParallelSegments);
        }
    }
}
