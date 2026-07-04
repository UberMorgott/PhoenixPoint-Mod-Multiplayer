namespace Multiplayer.Harmony
{
    // Inc1 Task C: pure, Unity-free gate decision behind ClientTftvAircraftFreezePatch. Extracted from
    // the Harmony Prefix so the four cases (no engine / inactive / host / active-client) are unit-testable
    // without touching NetworkEngine or game types. Returns whether TFTV's AdjustAircraftSpeed (speed-set
    // + re-Navigate) should run NORMALLY:
    //   * true  -> let TFTV run: single-player / no session (engine absent or inactive), OR we are the host
    //              (the sole authoritative simulator).
    //   * false -> SUPPRESS: active-session CLIENT only. The client mirrors host-authoritative speed/path
    //              via the snapshot, so it must not self-adjust speed or re-navigate its own path.
    public static class ClientTftvAircraftFreezeGate
    {
        public static bool ShouldRunTftvNormally(bool engineExists, bool isActive, bool isHost)
        {
            if (!engineExists || !isActive) return true; // single-player / no active session
            if (isHost) return true;                      // host is the sole authoritative simulator
            return false;                                 // active client: suppress speed/re-navigate
        }
    }
}
