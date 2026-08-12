using System;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// FIVE SECONDS BEFORE THE GEOSCAPE COMES BACK, SHOWN TO EVERY PEER. When a mission ends and any peer
    /// clicks Continue on the battle summary, a countdown strip appears on ALL peers' screens and ticks
    /// 5 → 0; the return happens when it reaches zero.
    ///
    /// EVERY HOLD HAS EXACTLY ONE OWNER (<see cref="_mine"/>), and that is the whole state machine:
    ///   • a peer that clicked Continue OWNS the hold that swallowed its click. Only its own clock
    ///     (<see cref="Tick"/>) and its own CANCEL press may end it. NO remote message — not the host's
    ///     CLEAR, not another peer's veto — may zero it, because the click it ate is gone otherwise and
    ///     that peer never leaves the summary screen (live 2026-08-12, both directions);
    ///   • a strip a peer merely MIRRORS (the host armed it) is not owned here, so a CLEAR drops it;
    ///   • CLEAR therefore means exactly one thing: "the arm I broadcast is cancelled". Reaching zero
    ///     broadcasts nothing — every peer's own clock expires on its own.
    /// A host that is no longer in the battle arms nothing, cancels nothing and broadcasts nothing
    /// (<see cref="HostArm"/> refuses without a live view), which is what stops the arm-then-cancel loop
    /// that stranded a client still sitting on the summary.
    ///
    /// NOT A QUORUM in either direction: nobody waits for another human. A client that clicks Continue
    /// returns on its own five seconds whatever the host does, and the host's own click still pulls
    /// everyone out through the ordinary <c>OpLeave</c> rail.
    ///
    /// THE SEAM IS THE GAME'S OWN AND THERE IS EXACTLY ONE: <c>TacticalView.GoToGeoscape</c>
    /// (TacticalView.cs:1112) is the private callback every end-of-battle route hands to the summary
    /// screen — <c>GetLevelFinishedViewState</c>:1109 (<c>UIStateBattleSummary</c>), :1105
    /// (<c>UIStateTacticalCutscene</c>) and the <c>battle_summary</c> console command :1200 — and its whole
    /// body is <c>PhoenixGame.FinishLevel</c>. Holding it is therefore holding the return itself, with no
    /// second path to forget and nothing torn down early. Same shape as
    /// <c>DeployCountdown.Gate</c> holds a launch, in the other direction.
    ///
    /// BROADCAST, NOT LOCAL. When any peer clicks Continue, the host arms the countdown for all peers via
    /// <see cref="PacketType.ReturnCountdown"/> (0x4B). When any peer clicks Cancel, the host clears it
    /// for all peers. Same two-write shape as <see cref="Lobby.LobbyCountdown"/>: each peer counts its own
    /// display down from the arm off local realtime. NOT A QUORUM (P13): the countdown expires by itself,
    /// cancel is an opt-out that any single peer can exercise.
    ///
    /// NO NEW WIDGET (native-UI-first). The strip is the mod's EXISTING top-of-screen countdown plate,
    /// <see cref="Multiplayer.UI.CountdownPanel"/> — the same skinned plate the deployment drop and the
    /// lobby start already share.
    ///
    /// REPAINT SEAM: <c>MultiplayerUI.Update</c>:1977 (<c>_countdownPanel?.Sync()</c>), unconditional and
    /// every frame in every scene, which is what makes the number tick on an already-open screen.
    /// </summary>
    internal static class ReturnCountdown
    {
        /// <summary>Five, matching <c>DeployCountdown.CountdownSeconds</c> and
        /// <c>LobbyCountdown.CountdownSeconds</c> — one number for every countdown in the mod.</summary>
        internal const int CountdownSeconds = 5;

        private static readonly System.Reflection.MethodInfo GoToGeoscapeMethod =
            AccessTools.Method(typeof(TacticalView), "GoToGeoscape");     // TacticalView.cs:1112

        /// <summary>Peer-local deadline (realtime), 0 = nothing running.</summary>
        private static float _zeroAt;

        /// <summary>The view whose return is being held. Unity-null once the level is gone, which is one of
        /// the two ways <see cref="Tick"/> gives up.</summary>
        private static TacticalView _view;

        /// <summary>Set across every invoke the MOD makes through <c>TacticalTurnSync.InvokeNativeLeave</c>
        /// — our own release at zero, and the host executing an accepted peer ask — so the prefix lets those
        /// through. <c>ApplyLeave</c> is the third such invoke and rides <c>SyncApplyScope</c> instead,
        /// because L64 requires it to call the native handle directly.</summary>
        internal static bool ModDriving;

        /// <summary>THE OWNERSHIP BIT. True when this hold swallowed THIS peer's own Continue click; false
        /// when the strip is only mirroring an arm the host broadcast. A hold that is <c>_mine</c> is ended by
        /// this peer alone — its own clock or its own CANCEL press — and every remote clear path refuses to
        /// touch it. The host's CLEAR landing on a client's own hold is exactly what threw away a click that
        /// had already been swallowed, leaving that peer on the battle summary for good.</summary>
        private static bool _mine;

        /// <summary>Drop THIS peer's hold. The only writer that ends a hold, so ownership cannot be lost in
        /// one place and kept in another.</summary>
        private static void ClearLocal() { _zeroAt = 0f; _view = null; _mine = false; }

        /// <summary>Session/level teardown — a live count must not survive into the next battle.</summary>
        internal static void Reset() { ClearLocal(); ModDriving = false; }

        /// <summary>THE HOLD JUST SWALLOWED THIS CALL — read by <c>TacLeaveBattleCapture</c>, which must not
        /// announce a leave that has not happened. Exactly mirrors the arms on which
        /// <see cref="ReturnHoldPatch"/> returns false: every one of them has set the deadline first, and
        /// <see cref="Tick"/> clears it BEFORE it re-invokes, so the release is not "holding".</summary>
        internal static bool Holding => _zeroAt > 0f;

        /// <summary>What the strip shows on THIS peer. Holds at 1 rather than reaching 0 by itself: zero is
        /// the frame the return actually fires, the same rule <c>LobbyCountdown.DisplaySecondsLeft</c> uses.</summary>
        internal static int DisplaySecondsLeft()
        {
            if (_zeroAt <= 0f) return 0;
            int left = Mathf.CeilToInt(_zeroAt - Time.realtimeSinceStartup);
            return left < 1 ? 1 : left;
        }

        /// <summary>Host arms the countdown for all peers. Called when ANY peer's Continue click reaches the
        /// host (the host's own click is local, a client's crosses as <see cref="RequestArm"/>).</summary>
        internal static void HostArm(NetworkEngine engine, TacticalView viewIfHost)
        {
            if (engine == null || !engine.IsHost) return;
            if (_zeroAt > 0f) return;                        // already counting
            // A CLIENT'S ASK ARRIVES WITHOUT A VIEW (NetworkEngine:721 passes null) and the host's own return
            // is what pulls every other peer out, so the view is taken from the host's live level instead of
            // left null. Null here is what stranded a whole session on 2026-08-12: Tick found nothing to run,
            // dropped the strip and broadcast the CLEAR that stopped the asking client's own countdown too.
            _view = viewIfHost != null ? viewIfHost : TacticalDamageSync.Tlc()?.View;
            if (_view == null)
            {
                // THE HOST HAS ALREADY LEFT THE BATTLE. Arming here armed a hold with nothing to run, which
                // Tick then cancelled one frame later — and that cancel used to broadcast a CLEAR at a peer
                // still sitting on its own summary screen. A host that is out of the battle owns nothing here
                // and says nothing about a peer that is not: the asking peer counts its own five seconds and
                // its accepted OpLeaveBattle carries the rest (live 2026-08-12, the loop this refusal ends).
                Debug.Log("[MP][return] a peer asked for the return strip, but this host is no longer in the " +
                          "battle — arming nothing and broadcasting nothing. That peer releases its own hold " +
                          "on its own clock; nothing here may cancel it.");
                return;
            }
            _mine = viewIfHost != null;                      // the host's OWN click owns this hold; an ask does not
            _zeroAt = Time.realtimeSinceStartup + CountdownSeconds;
            Debug.Log("[MP][return] host arming the return countdown for ALL peers — " + CountdownSeconds +
                      " s. Any peer can cancel; nobody needs to press anything for it to expire.");
            engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown,
                                                     new byte[] { (byte)CountdownSeconds }));
        }

        /// <summary>A REMOTE veto reaching the host. It may clear a strip the host armed on somebody's behalf,
        /// and it may NOT touch a hold the host armed by its own click — that hold ate the host's own Continue
        /// and only the host can un-eat it.</summary>
        internal static void HostCancel(NetworkEngine engine, string who)
        {
            if (engine == null || !engine.IsHost) return;
            if (_zeroAt <= 0f)
            {
                Debug.Log("[MP][return] cancel from " + who + " arrived with NO countdown running — nothing to stop.");
                return;
            }
            if (_mine)
            {
                Debug.Log("[MP][return] cancel from " + who + " does NOT stop this host's own countdown — the " +
                          "host clicked Continue itself and that click is already swallowed. Every peer ends " +
                          "its own hold; nobody ends anybody else's.");
                return;
            }
            Debug.Log("[MP][return] countdown CANCELLED by " + who +
                      " — the return is NOT cancelled, only the countdown: the summary screen is still there " +
                      "and Continue can be pressed again.");
            ClearLocal();
            engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown, new byte[] { 0 }));
        }

        /// <summary>THIS peer's own CANCEL press — the owner's opt-out, so it always ends this peer's hold,
        /// whatever armed it. The host additionally withdraws the arm it broadcast; a client's press is its
        /// own business and stops nobody else (the host is still returning, and would not be stopped by a
        /// veto over its own click anyway).</summary>
        internal static void RequestCancel()
        {
            bool had = _zeroAt > 0f;
            ClearLocal();
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                Debug.LogWarning("[MP][return] CANCEL pressed with no active co-op session — this peer's own " +
                                 "countdown is stopped and its Continue button still works.");
                return;
            }
            Debug.Log("[MP][return] CANCEL pressed — this peer's own countdown is stopped" +
                      (had ? "" : " (there was none running)") + ". The return is NOT cancelled: the summary " +
                      "screen is still there and Continue can be pressed again.");
            if (engine.IsHost)
                engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown, new byte[] { 0 }));
            else
                engine.SendToHost(new NetworkMessage(PacketType.ReturnCountdownCancel));
        }

        /// <summary>Client applier for 0x4B: adopt the host's arm (or clear) and start counting locally.</summary>
        internal static void HandleCountdown(NetworkEngine engine, NetworkMessage msg)
        {
            if (engine == null || engine.IsHost) return;
            int seconds = msg?.Payload != null && msg.Payload.Length > 0 ? msg.Payload[0] : 0;
            if (seconds <= 0)
            {
                // OWNERSHIP. A CLEAR withdraws the host's ARM and nothing else. This peer's own swallowed
                // Continue is not the host's to discard: doing so is what left a client whose click had
                // already been eaten sitting on the summary with a vanished strip and no return (2026-08-12).
                if (_mine)
                {
                    Debug.Log("[MP][return] the host cleared ITS countdown — this peer keeps its own, because it " +
                              "clicked Continue itself and that click is already swallowed. It returns on its " +
                              "own clock.");
                    return;
                }
                ClearLocal();
                Debug.Log("[MP][return] countdown CLEARED by the host — the arm it broadcast is cancelled.");
                return;
            }
            if (_zeroAt > 0f) return;                        // never restart a hold that is already running
            _zeroAt = Time.realtimeSinceStartup + seconds;
            Debug.Log("[MP][return] countdown ARMED by the host for " + seconds +
                      " s — this peer counts its own display down from here. CANCEL stops it for everyone.");
        }

        /// <summary>Host applier for 0x4C: any peer's cancel.</summary>
        internal static void HandleCancel(NetworkEngine engine, NetworkMessage msg)
        {
            if (engine == null || !engine.IsHost || msg == null) return;
            HostCancel(engine, "peer=" + msg.SenderSteamId);
        }

        /// <summary>THE HOLD. Returns false to swallow the native return while the strip counts.
        ///
        /// ORDER IS DECLARED, NOT HOPED FOR. The mod puts a SECOND prefix on this very method —
        /// <c>TacLeaveBattleCapture</c>, which latches <c>TacticalTurnSync.LeftBattle</c> and announces the
        /// leave to every peer — and a prefix returning false cancels the ones behind it. Unordered,
        /// whether the leave went out at the CLICK or five seconds later at the RELEASE came down to
        /// registration order. <c>Priority.First</c> settles it.
        ///
        /// THE PRIORITY IS ONLY HALF OF IT, and Harmony does NOT do the cancelling. In HarmonyLib 2.2.0.0 a
        /// false prefix skips only the prefixes that can affect the original — ones returning bool or taking
        /// a ref/out the body reads; a <c>void</c> prefix is emitted unguarded and runs at any priority
        /// (probed against the ModSDK's own 0Harmony.dll, 2026-08-11, after a first attempt at this shipped
        /// as a runtime no-op). The capture is <c>void</c> and L64 requires it to stay that way, so it reads
        /// <see cref="Holding"/> instead. The priority is still load-bearing: this hold must ARM before the
        /// capture asks.
        ///
        /// THE HOLD MUST WIN, so the announcement happens at the release: this hold can be ABANDONED
        /// (<see cref="Tick"/> gives up when the session dies, the level goes away, or the method stops
        /// resolving) and a leave announced at the click would then have carried every other peer out of a
        /// battle this one never left — the exact stranding <c>TacLeaveBattleCapture</c> exists to prevent.
        /// A leave is real when it happens, not when it is scheduled. The release re-invokes through this
        /// same chain with <see cref="ModDriving"/> set, so the capture runs then, exactly once.
        ///
        /// IT HOLDS A HUMAN'S CLICK AND NOTHING ELSE. The strip's whole premise is that the countdown is
        /// HOST-AUTHORITATIVE and starts when any peer clicks Continue; the mod's own three invocations of
        /// this funnel are not clicks and must go through untouched:
        ///   • the release (<see cref="ModDriving"/>) — obviously, it IS the expiry;
        ///   • <c>TacticalTurnSync.HandleLeaveBattle</c>, the host executing a peer's accepted ask. Held, it
        ///     returned false to a caller that then logged "ACCEPTED — running the host's own GoToGeoscape"
        ///     over nothing, and any throw from the real body surfaced 5 s later inside <see cref="Tick"/>,
        ///     on a stack where <c>IntentRail.HandleInbound</c>'s catch — the asking peer's only reject —
        ///     is long gone. The asking peer has ALREADY spent its own five seconds; the host adding five
        ///     more just delays every other peer behind it.
        ///   • <c>TacticalTurnSync.ApplyLeave</c> (<see cref="SyncApplyScope"/>), a peer being carried out
        ///     by the host. Held, the capture ran at the RELEASE instead — outside the apply scope that was
        ///     the only thing suppressing it — and the carried peer sent a leave ask straight back to the
        ///     host: the direct echo loop law 8 exists to forbid. A peer that never clicked has nothing to
        ///     count down anyway.</summary>
        [HarmonyPatch(typeof(TacticalView), "GoToGeoscape")]
        internal static class ReturnHoldPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(TacticalView __instance)
            {
                try
                {
                    if (ModDriving || SyncApplyScope.Active) return true;
                    var engine = NetworkEngine.Instance;
                    if (engine == null || !engine.IsActiveSession) return true;
                    if (GoToGeoscapeMethod == null)
                    {
                        Debug.LogError("[MP][return] TacticalView.GoToGeoscape did not resolve — no countdown " +
                                       "strip for this return; going back to the geoscape immediately.");
                        return true;
                    }
                    if (_zeroAt > 0f) return false;                // already counting; eat the re-click

                    // On the HOST, arm the countdown for everyone. On a CLIENT, ask the host to arm it;
                    // the client's own _view is latched below so its own Tick can release when the host's
                    // broadcast comes back and the local clock expires.
                    _view = __instance;
                    if (engine.IsHost)
                    {
                        HostArm(engine, __instance);
                    }
                    else
                    {
                        // ARM THIS PEER'S OWN CLOCK AT THE CLICK, not when the host's broadcast comes back.
                        // The swallow above is only safe while Holding is true: TacLeaveBattleCapture reads it
                        // to decide whether the leave really happened, and a client that swallowed its click
                        // WITHOUT arming latched LeftBattle over a return that never ran — after which the
                        // host's own OpLeave was a no-op on that peer and nothing ever took it out of the
                        // battle (live 2026-08-12, client sat on the summary screen for the rest of the
                        // session while the host loaded the geoscape). Every peer now holds its own click and
                        // releases it itself in Tick; nobody waits for anybody to press anything.
                        _zeroAt = Time.realtimeSinceStartup + CountdownSeconds;
                        _mine = true;                        // OWNED: no CLEAR from anywhere may discard this click
                        Debug.Log("[MP][return] this client clicked Continue — holding its own return for " +
                                  CountdownSeconds + " s and asking the host to arm the same strip for all peers.");
                        // The arm request rides the cancel's reverse: a byte payload with the seconds.
                        // The host handler for 0x4B-as-intent treats a non-zero payload from a client as
                        // "please arm". We reuse the same packet type but coming FROM a client.
                        engine.SendToHost(new NetworkMessage(PacketType.ReturnCountdown,
                                                             new byte[] { (byte)CountdownSeconds }));
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Reset();
                    Debug.LogError("[MP][return] arming the pre-geoscape countdown failed — returning now: " + ex);
                    return true;
                }
            }
        }

        /// <summary>Driven from <c>MultiplayerUI.Update</c>, next to the strip's own repaint — the one loop
        /// that runs in every scene including the tactical one.</summary>
        internal static void Tick()
        {
            if (_zeroAt <= 0f) return;
            try
            {
                var engine = NetworkEngine.Instance;

                // EVERY PEER RELEASES ITS OWN HOLD. The hold swallowed a local click, so only this peer can
                // un-swallow it: waiting for the host's CLEAR left a client that pressed Continue sitting on
                // its summary screen forever, because the CLEAR only stops a strip — it does not run anybody's
                // return (live 2026-08-12). Not a quorum in either direction: each peer counts its own five
                // seconds off its own clock and nobody waits for a human.
                var view = _view != null ? _view : TacticalDamageSync.Tlc()?.View;
                if (view == null || GoToGeoscapeMethod == null || TacticalTurnSync.LeftBattle)
                {
                    // LOCAL ONLY, on purpose. This peer losing its level says nothing about anybody else's,
                    // and the host announcing it here cancelled countdowns on peers that were still standing
                    // on their own summary screens.
                    Debug.Log("[MP][return] the held return has nothing left to run on this peer — dropping the " +
                              "strip. It has already left the battle, or holds no level any more; its Continue " +
                              "button still works. No other peer is told: their holds are theirs.");
                    ClearLocal();
                    return;
                }
                bool sessionGone = engine == null || !engine.IsActiveSession;
                if (!sessionGone && Time.realtimeSinceStartup < _zeroAt) return;

                ClearLocal();
                // NOTHING IS BROADCAST AT ZERO. Every peer's own clock expires within milliseconds of this
                // one and clears its own strip; the CLEAR that used to go out here was indistinguishable from
                // a cancel and killed the hold a peer had armed for its own swallowed click.
                Debug.Log("[MP][return] " + (sessionGone
                              ? "the session ended while the return was held — going back to the geoscape now"
                              : "countdown reached zero") + " — running the game's own TacticalView.GoToGeoscape " +
                          "(PhoenixGame.FinishLevel) exactly as the summary screen would have.");
                TacticalTurnSync.InvokeNativeLeave(view);
            }
            catch (Exception ex)
            {
                Reset();
                Debug.LogError("[MP][return] releasing the pre-geoscape countdown failed — this peer is still " +
                               "on the summary screen and its Continue button still works: " + ex);
            }
        }
    }
}
