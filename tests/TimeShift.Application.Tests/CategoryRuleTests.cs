using TimeShift.Application.Conversations;
using TimeShift.Application.Llm;
using TimeShift.Application.Questions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>
/// Правила классификации по времени суток (spec §4.2, #52): создаются кнопкой под ответом о категории и фразой
/// через LLM, применяются при любой записи, видны и удаляются в /facts.
/// </summary>
public class CategoryRuleTests
{
    private const long ChatId = 9;

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

    private static ReplyButton Button(BotReply reply, string label) =>
        Assert.Single(reply.Buttons.SelectMany(row => row), b => b.Label == label);

    /// <summary>Категория отрезка, начавшегося в этот момент.</summary>
    private string? CategoryAt(DateTime startUtc, string title) =>
        Assert.Single(_store.SegmentsOf(Me.Id), s => s.StartUtc == startUtc && s.Title == title).CategoryKey;

    private List<(PartOfDay Part, string Category)> Rules() =>
        _store.Rules.Where(r => r.Key.User == Me.Id).Select(r => (r.Key.Part, r.Value)).ToList();

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
    public async Task Only_this_part_of_day_turns_the_answer_into_a_rule_and_the_bot_asks_again_at_another_time()
    {
        await OnboardAsync(Msk(11, 30));

        // Утро: ответ на вопрос о категории — как раньше, категория занятия; под ним — «только утром».
        var asked = Question(await Text("Ютуб"));
        var answered = await Press(Button(asked, "Учёба").Data);
        var remembered = Assert.Single(answered, r => r.Text == "Запомнил: «Ютуб» — Учёба.");
        var youtube = Assert.Single(_store.Activities);
        Assert.Equal(DefaultCategories.Study, youtube.CategoryKey);
        var only = Button(remembered, "Только утром (06:00–12:00)");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(only.Data) <= 64, "данные кнопки Telegram — не больше 64 байт");

        // Нажал: ответ стал правилом на утро, у занятия категории снова нет, о текущем отрезке бот не переспрашивает.
        var narrowed = await Press(only.Data);
        Assert.Contains(narrowed, r => r.Text.StartsWith("Запомнил: «Ютуб» утром (06:00–12:00) — Учёба. В другое время суток спрошу отдельно.", StringComparison.Ordinal) && r.ReplacesSource);
        Assert.Null(youtube.CategoryKey);
        Assert.Equal([(PartOfDay.Morning, DefaultCategories.Study)], Rules());
        Assert.DoesNotContain(_store.QuestionList, q => q.Kind == QuestionKind.ActivityCategory);
        Assert.Contains(await Press(only.Data), r => r.Text.StartsWith("Это уже учтено.", StringComparison.Ordinal));
        Assert.Equal([(PartOfDay.Morning, DefaultCategories.Study)], Rules());
        await Text("полчаса");

        // День: правило не подходит — бот спрашивает ещё раз; обычный ответ задаёт категорию занятия.
        _clock.UtcNow = Msk(12, 30);
        await Text("Сериал");
        await Text("час");
        _clock.UtcNow = Msk(13);
        var again = Question(await Text("Ютуб"));
        Assert.Contains("YouTube бывает разным", again.Text, StringComparison.Ordinal);
        Assert.Null(CategoryAt(Msk(13), "Ютуб"));
        var plain = Assert.Single(await Press(Button(again, "Отдых").Data), r => r.Text == "Запомнил: «Ютуб» — Осознанный отдых.");
        Button(plain, "Только днём (12:00–18:00)");
        Assert.Equal(DefaultCategories.MindfulRest, youtube.CategoryKey);

        // Запись задним числом: утром — по правилу, днём — по категории занятия. Записанное раньше не меняется.
        _clock.UtcNow = Msk(13, 30);
        await Text("/add 9-10 Ютуб\n12-12:20 Ютуб");
        Assert.Equal(DefaultCategories.Study, CategoryAt(Msk(9), "Ютуб"));
        Assert.Equal(DefaultCategories.MindfulRest, CategoryAt(Msk(12), "Ютуб"));
        Assert.Null(CategoryAt(Msk(13), "Ютуб"));

        // /facts показывает правило с часами и даёт его убрать.
        var facts = Assert.Single(await Text("/facts"));
        Assert.Contains("• Ютуб — 30 мин, Осознанный отдых\n   ↳ утром (06:00–12:00) — Учёба", facts.Text, StringComparison.Ordinal);
        var remove = Button(facts, "✖ Ютуб · утром");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(remove.Data) <= 64, "данные кнопки Telegram — не больше 64 байт");
        var removed = Assert.Single(await Press(remove.Data));
        Assert.StartsWith("Правило убрал", removed.Text, StringComparison.Ordinal);
        Assert.True(removed.ReplacesSource);
        Assert.DoesNotContain("↳", removed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(removed.Buttons.SelectMany(row => row), b => b.Label.StartsWith('✖'));
        Assert.Empty(Rules());
        Assert.StartsWith("Этого правила уже нет.", Assert.Single(await Press(remove.Data)).Text, StringComparison.Ordinal);

        // Без правила утро — снова по категории занятия; уже записанное утро осталось учёбой.
        await Text("/add 10-11 Ютуб");
        Assert.Equal(DefaultCategories.MindfulRest, CategoryAt(Msk(10), "Ютуб"));
        Assert.Equal(DefaultCategories.Study, CategoryAt(Msk(9), "Ютуб"));
    }

    [Fact]
    public async Task A_started_activity_gets_the_category_of_the_rule_and_is_not_asked_about()
    {
        await OnboardAsync(Msk(20));
        var youtube = await Tracking.ResolveActivityAsync(Me, "Ютуб", Ct);
        await Tracking.SetRuleAsync(Me, youtube, PartOfDay.Evening, DefaultCategories.MindfulRest, Ct);

        var started = await Text("Ютуб");

        Assert.Equal(DefaultCategories.MindfulRest, CategoryAt(Msk(20), "Ютуб"));
        Assert.DoesNotContain(started, r => r.Text.Contains("YouTube бывает разным", StringComparison.Ordinal));
        Assert.DoesNotContain(_store.QuestionList, q => q.Kind == QuestionKind.ActivityCategory);
        Assert.Null(youtube.CategoryKey);
    }

    [Fact]
    public async Task A_phrase_through_the_llm_makes_a_rule_that_periods_and_starts_follow()
    {
        _llm = new ScriptedLlm(enabled: true);
        await OnboardAsync(Msk(20, 30));

        var saved = Assert.Single(await Say(
            "ютуб по вечерам — это отдых",
            """{"intents":[{"type":"classify","activity":"YouTube","category":"rest","when":"evening"}]}"""));
        Assert.StartsWith("Запомнил: «YouTube» вечером (18:00–24:00) — Осознанный отдых.", saved.Text, StringComparison.Ordinal);
        Assert.Equal([(PartOfDay.Evening, DefaultCategories.MindfulRest)], Rules());
        Assert.Null(Assert.Single(_store.Activities).CategoryKey);

        // Без when — как раньше: категория самого занятия, правило остаётся.
        await Say("ютуб — залипание", """{"intents":[{"type":"classify","activity":"YouTube","category":"wasted.youtube"}]}""");
        Assert.Equal(DefaultCategories.WastedYouTube, Assert.Single(_store.Activities).CategoryKey);
        Assert.Equal([(PartOfDay.Evening, DefaultCategories.MindfulRest)], Rules());

        // Период: время пунктов считает код, правило применяется по началу каждого отрезка — и в предложении, и в записи.
        await Say(
            "с 17 до 19 смотрел ютуб, потом ужинал полчаса и опять ютуб",
            """{"intents":[{"type":"describe_period","from":"17:00","to":"20:30","items":[{"activity":"YouTube","start":"17:00","end":"19:00"},{"activity":"Ужин","category":"household","minutes":30},{"activity":"YouTube","rest":true}]}]}""");
        await Press("per:ok");
        Assert.Equal(DefaultCategories.WastedYouTube, CategoryAt(Msk(17), "YouTube"));
        Assert.Equal(DefaultCategories.MindfulRest, CategoryAt(Msk(19, 30), "YouTube"));

        // «Смотрю ютуб» вечером и параллельное занятие — тоже по правилу.
        await Say("смотрю ютуб", """{"intents":[{"type":"start_activity","activity":"YouTube"}]}""");
        Assert.Equal(DefaultCategories.MindfulRest, CategoryAt(Msk(20, 30), "YouTube"));
        await Say("чай по вечерам — быт", """{"intents":[{"type":"classify","activity":"Чай","category":"household","when":"evening"}]}""");
        await Say("пью чай за ютубом", """{"intents":[{"type":"start_activity","activity":"Чай","parallel":true}]}""");
        Assert.Equal(DefaultCategories.Household, Assert.Single(_store.SegmentsOf(Me.Id), s => s.IsParallel).CategoryKey);

        // Явная категория отрезка главнее правила: «нет, этот ютуб был учёбой».
        await Say("ютуб в 19:30 был учёбой", """{"intents":[{"type":"edit_segment","target":{"at":"19:40","activity":"YouTube"},"changes":{"category":"study"}}]}""");
        Assert.Equal(DefaultCategories.Study, CategoryAt(Msk(19, 30), "YouTube"));
    }

    [Fact]
    public async Task Deleting_an_own_category_drops_the_rules_that_led_to_it()
    {
        await OnboardAsync(Msk(12));
        await Press("cat:add");
        await Press("cat:grp:Rest");
        await Text("Музыка");
        var music = Assert.Single(Me.OwnCategories);

        var asked = Question(await Text("Гитара"));
        var remembered = Assert.Single(await Press(Button(asked, "Музыка").Data), r => r.Text == "Запомнил: «Гитара» — Музыка.");
        await Press(Button(remembered, "Только днём (12:00–18:00)").Data);
        Assert.Equal([(PartOfDay.Afternoon, music.Key)], Rules());
        Assert.Contains("↳ днём (12:00–18:00) — Музыка", Assert.Single(await Text("/facts")).Text, StringComparison.Ordinal);

        await Press($"cat:del:{music.Key}");

        Assert.Empty(Rules());
        Assert.DoesNotContain("↳", Assert.Single(await Text("/facts")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_classify_intent_takes_an_optional_part_of_day()
    {
        var conditional = IntentParser.Parse(
            """{"intents":[{"type":"classify","activity":"YouTube","category":"rest","when":"evening"}]}""", CategorySet.System);
        Assert.Equal(new ClassifyIntent("YouTube", "rest", PartOfDay.Evening), Assert.Single(conditional.Intents));

        var plain = IntentParser.Parse("""{"intents":[{"type":"classify","activity":"Dota 2","category":"wasted.multiplayer"}]}""", CategorySet.System);
        Assert.Null(Assert.IsType<ClassifyIntent>(Assert.Single(plain.Intents)).When);

        var wrong = IntentParser.Parse("""{"intents":[{"type":"classify","activity":"YouTube","category":"rest","when":"weekend"}]}""", CategorySet.System);
        Assert.False(wrong.IsValid);
        Assert.Contains(wrong.Errors, e => e.Contains("morning, afternoon, evening или night", StringComparison.Ordinal));

        var prompt = IntentPrompt.Build(new IntentContext { NowUtc = Msk(12), TimeZone = TimeZoneInfo.Utc });
        Assert.Contains("\"when\":\"evening\"", prompt, StringComparison.Ordinal);
    }
}
