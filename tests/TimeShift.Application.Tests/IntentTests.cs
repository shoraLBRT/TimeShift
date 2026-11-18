using System.Text.RegularExpressions;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Llm;
using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Tests;

public partial class IntentParserTests
{
    [Fact]
    public void Parses_every_intent_type()
    {
        var result = IntentParser.Parse("""
            {"intents":[
              {"type":"start_activity","activity":"Тренировка","at":"14:00","day":"today","place":"зал","category":"sport"},
              {"type":"stop_activity","at":"15:30"},
              {"type":"describe_period","day":"yesterday","from":"19:00","to":"23:00","items":[
                 {"activity":"Тренировка","category":"sport","minutes":60},
                 {"activity":"Дорога","route":{"from":"зал","to":"дом"}},
                 {"activity":"Ужин","start":"21:00","end":"21:30","parallel":true},
                 {"activity":"YouTube","rest":true}]},
              {"type":"answer","value":"25"},
              {"type":"classify","activity":"Dota 2","category":"wasted.multiplayer"},
              {"type":"set_fact","route":{"from":"зал","to":"дом"},"minutes":25},
              {"type":"quiet","until":"18:00"},
              {"type":"edit_segment","target":{"activity":"Тренировка"},"changes":{"end":"15:30"}},
              {"type":"unclear","question":"Ты про вчера или сегодня?"}
            ]}
            """);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Equal(9, result.Intents.Count);

        var start = Assert.IsType<StartActivityIntent>(result.Intents[0]);
        Assert.Equal((new TimeOnly(14, 0), "sport", "зал"), (start.At!.Value, start.Category, start.Place));
        Assert.Equal(new DayRef.DaysAgo(0), start.Day);

        var period = Assert.IsType<DescribePeriodIntent>(result.Intents[2]);
        Assert.Equal(new DayRef.DaysAgo(1), period.Day);
        Assert.Equal(4, period.Items.Count);
        Assert.Equal(60, period.Items[0].Minutes);
        Assert.Equal(new RouteRef("зал", "дом"), period.Items[1].Route);
        Assert.True(period.Items[2].Parallel);
        Assert.True(period.Items[3].Rest);

        Assert.Equal(new SetFactIntent(25, null, new RouteRef("зал", "дом")), result.Intents[5]);
        Assert.Equal(new QuietIntent(null, new TimeOnly(18, 0)), result.Intents[6]);
        var edit = Assert.IsType<EditSegmentIntent>(result.Intents[7]);
        Assert.Equal(new TimeOnly(15, 30), edit.NewEnd);
    }

    [Theory]
    [InlineData("не json", "не JSON")]
    [InlineData("""{"result":[]}""", "intents")]
    [InlineData("""{"intents":[]}""", "пуст")]
    [InlineData("""{"intents":[{"type":"dance"}]}""", "неизвестный тип")]
    [InlineData("""{"intents":[{"type":"start_activity"}]}""", "intents[0].activity: обязательное поле")]
    [InlineData("""{"intents":[{"type":"stop_activity","at":"3pm"}]}""", "HH:mm")]
    [InlineData("""{"intents":[{"type":"stop_activity","day":"завтра"}]}""", "today, yesterday")]
    [InlineData("""{"intents":[{"type":"describe_period","items":[]}]}""", "хотя бы один пункт")]
    [InlineData("""{"intents":[{"type":"describe_period","items":[{"activity":"A","minutes":30,"rest":true}]}]}""", "либо minutes")]
    [InlineData("""{"intents":[{"type":"describe_period","items":[{"activity":"A","start":"10:00"}]}]}""", "вместе")]
    [InlineData("""{"intents":[{"type":"describe_period","items":[{"activity":"A","rest":true},{"activity":"B","rest":true}]}]}""", "только у одного")]
    [InlineData("""{"intents":[{"type":"describe_period","items":[{"activity":"A","minutes":0}]}]}""", "целое от 1")]
    [InlineData("""{"intents":[{"type":"classify","activity":"Dota","category":"games"}]}""", "неизвестная категория")]
    [InlineData("""{"intents":[{"type":"set_fact","minutes":20}]}""", "ровно одно")]
    [InlineData("""{"intents":[{"type":"quiet"}]}""", "minutes или until")]
    [InlineData("""{"intents":[{"type":"edit_segment","target":{"day":"today"},"changes":{"end":"15:00"}}]}""", "at или activity")]
    [InlineData("""{"intents":[{"type":"edit_segment","target":{"at":"15:00"},"changes":{}}]}""", "нечего менять")]
    [InlineData("""{"intents":[{"type":"describe_period","part_of_day":"noon","items":[{"activity":"A"}]}]}""", "morning, afternoon")]
    public void Rejects_answers_outside_the_contract(string json, string error)
    {
        var result = IntentParser.Parse(json);

        Assert.False(result.IsValid);
        Assert.Empty(result.Intents);
        Assert.Contains(result.Errors, e => e.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_category_on_an_activity_is_dropped_not_rejected()
    {
        var result = IntentParser.Parse("""{"intents":[{"type":"start_activity","activity":"Гитара","category":"music"}]}""");

        Assert.True(result.IsValid);
        Assert.Null(Assert.IsType<StartActivityIntent>(Assert.Single(result.Intents)).Category);
    }

    [Fact]
    public void Every_example_in_the_prompt_passes_validation()
    {
        var prompt = IntentPrompt.Build(new IntentContext { NowUtc = DateTime.UtcNow, TimeZone = TimeZoneInfo.Utc });
        var examples = ExampleRegex().Matches(prompt).Select(m => m.Value).ToList();

        Assert.True(examples.Count >= 8, $"examples found: {examples.Count}");
        Assert.All(examples, json => Assert.True(IntentParser.Parse(json).IsValid, json));
    }

    [GeneratedRegex("""\{"intents":\[.*\]\}""")]
    private static partial Regex ExampleRegex();
}

public class PastTimeTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    /// <summary>Москва, суббота 14 ноября 2026.</summary>
    private static DateTime Msk(int day, int hour, int minute = 0) =>
        new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-3);

    private static readonly DateTime SixAm = Msk(14, 6);

    private static (DateTime? Start, DateTime End) Window(
        DayRef? day, int? from, int? to, PartOfDay? part = null, DateTime? now = null)
    {
        var result = PastTime.ResolveWindow(
            day, from is { } f ? new TimeOnly(f, 0) : null, to is { } t ? new TimeOnly(t % 24, 0) : null, part, now ?? SixAm, Moscow);
        Assert.Null(result.Error);
        return (result.Window!.StartUtc, result.Window.EndUtc);
    }

    [Fact]
    public void Without_a_day_the_latest_past_occurrence_is_taken()
    {
        // В 6 утра «с 19 до 21» и «вечером» — вчера.
        Assert.Equal((Msk(13, 19), Msk(13, 21)), Window(null, 19, 21));
        Assert.Equal((Msk(13, 18), Msk(14, 0)), Window(null, null, null, PartOfDay.Evening));

        // «ночью» в 6 утра — прошедшая ночь, сегодня 00–06.
        Assert.Equal((Msk(14, 0), Msk(14, 6)), Window(null, null, null, PartOfDay.Night));

        // В 20:00 «вечером» — сегодня, до сейчас.
        Assert.Equal((Msk(14, 18), Msk(14, 20)), Window(null, null, null, PartOfDay.Evening, Msk(14, 20)));
    }

    [Fact]
    public void Ranges_cross_midnight_and_are_clipped_to_now()
    {
        Assert.Equal((Msk(13, 23), Msk(14, 1)), Window(null, 23, 1));
        Assert.Equal((Msk(13, 23), Msk(14, 1)), Window(new DayRef.DaysAgo(1), 23, 1));

        // «с 5 до 7» в 6 утра — до сейчас.
        Assert.Equal((Msk(14, 5), Msk(14, 6)), Window(null, 5, 7));
    }

    [Fact]
    public void Explicit_days_weekdays_and_dates()
    {
        Assert.Equal((Msk(12, 19), Msk(12, 21)), Window(new DayRef.DaysAgo(2), 19, 21));
        Assert.Equal((Msk(10, 12), Msk(10, 15)), Window(new DayRef.Weekday(DayOfWeek.Tuesday), 12, 15));
        // «в субботу» в субботу — это сегодня: с последнего известного до сейчас.
        Assert.Equal(((DateTime?)null, SixAm), Window(new DayRef.Weekday(DayOfWeek.Saturday), null, null));
        Assert.Equal((Msk(1, 9), Msk(1, 10)), Window(new DayRef.Date(new DateOnly(2026, 11, 1)), 9, 10));

        // Часть суток дополняет недостающую границу: «вчера с 19 … весь вечер» — 19–24.
        Assert.Equal((Msk(13, 19), Msk(14, 0)), Window(new DayRef.DaysAgo(1), 19, null, PartOfDay.Evening));

        // «вчера» без часов — весь вчерашний день.
        Assert.Equal((Msk(13, 0), Msk(14, 0)), Window(new DayRef.DaysAgo(1), null, null));
    }

    [Fact]
    public void Open_start_end_only_and_errors()
    {
        // Ничего не названо — с последнего известного до сейчас.
        Assert.Equal(((DateTime?)null, SixAm), Window(null, null, null));

        // «с 5» — до сейчас; «до 3» — начало решит применение.
        Assert.Equal((Msk(14, 5), SixAm), Window(null, 5, null));
        Assert.Equal(((DateTime?)null, Msk(14, 3)), Window(null, null, 3));

        Assert.Equal(WindowError.Future,
            PastTime.ResolveWindow(new DayRef.DaysAgo(0), new TimeOnly(19, 0), new TimeOnly(21, 0), null, SixAm, Moscow).Error);
        Assert.Equal(WindowError.MissingEnd,
            PastTime.ResolveWindow(new DayRef.DaysAgo(1), new TimeOnly(19, 0), null, null, SixAm, Moscow).Error);
    }

    [Fact]
    public void Instants_and_times_inside_a_window()
    {
        Assert.Equal(Msk(13, 23), PastTime.ResolveInstant(null, new TimeOnly(23, 0), SixAm, Moscow));
        Assert.Equal(Msk(14, 5), PastTime.ResolveInstant(null, new TimeOnly(5, 0), SixAm, Moscow));
        Assert.Null(PastTime.ResolveInstant(new DayRef.DaysAgo(0), new TimeOnly(7, 0), SixAm, Moscow));

        Assert.Equal(Msk(14, 0, 30), PastTime.ResolveInWindow(new TimeOnly(0, 30), Msk(13, 23), Msk(14, 1), Moscow));
        Assert.Null(PastTime.ResolveInWindow(new TimeOnly(12, 0), Msk(13, 23), Msk(14, 1), Moscow));
    }
}

public class IntentExtractorTests
{
    private static readonly IntentContext Context = new()
    {
        NowUtc = new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc),
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"),
        OpenActivity = ("Работа", new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc)),
        KnownActivities = ["Работа", "Тренировка"],
        LastBotQuestion = "Сколько обычно едешь из зала домой?",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_answer_is_returned_with_tokens_and_context_in_the_prompt()
    {
        var llm = new ScriptedLlm("""{"intents":[{"type":"answer","value":"25"}]}""");

        var result = await new IntentExtractor(llm).ExtractAsync("минут 25", Context, Ct);

        Assert.Equal(ExtractionStatus.Ok, result.Status);
        Assert.Equal(new AnswerIntent("25"), Assert.Single(result.Intents));
        Assert.Equal((10, 5), (result.PromptTokens, result.CompletionTokens));
        var request = Assert.Single(llm.Requests);
        Assert.Equal("минут 25", request.UserMessage);
        Assert.Contains("суббота, 14 ноября 2026, 06:00", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("«Работа» с 04:00", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Сколько обычно едешь", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_answer_is_retried_once_with_the_errors()
    {
        var llm = new ScriptedLlm(
            """{"intents":[{"type":"stop_activity","at":"half past three"}]}""",
            """{"intents":[{"type":"stop_activity","at":"15:30"}]}""");

        var result = await new IntentExtractor(llm).ExtractAsync("закончил в полчетвёртого", Context, Ct);

        Assert.Equal(ExtractionStatus.Ok, result.Status);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("HH:mm", llm.Requests[1].UserMessage, StringComparison.Ordinal);
        Assert.Equal(2, result.RawResponses.Count);
    }

    [Fact]
    public async Task Two_invalid_answers_give_up()
    {
        var llm = new ScriptedLlm("""{"intents":[{"type":"dance"}]}""");

        var result = await new IntentExtractor(llm).ExtractAsync("потанцевал", Context, Ct);

        Assert.Equal(ExtractionStatus.Invalid, result.Status);
        Assert.Empty(result.Intents);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Provider_errors_and_disabled_provider()
    {
        var failing = new ScriptedLlm { Failure = new LlmException("429", isTransient: true) };
        Assert.Equal(ExtractionStatus.Unavailable, (await new IntentExtractor(failing).ExtractAsync("работаю", Context, Ct)).Status);

        var disabled = new ScriptedLlm { Enabled = false };
        Assert.Equal(ExtractionStatus.Disabled, (await new IntentExtractor(disabled).ExtractAsync("работаю", Context, Ct)).Status);
        Assert.Empty(disabled.Requests);
    }

    private sealed class ScriptedLlm(params string[] responses) : ILlmProvider
    {
        public bool Enabled { get; init; } = true;

        public LlmException? Failure { get; init; }

        public List<LlmRequest> Requests { get; } = [];

        public bool IsEnabled => Enabled;

        public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            var content = responses[Math.Min(Requests.Count - 1, responses.Length - 1)];
            return Task.FromResult(new LlmResponse(content, 10 / responses.Length, 5 / responses.Length));
        }
    }
}
