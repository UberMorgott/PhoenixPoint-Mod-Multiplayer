using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.PhoenixBases.FacilityComponents;

namespace RailCheck
{
    /// <summary>
    /// L560 — A FACILITY'S SOLDIER SLOTS ARE HOST-AUTHORITATIVE, AND THEY ARE ON THE RAIL.
    ///
    /// Both halves of one defect, found 2026-08-17. Before it, <c>UseSoldiersFacilityComponent</c> appeared
    /// NOWHERE in src/: a client dragging a worker into a lab wrote its own model and no peer — the host
    /// included — ever heard about it. And there was nothing to correct it with either, because
    /// <c>GeoPhoenixFacility._components</c> (GeoPhoenixFacility.cs:48) was EXCLUDED from the rail ("blob
    /// husk on GeoFacilityComponent"), so the slot arrays only ever crossed inside the facility CREATE blob
    /// and every later change was invisible. That is economy divergence, not a stale panel: the slots feed
    /// <c>ResourceGeneratorFacilityComponent.UpdateOutput</c>, the heal/XP rates and the whole
    /// <c>PhoenixBaseStats</c> rollup.
    ///
    /// ARMS
    ///   (a) <c>slot-state-off-the-rail</c> — <c>GeoFacilityComponent</c> must stay KEYABLE and
    ///       <c>_components</c> must stay an <c>EntityCollection</c>. Element-ADDRESSED is the whole
    ///       carve-out: it never rebuilds the readonly array and is deliberately not husk-gated
    ///       (RailTypes.cs:551-553). Drop the <c>"ComponentDef"</c> probe and this falls straight back to
    ///       the excluded husk it was.
    ///   (b) <c>funnel-bypassed</c> — the capture is ONE prefix because
    ///       <c>UseSoldiersFacilityComponent.AssignSoldierSlot</c> is the ONE writer of the two slot arrays.
    ///       That is a property of the SHIPPED GAME, not of this mod, so it is swept out of the game
    ///       assembly: every method of the type that stores into a reference array must be that writer or
    ///       the post-read normalizer. A second writer (a patch, a version bump) makes the single prefix
    ///       silently partial, which reads as "assignment sometimes syncs".
    ///   (c) <c>capture-bypasses-the-one-door</c> — the prefix must reach
    ///       <c>IntentRail.ShouldRunNative</c>. A prefix that decides the posture itself is the block-first
    ///       law (P4a) broken in the direction nothing else can see.
    ///   (d) <c>gesture-not-sent</c> — and it must reach <c>IntentRail.Send</c>. A capture that blocks and
    ///       sends nothing is WORSE than no capture: the client's click is silently discarded forever.
    ///   (e) <c>op-unregistered</c> — the registration is EXECUTED, not read: after
    ///       <c>FacilitySync.RegisterIntents()</c> the 0xB1 family's op table must actually answer the
    ///       assign op. An unregistered op dies as "unknown op" while every other base gesture keeps
    ///       working, which reads as a dead button rather than a missing table row.
    ///   (f) <c>host-write-unvalidated</c> — the host handler must reach the NATIVE funnel
    ///       (<c>AssignSoldierSlot</c>) and must reach <c>IntentRail.Reject</c>. The wire carries a bare
    ///       slot index, an enum byte and a character id; a handler with no refusal path hands all three to
    ///       a method that throws on <c>SlotType.None</c> and indexes an array with whatever arrived.
    ///
    /// NO QUORUM: the client's refusal is immediate and local, and the intent travels on its own. Nothing
    /// here waits on another peer or on a human.
    ///
    /// Falsify: delete <c>"ComponentDef"</c> from <c>IdentityResolver.IdProbes</c> → (a); make the prefix
    /// <c>return true</c> → (c); drop the <c>IntentRail.Send</c> call → (d); remove the <c>OpAssignSlot</c>
    /// row from <c>RegisterIntents</c> → (e); drop the <c>Reject</c> or the native call from the host arm
    /// → (f).
    /// </summary>
    internal static class L560_AFacilitySlotIsHostAuthoritativeAndOnTheRail
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The two methods of <c>UseSoldiersFacilityComponent</c> that may store into a slot array:
        /// the ONE writer every gesture funnels through, and the post-read normalizer that blanks both
        /// arrays (UseSoldiersFacilityComponent.cs:410-423).</summary>
        private static readonly string[] AllowedArrayWriters = { "AssignSoldierSlot", "OnDeserialize" };

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(FacilitySync);
            var modAsm = sync.Assembly;
            var comp = typeof(UseSoldiersFacilityComponent);
            var writer = comp.GetMethod("AssignSoldierSlot", All);
            var capture = sync.GetNestedType("AssignSlotCapturePatch", All);
            var prefix = capture?.GetMethod("Prefix", All);
            var handle = sync.GetMethod("HandleIntent", All);
            var registerIntents = sync.GetMethod("RegisterIntents", All);
            var door = typeof(IntentRail).GetMethod("ShouldRunNative", All);
            var send = typeof(IntentRail).GetMethod("Send", All);
            var opField = sync.GetField("OpAssignSlot", All);
            var families = typeof(IntentRail).GetField("_families", All);

            if (writer == null || prefix == null || handle == null || registerIntents == null ||
                door == null || send == null || opField == null || families == null)
            {
                yield return "L560 premise-changed: the facility-slot seam no longer resolves " +
                             "(UseSoldiersFacilityComponent.AssignSoldierSlot, " +
                             "FacilitySync.AssignSlotCapturePatch.Prefix / HandleIntent / RegisterIntents / " +
                             "OpAssignSlot, IntentRail.ShouldRunNative / Send / _families). Re-point the law " +
                             "at whatever carries the gesture now; do NOT delete it — an uncaptured slot " +
                             "assignment moves both peers' economies apart with nothing in any log";
                yield break;
            }

            // ═══ (a) the state itself is addressable ═══
            if (!IdentityResolver.TypeKeyable(typeof(GeoFacilityComponent)))
                yield return "L560 slot-state-off-the-rail: GeoFacilityComponent is no longer KEYABLE. The " +
                             "declared element type of GeoPhoenixFacility._components carries its def as " +
                             "ComponentDef and nothing else, so without that probe the array classifies " +
                             "EntityList, dies on the husk gate, and every slot assignment after the " +
                             "facility's create blob is invisible to every peer.";
            var componentsField = RailType.Get(typeof(GeoPhoenixFacility))?.FieldByName("_components");
            if (componentsField == null || componentsField.Class != FieldClass.EntityCollection)
                yield return "L560 slot-state-off-the-rail: GeoPhoenixFacility._components is " +
                             (componentsField == null ? "not classified at all"
                                                      : "classified " + componentsField.Class +
                                                        (componentsField.Exclude == null ? "" : " (" + componentsField.Exclude + ")")) +
                             " instead of EntityCollection. Only the element-ADDRESSED classification " +
                             "survives here: the array is readonly (no rebuild) and the husk gate is " +
                             "deliberately not applied to it (RailTypes.cs:551-553).";

            // ═══ (b) the ONE writer is still the only writer, in the SHIPPED GAME ═══
            foreach (var m in comp.GetMethods(All).Cast<MethodBase>().Concat(comp.GetConstructors(All)))
            {
                if (AllowedArrayWriters.Contains(m.Name)) continue;
                if (!StoresIntoReferenceArray(m)) continue;
                yield return "L560 funnel-bypassed: UseSoldiersFacilityComponent." + m.Name + " stores into a " +
                             "slot array WITHOUT going through AssignSoldierSlot. The capture is a single " +
                             "prefix precisely because that method was the one writer every route reduced to " +
                             "(:185/:200/:243/:354/:361/:372/:380); a second one makes the capture silently " +
                             "partial, and partial assignment sync reads as a flaky UI, not as a hole.";
            }

            // ═══ (c)+(d) the capture asks the one door, and it actually sends ═══
            var captureCallees = Program.Callees(prefix, modAsm).ToList();
            if (!captureCallees.Any(m => m.MetadataToken == door.MetadataToken))
                yield return "L560 capture-bypasses-the-one-door: FacilitySync.AssignSlotCapturePatch.Prefix " +
                             "never reaches IntentRail.ShouldRunNative, so it decides the client/host posture " +
                             "itself. Either the client writes its own slots again (P4a), or the host — or a " +
                             "delta apply — gets blocked and the assignment happens nowhere at all.";
            if (!captureCallees.Any(m => m.MetadataToken == send.MetadataToken))
                yield return "L560 gesture-not-sent: FacilitySync.AssignSlotCapturePatch.Prefix blocks the " +
                             "native write and never reaches IntentRail.Send. A blocked gesture with no " +
                             "intent behind it is a click that is discarded in silence — strictly worse than " +
                             "the local write it replaced, because the player is not even wrong locally.";

            // ═══ (e) the op is REGISTERED — executed, not read off the source ═══
            byte op = (byte)opField.GetValue(null);
            string registrationFailure = null;
            try
            {
                registerIntents.Invoke(null, null);
                var table = families.GetValue(null) as IDictionary;
                object family = table == null || !table.Contains(SurfaceIds.GeoBaseIntent)
                    ? null : table[SurfaceIds.GeoBaseIntent];
                var ops = family?.GetType().GetField("Ops", All)?.GetValue(family) as IDictionary;
                if (ops == null || !ops.Contains(op))
                    registrationFailure = "op " + op + " is absent from the 0xB1 base family's table";
            }
            catch (Exception ex)
            {
                registrationFailure = "RegisterIntents threw " +
                                      ((ex as TargetInvocationException)?.InnerException ?? ex).GetType().Name;
            }
            if (registrationFailure != null)
                yield return "L560 op-unregistered: " + registrationFailure + ". Every slot gesture a client " +
                             "makes is then answered with \"unknown op\" while build/demolish/repair/power/" +
                             "rename keep working on the same surface — a dead button, not a visible break.";

            // ═══ (f) the host applies through the NATIVE funnel and can refuse ═══
            // Two sweeps, because Program.Callees filters to ONE assembly (Program.cs:10948): the native
            // funnel lives in the game assembly, the reject in this mod's.
            var hostGameCallees = Program.Callees(handle, comp.Assembly).ToList();
            var hostModCallees = Program.Callees(handle, modAsm).ToList();
            if (!hostGameCallees.Any(m => m.MetadataToken == writer.MetadataToken && m.Module == writer.Module))
                yield return "L560 host-write-unvalidated: FacilitySync.HandleIntent no longer reaches " +
                             "UseSoldiersFacilityComponent.AssignSoldierSlot. The host must apply the gesture " +
                             "through the game's OWN funnel — anything else writes the arrays behind the " +
                             "component's own bookkeeping (the OnCharacterRemoved subscription at :216-221) " +
                             "and leaves a dead soldier wired to a slot forever.";
            if (!hostModCallees.Any(m => m.Name == "Reject" && m.DeclaringType == typeof(IntentRail)))
                yield return "L560 host-write-unvalidated: FacilitySync.HandleIntent has no path to " +
                             "IntentRail.Reject. The wire carries a raw slot index, a raw SlotType byte and a " +
                             "raw character id; GetSoldierSlots THROWS on SlotType.None " +
                             "(UseSoldiersFacilityComponent.cs:263) and indexes the array with whatever " +
                             "arrived, so a handler with no refusal path is an unbounded write driven by a peer.";
        }

        /// <summary>Does this method store into a REFERENCE array (<c>stelem.ref</c>, 0xA4)? Opcode-anchored
        /// and therefore a SUPERSET, exactly like <see cref="Il.CalledMethods"/>: an operand byte can alias
        /// the opcode. A superset is the right direction here — the arm's job is to notice a NEW writer, so
        /// a spurious hit is argued down in review while a missed one is the silent hole.</summary>
        private static bool StoresIntoReferenceArray(MethodBase m)
        {
            var il = Il.Body(m);
            if (il == null) return false;
            for (int i = 0; i < il.Length; i++)
                if (il[i] == 0xA4) return true;
            return false;
        }
    }
}
