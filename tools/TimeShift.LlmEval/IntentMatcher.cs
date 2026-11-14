using TimeShift.Application.Llm;

namespace TimeShift.LlmEval;

/// <summary>
/// Сравнение ответа модели с ожидаемым. Строго — то, от чего зависит лента: типы и порядок намерений, дни, время,
/// минуты, способ задать длительность, «параллельно». Мягко — названия: без регистра и «ё», варианты через «|»,
/// совпадение по началу слова (≥ 4 букв). Категория сверяется, только если она есть в ожидаемом.
/// </summary>
public static class IntentMatcher
{
    public static IReadOnlyList<string> Compare(IReadOnlyList<Intent> expected, IReadOnlyList<Intent> actual)
    {
        var problems = new List<string>();
        if (expected.Count != actual.Count)
        {
            problems.Add($"намерений {actual.Count}, ожидалось {expected.Count}: [{Types(actual)}] вместо [{Types(expected)}]");
            return problems;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            CompareOne(expected[i], actual[i], $"#{i}", problems);
        }

        return problems;
    }

    public static bool NameMatches(string expected, string actual)
    {
        var a = Normalize(actual);
        return expected.Split('|').Select(Normalize).Any(e =>
            e == a || (e.Length >= 4 && a.Length >= 4 && (a.StartsWith(e[..4], StringComparison.Ordinal) || a.Contains(e, StringComparison.Ordinal))));
    }

    private static void CompareOne(Intent expected, Intent actual, string at, List<string> problems)
    {
        if (expected.GetType() != actual.GetType())
        {
            problems.Add($"{at}: {TypeName(actual)} вместо {TypeName(expected)}");
            return;
        }

        void Same<T>(string field, T e, T a)
        {
            if (!EqualityComparer<T>.Default.Equals(e, a))
            {
                problems.Add($"{at}.{field}: «{a}» вместо «{e}»");
            }
        }

        void Name(string field, string? e, string? a)
        {
            if (e is null)
            {
                return;
            }

            if (a is null || !NameMatches(e, a))
            {
                problems.Add($"{at}.{field}: «{a}» вместо «{e}»");
            }
        }

        void Category(string? e, string? a)
        {
            if (e is not null && e != a)
            {
                problems.Add($"{at}.category: «{a}» вместо «{e}»");
            }
        }

        switch ((expected, actual))
        {
            case (StartActivityIntent e, StartActivityIntent a):
                Name("activity", e.Activity, a.Activity);
                Same("day", e.Day, a.Day);
                Same("at", e.At, a.At);
                Same("parallel", e.Parallel, a.Parallel);
                Category(e.Category, a.Category);
                break;

            case (StopActivityIntent e, StopActivityIntent a):
                Same("day", e.Day, a.Day);
                Same("at", e.At, a.At);
                break;

            case (DescribePeriodIntent e, DescribePeriodIntent a):
                Same("day", e.Day, a.Day);
                Same("from", e.From, a.From);
                Same("to", e.To, a.To);
                Same("part_of_day", e.PartOfDay, a.PartOfDay);
                if (e.Items.Count != a.Items.Count)
                {
                    problems.Add($"{at}.items: {a.Items.Count} пунктов вместо {e.Items.Count} " +
                                 $"([{string.Join(", ", a.Items.Select(x => x.Activity))}])");
                    break;
                }

                for (var j = 0; j < e.Items.Count; j++)
                {
                    var (ei, ai, p) = (e.Items[j], a.Items[j], $"items[{j}]");
                    Name($"{p}.activity", ei.Activity, ai.Activity);
                    Same($"{p}.minutes", ei.Minutes, ai.Minutes);
                    Same($"{p}.start", ei.Start, ai.Start);
                    Same($"{p}.end", ei.End, ai.End);
                    Same($"{p}.rest", ei.Rest, ai.Rest);
                    Same($"{p}.parallel", ei.Parallel, ai.Parallel);
                    Same($"{p}.route", ei.Route is null, ai.Route is null);
                    if (ei.Route is { } er && ai.Route is { } ar)
                    {
                        Name($"{p}.route.from", er.From, ar.From);
                        Name($"{p}.route.to", er.To, ar.To);
                    }

                    if (ei.Category is not null && ei.Category != ai.Category)
                    {
                        problems.Add($"{at}.{p}.category: «{ai.Category}» вместо «{ei.Category}»");
                    }
                }

                break;

            case (AnswerIntent e, AnswerIntent a):
                Name("value", e.Value, a.Value);
                break;

            case (ClassifyIntent e, ClassifyIntent a):
                Name("activity", e.Activity, a.Activity);
                Same("category", e.Category, a.Category);
                break;

            case (SetFactIntent e, SetFactIntent a):
                Same("minutes", e.Minutes, a.Minutes);
                Name("activity", e.Activity, a.Activity);
                Same("route", e.Route is null, a.Route is null);
                if (e.Route is { } fr && a.Route is { } far)
                {
                    Name("route.from", fr.From, far.From);
                    Name("route.to", fr.To, far.To);
                }

                break;

            case (QuietIntent e, QuietIntent a):
                Same("minutes", e.Minutes, a.Minutes);
                Same("until", e.Until, a.Until);
                break;

            case (EditSegmentIntent e, EditSegmentIntent a):
                Same("target.day", e.Target.Day, a.Target.Day);
                Same("target.at", e.Target.At, a.Target.At);
                Name("target.activity", e.Target.Activity, a.Target.Activity);
                Same("changes.start", e.NewStart, a.NewStart);
                Same("changes.end", e.NewEnd, a.NewEnd);
                Name("changes.activity", e.NewActivity, a.NewActivity);
                Same("changes.category", e.NewCategory, a.NewCategory);
                break;

            case (UnclearIntent, UnclearIntent):
                break;
        }
    }

    private static string Types(IEnumerable<Intent> intents) => string.Join(", ", intents.Select(TypeName));

    private static string TypeName(Intent intent) => intent.GetType().Name.Replace("Intent", string.Empty, StringComparison.Ordinal);

    private static string Normalize(string text) => text.Trim().ToLowerInvariant().Replace('ё', 'е');
}
