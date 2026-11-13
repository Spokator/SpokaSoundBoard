using System.Numerics;

namespace SpokaSoundBoard.Core.Models;

public static class HotkeyMatching
{
    /// <summary>
    /// Indexes of the bindings a key press triggers. A binding fires when its key matches and every modifier it
    /// needs is held, so « F1 » still works while the player holds Shift to sprint. When several bindings share the
    /// key, only the most specific ones fire: with « F1 » and « Maj + F1 » bound, Shift+F1 plays only the second.
    /// </summary>
    /// <param name="device">Controller that sent the button (<see cref="ControllerButtons.DeviceKey"/>), null for keys.</param>
    public static List<int> Select(IReadOnlyList<HotkeyBinding> bindings, int virtualKey, HotkeyModifiers pressed, string? device = null)
    {
        var result = new List<int>();
        var best = -1;
        for (var i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (!b.IsSet || !b.SameInput(virtualKey, device) || (b.Modifiers & ~pressed) != 0) continue;
            var specificity = BitOperations.PopCount((uint)b.Modifiers);
            if (specificity > best)
            {
                best = specificity;
                result.Clear();
            }
            if (specificity == best) result.Add(i);
        }
        return result;
    }

    /// <summary>Other bindings set to exactly the same key and modifiers as <paramref name="binding"/>.</summary>
    public static IEnumerable<T> Conflicts<T>(HotkeyBinding binding, IEnumerable<(HotkeyBinding Binding, T Owner)> others)
    {
        if (!binding.IsSet) yield break;
        foreach (var (other, owner) in others)
            if (!ReferenceEquals(other, binding) && other.IsSet && other.SameInput(binding.VirtualKey, binding.Device) && other.Modifiers == binding.Modifiers)
                yield return owner;
    }
}
