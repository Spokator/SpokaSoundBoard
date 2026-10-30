using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class LinkBar : UserControl
{
    public LinkBar()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(() => UrlBox.Focus());
        };
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not LinkImporter vm) return;
        if (e.Key == Key.Enter && vm.ImportCommand.CanExecute(null)) vm.ImportCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.IsOpen = false;
    }

    /// <summary>Returns a web link carried by a drag-and-drop from a browser, if any.</summary>
    public static string? GetDroppedUrl(IDataObject data)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text })
        {
            if (!data.GetDataPresent(format)) continue;
            var value = data.GetData(format) switch
            {
                string s => s,
                System.IO.MemoryStream ms => format.EndsWith('W')
                    ? System.Text.Encoding.Unicode.GetString(ms.ToArray())
                    : System.Text.Encoding.ASCII.GetString(ms.ToArray()),
                _ => null,
            };
            value = value?.Split('\0', '\r', '\n')[0].Trim();
            if (Core.Online.MediaDownloader.LooksLikeUrl(value)) return value;
        }
        return null;
    }
}
