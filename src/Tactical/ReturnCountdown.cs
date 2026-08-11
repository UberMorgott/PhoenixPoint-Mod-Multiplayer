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
    /// 5 → 0; the return happens when it reaches zero. Any peer can CANCEL it, which stops the countdown
    /// for everyone — an opt-out, not a quorum.
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

        /// <summary>Session/level teardown — a live count must not survive into the next battle.</summary>
        internal static void Reset() { _zeroAt = 0f; _view = null; ModDriving = false; }

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
            if (viewIfHost != null) _view = viewIfHost;
            _zeroAt = Time.realtimeSinceStartup + CountdownSeconds;
            Debug.Log("[MP][return] host arming the return countdown for ALL peers — " + CountdownSeconds +
                      " s. Any peer can cancel; nobody needs to press anything for it to expire.");
            engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown,
                                                     new byte[] { (byte)CountdownSeconds }));
        }

        /// <summary>Host clears the countdown for all peers.</summary>
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
            _zeroAt = 0f;
            engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown, new byte[] { 0 }));
        }

        /// <summary>The cancel gesture from either side. On the host it IS the decision; on a client it
        /// crosses as 0x4C and the host applies it.</summary>
        internal static void RequestCancel()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                Debug.LogWarning("[MP][return] CANCEL pressed with no active co-op session — the veto goes nowhere.");
                return;
            }
            if (engine.IsHost) { HostCancel(engine, "this host"); return; }
            Debug.Log("[MP][return] CANCEL leaving this client as 0x" +
                      ((byte)PacketType.ReturnCountdownCancel).ToString("X2") +
                      " — the host owns the countdown, so the veto takes one round trip.");
            engine.SendToHost(new NetworkMessage(PacketType.ReturnCountdownCancel));
        }

        /// <summary>Client applier for 0x4B: adopt the host's arm (or clear) and start counting locally.</summary>
        internal static void HandleCountdown(NetworkEngine engine, NetworkMessage msg)
        {
            if (engine == null || engine.IsHost) return;
            int seconds = msg?.Payload != null && msg.Payload.Length > 0 ? msg.Payload[0] : 0;
            if (seconds <= 0)
            {
                _zeroAt = 0f;
                Debug.Log("[MP][return] countdown CLEARED by the host — either it reached zero and the geoscape " +
                          "return is happening, or somebody cancelled it.");
                return;
            }
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
                        Debug.Log("[MP][return] this client clicked Continue — asking the host to arm the " +
                                  "return countdown for all peers.");
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
                bool isHost = engine != null && engine.IsHost;

                // Only the HOST releases — clients wait for the host's own CLEAR broadcast, which arrives
                // after the host's Tick fires here. A client whose local clock drifts past zero holds at 1
                // (DisplaySecondsLeft) until the host says so.
                if (!isHost) return;

                if (_view == null || GoToGeoscapeMethod == null)
                {
                    Debug.LogError("[MP][return] the held return has nothing left to run — dropping the strip. " +
                                   "This peer stays where it is; its Continue button still works.");
                    HostCancel(engine, "lost view/method");
                    return;
                }
                bool sessionGone = !engine.IsActiveSession;
                if (!sessionGone && Time.realtimeSinceStartup < _zeroAt) return;

                var view = _view;
                _zeroAt = 0f;
                _view = null;
                // Broadcast the CLEAR so every peer's strip disappears.
                engine.BroadcastToAll(new NetworkMessage(PacketType.ReturnCountdown, new byte[] { 0 }));
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
