using System.Net;
using System.Text.Json.Nodes;

namespace TimeShift.Web.Tests;

/// <summary>
/// Правила классификации по времени суток через REST (#52, spec §4.2) — на настоящей базе: правило хранится фактом
/// пользователя и применяется к тому, что записывает клиент.
/// </summary>
public class RuleTests
{
    private static List<(string Part, string Category)> Rules(JsonNode activity) =>
        activity["rules"]!.AsArray().Select(r => (r!["part"]!.GetValue<string>(), r["category"]!.GetValue<string>())).ToList();

    private static async Task<JsonNode> ActivityAsync(TestApp app, string name) =>
        (await app.GetAsync("/api/activities")).Json.AsArray().Single(a => a!["name"]!.GetValue<string>() == name)!;

    /// <summary>Категория куска дня, начинающегося в эту минуту местного времени.</summary>
    private static async Task<string?> CategoryAsync(TestApp app, string date, int hour, string title)
    {
        var day = (await app.GetAsync($"/api/day/{date}")).Json;
        var piece = day["pieces"]!.AsArray().Single(p => p!["startMin"]!.GetValue<int>() == hour * 60 && p["title"]!.GetValue<string>() == title)!;
        return piece["category"]?.GetValue<string>();
    }

    [Fact]
    public async Task A_rule_is_set_listed_replaced_and_removed()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "YouTube" });
        var youtube = await ActivityAsync(app, "YouTube");
        var id = youtube["id"]!.GetValue<string>();
        Assert.Empty(Rules(youtube));

        var set = await app.PutAsync($"/api/activities/{id}/rules/evening", new { category = "rest" });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal([("evening", "rest")], Rules(set.Json));
        Assert.Null(set.Json["category"]);

        // Одно правило на часть суток: второе заменяет первое; список — от утра к ночи и читается из базы.
        await app.PutAsync($"/api/activities/{id}/rules/night", new { category = "wasted.youtube" });
        await app.PutAsync($"/api/activities/{id}/rules/evening", new { category = "study" });
        await app.PutAsync($"/api/activities/{id}/rules/morning", new { category = "study" });
        Assert.Equal([("morning", "study"), ("evening", "study"), ("night", "wasted.youtube")], Rules(await ActivityAsync(app, "YouTube")));

        // Правка самого занятия правил не трогает.
        var patched = await app.PatchAsync($"/api/activities/{id}", new { category = "wasted.youtube", typicalMin = 40 });
        Assert.Equal(3, Rules(patched.Json).Count);

        var removed = await app.DeleteAsync($"/api/activities/{id}/rules/morning");
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Equal([("evening", "study"), ("night", "wasted.youtube")], Rules(removed.Json));
        Assert.Equal(HttpStatusCode.OK, (await app.DeleteAsync($"/api/activities/{id}/rules/morning")).Status);

        // Тот же список — в /facts бота.
        var facts = (await app.PostAsync("/api/messages", new { text = "/facts" })).Json["replies"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("↳ вечером (18:00–24:00) — Учёба", facts, StringComparison.Ordinal);
        Assert.Contains("↳ ночью (00:00–06:00) — YouTube", facts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_part_category_or_activity_is_rejected()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "YouTube" });
        var id = (await ActivityAsync(app, "YouTube"))["id"]!.GetValue<string>();

        var part = await app.PutAsync($"/api/activities/{id}/rules/weekend", new { category = "rest" });
        Assert.Equal((HttpStatusCode)422, part.Status);
        Assert.Equal("part", part.Json["field"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode)422, (await app.DeleteAsync($"/api/activities/{id}/rules/weekend")).Status);

        var category = await app.PutAsync($"/api/activities/{id}/rules/evening", new { category = "games" });
        Assert.Equal((HttpStatusCode)422, category.Status);
        Assert.Equal("category", category.Json["field"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode)422, (await app.PutAsync($"/api/activities/{id}/rules/evening", new { })).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await app.PutAsync($"/api/activities/{Guid.NewGuid()}/rules/evening", new { category = "rest" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await app.DeleteAsync($"/api/activities/{Guid.NewGuid()}/rules/evening")).Status);
        Assert.Empty(Rules(await ActivityAsync(app, "YouTube")));
    }

    [Fact]
    public async Task What_the_client_records_follows_the_rule_of_the_part_of_day_it_starts_in()
    {
        await using var app = new TestApp(); // сейчас 11:30 по Москве, 17 ноября
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(9, 30), title = "YouTube" });
        var id = (await ActivityAsync(app, "YouTube"))["id"]!.GetValue<string>();
        await app.PatchAsync($"/api/activities/{id}", new { category = "wasted.youtube" });
        await app.PutAsync($"/api/activities/{id}/rules/evening", new { category = "rest" });

        // Вчера вечером — по правилу; вчера днём — по категории занятия; названная категория главнее правила.
        await app.PostAsync("/api/entries", new { start = app.Local(19, 0, -1), end = app.Local(20, 0, -1), activityId = id });
        await app.PostAsync("/api/entries", new { start = app.Local(14, 0, -1), end = app.Local(15, 0, -1), title = "youtube" });
        await app.PostAsync("/api/entries", new { start = app.Local(21, 0, -1), end = app.Local(22, 0, -1), activityId = id, category = "study" });
        Assert.Equal("rest", await CategoryAsync(app, "2026-11-16", 19, "YouTube"));
        Assert.Equal("wasted.youtube", await CategoryAsync(app, "2026-11-16", 14, "YouTube"));
        Assert.Equal("study", await CategoryAsync(app, "2026-11-16", 21, "YouTube"));

        // Часть суток — по началу записи: 17:30–18:30 началась днём.
        await app.PostAsync("/api/entries", new { start = app.Local(17, 30, -1), end = app.Local(18, 30, -1), activityId = id });
        var day = (await app.GetAsync("/api/day/2026-11-16")).Json;
        Assert.Equal("wasted.youtube", day["pieces"]!.AsArray().Single(p => p!["startMin"]!.GetValue<int>() == 17 * 60 + 30)!["category"]!.GetValue<string>());

        // Запись, которую переименовали в это занятие, получает его категорию по тем же правилам.
        var other = await app.PostAsync("/api/entries", new { start = app.Local(22, 0, -1), end = app.Local(23, 0, -1), title = "Кино", category = "work" });
        var segmentId = (await app.GetAsync("/api/day/2026-11-16")).Json["pieces"]!.AsArray()
            .Single(p => p!["title"]!.GetValue<string>() == "Кино")!["segment"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, other.Status);
        await app.PatchAsync($"/api/entries/{segmentId}", new { title = "YouTube" });
        Assert.Equal("rest", await CategoryAsync(app, "2026-11-16", 22, "YouTube"));

        // «Начать» сейчас (утро): правило на утро появляется — следующее начало идёт по нему, идущая запись не меняется.
        var started = await app.PostAsync("/api/now/start", new { activityId = id });
        Assert.Equal("wasted.youtube", started.Json["day"]!["current"]!["category"]!.GetValue<string>());
        await app.PutAsync($"/api/activities/{id}/rules/morning", new { category = "study" });
        Assert.Equal("wasted.youtube", (await app.GetAsync("/api/day/2026-11-17")).Json["current"]!["category"]!.GetValue<string>());
        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(10);
        await app.PostAsync("/api/now/start", new { title = "Чай" });
        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(10);
        var again = await app.PostAsync("/api/now/start", new { activityId = id });
        Assert.Equal("study", again.Json["day"]!["current"]!["category"]!.GetValue<string>());

        // Правило убрали: новое — по категории занятия, записанное по правилу осталось как было.
        await app.DeleteAsync($"/api/activities/{id}/rules/evening");
        await app.PostAsync("/api/entries", new { start = app.Local(20, 0, -1), end = app.Local(21, 0, -1), activityId = id });
        Assert.Equal("wasted.youtube", await CategoryAsync(app, "2026-11-16", 20, "YouTube"));
        Assert.Equal("rest", await CategoryAsync(app, "2026-11-16", 19, "YouTube"));
    }

    [Fact]
    public async Task Deleting_an_own_category_drops_its_rules()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        var me = (await app.PostAsync("/api/categories", new { name = "Музыка", group = "rest" })).Json;
        var music = me["categories"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Музыка")!["key"]!.GetValue<string>();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Гитара" });
        var id = (await ActivityAsync(app, "Гитара"))["id"]!.GetValue<string>();
        await app.PutAsync($"/api/activities/{id}/rules/evening", new { category = music });
        await app.PutAsync($"/api/activities/{id}/rules/night", new { category = "rest" });
        await app.PostAsync("/api/entries", new { start = app.Local(19, 0, -1), end = app.Local(20, 0, -1), activityId = id });

        Assert.Equal(HttpStatusCode.OK, (await app.DeleteAsync($"/api/categories/{music}")).Status);

        // Правило в удалённую категорию пропало, остальные на месте; старая запись хранит её по-прежнему.
        Assert.Equal([("night", "rest")], Rules(await ActivityAsync(app, "Гитара")));
        Assert.Equal(music, await CategoryAsync(app, "2026-11-16", 19, "Гитара"));
        await app.PostAsync("/api/entries", new { start = app.Local(20, 0, -1), end = app.Local(21, 0, -1), activityId = id });
        Assert.Null(await CategoryAsync(app, "2026-11-16", 20, "Гитара"));
        Assert.Equal((HttpStatusCode)422, (await app.PutAsync($"/api/activities/{id}/rules/evening", new { category = music })).Status);
    }
}
