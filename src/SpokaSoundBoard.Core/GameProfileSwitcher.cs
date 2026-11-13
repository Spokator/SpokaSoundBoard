using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core;

/// <summary>What a game profile can change in the app (implemented by the app on its UI thread, faked by the tests).</summary>
public interface IProfileTarget
{
    string Category { get; set; }
    bool Effects { get; set; }
    bool Radio { get; set; }
    bool NoiseSuppression { get; set; }
    bool MediaToMic { get; set; }

    /// <summary>Name of the selected voice preset (null when the voice was tuned by hand).</summary>
    string? Preset { get; }

    /// <summary>Applies a preset by name. False when it does not exist (deleted since).</summary>
    bool ApplyPreset(string name);

    /// <summary>Copy of the current voice (preset name and every effect value), to put back later.</summary>
    object CaptureVoice();

    void RestoreVoice(object snapshot);

    void Notify(string message);
}

/// <summary>
/// Applies the profile of the game being played and puts things back afterwards. Only what the profile changed is
/// restored, and only if the user did not change it meanwhile (a voice effect picked by hand during the game stays).
/// The previous settings come back a few seconds after the game ends, so a quick alt-tab or a game that restarts
/// does not make the settings flicker. UI thread only.
/// </summary>
public sealed class GameProfileSwitcher(IProfileTarget target)
{
    /// <summary>How long the game must stay away before its profile ends.</summary>
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(5);

    private readonly List<Action> _restore = [];
    private DateTime? _leftAt;

    /// <summary>Profile currently applied, null when none.</summary>
    public GameProfile? Active { get; private set; }

    /// <summary>Raised when <see cref="Active"/> changes.</summary>
    public event Action? ActiveChanged;

    /// <summary>
    /// The profile that should be active: enabled, with a program name, whose game runs (or is in front, for profiles
    /// limited to the foreground). The game in front wins; otherwise the first one in the list.
    /// </summary>
    public static GameProfile? Choose(IEnumerable<GameProfile> profiles, IReadOnlySet<string> running, string? foreground)
    {
        GameProfile? first = null;
        foreach (var p in profiles)
        {
            if (!p.Enabled || p.ProcessName.Length == 0 || !p.HasSomethingToApply) continue;
            var front = foreground != null && string.Equals(p.ProcessName, foreground, StringComparison.OrdinalIgnoreCase);
            if (front) return p;
            if (!p.OnlyWhenForeground && running.Contains(p.ProcessName)) first ??= p;
        }
        return first;
    }

    /// <param name="running">Program names running now (normalized, see <see cref="GameProfile.NormalizeProcess"/>).</param>
    /// <param name="foreground">Program owning the window in front, normalized, or null.</param>
    public void Update(IEnumerable<GameProfile> profiles, IReadOnlySet<string> running, string? foreground, DateTime now)
    {
        var wanted = Choose(profiles, running, foreground);
        if (wanted != null && ReferenceEquals(wanted, Active))
        {
            _leftAt = null;
            return;
        }
        if (wanted == null)
        {
            if (Active == null) return;
            // The game is gone (or in the background): wait a little before putting things back.
            _leftAt ??= now;
            if (now - _leftAt < LeaveDelay) return;
            End(notify: true);
            return;
        }

        // Another game took over: end the previous profile at once, then apply the new one.
        if (Active != null) End(notify: false);
        Apply(wanted);
    }

    /// <summary>Ends the active profile now (profile deleted or disabled, app closing).</summary>
    public void Stop()
    {
        if (Active != null) End(notify: false);
    }

    private void Apply(GameProfile profile)
    {
        var changes = new List<string>();

        if (profile.Preset is { } preset)
        {
            var before = target.CaptureVoice();
            if (target.ApplyPreset(preset))
            {
                _restore.Add(() =>
                {
                    if (target.Preset == preset) target.RestoreVoice(before);
                });
                changes.Add($"effet {preset}");
            }
        }
        Track(profile.Category, () => target.Category, v => target.Category = v,
            v => changes.Add(v.Length == 0 ? "tous les sons" : $"onglet {v}"));
        Track(profile.Effects, () => target.Effects, v => target.Effects = v, v => changes.Add(v ? "effets activés" : "effets coupés"));
        Track(profile.Radio, () => target.Radio, v => target.Radio = v, v => changes.Add(v ? "mode radio" : "sans mode radio"));
        Track(profile.NoiseSuppression, () => target.NoiseSuppression, v => target.NoiseSuppression = v,
            v => changes.Add(v ? "anti-bruit" : "sans anti-bruit"));
        Track(profile.MediaToMic, () => target.MediaToMic, v => target.MediaToMic = v,
            v => changes.Add(v ? "musique dans le micro" : "musique pour vous seul"));

        Active = profile;
        _leftAt = null;
        target.Notify(changes.Count == 0
            ? $"Profil « {profile.Name} » actif"
            : $"Profil « {profile.Name} » : {string.Join(", ", changes)}");
        ActiveChanged?.Invoke();
    }

    /// <summary>Sets a value if the profile asks for one, and remembers how to put the previous one back.</summary>
    private void Track<T>(T? wanted, Func<T> get, Action<T> set, Action<T> describe) where T : notnull
    {
        if (wanted == null) return;
        var value = wanted is T v ? v : default!;
        var before = get();
        if (EqualityComparer<T>.Default.Equals(before, value)) return;
        set(value);
        describe(value);
        _restore.Add(() =>
        {
            // Changed by hand during the game: the user's choice stays.
            if (EqualityComparer<T>.Default.Equals(get(), value)) set(before);
        });
    }

    private void Track(bool? wanted, Func<bool> get, Action<bool> set, Action<bool> describe)
    {
        if (wanted is not { } value) return;
        Track<bool>(value, get, set, describe);
    }

    private void End(bool notify)
    {
        var profile = Active!;
        for (var i = _restore.Count - 1; i >= 0; i--)
        {
            try
            {
                _restore[i]();
            }
            catch
            {
                // One setting that cannot be put back must not block the others.
            }
        }
        _restore.Clear();
        Active = null;
        _leftAt = null;
        if (notify) target.Notify($"Fin du profil « {profile.Name} » : réglages précédents remis");
        ActiveChanged?.Invoke();
    }
}
