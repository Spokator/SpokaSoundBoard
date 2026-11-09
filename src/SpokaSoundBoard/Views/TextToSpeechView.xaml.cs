using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SpokaSoundBoard.Views;

/// <summary>"Synthèse vocale" page of the main menu.</summary>
public partial class TextToSpeechView : UserControl
{
    public TextToSpeechView() => InitializeComponent();

    /// <summary>Opening the page puts the cursor in the phrase box: type, Enter, done.</summary>
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => PhraseBox.Focus(), DispatcherPriority.Input);
    }
}
