using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L140 — A MIRRORED TARGET IS RESOLVED, NEVER APPROXIMATED: everything the host's target NAMES, the
    /// receiving peer names too, or the order is refused rather than half-played.
    ///
    /// WHY THIS IS A LAW AND NOT A BOUNDS CHECK — the 2026-08-06 investigation nearly shipped the opposite.
    /// The crashing shot's target carried <c>PositionToApply = (-806.8, 66.4, -615.6)</c> on a map whose tiles
    /// are ±20, and that reads exactly like a raw free-aim ray point being mirrored as if it were a
    /// destination. It is not a defect. A free-aim shot's <c>PositionToApply</c> IS a far-off ray point by
    /// construction, the HOST played that identical value natively with no incident, and the host's own engine
    /// line for the same activation is <c>Parameter: &lt;AbilityTarget: Soldier_4&gt;</c> —
    /// <c>TacticalAbilityTarget.ToString</c>:246-249 prints <c>Actor</c> BEFORE <c>PositionToApply</c>, so the
    /// resolved actor was present all along and already rides
    /// (<see cref="TacAbilityTargetCodec.BitActor"/>). Refusing on distance would refuse every legal free-aim
    /// shot in the game. Arm (b) is that trap, written down and executed, so the next reader cannot re-derive
    /// the wrong fix from the same log line.
    ///
    /// WHAT MUST ACTUALLY HOLD. Every actor-shaped field crosses as a SHARED KEY, never as a position or a
    /// transform (<c>Write</c> goes through <c>TacticalActorKey.Of</c>, <c>Read</c> through
    /// <c>TacticalActorKey.Resolve</c>), and <c>Read</c> collects a sentence for each key that did not resolve
    /// HERE. If any did not, the target this peer would rebuild is not the target the host shot at, and
    /// replaying it is worse than not replaying it — because <c>TacticalAbilityTarget.GetWorkingPosition</c>
    /// :175-192 does not fail loudly. It walks a nine-step fallback chain (item aim point, damage-receiver aim
    /// point, equipment aim point, scene object, grid position, target-actor aim point, inventory, container,
    /// cone tip) and only then returns <c>InvalidPosition</c> — NaN — with one engine error. The crash log
    /// carries that tail four times over. So the verdict is taken ONCE, up front, as a pure decision, and the
    /// damage still arrives authoritatively on 0x84 either way: what a refusal costs is this peer's animation,
    /// which is the cheap half.
    ///
    /// Arm (a) executes the shipped verdict over the whole grid. Arm (b) is the anti-over-correction case, fed
    /// the real free-aim shot. Arm (c) is structural: the three actor-shaped fields must each have a declared
    /// bit AND be inside <c>KnownBits</c> — a build that dropped one would not merely lose the field, it would
    /// take the <c>mask &amp; ~KnownBits</c> throw at <c>Read</c> (the codec refuses to guess at a misaligned
    /// payload), so the two must move together. Arm (d) keeps the verdict from becoming decorative.
    ///
    /// DECLARED LIMIT: the codec itself is not round-tripped here. <c>Write</c> needs a live
    /// <c>TacticalActorBase</c> to key and <c>Read</c> needs a <c>TacticalLevelController</c> to resolve
    /// against, and neither can be built in a console host — so "the actor survives the wire" is asserted as
    /// the bit set plus the two key calls, not as a byte round trip. L131/L137 arm (b) round-trip the settle
    /// codec because that one is made of primitives; this one is made of scene objects.
    ///
    /// Falsify: make <c>CommandMustBeRefused</c> return true always → <c>L140 legal-free-aim-shot-refused</c>
    /// (quoting the real (-806.8, 66.4, -615.6) case); return false always → <c>L140
    /// unresolved-target-is-played</c> and <c>L140 unkeyed-actor-is-played</c>; remove
    /// <c>BitActor</c> from <c>KnownBits</c> → <c>L140 actor-field-not-decodable</c>; drop the
    /// <c>TacticalActorKey.Of</c> call from <c>Write</c> → <c>L140 actor-shipped-unkeyed</c>; stop consulting
    /// the verdict in <c>ApplyActivate</c> → <c>L140 verdict-is-decorative</c>.
    /// </summary>
    internal static class L140_MirroredTargetIsResolvedNotApproximated
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var codec = typeof(TacAbilityTargetCodec);
            var refused = cmd.GetMethod("CommandMustBeRefused", All);
            var applyActivate = cmd.GetMethod("ApplyActivate", All);
            var write = codec.GetMethod("Write", All);
            var read = codec.GetMethod("Read", All);
            if (refused == null || applyActivate == null || write == null || read == null)
            {
                yield return "L140 premise-changed: TacticalCommandSync.{CommandMustBeRefused,ApplyActivate} or " +
                             "TacAbilityTargetCodec.{Write,Read} no longer resolves. Nothing else decides " +
                             "whether a mirrored order may be replayed against a target this peer could not " +
                             "name — re-read this law before assuming something does.";
                yield break;
            }

            var mod = cmd.Assembly;

            // ── (a) THE VERDICT, over the whole grid ─────────────────────────
            if (!TacticalCommandSync.CommandMustBeRefused(false, 0))
                yield return "L140 unkeyed-actor-is-played: a command whose ACTOR key did not resolve on this " +
                             "peer is replayed anyway. There is no actor to play it on; what follows is a null " +
                             "deref or an ability played on the wrong soldier.";
            foreach (var n in new[] { 1, 2, 7 })
            {
                if (!TacticalCommandSync.CommandMustBeRefused(true, n))
                    yield return "L140 unresolved-target-is-played: a command carrying " + n + " target field(s) " +
                                 "this peer could not resolve is replayed anyway. GetWorkingPosition:175-192 " +
                                 "then walks its nine-step fallback and returns NaN, which is the " +
                                 "'Trying to get working position from TacticalAbilityTarget that has no valid " +
                                 "one set' tail that preceded the native crash.";
                if (!TacticalCommandSync.CommandMustBeRefused(false, n))
                    yield return "L140 both-failures-not-refused: neither the actor nor " + n + " target field(s) " +
                                 "resolved and the order is still replayed.";
            }

            // ── (b) THE TRAP: the real free-aim shot must PLAY ───────────────
            // Actor resolved, nothing unresolved. Its PositionToApply is (-806.8, 66.4, -615.6) — a legal
            // free-aim ray point ~800 units off a ±20 map, taken verbatim from the host's log for the shot
            // whose mirror preceded the crash. Refusing this is refusing free aim.
            if (TacticalCommandSync.CommandMustBeRefused(true, 0))
                yield return "L140 legal-free-aim-shot-refused: a mirrored order whose actor resolved and whose " +
                             "every target field resolved is being refused. The shot that motivated this law is " +
                             "exactly that shape — Soldier_6's ShootAbility naming Soldier_4, with " +
                             "PositionToApply (-806.8, 66.4, -615.6) because free aim's position IS a far ray " +
                             "point. Refusing on the position refuses every free-aim shot in the game, and the " +
                             "host played that identical target natively without incident.";

            // ── (c) the actor-shaped fields must be shippable AND decodable ──
            foreach (var bit in new[]
                     {
                         new KeyValuePair<string, ushort>("Actor", TacAbilityTargetCodec.BitActor),
                         new KeyValuePair<string, ushort>("ShootTargetActor", TacAbilityTargetCodec.BitShootTargetActor),
                         new KeyValuePair<string, ushort>("DamageReceiver", TacAbilityTargetCodec.BitDamageReceiver),
                     })
            {
                if (bit.Value == 0)
                    yield return "L140 actor-field-has-no-bit: TacticalAbilityTarget." + bit.Key + " has no " +
                                 "declared codec bit, so it cannot ride at all and every peer resolves that " +
                                 "field for itself — a shot at nobody.";
                else if ((TacAbilityTargetCodec.KnownBits & bit.Value) == 0)
                    yield return "L140 actor-field-not-decodable: TacticalAbilityTarget." + bit.Key + " has a " +
                                 "bit that KnownBits does not cover. Read:675-679 throws on any bit outside " +
                                 "KnownBits rather than guess at a misaligned payload, so this is not a lost " +
                                 "field — it is every activation carrying it refused at the reader.";
            }
            if (!Program.Callees(write, mod).Any(c => c.Name == "Of" &&
                                                      c.DeclaringType == typeof(TacticalActorKey)))
                yield return "L140 actor-shipped-unkeyed: TacAbilityTargetCodec.Write no longer reaches " +
                             "TacticalActorKey.Of. An actor addressed any other way is a session-local handle, " +
                             "which is the v1 arbiter's death (law 2) and resolves to nothing on the far side.";
            // Read resolves through its OWN one-line helper (ResolveActor, which is also what appends the
            // unresolved sentence), so this arm follows that one hop rather than demanding a direct call —
            // requiring directness would go RED on the shipped, correct shape.
            var readCallees = Program.Callees(read, mod).ToList();
            var resolveActor = codec.GetMethod("ResolveActor", All);
            bool resolvesDirectly = readCallees.Any(c => c.Name == "Resolve" &&
                                                         c.DeclaringType == typeof(TacticalActorKey));
            bool resolvesViaHelper = resolveActor != null &&
                                     readCallees.Any(c => c.MetadataToken == resolveActor.MetadataToken) &&
                                     Program.Callees(resolveActor, mod)
                                            .Any(c => c.Name == "Resolve" &&
                                                      c.DeclaringType == typeof(TacticalActorKey));
            if (!resolvesDirectly && !resolvesViaHelper)
                yield return "L140 actor-read-unresolved: TacAbilityTargetCodec.Read no longer reaches " +
                             "TacticalActorKey.Resolve, directly or through its own ResolveActor helper, so " +
                             "nothing decides whether the key names anything HERE and the unresolved list " +
                             "arm (a) depends on is empty by construction.";

            // ── (d) the verdict must actually be the one ApplyActivate takes ──
            if (!Program.Callees(applyActivate, mod).Any(c => c.MetadataToken == refused.MetadataToken))
                yield return "L140 verdict-is-decorative: ApplyActivate no longer consults CommandMustBeRefused, " +
                             "so arms (a) and (b) execute a decision nothing makes and the real refusal is " +
                             "whatever got inlined at the call site.";
        }
    }
}
