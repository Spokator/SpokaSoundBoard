using System.Runtime.CompilerServices;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Tests;

internal static class TestSetup
{
    /// <summary>
    /// The mixer and player tests render blocks far faster than real time: their decoders decode on demand so the
    /// results do not depend on thread pool timing. <see cref="DecodeAheadTests"/> exercise the real background mode.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => DecodeAhead.InlineByDefault = true;
}
