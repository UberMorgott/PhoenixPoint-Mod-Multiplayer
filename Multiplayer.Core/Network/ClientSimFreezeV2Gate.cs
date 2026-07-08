using System;

namespace Multiplayer.Network
{
    // Inc4 — CLIENT geoscape sim-freeze V2 ("true sim pin"). Design: the client is a PURE MIRROR, so its
    // geoscape sim clock must not advance at all. V1 (ClientSimFreeze) pins Timing.Paused=true so producers
    // stay Max'd, but TimeSyncManager.WriteClock ALSO overwrote the geoscape Timing every frame via
    // ProcessInstanceData with StartTime = the host-mirrored display time — which ADVANCED Timing.Now each
    // frame (the client-only per-frame burner) and violated the "Now frozen" assumption other mirrors already
    // rely on (e.g. GeoVehicleExploreMirror anchors its progress bar "around the frozen Now").
    //
    // V2 splits the SIM clock from the DISPLAY clock on the client:
    //   * SIM   — StartTime is NOT rewritten; with Timing.Paused pinned true (asserted at each geoscape load
    //             by FreezeClientGeoSim, plus a cheap per-frame drift guard), Timing.Now stays CONSTANT →
    //             the geoscape sim clock is genuinely frozen (canon: client never simulates).
    //   * DISPLAY — the on-screen HUD date/time widget is repainted display-only from the host-mirrored value
    //             (ClientTimeDateDisplayFreezePatch), so the player still sees the live host clock.
    //
    // NetworkEngine has no config system, so the gate is a plain static (mirrors ClientSimFreeze.Enabled).
    // Every V2 injection point reads it; OFF = the exact V1 behaviour (per-frame StartTime advance, widget
    // reads Now itself) → a known-good, byte-identical rollback with no code revert.
    public static class ClientSimFreezeV2Gate
    {
        // DEFAULT-ON for validation. OFF (+ rebuild) restores V1: WriteClock advances the sim clock every
        // frame and the display postfix is inert (DisplayActive stays false).
        public static bool Enabled = true;

        // Pure gate: pin the sim clock (skip the per-frame sim write + drive the HUD display separately) ONLY
        // when the V2 gate is ON AND the V1 client sim-freeze is active. The <paramref name="freeze"/> input is
        // ClientSimFreeze.ShouldFreeze(...)'s result, so host / single-player / no-session / V1-flag-OFF all
        // fall through to false here as well (V2 is a strict refinement of V1, never active without it).
        public static bool ShouldPinSim(bool v2Enabled, bool freeze)
        {
            return v2Enabled && freeze;
        }

        // Pure reproduction of Base.Core.TimeUnit.DateTime (TimeUnit.cs:21 => default(DateTime) + _time) for a
        // game-time expressed in seconds. The V1 widget rendered its date from Timing.Now.DateTime where
        // Now == StartTime == TimeUnit.FromTimeSpan(TimeSpan.FromSeconds(displaySeconds)); the V2 display
        // postfix must paint the SAME DateTime from the mirrored display seconds while the sim Now is pinned.
        // Kept here (BCL-only, unit-tested) so the mod-side patch and the tests share one definition.
        public static DateTime DisplayDateTime(double gameSeconds)
        {
            return default(DateTime) + TimeSpan.FromSeconds(gameSeconds);
        }
    }
}
