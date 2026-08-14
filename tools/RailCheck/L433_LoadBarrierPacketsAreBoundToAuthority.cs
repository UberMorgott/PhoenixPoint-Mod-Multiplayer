using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;

namespace RailCheck
{
    internal static class L433_LoadBarrierPacketsAreBoundToAuthority
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var boundary = Guid.NewGuid();
            if (!LoadBarrierAuthority.AcceptLoadComplete(true, true, false, boundary, boundary,
                    true, 2, 2))
                yield return "L433 POSITIVE CONTROL: an active host rejected the matching sender slot and boundary.";
            if (LoadBarrierAuthority.AcceptLoadComplete(true, true, false, boundary, boundary,
                    true, 2, 3))
                yield return "L433 forged-slot: one transport sender can complete another roster slot.";
            if (LoadBarrierAuthority.AcceptLoadComplete(true, true, false, boundary, Guid.NewGuid(),
                    true, 2, 2))
                yield return "L433 stale-boundary: a LoadComplete from another transfer is accepted.";
            if (LoadBarrierAuthority.AcceptLoadComplete(true, false, false, boundary, boundary,
                    true, 2, 2))
                yield return "L433 inactive-boundary: LoadComplete mutates a closed barrier.";

            if (!LoadBarrierAuthority.AcceptRevealAll(false, true, false, boundary, boundary, 41UL, 41UL))
                yield return "L433 POSITIVE CONTROL: an active client rejected RevealAll from its host.";
            if (LoadBarrierAuthority.AcceptRevealAll(false, true, false, boundary, boundary, 41UL, 42UL))
                yield return "L433 forged-reveal: a non-host peer can release another client's curtain.";
            if (LoadBarrierAuthority.AcceptRevealAll(false, false, false, boundary, boundary, 41UL, 41UL))
                yield return "L433 inactive-reveal: host RevealAll releases a client outside a load phase.";
            if (LoadBarrierAuthority.AcceptRevealAll(false, true, false, boundary, Guid.NewGuid(), 41UL, 41UL))
                yield return "L433 stale-reveal: a RevealAll from an older boundary releases the current one.";
            var revealPayload = MessageSerializer.SerializeRevealAll(boundary, 7L);
            var revealRoundTrip = MessageSerializer.DeserializeRevealAll(revealPayload);
            if (revealRoundTrip.boundaryId != boundary || revealRoundTrip.serverTicks != 7L)
                yield return "L433 reveal-boundary-codec-lossy: RevealAll does not preserve its boundary id.";

            var asm = typeof(SaveTransferCoordinator).Assembly;
            var load = typeof(SaveTransferCoordinator).GetMethod("OnLoadComplete", All);
            var reveal = typeof(SaveTransferCoordinator).GetMethod("OnRevealAll", All);
            var live = typeof(SessionManager).GetMethod("GetLiveRosterSlots", All);
            var update = typeof(SaveTransferCoordinator).GetMethod("Update", All);
            var replay = typeof(SaveTransferCoordinator).GetMethod("ReplayRevealTo", All);
            var resume = typeof(SessionManager).GetMethod("ResumePeer", All);
            if (load == null || reveal == null || live == null || update == null || replay == null || resume == null)
            {
                yield return "L433 premise-changed: load barrier handlers or live-roster projection moved.";
                yield break;
            }
            if (!Program.Callees(load, asm).Any(m => m.Name == "AcceptLoadComplete"))
                yield return "L433 decision-unwired: OnLoadComplete bypasses the executable authority decision.";
            if (!Program.Callees(reveal, asm).Any(m => m.Name == "AcceptRevealAll"))
                yield return "L433 reveal-decision-unwired: OnRevealAll bypasses host authority.";
            if (!Program.Callees(update, asm).Any(m => m.MetadataToken == live.MetadataToken))
                yield return "L433 paused-peer-blocks: the post-entry AllDone gate still waits on non-live slots.";
            if (!Program.Callees(resume, asm).Any(m => m.MetadataToken == replay.MetadataToken))
                yield return "L433 resumed-peer-stays-curtained: ResumePeer does not replay the terminal reveal.";
        }
    }
}
