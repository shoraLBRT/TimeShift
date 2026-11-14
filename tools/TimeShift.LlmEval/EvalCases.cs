using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimeShift.Application.Llm;
using TimeShift.Domain.Schedule;

namespace TimeShift.LlmEval;

/// <summary>Открытое занятие в кейсе: название и с какого местного времени того же дня.</summary>
public sealed record EvalOpenActivity(string Activity, string Since);

/// <summary>
/// Кейс набора (spec §8): фраза, контекст и ожидаемый ответ в формате контракта. В названии занятия допустимы
/// варианты через «|» («Тренировка|Зал»): модель может назвать занятие любым из них.
/// </summary>
public sealed record EvalCase(
    string Id,
    string Text,
    string Now,
    JsonElement Expected,
    EvalOpenActivity? Open = null,
    string? Question = null,
    string? Note = null,
    string? TimeZone = null)
{
    public const string DefaultTimeZone = "Europe/Moscow";

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone ?? DefaultTimeZone);

    public DateTime NowUtc => LocalTime.ToUtc(DateTime.ParseExact(Now, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), Zone);

    public string ExpectedJson => Expected.GetRawText();

    public IntentContext Context(IReadOnlyList<string> knownActivities)
    {
        var now = NowUtc;
        (string, DateTime)? open = null;
        if (Open is { } o)
        {
            var date = DateTime.ParseExact(Now, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture).Date;
            var since = date + TimeOnly.ParseExact(o.Since, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
            open = (o.Activity, LocalTime.ToUtc(since, Zone));
        }

        return new IntentContext
        {
            NowUtc = now,
            TimeZone = Zone,
            OpenActivity = open,
            KnownActivities = knownActivities,
            LastBotQuestion = Question,
        };
    }
}

/// <summary>Файл набора: известные занятия (общий контекст) и кейсы.</summary>
public sealed record EvalSet(IReadOnlyList<string> KnownActivities, IReadOnlyList<EvalCase> Cases)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static EvalSet Load(string path) =>
        JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Пустой набор: {path}");
}

/// <summary>Записанные ответы модели — для офлайн-регрессии без сети (spec §8: тестовый набор).</summary>
public sealed record RecordedRun(string Model, string RecordedAt, IReadOnlyDictionary<string, string> Responses)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static RecordedRun Load(string path) =>
        JsonSerializer.Deserialize<RecordedRun>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Пустая запись: {path}");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
