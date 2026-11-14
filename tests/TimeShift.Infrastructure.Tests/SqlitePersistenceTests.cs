using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;
using TimeShift.Infrastructure.Persistence;

namespace TimeShift.Infrastructure.Tests;

/// <summary>
/// Настоящий SQLite в памяти через миграции: маппинг агрегата, отрезков, журнала /undo и UTC при чтении.
/// Каждый шаг — в своём scope, как одно сообщение в боте.
/// </summary>
public sealed class SqlitePersistenceTests : IAsyncLifetime
{
    private readonly string _connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
    private SqliteConnection _keepAlive = null!;
    private ServiceProvider _services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int hour, int minute = 0) => new(2026, 11, 14, hour, minute, 0, DateTimeKind.Utc);

    public async ValueTask InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync(Ct);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:Default"] = _connectionString,
            })
            .Build();
        _services = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TimeShiftDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await action(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        return result;
    }

    private Task InScopeAsync(Func<IServiceProvider, Task> action) =>
        InScopeAsync(async sp =>
        {
            await action(sp);
            return true;
        });

    private Task<Guid> CreateUserAsync() => InScopeAsync(sp =>
    {
        var user = new User(1, "Тест", User.DefaultTimeZone, Utc(8));
        user.TrySetSleep((new TimeOnly(23, 0), new TimeOnly(7, 0)), (new TimeOnly(1, 0), new TimeOnly(10, 0)));
        user.TryAddBlock(new ScheduleBlock(
            ScheduleBlockKind.Fixed, "Английский", null, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], new TimeOnly(11, 0), new TimeOnly(12, 0)));
        user.Settings.Extension = ExtensionPolicy.Infinite;
        user.RegisterActivity(Utc(8, 30));
        user.CompleteOnboarding();
        sp.GetRequiredService<IUserRepository>().Add(user);
        return Task.FromResult(user.Id);
    });

    private Task<Activity> CreateActivityAsync(Guid userId, string name, string? category = null) => InScopeAsync(sp =>
    {
        var activity = new Activity(userId, name, category);
        sp.GetRequiredService<IActivityRepository>().Add(activity);
        return Task.FromResult(activity);
    });

    private Task StartAsync(Guid userId, Activity activity, DateTime at) => InScopeAsync(async sp =>
    {
        var repo = sp.GetRequiredService<ITimelineRepository>();
        var timeline = await repo.LoadAsync(userId, at.AddDays(-2), Ct);
        var change = timeline.Apply(
            [new Segment(at, null, activity.Name, SegmentStatus.Confirmed, SegmentSource.User, activity.Id, activity.CategoryKey)], at);
        repo.Record(userId, change, at);
    });

    private Task<Timeline> LoadAsync(Guid userId) =>
        InScopeAsync(sp => sp.GetRequiredService<ITimelineRepository>().LoadAsync(userId, Utc(0), Ct));

    private static string Describe(Segment s) =>
        $"{s.StartUtc:HH:mm}-{(s.EndUtc is { } e ? e.ToString("HH:mm") : "…")} {s.Title}";

    [Fact]
    public async Task Facts_pending_periods_and_message_log_round_trip()
    {
        var userId = await CreateUserAsync();

        // Факт: добавить, затем заменить в другом scope.
        await InScopeAsync(sp => sp.GetRequiredService<IFactRepository>().SetAsync(userId, "route:зал→дом", 20, Ct));
        await InScopeAsync(sp => sp.GetRequiredService<IFactRepository>().SetAsync(userId, "route:зал→дом", 25, Ct));
        var facts = await InScopeAsync(sp => sp.GetRequiredService<IFactRepository>().GetAsync(userId, Ct));
        Assert.Equal(25, facts["route:зал→дом"]);

        // Отложенный период: задать, заменить в той же единице работы, прочитать, удалить.
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IPendingPeriodRepository>();
            await repo.SetAsync(new Application.Llm.PendingPeriod(userId, Utc(9), "первый", "{}", 0, "route:зал→дом", "Сколько?"), Ct);
            await repo.SetAsync(new Application.Llm.PendingPeriod(userId, Utc(10), "второй", "{}", 0, null, null), Ct);
        });
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IPendingPeriodRepository>();
            var pending = await repo.GetAsync(userId, Ct);
            Assert.Equal("второй", pending!.Text);
            Assert.False(pending.IsWaitingForFact);
            Assert.Equal(DateTimeKind.Utc, pending.CreatedAtUtc.Kind);

            // Удалить и сразу положить новый — тоже в одной единице работы.
            await repo.RemoveAsync(userId, Ct);
            Assert.Null(await repo.GetAsync(userId, Ct));
            await repo.SetAsync(new Application.Llm.PendingPeriod(userId, Utc(11), "третий", "{}", 0, null, null), Ct);
        });
        var last = await InScopeAsync(sp => sp.GetRequiredService<IPendingPeriodRepository>().GetAsync(userId, Ct));
        Assert.Equal("третий", last!.Text);

        await InScopeAsync(sp =>
        {
            sp.GetRequiredService<IMessageLog>().Add(new Application.Llm.MessageLogEntry
            {
                UserId = userId, AtUtc = Utc(12), Text = "работаю", Status = "Ok", Response = "{}", PromptTokens = 10, CompletionTokens = 2,
            });
            return Task.CompletedTask;
        });
        var logged = await InScopeAsync(sp => sp.GetRequiredService<TimeShiftDbContext>().Messages.CountAsync(Ct));
        Assert.Equal(1, logged);
    }

    [Fact]
    public async Task Question_queue_round_trips()
    {
        var userId = await CreateUserAsync();
        var subject = Guid.NewGuid();
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<Application.Questions.IQuestionRepository>();
            repo.Add(new Application.Questions.PendingQuestion(userId, Application.Questions.QuestionKind.ActivityDuration, subject, Utc(9)));

            // Добавленный в этой же единице работы уже виден.
            Assert.NotNull(await repo.NextAsync(userId, Ct));
        });

        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<Application.Questions.IQuestionRepository>();
            var next = await repo.NextAsync(userId, Ct);
            next!.MarkAsked(Utc(10));
        });

        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<Application.Questions.IQuestionRepository>();
            Assert.Null(await repo.NextAsync(userId, Ct));
            var open = await repo.OpenAsync(userId, Ct);
            Assert.Equal(Utc(10), open!.AskedAtUtc);
            open.Resolve(Utc(11));
        });

        var found = await InScopeAsync(sp => sp.GetRequiredService<Application.Questions.IQuestionRepository>()
            .FindAsync(userId, Application.Questions.QuestionKind.ActivityDuration, subject, Ct));
        Assert.Equal(Utc(11), found!.ResolvedAtUtc);
        Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<Application.Questions.IQuestionRepository>().OpenAsync(userId, Ct)));
    }

    [Fact]
    public async Task User_aggregate_round_trips_with_blocks_settings_and_utc_times()
    {
        await CreateUserAsync();

        var user = await InScopeAsync(sp => sp.GetRequiredService<IUserRepository>().FindByTelegramChatIdAsync(1, Ct));

        Assert.NotNull(user);
        Assert.True(user.IsOnboarded);
        Assert.Equal(ExtensionPolicy.Infinite, user.Settings.Extension);
        Assert.Equal(DateTimeKind.Utc, user.LastSeenAtUtc!.Value.Kind);
        Assert.Equal(Utc(8, 30), user.LastSeenAtUtc);
        Assert.Equal(3, user.Blocks.Count);
        var arabic = Assert.Single(user.Blocks, b => b.Kind == ScheduleBlockKind.Fixed);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], arabic.Days.Order());
        Assert.Equal(3, user.Template().Blocks.Count);
    }

    [Fact]
    public async Task Segments_journal_undo_and_recent_activities()
    {
        var userId = await CreateUserAsync();
        var work = await CreateActivityAsync(userId, "Работа", "work");
        var lunch = await CreateActivityAsync(userId, "Обед");

        await StartAsync(userId, work, Utc(9));
        await StartAsync(userId, lunch, Utc(12));
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<ITimelineRepository>();
            var timeline = await repo.LoadAsync(userId, Utc(0), Ct);
            repo.Record(userId, timeline.Continue(Utc(12, 30)), Utc(12, 30));
        });

        var loaded = await LoadAsync(userId);
        Assert.Equal(["09:00-12:00 Работа", "12:00-12:30 Обед", "12:30-… Обед"], loaded.Segments.Select(Describe));
        Assert.All(loaded.Segments, s => Assert.Equal(DateTimeKind.Utc, s.StartUtc.Kind));
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.OpenSegment!.ElapsedBefore);

        var recent = await InScopeAsync(sp => sp.GetRequiredService<IActivityRepository>().RecentAsync(userId, 5, Ct));
        Assert.Equal(["Обед", "Работа"], recent.Select(a => a.Name));
        var found = await InScopeAsync(sp => sp.GetRequiredService<IActivityRepository>().FindByNameAsync(userId, "РАБОТА", Ct));
        Assert.Equal(work.Id, found!.Id);

        // /undo дважды: «продолжаю», затем «обед» — работа снова открыта.
        for (var i = 0; i < 2; i++)
        {
            await InScopeAsync(async sp =>
            {
                var repo = sp.GetRequiredService<ITimelineRepository>();
                var last = await repo.LastChangeAsync(userId, Ct);
                var timeline = await repo.LoadAsync(userId, Utc(0), Ct);
                timeline.Undo(last!.Change);
                await repo.RevertAsync(userId, last, Ct);
            });
        }

        var afterUndo = await LoadAsync(userId);
        var open = Assert.Single(afterUndo.Segments);
        Assert.True(open.IsOpen);
        Assert.Equal("Работа", open.Title);
    }
}
