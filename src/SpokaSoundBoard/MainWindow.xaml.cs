using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        Loaded += (_, _) => _vm.Initialize();
        Closed += (_, _) => _vm.Dispose();
    }

    private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragging = true;
        _vm.Music.IsSeeking = true;
    }

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        _vm.Music.SeekTo(SeekSlider.Value);
        _vm.Music.IsSeeking = false;
    }

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e) => _vm.Music.IsSeeking = true;

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // A simple click on the bar (no thumb drag) also seeks.
        if (_dragging) return;
        _vm.Music.SeekTo(SeekSlider.Value);
        _vm.Music.IsSeeking = false;
    }
}
