using CommunityToolkit.Mvvm.ComponentModel;

namespace SpokaSoundBoard.Core.Models;

/// <summary>
/// Settings applied automatically while a given game runs. Every setting is optional: null means « ne pas changer ».
/// </summary>
public partial class GameProfile : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] private string _name = "";

    /// <summary>Program name without « .exe » (e.g. « cs2 »), compared without case.</summary>
    [ObservableProperty] private string _processName = "";

    [ObservableProperty] private bool _enabled = true;

    /// <summary>Only while the game is the window in front (otherwise: as long as it runs, even behind Discord).</summary>
    [ObservableProperty] private bool _onlyWhenForeground;

    /// <summary>Soundboard tab to show (« » = Tous).</summary>
    [ObservableProperty] private string? _category;

    /// <summary>Voice effect preset to apply.</summary>
    [ObservableProperty] private string? _preset;

    [ObservableProperty] private bool? _effects;
    [ObservableProperty] private bool? _radio;
    [ObservableProperty] private bool? _noiseSuppression;
    [ObservableProperty] private bool? _mediaToMic;

    /// <summary>« cs2.exe », « C:\Jeux\cs2.exe », « CS2 » → « cs2 ».</summary>
    public static string NormalizeProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var file = Path.GetFileName(name.Trim().Trim('"'));
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        return file.Trim().ToLowerInvariant();
    }

    public bool HasSomethingToApply =>
        Category != null || Preset != null || Effects != null || Radio != null || NoiseSuppression != null || MediaToMic != null;

    /// <summary>What screen readers announce for a profile in the list.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? ProcessName : Name;

    public void Sanitize()
    {
        ProcessName = NormalizeProcess(ProcessName);
        Name = string.IsNullOrWhiteSpace(Name) ? ProcessName : Name.Trim();
        if (Preset != null && string.IsNullOrWhiteSpace(Preset)) Preset = null;
        if (Id == Guid.Empty) Id = Guid.NewGuid();
    }
}
