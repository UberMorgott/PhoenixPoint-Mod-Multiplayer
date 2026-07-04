using System.Collections.Generic;
using Multiplayer.Network;
using Xunit;

// Inc4 S0: unit-tests the pure freeze-decision gate for the CLIENT geoscape sim-freeze. The Harmony wiring
// (ClientGeoSimFreezePatch postfix on GeoscapeEventSystem.OnLevelStart) is not unit-testable, but the
// decision — "should the client geoscape sim be frozen?" — is extracted into ClientSimFreeze.ShouldFreeze
// (mirroring ClientTftvAircraftFreezeGate) so the truth table is asserted directly. Freeze ONLY on:
// flag ON + active session + NOT host. Also pins the S0 invariant that the flag ships default-OFF (inert).
public class ClientSimFreezeGateTests
{
    [Fact]
    public void FlagOff_NeverFreezes_EvenActiveClient()
    {
        // Feature flag OFF (S0/default, and the flag-OFF rollback path): never freeze, even an active client.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: false, engineExists: true, isActive: true, isHost: false));
    }

    [Fact]
    public void FlagOn_NoEngine_DoesNotFreeze()
    {
        // Single-player / no NetworkEngine.Instance -> run normally, no freeze.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: false, isActive: false, isHost: false));
    }

    [Fact]
    public void FlagOn_EngineInactive_DoesNotFreeze()
    {
        // Engine exists but no active session (lobby / torn down) -> no freeze.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: false, isHost: false));
    }

    [Fact]
    public void FlagOn_ActiveHost_DoesNotFreeze()
    {
        // Host is the sole authoritative simulator -> never freeze the host sim.
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: true));
    }

    [Fact]
    public void FlagOn_ActiveClient_Freezes()
    {
        // Active-session CLIENT + flag ON -> freeze the local geoscape sim (mirror host-authoritative state).
        Assert.True(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: false));
    }

    [Fact]
    public void FlagEnabledForS1InGameGate()
    {
        // The flag is DESIGNED default-OFF (inert scaffolding), but it is FLIPPED ON for the Inc4 S1 in-game
        // gate by the single revertable "enable" commit. This test pins that live state: `git revert` of that
        // commit restores `Enabled = false` AND this assertion (back to `Assert.False`) together — clean
        // rollback. It flips back to the committed default at S3 only after the S1+S2 gates pass.
        Assert.True(ClientSimFreeze.Enabled);
    }

    // ─── Inc4 S1 (§3.2) — DISPLAY-clock vs SIM-clock split (WriteClock paused-field selection) ───

    [Fact]
    public void SimPaused_UnderFreeze_AlwaysTrue_RegardlessOfHost()
    {
        // Under the freeze the value written to TimingInstanceData.Paused (the sim _paused field) is ALWAYS
        // true — so producers stay Max'd — REGARDLESS of the host anchor's paused value. The host paused value
        // drives only the cosmetic glyph, never the sim clock. (Display readout still tracks host via StartTime.)
        Assert.True(ClientSimFreeze.SimPaused(freeze: true, hostPaused: false));
        Assert.True(ClientSimFreeze.SimPaused(freeze: true, hostPaused: true));
    }

    [Fact]
    public void SimPaused_NoFreeze_MirrorsHost()
    {
        // Flag OFF / not an active client (freeze=false): pre-S1 behaviour is preserved byte-for-byte — the
        // client sim clock's paused field mirrors the host anchor's paused value.
        Assert.False(ClientSimFreeze.SimPaused(freeze: false, hostPaused: false));
        Assert.True(ClientSimFreeze.SimPaused(freeze: false, hostPaused: true));
    }

    // ─── Inc4 S1 (§3.1) — load-completion freeze re-assert predicate ───

    [Fact]
    public void ReAssertPredicate_IsShouldFreeze_ClientInSessionOnly()
    {
        // The load-completion re-assert (ClientGeoSimFreezePatch.Postfix → TimeSyncManager.FreezeClientGeoSim)
        // fires iff ShouldFreeze: an active-session CLIENT with the flag ON — and NEVER the host, never
        // single-player, never with the flag OFF. It re-runs on EVERY (re)load (postfix on OnLevelStart), but
        // the DECISION is exactly this pure predicate.
        Assert.True(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: false));
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: true, isActive: true, isHost: true));
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: false, engineExists: true, isActive: true, isHost: false));
        Assert.False(ClientSimFreeze.ShouldFreeze(enabled: true, engineExists: false, isActive: false, isHost: false));
    }

    // ─── Inc4 S1 review fix BUG 1a — relayed "current paused" source (TimeSyncManager.CurrentPaused) ───

    [Fact]
    public void RelayCurrentPaused_FreezeWithAnchor_ReadsHostAnchor_NotPinnedLocal()
    {
        // Under the freeze the local Timing.Paused is PINNED true. A client SPEED click relays
        // {CurrentPaused(), presetIndex}; reading the pinned local would relay Paused=true → the host
        // PAUSES on a mere speed change. The relayed value must be the HOST anchor's paused.
        Assert.False(ClientSimFreeze.RelayCurrentPaused(freeze: true, haveAnchor: true, anchorPaused: false, localPaused: true));
        Assert.True(ClientSimFreeze.RelayCurrentPaused(freeze: true, haveAnchor: true, anchorPaused: true, localPaused: false));
    }

    [Fact]
    public void RelayCurrentPaused_FreezeWithoutAnchor_FallsBackToLocal()
    {
        // No host anchor received yet → nothing authoritative to read; fall back to the local value.
        Assert.True(ClientSimFreeze.RelayCurrentPaused(freeze: true, haveAnchor: false, anchorPaused: false, localPaused: true));
        Assert.False(ClientSimFreeze.RelayCurrentPaused(freeze: true, haveAnchor: false, anchorPaused: true, localPaused: false));
    }

    [Fact]
    public void RelayCurrentPaused_NoFreeze_ReadsLocal_ByteIdentical()
    {
        // Host / flag-OFF (freeze inactive): pre-fix behavior preserved byte-for-byte — local read,
        // anchor ignored even when present.
        Assert.True(ClientSimFreeze.RelayCurrentPaused(freeze: false, haveAnchor: true, anchorPaused: false, localPaused: true));
        Assert.False(ClientSimFreeze.RelayCurrentPaused(freeze: false, haveAnchor: true, anchorPaused: true, localPaused: false));
    }

    // ─── Inc4 S1 review fix BUG 1b — pause-button relay arg (TimeControlPausePatch) ───

    [Fact]
    public void PauseRelayArg_UnderFreeze_TogglesHostGlyphState()
    {
        // The widget computes OnPauseTime(!_timing.Paused); with local Paused pinned true the arg is
        // ALWAYS false → the client could only ever UNPAUSE the host. Under the freeze the toggle
        // intent is against the HOST state: relay !GlyphHostPaused, whatever the widget computed.
        Assert.True(ClientSimFreeze.PauseRelayArg(freeze: true, widgetPauseArg: false, glyphHostPaused: false));
        Assert.True(ClientSimFreeze.PauseRelayArg(freeze: true, widgetPauseArg: true, glyphHostPaused: false));
        Assert.False(ClientSimFreeze.PauseRelayArg(freeze: true, widgetPauseArg: false, glyphHostPaused: true));
        Assert.False(ClientSimFreeze.PauseRelayArg(freeze: true, widgetPauseArg: true, glyphHostPaused: true));
    }

    [Fact]
    public void PauseRelayArg_NoFreeze_PassesWidgetArg_ByteIdentical()
    {
        // Freeze inactive (host / flag-OFF): the widget's computed arg passes through unchanged.
        Assert.False(ClientSimFreeze.PauseRelayArg(freeze: false, widgetPauseArg: false, glyphHostPaused: false));
        Assert.True(ClientSimFreeze.PauseRelayArg(freeze: false, widgetPauseArg: true, glyphHostPaused: true));
        Assert.True(ClientSimFreeze.PauseRelayArg(freeze: false, widgetPauseArg: true, glyphHostPaused: false));
        Assert.False(ClientSimFreeze.PauseRelayArg(freeze: false, widgetPauseArg: false, glyphHostPaused: true));
    }

    // ─── Inc4 S1 review fix BUG 2 — freeze re-assert must reschedule even when the setter no-ops ───

    [Fact]
    public void ReassertFreeze_PausedAlreadyPinnedTrue_StillReschedules()
    {
        // Model the NATIVE setter short-circuit (Timing.cs:112): value==_paused → NO reschedule.
        // WriteClock field-pins _paused=true every frame (ProcessInstanceData, no reschedule); when the
        // pin lands before the re-assert, the setter no-ops — producers Started while unpaused keep
        // live times and each fires ONE stale tick. The re-assert must fire the explicit reschedule
        // UNCONDITIONALLY.
        bool paused = true; // pre-pinned by WriteClock's field write
        int setterReschedules = 0, explicitReschedules = 0;
        ClientSimFreeze.ReassertFreeze(
            setPaused: v => { if (v != paused) { paused = v; setterReschedules++; } }, // native short-circuit
            rescheduleForTiming: () => explicitReschedules++);
        Assert.True(paused);
        Assert.Equal(0, setterReschedules);   // setter no-op'd, as in the live race
        Assert.Equal(1, explicitReschedules); // the fix: explicit reschedule fired anyway
    }

    [Fact]
    public void ReassertFreeze_SetsPausedBeforeRescheduling()
    {
        // Order contract: Paused=true must be committed BEFORE the reschedule, so RescheduleForTiming's
        // UpdateSchedulerTime reads a paused timing (NextUpdate.ConvertToTiming → Max) for every producer.
        var calls = new List<string>();
        bool paused = false; // setter path really flips (fresh load, pin not landed yet)
        ClientSimFreeze.ReassertFreeze(
            setPaused: v => { if (v != paused) { paused = v; calls.Add("setPaused"); } },
            rescheduleForTiming: () => calls.Add("reschedule"));
        Assert.Equal(new[] { "setPaused", "reschedule" }, calls);
        Assert.True(paused);
    }
}
