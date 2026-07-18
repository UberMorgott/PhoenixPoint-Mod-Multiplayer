using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Ambient "a delta apply is running" scope (law 8, indirect echo loop). Client delta-appliers wrap
    /// themselves in <c>using (SyncApplyScope.Enter())</c>; every intent-capture seam checks
    /// <see cref="Active"/> and lets native code run WITHOUT emitting an intent while an apply is on the
    /// stack — an apply that fires native game events can never echo an intent back to the host.
    /// Game code is main-thread only, so a plain static depth counter suffices (no ThreadStatic).
    /// </summary>
    public static class SyncApplyScope
    {
        private static int _depth;

        public static bool Active => _depth > 0;

        public static IDisposable Enter() => new Scope();

        /// <summary>Session teardown belt. Every <see cref="Enter"/> call site uses <c>using</c>, whose
        /// try/finally makes a leaked depth impossible today — this only guarantees that a future
        /// non-<c>using</c> caller can never wedge the counter and silently suppress intent capture for
        /// the rest of the process.</summary>
        public static void Reset() => _depth = 0;

        private sealed class Scope : IDisposable
        {
            public Scope() { _depth++; }
            public void Dispose() { _depth--; }
        }
    }
}
