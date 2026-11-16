using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace TimeShift.Web;

/// <summary>
/// Настройки web-клиента (секция <c>Web</c>): <c>Web:AccessKey</c> — ключ, который владелец вводит один раз на устройстве
/// (env <c>Web__AccessKey</c>); пустой — web-клиент выключен. <c>Web:TelegramChatId</c> — чьи данные открывать
/// (тот же пользователь, что в Telegram); не задан — единственный пользователь или пользователь без Telegram.
/// </summary>
public sealed class WebOptions
{
    public string AccessKey { get; init; } = string.Empty;

    public long? TelegramChatId { get; init; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(AccessKey);

    public static WebOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Web");
        var chat = section["TelegramChatId"];
        long? chatId = null;
        if (!string.IsNullOrWhiteSpace(chat))
        {
            chatId = long.TryParse(chat, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"Web:TelegramChatId '{chat}' is not a number.");
        }

        return new WebOptions { AccessKey = section["AccessKey"]?.Trim() ?? string.Empty, TelegramChatId = chatId };
    }
}

/// <summary>Сведения о приложении для <c>/api/meta</c>.</summary>
public sealed record WebAppInfo(string Version);
