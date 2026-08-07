using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Base.Defs;
using Base.Serialization.General;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;

namespace RailCheck
{
    /// <summary>
    /// L192 — A DEF UNDER AN INTERFACE-DECLARED SLOT RIDES AS THE LEAF IT ALREADY IS, AND NOTHING WIDER DOES.
    ///
    /// THE DEFECT (2026-08-07 co-op session, HOST log, ×3 factions at the FIRST campaign walk):
    ///   [rail] DiffEngine excluded: FactionDiplomacy._factionsDiplomacyState:
    ///          entity-list encode failed: polymorphic value PPFactionDef as IDiplomaticPartyKey
    /// The whole field aborted at encode and stayed excluded for the remaining sixteen minutes, so faction
    /// diplomacy NEVER crossed to a client: its relations screen ran on whatever the save transfer held at
    /// 23:09 and never updated again. L188 is the reporting half of that same incident; this is the encode.
    ///
    /// IT IS NOT OBJECT POLYMORPHISM. A <c>PPFactionDef</c> is a <c>BaseDef</c>, i.e. already a LEAF on this
    /// rail (<c>LeafKindOf</c> → <c>LeafKind.DefRef</c>): its entire wire form is a guid, and the client
    /// resolves it out of its OWN def repository. The only reason it reached the blob path at all is that the
    /// DECLARED slot is an interface, which the leaf test cannot see through — and the blob path's first act
    /// is to refuse a runtime type that is not the declared one. The slot that actually fires is
    /// <c>Relation</c>'s <c>[SerializeCustomCreate]</c> <c>WithParty</c> create param (EncodeObjectBody's
    /// <c>ci.Params</c> loop), reached from <c>_factionsDiplomacyState</c> →
    /// <c>FactionDiplomacyState.Relation</c>.
    ///
    /// WHAT THIS DOES NOT FIX, so nobody reads the green as "diplomacy is fully mirrored": three CLASSIFY-time
    /// exclusions in the same neighbourhood are untouched and stay loud in the reviewed baseline —
    /// <c>FactionDiplomacy._relations</c> ("dictionary with non-simple key/value",
    /// <c>docs/rail-baseline.txt</c>:488), <c>FactionDiplomacy.Party</c> (:486) and
    /// <c>Relation._thisParty</c>/<c>_withParty</c> (:132-133), all "untyped/interface member". A different
    /// class: reviewed, visible, never silent. This arm unblocks the FIELD they sit in, so the list ships
    /// <c>AtWar</c>, <c>PointOfInterest</c>, the titles and <c>Relation._diplomacy</c> instead of nothing at
    /// all — with <c>_withParty</c> arriving through the create param above.
    ///
    /// THE NARROWNESS IS THE LOAD-BEARING PART, so arm (c) is not decoration. `docs/rail-contract.txt`'s
    /// frozen <c>polymorphic-codec: no</c> gates the reviewed TYPE CLOSURE (<c>Closure()</c> expands to
    /// <c>Concretions(...)</c> when it is yes) and <c>L55</c> argues from it by name. The arm is therefore
    /// gated on the RUNTIME value being a <c>BaseDef</c> and nothing else: a derived CLASS under a base class
    /// still throws exactly as before, the probe's own shape is unaffected, and this run still reports
    /// <c>polymorphic-codec=no</c> with <c>types=96</c> unchanged. If arm (c) ever goes quiet, the general
    /// codec has been smuggled in behind a bug fix and the closure has stopped meaning what it says.
    ///
    /// Falsify: remove the leaf arm → <c>L192 def-under-interface-aborts-the-field</c>; drop the decode twin →
    /// <c>L192 leaf-tag-unreadable</c>; widen the arm past BaseDef → <c>L192 codec-quietly-went-polymorphic</c>;
    /// let a def stop being a leaf, or the interface start being one → <c>L192 premise-changed</c>.
    /// </summary>
    internal static class L192_ADefWearingAnInterfaceStillRidesAsALeaf
    {
        internal static IEnumerable<string> Check()
        {
            var def = typeof(PPFactionDef);
            var slot = typeof(IDiplomaticPartyKey);
            bool defIsLeaf = RailMeta.LeafKindOf(def, out var kind);
            bool slotIsLeaf = RailMeta.LeafKindOf(slot, out _);

            if (RailMeta.GameSerializer == null || !typeof(BaseDef).IsAssignableFrom(def) ||
                !slot.IsAssignableFrom(def) || !defIsLeaf || kind != LeafKind.DefRef || slotIsLeaf)
            {
                yield return "L192 premise-changed: the game serializer is unavailable, or PPFactionDef is no " +
                             "longer a BaseDef / no longer implements IDiplomaticPartyKey, or the def stopped " +
                             "being a DefRef leaf (" + kind + "), or the interface became a leaf itself. The " +
                             "whole argument of this arm is that the VALUE is already a leaf and only the " +
                             "SLOT hides it; without that it would be a polymorphic blob codec, which the " +
                             "frozen contract line forbids.";
                yield break;
            }

            // ── (a) THE OUTCOME: the field that aborted now encodes ──
            // An uninitialised def is enough and is all a console host can build: the DefRef codec's entire
            // read of it is the public `Guid` field, and what this arm is about is which BRANCH is taken.
            var party = (IDiplomaticPartyKey)FormatterServices.GetUninitializedObject(def);
            var partyField = new RailField
            {
                Name = "probe", Class = FieldClass.EntityList,
                ValueType = typeof(List<IDiplomaticPartyKey>), ElemType = slot,
            };
            byte[] wire = null;
            Exception aborted = null;
            try { wire = RailMeta.EncodeEntityList(partyField, new List<IDiplomaticPartyKey> { party }); }
            catch (Exception ex) { aborted = ex; }
            if (aborted != null)
                yield return "L192 def-under-interface-aborts-the-field: encoding a def through an " +
                             "interface-declared slot threw (" + aborted.GetType().Name + ": " + aborted.Message +
                             "). That is the 2026-08-07 line verbatim — FactionDiplomacy." +
                             "_factionsDiplomacyState died on it at the first campaign walk and no peer's " +
                             "diplomacy has crossed since, because an EntityList aborts WHOLE when one " +
                             "element will not encode.";

            // ── (b) the decode twin KNOWS the tag the encode writes ──
            // The full round trip is not decidable in a console host: the DefRef codec's last act is a
            // DefRepository lookup, which is an ECall and throws SecurityException with no game. What IS
            // decidable — and is the whole hazard of adding a tag — is whether the decoder dispatches on it
            // at all: an unknown tag dies in DecodeValue's `default` as IOException("bad blob tag 8"), BEFORE
            // any def lookup. So reaching the lookup is the pass condition, and only the tag verdict is read.
            if (wire != null)
            {
                Exception back = null;
                try { RailMeta.DecodeEntityList(wire, partyField, null); }
                catch (Exception ex) { back = ex; }
                if (back is System.IO.IOException && back.Message.IndexOf("bad blob tag", StringComparison.Ordinal) >= 0)
                    yield return "L192 leaf-tag-unreadable: the decoder does not know the tag this encode " +
                                 "writes (" + back.Message + "). A tag written by one half and unknown to the " +
                                 "other is worse than the abort it replaced: the host would believe the field " +
                                 "is riding while every client tears on the packet that carries it.";
            }

            // ── (c) POSITIVE CONTROL — the narrowness the frozen contract line depends on ──
            var plainField = new RailField
            {
                Name = "probe", Class = FieldClass.EntityList,
                ValueType = typeof(List<PlainBase>), ElemType = typeof(PlainBase),
            };
            bool refused = false;
            try { RailMeta.EncodeEntityList(plainField, new List<PlainBase> { new PlainDerived() }); }
            catch (NotSupportedException) { refused = true; }
            catch { }
            if (!refused)
                yield return "L192 codec-quietly-went-polymorphic: a plain DERIVED CLASS under a base class " +
                             "now encodes too, so the codec is no longer declared-type-only. That flips " +
                             "docs/rail-contract.txt's frozen `polymorphic-codec: no`, which gates the " +
                             "REVIEWED TYPE CLOSURE (Closure -> Concretions) and which L55 argues from by " +
                             "name — a general codec arriving as a side effect of a diplomacy fix is exactly " +
                             "the change that must never be silent.";
        }

        // The control's own shape, mirroring the harness's startup probe: a base and a derived, both plain
        // classes and neither a def, so the ONLY thing separating them from arm (a) is BaseDef-ness.
        [SerializeType]
        private class PlainBase { [SerializeMember] public int A; }

        [SerializeType]
        private sealed class PlainDerived : PlainBase { }
    }
}
