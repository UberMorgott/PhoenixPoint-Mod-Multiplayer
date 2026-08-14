using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L505 — A DERIVED MEMBER WHOSE SETTER MUTATES A SIBLING NEVER RIDES THE WIRE.
    ///
    /// THE FAILURE. <c>ModifiableValue</c> crosses as ONE Composite leaf carrying all three of its members —
    /// <c>BaseValue</c>, <c>EndValue</c>, <c>ModificationValue</c> — written in ORDINAL name order
    /// (RailTypes.Build, the <c>OrderBy(e =&gt; e.name, StringComparer.Ordinal)</c> that fixes the wire ids).
    /// But <c>EndValue</c> has NO storage: <c>get =&gt; BaseValue + ModificationValue</c> and
    /// <c>set =&gt; BaseValue = value - ModificationValue</c> (decompile ModifiableValue.cs:14-24). So the
    /// decode writes <c>BaseValue</c>, then writes <c>EndValue</c> — which RE-DERIVES <c>BaseValue</c> from
    /// whatever <c>ModificationValue</c> the box holds AT THAT MOMENT, and that is 0: <c>DecodeLeaf</c>'s
    /// Composite arm builds a FRESH box and writes <c>ModificationValue</c> LAST. The receiver's
    /// <c>BaseValue</c> therefore comes out equal to the SENDER'S END VALUE — inflated by exactly the
    /// modification — on EVERY walk that ships the stat, not only when the modification moves. Measured by
    /// arm (d): {Base=10, Mod=5} arrives as {Base=15, Mod=5}. Zero-modification stats hide it entirely, which
    /// is why it survived — every augmented, buffed or equipment-modified stat mirrors wrong, and the error
    /// PERSISTS because the next walk reads the corrupted value back as the new truth.
    ///
    /// THE RULE, stated over the SHAPE and not over the one type that has it: a rail table member that is a
    /// PROPERTY whose setter stores into a SIBLING field of the same type is DERIVED, and mirroring it writes
    /// state it was not asked to write. It must be an explicit opt-out (<c>RailMeta._optOutMembers</c>, i.e.
    /// <c>RailField.OptedOut</c>), and the members it derives FROM ride instead — the game recomputes it on
    /// every read. Same principle as the derived pose (L-series GeoVehicle SurfacePos/SurfaceRot/Rot) and the
    /// mist-repeller radius (L483): what the client can recompute is not state to mirror. Here it is stronger
    /// than a churn argument — the mirrored write is arithmetically WRONG, not merely redundant.
    ///
    /// THE ARMS:
    ///   (0) <c>premise-changed</c> — <c>ModifiableValue</c> still has the shape the scan is calibrated on:
    ///       an <c>EndValue</c> property whose setter writes the <c>BaseValue</c> FIELD.
    ///   (a) <c>derived-rides</c> — the GENERIC sweep. Every Composite-leaf value type in the game assembly
    ///       is classified; any table member whose property setter writes a sibling field must be
    ///       <c>OptedOut</c>. This is the arm that catches the NEXT such member, not just this one.
    ///   (b) <c>not-recomputed</c> — <c>EndValue</c>'s GETTER reads BOTH sibling fields, so a peer that never
    ///       receives it still reads the right number. If the getter stopped deriving, the value would be
    ///       real storage and would have to ride.
    ///   (c) <c>still-composite</c> — opting a member out must not drop the struct out of
    ///       <c>LeafKind.Composite</c> altogether (that would stop stats mirroring at all, not fix them), and
    ///       the surviving independent members must still ride.
    ///   (d) <c>codec-ships-optout</c> — the Composite codec HONOURS the exclusion:
    ///       <c>WireFieldCount</c> is short by exactly the opted-out members. Before this law the composite
    ///       codec walked <c>Fields</c> raw, so an opt-out row on a struct member was inert.
    ///
    /// Falsify: delete the ModifiableValue.EndValue opt-out row → (a); make the codec write every field
    /// again → (d); make EndValue a stored field → (0)/(b); exclude BaseValue or ModificationValue too → (c).
    /// </summary>
    internal static class L505_ADerivedSetterNeverRidesTheWire
    {
        internal static IEnumerable<string> Check(Assembly game)
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;

            var mv = typeof(Base.Entities.Statuses.ModifiableValue);
            var endValue = mv.GetProperty("EndValue", Any);
            var baseValue = mv.GetField("BaseValue", Any);
            var modValue = mv.GetField("ModificationValue", Any);

            // (0) The calibration case. Everything below is a sweep, and a sweep that no longer contains a
            // known positive is a sweep nobody would notice going empty.
            if (endValue == null || baseValue == null || modValue == null ||
                !WritesSibling(endValue.GetSetMethod(true), mv, out _))
            {
                yield return "L505 premise-changed: ModifiableValue no longer exposes an EndValue property " +
                             "whose setter writes the BaseValue/ModificationValue fields (decompile " +
                             "ModifiableValue.cs:14-24). The derived-setter hazard this law sweeps for is " +
                             "defined by that shape — recalibrate the law before trusting a green run.";
                yield break;
            }

            // (a) THE SWEEP. Every value type the rail carries as one Composite leaf, not just this one.
            int composites = 0, examined = 0;
            bool sawEndValue = false;
            foreach (var t in ValueTypes(game))
            {
                if (!RailMeta.LeafKindOf(t, out var kind) || kind != LeafKind.Composite) continue;
                var table = RailType.Get(t);
                if (table == null) continue;
                composites++;

                foreach (var f in table.Fields)
                {
                    var setter = f.Pi?.GetSetMethod(true);
                    if (setter == null || !WritesSibling(setter, t, out var sibling)) continue;
                    examined++;
                    if (t == mv && f.Name == "EndValue") sawEndValue = true;
                    if (f.OptedOut) continue;

                    yield return "L505 derived-rides: " + t.FullName + "." + f.Name + " is a DERIVED member — " +
                                 "its setter writes the sibling field '" + sibling + "' — yet it rides as '" +
                                 f.Class + "'. Mirroring it does not carry its own value, it REWRITES the " +
                                 "member it is derived from, against whatever the receiver still holds for the " +
                                 "other input (composite members are written in ordinal name order, so at " +
                                 "least one input is always stale). Opt it out in RailMeta._optOutMembers and " +
                                 "let the game recompute it from the members that do ride.";
                }
            }

            // Positive control on the sweep itself: it must have classified real composites AND found the
            // calibration case. Either at zero means the arms above ran over nothing.
            if (composites == 0 || !sawEndValue)
                yield return "L505 vacuous: the sweep classified " + composites + " Composite leaf types and " +
                             (sawEndValue ? "found" : "did NOT find") + " ModifiableValue.EndValue among the " +
                             examined + " derived-setter members it examined — with the known positive missing " +
                             "the sweep asserts nothing about the ones it did not see.";

            // (b) Nothing is lost by dropping it: the getter re-derives from both inputs on every read.
            var getter = endValue.GetGetMethod(true);
            if (!ReadsField(getter, baseValue) || !ReadsField(getter, modValue))
                yield return "L505 not-recomputed: ModifiableValue.EndValue's getter no longer reads BOTH " +
                             "BaseValue and ModificationValue, so it is not a pure derivation any more. A peer " +
                             "that never receives the member would then show a stale number and the value " +
                             "would have to ride again — with an ordering fix, not an exclusion.";

            // (c) The struct must still cross, minus the derived member.
            var mvTable = RailType.Get(mv);
            if (!RailMeta.LeafKindOf(mv, out var mvKind) || mvKind != LeafKind.Composite)
                yield return "L505 still-composite: ModifiableValue no longer classifies LeafKind.Composite. " +
                             "The opt-out is supposed to drop ONE derived member, not the whole struct — " +
                             "without the composite kind every stat (StatusStat Min/Max/Value) stops mirroring.";
            else
            {
                foreach (var name in new[] { "BaseValue", "ModificationValue" })
                {
                    var f = mvTable?.FieldByName(name);
                    if (f == null || f.Class != FieldClass.Leaf)
                        yield return "L505 still-composite: ModifiableValue." + name + " is " +
                                     (f == null ? "absent from the table" : "'" + f.Class + "' (" + f.Exclude + ")") +
                                     " — it is one of the two INDEPENDENT members the derived EndValue is " +
                                     "recomputed from. Excluding it makes the exclusion of EndValue lossy.";
                }

                // (d) THE ARITHMETIC, run rather than asserted: a modified stat through the REAL codec.
                // Asserting WireFieldCount alone would pass on an encoder that computes the number and then
                // ships every field anyway — the exact inertness this law exists to catch — so the arm puts
                // a value through EncodeLeaf/DecodeLeaf and reads the members back.
                int optedOut = mvTable.Fields.Count(f => f.OptedOut);
                var sent = new Base.Entities.Statuses.ModifiableValue { BaseValue = 10f, ModificationValue = 5f };
                byte[] wire = Encoded(sent);
                var got = (Base.Entities.Statuses.ModifiableValue)Decoded(wire, sent.GetType());

                // BOTH ends, because either one alone would let the mutation through: the DECODER's refusal
                // to write an opted-out member is what makes the value right, and the ENCODER's skip is what
                // keeps it off the wire at all. wire[0] is the LeafKind, wire[1] the member count.
                if (optedOut == 0 || got.BaseValue != sent.BaseValue || got.ModificationValue != sent.ModificationValue)
                    yield return "L505 codec-ships-optout (decode): a ModifiableValue{Base=10, Mod=5} came " +
                                 "back as {Base=" + got.BaseValue + ", Mod=" + got.ModificationValue + "} " +
                                 "through the shipped Composite codec, with " + optedOut + " opted-out " +
                                 "member(s). DecodeLeaf builds a FRESH box, so a derived EndValue lands while " +
                                 "ModificationValue is still 0 and sets BaseValue to the sender's END value — " +
                                 "every modified stat's base inflated by exactly its modification, on EVERY " +
                                 "walk that ships the stat, not only when the modification changes.";

                if (wire.Length < 2 || wire[0] != (byte)LeafKind.Composite || wire[1] != mvTable.WireFieldCount)
                    yield return "L505 codec-ships-optout (encode): ModifiableValue encoded kind=" +
                                 (wire.Length > 0 ? wire[0].ToString() : "<empty>") + " with " +
                                 (wire.Length > 1 ? wire[1].ToString() : "<none>") + " member(s) on the wire, " +
                                 "not the " + mvTable.WireFieldCount + " its table carries. A Composite leaf " +
                                 "is the one table whose codec indexes Fields DIRECTLY (the u16 wire id IS " +
                                 "the index), so an opt-out row there is inert unless the encoder skips it — " +
                                 "which is how the derived EndValue kept riding.";
            }
        }

        /// <summary>THE SHIPPED CODEC, exercised end to end — real <c>EncodeLeaf</c> bytes read back by the
        /// real <c>DecodeLeaf</c>. <c>geo</c> is null because a Composite of floats never consults the level
        /// (only <c>LeafKind.EntityRef</c> does).</summary>
        private static byte[] Encoded(object src)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    RailMeta.EncodeLeaf(w, src.GetType(), src);
                return ms.ToArray();
            }
        }

        private static object Decoded(byte[] wire, Type declared)
        {
            using (var ms = new System.IO.MemoryStream(wire))
            using (var r = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                return RailMeta.DecodeLeaf(r, declared, null);
        }

        /// <summary>Types worth classifying: the game's own non-primitive value types. Nested and generic
        /// definitions are skipped the same way the rail's own classifier can never reach them.</summary>
        private static IEnumerable<Type> ValueTypes(Assembly game)
        {
            Type[] all;
            try { all = game.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in all)
                if (t.IsValueType && !t.IsPrimitive && !t.IsEnum && !t.IsGenericTypeDefinition)
                    yield return t;
        }

        /// <summary>True when <paramref name="setter"/> stores into a field of <paramref name="owner"/> that
        /// is NOT its own compiler-generated backing field — i.e. it mutates a SIBLING, which is what makes
        /// the member derived rather than stored.</summary>
        private static bool WritesSibling(MethodBase setter, Type owner, out string sibling)
        {
            sibling = null;
            byte[] il = Body(setter);
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x7D) continue;                        // stfld
                FieldInfo f = null;
                try { f = setter.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f == null || f.DeclaringType != owner) continue;
                if (f.Name.StartsWith("<", StringComparison.Ordinal)) continue; // own backing field
                sibling = f.Name;
                return true;
            }
            return false;
        }

        private static bool ReadsField(MethodBase reader, FieldInfo target)
        {
            byte[] il = Body(reader);
            if (il == null || target == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x7B && il[i] != 0x7E) continue;       // ldfld / ldsfld
                FieldInfo f = null;
                try { f = reader.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
            }
            return false;
        }

        private static byte[] Body(MethodBase m)
        {
            if (m == null) return null;
            try { return m.GetMethodBody()?.GetILAsByteArray(); } catch { return null; }
        }
    }
}
