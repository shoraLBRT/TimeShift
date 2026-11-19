using TimeShift.Domain.Periods;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using static TimeShift.Domain.Tests.TestTime;

namespace TimeShift.Domain.Tests;

/// <summary>Сценарий spec §3.4 целиком: продление → разбор периода → применение → новая лента.</summary>
public class Spec34ScenarioTests
{
    private const string RouteGymHome = "route:зал→дом";

    private static readonly WeekTemplate Template = new([ScheduleBlock.Sleep(DaysOfWeek.All, T(1), T(9))], Moscow);

    private static readonly TimeRange Window = new(Msk(14), Msk(17));

    private static readonly PeriodItem[] Items =
    [
        new("Тренировка", new DurationSpec.Minutes(60), "sport"),
        new("Дорога зал → дом", new DurationSpec.FromFact(RouteGymHome), "commute"),
        new("YouTube", new DurationSpec.Rest()),
    ];

    private static IReadOnlyList<TimelinePiece> BuildDay(Timeline timeline) =>
        TimelineBuilder.Build(new TimelineRequest
        {
            Window = Window,
            NowUtc = Msk(17),
            Template = Template,
            Segments = timeline.Segments.Where(s => !s.IsOpen).ToList(),
            OpenActivity = timeline.OpenSegment is { } open ? OpenActivity.FromSegment(open, TimeSpan.FromMinutes(60)) : null,
            Policy = ExtensionPolicy.Default,
        });

    [Fact]
    public void Training_is_extended_then_resolved_applied_and_confirmed()
    {
        // 14:00 «тренируюсь» → открытый отрезок.
        var timeline = new Timeline();
        timeline.Apply([new Segment(Msk(14), null, "Тренировка", SegmentStatus.Confirmed, SegmentSource.User, categoryKey: "sport")], Msk(14));

        // 17:00: лента до ответа пользователя.
        var before = BuildDay(timeline);
        AssertCovers(before, Window);
        Assert.Equal(
            [
                "14:00-15:30 Тренировка Assumed",
                "15:30-16:00 Тренировка Questionable",
                "16:00-17:00 Не записано Questionable",
            ],
            before.Select(p => Describe(p, Moscow)));

        // «тренировался только час, потом поехал домой, дома смотрел ютуб».
        var resolution = PeriodResolver.Resolve(new PeriodRequest
        {
            Window = Window,
            Items = Items,
            Facts = new Dictionary<string, int> { [RouteGymHome] = 25 },
            TimeZone = Moscow,
        });
        var proposal = Assert.IsType<PeriodProposal>(resolution);

        // Пользователь нажал «✅ Верно» → применяем.
        // Описанный период доходит до «сейчас» — тренировка не продолжается после него.
        var change = timeline.Apply(proposal.Segments, Msk(17));
        Assert.Null(timeline.OpenSegment);
        Assert.Single(change.Before);

        var after = BuildDay(timeline);
        AssertCovers(after, Window);
        Assert.Equal(
            [
                "14:00-15:00 Тренировка Confirmed",
                "15:00-15:25 Дорога зал → дом Confirmed",
                "15:25-17:00 YouTube Confirmed",
            ],
            after.Select(p => Describe(p, Moscow)));

        // /undo возвращает открытое занятие и ленту «до».
        timeline.Undo(change);
        Assert.Equal(before, BuildDay(timeline));
    }

    [Fact]
    public void Without_route_fact_the_bot_asks_and_then_recomputes()
    {
        var first = PeriodResolver.Resolve(new PeriodRequest { Window = Window, Items = Items, TimeZone = Moscow });
        var clarification = Assert.IsType<NeedsClarification>(first);
        Assert.Equal([new ClarificationReason.MissingFact(RouteGymHome)], clarification.Reasons);

        // «Сколько обычно едешь из зала домой?» → «минут 25» → пересчёт.
        var second = PeriodResolver.Resolve(new PeriodRequest
        {
            Window = Window,
            Items = Items,
            Facts = new Dictionary<string, int> { [RouteGymHome] = 25 },
            TimeZone = Moscow,
        });
        Assert.Equal(3, Assert.IsType<PeriodProposal>(second).Segments.Count);
    }
}
