using TimeShift.Domain.Periods;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

/// <summary>Инварианты на сотнях случайных входов (seed фиксирован — падения воспроизводимы).</summary>
public class RandomizedInvariantTests
{
    private static readonly TimeZoneInfo[] Zones =
    [
        Moscow,
        Berlin,
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"), // +05:45
        TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe"), // летнее время на 30 минут
    ];

    // Дни около переходов на летнее время и обычные дни 2026 года.
    private static readonly DateTime[] Anchors =
    [
        Utc(2026, 3, 8, 0), Utc(2026, 3, 29, 0), Utc(2026, 4, 5, 0), Utc(2026, 10, 4, 0),
        Utc(2026, 10, 25, 0), Utc(2026, 11, 1, 0), Utc(2026, 6, 15, 0), Utc(2026, 11, 13, 0),
    ];

    private static readonly ExtensionPolicy[] Policies =
    [
        ExtensionPolicy.UpToTypical, ExtensionPolicy.Default, ExtensionPolicy.Infinite,
        ExtensionPolicy.Custom(1.2, 3), ExtensionPolicy.Custom(2, double.PositiveInfinity),
    ];

    [Fact]
    public void Builder_output_always_covers_window_exactly()
    {
        var random = new Random(20261113);
        for (var run = 0; run < 400; run++)
        {
            var zone = Zones[random.Next(Zones.Length)];
            var baseTime = Anchors[random.Next(Anchors.Length)].AddMinutes(random.Next(-36 * 60, 36 * 60));
            var window = new TimeRange(baseTime, baseTime.AddMinutes(random.Next(1, 4 * 24 * 60)));
            var now = window.StartUtc.AddMinutes(random.Next(-6 * 60, (int)window.Duration.TotalMinutes + (6 * 60)));
            var segments = RandomSegments(random, window);
            OpenActivity? open = random.Next(3) == 0
                ? null
                : new OpenActivity(
                    now.AddMinutes(-random.Next(0, 10 * 60)),
                    "Open",
                    random.Next(4) == 0 ? null : TimeSpan.FromMinutes(random.Next(1, 240)));

            var request = new TimelineRequest
            {
                Window = window,
                NowUtc = now,
                Template = RandomTemplate(random, zone),
                Segments = segments,
                OpenActivity = open,
                Policy = Policies[random.Next(Policies.Length)],
            };

            var pieces = TimelineBuilder.Build(request);

            AssertCovers(pieces, window);
            foreach (var piece in pieces)
            {
                Assert.Equal(piece.StartUtc >= now, piece.IsFuture);
                if (piece.Source == SegmentSource.Extension)
                {
                    Assert.True(piece.EndUtc <= now, $"run {run}: extension beyond now");
                }
            }

            // Материализованные отрезки видны как есть.
            foreach (var s in segments)
            {
                Assert.All(
                    pieces.Where(p => p.StartUtc < s.EndUtc && s.StartUtc < p.EndUtc),
                    p => Assert.Equal((s.Title, s.Status), (p.Title, p.Status)));
            }
        }
    }

    [Fact]
    public void Resolver_proposals_always_cover_window_exactly()
    {
        var random = new Random(35);
        var proposals = 0;
        for (var run = 0; run < 400; run++)
        {
            var zone = Zones[random.Next(Zones.Length)];
            var start = Anchors[random.Next(Anchors.Length)].AddMinutes(random.Next(-24 * 60, 24 * 60));
            var window = new TimeRange(start, start.AddMinutes(random.Next(1, 8 * 60)));
            var items = Enumerable.Range(0, random.Next(1, 6))
                .Select(i => new PeriodItem($"item{i}", random.Next(5) switch
                {
                    0 => new DurationSpec.Rest(),
                    1 => new DurationSpec.FromFact(random.Next(2) == 0 ? "known" : "unknown"),
                    _ => new DurationSpec.Minutes(random.Next(1, 120)),
                }))
                .ToList();

            var result = PeriodResolver.Resolve(new PeriodRequest
            {
                Window = window,
                Items = items,
                Facts = new Dictionary<string, int> { ["known"] = 25 },
                TimeZone = zone,
                RoundingMinutes = new[] { 5, 10, 15 }[random.Next(3)],
            });

            if (result is PeriodProposal proposal)
            {
                proposals++;
                PeriodResolverTests.AssertExactCover(proposal, window);
            }
            else
            {
                Assert.NotEmpty(Assert.IsType<NeedsClarification>(result).Reasons);
            }
        }

        Assert.True(proposals > 50, $"only {proposals} proposals");
    }

    private static List<Segment> RandomSegments(Random random, TimeRange window)
    {
        var result = new List<Segment>();
        var cursor = window.StartUtc.AddMinutes(-random.Next(0, 120));
        var count = random.Next(0, 8);
        for (var i = 0; i < count; i++)
        {
            var start = cursor.AddMinutes(random.Next(0, 300));
            var end = start.AddMinutes(random.Next(1, 300));
            var status = (SegmentStatus)random.Next(3);
            result.Add(new Segment(start, end, random.Next(2) == 0 ? "Работа" : "Отдых", status, SegmentSource.User));
            cursor = end;
        }

        return result;
    }

    private static WeekTemplate RandomTemplate(Random random, TimeZoneInfo zone)
    {
        var blocks = new List<ScheduleBlock>();
        var attempts = random.Next(0, 8);
        for (var i = 0; i < attempts; i++)
        {
            var days = DaysOfWeek.All.Where(_ => random.Next(2) == 0).ToList();
            if (days.Count == 0)
            {
                continue;
            }

            var startTime = new TimeOnly(random.Next(24), random.Next(12) * 5);
            var endTime = startTime.AddMinutes(random.Next(1, 12 * 60));
            if (endTime == startTime)
            {
                continue;
            }

            var block = i == 0
                ? ScheduleBlock.Sleep(days, startTime, endTime)
                : new ScheduleBlock(ScheduleBlockKind.Custom, $"Блок {i}", null, days, startTime, endTime);
            try
            {
                _ = new WeekTemplate([.. blocks, block], zone);
                blocks.Add(block);
            }
            catch (ArgumentException)
            {
                // Пересечение — пробуем следующий.
            }
        }

        return new WeekTemplate(blocks, zone);
    }
}
