namespace TimeShift.Domain.Timelines;

/// <summary>Статус уверенности отрезка (spec §3.1).</summary>
public enum SegmentStatus
{
    /// <summary>✅ Пользователь сказал сам или подтвердил.</summary>
    Confirmed,

    /// <summary>⚪ Продление в пределах нормы, постоянный блок, сон по шаблону.</summary>
    Assumed,

    /// <summary>🟡 Превышение нормы, дневной фон.</summary>
    Questionable,
}
