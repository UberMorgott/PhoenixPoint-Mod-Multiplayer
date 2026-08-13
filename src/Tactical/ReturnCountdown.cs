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
    /// ONE COUNTDOWN, AND A CLEAR ENDS IT EVERYWHERE. There is exactly one shared strip per battle, so
    /// CANCEL restores the pre-arm state on EVERY peer and the next press by ANY peer starts a fresh five
    /// seconds. Reaching zero broadcasts nothing — every peer's own clock expires on its own — so a CLEAR
    /// on the wire can only ever mean "a human pressed CANCEL", which is precisely the message that must
    /// land unconditionally.
    ///
    /// THE OWNERSHIP BIT IS GONE (2026-08-13). A hold that had swallowed a peer's own Continue used to
    /// refuse every remote CLEAR, which was needed only while a CLEAR could go out for reasons other than a
    /// human cancel — <see cref="Tick"/> broadcasting at zero, its give-up broadcasting, a view-less host
    /// arming and cancelling within a round trip. None of those broadcasts exist any more (see
    /// <see cref="Tick"/> and <see cref="HostArm"/>), and what the bit bought instead was an ORPHAN hold:
    /// two peers clicking Continue at the end of a mission is ordinary, the second click is swallowed by
    /// the strip already running, and after a CANCEL that swallowed click kept ticking on a peer nobody
    /// could see. It expired, its leave went out, and every peer was pulled to the geoscape with no
    /// countdown on screen at all (live 2026-08-13). A cancelled countdown leaves the summary screen up on
    /// every peer with a live Continue button; nothing is lost but the five seconds.
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
    /// display down from the arm off local realtime. NOT A QUORUM (P13): the countdown expires by itself and
    /// CANCEL blocks nobody — it takes the five seconds back on every peer and leaves every summary screen
    /// standing with a live Continue button, so any peer may start a new one on the very next frame. It
    /// keeps nobody in the battle either: another peer's accepted <c>OpLeaveBattle</c> still pulls this one
    /// out (<c>TacticalTurnSync.cs:778</c>), which is correct — no peer may block another.
    ///
    /// NO NEW WIDGET (native-UI-first). The strip is the mod's EXISTING top-of-screen countdown plate,
    /// <see cref="Multiplayer.UI.CountdownPanel"/> — the same skinned plate the deployment drop and the
    /// lobby start already share, cancel button and all.
    ///
    /// REPAINT SEAM: <c>MultiplayerUI.Update</c>:1932 (<c>_countdownPanel?.Sync()</c>) with <see cref="Tick"/>
    /// at :1940, unconditional and every frame in every scene — which is what makes the arm appear, the
    /// number tick and the cancel vanish on an ALREADY-OPEN screen on every peer, with no re-entry.
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

        /// <summary>Drop THIS peer's hold — the ONE writer that ends one, so no clear path can restore half
        /// the pre-arm state and leave the rest standing.</summary>
        private static void ClearLocal() { _zeroAt = 0f; _view = null; }

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
            _zeroAt = Time.realtimeSinceStartup + CountdownSeconds;
            Debug.Log("[MP][return] host arming the return countdown for ALL peers — " + CountdownSeconds +
                      " s. Any peer can cancel; nobody needs to press anything for it to expire.");
            engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown,
                                                     new byte[] { (byte)CountdownSeconds }));
        }

        /// <summary>A REMOTE cancel reaching the host. It ends the ONE countdown, whoever armed it and whoever
        /// swallowed a click into it, and the host says so to every peer — a cancel that left a hold running
        /// anywhere is a hold nobody can see and everybody follows when it expires.</summary>
        internal static void HostCancel(NetworkEngine engine, string who)
        {
            if (engine == null || !engine.IsHost) return;
            if (_zeroAt <= 0f)
            {
                Debug.Log("[MP][return] cancel from " + who + " arrived with NO countdown running — nothing to stop.");
                return;
            }
            Debug.Log("[MP][return] countdown CANCELLED by " + who +
                      " — the return is NOT cancelled, only the countdown: the summary screen is still there " +
                      "and Continue can be pressed again.");
            ClearLocal();
            engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown, new byte[] { 0 }));
        }

        /// <summary>A CANCEL press on this peer. It ends the ONE shared countdown: locally at once, and on
        /// every other peer through the host — the host broadcasts the clear itself, a client asks the host
        /// to. Blocks nobody (P13): every summary screen stays up and the next Continue, from any peer,
        /// arms a fresh five seconds.</summary>
        internal static void RequestCancel()
        {
            bool had = _zeroAt > 0f;
            ClearLocal();
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                Debug.LogWarning("[MP][return] CANCEL pressed with no active co-op session — this peer's " +
                                 "countdown is stopped and its Continue button still works.");
                return;
            }
            Debug.Log("[MP][return] CANCEL pressed — the countdown is stopped on every peer" +
                      (had ? "" : " (there was none running here)") + ". The return is NOT cancelled: every " +
                      "summary screen is still there and Continue can be pressed again by anybody.");
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
                // UNCONDITIONAL, and that is the whole point: a CLEAR can only be a human's CANCEL now
                // (nothing broadcasts at zero), so it puts this peer back exactly where it was before the arm
                // — including when this peer's own Continue was swallowed into the strip. A hold kept here
                // ran on invisibly and pulled every peer to the geoscape with no countdown (2026-08-13).
                ClearLocal();
                Debug.Log("[MP][return] countdown CANCELLED — this peer is back where it was before the arm, " +
                          "summary screen and all. Any peer pressing Continue starts a fresh countdown.");
                return;
            }
            if (_zeroAt > 0f) return;                        // never restart a hold that is already running
            _zeroAt = Time.realtimeSinceStartup + seconds;
            Debug.Log("[MP][return] countdown ARMED by the host for " + seconds +
                      " s — this peer counts its own display down from here. CANCEL stops THIS peer's " +
                      "countdown only; every other peer keeps its own.");
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
                    // ALREADY COUNTING: eat the re-click. It rides the strip that is already running and does
                    // NOT become a hold of its own — a second click that outlived the shared CANCEL kept
                    // ticking where nobody could see it and took every peer to the geoscape when it expired.
                    if (_zeroAt > 0f) return false;

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
