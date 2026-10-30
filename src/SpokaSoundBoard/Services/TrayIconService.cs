using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SpokaSoundBoard.Services;

/// <summary>Notification-area (systray) icon with a quick-actions menu.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _micItem;
    private readonly Forms.ToolStripMenuItem _effectsItem;
    private readonly Forms.ToolStripMenuItem _discordItem;

    public TrayIconService(TrayActions actions)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var open = new Forms.ToolStripMenuItem("Ouvrir SpokaSoundBoard", null, (_, _) => actions.Show());
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        _micItem = new Forms.ToolStripMenuItem("Micro actif", null, (_, _) => actions.ToggleMic());
        _effectsItem = new Forms.ToolStripMenuItem("Effets de voix", null, (_, _) => actions.ToggleEffects());
        _discordItem = new Forms.ToolStripMenuItem("Musique et sons aussi sur Discord", null, (_, _) => actions.ToggleDiscordShare());
        menu.Items.AddRange(
        [
            open,
            new Forms.ToolStripSeparator(),
            _micItem,
            _effectsItem,
            _discordItem,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Lecture / pause musique", null, (_, _) => actions.PlayPause()),
            new Forms.ToolStripMenuItem("Arrêter tous les sons", null, (_, _) => actions.StopAll()),
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Quitter", null, (_, _) => actions.Exit()),
        ]);
        menu.Opening += (_, _) =>
        {
            _micItem.Checked = actions.IsMicEnabled();
            _effectsItem.Checked = actions.AreEffectsEnabled();
            _discordItem.Checked = actions.IsSharingWithDiscord();
            _discordItem.Visible = actions.HasDiscordOutput();
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "SpokaSoundBoard",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) actions.Show();
        };
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private static Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource != null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (IOException)
        {
        }
        return Icon.ExtractAssociatedIcon(WindowsIntegration.ExecutablePath) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

public sealed record TrayActions(
    Action Show,
    Action Exit,
    Action ToggleMic,
    Action ToggleEffects,
    Action ToggleDiscordShare,
    Action PlayPause,
    Action StopAll,
    Func<bool> IsMicEnabled,
    Func<bool> AreEffectsEnabled,
    Func<bool> IsSharingWithDiscord,
    Func<bool> HasDiscordOutput);
