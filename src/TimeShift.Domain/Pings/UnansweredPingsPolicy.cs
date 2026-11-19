namespace TimeShift.Domain.Pings;

/// <summary>Что делать, когда подряд набралось <c>max_unanswered</c> неотвеченных пингов (spec §5.2).</summary>
public enum UnansweredPingsPolicy
{
    /// <summary>Затухание: каждый следующий интервал вдвое длиннее (потолок — 4 ч); первый же ответ возвращает обычный.</summary>
    BackOff,

    /// <summary>Пауза до первого сообщения пользователя.</summary>
    Stop,
}
