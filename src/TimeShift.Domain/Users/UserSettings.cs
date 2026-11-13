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

    public Guid UserId { get; private set; }

    public int PingIntervalMin { get; set; } = DefaultPingIntervalMin;

    /// <summary>Множитель нормального продления открытого занятия (spec §3.3).</summary>
    public double NormFactor { get; set; } = DefaultNormFactor;

    /// <summary>Множитель максимального продления; <c>null</c> — без ограничения (∞).</summary>
    public double? MaxFactor { get; set; } = DefaultMaxFactor;

    public int MaxUnansweredPings { get; set; } = DefaultMaxUnansweredPings;

    public bool PingDuringBlocks { get; set; }

    public int EveningReviewOffsetMin { get; set; } = DefaultEveningReviewOffsetMin;

    public int RoundingMin { get; set; } = DefaultRoundingMin;
}
