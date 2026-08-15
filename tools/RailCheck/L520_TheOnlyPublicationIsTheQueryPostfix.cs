using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

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

            // POSITIVE CONTROL: the arms above are only meaningful while the publisher and the surface ids
            // they name still exist. If either premise evaporates the law must say so rather than pass.
            if (!MentionsRaiseSurface(publish))
                yield return "L520 positive-control: GeoModalMirror.HostBroadcast no longer mentions a " +
                             "window raise surface id, so arm (a) counted a set that cannot contain the " +
                             "real publisher and would report 0 publishers as a pass.";
        }

        private static bool MentionsRaiseSurface(MethodBase m) =>
            Il.MentionsByte(m, SurfaceIds.GeoEventRaise) || Il.MentionsByte(m, SurfaceIds.GeoModalRaise);
    }
}
