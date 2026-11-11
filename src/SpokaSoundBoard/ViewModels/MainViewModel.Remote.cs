using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Remote;

namespace SpokaSoundBoard.ViewModels;

/// <summary>« Télécommande » : Stream Deck, Touch Portal, OBS or a phone trigger the soundboard over HTTP.</summary>
public partial class MainViewModel
{
    private RemoteServer? _remote;

    public RemoteSettings RemoteSettings => Settings.Remote;

    [ObservableProperty] private string _remoteStatus = "";
    [ObservableProperty] private string _remoteLocalUrl = "";
    [ObservableProperty] private string _remoteNetworkUrl = "";
    [ObservableProperty] private BitmapSource? _remoteQrCode;

    public bool HasRemoteNetworkUrl => RemoteNetworkUrl.Length > 0;

    partial void OnRemoteNetworkUrlChanged(string value) => OnPropertyChanged(nameof(HasRemoteNetworkUrl));

    private void InitializeRemote()
    {
        RemoteSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Core.Models.RemoteSettings.Enabled) or nameof(Core.Models.RemoteSettings.AllowNetwork)
                or nameof(Core.Models.RemoteSettings.Port))
                ApplyRemote();
            Save();
        };
        ApplyRemote();
    }

    /// <summary>Starts, restarts or stops the server to match the settings.</summary>
    private void ApplyRemote()
    {
        _remote?.Stop();
        RemoteQrCode = null;
        RemoteNetworkUrl = "";
        RemoteLocalUrl = "";
        if (!RemoteSettings.Enabled)
        {
            RemoteStatus = "Désactivée.";
            return;
        }
        _remote ??= new RemoteServer(request => RemoteApi.HandleAsync(request, new RemoteActions(this)));
        _remote.Code = RemoteSettings.Code;
        try
        {
            _remote.Start(RemoteSettings.Port, RemoteSettings.AllowNetwork);
        }
        catch (SocketException)
        {
            RemoteStatus = $"Le port {RemoteSettings.Port} est déjà utilisé par une autre application : choisissez-en un autre.";
            return;
        }
        RemoteLocalUrl = $"http://127.0.0.1:{_remote.Port}/?code={RemoteSettings.Code}";
        if (RemoteSettings.AllowNetwork && LocalNetworkAddress() is { } ip)
        {
            RemoteNetworkUrl = $"http://{ip}:{_remote.Port}/?code={RemoteSettings.Code}";
            RemoteQrCode = QrCode(RemoteNetworkUrl);
            RemoteStatus = "Active sur ce PC et sur votre réseau local. Scannez le QR code avec votre téléphone (même Wi-Fi). " +
                           "Si Windows le demande, autorisez SpokaSoundBoard sur les réseaux privés.";
        }
        else
        {
            RemoteStatus = RemoteSettings.AllowNetwork
                ? "Active sur ce PC, mais aucun réseau local n'a été trouvé pour le téléphone."
                : "Active sur ce PC (Stream Deck, Touch Portal, OBS…).";
        }
    }

    [RelayCommand]
    private void NewRemoteCode()
    {
        RemoteSettings.Code = RemoteServer.NewCode();
        Save();
        ApplyRemote();
        ShowToast("Nouveau code d'accès : les anciennes adresses ne fonctionnent plus.");
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowToast("Copié.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowToast("Le presse-papiers est occupé : réessayez.");
        }
    }

    [RelayCommand]
    private void OpenRemotePage()
    {
        if (RemoteLocalUrl.Length > 0) Services.WindowsIntegration.Open(RemoteLocalUrl);
    }

    /// <summary>The PC's address on the home network (Wi-Fi or Ethernet), private ranges first.</summary>
    private static IPAddress? LocalNetworkAddress()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
            static IEnumerable<IPAddress> V4(IEnumerable<NetworkInterface> list) => list
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            // The home network has a router (gateway); a private address without one (hotspot, cable) is the fallback.
            var routed = V4(up.Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)).ToList();
            return routed.FirstOrDefault(IsPrivate) ?? routed.FirstOrDefault() ?? V4(up).FirstOrDefault(IsPrivate);
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
        }
    }

    private static BitmapSource? QrCode(string text)
    {
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private void DisposeRemote() => _remote?.Stop();

    /// <summary>Remote actions, run on the UI thread like a click.</summary>
    private sealed class RemoteActions(MainViewModel vm) : IRemoteActions
    {
        private Task<T> Ui<T>(Func<T> f) => vm._dispatcher.InvokeAsync(f).Task;
        private Task Ui(Action a) => vm._dispatcher.InvokeAsync(a).Task;

        public Task<IReadOnlyList<RemotePad>> PadsAsync() => Ui<IReadOnlyList<RemotePad>>(() =>
        {
            var playing = vm.Engine.Pads.PlayingIds();
            vm.Engine.PrivatePads.FillPlayingIds(playing, clear: false);
            return vm.Settings.Pads.Select(p => new RemotePad(p.Id, p.Name, p.Color, p.Category, playing.Contains(p.Id),
                p.Hotkey.IsSet ? p.Hotkey.Display : null)).ToList();
        });

        public Task<bool> PlayPadAsync(string idOrName) => Ui(() =>
        {
            var pad = RemoteApi.FindPad(vm.Settings.Pads, idOrName, p => p.Id, p => p.Name);
            if (pad == null) return false;
            vm.Soundboard.PlayPadCommand.Execute(pad);
            return true;
        });

        public Task StopAllAsync() => Ui(() => vm.Soundboard.StopAllCommand.Execute(null));
        public Task ToggleMicAsync() => Ui(vm.ToggleMic);
        public Task ToggleEffectsAsync() => Ui(vm.ToggleEffects);
        public Task CyclePresetAsync(int direction) => Ui(() => vm.Voice.CyclePreset(direction));
        public Task MusicPlayPauseAsync() => Ui(() => vm.Music.PlayPauseCommand.Execute(null));
        public Task MusicNextAsync() => Ui(() => vm.Music.NextCommand.Execute(null));

        public Task<RemoteStatus> StatusAsync() => Ui(() => new RemoteStatus(vm.Mix.MicEnabled, vm.VoiceSettings.EffectsEnabled,
            vm.Voice.SelectedPresetName ?? "Voix normale", vm.Music.CurrentTrack?.Title, vm.Music.IsPlaying));
    }
}
