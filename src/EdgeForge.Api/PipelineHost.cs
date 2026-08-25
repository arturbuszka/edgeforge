using EdgeForge.Core;
using EdgeForge.Decision;
using EdgeForge.Device;
using EdgeForge.Runtime;
using EdgeForge.Storage;
using EdgeForge.Vision;
using Microsoft.AspNetCore.SignalR;

namespace EdgeForge.Api;

/// <summary>
/// Owns the capture loop for the lifetime of the host, and pushes a stats
/// snapshot to connected browsers whenever it changes.
/// </summary>
/// <remarks>
/// <para><b>A BackgroundService rather than a Task in Program.cs.</b> StopAsync
/// runs before Kestrel finishes draining, so a meal in progress is flushed while
/// the process is still alive. A bare Task would race the return of app.RunAsync,
/// and the meal in progress at shutdown would be the one the journal never hears
/// about.</para>
///
/// <para><b>The object graph is built with plain `new`</b>, exactly as Demo and
/// Ui build theirs. Only the hub context and the store arrive through the
/// constructor, because neither can be constructed by hand.</para>
///
/// <para><b>Video is not here.</b> MediaMTX pulls the camera's RTSP stream and
/// republishes it to the browser; this class opens its own session against the
/// same camera for inference and never encodes or serves a pixel.</para>
/// </remarks>
public sealed class PipelineHost : BackgroundService
{
    private readonly EdgeForgeOptions _options;
    private readonly IHubContext<LiveHub, ILiveClient> _hub;
    private readonly IMealStore _store;
    private readonly ILogger<PipelineHost> _log;

    private readonly IVisionDetector _detector;
    private readonly InspectionSession _session;
    private readonly MealEventSink? _meals;
    private readonly MealJournal? _journal;

    // Written by the capture thread from FrameObserver, read by the push loop.
    // Negative means "no frame seen yet", which is reported as null rather than 0:
    // "no animals at the bowls" and "nothing is looking" are different facts.
    private int _catsOnScreen = -1;

    private StatsSnapshot _current;

    // How many of today's meals have reached SQLite, so each is stored once.
    private int _storedMeals;

    public PipelineHost(
        EdgeForgeOptions options,
        IHubContext<LiveHub, ILiveClient> hub,
        IMealStore store,
        ILogger<PipelineHost> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _hub = hub;
        _store = store;
        _log = log;

        (_detector, var inspector) = DetectorFactory.Create(options.Detector);

        // The decision layer sits behind a console writer rather than replacing
        // it, same as Demo: the per-frame reading stays visible in the log.
        IInspectionAction action = new ConsoleInspectionAction();
        action = MealSinkFactory.Create(
            action,
            options.Meals,
            options.Detector.ClassName,
            AppContext.BaseDirectory,
            note => _log.LogInformation("Meals: {Note}", note));

        _meals = action as MealEventSink;

        if (_meals is not null)
        {
            _journal = new MealJournal(JournalPath(options.Meals));

            // MealSinkFactory.Create already restored today from the journal, so
            // those meals are in the database too - either stored live before the
            // restart, or about to be by the startup backfill. Starting the
            // counter here rather than at zero stops the first tick re-offering
            // all of them.
            _storedMeals = _meals.Snapshot.MealsToday;
        }

        var pipeline = new InspectionPipeline(_detector, inspector, action);
        _session = new InspectionSession(pipeline, new FrameWriter(options.Capture));

        // The only thing FrameObserver is still for. It runs on the capture
        // thread inside the snapshot gate, so it must not block and must not
        // retain the frame; a volatile write of an int satisfies both trivially.
        var className = options.Detector.ClassName;
        _session.FrameObserver = (_, detections) =>
        {
            var count = 0;
            foreach (var detection in detections)
            {
                if (detection.ClassName == className) count++;
            }

            Volatile.Write(ref _catsOnScreen, count);
        };

        _session.Notified += note => _log.LogInformation("{Note}", note);
        _session.Stopped += reason =>
        {
            if (reason is not null) _log.LogWarning("Capture stopped: {Reason}", reason);
        };

        _current = Snapshot(DateTimeOffset.UtcNow);
    }

    /// <summary>The latest snapshot, for /api/stats and for a fresh hub connection.</summary>
    public StatsSnapshot Current => Volatile.Read(ref _current);

    public bool IsRunning => _session.IsRunning;

    /// <summary>Today's completed meals, newest first, served from memory.</summary>
    public IReadOnlyList<MealEvent> MealsToday(int limit = 50) =>
        _meals?.MealsToday(limit) ?? [];

    /// <summary>The journal, for the startup backfill. Null when meals are off.</summary>
    public MealJournal? Journal => _journal;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var source = _options.Camera.Source;

        if (string.IsNullOrWhiteSpace(source))
        {
            // A deliberate configuration, not a failure: the API is useful on its
            // own while the front-end is being worked on, and refusing to start
            // would make that awkward for no safety gain.
            _log.LogWarning(
                "Camera:Source is empty - serving stats only, with no capture loop.");
        }
        else
        {
            _session.Start(source, _options.Camera.Capacity);
            _log.LogInformation("Capture started from {Source}", Redact(source));
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.Api.PushIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// Recomputes the snapshot, stores any newly completed meal, and pushes to
    /// browsers when something changed.
    /// </summary>
    /// <remarks>
    /// Comparing the meal counter rather than subscribing to an event keeps the
    /// decision layer untouched: MealEventSink has no completion event, and a
    /// sub-second delay on a meal counter is imperceptible. The trade is stated
    /// rather than hidden, because this is the seam where the database is fed.
    /// </remarks>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(DateTimeOffset.UtcNow);
        var previous = Volatile.Read(ref _current);

        Volatile.Write(ref _current, snapshot);

        if (snapshot.MealsToday > _storedMeals)
        {
            await StoreNewMealsAsync(snapshot, cancellationToken);
        }

        // UpdatedUtc changes every tick, so comparing whole records would push
        // once a second forever. Only the fields a HUD renders count as a change.
        if (!Changed(previous, snapshot)) return;

        try
        {
            await _hub.Clients.All.StatsUpdated(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A browser that vanished mid-push must not take the capture loop
            // down with it.
            _log.LogDebug(ex, "Stats push failed.");
        }
    }

    private async Task StoreNewMealsAsync(StatsSnapshot snapshot, CancellationToken cancellationToken)
    {
        var added = snapshot.MealsToday - _storedMeals;

        // Newest first, so reverse into chronological order before storing. It
        // makes no difference to correctness - the unique index is on the start
        // instant, not on insertion order - but it keeps row ids matching the
        // order meals actually happened, which is what a human reading the table
        // expects.
        var recent = MealsToday(added);

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            try
            {
                await _store.AppendAsync(recent[i], cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The journal already has it, and the journal is the source of
                // truth: the next start's backfill picks this up. Losing the
                // pipeline over a database write would be the worse trade.
                //
                // _storedMeals is deliberately not advanced, so the next tick
                // retries rather than skipping the meal until restart.
                _log.LogWarning(ex, "Could not store meal in the database.");
                return;
            }
        }

        _storedMeals = snapshot.MealsToday;
    }

    private static bool Changed(StatsSnapshot a, StatsSnapshot b) =>
        a.MealsToday != b.MealsToday
        || a.CurrentlyFeeding != b.CurrentlyFeeding
        || a.TotalFeedingSecondsToday != b.TotalFeedingSecondsToday
        || a.LastMealEndedUtc != b.LastMealEndedUtc
        || a.CurrentMealStartedUtc != b.CurrentMealStartedUtc
        || a.CatsOnScreen != b.CatsOnScreen
        || a.PipelineRunning != b.PipelineRunning
        || a.Date != b.Date;

    private StatsSnapshot Snapshot(DateTimeOffset now)
    {
        var cats = Volatile.Read(ref _catsOnScreen);

        return ApiEndpoints.BuildStats(
            _meals?.Snapshot,
            cats < 0 ? null : cats,
            IsRunning,
            now);
    }

    /// <summary>Where MealSinkFactory put the journal, so the backfill reads that same file.</summary>
    private static string JournalPath(MealsOptions meals) =>
        string.IsNullOrWhiteSpace(meals.JournalPath)
            ? Path.Combine(AppContext.BaseDirectory, "meals.jsonl")
            : Path.Combine(AppContext.BaseDirectory, meals.JournalPath);

    /// <summary>
    /// A camera URL fit for a log line, with any credentials removed.
    /// </summary>
    /// <remarks>
    /// An RTSP URL carries the camera password in its userinfo field, and a log
    /// file is the easiest place for it to end up somewhere it should not be.
    /// </remarks>
    private static string Redact(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return source;
        if (string.IsNullOrEmpty(uri.UserInfo)) return source;

        return $"{uri.Scheme}://***@{uri.Authority}{uri.PathAndQuery}";
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        await _session.StopAsync();

        // Close a meal still in progress. Unflushed, it is a meal the day never
        // counted - and this runs before Kestrel finishes draining, which is the
        // whole reason this is a hosted service.
        _meals?.Flush();

        _session.Dispose();
        (_detector as IDisposable)?.Dispose();
    }
}
