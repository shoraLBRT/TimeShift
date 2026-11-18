namespace TimeShift.Domain.Timelines;

/// <summary>
/// Материализованный отрезок ленты <c>[StartUtc, EndUtc)</c> (spec §2, §7: segments).
/// <see cref="EndUtc"/> = <c>null</c> — открытое занятие. Неизменяемый: изменения — через новые экземпляры.
/// <see cref="IsParallel"/> — параллельное занятие («ел и смотрел YouTube»): лежит поверх основной ленты и не
/// вытесняет её (spec §3.6).
/// </summary>
public sealed record Segment
{
    public Segment(
        DateTime startUtc,
        DateTime? endUtc,
        string title,
        SegmentStatus status,
        SegmentSource source,
        Guid? activityId = null,
        string? categoryKey = null,
        TimeSpan elapsedBefore = default,
        bool isParallel = false,
        Guid? id = null)
    {
        Utc.Ensure(startUtc, nameof(startUtc));
        if (endUtc is { } end)
        {
            Utc.Ensure(end, nameof(endUtc));
            if (end <= startUtc)
            {
                throw new ArgumentException("Конец отрезка должен быть позже начала.", nameof(endUtc));
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (elapsedBefore < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedBefore), "Длительность не может быть отрицательной.");
        }

        if (elapsedBefore > TimeSpan.Zero && endUtc is not null)
        {
            throw new ArgumentException("Уже прожитое время задаётся только для открытого отрезка.", nameof(elapsedBefore));
        }

        if (isParallel && endUtc is null)
        {
            throw new ArgumentException("Параллельное занятие должно быть закрытым отрезком.", nameof(isParallel));
        }

        Id = id ?? Guid.NewGuid();
        StartUtc = startUtc;
        EndUtc = endUtc;
        Title = title;
        Status = status;
        Source = source;
        ActivityId = activityId;
        CategoryKey = categoryKey;
        ElapsedBefore = elapsedBefore;
        IsParallel = isParallel;
    }

    // Для EF Core.
    private Segment()
    {
        Title = string.Empty;
    }

    /// <summary>Идентичность строки в хранилище. Новые границы (<see cref="WithRange"/>) — новый отрезок, новый Id.</summary>
    public Guid Id { get; }

    public DateTime StartUtc { get; }

    /// <summary><c>null</c> — открытое занятие.</summary>
    public DateTime? EndUtc { get; }

    public string Title { get; }

    public SegmentStatus Status { get; }

    public SegmentSource Source { get; }

    public Guid? ActivityId { get; }

    /// <summary>Ключ категории (например, <c>sport</c>, <c>wasted.youtube</c>); <c>null</c> — не определена.</summary>
    public string? CategoryKey { get; }

    /// <summary>
    /// Для открытого отрезка, продолженного после перерыва: сколько этого же занятия уже было до перерыва.
    /// Учитывается в продлении — норма считается от суммарного времени, а не от последнего возобновления.
    /// </summary>
    public TimeSpan ElapsedBefore { get; }

    public bool IsParallel { get; }

    public bool IsOpen => EndUtc is null;

    /// <summary>Конец для расчёта пересечений: открытый отрезок тянется в бесконечность.</summary>
    internal DateTime EffectiveEndUtc => EndUtc ?? DateTime.MaxValue;

    /// <summary>Тот же отрезок с другими границами; при закрытии <see cref="ElapsedBefore"/> сбрасывается.</summary>
    public Segment WithRange(DateTime startUtc, DateTime? endUtc) =>
        new(startUtc, endUtc, Title, Status, Source, ActivityId, CategoryKey, endUtc is null ? ElapsedBefore : default, IsParallel);

    /// <summary>
    /// Тот же отрезок с другой категорией — новый отрезок с новым Id, как и <see cref="WithRange"/>; уже прожитое
    /// время открытого отрезка сохраняется.
    /// </summary>
    public Segment WithCategory(string? categoryKey) =>
        new(StartUtc, EndUtc, Title, Status, Source, ActivityId, categoryKey, ElapsedBefore, IsParallel);

    /// <summary>Открытое продолжение этого отрезка после перерыва.</summary>
    internal Segment ResumedAt(DateTime startUtc, TimeSpan elapsedBefore) =>
        new(startUtc, null, Title, Status, Source, ActivityId, CategoryKey, elapsedBefore, IsParallel);

    internal bool Overlaps(Segment other) =>
        StartUtc < other.EffectiveEndUtc && other.StartUtc < EffectiveEndUtc;
}
