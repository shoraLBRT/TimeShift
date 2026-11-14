using TimeShift.Application.Conversations;
using TimeShift.Application.Llm;
using TimeShift.Application.Pings;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>Текст через LLM (фейковый) → намерения → лента (#6, spec §3.4–3.6, §5.3, §5.6).</summary>
public class IntentApplierTests
{
    private const long ChatId = 11;

    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new();

    private readonly ScriptedLlm _llm = new(enabled: true);

    private TrackingService Tracking => new(_store, _store);

    private ConversationService Conversation => new(_store, _store, Tracking, _store.Applier(Tracking, _llm), _store.Speech, _store.Questions(Tracking), _clock);

    private PingService Pings => new(_store, Tracking, _store.Applier(Tracking, _llm), _store, _store, _clock);

    private User Me => Assert.Single(_store.Users);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Москва (UTC+3), 14 ноября 2026.</summary>
    private static DateTime Msk(int hour, int minute = 0, int day = 14) =>
        new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-3);

    private Task<IReadOnlyList<BotReply>> Text(string text) =>
        Conversation.HandleAsync(IncomingMessage.FromText(ChatId, "Тест", text), Ct);

    private Task<IReadOnlyList<BotReply>> Press(string data) =>
        Conversation.HandleAsync(IncomingMessage.FromButton(ChatId, "Тест", data), Ct);

    /// <summary>Следующий ответ модели — и сообщение пользователя.</summary>
    private Task<IReadOnlyList<BotReply>> Say(string text, string llmJson)
    {
        _llm.Enqueue(llmJson);
        return Text(text);
    }

    private List<string> Main() => _store.SegmentsOf(Me.Id).Where(s => !s.IsParallel).Select(Describe).ToList();

    private List<string> Parallel() => _store.SegmentsOf(Me.Id).Where(s => s.IsParallel).Select(Describe).ToList();

    private string Describe(Segment s) =>
        $"{TrackingReplies.Clock(s.StartUtc, Me.TimeZoneInfo)}-{(s.EndUtc is { } e ? TrackingReplies.Clock(e, Me.TimeZoneInfo) : "…")} {s.Title} {s.Status}";

    private static List<string> Data(IEnumerable<BotReply> replies) =>
        replies.SelectMany(r => r.Buttons).SelectMany(row => row).Select(b => b.Data).ToList();

    private async Task OnboardAsync(DateTime at)
    {
        _clock.UtcNow = at;
        await Text("/start");
        await Press("tz:Europe/Moscow");
        await Text("1-9");
        await Press("wkend:same");
        await Press("next");
        await Press("next");
        await Press("ping:30");
        await Press("ext:default");
    }

    [Fact]
    public async Task Scenario_3_4_by_text_asks_for_the_route_then_proposes_and_records()
    {
        await OnboardAsync(Msk(13, 50));
        _clock.UtcNow = Msk(14);
        await Say("тренируюсь", """{"intents":[{"type":"start_activity","activity":"Тренировка","category":"sport"}]}""");
        Assert.Equal(["14:00-… Тренировка Confirmed"], Main());

        _clock.UtcNow = Msk(17);
        var question = await Say(
            "тренировался только час, потом поехал домой, дома смотрел ютуб",
            """{"intents":[{"type":"describe_period","items":[{"activity":"Тренировка","minutes":60},{"activity":"Дорога","category":"commute","route":{"from":"зал","to":"дом"}},{"activity":"YouTube","rest":true}]}]}""");
        Assert.Contains(question, r => r.Text.Contains("дорога зал → дом", StringComparison.Ordinal));
        Assert.True(_store.PendingOf(Me.Id)!.IsWaitingForFact);

        // Вопрос бота уходит в контекст модели — она отвечает answer.
        _clock.UtcNow = Msk(17, 1);
        var proposal = await Say("минут 25", """{"intents":[{"type":"answer","value":"25"}]}""");
        Assert.Contains("Сколько обычно занимает дорога зал → дом", _llm.Requests[^1].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(proposal, r => r.Text.StartsWith("Запомнил", StringComparison.Ordinal));
        var offer = Assert.Single(proposal, r => r.Text.StartsWith("Записать так?", StringComparison.Ordinal));
        Assert.Contains("14:00–15:00 Тренировка", offer.Text, StringComparison.Ordinal);
        Assert.Contains("15:25–17:00 YouTube", offer.Text, StringComparison.Ordinal);
        Assert.Equal(25, _store.Facts[(Me.Id, "route:зал→дом")]);

        // Пока не нажал «Верно» — в ленте ничего не поменялось.
        Assert.Equal(["14:00-… Тренировка Confirmed"], Main());

        var done = await Press("per:ok");
        Assert.True(Assert.Single(done).ReplacesSource);
        Assert.Equal(
            ["14:00-15:00 Тренировка Confirmed", "15:00-15:25 Дорога Confirmed", "15:25-17:00 YouTube Confirmed"],
            Main());
        Assert.Null(_store.PendingOf(Me.Id));

        await Text("/undo");
        Assert.Equal(["14:00-… Тренировка Confirmed"], Main());
    }

    [Fact]
    public async Task At_six_in_the_morning_yesterday_evening_is_recorded_and_the_open_activity_survives()
    {
        await OnboardAsync(Msk(18, 0, day: 13));
        _clock.UtcNow = Msk(5, 30);
        await Say("читаю", """{"intents":[{"type":"start_activity","activity":"Чтение"}]}""");

        _clock.UtcNow = Msk(6);
        var offer = await Say(
            "вчера с 19 до 21 был в зале",
            """{"intents":[{"type":"describe_period","day":"yesterday","from":"19:00","to":"21:00","items":[{"activity":"Тренировка","category":"sport","rest":true}]}]}""");
        Assert.Contains(offer, r => r.Text.Contains("19:00–21:00 Тренировка", StringComparison.Ordinal));

        await Press("per:ok");

        Assert.Contains(_store.SegmentsOf(Me.Id), s =>
            s.Title == "Тренировка" && s.StartUtc == Msk(19, 0, day: 13) && s.EndUtc == Msk(21, 0, day: 13) && s.Status == SegmentStatus.Confirmed);
        Assert.Contains("05:30-… Чтение Confirmed", Main());
        Assert.Equal("sport", Assert.Single(_store.Activities, a => a.Name == "Тренировка").CategoryKey);
    }

    [Fact]
    public async Task Unconfirmed_proposal_becomes_assumed_after_timeout_or_on_next_message()
    {
        await OnboardAsync(Msk(9));
        _clock.UtcNow = Msk(12);
        var period = """{"intents":[{"type":"describe_period","from":"10:00","to":"11:00","items":[{"activity":"Уборка","rest":true}]}]}""";
        await Say("с 10 до 11 убирался", period);

        _clock.UtcNow = Msk(12, 9);
        await Pings.RunAsync(Ct);
        Assert.NotNull(_store.PendingOf(Me.Id));

        _clock.UtcNow = Msk(12, 11);
        await Pings.RunAsync(Ct);
        Assert.Null(_store.PendingOf(Me.Id));
        Assert.Contains("10:00-11:00 Уборка Assumed", Main());

        // Новое сообщение при висящем предложении — оно тоже записывается как ⚪.
        await Say("с 11 до 12 гулял", """{"intents":[{"type":"describe_period","from":"11:00","to":"12:00","items":[{"activity":"Прогулка","rest":true}]}]}""");
        await Say("работаю", """{"intents":[{"type":"start_activity","activity":"Работа"}]}""");
        Assert.Contains("11:00-12:00 Прогулка Assumed", Main());
        Assert.Contains("12:11-… Работа Confirmed", Main());
    }

    [Fact]
    public async Task Edit_button_discards_the_proposal()
    {
        await OnboardAsync(Msk(9));
        _clock.UtcNow = Msk(12);
        await Say("с 10 до 11 убирался", """{"intents":[{"type":"describe_period","from":"10:00","to":"11:00","items":[{"activity":"Уборка","rest":true}]}]}""");

        var replies = await Press("per:edit");

        Assert.Contains(replies, r => r.Text.Contains("не записываю", StringComparison.Ordinal));
        Assert.Null(_store.PendingOf(Me.Id));
        Assert.Empty(Main());
        Assert.Contains(await Press("per:ok"), r => r.Text.Contains("неактуально", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parallel_activities_go_to_their_own_track()
    {
        await OnboardAsync(Msk(9));
        _clock.UtcNow = Msk(10);
        await Say("работаю", """{"intents":[{"type":"start_activity","activity":"Работа","category":"work"}]}""");

        _clock.UtcNow = Msk(13);
        var lunch = await Say("обедаю за компом", """{"intents":[{"type":"start_activity","activity":"Обед","parallel":true}]}""");
        Assert.Contains(lunch, r => r.Text.Contains("Параллельно", StringComparison.Ordinal));
        Assert.Equal(["10:00-… Работа Confirmed"], Main());
        Assert.Equal(["13:00-13:30 Обед Assumed"], Parallel());

        _clock.UtcNow = Msk(20);
        await Text("/undo");
        await Say("закончил в 19", """{"intents":[{"type":"stop_activity","at":"19:00"}]}""");
        await Say(
            "ел и смотрел ютуб минут 30",
            """{"intents":[{"type":"describe_period","items":[{"activity":"YouTube","minutes":30},{"activity":"Еда","minutes":30,"parallel":true}]}]}""");
        await Press("per:ok");

        Assert.Contains("19:00-19:30 YouTube Confirmed", Main());
        Assert.Equal(["19:00-19:30 Еда Confirmed"], Parallel());
    }

    [Fact]
    public async Task Facts_classification_and_quiet_are_applied_at_once()
    {
        await OnboardAsync(Msk(9));
        _clock.UtcNow = Msk(14);

        await Say("тренировка у меня полтора часа", """{"intents":[{"type":"set_fact","activity":"Тренировка","minutes":90}]}""");
        Assert.Equal(TimeSpan.FromMinutes(90), Assert.Single(_store.Activities).TypicalDuration);

        await Say("дота — это залипание", """{"intents":[{"type":"classify","activity":"Dota 2","category":"wasted.multiplayer"}]}""");
        Assert.Equal("wasted.multiplayer", _store.Activities.Single(a => a.Name == "Dota 2").CategoryKey);

        await Say("от дома до работы минут 40", """{"intents":[{"type":"set_fact","route":{"from":"Дом","to":"Работа"},"minutes":40}]}""");
        Assert.Equal(40, _store.Facts[(Me.Id, "route:дом→работа")]);

        var quiet = await Say("не пиши до 18", """{"intents":[{"type":"quiet","until":"18:00"}]}""");
        Assert.Equal(Msk(18), Me.QuietUntilUtc);
        Assert.Contains(quiet, r => r.Text.Contains("до 18:00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unresolvable_periods_explain_what_is_missing()
    {
        await OnboardAsync(Msk(9));
        _clock.UtcNow = Msk(13);

        var vague = await Say(
            "с утра убирался, потом ходил в магазин",
            """{"intents":[{"type":"describe_period","part_of_day":"morning","items":[{"activity":"Уборка"},{"activity":"Магазин"}]}]}""");
        Assert.Contains(vague, r => r.Text.Contains("сколько длилось каждое", StringComparison.Ordinal));
        Assert.Null(_store.PendingOf(Me.Id));

        var future = await Say("с 19 до 21 буду в зале", """{"intents":[{"type":"describe_period","day":"today","from":"19:00","to":"21:00","items":[{"activity":"Зал","rest":true}]}]}""");
        Assert.Contains(future, r => r.Text.Contains("в будущем", StringComparison.Ordinal));

        var tooMuch = await Say(
            "с 12 до 13 два часа читал",
            """{"intents":[{"type":"describe_period","from":"12:00","to":"13:00","items":[{"activity":"Чтение","minutes":120}]}]}""");
        Assert.Contains(tooMuch, r => r.Text.Contains("Не сходится", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_and_unclear_answers_offer_buttons_and_everything_is_logged()
    {
        await OnboardAsync(Msk(9));
        _clock.UtcNow = Msk(13);

        _llm.Enqueue("""{"intents":[{"type":"dance"}]}""");
        var invalid = await Say("потанцевал", """{"intents":[{"type":"dance"}]}""");
        Assert.Contains(invalid, r => r.Text.StartsWith("Не понял", StringComparison.Ordinal));
        Assert.Contains("act:other", Data(invalid));

        var unclear = await Say("ну такое", """{"intents":[{"type":"unclear","question":"Ты про сейчас или про вчера?"}]}""");
        Assert.Contains(unclear, r => r.Text == "Ты про сейчас или про вчера?");

        Assert.Equal(["Invalid", "Ok"], _store.Log.Select(l => l.Status));
        Assert.All(_store.Log, l => Assert.Equal(Me.Id, l.UserId));
    }

    [Theory]
    [InlineData("25", 25)]
    [InlineData("минут 40", 40)]
    [InlineData("полчаса", 30)]
    [InlineData("час", 60)]
    [InlineData("полтора часа", 90)]
    [InlineData("2 часа", 120)]
    [InlineData("1,5 часа", 90)]
    [InlineData("не знаю", null)]
    public void Minutes_are_parsed_from_answers(string text, int? minutes)
    {
        Assert.Equal(minutes, IntentApplier.ParseMinutes(text));
    }
}
