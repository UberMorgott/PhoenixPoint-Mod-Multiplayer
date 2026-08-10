using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L407 — THE POST-MISSION RESUPPLY SCREEN IS FIRST, AND IT IS ASKED FOR ON AN EDGE, NOT ON A TIMER.
    ///
    /// TWO OWNER DECISIONS OF 2026-08-10 IN ONE LAW, because they are one window.
    ///
    /// ORDER ("Да, первым после миссии"). <see cref="ReplenishSync.ReplenishRank"/> was 20 — above the event
    /// family (<c>OnGeoscapeEventRaised</c> mints 0 / 10 / 15) and deliberately BELOW
    /// <c>UIStateGeoCutscene</c> (100). A cinematic therefore opened ahead of the resupply screen, which is
    /// not the native post-mission flow. It is <c>int.MaxValue</c> now: <c>QueryStateSwitch</c>:77-82 inserts
    /// before the first STRICTLY lower priority, so the post-mission outcome modal (<c>UIStateInitial</c>:112,
    /// also <c>int.MaxValue</c> and queued first from the same arrival) still shows first and the resupply
    /// screen comes immediately after it, ahead of everything else.
    ///
    /// THE PROMOTION MUST NOT ALSO PROMOTE THE YANK. The 2026-08-07 ruling — a review window waits for the
    /// map instead of pulling a player off a screen he opened — is held by
    /// <c>WindowOrder.HoldsForOpenScreen</c>:294, and <c>UIStateReplenish</c> is now named in
    /// <c>WindowOrder.HeldTransitionStates</c> exactly as <c>UIStateRosterDeployment</c> already was. L163
    /// arm (d) owns that outcome; this law owns the rank that made it necessary.
    ///
    /// THE ASK ("делай как считаешь нужным"). A client's post-battle geoscape is rebuilt from the HOST'S
    /// MID-TACTICAL save, so at <c>UIStateInitial.EnterState</c>:125 the returning squad still carries its
    /// full pre-battle loadout, <c>GeoPhoenixFaction.GetMissingItems()</c> is empty and the game's own
    /// <c>QueueReplenishState</c>:127 never runs. Until today the client raced that with a 180-frame poll —
    /// a guess about the wire dressed up as a timeout. Now the HOST states the fact: a postfix on
    /// <c>GeoMission.Complete</c> (the sole caller of <c>PostmissionReplenish</c>, GeoMission.cs:896) arms
    /// <see cref="ReplenishSync.HostPostMissionTick"/>, which broadcasts 0xB2 from <c>SyncEngine.Tick</c>
    /// IMMEDIATELY AFTER <c>DiffEngine.HostTick</c> — behind the batch carrying the writes. Ordered
    /// transport turns "sent after" into "arrives after", so a client holding that message has already
    /// applied them and asks the game's own question ONCE.
    ///
    /// Arms (each falsified for real, one defect at a time, restored and re-verified between):
    ///   (a) <c>resupply-not-first</c> — the rank clears every priority the game itself mints.
    ///   (b) <c>rank-table-overreaches</c> — and still names only that one window.
    ///   (c) <c>edge-is-unclaimed</c> — 0xB2 is claimed by <see cref="ReplenishSync.HandleInbound"/> and by
    ///       nothing else; an unclaimed surface falls through to the value rail.
    ///   (d) <c>edge-is-unwired</c> — the host seam is a POSTFIX on <c>GeoMission.Complete</c>, and the send
    ///       is reached from <c>SyncEngine.Tick</c> AFTER <c>DiffEngine.HostTick</c>. An edge emitted before
    ///       the walk announces state the client has not applied, which is the bug inverted.
    ///   (e) <c>ceiling-became-the-mechanism-again</c> — the poll is still BOUNDED (never a quorum, never an
    ///       unbounded hold) and the edge path exists to end it early.
    ///
    /// Falsify: set <c>ReplenishRank</c> back to 20 → (a); add a second kind to the rank table → (b); delete
    /// the <c>HandleInbound</c> line from the geoscape chain → (c); move
    /// <c>ReplenishSync.HostPostMissionTick</c> above <c>DiffEngine.HostTick</c> in <c>Tick</c> → (d);
    /// delete <c>OnPostMissionWritesCommitted</c> → (e).
    /// </summary>
    internal static class L407_PostMissionResupplyIsFirstAndAsksOnAnEdge
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var rankFor = typeof(ReplenishSync).GetMethod("RankFor", AllMembers);
            var inbound = typeof(ReplenishSync).GetMethod("HandleInbound", AllMembers);
            var hostTick = typeof(ReplenishSync).GetMethod("HostPostMissionTick", AllMembers);
            var onEdge = typeof(ReplenishSync).GetMethod("OnPostMissionWritesCommitted", AllMembers);
            var patch = typeof(ReplenishSync).GetNestedType("HostPostMissionCommitPatch", AllMembers);
            var tick = typeof(SyncEngine).GetMethod("Tick", AllMembers);
            if (rankFor == null || inbound == null || hostTick == null || onEdge == null ||
                patch == null || tick == null)
            {
                yield return "L407 premise-changed: one of ReplenishSync.{RankFor,HandleInbound," +
                             "HostPostMissionTick,OnPostMissionWritesCommitted,HostPostMissionCommitPatch} or " +
                             "SyncEngine.Tick no longer resolves — the resupply arc has moved and every arm " +
                             "below is asleep";
                yield break;
            }

            // ── (a) FIRST: the rank clears every priority the game itself mints ─────────────────────
            int? rank = ReplenishSync.RankFor(typeof(UIStateReplenish));
            if (rank == null)
            {
                yield return "L407 resupply-not-first: the rank table no longer ranks UIStateReplenish at all, " +
                             "so the resupply screen falls back to the game's own priority 0 and orders by " +
                             "whichever window that peer happened to queue first.";
            }
            else
            {
                // 0/10/15 = OnGeoscapeEventRaised:2044/:2049/:2057; 100 = ToCutsceneState; int.MaxValue =
                // UIStateInitial:112, the post-mission outcome modal, which shares the ceiling and is queued
                // first from the same arrival — so a TIE with it is the native order, not a failure.
                foreach (var native in new[] { 0, 10, 15, WindowOrder.TransitionPriority })
                    if (rank.Value <= native)
                        yield return "L407 resupply-not-first: UIStateReplenish ranks " + rank.Value +
                                     ", which does not clear the native priority " + native + ". The owner's " +
                                     "2026-08-10 ruling is \"первым после миссии\" — a rank that ties or loses " +
                                     "hands the order back to whoever queued first.";
                if (rank.Value != int.MaxValue)
                    yield return "L407 resupply-not-first: UIStateReplenish ranks " + rank.Value +
                                 " rather than int.MaxValue, the ceiling the game itself uses. Anything below " +
                                 "leaves a band a future window could be minted into, silently ahead of it.";
            }

            // ── (b) and it still moves only what it names ───────────────────────────────────────────
            foreach (var other in new[] { typeof(UIStateGeoscapeEvent), typeof(UIStateGeoCutscene),
                                          typeof(UIStateRosterDeployment), typeof(UIStateGeoModal) })
                if (ReplenishSync.RankFor(other) != null)
                    yield return "L407 rank-table-overreaches: the rank table now names " + other.Name +
                                 " too. Its whole safety property is that anything it does NOT name keeps the " +
                                 "game's own priority — ranking a second kind re-orders windows this decision " +
                                 "never covered.";

            // ── (c) the edge surface is claimed, and only by this handler ───────────────────────────
            // Read as METADATA, not as the compile-time literal: two consts compared in source fold to a
            // constant and the compiler drops the arm — a law that cannot fail by construction.
            var idField = typeof(SurfaceIds).GetField("GeoPostMissionCommit", AllMembers);
            byte edgeId = idField == null ? (byte)0 : Convert.ToByte(idField.GetRawConstantValue());
            if (edgeId != 0xB2)
                yield return "L407 edge-is-unclaimed: SurfaceIds.GeoPostMissionCommit is 0x" +
                             edgeId.ToString("X2") + ", not the documented 0xB2. The " +
                             "id is part of the wire contract both peers compile against.";
            if (!ReplenishSync.HandleInbound(null, 0UL, SurfaceIds.GeoPostMissionCommit, new byte[0]))
                yield return "L407 edge-is-unclaimed: ReplenishSync.HandleInbound declines its own surface, so " +
                             "0x" + SurfaceIds.GeoPostMissionCommit.ToString("X2") + " falls through the " +
                             "geoscape chain to the value rail and the client is back on the poll.";
            if (ReplenishSync.HandleInbound(null, 0UL, SurfaceIds.GeoRail, new byte[0]))
                yield return "L407 edge-is-unclaimed: ReplenishSync.HandleInbound consumes the generic value " +
                             "rail 0xAC. A handler that swallows another surface silently deletes it.";
            var chain = Program.Callees(tick, typeof(ReplenishSync).Assembly).ToArray();
            var armed = typeof(SyncEngine).GetMethods(AllMembers)
                .SelectMany(m => SafeCallees(m)).Any(c => c.Name == "HandleInbound" &&
                                                          c.DeclaringType == typeof(ReplenishSync));
            if (!armed)
                yield return "L407 edge-is-unclaimed: nothing in SyncEngine reaches " +
                             "ReplenishSync.HandleInbound, so the geoscape inbound chain never consults it.";

            // ── (d) the send sits BEHIND the walk, and the host seam is a postfix on Complete ───────
            int walk = Array.FindIndex(chain, c => c.Name == "HostTick" && c.DeclaringType == typeof(DiffEngine));
            int send = Array.FindIndex(chain, c => c.Name == "HostPostMissionTick");
            if (walk < 0 || send < 0)
                yield return "L407 edge-is-unwired: SyncEngine.Tick no longer reaches both DiffEngine.HostTick " +
                             "(" + walk + ") and ReplenishSync.HostPostMissionTick (" + send + "). The edge is " +
                             "only meaningful as a position in the outbound stream.";
            else if (send < walk)
                yield return "L407 edge-is-unwired: ReplenishSync.HostPostMissionTick is reached BEFORE " +
                             "DiffEngine.HostTick in SyncEngine.Tick, so 0x" +
                             SurfaceIds.GeoPostMissionCommit.ToString("X2") + " leaves ahead of the batch " +
                             "carrying the post-mission writes and announces state the client has not applied. " +
                             "That is the 2026-08-06 race with an extra message in it.";
            var attr = patch.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().ToArray();
            if (patch.GetMethod("Postfix", AllMembers) == null || patch.GetMethod("Prefix", AllMembers) != null)
                yield return "L407 edge-is-unwired: the host seam is not a POSTFIX on GeoMission.Complete. The " +
                             "announced fact is \"the body has run\" — ApplyMissionResults reaches " +
                             "PostmissionReplenish at GeoMission.cs:896 — and a prefix announces it before it " +
                             "is true.";
            if (!attr.Any(a => a.info != null && a.info.declaringType == typeof(GeoMission) &&
                               string.Equals(a.info.methodName, "Complete", StringComparison.Ordinal)))
                yield return "L407 edge-is-unwired: HostPostMissionCommitPatch no longer targets " +
                             "GeoMission.Complete, which is the sole writer of the state the resupply gate " +
                             "reads.";

            // ── (e) the poll is a bounded safety net, never the mechanism and never a wait ──────────
            var ceiling = typeof(ReplenishSync).GetField("RecheckFrames", AllMembers);
            int frames = ceiling == null ? -1 : Convert.ToInt32(ceiling.GetRawConstantValue());
            if (frames <= 0 || frames > 600)
                yield return "L407 ceiling-became-the-mechanism-again: the re-ask ceiling is " + frames +
                             " frames. It must stay a bounded LOCAL countdown — ResupplyVerdictPending holds " +
                             "this peer's other windows while it runs, and an unbounded hold is the one thing " +
                             "P13 forbids.";
            if (!Program.Callees(onEdge, typeof(ReplenishSync).Assembly).Any(c => c.Name == "TryQueueReplenish"))
                yield return "L407 ceiling-became-the-mechanism-again: OnPostMissionWritesCommitted no longer " +
                             "asks the game's own question, so the edge arrives and nothing happens — the " +
                             "ceiling is silently back to being the whole mechanism.";
            // NOTHING IS APPLIED (law 3): the edge path may only ask the game and hand the job back to it.
            foreach (var callee in Program.Callees(onEdge, typeof(ReplenishSync).Assembly)
                         .Concat(Program.Callees(hostTick, typeof(ReplenishSync).Assembly)))
                if (callee.Name == "PostmissionReplenish" || callee.Name == "SetItems" ||
                    callee.Name == "RepairItem" || callee.Name == "ReplenishAll")
                    yield return "L407 edge-is-unwired: the commit edge reaches " + callee.Name +
                                 ", i.e. it APPLIES post-mission state instead of asking about it. Every value " +
                                 "it is about already crossed on the 0xAC value rail (law 3).";

            // POSITIVE CONTROL: the surface discriminator really does discriminate.
            if (ReplenishSync.HandleInbound(null, 0UL, SurfaceIds.GeoPostMissionCommit, new byte[0]) ==
                ReplenishSync.HandleInbound(null, 0UL, SurfaceIds.GeoRail, new byte[0]))
                yield return "L407 control-not-red: HandleInbound answers the same for its own surface and for " +
                             "the value rail, so arm (c) is comparing a constant.";
        }

        private static IEnumerable<MethodBase> SafeCallees(MethodBase m)
        {
            try { return Program.Callees(m, typeof(ReplenishSync).Assembly); }
            catch { return Enumerable.Empty<MethodBase>(); }
        }
    }
}
