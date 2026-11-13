using System.Windows;
using System.Windows.Controls;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>« Ajouter un jeu ouvert… »: a menu of the programs that have a window right now.</summary>
    private void OnAddRunningGameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Button button) return;
        vm.RefreshRunningAppsCommand.Execute(null);
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (vm.RunningApps.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Aucun programme ouvert (lancez le jeu, ou choisissez son programme)", IsEnabled = false });
        foreach (var app in vm.RunningApps)
        {
            var item = new MenuItem { Header = app.Label };
            item.Click += (_, _) => vm.AddRunningGameCommand.Execute(app);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
