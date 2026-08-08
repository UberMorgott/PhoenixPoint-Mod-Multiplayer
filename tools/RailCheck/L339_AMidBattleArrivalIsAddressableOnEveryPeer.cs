using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Base.Entities;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Equipments;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L339 — AN ACTOR THAT ENTERS PLAY AFTER THE BATTLE-KEY MAP IS BUILT IS ADDRESSABLE ON EVERY PEER,
    /// IN BOTH DIRECTIONS, AND NOTHING ABOUT IT RIDES OUT BEFORE THE RECORD THAT NAMES IT.
    ///
    /// THE REPORT (live 2026-08-08, one mission, host <c>multiplayer.log</c>): eight
    /// <c>a weapon switch on Soldier_NN cannot be relayed — that actor has no shared key at all, and the
    /// battle key map IS built … it entered play on this peer alone</c>
    /// (:2468 :2652 :2953 :3129 :3279 :3657 :3877 :4213). Read as a lost spawn-key mechanism. It was not:
    /// every one of the eight is followed 30-60 ms later by THAT VERY ACTOR's own record —
    /// <c>HOST spawn Jinx key=-149 seq=1</c> (:2470), Tempest -150 (:2654), Venom -151 (:2955),
    /// Razor -152 (:3131), Drift -153 (:3281), Mangle -154 (:3659), Ravenna -155 (:3879),
    /// Vortex -156 (:4215). 8/8. The key was minted and the spawn shipped every single time.
    ///
    /// WHAT IS ACTUALLY TRUE. A mid-battle arrival is KEYLESS for the whole of its own
    /// <c>ActorComponent.DoEnterPlay</c>:114-124, and it cannot be otherwise:
    /// <c>GeoUnitId</c> is stamped INSIDE that body (:120 -> <c>TacticalActorBase.ProcessInstanceData</c>:399),
    /// so a prefix that minted would stamp a derived ordinal over an actor that has a real cross-layer
    /// identity; and the record that NAMES the actor is only assembled AFTER <c>FinalizeEnterPlay</c>, where
    /// TFTV's champ roll lands. Meanwhile <c>ActorComponent.OnEnterPlay</c>:132-134 runs every
    /// <c>IActorLifecycleListener</c>, and <c>EquipmentComponent.OnActorEnteredPlay</c>:56 raises
    /// <c>SetSelectedEquipment</c> right there — into a capture seam, with no key. The spawn rides 0x84 and
    /// that seam rides 0x82: two seq streams, no ordering between them, so anything emitted in that window
    /// can overtake its own spawn and land on a peer that has never heard of the key.
    ///
    /// So the window is now DECLARED rather than survived by accident
    /// (<see cref="TacticalActorLifecycle.HoldMidBattleEnterPlay"/>) — the host holds the same
    /// <c>SyncApplyScope</c> across a mid-battle enter-play that the mirror side already holds across its
    /// rebuild of the same actor in <see cref="TacticalActorLifecycle.ApplySpawn"/>.
    ///
    /// THE ARMS. (b) is the OUTCOME and it is EXECUTED on the real key table, both directions; everything
    /// else exists because (b) alone would stay green while the window lied.
    ///   (a) PREMISE — the family resolves.
    ///   (b) OUTCOME, EXECUTED, BOTH WAYS: the number the host mints for an actor that arrives after the
    ///       build is a number the HOST resolves back to that actor (a client's order about it lands), and
    ///       the number a peer ADOPTS resolves back to its own copy (a host record about it lands). Same
    ///       number, two peers, no key 0 and no "why". Plus: adoption must not move the counter, because a
    ///       peer that adopts before its own build would otherwise shift every ordinal it is about to mint
    ///       and the two rosters would silently name different monsters.
    ///   (c) THE WINDOW IS DECLARED — the seam takes the hold, the hold is a <c>SyncApplyScope</c>, and it is
    ///       released by a FINALIZER (a postfix does not run when the original throws, and a leaked depth
    ///       suppresses every intent capture on this peer with no log line at all).
    ///   (d) AND THE MINT DOES NOT MOVE INTO IT. The tempting wrong fix is minting in the prefix; it reads
    ///       GeoUnitId 0 for an actor that has one.
    ///   (e) THE SEAM THE FIX RIDES. Suppression works only for as long as the capture seams still consult
    ///       <c>SyncApplyScope.Active</c> and the game still raises the selection from inside enter-play.
    ///   (f) THE TWO SURFACES ARE STILL TWO. If the spawn and the command stream ever merged into one seq
    ///       stream, the "nothing may ride early" argument changes and this law should be re-derived.
    ///   (g) NOT ARMED AT BATTLE START. Before <c>Built</c> the selection seam PENDS its raise and
    ///       <c>BuildBattleKeys</c> flushes it; silencing it there would drop the mission-entry selections
    ///       for good (L186).
    ///   (h) THE CLIENT TAKES THE HOST'S NUMBER — <c>ApplySpawn</c> must still <c>Adopt</c>. A rebuild that
    ///       does not adopt gives the actor a local identity nobody else can name, which is the host-only
    ///       actor this law is named after.
    ///
    /// Falsify (verified RED, then restored):
    ///   • drop the <c>HoldMidBattleEnterPlay</c> call from <c>ActorEnterPlaySeam.Prefix</c> → window-undeclared
    ///   • make <c>TacticalActorKey.Adopt</c> return before it registers → arrival-not-addressable-on-the-adopting-peer
    /// </summary>
    internal static class L339_AMidBattleArrivalIsAddressableOnEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(TacticalActorLifecycle).Assembly;
            var unity = typeof(GameObject).Assembly;
            var game = typeof(ActorComponent).Assembly;

            var key = mod.GetType("Multiplayer.Tactical.TacticalActorKey");
            var seam = mod.GetType("Multiplayer.Tactical.ActorEnterPlaySeam");
            var cmd = mod.GetType("Multiplayer.Tactical.TacticalCommandSync");
            var surfaces = mod.GetType("Multiplayer.Network.Sync.SurfaceIds");

            var hold = typeof(TacticalActorLifecycle).GetMethod("HoldMidBattleEnterPlay", All);
            var entered = typeof(TacticalActorLifecycle).GetMethod("OnActorEnteredPlay", All);
            var applySpawn = typeof(TacticalActorLifecycle).GetMethod("ApplySpawn", All);
            var prefix = seam == null ? null : seam.GetMethod("Prefix", All);
            var postfix = seam == null ? null : seam.GetMethod("Postfix", All);
            var finalizer = seam == null ? null : seam.GetMethod("Finalizer", All);
            var assign = key == null ? null : key.GetMethod("AssignHostKey", All);
            var adopt = key == null ? null : key.GetMethod("Adopt", All);
            var resolve = key == null ? null : key.GetMethod("Resolve", All);
            var reset = key == null ? null : key.GetMethod("Reset", All);
            var built = key == null ? null : key.GetField("_built", All);
            var nextDerived = key == null ? null : key.GetField("_nextDerived", All);
            var onSelected = cmd == null ? null : cmd.GetMethod("OnEquipmentSelected", All);
            var scopeEnter = typeof(SyncApplyScope).GetMethod("Enter", All);
            var scopeActive = typeof(SyncApplyScope).GetMethod("get_Active", All);
            var scopeBuilt = key == null ? null : key.GetMethod("get_Built", All);
            var doEnterPlay = typeof(ActorComponent).GetMethod("DoEnterPlay", All);
            var onEnterPlay = typeof(ActorComponent).GetMethod("OnEnterPlay", All);
            // ActorComponent's own declaration, not TacticalActorBase's override: DoEnterPlay's IL carries the
            // token of the method it CALLS, and the override is a different token entirely.
            var processInstance = typeof(ActorComponent).GetMethod("ProcessInstanceData", All);
            var eqEnteredPlay = typeof(EquipmentComponent).GetMethod("OnActorEnteredPlay", All);
            var setSelected = typeof(EquipmentComponent).GetMethod("SetSelectedEquipment", All);
            var dispose = typeof(IDisposable).GetMethod("Dispose", All);

            if (hold == null || entered == null || applySpawn == null || prefix == null || postfix == null ||
                finalizer == null || assign == null || adopt == null || resolve == null || reset == null ||
                built == null || nextDerived == null || onSelected == null || scopeEnter == null ||
                scopeActive == null || scopeBuilt == null || doEnterPlay == null || onEnterPlay == null ||
                processInstance == null || eqEnteredPlay == null || setSelected == null || surfaces == null)
            {
                yield return "L339 premise-changed: the mid-battle arrival family no longer resolves " +
                             "(TacticalActorLifecycle.HoldMidBattleEnterPlay/OnActorEnteredPlay/ApplySpawn, " +
                             "ActorEnterPlaySeam.Prefix/Postfix/Finalizer, TacticalActorKey.AssignHostKey/Adopt/" +
                             "Resolve/Reset/_built/_nextDerived/Built, TacticalCommandSync.OnEquipmentSelected, " +
                             "SyncApplyScope.Enter/Active, ActorComponent.DoEnterPlay/OnEnterPlay, " +
                             "TacticalActorBase.ProcessInstanceData, EquipmentComponent.OnActorEnteredPlay/" +
                             "SetSelectedEquipment, SurfaceIds). Every arm below would pass vacuously, so 'an " +
                             "actor that arrives mid-battle is addressable on every peer' is UNCHECKED rather " +
                             "than satisfied";
                yield break;
            }

            // ═══ (b) THE OUTCOME, EXECUTED, BOTH DIRECTIONS ═══
            foreach (var v in BothDirections(reset, built, nextDerived, assign, adopt, resolve)) yield return v;

            // ═══ (c) THE WINDOW IS DECLARED ═══
            if (!Reaches(prefix, hold, mod))
                yield return "L339 window-undeclared: ActorEnterPlaySeam.Prefix no longer takes " +
                             "TacticalActorLifecycle.HoldMidBattleEnterPlay, so a mid-battle arrival runs its " +
                             "whole DoEnterPlay with no declared window. EquipmentComponent.OnActorEnteredPlay:56 " +
                             "raises SetSelectedEquipment from inside ActorComponent.OnEnterPlay:132-134 — before " +
                             "the key exists and before the spawn record ships — so the capture seam refuses it " +
                             "with 'that actor has no shared key at all … it entered play on this peer alone': a " +
                             "claim of PERMANENT roster divergence about a 40 ms window. That message is what sent " +
                             "a whole session hunting a spawn-key bug that was never there (host log :2468 vs the " +
                             "same actor's HOST spawn key=-149 at :2470)";
            if (!Reaches(hold, scopeEnter, mod))
                yield return "L339 window-is-not-a-scope: HoldMidBattleEnterPlay no longer enters " +
                             "SyncApplyScope. The scope IS the declaration — it is the one thing every capture " +
                             "seam already consults, and it is what the mirror side holds across its rebuild of " +
                             "the very same actor (ApplySpawn). A hold that is not that scope suppresses nothing";
            if (!Reaches(finalizer, dispose, typeof(IDisposable).Assembly))
                yield return "L339 window-never-closes: ActorEnterPlaySeam.Finalizer does not dispose the hold. " +
                             "A leaked SyncApplyScope depth makes this peer believe a delta apply is permanently " +
                             "on the stack, so EVERY intent capture stands down for the rest of the session — " +
                             "silently, with no log line, which is this repo's dominant failure shape";
            if (seam.GetMethod("Finalizer", All).ReturnType != typeof(Exception))
                yield return "L339 window-swallows-the-throw: ActorEnterPlaySeam.Finalizer does not return an " +
                             "Exception. A Harmony finalizer that returns void (or returns null) SWALLOWS a throw " +
                             "out of ActorComponent.DoEnterPlay, so a half-built actor becomes a silent one";

            // ═══ (d) AND THE MINT DOES NOT MOVE INTO THE PREFIX ═══
            if (Reaches(prefix, assign, mod))
                yield return "L339 key-minted-before-its-identity: ActorEnterPlaySeam.Prefix reaches " +
                             "TacticalActorKey.AssignHostKey. This is the obvious wrong fix for the keyless " +
                             "window and it corrupts identity: GeoUnitId is stamped INSIDE the body being " +
                             "prefixed (ActorComponent.DoEnterPlay:120 -> TacticalActorBase.ProcessInstanceData" +
                             ":399), so the prefix reads 0 for an actor that HAS a cross-layer identity and " +
                             "stamps a derived ordinal over it — the one case AssignHostKey exists to refuse";
            if (!Reaches(postfix, entered, mod) || !Reaches(entered, assign, mod))
                yield return "L339 arrival-never-keyed: the mint no longer runs at the enter-play postfix " +
                             "(ActorEnterPlaySeam.Postfix -> OnActorEnteredPlay -> AssignHostKey). That postfix is " +
                             "the earliest point at which BOTH facts an arrival's key needs are true: GeoUnitId " +
                             "has been stamped, and TFTV's champ roll (FinalizeEnterPlay) has happened, so the " +
                             "record that names the actor can ship complete";

            // ═══ (e) THE SEAM THE FIX RIDES ═══
            if (!Reaches(onSelected, scopeActive, mod))
                yield return "L339 capture-ignores-the-window: TacticalCommandSync.OnEquipmentSelected no longer " +
                             "consults SyncApplyScope.Active. The declared window contains that seam and no other " +
                             "way — a seam that stops asking is back to refusing a mid-battle arrival's own " +
                             "enter-play selection with 'it entered play on this peer alone', and back to emitting " +
                             "on 0x82 ahead of the 0x84 spawn that names the actor";
            if (!Reaches(eqEnteredPlay, setSelected, game))
                yield return "L339 premise-changed: EquipmentComponent.OnActorEnteredPlay no longer raises " +
                             "SetSelectedEquipment. That raise, from inside ActorComponent.OnEnterPlay:132-134, IS " +
                             "the keyless window's observed symptom; if the game moved it, re-derive the window " +
                             "against whatever replaced it rather than assuming it closed";
            if (!Reaches(doEnterPlay, onEnterPlay, game) || !Reaches(doEnterPlay, processInstance, game))
                yield return "L339 premise-changed: ActorComponent.DoEnterPlay no longer runs both " +
                             "ProcessInstanceData (which stamps GeoUnitId) and OnEnterPlay (which raises the " +
                             "listeners) inside its own body. The whole shape of this law — mint after, suppress " +
                             "during — is derived from those two living in there together";

            // ═══ (f) THE TWO SURFACES ARE STILL TWO ═══
            var result = surfaces.GetField("TacResult", All);
            var command = surfaces.GetField("TacCommand", All);
            if (result == null || command == null || Equals(result.GetValue(null), command.GetValue(null)))
                yield return "L339 premise-changed: SurfaceIds.TacResult and SurfaceIds.TacCommand are no longer " +
                             "two distinct surfaces. The spawn record rides the first and every enter-play capture " +
                             "seam rides the second, and it is precisely because they are TWO seq streams that " +
                             "nothing about an arrival may be emitted before its own spawn. Merge them and this " +
                             "law's reasoning has to be redone, not assumed";

            // ═══ (g) NOT ARMED AT BATTLE START ═══
            if (!Reaches(hold, scopeBuilt, mod))
                yield return "L339 window-swallows-the-mission-entry: HoldMidBattleEnterPlay no longer gates on " +
                             "TacticalActorKey.Built. Before the map is built the selection seam PENDS its raise " +
                             "and BuildBattleKeys flushes it the instant the key exists; silencing the seam there " +
                             "means nothing is ever pended and every soldier's mission-entry weapon selection is " +
                             "lost for good (L186)";

            // ═══ (h) THE CLIENT TAKES THE HOST'S NUMBER ═══
            if (!Reaches(applySpawn, adopt, mod))
                yield return "L339 spawn-key-dropped: ApplySpawn rebuilds the host's actor without adopting the " +
                             "key that came with it. The rebuilt actor then carries a local identity no other peer " +
                             "can name: every host record about it is refused here and every order this peer sends " +
                             "about it is refused there — an actor alive on both screens and addressable on " +
                             "neither";
        }

        /// <summary>The outcome, run for real on the live key table, once as the HOST and once as the peer that
        /// ADOPTS. Not an iterator: the table is process-global, so the restore has to be a finally.
        ///
        /// The actors are uninitialized instances (same idiom as L112): a real one needs a Unity scene the
        /// harness has no business building, and every member this exercises — <c>GeoUnitId</c>'s backing
        /// field, <c>UnityEngine.Object.GetHashCode</c> — is a plain field read. ONE actor per phase, with a
        /// <c>Reset</c> between them, deliberately: two would collide in the by-actor dictionary (both carry
        /// instance id 0) and reach <c>UnityEngine.Object.Equals</c>, which is an ECall and cannot run
        /// outside the player.</summary>
        private static IEnumerable<string> BothDirections(MethodInfo reset, FieldInfo built, FieldInfo nextDerived,
                                                          MethodInfo assign, MethodInfo adopt, MethodInfo resolve)
        {
            var found = new List<string>();
            try
            {
                reset.Invoke(null, null);
                built.SetValue(null, true);   // the battle-key map exists: this is the mid-battle window
                var hostActor = (TacticalActorBase)FormatterServices.GetUninitializedObject(typeof(TacticalActor));

                int key = (int)assign.Invoke(null, new object[] { hostActor });
                if (key == 0)
                    found.Add("L339 arrival-unkeyed: AssignHostKey answered 0 for an actor entering play after " +
                              "the battle-key map was built. 0 is 'no shared identity' — nothing this peer sends " +
                              "about that actor can name it, and nothing another peer sends can reach it");
                object[] hostArgs = { null, key, null };
                var backOnHost = resolve.Invoke(null, hostArgs);
                if (!ReferenceEquals(backOnHost, hostActor))
                    found.Add("L339 arrival-not-addressable-on-the-host: the host minted key " + key + " for an " +
                              "actor that arrived after the build and then could not resolve that very key back " +
                              "to it (" + Why(hostArgs) + "). This is the client->host half verbatim: every order " +
                              "a peer sends about that actor is answered 'no such actor on the host'");

                int counterAfterMint = (int)nextDerived.GetValue(null);

                // ─── the SAME number, on a peer that only ever heard it ───
                reset.Invoke(null, null);
                built.SetValue(null, true);
                int counterBeforeAdopt = (int)nextDerived.GetValue(null);
                var peerActor = (TacticalActorBase)FormatterServices.GetUninitializedObject(typeof(TacticalActor));
                adopt.Invoke(null, new object[] { peerActor, key });
                object[] peerArgs = { null, key, null };
                var backOnPeer = resolve.Invoke(null, peerArgs);
                if (!ReferenceEquals(backOnPeer, peerActor))
                    found.Add("L339 arrival-not-addressable-on-the-adopting-peer: a peer took the host's key " +
                              key + " for a mid-battle arrival and still cannot resolve it (" + Why(peerArgs) +
                              "). This is the host->client half verbatim: every settle, hit, death and command " +
                              "the host sends about that actor is refused here, so it fights on one screen alone");
                if ((int)nextDerived.GetValue(null) != counterBeforeAdopt)
                    found.Add("L339 adoption-moved-the-counter: adopting the host's key consumed an ordinal. A " +
                              "key is GIVEN, not minted — a peer that adopts BEFORE its own BuildBattleKeys would " +
                              "then start its ordinals one lower than the host's, so every derived key in the " +
                              "battle names a DIFFERENT actor on the two peers and nothing says so");
                if (counterAfterMint == counterBeforeAdopt)
                    found.Add("L339 mint-did-not-consume: AssignHostKey handed out a key without moving the " +
                              "shared counter, so the next arrival is minted the same number and two actors " +
                              "answer to one key");
            }
            catch (Exception ex)
            {
                // A RED LINE, NEVER A CRASH. Invoking production methods by a hardcoded argument array makes
                // this arm a tripwire on their signatures (Program.Add's own second-order lesson); a crash
                // aborts the whole run and proves nothing, while this names exactly what moved.
                found.Add("L339 premise-changed: executing the arrival key table threw " + ex.GetType().Name +
                          " (" + (ex.InnerException == null ? ex.Message : ex.InnerException.Message)
                              .Replace("\r", "").Replace("\n", " ") + "). The OUTCOME arm — the one that proves " +
                          "a mid-battle arrival is addressable on the host AND on a peer that adopted its key — " +
                          "did not run, so it is UNCHECKED. Re-derive it against whatever TacticalActorKey's " +
                          "AssignHostKey/Adopt/Resolve/Reset look like now");
            }
            finally
            {
                try { reset.Invoke(null, null); } catch { }
            }
            return found;
        }

        private static string Why(object[] args) =>
            args[2] == null ? "and gave NO reason, which is the empty-refusal shape that hid a mission-long " +
                              "divergence once already" : "it says: " + args[2];

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null &&
            Program.Callees(from, asm).Any(c => c.MetadataToken == target.MetadataToken &&
                                                c.Module == target.Module);
    }
}
