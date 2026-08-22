namespace EdgeForge.Ui;

/// <summary>
/// One "label: value" line in the stats panel.
/// </summary>
/// <remarks>
/// The panel is data-driven rather than a hand-laid grid of TextBlocks because the
/// list of things worth watching is expected to keep growing — per-cat counts,
/// zone dwell time, meals per day. Adding one should be a line in the view-model,
/// not a XAML edit.
/// </remarks>
public sealed class StatRow : ViewModelBase
{
    private string _value = "—";

    public StatRow(string label) => Label = label;

    public string Label { get; }

    public string Value { get => _value; set => SetField(ref _value, value); }
}
