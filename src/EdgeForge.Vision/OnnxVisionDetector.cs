using EdgeForge.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EdgeForge.Vision;

/// <summary>
/// Local YOLOv8 object detector behind the domain <see cref="IVisionDetector"/>
/// contract. ONNX Runtime types never leave this class: it takes a
/// <see cref="Frame"/> and returns domain <see cref="Detection"/> objects with
/// normalised boxes.
/// </summary>
/// <remarks>
/// The class to keep is a parameter, not a constant. The coop wants hens
/// (COCO 14 = bird); development happens against cats (COCO 15) because they are
/// indoors and always available, and both exercise the identical code path.
/// Switching targets is a configuration change, never an edit here.
/// <para>
/// Postprocessing differs from YOLOX in every respect that matters:
/// the output is transposed <c>[1, 4 + numClasses, numAnchors]</c>, there is no
/// objectness channel (score is the best class score alone), and boxes come out
/// as cx/cy/w/h already in network pixels — no grid, no stride, no exp().
/// </para>
/// </remarks>
public sealed class OnnxVisionDetector : IVisionDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _netSize;
    private readonly float _scoreThresh;
    private readonly float _nmsThresh;
    private readonly int _classId;
    private readonly string _className;

    /// <summary>Provider the session actually opened with, for the status banner.</summary>
    public string ActiveProvider { get; }

    /// <param name="modelPath">Path to the exported YOLOv8 .onnx model.</param>
    /// <param name="netSize">Model input size, square (default 640 = imgsz=640).</param>
    /// <param name="scoreThresh">Minimum class score to keep.</param>
    /// <param name="nmsThresh">IoU threshold for per-class NMS.</param>
    /// <param name="classId">COCO class index to keep (15 = cat, 14 = bird).</param>
    /// <param name="className">Domain name reported for that class.</param>
    /// <param name="executionProvider">"directml" for the GPU, "cpu" to force the CPU.</param>
    /// <param name="gpuDeviceId">DXGI adapter index for DirectML. See DetectorOptions.</param>
    public OnnxVisionDetector(
        string modelPath,
        int netSize = 640,
        float scoreThresh = 0.25f,
        float nmsThresh = 0.45f,
        int classId = 15,
        string className = "cat",
        string executionProvider = "directml",
        int gpuDeviceId = 0)
    {
        // Loaded once, for the lifetime of the detector. Building a session per
        // frame would dwarf inference itself.
        (_session, ActiveProvider) = CreateSession(modelPath, executionProvider, gpuDeviceId);
        _inputName = _session.InputMetadata.Keys.First();
        _netSize = netSize;
        _scoreThresh = scoreThresh;
        _nmsThresh = nmsThresh;
        _classId = classId;
        _className = className;
    }

    /// <summary>
    /// Opens the session on the requested provider, falling back to the CPU when
    /// DirectML is unavailable.
    /// </summary>
    /// <remarks>
    /// A machine with no DX12 GPU is a deployment fact, not a bug: the pipeline
    /// should still run, just slower. Anything else - a corrupt model, a missing
    /// file - is a real failure and propagates, because silently degrading those
    /// would hide the actual problem behind a latency number.
    /// <para>
    /// The two session options are required, not tuning. DirectML registers its
    /// operators through a custom registry that memory-pattern planning and
    /// parallel execution both interfere with; ORT's own DirectML samples set
    /// exactly these.
    /// </para>
    /// </remarks>
    private static (InferenceSession Session, string Provider) CreateSession(
        string modelPath, string executionProvider, int gpuDeviceId)
    {
        if (!string.Equals(executionProvider, "directml", StringComparison.OrdinalIgnoreCase))
        {
            return (new InferenceSession(modelPath), "cpu");
        }

        try
        {
            var options = new SessionOptions
            {
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            options.AppendExecutionProvider_DML(gpuDeviceId);

            return (new InferenceSession(modelPath, options), $"directml:{gpuDeviceId}");
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException)
        {
            // No DX12 device, or the native DirectML.dll never made it next to the
            // binary (see RuntimeIdentifier in the .csproj). Either way the CPU
            // provider still answers.
            return (new InferenceSession(modelPath), "cpu");
        }
    }

    public Task<IReadOnlyList<Detection>> DetectAsync(
        Frame frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mat = ReadablePixels(frame);
        if (mat is null)
        {
            // No pixels this detector can read is an empty observation, not a crash.
            return Task.FromResult<IReadOnlyList<Detection>>(Array.Empty<Detection>());
        }

        // Inference is synchronous and CPU-bound. The pipeline is serial, so
        // blocking the consumer for 50-150 ms deepens the backlog in the bounded
        // capture channel instead of just delaying this frame.
        return Task.Run<IReadOnlyList<Detection>>(
            () => Detect(mat, frame.Width, frame.Height, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Pixels this detector can read, or null. The synthetic demo sequence builds
    /// frames with no image at all, and an <see cref="IFrameImage"/> from another
    /// adapter holds a buffer this detector does not understand — both are an
    /// absence of observation rather than a failure.
    /// </summary>
    /// <remarks>
    /// The pattern match is the point of <see cref="MatFrameImage"/>: full-speed,
    /// zero-copy access to the capture buffer without OpenCvSharp reaching the core.
    /// </remarks>
    internal static Mat? ReadablePixels(Frame frame) =>
        frame.Image is MatFrameImage matImage ? matImage.Mat : null;

    private IReadOnlyList<Detection> Detect(
        Mat frameBgr, int frameWidth, int frameHeight, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using LetterboxResult lb = LetterboxResize.Apply(frameBgr, _netSize, _netSize);
        DenseTensor<float> input = Preprocess(lb.Padded);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, input),
        };

        cancellationToken.ThrowIfCancellationRequested();

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        Tensor<float> output = results.First().AsTensor<float>();

        var candidates = DecodeTransposed(output, _scoreThresh, _classId);
        var kept = NonMaxSuppression(candidates, _nmsThresh);

        var detections = new List<Detection>(kept.Count);
        foreach (var (tlbr, score, _) in kept)
        {
            // Network pixels -> original image pixels -> normalised 0..1.
            double x1 = tlbr[0] / lb.Ratio / frameWidth;
            double y1 = tlbr[1] / lb.Ratio / frameHeight;
            double x2 = tlbr[2] / lb.Ratio / frameWidth;
            double y2 = tlbr[3] / lb.Ratio / frameHeight;

            detections.Add(new Detection(
                _className,
                score,
                new BoundingBox(x1, y1, x2 - x1, y2 - y1)));
        }

        return detections;
    }

    /// <summary>
    /// BGR Mat -> CHW float tensor with BGR-&gt;RGB and /255. YOLOv8 exports fold
    /// normalisation into the graph, so there is no ImageNet mean/std here.
    /// </summary>
    private static DenseTensor<float> Preprocess(Mat padded)
    {
        int h = padded.Height;
        int w = padded.Width;
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });

        padded.GetArray(out Vec3b[] pixels);

        // Write through the flat backing buffer rather than tensor[0, c, y, x].
        // The multi-dimensional indexer recomputes strides per element, and at
        // 640x640x3 that arithmetic - not the arithmetic we actually want - was
        // costing ~48 ms per frame. Once inference moved to the GPU that was over
        // a sixth of the frame budget. The plane offsets below are the same strides,
        // hoisted out of the loop.
        Span<float> data = tensor.Buffer.Span;
        int plane = h * w;
        int gPlane = plane;
        int bPlane = plane * 2;

        for (int i = 0; i < plane; i++)
        {
            Vec3b p = pixels[i];
            // p.Item0=B, p.Item1=G, p.Item2=R; the tensor wants RGB.
            data[i] = p.Item2 / 255f;
            data[gPlane + i] = p.Item1 / 255f;
            data[bPlane + i] = p.Item0 / 255f;
        }

        return tensor;
    }

    /// <summary>
    /// Decodes raw YOLOv8 output <c>[1, 4 + numClasses, numAnchors]</c> into
    /// corner boxes in network-pixel space, keeping only anchors whose best class
    /// is <paramref name="classId"/> and scores at least <paramref name="scoreThresh"/>.
    /// </summary>
    /// <remarks>
    /// Note the transposed layout: channel is the middle dimension, anchor the
    /// last. Channels 0..3 are cx, cy, w, h in network pixels — already decoded by
    /// the export, so there is no grid offset, no stride multiply and no exp().
    /// Channels 4.. are per-class scores; the detection score is simply the best
    /// of them, because YOLOv8 has no objectness channel to multiply in.
    /// <paramref name="classId"/> of -1 keeps every class.
    /// </remarks>
    internal static List<(double[] Tlbr, float Score, int Cls)> DecodeTransposed(
        Tensor<float> output, float scoreThresh, int classId)
    {
        if (output.Dimensions.Length != 3)
        {
            throw new InvalidOperationException(
                $"Expected a 3-D YOLOv8 output [1, 4+numClasses, numAnchors], " +
                $"got {output.Dimensions.Length} dimensions.");
        }

        int channels = output.Dimensions[1];
        int numAnchors = output.Dimensions[2];
        int numClasses = channels - 4;

        if (numClasses < 1)
        {
            throw new InvalidOperationException(
                $"Expected at least 5 channels (4 box + classes), got {channels}. " +
                "A [1, numAnchors, 5+numClasses] shape means a YOLOX model, not YOLOv8.");
        }

        var candidates = new List<(double[] Tlbr, float Score, int Cls)>();

        // Same reason as Preprocess: output[0, c, i] recomputes strides on every
        // read, and this loop reads 8400 anchors x 84 channels. Going through the
        // flat buffer turns the stride arithmetic into one addition. A DenseTensor
        // is the common case; anything else (a test-built tensor of another kind)
        // falls back to the indexer rather than assuming a layout.
        var dense = output as DenseTensor<float>;
        ReadOnlySpan<float> flat = dense is not null ? dense.Buffer.Span : default;
        bool useFlat = dense is not null;

        for (int i = 0; i < numAnchors; i++)
        {
            int bestCls = 0;
            float bestScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float cs = useFlat ? flat[(4 + c) * numAnchors + i] : output[0, 4 + c, i];
                if (cs > bestScore)
                {
                    bestScore = cs;
                    bestCls = c;
                }
            }

            if (bestScore < scoreThresh)
            {
                continue;
            }

            if (classId >= 0 && bestCls != classId)
            {
                continue;
            }

            // Already in network pixels: centre/size straight to corners.
            double cx = useFlat ? flat[i] : output[0, 0, i];
            double cy = useFlat ? flat[numAnchors + i] : output[0, 1, i];
            double bw = useFlat ? flat[2 * numAnchors + i] : output[0, 2, i];
            double bh = useFlat ? flat[3 * numAnchors + i] : output[0, 3, i];

            candidates.Add((
                new[] { cx - bw / 2.0, cy - bh / 2.0, cx + bw / 2.0, cy + bh / 2.0 },
                bestScore,
                bestCls));
        }

        return candidates;
    }

    /// <summary>Greedy per-class NMS; returns the surviving boxes.</summary>
    internal static List<(double[] Tlbr, float Score, int Cls)> NonMaxSuppression(
        List<(double[] Tlbr, float Score, int Cls)> candidates, float nmsThresh)
    {
        var kept = new List<(double[] Tlbr, float Score, int Cls)>();

        foreach (int cls in candidates.Select(c => c.Cls).Distinct())
        {
            var byClass = candidates
                .Where(c => c.Cls == cls)
                .OrderByDescending(c => c.Score)
                .ToList();

            var suppressed = new bool[byClass.Count];
            for (int i = 0; i < byClass.Count; i++)
            {
                if (suppressed[i])
                {
                    continue;
                }

                kept.Add(byClass[i]);

                for (int j = i + 1; j < byClass.Count; j++)
                {
                    if (suppressed[j])
                    {
                        continue;
                    }

                    if (Iou(byClass[i].Tlbr, byClass[j].Tlbr) > nmsThresh)
                    {
                        suppressed[j] = true;
                    }
                }
            }
        }

        return kept;
    }

    internal static double Iou(double[] a, double[] b)
    {
        double interX1 = Math.Max(a[0], b[0]);
        double interY1 = Math.Max(a[1], b[1]);
        double interX2 = Math.Min(a[2], b[2]);
        double interY2 = Math.Min(a[3], b[3]);

        double interW = Math.Max(0.0, interX2 - interX1);
        double interH = Math.Max(0.0, interY2 - interY1);
        double inter = interW * interH;

        double areaA = Math.Max(0.0, a[2] - a[0]) * Math.Max(0.0, a[3] - a[1]);
        double areaB = Math.Max(0.0, b[2] - b[0]) * Math.Max(0.0, b[3] - b[1]);
        double union = areaA + areaB - inter;

        return union <= 0.0 ? 0.0 : inter / union;
    }

    public void Dispose() => _session.Dispose();
}
