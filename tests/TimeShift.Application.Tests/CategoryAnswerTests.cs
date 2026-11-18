using TimeShift.Application.Clients;
using TimeShift.Application.Conversations;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>
/// Ответ на вопрос о категории доходит и до идущей записи (#62): запись занятия без категории получает её вместе с
/// ответом — обычным, «только вечером» или фразой через LLM, — и это отдельный шаг /undo, который сам ответ не трогает.
/// </summary>
public class CategoryAnswerTests
{
    private const long ChatId = 9;

    private const string Undone = "↩️ Отменил последнее изменение ленты";

    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new();

    private ScriptedLlm _llm = new();

    private TrackingService Tracking => new(_store, _store, _store);

    private ConversationService Conversation => new(_store, _store, Tracking, _store.Applier(Tracking, _llm), _store.Speech, _store.Questions(Tracking), new YellowReview(Tracking), new DailyReview(Tracking, new YellowReview(Tracking)), _store.Renderer, _clock);

    private User Me => Assert.Single(_store.Users);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Москва (UTC+3), 14 ноября 2026.</summary>
    private static DateTime Msk(int hour, int minute = 0) =>
        new DateTime(2026, 11, 14, hour, minute, 0, DateTimeKind.Utc).AddHours(-3);

    private Task<IReadOnlyList<BotReply>> Text(string text) =>
        Conversation.HandleAsync(IncomingMessage.FromText(ChatId, "Тест", text), Ct);

    private Task<IReadOnlyList<BotReply>> Press(string data) =>
        Conversation.HandleAsync(IncomingMessage.FromButton(ChatId, "Тест", data), Ct);

    private Task<IReadOnlyList<BotReply>> Say(string text, string llmJson)
    {
        _llm.Enqueue(llmJson);
        return Text(text);
    }

    private static BotReply Question(IEnumerable<BotReply> replies) =>
        Assert.Single(replies, r => r.Text.StartsWith("❓", StringComparison.Ordinal));

    private static BotReply Remembered(IEnumerable<BotReply> replies) =>
        Assert.Single(replies, r => r.Text.StartsWith("Запомнил: ", StringComparison.Ordinal));

    private static ReplyButton Button(BotReply reply, string label) =>
        Assert.Single(reply.Buttons.SelectMany(row => row), b => b.Label == label);

    /// <summary>Идущая запись — единственная открытая.</summary>
    private Segment Running => Assert.Single(_store.SegmentsOf(Me.Id), s => s.IsOpen);

    private List<(PartOfDay Part, string Category)> Rules() =>
        _store.Rules.Where(r => r.Key.User == Me.Id).Select(r => (r.Key.Part, r.Value)).ToList();

    private async Task<string> DayTotalsAsync()
    {
        var day = Assert.Single(await Text("/day")).Text;
        return day[day.IndexOf("Итого:", StringComparison.Ordinal)..];
    }

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
    public async Task The_answer_puts_the_category_on_the_running_entry_and_undo_takes_it_off_but_keeps_the_answer()
    {
        await OnboardAsync(Msk(12));

        // Первая запись нового занятия: категории нет — бот спрашивает.
        var asked = Question(await Text("Гитара"));
        Assert.Null(Running.CategoryKey);

        _clock.UtcNow = Msk(12, 20);
        var answered = await Press(Button(asked, "Учёба").Data);
        Assert.Contains(answered, r => r.Text == "Запомнил: «Гитара» — Учёба. Текущая запись теперь — Учёба (отменить — /undo).");
        var guitar = Assert.Single(_store.Activities);
        Assert.Equal(DefaultCategories.Study, guitar.CategoryKey);

        // Та же запись — с того же начала, всё ещё идёт — теперь с категорией и считается в итогах под ней.
        var entry = Assert.Single(_store.SegmentsOf(Me.Id));
        Assert.Equal((Msk(12), true, guitar.Id, DefaultCategories.Study), (entry.StartUtc, entry.IsOpen, entry.ActivityId!.Value, entry.CategoryKey));
        Assert.Contains("Учёба 20 мин", await DayTotalsAsync(), StringComparison.Ordinal);
        Assert.Contains("Учёба", Assert.Single(await Text("/week")).Text, StringComparison.Ordinal);
        var today = DateOnly.FromDateTime(Msk(12).AddHours(3));
        var summary = await new DayService(Tracking, _clock).SummaryAsync(Me, today, today, Ct);
        Assert.Equal(20, Assert.Single(summary.Totals, t => t.Category == DefaultCategories.Study).Minutes);

        // /undo — отдельный шаг: категория с записи снята, запись идёт дальше, ответ остался у занятия.
        Assert.StartsWith(Undone, Assert.Single(await Text("/undo")).Text, StringComparison.Ordinal);
        entry = Assert.Single(_store.SegmentsOf(Me.Id));
        Assert.Equal((Msk(12), true, (string?)null), (entry.StartUtc, entry.IsOpen, entry.CategoryKey));
        Assert.Equal(DefaultCategories.Study, guitar.CategoryKey);
        Assert.DoesNotContain("Учёба", await DayTotalsAsync(), StringComparison.Ordinal);

        // Следующая запись занятия берёт категорию из ответа уже при старте.
        _clock.UtcNow = Msk(13);
        await Text("Сериал");
        _clock.UtcNow = Msk(14);
        await Text("Гитара");
        Assert.Equal((Msk(14), DefaultCategories.Study), (Running.StartUtc, Running.CategoryKey));
    }

    [Fact]
    public async Task Only_this_part_of_day_keeps_the_category_on_the_entry_without_a_second_undo_step()
    {
        await OnboardAsync(Msk(11, 30));

        var asked = Question(await Text("Ютуб"));
        _clock.UtcNow = Msk(11, 40);
        var remembered = Assert.Single(await Press(Button(asked, "Учёба").Data), r => r.Text.StartsWith("Запомнил: «Ютуб» — Учёба.", StringComparison.Ordinal));
        Assert.Equal(DefaultCategories.Study, Running.CategoryKey);

        // «Только утром»: ответ стал правилом, запись началась утром — категория на ней та же, новой правки ленты нет.
        var only = Button(remembered, "Только утром (06:00–12:00)");
        var narrowed = Assert.Single(await Press(only.Data), r => r.Text.StartsWith("Запомнил: «Ютуб» утром", StringComparison.Ordinal));
        Assert.DoesNotContain("Текущая запись", narrowed.Text, StringComparison.Ordinal);
        Assert.Equal((Msk(11, 30), DefaultCategories.Study), (Running.StartUtc, Running.CategoryKey));

        // Один /undo снимает категорию с записи; правило (сам ответ) остаётся.
        await Text("/undo");
        Assert.Equal((Msk(11, 30), (string?)null), (Running.StartUtc, Running.CategoryKey));
        Assert.Equal([(PartOfDay.Morning, DefaultCategories.Study)], Rules());
        Assert.Null(Assert.Single(_store.Activities).CategoryKey);

        // Второй /undo — уже сама запись: шаг у ответа был один.
        await Text("/undo");
        Assert.Empty(_store.SegmentsOf(Me.Id));
    }

    [Fact]
    public async Task A_rule_answer_reaches_the_entry_only_when_it_started_in_that_part_of_day()
    {
        _llm = new ScriptedLlm(enabled: true);
        await OnboardAsync(Msk(13));

        // Запись началась днём, правило — на вечер: запись остаётся без категории.
        await Say("смотрю ютуб", """{"intents":[{"type":"start_activity","activity":"YouTube"}]}""");
        _clock.UtcNow = Msk(13, 10);
        var evening = Remembered(await Say(
            "ютуб по вечерам — это отдых",
            """{"intents":[{"type":"classify","activity":"YouTube","category":"rest","when":"evening"}]}"""));
        Assert.DoesNotContain("Текущая запись", evening.Text, StringComparison.Ordinal);
        Assert.Null(Running.CategoryKey);

        // Правило на день — запись началась днём и получает его категорию; /undo снимает её, правило остаётся.
        _clock.UtcNow = Msk(13, 20);
        var afternoon = Remembered(await Say(
            "а днём ютуб — учёба",
            """{"intents":[{"type":"classify","activity":"YouTube","category":"study","when":"afternoon"}]}"""));
        Assert.StartsWith(
            "Запомнил: «YouTube» днём (12:00–18:00) — Учёба. Текущая запись теперь — Учёба (отменить — /undo).", afternoon.Text, StringComparison.Ordinal);
        Assert.Equal((Msk(13), DefaultCategories.Study), (Running.StartUtc, Running.CategoryKey));

        await Text("/undo");
        Assert.Equal((Msk(13), (string?)null), (Running.StartUtc, Running.CategoryKey));
        Assert.Equal(
            [(PartOfDay.Afternoon, DefaultCategories.Study), (PartOfDay.Evening, DefaultCategories.MindfulRest)], Rules().OrderBy(r => r.Part));
    }

    [Fact]
    public async Task An_entry_with_its_own_category_is_left_alone()
    {
        _llm = new ScriptedLlm(enabled: true);
        await OnboardAsync(Msk(12));

        var asked = Question(await Say("играю на гитаре", """{"intents":[{"type":"start_activity","activity":"Гитара"}]}"""));

        // Категорию записи назвали явно — у самого занятия её по-прежнему нет.
        _clock.UtcNow = Msk(12, 10);
        await Say("эта гитара — отдых", """{"intents":[{"type":"edit_segment","target":{"activity":"Гитара"},"changes":{"category":"rest"}}]}""");
        Assert.Equal(DefaultCategories.MindfulRest, Running.CategoryKey);
        var guitar = Assert.Single(_store.Activities);
        Assert.Null(guitar.CategoryKey);

        // Ответ на вопрос запоминается для занятия, а запись не меняется — и нового шага /undo нет.
        _clock.UtcNow = Msk(12, 20);
        var answered = await Press(Button(asked, "Учёба").Data);
        Assert.Contains(answered, r => r.Text == "Запомнил: «Гитара» — Учёба.");
        Assert.Equal(DefaultCategories.Study, guitar.CategoryKey);
        Assert.Equal((Msk(12), DefaultCategories.MindfulRest), (Running.StartUtc, Running.CategoryKey));

        // /undo отменяет явную правку записи, а не ответ.
        await Text("/undo");
        Assert.Null(Running.CategoryKey);
        Assert.Equal(DefaultCategories.Study, guitar.CategoryKey);
    }

    [Fact]
    public async Task The_answer_does_not_touch_another_running_activity_or_past_entries()
    {
        await OnboardAsync(Msk(12));

        // Вопрос о гитаре задан, но к ответу идёт уже другое занятие.
        var asked = Question(await Text("Гитара"));
        _clock.UtcNow = Msk(12, 30);
        await Text("Сериал");
        _clock.UtcNow = Msk(12, 40);
        var answered = await Press(Button(asked, "Учёба").Data);
        Assert.Contains(answered, r => r.Text == "Запомнил: «Гитара» — Учёба.");

        // Закрытая запись гитары осталась без категории, сериал — со своей.
        Assert.Null(Assert.Single(_store.SegmentsOf(Me.Id), s => s.Title == "Гитара").CategoryKey);
        Assert.Equal((Msk(12, 30), DefaultCategories.MindfulRest), (Running.StartUtc, Running.CategoryKey));
    }

    [Fact]
    public async Task A_phrase_through_the_llm_reaches_the_running_entry_too_when_it_is_the_only_intent()
    {
        _llm = new ScriptedLlm(enabled: true);
        await OnboardAsync(Msk(20, 30));

        await Say("играю на гитаре","""{"intents":[{"type":"start_activity","activity":"Guitar"}]}""");
        Assert.Null(Running.CategoryKey);

        // Вместе с другим намерением — только факт о занятии: лента в этом сообщении уже могла измениться.
        _clock.UtcNow = Msk(20, 40);
        await Say(
            "гитара — это отдых, обычно час",
            """{"intents":[{"type":"classify","activity":"Guitar","category":"rest"},{"type":"set_fact","activity":"Guitar","minutes":60}]}""");
        var guitar = Assert.Single(_store.Activities);
        Assert.Equal(DefaultCategories.MindfulRest, guitar.CategoryKey);
        Assert.Null(Running.CategoryKey);

        // Одна фраза о категории — как ответ на вопрос: категория и у занятия, и у идущей записи.
        _clock.UtcNow = Msk(20, 50);
        var saved = Remembered(await Say(
            "нет, гитара — учёба","""{"intents":[{"type":"classify","activity":"Guitar","category":"study"}]}"""));
        Assert.StartsWith("Запомнил: «Guitar» — ", saved.Text, StringComparison.Ordinal);
        Assert.EndsWith("(отменить — /undo).", saved.Text, StringComparison.Ordinal);
        Assert.Equal(DefaultCategories.Study, guitar.CategoryKey);
        Assert.Equal((Msk(20, 30), DefaultCategories.Study), (Running.StartUtc, Running.CategoryKey));

        await Text("/undo");
        Assert.Null(Running.CategoryKey);
        Assert.Equal(DefaultCategories.Study, guitar.CategoryKey);
    }
}
