using System.Windows;
using System.Windows.Controls;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class VoiceView : UserControl
{
    public VoiceView() => InitializeComponent();

    /// <summary>Opening the page looks for the graphics card once, for the AI voice changer.</summary>
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is MainViewModel vm) vm.VoiceChanger.ProbeGpu();
    }
}
