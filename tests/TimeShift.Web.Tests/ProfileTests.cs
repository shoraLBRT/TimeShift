using System.Net;
using System.Text.Json.Nodes;

namespace TimeShift.Web.Tests;

public class ProfileTests
{
    [Fact]
    public async Task Me_has_defaults_limits_and_categories()
    {
        await using var app = new TestApp();

        var me = (await app.GetAsync("/api/me")).Json;

        Assert.False(me["onboarded"]!.GetValue<bool>());
        Assert.Equal("Europe/Moscow", me["timeZone"]!.GetValue<string>());
        Assert.Equal(30, me["settings"]!["pingIntervalMin"]!.GetValue<int>());
        Assert.Equal(1.5, me["settings"]!["normFactor"]!.GetValue<double>());
        Assert.Equal(5, me["limits"]!["pingIntervalMin"]!["min"]!.GetValue<int>());
        Assert.Equal(240, me["limits"]!["pingIntervalMin"]!["max"]!.GetValue<int>());
        Assert.True(me["settings"]!["pingsEnabled"]!.GetValue<bool>());
        Assert.Equal("backOff", me["settings"]!["afterUnansweredPings"]!.GetValue<string>());
        Assert.Null(me["settings"]!["quietHours"]);
        Assert.Equal([1, 5, 10, 15], me["limits"]!["roundingMin"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Null(me["sleep"]);
        Assert.Empty(me["blocks"]!.AsArray());
        var youtube = me["categories"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == "wasted.youtube")!;
        Assert.Equal("wasted", youtube["parent"]!.GetValue<string>());
        Assert.Equal("lost", youtube["group"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_patch_updates_and_validates_with_the_field_name()
    {
        await using var app = new TestApp();

        var ok = await app.PatchAsync("/api/settings", new { displayName = "Аня", pingIntervalMin = 15, roundingMin = 10, pingDuringBlocks = true, normFactor = (double?)null, maxFactor = (double?)null });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("Аня", ok.Json["displayName"]!.GetValue<string>());
        Assert.Equal(15, ok.Json["settings"]!["pingIntervalMin"]!.GetValue<int>());
        Assert.True(ok.Json["settings"]!["pingDuringBlocks"]!.GetValue<bool>());
        Assert.Null(ok.Json["settings"]!["normFactor"]);
        Assert.Null(ok.Json["settings"]!["maxFactor"]);

        foreach (var (body, field) in new (object, string)[]
        {
            (new { pingIntervalMin = 4 }, "pingIntervalMin"),
            (new { pingIntervalMin = 241 }, "pingIntervalMin"),
            (new { maxUnansweredPings = 0 }, "maxUnansweredPings"),
            (new { eveningReviewOffsetMin = 181 }, "eveningReviewOffsetMin"),
            (new { roundingMin = 7 }, "roundingMin"),
            (new { timeZone = "Mars/Olympus" }, "timeZone"),
            (new { displayName = "  " }, "displayName"),
            (new { normFactor = 0.0 }, "normFactor"),
        })
        {
            var bad = await app.PatchAsync("/api/settings", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
            Assert.Equal("validation", bad.Error);
            Assert.Equal(field, bad.Json["field"]!.GetValue<string>());
        }

        var factors = await app.PatchAsync("/api/settings", new { normFactor = 2.0, maxFactor = 1.5 });
        Assert.Equal("maxFactor", factors.Json["field"]!.GetValue<string>());

        var pings = await app.PatchAsync("/api/settings", new { pingsEnabled = false, afterUnansweredPings = "stop", quietHours = new { start = "22:00", end = "08:00" } });
        Assert.Equal(HttpStatusCode.OK, pings.Status);
        Assert.False(pings.Json["settings"]!["pingsEnabled"]!.GetValue<bool>());
        Assert.Equal("stop", pings.Json["settings"]!["afterUnansweredPings"]!.GetValue<string>());
        Assert.Equal("22:00", pings.Json["settings"]!["quietHours"]!["start"]!.GetValue<string>());
        Assert.Equal("08:00", pings.Json["settings"]!["quietHours"]!["end"]!.GetValue<string>());

        var same = await app.PatchAsync("/api/settings", new { quietHours = new { start = "09:00", end = "09:00" } });
        Assert.Equal("quietHours", same.Json["field"]!.GetValue<string>());

        var cleared = await app.PatchAsync("/api/settings", new { quietHours = (object?)null, afterUnansweredPings = "backOff" });
        Assert.Null(cleared.Json["settings"]!["quietHours"]);
        Assert.Equal("backOff", cleared.Json["settings"]!["afterUnansweredPings"]!.GetValue<string>());
        Assert.False(cleared.Json["settings"]!["pingsEnabled"]!.GetValue<bool>());

        var zone = await app.PatchAsync("/api/settings", new { timeZone = "Asia/Yekaterinburg", pingIntervalMin = 240 });
        Assert.Equal("Asia/Yekaterinburg", zone.Json["timeZone"]!.GetValue<string>());
        Assert.Equal(240, zone.Json["settings"]!["pingIntervalMin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Sleep_and_blocks_round_trip_and_overlaps_are_409()
    {
        await using var app = new TestApp();

        var early = await app.PostAsync("/api/onboarding/complete");
        Assert.Equal(HttpStatusCode.Conflict, early.Status);

        var sleep = await app.PutAsync("/api/schedule/sleep", new { start = "00:30", end = "08:30", weekend = new { start = "02:00", end = "10:00" } });
        Assert.Equal(HttpStatusCode.OK, sleep.Status);
        Assert.Equal("00:30", sleep.Json["sleep"]!["start"]!.GetValue<string>());
        Assert.Equal("02:00", sleep.Json["sleep"]!["weekend"]!["start"]!.GetValue<string>());

        var work = await app.PostAsync("/api/schedule/blocks", new { kind = "fixed", title = "Работа", days = new[] { 1, 2, 3, 4, 5 }, start = "11:00", end = "19:00" });
        Assert.Equal(HttpStatusCode.OK, work.Status);
        var block = Assert.Single(work.Json["blocks"]!.AsArray())!;
        Assert.Equal("work", block["category"]!.GetValue<string>());
        Assert.Equal([1, 2, 3, 4, 5], block["days"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal("fixed", block["kind"]!.GetValue<string>());
        var id = block["id"]!.GetValue<string>();

        var overlap = await app.PostAsync("/api/schedule/blocks", new { kind = "custom", title = "Йога", days = new[] { 1 }, start = "18:00", end = "20:00" });
        Assert.Equal(HttpStatusCode.Conflict, overlap.Status);
        Assert.Contains("Работа", overlap.Json["message"]!.GetValue<string>(), StringComparison.Ordinal);

        var overSleep = await app.PostAsync("/api/schedule/blocks", new { kind = "custom", title = "Йога", days = new[] { 1 }, start = "07:00", end = "09:00" });
        Assert.Equal(HttpStatusCode.Conflict, overSleep.Status);

        var moved = await app.PutAsync($"/api/schedule/blocks/{id}", new { kind = "fixed", title = "Работа", days = new[] { 1, 2, 3, 4, 5 }, start = "12:00", end = "20:00" });
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        var movedBlock = Assert.Single(moved.Json["blocks"]!.AsArray())!;
        Assert.Equal("12:00", movedBlock["start"]!.GetValue<string>());

        // Неудачное обновление оставляет блок как был.
        var conflictingMove = await app.PutAsync($"/api/schedule/blocks/{movedBlock["id"]!.GetValue<string>()}", new { kind = "fixed", title = "Работа", days = new[] { 1 }, start = "07:00", end = "20:00" });
        Assert.Equal(HttpStatusCode.Conflict, conflictingMove.Status);
        var afterFailure = await app.GetAsync("/api/me");
        Assert.Equal("12:00", Assert.Single(afterFailure.Json["blocks"]!.AsArray())!["start"]!.GetValue<string>());

        var invalid = await app.PostAsync("/api/schedule/blocks", new { kind = "fixed", title = "X", days = new[] { 8 }, start = "10:00", end = "11:00" });
        Assert.Equal("days", invalid.Json["field"]!.GetValue<string>());
        var sleepKind = await app.PostAsync("/api/schedule/blocks", new { kind = "sleep", title = "X", days = new[] { 1 }, start = "10:00", end = "11:00" });
        Assert.Equal("kind", sleepKind.Json["field"]!.GetValue<string>());

        var current = (await app.GetAsync("/api/me")).Json["blocks"]!.AsArray()[0]!["id"]!.GetValue<string>();
        var removed = await app.DeleteAsync($"/api/schedule/blocks/{current}");
        Assert.Empty(removed.Json["blocks"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.DeleteAsync($"/api/schedule/blocks/{current}")).Status);

        var done = await app.PostAsync("/api/onboarding/complete");
        Assert.Equal(HttpStatusCode.OK, done.Status);
        Assert.True(done.Json["onboarded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Activities_list_and_patch()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/now/start", new { title = "Чтение" });

        var list = (await app.GetAsync("/api/activities")).Json.AsArray();
        var reading = Assert.Single(list)!;
        Assert.Equal("Чтение", reading["name"]!.GetValue<string>());
        Assert.NotNull(reading["lastUsedUtc"]);
        Assert.Null(reading["typicalMin"]);
        var id = reading["id"]!.GetValue<string>();

        var patched = await app.PatchAsync($"/api/activities/{id}", new { name = "Чтение книг", category = "projects", typicalMin = 90 });
        Assert.Equal(HttpStatusCode.OK, patched.Status);
        Assert.Equal("Чтение книг", patched.Json["name"]!.GetValue<string>());
        Assert.Equal("projects", patched.Json["category"]!.GetValue<string>());
        Assert.Equal(90, patched.Json["typicalMin"]!.GetValue<int>());

        var cleared = await app.PatchAsync($"/api/activities/{id}", new { typicalMin = (int?)null });
        Assert.Null(cleared.Json["typicalMin"]);
        Assert.Equal("projects", cleared.Json["category"]!.GetValue<string>());

        Assert.Equal("typicalMin", (await app.PatchAsync($"/api/activities/{id}", new { typicalMin = 1441 })).Json["field"]!.GetValue<string>());
        Assert.Equal("category", (await app.PatchAsync($"/api/activities/{id}", new { category = "nope" })).Json["field"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await app.PatchAsync($"/api/activities/{Guid.NewGuid()}", new { name = "X" })).Status);
    }
}
