using SpokaSoundBoard.Core.Music;

namespace SpokaSoundBoard.Core.Models;

public sealed record PadIcon(string Key, string Label);

/// <summary>
/// Icon of a sound on the phone remote. A pad stores a key from <see cref="All"/>, or "" to let the app pick one from
/// its name (« Rire du public » → a laughing face, « Explosion » → a bomb…).
/// </summary>
public static class PadIcons
{
    public const string Default = "wave-sine";

    public static readonly IReadOnlyList<PadIcon> All =
    [
        new(Default, "Son"), new("confetti", "Fête, applaudissements"), new("mood-happy", "Rire"), new("mood-crazy-happy", "Fou rire"),
        new("mood-wink", "Clin d'œil"), new("mood-surprised", "Surprise"), new("mood-sad", "Triste"), new("mood-angry", "Colère"),
        new("bomb", "Explosion"), new("alarm", "Alarme, sirène"), new("bell-ringing", "Sonnerie"), new("bell", "Cloche"),
        new("car", "Voiture, klaxon"), new("speakerphone", "Annonce, corne"), new("radio", "Radio"), new("music", "Musique"),
        new("ghost", "Fantôme"), new("skull", "Mort"), new("heart", "Amour"), new("flame", "Feu"), new("bolt", "Éclair"),
        new("rocket", "Fusée"), new("swords", "Combat"), new("target", "Cible"), new("crown", "Victoire"), new("trophy", "Trophée"),
        new("thumb-up", "Bravo"), new("thumb-down", "Raté"), new("question-mark", "Question"), new("alert-triangle", "Attention"),
        new("hand-stop", "Stop"), new("zzz", "Dodo"), new("coin", "Argent"), new("clock", "Horloge"), new("cat", "Chat"),
        new("dog", "Chien"), new("poo", "Prout"), new("ball-football", "Football"), new("device-gamepad-2", "Jeu vidéo"),
        new("dice-5", "Dé"), new("moon", "Nuit"), new("sun", "Soleil"), new("snowflake", "Froid"), new("cloud-rain", "Pluie"),
        new("balloon", "Ballon"), new("cake", "Anniversaire"), new("gift", "Cadeau"), new("door", "Porte"),
        new("phone-call", "Téléphone"), new("ambulance", "Ambulance"), new("sparkles", "Magie, suspense"),
        new("broadcast", "Voix"), new("microphone", "Micro"),
    ];

    private static readonly HashSet<string> Keys = All.Select(i => i.Key).ToHashSet();

    // Word starts that suggest an icon, most specific first. Matched against the accent-free, lower-case name.
    private static readonly (string[] Words, string Icon)[] Hints =
    [
        (["applau", "bravo", "ovation", "clap", "fete", "hourra", "yay"], "confetti"),
        (["fou rire", "crazy"], "mood-crazy-happy"),
        (["rire", "rigol", "lol", "mdr", "haha", "hihi", "laugh"], "mood-happy"),
        (["triste", "pleur", "sniff", "sad", "ouin"], "mood-sad"),
        (["colere", "enerv", "angry", "rage", "cri"], "mood-angry"),
        (["surpri", "wow", "choc", "omg"], "mood-surprised"),
        (["explos", "boom", "bomb", "detonat"], "bomb"),
        (["siren", "alarm", "alerte", "police", "pompier", "nee nee"], "alarm"),
        (["sonnerie", "ring", "ding", "dring"], "bell-ringing"),
        (["cloche", "bell"], "bell"),
        (["klaxon", "voiture", "moteur", "vroum", "car "], "car"),
        (["corne", "annonce", "megaphone", "brume", "horn", "stade"], "speakerphone"),
        (["radio", "bip", "talkie", "roger"], "radio"),
        (["musique", "music", "song", "chanson", "melodi", "tambour", "drum", "guitare"], "music"),
        (["fantome", "ghost", "boo"], "ghost"),
        (["mort", "dead", "skull", "crane", "rip"], "skull"),
        (["amour", "love", "coeur", "bisou", "kiss"], "heart"),
        (["feu", "fire", "flamme"], "flame"),
        (["eclair", "tonnerre", "thunder", "orage"], "bolt"),
        (["fusee", "rocket", "decoll"], "rocket"),
        (["epee", "sword", "combat", "fight", "duel", "baston"], "swords"),
        (["victoire", "win", "gagn", "champion", "level up"], "crown"),
        (["trophee", "trophy", "medaille"], "trophy"),
        (["rate", "fail", "echec", "perdu", "loose", "lose", "wasted"], "thumb-down"),
        (["question", "quoi", "hein"], "question-mark"),
        (["attention", "warning", "danger"], "alert-triangle"),
        (["dodo", "ronfl", "sleep", "zzz"], "zzz"),
        (["argent", "money", "cash", "caisse", "piece"], "coin"),
        (["horloge", "tic tac", "clock", "minuteur", "chrono"], "clock"),
        (["miaou", "chat ", "cat", "meow"], "cat"),
        (["chien", "wouf", "dog", "aboi"], "dog"),
        (["prout", "pet ", "caca", "fart", "poop"], "poo"),
        (["but", "goal", "foot"], "ball-football"),
        (["mario", "jeu video", "game over", "8 bit", "arcade"], "device-gamepad-2"),
        (["nuit", "night"], "moon"),
        (["pluie", "rain"], "cloud-rain"),
        (["anniversaire", "birthday"], "cake"),
        (["cadeau", "gift"], "gift"),
        (["porte", "door", "toc toc"], "door"),
        (["telephone", "appel", "phone", "call"], "phone-call"),
        (["ambulance"], "ambulance"),
        (["suspense", "mystere", "magie", "magic", "tadam"], "sparkles"),
    ];

    /// <summary>The key to show: the pad's own choice when valid, otherwise a guess from its name.</summary>
    public static string Resolve(string? chosen, string? name) =>
        !string.IsNullOrEmpty(chosen) && Keys.Contains(chosen) ? chosen : Guess(name);

    public static string Guess(string? name)
    {
        var text = " " + MusicLibrary.Normalize(name) + " ";
        foreach (var (words, icon) in Hints)
            foreach (var word in words)
                if (text.Contains(" " + word, StringComparison.Ordinal))
                    return icon;
        return Default;
    }

    /// <summary>« » (automatic) or a known key.</summary>
    public static string Clean(string? key) => key != null && Keys.Contains(key) ? key : "";
}
