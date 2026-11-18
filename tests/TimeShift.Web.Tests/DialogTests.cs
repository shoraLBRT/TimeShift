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
}
