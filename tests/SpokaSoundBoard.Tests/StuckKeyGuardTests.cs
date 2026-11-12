using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

/// <summary>2.8.1: a key whose release never reached the hook (lock screen, UAC…) is released.</summary>
public sealed class StuckKeyGuardTests
{
    [Fact]
    public void AKeyStillDown_IsKept()
    {
        var guard = new StuckKeyGuard();
        for (var i = 0; i < 5; i++) Assert.Empty(guard.Check([0x56], _ => true));
    }

    [Fact]
    public void AKeyUp_IsReleasedOnTheSecondCheckOnly()
    {
        var guard = new StuckKeyGuard();
        Assert.Empty(guard.Check([0x56], _ => false));
        Assert.Equal([0x56], guard.Check([0x56], _ => false));
    }

    [Fact]
    public void ABriefMiss_StartsAgainFromZero()
    {
        var guard = new StuckKeyGuard();
        Assert.Empty(guard.Check([0x56], _ => false));
        Assert.Empty(guard.Check([0x56], _ => true));
        Assert.Empty(guard.Check([0x56], _ => false));
        Assert.Equal([0x56], guard.Check([0x56], _ => false));
    }

    [Fact]
    public void AKeyReleasedNormally_IsForgotten()
    {
        var guard = new StuckKeyGuard();
        Assert.Empty(guard.Check([0x56], _ => false));
        // Its key-up arrived: it is no longer held, then pressed again much later.
        Assert.Empty(guard.Check([], _ => false));
        Assert.Empty(guard.Check([0x56], _ => false));
    }

    [Fact]
    public void OnlyTheStuckKeysAreReleased()
    {
        var guard = new StuckKeyGuard();
        bool Down(int vk) => vk == 0x05; // mouse side button still held, « V » stuck
        guard.Check([0x05, 0x56], Down);
        Assert.Equal([0x56], guard.Check([0x05, 0x56], Down));
    }
}

/// <summary>2.8.1: while typing in the app, only keys that write text are kept for the text box.</summary>
public sealed class TypingKeyTests
{
    [Theory]
    [InlineData(0x41)] // A
    [InlineData(0x20)] // Espace
    [InlineData(0x35)] // 5
    [InlineData(0x65)] // pavé 5
    [InlineData(0xBC)] // virgule
    [InlineData(0xDE)] // ²/apostrophe
    [InlineData(0xE2)] // < >
    [InlineData(0x0D)] // Entrée
    [InlineData(0x08)] // Retour arrière
    [InlineData(0x25)] // flèche gauche
    public void TextKeys_AreTyping(int vk) => Assert.True(KeyNames.IsTypingKey(vk));

    [Theory]
    [InlineData(0x70)] // F1
    [InlineData(0x7B)] // F12
    [InlineData(0x87)] // F24
    [InlineData(KeyNames.VkMButton)]
    [InlineData(KeyNames.VkXButton1)]
    [InlineData(KeyNames.VkXButton2)]
    [InlineData(0xB3)] // lecture / pause
    [InlineData(0x13)] // Pause
    [InlineData(0x91)] // Arrêt défil
    public void OtherKeys_StayShortcuts(int vk) => Assert.False(KeyNames.IsTypingKey(vk));
}
