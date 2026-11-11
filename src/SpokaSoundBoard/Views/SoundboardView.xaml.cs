using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class SoundboardView : UserControl
{
    private const string PadFormat = "SpokaSoundBoard.Pad";

    private Point _dragStart;
    private SoundPad? _dragCandidate;

    public SoundboardView() => InitializeComponent();

    private SoundboardViewModel? Vm => DataContext as SoundboardViewModel;

    private void OnPackMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnCategoryBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
    }

    /// <summary>Checked rather than Command: also works when a screen reader selects the tab.</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PadTab tab && Vm is { } vm && vm.SelectedCategory != tab.Key)
            vm.SelectCategoryCommand.Execute(tab.Key);
    }

    // ---- Files and links dropped on the page --------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || LinkBar.GetDroppedUrl(e.Data) != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.GetDataPresent(PadFormat)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) vm.AddFiles(files);
        else if (LinkBar.GetDroppedUrl(e.Data) is { } url)
        {
            vm.ShowSoundSearch();
            _ = vm.Link.ImportAsync(url);
        }
    }

    // ---- Dragging a sound: onto another sound to reorder, onto a tab to file it ---------------------------------

    private void OnPadMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as SoundPad;
    }

    private void OnPadMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var pad = _dragCandidate;
        _dragCandidate = null;
        // The drag takes the mouse: the button does not see a click, so the sound does not play.
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(PadFormat, pad), DragDropEffects.Move);
    }

    private static SoundPad? DraggedPad(DragEventArgs e) => e.Data.GetData(PadFormat) as SoundPad;

    private void OnPadDragOver(object sender, DragEventArgs e)
    {
        if (DraggedPad(e) is null) return; // files and links: handled by the page
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPadDrop(object sender, DragEventArgs e)
    {
        if (DraggedPad(e) is not { } pad) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is SoundPad target && !ReferenceEquals(target, pad))
            Vm?.MovePad(pad, target);
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        if (DraggedPad(e) is null) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        if (DraggedPad(e) is not { } pad) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is PadTab tab) Vm?.MoveToCategory(pad, tab.Key);
    }
}
