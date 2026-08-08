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
    /// L262 — WHO THIS ENEMY IS, IS THE HOST'S ANSWER, ON EVERY PEER.
    ///
    /// THE HOLE, and it is the same FIELD CLASS as L131 (statuses), L137 (traits), L186 (selection) and L242
    /// (per-turn uses): a piece of an actor that existed on each peer only as a side effect of that peer
    /// running some code. Here the code is TFTV's:
    /// <c>TFTVHumanEnemies.GiveRankAndNameToHumaoidEnemy</c>:1613 rolls a human enemy's RANK and NAME from
    /// <c>UnityEngine.Random.InitState((int)Stopwatch.GetTimestamp())</c> (:1631, :1651) at enter-play, and it
    /// ran on EVERY peer over that peer's own copy of the mirrored actor. Measured on a live 3-instance
    /// battle: host <c>Soldier_11 -&gt; Havara</c>, instance2 <c>Soldier_1 -&gt; Starbuck</c>, instance3
    /// <c>Soldier_1 -&gt; Dag</c> — one battlefield, three different elites. <c>TftvClientChampGuard</c>
    /// suppressed the re-roll, which made the peers agree to have NOTHING: right enemy, no name, no rank.
    ///
    /// IT IS NOT COSMETIC, for the same reason <c>HasEndedTurn</c> is not a label. TFTV postfixes
    /// <c>TacticalActorBase.get_DisplayName</c> (TFTVHumanEnemies.cs:851-871) with
    /// <c>__result = __instance.name + GetRankName(tags)</c>, so the actor's Unity NAME and its
    /// <c>HumanEnemy*</c> tags together ARE the enemy's identity everywhere the game shows one, and the tier
    /// tag also drives TFTV's healthbar class icon.
    ///
    /// TWO CARRIERS, BECAUSE THERE ARE TWO POPULATIONS. A MID-BATTLE spawn rides the 0x84 spawn record: TFTV
    /// rolls in <c>FinalizeEnterPlay</c> (<c>TacticalActorBase</c>:550), the last call inside
    /// <c>ActorComponent.DoEnterPlay</c>, so the host's postfix on that method already sees the result. A
    /// BATTLE-START human enemy is in no spawn record at all — its tags reach the client through the entry
    /// save (<c>TacActorBaseInstanceData.AdditionalGameTags</c>) and its NAME reaches it through nothing,
    /// because the name is not instance data in any form and <c>ActorSpawner</c>:17 regenerates it per peer
    /// as <c>&lt;prefab&gt;_&lt;NextSpawnedActorID&gt;</c>. Only the 0x82 settle's turn-edge sweep reaches
    /// those, and it re-asserts rather than retries.
    ///
    /// WHAT THIS LAW ASSERTS. Arm (a) is the codec, and it is the load-bearing one: the identity is the LAST
    /// field of the settle and the last field of the spawn record, so a one-sided change does not lose a name
    /// — it misaligns every field behind it for every actor in the message. The null case is the COMMON case
    /// (every actor that is not a TFTV human enemy, and every actor in a session without TFTV), so it is
    /// checked against a sentinel: a reader that stops early takes the rest of the message with it. Arms
    /// (b)-(d) are structural — a perfect codec that nothing calls converges nothing — and arm (e) is
    /// postulate 1, because nothing in the game repaints an identity a peer did not change itself.
    ///
    /// Falsify: drop the <c>Write</c> from <c>HostSettle</c> → <c>L262 settle-carries-no-identity</c>; drop
    /// the <c>Apply</c> from <c>ApplySettle</c> → <c>L262 settle-applies-no-identity</c>; drop either half
    /// from the spawn path → <c>L262 spawn-carries-no-identity</c> / <c>L262 spawn-applies-no-identity</c>;
    /// make <c>Write</c> skip the tag count for a null identity → <c>L262 absent-identity-misreads-the-stream</c>;
    /// drop the <c>MarkDirty</c> from <c>Apply</c> → <c>L262 renamed-enemy-stays-stale</c>.
    /// </summary>
    internal static class L262_ChampIdentityIsTheHostsOnEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var champ = typeof(TftvChampIdentity);
            var life = typeof(TacticalActorLifecycle);
            var cmd = typeof(TacticalCommandSync);
            var collect = champ.GetMethod("Collect", All);
            var write = champ.GetMethod("Write", All);
            var read = champ.GetMethod("Read", All);
            var apply = champ.GetMethod("Apply", All);
            var applySettle = cmd.GetMethod("ApplySettle", All);
            var applySpawn = life.GetMethod("ApplySpawn", All);
            if (collect == null || write == null || read == null || apply == null ||
                applySettle == null || applySpawn == null)
            {
                yield return "L262 premise-changed: TftvChampIdentity.{Collect,Write,Read,Apply} / " +
                             "TacticalCommandSync.ApplySettle / TacticalActorLifecycle.ApplySpawn no longer " +
                             "resolves. These are the only things that carry a TFTV human enemy's rolled rank " +
                             "and name between peers — re-read this law before assuming the three screens still " +
                             "show the same elite.";
                yield break;
            }

            // ── (a) the codec, both directions and both shapes ───────────────
            {
                var sent = new TftvChampIdentity.Identity
                {
                    Name = "Havara",
                    Tags = new List<string> { "HumanEnemyFaction_NJ_GameTagDef", "HumanEnemyTier_2_GameTagDef",
                                              "HumanEnemy_GameTagDef" }
                };
                TftvChampIdentity.Identity got = null;
                string threw = null;
                try { got = RoundTrip(sent, null); }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null)
                    yield return "L262 codec-throws: writing and reading a champ identity threw (" + threw +
                                 "). It is the LAST field of both the 0x82 settle and the 0x84 spawn record, so a " +
                                 "throw here does not lose a name — it aborts the message that carries the " +
                                 "position, the AP, the status set and the spawn itself.";
                else if (got == null || got.Name != sent.Name ||
                         !(got.Tags ?? new List<string>()).SequenceEqual(sent.Tags, StringComparer.Ordinal))
                    yield return "L262 codec-round-trip-lost: an identity written by the host reads back as '" +
                                 (got == null ? "<null>" : got.Name + "' [" +
                                  string.Join(", ", (got.Tags ?? new List<string>()).ToArray()) + "]") +
                                 " instead of '" + sent.Name + "' [" + string.Join(", ", sent.Tags.ToArray()) +
                                 "]. The two sides of this codec have diverged, which misaligns the stream itself.";

                // THE COMMON CASE IS NULL — every actor that is not a TFTV human enemy, and every actor in a
                // session with no TFTV at all. It must consume its bytes exactly like a full one.
                const int sentinel = 0x5EA2;
                bool clean;
                try { clean = RoundTrip(null, sentinel) == null; }
                catch { clean = false; }
                if (!clean)
                    yield return "L262 absent-identity-misreads-the-stream: an ABSENT identity does not round-trip " +
                                 "to null while leaving the stream exactly where it started. That is the common " +
                                 "case — nearly every actor in nearly every battle — so this desynchronises the " +
                                 "settle and spawn readers for every message rather than for an unusual one.";
            }

            // ── (b)-(d) it must CROSS and it must LAND, on both carriers ─────
            var asm = champ.Assembly;
            foreach (var v in MustReachInType(cmd, "Write", champ, asm,
                     "L262 settle-carries-no-identity: HostSettle no longer writes the champ identity. The " +
                     "turn-edge sweep is the ONLY carrier that reaches a BATTLE-START human enemy — no spawn " +
                     "record ever named it, and its name is in no save — so without it every peer but the host " +
                     "shows that enemy under a locally generated <prefab>_<id> (ActorSpawner:17).")) yield return v;
            foreach (var v in MustReachInType(life, "Write", champ, asm,
                     "L262 spawn-carries-no-identity: the 0x84 spawn record no longer writes the champ identity. " +
                     "A mid-battle spawn is rebuilt from a def with a NULL instance-data template, so it inherits " +
                     "neither the rolled name nor the HumanEnemy* tags from anywhere else.")) yield return v;
            if (!Program.Callees(applySettle, asm).Any(c => c.MetadataToken == apply.MetadataToken))
                yield return "L262 settle-applies-no-identity: ApplySettle reads the host's identity and does " +
                             "nothing with it. Reading it is not the point — this is the only correction a " +
                             "battle-start human enemy ever gets.";
            if (!Program.Callees(applySpawn, asm).Any(c => c.MetadataToken == apply.MetadataToken))
                yield return "L262 spawn-applies-no-identity: ApplySpawn reads the host's identity and does " +
                             "nothing with it, so a mid-battle reinforcement keeps this peer's own engine name.";

            // ── (e) postulate 1 ─────────────────────────────────────────────
            if (!Program.Callees(apply, asm).Any(c => c.Name == "Repaint" && c.DeclaringType == champ))
                yield return "L262 renamed-enemy-stays-stale: TftvChampIdentity.Apply changes the actor's name and " +
                             "tags without repainting. Nothing in the game watches GameTagsList.Changed on the " +
                             "tactical side (its only subscribers are AddonsManager:103 and geoscape sites) and " +
                             "TFTV sets the healthbar rank icon ONCE at InitHealthbar, so the screen watching this " +
                             "enemy keeps the old identity for as long as it stays open.";
            var repaint = champ.GetMethod("Repaint", All);
            if (repaint != null && !Program.Callees(repaint, asm).Any(c => c.Name == "MarkDirty" &&
                                                                          c.DeclaringType == typeof(TacticalUiRepaint)))
                yield return "L262 renamed-enemy-stays-stale: the repaint no longer marks the tactical UI dirty, " +
                             "which is the only thing that re-enters the open ability-bar state so its target " +
                             "icons re-read this actor.";
        }

        private static TftvChampIdentity.Identity RoundTrip(TftvChampIdentity.Identity id, int? sentinel)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    TftvChampIdentity.Write(w, id);
                    if (sentinel.HasValue) w.Write(sentinel.Value);
                }
                ms.Position = 0;
                using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                {
                    var got = TftvChampIdentity.Read(r);
                    // The reader that ate bytes which were not its own is the failure this exists to catch, so
                    // it is reported as "did not round-trip" rather than silently passing.
                    if (sentinel.HasValue && r.ReadInt32() != sentinel.Value)
                        return new TftvChampIdentity.Identity { Name = "<stream desynchronised>" };
                    return got;
                }
            }
        }

        /// <summary>Both writers hand their body to <c>Send</c> as a lambda, so the call lives in a
        /// compiler-generated closure rather than in the named method (the same reason L131 arm (c) and L137
        /// are written this way). The honest question is "does this rail type write the identity anywhere",
        /// and deleting the call still answers no.</summary>
        private static IEnumerable<string> MustReachInType(Type owner, string callee, Type calleeOwner,
                                                           Assembly asm, string violation)
        {
            foreach (var t in new[] { owner }.Concat(owner.GetNestedTypes(All)))
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                   .Concat(t.GetConstructors(All)))
                    if (Program.Callees(m, asm).Any(c => c.Name == callee && c.DeclaringType == calleeOwner))
                        yield break;
            yield return violation;
        }
    }
}
