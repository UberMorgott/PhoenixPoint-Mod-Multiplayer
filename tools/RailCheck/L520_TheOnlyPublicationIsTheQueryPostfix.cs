using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L520 — THERE IS EXACTLY ONE HOST PUBLICATION OF A WINDOW, AND IT IS THE QueryStateSwitch POSTFIX.
    ///
    /// This asserts "there is ONE mechanism", never "the mechanisms agree" — L496 was written the second
    /// way and legitimised the duplicate it should have killed (R7). Two arms:
    ///
    ///   (a) one-publisher — exactly ONE type in the mod assembly broadcasts a SurfaceIds.GeoEventRaise or
    ///       SurfaceIds.GeoModalRaise envelope, and it is GeoModalMirror, the file the QueryStateSwitch
    ///       postfix reaches. Measured cause: on 2026-08-15 the host queued research then event and both
    ///       clients presented event then research, 363 ms apart, because two independent publication
    ///       paths keyed the same queue.
    ///       WHY THE TYPE AND NOT THE METHOD (deviation from the plan's literal wording, deliberate):
    ///       GeoModalMirror carries one broadcast per raise SURFACE — HostBroadcast for 0xB7 and
    ///       HostBroadcastEventPayload for 0xB6, the event family's door into this same path. Counting
    ///       methods would score that single path as two. The defect this law exists to kill is a SECOND
    ///       FILE publishing behind the postfix's back, and the type-level count names exactly that, while
    ///       still listing the offending methods so a red line is actionable.
    ///   (b) no-reflective-raise — no method in the mod assembly obtains a MethodInfo for a native
    ///       window-raise handler (GeoscapeView.OnFactionResearchCompleted, GeoscapeLog.Faction_ResearchCompleted)
    ///       and Invokes it. A client presents a window by draining its journal cursor, never by replaying
    ///       a private native handler whose signature can change silently under it.
    ///   (c) the-two-bypasses-are-closed (§A.8) — the two paths that could put a window up without passing
    ///       the mint seam both route through it. (c1) SAVE RESTORE: GeoscapeViewSwitchQuery.RestoreData
    ///       rebuilds the pending list directly (GeoscapeViewSwitchQuery.cs:37-55) and never calls
    ///       QueryStateSwitch, so a patch on it must claim the positions. (c2) THE MISSION-OUTCOME MODAL
    ///       raised by UIStateInitial.EnterState:112 after the queue is rebuilt (L117): the plan assumed it
    ///       bypassed the queue entirely; the decompile says otherwise — GeoscapeView.OpenModalPersistent
    ///       :849-865 queues through QueryStateSwitch, so the raise ALREADY reaches the mint seam. Adding a
    ///       second mint for it would double-journal every outcome modal, so the arm PINS the route instead
    ///       (OpenModalPersistent -> QueryStateSwitch -> a mod patch that mints) and goes red the moment
    ///       either half of it stops holding. Arm (c1) likewise goes red if RestoreData ever starts calling
    ///       QueryStateSwitch, because the mod's own restore patch would then be the duplicate.
    ///
    /// ROLES SEPARATED (spec §C.3): both arms are statements about which METHODS exist and what they call,
    /// which is role-independent by construction — a host-only publication path is as visible as a
    /// client-only one. L507's blind spot was executing both roles in one process; there is no execution
    /// here to confuse.
    ///
    /// Falsify (compile-valid src mutations): re-add the BroadcastToAll of a GeoEventRaise envelope at the
    /// end of EventPopup.HostBroadcast -> (a); re-add
    /// AccessTools.Method(typeof(GeoscapeView), "OnFactionResearchCompleted") and an Invoke of it in
    /// ResearchSync -> (b).
    /// </summary>
    internal static class L520_TheOnlyPublicationIsTheQueryPostfix
    {
        internal static IEnumerable<string> Check()
        {
            var mirror = typeof(GeoModalMirror);
            var asm = mirror.Assembly;
            var publish = mirror.GetMethod("HostBroadcast", BindingFlags.Static | BindingFlags.NonPublic |
                                                            BindingFlags.Public);
            if (publish == null)
            {
                yield return "L520 premise-changed: GeoModalMirror.HostBroadcast did not resolve, so this " +
                             "law cannot see the one publication path it exists to protect. Re-point it " +
                             "before believing the verdict.";
                yield break;
            }

            // (a) ONE PUBLISHER. Every method that mentions BOTH a raise surface id and a broadcast call.
            var broadcast = typeof(NetworkEngine).GetMethod("BroadcastToAll",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (broadcast == null)
            {
                yield return "L520 premise-changed: NetworkEngine.BroadcastToAll did not resolve; arm (a) " +
                             "has nothing to count.";
                yield break;
            }
            var publishers = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly))
                .Where(m => MentionsRaiseSurface(m) && Il.References(m, broadcast))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var publisherTypes = publishers.Select(n => n.Substring(0, n.IndexOf('.')))
                                           .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (publisherTypes.Count != 1)
                yield return "L520 one-publisher: " + publisherTypes.Count + " type(s) broadcast a window " +
                             "raise surface (" + string.Join(", ", publishers) + "). Exactly one may — " +
                             "GeoModalMirror, reached from the QueryStateSwitch postfix. " +
                             "Two publication paths is how the host queued research then event on " +
                             "2026-08-15 while both clients presented event then research, 363 ms apart.";
            else if (publisherTypes[0] != "GeoModalMirror")
                yield return "L520 one-publisher: the single publisher is " + publisherTypes[0] +
                             ", not GeoModalMirror. The publication seam moved without this " +
                             "law being re-pointed, so nothing is guarding the seam any more.";

            // (b) NO REFLECTIVE NATIVE WINDOW RAISE.
            // The handler names AND the field names the mod used to hold them in. A static readonly
            // MethodInfo is initialised from the .cctor, and .cctor is not a GetMethods() result, so the
            // ldstr arm alone cannot see the shape ResearchSync actually shipped (:86-89) — the field name
            // is what makes arm (b) bite on it.
            var forbidden = new[] { "OnFactionResearchCompleted", "Faction_ResearchCompleted",
                                    "OnGeoscapeEventRaised", "ViewResearchCompletedMethod",
                                    "LogResearchCompletedMethod" };
            var offenders = asm.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Instance |
                                             BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.DeclaredOnly)
                                  .Where(f => typeof(MethodBase).IsAssignableFrom(f.FieldType))
                                  .Select(f => t.Name + "." + f.Name))
                .Where(n => forbidden.Any(bad => n.IndexOf(bad, StringComparison.Ordinal) >= 0))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            // A MethodInfo field named after the handler is the shape ResearchSync used (:86-89); the
            // string literal is what survives a rename of the field, so both are checked.
            var literalHolders = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly))
                .Where(m => Il.MentionsAnyString(m, forbidden))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (offenders.Count > 0 || literalHolders.Count > 0)
                yield return "L520 no-reflective-raise: the mod still reaches a native window-raise " +
                             "handler by reflection (fields: " + string.Join(", ", offenders) +
                             "; methods naming it: " + string.Join(", ", literalHolders) + "). A client " +
                             "presents a window by draining its journal cursor. Replaying a PRIVATE native " +
                             "handler means reconstructing its arguments and swallowing its failures into " +
                             "a LogWarning, so every native signature change breaks presentation silently.";

            // (c) THE TWO BYPASSES ARE CLOSED (§A.8). A window that reaches the queue without passing the
            // mint seam has no position, and WindowJournal.Append refuses a position-0 entry — so such a
            // window is never journalled and presents in whatever local order each peer happened to build.
            var mint = typeof(WindowJournal).GetMethod("MintHostPosition",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var restoreData = typeof(GeoscapeViewSwitchQuery).GetMethod("RestoreData", All);
            var queryStateSwitch = typeof(GeoscapeViewSwitchQuery).GetMethod("QueryStateSwitch", All);
            var openPersistent = typeof(GeoscapeView).GetMethod("OpenModalPersistent", All);
            if (mint == null || restoreData == null || queryStateSwitch == null || openPersistent == null)
            {
                yield return "L520 premise-changed: WindowJournal.MintHostPosition, " +
                             "GeoscapeViewSwitchQuery.{RestoreData,QueryStateSwitch} or " +
                             "GeoscapeView.OpenModalPersistent did not resolve, so arm (c) cannot see the " +
                             "two bypasses it exists to close.";
                yield break;
            }

            // (c1) BYPASS 1 — SAVE RESTORE. RestoreData rebuilds _viewStateSwitchRequests by adding to the
            // list directly (GeoscapeViewSwitchQuery.cs:37-55); it never calls QueryStateSwitch, so the mint
            // seam cannot see a restored window. A patch on RestoreData must claim the positions instead,
            // or the whole restored queue is orderless on every peer after every battle
            // (GeoscapeView.GetStateSwitchInstanceData:1298-1300 -> RestoreData -> GeoscapeView.cs:349).
            if (Il.References(restoreData, queryStateSwitch))
                yield return "L520 premise-changed: GeoscapeViewSwitchQuery.RestoreData now calls " +
                             "QueryStateSwitch itself, so the mint seam already sees restored windows and " +
                             "arm (c1) is asserting a bypass that no longer exists — and the mod's own " +
                             "RestoreData patch is now MINTING A SECOND POSITION for every restored window.";
            else if (!PatchersOf(asm, restoreData).Any(m => Il.References(m, mint)))
                yield return "L520 restore-bypasses-the-journal: no patch on " +
                             "GeoscapeViewSwitchQuery.RestoreData claims a journal position. A restored " +
                             "request then carries no position at all, so it is refused by the journal and " +
                             "presents in each peer's own rebuild order — a second order by another name, " +
                             "and one that survives a save/load across every battle.";

            // (c2) BYPASS 2 — THE MISSION-OUTCOME MODAL raised by UIStateInitial.EnterState:112 AFTER the
            // queue is rebuilt (L117). It is NOT a bypass today and this arm is what keeps that true:
            // GeoscapeView.OpenModalPersistent:849-865 queues through QueryStateSwitch, so the raise reaches
            // the mint seam like any other pushed window. Asserted as ONE mechanism, never "the two agree"
            // (R7): the property is that the raise routes through the single mint seam, and both halves of
            // that route are checked. Closing this bypass is about the RAISE claiming a position; it does
            // NOT make the mission-outcome family rail-covered, which stays out of scope
            // (src/Rail/GeoWindowCoverage.cs:313, 11 ModalTypes).
            if (!Il.References(openPersistent, queryStateSwitch))
                yield return "L520 initial-state-bypasses-the-journal: GeoscapeView.OpenModalPersistent no " +
                             "longer queues through GeoscapeViewSwitchQuery.QueryStateSwitch, so the " +
                             "mission-outcome modal raised at UIStateInitial.EnterState:112 never reaches " +
                             "the mint seam and can never be journalled. It must claim a position at its " +
                             "raise instead.";
            else if (!PatchersOf(asm, queryStateSwitch).Any(m => Il.References(m, mint)))
                yield return "L520 initial-state-bypasses-the-journal: nothing in the mod patches " +
                             "QueryStateSwitch to mint a journal position, so the one seam every pushed " +
                             "window (including the mission-outcome modal) routes through claims nothing.";

            // POSITIVE CONTROL: the arms above are only meaningful while the publisher and the surface ids
            // they name still exist. If either premise evaporates the law must say so rather than pass.
            if (!MentionsRaiseSurface(publish))
                yield return "L520 positive-control: GeoModalMirror.HostBroadcast no longer mentions a " +
                             "window raise surface id, so arm (a) counted a set that cannot contain the " +
                             "real publisher and would report 0 publishers as a pass.";
        }

        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Every method declared on a mod type that carries a <c>[HarmonyPatch]</c> for
        /// <paramref name="target"/>. Named by ATTRIBUTE rather than by class name so renaming the patch
        /// class cannot silently retire the arm.</summary>
        private static IEnumerable<MethodBase> PatchersOf(Assembly asm, MethodBase target) =>
            asm.GetTypes()
               .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                            .Cast<HarmonyPatch>()
                            .Any(a => a.info != null && a.info.declaringType == target.DeclaringType &&
                                      a.info.methodName == target.Name))
               .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly))
               .Cast<MethodBase>();

        private static bool MentionsRaiseSurface(MethodBase m) =>
            Il.MentionsByte(m, SurfaceIds.GeoEventRaise) || Il.MentionsByte(m, SurfaceIds.GeoModalRaise);
    }
}
