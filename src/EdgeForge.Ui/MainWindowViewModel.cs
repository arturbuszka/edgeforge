using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EdgeForge.Core;
using EdgeForge.Runtime;
using EdgeForge.Vision;

namespace EdgeForge.Ui;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int EventLogLimit = 200;
    private const int FpsWindow = 30;

    private readonly EdgeForgeOptions _options;
    private readonly InspectionSession _session;
    private readonly FrameWriter _writer;
    private readonly IVisionDetector _detector;
    private readonly RelayCommand _startCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _snapshotCommand;

    // Rolling window of frame arrival times; FPS over a window rides out the
    // jitter a single inter-frame gap is full of.
    private readonly Queue<long> _frameTicks = new(FpsWindow + 1);

    private readonly DispatcherTimer _uptimeTimer;
    private readonly Stopwatch _uptime = new();

    private WriteableBitmap? _frameBitmap;
    private Size _videoSize;
    private FrameSnapshot? _current;

    private string _source = "";
    private bool _isRunning;
    private bool _autoSave;
    private string _statusText = "Idle";
    private IBrush _statusBrush = Brushes.Gray;
    private string _modelDescription = "";
    private int _pumpScheduled;

    private long _processed;
    private long _ok;
    private long _nok;
    private int _maxCats;

    public MainWindowViewModel(EdgeForgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _source = options.Camera.Source;
        _modelDescription = DetectorFactory.Describe(options.Detector);

        (_detector, var inspector) = DetectorFactory.Create(options.Detector);

        _writer = new FrameWriter(options.Capture)
        {
            AutoSaveEnabled = options.Capture.AutoSaveDetections
        };
        _autoSave = _writer.AutoSaveEnabled;

        var pipeline = new InspectionPipeline(_detector, inspector, new UiInspectionAction(LogFromWorker));

        _session = new InspectionSession(pipeline, _writer);
        _session.SnapshotAvailable += OnSnapshotAvailable;
        _session.Stopped += OnSessionStopped;
        _session.Notified += LogFromWorker;

        _startCommand = new RelayCommand(Start, () => !IsRunning);
        _stopCommand = new RelayCommand(() => _ = StopAsync(), () => IsRunning);
        _snapshotCommand = new RelayCommand(SaveSnapshot, () => IsRunning);

        LiveStats = [CatsOnScreenStat, MaxCatsStat, ConfidenceStat, ResolutionStat];
        PerformanceStats =
        [
            FpsStat, CaptureLatencyStat, InferenceLatencyStat, DecisionLatencyStat,
            TotalLatencyStat, ProcessedStat, DroppedStat, VerdictStat, SavedStat, UptimeStat
        ];

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UptimeStat.Value = FormatUptime(_uptime.Elapsed);

        Log($"Model: {_modelDescription}");
        Log($"Captures: {_writer.Directory}");
    }

    /// <summary>Raised on the UI thread once a new frame has reached the bitmap.</summary>
    public event Action? FrameReady;

    public ObservableCollection<DetectionBox> Boxes { get; } = [];

    public ObservableCollection<string> EventLog { get; } = [];

    public ObservableCollection<StatRow> LiveStats { get; }

    public ObservableCollection<StatRow> PerformanceStats { get; }

    public ICommand StartCommand => _startCommand;

    public ICommand StopCommand => _stopCommand;

    public ICommand SnapshotCommand => _snapshotCommand;

    // Live figures: what is in front of the camera right now.
    public StatRow CatsOnScreenStat { get; } = new("On screen");
    public StatRow MaxCatsStat { get; } = new("Most at once");
    public StatRow ConfidenceStat { get; } = new("Best confidence");
    public StatRow ResolutionStat { get; } = new("Resolution");

    // Performance figures: whether the pipeline is keeping up.
    public StatRow FpsStat { get; } = new("FPS");
    public StatRow CaptureLatencyStat { get; } = new("Queue latency");
    public StatRow InferenceLatencyStat { get; } = new("Inference");
    public StatRow DecisionLatencyStat { get; } = new("Decision");
    public StatRow TotalLatencyStat { get; } = new("Total");
    public StatRow ProcessedStat { get; } = new("Frames processed");
    public StatRow DroppedStat { get; } = new("Frames dropped");
    public StatRow VerdictStat { get; } = new("Ok / Nok");
    public StatRow SavedStat { get; } = new("Frames saved");
    public StatRow UptimeStat { get; } = new("Uptime");

    public string Source
    {
        get => _source;
        set => SetField(ref _source, value);
    }

    public WriteableBitmap? FrameBitmap
    {
        get => _frameBitmap;
        private set => SetField(ref _frameBitmap, value);
    }

    /// <summary>Whether a frame has ever been painted; drives the "no signal" hint.</summary>
    public bool HasVideo => _frameBitmap is not null;

    /// <summary>Size of the incoming frame; the overlay needs it to place boxes.</summary>
    public Size VideoSize
    {
        get => _videoSize;
        private set => SetField(ref _videoSize, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            SetField(ref _isRunning, value);
            Raise(nameof(IsIdle));
            _startCommand.RaiseCanExecuteChanged();
            _stopCommand.RaiseCanExecuteChanged();
            _snapshotCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !_isRunning;

    public bool AutoSaveEnabled
    {
        get => _autoSave;
        set
        {
            if (_autoSave == value) return;

            SetField(ref _autoSave, value);
            _session.AutoSaveEnabled = value;
            Log(value ? "Auto-save on" : "Auto-save off");
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetField(ref _statusBrush, value);
    }

    public string ModelDescription
    {
        get => _modelDescription;
        private set => SetField(ref _modelDescription, value);
    }

    /// <summary>
    /// Recomputes box positions. Called for every new frame and whenever the window
    /// resizes, because Uniform stretch moves the painted rectangle with the control.
    /// </summary>
    public void ProjectBoxes(Size controlSize)
    {
        var snapshot = _current;
        if (snapshot is null)
        {
            Boxes.Clear();
            return;
        }

        var detections = snapshot.Detections;

        // Grow and shrink in place: swapping the collection wholesale would make
        // the ItemsControl rebuild every container at camera framerate.
        while (Boxes.Count > detections.Count) Boxes.RemoveAt(Boxes.Count - 1);
        while (Boxes.Count < detections.Count) Boxes.Add(new DetectionBox());

        for (var i = 0; i < detections.Count; i++)
        {
            var projected = BoxProjection.Project(
                detections[i].BoundingBox,
                snapshot.Width,
                snapshot.Height,
                controlSize.Width,
                controlSize.Height);

            Boxes[i].Update(detections[i], projected);
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        await _session.StopAsync();
    }

    private void Start()
    {
        if (IsRunning) return;

        var source = Source?.Trim() ?? "";
        if (source.Length == 0)
        {
            Log("Set a source first: a USB index, an rtsp:// URL, or a file path.");
            return;
        }

        try
        {
            _session.Start(source, _options.Camera.Capacity);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log($"Could not start: {ex.Message}");
            return;
        }

        ResetCounters();
        _uptime.Restart();
        _uptimeTimer.Start();
        IsRunning = true;
        SetStatus("Running", Brushes.SteelBlue);
        Log("Started");
    }

    private void SaveSnapshot()
    {
        var path = _session.SaveSnapshot();

        Log(path is null ? "Nothing to save yet." : $"Saved {Path.GetFileName(path)}");

        SavedStat.Value = _writer.Written.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Raised on the capture thread. Posts one pump rather than one per frame: the
    /// pump always reads the newest snapshot, so queuing more would only make the
    /// dispatcher repaint frames it is already behind on.
    /// </summary>
    private void OnSnapshotAvailable()
    {
        if (Interlocked.Exchange(ref _pumpScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(Pump, DispatcherPriority.Background);
    }

    private void Pump()
    {
        Volatile.Write(ref _pumpScheduled, 0);

        var snapshot = _session.TakeSnapshot();
        if (snapshot is null) return;

        _current = snapshot;

        UpdateBitmap(snapshot);
        UpdateStats(snapshot);

        // The view re-projects against its own bounds; only it knows the control size.
        FrameReady?.Invoke();
    }

    private void UpdateBitmap(FrameSnapshot snapshot)
    {
        if (!snapshot.HasPixels) return;

        var size = new PixelSize(snapshot.Width, snapshot.Height);

        // Reallocate only when the stream changes shape; a per-frame allocation of a
        // multi-megabyte bitmap would dominate the UI thread.
        if (_frameBitmap is null ||
            _frameBitmap.PixelSize.Width != size.Width ||
            _frameBitmap.PixelSize.Height != size.Height)
        {
            var replaced = _frameBitmap;

            _frameBitmap = new WriteableBitmap(
                size,
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            VideoSize = new Size(snapshot.Width, snapshot.Height);
            Raise(nameof(FrameBitmap));
            Raise(nameof(HasVideo));

            replaced?.Dispose();
        }

        using var locked = _frameBitmap.Lock();
        var rowBytes = snapshot.Width * 4;

        if (locked.RowBytes == rowBytes)
        {
            Marshal.Copy(snapshot.Bgra, 0, locked.Address, snapshot.Bgra.Length);
        }
        else
        {
            // Avalonia may hand back a padded stride; copy row by row.
            for (var y = 0; y < snapshot.Height; y++)
            {
                Marshal.Copy(
                    snapshot.Bgra,
                    y * rowBytes,
                    locked.Address + (y * locked.RowBytes),
                    rowBytes);
            }
        }
    }

    private void UpdateStats(FrameSnapshot snapshot)
    {
        _processed++;

        switch (snapshot.Status)
        {
            case InspectionStatus.Ok: _ok++; break;
            case InspectionStatus.Nok: _nok++; break;
        }

        var count = snapshot.Detections.Count;
        if (count > _maxCats) _maxCats = count;

        var best = 0.0;
        foreach (var detection in snapshot.Detections)
        {
            if (detection.Confidence > best) best = detection.Confidence;
        }

        CatsOnScreenStat.Value = count.ToString(CultureInfo.InvariantCulture);
        MaxCatsStat.Value = _maxCats.ToString(CultureInfo.InvariantCulture);
        ConfidenceStat.Value = count == 0 ? "-" : best.ToString("P0", CultureInfo.InvariantCulture);
        ResolutionStat.Value = $"{snapshot.Width}x{snapshot.Height}";

        FpsStat.Value = Tick().ToString("F1", CultureInfo.InvariantCulture);
        CaptureLatencyStat.Value = Ms(snapshot.Metrics.CaptureLatency);
        InferenceLatencyStat.Value = Ms(snapshot.Metrics.InferenceLatency);
        DecisionLatencyStat.Value = Ms(snapshot.Metrics.DecisionLatency);
        TotalLatencyStat.Value = Ms(snapshot.Metrics.TotalLatency);
        ProcessedStat.Value = _processed.ToString(CultureInfo.InvariantCulture);
        DroppedStat.Value = snapshot.DroppedFrames.ToString(CultureInfo.InvariantCulture);
        VerdictStat.Value = $"{_ok} / {_nok}";
        SavedStat.Value = _writer.Written.ToString(CultureInfo.InvariantCulture);

        (string Text, IBrush Brush) status = snapshot.Status switch
        {
            InspectionStatus.Ok => ("OK", Brushes.MediumSeaGreen),
            InspectionStatus.Nok => (snapshot.Reason ?? "NOK", Brushes.IndianRed),
            _ => ("Unknown", Brushes.Gray)
        };

        SetStatus(status.Text, status.Brush);
    }

    private double Tick()
    {
        var now = Stopwatch.GetTimestamp();
        _frameTicks.Enqueue(now);
        while (_frameTicks.Count > FpsWindow) _frameTicks.Dequeue();

        if (_frameTicks.Count < 2) return 0;

        var span = (now - _frameTicks.Peek()) / (double)Stopwatch.Frequency;
        return span > 0 ? (_frameTicks.Count - 1) / span : 0;
    }

    private void OnSessionStopped(string? failure) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = false;
            _uptime.Stop();
            _uptimeTimer.Stop();

            Boxes.Clear();
            _current = null;

            if (failure is null)
            {
                SetStatus("Stopped", Brushes.Gray);
                Log("Stopped");
            }
            else
            {
                SetStatus("Failed", Brushes.IndianRed);
                Log($"Capture failed: {failure}");
            }
        });

    private void SetStatus(string text, IBrush brush)
    {
        StatusText = text;
        StatusBrush = brush;
    }

    private void ResetCounters()
    {
        _processed = 0;
        _ok = 0;
        _nok = 0;
        _maxCats = 0;
        _frameTicks.Clear();
    }

    /// <summary>Log entry point for the capture thread.</summary>
    private void LogFromWorker(string message) => Dispatcher.UIThread.Post(() => Log(message));

    private void Log(string message)
    {
        EventLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");

        while (EventLog.Count > EventLogLimit) EventLog.RemoveAt(EventLog.Count - 1);
    }

    private static string Ms(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:F1} ms");

    private static string FormatUptime(TimeSpan value) =>
        value.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _uptimeTimer.Stop();
        _session.Dispose();
        _frameBitmap?.Dispose();
        (_detector as IDisposable)?.Dispose();
    }
}
