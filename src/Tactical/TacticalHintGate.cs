using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.ContextHelp;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Tactical.ContextHelp;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// A HINT IS ONE PEER'S SCREEN, NEVER EVERY PEER'S CLOCK (law L91, the zero-blocking rule).
    ///
    /// THE REPORT (live 3-instance run, 2026-08-05). An Umbra appeared. The HOST got the native
    /// first-encounter panel, the ALIEN TURN stopped dead — on every peer — and nothing moved anywhere until
    /// the host pressed OK. The clients never saw a hint at all: zero <c>Showing hint</c> and zero
    /// <c>UIStateTacticalContextHelp</c> in either client log, while client I2 logged
    /// <c>still holding in 'Alien_TacticalFactionDef's turn after 60s</c> and was released at t=481,8 —
    /// the exact moment the host's popup closed (host: enter at t=346,7, <c>Showing hint: UmbraSighted</c>,
    /// exit at t=471,7). Two players sat in front of a frozen battlefield waiting on a third player's mouse.
    ///
    /// THE ROOT, and it is a single shared line. The alien turn is <c>TacticalFaction.AIUpdateCrt</c>, which
    /// only the host runs (<see cref="ClientAiGate"/> replaces it on clients with a hold on the host's turn
    /// cursor). That coroutine yields on the LOCAL hint queue twice —
    /// <c>TacticalFaction.cs</c>:567 (once, before the actor loop) and :621 (after EVERY executed AI action)
    /// — through <c>TacticalView.WaitUntilHintsAreConfirmed</c> (<c>TacticalView.cs</c>:359-373), whose loop
    /// spins while <c>_statesStack.CurrentState</c> is the hint popup. Those two lines are the ONLY callers
    /// of that method in the whole game, so ONE prefix on the funnel covers both — no per-call-site guard.
    ///
    /// WHY LOCAL AND NOT REPLICATED. Replicating the pause would need a new surface, a release message AND a
    /// timeout, and it would STILL wedge the battle if the peer holding the popup dropped — the same
    /// cross-peer wait wearing a rail costume. Local costs nothing, because the game already delivers the
    /// panel per peer without this method: the hint stays queued in <c>ContextHelpManager._hintsPendingDisplay</c>
    /// and <c>UIStateCharacterSelected.UpdateState</c>:1103-1106 calls <c>TacticalView.TryShowContextHint</c>
    /// EVERY FRAME, so each peer pops its own Umbra panel in its own idle state and releases only itself.
    /// Hints are already declared per-peer presentation on the rail (<c>ContextHelpData</c> is an excluded
    /// field, <c>RailMeta.cs</c>:753), and <see cref="ClientMissionStartHints"/> already rebuilds them
    /// client-side for exactly that reason.
    ///
    /// THE EMPTY ENUMERATOR IS THE NATIVE ANSWER, not a guess. Both call sites consume the result as
    /// <c>yield return TacticalLevel.Timing.Call(WaitUntilHintsAreConfirmed())</c>, and
    /// <c>Timing.Call</c> (<c>Base.Core/Timing.cs</c>:257-260) hands it straight to
    /// <c>Start(coroutine, NextUpdate.Now, setCaller: true)</c> — a null would go into the scheduler, not be
    /// tested for. An enumerator that finishes immediately is the method's OWN hot path: line 361-364 is
    /// <c>if (!TryShowContextHint()) yield break;</c>, i.e. every turn with no pending hint already runs this
    /// exact shape through this exact <c>Timing.Call</c>. So <see cref="Nothing"/> reproduces a case the
    /// engine takes thousands of times per battle; <c>null</c> reproduces nothing.
    ///
    /// APPLIES ON EVERY PEER INCLUDING THE HOST — that is the point. The host is the peer whose popup froze
    /// everyone else. Single-player is untouched (<c>IsActiveSession</c> false → the native method runs).
    /// </summary>
    [HarmonyPatch(typeof(TacticalView), "WaitUntilHintsAreConfirmed")]
    internal static class HintWaitGate
    {
        private static bool Prefix(ref IEnumerator<NextUpdate> __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;
            MpLog.Log("[Multiplayer][tac] hint wait made LOCAL — the turn coroutine does not stop on this " +
                      "peer's popup. The hint stays queued and TryShowContextHint pops it on each peer's own " +
                      "idle frame.");
            __result = Nothing();
            return false;
        }

        private static IEnumerator<NextUpdate> Nothing() { yield break; }
    }

    /// <summary>
    /// A HINT FIRES ON ONE PEER'S EYES AND MUST REACH EVERY PEER'S SCREEN (surface 0x8A, law L130).
    ///
    /// THE REPORT (live 3-instance run, 2026-08-05, and it corrects the earlier "host-only" reading).
    /// An Umbra was sighted and the TFTV panel appeared on ONE CLIENT — not the host, not the third peer.
    /// In a second mission an elite/gang panel appeared on a DIFFERENT single client. So there was no
    /// mirroring at all in either direction; whichever peer happened to reveal the actor first was the only
    /// one that ever saw the hint.
    ///
    /// THE ROOT IS LOCAL VISION, and it is upstream of everything <see cref="ClientMissionStartHints"/>
    /// fixed. <c>TacContextHelpManager.OnActorSeen</c>:199-205 is subscribed to
    /// <c>TacticalLevelController.ActorSawOtherFactionActorEvent</c> (:118) and returns immediately unless
    /// <c>viewer.IsFromViewerFaction</c> — so the trigger is raised by THIS peer's own reveal, against THIS
    /// peer's own <c>Vision</c>, and a peer whose soldiers never got line of sight to that Umbra never runs
    /// <c>EventTypeTriggered</c>:53 at all. Nothing about it is host-specific, which is exactly why the
    /// panel landed on a client twice. The mission-start replay could not cover it either: that one only
    /// re-fires what was ALREADY visible at deployment, and a boss walking into view mid-battle is not.
    ///
    /// THE MIRROR IS ONE WIRE ON THE TRIGGER, NEVER ON THE DISPLAY. The capture is a POSTFIX on
    /// <c>EventTypeTriggered</c> — the single method every tactical hint registration funnels through
    /// (<c>OnActorSeen</c>, <c>OnActorDied</c>, <c>OnStartTurn</c>, <c>OnStatusApplied</c> and the other
    /// sixteen handlers all end here) — so ONE seam covers every hint the game or TFTV will ever add, with
    /// no per-boss list and no enumerated trigger set. The display pump is untouched, as law L129 arm (b)
    /// requires: we neither patch <c>TryShowContextHint</c> nor <c>RegisterContextHelpHint</c>; we CALL the
    /// latter, which is the same thing the game's own <c>CmdShowHint</c>:395 does.
    ///
    /// WHY A NAME AND NOT THE DEF GUID. TFTV mints hint defs at runtime with <c>Guid.NewGuid()</c>
    /// (TFTVHints.cs:175/211), so a guid names a different def on each peer, while the def's <c>name</c> is
    /// a string literal in TFTV's own source and is identical everywhere. The receiver resolves it out of
    /// the two LIVE hint DBs — the same two <c>EventTypeTriggered</c> itself walks — so whatever registered
    /// a hint on the sender is by construction findable by name on any peer that has the same defs.
    ///
    /// EVERY PEER SENDS AND THE HOST RELAYS. A client's sighting reaches the host on 0x8A and the host
    /// fans it back out to all, because a client cannot address its fellow clients; that relay is the whole
    /// reason this surface is bidirectional rather than the usual intent/outcome pair — there is nothing to
    /// validate and nothing to arbitrate, a hint spends nothing and orders nothing.
    ///
    /// ONE CHOKE POINT, AND IT GUARDS THE DISPLAY ONLY: <see cref="ShouldSend"/>. It is true exactly once
    /// per hint name per battle, so a peer that already showed the hint locally drops the mirrored copy.
    /// The game's own <c>IsHintRegistered</c>:152 (which consults both <c>_hintsPendingDisplay</c> and
    /// <c>_shownHints</c>) is a second, independent net underneath it.
    ///
    /// THE RELAY IS NOT BEHIND THAT CHOKE, AND PUTTING IT THERE WAS THE BUG (2026-08-06 session, fixed
    /// here). The host runs the real sim, so it is normally the FIRST peer to sight a boss and the first to
    /// mark the name; a client that sighted it independently then handed the host a 0x8A that the choke
    /// swallowed, and the third peer — who had seen nothing — never got a copy at all. The host now relays
    /// UNCONDITIONALLY and the echo cannot come back, for a structural reason rather than a flag: the only
    /// thing that SENDS is <see cref="Capture"/>, which is itself gated by the choke, and a client never
    /// re-sends what it receives.
    ///
    /// AND A CLAIM THAT COULD NOT BE HONOURED IS GIVEN BACK (<see cref="Forget"/>). Taking the name off the
    /// wire and then failing to show it — no live manager yet, or the TFTV def not rebuilt on this peer —
    /// used to silence that peer's OWN later sighting of the same boss for the rest of the battle, with the
    /// panel simply absent and one warning line to explain it.
    ///
    /// NOTHING HERE BLOCKS ANY PEER. This registers a hint and returns; the panel is popped by that peer's
    /// own per-frame pump (<c>UIStateCharacterSelected.UpdateState</c>:1105 and its four siblings), and the
    /// shared turn coroutine never stops on it thanks to <see cref="HintWaitGate"/> above. No peer waits on
    /// another's click, ever.
    ///
    /// FAILS SOFT AND LOUD when the def does not exist here — the known gap for hints minted by host-only
    /// turn-0 code (TFTVRevenant.cs:1441/1669, TFTVAncients.cs:2478/2523, TFTVBaseDefenseTactical.cs:3503/
    /// 3533, all reached only from TFTVTactical.cs:505-540 under <c>turnNumber == 0</c>, which a client's
    /// loaded-save battle is always past). It logs and skips. It never throws into game code.
    /// </summary>
    internal static class HintMirror
    {
        /// <summary>THE choke point (see the class doc). Hint names this peer has put on the wire OR taken
        /// off it, whichever came first.</summary>
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Per-BATTLE, driven from <c>TacticalTurnSync.Reset</c>: the next mission must be able to
        /// show the same boss panel again, and a name held across a teardown would silence it.</summary>
        internal static void Reset() { lock (Known) Known.Clear(); }

        /// <summary>TRUE EXACTLY ONCE per hint name per battle — the send gate, the receive gate and the
        /// echo stop, all of them. Deliberately PURE (no engine, no Unity, no logging) so RailCheck L130
        /// executes it directly rather than asserting that some call site exists.</summary>
        internal static bool ShouldSend(string hintName)
        {
            if (string.IsNullOrEmpty(hintName)) return false;
            lock (Known) return Known.Add(hintName);
        }

        /// <summary>GIVE THE NAME BACK when this peer took it off the wire and then could NOT show it — no
        /// live tactical hint manager yet, or no def by that name here (see <see cref="Show"/>). Without
        /// this, a claim is permanent: the one silent line in the whole seam, because the peer that dropped
        /// the panel also silences its OWN later sighting of the same boss, forever, for this battle. Also
        /// PURE, so RailCheck L162 drives the claim/release pair directly.</summary>
        internal static void Forget(string hintName)
        {
            if (string.IsNullOrEmpty(hintName)) return;
            lock (Known) Known.Remove(hintName);
        }

        // ─── capture: the ONE trigger funnel, on every peer alike (law 4c, presentation seam) ───

        /// <summary>A POSTFIX: this peer's own hint runs whatever the wire does, and nothing here alters or
        /// delays it.</summary>
        [HarmonyPatch(typeof(TacContextHelpManager), nameof(TacContextHelpManager.EventTypeTriggered))]
        internal static class TriggerBroadcast
        {
            private static void Postfix(TacContextHelpManager __instance) => Capture(__instance);
        }

        private static readonly FieldInfo PendingField =
            AccessTools.Field(typeof(ContextHelpManager), "_hintsPendingDisplay");

        // Private, and the reason AccessTools appears at all: the game loads a hint's image + audio bank
        // here (TacContextHelpManager:77-94) and CmdShowHint:398 calls it right after registering.
        private static readonly MethodInfo LoadAssets =
            AccessTools.Method(typeof(TacContextHelpManager), "StartLoadingHintAssets",
                               new[] { typeof(ContextHelpHintDef) });

        /// <summary>Everything the trigger just queued that this peer has not already mirrored. Reading the
        /// QUEUE rather than intercepting the registration is what keeps the display path unpatched.</summary>
        private static void Capture(TacContextHelpManager mgr)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || mgr == null) return;
            try
            {
                var pending = PendingField == null ? null
                                                   : PendingField.GetValue(mgr) as List<ContextHelpHint>;
                if (pending == null)
                {
                    MpLog.LogError("[MP][hint] hint mirroring is OFF — ContextHelpManager._hintsPendingDisplay " +
                                   "did not resolve, so a boss panel stays on the one peer that saw it.");
                    return;
                }
                foreach (var queued in pending)
                {
                    var def = queued == null ? null : queued.HintDef;
                    if (def == null || !ShouldSend(def.name)) continue;
                    Send(engine, def.name);
                }
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][hint] trigger capture FAILED — this sighting reaches no other peer: " + ex);
            }
        }

        private static NetworkMessage Envelope(string hintName)
        {
            byte[] inner;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(hintName);
                inner = ms.ToArray();
            }
            return new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.TacHint, SyncKind.StateDelta, inner));
        }

        /// <summary>Host fans out; a client hands it to the host, which relays. BOTH arms exist because the
        /// bug was never host-only — the panel landed on a client twice.</summary>
        private static void Send(NetworkEngine engine, string hintName)
        {
            var msg = Envelope(hintName);
            if (engine.IsHost) engine.BroadcastToAll(msg);
            else engine.SendToHost(msg);
            MpLog.Log("[MP][hint] '" + hintName + "' triggered on this peer — mirrored to every peer");
        }

        // ─── inbound: relay (host) then show, once ───

        /// <summary>Returns true when the surface was consumed.</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacHint) return false;
            if (engine == null) return true;
            try
            {
                string name;
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                    name = WireString.ReadKey(r);

                // THE RELAY IS UNCONDITIONAL, AND IT COMES FIRST. It used to sit behind the display choke,
                // which made the host's own eyes silence the third peer: the host sights the boss first
                // (it runs the real sim), marks the name, and a client's 0x8A for that same hint was then
                // dropped at the choke and NEVER fanned out — so in a 3-peer session the panel reached
                // whoever the host had already reached and nobody else. Relaying a known name cannot echo:
                // the ONLY sender is Capture, which is itself gated by the choke, and a client never
                // re-sends what it receives. The host must also relay even when its own tactical manager is
                // momentarily unreachable, which is the second reason this is above the choke and not below.
                if (engine.IsHost) engine.BroadcastToAll(Envelope(name));

                // THE DISPLAY CHOKE, and only the display: already known here means this peer has shown it
                // or triggered it locally.
                if (!ShouldSend(name)) return true;

                // A claim this peer cannot honour is GIVEN BACK — otherwise the drop is permanent and
                // silences this peer's own later sighting too (see Forget).
                if (!Show(name)) Forget(name);
            }
            catch (Exception ex) { MpLog.LogError("[MP][hint] inbound FAILED: " + ex); }
            return true;
        }

        /// <summary>The game's OWN by-name path (<c>TacContextHelpManager.CmdShowHint</c>:385-398): find the
        /// def in the live hint DBs, register it, load its assets. Nothing is displayed from here — the
        /// per-frame pump does that on its own idle frame, which is why no peer is ever gated on a click.
        ///
        /// RETURNS FALSE when the hint did NOT reach this peer's queue, which is the caller's signal to give
        /// the name back (<see cref="Forget"/>): a peer still loading its battle, or one whose TFTV def has
        /// not been rebuilt yet, must not be silenced for the rest of the mission by a delivery it could not
        /// use. TRUE also covers "already queued or already dismissed here" — that IS shown.</summary>
        private static bool Show(string name)
        {
            var level = GameUtl.CurrentLevel();
            var mgr = level == null ? null : level.GetComponent<TacContextHelpManager>();
            if (mgr == null)
            {
                MpLog.LogWarning("[MP][hint] '" + name + "' DROPPED — this peer has no live tactical hint " +
                                 "manager (not in a battle yet, or already tearing down); the name is released " +
                                 "so a later sighting on this peer can still raise it.");
                return false;
            }
            // isMandatory follows the DB the def lives in, exactly as EventTypeTriggered:59-60 assigns it:
            // AlwaysDisplayedHintsDb is the "show even with hints switched off" set, and that is where TFTV
            // puts every one of its sighting hints (TFTVHints.cs:295).
            bool mandatory = false;
            var def = Find(mgr.HintDb, name);
            if (def == null) { def = Find(mgr.AlwaysDisplayedHintsDb, name); mandatory = def != null; }
            if (def == null)
            {
                MpLog.LogWarning("[MP][hint] '" + name + "' SKIPPED on this peer — no hint def by that name in " +
                                 "either live hint DB. Hints minted by host-only turn-0 code (TFTV's per-mission " +
                                 "gang/Revenant/Ancient defs) do not exist where the battle is a loaded save; " +
                                 "the name is released so a later delivery, after the def is rebuilt, still lands.");
                return false;
            }
            if (!mgr.RegisterContextHelpHint(def, mandatory, null))
            {
                MpLog.Log("[MP][hint] '" + name + "' already queued or already dismissed here — not shown twice");
                return true;
            }
            if (LoadAssets != null) LoadAssets.Invoke(mgr, new object[] { def });
            MpLog.Log("[MP][hint] '" + name + "' queued on this peer from another peer's sighting — the native " +
                      "per-frame pump pops it on this peer's own idle frame, blocking nobody");
            return true;
        }

        private static ContextHelpHintDef Find(ContextHelpHintDbDef db, string name)
            => db == null || db.Hints == null
                   ? null
                   : db.Hints.FirstOrDefault(h => h != null && h.name == name);
    }
}
