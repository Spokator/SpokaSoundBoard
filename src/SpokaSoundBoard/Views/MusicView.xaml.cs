using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class MusicView : UserControl
{
    public MusicView() => InitializeComponent();

    private MusicViewModel? Vm => DataContext as MusicViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm?.AddPaths(files);
    }

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Core.Models.TrackItem track })
            Vm?.PlayTrackCommand.Execute(track);
    }

    private void OnTrackKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.Key == Key.Enter) Vm.PlayTrackCommand.Execute(Vm.SelectedTrack);
        else if (e.Key == Key.Delete) Vm.RemoveTrackCommand.Execute(Vm.SelectedTrack);
    }
}
