using System;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-2: [ThreadStatic] re-entrancy guard for client-side entity-op replay. Mirrors
    // CommandRelay's IsApplying guard (CommandRelay.cs:26-27). The ClientEntityOpApplier wraps the
    // native CreateVehicle/Destroy/DestroySite call in `using (EntityReplicationScope.Enter())` so the
    // birth/death postfixes those native calls trigger (HostEntityOpBroadcastPatch) recognize a replay
    // and do NOT re-broadcast it (host) / do not recurse. Nested-safe (restores the prior value).
    public static class EntityReplicationScope
    {
        [ThreadStatic] private static bool _applying;
        public static bool IsApplying => _applying;

        public static IDisposable Enter() => new Token();

        private sealed class Token : IDisposable
        {
            private readonly bool _prev;
            public Token() { _prev = _applying; _applying = true; }
            public void Dispose() { _applying = _prev; }
        }
    }
}
