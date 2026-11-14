using TimeShift.Application.Llm;
using TimeShift.LlmEval;

namespace TimeShift.Infrastructure.Tests;

/// <summary>Тестовый набор фраз (#5) без сети: сам набор корректен, сравнение работает, записанные прогоны держат планку.</summary>
public class LlmEvalSetTests
{
    private static readonly EvalSet Set = EvalSet.Load(Path.Combine(AppContext.BaseDirectory, "cases.json"));

    [Fact]
    public void Set_has_30_to_50_unique_cases_with_valid_expectations()
    {
        Assert.InRange(Set.Cases.Count, 30, 50);
        Assert.Equal(Set.Cases.Count, Set.Cases.Select(c => c.Id).Distinct().Count());
        Assert.All(Set.Cases, c =>
        {
            var parsed = IntentParser.Parse(c.ExpectedJson);
            Assert.True(parsed.IsValid, $"{c.Id}: {string.Join("; ", parsed.Errors)}");
            Assert.Equal(DateTimeKind.Utc, c.NowUtc.Kind);
            _ = c.Context(Set.KnownActivities);
        });
    }

    [Fact]
    public void Set_covers_every_intent_type_and_past_entries()
    {
        var types = Set.Cases.SelectMany(c => IntentParser.Parse(c.ExpectedJson).Intents).Select(i => i.GetType()).ToHashSet();
        Assert.Equal(9, types.Count);
        Assert.True(Set.Cases.Count(c => c.Id.StartsWith("past-", StringComparison.Ordinal)) >= 6);
        Assert.True(Set.Cases.Count(c => c.Id.StartsWith("par-", StringComparison.Ordinal)) >= 3);
    }

    [Fact]
    public void Every_expected_period_resolves_to_a_window()
    {
        foreach (var c in Set.Cases)
        {
            foreach (var period in IntentParser.Parse(c.ExpectedJson).Intents.OfType<DescribePeriodIntent>())
            {
                var window = PastTime.ResolveWindow(period.Day, period.From, period.To, period.PartOfDay, c.NowUtc, c.Zone);
                Assert.True(window.Error is null, $"{c.Id}: {window.Error}");
            }
        }
    }

    [Fact]
    public void Expected_answers_match_themselves()
    {
        Assert.All(Set.Cases, c =>
        {
            var intents = IntentParser.Parse(c.ExpectedJson).Intents;
            Assert.Empty(IntentMatcher.Compare(intents, intents));
        });
    }

    [Fact]
    public void Matcher_is_strict_on_structure_and_loose_on_names()
    {
        static IReadOnlyList<Intent> P(string json) => IntentParser.Parse(json).Intents;
        var expected = P("""{"intents":[{"type":"describe_period","day":"yesterday","items":[{"activity":"Тренировка|Зал","minutes":60},{"activity":"YouTube","rest":true}]}]}""");

        Assert.Empty(IntentMatcher.Compare(expected, P(
            """{"intents":[{"type":"describe_period","day":"yesterday","items":[{"activity":"зал","minutes":60,"category":"sport"},{"activity":"Ютуб|YouTube видео","rest":true}]}]}""")));

        Assert.Contains(IntentMatcher.Compare(expected, P(
            """{"intents":[{"type":"describe_period","day":"yesterday","items":[{"activity":"Зал","minutes":90},{"activity":"YouTube","rest":true}]}]}""")),
            p => p.Contains("minutes", StringComparison.Ordinal));

        Assert.Contains(IntentMatcher.Compare(expected, P(
            """{"intents":[{"type":"describe_period","items":[{"activity":"Зал","minutes":60},{"activity":"YouTube","rest":true}]}]}""")),
            p => p.Contains("day", StringComparison.Ordinal));

        Assert.Contains(IntentMatcher.Compare(expected, P("""{"intents":[{"type":"start_activity","activity":"Зал"}]}""")),
            p => p.Contains("вместо", StringComparison.Ordinal));

        Assert.True(IntentMatcher.NameMatches("Dota 2|Дота", "дота 2"));
        Assert.True(IntentMatcher.NameMatches("Тренировка", "Тренировка в зале"));
        Assert.False(IntentMatcher.NameMatches("Работа", "Сериал"));
    }

    [Fact]
    public void Recorded_runs_parse_and_keep_the_bar()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "recorded");
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
        foreach (var file in files)
        {
            var run = RecordedRun.Load(file);
            var passed = 0;
            foreach (var c in Set.Cases)
            {
                if (run.Responses.TryGetValue(c.Id, out var response)
                    && IntentParser.Parse(response) is { IsValid: true } actual
                    && IntentMatcher.Compare(IntentParser.Parse(c.ExpectedJson).Intents, actual.Intents).Count == 0)
                {
                    passed++;
                }
            }

            Assert.True(passed >= Set.Cases.Count * 0.9, $"{run.Model}: {passed}/{Set.Cases.Count}");
        }
    }
}
