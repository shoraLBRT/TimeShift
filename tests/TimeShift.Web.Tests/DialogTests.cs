using System.Net;
using System.Text.Json.Nodes;

namespace TimeShift.Web.Tests;

/// <summary>
/// Диалог на настоящей базе (#65): <c>POST /api/messages</c> ведёт тот же диалог, что и бот, а сохраняет он один раз
/// в конце сообщения — всё, что сообщение читает после своей же записи, должно эту запись видеть. Хранилище в памяти
/// из тестов Application видит её всегда, поэтому эти сценарии проверяются здесь, на SQLite и на Postgres.
/// </summary>
public class DialogTests
{
    private static List<string> Buttons(ApiResponse response) =>
        response.Json["replies"]!.AsArray()
            .SelectMany(r => r!["buttons"]!.AsArray())
            .SelectMany(row => row!.AsArray())
            .Select(b => b!["data"]!.GetValue<string>())
            .ToList();

    private static string Texts(ApiResponse response) =>
        string.Join("\n", response.Json["replies"]!.AsArray().Select(r => r!["text"]!.GetValue<string>()));

    private static string? Current(ApiResponse response) => response.Json["day"]!["current"]?["title"]?.GetValue<string>();

    /// <summary>Записанные отрезки основной дорожки: «630-660 Работа», открытый — до «сейчас».</summary>
    private static List<string> Recorded(ApiResponse response) =>
        response.Json["day"]!["pieces"]!.AsArray()
            .Where(p => p!["segment"] is not null)
            .Select(p => $"{p!["startMin"]!.GetValue<int>()}-{p["endMin"]!.GetValue<int>()} {p["title"]!.GetValue<string>()}")
            .ToList();

    /// <summary>
    /// Категория самой записи — по её куску ленты: у «текущего» в ответе она дополняется категорией занятия.
    /// </summary>
    private static string? EntryCategory(ApiResponse response, string title) =>
        Assert.Single(response.Json["day"]!["pieces"]!.AsArray(), p => p!["segment"] is not null && p["title"]!.GetValue<string>() == title)!["category"]
            ?.GetValue<string>();

    /// <summary>Итоги дня по категориям, минуты; «Без категории» — пустой ключ.</summary>
    private static Dictionary<string, int> Totals(JsonNode totals) =>
        totals.AsArray().ToDictionary(t => t!["category"]?.GetValue<string>() ?? string.Empty, t => t!["minutes"]!.GetValue<int>());

    private static async Task<string?> ActivityCategoryAsync(TestApp app, string name) =>
        Assert.Single((await app.GetAsync("/api/activities")).Json.AsArray(), a => a!["name"]!.GetValue<string>() == name)!["category"]?.GetValue<string>();

    private static async Task<ApiResponse> SayAsync(TestApp app, string text, string? llmJson = null)
    {
        if (llmJson is not null)
        {
            app.Llm.Enqueue(llmJson);
        }

        var response = await app.PostAsync("/api/messages", new { text });
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response;
    }

    [Fact]
    public async Task The_question_about_a_new_activity_comes_in_the_reply_to_the_message_that_started_it()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        // Первое занятие вообще: до него «текущего» нет.
        var guitar = await SayAsync(app, "Гитара");
        Assert.Equal("Гитара", Current(guitar));
        Assert.Contains("«Гитара»", Texts(guitar), StringComparison.Ordinal);
        var asked = Assert.Single(Buttons(guitar).Select(b => b.Split(':')).Where(b => b[0] == "q").Select(b => b[1]).Distinct());

        // Ответ на вопрос — и следующий вопрос (о длительности) о том же занятии.
        var answered = await app.PostAsync("/api/messages", new { button = $"q:{asked}:study" });
        Assert.Equal("study", answered.Json["day"]!["current"]!["category"]!.GetValue<string>());
        Assert.Contains(Buttons(answered), b => b.StartsWith("q:", StringComparison.Ordinal) && b.EndsWith(":60", StringComparison.Ordinal));
        await app.PostAsync("/api/messages", new { button = Buttons(answered).First(b => b.EndsWith(":60", StringComparison.Ordinal)) });

        // Следующее новое занятие при идущем старом: вопрос — о новом, а не о том, что шло до сообщения.
        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(30);
        var walk = await SayAsync(app, "Прогулка");
        Assert.Equal("Прогулка", Current(walk));
        Assert.Contains("«Прогулка»", Texts(walk).Split('\n')[^1], StringComparison.Ordinal);
        Assert.Contains(Buttons(walk), b => b.StartsWith("q:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Intents_of_one_message_build_on_each_other_and_undo_takes_them_back_one_by_one()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        // 11:30 по Москве. Одно сообщение — три записи ленты одним сохранением.
        var said = await SayAsync(
            app,
            "с десяти работал, в одиннадцать пообедал, сейчас гитара",
            """
            {"intents":[
              {"type":"start_activity","activity":"Работа","at":"10:00","category":"work"},
              {"type":"start_activity","activity":"Обед","at":"11:00","category":"household"},
              {"type":"start_activity","activity":"Гитара","category":"study"}]}
            """);
        // Гитара началась только что — куска ленты у неё ещё нет, она «текущее».
        Assert.Equal(["600-660 Работа", "660-690 Обед"], Recorded(said));
        Assert.Equal("Гитара", Current(said));

        // /undo снимает их в обратном порядке, по одной за раз.
        var undone = await SayAsync(app, "/undo");
        Assert.Equal(["600-660 Работа", "660-690 Обед"], Recorded(undone));
        Assert.Equal("Обед", Current(undone));

        undone = await SayAsync(app, "/undo");
        Assert.Equal(["600-690 Работа"], Recorded(undone));
        Assert.Equal("Работа", Current(undone));

        undone = await SayAsync(app, "/undo");
        Assert.Empty(Recorded(undone));
        Assert.Null(Current(undone));
        Assert.Contains("Отменять нечего", Texts(await SayAsync(app, "/undo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_and_starting_in_one_message_leaves_one_copy_of_the_stopped_entry()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync("/api/now/start", new { title = "Работа", at = app.Local(10, 0) })).Status);

        var said = await SayAsync(
            app,
            "закончил работу в одиннадцать, с тех пор обедаю",
            """{"intents":[{"type":"stop_activity","at":"11:00"},{"type":"start_activity","activity":"Обед","at":"11:00","category":"household"}]}""");
        Assert.Equal(["600-660 Работа", "660-690 Обед"], Recorded(said));
        Assert.Equal("Обед", Current(said));

        // Сначала отменяется начало обеда (работа кончилась в 11:00, дальше фон), потом — «закончил».
        var undone = await SayAsync(app, "/undo");
        Assert.Equal(["600-660 Работа"], Recorded(undone));
        Assert.Null(Current(undone));

        undone = await SayAsync(app, "/undo");
        Assert.Equal(["600-690 Работа"], Recorded(undone));
        Assert.Equal("Работа", Current(undone));
    }

    [Fact]
    public async Task A_category_said_with_the_start_reaches_the_entry_and_is_a_separate_undo_step()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        // 11:30 по Москве. «Начал X, X — это категория» одним сообщением: сначала начало, потом категория (#67).
        var said = await SayAsync(
            app,
            "с одиннадцати играю на гитаре, гитара — это учёба",
            """
            {"intents":[
              {"type":"start_activity","activity":"Гитара","at":"11:00"},
              {"type":"classify","activity":"Гитара","category":"study"}]}
            """);
        Assert.Contains("Запомнил: «Гитара» — Учёба. Текущая запись теперь — Учёба (отменить — /undo).", Texts(said), StringComparison.Ordinal);
        Assert.DoesNotContain(Buttons(said), b => b.StartsWith("q:", StringComparison.Ordinal) && b.EndsWith(":study", StringComparison.Ordinal));

        // Запись гитары — с категорией и считается под ней в итогах дня и сводки.
        Assert.Equal(["660-690 Гитара"], Recorded(said));
        Assert.Equal(("Гитара", "study"), (Current(said), EntryCategory(said, "Гитара")));
        Assert.Equal(30, Totals(said.Json["day"]!["totals"]!)["study"]);
        Assert.Equal(30, Totals((await app.GetAsync("/api/summary")).Json["totals"]!)["study"]);

        // Первый /undo снимает категорию с записи: запись идёт дальше, ответ остался у занятия.
        var undone = await SayAsync(app, "/undo");
        Assert.Equal(["660-690 Гитара"], Recorded(undone));
        Assert.Equal(("Гитара", null), (Current(undone), EntryCategory(undone, "Гитара")));
        Assert.DoesNotContain("study", Totals(undone.Json["day"]!["totals"]!).Keys);
        Assert.Equal("study", await ActivityCategoryAsync(app, "Гитара"));

        // Второй — само начало.
        undone = await SayAsync(app, "/undo");
        Assert.Empty(Recorded(undone));
        Assert.Null(Current(undone));
        Assert.Contains("Отменять нечего", Texts(await SayAsync(app, "/undo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_said_before_the_start_is_taken_by_the_entry_at_its_start()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        // Те же два намерения в другом порядке: итог в ленте тот же, но правки записи нет — и шаг /undo один.
        var said = await SayAsync(
            app,
            "гитара — это учёба, играю с одиннадцати",
            """
            {"intents":[
              {"type":"classify","activity":"Гитара","category":"study"},
              {"type":"start_activity","activity":"Гитара","at":"11:00"}]}
            """);
        Assert.Contains("Запомнил: «Гитара» — Учёба.", Texts(said), StringComparison.Ordinal);
        Assert.DoesNotContain("Текущая запись теперь", Texts(said), StringComparison.Ordinal);
        Assert.Equal(["660-690 Гитара"], Recorded(said));
        Assert.Equal(("Гитара", "study"), (Current(said), EntryCategory(said, "Гитара")));
        Assert.Equal(30, Totals(said.Json["day"]!["totals"]!)["study"]);

        var undone = await SayAsync(app, "/undo");
        Assert.Empty(Recorded(undone));
        Assert.Null(Current(undone));
        Assert.Equal("study", await ActivityCategoryAsync(app, "Гитара"));
        Assert.Contains("Отменять нечего", Texts(await SayAsync(app, "/undo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_said_about_another_activity_leaves_the_running_entry_alone()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        // Идёт гитара без категории, а категория названа доте — в том же сообщении, что и начало.
        var said = await SayAsync(
            app,
            "с одиннадцати играю на гитаре, а дота — это залипание",
            """
            {"intents":[
              {"type":"start_activity","activity":"Гитара","at":"11:00"},
              {"type":"classify","activity":"Дота","category":"wasted.multiplayer"}]}
            """);
        Assert.Contains("Запомнил: «Дота»", Texts(said), StringComparison.Ordinal);
        Assert.DoesNotContain("Текущая запись теперь", Texts(said), StringComparison.Ordinal);
        Assert.Equal(["660-690 Гитара"], Recorded(said));
        Assert.Equal(("Гитара", null), (Current(said), EntryCategory(said, "Гитара")));
        Assert.DoesNotContain("wasted.multiplayer", Totals(said.Json["day"]!["totals"]!).Keys);
        Assert.Equal("wasted.multiplayer", await ActivityCategoryAsync(app, "Дота"));

        // И отдельным сообщением среди других намерений — тоже; лента от этого не менялась, /undo снимает начало.
        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(10);
        said = await SayAsync(
            app,
            "дота — отдых, обычно час",
            """{"intents":[{"type":"classify","activity":"Дота","category":"rest"},{"type":"set_fact","activity":"Дота","minutes":60}]}""");
        Assert.DoesNotContain("Текущая запись теперь", Texts(said), StringComparison.Ordinal);
        Assert.Equal(("Гитара", null), (Current(said), EntryCategory(said, "Гитара")));
        Assert.Equal("rest", await ActivityCategoryAsync(app, "Дота"));

        var undone = await SayAsync(app, "/undo");
        Assert.Empty(Recorded(undone));
        Assert.Null(Current(undone));
    }

    [Fact]
    public async Task A_category_said_with_the_stop_reaches_the_entry_only_while_it_is_still_running()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        // Намерения применяются по порядку: категория названа раньше «закончил» — запись ещё шла и получила её.
        await SayAsync(app, "с десяти играю на гитаре", """{"intents":[{"type":"start_activity","activity":"Гитара","at":"10:00"}]}""");
        var said = await SayAsync(
            app,
            "гитара — это учёба, закончил в одиннадцать",
            """{"intents":[{"type":"classify","activity":"Гитара","category":"study"},{"type":"stop_activity","at":"11:00"}]}""");
        Assert.Contains("Текущая запись теперь — Учёба", Texts(said), StringComparison.Ordinal);
        Assert.Equal(["600-660 Гитара"], Recorded(said));
        Assert.Equal((null, "study"), (Current(said), EntryCategory(said, "Гитара")));

        // «Закончил» раньше категории — запись уже закрыта, а закрытые ответ не трогает (#62).
        await SayAsync(app, "с одиннадцати вяжу", """{"intents":[{"type":"start_activity","activity":"Вязание","at":"11:00"}]}""");
        said = await SayAsync(
            app,
            "закончил, вязание — это отдых",
            """{"intents":[{"type":"stop_activity"},{"type":"classify","activity":"Вязание","category":"rest"}]}""");
        Assert.DoesNotContain("Текущая запись теперь", Texts(said), StringComparison.Ordinal);
        Assert.Equal(["600-660 Гитара", "660-690 Вязание"], Recorded(said));
        Assert.Equal((null, null), (Current(said), EntryCategory(said, "Вязание")));
        Assert.Equal("rest", await ActivityCategoryAsync(app, "Вязание"));
    }
}
