using System.Diagnostics;
using EdgeForge.Core;
using EdgeForge.Decision;
using EdgeForge.Device;
using EdgeForge.Runtime;
using EdgeForge.Vision;
using Microsoft.ML.OnnxRuntime;

Console.WriteLine("EDGEFORGE INSPECTION");
Console.WriteLine("====================");
Console.WriteLine("Local inspection pipeline prototype");
Console.WriteLine();

EdgeForgeOptions options;
try
{
    options = EdgeForgeConfiguration.Load(AppContext.BaseDirectory);

    // Strict here, unlike the viewer: a headless run with a bad meal threshold
    // would count nothing and say so only in passing.
    if (options.Scenario == ScenarioNames.Bowls && options.Meals.Enabled) options.Meals.Validate();
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException)
{
    Console.Error.WriteLine($"Invalid configuration: {ex.Message}");
    return 2;
}

// Plain `new`, no container. The scenario decides detector, rule and decision
// layer; this host only supplies its console writer and the capture loop.
Scenario scenario;
try
{
    scenario = Scenario.Create(
        options,
        new ConsoleInspectionAction(),
        AppContext.BaseDirectory,
        note => Console.WriteLine($"Event  : {note}"));
}
catch (Exception ex) when (ex is OnnxRuntimeException or FileNotFoundException)
{
    // A missing or unreadable model is a configuration failure, not a crash.
    Console.Error.WriteLine($"Model unavailable: {ex.Message}");
    return 2;
}

using (scenario)
{
    foreach (var line in scenario.Describe(options)) Console.WriteLine(line);

    var pipeline = new InspectionPipeline(
        scenario.Detector,
        scenario.Inspector,
        scenario.Action);

    try
    {
        // Precedence: CLI argument > environment > profile > appsettings.json.
        var source = args.Length > 0 ? args[0] : options.Camera.Source;

        if (string.IsNullOrWhiteSpace(source))
        {
            await RunSyntheticAsync(pipeline);
            return 0;
        }

        try
        {
            await RunCameraAsync(pipeline, source, options.Camera.Capacity, scenario.Sink);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Invalid source: {ex.Message}");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Capture failed: {ex.Message}");
            return 1;
        }
    }
    finally
    {
        // Belt and braces: RunCameraAsync flushes before printing its summary, but
        // the synthetic and error paths never reach that. Flush is idempotent.
        scenario.Sink?.Flush();
    }
}

// Runs with nothing connected, so the demo always has something to show.
static async Task RunSyntheticAsync(InspectionPipeline pipeline)
{
    var products = Enumerable.Range(1, 6)
        .Select(i => $"BOX-{i:000}");

    foreach (var productId in products)
    {
        var frame = new Frame(
            productId,
            DateTimeOffset.UtcNow,
            1920,
            1080);

        var (result, _) = await pipeline.ProcessAsync(frame);

        Console.WriteLine($"Product: {result.FrameId}");
        Console.WriteLine($"Result : {result.Status}");

        if (result.Reason is not null)
            Console.WriteLine($"Reason : {result.Reason}");

        Console.WriteLine($"Objects: {result.Detections.Count}");
        Console.WriteLine($"Latency: {result.ProcessingTime.TotalMilliseconds:F3} ms");
        Console.WriteLine(new string('-', 40));
    }
}

static async Task RunCameraAsync(
    InspectionPipeline pipeline, string source, int capacity, IDecisionSink? sink)
{
    var spec = FrameSourceSpec.Parse(source);
    var frameSource = new OpenCvFrameSource(spec, capacity);

    Console.WriteLine($"Source : {spec.Kind} '{spec.Target}' via {spec.Api}");
    Console.WriteLine("Press Ctrl+C to stop.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        // Cancel the loop instead of killing the process with native Mats alive.
        e.Cancel = true;
        cts.Cancel();
    };

    var processed = 0L;
    var ok = 0L;
    var nok = 0L;
    var unknown = 0L;
    var elapsed = Stopwatch.StartNew();

    try
    {
        await foreach (var frame in frameSource.ReadFramesAsync(cts.Token))
        {
            try
            {
                var (result, metrics) = await pipeline.ProcessAsync(frame, cts.Token);

                processed++;
                switch (result.Status)
                {
                    case InspectionStatus.Ok: ok++; break;
                    case InspectionStatus.Nok: nok++; break;
                    default: unknown++; break;
                }

                // Printing every frame at 25fps makes the console the bottleneck
                // and distorts the very latency we are trying to measure.
                if (result.Status == InspectionStatus.Nok || processed % 30 == 1)
                {
                    Console.WriteLine(
                        $"{frame.Id}  {result.Status,-7} {frame.Width}x{frame.Height}  " +
                        $"dets {result.Detections.Count}  " +
                        $"queue {metrics.CaptureLatency.TotalMilliseconds:F1} ms  " +
                        $"infer {metrics.InferenceLatency.TotalMilliseconds:F1} ms  " +
                        $"total {metrics.TotalLatency.TotalMilliseconds:F1} ms" +
                        (result.Reason is null ? string.Empty : $"  [{result.Reason}]"));
                }
            }
            finally
            {
                // The consumer owns the frame after read.
                frame.Image?.Dispose();
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C: exit cleanly.
    }

    elapsed.Stop();
    var seconds = elapsed.Elapsed.TotalSeconds;

    // Close anything still in progress - a meal - BEFORE the summary is printed.
    // Left to the outer finally it would run after these lines, and a run that
    // ended mid-meal would report one fewer than it wrote to the journal.
    sink?.Flush();

    Console.WriteLine();
    Console.WriteLine("SUMMARY");
    Console.WriteLine($"Processed : {processed}");
    Console.WriteLine($"Ok/Nok/Unk: {ok}/{nok}/{unknown}");
    Console.WriteLine($"Dropped   : {frameSource.DroppedFrames}");
    Console.WriteLine($"FPS       : {(seconds > 0 ? processed / seconds : 0):F1}");

    if (sink is not null)
    {
        // The numbers the decision layer exists to produce: events, not frames
        // with a detection in them.
        foreach (var line in sink.Readout())
        {
            Console.WriteLine($"{line.Label,-14}: {line.Value}");
        }
    }
}
