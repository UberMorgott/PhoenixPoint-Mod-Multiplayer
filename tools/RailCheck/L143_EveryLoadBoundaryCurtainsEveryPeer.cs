using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L143 — THE CURTAIN-ARM BROADCAST PRECEDES THE HOST'S OWN LOAD ON THE WIRE: NO PEER'S SCREEN IS THE
    /// ONLY ONE SHOWING A LOAD.
    ///
    /// THE RULE, stated by the user: everyone enters the loading screen at the same time and leaves it at the
    /// same time. Only the LEAVE half was built — L94 arm (n), `06bad1e`: the gate is armed at the load
    /// boundary and released collectively by `AllDone(GetRosterSlots())` → `RevealAll`. Nothing ever asserted
    /// the ENTER half, and it was missing at two of the three seams.
    ///
    /// MEASURED (2026-08-06): the host entered at 21:16:58.750 `curtain lift PARKED — holding for all-players
    /// reveal`; the client only at 21:17:10.126 `EnterLevel → FinishLevel`. ELEVEN POINT FOUR SECONDS of one
    /// screen loading alone while the others stayed live and clickable — which is not merely a UX complaint,
    /// it is how a peer ends up INSIDE a sub-screen when its level is torn down (law L70's blocker) and it is
    /// the same class L71 already fixed for the tactical entry.
    ///
    /// WHY THE TWO SEAMS LOOKED FINE. `ArmNewCampaignBootstrap` (`SaveTransferCoordinator.cs`:1050) DID tell
    /// the clients something — a system-CHAT line — and then native campaign creation ran and the host
    /// curtained alone. `MultiplayerUI.OnLobbyPlay` dropped the curtain on the HOST's screen at the press
    /// (`DropCurtainEarly`) and told nobody. In both cases the client's enter was a SIDE EFFECT of the first
    /// save byte arriving (`SaveTransferCoordinator.cs`:1473-1475 → `EnterDownloadLoadingScreen`), i.e. of
    /// work the host had to finish first.
    ///
    /// NO SECOND MECHANISM. `PacketType.EntryTransferBegin` = 0x48 already means "host→all: every peer
    /// curtains NOW", its handler `OnEntryTransferBegin` was already fully generic (only its label was
    /// tactical), and L71 already pins the tactical seam. The fix is to raise the SAME packet at the two
    /// missing seams and generalise the label — the exit half then closes over the same load with nothing new.
    ///
    /// BROADCAST-AND-GO (postulate 2). There is no ack, no quorum and no wait: the host announces and then
    /// starts its own load in the very next statement. A peer that is gone leaves the ROSTER; a peer that is
    /// merely slow is waited on by the EXISTING exit barrier and by nothing added here. Arm (c) is what keeps
    /// that true — an ack wait smuggled into the announce would be a new blocker on another human.
    ///
    /// AND AN ANNOUNCED BOUNDARY OWNS ITS UNDO (arm (d)): both new seams can fail AFTER the announce — the
    /// lobby start can throw or return false, and the host can back out of the native new-game settings — and
    /// no save transfer would ever exist to lift the clients' curtain. Both route to the EXISTING abort packet
    /// 0x47, whose client handler already un-curtains through the RCA-hardened `PerformDeferredLift`.
    ///
    /// Falsify BOTH WAYS: delete the announce from `OnLobbyPlay` → `seam-never-announces`; move it BELOW
    /// `DropCurtainEarly` → `seam-loads-before-it-announces`; make the announce consult the roster or return
    /// an IEnumerator → `announce-waits-on-a-peer`; drop the abort from `ReopenAfterFailedStart` or
    /// `DisarmNewCampaignBootstrap` → `announced-boundary-has-no-undo`; drop the lobby hide from
    /// `EnterTacLoadCurtain` → `client-curtain-leaves-the-lobby-up`.
    /// </summary>
    internal static class L143_EveryLoadBoundaryCurtainsEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        // Names that would turn "announce and go" into "announce and wait on another human".
        private static readonly string[] WaitShaped =
            { "GetRosterSlots", "AllDone", "IsDone", "RunUntilComplete", "WaitFor" };

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var modAsm = coord.Assembly;
            var ui = modAsm.GetType("Multiplayer.UI.MultiplayerUI");

            var announce = coord.GetMethod("BroadcastLoadBoundaryBegin", All);
            var abort = coord.GetMethod("BroadcastLoadBoundaryAbort", All);
            var arm = coord.GetMethod("ArmNewCampaignBootstrap", All);
            var disarm = coord.GetMethod("DisarmNewCampaignBootstrap", All);
            var tacBarrier = coord.GetMethod("OpenTacticalEntryBarrier", All);
            var startSession = coord.GetMethod("HostStartSession", All);
            var play = ui?.GetMethod("OnLobbyPlay", All);
            var drop = ui?.GetMethod("DropCurtainEarly", All);
            var curtain = ui?.GetMethod("EnterTacLoadCurtain", All);
            var reopen = ui?.GetMethods(All).FirstOrDefault(m => m.Name.Contains("ReopenAfterFailedStart"));

            if (announce == null || abort == null || arm == null || disarm == null || tacBarrier == null ||
                startSession == null || play == null || drop == null || curtain == null || reopen == null)
            {
                yield return "L143 premise-changed: one of SaveTransferCoordinator.{BroadcastLoadBoundaryBegin," +
                             "BroadcastLoadBoundaryAbort,ArmNewCampaignBootstrap,DisarmNewCampaignBootstrap," +
                             "OpenTacticalEntryBarrier,HostStartSession} / MultiplayerUI.{OnLobbyPlay," +
                             "DropCurtainEarly,EnterTacLoadCurtain,ReopenAfterFailedStart} no longer resolves. " +
                             "The load-boundary enter seam has moved and this law is asserting something about a " +
                             "shape the mod no longer has — re-read it before assuming every peer still curtains " +
                             "together.";
                yield break;
            }

            // ── (a) every seam that starts a load announces it ───────────────
            // The tactical seam emits 0x48 INLINE rather than through the helper: L71's `never-announced`
            // arm (Program.cs:9222) walks DIRECT callees, so hiding that send behind a helper would take L71
            // down with it. Same packet, same instant — asserted here at its own call shape.
            var playCallees = Program.Callees(play, modAsm).ToList();
            if (!playCallees.Any(c => c.MetadataToken == announce.MetadataToken))
                yield return "L143 seam-never-announces (lobby-play): MultiplayerUI.OnLobbyPlay drops the HOST's " +
                             "curtain and tells no one. Every client keeps a live, clickable lobby until its own " +
                             "first save byte lands — 11.4 s in the 2026-08-06 run — which is the exact asymmetry " +
                             "the enter half exists to remove.";
            if (!Program.Callees(arm, modAsm).Any(c => c.MetadataToken == announce.MetadataToken))
                yield return "L143 seam-never-announces (new-campaign): ArmNewCampaignBootstrap sends a system-CHAT " +
                             "line and nothing else. Native campaign creation then runs and the host curtains " +
                             "ALONE; a chat line is not a loading screen and the clients' enter falls back to the " +
                             "arrival of the first byte.";
            if (!Program.Callees(tacBarrier, typeof(NetworkEngine).Assembly)
                        .Any(c => c.Name == "BroadcastToAll"))
                yield return "L143 seam-never-announces (tac-entry): OpenTacticalEntryBarrier no longer broadcasts. " +
                             "This is L71's own seam and the only one of the three that was ever built; losing it " +
                             "would put the geo→tac boundary back to 13 s of clickable geoscape after the host had " +
                             "already committed to the battle.";

            // ── (b) …and it announces BEFORE it starts its own load ──────────
            int iAnnounce = playCallees.FindIndex(c => c.MetadataToken == announce.MetadataToken);
            int iDrop = playCallees.FindIndex(c => c.MetadataToken == drop.MetadataToken);
            int iStart = playCallees.FindIndex(c => c.MetadataToken == startSession.MetadataToken);
            foreach (var own in new[] { new { i = iDrop, what = "DropCurtainEarly (the host's own screen)" },
                                        new { i = iStart, what = "HostStartSession (the host's own load)" } })
            {
                if (iAnnounce >= 0 && own.i >= 0 && iAnnounce > own.i)
                    yield return "L143 seam-loads-before-it-announces: OnLobbyPlay reaches " + own.what + " BEFORE " +
                                 "the 0x48 announce. The order IS the law — the announce is what makes the other " +
                                 "peers' screens change at the same instant as ours, and anything the host does " +
                                 "first is time in which its screen is the only one showing a load.";
            }

            // ── (c) the announce is not a blocker (postulate 2) ──────────────
            if (announce is MethodInfo ann && ann.ReturnType != typeof(void))
                yield return "L143 announce-waits-on-a-peer: BroadcastLoadBoundaryBegin no longer returns void. A " +
                             "coroutine or a result to be awaited is a wait on another human at the one seam that " +
                             "must be broadcast-and-go; the ONLY wait in this system is the EXISTING exit barrier.";
            foreach (var c in Program.Callees(announce, modAsm).Where(c => WaitShaped.Contains(c.Name))
                                     .Select(c => c.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                yield return "L143 announce-waits-on-a-peer: BroadcastLoadBoundaryBegin consults " + c + ". The " +
                             "enter half tells every peer to curtain and returns; consulting the roster or an ack " +
                             "here re-introduces exactly the blocker postulate 2 forbids, and a peer that is gone " +
                             "would hold the host's own load hostage.";

            // ── (d) an announced boundary owns its undo ─────────────────────
            if (!Program.Callees(reopen, modAsm).Any(c => c.MetadataToken == abort.MetadataToken))
                yield return "L143 announced-boundary-has-no-undo (lobby-play): the failed-start reopen path lifts " +
                             "the HOST's curtain only. The clients were curtained at the press and no save transfer " +
                             "will ever start to lift theirs — they sit on a loading screen for a session that " +
                             "already gave up, which is the announce turned into a hang.";
            if (!Program.Callees(disarm, modAsm).Any(c => c.MetadataToken == abort.MetadataToken))
                yield return "L143 announced-boundary-has-no-undo (new-campaign): DisarmNewCampaignBootstrap does " +
                             "not un-curtain the clients. A host that backs out of the native new-game settings " +
                             "leaves every other peer waiting on a campaign nobody is creating.";

            // ── (e) reactivity: the announced curtain must OWN the screen ────
            if (!Program.Callees(curtain, modAsm).Any(c => c.Name == "HideForNativeScreen"))
                yield return "L143 client-curtain-leaves-the-lobby-up: EnterTacLoadCurtain drops the native curtain " +
                             "but never takes the lobby down. 0x48 now also fires at the LOBBY seams, where the " +
                             "lobby canvas sits on top of that curtain — the peer is told a load has begun and its " +
                             "screen does not change, which is the same as not telling it (postulate 1).";
        }
    }
}
