using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Users;

/// <summary>Настройки пользователя (spec §6, §7: user_settings). Один к одному с <see cref="User"/>.</summary>
public class UserSettings
{
    public const int DefaultPingIntervalMin = 30;
    public const double DefaultNormFactor = 1.5;
    public const double DefaultMaxFactor = 2.0;
    public const int DefaultMaxUnansweredPings = 2;
    public const int DefaultEveningReviewOffsetMin = 30;
    public const int DefaultRoundingMin = 5;

    /// <summary>Допустимый интервал пингов, минут (spec §5.1, §6).</summary>
    public const int MinPingIntervalMin = 5;

    public const int MaxPingIntervalMin = 240;

    /// <summary>Интервал пингов в допустимых пределах?</summary>
    /// <summary>Сколько неотвеченных пингов подряд можно задать до паузы.</summary>
    public const int MinUnansweredPingsLimit = 1;

    public const int MaxUnansweredPingsLimit = 10;

    /// <summary>За сколько минут до сна начинать вечерний разбор.</summary>
    public const int MinEveningReviewOffsetMin = 0;

    public const int MaxEveningReviewOffsetMin = 180;

    /// <summary>Допустимое округление границ, минут.</summary>
    public static IReadOnlyList<int> RoundingOptionsMin { get; } = [1, 5, 10, 15];

    public static bool IsValidPingInterval(int minutes) => minutes is >= MinPingIntervalMin and <= MaxPingIntervalMin;

    public Guid UserId { get; private set; }

    public int PingIntervalMin { get; set; } = DefaultPingIntervalMin;

    /// <summary>Множитель нормального продления открытого занятия (spec §3.3); <c>null</c> — без ограничения (∞).</summary>
    public double? NormFactor { get; set; } = DefaultNormFactor;

    /// <summary>Множитель максимального продления; <c>null</c> — без ограничения (∞).</summary>
    public double? MaxFactor { get; set; } = DefaultMaxFactor;

    public int MaxUnansweredPings { get; set; } = DefaultMaxUnansweredPings;

    public bool PingDuringBlocks { get; set; }

    public int EveningReviewOffsetMin { get; set; } = DefaultEveningReviewOffsetMin;

    public int RoundingMin { get; set; } = DefaultRoundingMin;

    /// <summary>Режим продления как политика ленты; ∞ хранится как <c>null</c>.</summary>
    public ExtensionPolicy Extension
    {
        get
        {
            var norm = NormFactor ?? double.PositiveInfinity;
            var max = MaxFactor ?? double.PositiveInfinity;
            return (norm, max) switch
            {
                (1.0, 1.0) => ExtensionPolicy.UpToTypical,
                (DefaultNormFactor, DefaultMaxFactor) => ExtensionPolicy.Default,
                _ when double.IsPositiveInfinity(norm) => ExtensionPolicy.Infinite,
                _ => ExtensionPolicy.Custom(norm, max),
            };
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            NormFactor = double.IsPositiveInfinity(value.NormFactor) ? null : value.NormFactor;
            MaxFactor = double.IsPositiveInfinity(value.MaxFactor) ? null : value.MaxFactor;
        }
    }
}
