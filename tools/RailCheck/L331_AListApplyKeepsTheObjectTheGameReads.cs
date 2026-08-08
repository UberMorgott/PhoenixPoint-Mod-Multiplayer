using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Serialization.General;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Levels.Factions;

namespace RailCheck
{
    /// <summary>
    /// L331 — A LIST APPLY LANDS ON THE OBJECT THE REST OF THE GAME READS.
    ///
    /// THE DEFECT (2026-08-08 co-op session): faction reputation percentages froze on the client after the
    /// first diplomacy delta and never moved again, while the client log showed the delta ARRIVING and the
    /// screen repainting normally (multiplayer.log:2404). Nothing was missing. It was landing on orphans.
    ///
    /// THE SHAPE, and it is a general one, not a diplomacy one. <c>FactionDiplomacyState.Relation</c> is a
    /// READONLY field holding the very same <c>PartyDiplomacy.Relation</c> instance the <c>_relations</c>
    /// dictionary holds (FactionDiplomacyState.cs:12/:31-33). <c>_relations</c> is the side every read goes
    /// through (PartyDiplomacy.cs:84/:105-116/:126-136) and is itself excluded from the rail
    /// (interface-keyed dictionary). We ship the sibling list, and the apply rebuilt it Clear+Add from
    /// FRESHLY CONSTRUCTED elements — so after one delta the list held new elements pointing at new
    /// Relations while <c>_relations</c> still held the originals. Readonly means the field cannot be
    /// re-pointed afterwards: the value had nowhere to land, permanently, from the first delta on.
    ///
    /// WHAT THIS ASSERTS IS THE OUTCOME. Not "the reuse path ran": it holds a second reference to the
    /// aliased object — standing in for <c>_relations</c> — and requires THAT object to be the one carrying
    /// the mirrored value afterwards. An implementation that reconciles the element but re-points its
    /// sub-object fails arm (a) exactly as the original defect did.
    ///
    /// ARMS
    ///   (a) <c>value-landed-on-an-orphan</c> — after applying, the aliased object still reachable from
    ///       outside the list carries the new value, the element is the SAME instance, and its ordinary
    ///       leaves moved too.
    ///   (b) <c>ordinal-pairing-widened</c>, THE NARROWNESS — an UNORDERED live container (HashSet) does
    ///       NOT get positional reconcile. Its enumeration order is an implementation detail, so pairing by
    ///       position there would write element A's state into element B. If this arm goes quiet, the fix
    ///       has silently become "reconcile anything that enumerates".
    ///   (c) <c>pairing-overruns</c> — a wire list LONGER than the live one reconciles element 0 in place
    ///       and BUILDS element 1. The list grows in play (StartRelations appends on first contact), and
    ///       reusing the live element for every index would collapse two mirrored entries onto one object.
    ///   (d) <c>custom-create-alias-replaced</c> — the SAME outcome on the rung arms (a)-(c) never reach, run
    ///       over the game's own <c>FactionDiplomacyState</c>/<c>PartyDiplomacy.Relation</c>. See the arm itself:
    ///       this is why the three arms above stayed green through the live re-break.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • drop the `existing` argument in DecodeEntityList → <c>value-landed-on-an-orphan</c> (both halves)
    ///   • keep element reuse but restore the unconditional `f.SetValue` on the TagBlob arm →
    ///     <c>value-landed-on-an-orphan</c> (the alias half only — the exact original defect)
    ///   • let OrderedLiveElements accept any IEnumerable → <c>ordinal-pairing-widened</c>
    ///   • restore the unconditional construct-fresh on the custom-create rung (`argsUnchanged = false`) →
    ///     <c>custom-create-alias-replaced</c>
    /// </summary>
    internal static class L331_AListApplyKeepsTheObjectTheGameReads
    {
        // The measured shape, minimal: an element holding a READONLY reference to an object that something
        // else also holds. Same as FactionDiplomacyState -> Relation <- PartyDiplomacy._relations.
        [SerializeType]
        private sealed class Aliased { [SerializeMember] public int Value; }

        [SerializeType]
        private sealed class Entry
        {
            [SerializeMember] public readonly Aliased Ref;
            [SerializeMember] public bool Flag;
            public Entry(Aliased r) { Ref = r; }
            private Entry() { }
        }

        private static RailField Field() => new RailField
        {
            Name = "probe", Class = FieldClass.EntityList,
            ValueType = typeof(List<Entry>), ElemType = typeof(Entry),
        };

        internal static IEnumerable<string> Check()
        {
            if (RailMeta.GameSerializer == null)
            {
                yield return "L331 premise-changed: no game serializer, so neither half of the round trip " +
                             "this law executes can run and every arm below would pass vacuously.";
                yield break;
            }

            // The host's version of the list: same one element, moved values.
            var source = new List<Entry> { new Entry(new Aliased { Value = 7 }) { Flag = true } };
            byte[] wire;
            Exception encodeFailed = null;
            try { wire = RailMeta.EncodeEntityList(Field(), source); }
            catch (Exception ex) { wire = null; encodeFailed = ex; }
            if (wire == null)
            {
                yield return "L331 premise-changed: the probe list would not encode (" +
                             (encodeFailed == null ? "null wire" : encodeFailed.GetType().Name + ": " + encodeFailed.Message) +
                             "). An element holding a readonly reference is the SHAPE this law is about; if " +
                             "the codec stopped carrying it, the arms below test nothing.";
                yield break;
            }

            // ── (a) THE OUTCOME ──────────────────────────────────────────────────────────────────────
            // `alias` is the stand-in for PartyDiplomacy._relations: a reference held OUTSIDE the list, by
            // the side every read actually goes through.
            var alias = new Aliased { Value = 0 };
            var e0 = new Entry(alias);
            var live = new List<Entry> { e0 };
            var back = RailMeta.DecodeEntityList(wire, Field(), null, live);

            if (back == null || back.Count != 1 || !ReferenceEquals(back[0], e0))
                yield return "L331 value-landed-on-an-orphan: the apply produced a NEW element instead of " +
                             "reconciling the one the client already had. Everything the old element was " +
                             "wired into keeps pointing at the old one, so the mirrored value is written " +
                             "somewhere nothing reads — the reputation numbers froze exactly like this, with " +
                             "the delta arriving and the screen repainting on top of a dead object.";
            else if (!ReferenceEquals(e0.Ref, alias))
                yield return "L331 value-landed-on-an-orphan: the element survived but its READONLY " +
                             "reference was re-pointed at a freshly decoded object. This is the defect " +
                             "itself, one level down: FactionDiplomacyState.Relation is the same instance " +
                             "PartyDiplomacy._relations holds, and readonly means nothing can put it back " +
                             "afterwards.";
            else if (alias.Value != 7 || !e0.Flag)
                yield return "L331 value-landed-on-an-orphan: the aliased object was kept but did not " +
                             "receive the value (Value=" + alias.Value + " expected 7, Flag=" + e0.Flag +
                             " expected true). Keeping the instance and not writing into it is the same " +
                             "outcome as replacing it — a frozen number on every peer but the host.";

            // ── (b) NARROWNESS: an unordered container is NOT paired by position ─────────────────────
            var hAlias = new Aliased { Value = 0 };
            var hEntry = new Entry(hAlias);
            var unordered = new HashSet<Entry> { hEntry };
            var hBack = RailMeta.DecodeEntityList(wire, Field(), null, unordered);
            if (hBack != null && hBack.Count == 1 && ReferenceEquals(hBack[0], hEntry))
                yield return "L331 ordinal-pairing-widened: an element of an UNORDERED live container was " +
                             "reconciled by position. A HashSet's (or Dictionary's) enumeration order is an " +
                             "implementation detail and is not the same on two peers, so position there does " +
                             "not identify an element — this would quietly write one element's mirrored state " +
                             "into another's, which is worse than the rebuild it replaced.";

            // ── (c) A GROWING LIST: reconcile what exists, BUILD what does not ──────────────────────
            // The list is not fixed-size — FactionDiplomacy.StartRelations appends an entry the first time
            // a party is met. So the pairing has to be bounded by the live list and the surplus has to be
            // constructed. The failure this catches is the plausible one: reusing the live element for
            // EVERY index, which collapses two mirrored entries onto one object and loses one outright.
            var grown = new List<Entry>
            {
                new Entry(new Aliased { Value = 7 }) { Flag = true },
                new Entry(new Aliased { Value = 9 }) { Flag = false },
            };
            byte[] wire2 = null;
            try { wire2 = RailMeta.EncodeEntityList(Field(), grown); } catch { }
            if (wire2 != null)
            {
                var keptAlias = new Aliased { Value = 0 };
                var kept = new Entry(keptAlias);
                var short1 = new List<Entry> { kept };
                var grownBack = RailMeta.DecodeEntityList(wire2, Field(), null, short1);
                var second = grownBack != null && grownBack.Count > 1 ? grownBack[1] as Entry : null;
                if (grownBack == null || grownBack.Count != 2 ||
                    !ReferenceEquals(grownBack[0], kept) || keptAlias.Value != 7 ||
                    second == null || ReferenceEquals(second, kept) ||
                    ReferenceEquals(second.Ref, keptAlias) || second.Ref == null || second.Ref.Value != 9)
                    yield return "L331 pairing-overruns: a wire list LONGER than the live one did not " +
                                 "reconcile element 0 in place and build element 1 fresh (got " +
                                 (grownBack == null ? "null" : grownBack.Count + " element(s), [0] reused=" +
                                  ReferenceEquals(grownBack[0], kept) + " value=" + keptAlias.Value +
                                  ", [1] reused=" + ReferenceEquals(second, kept) + ")") +
                                 ". The list GROWS in play — StartRelations appends an entry the first time " +
                                 "a party is met — so pairing that runs past the end would collapse two " +
                                 "mirrored entries onto one live object and drop the other's state entirely.";
            }

            // ── (d) THE REAL SUBJECT, on the rung the fabricated one never reaches ───────────────────
            foreach (var v in CustomCreateSubject()) yield return v;
        }

        /// <summary>
        /// (d) <c>custom-create-alias-replaced</c>. Arms (a)-(c) run over <c>Aliased</c>/<c>Entry</c>, which carry
        /// NO <c>[SerializeCustomCreate]</c> — so they exercise the Activator rung, and the real subject never
        /// goes down it. <c>PartyDiplomacy.Relation</c> IS a custom-create type (one param, <c>WithParty</c>,
        /// PartyDiplomacy.cs:70-74), and the decoder's carve-out excluded that whole rung from in-place reuse:
        /// it minted a fresh Relation on every delta and re-pointed the readonly
        /// <c>FactionDiplomacyState.Relation</c> at it, while <c>PartyDiplomacy._relations</c> — off the rail,
        /// and the side every read goes through (:104-116, the top bar's
        /// <c>MainDiplomacyRelationDisplayRowElement</c>:22) — kept the original. The mirror then AGREED with the
        /// host on CRC, because the sibling it ships held the correct value; only the screen was wrong, and a
        /// re-emit just minted another orphan. That is why arms (a)-(c) were green through live breakage, and why
        /// this arm runs the GAME's own types rather than a stand-in.
        /// </summary>
        private static IEnumerable<string> CustomCreateSubject()
        {
            var relT = typeof(PartyDiplomacy.Relation);
            var ctor = relT.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                                           new[] { typeof(IDiplomaticPartyKey) }, null);
            var dip = relT.GetField("_diplomacy", BindingFlags.NonPublic | BindingFlags.Instance);
            bool customCreate = relT.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Any(m => m.GetCustomAttributes(true).Any(a => a.GetType().Name == "SerializeCustomCreateAttribute"));
            if (ctor == null || dip == null || !customCreate)
            {
                yield return "L331 premise-changed: PartyDiplomacy.Relation is no longer the shape this arm is " +
                             "about (" + (ctor == null ? "no Relation(IDiplomaticPartyKey)" :
                                          dip == null ? "no _diplomacy field" : "no [SerializeCustomCreate]") +
                             "). Arms (a)-(c) run over a fabricated pair with no custom create, so with this arm " +
                             "gone the law is back to testing an Activator rung the real subject never reaches — " +
                             "re-point it at whatever carries a relation now.";
                yield break;
            }

            var field = new RailField
            {
                Name = "probe", Class = FieldClass.EntityList,
                ValueType = typeof(List<FactionDiplomacyState>), ElemType = typeof(FactionDiplomacyState),
            };

            var hostRel = ctor.Invoke(new object[] { null });
            dip.SetValue(hostRel, 7);
            var source = new List<FactionDiplomacyState>
            {
                new FactionDiplomacyState((PartyDiplomacy.Relation)hostRel) { AtWar = true },
            };
            byte[] wire = null;
            Exception failed = null;
            try { wire = RailMeta.EncodeEntityList(field, source); }
            catch (Exception ex) { failed = ex; }
            if (wire == null)
            {
                yield return "L331 premise-changed: a FactionDiplomacyState would not encode (" +
                             (failed == null ? "null wire" : failed.GetType().Name + ": " + failed.Message) +
                             "). This arm is the only one that reaches the custom-create rung; if the codec " +
                             "stopped carrying the real subject, the alias defect is unguarded again.";
                yield break;
            }

            // The client's side: the relation instance PartyDiplomacy._relations also holds.
            var liveRel = (PartyDiplomacy.Relation)ctor.Invoke(new object[] { null });
            var liveState = new FactionDiplomacyState(liveRel);
            var live = new List<FactionDiplomacyState> { liveState };
            var back = RailMeta.DecodeEntityList(wire, field, null, live);

            var got = back != null && back.Count == 1 ? back[0] as FactionDiplomacyState : null;
            if (got == null || !ReferenceEquals(got, liveState))
                yield return "L331 custom-create-alias-replaced: the apply produced a NEW FactionDiplomacyState " +
                             "instead of reconciling the live one, so nothing the game already wired to it can " +
                             "see the mirrored value.";
            else if (!ReferenceEquals(got.Relation, liveRel))
                yield return "L331 custom-create-alias-replaced: the state survived but its READONLY Relation was " +
                             "re-pointed at a freshly CREATED one. This is the shipped defect: a custom-create " +
                             "type was excluded from in-place reuse, so every diplomacy delta minted an orphan " +
                             "while PartyDiplomacy._relations — the side the top bar reads — kept the original. " +
                             "The mirror still agreed on CRC, because the sibling we ship held the right number, " +
                             "so nothing reported it and a re-emit only minted another orphan.";
            else if ((int)dip.GetValue(liveRel) != 7 || !got.AtWar)
                yield return "L331 custom-create-alias-replaced: the aliased Relation was kept but did not receive " +
                             "the value (_diplomacy=" + dip.GetValue(liveRel) + " expected 7, AtWar=" + got.AtWar +
                             " expected true). Keeping the instance without writing into it freezes the number on " +
                             "every peer but the host, exactly as replacing it did.";
        }
    }
}
