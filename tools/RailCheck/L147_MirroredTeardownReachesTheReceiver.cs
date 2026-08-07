using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L147 — A VISUAL A STATUS OWNS IS TORN DOWN ON THE RECEIVING PEER, NOT ONLY ON THE ONE THAT ACTED.
    ///
    /// THE REPORT: a soldier goes on overwatch, the red cone appears; the overwatch FIRES; on the host the cone
    /// vanishes, on the client it hangs in the air for the rest of the battle. Overwatch firing is vanilla, so
    /// the question is not why vanilla failed — it is which vanilla edge the mirror path never delivers. Two
    /// answers, and they are the same defect at two heights: THE MIRRORED STATUS IS NOT THE STATUS THE HOST
    /// HAS, and NOTHING SHIPS THE MOMENT IT CHANGED.
    ///
    /// (1) THE STATUS ARRIVED WITHOUT ITS SOURCE. The game applies overwatch as
    /// <c>Status.ApplyStatus(OverwatchAbilityDef.OverwatchStatus, OverwatchWeapon.ItemDef)</c>
    /// (<c>OverwatchAbility</c>:89) and the status READS that source back:
    /// <c>OverwatchStatus.GetWeapon</c>:59-68 casts <c>Source</c> to a <c>WeaponDef</c> and finds the live
    /// weapon by it. <c>TacticalStatusSet.ApplyOne</c> rebuilt the status with
    /// <c>Instantiate</c> + <c>ApplyStatus(status)</c> and no source at all, so <c>GetWeapon()</c> returned
    /// null and <c>OnApply</c>:38 NRE'd at <c>GetWeapon().Detached +=</c> — three times in the 2026-08-06
    /// client log (22:31:53 Soldier_1, 22:32:45 Brainrot, 22:32:48 Strife), each one caught and logged by the
    /// mod's own guard. THE TEARDOWN IS THE SAME NULL ONE LINE EARLIER: <c>OnUnapply</c>:51-54 runs
    /// <c>GetWeapon().Detached -=</c> BEFORE <c>SetCone(null)</c>, so on a peer holding a source-less copy the
    /// unapply throws and the cone's <c>Destroy</c> (:96-102) is never reached. Generic, not about overwatch:
    /// any status whose lifecycle reads its own source is half-applied and never fully unapplied.
    ///
    /// (2) NOTHING SHIPPED THE MOMENT THE HOST DROPPED IT. The status set rides only on the 0x82 settle, and
    /// <c>TacticalCommandSync.OnAbilityActionEnded</c> emits one only for a declared RIDER — i.e. for an
    /// ORDER. The overwatch shot is not an order: <c>TacticalLevelController.ExecuteOverwatch</c>:1353-1391 is
    /// started by <c>TriggerOverwatch</c> off the VICTIM's navigation, and it is that coroutine — :1381
    /// <c>SetConeVisualsMode(false,false)</c>, :1387 <c>UnapplyStatus(overwatch)</c> — that owns the teardown.
    /// The receiving peer cannot run it either, by design: law L83 blocks a client's own reaction ("a local
    /// Overwatch reaction was BLOCKED here", client log 22:31:49) because raising it would be a second shot
    /// from one actor. So the shot arrived, the damage arrived, and the removal that destroys the cone arrived
    /// on nothing at all until the next turn-edge sweep.
    ///
    /// ONE SEAM FOR (2), AND IT IS GENERIC BY CONSTRUCTION: the host settles an actor whenever its status set
    /// CHANGES, patched at <c>StatusComponent.CallApplyStatus</c>/<c>CallUnapplyStatus</c> — the two private
    /// calls where <c>OnApply</c>/<c>OnUnapply</c> actually run, and the only funnel every route reaches. The
    /// public <c>UnapplyStatus</c>:192 may merely queue into <c>_statusesToRemove</c> and let
    /// <c>ApplyStatus</c>:249 do it later, and a TIMED status expires straight out of
    /// <c>UpdateStatuses</c>:256-262 without touching either public method — arm (c) is why the seam is the
    /// private pair and not the obvious public one. Nothing about overwatch appears anywhere in it
    /// (postulate 3): a reaction, an effect, an expiry and a modded status all take the same road.
    ///
    /// REACTIVITY (postulate 1) is then the GAME's: the receiving peer's <c>Reconcile</c> calls
    /// <c>comp.UnapplyStatus</c>, whose <c>OnUnapply</c> destroys the cone GameObjects in that same frame.
    /// No hand-rolled visual poke, no per-status code.
    ///
    /// WHAT THIS LAW CANNOT ASSERT: that a Unity GameObject was destroyed. There is no cone, no prefab and no
    /// frame in a console host. What it CAN assert, and does, is that the two things which made the teardown
    /// unreachable are gone — the identity now carries the source and the codec round-trips it (arms (a),(b)),
    /// and a status change is a settle (arm (c)) — and it says so rather than implying more.
    ///
    /// Falsify: stop writing the source into the key (<c>Key(defName, refKey, "")</c> in <c>KeyOf</c>) →
    /// <c>L147 source-is-not-part-of-the-identity</c>; drop the <c>status.Source =</c> assignment from
    /// <c>ApplyOne</c> → <c>L147 rebuilt-status-has-no-source</c>; drop the source field from the codec →
    /// <c>L147 codec-drops-the-source</c>; delete the <c>HostSettle</c> call from the status-change postfix, or
    /// point it back at the public <c>ApplyStatus</c>/<c>UnapplyStatus</c> pair →
    /// <c>L147 status-change-does-not-settle</c> / <c>L147 settle-seam-misses-the-deferred-routes</c>.
    /// </summary>
    internal static class L147_MirroredTeardownReachesTheReceiver
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        // The overwatch pair the report is about, as the rail mints it, plus the same status from a different
        // weapon: with the source out of the identity these two are INDISTINGUISHABLE, which is the bug.
        // "<refKey>@<defName>|<sourceDefName>|<targetTag>" — the trailing field is the status's own Target,
        // which joined the identity after Bleed_StatusDef NRE'd five times rebuilt without one.
        private const string OwPdw = "0@Overwatch_StatusDef|PDW_WeaponDef|";
        private const string OwRifle = "0@Overwatch_StatusDef|AssaultRifle_WeaponDef|";

        internal static IEnumerable<string> Check()
        {
            var set = typeof(TacticalStatusSet);
            var key = set.GetMethod("Key", All, null,
                                    new[] { typeof(string), typeof(int), typeof(string), typeof(string) }, null);
            var keyOf = set.GetMethod("KeyOf", All);
            var applyOne = set.GetMethod("ApplyOne", All);
            var settleGate = set.Assembly.GetType("Multiplayer.Tactical.StatusChangeSettlesTheActor");
            var postfix = settleGate?.GetMethod("Postfix", All);
            var targets = settleGate?.GetMethod("TargetMethods", All);
            if (key == null || keyOf == null || applyOne == null || postfix == null || targets == null)
            {
                yield return "L147 premise-changed: TacticalStatusSet.{Key,KeyOf,ApplyOne} or the status-change " +
                             "settle no longer resolves. Those are the only two things carrying a status's " +
                             "source and the moment it changed — re-read this law before assuming a visual a " +
                             "status owns still disappears on the peers that did not act.";
                yield break;
            }

            // ── (a) THE SOURCE IS PART OF THE IDENTITY ────────────────────────
            if (string.Equals(TacticalStatusSet.Key("Overwatch_StatusDef", 0, "PDW_WeaponDef"),
                              TacticalStatusSet.Key("Overwatch_StatusDef", 0, "AssaultRifle_WeaponDef"),
                              StringComparison.Ordinal))
                yield return "L147 source-is-not-part-of-the-identity: two overwatch statuses applied from " +
                             "DIFFERENT weapons mint the same key. The plan then calls them interchangeable, so " +
                             "a peer holding the wrong one is never corrected — and the wrong one is exactly the " +
                             "one whose GetWeapon() finds nothing and whose OnUnapply throws before SetCone(null).";
            {
                // …and it must actually reconcile: wrong source in, right source out.
                var apply = new List<string>();
                var unapply = new List<string>();
                TacticalStatusSet.Plan(new List<string> { OwRifle }, new List<string> { OwPdw }, apply, unapply);
                if (!unapply.Contains(OwRifle) || !apply.Contains(OwPdw))
                    yield return "L147 wrong-source-survives-the-plan: a peer holding the overwatch status from " +
                                 "the wrong weapon is planned " + unapply.Count + " removal(s) and " + apply.Count +
                                 " apply(s), which does not replace it. A status is only as good as the source " +
                                 "its own OnApply/OnUnapply read back.";
            }

            // ── (b) THE CODEC CARRIES IT ──────────────────────────────────────
            {
                var sent = new List<string> { OwPdw, "0@Panic_StatusDef||" };
                List<string> got = null;
                string threw = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) TacticalStatusSet.Write(w, sent);
                        ms.Position = 0;
                        using (var r = new BinaryReader(ms, Encoding.UTF8, true)) got = TacticalStatusSet.Read(r);
                    }
                }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null)
                    yield return "L147 codec-throws: writing and reading a status set with a source threw (" +
                                 threw + "). It rides inside the 0x82 settle, so a throw here is not one lost " +
                                 "status — it is every field behind it in the same message.";
                else if (got == null || !got.SequenceEqual(sent, StringComparer.Ordinal))
                    yield return "L147 codec-drops-the-source: a status set written as [" +
                                 string.Join(", ", sent.ToArray()) + "] reads back as [" +
                                 string.Join(", ", (got ?? new List<string>()).ToArray()) + "]. The receiving " +
                                 "peer then rebuilds a source-less status — OverwatchStatus.OnApply:38 NREs at " +
                                 "GetWeapon().Detached, and OnUnapply:53 NREs one line before SetCone(null), " +
                                 "which is why the cone can never be destroyed there.";
            }

            // ── (c) THE REBUILD ASSIGNS IT, AND A CHANGE SHIPS ────────────────
            var asm = set.Assembly;
            var status = typeof(Base.Entities.Statuses.Status);
            var sourceSetter = status.GetProperty("Source", All)?.GetSetMethod();
            if (sourceSetter == null || !Program.Callees(applyOne, status.Assembly)
                                                .Any(c => c.MetadataToken == sourceSetter.MetadataToken))
                yield return "L147 rebuilt-status-has-no-source: ApplyOne never assigns Status.Source, so every " +
                             "status this rail rebuilds is the source-less copy whose OnApply already threw " +
                             "three times in one live mission. Carrying the source on the wire and then not " +
                             "using it is the same defect with more bytes.";
            if (!Program.Callees(postfix, asm).Any(c => c.Name == "HostSettle"))
                yield return "L147 status-change-does-not-settle: a status appearing or disappearing on the host " +
                             "ships nothing. Then a teardown the host performs outside an ORDER — a reaction, an " +
                             "effect, an expiry — reaches the other peers only at the next turn-edge sweep, and " +
                             "the visual that status owns stays on their screens until then. The overwatch cone " +
                             "is one instance of that, not the class.";

            // The seam must be the pair where OnApply/OnUnapply actually run. The public methods miss the
            // deferred removal (UnapplyStatus:192 -> _statusesToRemove -> ApplyStatus:249) and the timed
            // expiry (UpdateStatuses:256-262) entirely.
            var bound = (targets.Invoke(null, null) as IEnumerable<MethodBase> ?? Enumerable.Empty<MethodBase>())
                        .Where(m => m != null).Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (bound.Count != 2 || bound[0] != "CallApplyStatus" || bound[1] != "CallUnapplyStatus")
                yield return "L147 settle-seam-misses-the-deferred-routes: the status-change settle binds [" +
                             string.Join(", ", bound.ToArray()) + "] instead of StatusComponent." +
                             "{CallApplyStatus,CallUnapplyStatus}. Those two are where OnApply/OnUnapply actually " +
                             "run and the only funnel every route reaches — the public UnapplyStatus:192 may " +
                             "merely queue the removal for ApplyStatus:249 to perform, and a timed status " +
                             "expires out of UpdateStatuses:256-262 touching neither public method. A seam that " +
                             "misses those is silent for exactly the removals nobody ordered, which is the " +
                             "whole subject of this law.";
        }
    }
}
