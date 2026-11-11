namespace SpokaSoundBoard.Core.Models;

/// <summary>
/// Soundboard tabs. The ordered list lives in <see cref="AppSettings.PadCategories"/>, each pad names its own; these
/// helpers keep both in step (a category typed in the pad editor appears as a tab, renaming moves its pads…).
/// </summary>
public static class PadCategories
{
    public const int MaxLength = 30;

    public static string Clean(string? name)
    {
        var clean = (name ?? "").Trim();
        return clean.Length > MaxLength ? clean[..MaxLength].TrimEnd() : clean;
    }

    /// <summary>Adds the categories used by pads but missing from the list, in pad order. Returns true if the list changed.</summary>
    public static bool Merge(List<string> categories, IEnumerable<SoundPad> pads)
    {
        var changed = categories.RemoveAll(string.IsNullOrWhiteSpace) > 0;
        foreach (var pad in pads)
        {
            if (string.IsNullOrEmpty(pad.Category) || Contains(categories, pad.Category)) continue;
            categories.Add(pad.Category);
            changed = true;
        }
        return changed;
    }

    /// <summary>Renames a category and moves its pads. Renaming onto an existing category merges both.</summary>
    public static void Rename(List<string> categories, IEnumerable<SoundPad> pads, string oldName, string newName)
    {
        newName = Clean(newName);
        if (newName.Length == 0) return;
        var index = IndexOf(categories, oldName);
        var target = IndexOf(categories, newName);
        if (index >= 0)
        {
            if (target >= 0 && target != index) categories.RemoveAt(index);
            else categories[index] = newName;
        }
        else if (target < 0)
        {
            categories.Add(newName);
        }
        var canonical = categories[IndexOf(categories, newName)];
        foreach (var pad in pads)
            if (string.Equals(pad.Category, oldName, StringComparison.CurrentCultureIgnoreCase))
                pad.Category = canonical;
    }

    /// <summary>Removes the tab; its sounds stay in the soundboard, without category.</summary>
    public static void Remove(List<string> categories, IEnumerable<SoundPad> pads, string name)
    {
        var index = IndexOf(categories, name);
        if (index >= 0) categories.RemoveAt(index);
        foreach (var pad in pads)
            if (string.Equals(pad.Category, name, StringComparison.CurrentCultureIgnoreCase))
                pad.Category = "";
    }

    /// <summary>The spelling already used for this category, so « bruitages » joins « Bruitages ».</summary>
    public static string Canonical(List<string> categories, string? name)
    {
        var clean = Clean(name);
        var index = IndexOf(categories, clean);
        return index >= 0 ? categories[index] : clean;
    }

    private static bool Contains(List<string> categories, string name) => IndexOf(categories, name) >= 0;

    private static int IndexOf(List<string> categories, string name) =>
        categories.FindIndex(c => string.Equals(c, name, StringComparison.CurrentCultureIgnoreCase));
}
