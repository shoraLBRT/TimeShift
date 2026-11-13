using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace TimeShift.Telegram;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует адаптер Telegram. Без токена адаптер выключен: клиент не регистрируется.
    /// Логики бота пока нет — это следующий шаг.
    /// </summary>
    public static IServiceCollection AddTelegramAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new TelegramOptions
        {
            BotToken = configuration[$"{TelegramOptions.SectionName}:{nameof(TelegramOptions.BotToken)}"],
        };
        services.AddSingleton(options);

        if (options.IsEnabled)
        {
            services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(options.BotToken!));
        }

        return services;
    }
}
