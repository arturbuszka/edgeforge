using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>
/// Where the decision layer meets the pipeline: feeds each frame's presence
/// reading into <see cref="MealDetector"/>, and persists whatever comes out.
/// </summary>
/// <remarks>
/// Named a sink rather than an action because that is what it is. It implements
/// <see cref="IInspectionAction"/> to sit in the existing pipeline without
/// reshaping it, but its job is to receive events, not to actuate anything.
///
/// It decorates an inner action rather than replacing one, so the console writer
/// and the viewer log keep working untouched and the per-frame reading stays
/// visible. Presence is still what <c>PresenceInspector</c> says it is; this class
/// only asks the further question of what those readings mean over time.
///
/// <para><b>Why here and not in an inspector.</b> Writing files inside
/// <c>IInspector.Inspect</c> would fold disk I/O into <c>DecisionLatency</c>, a
/// metric that currently measures the cost of the rule itself. Downstream, the
/// cost lands in <c>ActionLatency</c>, which exists for exactly that. The state
/// machine itself lives in <see cref="MealDetector"/> either way, so moving this
/// wiring up a layer later is a small change.</para>
///
/// <para><b>Threading.</b> Called from the single capture loop, like every other
/// action, and holds no lock. <see cref="Snapshot"/> is the exception: the viewer
/// reads it from the UI thread, so the few fields it touches are published through
/// a lock.</para>
/// </remarks>
public sealed class MealEventSink : IInspectionAction
{
    private readonly IInspectionAction _inner;
    private readonly MealDetector _detector;
    private readonly DailyMealLog _log;
    private readonly MealJournal? _journal;
    private readonly StatsFile? _stats;
    private readonly string _className;
    private readonly TimeSpan _statsInterval;
    private readonly Action<string>? _notify;
    private readonly Lock _gate = new();

    private DateTimeOffset _lastStatsWrite = DateTimeOffset.MinValue;
    private int _mealsToday;
    private bool _isFeeding;

    // Today's meals, kept in memory so a live readout costs no disk read. The
    // journal remains the durable copy; this is the same data, seeded from it on
    // Restore and appended to as meals complete. Guarded by _gate along with the
    // counters, because a reader can be on any thread.
    private readonly List<MealEvent> _today = [];
    private DateOnly _todayDay;

    /// <param name="inner">The action to run first; the existing console or viewer sink.</param>
    /// <param name="journal">Append-only history, or null to keep meals in memory only.</param>
    /// <param name="stats">The overlay file, or null when nothing is reading one.</param>
    /// <param name="className">Detection class that counts as an animal at the bowls.</param>
    /// <param name="statsInterval">
    /// Floor on how often <paramref name="stats"/> is rewritten for a mere change of
    /// state. A completed meal always writes immediately - the number on stream
    /// should move when the cat leaves, not up to a few seconds later.
    /// </param>
    /// <param name="notify">Optional log callback, for the console or the viewer log.</param>
    public MealEventSink(
        IInspectionAction inner,
        MealDetector detector,
        DailyMealLog log,
        MealJournal? journal = null,
        StatsFile? stats = null,
        string className = "cat",
        TimeSpan? statsInterval = null,
        Action<string>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _detector = detector;
        _log = log;
        _journal = journal;
        _stats = stats;
        _className = className;
        _statsInterval = statsInterval ?? TimeSpan.FromSeconds(5);
        _notify = notify;
    }

    /// <summary>Today's tally and the current state, for a live readout.</summary>
    /// <remarks>
    /// Published through <see cref="_gate"/> because the viewer and the web layer
    /// read it from threads other than the capture loop that writes it.
    /// </remarks>
    public MealsSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new MealsSnapshot(
                    _mealsToday,
                    _isFeeding,
                    _log.TotalDuration,
                    _log.LastMealEnded,
                    _detector.CurrentMealStarted,
                    _log.Day);
            }
        }
    }

    /// <summary>
    /// Today's completed meals, newest first, at most <paramref name="limit"/> of them.
    /// </summary>
    /// <remarks>
    /// Served from memory rather than re-reading the journal: the web view polls this
    /// every couple of seconds, and a file read per poll would put disk I/O on a
    /// timer for the life of the process.
    /// </remarks>
    public IReadOnlyList<MealEvent> MealsToday(int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        lock (_gate)
        {
            DropStaleDayCore();

            var take = Math.Min(limit, _today.Count);
            var result = new MealEvent[take];

            // Newest first: the list is appended in completion order.
            for (var i = 0; i < take; i++) result[i] = _today[_today.Count - 1 - i];

            return result;
        }
    }

    /// <summary>
    /// Clears the in-memory list when the day has rolled over. Call under _gate.
    /// </summary>
    /// <remarks>
    /// DailyMealLog rolls itself at local midnight; this keeps the list it mirrors
    /// from carrying yesterday's meals into today's readout.
    /// </remarks>
    private void DropStaleDayCore()
    {
        var day = _log.Day;
        if (day == _todayDay) return;

        _today.Clear();
        _todayDay = day;
    }

    /// <summary>
    /// Rebuilds today's count from the journal. Call once before starting.
    /// </summary>
    /// <remarks>
    /// Without this a restart at noon silently resets the day to zero and the
    /// overlay is wrong until midnight. Failures are reported and swallowed: an
    /// unreadable history is a reason to start counting from now, not to refuse
    /// to run.
    /// </remarks>
    public void Restore(TimeProvider? time = null)
    {
        if (_journal is null) return;

        try
        {
            var restored = _journal.ReadDay(_log.Day, time);
            foreach (var meal in restored) _log.Add(meal);

            lock (_gate)
            {
                _mealsToday = _log.Meals;
                _todayDay = _log.Day;
                _today.Clear();
                _today.AddRange(restored);
            }

            if (restored.Count > 0)
            {
                _notify?.Invoke($"Restored {restored.Count} meal(s) from today");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notify?.Invoke($"Could not read meal history: {ex.Message}");
        }
    }

    public async Task ExecuteAsync(
        InspectionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await _inner.ExecuteAsync(result, cancellationToken);

        var count = 0;
        foreach (var detection in result.Detections)
        {
            if (detection.ClassName == _className) count++;
        }

        // The frame's own timestamp would be better still, but InspectionResult
        // does not carry it. Wall clock at the sink is within one frame of it on a
        // live camera; a recorded file replays faster than real time and will read
        // as a shorter meal. Noted in the README as a limitation of file replay,
        // not worked around here - widening InspectionResult for it is a change to
        // a core contract, and the live path is the one that matters.
        var now = DateTimeOffset.UtcNow;

        var meal = _detector.Observe(now, count);
        var feeding = _detector.IsFeeding;

        // Publish the state before deciding on it, so the flag this thread compares
        // against is the one it last wrote rather than a value another thread could
        // be reading mid-update.
        bool changed;
        lock (_gate)
        {
            changed = feeding != _isFeeding;
            _isFeeding = feeding;
        }

        if (meal is not null)
        {
            Record(meal);
            WriteStats(feeding, now, force: true);
        }
        else if (changed || now - _lastStatsWrite >= _statsInterval)
        {
            // A state change is worth showing at once: the overlay should flip to
            // "eating now" as the cat settles, not up to five seconds later.
            WriteStats(feeding, now, force: changed);
        }
    }

    /// <summary>
    /// Closes a meal still in progress and writes the final state. For shutdown
    /// and end of stream: an unflushed meal is one the day never counted.
    /// </summary>
    public void Flush()
    {
        var meal = _detector.Flush();
        if (meal is not null) Record(meal);

        lock (_gate) _isFeeding = false;

        WriteStats(feeding: false, DateTimeOffset.UtcNow, force: true);
    }

    private void Record(MealEvent meal)
    {
        _log.Add(meal);

        lock (_gate)
        {
            _mealsToday = _log.Meals;
            DropStaleDayCore();
            _today.Add(meal);
        }

        _notify?.Invoke(
            $"Meal #{_log.Meals} ended — {meal.Duration.TotalSeconds:F0} s, " +
            $"peak {meal.PeakCount}");

        if (_journal is null) return;

        try
        {
            _journal.Append(meal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full or read-only disk must not take the pipeline down. The meal is
            // still counted in memory; only the history loses it.
            _notify?.Invoke($"Could not write meal history: {ex.Message}");
        }
    }

    private void WriteStats(bool feeding, DateTimeOffset now, bool force)
    {
        if (_stats is null) return;
        if (!force && now - _lastStatsWrite < _statsInterval) return;

        _lastStatsWrite = now;

        try
        {
            _stats.Write(_log, feeding, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notify?.Invoke($"Could not write stats: {ex.Message}");
        }
    }
}
