using TimeShift.Application.Conversations;
using TimeShift.Application.Pings;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>Учёт после онбординга: запись занятий кнопками и текстом, /day /quiet /undo, пинги.</summary>
public class TrackingTests
{
    private const long ChatId = 7;

    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new(); // 09:00 UTC = 12:00 Москва

    private TrackingService Tracking => new(_store, _store);

    private ConversationService Conversation => new(_store, _store, Tracking, _clock);

    private PingService Pings => new(_store, Tracking, _store, _store, _clock);

    private User Me => Assert.Single(_store.Users);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<IReadOnlyList<BotReply>> Text(string text) =>
        Conversation.HandleAsync(IncomingMessage.FromText(ChatId, "Тест", text), Ct);

    private Task<IReadOnlyList<BotReply>> Press(string data) =>
        Conversation.HandleAsync(IncomingMessage.FromButton(ChatId, "Тест", data), Ct);

    private static List<string> Data(IEnumerable<BotReply> replies) =>
        replies.SelectMany(r => r.Buttons).SelectMany(row => row).Select(b => b.Data).ToList();

    private List<string> Lines() =>
        _store.SegmentsOf(Me.Id)
            .Select(s => $"{s.StartUtc:HH:mm}-{(s.EndUtc is { } e ? e.ToString("HH:mm") : "…")} {s.Title}")
            .ToList();

    private void Advance(int minutes) => _clock.Advance(TimeSpan.FromMinutes(minutes));

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

    [Fact]
    public async Task Text_starts_an_activity_and_buttons_continue_and_stop_it()
    {
        await OnboardAsync();

        var started = await Text("Тренировка");
        Assert.Contains(started, r => r.Text.Contains("«Тренировка» с 12:00"));
        Assert.Contains("act:stop", Data(started));
        Assert.Equal(["09:00-… Тренировка"], Lines());

        Advance(30);
        await Press("act:cont");
        Assert.Equal(["09:00-09:30 Тренировка", "09:30-… Тренировка"], Lines());
        Assert.Equal(TimeSpan.FromMinutes(30), _store.SegmentsOf(Me.Id)[^1].ElapsedBefore);

        Advance(45);
        var stopped = await Press("act:stop");
        Assert.Contains(stopped, r => r.Text.Contains("12:30–13:15"));
        Assert.Contains(stopped, r => r.Text == "Чем занят теперь?");
        Assert.Equal(["09:00-09:30 Тренировка", "09:30-10:15 Тренировка"], Lines());
    }

    [Fact]
    public async Task Recent_activities_are_offered_and_names_are_matched_case_insensitively()
    {
        await OnboardAsync();
        await Text("Работа");
        Advance(60);
        await Text("Обед");
        Advance(30);

        var now = await Text("/now");
        var work = Assert.Single(_store.Activities, a => a.Name == "Работа");
        Assert.Contains($"act:start:{work.Id}", Data(now));
        Assert.Contains("act:cont", Data(now));

        await Press($"act:start:{work.Id}");
        Advance(10);
        await Text("работа");

        Assert.Equal(2, _store.Activities.Count);
        Assert.Equal(["09:00-10:00 Работа", "10:00-10:30 Обед", "10:30-10:40 Работа", "10:40-… Работа"], Lines());
    }

    [Fact]
    public async Task Other_asks_for_a_name_and_undo_reverts_the_last_record()
    {
        await OnboardAsync();
        await Text("Работа");
        Advance(20);

        await Press("act:other");
        await Text("Звонок маме");
        Assert.Equal(["09:00-09:20 Работа", "09:20-… Звонок маме"], Lines());

        var undo = await Text("/undo");
        Assert.Contains(undo, r => r.Text.StartsWith("↩️", StringComparison.Ordinal));
        Assert.Equal(["09:00-… Работа"], Lines());

        await Text("/undo");
        Assert.Empty(Lines());
        var nothing = await Text("/undo");
        Assert.Contains(nothing, r => r.Text == "Отменять нечего.");
    }

    [Fact]
    public async Task Day_shows_confirmed_extension_and_background()
    {
        await OnboardAsync();
        _clock.UtcNow = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc); // 13:00 Москва
        await Text("Работа");
        Advance(30);
        await Press("act:stop");
        Advance(60);

        var day = Assert.Single(await Text("/day")).Text;

        Assert.Contains("00:00–01:00 🟡 Залипание", day);
        Assert.Contains("01:00–09:00 ⚪ Сон", day);
        Assert.Contains("13:00–13:30 ✅ Работа (30 мин)", day);
        Assert.Contains("13:30–14:30 🟡 Залипание (1 ч)", day);
    }

    [Fact]
    public async Task Long_free_text_is_not_taken_as_an_activity_name()
    {
        await OnboardAsync();

        var replies = await Text("Тренировался час, потом поехал домой, дома смотрел ютуб и потом немного поспал");

        Assert.Contains(replies, r => r.Text.Contains("короткое название"));
        Assert.Empty(Lines());
    }

    [Fact]
    public async Task Pings_follow_the_interval_delete_the_unanswered_one_and_pause()
    {
        await OnboardAsync();

        Advance(29);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);

        Advance(1);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
        Assert.Equal("Чем занят?", _store.Sent[^1].Reply.Text);

        Advance(30);
        await Pings.RunAsync(Ct);
        Assert.Equal(["1"], _store.Deleted);
        Assert.Equal(2, Me.UnansweredPings);

        // Два без ответа — пауза.
        Advance(120);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);

        // Ответ снимает паузу; следующий пинг — через интервал после ответа.
        await Text("Читаю");
        Advance(30);
        await Pings.RunAsync(Ct);
        Assert.Equal(3, _store.Sent.Count);
        Assert.Equal(1, Me.UnansweredPings);
        Assert.StartsWith("Всё ещё «Читаю»?", _store.Sent[^1].Reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task During_an_activity_the_first_ping_waits_for_its_typical_duration()
    {
        await OnboardAsync();
        await Text("Тренировка");
        _store.Activities.Single().TypicalDuration = TimeSpan.FromMinutes(90);

        Advance(60);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);

        Advance(30);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
        Assert.Contains("act:cont", Data([_store.Sent[^1].Reply]));
    }

    [Fact]
    public async Task No_pings_during_sleep_and_quiet_hours()
    {
        await OnboardAsync();

        // 01:30 Москва — сон 01:00–09:00.
        _clock.UtcNow = new DateTime(2026, 11, 14, 22, 30, 0, DateTimeKind.Utc);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);
        Assert.Equal(new DateTime(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc), PingService.NextDue(Me, null, _clock.UtcNow));

        // Утром — тихие часы на 2 часа.
        _clock.UtcNow = new DateTime(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc);
        await Text("/quiet 2");
        Assert.Equal(new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc), Me.QuietUntilUtc);
        Advance(60);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);
        Advance(60);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);

        await Press("quiet:off");
        Assert.Null(Me.QuietUntilUtc);
    }
}
