namespace TimeShift.Domain.Periods;

/// <summary>Как задана длительность пункта разбора периода (spec §3.5).</summary>
public abstract record DurationSpec
{
    private DurationSpec()
    {
    }

    /// <summary>Явная длительность: «час», «минут 20».</summary>
    public sealed record Minutes : DurationSpec
    {
        public Minutes(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Value = value;
        }

        public int Value { get; }
    }

    /// <summary>Явный интервал «с … до …» в местном времени; конец ≤ начала — через полночь. Становится якорем.</summary>
    public sealed record Range : DurationSpec
    {
        public Range(TimeOnly start, TimeOnly end)
        {
            if (start == end)
            {
                throw new ArgumentException("Начало и конец интервала совпадают.", nameof(end));
            }

            Start = start;
            End = end;
        }

        public TimeOnly Start { get; }

        public TimeOnly End { get; }
    }

    /// <summary>Длительность из факта пользователя (маршрут, типичная длительность занятия) по ключу.</summary>
    public sealed record FromFact : DurationSpec
    {
        public FromFact(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            Key = key;
        }

        public string Key { get; }
    }

    /// <summary>«Всё оставшееся время».</summary>
    public sealed record Rest : DurationSpec;

    /// <summary>Длительность не названа; одиночный такой пункт работает как <see cref="Rest"/>.</summary>
    public sealed record Unspecified : DurationSpec;
}
