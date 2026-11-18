using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Llm;

/// <summary>
/// Ссылка на день, как её сказал пользователь (spec §8): сегодня, вчера, позавчера, день недели или дата.
/// Превращает в дату код (<see cref="PastTime"/>), а не модель.
/// </summary>
public abstract record DayRef
{
    private DayRef()
    {
    }

    /// <summary>Сегодня минус <see cref="Days"/> дней: 0 — сегодня, 1 — вчера, 2 — позавчера.</summary>
    public sealed record DaysAgo(int Days) : DayRef;

    /// <summary>Последний прошедший такой день недели (сегодня тоже подходит).</summary>
    public sealed record Weekday(DayOfWeek Day) : DayRef;

    public sealed record Date(DateOnly Value) : DayRef;
}

/// <summary>Маршрут пункта периода: «дорога из зала домой».</summary>
public sealed record RouteRef(string From, string To);

/// <summary>Пункт описанного периода (spec §3.5). Ровно одно из: минуты, начало/конец, маршрут, «остальное» — или ничего.</summary>
public sealed record PeriodItemIntent(
    string Activity,
    string? Category = null,
    int? Minutes = null,
    TimeOnly? Start = null,
    TimeOnly? End = null,
    RouteRef? Route = null,
    bool Rest = false,
    bool Parallel = false);

/// <summary>Намерение пользователя, извлечённое LLM (spec §8). Модель ничего не считает — только извлекает.</summary>
public abstract record Intent;

/// <summary>«Начал тренировку», «в 14 пошёл в зал».</summary>
public sealed record StartActivityIntent(
    string Activity, DayRef? Day = null, TimeOnly? At = null, string? Place = null, string? Category = null, bool Parallel = false) : Intent;

/// <summary>«Закончил», «в 15 закончил».</summary>
public sealed record StopActivityIntent(DayRef? Day = null, TimeOnly? At = null) : Intent;

/// <summary>«Тренировался час, потом поехал домой, дома ютуб»; «вчера с 19 до 21 был в зале».</summary>
public sealed record DescribePeriodIntent(
    IReadOnlyList<PeriodItemIntent> Items, DayRef? Day = null, TimeOnly? From = null, TimeOnly? To = null, PartOfDay? PartOfDay = null)
    : Intent;

/// <summary>Ответ на последний вопрос бота: «да», «минут 25», «отдых».</summary>
public sealed record AnswerIntent(string Value) : Intent;

/// <summary>
/// «Дота — это залипание» — категория занятия; с <paramref name="When"/> — правило классификации на часть суток
/// («ютуб по вечерам — отдых», spec §4.2), категория самого занятия при этом не меняется.
/// </summary>
public sealed record ClassifyIntent(string Activity, string Category, PartOfDay? When = null) : Intent;

/// <summary>Факт: типичная длительность занятия или маршрута.</summary>
public sealed record SetFactIntent(int Minutes, string? Activity = null, RouteRef? Route = null) : Intent;

/// <summary>«Не беспокой 2 часа», «до 18».</summary>
public sealed record QuietIntent(int? Minutes = null, TimeOnly? Until = null) : Intent;

/// <summary>Какой отрезок править: по дню, времени внутри него и/или названию.</summary>
public sealed record SegmentTarget(DayRef? Day = null, TimeOnly? At = null, string? Activity = null);

/// <summary>«Тренировка была до 15:30», «этот ютуб был учёбой».</summary>
public sealed record EditSegmentIntent(
    SegmentTarget Target, TimeOnly? NewStart = null, TimeOnly? NewEnd = null, string? NewActivity = null, string? NewCategory = null)
    : Intent;

/// <summary>Модель не поняла — что переспросить.</summary>
public sealed record UnclearIntent(string Question) : Intent;
