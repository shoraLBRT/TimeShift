namespace TimeShift.Domain.Timelines;

/// <summary>Непустой полуинтервал <c>[StartUtc, EndUtc)</c> в UTC.</summary>
public readonly record struct TimeRange
{
    public TimeRange(DateTime startUtc, DateTime endUtc)
    {
        Utc.Ensure(startUtc, nameof(startUtc));
        Utc.Ensure(endUtc, nameof(endUtc));
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("Конец интервала должен быть позже начала.", nameof(endUtc));
        }

        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public DateTime StartUtc { get; }

    public DateTime EndUtc { get; }

    public TimeSpan Duration => EndUtc - StartUtc;

    public bool Contains(DateTime utc) => StartUtc <= utc && utc < EndUtc;

    public override string ToString() => $"[{StartUtc:yyyy-MM-dd HH:mm}, {EndUtc:yyyy-MM-dd HH:mm}) UTC";
}
