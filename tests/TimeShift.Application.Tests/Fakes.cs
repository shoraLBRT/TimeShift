using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Application.Llm;
using TimeShift.Application.Questions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

/// <summary>Управляемые часы.</summary>
internal sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Всё хранилище в памяти: пользователи, лента с журналом, занятия, отправленные пинги.</summary>
/// <summary>LLM по сценарию: отдаёт ответы по очереди (последний повторяется). По умолчанию выключен.</summary>
internal sealed class ScriptedLlm(bool enabled = false) : ILlmProvider
{
    private readonly Queue<string> _queue = new();
    private string? _last;

    public List<LlmRequest> Requests { get; } = [];

    public bool IsEnabled => enabled;

    public void Enqueue(string response) => _queue.Enqueue(response);

    public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        _last = _queue.Count > 0 ? _queue.Dequeue() : _last ?? throw new LlmException("нет ответа", false);
        return Task.FromResult(new LlmResponse(_last, 100, 20));
    }
}

/// <summary>Распознавание по сценарию: отдаёт заданный текст; <see cref="Enabled"/> = false — выключено.</summary>
internal sealed class FakeSpeech : ISpeechToText
{
    public bool Enabled { get; set; } = true;

    public string Text { get; set; } = string.Empty;

    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public bool IsEnabled => Enabled;

    public TimeSpan MaxDuration => TimeSpan.FromMinutes(2);

    public Task<string> TranscribeAsync(Stream oggOpus, CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is not null ? throw Failure : Task.FromResult(Text);
    }
}

/// <summary>Рендер-заглушка: запоминает, что просили нарисовать, и отдаёт фиктивный PNG.</summary>
internal sealed class FakeRenderer : IDayRenderer
{
    public List<DayImage> Days { get; } = [];

    public List<WeekImage> Weeks { get; } = [];

    public byte[]? RenderDay(DayImage day)
    {
        Days.Add(day);
        return [0x89, 0x50];
    }

    public byte[]? RenderWeek(WeekImage week)
    {
        Weeks.Add(week);
        return [0x89, 0x50];
    }
}

internal sealed class InMemoryStore : IUserRepository, ITimelineRepository, IActivityRepository, IUnitOfWork, INotifier,
    IFactRepository, IPendingPeriodRepository, IMessageLog, IQuestionRepository
{
    public List<PendingQuestion> QuestionList { get; } = [];

    public QuestionService Questions(TrackingService tracking) => new(this, tracking, this, this);

    Task<PendingQuestion?> IQuestionRepository.FindAsync(Guid userId, QuestionKind kind, Guid subjectId, CancellationToken cancellationToken) =>
        Task.FromResult(QuestionList.FirstOrDefault(q => q.UserId == userId && q.Kind == kind && q.SubjectId == subjectId));

    Task<PendingQuestion?> IQuestionRepository.GetAsync(Guid userId, Guid questionId, CancellationToken cancellationToken) =>
        Task.FromResult(QuestionList.FirstOrDefault(q => q.UserId == userId && q.Id == questionId));

    Task<PendingQuestion?> IQuestionRepository.OpenAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(QuestionList.FirstOrDefault(q => q.UserId == userId && q.AskedAtUtc != null && q.ResolvedAtUtc == null));

    Task<PendingQuestion?> IQuestionRepository.NextAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(QuestionList.Where(q => q.UserId == userId && q.AskedAtUtc == null && q.ResolvedAtUtc == null).OrderBy(q => q.CreatedAtUtc).FirstOrDefault());

    void IQuestionRepository.Add(PendingQuestion question) => QuestionList.Add(question);

    Task<IReadOnlyList<Activity>> IActivityRepository.ListAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Activity>>(Activities.Where(a => a.UserId == userId).ToList());

    private readonly Dictionary<Guid, PendingPeriod> _pending = [];

    public FakeSpeech Speech { get; } = new();

    public FakeRenderer Renderer { get; } = new();

    public Dictionary<(Guid, string), int> Facts { get; } = [];

    public List<MessageLogEntry> Log { get; } = [];

    public PendingPeriod? PendingOf(Guid userId) => _pending.GetValueOrDefault(userId);

    public IntentApplier Applier(TrackingService tracking, ILlmProvider llm) =>
        new(llm, new IntentExtractor(llm), tracking, this, this, this, this);

    // IFactRepository
    Task<IReadOnlyDictionary<string, int>> IFactRepository.GetAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(Facts.Where(f => f.Key.Item1 == userId).ToDictionary(f => f.Key.Item2, f => f.Value));

    public Task SetAsync(Guid userId, string key, int minutes, CancellationToken cancellationToken)
    {
        Facts[(userId, key)] = minutes;
        return Task.CompletedTask;
    }

    // IPendingPeriodRepository
    Task<PendingPeriod?> IPendingPeriodRepository.GetAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_pending.GetValueOrDefault(userId));

    public Task SetAsync(PendingPeriod pending, CancellationToken cancellationToken)
    {
        _pending[pending.UserId] = pending;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        _pending.Remove(userId);
        return Task.CompletedTask;
    }

    // IMessageLog
    public void Add(MessageLogEntry entry) => Log.Add(entry);

    private readonly Dictionary<Guid, List<Segment>> _segments = [];
    private readonly Dictionary<Guid, List<StoredTimelineChange>> _journal = [];
    private int _messageId;

    public List<User> Users { get; } = [];

    public List<Activity> Activities { get; } = [];

    public List<(long ChatId, BotReply Reply, string Ref)> Sent { get; } = [];

    public List<string> Deleted { get; } = [];

    public IReadOnlyList<Segment> SegmentsOf(Guid userId) =>
        _segments.TryGetValue(userId, out var list) ? list.OrderBy(s => s.StartUtc).ToList() : [];

    // IUserRepository
    public Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.FirstOrDefault(u => u.TelegramChatId == chatId));

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

    public Task<User?> FindWithoutTelegramAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Users.FirstOrDefault(u => u.TelegramChatId is null));

    public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Users.Count);

    public Task<User?> FindFirstAsync(CancellationToken cancellationToken) => Task.FromResult(Users.FirstOrDefault());

    public Task<IReadOnlyList<User>> ListOnboardedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<User>>(Users.Where(u => u.IsOnboarded).ToList());

    public void Add(User user) => Users.Add(user);

    // ITimelineRepository
    public Task<Timeline> LoadAsync(Guid userId, DateTime fromUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new Timeline(SegmentsOf(userId).Where(s => s.EndUtc is null || s.EndUtc > fromUtc)));

    public Task<Segment?> FindSegmentAsync(Guid userId, Guid segmentId, CancellationToken cancellationToken) =>
        Task.FromResult(SegmentsOf(userId).FirstOrDefault(s => s.Id == segmentId));

    public Task<Segment?> FindOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(SegmentsOf(userId).FirstOrDefault(s => s.IsOpen && !s.IsParallel));

    public void Record(Guid userId, TimelineChange change, DateTime atUtc)
    {
        if (change.IsEmpty)
        {
            return;
        }

        var list = List(userId);
        list.RemoveAll(s => change.Before.Any(b => b.Id == s.Id));
        list.AddRange(change.After);
        Journal(userId).Add(new StoredTimelineChange(Guid.NewGuid(), atUtc, change));
    }

    public Task<StoredTimelineChange?> LastChangeAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Journal(userId).LastOrDefault());

    public Task RevertAsync(Guid userId, StoredTimelineChange change, CancellationToken cancellationToken)
    {
        var list = List(userId);
        list.RemoveAll(s => change.Change.After.Any(a => a.Id == s.Id));
        list.AddRange(change.Change.Before);
        Journal(userId).Remove(change);
        return Task.CompletedTask;
    }

    // IActivityRepository
    public Task<Activity?> GetAsync(Guid userId, Guid activityId, CancellationToken cancellationToken) =>
        Task.FromResult(Activities.FirstOrDefault(a => a.UserId == userId && a.Id == activityId));

    public Task<Activity?> FindByNameAsync(Guid userId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(Activities.FirstOrDefault(a => a.UserId == userId && a.NormalizedName == Activity.Normalize(name)));

    public Task<IReadOnlyList<Activity>> RecentAsync(Guid userId, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Activity>>(SegmentsOf(userId)
            .Where(s => s.ActivityId is not null)
            .OrderByDescending(s => s.StartUtc)
            .Select(s => s.ActivityId!.Value)
            .Distinct()
            .Take(count)
            .Select(id => Activities.Single(a => a.Id == id))
            .ToList());

    public Task<IReadOnlyDictionary<Guid, DateTime>> LastUsedAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DateTime>>(SegmentsOf(userId)
            .Where(s => s.ActivityId is not null)
            .GroupBy(s => s.ActivityId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(s => s.StartUtc)));

    void IActivityRepository.Add(Activity activity) => Activities.Add(activity);

    // IUnitOfWork
    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // INotifier
    public Task<bool> CanReachAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
    {
        var id = (++_messageId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Sent.Add((user.TelegramChatId ?? 0, reply, id));
        return Task.FromResult<string?>(id);
    }

    public Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken)
    {
        Deleted.Add(messageRef);
        return Task.CompletedTask;
    }

    private List<Segment> List(Guid userId) =>
        _segments.TryGetValue(userId, out var list) ? list : _segments[userId] = [];

    private List<StoredTimelineChange> Journal(Guid userId) =>
        _journal.TryGetValue(userId, out var list) ? list : _journal[userId] = [];
}
