using System.Net;
using System.Text.Json.Nodes;

namespace TimeShift.Web.Tests;

public class TimelineTests
{
    private static JsonArray Pieces(ApiResponse response, string path = "day") => response.Json[path]!["pieces"]!.AsArray();

    private static JsonNode? PieceTitled(JsonNode day, string title) =>
        day["pieces"]!.AsArray().FirstOrDefault(p => p!["title"]!.GetValue<string>() == title);

    [Fact]
    public async Task Day_pieces_are_contiguous_and_cover_the_whole_day()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var day = (await app.GetAsync("/api/day/2026-11-17")).Json;

        Assert.Equal("2026-11-17", day["date"]!.GetValue<string>());
        Assert.True(day["isToday"]!.GetValue<bool>());
        Assert.Equal(1440, day["dayMinutes"]!.GetValue<int>());
        Assert.Equal(690, day["nowMin"]!.GetValue<int>());
        Assert.Equal("2026-11-16T21:00:00Z", day["startUtc"]!.GetValue<string>());
        Assert.Equal("2026-11-17T21:00:00Z", day["endUtc"]!.GetValue<string>());
        Assert.Null(day["current"]);

        var pieces = day["pieces"]!.AsArray();
        Assert.Equal(0, pieces[0]!["startMin"]!.GetValue<int>());
        Assert.Equal(1440, pieces[^1]!["endMin"]!.GetValue<int>());
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1]!["endMin"]!.GetValue<int>(), pieces[i]!["startMin"]!.GetValue<int>());
        }

        var sleep = PieceTitled(day, "Сон")!;
        Assert.Equal(30, sleep["startMin"]!.GetValue<int>());
        Assert.Equal(510, sleep["endMin"]!.GetValue<int>());
        Assert.Equal("schedule", sleep["source"]!.GetValue<string>());
        Assert.Equal("assumed", sleep["status"]!.GetValue<string>());
        Assert.Equal("sleep", sleep["category"]!.GetValue<string>());
        Assert.Null(sleep["segment"]);
        Assert.False(sleep["future"]!.GetValue<bool>());

        Assert.All(pieces.Where(p => p!["startMin"]!.GetValue<int>() >= 690), p => Assert.True(p!["future"]!.GetValue<bool>()));
        Assert.Equal(480, day["status"]!["assumedMin"]!.GetValue<int>());
        Assert.Equal(180 + 30, day["status"]!["questionableMin"]!.GetValue<int>());
        Assert.Equal("sleep", day["totals"]![0]!["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task Another_day_has_no_now_and_a_future_day_is_all_forecast()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var yesterday = (await app.GetAsync("/api/day/2026-11-16")).Json;
        Assert.False(yesterday["isToday"]!.GetValue<bool>());
        Assert.Null(yesterday["nowMin"]);
        Assert.All(yesterday["pieces"]!.AsArray(), p => Assert.False(p!["future"]!.GetValue<bool>()));

        var tomorrow = (await app.GetAsync("/api/day/2026-11-18")).Json;
        Assert.All(tomorrow["pieces"]!.AsArray(), p => Assert.True(p!["future"]!.GetValue<bool>()));
        Assert.Empty(tomorrow["totals"]!.AsArray());
        Assert.NotNull(PieceTitled(tomorrow, "Сон"));

        var bad = await app.GetAsync("/api/day/17.11.2026");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
    }

    [Fact]
    public async Task Adjacent_stored_segments_with_the_same_content_stay_two_pieces()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Чтение" });
        var second = await app.PostAsync("/api/entries", new { start = app.Local(10, 0), end = app.Local(11, 0), title = "Чтение" });

        var reading = Pieces(second).Where(p => p!["title"]!.GetValue<string>() == "Чтение").ToList();
        Assert.Equal(2, reading.Count);
        Assert.Equal(540, reading[0]!["startMin"]!.GetValue<int>());
        Assert.Equal(600, reading[0]!["endMin"]!.GetValue<int>());
        Assert.Equal(660, reading[1]!["endMin"]!.GetValue<int>());
        Assert.NotEqual(reading[0]!["segment"]!["id"]!.GetValue<string>(), reading[1]!["segment"]!["id"]!.GetValue<string>());
        Assert.Equal("2026-11-17T09:00", reading[0]!["segment"]!["startLocal"]!.GetValue<string>());
        Assert.Equal("2026-11-17T10:00", reading[0]!["segment"]!["endLocal"]!.GetValue<string>());
        Assert.All(reading, p => Assert.Equal("confirmed", p!["status"]!.GetValue<string>()));
        Assert.Equal(120, second.Json["day"]!["status"]!["confirmedMin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Start_continue_and_stop_drive_the_running_activity()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var started = await app.PostAsync("/api/now/start", new { title = "Работа" });
        Assert.Equal(HttpStatusCode.OK, started.Status);
        var current = started.Json["day"]!["current"]!;
        Assert.Equal("Работа", current["title"]!.GetValue<string>());
        Assert.Equal(0, current["elapsedMin"]!.GetValue<int>());
        Assert.Equal("2026-11-17T11:30", current["startLocal"]!.GetValue<string>());

        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(30);
        var running = (await app.GetAsync("/api/day/2026-11-17")).Json;
        var open = running["pieces"]!.AsArray().Single(p => p!["open"]!.GetValue<bool>())!;
        Assert.Equal("extension", open["source"]!.GetValue<string>());
        Assert.Null(open["segment"]!["endLocal"]);
        Assert.Equal(30, running["current"]!["elapsedMin"]!.GetValue<int>());
        Assert.Equal(running["current"]!["segmentId"]!.GetValue<string>(), open["segment"]!["id"]!.GetValue<string>());

        var continued = await app.PostAsync("/api/now/continue");
        Assert.Equal(HttpStatusCode.OK, continued.Status);
        Assert.Equal(30, continued.Json["day"]!["status"]!["confirmedMin"]!.GetValue<int>());
        Assert.Equal(30, continued.Json["day"]!["current"]!["elapsedMin"]!.GetValue<int>());

        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(15);
        var stopped = await app.PostAsync("/api/now/stop");
        Assert.Null(stopped.Json["day"]!["current"]);
        Assert.Equal(45, stopped.Json["day"]!["status"]!["confirmedMin"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync("/api/now/stop")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync("/api/now/continue")).Status);
    }

    [Fact]
    public async Task Start_accepts_an_activity_id_and_a_past_start_but_not_the_future()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/now/start", new { title = "Чтение" });
        var id = (await app.GetAsync("/api/activities")).Json[0]!["id"]!.GetValue<string>();

        var future = await app.PostAsync("/api/now/start", new { activityId = id, at = app.Local(12, 0) });
        Assert.Equal("at", future.Json["field"]!.GetValue<string>());
        var both = await app.PostAsync("/api/now/start", new { activityId = id, title = "X" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.Status);
        var none = await app.PostAsync("/api/now/start", new { });
        Assert.Equal("title", none.Json["field"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync("/api/now/start", new { activityId = Guid.NewGuid() })).Status);

        var past = await app.PostAsync("/api/now/start", new { activityId = id, at = app.Local(11, 0) });
        Assert.Equal(HttpStatusCode.OK, past.Status);
        Assert.Equal("2026-11-17T11:00", past.Json["day"]!["current"]!["startLocal"]!.GetValue<string>());
    }

    [Fact]
    public async Task Entries_can_be_added_edited_and_deleted()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var added = await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Тренировка" });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        var piece = PieceTitled(added.Json["day"]!, "Тренировка")!;
        Assert.Equal("sport", piece["category"]!.GetValue<string>());
        Assert.Equal("user", piece["source"]!.GetValue<string>());
        var id = piece["segment"]!["id"]!.GetValue<string>();

        var shortened = await app.PatchAsync($"/api/entries/{id}", new { end = app.Local(9, 30) });
        var shortenedPiece = PieceTitled(shortened.Json["day"]!, "Тренировка")!;
        Assert.Equal(570, shortenedPiece["endMin"]!.GetValue<int>());

        var renamed = await app.PatchAsync($"/api/entries/{shortenedPiece["segment"]!["id"]!.GetValue<string>()}", new { title = "Работа", category = "work" });
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        var work = PieceTitled(renamed.Json["day"]!, "Работа")!;
        Assert.Equal("work", work["category"]!.GetValue<string>());
        Assert.Null(PieceTitled(renamed.Json["day"]!, "Тренировка"));

        var deleted = await app.DeleteAsync($"/api/entries/{work["segment"]!["id"]!.GetValue<string>()}");
        Assert.Equal(HttpStatusCode.OK, deleted.Status);
        Assert.Null(PieceTitled(deleted.Json["day"]!, "Работа"));
        Assert.Equal(HttpStatusCode.NotFound, (await app.DeleteAsync($"/api/entries/{Guid.NewGuid()}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PatchAsync($"/api/entries/{Guid.NewGuid()}", new { title = "X" })).Status);
    }

    [Fact]
    public async Task Entries_validate_their_input()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        async Task<string?> FieldOf(object body) =>
            (await app.PostAsync("/api/entries", body)).Json["field"]?.GetValue<string>();

        Assert.Equal("end", await FieldOf(new { start = app.Local(9, 0), end = app.Local(12, 0), title = "Чтение" }));
        Assert.Equal("end", await FieldOf(new { start = app.Local(10, 0), end = app.Local(9, 0), title = "Чтение" }));
        Assert.Equal("category", await FieldOf(new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Чтение", category = "nope" }));
        Assert.Equal("start", await FieldOf(new { end = app.Local(10, 0), title = "Чтение" }));
        Assert.Equal("title", await FieldOf(new { start = app.Local(9, 0), end = app.Local(10, 0), title = new string('я', 61) }));
        Assert.Equal("title", await FieldOf(new { start = app.Local(9, 0), end = app.Local(10, 0) }));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync("/api/entries", new { start = "вчера", end = app.Local(10, 0), title = "X" })).Status);
    }

    [Fact]
    public async Task Parallel_entries_do_not_displace_the_main_track()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(11, 0), title = "Работа" });

        var parallel = await app.PostAsync("/api/entries", new { start = app.Local(10, 0), end = app.Local(10, 30), title = "Подкаст", parallel = true });

        var day = parallel.Json["day"]!;
        Assert.Equal(120, PieceTitled(day, "Работа")!["endMin"]!.GetValue<int>() - PieceTitled(day, "Работа")!["startMin"]!.GetValue<int>());
        var side = Assert.Single(day["parallel"]!.AsArray())!;
        Assert.Equal("Подкаст", side["title"]!.GetValue<string>());
        Assert.Equal(30, day["parallelMin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Confirm_turns_assumed_pieces_into_confirmed_ones()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var future = await app.PostAsync("/api/confirm", new { start = app.Local(0, 0), end = app.Local(23, 0) });
        Assert.Equal("end", future.Json["field"]!.GetValue<string>());

        var confirmed = await app.PostAsync("/api/confirm", new { start = app.Local(0, 0), end = app.Local(8, 30) });
        Assert.Equal(HttpStatusCode.OK, confirmed.Status);
        var day = confirmed.Json["day"]!;
        Assert.Equal(480, day["status"]!["confirmedMin"]!.GetValue<int>());
        Assert.Equal(0, day["status"]!["assumedMin"]!.GetValue<int>());
        var sleep = PieceTitled(day, "Сон")!;
        Assert.Equal("confirmed", sleep["status"]!.GetValue<string>());
        Assert.NotNull(sleep["segment"]);

        // 🟡-фон подтверждается только по просьбе.
        var background = PieceTitled(day, "Не записано")!;
        Assert.Equal("questionable", background["status"]!.GetValue<string>());
        Assert.Equal("background", background["source"]!.GetValue<string>());
        Assert.Null(background["category"]);
        var more = await app.PostAsync("/api/confirm", new { start = app.Local(0, 0), end = app.Local(0, 30), includeQuestionable = true });
        Assert.Equal(510, more.Json["day"]!["status"]!["confirmedMin"]!.GetValue<int>());

        // Неизвестное время, подтверждённое «как есть», становится тем, что предлагалось первым, — залипанием (#47).
        var wasted = PieceTitled(more.Json["day"]!, "Залипание")!;
        Assert.Equal("wasted", wasted["category"]!.GetValue<string>());
        Assert.Equal("confirmed", wasted["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_time_is_counted_apart_from_the_categories()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        var day = (await app.GetAsync($"/api/day/{app.Local(0, 0)[..10]}")).Json;

        // Фон — не «Залипание» и не «Без категории»: отдельное поле unknownMin, в totals не входит (#47).
        var unknown = day["pieces"]!.AsArray()
            .Where(p => p!["source"]!.GetValue<string>() == "background" && !p["future"]!.GetValue<bool>())
            .Sum(p => Math.Min(p!["endMin"]!.GetValue<int>(), day["nowMin"]!.GetValue<int>()) - p["startMin"]!.GetValue<int>());
        Assert.True(unknown > 0);
        Assert.Equal(unknown, day["unknownMin"]!.GetValue<int>());
        var totals = day["totals"]!.AsArray();
        Assert.DoesNotContain(totals, t => t!["category"]?.GetValue<string>() == "wasted");
        Assert.DoesNotContain(totals, t => t!["category"] is null);
        Assert.Equal(day["nowMin"]!.GetValue<int>(), totals.Sum(t => t!["minutes"]!.GetValue<int>()) + unknown);

        var summary = (await app.GetAsync("/api/summary")).Json;
        var days = summary["days"]!.AsArray();
        Assert.Equal(unknown, days[^1]!["unknownMin"]!.GetValue<int>());
        Assert.Equal(days.Sum(d => d!["unknownMin"]!.GetValue<int>()), summary["unknownMin"]!.GetValue<int>());
        Assert.DoesNotContain(summary["totals"]!.AsArray(), t => t!["category"]?.GetValue<string>() == "wasted");
    }

    [Fact]
    public async Task Confirm_continues_a_running_activity_that_is_still_assumed()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PatchAsync("/api/settings", new { normFactor = (double?)null, maxFactor = (double?)null });
        var id = (await app.PostAsync("/api/now/start", new { title = "Работа" })).Json["day"]!["current"]!["activityId"]!.GetValue<string>();
        await app.PatchAsync($"/api/activities/{id}", new { typicalMin = 600 });

        app.Clock.UtcNow = app.Clock.UtcNow.AddMinutes(45);
        var confirmed = await app.PostAsync("/api/confirm", new { start = app.Local(11, 30), end = app.Local(12, 15) });

        var day = confirmed.Json["day"]!;
        Assert.Equal(45, day["status"]!["confirmedMin"]!.GetValue<int>());
        Assert.Equal("Работа", day["current"]!["title"]!.GetValue<string>());
        Assert.Equal(45, day["current"]!["elapsedMin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Undo_reverts_the_last_change()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Чтение" });

        var undone = await app.PostAsync("/api/undo");
        Assert.Equal("undone", undone.Json["result"]!.GetValue<string>());
        Assert.Null(PieceTitled(undone.Json["day"]!, "Чтение"));

        var nothing = await app.PostAsync("/api/undo");
        Assert.Equal("nothing", nothing.Json["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task Moving_one_edge_of_an_entry_displaces_the_neighbour_and_undo_returns_both()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Тренировка" });
        var added = await app.PostAsync("/api/entries", new { start = app.Local(10, 0), end = app.Local(11, 0), title = "Чтение" });
        var id = PieceTitled(added.Json["day"]!, "Чтение")!["segment"]!["id"]!.GetValue<string>();

        // What dragging a boundary on the timeline sends: the moved edge alone.
        var moved = await app.PatchAsync($"/api/entries/{id}", new { start = app.Local(9, 40) });
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        var reading = PieceTitled(moved.Json["day"]!, "Чтение")!;
        Assert.Equal(580, reading["startMin"]!.GetValue<int>());
        Assert.Equal(660, reading["endMin"]!.GetValue<int>());
        Assert.Equal(580, PieceTitled(moved.Json["day"]!, "Тренировка")!["endMin"]!.GetValue<int>());

        var undone = await app.PostAsync("/api/undo");
        Assert.Equal("undone", undone.Json["result"]!.GetValue<string>());
        Assert.Equal(600, PieceTitled(undone.Json["day"]!, "Чтение")!["startMin"]!.GetValue<int>());
        Assert.Equal(600, PieceTitled(undone.Json["day"]!, "Тренировка")!["endMin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Quiet_sets_and_clears_quiet_hours()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var quiet = await app.PostAsync("/api/quiet", new { minutes = 120 });
        Assert.Equal("2026-11-17T10:30:00Z", quiet.Json["day"]!["attention"]!["quietUntilUtc"]!.GetValue<string>());
        Assert.Equal("2026-11-17T10:30:00Z", (await app.GetAsync("/api/me")).Json["quietUntilUtc"]!.GetValue<string>());

        var off = await app.PostAsync("/api/quiet", new { minutes = 0 });
        Assert.Null(off.Json["day"]!["attention"]!["quietUntilUtc"]);

        var tooShort = await app.PostAsync("/api/quiet", new { minutes = 3 });
        Assert.Equal("minutes", tooShort.Json["field"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.PostAsync("/api/quiet", new { minutes = 1441 })).Status);
    }

    [Fact]
    public async Task Changes_show_the_day_from_the_view_parameter()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var response = await app.PostAsync("/api/quiet?view=2026-11-16", new { minutes = 0 });

        Assert.Equal("2026-11-16", response.Json["day"]!["date"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.PostAsync("/api/quiet?view=wrong", new { minutes = 0 })).Status);
    }

    [Fact]
    public async Task Mutations_count_as_an_answer_but_reads_do_not()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        var before = (await app.GetAsync("/api/day/2026-11-17")).Json["attention"]!["pingPending"]!.GetValue<bool>();
        Assert.False(before);

        await app.GetAsync("/api/day/2026-11-17");
        await app.GetAsync("/api/me");
        await app.GetAsync("/api/summary");
        await app.PostAsync("/api/quiet", new { minutes = 0 });

        // Без пингов проверять нечего, но запросы не должны падать и не должны менять ленту.
        Assert.Empty((await app.GetAsync("/api/activities")).Json.AsArray());
    }

    [Fact]
    public async Task Summary_totals_the_past_per_day_and_for_the_range()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();
        await app.PostAsync("/api/entries", new { start = app.Local(9, 0), end = app.Local(10, 0), title = "Тренировка" });

        var summary = await app.GetAsync("/api/summary?from=2026-11-16&to=2026-11-17");

        Assert.Equal("2026-11-16", summary.Json["from"]!.GetValue<string>());
        var days = summary.Json["days"]!.AsArray();
        Assert.Equal(2, days.Count);
        var today = days[1]!;
        Assert.Equal("2026-11-17", today["date"]!.GetValue<string>());
        Assert.Equal(60, today["totals"]!.AsArray().Single(t => t!["category"]?.GetValue<string>() == "sport")!["minutes"]!.GetValue<int>());
        Assert.Equal(60, today["status"]!["confirmedMin"]!.GetValue<int>());
        Assert.Equal(1440, days[0]!["status"]!["assumedMin"]!.GetValue<int>() + days[0]!["status"]!["questionableMin"]!.GetValue<int>());
        var total = summary.Json["totals"]!.AsArray();
        Assert.Equal(960, total.Single(t => t!["category"]?.GetValue<string>() == "sleep")!["minutes"]!.GetValue<int>());
        Assert.True(total[0]!["minutes"]!.GetValue<int>() >= total[1]!["minutes"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.GetAsync("/api/summary?from=2026-10-01&to=2026-11-17")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.GetAsync("/api/summary?from=2026-11-17&to=2026-11-16")).Status);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/summary")).Status);
    }

    [Fact]
    public async Task Messages_run_the_same_dialog_as_the_bot()
    {
        await using var app = new TestApp();

        var early = await app.PostAsync("/api/messages", new { text = "Работаю" });
        Assert.Equal(HttpStatusCode.Conflict, early.Status);

        await app.OnboardAsync();
        var started = await app.PostAsync("/api/messages", new { text = "Читаю" });
        Assert.Equal(HttpStatusCode.OK, started.Status);
        Assert.NotEmpty(started.Json["replies"]!.AsArray());
        Assert.False(started.Json["replies"]![0]!["hasImage"]!.GetValue<bool>());
        Assert.Equal("Читаю", started.Json["day"]!["current"]!["title"]!.GetValue<string>());

        var help = await app.PostAsync("/api/messages", new { text = "/help" });
        Assert.Contains("/day", help.Json["replies"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var stopped = await app.PostAsync("/api/messages", new { button = "act:stop" });
        Assert.Equal(HttpStatusCode.OK, stopped.Status);
        Assert.Null(stopped.Json["day"]!["current"]);

        Assert.Equal("text", (await app.PostAsync("/api/messages", new { })).Json["field"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.PostAsync("/api/messages", new { text = "x", button = "y" })).Status);
    }
}
