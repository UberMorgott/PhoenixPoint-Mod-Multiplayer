using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>L396 / DWI-21 — Back is a committed local deferral, never a shared cancellation.</summary>
    internal static class L396_BackDefersPreparationWithoutCancelling
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        internal static IEnumerable<string> Check()
        {
            var member = new MembershipId("player");
            var occurrence = new OccurrenceId("DeploymentPreparing", "back", new[] { "S#4", "M#4" });
            var predecessor = new OccurrenceId("Modal:GeoScavengeBrief", "offer", new[] { "S#4", "M#4" });
            var members = new[] { member };
            DurableInboxStore Make(InboxLifecycle lifecycle = InboxLifecycle.Open, ulong preparation = 3) =>
                new DurableInboxStore(new HostLedger(new[] {
                    new InboxEntry(predecessor, member, InboxLifecycle.Removed, default(CanonicalChoiceId), 1, 1,
                        terminalReason: TerminalReason.Superseded,
                        supersededBy: occurrence),
                    new InboxEntry(occurrence, member, lifecycle, default(CanonicalChoiceId), 2, 0,
                        predecessor: predecessor, preparationRevision: preparation, preparationAuthorityRevision: 1)
                }, 1, members));

            var store = Make(); var carrier = new ProbeCarrier();
            var engine = new DurableInboxEngine(store, member, carrier);
            if (!engine.TryDeferPreparation(occurrence) ||
                store.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Deferred ||
                store.Ledger.CommittedRevision != 2)
                yield return "L396 Back-did-not-commit-Deferred-before-navigation";
            if (engine.TryPresentNext(true, typeof(UIStateNothingSelected)) || carrier.PresentCalls != 0)
                yield return "L396 deferred-preparation-reopened-in-the-same-tick";

            var ack = new InboxMessage(InboxMessageKind.TransportAck, occurrence,
                new CanonicalResultId(occurrence, "result"), Array.Empty<CanonicalRewardItemId>(), member, 99, 0, InboxLifecycle.Open, default(CanonicalChoiceId));
            var churn = DurableInboxReducer.Apply(store.Ledger, InboxCommand.FromMessage(ack));
            if (churn.Changed || churn.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Deferred)
                yield return "L396 active-transport-churn-cleared-local-deferral";
            var genericRequeue = DurableInboxReducer.Apply(store.Ledger,
                InboxCommand.SetLifecycle(occurrence, member, InboxLifecycle.Queued, 100));
            if (genericRequeue.Changed || genericRequeue.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Deferred)
                yield return "L396 generic-lifecycle-message-bypassed-explicit-reentry-and-material-revision-gates";
            if (!store.InstallPreparationEdit(new PreparationEditDelta(occurrence, 4, 3, new[] { "U#1" })) ||
                store.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Queued)
                yield return "L396 newer-material-preparation-revision-did-not-requeue";
            var stale = Make(InboxLifecycle.Deferred);
            if (stale.InstallPreparationEdit(new PreparationEditDelta(occurrence, 3, 3, new[] { "U#1" })) ||
                stale.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Deferred)
                yield return "L396 non-newer-revision-cleared-deferral";

            var explicitStore = Make(InboxLifecycle.Deferred); OccurrenceId rebound;
            NativeRaiserToken.DeploymentCapture.ExplicitReentryToken explicitToken = null;
            if (NativeRaiserToken.DeploymentCapture.TryTakeExplicitReentry("M#4", out rebound) ||
                !NativeRaiserToken.DeploymentCapture.TryBeginExplicitReentry(explicitStore, member, "M#4", out explicitToken))
                yield return "L396 automatic-raiser-gained-an-explicit-user-token";
            if (!NativeRaiserToken.DeploymentCapture.BeginExplicitReentry(explicitToken) ||
                NativeRaiserToken.DeploymentCapture.BeginExplicitReentry(explicitToken))
                yield return "L396 nested-explicit-input-overwrote-the-active-token";
            if (NativeRaiserToken.DeploymentCapture.TryTakeExplicitReentry("M#wrong", out rebound) ||
                !NativeRaiserToken.DeploymentCapture.TryTakeExplicitReentry("M#4", out rebound) ||
                !rebound.Equals(occurrence) ||
                NativeRaiserToken.DeploymentCapture.TryTakeExplicitReentry("M#4", out rebound))
                yield return "L396 explicit-user-token-was-not-exact-and-one-shot";
            NativeRaiserToken.DeploymentCapture.EndExplicitReentry(explicitToken);
            if (!DeploymentWindowClose.TryReenterDeferredForMission(explicitStore, member, "M#4", out rebound) ||
                !rebound.Equals(occurrence) ||
                explicitStore.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Queued)
                yield return "L396 exact-later-native-raiser-did-not-requeue-and-bind-the-deferred-occurrence";

            var otherMember = new MembershipId("other");
            var twoMembers = new[] { member,
                otherMember };
            var perPlayer = new DurableInboxStore(new HostLedger(new[] {
                new InboxEntry(predecessor, member, InboxLifecycle.Removed, default(CanonicalChoiceId), 1, 1,
                    terminalReason: TerminalReason.Superseded,
                    supersededBy: occurrence),
                new InboxEntry(predecessor, otherMember, InboxLifecycle.Removed, default(CanonicalChoiceId), 1, 1,
                    terminalReason: TerminalReason.Superseded,
                    supersededBy: occurrence),
                new InboxEntry(occurrence, member, InboxLifecycle.Deferred, default(CanonicalChoiceId), 3, 0, predecessor: predecessor),
                new InboxEntry(occurrence, otherMember, InboxLifecycle.Deferred, default(CanonicalChoiceId), 3, 0, predecessor: predecessor)
            }, 3, twoMembers));
            if (!DeploymentWindowClose.TryReenterDeferredForMission(perPlayer, member, "M#4", out rebound) ||
                perPlayer.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Queued ||
                perPlayer.Ledger.Get(occurrence, otherMember).Lifecycle != InboxLifecycle.Deferred)
                yield return "L396 local-reentry-mutated-another-members-deferral";

            var unrelated = Make(InboxLifecycle.Deferred);
            if (DeploymentWindowClose.TryReenterDeferredForMission(unrelated, member, "M#unrelated", out rebound))
                yield return "L396 unrelated-mission-stole-a-deferred-preparation";
            var fresh = new OccurrenceId("DeploymentPreparing", "fresh-unrelated", new[] { "M#unrelated" });
            var freshSequencer = new HostInboxSequencer(unrelated.Ledger);
            if (!freshSequencer.CreateOccurrence(fresh) ||
                !freshSequencer.Ledger.AllEntries.Any(x => x.Occurrence.Equals(fresh)))
                yield return "L396 unrelated-native-raiser-could-not-follow-the-normal-fresh-capture-path";

            var secondOccurrence = new OccurrenceId("DeploymentPreparing", "second", new[] { "M#4" });
            var secondPredecessor = new OccurrenceId("Modal:GeoScavengeBrief", "offer-second", new[] { "M#4" });
            var ambiguousEntry = new InboxEntry(secondOccurrence,
                member, InboxLifecycle.Deferred, default(CanonicalChoiceId), 2, 0,
                predecessor: secondPredecessor);
            var secondOffer = new InboxEntry(secondPredecessor, member, InboxLifecycle.Removed,
                default(CanonicalChoiceId), 1, 1,
                terminalReason: TerminalReason.Superseded, supersededBy: secondOccurrence);
            var ambiguous = new DurableInboxStore(new HostLedger(Make(InboxLifecycle.Deferred).Ledger.AllEntries
                .Concat(new[] { secondOffer, ambiguousEntry }), 3, members));
            if (DeploymentWindowClose.TryReenterDeferredForMission(ambiguous, member, "M#4", out rebound))
                yield return "L396 ambiguous-occurrence-was-guessed-instead-of-requiring-one-exact-predecessor";

            // Session state: a rebuilt store must still carry the deferral and its preparation revision.
            var savedDeferred = Make(InboxLifecycle.Deferred);
            var rebuilt = new DurableInboxStore(savedDeferred.Ledger).Ledger.Get(occurrence, member);
            if (rebuilt.Lifecycle != InboxLifecycle.Deferred || rebuilt.PreparationRevision != 3)
                yield return "L396 store-rebuild-lost-the-deferred-lifecycle-or-preparation-revision";

            var rollback = Make(InboxLifecycle.Deferred); var rollbackEngine = new DurableInboxEngine(rollback, member, new ProbeCarrier());
            if (!rollbackEngine.TryRestoreDeferredAfterFailedBack(occurrence, 2) ||
                rollback.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Open ||
                rollbackEngine.TryRestoreDeferredAfterFailedBack(occurrence, 2))
                yield return "L396 native-Back-exception-did-not-compare-and-restore-the-exact-deferred-revision";

            var checkpoint = new InboxWindowCheckpoint("deployment", "U#7", "roster");
            var checkpointBase = Make().Ledger;
            var checkpointOpen = new InboxEntry(occurrence, member, InboxLifecycle.Open,
                default(CanonicalChoiceId), 2, 0, checkpoint: checkpoint, predecessor: predecessor,
                preparationRevision: 3, preparationAuthorityRevision: 1);
            var checkpointStore = new DurableInboxStore(new HostLedger(checkpointBase.AllEntries.Select(x =>
                x.Occurrence.Equals(occurrence) ? checkpointOpen : x), checkpointBase.CommittedRevision,
                checkpointBase.Members));
            var checkpointEngine = new DurableInboxEngine(checkpointStore, member, new ProbeCarrier());
            if (!checkpointEngine.TryDeferPreparation(occurrence) ||
                !checkpointEngine.TryRestoreDeferredAfterFailedBack(occurrence, 3) ||
                !checkpoint.Equals(checkpointStore.Ledger.Get(occurrence, member).Checkpoint))
                yield return "L396 failed-Back-rollback-lost-the-open-preparation-checkpoint";

            var race = Make(InboxLifecycle.Deferred); int winners = 0; var errors = new ConcurrentQueue<Exception>();
            Parallel.For(0, 16, _ => { try { if (new DurableInboxEngine(race, member, new ProbeCarrier())
                    .TryReenterDeferredPreparation(occurrence)) Interlocked.Increment(ref winners); }
                catch (Exception ex) { errors.Enqueue(ex); } });
            if (!errors.IsEmpty || winners != 1 || race.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Queued)
                yield return "L396 concurrent-reentry-committed-more-than-one-transition";
            var deferRace = Make(); errors = new ConcurrentQueue<Exception>();
            Parallel.For(0, 16, _ => { try { new DurableInboxEngine(deferRace, member, new ProbeCarrier())
                    .TryDeferPreparation(occurrence); } catch (Exception ex) { errors.Enqueue(ex); } });
            if (!errors.IsEmpty || deferRace.Ledger.CommittedRevision != 2 ||
                deferRace.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Deferred ||
                deferRace.Ledger.Get(occurrence, member).LifecycleRevision != 3)
                yield return "L396 concurrent-or-reentrant-Back-committed-the-deferral-more-than-once";

            var prefix = typeof(DeploymentPreparationBack).GetMethod("Prefix", All);
            var finalizer = typeof(DeploymentPreparationBack).GetMethod("Finalizer", All);
            var defer = typeof(DeploymentWindowClose).GetMethod("TryDeferCurrentPreparation", All);
            var restore = typeof(DeploymentWindowClose).GetMethod("RestoreFailedBack", All);
            var capture = typeof(NativeRaiserToken.DeploymentCapture).GetMethod("AfterSuccessfulQueue", All);
            var capturePrefix = typeof(NativeRaiserToken.DeploymentCapture).GetMethod("Prefix", All);
            var inputPrefix = typeof(NativeRaiserToken.ExplicitDeploymentInput).GetMethod("Prefix", All);
            var inputFinalizer = typeof(NativeRaiserToken.ExplicitDeploymentInput).GetMethod("Finalizer", All);
            var missionReentry = typeof(DeploymentWindowClose).GetMethods(All).FirstOrDefault(x =>
                x.Name == "TryReenterDeferredForMission" && x.GetParameters().FirstOrDefault()?.ParameterType == typeof(DurableInboxStore));
            if (prefix == null || finalizer == null || defer == null || restore == null || capture == null || capturePrefix == null ||
                inputPrefix == null || inputFinalizer == null || missionReentry == null || !Calls(prefix, defer) ||
                !Calls(prefix, typeof(DeploymentWindowClose).GetMethod("TryCurrentPreparationOccurrence", All)) ||
                !Calls(finalizer, restore) ||
                !Calls(capturePrefix, typeof(NativeRaiserToken.DeploymentCapture).GetMethod("TryTakeExplicitReentry", All)) ||
                !Calls(capture, typeof(DeploymentWindowClose).GetMethod("TryReenterDeferredOccurrence", All)) ||
                !Calls(inputPrefix, typeof(NativeRaiserToken.DeploymentCapture).GetMethod("TryBeginExplicitReentry", All)) ||
                !Calls(inputPrefix, typeof(NativeRaiserToken.DeploymentCapture).GetMethod("BeginExplicitReentry", All)) ||
                !Calls(inputFinalizer, typeof(NativeRaiserToken.DeploymentCapture).GetMethod("EndExplicitReentry", All)) ||
                !Attribute.IsDefined(typeof(DeploymentPreparationBack), typeof(HarmonyLib.HarmonyPatch)))
                yield return "L396 premise-changed: native-Back-or-ToDeploymentState-no-longer-reaches-the-transaction-seams";
            var beginToken = typeof(NativeRaiserToken.DeploymentCapture).GetMethod("BeginExplicitReentry", All);
            if (AnyCalls(typeof(MissionSync), beginToken) || AnyCalls(typeof(EventSync), beginToken))
                yield return "L396 automatic-MissionSync-or-EventSync-path-can-mint-an-explicit-user-token";
            // DECLARED methods only. GetMethods without DeclaredOnly also hands back every INHERITED
            // method — System.Enum.ToString, once per enum type in this assembly — and Calls() is a raw
            // scan for the target's four metadata-token BYTES anywhere in a method body, not an IL
            // decode. Adding any member anywhere in the mod renumbers every metadata token, so the
            // pattern eventually lands inside a BCL body this sweep never meant to read: it did, and
            // thirty-five enums each reported themselves as a producer of a token only our own patch
            // class can mint. The question the law asks is about code WE declare; ask only about that.
            var ownAssembly = typeof(NativeRaiserToken).Assembly;
            var tokenProducers = ownAssembly.GetTypes()
                .SelectMany(x => x.GetMethods(All | BindingFlags.DeclaredOnly))
                .Where(x => Calls(x, beginToken)).ToArray();
            if (tokenProducers.Length != 1 || tokenProducers[0].DeclaringType != typeof(NativeRaiserToken.ExplicitDeploymentInput))
                // Name them: a byte-scan false positive is indistinguishable from a real second producer
                // until you can see which method it claims, and that is the difference between a five-
                // minute diagnosis and an afternoon.
                yield return "L396 explicit-user-token-has-a-producer-outside-the-grounded-LaunchMissionAbility-input: " +
                             string.Join(", ", tokenProducers.Select(x => x.DeclaringType.FullName + "." + x.Name));
            if (Calls(prefix, typeof(GeoMission).GetMethod("Cancel", All)))
                yield return "L396 Back-deferral-directly-called-mission-Cancel";

            // Deployment availability is an explicit door: a remote queued occurrence must not navigate.
            var controlCarrier = new ProbeCarrier();
            if (new DurableInboxEngine(Make(InboxLifecycle.Queued), member, controlCarrier)
                    .TryPresentNext(true, typeof(UIStateNothingSelected)) || controlCarrier.PresentCalls != 0)
                yield return "L396 queued-preparation-forced-remote-navigation";

            var ordinary = new OccurrenceId("OrdinaryNotice", "after-prep", new[] { "notice" });
            var mixed = new DurableInboxStore(new HostLedger(new[] {
                new InboxEntry(predecessor, member, InboxLifecycle.Removed, default(CanonicalChoiceId), 1, 1,
                    terminalReason: TerminalReason.Superseded,
                    supersededBy: occurrence),
                new InboxEntry(occurrence, member, InboxLifecycle.Queued, default(CanonicalChoiceId), 2, 0,
                    predecessor: predecessor),
                new InboxEntry(ordinary, member, InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0)
            }, 2, members));
            var mixedCarrier = new ProbeCarrier();
            if (!new DurableInboxEngine(mixed, member, mixedCarrier)
                    .TryPresentNext(true, typeof(UIStateNothingSelected)) || mixedCarrier.PresentCalls != 1 ||
                mixed.Ledger.Get(ordinary, member).Lifecycle != InboxLifecycle.Open)
                yield return "L396 explicit-prep-blocked-the-ordinary-window-behind-it";
        }

        private static bool Calls(MethodInfo from, MethodInfo to)
        {
            var il = from?.GetMethodBody()?.GetILAsByteArray(); if (il == null || to == null) return false;
            var token = BitConverter.GetBytes(to.MetadataToken);
            for (int i = 0; i <= il.Length - 4; i++)
                if (il[i] == token[0] && il[i + 1] == token[1] && il[i + 2] == token[2] && il[i + 3] == token[3]) return true;
            return false;
        }
        // DeclaredOnly for the same reason the producer sweep above carries it: an inherited BCL body is
        // not this type's code, and Calls() is a byte scan that will eventually find the token in one.
        private static bool AnyCalls(Type type, MethodInfo target)
        {
            if (type == null || target == null) return false;
            if (type.GetMethods(All | BindingFlags.DeclaredOnly).Any(x => Calls(x, target))) return true;
            return type.GetNestedTypes(All).Any(x => AnyCalls(x, target));
        }

        private sealed class ProbeCarrier : IDurableWindowCarrierAdapter
        {
            internal int PresentCalls;
            public InboxWindowCheckpoint Capture(OccurrenceId occurrence) => null;
            public bool Present(OccurrenceId occurrence) { PresentCalls++; return true; }
            public bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint) => false;
            public void Abandon(OccurrenceId occurrence) { }
            public void FinalizeRestore(OccurrenceId occurrence) { }
        }
    }
}
