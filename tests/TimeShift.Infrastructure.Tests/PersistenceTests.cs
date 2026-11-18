using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;
using TimeShift.Infrastructure.Persistence;
using TimeShift.TestSupport;

namespace TimeShift.Infrastructure.Tests;

/// <summary>
/// Настоящая база через миграции — SQLite, а с <see cref="TestDatabase.PostgresVariable"/> Postgres: маппинг
/// агрегата, отрезков, журнала /undo и UTC при чтении. Каждый шаг — в своём scope, как одно сообщение в боте.
/// </summary>
public sealed class PersistenceTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private ServiceProvider _services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Utc(int hour, int minute = 0) => new(2026, 11, 14, hour, minute, 0, DateTimeKind.Utc);

    public async ValueTask InitializeAsync()
    {
        _database = TestDatabase.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = _database.Provider,
                ["ConnectionStrings:Default"] = _database.ConnectionString,
            })
            .Build();
        _services = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TimeShiftDbContext>();
        Assert.Equal(TestDatabase.IsPostgres, db.Database.IsNpgsql());
        await db.Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
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

            // Заданный в этой единице работы уже не «следующий», а «открытый» — хотя в базе он ещё не задан (#65).
            Assert.Null(await repo.NextAsync(userId, Ct));
            Assert.Same(next, await repo.OpenAsync(userId, Ct));
        });

        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<Application.Questions.IQuestionRepository>();
            Assert.Null(await repo.NextAsync(userId, Ct));
            var open = await repo.OpenAsync(userId, Ct);
            Assert.Equal(Utc(10), open!.AskedAtUtc);
            open.Resolve(Utc(11));

            // Закрытый в этой единице работы больше не «открытый» — следующий вопрос можно задать этим же ответом.
            Assert.Null(await repo.OpenAsync(userId, Ct));
            var later = new Application.Questions.PendingQuestion(userId, Application.Questions.QuestionKind.ActivityCategory, subject, Utc(11));
            repo.Add(later);
            Assert.Same(later, await repo.NextAsync(userId, Ct));
            repo.Remove(later);
            Assert.Null(await repo.NextAsync(userId, Ct));
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

    [Fact]
    public async Task A_category_answer_rewrites_the_running_segment_and_undo_restores_it()
    {
        var userId = await CreateUserAsync();
        var guitar = await CreateActivityAsync(userId, "Гитара");
        await StartAsync(userId, guitar, Utc(9));
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<ITimelineRepository>();
            var timeline = await repo.LoadAsync(userId, Utc(0), Ct);
            repo.Record(userId, timeline.Continue(Utc(9, 30)), Utc(9, 30));
        });

        // Ответ о категории (#62): категория занятия и — тем же сохранением — идущей записи; прожитое время при ней.
        var changed = await InScopeAsync(async sp =>
        {
            var user = (await sp.GetRequiredService<IUserRepository>().FindByIdAsync(userId, Ct))!;
            var activity = (await sp.GetRequiredService<IActivityRepository>().GetAsync(userId, guitar.Id, Ct))!;
            activity.CategoryKey = DefaultCategories.Study;
            return await Tracking(sp).CategorizeCurrentAsync(user, activity, Utc(9, 40), Ct);
        });
        Assert.Equal(DefaultCategories.Study, changed!.CategoryKey);

        var loaded = await LoadAsync(userId);
        Assert.Equal(["09:00-09:30 Гитара", "09:30-… Гитара"], loaded.Segments.Select(Describe));
        Assert.Equal([null, DefaultCategories.Study], loaded.Segments.Select(s => s.CategoryKey));
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.OpenSegment!.ElapsedBefore);

        // У записи категория уже есть — второй раз менять нечего.
        Assert.Null(await InScopeAsync(async sp =>
        {
            var user = (await sp.GetRequiredService<IUserRepository>().FindByIdAsync(userId, Ct))!;
            var activity = (await sp.GetRequiredService<IActivityRepository>().GetAsync(userId, guitar.Id, Ct))!;
            return await Tracking(sp).CategorizeCurrentAsync(user, activity, Utc(9, 45), Ct);
        }));

        // /undo: категория снята с записи, у занятия осталась.
        Assert.Equal(UndoResult.Undone, await InScopeAsync(async sp =>
        {
            var user = (await sp.GetRequiredService<IUserRepository>().FindByIdAsync(userId, Ct))!;
            return await Tracking(sp).UndoAsync(user, Ct);
        }));
        loaded = await LoadAsync(userId);
        Assert.Equal(["09:00-09:30 Гитара", "09:30-… Гитара"], loaded.Segments.Select(Describe));
        Assert.Null(loaded.OpenSegment!.CategoryKey);
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.OpenSegment.ElapsedBefore);
        var kept = await InScopeAsync(sp => sp.GetRequiredService<IActivityRepository>().GetAsync(userId, guitar.Id, Ct));
        Assert.Equal(DefaultCategories.Study, kept!.CategoryKey);
    }

    [Fact]
    public async Task Reads_see_what_the_same_unit_of_work_has_just_recorded()
    {
        var userId = await CreateUserAsync();
        var work = await CreateActivityAsync(userId, "Работа", "work");
        await StartAsync(userId, work, Utc(9));

        // Одно сообщение — одна единица работы: сохранение в самом конце (#65), а читают ленту и до него.
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<ITimelineRepository>();
            var tracking = Tracking(sp);
            var user = (await sp.GetRequiredService<IUserRepository>().FindByIdAsync(userId, Ct))!;
            var before = (await repo.FindOpenAsync(userId, Ct))!;

            // Новое занятие (карточки ещё нет в базе) начато, но не сохранено.
            var guitar = await tracking.ResolveActivityAsync(user, "Гитара", Ct);
            var started = await tracking.StartAsync(user, guitar, Utc(10), Ct);

            var open = await repo.FindOpenAsync(userId, Ct);
            Assert.Equal(started.Id, open!.Id);
            var timeline = await repo.LoadAsync(userId, Utc(0), Ct);
            Assert.Equal(["09:00-10:00 Работа", "10:00-… Гитара"], timeline.Segments.Select(Describe));
            Assert.Null(await repo.FindSegmentAsync(userId, before.Id, Ct));
            Assert.Equal(started.Id, (await repo.FindSegmentAsync(userId, started.Id, Ct))!.Id);

            var current = await tracking.CurrentAsync(user, Ct);
            Assert.Equal("Гитара", current!.Segment.Title);
            Assert.Same(guitar, current.Activity);
            Assert.Contains(guitar, await sp.GetRequiredService<IActivityRepository>().ListAsync(userId, Ct));

            // Вторая запись того же сообщения строится на первой, а не на состоянии из базы.
            var lunch = await tracking.ResolveActivityAsync(user, "Обед", Ct);
            await tracking.StartAsync(user, lunch, Utc(11), Ct);
            timeline = await repo.LoadAsync(userId, Utc(0), Ct);
            Assert.Equal(["09:00-10:00 Работа", "10:00-11:00 Гитара", "11:00-… Обед"], timeline.Segments.Select(Describe));

            // Откат несохранённого изменения — тоже виден сразу, и откатывается именно последнее.
            Assert.Equal(UndoResult.Undone, await tracking.UndoAsync(user, Ct));
            timeline = await repo.LoadAsync(userId, Utc(0), Ct);
            Assert.Equal(["09:00-10:00 Работа", "10:00-… Гитара"], timeline.Segments.Select(Describe));

            // И ещё одно: возвращается отрезок, который уже лежит в базе и в этой единице работы был убран.
            Assert.Equal(UndoResult.Undone, await tracking.UndoAsync(user, Ct));
            Assert.Equal(before.Id, (await repo.FindOpenAsync(userId, Ct))!.Id);

            await tracking.StartAsync(user, guitar, Utc(10), Ct);
        });

        // После сохранения в базе то же, что видели чтения до него.
        var loaded = await LoadAsync(userId);
        Assert.Equal(["09:00-10:00 Работа", "10:00-… Гитара"], loaded.Segments.Select(Describe));
    }

    [Fact]
    public async Task Changes_of_one_unit_of_work_are_undone_in_reverse_order()
    {
        var userId = await CreateUserAsync();
        string[] names = ["Работа", "Обед", "Гитара", "Прогулка", "Чтение", "Ужин"];
        var activities = new List<Activity>();
        foreach (var name in names)
        {
            activities.Add(await CreateActivityAsync(userId, name));
        }

        // Шесть изменений ленты одним сохранением и с одним временем журнала — как намерения одного сообщения (#65).
        // Каждое записано поверх ленты, собранной в памяти: порядок отката не должен зависеть от чтений до сохранения.
        var at = Utc(15);
        await InScopeAsync(sp =>
        {
            var repo = sp.GetRequiredService<ITimelineRepository>();
            var timeline = new Timeline([]);
            for (var i = 0; i < activities.Count; i++)
            {
                var start = Utc(9 + i);
                var change = timeline.Apply(
                    [new Segment(start, null, activities[i].Name, SegmentStatus.Confirmed, SegmentSource.User, activities[i].Id)], start);
                repo.Record(userId, change, at);
            }

            return Task.CompletedTask;
        });
        Assert.Equal("Ужин", (await LoadAsync(userId)).OpenSegment!.Title);

        // /undo — каждое в своём сообщении: открытым становится предыдущее занятие, и так до пустой ленты.
        for (var i = names.Length - 2; i >= 0; i--)
        {
            Assert.Equal(UndoResult.Undone, await UndoAsync(userId));
            var loaded = await LoadAsync(userId);
            Assert.Equal(names[i], loaded.OpenSegment!.Title);
            Assert.Equal(i + 1, loaded.Segments.Count);
        }

        Assert.Equal(UndoResult.Undone, await UndoAsync(userId));
        Assert.Empty((await LoadAsync(userId)).Segments);
        Assert.Equal(UndoResult.NothingToUndo, await UndoAsync(userId));
    }

    [Fact]
    public async Task Changes_recorded_at_the_same_instant_by_different_units_of_work_are_undone_in_reverse_order()
    {
        var userId = await CreateUserAsync();
        string[] names = ["Работа", "Обед", "Гитара", "Прогулка", "Чтение", "Ужин"];
        var at = Utc(15);
        for (var i = 0; i < names.Length; i++)
        {
            // Часы между сообщениями могут и не сдвинуться (тесты с остановленными часами, два сообщения подряд).
            var activity = await CreateActivityAsync(userId, names[i]);
            await InScopeAsync(async sp =>
            {
                var repo = sp.GetRequiredService<ITimelineRepository>();
                var timeline = await repo.LoadAsync(userId, Utc(0), Ct);
                var start = Utc(9 + i);
                repo.Record(
                    userId,
                    timeline.Apply([new Segment(start, null, activity.Name, SegmentStatus.Confirmed, SegmentSource.User, activity.Id)], start),
                    at);
            });
        }

        for (var i = names.Length - 2; i >= 0; i--)
        {
            Assert.Equal(UndoResult.Undone, await UndoAsync(userId));
            Assert.Equal(names[i], (await LoadAsync(userId)).OpenSegment!.Title);
        }
    }

    private Task<UndoResult> UndoAsync(Guid userId) => InScopeAsync(async sp =>
    {
        var user = (await sp.GetRequiredService<IUserRepository>().FindByIdAsync(userId, Ct))!;
        return await Tracking(sp).UndoAsync(user, Ct);
    });

    private static TrackingService Tracking(IServiceProvider sp) => new(
        sp.GetRequiredService<ITimelineRepository>(), sp.GetRequiredService<IActivityRepository>(), sp.GetRequiredService<IFactRepository>());
}
