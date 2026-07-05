using System;

namespace Multiplayer.Network
{
    // Inc4 feature flag + pure gate for the CLIENT geoscape sim-freeze. Design spec:
    // docs/superpowers/specs/2026-07-02-multiplayer-inc4-client-sim-freeze-design.md §3.4, §6.
    // NetworkEngine has no config system, so the flag is a plain static; every freeze injection point
    // reads it. The freeze MECHANISM (Timing.Paused=true re-assert, WriteClock Paused pin, glyph
    // decouple) rides behind this flag; the legacy producer-table + event-suppress path stays until S4
    // so flipping the flag OFF (+ rebuild) is a known-good rollback with zero code revert.
    public static class ClientSimFreeze
    {
        // COMMITTED DEFAULT-ON (Inc4 S3). S1 (clock-freeze) + S2 (host-driven travel mirror) both passed
        // their in-game gates, so the client sim-freeze is the shipped default — this ON value is now
        // permanent, not the temporary S1-gate flip it began as. Rollback until S4 retires the legacy
        // gates: set this false and rebuild → the producer-table + event-suppress path is fully restored
        // (both paths still present) → HEAD behaviour, no code revert.
        public static bool Enabled = true;

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

        // Inc4 S1 (§3.2) — DISPLAY-clock vs SIM-clock split, pure + unit-testable. The client's per-frame
        // TimeSyncManager.WriteClock overwrites the geoscape Timing via ProcessInstanceData. This picks the
        // value written to TimingInstanceData.Paused (the sim _paused field):
        //   * freeze ON  -> ALWAYS true — pins _paused=true every frame so any newly-Started geoscape
        //                   producer auto-Max's (NextUpdate.ConvertToTiming reads the live _paused) and any
        //                   reschedule (e.g. host speed-change → set_Scale) RE-Max's instead of un-freezing.
        //                   The host's real paused value is IRRELEVANT to the sim clock under the freeze; it
        //                   drives only the cosmetic pause/speed GLYPH (§3.3), never the sim.
        //   * freeze OFF -> the host's paused value (pre-S1 behaviour: the client clock mirrors host pause).
        // Note the display READOUT still tracks the host regardless — WriteClock hard-sets StartTime=display
        // (OwnNow=0 ⇒ Now==display), independent of this paused flag.
        public static bool SimPaused(bool freeze, bool hostPaused)
        {
            return freeze || hostPaused;
        }

        // Inc4 S1 review fix (BUG 1a) — which "current paused" the client time-control relay reads.
        // Under the freeze the local Timing.Paused is PINNED true, so relaying it poisons the host
        // (a speed click would relay {Paused=true} → host pauses). The relayed value must be the HOST
        // anchor's paused when the freeze is active and an anchor exists; local otherwise (host /
        // flag-OFF / pre-anchor behavior byte-identical).
        public static bool RelayCurrentPaused(bool freeze, bool haveAnchor, bool anchorPaused, bool localPaused)
        {
            return freeze && haveAnchor ? anchorPaused : localPaused;
        }

        // Inc4 S1 review fix (BUG 1b) — the paused arg the pause-button relay sends to the host.
        // The widget computes OnPauseTime(!_timing.Paused) (UIModuleTimeControl.cs:178); with local
        // Paused pinned true the computed arg is ALWAYS false → the client could only ever unpause.
        // Under the freeze the user's toggle intent is against the HOST state: send !glyphHostPaused.
        // No freeze → the widget's computed arg unchanged (host / flag-OFF byte-identical).
        public static bool PauseRelayArg(bool freeze, bool widgetPauseArg, bool glyphHostPaused)
        {
            return freeze ? !glyphHostPaused : widgetPauseArg;
        }

        // Inc4 S1 review fix (BUG 2) — freeze re-assert must reschedule UNCONDITIONALLY.
        // Timing.Paused's setter short-circuits when value==_paused (Timing.cs:112) → no
        // RescheduleForTiming. WriteClock field-pins _paused=true every frame via ProcessInstanceData
        // (no reschedule); if that pin lands before the re-assert, the setter no-ops and producers
        // Started while unpaused keep live times (each fires ONE stale tick). Contract: set Paused=true
        // via the setter, THEN always fire the explicit reschedule (delegate seam = the reflection
        // call in TimeSyncManager.FreezeClientGeoSim; pure + fakeable here).
        public static void ReassertFreeze(Action<bool> setPaused, Action rescheduleForTiming)
        {
            setPaused(true);            // commit Paused=true FIRST (reschedule must read a paused timing)
            rescheduleForTiming();      // then ALWAYS reschedule — even when the setter short-circuited
        }
    }
}
