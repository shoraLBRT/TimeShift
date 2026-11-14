using TimeShift.Application.Conversations;
using TimeShift.Domain.Categories;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Questions;

/// <summary>Что бот хочет выяснить (spec §5.5). Сейчас — обычная длительность занятия; #11 добавит категории.</summary>
public enum QuestionKind
{
    ActivityDuration,

    /// <summary>Категория занятия, которую не вывести из названия (YouTube, игры, неочевидное) — spec §4.2.</summary>
    ActivityCategory,
}

/// <summary>Вопрос в очереди. Задаётся один раз; <see cref="AskedAtUtc"/> — уже задан, <see cref="ResolvedAtUtc"/> — закрыт.</summary>
public sealed class PendingQuestion
{
    public PendingQuestion(Guid userId, QuestionKind kind, Guid subjectId, DateTime createdAtUtc)
    {
        UserId = userId;
        Kind = kind;
        SubjectId = subjectId;
        CreatedAtUtc = createdAtUtc;
    }

    // Для EF Core.
    private PendingQuestion()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public QuestionKind Kind { get; private set; }

    /// <summary>О чём вопрос: для <see cref="QuestionKind.ActivityDuration"/> — Id занятия.</summary>
    public Guid SubjectId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? AskedAtUtc { get; private set; }

    public DateTime? ResolvedAtUtc { get; private set; }

    public void MarkAsked(DateTime atUtc) => AskedAtUtc = atUtc;

    public void Resolve(DateTime atUtc) => ResolvedAtUtc = atUtc;
}

/// <summary>Очередь вопросов пользователя.</summary>
public interface IQuestionRepository
{
    /// <summary>Вопрос о том же (вид + предмет) — открытый или уже заданный когда-либо; чтобы не спрашивать дважды.</summary>
    Task<PendingQuestion?> FindAsync(Guid userId, QuestionKind kind, Guid subjectId, CancellationToken cancellationToken);

    Task<PendingQuestion?> GetAsync(Guid userId, Guid questionId, CancellationToken cancellationToken);

    /// <summary>Заданный и ещё не закрытый вопрос (не больше одного).</summary>
    Task<PendingQuestion?> OpenAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Самый старый незаданный.</summary>
    Task<PendingQuestion?> NextAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PendingQuestion question);
}

/// <summary>
/// Очередь уточняющих вопросов (spec §5.5): бот выясняет неизвестное по одному, сразу после ответа пользователя,
/// а не отдельным пингом, и спрашивает о каждом один раз. Ответ — кнопкой или числом («полчаса», «90»).
/// </summary>
public sealed partial class QuestionService(
    IQuestionRepository questions,
    TrackingService tracking,
    Abstractions.IActivityRepository activities,
    Abstractions.IFactRepository facts)
{
    private static readonly int[] QuickMinutes = [30, 60, 90, 120];

    /// <summary>
    /// После ответа бота: завести вопросы о том, чего не знаем про текущее занятие, и задать следующий, если сейчас
    /// ни один не задан. Возвращает вопрос (добавить в конец ответа) или <c>null</c>.
    /// </summary>
    public async Task<BotReply?> NextAsync(User user, DateTime now, CancellationToken ct)
    {
        // Сначала категория (от неё статистика), потом длительность (от неё пинги и продление).
        var current = await tracking.CurrentAsync(user, ct);
        if (current?.Activity is { CategoryKey: null } uncategorized
            && await questions.FindAsync(user.Id, QuestionKind.ActivityCategory, uncategorized.Id, ct) is null)
        {
            questions.Add(new PendingQuestion(user.Id, QuestionKind.ActivityCategory, uncategorized.Id, now));
        }

        if (current?.Activity is { TypicalDuration: null } activity
            && await questions.FindAsync(user.Id, QuestionKind.ActivityDuration, activity.Id, ct) is null)
        {
            questions.Add(new PendingQuestion(user.Id, QuestionKind.ActivityDuration, activity.Id, now.AddTicks(1)));
        }

        if (await questions.OpenAsync(user.Id, ct) is not null || await questions.NextAsync(user.Id, ct) is not { } next)
        {
            return null;
        }

        var reply = await AskAsync(user, next, ct);
        if (reply is null)
        {
            next.Resolve(now);
            return null;
        }

        next.MarkAsked(now);
        return reply;
    }

    /// <summary>Ответ текстом на заданный вопрос. <c>null</c> — не похоже на ответ, сообщение идёт дальше как обычно.</summary>
    public async Task<BotReply?> AnswerAsync(User user, string text, DateTime now, CancellationToken ct)
    {
        if (await questions.OpenAsync(user.Id, ct) is not { } open)
        {
            return null;
        }

        if (open.Kind == QuestionKind.ActivityCategory)
        {
            return CategoryByName(text) is { } key ? await ResolveCategoryAsync(user, open, key, now, ct) : null;
        }

        return DurationAnswer().IsMatch(text.Trim().ToLowerInvariant()) && Llm.IntentApplier.ParseMinutes(text) is { } minutes
            ? await ResolveAsync(user, open, minutes, now, ct)
            : null;
    }

    /// <summary>Кнопки вопроса: <c>q:&lt;id&gt;:&lt;минуты&gt;</c>, <c>q:&lt;id&gt;:skip</c>.</summary>
    public async Task<BotReply?> OnButtonAsync(User user, string data, DateTime now, CancellationToken ct)
    {
        var parts = data.Split(':');
        if (parts is not ["q", var id, var value] || !Guid.TryParse(id, out var questionId))
        {
            return null;
        }

        if (await questions.GetAsync(user.Id, questionId, ct) is not { ResolvedAtUtc: null } question)
        {
            return new BotReply("Этот вопрос уже закрыт.", [], ReplacesSource: true);
        }

        if (value == "skip")
        {
            question.Resolve(now);
            return new BotReply("Хорошо, не буду спрашивать. Поменять можно в /facts.", [], ReplacesSource: true);
        }

        if (question.Kind == QuestionKind.ActivityCategory)
        {
            return DefaultCategories.Find(value) is null
                ? null
                : (await ResolveCategoryAsync(user, question, value, now, ct)) with { ReplacesSource = true };
        }

        return int.TryParse(value, out var minutes) && minutes > 0
            ? (await ResolveAsync(user, question, minutes, now, ct)) with { ReplacesSource = true }
            : null;
    }

    /// <summary>Спросить заново о длительности занятия (кнопка в /facts).</summary>
    public async Task<BotReply?> AskDurationAgainAsync(User user, Guid activityId, DateTime now, CancellationToken ct)
    {
        var question = await questions.FindAsync(user.Id, QuestionKind.ActivityDuration, activityId, ct);
        if (question is null || question.ResolvedAtUtc is not null)
        {
            question = new PendingQuestion(user.Id, QuestionKind.ActivityDuration, activityId, now);
            questions.Add(question);
        }

        var reply = await AskAsync(user, question, ct);
        question.MarkAsked(now);
        return reply;
    }

    /// <summary>/facts (spec §5.7): что бот знает — длительности и категории занятий, маршруты.</summary>
    public async Task<BotReply> FactsAsync(User user, CancellationToken ct)
    {
        var known = await activities.ListAsync(user.Id, ct);
        var routes = (await facts.GetAsync(user.Id, ct))
            .Select(f => (Route: Domain.Facts.UserFact.ParseRouteKey(f.Key), f.Value))
            .Where(f => f.Route is not null)
            .ToList();

        var lines = new List<string> { "Что я знаю:" };
        if (known.Count == 0 && routes.Count == 0)
        {
            lines.Add("пока ничего — запомню, когда расскажешь.");
        }

        foreach (var a in known.OrderBy(a => a.Name, StringComparer.CurrentCulture))
        {
            var duration = a.TypicalDuration is { } d ? TrackingReplies.Duration(d) : "длительность ?";
            var category = a.CategoryKey is { } k ? Domain.Categories.DefaultCategories.Find(k)?.Name ?? k : "категория ?";
            lines.Add($"• {a.Name} — {duration}, {category}");
        }

        foreach (var (route, minutes) in routes)
        {
            lines.Add($"• Дорога {route!.Value.From} → {route.Value.To} — {minutes} мин");
        }

        var buttons = known
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .Take(10)
            .Select(a => new ReplyButton($"⏱ {a.Name}", $"fdur:{a.Id}"))
            .Chunk(2)
            .Select(c => (IReadOnlyList<ReplyButton>)c)
            .ToList();
        lines.Add(string.Empty);
        lines.Add("Поменять длительность — кнопкой; маршрут или категорию — просто напиши («до работы минут 40», «дота — залипание»).");
        return new BotReply(string.Join('\n', lines), buttons);
    }

    private async Task<BotReply> ResolveAsync(User user, PendingQuestion question, int minutes, DateTime now, CancellationToken ct)
    {
        question.Resolve(now);
        var activity = await tracking.GetActivityAsync(user, question.SubjectId, ct);
        if (activity is null)
        {
            return BotReply.Plain("Это занятие уже удалено.");
        }

        activity.TypicalDuration = TimeSpan.FromMinutes(minutes);
        return BotReply.Plain(
            $"Запомнил: «{activity.Name}» обычно {TrackingReplies.Duration(activity.TypicalDuration.Value)}. " +
            "Столько буду считать его в норме и не стану пинговать раньше.");
    }

    /// <summary>Сам текст — длительность: «40», «40 минут», «около часа», «полтора часа», «2 ч».</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(около |примерно |где-то |~)?((\d+([.,]\d+)?\s*(мин\w*|м|час\w*|ч)?)|(мин\w*\s+\d+)|(пол\s?часа|полтора( часа)?|час\w*))\.?$")]
    private static partial System.Text.RegularExpressions.Regex DurationAnswer();

    private async Task<BotReply> ResolveCategoryAsync(User user, PendingQuestion question, string key, DateTime now, CancellationToken ct)
    {
        question.Resolve(now);
        var activity = await tracking.GetActivityAsync(user, question.SubjectId, ct);
        if (activity is null)
        {
            return BotReply.Plain("Это занятие уже удалено.");
        }

        activity.CategoryKey = key;
        return BotReply.Plain($"Запомнил: «{activity.Name}» — {DefaultCategories.Find(key)!.Name}.");
    }

    /// <summary>Ответ текстом на вопрос о категории: «учёба», «отдых», «залипание».</summary>
    private static string? CategoryByName(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace('ё', 'е').TrimEnd('.', '!');
        if (t.Length < 3)
        {
            return null;
        }

        return DefaultCategories.All
            .Where(c => c.ParentKey is null)
            .FirstOrDefault(c => c.Name.ToLowerInvariant().Replace('ё', 'е').StartsWith(t[..Math.Min(4, t.Length)], StringComparison.Ordinal)
                                 || c.Name.ToLowerInvariant().Replace('ё', 'е').Contains(t, StringComparison.Ordinal))?.Key;
    }

    private static IReadOnlyList<(string Label, string Key)> CategoryOptions(string activity) =>
        Classifier.IsYouTube(activity)
            ? [("Отдых", DefaultCategories.MindfulRest), ("Учёба", DefaultCategories.Study), ("Залипание", DefaultCategories.WastedYouTube)]
            : Classifier.IsGame(activity)
                ? [("Синглплеер — отдых", DefaultCategories.MindfulRest), ("Мультиплеер — залипание", DefaultCategories.WastedMultiplayer)]
                : DefaultCategories.All.Where(c => c.ParentKey is null).Select(c => (c.Name, c.Key)).ToList();

    private async Task<BotReply?> AskAsync(User user, PendingQuestion question, CancellationToken ct)
    {
        var activity = await tracking.GetActivityAsync(user, question.SubjectId, ct);
        if (activity is null)
        {
            return null;
        }

        if (question.Kind == QuestionKind.ActivityCategory)
        {
            var options = CategoryOptions(activity.Name);
            var rows = options.Select(o => new ReplyButton(o.Label, $"q:{question.Id}:{o.Key}"))
                .Chunk(options.Count <= 3 ? 3 : 2)
                .Select(c => (IReadOnlyList<ReplyButton>)c)
                .ToList();
            rows.Add([new ReplyButton("Пропустить", $"q:{question.Id}:skip")]);
            var hint = Classifier.IsYouTube(activity.Name)
                ? "YouTube бывает разным — каким он был сейчас? Запомню для этого занятия."
                : Classifier.IsGame(activity.Name)
                    ? "Это одиночная игра или сетевая? Запомню для этой игры."
                    : "К чему отнести?";
            return new BotReply($"❓ «{activity.Name}» — {hint}", rows);
        }

        var buttons = QuickMinutes
            .Select(m => new ReplyButton(TrackingReplies.Duration(TimeSpan.FromMinutes(m)), $"q:{question.Id}:{m}"))
            .ToList();
        return new BotReply(
            $"❓ Сколько обычно длится «{activity.Name}»? Нажми или напиши, например «40 минут».",
            [buttons, [new ReplyButton("Не знаю, по-разному", $"q:{question.Id}:skip")]]);
    }
}
