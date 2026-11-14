using System.Globalization;
using System.Text.Json;
using TimeShift.Domain.Categories;

namespace TimeShift.Application.Llm;

/// <summary>Итог разбора: намерения или список ошибок (их же отправляем модели при повторной попытке).</summary>
public sealed record IntentParseResult(IReadOnlyList<Intent> Intents, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && Intents.Count > 0;
}

/// <summary>
/// Строгая проверка ответа LLM по контракту <see cref="IntentPrompt"/> (spec §8). Неизвестный тип, нет обязательного
/// поля, неверный формат времени — ошибка. Неизвестная категория — не ошибка: категория просто отбрасывается
/// (спросим пользователя позже).
/// </summary>
public static class IntentParser
{
    private static readonly string[] WeekdayNames = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static IntentParseResult Parse(string json)
    {
        var errors = new List<string>();
        var intents = new List<Intent>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            return new IntentParseResult([], [$"Ответ — не JSON: {e.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("intents", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return new IntentParseResult([], ["Нужен объект {\"intents\": [...]}."]);
            }

            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                var reader = new Reader(element, $"intents[{index++}]", errors);
                if (ParseOne(reader) is { } intent)
                {
                    intents.Add(intent);
                }
            }

            if (index == 0)
            {
                errors.Add("Список intents пуст — если непонятно, верни unclear.");
            }
        }

        return new IntentParseResult(errors.Count == 0 ? intents : [], errors);
    }

    private static Intent? ParseOne(Reader r)
    {
        var type = r.RequiredString("type");
        switch (type)
        {
            case null:
                return null;
            case "start_activity":
                return r.RequiredString("activity") is { } started
                    ? new StartActivityIntent(started, r.Day("day"), r.Time("at"), r.String("place"), r.Category("category"), r.Bool("parallel"))
                    : null;
            case "stop_activity":
                return new StopActivityIntent(r.Day("day"), r.Time("at"));
            case "describe_period":
                return ParsePeriod(r);
            case "answer":
                return r.RequiredString("value") is { } value ? new AnswerIntent(value) : null;
            case "classify":
            {
                var classified = r.RequiredString("activity");
                var category = r.RequiredString("category");
                if (category is not null && DefaultCategories.Find(category) is null)
                {
                    r.Error("category", $"неизвестная категория «{category}»");
                    return null;
                }

                return classified is not null && category is not null ? new ClassifyIntent(classified, category) : null;
            }

            case "set_fact":
            {
                var minutes = r.Int("minutes", 1, 24 * 60, required: true);
                var factActivity = r.String("activity");
                var route = r.Route("route");
                if (factActivity is null == route is null)
                {
                    r.Error("activity", "нужно ровно одно: activity или route");
                    return null;
                }

                return minutes is { } m ? new SetFactIntent(m, factActivity, route) : null;
            }

            case "quiet":
            {
                var minutes = r.Int("minutes", 5, 24 * 60);
                var until = r.Time("until");
                if (minutes is null && until is null)
                {
                    r.Error("minutes", "нужно minutes или until");
                    return null;
                }

                return new QuietIntent(minutes, until);
            }

            case "edit_segment":
            {
                if (r.Object("target") is not { } target)
                {
                    r.Error("target", "обязательное поле");
                    return null;
                }

                var t = new SegmentTarget(target.Day("day"), target.Time("at"), target.String("activity"));
                if (t.At is null && t.Activity is null)
                {
                    target.Error("at", "нужно at или activity, чтобы найти отрезок");
                    return null;
                }

                if (r.Object("changes") is not { } changes)
                {
                    r.Error("changes", "обязательное поле");
                    return null;
                }

                var edit = new EditSegmentIntent(
                    t, changes.Time("start"), changes.Time("end"), changes.String("activity"), changes.Category("category"));
                if (edit is { NewStart: null, NewEnd: null, NewActivity: null, NewCategory: null })
                {
                    changes.Error("start", "нечего менять");
                    return null;
                }

                return edit;
            }

            case "unclear":
                return r.RequiredString("question") is { } question ? new UnclearIntent(question) : null;

            default:
                r.Error("type", $"неизвестный тип «{type}»");
                return null;
        }
    }

    private static DescribePeriodIntent? ParsePeriod(Reader r)
    {
        var items = new List<PeriodItemIntent>();
        var array = r.Items("items");
        if (array is null || array.Count == 0)
        {
            r.Error("items", "нужен хотя бы один пункт");
            return null;
        }

        var rest = 0;
        foreach (var item in array)
        {
            if (item.RequiredString("activity") is not { } activity)
            {
                continue;
            }

            var parsed = new PeriodItemIntent(
                activity,
                item.Category("category"),
                item.Int("minutes", 1, 24 * 60),
                item.Time("start"),
                item.Time("end"),
                item.Route("route"),
                item.Bool("rest"),
                item.Bool("parallel"));

            var kinds = (parsed.Minutes is null ? 0 : 1) + (parsed.Start is null && parsed.End is null ? 0 : 1)
                + (parsed.Rest ? 1 : 0);
            if (kinds > 1)
            {
                item.Error("minutes", "у пункта либо minutes, либо start/end, либо rest");
                continue;
            }

            if ((parsed.Start is null) != (parsed.End is null))
            {
                item.Error("start", "start и end задаются вместе");
                continue;
            }

            rest += parsed.Rest ? 1 : 0;
            items.Add(parsed);
        }

        if (rest > 1)
        {
            r.Error("items", "rest может быть только у одного пункта");
            return null;
        }

        return items.Count == array.Count
            ? new DescribePeriodIntent(items, r.Day("day"), r.Time("from"), r.Time("to"), r.PartOfDay("part_of_day"))
            : null;
    }

    /// <summary>Чтение полей одного объекта с накоплением ошибок по пути <c>intents[0].items[1].start</c>.</summary>
    private sealed class Reader(JsonElement element, string path, List<string> errors)
    {
        public void Error(string field, string message) => errors.Add($"{path}.{field}: {message}");

        public string? RequiredString(string name)
        {
            var value = String(name);
            if (value is null && !HasError(name))
            {
                Error(name, "обязательное поле");
            }

            return value;
        }

        public string? String(string name)
        {
            if (!TryGet(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                Error(name, "ожидается строка");
                return null;
            }

            var text = value.GetString()!.Trim();
            return text.Length == 0 ? null : text;
        }

        public bool Bool(string name)
        {
            if (!TryGet(name, out var value))
            {
                return false;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            Error(name, "ожидается true/false");
            return false;
        }

        public int? Int(string name, int min, int max, bool required = false)
        {
            if (!TryGet(name, out var value))
            {
                if (required)
                {
                    Error(name, "обязательное поле");
                }

                return null;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            {
                Error(name, $"целое от {min} до {max}");
                return null;
            }

            return number;
        }

        /// <summary>Местное время «HH:mm»; «24:00» — полночь.</summary>
        public TimeOnly? Time(string name)
        {
            if (String(name) is not { } text)
            {
                return null;
            }

            if (text == "24:00")
            {
                return TimeOnly.MinValue;
            }

            if (TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return time;
            }

            Error(name, $"время в формате HH:mm, а не «{text}»");
            return null;
        }

        public DayRef? Day(string name)
        {
            if (String(name) is not { } text)
            {
                return null;
            }

            switch (text.ToLowerInvariant())
            {
                case "today":
                    return new DayRef.DaysAgo(0);
                case "yesterday":
                    return new DayRef.DaysAgo(1);
                case "day_before_yesterday":
                    return new DayRef.DaysAgo(2);
            }

            var weekday = System.Array.IndexOf(WeekdayNames, text.ToLowerInvariant());
            if (weekday >= 0)
            {
                return new DayRef.Weekday((DayOfWeek)weekday);
            }

            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return new DayRef.Date(date);
            }

            Error(name, $"today, yesterday, day_before_yesterday, день недели по-английски или YYYY-MM-DD, а не «{text}»");
            return null;
        }

        public PartOfDay? PartOfDay(string name) => String(name)?.ToLowerInvariant() switch
        {
            null => null,
            "morning" => Llm.PartOfDay.Morning,
            "afternoon" => Llm.PartOfDay.Afternoon,
            "evening" => Llm.PartOfDay.Evening,
            "night" => Llm.PartOfDay.Night,
            var other => Invalid<PartOfDay>(name, $"morning, afternoon, evening или night, а не «{other}»"),
        };

        /// <summary>Ключ категории из стартового набора; неизвестная — отбрасывается без ошибки.</summary>
        public string? Category(string name) =>
            String(name) is { } key && DefaultCategories.Find(key) is not null ? key : null;

        public RouteRef? Route(string name)
        {
            if (Object(name) is not { } route)
            {
                return null;
            }

            var from = route.RequiredString("from");
            var to = route.RequiredString("to");
            return from is not null && to is not null ? new RouteRef(from, to) : null;
        }

        public Reader? Object(string name)
        {
            if (!TryGet(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                Error(name, "ожидается объект");
                return null;
            }

            return new Reader(value, $"{path}.{name}", errors);
        }

        public List<Reader>? Items(string name)
        {
            if (!TryGet(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                Error(name, "ожидается массив");
                return null;
            }

            return value.EnumerateArray().Select((e, i) => new Reader(e, $"{path}.{name}[{i}]", errors)).ToList();
        }

        private T? Invalid<T>(string name, string message)
            where T : struct
        {
            Error(name, message);
            return null;
        }

        private bool HasError(string name) => errors.Any(e => e.StartsWith($"{path}.{name}:", StringComparison.Ordinal));

        private bool TryGet(string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            {
                return true;
            }

            value = default;
            return false;
        }
    }
}
