using System.Globalization;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Parsing;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Conversations;

/// <summary>
/// Диалог с пользователем, не зависящий от клиента: онбординг (spec §5.1), <c>/blocks</c> и <c>/settings</c>
/// (spec §5.7, §6). Клиент (Telegram) только переводит входящие сообщения и ответы.
/// Что бот ждёт в ответ, хранится в <see cref="User.DialogState"/> как <c>O:Step</c> (онбординг) или <c>E:Step</c>
/// (правка из меню — после неё бот возвращает в меню).
/// </summary>
public sealed partial class ConversationService(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    TrackingService tracking,
    Llm.IntentApplier intents,
    ISpeechToText speech,
    Questions.QuestionService questions,
    YellowReview yellow,
    DailyReview daily,
    IDayRenderer renderer,
    IClock clock)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly (string Label, string Id)[] TimeZones =
    [
        ("Калининград", "Europe/Kaliningrad"), ("Москва", "Europe/Moscow"), ("Самара", "Europe/Samara"),
        ("Екатеринбург", "Asia/Yekaterinburg"), ("Омск", "Asia/Omsk"), ("Новосибирск", "Asia/Novosibirsk"),
        ("Иркутск", "Asia/Irkutsk"), ("Владивосток", "Asia/Vladivostok"),
    ];

    private static readonly string[] SleepPresets = ["23:00-07:00", "00:00-08:00", "01:00-09:00", "02:00-10:00"];

    private static readonly int[] PingIntervals = [15, 30, 45, 60, 90, 120];

    public async Task<IReadOnlyList<BotReply>> HandleAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Voice is { } voice)
        {
            // Голос → текст, дальше как обычное сообщение; распознанное показываем, чтобы было видно ошибки.
            var (text, problem) = await TranscribeAsync(voice, message.VoiceDuration, cancellationToken);
            if (text is null)
            {
                return [BotReply.Plain(problem!)];
            }

            var handled = await HandleAsync(message with { Text = text, Voice = null }, cancellationToken);
            return [BotReply.Plain($"🎙 «{text}»"), .. handled];
        }

        var now = clock.UtcNow;
        var user = await users.FindByTelegramChatIdAsync(message.ChatId, cancellationToken);
        IReadOnlyList<BotReply> replies;
        if (user is null)
        {
            var name = string.IsNullOrWhiteSpace(message.DisplayName) ? "друг" : message.DisplayName.Trim();
            user = new User(message.ChatId, name, User.DefaultTimeZone, now);
            users.Add(user);
            user.RegisterActivity(now);
            replies = StartOnboarding(user);
        }
        else
        {
            user.RegisterActivity(now);
            replies = (user.IsOnboarded ? await TrackAsync(user, message, now, cancellationToken) : null)
                ?? Dispatch(user, message);

            // Уточняющий вопрос — сразу после ответа, по одному, и только когда бот ничего другого не ждёт (spec §5.5).
            // Время до начала работы с ботом не разбираем — точка отсчёта 🟡 — первое сообщение после онбординга.
            if (user.IsOnboarded && user.YellowReviewedUntilUtc is null)
            {
                user.MarkYellowReviewed(now);
            }

            // После обычного сообщения или кнопки (не команды): сначала разобрать 🟡 «пока тебя не было» (spec §5.4),
            // иначе — очередь вопросов.
            var answersBot = message.ButtonData is { } pressed
                ? pressed.StartsWith("act:", StringComparison.Ordinal) || pressed.StartsWith("per:", StringComparison.Ordinal)
                  || pressed.StartsWith("q:", StringComparison.Ordinal)
                : message.Text?.TrimStart().StartsWith('/') != true;
            if (user.IsOnboarded && answersBot && user.DialogState is null && !await intents.IsWaitingForFactAsync(user, cancellationToken)
                && (await daily.MorningAsync(user, now, cancellationToken)
                    ?? await EveningOnSleepAsync(user, now, cancellationToken)
                    ?? await yellow.NextAsync(user, now, cancellationToken)
                    ?? await questions.NextAsync(user, now, cancellationToken)) is { } question)
            {
                replies = [.. replies, question];
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return replies;
    }

    /// <summary>«Ложусь спать» (начат сон) — вечерний разбор, если сегодня его ещё не было (spec §5.4).</summary>
    private async Task<BotReply?> EveningOnSleepAsync(User user, DateTime now, CancellationToken ct)
    {
        var current = await tracking.CurrentAsync(user, ct);
        var isSleep = (current?.Activity?.CategoryKey ?? current?.Segment.CategoryKey) == Domain.Categories.DefaultCategories.Sleep;
        return isSleep && user.LastEveningReview != DailyReview.ReviewDay(user, now) ? await daily.EveningAsync(user, now, ct) : null;
    }

    private async Task<(string? Text, string? Problem)> TranscribeAsync(byte[] voice, TimeSpan duration, CancellationToken ct)
    {
        if (!speech.IsEnabled)
        {
            return (null, "Голосовые пока не включены на сервере — напиши текстом, пожалуйста.");
        }

        if (duration > speech.MaxDuration)
        {
            return (null, $"Голосовое длиннее {TrackingReplies.Duration(speech.MaxDuration)} — раздели на части или напиши текстом.");
        }

        try
        {
            using var stream = new MemoryStream(voice, writable: false);
            var text = await speech.TranscribeAsync(stream, ct);
            return string.IsNullOrWhiteSpace(text)
                ? (null, "Не расслышал 🤔 Повтори или напиши текстом.")
                : (text, null);
        }
        catch (SpeechException)
        {
            return (null, "Не получилось распознать голосовое — напиши текстом, пожалуйста.");
        }
    }

    private static IReadOnlyList<BotReply> Dispatch(User user, IncomingMessage message)
    {
        if (message.ButtonData is { } data)
        {
            return OnButton(user, data);
        }

        if (message.Text is null)
        {
            return [BotReply.Plain("Я понимаю текст, голосовые и кнопки. Фото и файлы пока не разбираю.")];
        }

        var text = message.Text.Trim();
        if (text.StartsWith('/'))
        {
            return OnCommand(user, text);
        }

        return OnText(user, text);
    }

    // ---- Команды ----------------------------------------------------------------------------------------------

    private static IReadOnlyList<BotReply> OnCommand(User user, string text)
    {
        var command = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        if (command == "/start" && !user.IsOnboarded)
        {
            return StartOnboarding(user);
        }

        if (!user.IsOnboarded)
        {
            return [BotReply.Plain("Давай сначала закончим настройку — это пара минут."), .. Repeat(user)];
        }

        user.DialogState = null;
        return command switch
        {
            "/start" or "/help" => [Help(user)],
            "/blocks" => [BlocksMenu(user)],
            "/settings" => [SettingsMenu(user)],
            _ => [BotReply.Plain("Такой команды пока нет."), Help(user)],
        };
    }

    private static BotReply Help(User user) => BotReply.Plain(
        $"Привет, {user.DisplayName}! Я TimeShift — помогаю понять, куда уходит время.\n\n" +
        $"Раз в {user.Settings.PingIntervalMin} мин я спрашиваю, чем ты занят, — ответь кнопкой. Можно и самому " +
        "написать занятие («Работаю», «Тренировка») — запишу его с этого момента.\n\n" +
        "/now — что сейчас идёт, сменить занятие\n" +
        "/day — лента за сегодня (/day вчера, /day 13.11 — другой день)\n" +
        "/add — записать прошедшее: «вчера 19-21 Зал»\n" +
        "/quiet — не беспокоить пару часов\n" +
        "/undo — отменить последнюю запись\n" +
        "/facts — что я о тебе знаю: длительности, маршруты, категории\n" +
        "/blocks — шаблон недели (сон, работа, учёба, свои блоки)\n" +
        "/settings — пинги, продление занятий, часовой пояс\n\n" +
        "Разбирать фразы вроде «час тренировался, потом поехал домой» и голосовые я научусь чуть позже.");

    // ---- Онбординг --------------------------------------------------------------------------------------------

    private static IReadOnlyList<BotReply> StartOnboarding(User user)
    {
        user.DialogState = State(Mode.Onboarding, Step.TimeZone);
        return
        [
            BotReply.Plain(
                $"Привет, {user.DisplayName}! Я TimeShift: сам заполняю, как прошёл твой день, а ты только " +
                "подтверждаешь или поправляешь — кнопкой, текстом или голосом.\n\n" +
                "Сначала настроим шаблон недели. Это пара минут, всё можно поменять потом."),
            Ask(Step.TimeZone),
        ];
    }

    private static IReadOnlyList<BotReply> Repeat(User user) =>
        Parse(user.DialogState) is { } state ? [Ask(state.Step)] : [];

    private static BotReply Ask(Step step) => step switch
    {
        Step.TimeZone => new BotReply(
            "Какой у тебя часовой пояс? Можно нажать кнопку или написать IANA-название, например «Asia/Almaty».",
            Rows(TimeZones.Select(z => new ReplyButton(z.Label, $"tz:{z.Id}")), 2)),
        Step.Sleep => new BotReply(
            "Во сколько ты обычно ложишься и встаёшь? Нажми кнопку или напиши, например, «1-9» или «23:30-7:30».",
            Rows(SleepPresets.Select(p => new ReplyButton(p.Replace("-", "–"), $"sleep:{p}")), 2)),
        Step.SleepWeekendQuestion => new BotReply(
            "На выходных так же?",
            [[new ReplyButton("Да, так же", "wkend:same"), new ReplyButton("Нет, по-другому", "wkend:diff")]]),
        Step.SleepWeekend => new BotReply(
            "Во сколько ложишься и встаёшь перед выходными (в ночь на субботу и на воскресенье)?",
            Rows(SleepPresets.Select(p => new ReplyButton(p.Replace("-", "–"), $"sleep:{p}")), 2)),
        Step.FixedBlocks => new BotReply(
            "Всё, что вне сна и блоков, я по умолчанию считаю фоном «Залипание» 🟡 — это не упрёк, а честная " +
            "отметка «не знаю, что было». Я буду переспрашивать, и фон быстро сменится настоящими делами.\n\n" +
            "Есть работа или учёба по расписанию? Напиши блок, например:\n" +
            "«Работа пн-пт 9-18»\n«Учёба пн, ср, пт 9:30-15»\n«Учёба по субботам 10-13»\n\n" +
            "По одному блоку в сообщении. Когда всё — нажми «Готово».",
            [[new ReplyButton("Нет такого / Готово", "next")]]),
        Step.CustomBlocks => new BotReply(
            "Есть ещё что-то регулярное? Например «Тренировка вт, чт 19-21» или «Йога каждый день 7-7:30».",
            [[new ReplyButton("Нет / Готово", "next")]]),
        Step.PingInterval => new BotReply(
            $"Как часто спрашивать, чем ты занят? Нажми кнопку или напиши число минут — от {UserSettings.MinPingIntervalMin} до {UserSettings.MaxPingIntervalMin}.",
            Rows(PingIntervals.Select(m => new ReplyButton($"Раз в {m} мин", $"ping:{m}")), 3)),
        Step.Extension => new BotReply(
            "Если ты сказал «тренируюсь» и пропал — сколько считать, что ты всё ещё тренируешься?\n\n" +
            "Рекомендую: до полуторной обычной длительности — в норме ⚪, до двойной — под вопросом 🟡, дальше — фон.",
            ExtensionButtons(null, onboarding: true)),
        Step.CustomExtension => BotReply.Plain(
            "Напиши два числа: множитель нормы и предела, например «1.2 2.5». «∞» — без ограничения."),
        Step.ActivityName => BotReply.Plain("Чем занят? Напиши коротко, например «Готовлю ужин»."),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    /// <summary>Шаг выполнен: в онбординге — следующий вопрос, при правке — назад в меню.</summary>
    private static IReadOnlyList<BotReply> Advance(User user, Mode mode, Step done, BotReply? confirmation = null)
    {
        var replies = new List<BotReply>();
        if (confirmation is not null)
        {
            replies.Add(confirmation);
        }

        if (mode == Mode.Edit)
        {
            user.DialogState = null;
            replies.Add(done is Step.TimeZone or Step.CustomExtension ? SettingsMenu(user) : BlocksMenu(user));
            return replies;
        }

        var next = done switch
        {
            Step.TimeZone => Step.Sleep,
            Step.Sleep => Step.SleepWeekendQuestion,
            Step.SleepWeekendQuestion or Step.SleepWeekend => Step.FixedBlocks,
            Step.FixedBlocks => Step.CustomBlocks,
            Step.CustomBlocks => Step.PingInterval,
            Step.PingInterval => Step.Extension,
            _ => (Step?)null,
        };

        if (next is { } step)
        {
            user.DialogState = State(Mode.Onboarding, step);
            replies.Add(Ask(step));
            return replies;
        }

        user.DialogState = null;
        user.CompleteOnboarding();
        replies.Add(BotReply.Plain(
            "Готово! Твой шаблон недели:\n\n" + TemplateText(user) + "\n\n" +
            "Поменять можно в любой момент: /blocks — блоки, /settings — пинги и продление.\n\n" +
            $"Теперь раз в {user.Settings.PingIntervalMin} мин я буду спрашивать, чем ты занят. Можно и самому " +
            "написать занятие, например «Работаю», — запишу с этого момента. /help — все команды."));
        return replies;
    }

    // ---- Текст ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<BotReply> OnText(User user, string text)
    {
        if (Parse(user.DialogState) is not { } state)
        {
            return user.IsOnboarded
                ? [BotReply.Plain("Записывать занятия я пока не умею — это следующий шаг разработки. Сейчас доступны /blocks и /settings.")]
                : StartOnboarding(user);
        }

        var (mode, step) = state;
        switch (step)
        {
            case Step.TimeZone:
                return TrySetTimeZone(user, text, mode);

            case Step.Sleep or Step.SleepWeekend:
                return ScheduleText.TryParseTimeRange(text, out var start, out var end)
                    ? SetSleep(user, mode, step, start, end)
                    : [BotReply.Plain("Не понял время. Напиши, например, «1-9» или «23:30-7:30»."), Ask(step)];

            case Step.FixedBlocks or Step.CustomBlocks:
                if (IsDone(text))
                {
                    return Advance(user, mode, step);
                }

                return AddBlock(user, mode, step, text);

            case Step.PingInterval:
                return int.TryParse(text.Split(' ')[0], out var minutes) && UserSettings.IsValidPingInterval(minutes)
                    ? SetPing(user, mode, minutes)
                    : [BotReply.Plain($"Интервал — от {UserSettings.MinPingIntervalMin} до {UserSettings.MaxPingIntervalMin} минут."), Ask(step)];

            case Step.CustomExtension:
                return SetCustomExtension(user, mode, text);

            default:
                return [BotReply.Plain("Выбери кнопкой, пожалуйста."), Ask(step)];
        }
    }

    private static bool IsDone(string text) =>
        text.ToLowerInvariant().Trim('.', '!', ' ') is "готово" or "нет" or "всё" or "все" or "хватит" or "дальше";

    // ---- Кнопки -----------------------------------------------------------------------------------------------

    private static IReadOnlyList<BotReply> OnButton(User user, string data)
    {
        var (kind, value) = data.IndexOf(':') is var i and >= 0 ? (data[..i], data[(i + 1)..]) : (data, string.Empty);
        var state = Parse(user.DialogState);
        var mode = state?.Mode ?? Mode.Edit;

        switch (kind)
        {
            case "tz":
                return TrySetTimeZone(user, value, mode);

            case "sleep" when ScheduleText.TryParseTimeRange(value, out var start, out var end):
                var sleepStep = state?.Step == Step.SleepWeekend ? Step.SleepWeekend : Step.Sleep;
                return SetSleep(user, mode, sleepStep, start, end);

            case "wkend" when value == "same":
                return Advance(user, mode, Step.SleepWeekendQuestion);

            case "wkend":
                user.DialogState = State(mode, Step.SleepWeekend);
                return [Ask(Step.SleepWeekend)];

            case "next" when state is { Step: Step.FixedBlocks or Step.CustomBlocks }:
                return Advance(user, mode, state.Value.Step);

            case "next":
                return Repeat(user);

            case "undo" when Guid.TryParse(value, out var addedId):
                var removed = user.Blocks.FirstOrDefault(b => b.Id == addedId);
                return removed is not null && user.RemoveBlock(addedId)
                    ? [new BotReply($"Убрал: {ScheduleText.Format(removed)}", [], ReplacesSource: true)]
                    : [new BotReply("Этого блока уже нет.", [], ReplacesSource: true)];

            case "ping" when int.TryParse(value, out var minutes) && UserSettings.IsValidPingInterval(minutes):
                if (state is { Mode: Mode.Onboarding, Step: Step.PingInterval })
                {
                    return SetPing(user, Mode.Onboarding, minutes);
                }

                user.Settings.PingIntervalMin = minutes;
                return [SettingsMenu(user, replaces: true)];

            case "ext":
                return OnExtensionButton(user, state, value);
        }

        if (!user.IsOnboarded)
        {
            return Repeat(user);
        }

        // Меню (только после онбординга).
        switch (kind)
        {
            case "menu" when value == "blocks":
                user.DialogState = null;
                return [BlocksMenu(user, replaces: true)];

            case "menu":
                user.DialogState = null;
                return [SettingsMenu(user, replaces: true)];

            case "del" when Guid.TryParse(value, out var id):
                user.RemoveBlock(id);
                return [BlocksMenu(user, replaces: true)];

            case "add":
                var step = value == "custom" ? Step.CustomBlocks : Step.FixedBlocks;
                user.DialogState = State(Mode.Edit, step);
                return [BotReply.Plain(step == Step.FixedBlocks
                    ? "Напиши блок, например «Работа пн-пт 9-18» или «Учёба по субботам 10-13»."
                    : "Напиши блок, например «Тренировка вт, чт 19-21».")];

            case "edit" when value == "sleep":
                user.DialogState = State(Mode.Edit, Step.Sleep);
                return [Ask(Step.Sleep)];

            case "edit" when value == "tz":
                user.DialogState = State(Mode.Edit, Step.TimeZone);
                return [Ask(Step.TimeZone)];

            case "set":
                ChangeSetting(user.Settings, value);
                return [SettingsMenu(user, replaces: true)];
        }

        return [BotReply.Plain("Эта кнопка устарела."), Help(user)];
    }

    private static IReadOnlyList<BotReply> OnExtensionButton(User user, (Mode Mode, Step Step)? state, string value)
    {
        var onboarding = state is { Mode: Mode.Onboarding, Step: Step.Extension };
        if (value == "custom")
        {
            user.DialogState = State(onboarding ? Mode.Onboarding : Mode.Edit, Step.CustomExtension);
            return [Ask(Step.CustomExtension)];
        }

        ExtensionPolicy? policy = value switch
        {
            "typical" => ExtensionPolicy.UpToTypical,
            "default" => ExtensionPolicy.Default,
            "infinite" => ExtensionPolicy.Infinite,
            _ => null,
        };
        if (policy is null)
        {
            return Repeat(user);
        }

        user.Settings.Extension = policy;
        return onboarding
            ? Advance(user, Mode.Onboarding, Step.Extension, BotReply.Plain($"Продление: {ExtensionText(user.Settings)}."))
            : [SettingsMenu(user, replaces: true)];
    }

    // ---- Действия ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<BotReply> TrySetTimeZone(User user, string id, Mode mode)
    {
        id = id.Trim();
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            user.TimeZone = id;
            var offset = zone.GetUtcOffset(DateTime.UtcNow);
            var sign = offset < TimeSpan.Zero ? "−" : "+";
            return Advance(user, mode, Step.TimeZone, BotReply.Plain($"Часовой пояс: {id} (UTC{sign}{offset:hh\\:mm})."));
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return [BotReply.Plain($"Не знаю часовой пояс «{id}»."), Ask(Step.TimeZone)];
        }
    }

    private static IReadOnlyList<BotReply> SetSleep(User user, Mode mode, Step step, TimeOnly start, TimeOnly end)
    {
        ScheduleBlock? conflict;
        if (step == Step.SleepWeekend)
        {
            var weekday = user.Blocks.FirstOrDefault(b => b.Kind == ScheduleBlockKind.Sleep);
            if (weekday is null)
            {
                user.DialogState = State(mode, Step.Sleep);
                return [Ask(Step.Sleep)];
            }

            conflict = user.TrySetSleep((weekday.Start, weekday.End), (start, end));
        }
        else
        {
            conflict = user.TrySetSleep((start, end));
        }

        if (conflict is not null)
        {
            return [BotReply.Plain($"Сон пересекается с блоком «{ScheduleText.Format(conflict)}». Поправь время или сначала убери блок в /blocks."), Ask(step)];
        }

        var text = "Записал сон:\n" + string.Join('\n', user.Blocks
            .Where(b => b.Kind == ScheduleBlockKind.Sleep)
            .Select(b => $"😴 {ScheduleText.FormatTime(b.Start, b.End)} — {SleepNights(b)}"));

        // Из меню после буднего сна тоже спрашиваем про выходные.
        if (step == Step.Sleep)
        {
            user.DialogState = State(mode, Step.SleepWeekendQuestion);
            return [BotReply.Plain(text), Ask(Step.SleepWeekendQuestion)];
        }

        return Advance(user, mode, step, BotReply.Plain(text));
    }

    private static string SleepNights(ScheduleBlock block) => block.Days.Count == 7
        ? "каждую ночь"
        : "начало: " + ScheduleText.FormatDays(block.Days);

    private static IReadOnlyList<BotReply> AddBlock(User user, Mode mode, Step step, string text)
    {
        if (!ScheduleText.TryParseBlock(text, out var parsed, out var error))
        {
            return [BotReply.Plain(error!)];
        }

        var kind = step == Step.FixedBlocks ? ScheduleBlockKind.Fixed : ScheduleBlockKind.Custom;
        var block = new ScheduleBlock(kind, parsed!.Title, ScheduleText.GuessCategory(parsed.Title), parsed.Days, parsed.Start, parsed.End);
        if (user.TryAddBlock(block) is { } conflict)
        {
            return [BotReply.Plain($"Пересекается с «{ScheduleText.Format(conflict)}». Поправь время или дни.")];
        }

        var added = $"Добавил: {ScheduleText.Format(block)}";
        if (mode == Mode.Edit)
        {
            return Advance(user, mode, step, BotReply.Plain(added));
        }

        return
        [
            new BotReply(
                added + "\n\nЕщё блок — просто напиши его. Если всё — «Готово».",
                [[new ReplyButton("↩️ Убрать", $"undo:{block.Id}"), new ReplyButton("Готово", "next")]]),
        ];
    }

    private static IReadOnlyList<BotReply> SetPing(User user, Mode mode, int minutes)
    {
        user.Settings.PingIntervalMin = minutes;
        return Advance(user, mode, Step.PingInterval, BotReply.Plain($"Буду спрашивать раз в {minutes} мин."));
    }

    private static IReadOnlyList<BotReply> SetCustomExtension(User user, Mode mode, string text)
    {
        var parts = text.Replace(',', '.').Replace("×", string.Empty).Replace("x", string.Empty)
            .Split([' ', '/', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryFactor(parts[0], out var norm) && TryFactor(parts[1], out var max) && norm > 0 && max >= norm)
        {
            user.Settings.Extension = ExtensionPolicy.Custom(norm, max);
            var confirmation = BotReply.Plain($"Продление: {ExtensionText(user.Settings)}.");
            return mode == Mode.Onboarding
                ? Advance(user, mode, Step.Extension, confirmation)
                : Advance(user, mode, Step.CustomExtension, confirmation);
        }

        return [BotReply.Plain("Нужны два числа: норма > 0 и предел не меньше нормы, например «1.2 2.5» или «2 ∞».")];
    }

    private static bool TryFactor(string text, out double value)
    {
        if (text is "∞" or "inf" or "бесконечно")
        {
            value = double.PositiveInfinity;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static void ChangeSetting(UserSettings settings, string name)
    {
        switch (name)
        {
            case "blockping":
                settings.PingDuringBlocks = !settings.PingDuringBlocks;
                break;
            case "unanswered":
                settings.MaxUnansweredPings = settings.MaxUnansweredPings % 5 + 1;
                break;
            case "review":
                settings.EveningReviewOffsetMin = Cycle([15, 30, 60], settings.EveningReviewOffsetMin);
                break;
            case "rounding":
                settings.RoundingMin = Cycle([5, 10, 15], settings.RoundingMin);
                break;
        }
    }

    private static int Cycle(int[] values, int current) =>
        values[(Array.IndexOf(values, current) + 1) % values.Length];

    // ---- Меню -------------------------------------------------------------------------------------------------

    private static BotReply BlocksMenu(User user, bool replaces = false)
    {
        var rows = user.Blocks
            .Where(b => b.Kind != ScheduleBlockKind.Sleep)
            .OrderBy(b => b.Start)
            .Select(b => (IReadOnlyList<ReplyButton>)[new ReplyButton($"🗑 {ScheduleText.Format(b)}", $"del:{b.Id}")])
            .ToList();
        rows.Add([new ReplyButton("➕ Работа / учёба", "add:fixed"), new ReplyButton("➕ Свой блок", "add:custom")]);
        rows.Add([new ReplyButton("😴 Изменить сон", "edit:sleep")]);

        return new BotReply(
            "Шаблон недели:\n\n" + TemplateText(user) + "\n\nНажми на блок, чтобы удалить его.",
            rows,
            replaces);
    }

    private static string TemplateText(User user)
    {
        var lines = user.Blocks
            .OrderBy(b => b.Kind != ScheduleBlockKind.Sleep)
            .ThenBy(b => b.Start)
            .Select(b => b.Kind switch
            {
                ScheduleBlockKind.Sleep => $"😴 Сон · {ScheduleText.FormatTime(b.Start, b.End)} · {SleepNights(b)}",
                ScheduleBlockKind.Fixed => $"📌 {ScheduleText.Format(b)}",
                _ => $"🔁 {ScheduleText.Format(b)}",
            })
            .ToList();

        lines.Add("🟡 Остальное время — фон «Залипание», пока не скажешь, что было.");
        return string.Join('\n', lines);
    }

    private static BotReply SettingsMenu(User user, bool replaces = false)
    {
        var s = user.Settings;
        var text =
            "Настройки:\n\n" +
            $"🌍 Часовой пояс: {user.TimeZone}\n" +
            $"⏰ Пинг: раз в {s.PingIntervalMin} мин\n" +
            $"⏳ Продление: {ExtensionText(s)}\n" +
            $"📌 Пинговать во время блоков: {(s.PingDuringBlocks ? "да" : "нет")}\n" +
            $"🔕 Неотвеченных пингов до паузы: {s.MaxUnansweredPings}\n" +
            $"🌙 Вечерний разбор: за {s.EveningReviewOffsetMin} мин до сна\n" +
            $"📏 Округление границ: {s.RoundingMin} мин\n" +
            "🤖 LLM: общий для сервера (настраивается позже)";

        IReadOnlyList<IReadOnlyList<ReplyButton>> rows =
        [
            .. Rows(PingIntervals.Select(m => new ReplyButton(Mark(s.PingIntervalMin == m, $"{m} мин"), $"ping:{m}")), 3),
            .. ExtensionButtons(s.Extension, onboarding: false),
            [
                new ReplyButton($"Пинг в блоках: {(s.PingDuringBlocks ? "да" : "нет")}", "set:blockping"),
                new ReplyButton($"До паузы: {s.MaxUnansweredPings}", "set:unanswered"),
            ],
            [
                new ReplyButton($"Разбор: за {s.EveningReviewOffsetMin} мин", "set:review"),
                new ReplyButton($"Округление: {s.RoundingMin} мин", "set:rounding"),
            ],
            [new ReplyButton("🌍 Часовой пояс", "edit:tz")],
        ];

        return new BotReply(text, rows, replaces);
    }

    private static IReadOnlyList<IReadOnlyList<ReplyButton>> ExtensionButtons(ExtensionPolicy? current, bool onboarding)
    {
        var isCustom = current is not null && current != ExtensionPolicy.UpToTypical
            && current != ExtensionPolicy.Default && current != ExtensionPolicy.Infinite;
        return
        [
            [
                new ReplyButton(Mark(current == ExtensionPolicy.Default, onboarding ? "×1.5 / ×2 (рекомендую)" : "×1.5 / ×2"), "ext:default"),
                new ReplyButton(Mark(current == ExtensionPolicy.UpToTypical, "До обычной длительности"), "ext:typical"),
            ],
            [
                new ReplyButton(Mark(current == ExtensionPolicy.Infinite, "Пока не скажу"), "ext:infinite"),
                new ReplyButton(Mark(isCustom, "Свой…"), "ext:custom"),
            ],
        ];
    }

    private static string ExtensionText(UserSettings settings)
    {
        var p = settings.Extension;
        if (p == ExtensionPolicy.Infinite)
        {
            return "пока не скажешь, что закончил";
        }

        return $"норма ×{Factor(p.NormFactor)}, предел ×{Factor(p.MaxFactor)}";
    }

    private static string Factor(double value) =>
        double.IsPositiveInfinity(value) ? "∞" : value.ToString("0.##", Ru);

    private static string Mark(bool selected, string label) => selected ? $"✓ {label}" : label;

    private static List<IReadOnlyList<ReplyButton>> Rows(IEnumerable<ReplyButton> buttons, int perRow) =>
        buttons.Chunk(perRow).Select(c => (IReadOnlyList<ReplyButton>)c).ToList();

    // ---- Состояние диалога ------------------------------------------------------------------------------------

    private enum Mode
    {
        Onboarding,
        Edit,
    }

    private enum Step
    {
        TimeZone,
        Sleep,
        SleepWeekendQuestion,
        SleepWeekend,
        FixedBlocks,
        CustomBlocks,
        PingInterval,
        Extension,
        CustomExtension,
        ActivityName,
        AddPast,
        SleepFix,
    }

    private static string State(Mode mode, Step step) => $"{(mode == Mode.Onboarding ? 'O' : 'E')}:{step}";

    private static (Mode Mode, Step Step)? Parse(string? state)
    {
        var stepName = state is { Length: > 2 } ? state[2..].Split(':')[0] : string.Empty;
        if (state is not { Length: > 2 } || state[1] != ':' || !Enum.TryParse<Step>(stepName, out var step))
        {
            return null;
        }

        return (state[0] == 'O' ? Mode.Onboarding : Mode.Edit, step);
    }

    /// <summary>Аргумент состояния после шага: <c>E:AddPast:2026-11-13</c> → <c>2026-11-13</c>.</summary>
    private static string? StateArgument(string? state)
    {
        var parts = state?.Split(':', 3);
        return parts is { Length: 3 } ? parts[2] : null;
    }
}
