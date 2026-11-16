namespace TimeShift.Domain.Activities;

/// <summary>
/// Занятие пользователя (spec §7: activities): название, категория и типичная длительность, если известна.
/// По типичной длительности считаются продление (spec §3.3) и первый пинг во время занятия (spec §5.2).
/// </summary>
public class Activity
{
    public Activity(Guid userId, string name, string? categoryKey = null, TimeSpan? typicalDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        UserId = userId;
        Name = name.Trim();
        CategoryKey = categoryKey;
        TypicalDuration = typicalDuration;
    }

    // Для EF Core.
    private Activity()
    {
        Name = string.Empty;
    }

    public const int MaxNameLength = 60;

    /// <summary>Верхняя граница типичной длительности, минут (сутки).</summary>
    public const int MaxTypicalMinutes = 1440;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Переименовывает занятие (прошлые отрезки ленты хранят прежнее название).</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>Для поиска без учёта регистра и «ё».</summary>
    public string NormalizedName
    {
        get => Normalize(Name);
        private set { }
    }

    public string? CategoryKey { get; set; }

    /// <summary><c>null</c> — неизвестна (бот спросит, spec §5.5).</summary>
    public TimeSpan? TypicalDuration
    {
        get;
        set
        {
            if (value is { } v && v <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Типичная длительность должна быть > 0.");
            }

            field = value;
        }
    }

    public static string Normalize(string name) =>
        name.Trim().ToLowerInvariant().Replace('ё', 'е');
}
