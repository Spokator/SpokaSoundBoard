using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

public class BlindTestScoringTests
{
    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 2)]
    [InlineData(false, true, false, 1)]
    [InlineData(true, true, false, 4)]
    [InlineData(true, true, true, 5)]
    [InlineData(false, true, true, 2)]
    [InlineData(false, false, true, 0)] // fast but nothing found: no bonus
    public void PointsFollowTheRules(bool title, bool artist, bool fast, int expected) =>
        Assert.Equal(expected, BlindTestScoring.Points(title, artist, fast));

    [Fact]
    public void AnswerRecomputesPointsAndNotifies()
    {
        var answer = new BlindTestAnswer();
        var changed = new List<string?>();
        answer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        answer.Title = true;
        answer.Artist = true;
        Assert.Equal(4, answer.Points);
        Assert.Contains(nameof(BlindTestAnswer.Points), changed);
    }

    [Fact]
    public void RanksHandleTies() =>
        Assert.Equal(new[] { 2, 1, 2, 4 }, BlindTestScoring.Ranks(new[] { 5, 9, 5, 0 }));
}

public class BlindTestPickerTests
{
    private static TrackItem Track(int i, string? artist = null, string? title = null) =>
        new() { DisplayTitle = title ?? $"Titre {i}", Artist = artist ?? $"Artiste {i}", SourceUrl = $"https://example.com/{i}" };

    [Fact]
    public void NoRepeatsAndReadyTracksFirst()
    {
        var tracks = Enumerable.Range(0, 30).Select(i => Track(i)).ToList();
        var ready = tracks.Where((_, i) => i % 3 == 0).ToHashSet();
        // The same instance twice and the same song under another id.
        var candidates = tracks.Concat(new[] { tracks[4], Track(99, "Artiste 5", "TITRE 5") }).ToList();

        var order = BlindTestPicker.Order(candidates, ready.Contains, new Random(42));

        Assert.Equal(30, order.Count);
        Assert.Equal(order.Count, order.Select(t => t.Id).Distinct().Count());
        Assert.All(order.Take(ready.Count), t => Assert.Contains(t, ready));
        Assert.All(order.Skip(ready.Count), t => Assert.DoesNotContain(t, ready));
    }

    [Fact]
    public void OrderIsShuffled()
    {
        var tracks = Enumerable.Range(0, 40).Select(i => Track(i)).ToList();
        var a = BlindTestPicker.Order(tracks, _ => true, new Random(1));
        var b = BlindTestPicker.Order(tracks, _ => true, new Random(2));
        Assert.NotEqual(tracks, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SkipsTracksThatCannotBeFound()
    {
        var empty = new TrackItem();
        var order = BlindTestPicker.Order([empty, Track(1)], _ => false, new Random(3));
        Assert.Single(order);
    }

    [Fact]
    public void ExcerptStartsBetween20And45PercentAndFits()
    {
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var start = BlindTestPicker.ExcerptStart(200, 20, random);
            Assert.InRange(start, 40, 90);
        }
        // Short song: moved earlier so the whole excerpt fits.
        for (var i = 0; i < 100; i++)
        {
            var start = BlindTestPicker.ExcerptStart(30, 20, random);
            Assert.InRange(start, 0, 9);
        }
        Assert.Equal(0, BlindTestPicker.ExcerptStart(10, 20, random));
        Assert.Equal(0, BlindTestPicker.ExcerptStart(0, 20, random));
    }
}

public class BlindTestAnswerMatcherTests
{
    [Theory]
    [InlineData("daft punk", "Daft Punk")]
    [InlineData("DAFT PUNK !", "Daft Punk")]
    [InlineData("daftpunk", "Daft Punk")]
    [InlineData("je crois que c'est daft punk", "Daft Punk")]
    [InlineData("stromae", "Stromaé")]
    [InlineData("alors on danse", "Alors on danse (Radio Edit)")]
    [InlineData("alors on dance", "Alors on danse")]
    [InlineData("beatles", "The Beatles")]
    [InlineData("c'est les beatles", "The Beatles")]
    [InlineData("bohemian rapsody", "Bohemian Rhapsody - Remastered 2011")]
    [InlineData("get lucky", "Get Lucky (feat. Pharrell Williams)")]
    [InlineData("get lucky", "Get Lucky feat. Pharrell Williams")]
    [InlineData("ABBA", "ABBA")]
    public void AcceptsCloseAnswers(string guess, string expected) =>
        Assert.True(BlindTestAnswerMatcher.IsMatch(guess, expected));

    [Theory]
    [InlineData("u3", "U2")] // too short for typos
    [InlineData("justice", "Daft Punk")]
    [InlineData("", "Daft Punk")]
    [InlineData("daft punk", "")]
    [InlineData("bohemian", "Bohemian Rhapsody")]
    [InlineData("la vie en couleur", "La Vie en rose")]
    public void RejectsWrongAnswers(string guess, string expected) =>
        Assert.False(BlindTestAnswerMatcher.IsMatch(guess, expected));

    [Theory]
    [InlineData("Alors on danse (Radio Edit)", "Alors on danse")]
    [InlineData("Get Lucky feat. Pharrell Williams", "Get Lucky")]
    [InlineData("Bohemian Rhapsody - Remastered 2011", "Bohemian Rhapsody")]
    [InlineData("Été 85 [Clip officiel]", "Été 85")]
    [InlineData("(Intro)", "(Intro)")]
    public void StripDecorationsKeepsTheSpokenTitle(string raw, string expected) =>
        Assert.Equal(expected, BlindTestAnswerMatcher.StripDecorations(raw));

    [Fact]
    public void LevenshteinCountsEditsAndStopsEarly()
    {
        Assert.Equal(3, BlindTestAnswerMatcher.Levenshtein("kitten", "sitting"));
        Assert.Equal(0, BlindTestAnswerMatcher.Levenshtein("abc", "abc"));
        Assert.Equal(3, BlindTestAnswerMatcher.Levenshtein("", "abc"));
        Assert.True(BlindTestAnswerMatcher.Levenshtein("aaaaaaaa", "bbbbbbbb", 2) > 2);
    }
}

public class BlindTestSettingsTests
{
    [Fact]
    public void PlayersAreTrimmedUniqueAndLimited()
    {
        var s = new BlindTestSettings();
        Assert.True(s.AddPlayer("  Léa   Martin "));
        Assert.False(s.AddPlayer("léa martin"));
        Assert.False(s.AddPlayer("   "));
        Assert.Equal("Léa Martin", s.Players[0]);
        Assert.True(s.AddPlayer(new string('x', 40)));
        Assert.Equal(BlindTestSettings.MaxNameLength, s.Players[1].Length);
        for (var i = 0; i < 30; i++) s.AddPlayer($"Joueur {i}");
        Assert.Equal(BlindTestSettings.MaxPlayers, s.Players.Count);
    }

    [Fact]
    public void RecordGameKeepsTotalsBestScoresAndWins()
    {
        var s = new BlindTestSettings();
        s.RecordGame([("Léa", 12), ("Tom", 7), ("Max", 12)]);
        s.RecordGame([("léa", 3), ("Tom", 9)]);

        var lea = s.Records.Single(r => r.Name == "Léa");
        Assert.Equal(15, lea.TotalPoints);
        Assert.Equal(2, lea.GamesPlayed);
        Assert.Equal(12, lea.BestScore);
        Assert.Equal(1, lea.Wins);
        var tom = s.Records.Single(r => r.Name == "Tom");
        Assert.Equal(16, tom.TotalPoints);
        Assert.Equal(1, tom.Wins);
        Assert.Equal(1, s.Records.Single(r => r.Name == "Max").Wins);
        Assert.Equal("Tom", s.Records[0].Name); // best total first
    }

    [Fact]
    public void SoloGameAndZeroScoresAreNotWins()
    {
        var s = new BlindTestSettings();
        s.RecordGame([("Solo", 10)]);
        s.RecordGame([("A", 0), ("B", 0)]);
        Assert.All(s.Records, r => Assert.Equal(0, r.Wins));
    }

    [Fact]
    public void OldSettingsFilesLoadWithDefaults()
    {
        var settings = SettingsStore.Deserialize("""{ "FirstRunDone": true }""");
        Assert.NotNull(settings.BlindTest);
        Assert.Equal(10, settings.BlindTest.Rounds);
        Assert.Equal(20, settings.BlindTest.ExcerptSeconds);
        Assert.NotNull(settings.TextToSpeech);
        Assert.Equal(1, settings.TextToSpeech.Rate);
    }

    [Fact]
    public void OutOfRangeValuesAreClampedAndRoundTrip()
    {
        var settings = SettingsStore.Deserialize("""
            { "BlindTest": { "Rounds": 500, "ExcerptSeconds": 1, "Players": ["A", "a", " ", "B"], "Records": null },
              "TextToSpeech": { "Rate": 40, "History": null } }
            """);
        Assert.Equal(BlindTestSettings.MaxRounds, settings.BlindTest.Rounds);
        Assert.Equal(BlindTestSettings.MinExcerptSeconds, settings.BlindTest.ExcerptSeconds);
        Assert.Equal(new[] { "A", "B" }, settings.BlindTest.Players);
        Assert.Empty(settings.BlindTest.Records);
        Assert.Equal(3, settings.TextToSpeech.Rate);

        settings.BlindTest.RecordGame([("A", 4), ("B", 2)]);
        var copy = SettingsStore.Deserialize(SettingsStore.Serialize(settings));
        Assert.Equal(2, copy.BlindTest.Records.Count);
        Assert.Equal(4, copy.BlindTest.Records[0].TotalPoints);
    }

    [Fact]
    public void TextToSpeechHistoryKeepsTheLastPhrasesWithoutDuplicates()
    {
        var s = new TextToSpeechSettings();
        for (var i = 0; i < 15; i++) s.Remember($"Phrase {i}");
        s.Remember("phrase 3");
        Assert.Equal(TextToSpeechSettings.MaxHistory, s.History.Count);
        Assert.Equal("phrase 3", s.History[0]);
        Assert.Single(s.History, h => h.Equals("phrase 3", StringComparison.OrdinalIgnoreCase));
    }
}
