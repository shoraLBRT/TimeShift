using System.Net;
using System.Text.Json.Nodes;

namespace TimeShift.Web.Tests;

/// <summary>Свои категории через REST (#52) — на настоящей базе: каждый запрос читает пользователя заново.</summary>
public class CategoryTests
{
    private static JsonNode Category(JsonNode me, string name) =>
        me["categories"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;

    private static string Key(JsonNode me, string name) => Category(me, name)["key"]!.GetValue<string>();

    private static int Minutes(JsonNode totals, string key) =>
        totals.AsArray().Where(t => t!["category"]?.GetValue<string>() == key).Sum(t => t!["minutes"]!.GetValue<int>());

    [Fact]
    public async Task Own_category_is_created_edited_and_listed_with_the_system_ones()
    {
        await using var app = new TestApp();

        var system = Category((await app.GetAsync("/api/me")).Json, "Работа");
        Assert.True(system["system"]!.GetValue<bool>());
        Assert.False(system["archived"]!.GetValue<bool>());

        var added = await app.PostAsync("/api/categories", new { name = " Музыка ", group = "rest" });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        var music = Category(added.Json, "Музыка");
        var key = music["key"]!.GetValue<string>();
        Assert.StartsWith("u.", key);
        Assert.Equal("rest", music["group"]!.GetValue<string>());
        Assert.Null(music["parent"]);
        Assert.False(music["system"]!.GetValue<bool>());
        Assert.False(music["archived"]!.GetValue<bool>());

        // Подвид: группа — от родителя, даже если прислали другую.
        var sub = await app.PostAsync("/api/categories", new { name = "Созвоны", group = "lost", parent = "work" });
        var calls = Category(sub.Json, "Созвоны");
        Assert.Equal("work", calls["parent"]!.GetValue<string>());
        Assert.Equal("useful", calls["group"]!.GetValue<string>());

        var renamed = await app.PatchAsync($"/api/categories/{key}", new { name = "Музыка и звук" });
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal(key, Key(renamed.Json, "Музыка и звук"));
        Assert.Equal("rest", Category(renamed.Json, "Музыка и звук")["group"]!.GetValue<string>());

        var moved = await app.PatchAsync($"/api/categories/{key}", new { group = "useful" });
        Assert.Equal("useful", Category(moved.Json, "Музыка и звук")["group"]!.GetValue<string>());

        var callsKey = calls["key"]!.GetValue<string>();
        var detached = await app.PatchAsync($"/api/categories/{callsKey}", new { parent = (string?)null, group = "people" });
        Assert.Null(Category(detached.Json, "Созвоны")["parent"]);
        Assert.Equal("people", Category(detached.Json, "Созвоны")["group"]!.GetValue<string>());
        var attached = await app.PatchAsync($"/api/categories/{callsKey}", new { parent = key });
        Assert.Equal(key, Category(attached.Json, "Созвоны")["parent"]!.GetValue<string>());
        Assert.Equal("useful", Category(attached.Json, "Созвоны")["group"]!.GetValue<string>());

        // Всё это пережило запросы: набор читается из базы.
        var me = (await app.GetAsync("/api/me")).Json;
        Assert.Equal(16 + 2, me["categories"]!.AsArray().Count);
        Assert.Equal(key, Category(me, "Созвоны")["parent"]!.GetValue<string>());
    }

    [Fact]
    public async Task Category_requests_are_validated()
    {
        await using var app = new TestApp();
        var key = Key((await app.PostAsync("/api/categories", new { name = "Музыка", group = "rest" })).Json, "Музыка");

        foreach (var (body, field) in new (object, string)[]
        {
            (new { name = "", group = "rest" }, "name"),
            (new { name = new string('я', 41), group = "rest" }, "name"),
            (new { name = "музыка", group = "useful" }, "name"),
            (new { name = "Работа", group = "useful" }, "name"),
            (new { name = "Чтение" }, "group"),
            (new { name = "Чтение", parent = "wasted.youtube" }, "parent"),
            (new { name = "Чтение", parent = "u.nope" }, "parent"),
        })
        {
            var bad = await app.PostAsync("/api/categories", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
            Assert.Equal(field, bad.Json["field"]!.GetValue<string>());
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync("/api/categories", new { name = "Чтение", group = "fun" })).Status);
        Assert.Equal("name", (await app.PatchAsync($"/api/categories/{key}", new { name = "Сон" })).Json["field"]!.GetValue<string>());
        Assert.Equal("parent", (await app.PatchAsync($"/api/categories/{key}", new { parent = key })).Json["field"]!.GetValue<string>());

        // Системные не правятся и не удаляются; неизвестных нет.
        Assert.Equal(HttpStatusCode.Conflict, (await app.PatchAsync("/api/categories/work", new { name = "Труд" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await app.DeleteAsync("/api/categories/wasted.youtube")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PatchAsync("/api/categories/u.nope", new { name = "Труд" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await app.DeleteAsync("/api/categories/u.nope")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Post, "/api/categories", new { name = "X", group = "rest" }, authenticated: false)).Status);

        Assert.Equal(16 + 1, (await app.GetAsync("/api/me")).Json["categories"]!.AsArray().Count);
    }

    [Fact]
    public async Task Own_category_is_available_when_recording_and_counted_in_the_totals()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        var key = Key((await app.PostAsync("/api/categories", new { name = "Музыка", group = "rest" })).Json, "Музыка");
        var guitar = Key((await app.PostAsync("/api/categories", new { name = "Гитара", parent = key })).Json, "Гитара");

        // Запись с категорией; занятие, блок шаблона и правка записи принимают её так же.
        var added = await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Репетиция", category = key });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        var piece = added.Json["day"]!["pieces"]!.AsArray().Single(p => p!["title"]!.GetValue<string>() == "Репетиция")!;
        Assert.Equal(key, piece["category"]!.GetValue<string>());
        var second = await app.PostAsync("/api/entries", new { start = app.Local(10, 0), end = app.Local(10, 30), title = "Аккорды", category = "work" });
        var segmentId = second.Json["day"]!["pieces"]!.AsArray().Single(p => p!["title"]!.GetValue<string>() == "Аккорды")!["segment"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await app.PatchAsync($"/api/entries/{segmentId}", new { category = guitar })).Status);

        var activityId = (await app.GetAsync("/api/activities")).Json.AsArray().Single(a => a!["name"]!.GetValue<string>() == "Репетиция")!["id"]!.GetValue<string>();
        Assert.Equal(key, (await app.PatchAsync($"/api/activities/{activityId}", new { category = key })).Json["category"]!.GetValue<string>());

        // Итоги дня и сводки считают её минуты (REST отдаёт их по ключам, подвид — своим ключом).
        var day = (await app.GetAsync("/api/day/2026-11-17")).Json;
        Assert.Equal(60, Minutes(day["totals"]!, key));
        Assert.Equal(30, Minutes(day["totals"]!, guitar));
        var summary = (await app.GetAsync("/api/summary")).Json;
        Assert.Equal(60, Minutes(summary["totals"]!, key));
        Assert.Equal(30, Minutes(summary["days"]!.AsArray()[^1]!["totals"]!, guitar));

        // Тот же диалог, что в Telegram: «Итого» дня складывает подвид в категорию и называет её по имени.
        var text = (await app.PostAsync("/api/messages", new { text = "/day" })).Json["replies"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Музыка 1 ч 30 мин", text[text.IndexOf("Итого:", StringComparison.Ordinal)..], StringComparison.Ordinal);

        // Блок шаблона с ней: предполагаемое время прошлых дней тоже идёт в её итог.
        var block = await app.PostAsync("/api/schedule/blocks", new { kind = "custom", title = "Оркестр", category = key, days = new[] { 1, 2, 3, 4, 5, 6, 7 }, start = "20:00", end = "21:00" });
        Assert.Equal(key, block.Json["blocks"]![0]!["category"]!.GetValue<string>());
        Assert.Equal(60 + 6 * 60, Minutes((await app.GetAsync("/api/summary")).Json["totals"]!, key));
    }

    [Fact]
    public async Task Deleting_a_category_keeps_old_entries_and_takes_it_out_of_the_choice()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        var key = Key((await app.PostAsync("/api/categories", new { name = "Музыка", group = "rest" })).Json, "Музыка");
        var guitar = Key((await app.PostAsync("/api/categories", new { name = "Гитара", parent = key })).Json, "Гитара");
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Репетиция", category = key });
        await app.PostAsync("/api/entries", new { start = app.Local(10, 0), end = app.Local(10, 30), title = "Аккорды", category = guitar });
        await app.PostAsync("/api/schedule/blocks", new { kind = "custom", title = "Оркестр", category = guitar, days = new[] { 1, 2, 3, 4, 5, 6, 7 }, start = "20:00", end = "21:00" });
        var before = (await app.GetAsync("/api/day/2026-11-17")).Json;

        var removed = await app.DeleteAsync($"/api/categories/{key}");

        // Категория и её подвид остались в наборе как удалённые — чтобы назвать старые записи.
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.True(Category(removed.Json, "Музыка")["archived"]!.GetValue<bool>());
        Assert.True(Category(removed.Json, "Гитара")["archived"]!.GetValue<bool>());
        Assert.Equal(key, Category(removed.Json, "Гитара")["parent"]!.GetValue<string>());
        Assert.Null(removed.Json["blocks"]![0]!["category"]);

        // Старые записи не изменились: те же куски, та же категория, те же итоги.
        var after = (await app.GetAsync("/api/day/2026-11-17")).Json;
        static string Stored(JsonNode day) => string.Join(" | ", day["pieces"]!.AsArray().Where(p => p!["segment"] is not null).Select(p => p!.ToJsonString()));
        Assert.Equal(2, after["pieces"]!.AsArray().Count(p => p!["segment"] is not null));
        Assert.Equal(Stored(before), Stored(after));
        Assert.Equal(before["totals"]!.ToJsonString(), after["totals"]!.ToJsonString());
        Assert.Equal(60, Minutes(after["totals"]!, key));
        Assert.Equal(30, Minutes((await app.GetAsync("/api/summary")).Json["days"]!.AsArray()[^1]!["totals"]!, guitar));
        var text = (await app.PostAsync("/api/messages", new { text = "/day" })).Json["replies"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Музыка 1 ч 30 мин", text, StringComparison.Ordinal);

        // Занятия с ней — без категории; для новых записей и правок её больше нет, второй раз не удаляется.
        Assert.All((await app.GetAsync("/api/activities")).Json.AsArray(), a => Assert.Null(a!["category"]));
        var rejected = await app.PostAsync("/api/entries", new { start = app.Local(8, 30), end = app.Local(9, 0), title = "Гаммы", category = key });
        Assert.Equal("category", rejected.Json["field"]!.GetValue<string>());
        Assert.Equal("parent", (await app.PostAsync("/api/categories", new { name = "Пианино", parent = key })).Json["field"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await app.DeleteAsync($"/api/categories/{key}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PatchAsync($"/api/categories/{guitar}", new { name = "Бас" })).Status);

        // Старую запись по-прежнему можно править, не трогая её категорию.
        var segmentId = after["pieces"]!.AsArray().Single(p => p!["title"]!.GetValue<string>() == "Репетиция")!["segment"]!["id"]!.GetValue<string>();
        var edited = await app.PatchAsync($"/api/entries/{segmentId}", new { end = app.Local(9, 45) });
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        Assert.Equal(45, Minutes(edited.Json["day"]!["totals"]!, key));

        // Завести категорию с тем же именем — вернуть ту же: прошлое и новое снова вместе.
        var again = await app.PostAsync("/api/categories", new { name = "Музыка", group = "rest" });
        Assert.Equal(key, Key(again.Json, "Музыка"));
        Assert.False(Category(again.Json, "Музыка")["archived"]!.GetValue<bool>());
        Assert.True(Category(again.Json, "Гитара")["archived"]!.GetValue<bool>());
    }
}
