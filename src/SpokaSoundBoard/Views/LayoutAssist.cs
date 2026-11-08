using System.Windows;

namespace SpokaSoundBoard.Views;

/// <summary>
/// Inherited flag telling templates that their container is in compact (icons only) mode,
/// so a generic style can hide its label without knowing about any view model.
/// </summary>
public static class LayoutAssist
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
        "Compact", typeof(bool), typeof(LayoutAssist),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);

    public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);
}
