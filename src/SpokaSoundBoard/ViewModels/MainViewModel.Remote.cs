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
        RemoteTalk(false);
        _remoteClientsTimer?.Stop();
        RemoteClientsText = "";
        if (!RemoteSettings.Enabled)
        {
            RemoteStatus = "Désactivée.";
            return;
        }
        _remoteClientsTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(3),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => UpdateRemoteClients(), _dispatcher);
        if (_remote == null)
        {
            _remote = new RemoteServer(request => RemoteApi.HandleAsync(request, new RemoteActions(this)));
            _remote.ClientConnected += OnRemoteClientConnected;
        }
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
        UpdateRemoteClients();
        _remoteClientsTimer.Start();
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

    private void DisposeRemote()
    {
        RemoteTalk(false);
        _remote?.Stop();
    }

    // ---- Phone push-to-talk: released by itself if the phone stops repeating « down » (lost Wi-Fi, locked screen) ----

    private DateTime _remoteTalkUntil;
    private System.Windows.Threading.DispatcherTimer? _remoteTalkWatch;

    private void RemoteTalk(bool down)
    {
        if (!down)
        {
            Engine.Core.RemotePushToTalkHeld = false;
            _remoteTalkWatch?.Stop();
            return;
        }
        _remoteTalkUntil = DateTime.UtcNow.AddMilliseconds(900);
        Engine.Core.RemotePushToTalkHeld = true;
        _remoteTalkWatch ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200),
            System.Windows.Threading.DispatcherPriority.Normal, (_, _) =>
            {
                if (DateTime.UtcNow <= _remoteTalkUntil) return;
                Engine.Core.RemotePushToTalkHeld = false;
                _remoteTalkWatch!.Stop();
            }, _dispatcher);
        _remoteTalkWatch.Start();
    }

    // ---- Connected devices ---------------------------------------------------------------------------------------

    [ObservableProperty] private string _remoteClientsText = "";
    private System.Windows.Threading.DispatcherTimer? _remoteClientsTimer;

    private void OnRemoteClientConnected(IPAddress address) => _dispatcher.BeginInvoke(() =>
    {
        var who = IPAddress.IsLoopback(address) ? "sur ce PC" : $"depuis {address}";
        ShowToast($"Télécommande connectée {who}.");
        UpdateRemoteClients();
    });

    private void UpdateRemoteClients()
    {
        var clients = _remote?.ActiveClients(TimeSpan.FromSeconds(20)) ?? [];
        RemoteClientsText = clients.Count switch
        {
            0 => "Aucun appareil connecté pour l'instant.",
            1 => "1 appareil connecté : " + Describe(clients[0]) + ".",
            _ => $"{clients.Count} appareils connectés : " + string.Join(", ", clients.Select(Describe)) + ".",
        };

        static string Describe(IPAddress a) => IPAddress.IsLoopback(a) ? "ce PC" : a.ToString();
    }

    private static byte[]? _appIcon;

    private static byte[]? AppIcon()
    {
        if (_appIcon != null) return _appIcon;
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png"));
            if (info == null) return null;
            using var stream = info.Stream;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return _appIcon = copy.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Remote actions, run on the UI thread like a click.</summary>
    private sealed class RemoteActions(MainViewModel vm) : IRemoteActions
    {
        private Task<T> Ui<T>(Func<T> f) => vm._dispatcher.InvokeAsync(f).Task;
        private Task Ui(Action a) => vm._dispatcher.InvokeAsync(a).Task;

        public byte[]? Icon => AppIcon();

        public Task<RemoteCatalog> CatalogAsync() => Ui(BuildCatalog);

        private RemoteCatalog BuildCatalog()
        {
            var accent = vm.Settings.Appearance.AccentColor;
            var color = Services.ThemeService.Parse(accent, "#7C5CFF");
            var onAccent = Core.ColorMath.PrefersDarkText(color.R, color.G, color.B) ? "#11141c" : "#ffffff";
            return new RemoteCatalog(
                vm.Settings.Pads.Select(p => new RemotePad(p.Id, p.Name, p.Color, p.Category, false, p.Hotkey.IsSet ? p.Hotkey.Display : null,
                    PadIcons.Resolve(p.Icon, p.Name))).ToList(),
                vm.Voice.Presets.Select(p => new RemotePreset(p.Name, p.Description)).ToList(),
                vm.Soundboard.Tts.QuickPhrases.ToList(),
                vm.Settings.TextToSpeech.History.ToList(),
                vm.RemoteSettings.Deck.ToList(),
                $"#{color.R:x2}{color.G:x2}{color.B:x2}",
                onAccent);
        }

        public Task<RemoteState> StateAsync() => Ui(() =>
        {
            // The catalog's fingerprint: the phone reloads it when a sound, an effect, a phrase or the deck changes.
            var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(RemoteApi.Serialize(BuildCatalog()))))[..12];
            var playing = new HashSet<Guid>();
            vm.Engine.Pads.FillPlayingIds(playing);
            vm.Engine.PrivatePads.FillPlayingIds(playing, clear: false);
            var mix = vm.Mix;
            var track = vm.Music.CurrentTrack;
            string? cover = null;
            if (track?.ThumbnailUrl is { Length: > 0 } thumb)
                cover = thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? thumb : File.Exists(thumb) ? "/api/music/cover" : null;
            var music = new RemoteMusic(track != null, track?.Title, track?.Artist, cover, vm.Music.IsPlaying,
                vm.Music.Position, vm.Music.Duration);
            return new RemoteState(
                version,
                mix.MicEnabled,
                vm.VoiceSettings.EffectsEnabled,
                vm.Voice.SelectedPresetName ?? "Voix normale",
                mix.PushToTalkEnabled,
                mix.PushToTalkEnabled && vm.Engine.Core.Meters.Transmitting,
                playing.ToList(),
                new RemoteMix(mix.MusicVolume, mix.PadsVolume, mix.MediaToMicVolume, mix.MonitorVolume, mix.MediaToMicEnabled),
                music,
                BlindTestState(),
                vm.Soundboard.Tts.IsAvailable,
                mix.NoiseSuppressionEnabled,
                vm.ActiveProfileName);
        });

        private RemoteBlindTest? BlindTestState()
        {
            var bt = vm.BlindTest;
            if (bt.Phase == BlindTestPhase.Setup) return null;
            var state = bt.RoundState switch
            {
                BlindTestRoundState.Loading => "loading",
                BlindTestRoundState.Listening => "listening",
                BlindTestRoundState.TimeUp => "timeup",
                _ => "revealed",
            };
            return new RemoteBlindTest(
                bt.Phase == BlindTestPhase.Podium ? "podium" : "playing",
                bt.IsSuspended, bt.RoundNumber, bt.TotalRounds, bt.Countdown, state, bt.IsPausedByHost,
                bt.AnswerTitle, bt.CurrentTrack == null ? "" : bt.AnswerArtist, bt.HintText.Replace("\n", "  ·  "),
                bt.HintLevel < BlindTestHints.MaxLevel,
                bt.Leaderboard.Select(p => new RemoteBlindTestPlayer(p.Name, p.Score, p.Rank, p.Answer.Title, p.Answer.Artist, p.Answer.Fast)).ToList());
        }

        public Task<bool> PlayPadAsync(string idOrName) => Ui(() =>
        {
            var pad = RemoteApi.FindPad(vm.Settings.Pads, idOrName, p => p.Id, p => p.Name);
            if (pad == null) return false;
            vm.Soundboard.PlayPadCommand.Execute(pad);
            return true;
        });

        public Task<bool> ApplyPresetAsync(string name) => Ui(() =>
        {
            var preset = vm.Voice.Presets.FirstOrDefault(p => p.Name == name)
                         ?? vm.Voice.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (preset == null) return false;
            vm.Voice.ApplyPresetCommand.Execute(preset);
            if (!vm.VoiceSettings.EffectsEnabled) vm.VoiceSettings.EffectsEnabled = true;
            return true;
        });

        public Task<bool> RunAsync(string action) => Ui(() =>
        {
            var bt = vm.BlindTest;
            // Blind test buttons only mean something during a game (« reprendre » only once it was left).
            if (action.StartsWith("bt-", StringComparison.Ordinal)
                && (bt.Phase != BlindTestPhase.Playing || action != "bt-resume" && bt.IsSuspended))
                return false;
            switch (action)
            {
                case "stop": vm.Soundboard.StopAllCommand.Execute(null); return true;
                case "mic": vm.ToggleMic(); return true;
                case "effects": vm.ToggleEffects(); return true;
                case "noise":
                    if (!vm.NoiseSuppressionAvailable) return false;
                    vm.Mix.NoiseSuppressionEnabled = !vm.Mix.NoiseSuppressionEnabled;
                    vm.ShowToast(vm.Mix.NoiseSuppressionEnabled ? "Suppression du bruit activée" : "Suppression du bruit désactivée");
                    return true;
                case "radio":
                    vm.Mix.PushToTalkEnabled = !vm.Mix.PushToTalkEnabled;
                    vm.ShowToast(vm.Mix.PushToTalkEnabled ? "Mode radio activé : votre voix passe en maintenant la touche." : "Mode radio désactivé");
                    return true;
                case "preset-next": vm.Voice.CyclePreset(1); return true;
                case "preset-previous": vm.Voice.CyclePreset(-1); return true;
                case "music-playpause": vm.Music.PlayPauseCommand.Execute(null); return true;
                case "music-next": vm.Music.NextCommand.Execute(null); return true;
                case "music-previous": vm.Music.PreviousCommand.Execute(null); return true;
                case "music-volume-up": vm.StepMusicVolume(1); return true;
                case "music-volume-down": vm.StepMusicVolume(-1); return true;
                case "media-to-mic": vm.ToggleMediaToMic(); return true;
                case "live-voice": vm.Soundboard.Tts.ToggleLiveCommand.Execute(null); return true;
                case "bt-reveal": return Execute(bt.RevealCommand);
                case "bt-next": return Execute(bt.NextRoundCommand);
                case "bt-replay": return Execute(bt.ReplayCommand);
                case "bt-pause": return Execute(bt.TogglePauseCommand);
                case "bt-hint": return Execute(bt.HintCommand);
                case "bt-skip": return Execute(bt.SkipCommand);
                case "bt-resume":
                    // The game resumes on its page, like when the host comes back to it.
                    vm.CurrentPage = BlindTestViewModel.PageName;
                    if (bt.IsSuspended) bt.ResumeGameCommand.Execute(null);
                    return true;
                default: return false;
            }
        });

        private static bool Execute(System.Windows.Input.ICommand command)
        {
            if (!command.CanExecute(null)) return false;
            command.Execute(null);
            return true;
        }

        public Task PushToTalkAsync(bool down) => Ui(() => vm.RemoteTalk(down));

        public Task<bool> SetMixAsync(string key, double value) => Ui(() =>
        {
            if (!double.IsFinite(value)) return false;
            var v = (float)Math.Clamp(value, 0, 1.5);
            switch (key)
            {
                case "music": vm.Mix.MusicVolume = v; break;
                case "pads": vm.Mix.PadsVolume = v; break;
                case "tomic": vm.Mix.MediaToMicVolume = v; break;
                case "monitor": vm.Mix.MonitorVolume = v; break;
                default: return false;
            }
            return true;
        });

        public async Task<bool> SayAsync(string text)
        {
            var task = await Ui(() => vm.Soundboard.Tts.IsAvailable ? vm.Soundboard.Tts.SayAsync(text, remember: true) : Task.FromResult<string?>(null));
            return await task != null;
        }

        public Task<bool> SetDeckAsync(IReadOnlyList<string> items) => Ui(() =>
        {
            vm.RemoteSettings.Deck = items.ToList();
            vm.Save();
            return true;
        });

        public Task<bool> ToggleBlindTestAsync(string player, string field) => Ui(() =>
        {
            if (vm.BlindTest.Phase != BlindTestPhase.Playing) return false;
            var p = vm.BlindTest.Players.FirstOrDefault(x => x.Name == player);
            if (p == null) return false;
            switch (field)
            {
                case "title": p.Answer.Title = !p.Answer.Title; return true;
                case "artist": p.Answer.Artist = !p.Answer.Artist; return true;
                default: return false;
            }
        });

        public Task<(byte[] Data, string ContentType)?> CoverAsync() => Ui<(byte[] Data, string ContentType)?>(() =>
        {
            var path = vm.Music.CurrentTrack?.ThumbnailUrl;
            if (string.IsNullOrEmpty(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            // Only covers kept by the app (never an arbitrary file of the PC).
            var data = Path.GetFullPath(vm.DataDirectory) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(data, StringComparison.OrdinalIgnoreCase)) return null;
            var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            return (File.ReadAllBytes(path), type);
        });
    }
}
