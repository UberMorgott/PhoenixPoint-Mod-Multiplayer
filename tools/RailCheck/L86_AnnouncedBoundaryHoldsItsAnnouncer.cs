using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L86 — THE PEER THAT ANNOUNCES A LOAD BOUNDARY HOLDS ITS OWN SCREEN FOR EXACTLY AS LONG AS THE
    /// SCREEN IT IMPOSED ON EVERYONE ELSE, AND EVERY WAY THAT BOUNDARY CAN DIE UN-ANNOUNCES IT.
    ///
    /// THE REPORT (2026-08-07): "creating a new game makes the HOST load twice". It does, visibly: the
    /// freshly created geoscape appears and goes interactive, and then a second full loading screen drops
    /// on top of it while the host re-enters from its own autosave blob. The clients loading after the
    /// host is correct and expected — the save is being transferred — but the host showing two screens
    /// for one boundary is not.
    ///
    /// THE ROOT IS A DOC THAT ASSERTED A CALL AND NOT AN OUTCOME. <c>SaveTransferMath.CurtainHoldArmed</c>
    /// carried the sentence "the two windows abut with no gap by construction: LaunchTransfer returns only
    /// after Begin() has set the session started", and L94's handover arm evaluated exactly that assumed
    /// row (<c>sessionStarted: true</c> at the conclude edge) — so it stayed GREEN through the whole
    /// report. <c>LaunchTransfer</c> ends with <c>timing.Start(HostSerializeAndSendCrt)</c> and
    /// <c>return true</c>: <c>Begin()</c> is several yields away, past <c>ReadSavegameBinary</c>,
    /// <c>SendBlob</c> and the host's own <c>PrepareEntryFromBlobCrt</c> — a whole level load.
    /// <c>ConcludeNewCampaignBootstrap</c> runs the instant that call returns, so the real row is
    /// (started FALSE, pending FALSE) and the hold collapses for the entire window.
    ///
    /// THE PREDICATE IS THE ANNOUNCEMENT ITSELF, which is why this is one rule and not a patch for the
    /// new-campaign seam. <c>_loadBoundaryAnnounced</c> is set by <c>BroadcastLoadBoundaryBegin</c> — the
    /// packet that puts every OTHER peer behind a curtain — and cleared by the shared reveal
    /// (<c>PerformDeferredLift</c>) or an explicit <c>BroadcastLoadBoundaryAbort</c>. Reading it in the
    /// arm makes the host's hold the mirror of the hold it imposed, at EVERY announced boundary (the
    /// lobby PLAY press as well as the new-campaign arm), and it adds no clock to anybody's wait.
    ///
    /// AND THAT IS WHY ARM (c) IS PART OF THE SAME LAW. Widening the arm without it would be strictly
    /// worse than the bug: a boundary that dies after being announced would strand the ANNOUNCER behind
    /// its own curtain as well as every client. So every path that announces and then fails must abort —
    /// the host backing out of the new-game settings, a bootstrap that concludes without launching, and a
    /// serialization that produces no bytes. A wait with no deadline is only legitimate while something is
    /// still coming; the moment nothing is, it has to be taken down loudly and by name.
    ///
    /// Falsify: drop the announcement input from <c>CurtainHoldArmed</c> → <c>handover-gap</c>; make it
    /// hold with nothing armed at all → <c>hold-never-releases</c>; stop <c>CurtainHoldArmed</c> reading
    /// <c>_loadBoundaryAnnounced</c> → <c>arm-narrowed</c>; remove the abort from any of the three death
    /// paths → <c>announced-boundary-never-cancelled</c>; stop the abort or the reveal clearing the flag
    /// → <c>announcement-never-ends</c>; rename any member → <c>premise-changed</c>.
    /// </summary>
    internal static class L86_AnnouncedBoundaryHoldsItsAnnouncer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var mod = coord.Assembly;

            var announced = coord.GetField("_loadBoundaryAnnounced", All);
            var wide = coord.GetMethod("get_CurtainHoldArmed", All);
            var abort = coord.GetMethod("BroadcastLoadBoundaryAbort", All);
            var begin = coord.GetMethod("BroadcastLoadBoundaryBegin", All);
            var conclude = coord.GetMethod("ConcludeNewCampaignBootstrap", All);
            var disarm = coord.GetMethod("DisarmNewCampaignBootstrap", All);
            var lift = coord.GetMethod("PerformDeferredLift", All);
            var sendBody = IteratorBody(coord, "HostSerializeAndSendCrt");

            if (announced == null || wide == null || abort == null || begin == null || conclude == null ||
                disarm == null || lift == null || sendBody == null)
            {
                yield return "L86 premise-changed: one of SaveTransferCoordinator._loadBoundaryAnnounced, " +
                             ".CurtainHoldArmed, .BroadcastLoadBoundary{Begin,Abort}, " +
                             ".{Conclude,Disarm}NewCampaignBootstrap, .PerformDeferredLift or the " +
                             "HostSerializeAndSendCrt iterator body no longer resolves. The announce/abort " +
                             "pair this law rests on has been reshaped — re-read who curtains whom before " +
                             "trusting either half.";
                yield break;
            }

            // ── (a) THE REAL ROW AT THE HANDOVER, EXECUTED ────────────────
            // Lobby first start, the instant ConcludeNewCampaignBootstrap returns: the transfer coroutine is
            // running, Begin() has not happened, the latch is spent — and every client is already curtained.
            if (!SaveTransferMath.CurtainHoldArmed(false, false, true))
                yield return "L86 handover-gap: with the boundary ANNOUNCED but the session not yet begun " +
                             "and the bootstrap latch already spent, the curtain arm is open. That is the " +
                             "exact frame the host's freshly created campaign becomes visible and interactive " +
                             "— its DiffEngine already shipping fields — while the clients hold a curtain the " +
                             "host itself sent them and not one byte of the save has moved. The second " +
                             "loading screen the player sees is the blob re-entry landing on top of it.";

            // ── (b) NEGATIVE CONTROL: the arm is not simply always-on ─────
            if (SaveTransferMath.CurtainHoldArmed(false, false, false))
                yield return "L86 hold-never-releases: the arm holds with no session, no pending bootstrap " +
                             "and no announced boundary, so nothing can ever open it. A barrier that cannot " +
                             "release is the hang it exists to prevent — and single-player would never see " +
                             "its loading screen come down at all.";

            // ── (c) THE ARM MUST ASK THE WIDE QUESTION ────────────────────
            if (!Program.ReadsField(wide, announced))
                yield return "L86 arm-narrowed: SaveTransferCoordinator.CurtainHoldArmed no longer reads " +
                             "_loadBoundaryAnnounced, so it has collapsed back to a predicate that is FALSE " +
                             "for the whole serialize + send + host-re-entry window. The gate would ask the " +
                             "right method and get the answer that shows two loading screens for one boundary.";

            if (!WritesField(begin, announced))
                yield return "L86 announcement-never-ends: BroadcastLoadBoundaryBegin no longer writes " +
                             "_loadBoundaryAnnounced, so the flag the arm reads is never set by the packet " +
                             "that curtains everyone — the announcer and the announced-to would be held by " +
                             "different facts again.";

            // ── (d) EVERY DEATH OF AN ANNOUNCED BOUNDARY UN-ANNOUNCES IT ──
            foreach (var death in new[] { conclude, disarm, sendBody })
                if (!Program.Callees(death, mod).Any(c => Same(c, abort)))
                    yield return "L86 announced-boundary-never-cancelled: " + death.DeclaringType?.Name + "." +
                                 death.Name + " can end an ANNOUNCED load boundary without reaching " +
                                 "BroadcastLoadBoundaryAbort. Every peer it curtained then waits for a load " +
                                 "that will never be launched, with the only explanation on a system-chat line " +
                                 "they cannot see from behind that curtain — and since the host's own arm now " +
                                 "reads the same announcement, the host strands itself alongside them. An " +
                                 "unbounded wait is only legitimate while something is still coming.";

            // ── (e) AND THE TWO CLEARERS REALLY CLEAR IT ──────────────────
            foreach (var clearer in new[] { abort, lift })
                if (!WritesField(clearer, announced))
                    yield return "L86 announcement-never-ends: " + clearer.Name + " does not clear " +
                                 "_loadBoundaryAnnounced, so the announcement outlives the thing that ended " +
                                 "it. With the arm reading that flag, the curtain then survives the " +
                                 "synchronized reveal — the release that frees everyone would free nobody.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        /// <summary>The compiler-generated MoveNext of an iterator method — the only place its real IL lives.
        /// HostSerializeAndSendCrt is a coroutine, so scanning the method itself would see the state-machine
        /// constructor and nothing else.</summary>
        private static MethodBase IteratorBody(Type owner, string methodName)
        {
            foreach (var t in owner.GetNestedTypes(All))
            {
                if (!t.Name.Contains("<" + methodName + ">")) continue;
                var mv = t.GetMethod("MoveNext", All);
                if (mv != null) return mv;
            }
            return null;
        }

        /// <summary>Flat IL scan for a stfld/stsfld of <paramref name="f"/>. Same shape (and same
        /// tolerance for an unaligned hit failing ResolveField) as the probes L94 uses.</summary>
        private static bool WritesField(MethodBase m, FieldInfo f)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x7D && il[i] != 0x80) continue;   // stfld / stsfld
                int tok = BitConverter.ToInt32(il, i + 1);
                FieldInfo got = null;
                try { got = m.Module.ResolveField(tok); } catch { }
                if (got != null && got.MetadataToken == f.MetadataToken && got.Module == f.Module) return true;
            }
            return false;
        }
    }
}
