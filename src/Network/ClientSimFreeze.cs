namespace Multipleer.Network
{
    // Inc4 S0: feature flag + pure gate for the CLIENT geoscape sim-freeze. Design spec:
    // docs/superpowers/specs/2026-07-02-multipleer-inc4-client-sim-freeze-design.md §3.4.
    // NetworkEngine has no config system, so the flag is a plain static (default-OFF); every freeze
    // injection point reads it. S0 ships THIS flag + the empty guarded freeze-activate patch
    // (ClientGeoSimFreezePatch) — inert while OFF, byte-unchanged in-game. The freeze MECHANISM
    // (Timing.Paused=true re-assert, WriteClock Paused pin, glyph decouple) lands in S1 behind this same
    // flag; the legacy producer-table + event-suppress path stays until S4 so flag-OFF = known-good rollback.
    public static class ClientSimFreeze
    {
        // Default-OFF. Flip only for the S1 in-game gate (a separate, revertable commit).
        public static bool Enabled = false;

        // Pure, Unity-free freeze-decision gate (mirrors ClientTftvAircraftFreezeGate.ShouldRunTftvNormally
        // so the truth table is unit-testable without NetworkEngine or game types). Freeze the client
        // geoscape sim ONLY when the feature flag is ON, a co-op session is active, and we are NOT the host:
        //   * feature flag OFF                  -> false (S0/rollback: never freeze)
        //   * single-player / no active session -> false (nothing to mirror; run normally)
        //   * host                              -> false (host is the sole authoritative simulator)
        //   * active-session client + flag ON   -> true  (client mirrors host state; freeze local sim)
        public static bool ShouldFreeze(bool enabled, bool engineExists, bool isActive, bool isHost)
        {
            if (!enabled) return false;                    // feature flag OFF -> never freeze
            if (!engineExists || !isActive) return false;  // single-player / no active session
            if (isHost) return false;                      // host is the sole authoritative simulator
            return true;                                   // active-session client: freeze local sim
        }
    }
}
