using System.Globalization;
using System.Text.RegularExpressions;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Parsing;

/// <summary>Разобранный блок шаблона: «Работа пн-пт 9-18».</summary>
public sealed record ParsedBlock(string Title, IReadOnlyList<DayOfWeek> Days, TimeOnly Start, TimeOnly End);

/// <summary>
/// Детерминированный разбор расписания из текста — до появления LLM (шаг 5) и как запасной путь после.
/// Понимает «9-18», «9:30–15», «с 9 до 18», дни «пн-пт», «пн, ср, пт», «вт чт», «по субботам», «будни»,
/// «выходные», «каждый день».
/// </summary>
public static partial class ScheduleText
{
    private static readonly (string Prefix, DayOfWeek Day)[] DayPrefixes =
    [
        ("пон", DayOfWeek.Monday), ("вто", DayOfWeek.Tuesday), ("сре", DayOfWeek.Wednesday),
        ("чет", DayOfWeek.Thursday), ("пят", DayOfWeek.Friday), ("суб", DayOfWeek.Saturday),
        ("вос", DayOfWeek.Sunday),
    ];

    private static readonly Dictionary<string, DayOfWeek> DayAbbreviations = new()
    {
        ["пн"] = DayOfWeek.Monday, ["вт"] = DayOfWeek.Tuesday, ["ср"] = DayOfWeek.Wednesday,
        ["чт"] = DayOfWeek.Thursday, ["пт"] = DayOfWeek.Friday, ["сб"] = DayOfWeek.Saturday,
        ["вс"] = DayOfWeek.Sunday,
    };

    private static readonly string[] Abbreviations = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];

    private static readonly HashSet<string> StopWords = ["по", "в", "во", "и", "каждую", "каждое", "у", "меня"];

    /// <summary>Первый диапазон времени внутри текста: что до него и что после (для «вчера 19-21 Зал»).</summary>
    public static bool TryFindTimeRange(string text, out TimeOnly start, out TimeOnly end, out string before, out string after)
    {
        start = end = default;
        before = after = string.Empty;
        var lowered = text.ToLowerInvariant().Replace('–', '-').Replace('—', '-');
        var match = TimeRangeRegex().Match(lowered);
        if (!match.Success
            || !TryTime(match.Groups["h1"].Value, match.Groups["m1"].Value, out start)
            || !TryTime(match.Groups["h2"].Value, match.Groups["m2"].Value, out end)
            || start == end)
        {
            return false;
        }

        before = text[..match.Index].Trim(' ', ',', '.', ':');
        after = text[(match.Index + match.Length)..].Trim(' ', ',', '.', ':', '-');
        return true;
    }

    /// <summary>Диапазон времени «9-18», «23:30–7:30», «с 9 до 18» (весь текст — только диапазон).</summary>
    public static bool TryParseTimeRange(string text, out TimeOnly start, out TimeOnly end)
    {
        start = end = default;
        var normalized = Normalize(text);
        var match = TimeRangeRegex().Match(normalized);
        if (!match.Success || match.Value.Trim().Length != normalized.Trim().Length)
        {
            return false;
        }

        return TryTime(match.Groups["h1"].Value, match.Groups["m1"].Value, out start)
            && TryTime(match.Groups["h2"].Value, match.Groups["m2"].Value, out end)
            && start != end;
    }

    /// <summary>Блок с названием, днями и временем. При неудаче — <paramref name="error"/> для пользователя.</summary>
    public static bool TryParseBlock(string text, out ParsedBlock? block, out string? error)
    {
        block = null;
        var normalized = Normalize(text);

        var timeMatch = TimeRangeRegex().Match(normalized);
        if (!timeMatch.Success
            || !TryTime(timeMatch.Groups["h1"].Value, timeMatch.Groups["m1"].Value, out var start)
            || !TryTime(timeMatch.Groups["h2"].Value, timeMatch.Groups["m2"].Value, out var end)
            || start == end)
        {
            error = "Не нашёл время. Напиши, например, «9-18» или «с 9:30 до 15».";
            return false;
        }

        var rest = normalized.Remove(timeMatch.Index, timeMatch.Length);
        var days = new HashSet<DayOfWeek>();
        var titleWords = new List<string>();
        foreach (var word in rest.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryDays(word, out var parsed))
            {
                days.UnionWith(parsed);
            }
            else if (!StopWords.Contains(word))
            {
                titleWords.Add(word);
            }
        }

        // «каждый день», «ежедневно», «всю неделю».
        if (titleWords.Remove("каждый") | titleWords.Remove("ежедневно") | titleWords.Remove("неделю") | titleWords.Remove("всю"))
        {
            titleWords.Remove("день");
            days.UnionWith(DaysOfWeek.All);
        }

        if (days.Count == 0)
        {
            error = "Не понял, в какие дни. Напиши, например, «пн-пт», «вт, чт» или «каждый день».";
            return false;
        }

        if (titleWords.Count == 0)
        {
            error = "Как назвать этот блок? Напиши название, например «Работа пн-пт 9-18».";
            return false;
        }

        var title = string.Join(' ', titleWords);
        title = char.ToUpper(title[0], CultureInfo.GetCultureInfo("ru-RU")) + title[1..];
        block = new ParsedBlock(title, Sort(days), start, end);
        error = null;
        return true;
    }

    /// <summary>Категория по названию блока, если она очевидна; иначе <c>null</c> (спросим позже).</summary>
    public static string? GuessCategory(string title)
    {
        var t = title.ToLowerInvariant();
        return t switch
        {
            _ when t.Contains("работ") || t.Contains("смена") || t.Contains("офис") => DefaultCategories.Work,
            _ when t.Contains("учёб") || t.Contains("учеб") || t.Contains("пары") || t.Contains("универ")
                   || t.Contains("школ") || t.Contains("курс") || t.Contains("лекци") => DefaultCategories.Study,
            _ when t.Contains("трениров") || t.Contains("спорт") || t.Contains("зал") || t.Contains("бег")
                   || t.Contains("йог") || t.Contains("бассейн") || t.Contains("футбол") => DefaultCategories.Sport,
            _ when t.Contains("дорог") || t.Contains("езд") => DefaultCategories.Commute,
            _ when t.Contains("проект") => DefaultCategories.Projects,
            _ => null,
        };
    }

    /// <summary>«каждый день», «пн–пт», «вс–чт», «сб, вс», «пн, ср, пт».</summary>
    public static string FormatDays(IEnumerable<DayOfWeek> days)
    {
        var indices = days.Select(MondayFirst).Distinct().Order().ToList();
        if (indices.Count == 7)
        {
            return "каждый день";
        }

        if (indices.Count >= 3)
        {
            // Непрерывная дуга на кольце недели: ровно одно место, где следующий день не входит в набор.
            var set = indices.ToHashSet();
            var starts = indices.Where(i => !set.Contains((i + 6) % 7)).ToList();
            if (starts.Count == 1)
            {
                var first = starts[0];
                var last = (first + indices.Count - 1) % 7;
                return $"{Abbreviations[first]}–{Abbreviations[last]}";
            }
        }

        return string.Join(", ", indices.Select(i => Abbreviations[i]));
    }

    public static string FormatTime(TimeOnly start, TimeOnly end) => $"{start:HH\\:mm}–{end:HH\\:mm}";

    public static string Format(ScheduleBlock block) =>
        $"{block.Title} · {FormatDays(block.Days)} · {FormatTime(block.Start, block.End)}";

    private static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;

    private static List<DayOfWeek> Sort(IEnumerable<DayOfWeek> days) => days.OrderBy(MondayFirst).ToList();

    private static bool TryDays(string word, out IReadOnlyList<DayOfWeek> days)
    {
        days = [];
        switch (word)
        {
            case "будни" or "будням" or "будний" or "будние":
                days = DaysOfWeek.Weekdays;
                return true;
            case "выходные" or "выходным" or "выходной":
                days = DaysOfWeek.Weekend;
                return true;
        }

        var parts = word.Split('-');
        if (parts.Length == 2 && TryDay(parts[0], out var from) && TryDay(parts[1], out var to))
        {
            var result = new List<DayOfWeek>();
            for (var i = MondayFirst(from); ; i = (i + 1) % 7)
            {
                result.Add((DayOfWeek)((i + 1) % 7));
                if (i == MondayFirst(to))
                {
                    break;
                }
            }

            days = result;
            return true;
        }

        if (parts.Length == 1 && TryDay(word, out var day))
        {
            days = [day];
            return true;
        }

        return false;
    }

    private static bool TryDay(string word, out DayOfWeek day)
    {
        if (DayAbbreviations.TryGetValue(word.TrimEnd('.'), out day))
        {
            return true;
        }

        foreach (var (prefix, d) in DayPrefixes)
        {
            if (word.StartsWith(prefix, StringComparison.Ordinal))
            {
                day = d;
                return true;
            }
        }

        return false;
    }

    private static bool TryTime(string hours, string minutes, out TimeOnly time)
    {
        time = default;
        var h = int.Parse(hours, CultureInfo.InvariantCulture);
        var m = minutes.Length == 0 ? 0 : int.Parse(minutes, CultureInfo.InvariantCulture);
        if (h == 24 && m == 0)
        {
            h = 0;
        }

        if (h is < 0 or > 23 || m is < 0 or > 59)
        {
            return false;
        }

        time = new TimeOnly(h, m);
        return true;
    }

    private static string Normalize(string text) =>
        text.ToLowerInvariant().Replace('–', '-').Replace('—', '-').Trim();

    [GeneratedRegex(@"(?:\bс\s+)?(?<h1>\d{1,2})(?:[:.](?<m1>\d{2}))?\s*(?:-|\bдо\b)\s*(?<h2>\d{1,2})(?:[:.](?<m2>\d{2}))?")]
    private static partial Regex TimeRangeRegex();
}
