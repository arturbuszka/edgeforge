using System.Diagnostics;
using EdgeForge.Core;
using EdgeForge.Device;
using EdgeForge.Runtime;
using EdgeForge.Vision;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.OnnxRuntime;

Console.WriteLine("EDGEFORGE INSPECTION");
Console.WriteLine("====================");
Console.WriteLine("Local inspection pipeline prototype");
Console.WriteLine();

EdgeForgeOptions options;
try
{
    options = LoadOptions();
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException)
{
    Console.Error.WriteLine($"Invalid configuration: {ex.Message}");
    return 2;
}

// Plain `new`, no container: the graph is three objects deep and naming them
// here is more honest than registering them somewhere else. The detector pair
// itself comes from DetectorFactory so the viewer builds the same graph.
IVisionDetector detector;
IInspector inspector;
try
{
    (detector, inspector) = DetectorFactory.Create(options.Detector);
    Console.WriteLine($"Model  : {DetectorFactory.Describe(options.Detector, detector)}");
}
catch (Exception ex) when (ex is OnnxRuntimeException or FileNotFoundException)
{
    // A missing or unreadable model is a configuration failure, not a crash.
    Console.Error.WriteLine($"Model unavailable: {ex.Message}");
    return 2;
}

try
{
    var pipeline = new InspectionPipeline(
        detector,
        inspector,
        new ConsoleInspectionAction());

    // Precedence: CLI argument > environment > appsettings.json.
    var source = args.Length > 0 ? args[0] : options.Camera.Source;

    if (string.IsNullOrWhiteSpace(source))
    {
        await RunSyntheticAsync(pipeline);
        return 0;
    }

    try
    {
        await RunCameraAsync(pipeline, source, options.Camera.Capacity);
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
    // OnnxVisionDetector owns a native inference session.
    (detector as IDisposable)?.Dispose();
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

static EdgeForgeOptions LoadOptions()
{
    // No DI container and no host builder: just enough configuration to keep a
    // camera URL (and its credentials) out of the command line and shell history.
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        // Gitignored overrides: camera credentials and machine-specific paths
        // belong here, not in the tracked file above.
        .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("EDGEFORGE_")
        .Build();

    var options = configuration.Get<EdgeForgeOptions>() ?? new EdgeForgeOptions();

    if (options.Camera.Capacity < 1)
    {
        throw new InvalidOperationException(
            $"Camera:Capacity must be at least 1, got {options.Camera.Capacity}.");
    }

    return options;
}

static async Task RunCameraAsync(InspectionPipeline pipeline, string source, int capacity)
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

    Console.WriteLine();
    Console.WriteLine("SUMMARY");
    Console.WriteLine($"Processed : {processed}");
    Console.WriteLine($"Ok/Nok/Unk: {ok}/{nok}/{unknown}");
    Console.WriteLine($"Dropped   : {frameSource.DroppedFrames}");
    Console.WriteLine($"FPS       : {(seconds > 0 ? processed / seconds : 0):F1}");
}
