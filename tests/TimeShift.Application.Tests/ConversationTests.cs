using TimeShift.Application.Conversations;
using TimeShift.Domain.Pings;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

public class ConversationTests
{
    private const long ChatId = 100;

    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new();

    private ConversationService Service
    {
        get
        {
            var tracking = new Tracking.TrackingService(_store, _store, _store);
            return new(_store, _store, tracking, _store.Applier(tracking, new ScriptedLlm()), _store.Speech, _store.Questions(tracking), new Tracking.YellowReview(tracking), new Tracking.DailyReview(tracking, new Tracking.YellowReview(tracking)), _store.Renderer, _clock);
        }
    }

    private User Me => Assert.Single(_store.Users);

    private Task<IReadOnlyList<BotReply>> Text(string text) =>
        Service.HandleAsync(IncomingMessage.FromText(ChatId, "Тест", text), TestContext.Current.CancellationToken);

    private Task<IReadOnlyList<BotReply>> Press(string data) =>
        Service.HandleAsync(IncomingMessage.FromButton(ChatId, "Тест", data), TestContext.Current.CancellationToken);

    private static IEnumerable<string> ButtonData(IReadOnlyList<BotReply> replies) =>
        replies.SelectMany(r => r.Buttons).SelectMany(row => row).Select(b => b.Data);

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
    public async Task Full_onboarding_builds_the_week_template_and_settings()
    {
        var first = await Text("/start");
        Assert.Contains("tz:Europe/Moscow", ButtonData(first));
        Assert.False(Me.IsOnboarded);

        await Press("tz:Asia/Yekaterinburg");
        Assert.Equal("Asia/Yekaterinburg", Me.TimeZone);

        var sleep = await Text("23:30-7:30");
        Assert.Contains("wkend:diff", ButtonData(sleep));
        await Press("wkend:diff");
        await Press("sleep:02:00-10:00");

        var work = await Text("Работа пн-пт 9-18");
        Assert.Contains(work, r => r.Text.Contains("Работа · пн–пт · 09:00–18:00"));
        await Text("Учёба по субботам 12-15");
        await Text("готово");

        await Text("Тренировка вт, чт 19-21");
        await Press("next");
        await Press("ping:45");
        var done = await Press("ext:typical");

        Assert.True(Me.IsOnboarded);
        Assert.Null(Me.DialogState);
        Assert.Contains(done, r => r.Text.Contains("Готово!"));
        Assert.Equal(45, Me.Settings.PingIntervalMin);
        Assert.Equal(ExtensionPolicy.UpToTypical, Me.Settings.Extension);

        Assert.Equal(
            [
                "Sleep 23:30-07:30 [Sunday,Monday,Tuesday,Wednesday,Thursday]",
                "Sleep 02:00-10:00 [Sunday,Saturday]",
                "Fixed 09:00-18:00 [Monday,Tuesday,Wednesday,Thursday,Friday]",
                "Fixed 12:00-15:00 [Saturday]",
                "Custom 19:00-21:00 [Tuesday,Thursday]",
            ],
            Me.Blocks.Select(b => $"{b.Kind} {b.Start:HH\\:mm}-{b.End:HH\\:mm} [{string.Join(",", b.Days.Order())}]"));
        Assert.Equal("work", Me.Blocks.Single(b => b.Title == "Работа").CategoryKey);
        Assert.Equal("sport", Me.Blocks.Single(b => b.Title == "Тренировка").CategoryKey);

        // Шаблон собирается без пересечений.
        Assert.Equal(5, Me.Template().Blocks.Count);
    }

    [Fact]
    public async Task Ping_interval_accepts_the_whole_range_and_rejects_the_rest()
    {
        await Text("/start");
        await Press("tz:Europe/Moscow");
        await Text("1-9");
        await Press("wkend:same");
        await Press("next");
        var ask = await Press("next");
        Assert.Contains("ping:120", ButtonData(ask));

        foreach (var bad in new[] { "4", "241", "0", "много" })
        {
            var rejected = await Text(bad);
            Assert.Contains(rejected, r => r.Text.Contains("от 5 до 240"));
            Assert.Equal(UserSettings.DefaultPingIntervalMin, Me.Settings.PingIntervalMin);
        }

        await Text("15");
        Assert.Equal(15, Me.Settings.PingIntervalMin);
    }

    [Fact]
    public async Task Ping_interval_buttons_in_settings_cover_the_range()
    {
        await OnboardAsync();

        await Press("ping:120");
        Assert.Equal(120, Me.Settings.PingIntervalMin);
        await Press("ping:4");
        await Press("ping:241");
        Assert.Equal(120, Me.Settings.PingIntervalMin);
        Assert.True(UserSettings.IsValidPingInterval(5));
        Assert.True(UserSettings.IsValidPingInterval(240));
        Assert.False(UserSettings.IsValidPingInterval(4));
        Assert.False(UserSettings.IsValidPingInterval(241));
    }

    [Fact]
    public async Task Help_describes_only_what_exists()
    {
        await OnboardAsync();

        var help = Assert.Single(await Text("/help")).Text;

        // Без LLM фразы не разбираются, голос включён в фейке; устаревших обещаний нет.
        Assert.DoesNotContain("научусь", help);
        Assert.DoesNotContain("Можно писать свободно", help);
        Assert.Contains("Голосовые тоже понимаю", help);
        Assert.Contains("/add", help);

        _store.Speech.Enabled = false;
        Assert.DoesNotContain("Голосовые", Assert.Single(await Text("/help")).Text);
    }

    [Fact]
    public async Task Long_text_without_llm_suggests_add_instead_of_promising()
    {
        await OnboardAsync();

        var replies = await Text("час тренировался, потом поехал домой и ещё много всего случилось за день");

        var text = Assert.Single(replies).Text;
        Assert.Contains("/add", text);
        Assert.DoesNotContain("научусь", text);
    }

    [Fact]
    public async Task Any_first_message_starts_onboarding()
    {
        var replies = await Text("привет");

        Assert.Single(_store.Users);
        Assert.Contains("tz:Europe/Moscow", ButtonData(replies));
    }

    [Fact]
    public async Task Commands_during_onboarding_remind_to_finish_it()
    {
        await Text("/start");

        var replies = await Text("/settings");

        Assert.Contains(replies, r => r.Text.Contains("закончим настройку"));
        Assert.Contains("tz:Europe/Moscow", ButtonData(replies));
    }

    [Fact]
    public async Task Unparsable_and_conflicting_blocks_are_explained()
    {
        await Text("/start");
        await Press("tz:Europe/Moscow");
        await Text("1-9");
        await Press("wkend:same");

        var noTime = await Text("Работа по будням");
        Assert.Contains(noTime, r => r.Text.Contains("Не нашёл время"));

        var overlap = await Text("Пробежка каждый день 8-10");
        Assert.Contains(overlap, r => r.Text.Contains("Пересекается"));
        Assert.Single(Me.Blocks);
    }

    [Fact]
    public async Task Added_block_can_be_undone_from_its_message()
    {
        await Text("/start");
        await Press("tz:Europe/Moscow");
        await Text("1-9");
        await Press("wkend:same");
        var added = await Text("Работа пн-пт 9-18");

        var undo = ButtonData(added).Single(d => d.StartsWith("undo:", StringComparison.Ordinal));
        var replies = await Press(undo);

        Assert.True(Assert.Single(replies).ReplacesSource);
        Assert.DoesNotContain(Me.Blocks, b => b.Title == "Работа");
    }

    [Fact]
    public async Task Blocks_menu_adds_and_deletes_blocks()
    {
        await OnboardAsync();

        await Text("/blocks");
        await Press("add:custom");
        var afterAdd = await Text("Тренировка вт, чт 19-21");
        Assert.Contains(afterAdd, r => r.Text.StartsWith("Шаблон недели", StringComparison.Ordinal));
        Assert.Null(Me.DialogState);

        var training = Me.Blocks.Single(b => b.Title == "Тренировка");
        var menu = await Press($"del:{training.Id}");

        var removed = Assert.Single(menu);
        Assert.True(removed.ReplacesSource);
        Assert.StartsWith("Убрал: Тренировка", removed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Me.Blocks, b => b.Title == "Тренировка");

        // Промахнулся пальцем — «↩️ Вернуть» в том же сообщении.
        Assert.Equal($"rb:{training.Id}", ButtonData(menu).First());
        var back = Assert.Single(await Press($"rb:{training.Id}"));
        Assert.True(back.ReplacesSource);
        Assert.StartsWith("Вернул: Тренировка", back.Text, StringComparison.Ordinal);
        Assert.Contains(Me.Blocks, b => b.Id == training.Id);
        Assert.Contains("уже не вернуть", Assert.Single(await Press($"rb:{training.Id}")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Block_typed_again_with_the_same_title_replaces_the_old_one()
    {
        await OnboardAsync();
        await Press("add:fixed");
        await Text("Работа пн-пт 9-18");
        var work = Me.Blocks.Single(b => b.Title == "Работа");

        await Press("add:fixed");
        var replaced = await Text("работа пн-пт 10-19");

        Assert.Contains(replaced, r => r.Text.StartsWith("Заменил: Работа", StringComparison.Ordinal));
        var now = Assert.Single(Me.Blocks, b => b.Title.Equals("работа", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(work.Id, now.Id);
        Assert.Equal((new TimeOnly(10, 0), new TimeOnly(19, 0)), (now.Start, now.End));
        Assert.Equal(work.CategoryKey, now.CategoryKey);

        // Другое время без пересечения — второй блок с тем же названием.
        await Press("add:fixed");
        await Text("Работа сб 11-14");
        Assert.Equal(2, Me.Blocks.Count(b => b.Title.Equals("работа", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Settings_values_step_with_minus_and_plus_and_stop_at_the_ends()
    {
        await OnboardAsync();

        var menu = await Text("/settings");
        Assert.Contains("set:unanswered:+", ButtonData(menu));
        Assert.Contains("set:unanswered:-", ButtonData(menu));

        await Press("set:unanswered:-");
        Assert.Equal(1, Me.Settings.MaxUnansweredPings);
        var atMin = await Press("set:unanswered:-");
        Assert.Equal(1, Me.Settings.MaxUnansweredPings);
        Assert.DoesNotContain("set:unanswered:-", ButtonData(atMin));
        await Press("set:unanswered:+");
        Assert.Equal(2, Me.Settings.MaxUnansweredPings);

        await Press("set:review:+");
        Assert.Equal(45, Me.Settings.EveningReviewOffsetMin);
        Me.Settings.EveningReviewOffsetMin = 37;
        await Press("set:review:-");
        Assert.Equal(30, Me.Settings.EveningReviewOffsetMin);
        Me.Settings.EveningReviewOffsetMin = 175;
        var atMax = await Press("set:review:+");
        Assert.Equal(180, Me.Settings.EveningReviewOffsetMin);
        Assert.DoesNotContain("set:review:+", ButtonData(atMax));

        Assert.Contains("set:rounding:1", ButtonData(menu));
        await Press("set:rounding:15");
        Assert.Equal(15, Me.Settings.RoundingMin);
        await Press("set:rounding:7");
        Assert.Equal(15, Me.Settings.RoundingMin);

        // Кнопка из старого меню (цикл) ничего не меняет — меню перерисовывается.
        var stale = Assert.Single(await Press("set:unanswered"));
        Assert.True(stale.ReplacesSource);
        Assert.Equal(2, Me.Settings.MaxUnansweredPings);
        Assert.All(ButtonData(menu), d => Assert.True(System.Text.Encoding.UTF8.GetByteCount(d) <= 64, d));
    }

    [Fact]
    public async Task Sleep_can_be_changed_from_the_blocks_menu()
    {
        await OnboardAsync();

        await Press("edit:sleep");
        await Text("0-8");
        var menu = await Press("wkend:same");

        var sleep = Assert.Single(Me.Blocks, b => b.Kind == ScheduleBlockKind.Sleep);
        Assert.Equal((new TimeOnly(0, 0), new TimeOnly(8, 0)), (sleep.Start, sleep.End));
        Assert.Contains(menu, r => r.Text.StartsWith("Шаблон недели", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_menu_changes_values_in_place()
    {
        await OnboardAsync();

        var menu = await Text("/settings");
        Assert.Contains("set:rounding:10", ButtonData(menu));

        var rounding = await Press("set:rounding:10");
        Assert.True(Assert.Single(rounding).ReplacesSource);
        Assert.Equal(10, Me.Settings.RoundingMin);

        await Press("ping:60");
        await Press("set:blockping");
        Assert.Equal(60, Me.Settings.PingIntervalMin);
        Assert.True(Me.Settings.PingDuringBlocks);

        await Press("ext:custom");
        await Text("1.2 ∞");
        Assert.Equal(1.2, Me.Settings.NormFactor);
        Assert.Null(Me.Settings.MaxFactor);
        Assert.Null(Me.DialogState);

        await Press("set:pings");
        Assert.False(Me.Settings.PingsEnabled);
        await Press("set:afterlimit");
        Assert.Equal(UnansweredPingsPolicy.Stop, Me.Settings.AfterUnansweredPings);

        await Press("edit:quiet");
        var unclear = await Text("когда-нибудь");
        Assert.Contains(unclear, r => r.Text.Contains("Не понял время"));
        var quiet = await Text("22-8");
        Assert.Equal(new QuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0)), Me.Settings.QuietHours);
        Assert.Contains(quiet, r => r.Text.Contains("🤫 Тихие часы: 22:00–08:00"));
        Assert.Contains(quiet, r => r.Text.Contains("🔔 Пинги: выключены"));
        Assert.Contains(quiet, r => r.Text.Contains("пауза до ответа"));
        Assert.Null(Me.DialogState);
        await Press("edit:quiet");
        await Press("qhours:none");
        Assert.Null(Me.Settings.QuietHours);
        Assert.Null(Me.DialogState);

        await Press("edit:tz");
        var bad = await Text("Mars/Olympus");
        Assert.Contains(bad, r => r.Text.Contains("Не знаю часовой пояс"));
        await Text("Asia/Novosibirsk");
        Assert.Equal("Asia/Novosibirsk", Me.TimeZone);
    }

    [Fact]
    public async Task Voice_and_other_media_get_a_polite_answer()
    {
        await OnboardAsync();

        var replies = await Service.HandleAsync(new IncomingMessage(ChatId, "Тест"), TestContext.Current.CancellationToken);

        Assert.Contains(replies, r => r.Text.Contains("Фото и файлы"));
    }
}
