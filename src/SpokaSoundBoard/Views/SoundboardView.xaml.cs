using System.Windows;
using System.Windows.Controls;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class SoundboardView : UserControl
{
    public SoundboardView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is SoundboardViewModel vm)
            vm.AddFiles(files);
    }
}
