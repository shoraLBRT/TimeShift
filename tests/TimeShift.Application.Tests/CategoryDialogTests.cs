using TimeShift.Application.Conversations;
using TimeShift.Application.Llm;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>Свои категории в диалоге (#52): /categories, выбор при записи, итоги дня и недели, удаление без потери прошлого.</summary>
public class CategoryDialogTests
{
    private const long ChatId = 7;

    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new(); // 09:00 UTC = 12:00 Москва

    private TrackingService Tracking => new(_store, _store);

    private ConversationService Conversation => new(_store, _store, Tracking, _store.Applier(Tracking, new ScriptedLlm()), _store.Speech, _store.Questions(Tracking), new YellowReview(Tracking), new DailyReview(Tracking, new YellowReview(Tracking)), _store.Renderer, _clock);

    private User Me => Assert.Single(_store.Users);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<IReadOnlyList<BotReply>> Text(string text) =>
        Conversation.HandleAsync(IncomingMessage.FromText(ChatId, "Тест", text), Ct);

    private Task<IReadOnlyList<BotReply>> Press(string data) =>
        Conversation.HandleAsync(IncomingMessage.FromButton(ChatId, "Тест", data), Ct);

    private static List<string> Labels(BotReply reply) => reply.Buttons.SelectMany(row => row).Select(b => b.Label).ToList();

    private async Task OnboardAsync()
    {
        await Text("/start");
        await Press("tz:Europe/Moscow");
        await Text("1-9");
        await Press("wkend:same");
        await Press("next");
        await Press("next");
        await Press("ping:30");
        await Press("ext:default");
    }

    private async Task<UserCategory> AddAsync(string group, string text)
    {
        await Press("cat:add");
        await Press($"cat:grp:{group}");
        await Text(text);
        return Me.OwnCategories[^1];
    }

    [Fact]
    public async Task Categories_command_lists_the_set_and_adds_an_own_category_and_a_subcategory()
    {
        await OnboardAsync();

        var menu = Assert.Single(await Text("/categories"));
        Assert.Contains("Полезное: Работа · Учёба · Свои проекты · Спорт", menu.Text, StringComparison.Ordinal);
        Assert.Contains("Потерянное: Залипание (Соцсети/короткие видео, YouTube, Мультиплеер-игры, Валялся, Прочее)", menu.Text, StringComparison.Ordinal);
        Assert.Equal(["➕ Добавить категорию"], Labels(menu));

        var groups = Assert.Single(await Press("cat:add"));
        Assert.Equal(["Полезное", "Необходимое", "Люди", "Отдых", "Потерянное", "↩️ Назад"], Labels(groups));
        Assert.Contains("группе «Отдых»", Assert.Single(await Press("cat:grp:Rest")).Text, StringComparison.Ordinal);

        var added = (await Text("Музыка"))[0];
        Assert.StartsWith("Добавил: «Музыка» (Отдых).", added.Text, StringComparison.Ordinal);
        Assert.Contains("Отдых: Осознанный отдых · 🔹Музыка", added.Text, StringComparison.Ordinal);
        Assert.Equal(["🔹 Музыка", "➕ Добавить категорию"], Labels(added));
        var music = Assert.Single(Me.OwnCategories);
        Assert.Equal(("Музыка", CategoryGroup.Rest, null), (music.Name, music.Group, music.ParentKey));
        Assert.Null(Me.DialogState);

        // Подвид — через двоеточие; группа берётся у родителя, а не из нажатой кнопки.
        var calls = await AddAsync("Lost", "работа: Созвоны");
        Assert.Equal(("Созвоны", CategoryGroup.Useful, DefaultCategories.Work), (calls.Name, calls.Group, calls.ParentKey));
        Assert.Contains("Работа (🔹Созвоны)", Assert.Single(await Text("/categories")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_taken_name_is_asked_again_and_a_command_leaves_the_dialog()
    {
        await OnboardAsync();
        await AddAsync("Rest", "Музыка");

        await Press("cat:add");
        await Press("cat:grp:Useful");
        var taken = Assert.Single(await Text("музыка"));
        Assert.StartsWith("Категория «Музыка» уже есть.", taken.Text, StringComparison.Ordinal);
        Assert.NotNull(Me.DialogState);
        Assert.StartsWith("Категория «Работа» уже есть.", Assert.Single(await Text("Работа")).Text, StringComparison.Ordinal);

        await Text("Чтение");
        Assert.Equal(["Музыка", "Чтение"], Me.OwnCategories.Select(c => c.Name));
        Assert.Null(Me.DialogState);

        await Press("cat:add");
        await Press("cat:grp:Useful");
        await Text("/categories");
        Assert.Null(Me.DialogState);
        Assert.Equal(2, Me.OwnCategories.Count);
    }

    [Fact]
    public async Task An_own_category_is_offered_when_recording_and_counted_in_day_and_week_totals()
    {
        await OnboardAsync();
        var music = await AddAsync("Rest", "Музыка");

        // Новое занятие: бот спрашивает категорию и предлагает свою наравне с системными.
        var question = Assert.Single(await Text("Гитара"), r => r.Text.StartsWith("❓", StringComparison.Ordinal));
        var button = Assert.Single(question.Buttons.SelectMany(row => row), b => b.Label == "Музыка");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(button.Data) <= 64, "данные кнопки Telegram — не больше 64 байт");
        Assert.Contains(await Press(button.Data), r => r.Text == "Запомнил: «Гитара» — Музыка.");
        Assert.Equal(music.Key, _store.Activities.Single(a => a.Name == "Гитара").CategoryKey);

        await Text("/add 9-11 Гитара");
        Assert.Contains(_store.SegmentsOf(Me.Id), s => s.CategoryKey == music.Key && s.Status == SegmentStatus.Confirmed);

        var day = Assert.Single(await Text("/day"));
        Assert.Contains("Музыка 2 ч", day.Text[day.Text.IndexOf("Итого:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Same(_store.Renderer.Days[^1].Categories!.Find(music.Key), _store.Renderer.Days[^1].Categories!.FindActive(music.Key));

        var week = Assert.Single(await Text("/week"));
        Assert.Contains("Музыка", week.Text, StringComparison.Ordinal);
        Assert.Contains(_store.Renderer.Weeks[^1].Stats.Categories, c => c.Key == music.Key && c.Name == "Музыка" && c.Total == TimeSpan.FromHours(2));

        // /facts называет категорию по имени, ответ текстом на вопрос о категории понимает своё имя.
        Assert.Contains("• Гитара — длительность ?, Музыка", Assert.Single(await Text("/facts")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_changes_the_name_everywhere_and_delete_keeps_old_entries()
    {
        await OnboardAsync();
        var music = await AddAsync("Rest", "Музыка");
        var question = Assert.Single(await Text("Гитара"), r => r.Text.StartsWith("❓", StringComparison.Ordinal));
        await Press(question.Buttons.SelectMany(row => row).Single(b => b.Label == "Музыка").Data);
        await Text("/add вчера 19-21 Гитара");

        var card = Assert.Single(await Press($"cat:open:{music.Key}"));
        Assert.Contains("«Музыка» — группа «Отдых»", card.Text, StringComparison.Ordinal);
        Assert.Equal(["✏️ Переименовать", "🗑 Удалить", "↩️ Назад"], Labels(card));
        await Press($"cat:ren:{music.Key}");
        Assert.StartsWith("Переименовал: «Музыка» → «Звук».", (await Text("Звук"))[0].Text, StringComparison.Ordinal);
        Assert.Contains("Звук 2 ч", Assert.Single(await Text("/day вчера")).Text, StringComparison.Ordinal);

        var removed = Assert.Single(await Press($"cat:del:{music.Key}"));
        Assert.StartsWith("Удалил «Звук». Прошлые записи с ней остались как были. Занятий без категории теперь: 1", removed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("🔹", removed.Text[removed.Text.IndexOf("Категории:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Equal(["➕ Добавить категорию"], Labels(removed));

        // Прошлое на месте и по-прежнему названо; занятие осталось без категории, в выборе её больше нет.
        Assert.Contains(_store.SegmentsOf(Me.Id), s => s.CategoryKey == music.Key);
        Assert.Contains("Звук 2 ч", Assert.Single(await Text("/day вчера")).Text, StringComparison.Ordinal);
        Assert.Null(_store.Activities.Single(a => a.Name == "Гитара").CategoryKey);
        Assert.Null(Me.Categories.FindActive(music.Key));

        // Устаревшая кнопка не ломает диалог.
        Assert.Contains("Этой категории уже нет.", Assert.Single(await Press($"cat:open:{music.Key}")).Text, StringComparison.Ordinal);
        Assert.Contains("Этой категории уже нет.", Assert.Single(await Press($"cat:del:{music.Key}")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Week_and_day_totals_fold_own_subcategories_into_their_parent_and_keep_removed_names()
    {
        var now = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var user = new User(1, "Тест", User.DefaultTimeZone, now);
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, now);
        var guitar = user.AddCategory("Гитара", CategoryGroup.Rest, music.Key, now);
        var calls = user.AddCategory("Созвоны", CategoryGroup.Useful, DefaultCategories.Work, now);
        user.RemoveCategory(music.Key, now);
        TimelinePiece Piece(double from, double to, string category) =>
            new(now.AddHours(from), now.AddHours(to), "x", null, category, SegmentStatus.Confirmed, SegmentSource.User, false);
        List<TimelinePiece> pieces = [Piece(0, 1, music.Key), Piece(1, 3, guitar.Key), Piece(3, 4, calls.Key), Piece(4, 8, DefaultCategories.Work)];

        var stats = WeekStats.Of(pieces, [], null, user.Categories);

        Assert.Equal(
            [(DefaultCategories.Work, "Работа", 5.0), (music.Key, "Музыка", 3.0)],
            stats.Categories.Select(c => (c.Key!, c.Name, c.Total.TotalHours)));
        Assert.Equal("Итого: Работа 5 ч · Музыка 3 ч", TrackingReplies.Totals(pieces, [], user.Categories));

        // Без набора пользователя свои ключи остаются как есть — ничего не теряется.
        Assert.Equal(4, WeekStats.Of(pieces, []).Categories.Count);
    }

    [Fact]
    public void The_llm_contract_lists_own_categories_and_accepts_only_active_keys()
    {
        var now = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var user = new User(1, "Тест", User.DefaultTimeZone, now);
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, now);
        var old = user.AddCategory("Старое", CategoryGroup.Rest, null, now);
        user.RemoveCategory(old.Key, now);
        var json = $$"""{"intents":[{"type":"start_activity","activity":"Гитара","category":"{{music.Key}}"},{"type":"classify","activity":"Гитара","category":"{{music.Key}}"}]}""";

        var prompt = IntentPrompt.Build(new IntentContext { NowUtc = now, TimeZone = TimeZoneInfo.Utc, Categories = user.Categories });
        Assert.Contains($"{music.Key} (Музыка)", prompt, StringComparison.Ordinal);
        Assert.Contains("wasted.youtube (YouTube)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(old.Key, prompt, StringComparison.Ordinal);

        var parsed = IntentParser.Parse(json, user.Categories);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        Assert.Equal(music.Key, Assert.IsType<StartActivityIntent>(parsed.Intents[0]).Category);
        Assert.Equal(music.Key, Assert.IsType<ClassifyIntent>(parsed.Intents[1]).Category);

        // Чужой (или системный без набора) разбор тот же ключ не знает; удалённую категорию модель назначить не может.
        Assert.False(IntentParser.Parse(json).IsValid);
        Assert.False(IntentParser.Parse(json.Replace(music.Key, old.Key, StringComparison.Ordinal), user.Categories).IsValid);
    }
}
