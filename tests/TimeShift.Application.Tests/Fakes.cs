using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
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
internal sealed class InMemoryStore : IUserRepository, ITimelineRepository, IActivityRepository, IUnitOfWork, INotifier
{
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

    public Task<IReadOnlyList<User>> ListOnboardedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<User>>(Users.Where(u => u.IsOnboarded).ToList());

    public void Add(User user) => Users.Add(user);

    // ITimelineRepository
    public Task<Timeline> LoadAsync(Guid userId, DateTime fromUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new Timeline(SegmentsOf(userId).Where(s => s.EndUtc is null || s.EndUtc > fromUtc)));

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

    void IActivityRepository.Add(Activity activity) => Activities.Add(activity);

    // IUnitOfWork
    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // INotifier
    public Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
    {
        var id = (++_messageId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Sent.Add((user.TelegramChatId, reply, id));
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
