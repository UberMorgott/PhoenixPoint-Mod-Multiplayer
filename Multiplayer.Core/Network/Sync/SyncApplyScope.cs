using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Re-entrancy guard: <see cref="IsApplying"/> is true while the engine is replaying a
    /// remote action or applying a wallet echo. Every sync interceptor checks this FIRST and
    /// passes the original call through, so engine-driven replays do not re-trigger interception
    /// (which would cause an infinite relay loop).
    /// Thread-static so it is scoped to the thread that drives the apply.
    /// </summary>
    public static class SyncApplyScope
    {
        [ThreadStatic] private static int _depth;

        public static bool IsApplying => _depth > 0;

        public static IDisposable Enter()
        {
            _depth++;
            return new Handle();
        }

        private sealed class Handle : IDisposable
        {
            private bool _done;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _depth--;
            }
        }
    }
}
