using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Application.Pings;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Pings;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>Учёт после онбординга: запись занятий кнопками и текстом, /day /quiet /undo, пинги.</summary>
public class TrackingTests
{
    private const long ChatId = 7;

    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new(); // 09:00 UTC = 12:00 Москва

    private TrackingService Tracking => new(_store, _store, _store);

    private ConversationService Conversation => new(_store, _store, Tracking, _store.Applier(Tracking, new ScriptedLlm()), _store.Speech, _store.Questions(Tracking), new YellowReview(Tracking), new DailyReview(Tracking, new YellowReview(Tracking)), _store.Renderer, _clock);

    private PingService Pings => new(_store, Tracking, _store.Applier(Tracking, new ScriptedLlm()), new DailyReview(Tracking, new YellowReview(Tracking)), _store, _store, _clock);

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

        Assert.Contains("00:00–01:00 🟡 Не записано", day);
        Assert.Contains("01:00–09:00 ⚪ Сон", day);
        Assert.Contains("13:00–13:30 ✅ Работа (30 мин)", day);
        Assert.Contains("13:30–14:30 🟡 Не записано (1 ч)", day);
    }

    [Fact]
    public async Task Unanswered_pings_back_off_by_default_and_an_answer_restores_the_interval()
    {
        await OnboardAsync();
        Assert.Equal(UnansweredPingsPolicy.BackOff, Me.Settings.AfterUnansweredPings);

        Advance(30);
        await Pings.RunAsync(Ct);
        Advance(30);
        await Pings.RunAsync(Ct);
        Assert.Equal(2, Me.UnansweredPings);

        // Два без ответа — дальше реже: через 60, потом через 120 минут.
        Advance(59);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);
        Advance(1);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
        Advance(119);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);
        Advance(1);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
        Assert.Equal(4, Me.UnansweredPings);

        await Text("Читаю");
        Advance(30);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
        Assert.Equal(1, Me.UnansweredPings);
    }

    [Fact]
    public async Task Switched_off_pings_do_not_come()
    {
        await OnboardAsync();
        Me.Settings.PingsEnabled = false;

        Advance(90);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);
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
        Me.Settings.AfterUnansweredPings = UnansweredPingsPolicy.Stop;

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

    [Fact]
    public async Task Day_for_yesterday_shows_the_whole_day_and_navigates()
    {
        _clock.UtcNow = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc); // пятница 12:00 Москва
        await OnboardAsync();
        await Text("Работа");
        Advance(60);
        await Press("act:stop");

        _clock.UtcNow = new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc); // суббота 10:00
        var day = Assert.Single(await Text("/day вчера"));

        Assert.StartsWith("Вчера, 13 ноября", day.Text, StringComparison.Ordinal);
        Assert.NotNull(day.Image);
        Assert.Contains("Итого: Сон 8 ч · Работа 1 ч\nНе записано: 15 ч", day.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Залипание", day.Text, StringComparison.Ordinal); // неизвестное время — не залипание (#47)
        var drawn = _store.Renderer.Days[^1];
        Assert.Equal("Вчера, 13 ноября", drawn.Title);
        Assert.Equal(TimeSpan.FromHours(24), drawn.DayEndUtc - drawn.DayStartUtc);
        Assert.Contains("12:00–13:00 ✅ Работа (1 ч)", day.Text);
        Assert.Contains("13:00–00:00 🟡 Не записано", day.Text);
        Assert.Equal(["day:2026-11-12", "day:2026-11-14", "dayadd:2026-11-13"], Data([day]));

        var today = Assert.Single(await Press("day:2026-11-14"));
        Assert.True(today.ReplacesSource);
        Assert.StartsWith("Сегодня, 14 ноября", today.Text, StringComparison.Ordinal);
        Assert.Equal(["day:2026-11-13", "dayadd:2026-11-14"], Data([today]));

        Assert.Contains(await Text("/day 20.11"), r => r.Text.Contains("ещё не наступило", StringComparison.Ordinal));
        Assert.Contains(await Text("/day когда-то"), r => r.Text.Contains("Не понял, какой день", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Week_sums_match_its_days_and_skip_days_before_registration()
    {
        _clock.UtcNow = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc); // пятница 12:00 Москва
        await OnboardAsync();
        await Text("Работа");
        Advance(60);
        await Press("act:stop");

        _clock.UtcNow = new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc); // суббота 10:00
        var week = Assert.Single(await Text("/week"));

        // Пятница целиком (как в /day: Сон 8 ч · Работа 1 ч, не записано 15 ч) + суббота до 10:00 (сон 8 ч, фон 2 ч).
        Assert.StartsWith("Эта неделя, 9–15 ноября:", week.Text, StringComparison.Ordinal);
        Assert.Contains("Сон 16 ч\nРабота 1 ч\nНе записано 17 ч\nВсего: 34 ч", week.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("✅ 3% · ⚪ 47% · 🟡 50%", week.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("разница", week.Text, StringComparison.Ordinal);
        Assert.NotNull(week.Image);
        Assert.Empty(Data([week])); // раньше — только дни до регистрации, позже — будущее

        var drawn = _store.Renderer.Weeks[^1];
        Assert.Equal("Эта неделя, 9–15 ноября", drawn.Title);
        Assert.Equal([0, 0, 0, 0, 5, 3, 0], drawn.Days.Select(d => d.Pieces.Count).ToList());
        Assert.Equal(TimeSpan.FromHours(34), drawn.Stats.Total);
        Assert.Equal(drawn.Stats.Total, TimeSpan.FromTicks(drawn.Stats.Categories.Sum(c => c.Total.Ticks)));
        Assert.Equal(drawn.Stats.Total, drawn.Stats.Confirmed + drawn.Stats.Assumed + drawn.Stats.Questionable);
    }

    [Fact]
    public async Task Week_navigates_and_compares_with_the_same_days_of_the_previous_week()
    {
        _clock.UtcNow = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc); // пятница 12:00 Москва
        await OnboardAsync();

        _clock.UtcNow = new DateTime(2026, 11, 18, 7, 0, 0, DateTimeKind.Utc); // среда 10:00
        var first = Assert.Single(await Text("/week"));
        Assert.Contains("Всего: 58 ч", first.Text, StringComparison.Ordinal); // пн, вт и 10 часов среды
        Assert.DoesNotContain("разница", first.Text, StringComparison.Ordinal); // прошлая неделя — не вся после регистрации
        Assert.Equal(["week:2026-11-09"], Data([first]));

        var earlier = Assert.Single(await Press("week:2026-11-09"));
        Assert.True(earlier.ReplacesSource);
        Assert.StartsWith("Прошлая неделя, 9–15 ноября:", earlier.Text, StringComparison.Ordinal);
        Assert.Contains("Всего: 72 ч", earlier.Text, StringComparison.Ordinal); // пт, сб, вс
        Assert.Equal(["week:2026-11-16"], Data([earlier]));

        _clock.UtcNow = new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Utc); // среда через неделю, 10:00
        await Text("/add вчера 14-16 Работа");
        var compared = (await Text("/week"))[0];

        Assert.StartsWith("Эта неделя, 23–29 ноября:", compared.Text, StringComparison.Ordinal);
        Assert.Contains("Сон 24 ч (=)\nРабота 2 ч (+2 ч)\nНе записано 32 ч (−2 ч)\nВсего: 58 ч", compared.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("разница с теми же днями прошлой недели", compared.Text, StringComparison.Ordinal);
        Assert.Equal(["week:2026-11-16"], Data([compared]));

        // Будущая неделя с кнопки — это текущая.
        var future = Assert.Single(await Press("week:2026-12-07"));
        Assert.StartsWith("Эта неделя, 23–29 ноября:", future.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Day_with_a_clock_change_has_23_hours()
    {
        _clock.UtcNow = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();
        await Press("edit:tz");
        await Text("Europe/Berlin");

        var day = Assert.Single(await Text("/day 29.03")).Text;

        Assert.StartsWith("Вчера, 29 марта", day, StringComparison.Ordinal);
        Assert.Contains("01:00–09:00 ⚪ Сон (7 ч)", day);
        Assert.Contains("09:00–00:00 🟡 Не записано (15 ч)", day);
    }

    [Fact]
    public async Task Add_records_past_periods_line_by_line()
    {
        _clock.UtcNow = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();
        _clock.UtcNow = new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc); // суббота 06:00 Москва
        await Text("Читаю");

        var replies = await Text("/add вчера 19-21 Зал");
        Assert.Contains(replies, r => r.Text.Contains("13 ноября, 19:00–21:00 Зал", StringComparison.Ordinal));

        // Без аргумента — ждёт строки; без дня — последнее прошедшее; через полночь; ошибки по строкам.
        Assert.Contains(await Text("/add"), r => r.Text.StartsWith("Что и когда было?", StringComparison.Ordinal));
        var batch = await Text("""
            22:30-0:30 Сериал
            сегодня 7-8 Пробежка
            с 11 до 2 Вечеринка
            вс 10-12 Ничего
            13.11 21:30-23 Пересечение
            просто текст
            """);
        var ok = Assert.Single(batch, r => r.Text.StartsWith("✅", StringComparison.Ordinal)).Text;
        Assert.Contains("13 ноября, 22:30–00:30 Сериал", ok, StringComparison.Ordinal);
        var problems = Assert.Single(batch, r => r.Text.Contains("Поправь", StringComparison.Ordinal)).Text;
        Assert.Contains("7-8 Пробежка» — это ещё не наступило", problems, StringComparison.Ordinal);
        Assert.Contains("больше 12 часов", problems, StringComparison.Ordinal);
        Assert.Contains("не нашёл время", problems, StringComparison.Ordinal);
        Assert.Contains("8 ноября, 10:00–12:00 Ничего", ok, StringComparison.Ordinal);
        Assert.Contains("Пересечение» — пересекается с другой строкой", problems, StringComparison.Ordinal);
        Assert.Null(Me.DialogState);

        // Начатое занятие продолжается; категория угадана; «Пересечение» пересеклось с «Зал»? нет — с сериалом.
        Assert.Contains(Lines(), l => l.EndsWith("Читаю", StringComparison.Ordinal) && l.Contains("-…", StringComparison.Ordinal));
        Assert.Equal("sport", _store.Activities.Single(a => a.Name == "Зал").CategoryKey);

        await Text("/undo");
        Assert.DoesNotContain(Lines(), l => l.EndsWith("Сериал", StringComparison.Ordinal));
        Assert.Contains(Lines(), l => l.EndsWith("Зал", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_button_from_a_day_uses_that_day()
    {
        _clock.UtcNow = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();
        _clock.UtcNow = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

        Assert.Contains(await Press("dayadd:2026-11-11"), r => r.Text.StartsWith("Что было 11 ноября?", StringComparison.Ordinal));
        var replies = await Text("19-21 Английский");

        Assert.Contains(replies, r => r.Text.Contains("11 ноября, 19:00–21:00 Английский", StringComparison.Ordinal));
        Assert.Contains("day:2026-11-11", Data(replies));
    }

    [Fact]
    public async Task Voice_is_transcribed_and_handled_like_text()
    {
        await OnboardAsync();
        _store.Speech.Text = "Тренировка";

        var replies = await Conversation.HandleAsync(IncomingMessage.FromVoice(ChatId, "Тест", [1, 2, 3], TimeSpan.FromSeconds(3)), Ct);

        Assert.Equal("🎙 «Тренировка»", replies[0].Text);
        Assert.Contains(replies, r => r.Text.Contains("Записал: «Тренировка»", StringComparison.Ordinal));
        Assert.Equal(["09:00-… Тренировка"], Lines());
    }

    [Fact]
    public async Task Voice_from_a_client_that_knows_the_user_goes_the_same_way()
    {
        await OnboardAsync();
        var user = (await _store.FindByTelegramChatIdAsync(ChatId, Ct))!;
        _store.Speech.Text = "Тренировка";

        var replies = await Conversation.HandleAsync(user, IncomingMessage.FromVoice(0, "Тест", [1, 2, 3], TimeSpan.FromSeconds(3)), Ct);

        Assert.Equal("🎙 «Тренировка»", replies[0].Text);
        Assert.Equal(["09:00-… Тренировка"], Lines());

        _store.Speech.Text = string.Empty;
        var silence = await Conversation.HandleAsync(user, IncomingMessage.FromVoice(0, "Тест", [1], TimeSpan.FromSeconds(3)), Ct);
        Assert.Contains("Не расслышал", Assert.Single(silence).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voice_problems_are_explained_without_touching_the_timeline()
    {
        await OnboardAsync();
        async Task<string> Voice(int seconds) =>
            Assert.Single(await Conversation.HandleAsync(IncomingMessage.FromVoice(ChatId, "Тест", [1], TimeSpan.FromSeconds(seconds)), Ct)).Text;

        _store.Speech.Text = "   ";
        Assert.Contains("Не расслышал", await Voice(2), StringComparison.Ordinal);

        Assert.Contains("длиннее 2 мин", await Voice(150), StringComparison.Ordinal);

        _store.Speech.Failure = new SpeechException("boom");
        Assert.Contains("Не получилось распознать", await Voice(2), StringComparison.Ordinal);

        _store.Speech.Enabled = false;
        Assert.Contains("не включены", await Voice(2), StringComparison.Ordinal);

        Assert.Empty(Lines());
        Assert.Equal(2, _store.Speech.Calls);
    }

    private BotReply Question(IEnumerable<BotReply> replies) =>
        Assert.Single(replies, r => r.Text.StartsWith("❓", StringComparison.Ordinal));

    [Fact]
    public async Task Unknown_duration_is_asked_once_right_after_the_reply()
    {
        await OnboardAsync();

        var started = await Text("Тренировка");
        Assert.Contains("«Тренировка»", Question(started).Text, StringComparison.Ordinal);

        // Не ответ на вопрос — обычное сообщение; вопрос не повторяется.
        var stopped = await Press("act:stop");
        Assert.DoesNotContain(stopped, r => r.Text.StartsWith("❓", StringComparison.Ordinal));

        var answer = await Text("около часа");
        Assert.Contains(answer, r => r.Text.StartsWith("Запомнил: «Тренировка» обычно 1 ч", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromHours(1), _store.Activities.Single().TypicalDuration);

        Advance(60);
        Assert.DoesNotContain(await Text("Тренировка"), r => r.Text.StartsWith("❓", StringComparison.Ordinal));

        // Первый пинг — по обычной длительности, а не через 30 минут.
        Advance(30);
        Assert.Equal(0, (await Pings.RunAsync(Ct)).Sent);
        Advance(30);
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
    }

    [Fact]
    public async Task One_question_at_a_time_buttons_and_skip()
    {
        await OnboardAsync();
        var first = Question(await Text("Работа"));
        Assert.Contains("Не знаю, по-разному", first.Buttons.SelectMany(r => r).Select(b => b.Label));

        // Второе занятие без ответа на первый вопрос — новый вопрос не задаётся.
        Advance(10);
        Assert.DoesNotContain(await Text("Обед"), r => r.Text.StartsWith("❓", StringComparison.Ordinal));

        // Ответ кнопкой — и сразу следующий вопрос из очереди.
        var data = first.Buttons[0].Select(b => b.Data).Single(d => d.EndsWith(":90", StringComparison.Ordinal));
        var replies = await Press(data);
        Assert.True(replies[0].ReplacesSource);
        Assert.Equal(TimeSpan.FromMinutes(90), _store.Activities.Single(a => a.Name == "Работа").TypicalDuration);
        var second = Question(replies);
        Assert.Contains("«Обед»", second.Text, StringComparison.Ordinal);

        var skip = second.Buttons[1][0].Data;
        Assert.Contains(await Press(skip), r => r.Text.Contains("не буду спрашивать", StringComparison.Ordinal));
        Assert.Null(_store.Activities.Single(a => a.Name == "Обед").TypicalDuration);
        Assert.Contains(await Press(skip), r => r.Text.Contains("уже закрыт", StringComparison.Ordinal));

        // «закончил в 19» — не ответ «19 минут».
        Assert.Null(await new Questions.QuestionService(_store, Tracking, _store, _store).AnswerAsync(Me, "закончил в 19", _clock.UtcNow, Ct));
    }

    [Fact]
    public async Task Facts_lists_what_the_bot_knows_and_allows_changing_durations()
    {
        await OnboardAsync();
        await Text("Тренировка");
        await Text("90");
        await _store.SetAsync(Me.Id, "route:зал→дом", 25, Ct);

        var facts = Assert.Single(await Text("/facts"));

        Assert.Contains("• Тренировка — 1 ч 30 мин, Спорт", facts.Text, StringComparison.Ordinal);
        Assert.Contains("• Дорога зал → дом — 25 мин", facts.Text, StringComparison.Ordinal);
        var change = Data([facts]).Single(d => d.StartsWith("fdur:", StringComparison.Ordinal));

        Question(await Press(change));
        await Text("2 часа");
        Assert.Equal(TimeSpan.FromHours(2), _store.Activities.Single().TypicalDuration);
    }

    [Fact]
    public async Task YouTube_games_and_unknown_activities_are_asked_for_a_category_first()
    {
        await OnboardAsync();

        var youtube = Question(await Text("Ютуб"));
        Assert.Contains("YouTube бывает разным", youtube.Text, StringComparison.Ordinal);
        Assert.Equal(["Отдых", "Учёба", "Залипание"], youtube.Buttons[0].Select(b => b.Label));

        // Ответ — и следом вопрос о длительности того же занятия.
        var answered = await Press(youtube.Buttons[0][1].Data);
        Assert.Contains(answered, r => r.Text == "Запомнил: «Ютуб» — Учёба. Текущая запись теперь — Учёба (отменить — /undo).");
        Assert.Contains("Сколько обычно длится «Ютуб»", Question(answered).Text, StringComparison.Ordinal);
        await Text("полчаса");

        Advance(30);
        var game = Question(await Text("Играю в Elden Ring"));
        Assert.Equal(["Синглплеер — отдых", "Мультиплеер — залипание"], game.Buttons[0].Select(b => b.Label));
        await Press(game.Buttons[0][0].Data);
        Assert.Equal("rest", _store.Activities.Single(a => a.Name == "Играю в Elden Ring").CategoryKey);
        await Text("2 часа");

        Advance(30);
        var guitar = Question(await Text("Гитара"));
        Assert.Contains("К чему отнести?", guitar.Text, StringComparison.Ordinal);
        Assert.Contains(await Text("отдых"), r => r.Text == "Запомнил: «Гитара» — Осознанный отдых. Текущая запись теперь — Осознанный отдых (отменить — /undo).");

        // Очевидное не спрашивается.
        Advance(30);
        Assert.DoesNotContain(await Text("Сериал"), r => r.Text.Contains("К чему", StringComparison.Ordinal));
        Assert.Equal("rest", _store.Activities.Single(a => a.Name == "Сериал").CategoryKey);
    }

    private BotReply Yellow(IEnumerable<BotReply> replies) =>
        Assert.Single(replies, r => r.Text.StartsWith("🟡 Пока тебя не было", StringComparison.Ordinal));

    /// <summary>Работа 12:00–12:30, потом два часа тишины, потом «Читаю» в 14:30 (Москва).</summary>
    private async Task<IReadOnlyList<BotReply>> GapAsync()
    {
        await OnboardAsync();
        await Text("Работа");
        Advance(30);
        await Press("act:stop");
        Advance(120);
        return await Text("Читаю");
    }

    [Fact]
    public async Task Gap_is_asked_right_after_the_user_writes_and_filled_by_a_button()
    {
        var question = Yellow(await GapAsync());
        Assert.Contains("12:30–14:30 (2 ч)", question.Text, StringComparison.Ordinal);
        var labels = question.Buttons.SelectMany(r => r).Select(b => b.Label).ToList();
        Assert.Contains("Работа", labels);
        Assert.Equal("Залипание", labels[0]); // первым бот предлагает залипание (#47)
        Assert.Contains("✏️ Опишу", labels);

        var work = question.Buttons.SelectMany(r => r).Single(b => b.Label == "Работа").Data;
        var filled = await Press(work);

        Assert.Contains(filled, r => r.Text == "✅ 12:30–14:30 — Работа." && r.ReplacesSource);
        Assert.Contains("09:30-11:30 Работа", Lines());

        // Участок разобран — больше не спрашивается.
        Advance(5);
        Assert.DoesNotContain(await Text("Читаю дальше"), r => r.Text.StartsWith("🟡", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gap_can_be_confirmed_as_wasted_described_or_skipped()
    {
        var question = Yellow(await GapAsync());
        var buttons = question.Buttons.SelectMany(r => r).ToDictionary(b => b.Label, b => b.Data);

        await Press(buttons["Залипание"]);
        Assert.Equal("wasted", _store.Activities.Single(a => a.Name == "Залипание").CategoryKey);
        await Text("/undo");

        // «Опишу» — строки как в /add на тот день.
        var describe = await Press(buttons["✏️ Опишу"]);
        Assert.Contains(describe, r => r.Text.Contains("Напиши, что было в 12:30–14:30", StringComparison.Ordinal));
        await Text("12:30-13:30 Магазин");
        Assert.Contains("09:30-10:30 Магазин", Lines());

        Assert.Contains(await Press(buttons["Пропустить"]), r => r.Text.Contains("оставляю под вопросом", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Time_before_onboarding_and_short_gaps_are_not_asked()
    {
        _clock.UtcNow = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();

        // Утро до онбординга — 🟡 по фону, но о нём не спрашиваем.
        Assert.DoesNotContain(await Text("Работа"), r => r.Text.StartsWith("🟡", StringComparison.Ordinal));

        Advance(30);
        await Press("act:stop");
        Advance(10);
        Assert.DoesNotContain(await Text("Обед"), r => r.Text.StartsWith("🟡", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evening_review_comes_before_sleep_once_and_lists_yellow()
    {
        _clock.UtcNow = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc); // регистрация накануне
        await OnboardAsync();
        _clock.UtcNow = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc); // пятница 09:00 Москва
        await Press("mo:yes");
        await Text("Работа");
        Advance(8 * 60);
        await Press("act:stop");                                                   // работа 09:00–17:00

        _clock.UtcNow = new DateTime(2026, 11, 13, 21, 30, 0, DateTimeKind.Utc);   // 00:30, сон в 01:00, разбор за 30 мин
        Assert.Equal(1, (await Pings.RunAsync(Ct)).Sent);
        var evening = _store.Sent[^1].Reply;
        Assert.StartsWith("🌙 Итоги дня.", evening.Text, StringComparison.Ordinal);
        Assert.Contains("Сегодня, 13 ноября", evening.Text, StringComparison.Ordinal);
        Assert.Contains("🟡 Осталось разобрать: 00:00–01:00, 17:00–00:30", evening.Text, StringComparison.Ordinal);
        Assert.Contains("ev:yellow", Data([evening]));

        // Раз в день.
        Advance(5);
        await Pings.RunAsync(Ct);
        Assert.DoesNotContain(_store.Sent.Skip(1), s => s.Reply.Text.StartsWith("🌙", StringComparison.Ordinal));

        var yellow = Yellow(await Press("ev:yellow"));
        Assert.Contains("00:00–01:00", yellow.Text, StringComparison.Ordinal);

        // Сон ночью 13-го уже подтверждён утром — ⚪ не осталось, кнопки «Остальное верно» нет.
        Assert.DoesNotContain("ev:ok", Data([evening]));
        Assert.Contains("22:00-06:00 Сон", Lines());
    }

    [Fact]
    public async Task Going_to_sleep_by_text_triggers_the_evening_review()
    {
        _clock.UtcNow = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();
        _clock.UtcNow = new DateTime(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);    // 23:00 13-го — раньше разбора по шаблону

        var replies = await Text("Сон");

        var evening = Assert.Single(replies, r => r.Text.StartsWith("🌙 Итоги дня.", StringComparison.Ordinal));
        Assert.DoesNotContain(await Text("Сон"), r => r.Text.StartsWith("🌙", StringComparison.Ordinal));

        // «Остальное верно»: ⚪ дня (сон по шаблону ночью 13-го) становится ✅.
        Assert.Contains("ev:ok", Data([evening]));
        Assert.Contains(await Press("ev:ok"), r => r.Text.StartsWith("✅ Подтвердил 1 ⚪", StringComparison.Ordinal));
        Assert.Contains("22:00-06:00 Сон", Lines());
    }

    [Fact]
    public async Task Morning_asks_about_last_night_once()
    {
        _clock.UtcNow = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();

        // Первая ночь после регистрации, утро: 10:00 Москва.
        _clock.UtcNow = new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc);
        var morning = Assert.Single(await Text("Работа"), r => r.Text.StartsWith("☀️", StringComparison.Ordinal));
        Assert.Contains("Спал как обычно, 01:00–09:00?", morning.Text, StringComparison.Ordinal);

        await Press("mo:yes");
        Assert.Contains("22:00-06:00 Сон", Lines());
        Assert.DoesNotContain(await Text("Обед"), r => r.Text.StartsWith("☀️", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Morning_correction_records_actual_sleep()
    {
        _clock.UtcNow = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        await OnboardAsync();
        _clock.UtcNow = new DateTime(2026, 11, 14, 7, 30, 0, DateTimeKind.Utc);    // 10:30

        await Text("Работа");
        Assert.Contains(await Press("mo:no"), r => r.Text.StartsWith("Во сколько лёг и встал?", StringComparison.Ordinal));
        Assert.Contains(await Text("2-10"), r => r.Text.StartsWith("✅ Сон 02:00–10:00 (8 ч)", StringComparison.Ordinal));
        Assert.Contains("23:00-07:00 Сон", Lines());
    }
}
