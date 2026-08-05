using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L117 — A RESTORED WINDOW WHOSE SUBJECT HAS RESOLVED IS NOT RESTORED, AND THE REWARD WINDOW IS NOT
    /// TOUCHED BY THAT RULE.
    ///
    /// THE REPORT (2026-08-05). Returning from a tactical mission, the pre-deployment window history came
    /// back perfectly — and so did the START-MISSION window for the mission that had just been played and
    /// left. The reward window for that same mission must stay.
    ///
    /// WHY IT CAME BACK. The brief is a <c>UIStateGeoModal</c> opened via <c>OpenModalPersistent</c>:848;
    /// <c>Persistent</c> is what puts it in the save (<c>GenerateContext</c>:129-136 →
    /// <c>GetRestorableData</c>:25-37), and <c>RestoreData</c>:39-56 rebuilds every entry through
    /// <c>RegenerateState</c>:30-40, which tests only <c>_modalData == null</c>. A completed
    /// <c>GeoMission</c> is not null.
    ///
    /// THE TWO HALVES THIS LAW HOLDS, and the second is the load-bearing one:
    ///   (b) THE DROP IS BY SUBJECT STATE, using the GAME'S OWN verdict. <c>UIStateInitial.EnterState</c>:102
    ///       decides a mission is over with <c>IsCompleted || GetMissionOutcomeState() != Playing</c>; the
    ///       filter must reach both halves of it. A drop keyed on a <c>ModalType</c> instead would cover the
    ///       one reported window and leave the ten siblings <c>GetMissionBriefModal</c>:1724-1798 can return.
    ///   (c) THE REWARD WINDOW IS NEVER IN THE RESTORED SET. It is raised FRESH on the way back in, by
    ///       <c>UIStateInitial.EnterState</c>:112 <c>OpenModalPersistent(GetMissionOutcomeModal(...), …,
    ///       int.MaxValue)</c>, which runs AFTER <c>GeoscapeView.RestoreState</c>. That ordering is the ONLY
    ///       reason "drop the finished mission's windows" does not also delete the reward — the brief and the
    ///       outcome carry the SAME <c>GeoMission</c> as their subject, so nothing about the subject could
    ///       tell them apart. If the game ever moved the outcome raise onto the restore path, this fix would
    ///       start eating reward windows silently, and arm (c) is what turns that into a red build instead.
    ///
    /// ARM (a) HOLDS THE SEAM: the filter must sit on <c>RestoreData</c> — the one method every restored
    /// entry passes, whatever its context class — and it must be a PREFIX, because the native body clears the
    /// queue and rebuilds it in one pass; a postfix would be filtering live <c>IState</c>s it would then have
    /// to unpick, and a filter on <c>RegenerateState</c> would cover modals only.
    ///
    /// Falsify: drop the IsCompleted test or the GetMissionOutcomeState test → <c>subject-verdict-narrowed</c>;
    /// move the patch off GeoscapeViewSwitchQuery.RestoreData → <c>filter-off-the-restore-seam</c>; make it a
    /// postfix → <c>filter-not-a-prefix</c>; key the drop on ModalType instead of the subject →
    /// <c>drop-by-modaltype</c>; let UIStateInitial stop raising the outcome modal itself →
    /// <c>reward-rides-the-restore</c>.
    /// </summary>
    internal static class L117_RestoredSubjectStillLive
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var filter = typeof(RestoreDropsResolvedSubjects);
            var prefix = filter.GetMethod("Prefix", All);
            var resolved = filter.GetMethod("HasResolved", All);
            var subject = filter.GetMethod("SubjectMission", All);
            var patch = filter.GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                              .Cast<HarmonyPatch>().Select(a => a.info).FirstOrDefault();

            var restoreData = typeof(GeoscapeViewSwitchQuery).GetMethod("RestoreData", All);
            var isCompleted = typeof(GeoMission).GetProperty("IsCompleted", All)?.GetGetMethod(true);
            var outcomeState = typeof(GeoMission).GetMethod("GetMissionOutcomeState", All);
            var initial = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateInitial");
            var openPersistent = typeof(GeoscapeView).GetMethod("OpenModalPersistent", All);
            var outcomeModal = typeof(GeoscapeView).GetMethod("GetMissionOutcomeModal", All);

            if (prefix == null || resolved == null || subject == null || restoreData == null ||
                isCompleted == null || outcomeState == null || initial == null ||
                openPersistent == null || outcomeModal == null)
            {
                yield return "L117 premise-changed: RestoreDropsResolvedSubjects.{Prefix,HasResolved," +
                             "SubjectMission}, or GeoscapeViewSwitchQuery.RestoreData, or " +
                             "GeoMission.{IsCompleted,GetMissionOutcomeState}, or UIStateInitial / " +
                             "GeoscapeView.{OpenModalPersistent,GetMissionOutcomeModal}, no longer resolves. " +
                             "The window-restore path has moved and this law is asserting something about a " +
                             "shape it no longer has — re-read it before trusting the filter.";
                yield break;
            }

            // ── (a) the seam: one method, every restored entry, and it must BLOCK ──
            if (patch == null || patch.declaringType != typeof(GeoscapeViewSwitchQuery) ||
                patch.methodName != "RestoreData")
                yield return "L117 filter-off-the-restore-seam: the resolved-subject filter patches " +
                             (patch?.declaringType?.Name ?? "nothing") + "." + (patch?.methodName ?? "?") +
                             " instead of GeoscapeViewSwitchQuery.RestoreData. That is the ONE method every " +
                             "restored entry passes regardless of its context class — a filter on " +
                             "UIStateGeoModal.RestoreContext.RegenerateState would cover modals alone and let " +
                             "every other restorable window carry a dead subject back.";
            if (prefix.Name != "Prefix")
                yield return "L117 filter-not-a-prefix: the filter does not run as a Harmony Prefix. " +
                             "RestoreData:41 CLEARS the queue and rebuilds it in one pass, so a postfix would " +
                             "arrive after the dead window is already a live IState in the queue — it would " +
                             "have to unpick what it should simply never have let through.";

            // ── (b) the verdict is the GAME'S, and both halves of it ───────
            var verdict = Program.Callees(resolved, game).ToList();
            if (!verdict.Any(c => c.MetadataToken == isCompleted.MetadataToken && c.Module == isCompleted.Module))
                yield return "L117 subject-verdict-narrowed: HasResolved does not read GeoMission.IsCompleted, " +
                             "the flag Complete:274 and CompleteSilently:284-286 set. The mission the player " +
                             "just finished would read as still live and its start window would come back — the " +
                             "reported bug, unchanged.";
            if (!verdict.Any(c => c.MetadataToken == outcomeState.MetadataToken && c.Module == outcomeState.Module))
                yield return "L117 subject-verdict-narrowed: HasResolved does not ask " +
                             "GeoMission.GetMissionOutcomeState:556, the other half of the game's own test at " +
                             "UIStateInitial.EnterState:102. A mission carrying a Result but not yet flagged " +
                             "IsCompleted would restore its dead offer.";

            // The drop must be keyed on the SUBJECT, never on which window it is.
            if (Program.Callees(prefix, game).Any(c => c.DeclaringType == typeof(UIStateGeoModal) &&
                                                       c.Name == "get_ModalType"))
                yield return "L117 drop-by-modaltype: the filter reads UIStateGeoModal.ModalType, i.e. it is " +
                             "deciding by WHICH WINDOW rather than by the subject's state. There are eleven " +
                             "brief ModalTypes (GetMissionBriefModal:1724-1798) plus every future one, and a " +
                             "type-keyed drop covers whichever member was named and no other.";
            if (!Program.Callees(prefix, typeof(RestoreDropsResolvedSubjects).Assembly)
                        .Any(c => c.MetadataToken == subject.MetadataToken && c.Module == subject.Module))
                yield return "L117 drop-by-modaltype: the filter never calls SubjectMission, so whatever it is " +
                             "deciding on, it is not the window's subject. 'A restored window whose subject has " +
                             "resolved is not restored' is the whole rule — anything else is a blacklist.";

            // ── (c) THE REWARD WINDOW IS RAISED, NOT RESTORED ──────────────
            // This is what keeps the drop safe: brief and outcome share one GeoMission subject, so only the
            // fact that the outcome is created AFTER the restore separates them.
            var enter = initial.GetMethod("EnterState", All);
            if (enter == null)
            {
                yield return "L117 reward-rides-the-restore: UIStateInitial.EnterState does not resolve, so this " +
                             "law can no longer prove the post-mission reward window is raised fresh rather than " +
                             "restored. Until it can, the resolved-subject drop is unproven against the one " +
                             "window it must never remove.";
                yield break;
            }
            var arrival = Program.Callees(enter, game).ToList();
            if (!arrival.Any(c => c.MetadataToken == openPersistent.MetadataToken && c.Module == openPersistent.Module) ||
                !arrival.Any(c => c.MetadataToken == outcomeModal.MetadataToken && c.Module == outcomeModal.Module))
                yield return "L117 reward-rides-the-restore: UIStateInitial.EnterState no longer raises the " +
                             "mission outcome modal itself (GetMissionOutcomeModal + OpenModalPersistent at " +
                             ":108-112). The reward window was safe from the resolved-subject drop ONLY because " +
                             "it is created after GeoscapeView.RestoreState rather than carried through it — it " +
                             "names the very same completed GeoMission as the brief, so no subject test could " +
                             "ever tell the two apart. If the raise moved onto the restore path, this fix is now " +
                             "deleting the reward window the user explicitly asked to keep.";
        }
    }
}
