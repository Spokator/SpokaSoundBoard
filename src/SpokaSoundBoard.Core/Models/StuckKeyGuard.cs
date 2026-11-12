namespace SpokaSoundBoard.Core.Models;

/// <summary>
/// Finds keys the input hook still believes are held although Windows says they are up. That happens when the
/// release never reaches the hook: lock screen, UAC prompt, Ctrl+Alt+Suppr, a hook timed out by Windows…
/// Without it the radio key would keep the mic open, and the next press of that key would be ignored.
/// A key is released only after <see cref="ChecksBeforeRelease"/> checks in a row, so a release already
/// on its way to the hook is never raced.
/// </summary>
public sealed class StuckKeyGuard
{
    public const int ChecksBeforeRelease = 2;

    private readonly Dictionary<int, int> _misses = [];

    /// <summary>Keys of <paramref name="held"/> to release now. <paramref name="isDown"/> reads the real key state.</summary>
    public List<int> Check(IReadOnlyCollection<int> held, Func<int, bool> isDown)
    {
        var release = new List<int>();
        foreach (var vk in _misses.Keys.Where(k => !held.Contains(k)).ToList()) _misses.Remove(vk);
        foreach (var vk in held)
        {
            if (isDown(vk))
            {
                _misses.Remove(vk);
                continue;
            }
            var misses = _misses.GetValueOrDefault(vk) + 1;
            if (misses >= ChecksBeforeRelease)
            {
                _misses.Remove(vk);
                release.Add(vk);
            }
            else
            {
                _misses[vk] = misses;
            }
        }
        return release;
    }
}
