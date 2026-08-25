using System.Globalization;
using EdgeForge.Core;
using EdgeForge.Vision;

namespace EdgeForge.Runtime;

/// <summary>
/// Writes frames to disk, on the capture thread, under the limits in
/// <see cref="CaptureOptions"/>.
/// </summary>
/// <remarks>
/// Not thread-safe by design: only the capture loop calls it, and keeping it that
/// way means the throttle counters need no synchronisation.
/// </remarks>
public sealed class FrameWriter
{
    private readonly CaptureOptions _options;
    private readonly string _directory;
    private DateTimeOffset _lastAutoSave = DateTimeOffset.MinValue;
    private int _written;
    private bool _directoryReady;

    public FrameWriter(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _directory = string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(AppContext.BaseDirectory, "captures")
            : options.Directory;
    }

    /// <summary>Files written since the viewer started.</summary>
    public int Written => _written;

    public string Directory => _directory;

    /// <summary>Set by the UI toggle; starts from configuration.</summary>
    public bool AutoSaveEnabled { get; set; }

    /// <summary>
    /// Saves the frame if auto-save is on and the frame clears every limit.
    /// Returns the path written, or null when this frame was skipped.
    /// </summary>
    public string? TryAutoSave(Frame frame, IReadOnlyList<Detection> detections, DateTimeOffset now)
    {
        if (!AutoSaveEnabled) return null;
        if (detections.Count == 0) return null;

        if (_options.MaxFiles > 0 && _written >= _options.MaxFiles) return null;

        if (now - _lastAutoSave < TimeSpan.FromSeconds(_options.MinIntervalSeconds)) return null;

        var best = 0.0;
        foreach (var detection in detections)
        {
            if (detection.Confidence > best) best = detection.Confidence;
        }

        if (best < _options.MinConfidence) return null;

        // Only commit the throttle once the write actually lands, so a failing
        // disk does not silently consume the interval and hide the problem.
        var path = Save(frame, "auto", detections.Count, best);
        if (path is not null) _lastAutoSave = now;

        return path;
    }

    /// <summary>Saves on explicit request, ignoring the auto-save throttle.</summary>
    public string? SaveManual(Frame frame, IReadOnlyList<Detection> detections)
    {
        var best = 0.0;
        foreach (var detection in detections)
        {
            if (detection.Confidence > best) best = detection.Confidence;
        }

        return Save(frame, "snap", detections.Count, best);
    }

    private string? Save(Frame frame, string prefix, int detectionCount, double bestConfidence)
    {
        if (!EnsureDirectory()) return null;

        // Name carries what a later labelling pass needs to triage without opening
        // every file: when, how many, and how sure the detector was.
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-n{detectionCount}-c{bestConfidence * 100:F0}.jpg");

        var path = Path.Combine(_directory, name);

        try
        {
            if (!BitmapBridge.TrySaveToFile(frame, path)) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full or read-only disk must not take the capture loop down.
            return null;
        }

        _written++;
        return path;
    }

    private bool EnsureDirectory()
    {
        if (_directoryReady) return true;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            _directoryReady = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }
}
