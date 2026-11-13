namespace TimeShift.Application.Abstractions;

/// <summary>Источник текущего времени. Всё время — в UTC.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
