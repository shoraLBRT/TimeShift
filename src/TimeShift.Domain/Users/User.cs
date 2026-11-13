namespace TimeShift.Domain.Users;

/// <summary>Пользователь бота (spec §7: users).</summary>
public class User
{
    public const string DefaultTimeZone = "Europe/Moscow";

    public User(long telegramChatId, string displayName, string timeZone, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        if (createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Время хранится только в UTC.", nameof(createdAtUtc));
        }

        TelegramChatId = telegramChatId;
        DisplayName = displayName;
        TimeZone = timeZone;
        CreatedAtUtc = createdAtUtc;
        Settings = new UserSettings();
    }

    // Для EF Core.
    private User()
    {
        DisplayName = string.Empty;
        TimeZone = DefaultTimeZone;
        Settings = null!;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public long TelegramChatId { get; private set; }

    public string DisplayName { get; set; }

    /// <summary>IANA-идентификатор часового пояса.</summary>
    public string TimeZone { get; set; }

    public DateTime CreatedAtUtc { get; private set; }

    public UserSettings Settings { get; private set; }
}
