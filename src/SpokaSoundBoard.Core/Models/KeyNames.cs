namespace SpokaSoundBoard.Core.Models;

public static class KeyNames
{
    public const int VkMButton = 0x04;
    public const int VkXButton1 = 0x05;
    public const int VkXButton2 = 0x06;

    public static string Format(int vk, HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Maj");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>Optional OS-provided name lookup (keyboard-layout aware). Set by the application.</summary>
    public static Func<int, string?>? NameProvider { get; set; }

    public static string KeyName(int vk)
    {
        if (vk is not (VkMButton or VkXButton1 or VkXButton2) && !(vk >= 0x60 && vk <= 0x6F))
        {
            var name = NameProvider?.Invoke(vk);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return FallbackName(vk);
    }

    private static string FallbackName(int vk) => vk switch
    {
        VkMButton => "Clic molette",
        VkXButton1 => "Souris 4",
        VkXButton2 => "Souris 5",
        0x08 => "Retour arrière",
        0x09 => "Tab",
        0x0D => "Entrée",
        0x13 => "Pause",
        0x14 => "Verr. Maj",
        0x1B => "Échap",
        0x20 => "Espace",
        0x21 => "Page préc.",
        0x22 => "Page suiv.",
        0x23 => "Fin",
        0x24 => "Origine",
        0x25 => "Gauche",
        0x26 => "Haut",
        0x27 => "Droite",
        0x28 => "Bas",
        0x2C => "Impr. écran",
        0x2D => "Inser",
        0x2E => "Suppr",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Pavé num {vk - 0x60}",
        0x6A => "Pavé num *",
        0x6B => "Pavé num +",
        0x6D => "Pavé num -",
        0x6E => "Pavé num .",
        0x6F => "Pavé num /",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x90 => "Verr. Num",
        0x91 => "Arrêt défil.",
        0xA0 or 0xA1 or 0x10 => "Maj",
        0xA2 or 0xA3 or 0x11 => "Ctrl",
        0xA4 or 0xA5 or 0x12 => "Alt",
        0xAD => "Muet",
        0xAE => "Volume -",
        0xAF => "Volume +",
        0xB0 => "Piste suivante",
        0xB1 => "Piste précédente",
        0xB2 => "Stop média",
        0xB3 => "Lecture/Pause média",
        _ => $"Touche {vk}",
    };
}

