using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using TimeShift.Application.Llm;
using TimeShift.Infrastructure.Llm;
using TimeShift.LlmEval;

// Живой прогон тестового набора против LLM (spec §8, issue #5). Тратит деньги: ~40 запросов.
//   dotnet run --project tools/TimeShift.LlmEval -- [--only start-01,past-02] [--price-in 0.14 --price-out 0.28] [--record]
// Ключ и модель — как у бота: секция Llm (user-secrets Host-а, env Llm__ApiKey, Llm__Model, Llm__BaseUrl).

var options = ParseArgs(args);
var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(EvalCase).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();
var llmOptions = LlmOptions.From(configuration);
if (!llmOptions.IsEnabled)
{
    Console.Error.WriteLine("Llm:ApiKey не задан. Пример:");
    Console.Error.WriteLine("  dotnet user-secrets set \"Llm:ApiKey\" \"<ключ>\" --project src/TimeShift.Host");
    Console.Error.WriteLine("  dotnet user-secrets set \"Llm:BaseUrl\" \"https://api.proxyapi.ru/openai/v1\" --project src/TimeShift.Host");
    Console.Error.WriteLine("  dotnet user-secrets set \"Llm:Model\" \"deepseek-chat\" --project src/TimeShift.Host");
    return 2;
}

var set = EvalSet.Load(Path.Combine(AppContext.BaseDirectory, "cases.json"));
var cases = set.Cases.Where(c => options.Only.Count == 0 || options.Only.Contains(c.Id)).ToList();
var provider = new OpenAiCompatibleLlmProvider(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, llmOptions);
var extractor = new IntentExtractor(provider);

Console.WriteLine($"Модель {llmOptions.Model} @ {llmOptions.BaseUrl}, кейсов: {cases.Count}");
var (passed, promptTokens, completionTokens) = (0, 0L, 0L);
var latencies = new List<double>();
var recorded = new Dictionary<string, string>();
foreach (var evalCase in cases)
{
    var expected = IntentParser.Parse(evalCase.ExpectedJson);
    var watch = Stopwatch.StartNew();
    var result = await extractor.ExtractAsync(evalCase.Text, evalCase.Context(set.KnownActivities), CancellationToken.None);
    watch.Stop();
    latencies.Add(watch.Elapsed.TotalSeconds);
    promptTokens += result.PromptTokens;
    completionTokens += result.CompletionTokens;
    if (result.RawResponses.Count > 0)
    {
        recorded[evalCase.Id] = result.RawResponses[^1];
    }

    var problems = result.Status == ExtractionStatus.Ok
        ? IntentMatcher.Compare(expected.Intents, result.Intents)
        : [$"{result.Status}: {string.Join("; ", result.Errors)}"];
    var retried = result.RawResponses.Count > 1 ? " (со 2-й попытки)" : string.Empty;
    if (problems.Count == 0)
    {
        passed++;
        Console.WriteLine($"  ✓ {evalCase.Id}{retried}");
    }
    else
    {
        Console.WriteLine($"  ✗ {evalCase.Id}{retried}: «{evalCase.Text}»");
        foreach (var problem in problems)
        {
            Console.WriteLine($"      {problem}");
        }

        if (result.RawResponses.Count > 0)
        {
            Console.WriteLine($"      ответ: {result.RawResponses[^1]}");
        }
    }
}

var accuracy = cases.Count == 0 ? 0 : 100.0 * passed / cases.Count;
latencies.Sort();
Console.WriteLine();
Console.WriteLine($"Точность: {passed}/{cases.Count} = {accuracy:0.#}% (цель ≥ 90%)");
if (latencies.Count > 0)
{
    Console.WriteLine($"Задержка: медиана {latencies[latencies.Count / 2]:0.0} с, худшая {latencies[^1]:0.0} с");
}

Console.WriteLine($"Токены: вход {promptTokens}, выход {completionTokens}");
if (options.PriceIn is { } pin && options.PriceOut is { } pout && cases.Count > 0)
{
    var cost = (promptTokens * pin + completionTokens * pout) / 1_000_000;
    Console.WriteLine($"Цена прогона: ${cost:0.0000}; на 1000 сообщений ≈ ${cost / cases.Count * 1000:0.00}");
}

if (options.Record)
{
    var directory = Path.Combine(FindRepoTool(), "recorded");
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, $"{Sanitize(llmOptions.Model)}.json");
    new RecordedRun(llmOptions.Model, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), recorded).Save(file);
    Console.WriteLine($"Ответы записаны: {file}");
}

return accuracy >= 90 ? 0 : 1;

static (HashSet<string> Only, double? PriceIn, double? PriceOut, bool Record) ParseArgs(string[] args)
{
    var only = new HashSet<string>();
    double? priceIn = null, priceOut = null;
    var record = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--only":
                only.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries));
                break;
            case "--price-in":
                priceIn = double.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--price-out":
                priceOut = double.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--record":
                record = true;
                break;
        }
    }

    return (only, priceIn, priceOut, record);
}

static string FindRepoTool()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "tools", "TimeShift.LlmEval");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    return AppContext.BaseDirectory;
}

static string Sanitize(string model) =>
    string.Concat(model.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
