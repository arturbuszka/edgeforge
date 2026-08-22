using Avalonia.Controls;

namespace EdgeForge.Ui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;

    // Parameterless ctor exists for the XAML previewer only.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        // Boxes are positioned in control pixels, so they have to be recomputed
        // whenever a new frame arrives OR the control they sit on changes size.
        viewModel.FrameReady += ProjectBoxes;
        viewModel.FrameReady += RedrawVideo;
        Video.SizeChanged += (_, _) => ProjectBoxes();
    }

    private void ProjectBoxes() => _viewModel?.ProjectBoxes(Video.Bounds.Size);

    /// <summary>
    /// Tells the Image to repaint after the view-model has written new pixels.
    /// </summary>
    /// <remarks>
    /// The WriteableBitmap is deliberately reused frame to frame, so the binding
    /// never sees Source change and Avalonia has no reason to redraw: the pipeline
    /// runs, the statistics climb, and the picture sits on the first frame forever.
    /// Writing into a bitmap behind a binding is invisible to the framework, so the
    /// invalidation has to be explicit.
    /// </remarks>
    private void RedrawVideo() => Video.InvalidateVisual();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is null)
        {
            base.OnClosing(e);
            return;
        }

        // Tearing the window down while the capture thread still holds native Mats
        // is how this leaks. Cancel first, let the loop unwind, then really close.
        if (_viewModel.IsRunning)
        {
            e.Cancel = true;
            await _viewModel.StopAsync();
            Close();
            return;
        }

        _viewModel.FrameReady -= ProjectBoxes;
        _viewModel.FrameReady -= RedrawVideo;
        _viewModel.Dispose();

        base.OnClosing(e);
    }
}
