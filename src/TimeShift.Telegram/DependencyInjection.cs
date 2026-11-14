using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace TimeShift.Telegram;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует адаптер Telegram: клиент и фоновый long polling. Без токена адаптер выключен.
    /// Требует зарегистрированный <c>ConversationService</c> (scoped).
    /// </summary>
    public static IServiceCollection AddTelegramAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TelegramOptions.SectionName);
        var options = new TelegramOptions
        {
            BotToken = section[nameof(TelegramOptions.BotToken)],
            Proxy = section[nameof(TelegramOptions.Proxy)],
            AllowedChatIds = section.GetSection(nameof(TelegramOptions.AllowedChatIds)).GetChildren()
                .Select(c => long.Parse(c.Value!, System.Globalization.CultureInfo.InvariantCulture))
                .ToList(),
        };
        services.AddSingleton(options);

        if (options.IsEnabled)
        {
            services.AddSingleton<ITelegramBotClient>(_ =>
            {
                var handler = new SocketsHttpHandler();
                if (!string.IsNullOrWhiteSpace(options.Proxy))
                {
                    handler.Proxy = new WebProxy(options.Proxy);
                    handler.UseProxy = true;
                }

                return new TelegramBotClient(options.BotToken!, new HttpClient(handler));
            });
            services.AddSingleton<TimeShift.Application.Abstractions.INotifier, TelegramNotifier>();
            services.AddHostedService<TelegramPollingService>();
        }

        return services;
    }
}
