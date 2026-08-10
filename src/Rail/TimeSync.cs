using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// WHO SENT WHICH AIRCRAFT — host-only, and the ONLY reason it exists is the tab-pause rule in
    /// <see cref="TimeSync"/>. Vanilla stops the world whenever the player leaves the map, which is right
    /// for one player and wrong for three: a peer clicking Manufacturing froze the aircraft another peer
    /// was steering (live 2026-08-11, host Player.log — nine `[MP][pause] peer=N → paused=True` lines, every
    /// one of them a screen change). The game itself records no ordering peer on <c>GeoVehicle</c>, so the
    /// only place that knowledge exists is where the order was accepted: <c>VehicleSync</c>'s travel seam.
    /// Entries are pruned lazily on read — an aircraft that landed stops entitling anybody to a pause.
    /// ponytail: plain dictionary keyed by the live entity, no identity strings — host-local, one process,
    /// and it is only ever read on a screen change (user-gesture rate).
    /// </summary>
    internal static class AircraftDispatch
    {
        /// <summary>The host's own marker. <see cref="IntentRail.Reject"/> already treats 0 as "not a
        /// connected client", so it can never collide with a real sender id.</summary>
        internal const ulong HostPeer = 0;

        private static readonly Dictionary<PhoenixPoint.Geoscape.Entities.GeoVehicle, ulong> _byVehicle =
            new Dictionary<PhoenixPoint.Geoscape.Entities.GeoVehicle, ulong>();

        internal static void Reset() { _byVehicle.Clear(); }

        /// <summary>Record the peer whose order put this aircraft in the air. Player-faction aircraft
        /// only: attributing an alien scout's sim route to the host would make the host permanently
        /// "steering something" and re-freeze everyone the moment it opened a tab.</summary>
        internal static void Note(PhoenixPoint.Geoscape.Entities.GeoVehicle vehicle, ulong peer)
        {
            var level = GameUtl.CurrentLevel();
            var geo = level == null ? null : level.GetComponent<GeoLevelController>();
            if (vehicle == null || geo == null || !ReferenceEquals(vehicle.Owner, geo.PhoenixFaction)) return;
            _byVehicle[vehicle] = peer;
        }

        /// <summary>True when <paramref name="peer"/> ordered an aircraft that is STILL in the air.</summary>
        internal static bool HasAircraftInFlight(ulong peer)
        {
            if (_byVehicle.Count == 0) return false;
            List<PhoenixPoint.Geoscape.Entities.GeoVehicle> landed = null;
            bool found = false;
            foreach (var kv in _byVehicle)
            {
                bool flying;
                try { flying = kv.Key != null && kv.Key.Travelling; }
                catch { flying = false; }   // destroyed/torn-down aircraft: prune, never throw on a screen change
                if (!flying)
                {
                    if (landed == null) landed = new List<PhoenixPoint.Geoscape.Entities.GeoVehicle>();
                    landed.Add(kv.Key);
                    continue;
                }
                if (kv.Value == peer) found = true;
            }
            if (landed != null) foreach (var v in landed) _byVehicle.Remove(v);
            return found;
        }
    }

    /// <summary>
    /// GEOSCAPE TIME CONTROL intent seam (law 4a). Same shape as <see cref="PersonnelSync"/>: the
    /// client BLOCKS the native clock mutation and sends an intent; the host runs the SAME native
    /// path; the result reaches every peer through the existing host→client machinery — Paused/Scale
    /// as ordinary "T" leaves via the property setters, the clock base via the "TA" anchor, both
    /// same-frame because the host's Paused/Scale setters raise EffectiveScaleChangedEvent which
    /// DiffEngine's change-driven flush already subscribes (DiffEngine.ArmChangeDrivenFlush).
    /// There is NO host→all message here: this file is intent-only.
    ///
    /// SEAM CHOICE — the two native funnels every geoscape time write routes through (call-site
    /// sweep over the decompile):
    ///   • <c>UIModuleTimeControl.OnPauseTime</c> (UIModuleTimeControl.cs:182) — the pause/resume
    ///     GESTURE: play/pause button + keyboard land in OnPauseTimeKeyPressed:173 → here, where the
    ///     module writes <c>_timing.Paused</c> DIRECTLY (:187) before raising
    ///     OnTimePauseChangeRequested. Blocking the method blocks both the write and the event.
    ///   • <c>UIModuleTimeControl.UpdateSelectedTime</c> (:268) — the ONE UI write of
    ///     <c>_timing.Scale</c>: speed +/- buttons → ChangeTime → SelectTimePreset:192 → here.
    ///   • <c>GeoscapeView.SetGamePauseState</c> (GeoscapeView.cs:1256) — the PROGRAMMATIC pause
    ///     funnel: screen-open pauses (UIStateResearch:22, UIStateDiplomacy:27, UIStateManufacturing:51,
    ///     UIStateGeoscapeOptions:36, UIStateGeoscapeLog:18, UIModuleGeoSectionBar:119-194), the
    ///     launch resume (UIStateVehicleSelected:422/897) and the gesture event itself
    ///     (UIStateNothingSelected:99). Captured too: a client screen-open pause must pause EVERYONE
    ///     (the host's own screen-open pause already does, via the rail) — and any local-only write
    ///     is exactly the free-running-clock bug this seam exists to kill.
    /// There is no separate time-skip control in the geoscape: every speed/pause control routes
    /// through the two module methods above. NOT captured (deliberate): the quick-save/load
    /// coroutines' direct <c>Timing.Paused</c> writes (GeoscapeView.cs:1146/1154/1171 — transient,
    /// restored around the save) and level-init writes (GeoLevelController.cs:493/568/611 — reload
    /// boundary, TimeAnchor.Reset owns it); residual local drift from those is corrected by
    /// <see cref="TimeAnchor.EnforceDrift"/> on the tick below.
    ///
    /// The interception mini-game reuses the same module type bound to ITS OWN clock
    /// (UIStateInterception.cs:566 — <c>_interceptionGameController.Timing</c>, not the level clock);
    /// that one stays fully local, hence the <see cref="IsLevelClock"/> guard.
    ///
    /// HOST replay runs the identical native funnels: <c>GeoscapeView.SetGamePauseState</c> for
    /// pause (keeps the TimeLimit guard), <c>UIModuleTimeControl.SelectTimePreset</c> for speed
    /// (clamps the index natively, keeps the host's speed label honest — the module never repaints
    /// its label from a raw Scale write). Validation is trivial by design: co-op parity, any
    /// connected peer may control time. The wire carries the PRESET INDEX, never a raw float scale —
    /// both peers ship identical PresetTimes (mod parity, law 10), so the index is the stable id.
    /// </summary>
    public static class TimeSync
    {
        // Intent ops (GeoTimeIntent inner payload: [nonce:u32][op:u8][val:u8]).
        internal const byte OpPause = 1;  // val = 0 resume / 1 pause / 2 tab pause → SetGamePauseState
        internal const byte OpSpeed = 2;  // val = preset index            → SelectTimePreset
        // val 2 on OpPause = A TAB PAUSE, and it is the ONE conditional value on this wire: the sending
        // peer left the map for one of ITS OWN screens, which in vanilla stops the world. The host
        // decides whether that peer is entitled to stop it — see <see cref="HonourTabPause"/>.
        internal const byte PauseValTab = 2;
        // op 3 was the WINDOW HOLD. Dead, and it is not coming back: see PauseHold. A blocking window is a
        // one-shot pause the GAME issues (RequestGamePause → SetGamePauseState), captured by the seam below.

        private static float _nextEnforceAt;

        // The module's clock binding (private) — read only to tell the LEVEL clock from the
        // interception one; _updateTimeSpeedState is the view-refresh half of the blocked
        // UpdateSelectedTime, re-stated so the speed label repaints from the staged preset.
        private static readonly FieldInfo FTiming = AccessTools.Field(typeof(UIModuleTimeControl), "_timing");
        private static readonly FieldInfo FUpdateSpeedState = AccessTools.Field(typeof(UIModuleTimeControl), "_updateTimeSpeedState");

        private static bool _bindChecked;

        private static bool BindOk()
        {
            if (!_bindChecked)
            {
                _bindChecked = true;
                if (FTiming == null || FUpdateSpeedState == null)
                    Debug.LogError("[MP][time] FIELD BIND FAILED on UIModuleTimeControl — " +
                                   "client time gestures CANNOT be captured; clocks will desync.");
                else
                    Debug.Log("[MP][time] module fields bound");
            }
            return FTiming != null;
        }

        public static void Reset() => ResetForReloadBoundary();

        /// <summary>Stateless per intent (same rca-3 contract as <see cref="PersonnelSync"/>): nothing
        /// geoscape-bound is cached, dedup/nonce live in <see cref="IntentRail"/>, and the clock itself is
        /// re-seeded across the boundary by <see cref="TimeAnchor.Reset"/>. The ONE thing that does not
        /// survive the boundary is the dispatch ledger: the transferred save replaces every GeoVehicle,
        /// so entitlements keyed on the old instances would keep a landed aircraft "in the air" forever.
        /// </summary>
        public static void ResetForReloadBoundary() { AircraftDispatch.Reset(); }

        /// <summary>Arm the 0xB0 surface on the generic intent engine. No family reconverge and no
        /// reject prefixes: the client BLOCKED its local clock write, so a dropped intent leaves both
        /// clocks still equal, and residual drift from any writer is continuously corrected by
        /// <see cref="TimeAnchor.EnforceDrift"/> (~1 Hz) — the standing corrector IS this family's
        /// convergence mechanism.</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpPause] = HandleIntentOp,
                [OpSpeed] = HandleIntentOp,
            };
            IntentRail.Register(SurfaceIds.GeoTimeIntent, "time", ops);
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>Only the geoscape LEVEL clock is shared state; the interception module drives a
        /// private mini-game clock and stays native. No geoscape (mid-load) → native too: the reload
        /// boundary + save transfer own that window. Callers check <see cref="BindOk"/> first.</summary>
        private static bool IsLevelClock(UIModuleTimeControl module)
        {
            var geo = GeoLevel();
            return geo != null && ReferenceEquals(FTiming.GetValue(module), geo.Timing);
        }

        private static void Send(byte op, byte value, string what)
            => IntentRail.Send(SurfaceIds.GeoTimeIntent, op, what, w => w.Write(value));

        /// <summary>THE ONE ASYMMETRY in this family's block-first posture: after shipping the intent, a
        /// client lets a PAUSE run natively and still blocks a RESUME.
        ///
        /// It exists because block-first had a silent hole exactly here. A client's window pause reaches a
        /// host that is ALREADY paused, so <c>SetGamePauseState</c>:1265 writes a value the change-gated
        /// <c>Timing.Paused</c> setter (Timing.cs:112) swallows — no event, no diff, no delta — and the
        /// client that blocked its own write free-runs forever with nothing to correct it. That is the
        /// aircraft flying on while every player reads the popup (live 2026-08-04).
        ///
        /// Letting the pause through is not an optimistic model write in the sense law 3 forbids: a clock
        /// can only end up TOO SLOW, the host's authoritative rate rides "T"/"TA" as before, and
        /// <see cref="TimeAnchor.EnforceDrift"/> re-asserts it within ~1 s if the host disagrees. A RESUME
        /// stays blocked because that direction is not self-healing — it would run the client's campaign
        /// ahead of the host's. Blocked is NOT refused: the intent ships on the same line, the host applies
        /// it unconditionally (nothing vetoes a resume any more — see <see cref="PauseHold"/>) and the
        /// delta comes back, so the cost is one round trip and never a player who cannot play. The same
        /// EnforceDrift is the backstop for the mirror-image swallow — a client left paused against a host
        /// that is already running is re-asserted false within ~1 s (TimeAnchor.cs:224-245).
        /// Both native bodies are provably inert past the write for <c>paused == true</c>: the only side
        /// branch in <c>SetGamePauseState</c> needs <c>!paused</c> (GeoscapeView.cs:1259).</summary>
        private static bool PausesLocally(bool paused) => paused;

        // ─── THE TAB-PAUSE RULE (the pause follows the DISPATCHER, never the audience) ────────────────

        /// <summary>Set while the host replays a peer's pause intent through the native funnel, so the
        /// re-entrant <see cref="ProgrammaticPauseCapturePatch"/> does not re-judge a verdict that was
        /// already reached FOR ANOTHER PEER against the host's own screen.</summary>
        [ThreadStatic] private static bool _replayingRemotePause;

        /// <summary>
        /// A PRIVATE SCREEN — one peer's own tab, not a window every peer is looking at.
        ///
        /// Deliberately an ALLOW-LIST, and the fail-safe direction is "unknown ⇒ global": a window this
        /// table has never heard of keeps today's everyone-pauses behaviour, which is the wrong-but-safe
        /// answer (the 2026-08-04 bug was the opposite — an aircraft flying on while every player read a
        /// popup). Two shapes reach the seam and both are covered:
        ///   • the SECTION-BAR click, which pauses BEFORE it switches (UIModuleGeoSectionBar.cs:119/135/
        ///     148/160/172/183/194 — <c>SetGamePauseState(true)</c> then <c>To*State()</c>), so the state
        ///     still current here is the MAP state the player is leaving;
        ///   • the screen's own <c>EnterState</c> pause (UIStateResearch.cs:22/24, UIStateManufacturing
        ///     .cs:53, UIStateReplenish.cs:28, UIStatePhoenixBaseLayout.cs:42, UIStateDiplomacy.cs:26,
        ///     UIStateGeoscapeLog.cs:17, UIStateGeoscapeOptions.cs:35) and the deferred
        ///     <c>RequestGamePause</c> ones (GeoscapeView.cs:508 edit-unit, :730 haven details) — by then
        ///     the state IS the tab, because StateStack.SwitchToState pushes at StateStack.cs:86 and only
        ///     then calls Enter at :88.
        /// A queued WINDOW never lands here: GeoscapeViewSwitchQuery.cs:66-70 switches the state in the
        /// same call that requests the pause, and RequestPauseCrt (GeoscapeView.cs:1289-1295) waits a
        /// frame — so the modal/event/cutscene/deployment state is already current when the pause runs.
        /// </summary>
        private static readonly HashSet<Type> PrivateScreens = new HashSet<Type>
        {
            // the map itself (the section bar pauses from here, before the switch)
            typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected), typeof(UIStateInitial),
            // the tabs
            typeof(UIStateResearch), typeof(UIStateManufacturing), typeof(UIStateDiplomacy),
            typeof(UIStatePhoenixpedia), typeof(UIStatePhoenixBaseLayout), typeof(UIStateReplenish),
            typeof(UIStateGeoscapeLog), typeof(UIStateGeoscapeOptions), typeof(UIStateSaveLoad),
            typeof(UIStateMemorial), typeof(UIStateTrade), typeof(UIStateHavenDetailsScreen),
            // roster / soldier / vehicle screens reached from them
            typeof(UIStateGeoRoster), typeof(UIStateVehicleRoster), typeof(UIStateRosterAliens),
            typeof(UIStateRosterRecruits), typeof(UIStateEditSoldier), typeof(UIStateEditVehicle),
            typeof(UIStateViewVehicle), typeof(UIStateGeoCharacterStatus), typeof(UIStateBionics),
            typeof(UIStateMutate), typeof(UIStateBuyMutoid), typeof(UIStateSoldierCustomization),
            typeof(UIStateVehicleCustomization),   // + UIStateSoldierCustomization above; their shared
                                                   // base UIStateUnitCustomization<T> is generic, and the
                                                   // two closed subclasses are the only ones the game opens

            typeof(UIStatePhoenixFacilityRosterAssignment), typeof(UIStateVehicleBayAssignment),
        };

        private static bool IsPrivateScreenPause(GeoLevelController geo)
        {
            var state = geo.View == null ? null : geo.View.CurrentViewState;
            return state != null && PrivateScreens.Contains(state.GetType());
        }

        /// <summary>
        /// THE OWNER'S RULE, in one place and on the host only: a tab stops the shared clock ONLY for the
        /// peer who dispatched an aircraft that is still flying. Everybody else's tab is their own
        /// business — a peer reading Manufacturing may not freeze the peer steering on the map, and a peer
        /// who goes AFK inside a tab may not freeze the campaign (NO QUORUM: nothing here waits on a human,
        /// this only decides a pause bit).
        /// </summary>
        private static bool HonourTabPause(ulong peer)
        {
            bool own = AircraftDispatch.HasAircraftInFlight(peer);
            Debug.Log("[MP][pause] peer=" + peer + " opened a tab → " + (own
                ? "PAUSED — this peer dispatched an aircraft that is still in the air, so its own departure "
                  + "stops the clock exactly like single player"
                : "NOT paused — nothing this peer sent is still flying, and another peer may be steering on "
                  + "the map; vanilla would have frozen everyone"));
            return own;
        }

        /// <summary>The local half: never freeze the peers still on the map on this peer's say-so. The host
        /// judges itself immediately; a client ships the question, because the dispatch ledger is
        /// host-only, and blocks its own pause meanwhile (the host's verdict comes back as the ordinary
        /// "T" Paused delta — and <see cref="TimeAnchor.EnforceDrift"/> is the standing backstop either
        /// way).</summary>
        private static bool PrivateScreenPause()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // solo: vanilla, untouched
            if (engine.IsHost) return HonourTabPause(AircraftDispatch.HostPeer);
            Send(OpPause, PauseValTab, "pause (tab)");
            Debug.Log("[MP][pause] this peer opened a tab — asking the host whether its own aircraft is " +
                      "still in the air; the map keeps running for everyone until the host says otherwise");
            return false;
        }

        // ─── Harmony seams (law 4a, intent-capture only) ───────────────────

        /// <summary>The pause/resume gesture. A no-change request (SetTimeState re-stating the
        /// current value from GeoscapeView.RequestPauseCrt:1295) asks for nothing — the same no-op
        /// test native's setter would apply. The pause indicator flips when the host echo lands:
        /// the rail applies Paused through the property setter, whose OnPausedEvent the module
        /// already subscribes (UIModuleTimeControl.cs:105).</summary>
        [HarmonyPatch(typeof(UIModuleTimeControl), "OnPauseTime")]
        internal static class PauseGestureCapturePatch
        {
            private static bool Prefix(UIModuleTimeControl __instance, bool pause)
            {
                if (IntentRail.ShouldRunNative()) return true;
                if (!BindOk()) return false;                 // cannot identify the clock: never write locally
                if (!IsLevelClock(__instance)) return true;  // interception clock / mid-load: local by design
                try
                {
                    var geo = GeoLevel();
                    if (geo != null && geo.Timing.Paused != pause)
                        Send(OpPause, pause ? (byte)1 : (byte)0, pause ? "pause" : "resume");
                }
                catch (Exception ex) { Debug.LogError("[MP][time] pause capture failed: " + ex); }
                return PausesLocally(pause);
            }
        }

        /// <summary>The speed gesture. SelectTimePreset:192 has already staged SelectedPresetTime
        /// when this runs, so the intent ships that staged index; the view-refresh half of the
        /// blocked native body (_updateTimeSpeedState = true) is re-stated so the label repaints
        /// from the staged preset — the CLOCK itself only changes when the host echo lands.</summary>
        [HarmonyPatch(typeof(UIModuleTimeControl), "UpdateSelectedTime")]
        internal static class SpeedGestureCapturePatch
        {
            private static bool Prefix(UIModuleTimeControl __instance)
            {
                if (IntentRail.ShouldRunNative()) return true;
                if (!BindOk()) return false;                 // cannot identify the clock: never write locally
                if (!IsLevelClock(__instance)) return true;  // interception clock / mid-load: local by design
                try
                {
                    Send(OpSpeed, (byte)__instance.SelectedPresetTime, "speed preset=" + __instance.SelectedPresetTime);
                    FUpdateSpeedState?.SetValue(__instance, true);
                }
                catch (Exception ex) { Debug.LogError("[MP][time] speed capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>The programmatic pause funnel (screen-open pauses, launch resume). Always the
        /// LEVEL clock (SetGamePauseState body reads _context.Level.Timing). Blocked wholesale on a
        /// client: the TimeLimit branch inside it is host-only logic.
        ///
        /// IT IS ALSO THE WINDOW SEAM, and the mod has no other: a queued blocking window reaches exactly
        /// here — GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch:58-73 → RequestGamePause:1269 →
        /// RequestPauseCrt:1293 → SetGamePauseState(true) — so the peer whose event popup or cutscene
        /// opened pauses itself (PausesLocally) and relays the pause to everyone, ONCE. Nothing tracks
        /// whose window is still up: the pause is a courtesy edge, any peer's later resume simply wins
        /// (PauseHold).
        ///
        /// UNLIKE the two gesture seams above, this method is ENGINE-driven: almost every geoscape UI
        /// transition re-asserts the pause state on an already-paused clock (UIModuleGeoSectionBar.cs:119-194
        /// on every section click, UIStateResearch:22, UIStateManufacturing:51, UIStateDiplomacy:27/39,
        /// UIStateGeoscapeLog:18, UIStateGeoscapeOptions:36, GeoscapeView.RequestPauseCrt:1293). Those
        /// no-change calls are tested out BEFORE the shared gate, because the gate's host arm
        /// (DiffEngine.FlushOnHostGesture) would otherwise force one MONOLITHIC diff walk — and abandon
        /// the in-flight sliced cycle (DiffEngine.HostTick:320) — per UI transition.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.SetGamePauseState))]
        internal static class ProgrammaticPauseCapturePatch
        {
            private static bool Prefix(bool paused)
            {
                try
                {
                    var geo = GeoLevel();
                    // Native is provably INERT here: its only side branch needs (!paused && timing.Paused)
                    // = a change (GeoscapeView.cs:1259), and the else-write is swallowed by the
                    // change-gated Paused setter (Timing.cs:112). So nothing to capture and nothing to
                    // ship, on either peer. A REAL host pause/resume still flushes same-frame without
                    // this seam: that same setter raises EffectiveScaleChangedEvent (Timing.cs:126) →
                    // DiffEngine.OnEffectiveScaleChanged → FlushNow (DiffEngine.cs:243).
                    if (geo != null && geo.Timing.Paused == paused) return true;

                    // A TAB IS NOT A WINDOW. Vanilla stops the world when the player leaves the map;
                    // in co-op that is one peer's screen change freezing everyone else's aircraft, so
                    // the verdict goes through the dispatch rule instead. Skipped while the host is
                    // replaying a peer's verdict (re-entrancy) and inside an apply (law 8).
                    if (geo != null && paused && !_replayingRemotePause && !SyncApplyScope.Active &&
                        IsPrivateScreenPause(geo))
                        return PrivateScreenPause();

                    if (IntentRail.ShouldRunNative()) return true;
                    if (geo != null)
                        Send(OpPause, paused ? (byte)1 : (byte)0, paused ? "pause (screen)" : "resume (screen)");
                    return PausesLocally(paused);
                }
                catch (Exception ex) { Debug.LogError("[MP][time] screen-pause capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: apply through the SAME native funnels (dedup/decode/reject = IntentRail) ───────────

        private static void HandleIntentOp(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            byte val = r.ReadByte();

            var geo = GeoLevel();
            if (geo == null)
            { IntentRail.Reject(SurfaceIds.GeoTimeIntent, senderPeerId, "no geoscape op=" + op); return; }

            if (op == OpPause)
            {
                // UNCONDITIONAL, in BOTH directions, from ANY peer — there is no arbiter and no veto: a
                // player who has dismissed his own windows must be able to fly the instant he says so, even
                // while somebody else is still reading (and even while the host is AFK). Native funnel, so
                // the TimeLimit guard (GeoscapeView.cs:1259) stays and the change-gated Paused setter
                // raises the events that latch TimeAnchor + flush the delta to every peer.
                // …EXCEPT the one conditional value on this wire: a TAB pause is the sending peer leaving
                // the map for its own screen, and it only stops the shared clock if that peer is the one
                // whose aircraft is in the air. Judged here because the dispatch ledger is host-only.
                if (val == PauseValTab && !HonourTabPause(senderPeerId)) return;
                bool paused = val != 0;
                _replayingRemotePause = true;
                try
                {
                    if (geo.View != null) geo.View.SetGamePauseState(paused);
                    else geo.Timing.Paused = paused;   // view mid-init: same write, same events
                }
                finally { _replayingRemotePause = false; }
                Debug.Log("[MP][pause] peer=" + senderPeerId + " → paused=" + paused + " nonce=" + nonce);
                return;
            }

            // OpSpeed (the op set is table-gated upstream)
            var module = geo.View == null || geo.View.GeoscapeModules == null
                ? null : geo.View.GeoscapeModules.TimeControlModule;
            if (module == null)
            { IntentRail.Reject(SurfaceIds.GeoTimeIntent, senderPeerId, "speed: no time module"); return; }
            // Native funnel: clamps the index, writes Timing.Scale through the property
            // setter (events → anchor latch + flush) and keeps the host's label honest.
            module.SelectTimePreset(val);
            Debug.Log("[MP][time] HOST intent APPLIED op=" + op + " val=" + val +
                      " nonce=" + nonce + " peer=" + senderPeerId);
        }

        // ─── CLIENT: anchor enforcement cadence ────────────────────────────

        /// <summary>Client-side backstop for the rail's diff blindness: the rail re-sends nothing
        /// while HOST state is unchanged, so a client clock mutated by a writer the seams above do
        /// not capture (quick-save coroutine, another mod) would free-run forever. ~1 Hz, a handful
        /// of float ops — the check itself lives in <see cref="TimeAnchor.EnforceDrift"/> next to
        /// the prediction it re-asserts.</summary>
        public static void ClientTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            if (Time.realtimeSinceStartup < _nextEnforceAt) return;
            _nextEnforceAt = Time.realtimeSinceStartup + 1f;
            var geo = GeoLevel();
            if (geo != null) TimeAnchor.EnforceDrift(geo);
        }
    }
}
