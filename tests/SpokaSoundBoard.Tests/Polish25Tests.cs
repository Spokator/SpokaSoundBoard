using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

/// <summary>2.5: blind test themes and hints, readable text on the accent color.</summary>
public sealed class Polish25Tests
{
    private static SearchResult R(string title, string channel, double seconds, string id) =>
        new(title, channel, $"https://www.youtube.com/watch?v={id}", null, TimeSpan.FromSeconds(seconds), null, SearchSource.YouTube, null);

    [Fact]
    public void Themes_KeepSingleSongsWithAnArtist()
    {
        SearchResult[] results =
        [
            R("Indochine - L'aventurier (Clip officiel)", "Indochine", 230, "aaaaaaaaaa1"),
            R("Best of années 80 - 2 heures de tubes", "Compil", 7200, "aaaaaaaaaa2"),
            R("Top 100 années 80 MIX", "DJ", 400, "aaaaaaaaaa3"),
            R("Desireless - Voyage voyage", "Desireless", 260, "aaaaaaaaaa4"),
            R("Desireless - Voyage Voyage (Remix 2020)", "DJ", 240, "aaaaaaaaaa5"),
            R("Intro", "Quelqu'un", 40, "aaaaaaaaaa6"),
            R("Desireless - Voyage voyage", "Desireless Topic", 262, "aaaaaaaaaa7"), // same song again
            R("Mixtape vibes", "Someone", 200, "aaaaaaaaaa8"), // « mix » inside a word is fine; the channel is the artist
            R("Indochine - L'aventurier (Clip officiel)", "Indochine", 230, "aaaaaaaaaa1"),
        ];
        var tracks = BlindTestThemes.ToTracks(results);

        Assert.Equal(["L'aventurier", "Voyage voyage", "Mixtape vibes"], tracks.Select(t => t.Title));
        Assert.Equal("Indochine", tracks[0].Artist);
        Assert.All(tracks, t => Assert.True(t.IsTransient && t.State == TrackState.Remote && t.HasSource));
        Assert.Equal(3, BlindTestThemes.Queries("  années 80 ").Count);
        Assert.Contains("années 80", BlindTestThemes.Queries("  années 80 "));
    }

    [Fact]
    public void Hints_RevealProgressively()
    {
        Assert.Equal("", BlindTestHints.For("Voyage voyage", "Desireless", 0));
        Assert.Equal("Titre : •••••• ••••••", BlindTestHints.For("Voyage voyage", "Desireless", 1));
        Assert.Equal("Titre : V••••• V•••••", BlindTestHints.For("Voyage voyage", "Desireless", 2));
        Assert.Equal("Titre : V••••• V•••••\nArtiste : D•••••••••", BlindTestHints.For("Voyage voyage", "Desireless", 3));
        Assert.Equal("L'A•••••••••", BlindTestHints.Mask("L'aventurier", firstLetters: true));
        Assert.Equal("A-H•", BlindTestHints.Mask("a-ha", firstLetters: true));
        Assert.Equal("Titre : •••", BlindTestHints.For("Âme", null, 3).Split('\n')[0].Replace("Â", "•")[..11]);
    }

    [Fact]
    public void AccentText_StaysReadable()
    {
        Assert.False(ColorMath.PrefersDarkText(0x7C, 0x5C, 0xFF)); // default violet: white text
        Assert.False(ColorMath.PrefersDarkText(0x3B, 0x82, 0xF6)); // blue
        Assert.True(ColorMath.PrefersDarkText(0x10, 0xB9, 0x81));  // green
        Assert.False(ColorMath.PrefersDarkText(0xEF, 0x44, 0x44)); // red
        Assert.True(ColorMath.PrefersDarkText(0xF5, 0x9E, 0x0B));  // gold
        Assert.True(ColorMath.PrefersDarkText(0x84, 0xCC, 0x16));  // lime
        Assert.True(ColorMath.PrefersDarkText(0x22, 0xD3, 0xEE));  // cyan
        Assert.Equal(21, ColorMath.Contrast(ColorMath.Luminance(0, 0, 0), ColorMath.Luminance(255, 255, 255)), 1);
    }
}
