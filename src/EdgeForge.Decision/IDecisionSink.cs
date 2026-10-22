using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>One labelled figure for a live readout: a console summary or a viewer row.</summary>
public readonly record struct ReadoutLine(string Label, string Value);

/// <summary>
/// A scenario's decision layer as the hosts see it: an action in the pipeline,
/// something to close at end of stream, and a few figures to show.
/// </summary>
/// <remarks>
/// This is what lets the demo and the viewer run any scenario without knowing
/// which one it is. Anything scenario-specific beyond these three - the meal API,
/// the SQLite mirror - is reached through the concrete type by the one host that
/// needs it.
/// </remarks>
public interface IDecisionSink : IInspectionAction
{
    /// <summary>
    /// Closes anything still in progress. For shutdown and end of stream; must be
    /// safe to call more than once.
    /// </summary>
    void Flush();

    /// <summary>The figures worth showing right now. Safe to call from any thread.</summary>
    IReadOnlyList<ReadoutLine> Readout();
}
