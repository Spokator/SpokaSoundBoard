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
