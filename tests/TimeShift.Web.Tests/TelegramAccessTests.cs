using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TimeShift.Application.Abstractions;
using TimeShift.Telegram;

namespace TimeShift.Web.Tests;

/// <summary>Кому отвечает Telegram-бот (#49): по умолчанию закрыт, <c>Telegram:AllowAnyone</c> открывает.</summary>
public sealed class TelegramAccessTests
{
    private const long Stranger = 424242;

    private const long Owner = 777;

    [Fact]
    public async Task Empty_list_without_the_flag_refuses_a_chat_and_creates_no_user()
    {
        await using var app = new TestApp();
        var bot = new FakeBotClient();

        await HandleAsync(app, bot, new TelegramOptions { BotToken = "x" }, Stranger);

        Assert.Null(await FindUserAsync(app, Stranger));
        var reply = Assert.Single(bot.Requests.OfType<SendMessageRequest>());
        Assert.Equal(TelegramPollingService.RejectedText, reply.Text);
    }

    [Fact]
    public async Task A_chat_outside_the_list_is_refused_and_a_listed_one_is_served()
    {
        await using var app = new TestApp();
        var bot = new FakeBotClient();
        var options = new TelegramOptions { BotToken = "x", AllowedChatIds = [Owner] };

        await HandleAsync(app, bot, options, Stranger);
        await HandleAsync(app, bot, options, Owner);

        Assert.Null(await FindUserAsync(app, Stranger));
        Assert.NotNull(await FindUserAsync(app, Owner));
    }

    [Fact]
    public async Task Allow_anyone_serves_any_chat()
    {
        await using var app = new TestApp();
        var bot = new FakeBotClient();

        await HandleAsync(app, bot, new TelegramOptions { BotToken = "x", AllowAnyone = true }, Stranger);

        Assert.NotNull(await FindUserAsync(app, Stranger));
        Assert.DoesNotContain(bot.Requests.OfType<SendMessageRequest>(), r => r.Text == TelegramPollingService.RejectedText);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void The_flag_is_read_from_configuration(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:AllowAnyone"] = value })
            .Build();

        var options = new ServiceCollection().AddTelegramAdapter(configuration).BuildServiceProvider()
            .GetRequiredService<TelegramOptions>();

        Assert.Equal(expected, options.AllowAnyone);
        Assert.Equal(!expected, options.IsClosedToEveryone);
    }

    [Fact]
    public void An_empty_chat_id_from_compose_is_skipped()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:AllowedChatIds:0"] = string.Empty,
                ["Telegram:AllowedChatIds:1"] = "777",
            })
            .Build();

        var options = new ServiceCollection().AddTelegramAdapter(configuration).BuildServiceProvider()
            .GetRequiredService<TelegramOptions>();

        Assert.Equal([777L], options.AllowedChatIds);
    }

    private static async Task HandleAsync(TestApp app, FakeBotClient bot, TelegramOptions options, long chatId)
    {
        var service = new TelegramPollingService(
            bot, app.Services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<TelegramPollingService>.Instance);
        var update = new Update
        {
            Message = new Message
            {
                Chat = new Chat { Id = chatId, Type = ChatType.Private, FirstName = "Аня" },
                From = new User { Id = chatId, FirstName = "Аня" },
                Text = "/start",
                Date = DateTime.UtcNow,
            },
        };

        await service.HandleUpdateAsync(bot, update, TestContext.Current.CancellationToken);
    }

    private static async Task<TimeShift.Domain.Users.User?> FindUserAsync(TestApp app, long chatId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindByTelegramChatIdAsync(chatId, TestContext.Current.CancellationToken);
    }

    /// <summary>Вместо api.telegram.org: запоминает запросы, отвечает пустыми объектами.</summary>
    private sealed class FakeBotClient : ITelegramBotClient
    {
        public List<object> Requests { get; } = [];

        public bool LocalBotServer => false;

        public long BotId => 1;

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }

        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var response = typeof(TResponse).GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance<TResponse>()
                : default!;
            return Task.FromResult(response);
        }

        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
