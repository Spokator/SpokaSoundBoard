using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.ViewModels.Music;

namespace SpokaSoundBoard.Views;

public partial class MusicView : UserControl
{
    private const string ReorderFormat = "SpokaSoundBoard.Reorder";
    private Point _dragStart;
    private TrackItem? _dragItem;

    public MusicView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MusicViewModel old) old.Lyrics.CurrentLineChanged -= OnLyricLine;
            if (e.NewValue is MusicViewModel vm) vm.Lyrics.CurrentLineChanged += OnLyricLine;
        };
    }

    private MusicViewModel? Vm => DataContext as MusicViewModel;

    // ---- External drops: files, folders, web links ----------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ReorderFormat)) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || LinkBar.GetDroppedUrl(e.Data) != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ReorderFormat)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm?.AddPaths(files);
        else if (Vm != null && LinkBar.GetDroppedUrl(e.Data) is { } url) _ = Vm.Search.HandleUrlAsync(url);
    }

    // ---- Track list -------------------------------------------------------------------------------------------

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is TrackItem track) Vm?.PlayTrackCommand.Execute(track);
    }

    private void OnTrackKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.Key == Key.Enter) Vm.PlayTrackCommand.Execute(Vm.SelectedTrack);
        else if (e.Key == Key.Delete) Vm.RemoveTrackCommand.Execute(Vm.SelectedTrack);
    }

    /// <summary>"⋯" button: opens the row's context menu under the button.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var row = FindAncestorWithMenu(button);
        if (row?.ContextMenu == null) return;
        row.ContextMenu.PlacementTarget = row;
        row.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        row.ContextMenu.IsOpen = true;
    }

    private static FrameworkElement? FindAncestorWithMenu(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is FrameworkElement { ContextMenu: not null } fe) return fe;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ---- Search -----------------------------------------------------------------------------------------------

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.Key == Key.Enter)
        {
            Vm.Search.SubmitCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.Search.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Keep the suggestions open when the focus moves into them (click on a suggestion).
        if (e.NewFocus is DependencyObject d && SuggestPopup.Child != null && IsDescendant(SuggestPopup.Child, d)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Vm != null && !SuggestPopup.IsMouseOver) Vm.Search.ShowSuggestions = false;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool IsDescendant(DependencyObject root, DependencyObject node)
    {
        for (var n = node; n != null; n = VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
            if (n == root) return true;
        return false;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is MusicResultViewModel result) Vm?.Search.PlayCommand.Execute(result);
    }

    private void OnResultMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        button.Tag = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    // ---- Playlist name editor ---------------------------------------------------------------------------------

    private void OnPlaylistNameKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.Key == Key.Enter) Vm.ConfirmPlaylistNameCommand.Execute(null);
        else if (e.Key == Key.Escape) Vm.CancelPlaylistNameCommand.Execute(null);
    }

    private void OnPlaylistNameVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() =>
            {
                PlaylistNameBox.Focus();
                PlaylistNameBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---- Queue ------------------------------------------------------------------------------------------------

    private void OnQueueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is TrackItem track) Vm?.JumpToQueueItemCommand.Execute(track);
    }

    private void OnQueueKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || QueueList.SelectedItem is not TrackItem track) return;
        if (e.Key == Key.Enter) Vm.JumpToQueueItemCommand.Execute(track);
        else if (e.Key == Key.Delete) Vm.RemoveFromQueueCommand.Execute(track);
    }

    // ---- Drag-and-drop reordering (queue and playlists) -----------------------------------------------------------

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = IsOnButton(e.OriginalSource) ? null : ItemUnder(e.OriginalSource) as TrackItem;
    }

    private void OnQueueMouseMove(object sender, MouseEventArgs e) => TryStartDrag(QueueList, e);

    private void OnTrackListMouseMove(object sender, MouseEventArgs e)
    {
        // Only playlists have a user-defined order.
        if (Vm?.IsPlaylist == true) TryStartDrag(TrackList, e);
    }

    private void TryStartDrag(ListBox list, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var item = _dragItem;
        _dragItem = null;
        var data = new DataObject(ReorderFormat, item);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
    }

    private void OnReorderDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ReorderFormat)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnQueueDrop(object sender, DragEventArgs e)
    {
        if (Vm == null || e.Data.GetData(ReorderFormat) is not TrackItem item) return;
        e.Handled = true;
        var from = Vm.Queue.Items.IndexOf(item);
        var to = TargetIndex(QueueList, e, Vm.Queue.Count);
        if (from >= 0 && to >= 0) Vm.MoveQueueItem(from, Math.Min(to, Vm.Queue.Count - 1));
    }

    private void OnTrackListDrop(object sender, DragEventArgs e)
    {
        if (Vm == null || e.Data.GetData(ReorderFormat) is not TrackItem item) return;
        e.Handled = true;
        var from = Vm.VisibleTracks.IndexOf(item);
        var to = TargetIndex(TrackList, e, Vm.VisibleTracks.Count);
        if (from >= 0 && to >= 0) Vm.MoveInPlaylist(from, Math.Min(to, Vm.VisibleTracks.Count - 1));
    }

    private static int TargetIndex(ListBox list, DragEventArgs e, int count)
    {
        var element = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        var container = element != null ? ItemsControl.ContainerFromElement(list, element) as ListBoxItem : null;
        return container != null ? list.ItemContainerGenerator.IndexFromContainer(container) : count - 1;
    }

    private static object? ItemUnder(object source) =>
        source is FrameworkElement fe ? fe.DataContext : source is FrameworkContentElement fce ? fce.DataContext : null;

    private static bool IsOnButton(object source)
    {
        for (var d = source as DependencyObject; d != null && d is not ListBoxItem; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    // ---- Lyrics -----------------------------------------------------------------------------------------------

    private void OnLyricLine(LyricLineViewModel line)
    {
        if (!LyricsList.IsVisible) return;
        LyricsList.ScrollIntoView(line);
        // Centre the current line rather than leaving it at the edge.
        if (LyricsList.ItemContainerGenerator.ContainerFromItem(line) is FrameworkElement container
            && FindScrollViewer(LyricsList) is { } scroller)
        {
            var position = container.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
            var target = scroller.VerticalOffset + position - scroller.ViewportHeight / 2 + container.ActualHeight / 2;
            scroller.ScrollToVerticalOffset(Math.Max(0, target));
        }
    }

    private void OnLyricClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LyricLineViewModel line }) Vm?.Lyrics.SeekToLineCommand.Execute(line);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
