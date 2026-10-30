using System.Windows;
using System.Windows.Controls;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class SoundboardView : UserControl
{
    public SoundboardView() => InitializeComponent();

    private void OnPackMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || LinkBar.GetDroppedUrl(e.Data) != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not SoundboardViewModel vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) vm.AddFiles(files);
        else if (LinkBar.GetDroppedUrl(e.Data) is { } url) _ = vm.Link.ImportAsync(url);
    }
}
