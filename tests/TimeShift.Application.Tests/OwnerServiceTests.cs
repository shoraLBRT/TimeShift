using TimeShift.Application.Clients;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

public class OwnerServiceTests
{
    private readonly InMemoryStore _store = new();

    private readonly TestClock _clock = new();

    private OwnerService Owners => new(_store, _store, _clock);

    private User Telegram(long chatId)
    {
        var user = new User(chatId, "Тест", User.DefaultTimeZone, _clock.UtcNow);
        _store.Users.Add(user);
        return user;
    }

    [Fact]
    public async Task Empty_database_gets_a_user_without_telegram()
    {
        var owner = await Owners.ResolveAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(owner.TelegramChatId);
        Assert.Equal(OwnerService.DefaultDisplayName, owner.DisplayName);
        Assert.Equal(User.DefaultTimeZone, owner.TimeZone);
        Assert.Same(owner, Assert.Single(_store.Users));
        Assert.Same(owner, await Owners.ResolveAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_only_user_is_the_owner()
    {
        var only = Telegram(1);

        Assert.Same(only, await Owners.ResolveAsync(null, TestContext.Current.CancellationToken));
        Assert.Single(_store.Users);
    }

    [Fact]
    public async Task Several_users_without_a_configured_chat_fall_back_to_the_user_without_telegram()
    {
        Telegram(1);
        Telegram(2);

        var owner = await Owners.ResolveAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(owner.TelegramChatId);
        Assert.Equal(3, _store.Users.Count);
        Assert.Same(owner, await Owners.ResolveAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Configured_chat_picks_that_user_or_creates_it()
    {
        var first = Telegram(1);
        Telegram(2);

        Assert.Same(first, await Owners.ResolveAsync(1, TestContext.Current.CancellationToken));
        var created = await Owners.ResolveAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(3, created.TelegramChatId);
        Assert.Equal(3, _store.Users.Count);
    }
}
