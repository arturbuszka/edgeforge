namespace EdgeForge.Decision;

/// <summary>
/// Turns a per-frame presence reading into meal events.
/// </summary>
/// <remarks>
/// This is the debounce the whole decision layer exists for. Without it "seven
/// meals" is 1400 frames carrying a detection; with it, seven events.
///
/// Driven by FRAME TIMESTAMPS, never by the wall clock. Two consequences, both
/// deliberate:
/// <list type="bullet">
/// <item>Replaying a recorded file produces exactly the results the same footage
/// produced live, so a threshold can be re-tuned against yesterday's video.</item>
/// <item>Absence is measured in time rather than in consecutive empty frames. At
/// 41 ms per frame a stalled or dropped-frame stretch would otherwise read as the
/// animal leaving, and a pipeline hiccup would end a meal that never ended.</item>
/// </list>
///
/// Pure and single-threaded: no I/O, no clock, no locks. Persisting the event and
/// writing stats.json belong to the sink downstream. That keeps a 1400-frame test
/// a matter of microseconds and keeps this class free of the disk.
/// </remarks>
public sealed class MealDetector
{
    private enum State
    {
        /// <summary>Nothing at the bowls, or not yet for long enough.</summary>
        Idle,

        /// <summary>Seen, but the start threshold has not been crossed yet.</summary>
        Starting,

        /// <summary>A meal is in progress; brief gaps do not end it.</summary>
        Feeding
    }

    private readonly MealThresholds _thresholds;

    private State _state = State.Idle;
    private DateTimeOffset _firstSeen;
    private DateTimeOffset _lastSeen;
    private int _peakCount;
    private long _observedFrames;

    public MealDetector(MealThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new MealThresholds();
        _thresholds.Validate();
    }

    /// <summary>Whether a meal is currently in progress, for a live readout.</summary>
    public bool IsFeeding => _state == State.Feeding;

    /// <summary>
    /// When the in-progress meal began, or null when none is in progress.
    /// </summary>
    public DateTimeOffset? CurrentMealStarted => _state == State.Feeding ? _firstSeen : null;

    /// <summary>
    /// Feeds one frame's reading in and returns a meal if this observation closed one.
    /// </summary>
    /// <param name="timestamp">
    /// The frame's own timestamp. Must not go backwards across calls; out-of-order
    /// frames would corrupt the durations this class exists to measure.
    /// </param>
    /// <param name="count">Animals of the target class visible in this frame.</param>
    /// <returns>The completed meal, or null - which is the answer for almost every frame.</returns>
    public MealEvent? Observe(DateTimeOffset timestamp, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return count > 0
            ? OnPresent(timestamp, count)
            : OnAbsent(timestamp);
    }

    /// <summary>
    /// Closes any meal in progress, ending it at the last sighting.
    /// </summary>
    /// <remarks>
    /// For shutdown and for the end of a video file: without it a meal that was
    /// still running when the stream stopped is silently lost, and the day's count
    /// is short by one. Still subject to <see cref="MealThresholds.MinMealDuration"/>.
    ///
    /// Takes no timestamp on purpose. The meal ended when the animal was last
    /// seen, not when the process happened to shut down - dating it to shutdown
    /// would stretch the duration by however long the stream ran on empty.
    /// </remarks>
    public MealEvent? Flush()
    {
        var completed = _state == State.Feeding ? BuildEvent() : null;
        Reset();
        return completed;
    }

    private MealEvent? OnPresent(DateTimeOffset timestamp, int count)
    {
        switch (_state)
        {
            case State.Idle:
                // First sighting. Remember it rather than starting the meal here:
                // the start threshold decides whether this becomes a meal at all,
                // but the meal's duration is measured from this moment.
                _firstSeen = timestamp;
                _state = State.Starting;
                break;

            case State.Starting when timestamp - _firstSeen >= _thresholds.MinPresenceToStart:
                _state = State.Feeding;
                break;
        }

        _lastSeen = timestamp;
        _observedFrames++;
        if (count > _peakCount) _peakCount = count;

        return null;
    }

    private MealEvent? OnAbsent(DateTimeOffset timestamp)
    {
        // Idle is the state most of the day is spent in; nothing to time.
        if (_state == State.Idle) return null;

        if (timestamp - _lastSeen < _thresholds.MaxAbsenceToEnd)
        {
            // A pause, not an ending. The animal is mid-meal and looked away, or
            // the detector missed a frame - which at 76% confidence happens often
            // enough that treating it as departure would split every meal.
            return null;
        }

        // Starting -> the animal passed by without ever crossing the start
        // threshold. No meal, no event, and nothing worth logging.
        var completed = _state == State.Feeding ? BuildEvent() : null;
        Reset();
        return completed;
    }

    /// <summary>
    /// Builds the event for the meal in progress, or null if it was too short to
    /// count. Duration spans first to last sighting - the trailing absence that
    /// proved the meal was over is evidence, not part of the meal.
    /// </summary>
    private MealEvent? BuildEvent() =>
        _lastSeen - _firstSeen >= _thresholds.MinMealDuration
            ? new MealEvent(_firstSeen, _lastSeen, _peakCount, _observedFrames)
            : null;

    private void Reset()
    {
        _state = State.Idle;
        _peakCount = 0;
        _observedFrames = 0;
    }
}
