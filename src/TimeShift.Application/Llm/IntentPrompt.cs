using System.Globalization;
using System.Text;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Llm;

/// <summary>Контекст, который код собирает для модели (spec §8: вход).</summary>
public sealed record IntentContext
{
    public required DateTime NowUtc { get; init; }

    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Открытое занятие и с какого момента (UTC).</summary>
    public (string Title, DateTime SinceUtc)? OpenActivity { get; init; }

    /// <summary>Лента за сегодня, сжато: «09:00–10:00 Работа ✅».</summary>
    public IReadOnlyList<string> TodayTimeline { get; init; } = [];

    /// <summary>Известные занятия пользователя — чтобы модель называла их так же.</summary>
    public IReadOnlyList<string> KnownActivities { get; init; } = [];

    /// <summary>Последний вопрос бота, если пользователь, возможно, отвечает на него.</summary>
    public string? LastBotQuestion { get; init; }

    /// <summary>Набор категорий пользователя: в промпт идут действующие, разбор принимает только их ключи.</summary>
    public CategorySet Categories { get; init; } = CategorySet.System;
}

/// <summary>
/// Системный промпт: контракт JSON (spec §8) и контекст. Модель только извлекает — даты, длительности и
/// раскладку периода считает код (<see cref="PastTime"/>, PeriodResolver).
/// </summary>
public static class IntentPrompt
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Build(IntentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var local = LocalTime.ToLocal(context.NowUtc, context.TimeZone);
        var builder = new StringBuilder(Contract.Replace(CategoryList, ListOf(context.Categories), StringComparison.Ordinal));
        builder.AppendLine().AppendLine("## Контекст");
        builder.Append("Сейчас: ").Append(local.ToString("dddd, d MMMM yyyy, HH:mm", Ru))
            .Append(" (").Append(local.DayOfWeek.ToString().ToLowerInvariant()).Append("), часовой пояс ")
            .AppendLine(context.TimeZone.Id);

        if (context.OpenActivity is { } open)
        {
            builder.Append("Сейчас идёт: «").Append(open.Title).Append("» с ")
                .AppendLine(LocalTime.ToLocal(open.SinceUtc, context.TimeZone).ToString("HH:mm", Ru));
        }
        else
        {
            builder.AppendLine("Сейчас идёт: ничего не записано.");
        }

        if (context.TodayTimeline.Count > 0)
        {
            builder.AppendLine("Лента за сегодня:");
            foreach (var line in context.TodayTimeline)
            {
                builder.Append("- ").AppendLine(line);
            }
        }

        if (context.KnownActivities.Count > 0)
        {
            builder.Append("Известные занятия (называй так же, если подходит): ")
                .AppendLine(string.Join(", ", context.KnownActivities));
        }

        if (context.LastBotQuestion is { } question)
        {
            builder.Append("Последний вопрос бота: «").Append(question).AppendLine("» — если сообщение отвечает на него, верни answer.");
        }

        return builder.ToString();
    }

    /// <summary>Системные категории для поля category.</summary>
    public static string CategoryList { get; } = ListOf(CategorySet.System);

    /// <summary>Категории для поля category: «ключ (Имя)», только действующие.</summary>
    private static string ListOf(CategorySet categories) =>
        string.Join(", ", categories.Active.Select(c => $"{c.Key} ({c.Name})"));

    private static string Contract => $$$$"""
        Ты разбираешь сообщения пользователя бота учёта времени. Пользователь пишет по-русски, как ему удобно:
        что делает сейчас, что делал раньше (в том числе вчера и в другие дни), поправки.

        Верни ТОЛЬКО JSON-объект вида {"intents": [ ... ]} — без пояснений и markdown.
        Ничего не считай и не выдумывай: не вычисляй длительности и даты, не заполняй то, чего не было в сообщении.
        Время — строкой "HH:mm" (местное, как сказал пользователь; «в 7 вечера» → "19:00").
        День — "today", "yesterday", "day_before_yesterday", день недели по-английски ("saturday") или "YYYY-MM-DD";
        не указывай день, если пользователь его не назвал.

        ## Типы намерений
        - start_activity — начал занятие сейчас или в момент at:
          {"type":"start_activity","activity":"Тренировка","at":"14:00"?,"day":"today"?,"place":"зал"?,"category":"sport"?,"parallel":false?}
          parallel: true — если новое занятие идёт одновременно с текущим («ем за работой»).
        - stop_activity — закончил текущее занятие: {"type":"stop_activity","at":"15:00"?,"day":"today"?}
        - describe_period — что было за период, по порядку:
          {"type":"describe_period","day":"yesterday"?,"from":"19:00"?,"to":"23:00"?,"part_of_day":"evening"?,
           "items":[{"activity":"Тренировка","category":"sport"?,"minutes":60?},
                    {"activity":"Дорога","route":{"from":"зал","to":"дом"}},
                    {"activity":"YouTube","rest":true}]}
          У пункта ровно один способ задать длительность или ни одного: minutes (сказано «час», «минут 20»),
          start+end («с 19 до 20»), route (дорога между местами — длительность знает бот), rest:true («остальное
          время», «потом весь вечер» — только у одного пункта). parallel:true — пункт шёл одновременно с другим
          («ел и смотрел ютуб» → основной YouTube, параллельно Еда).
          part_of_day — morning, afternoon, evening, night, если сказано «утром», «днём», «вечером», «ночью» без часов.
        - answer — ответ на последний вопрос бота: {"type":"answer","value":"25"}
        - classify — к какой категории относится занятие: {"type":"classify","activity":"Dota 2","category":"wasted.multiplayer"}
          when — morning, afternoon, evening или night, только если правило названо для части суток («по вечерам»,
          «утром», «ночью»): {"type":"classify","activity":"YouTube","category":"rest","when":"evening"}.
          Это правило на будущее, а не запись: «вечером смотрел ютуб» — describe_period, а не classify.
        - set_fact — сколько обычно длится: {"type":"set_fact","activity":"Тренировка","minutes":60}
          или маршрут: {"type":"set_fact","route":{"from":"зал","to":"дом"},"minutes":25}
        - quiet — не беспокоить: {"type":"quiet","minutes":120} или {"type":"quiet","until":"18:00"}
        - edit_segment — поправка уже записанного:
          {"type":"edit_segment","target":{"day":"today"?,"at":"15:00"?,"activity":"Тренировка"?},
           "changes":{"start":"14:30"?,"end":"15:30"?,"activity":"Учёба"?,"category":"study"?}}
        - unclear — непонятно, что имелось в виду: {"type":"unclear","question":"Что переспросить, по-русски"}

        Категории (поле category, только эти ключи; если не уверен — не указывай):
        {{{{CategoryList}}}}

        ## Примеры
        «тренировался час, потом поехал домой, дома смотрел ютуб» →
        {"intents":[{"type":"describe_period","items":[{"activity":"Тренировка","category":"sport","minutes":60},{"activity":"Дорога","category":"commute","route":{"from":"зал","to":"дом"}},{"activity":"YouTube","rest":true}]}]}
        «вчера с 19 до 21 был в зале» →
        {"intents":[{"type":"describe_period","day":"yesterday","from":"19:00","to":"21:00","items":[{"activity":"Тренировка","category":"sport","rest":true}]}]}
        «вечером смотрел сериал часа два, потом читал» →
        {"intents":[{"type":"describe_period","part_of_day":"evening","items":[{"activity":"Сериал","category":"rest","minutes":120},{"activity":"Чтение","rest":true}]}]}
        «работаю» → {"intents":[{"type":"start_activity","activity":"Работа","category":"work"}]}
        «обедаю за компом, работаю дальше» → {"intents":[{"type":"start_activity","activity":"Обед","category":"household","parallel":true}]}
        «закончил в 15:30» → {"intents":[{"type":"stop_activity","at":"15:30"}]}
        «не беспокой пару часов» → {"intents":[{"type":"quiet","minutes":120}]}
        «ютуб по вечерам — это отдых» → {"intents":[{"type":"classify","activity":"YouTube","category":"rest","when":"evening"}]}
        «тренировка была до 15:30, а не до 15» →
        {"intents":[{"type":"edit_segment","target":{"activity":"Тренировка"},"changes":{"end":"15:30"}}]}

        """;
}
