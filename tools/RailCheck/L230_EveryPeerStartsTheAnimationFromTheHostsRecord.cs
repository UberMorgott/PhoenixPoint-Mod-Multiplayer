using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text;
using Multiplayer.Network;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L230 — EVERY PEER STARTS AN ATTACK ANIMATION FROM THE HOST'S RECORD, INCLUDING THE ONE THAT CLICKED,
    /// AND NO ACTOR IS LEFT WAITING FOR AN ECHO FOREVER.
    ///
    /// THE DEFECT (owner, 2026-08-08): "I throw a grenade and on one instance it has already exploded while
    /// on another it is only just leaving the hand. Same with shooting: one player is still aiming while on
    /// another instance that soldier has already fired and the enemy is dead." The rail was not dropping
    /// anything — the SAME order simply had three different start times by construction: the acting peer
    /// began at its own click (A3a's speculative local play), the host began when the intent landed, and the
    /// watchers began when the mirror landed. Every peer was correct in isolation.
    ///
    /// THE OUTCOME THIS LAW ASSERTS is therefore about WHERE an animation may start, not about whether a
    /// message was sent. A law that asserted "the intent is sent" would have been green through the whole
    /// defect — the intent was always sent. The arms below name the two facts that make the start times
    /// agree, plus the two that stop the cure being worse than the disease.
    ///
    /// THE ARMS:
    ///   (a) <c>click-plays-locally</c> — THE RULE, executed. <see cref="TacticalCommandSync.OrderWaitsForTheEcho"/>
    ///       must say WAIT for a rider that is not a move inside a shared battle, and must say PLAY-LOCALLY
    ///       for the three cases that are deliberately exempt (solo, a declared-local ability, and an
    ///       <c>IMoveAbility</c> — whose <c>FollowupAbility</c> the codec drops, so deferring it would delete
    ///       the acting player's follow-up attack instead of merely delaying it). Run to exhaustion over all
    ///       eight inputs, so a rule quietly inverted or widened is red.
    ///   (b) <c>echo-seam-unbound</c> — <c>ClickedOrderWaitsForTheEcho.Seam</c> must RESOLVE, and to
    ///       <c>TacticalViewState.ActivateAbility</c> with exactly the four parameter types. This is the trap
    ///       this repo keeps paying for: <c>AccessTools.Method(type, name, Type[])</c> does EXACT parameter
    ///       matching, so one widened or reordered type yields null, <c>Prepare()</c> stands the patch down,
    ///       and every clicked order silently plays locally again — the defect back in full with a green
    ///       build. Asserted on the RESOLVED member and not on the source text.
    ///   (c) <c>echo-wait-unbounded</c> — <see cref="TacticalCommandSync.EchoCeilingFrames"/> must be a real
    ///       bound, must be LONGER than the host's own <see cref="TacticalCommandSync.DeferCeilingSeconds"/>
    ///       hold (or a legitimately held order is abandoned by the peer that sent it), and
    ///       <c>TickEchoWaits</c> must reach <c>Debug.LogError</c>. A suppressed click plus a lost echo is a
    ///       soldier that cannot be commanded for the rest of the battle; dropping that silently is this
    ///       repo's dominant bug class landing on the most visible surface it has.
    ///   (d) <c>host-click-skips-arbitration</c> — <see cref="TacticalCommandSync.PublishClickedOrder"/> must
    ///       reach <c>HandleActivate</c>. THE HOST IS HALF THE DEFECT: a host click reaches <c>Activate</c>
    ///       natively, takes the <c>RelayMirror</c> branch and never touches <c>Validate</c> (the asymmetry
    ///       L210 measured — the host's free-aim shot fired for 76 damage on the same disabled state that
    ///       refused five client shots). Feeding the host's own click to the function that answers a peer's
    ///       is what puts both on one path; calling <c>ability.Activate</c> directly in that branch would
    ///       look identical from the outside and restore the bypass.
    ///   (e) <c>acting-peer-never-mirrored</c> — <see cref="TacticalCommandSync.OnAbilityActivated"/> must
    ///       reach <c>OrderWaitsForTheEcho</c>. The mirror used to EXCLUDE the peer that sent the intent,
    ///       because that peer had already played the order itself. A peer that now waits and is still
    ///       excluded waits for a record addressed to everybody but him — every shot would run to the arm (c)
    ///       ceiling. The exclusion must be decided by the same one rule, which is what this arm pins.
    ///   (f) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> inverts the rule, unbinds the seam, bounds
    ///       nothing and warns instead of erroring, activates directly instead of arbitrating, and excludes
    ///       by origin. All five arms must go red on it, or their green above is a scan that resolved nothing.
    ///
    /// NOT A QUORUM, and the distinction is the mod's hardest rule. The only peer ever waited on is the HOST,
    /// which answers by itself with no human action in the loop; no peer's progress depends on another peer
    /// ACTING, and an AFK peer cannot add one frame to the wait. Arm (c) is what keeps it that way even when
    /// the host's answer never comes at all.
    ///
    /// Falsify: invert <c>OrderWaitsForTheEcho</c> -> (a); widen a parameter type in <c>Seam</c> -> (b);
    /// set <c>EchoCeilingFrames</c> below <c>DeferCeilingSeconds*60</c>, or drop the <c>Debug.LogError</c>
    /// from <c>TickEchoWaits</c> -> (c); make the host branch call <c>ability.Activate</c> -> (d); restore
    /// <c>_replayOriginPeer</c> as the mirror's exclude argument -> (e).
    /// </summary>
    internal static class L230_EveryPeerStartsTheAnimationFromTheHostsRecord
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            // Provenance is structural, not an ability-name list: a client engine consequence is never a
            // command, a client UI gesture is, and every host-native activation is published exactly once.
            if (TacticalCommandSync.MayPublishActivation(false, false))
                yield return "L230 client-consequence-became-intent: a non-host activation outside the one UI " +
                             "order ingress is publishable. Status/turn consequences run on every peer, so this " +
                             "duplicates the host's native record.";
            if (!TacticalCommandSync.MayPublishActivation(false, true))
                yield return "L230 client-gesture-was-swallowed: an explicit client order cannot publish.";
            if (!TacticalCommandSync.MayPublishActivation(true, false))
                yield return "L230 host-consequence-was-swallowed: the host no longer publishes native engine " +
                             "consequences to the peers.";
            var sync = typeof(TacticalCommandSync);
            var tick = sync.GetMethod("TickEchoWaits", AllMembers);
            var publish = sync.GetMethod("PublishClickedOrder", AllMembers);
            var capture = sync.GetMethod("OnAbilityActivated", AllMembers);
            var patch = typeof(TacticalCommandSync).Assembly
                            .GetType("Multiplayer.Tactical.ClickedOrderWaitsForTheEcho");
            var followupPatch = typeof(TacticalCommandSync).Assembly
                            .GetType("Multiplayer.Tactical.MoveFollowupWaitsForTheEcho");
            var seamField = patch == null ? null : patch.GetField("Seam", AllMembers);
            var followupSeamField = followupPatch == null ? null : followupPatch.GetField("Seam", AllMembers);
            var followupPrefix = followupPatch == null ? null : followupPatch.GetMethod("Prefix", AllMembers);
            var clickPrefix = patch == null ? null : patch.GetMethod("Prefix", AllMembers);
            var clickFinalizer = patch == null ? null : patch.GetMethod("Finalizer", AllMembers);
            if (capture == null || !Reaches(capture, null, "MayPublishActivation"))
                yield return "L230 provenance-predicate-bypassed: OnAbilityActivated no longer asks the one " +
                             "host/explicit-order predicate, so client engine consequences become intents again.";
            if (clickPrefix == null || !Reaches(clickPrefix, null, "EnterExplicitOrder") ||
                clickFinalizer == null || !Reaches(clickFinalizer, null, "LeaveExplicitOrder"))
                yield return "L230 explicit-order-scope-unbound: the UI ingress does not bracket its native " +
                             "activation with EnterExplicitOrder/LeaveExplicitOrder. Either clicks are swallowed " +
                             "or the marker leaks and later engine consequences become commands.";
            var viewSeam = typeof(TacticalViewState).GetMethod("ActivateAbility", AllMembers);
            var queue = sync.GetMethod("QueueActivation", AllMembers);
            var pump = sync.GetMethod("TickScheduledActivations", AllMembers);
            var epoch = sync.GetMethod("ExecuteEpochReached", AllMembers);
            var apply = sync.GetMethod("ApplyActivate", AllMembers);
            var nativeGate = typeof(TacticalCommandSync).Assembly
                .GetType("Multiplayer.Tactical.HostNativeActivationWaitsForExecuteEpoch");
            var nativePrefix = nativeGate?.GetMethod("Prefix", AllMembers);

            if (tick == null || publish == null || capture == null || seamField == null || viewSeam == null ||
                followupSeamField == null || followupPrefix == null)
            {
                yield return "L230 premise-changed: one of TacticalCommandSync.{TickEchoWaits, " +
                             "PublishClickedOrder, OnAbilityActivated}, ClickedOrderWaitsForTheEcho.Seam or " +
                             "TacticalViewState.ActivateAbility no longer resolves. The seams this law is " +
                             "written over have moved, so every arm below would be asserting about a shape " +
                             "the build no longer has.";
                yield break;
            }

            if (nativePrefix == null || !Reaches(nativePrefix, null, "ScheduleHostNativeAtEpoch"))
                yield return "L230 native-producer-bypasses-epoch: host AI/reaction activations must be " +
                             "suppressed at their real virtual override and queued through the same execute epoch.";
            var lateBinder = typeof(TacticalCommandSync).Assembly.GetType("Multiplayer.Harmony.TftvLateBinder");
            if (!Reaches(lateBinder?.GetMethod("BindAll", AllMembers), null, "BindLateAssembly"))
                yield return "L230 late-tftv-activate-bypasses-epoch: the post-load TFTV binder must patch " +
                             "Activate(object) overrides loaded after PatchAll, before their derived mutation.";
            if (!Reaches(nativeGate?.GetMethod("Finalizer", AllMembers), null,
                         "ClearScheduledCaptureSuppression"))
                yield return "L230 host-prefix-token-leaks: the exact suppression token must be cleared by a " +
                             "finalizer even when a skipped derived override never reaches base capture.";

            byte recordOp = (byte)sync.GetField("OpActivate", AllMembers).GetRawConstantValue();
            byte intentOp = (byte)sync.GetField("OpIntentActivate", AllMembers).GetRawConstantValue();
            if (recordOp == 1 || recordOp == intentOp)
                yield return "L230 op1-tombstone-reused: legacy action record op 1 must remain retired; epoch wire " +
                             "uses a distinct op so mixed DLLs reject instead of mis-decoding actor bytes.";

            if (queue == null || pump == null || epoch == null || apply == null ||
                !Reaches(pump, null, "ApplyActivate") || !Reaches(pump, null, "TryHostNowMs") ||
                !Reaches(sync.GetMethod("HandleActivate", AllMembers), null, "QueueActivation"))
                yield return "L230 execute-epoch-bypassed: accepted actions must be queued with actionId/executeAt " +
                             "and only TickScheduledActivations may hand them to ApplyActivate after HostNowMs; " +
                             "HandleActivate must never start native animation immediately.";
            else
            {
                // Same host epoch, deliberately different packet-arrival instants: neither receiver starts
                // before 10_000, both start on/after it, and a late receiver starts immediately.
                bool earlyA = (bool)epoch.Invoke(null, new object[] { 9800L, 10000L });
                bool earlyB = (bool)epoch.Invoke(null, new object[] { 9950L, 10000L });
                bool onTime = (bool)epoch.Invoke(null, new object[] { 10000L, 10000L });
                bool late = (bool)epoch.Invoke(null, new object[] { 10125L, 10000L });
                if (earlyA || earlyB || !onTime || !late)
                    yield return "L230 execute-epoch-rule-broken: arrival skew must not start either peer before " +
                                 "the shared epoch, and a record already late must start immediately without quorum.";

                if (TacticalCommandSync.ComputeExecuteLeadMs(-1) != TacticalCommandSync.MinExecuteLeadMs ||
                    TacticalCommandSync.ComputeExecuteLeadMs(100) <= TacticalCommandSync.MinExecuteLeadMs ||
                    TacticalCommandSync.ComputeExecuteLeadMs(5000) != TacticalCommandSync.MaxExecuteLeadMs)
                    yield return "L230 execute-lead-not-adaptive: unknown/local RTT must use the small minimum, " +
                                 "ordinary RTT must price half-trip+jitter, and extreme RTT must hit a finite cap.";

                Func<ulong, ulong, ulong, ulong, long, int, bool, string> valid =
                    TacticalCommandSync.ValidateActionHeader;
                if (valid(7, 7, 6, 1, 100, 0, false) != null ||
                    valid(6, 7, 6, 1, 100, 0, false) != "epoch" ||
                    valid(7, 7, 6, 0, 100, 0, false) != "id" ||
                    valid(7, 7, 6, 1, 100, 0, true) != "duplicate" ||
                    valid(7, 7, 6, 2, 100000, 0, false) != "time")
                    yield return "L230 malformed-action-accepted: old epoch, zero id, duplicate-after-drain, " +
                                 "and far-future records must all be rejected while a valid record passes.";

                if (!TacticalCommandSync.CameraBusyAnswer(true, false) ||
                    TacticalCommandSync.CameraBusyAnswer(true, true) ||
                    TacticalCommandSync.CameraBusyAnswer(false, false))
                    yield return "L230 camera-identity-leaks: Busy may be forced false only for the exact shared " +
                                 "FireWeaponAtTargetCrt iterator; concurrent/nonshared camera reads keep native.";

                if (PingTable.HostClockUsable(false, 1000, 900) ||
                    PingTable.HostClockUsable(true, 1000, 0) ||
                    !PingTable.HostClockUsable(true, 1000, 900) ||
                    PingTable.HostClockUsable(true, 5000, 900))
                    yield return "L230 stale-clock-accepted: unknown/reset and stale host-clock samples must not " +
                                 "schedule a wait; only a fresh monotonic sample may price executeAt.";
                PingTable.ResetHostClock();
                long clockNow = PingTable.NowMs(), observed;
                PingTable.ObserveHostClock(clockNow - 10, clockNow, -1);
                bool unknownStayedUnknown = !PingTable.TryHostNowMs(false, out observed);
                PingTable.ObserveHostClock(clockNow - 10, clockNow, 20);
                bool validBecameKnown = PingTable.TryHostNowMs(false, out observed);
                PingTable.ResetHostClock();
                if (!unknownStayedUnknown || !validBecameKnown)
                    yield return "L230 unknown-rtt-created-clock: ObserveHostClock must ignore RTT=-1 and only " +
                                 "make the host clock usable after a real fresh RTT sample.";

                ulong retired, next;
                TacticalCommandSync.EpochResetModel(false, 41, 99, out retired, out next);
                bool clientReset = retired == 41 && next == 0;
                TacticalCommandSync.EpochResetModel(true, 0, 77, out retired, out next);
                if (!clientReset || retired != 0 || next != 77 ||
                    valid(41, 0, 41, 1, 0, 0, false) != "epoch" ||
                    valid(77, 77, 41, 1, 0, 0, false) != null)
                    yield return "L230 epoch-lifecycle-broken: client reset must retire the current epoch, latch " +
                                 "no local replacement, reject the late prior op, then accept the first host epoch.";

                if (!TrailingRecordsAreTransactional(sync))
                    yield return "L230 trailing-record-mutated: trailing op7/op8 must be fully decoded and EOF-" +
                                 "rejected before queue/apply/telemetry/Seq mutation; repeating it must still do zero.";
            }

            foreach (var v in ScanRule(TacticalCommandSync.OrderWaitsForTheEcho, "OrderWaitsForTheEcho"))
                yield return v;
            foreach (var v in ScanSeam(seamField.GetValue(null) as MethodBase, "ClickedOrderWaitsForTheEcho.Seam"))
                yield return v;
            foreach (var v in ScanBound(tick, TacticalCommandSync.EchoCeilingFrames,
                                        TacticalCommandSync.DeferCeilingSeconds, "TickEchoWaits"))
                yield return v;
            foreach (var v in ScanHostPath(publish, "PublishClickedOrder")) yield return v;
            foreach (var v in ScanMirrorAudience(capture, "OnAbilityActivated")) yield return v;

            var followupSeam = followupSeamField.GetValue(null) as MethodBase;
            if (followupSeam == null || followupSeam.DeclaringType != typeof(MoveAbility) ||
                followupSeam.Name != "TryToExecuteFollowupAbility")
                yield return "L230 move-followup-starts-locally: the follow-up patch is not bound to " +
                             "MoveAbility.TryToExecuteFollowupAbility, the engine producer that directly " +
                             "calls FollowupAbility.ExecuteAndWait.";
            var iterator = followupSeam == null ? null :
                followupSeam.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType
                            ?.GetMethod("MoveNext", AllMembers);
            if (iterator == null || !Reaches(iterator, null, "ExecuteAndWait"))
                yield return "L230 followup-producer-premise-changed: the bound engine iterator no longer " +
                             "reaches ExecuteAndWait. Re-audit the real FollowupAbility producer before moving " +
                             "the gate; a green patch on an obsolete method protects nothing.";
            if (!Reaches(followupPrefix, null, "PublishClickedOrder"))
                yield return "L230 move-followup-starts-locally: the producer prefix does not publish through " +
                             "the existing host-record gate before allowing/suppressing direct execution.";

            var freeCam = typeof(PhoenixPoint.Tactical.View.ViewStates.UIStateFreeCam);
            if (!freeCam.GetMethods(AllMembers).Any(m => Reaches(m, null, "ActivateAbility")))
                yield return "L230 freecam-bypasses-click-gate: UIStateFreeCam no longer routes its allied/free " +
                             "target confirmation through TacticalViewState.ActivateAbility; inspect the new " +
                             "producer before claiming the click seam covers free aim.";

            // ── arm (f): every arm must be able to SEE its own violation.
            var fake = typeof(FakeSeam);
            var control = ScanRule(FakeSeam.Rule, "FakeSeam.Rule")
                .Concat(ScanSeam(null, "FakeSeam.Seam"))
                .Concat(ScanBound(fake.GetMethod("Tick", AllMembers), 60, 10f, "FakeSeam.Tick"))
                .Concat(ScanHostPath(fake.GetMethod("Publish", AllMembers), "FakeSeam.Publish"))
                .Concat(ScanMirrorAudience(fake.GetMethod("Capture", AllMembers), "FakeSeam.Capture"))
                .ToList();
            foreach (var want in new[] { "click-plays-locally", "echo-seam-unbound", "echo-wait-unbounded",
                                         "host-click-skips-arbitration", "acting-peer-never-mirrored" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L230 control-not-red: FakeSeam commits " + want + " and the scan did not " +
                                 "flag it. That arm cannot tell the fixed shape from the broken one, so its " +
                                 "green above means nothing — the exact way L169 stayed green while a client " +
                                 "free-aim shot could not fire at all.";
            if (Reaches(fake.GetMethod("Followup", AllMembers), null, "PublishClickedOrder"))
                yield return "L230 control-not-red: a fake follow-up that activates directly was mistaken for " +
                             "one that publishes through the host-record gate.";
        }

        private static bool TrailingRecordsAreTransactional(Type sync)
        {
            try
            {
            var apply = sync.GetMethod("ApplyInbound", AllMembers);
            var scheduled = sync.GetField("_scheduledActivations", AllMembers)?.GetValue(null) as System.Collections.IList;
            var arrived = sync.GetField("_recordArrived", AllMembers);
            var seq = sync.GetField("Seq", AllMembers)?.GetValue(null);
            seq?.GetType().GetMethod("Reset", AllMembers)?.Invoke(seq, null);
            if (apply == null || scheduled == null || arrived == null) return false;
            scheduled.Clear();
            arrived.SetValue(null, -123f);
            byte op7 = (byte)sync.GetField("OpActivate", AllMembers).GetRawConstantValue();
            // op 8 is retired (L445) — a trailing record on the dead number must be as inert as one on op 7.
            foreach (var op in new byte[] { op7, 8 })
                for (int repeat = 0; repeat < 2; repeat++)
                    apply.Invoke(null, new object[] { TrailingRecord(op, op == op7) });
            return scheduled.Count == 0 && (float)arrived.GetValue(null) == -123f;
            }
            catch { return false; }
        }

        private static byte[] TrailingRecord(byte op, bool epoch)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(1u); w.Write(op);
                if (epoch) { w.Write(77UL); w.Write(1UL); w.Write(PingTable.NowMs()); }
                w.Write(0); w.Write("x"); w.Write(false); w.Write(false); w.Write((byte)0xA5);
                return ms.ToArray();
            }
        }

        /// <summary>Arm (a) — the rule, run to exhaustion.</summary>
        private static IEnumerable<string> ScanRule(Func<bool, bool, bool, bool> rule, string label)
        {
            if (!rule(true, true, false))
                yield return "L230 click-plays-locally: " + label + "(sharedBattle, rider, notMove) says the " +
                             "acting peer plays its own click. That is the defect verbatim — the clicking " +
                             "peer starts the animation at press time while every other peer starts it a ping " +
                             "later, so a grenade has already exploded on one screen while it is still in the " +
                             "hand on another. The order must be published and the animation started from the " +
                             "host's mirrored record, the same record every watching peer plays from.";
            if (rule(true, true, true))
                yield return "L230 click-plays-locally: " + label + " defers an IMoveAbility. FollowupAbility " +
                             "and FollowupAbilityTarget are in TacAbilityTargetCodec.Dropped, so a move that " +
                             "carries a follow-up attack (UIStateCharacterSelected.MoveAndActivateAbility:945) " +
                             "loses that attack on the wire: deferring the move DELETES the acting player's " +
                             "own follow-up shot rather than delaying it. Move is also the one rider whose " +
                             "divergence the settle/closer already corrects.";
            if (rule(true, false, false))
                yield return "L230 click-plays-locally: " + label + " defers a NON-rider. A declared-local " +
                             "ability (TacticalCommandSync.LocalAbilities — inventory, crate, idle, panic) is " +
                             "never mirrored at all, so waiting for an echo of it is waiting for a record no " +
                             "peer will ever send: that soldier stands frozen until the arm (c) ceiling.";
            if (rule(false, true, false))
                yield return "L230 click-plays-locally: " + label + " defers a click outside a shared battle. " +
                             "In a solo game there is no host to echo, so this makes single-player unplayable " +
                             "— every click would do nothing for " +
                             (TacticalCommandSync.EchoCeilingFrames / 60) + "s and then log an error.";
        }

        /// <summary>Arm (b) — the seam really binds, with the exact signature.</summary>
        private static IEnumerable<string> ScanSeam(MethodBase seam, string label)
        {
            if (seam == null)
            {
                yield return "L230 echo-seam-unbound: " + label + " is NULL. AccessTools.Method does EXACT " +
                             "parameter matching — one widened, reordered or renamed type resolves to null, " +
                             "Prepare() stands the patch down (loudly, but only in a live log), and every " +
                             "clicked order plays locally at press time again. The build stays green while " +
                             "the whole fix is absent.";
                yield break;
            }
            if (seam.DeclaringType != typeof(TacticalViewState) || seam.Name != "ActivateAbility")
                yield return "L230 echo-seam-unbound: " + label + " resolved to " + seam.DeclaringType + "." +
                             seam.Name + ". The seam must be TacticalViewState.ActivateAbility — the ONE " +
                             "method every player click passes through (UIStateShoot is its only override in " +
                             "the game and it calls base). Blocking one layer down at TacticalAbility.Activate " +
                             "cannot work: it is VIRTUAL, and skipping the base body still lets " +
                             "ShootAbility.Activate:165-174 run its own PlayAction(Shoot).";
            var want = new[] { typeof(TacticalAbility), typeof(TacticalAbilityTarget),
                               typeof(Base.UI.StateStackAction), typeof(Func<TacticalAbility, bool>) };
            var got = seam.GetParameters().Select(p => p.ParameterType).ToArray();
            if (!got.SequenceEqual(want))
                yield return "L230 echo-seam-unbound: " + label + " has parameters (" +
                             string.Join(", ", got.Select(t => t.Name).ToArray()) + ") — the click funnel this " +
                             "law is written over takes (TacticalAbility, TacticalAbilityTarget, " +
                             "StateStackAction, Func<TacticalAbility,bool>). A different overload is a " +
                             "different seam and the clicked orders route around it.";
        }

        /// <summary>Arm (c) — the wait is bounded, long enough, and loud when it trips.</summary>
        private static IEnumerable<string> ScanBound(MethodBase tick, int ceilingFrames, float hostHoldSeconds,
                                                     string label)
        {
            if (ceilingFrames <= 0 || ceilingFrames <= hostHoldSeconds * 60f)
                yield return "L230 echo-wait-unbounded: " + label + "'s ceiling is " + ceilingFrames +
                             " frames against a host hold of " + hostHoldSeconds + "s. It must be POSITIVE (an " +
                             "unbounded wait leaves a soldier uncommandable for the rest of the battle, since " +
                             "the click that would command him is suppressed) and LONGER than the host's own " +
                             "hold, because a host legitimately queuing this peer's order behind that same " +
                             "peer's previous one gives up first and answers with a reject + forced settle, " +
                             "which is what clears the wait through QueueSettle.";
            if (!Reaches(tick, "MpLog", "LogError"))
                yield return "L230 echo-wait-unbounded: " + label + " must reach Debug.LogError when the wait " +
                             "gives up. An echo that never arrives means this peer never played an action " +
                             "every other peer did — releasing the soldier quietly leaves a divergence with " +
                             "no trace at all, which is this repo's dominant bug class landing on the most " +
                             "visible surface it has. A warning is not enough: this is a lost order.";
        }

        /// <summary>Arm (d) — the host's own click is arbitrated like a peer's.</summary>
        private static IEnumerable<string> ScanHostPath(MethodBase publish, string label)
        {
            if (!Reaches(publish, null, "HandleActivate"))
                yield return "L230 host-click-skips-arbitration: " + label + " must hand the HOST's own click " +
                             "to HandleActivate — the very function that answers a client's order — so it is " +
                             "validated, held and mirrored from one place. Calling ability.Activate directly " +
                             "in that branch restores the RelayMirror bypass L210 measured: the host's own " +
                             "free-aim shot fired for 76 damage on exactly the disabled state that had just " +
                             "refused five client shots, because a host click never reaches Validate.";
        }

        /// <summary>Arm (e) — the mirror's audience is decided by the one rule.</summary>
        private static IEnumerable<string> ScanMirrorAudience(MethodBase capture, string label)
        {
            if (!Reaches(capture, null, "OrderWaitsForTheEcho"))
                yield return "L230 acting-peer-never-mirrored: " + label + " must decide the mirror's exclude " +
                             "argument through OrderWaitsForTheEcho. The exclusion exists only because a " +
                             "SPECULATIVE acting peer had already played the order; a peer that now waits for " +
                             "the echo and is still excluded from it waits for a record addressed to everybody " +
                             "but him, so every one of his shots runs to the ceiling and then logs a lost echo.";
        }

        /// <summary>ARM (f). Never instantiated, never registered — it exists only to be walked and called.
        /// One violation per arm: an inverted rule, an unbound seam, a bound shorter than the host's hold with
        /// only a warning, a host branch that activates directly, and a capture that excludes by origin.</summary>
        private static class FakeSeam
        {
            internal static bool Rule(bool inSharedBattle, bool abilityIsRider, bool abilityIsMove)
                => false;                                          // (a): nothing ever waits

            internal static void Tick()
            {
                Debug.LogWarning("echo wait gave up");             // (c): warns, never errors
            }

            internal static bool Publish(TacticalAbility ability, TacticalAbilityTarget target)
            {
                ability.Activate(target);                          // (d): straight past the arbitration
                return true;
            }

            internal static void Capture(TacticalAbility ability)
            {
                Send(ability == null ? 0UL : 1UL);                 // (e): excludes by origin, no rule read
            }

            internal static void Followup(TacticalAbility ability)
            {
                ability.Activate(null);
            }

            private static void Send(ulong excludePeer) { }
        }

        // ─── IL helpers (same primitives as L220; Program.cs is not partial) ─────────────────────

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || (c.DeclaringType != null &&
                                                                     c.DeclaringType.Name == declaringType)));

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F, 0x73))
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m == null ? null : m.GetMethodBody() == null ? null : m.GetMethodBody().GetILAsByteArray(); }
            catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
