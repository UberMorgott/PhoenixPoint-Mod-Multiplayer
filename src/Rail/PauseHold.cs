using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE SHARED CLOCK IS HELD BY ANY PEER THAT HAS A BLOCKING WINDOW UP — one mechanism for every
    /// window kind, never a per-popup special case (op 3 on the existing 0xB0 time family; no new surface).
    ///
    /// THE BUG IT ENDS (live 3-instance run 2026-08-04, multiplayer.log 21:20:36). An aircraft was flying,
    /// an event and then a cinematic fired, the HOST paused — and both clients kept running, so the plane
    /// flew on while every player was reading. Two independent halves, both silent:
    ///   • The MIRRORED window pushed itself with <c>PauseGame = false</c> (EventPopup / GeoModalMirror),
    ///     on the theory that "pause is host-authoritative and arrives via the rail". It does not arrive
    ///     when the host is ALREADY paused: <c>GeoscapeView.SetGamePauseState</c>:1265 writes
    ///     <c>timing.Paused</c>, whose setter is CHANGE-GATED (Timing.cs:112) — no event, no
    ///     EffectiveScaleChangedEvent, no diff, no delta. A client whose own local pause was blocked by the
    ///     block-first law then free-runs with nothing to correct it. Both raisers now push the game's own
    ///     <c>PauseGame = true</c> and a pause is allowed to run LOCALLY on a client (TimeSync), which is
    ///     never a divergence that matters: a clock can only be too slow, and TimeAnchor.EnforceDrift
    ///     re-asserts the host's rate within ~1 s if the host disagrees.
    ///   • Nothing expressed "somebody else is still reading". A resume was one boolean, last writer wins,
    ///     so the first peer to press play restarted the clock for everyone.
    ///
    /// THE HOLD SET IS THE MECHANISM. The host keeps the set of peers currently showing a blocking window
    /// (0 = the host itself). A hold PAUSES. A release does NOT resume — the game itself never resumes when
    /// a window closes (vanilla leaves the geoscape paused and the player presses play), and auto-resuming
    /// would start the clock the instant ONE peer finished reading. What a non-empty set does is VETO every
    /// resume, from any peer including the host: time resumes only once the window is dismissed everywhere.
    ///
    /// THE SEAM IS THE GAME'S OWN, AND IT NAMES NO WINDOW KIND.
    /// <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-73 is the single place a queued window
    /// becomes the current one, and it already asks the question we need — <c>request.PauseGame</c>, which
    /// EVERY queueing raiser in the game sets true (GeoscapeView.cs:596/:666/:678/:861/:883/:1323 and
    /// OnGeoscapeEventRaised:2062). It runs every <c>GeoscapeView.Update</c> frame (:1358), so ONE postfix
    /// reading <c>_currentStateSwitchRequest</c> sees both edges: the window becoming current, and
    /// <c>FinishCurrentStateSwitch</c>:118 having cleared it. Edge-triggered, so the wire cost is one
    /// message per window, not per frame.
    ///
    /// The host's set is PRUNED at read time against the live roster instead of hooking a disconnect
    /// callback: a peer that crashed while holding a window must not freeze the campaign forever, and
    /// prune-on-read needs no lifecycle wiring to be correct.
    ///
    /// <see cref="Decide"/> is PURE and RailCheck L26 executes it — an arbiter that only ever runs in a
    /// live 3-instance session is exactly how "the clock is stuck paused" ships.
    /// </summary>
    internal static class PauseHold
    {
        /// <summary>Op 3 on <see cref="SurfaceIds.GeoTimeIntent"/>: [val:u8] 1 = this peer has a blocking
        /// window up, 0 = it dismissed the last one. Same [nonce][op] envelope as pause/speed.</summary>
        internal const byte OpHold = 3;

        /// <summary>The host itself, in its own set. Peer ids are Steam/transport ids, never 0.</summary>
        internal const ulong HostPeer = 0;

        private static readonly HashSet<ulong> _holds = new HashSet<ulong>();

        /// <summary>What THIS peer last announced — the edge that keeps the per-frame postfix off the wire.</summary>
        private static bool _announced;

        /// <summary>Session teardown and reload boundary (driven from <see cref="TimeSync.Reset"/>): the
        /// windows are gone with the level, so a surviving hold would veto every resume in the next one.
        ///
        /// A CLIENT LEAVING THE GEOSCAPE RELEASES ON THE WIRE FIRST. Its edge seam lives on
        /// <c>GeoscapeView.Update</c>, which stops running the moment this peer drops into a battle or a
        /// load — so a peer that walked out from under an open window would otherwise leave its hold in the
        /// host's set with nothing left to clear it, and the campaign would refuse every resume for the rest
        /// of the session. (A DISCONNECTED peer is handled by <see cref="Prune"/>; this is the peer that is
        /// still here and simply has no geoscape.)</summary>
        internal static void Reset()
        {
            var engine = NetworkEngine.Instance;
            if (_announced && engine != null && engine.IsActiveSession && !engine.IsHost)
                IntentRail.Send(SurfaceIds.GeoTimeIntent, OpHold, "window release (left the geoscape)",
                    w => w.Write((byte)0));
            _holds.Clear();
            _announced = false;
        }

        // ─── THE ARBITER (pure — host facts only, law 3; RailCheck L26 executes it) ───

        /// <summary>The outcome of one (peer, op, val): the resulting hold set, the clock state the host
        /// must write (null = leave the clock alone), and the human reason it refused (null = applied).</summary>
        internal sealed class Decision
        {
            public HashSet<ulong> Holds;
            public bool? Paused;
            public string Refusal;
        }

        /// <summary>PURE. Never mutates <paramref name="holds"/> — the caller adopts <see cref="Decision.Holds"/>.
        /// A release deliberately leaves <see cref="Decision.Paused"/> null: see the class doc for why the
        /// last release must not resume.</summary>
        internal static Decision Decide(IEnumerable<ulong> holds, ulong peer, byte op, byte val)
        {
            var next = new HashSet<ulong>(holds ?? Enumerable.Empty<ulong>());
            if (op == OpHold)
            {
                if (val != 0) { next.Add(peer); return new Decision { Holds = next, Paused = true }; }
                next.Remove(peer);
                return new Decision { Holds = next };
            }
            // The pause/resume gesture (TimeSync.OpPause). A PAUSE is always granted — any peer may stop
            // the shared clock, co-op parity, and it is the safe direction.
            if (val != 0) return new Decision { Holds = next, Paused = true };
            if (next.Count > 0) return new Decision { Holds = next, Refusal = RefusalText(next.Count) };
            return new Decision { Holds = next, Paused = false };
        }

        private static string RefusalText(int holders)
            => "a blocking window is still open on " + holders + " peer(s) — the shared clock stays paused " +
               "until every peer has dismissed it, or one player's aircraft flies on while another is still reading";

        // ─── HOST: apply one decision ──────────────────────────────────────

        /// <summary>Host-side application of a hold/release/pause/resume from <paramref name="peer"/>
        /// (<see cref="HostPeer"/> for its own windows). The clock is written through the game's own
        /// funnel, so the TimeLimit guard and the events that latch <see cref="TimeAnchor"/> and flush the
        /// delta all still run.</summary>
        internal static void Apply(GeoLevelController geo, ulong peer, byte op, byte val)
        {
            Prune();
            var d = Decide(_holds, peer, op, val);
            _holds.Clear();
            foreach (var h in d.Holds) _holds.Add(h);

            if (d.Refusal != null)
            {
                // Not IntentRail.Reject: the asking client BLOCKED its own resume, so its clock is still
                // paused and there is nothing divergent to reconverge — a full resend at click rate would
                // be the cure being worse. Never silent, though: "the play button does nothing" is exactly
                // the shape a player reports as a freeze.
                Debug.Log("[MP][pause] resume from peer=" + peer + " REFUSED — " + d.Refusal);
                return;
            }
            if (d.Paused == null)
            {
                Debug.Log("[MP][pause] peer=" + peer + " released its window hold — " + _holds.Count +
                          " holder(s) left; the clock is untouched (the game never resumes on its own)");
                return;
            }
            if (geo == null) return;
            if (geo.View != null) geo.View.SetGamePauseState(d.Paused.Value);
            else geo.Timing.Paused = d.Paused.Value;   // view mid-init: same write, same events
            Debug.Log("[MP][pause] peer=" + peer + " op=" + op + " val=" + val + " → paused=" + d.Paused.Value +
                      " holders=" + _holds.Count);
        }

        /// <summary>A peer that dropped cannot dismiss anything. Pruned on READ (see the class doc).</summary>
        private static void Prune()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost || engine.Session == null) return;
            _holds.RemoveWhere(p => p != HostPeer && !engine.Session.TryGetClientName(p, out _));
        }

        /// <summary>THE RESUME VETO, asked by both <see cref="TimeSync"/> capture seams. HOST-ONLY inside:
        /// a client's resume is blocked at its own seam and travels as an intent the host refuses through
        /// <see cref="Decide"/>, so there is exactly ONE arbiter and it lives here.</summary>
        internal static bool VetoResume(GeoLevelController geo, out string why)
        {
            why = null;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return false;
            Prune();
            if (_holds.Count == 0) return false;
            // The game's OWN carve-out (GeoscapeView.cs:1259): past the time limit a "resume" is not a
            // resume at all, it is OnTimeLimitReached. Never veto that branch out of existence.
            if (geo?.TimeLimit != null && geo.Timing != null && geo.Timing.Now.DateTime >= geo.TimeLimit.Value)
                return false;
            why = RefusalText(_holds.Count);
            return true;
        }

        // ─── THE ANNOUNCE SEAM (law 4a, one patch, every window kind) ──────

        /// <summary>Is <paramref name="state"/> the window the game's own queue is currently showing?
        /// Used by <see cref="OpenUiRepaint"/> to keep its Exit+Enter fallback off one-shot presentations
        /// (a cinematic re-entered is a cinematic replayed) — the same field read as the hold edge, so
        /// there is one definition of "a queued window" in the mod.</summary>
        internal static bool IsCurrentQueuedWindow(GeoscapeView view, object state)
        {
            if (view == null || state == null) return false;
            var q = WindowQueueSync.SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (q == null || WindowQueueSync.CurrentRequestField == null) return false; // GetValue(null) throws
            var req = WindowQueueSync.CurrentRequestField.GetValue(q) as GeoscapeViewStateSwitchRequest;
            return req != null && ReferenceEquals(req.State, state);
        }

        /// <summary>Announce the edge: this peer started / stopped showing a blocking window. The host
        /// records its own hold directly, a client sends op 3.</summary>
        private static void AnnounceHold(bool hold)
        {
            _announced = hold;
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            if (engine.IsHost)
            {
                var level = Base.Core.GameUtl.CurrentLevel();
                Apply(level == null ? null : level.GetComponent<GeoLevelController>(),
                      HostPeer, OpHold, hold ? (byte)1 : (byte)0);
                return;
            }
            IntentRail.Send(SurfaceIds.GeoTimeIntent, OpHold,
                hold ? "window hold" : "window release", w => w.Write(hold ? (byte)1 : (byte)0));
        }

        /// <summary>The ONE seam, on the game's own single writer of the current queued window. A POSTFIX:
        /// nothing here blocks or alters the dequeue (law 4a — the queue stays the game's). Runs every
        /// geoscape frame and does one field read; only an EDGE reaches the wire.</summary>
        [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch))]
        internal static class QueueHoldEdge
        {
            private static void Postfix(GeoscapeViewSwitchQuery __instance)
            {
                try
                {
                    var engine = NetworkEngine.Instance;
                    if (engine == null || !engine.IsActiveSession) return;
                    var req = WindowQueueSync.CurrentRequestField?.GetValue(__instance) as GeoscapeViewStateSwitchRequest;
                    bool hold = req != null && req.PauseGame;
                    if (hold != _announced) AnnounceHold(hold);
                }
                catch (Exception ex)
                {
                    // A throw here would leave the shared clock held by a window nobody can see any more.
                    Debug.LogError("[MP][pause] window-hold edge FAILED — the shared clock may stay paused " +
                                   "or keep running against an open window: " + ex);
                }
            }
        }
    }
}
