using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>
/// Builds the meal-counting sink from configuration, so the demo and the viewer
/// get the same decision layer rather than two that drift apart.
/// </summary>
/// <remarks>
/// The mirror of <c>DetectorFactory</c>, and here for the same reason: one place
/// that knows how the objects go together, called from both executables.
/// </remarks>
public static class MealSinkFactory
{
    /// <summary>
    /// Wraps <paramref name="inner"/> in a meal sink, or returns it unchanged when
    /// meal counting is switched off.
    /// </summary>
    /// <param name="baseDirectory">
    /// Where <c>stats.json</c> and <c>meals.jsonl</c> go when the configured paths
    /// are empty - beside the binary, which is where OBS reads the overlay from.
    /// </param>
    /// <exception cref="InvalidOperationException">A threshold is not usable.</exception>
    public static IInspectionAction Create(
        IInspectionAction inner,
        MealsOptions options,
        string className,
        string baseDirectory,
        Action<string>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled) return inner;

        options.Validate();

        var thresholds = new MealThresholds
        {
            MinPresenceToStart = TimeSpan.FromSeconds(options.MinPresenceSeconds),
            MaxAbsenceToEnd = TimeSpan.FromSeconds(options.MaxAbsenceSeconds),
            MinMealDuration = TimeSpan.FromSeconds(options.MinMealSeconds)
        };

        var sink = new MealEventSink(
            inner,
            new MealDetector(thresholds),
            new DailyMealLog(),
            new MealJournal(Resolve(options.JournalPath, baseDirectory, "meals.jsonl")),
            new StatsFile(Resolve(options.StatsPath, baseDirectory, "stats.json")),
            className,
            TimeSpan.FromSeconds(options.StatsIntervalSeconds),
            notify);

        // Before the first frame, so a restart at noon does not show zero meals for
        // the rest of the day.
        sink.Restore();

        return sink;
    }

    /// <summary>One line describing the active rule, for a banner or status bar.</summary>
    public static string Describe(MealsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Enabled
            ? $"start after {options.MinPresenceSeconds:F0}s present, " +
              $"end after {options.MaxAbsenceSeconds:F0}s absent, " +
              $"min {options.MinMealSeconds:F0}s"
            : "off";
    }

    private static string Resolve(string configured, string baseDirectory, string fallback) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(baseDirectory, fallback)
            : configured;
}
