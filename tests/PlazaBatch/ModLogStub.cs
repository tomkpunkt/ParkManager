using System;

namespace ParkManager
{
    // The planners only use this catch-path logger; the game Mod is not loaded
    // by the standalone geometry test host.
    internal static class Mod
    {
        internal static readonly MockLog Log = new MockLog();
    }

    internal sealed class MockLog
    {
        internal void Warn(string message) => Console.Error.WriteLine(message);
    }
}
