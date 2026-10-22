using EdgeForge.Core;
using EdgeForge.Decision;
using EdgeForge.Vision;

namespace EdgeForge.Runtime;

/// <summary>
/// The parts of the system that change with what the camera is watching, built
/// from configuration: a detector, a per-frame rule, and a decision layer.
/// </summary>
/// <remarks>
/// Hosts - the headless demo, the viewer, the API - own capture, the pipeline and
/// the display, and never name a scenario. They hand in their own innermost
/// action (a console writer, a viewer log) and get back a pipeline-ready set.
/// Adding what the line inspects is configuration: a profile with limits, reject
/// classes and a detector. Adding a new kind of question - not "is this item in
/// spec" but something else - is a new case here and a new sink, and nothing in
/// the hosts.
/// </remarks>
public sealed class Scenario : IDisposable
{
    private Scenario(
        string name,
        IVisionDetector detector,
        IInspector inspector,
        IInspectionAction action,
        IDecisionSink? sink,
        ItemRule? rule,
        LineOptions? line)
    {
        Name = name;
        Detector = detector;
        Inspector = inspector;
        Action = action;
        Sink = sink;
        Rule = rule;
        Line = line;
    }

    /// <summary>See <see cref="ScenarioNames"/>.</summary>
    public string Name { get; }

    public IVisionDetector Detector { get; }

    public IInspector Inspector { get; }

    /// <summary>The action to give the pipeline: the host's own, wrapped by the decision layer.</summary>
    public IInspectionAction Action { get; }

    /// <summary>The decision layer, or null when it is switched off.</summary>
    public IDecisionSink? Sink { get; }

    /// <summary>The acceptance rule, for a viewer colouring boxes. Null outside the line scenario.</summary>
    public ItemRule? Rule { get; }

    /// <summary>Line geometry, for a viewer drawing the decision line. Null outside the line scenario.</summary>
    public LineOptions? Line { get; }

    /// <summary>
    /// Builds the scenario named in <paramref name="options"/>.
    /// </summary>
    /// <param name="inner">The host's own innermost action.</param>
    /// <param name="baseDirectory">Where journals go when their paths are not configured.</param>
    /// <param name="notify">Log callback for decision-layer messages.</param>
    /// <exception cref="InvalidOperationException">The scenario or a section it uses is not usable.</exception>
    /// <exception cref="FileNotFoundException">The model file is missing.</exception>
    public static Scenario Create(
        EdgeForgeOptions options,
        IInspectionAction inner,
        string baseDirectory,
        Action<string>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inner);

        options.ValidateScenario();

        var detector = DetectorFactory.CreateDetector(options.Detector, options.Blob);

        try
        {
            return options.Scenario == ScenarioNames.Line
                ? CreateLine(options, detector, inner, baseDirectory, notify)
                : CreateBowls(options, detector, inner, baseDirectory, notify);
        }
        catch
        {
            (detector as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>Lines for a startup banner: scenario, model, rule.</summary>
    public IReadOnlyList<string> Describe(EdgeForgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lines = new List<string>
        {
            $"Scenario: {Name}",
            $"Model: {DetectorFactory.Describe(options.Detector, Detector)}"
        };

        if (Name == ScenarioNames.Bowls)
        {
            lines.Add($"Meals: {MealSinkFactory.Describe(options.Meals)}");
        }
        else if (Line is not null && Rule is not null)
        {
            lines.Add($"Line: decision at {Line.DecisionLine:P0}, {Line.Direction}");
            lines.Add(Rule.Limits.Count == 0 && Line.RejectClasses.Length == 0
                ? "Rule: none - counting only"
                : "Rule: " + string.Join("; ", Rule.Limits.Select(DescribeLimit)
                    .Concat(Line.RejectClasses.Select(c => $"reject {c}"))));
        }

        return lines;
    }

    private static Scenario CreateBowls(
        EdgeForgeOptions options,
        IVisionDetector detector,
        IInspectionAction inner,
        string baseDirectory,
        Action<string>? notify)
    {
        // The placeholder detector emits label classes, so it keeps the label rule
        // it was written for; every real detector gets presence of its class.
        IInspector inspector = detector is SampleDetector
            ? new LabelInspector()
            : new PresenceInspector(ClassName(options));

        IInspectionAction action = inner;
        try
        {
            action = MealSinkFactory.Create(
                inner, options.Meals, ClassName(options), baseDirectory, notify);
        }
        catch (InvalidOperationException ex)
        {
            // A bad meal threshold turns counting off rather than stopping the
            // host: the viewer is still needed for aiming the camera. Hosts that
            // should refuse instead validate Meals before they get here.
            notify?.Invoke($"Meal counting off: {ex.Message}");
        }

        return new Scenario(
            ScenarioNames.Bowls, detector, inspector, action, action as IDecisionSink, null, null);
    }

    private static Scenario CreateLine(
        EdgeForgeOptions options,
        IVisionDetector detector,
        IInspectionAction inner,
        string baseDirectory,
        Action<string>? notify)
    {
        var line = options.Line;
        var rule = ItemRule.From(line);

        var journalPath = string.IsNullOrWhiteSpace(line.JournalPath)
            ? Path.Combine(baseDirectory, "items.jsonl")
            : line.JournalPath;

        var sink = new LineEventSink(
            inner,
            new LineCounter(line, rule),
            new ItemJournal(journalPath),
            notify);

        return new Scenario(
            ScenarioNames.Line, detector, new ItemRuleInspector(rule), sink, sink, rule, line);
    }

    private static string ClassName(EdgeForgeOptions options) =>
        options.Detector.Kind == DetectorKinds.Blob ? options.Blob.ClassName : options.Detector.ClassName;

    private static string DescribeLimit(LimitOptions limit)
    {
        var range = (limit.Min, limit.Max) switch
        {
            ({ } min, { } max) => $"{min}..{max}",
            ({ } min, null) => $">= {min}",
            (null, { } max) => $"<= {max}",
            _ => "any"
        };

        var reason = string.IsNullOrWhiteSpace(limit.Reason) ? "" : $" else {limit.Reason}";
        return $"{limit.Measurement} {range}{reason}";
    }

    public void Dispose() => (Detector as IDisposable)?.Dispose();
}
