namespace TimeShift.Domain.Timelines;

/// <summary>
/// Режим продления открытого занятия (spec §3.3): до <c>T × NormFactor</c> — ⚪, до <c>T × MaxFactor</c> — 🟡,
/// дальше — фон. <see cref="double.PositiveInfinity"/> — без ограничения (до «сейчас»).
/// </summary>
public sealed record ExtensionPolicy
{
    private ExtensionPolicy(double normFactor, double maxFactor)
    {
        NormFactor = normFactor;
        MaxFactor = maxFactor;
    }

    public double NormFactor { get; }

    public double MaxFactor { get; }

    /// <summary>«До типичной длительности»: 1.0 / 1.0.</summary>
    public static ExtensionPolicy UpToTypical { get; } = new(1.0, 1.0);

    /// <summary>«×1.5 норма, ×2 предел» — по умолчанию.</summary>
    public static ExtensionPolicy Default { get; } = new(1.5, 2.0);

    /// <summary>«Бесконечно»: ∞ / ∞.</summary>
    public static ExtensionPolicy Infinite { get; } = new(double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>«Свой»: любой <paramref name="normFactor"/> &gt; 0, <paramref name="maxFactor"/> ≥ norm.</summary>
    public static ExtensionPolicy Custom(double normFactor, double maxFactor)
    {
        if (double.IsNaN(normFactor) || normFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(normFactor), normFactor, "Коэффициент нормы должен быть > 0.");
        }

        if (double.IsNaN(maxFactor) || maxFactor < normFactor)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFactor), maxFactor, "Предел должен быть не меньше нормы.");
        }

        return new ExtensionPolicy(normFactor, maxFactor);
    }
}
