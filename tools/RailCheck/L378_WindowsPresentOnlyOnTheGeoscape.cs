using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    internal static class L378_WindowsPresentOnlyOnTheGeoscape
    {
        internal static IEnumerable<string> Check()
        {
            if (!DurableWindowRegistry.MayPresent(true, typeof(UIStateNothingSelected)) ||
                !DurableWindowRegistry.MayPresent(true, typeof(UIStateVehicleSelected)))
                yield return "L378 started-geoscape-map-was-refused";
            foreach (var blocked in new[] { typeof(UIStateResearch), typeof(UIStateManufacturing),
                                           typeof(UIStateRosterDeployment), typeof(UIStateLoading) })
                if (DurableWindowRegistry.MayPresent(true, blocked))
                    yield return "L378 non-geoscape-screen-admitted-" + blocked.Name;
            if (DurableWindowRegistry.MayPresent(false, typeof(UIStateNothingSelected)))
                yield return "L378 unstarted-geoscape-admitted";

            var step = typeof(MissionArrivalNav).GetMethod("Step",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (step == null || !CallsNamed(step, "MissionHasArrived") || !CallsNamed(step, "MayAutoOpen") ||
                !CallsNamed(step, "Announce"))
                yield return "L378 mission-arrival-does-not-revalidate-before-durable-door";

            var member = new MembershipId("arrival-player");
            var initial = new HostLedger(Array.Empty<InboxEntry>(), 9,
                new[] { member });
            var store = new DurableInboxStore(initial);
            var occurrence = new OccurrenceId("mission-offer", "event|S#4|M#9", new[] { "S#4", "V#2" });
            if (!DurableWindowRegistry.EnqueuePriorityOccurrence(store, occurrence) ||
                DurableWindowRegistry.EnqueuePriorityOccurrence(store, occurrence) ||
                store.Ledger.CommittedRevision != 10 || store.Ledger.EntriesFor(member).Count != 1 ||
                store.Ledger.EntriesFor(member).Single().Occurrence.TriggerId != "event|S#4|M#9")
                yield return "L378 arrival-enqueue-is-not-durable-stable-and-idempotent";
            var def = "11111111-2222-3333-4444-555555555555";
            string subject = DurableWindowRegistry.StableMissionSubject("S#1", def);
            var canonicalSubjects = DurableWindowRegistry.MissionOccurrenceSubjects("S#1", "V#2", subject);
            if (canonicalSubjects.Length != 3 || canonicalSubjects[0] != "S#1" ||
                canonicalSubjects[1] != "V#2" || canonicalSubjects[2] != subject)
                yield return "L378 stable-mission-subject-not-carried-into-occurrence";
            bool missingSubjectRefused = false;
            try { DurableWindowRegistry.MissionOccurrenceSubjects("S#1", "V#2", null); }
            catch (InvalidOperationException) { missingSubjectRefused = true; }
            if (!missingSubjectRefused)
                yield return "L378 arrival-enqueued-without-stable-mission-subject";
            string first = MissionArrivalNav.HostTrigger(null);
            string retry = MissionArrivalNav.HostTrigger(first);
            string secondRaise = MissionArrivalNav.HostTrigger(null);
            if (string.IsNullOrEmpty(subject) || first != retry || first == secondRaise)
                yield return "L378 prelaunch-subject-or-host-raise-trigger-boundary-failed";
            var liveResolver = typeof(DurableWindowRegistry).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(m => m.Name == "StableMissionSubject" && m.GetParameters().Length == 1);
            if (CallsNamed(liveResolver, "get_GlobalTime"))
                yield return "L378 mission-subject-depends-on-global-time";
            var secondOccurrence = new OccurrenceId("mission-offer", secondRaise, new[] { subject });
            if (!DurableWindowRegistry.EnqueuePriorityOccurrence(store, secondOccurrence) ||
                DurableWindowRegistry.EnqueuePriorityOccurrence(store, secondOccurrence) ||
                store.Ledger.EntriesFor(member).Count != 2)
                yield return "L378 distinct-host-raises-collapsed-or-retry-duplicated";

            // POSITIVE CONTROL: numeric priority cannot bypass the same display predicate.
            if (DurableWindowRegistry.MayPresent(true, typeof(UIStateResearch)))
                yield return "L378 control-not-red: priority-was-treated-as-a-display-gate";
        }

        private static bool CallsNamed(MethodInfo method, string name)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != OpCodes.Call.Value && il[i] != OpCodes.Callvirt.Value) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                try { if (method.Module.ResolveMethod(token)?.Name == name) return true; } catch { }
                i += 4;
            }
            return false;
        }
    }
}
