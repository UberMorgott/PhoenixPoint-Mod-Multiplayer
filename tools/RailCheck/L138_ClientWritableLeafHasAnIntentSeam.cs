using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>
    /// L138 — A RAIL-COVERED LEAF THE GAME'S PRESENTATION LAYER CAN WRITE ON A CLIENT EITHER IS REFUSED
    /// ON THAT CLIENT OR REACHES A REGISTERED IntentRail OP — AND WHAT ARRIVES ON A PEER IS RE-DERIVED,
    /// NOT JUST STORED.
    ///
    /// THE CLASS (gap iv: the host-side mutation never happens). Rail coverage has two halves, and the
    /// harness only ever asserted the first. <c>RootCoverageLaw</c> (Program.cs:2908) asserts the leaf is
    /// WALKED; nothing asserted that anything ever MOVES it on the host. Soldier customization was the
    /// textbook instance and it was green the whole time: <c>CharacterIdentity</c> is real persisted state
    /// (15 <c>[SerializeMember]</c> leaves, CharacterIdentity.cs:21), owned by <c>GeoCharacter._identity</c>
    /// (GeoCharacter.cs:60-61), covered 15/15 by the diff (rail-baseline.txt:168-183) with no exclusion, no
    /// husk gate and no metadata filter — and <c>grep -ri customiz src/</c> returned exactly ONE hit, a husk
    /// waiver string. Every writer is a bare field assignment on the LIVE identity
    /// (UIModuleUnitCustomization.cs:72-91, UIModuleSoldierCustomization.cs:163-235). So a client's clicks
    /// moved SHARED state locally, the host diffed host-now against host-before, found nothing and shipped
    /// nothing; the colours stayed on the one instance that made them, and the CRC backstop
    /// (DiffEngine.cs:486/:533) was free to revert them without a word. The same shape covers
    /// <c>GeoCharacter.Rename</c>:825 — also a covered leaf (<c>Name</c>), also with no write seam.
    ///
    /// THE OTHER HALF, WHICH IS ALSO A COVERAGE QUESTION. <c>GeoCharacter.GameTags</c> is NOT serialized;
    /// it is re-derived by the game's own <c>RefreshTags()</c> (GeoCharacter.cs:568-573), and the mesh and
    /// material addon builder renders off THOSE TAGS. So even a HOST-side edit mirrored all 15 leaves and
    /// still left every other peer's soldier in his old colours until reload — the value arrived and the
    /// derivation never re-ran. A law that stopped at "the leaf reached the peer" would have called that
    /// green too, which is why arms (c)/(e) exist.
    ///
    /// L36 <c>FunnelCoverageLaw</c> (Program.cs:4853) is the nearest neighbour and could never have seen
    /// this: it is hard-scoped to <c>GeoscapeEvent</c> funnels.
    ///
    /// THE ARMS. (a) EXECUTES the shipped payload codec over the rail's OWN leaf table for
    /// <c>CharacterIdentity</c> and asserts an entry per COVERED leaf plus a value round-trip — a payload
    /// that hand-listed fields and forgot one goes red, and so does a payload that cannot express a CLEARED
    /// slot (removing a beard ships no tag; a tag-list payload silently keeps the old one, which is why
    /// <c>InitFromTags</c>:355 is not what the host applies). (b) EXECUTES the real shipped capture decision
    /// over its whole truth table — the three silences are each a loop or a lie: solo, the mirror's own
    /// re-derivation, and the host (whose own write IS the shared state, and whose replay of a client intent
    /// ends in <c>RefreshTags()</c> as well). (c) EXECUTES the derived-cache consequence and pins it
    /// NAMELESS: <c>GetGameTags()</c> reads thirteen of the fifteen members, so a field-name gate here would
    /// be a transcription that rots the day the game adds a tag. (d) EXECUTES the real registration table —
    /// literally "reaches a registered IntentRail op". (e) is structural: the consequence must LAND through
    /// the game's own method and must be SCOPED, because the capture sits on that very method. (f) is the
    /// gap-class tripwire — it walks the GAME's presentation layer for stores into covered identity leaves
    /// and fires when a writer appears that the premise does not name.
    ///
    /// STATED LIMIT, honestly. (f) sweeps <c>CharacterIdentity</c>, not every covered type in the rail. A
    /// whole-rail sweep is the right shape and is not what this law ships: reachability from a
    /// presentation write to a mod seam is not statically decidable here — the game's own edge is a
    /// DELEGATE (<c>OnCustomizationChanged?.Invoke()</c>, UIModuleUnitCustomization.cs:64-70) — so (f) is a
    /// PREMISE GUARD that demands a re-derivation when the writer set changes, never a proof that a new
    /// writer is covered. Arm (a) likewise cannot build real <c>*TagDef</c> instances in a console host, so
    /// the twelve def-typed leaves are exercised at null; the clearing PROPERTY is proven on <c>Name</c>
    /// and generalises only because arm (a) also proves the wire carries an entry for every covered leaf.
    ///
    /// WHERE THE SEAM SITS, and why not on the model method. The first shipped attempt captured
    /// <c>GeoCharacter.RefreshTags</c>/<c>Rename</c> directly and L19 called it correctly: a postfix on a
    /// MODEL method ships a RESULT after the authoritative write. The identity writes are STAGING (a live
    /// colour preview is the feature), so the capture belongs where the game stages — the two closed
    /// <c>RefreshUnitDisplay</c> overrides and <c>UIModuleActorCycle.RenameCharacter</c>, all three under
    /// <c>*.View.*</c>. Arm (f) found the third writer this law's author had missed
    /// (<c>UIModuleVehicleCustomization</c>) on its first run, which is the whole point of a tripwire.
    ///
    /// Falsify: make the payload skip a covered leaf → <c>L138 payload-drops-a-covered-leaf</c>; make a
    /// null leaf a no-op instead of a clear → <c>L138 cleared-slot-does-not-cross</c>; let the HOST ship a
    /// customize intent → <c>L138 host-echoes-its-own-write</c>; silence it entirely → <c>L138
    /// client-gesture-is-swallowed</c>; take <c>OpenUiRepaint.Repaint</c> out of SyncApplyScope (BOTH sites
    /// — the native rebuild AND the fallback re-enter, which is the one that reaches this funnel) →
    /// <c>L138 mirror-echoes-back-as-a-gesture</c>; make <c>IdentityWriteConsequence</c> answer for
    /// everything → <c>L138 consequence-fires-for-anything</c>; drop <c>RefreshTags</c> from
    /// <c>FlushOrderReseed</c> → <c>L138 mirrored-appearance-never-re-derives</c>; unregister the op →
    /// <c>L138 captured-gesture-has-no-host-handler</c>. All eight verified RED then restored 2026-08-06.
    /// The <c>consequence-is-field-keyed</c> arm is the one that does NOT go red: giving
    /// <c>IdentityWriteConsequence</c> a field-name parameter breaks the BUILD, because arm (c) calls it
    /// directly. That is a stronger gate than a red line, and the reflective check stays as the belt for
    /// the case the compiler would accept — an ADDED overload beside the original.
    /// </summary>
    internal static class L138_ClientWritableLeafHasAnIntentSeam
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The one method of that name, or null. NOT <c>GetMethod(name, All)</c>: an ADDED overload
        /// makes that throw <c>AmbiguousMatchException</c>, which takes the whole harness down instead of
        /// printing a law — and "somebody added an overload" is exactly the drift these arms exist to
        /// notice, so it must surface as a premise-changed line and not as a crash.</summary>
        private static MethodInfo Single(Type t, string name)
        {
            var all = t.GetMethods(All).Where(m => m.Name == name).ToList();
            return all.Count == 1 ? all[0] : null;
        }

        /// <summary>The GAME types the premise names as presentation writers of an identity leaf, each with
        /// the funnel that carries its write to the wire. A store from anywhere else is what arm (f) is for.
        /// </summary>
        private static readonly Dictionary<string, string> KnownWriters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UIModuleUnitCustomization"]     = "OnCustomizationChanged → UIState*Customization.RefreshUnitDisplay (captured)",
            ["UIModuleSoldierCustomization"]  = "OnCustomizationChanged → UIStateSoldierCustomization.RefreshUnitDisplay (captured)",
            ["UIModuleVehicleCustomization"]  = "OnCustomizationChanged → UIStateVehicleCustomization.RefreshUnitDisplay (captured)",
            ["GeoCharacter"]                  = "GeoCharacter.Rename:825 ← UIModuleActorCycle.RenameCharacter:739 (captured)",
            ["CharacterIdentity"]             = "the type's own Init*/CopyFrom helpers — model-side, host-authoritative",
        };

        internal static IEnumerable<string> Check()
        {
            var rt = RailType.Get(typeof(CharacterIdentity));
            if (rt?.Fields == null)
            {
                yield return "L138 premise-changed: the rail has no leaf table for CharacterIdentity. Every " +
                             "arm below is derived from that table — re-read this law before assuming a " +
                             "soldier's appearance crosses at all.";
                yield break;
            }
            var covered = rt.Fields.Where(f => f.Class == FieldClass.Leaf && f.CanRead && f.IsWritable()).ToList();
            if (covered.Count == 0)
            {
                yield return "L138 premise-changed: CharacterIdentity has no covered writable leaves. " +
                             "Customization would then not be rail state at all.";
                yield break;
            }

            // ── (a) THE PAYLOAD IS THE LEAF TABLE, and a null CLEARS ────────────────────────────────
            byte[] wire;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                PersonnelSync.EncodeIdentity(w, new CharacterIdentity { Name = "Alpha", Sex = GeoCharacterSex.Female });
                wire = ms.ToArray();
            }
            if (wire.Length == 0 || wire[0] != covered.Count)
                yield return "L138 payload-drops-a-covered-leaf: the customize intent carries " +
                             (wire.Length == 0 ? 0 : wire[0]) + " of the " + covered.Count + " leaves the rail " +
                             "covers on CharacterIdentity. The leaves it omits are exactly the ones a client " +
                             "can change and no peer will ever see change — the bug this law exists for, one " +
                             "field narrower.";

            var dst = new CharacterIdentity { Name = "OLD", Sex = GeoCharacterSex.Male };
            int applied = 0;
            using (var ms = new MemoryStream(wire))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
                applied = PersonnelSync.ApplyIdentity(dst, r, null);
            if (applied != covered.Count)
                yield return "L138 payload-does-not-land: " + applied + " of " + covered.Count + " covered " +
                             "leaves were written onto the host's identity. The rest silently keep the host's " +
                             "old value, and the author's own screen is then the only place his edit exists.";
            if (dst.Name != "Alpha" || dst.Sex != GeoCharacterSex.Female)
                yield return "L138 customization-does-not-cross: a client's name/sex choice did not reach the " +
                             "host's identity through the shipped codec (name=" + (dst.Name ?? "null") +
                             ", sex=" + dst.Sex + "). Every peer keeps painting the old soldier.";

            // A CLEARED slot — the case a tag-LIST payload cannot express, and the reason the host does not
            // apply through CharacterIdentity.InitFromTags. Proven on Name, generalised by the entry-count
            // arm above (every covered leaf gets an entry, and a null entry is a real write).
            byte[] cleared;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                PersonnelSync.EncodeIdentity(w, new CharacterIdentity { Name = null, Sex = GeoCharacterSex.Male });
                cleared = ms.ToArray();
            }
            var clearDst = new CharacterIdentity { Name = "OLD", Sex = GeoCharacterSex.Female };
            using (var ms = new MemoryStream(cleared))
            using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
                PersonnelSync.ApplyIdentity(clearDst, r, null);
            if (clearDst.Name != null)
                yield return "L138 cleared-slot-does-not-cross: emptying a leaf left the host holding '" +
                             clearDst.Name + "'. That is the shave-the-beard case: the player removes a " +
                             "feature, ships a payload that says nothing about it, and every other peer — and " +
                             "eventually the author, after the CRC backstop re-emits — still sees it.";

            // ── (b) THE CAPTURE DECISION, whole truth table ─────────────────────────────────────────
            foreach (var (session, host, applying, want, why) in new[]
            {
                (true,  false, false, PersonnelSync.CustomizeAction.Ship,   "a client's own gesture"),
                (true,  false, true,  PersonnelSync.CustomizeAction.Silent, "a client re-deriving an APPLIED batch"),
                (true,  true,  false, PersonnelSync.CustomizeAction.Silent, "the host's own gesture"),
                (true,  true,  true,  PersonnelSync.CustomizeAction.Silent, "the host replaying a client intent"),
                (false, false, false, PersonnelSync.CustomizeAction.Silent, "solo"),
                (false, false, true,  PersonnelSync.CustomizeAction.Silent, "solo, mid-apply"),
                (false, true,  false, PersonnelSync.CustomizeAction.Silent, "solo host"),
                (false, true,  true,  PersonnelSync.CustomizeAction.Silent, "solo host, mid-apply"),
            })
            {
                var got = PersonnelSync.CustomizeShipDecision(session, host, applying);
                if (got == want) continue;
                if (want == PersonnelSync.CustomizeAction.Ship)
                    yield return "L138 client-gesture-is-swallowed: " + why + " ships nothing. The client's " +
                                 "clicks then move shared state on that instance ALONE — the original bug.";
                else if (host && !applying)
                    yield return "L138 host-echoes-its-own-write: " + why + " ships a customize intent to " +
                                 "itself. The host's write IS the shared state and the generic diff already " +
                                 "carries it; the intent replays it, whose RefreshTags re-enters the capture.";
                else if (applying)
                    yield return "L138 mirror-echoes-back-as-a-gesture: " + why + " ships an intent. The rail " +
                                 "re-derives tags after every batch that lands an identity leaf, so this is " +
                                 "the host's own mirror being sent straight back at it — one echo per batch, " +
                                 "for the rest of the session.";
                else
                    yield return "L138 solo-peer-talks: " + why + " ships an intent with no session.";
            }

            // ── (c) THE DERIVED-CACHE CONSEQUENCE, and it must stay NAMELESS ───────────────────────
            var consequence = Single(typeof(GenericApplier), "IdentityWriteConsequence");
            if (consequence == null || consequence.GetParameters().Length != 1)
                yield return "L138 consequence-is-field-keyed: GenericApplier.IdentityWriteConsequence no " +
                             "longer answers from the entity alone. CharacterIdentity.GetGameTags():104-121 " +
                             "reads THIRTEEN of the fifteen members, so a name gate here is a transcription " +
                             "of that method — and the leaf it forgets is a colour that never re-derives.";
            else
            {
                if (!GenericApplier.IdentityWriteConsequence(new CharacterIdentity()))
                    yield return "L138 identity-write-has-no-consequence: a rail write onto a CharacterIdentity " +
                                 "asks for no tag re-derivation. GeoCharacter.GameTags is not serialized " +
                                 "(GeoCharacter.cs:568-573) and the addon builder renders off it, so the peer " +
                                 "stores the new colours and keeps painting the old ones until reload.";
                if (GenericApplier.IdentityWriteConsequence(new object()) ||
                    GenericApplier.IdentityWriteConsequence("not an identity"))
                    yield return "L138 consequence-fires-for-anything: a non-identity rail write asks for a tag " +
                                 "re-derivation. Every batch would then chase a GeoCharacter root that the " +
                                 "path does not name, and the log line meant for a real miss becomes noise.";
            }

            // ── (d) THE CAPTURED GESTURE REACHES A REGISTERED OP ───────────────────────────────────
            foreach (var v in RegisteredOp()) yield return v;

            // ── (e) THE CONSEQUENCE LANDS, NATIVELY, AND SCOPED ────────────────────────────────────
            var mod = typeof(GenericApplier).Assembly;
            var game = typeof(GeoCharacter).Assembly;
            var refreshTags = Single(typeof(GeoCharacter), "RefreshTags");
            var flush = Single(typeof(GenericApplier), "FlushOrderReseed");
            var mark = Single(typeof(GenericApplier), "MarkOrderChange");
            var applyCustomize = Single(typeof(PersonnelSync), "ApplyCustomize");
            if (refreshTags == null || flush == null || mark == null || applyCustomize == null)
                yield return "L138 premise-changed: {GeoCharacter.RefreshTags, GenericApplier.FlushOrderReseed, " +
                             "GenericApplier.MarkOrderChange, PersonnelSync.ApplyCustomize} no longer all " +
                             "resolve. Those four ARE the seam this law asserts.";
            else
            {
                if (!Program.Callees(flush, game).Any(c => c.MetadataToken == refreshTags.MetadataToken))
                    yield return "L138 mirrored-appearance-never-re-derives: the post-batch flush no longer runs " +
                                 "the game's own GeoCharacter.RefreshTags. The 15 leaves land and GameTags — " +
                                 "which is what the mesh and material addon builder actually reads — keeps the " +
                                 "values it was born with. This is the half that broke host→client and " +
                                 "client→client even when the wire was perfect.";
                var repaint = Single(typeof(OpenUiRepaint), "Repaint");
                if (repaint == null || !Program.Callees(repaint, mod)
                        .Any(c => c.DeclaringType == typeof(SyncApplyScope) && c.Name == "Enter"))
                    yield return "L138 mirror-echoes-back-as-a-gesture: OpenUiRepaint's repaint no longer runs " +
                                 "inside SyncApplyScope. That repaint RE-ENTERS the open customization state, " +
                                 "whose EnterState raises OnCustomizationChanged straight back through the very " +
                                 "funnel this family captures — so without the scope a mirrored appearance is " +
                                 "read as a fresh gesture and shipped back at the host, once per batch, for the " +
                                 "rest of the session. Arm (b)'s apply-scope row is asserting a flag nothing sets.";
                if (!Program.Callees(mark, mod).Any(c => c.Name == "IdentityWriteConsequence"))
                    yield return "L138 consequence-never-consulted: MarkOrderChange no longer asks " +
                                 "IdentityWriteConsequence. The decision is then whatever is inlined at the call " +
                                 "site and arm (c) is executing something nothing runs.";
                if (!Program.Callees(applyCustomize, game).Any(c => c.MetadataToken == refreshTags.MetadataToken))
                    yield return "L138 host-stores-without-deriving: the host lands a client's appearance and " +
                                 "never re-derives its OWN tags. The host is then the one peer showing the old " +
                                 "soldier — and it is the peer every later diff is measured against.";
            }

            // ── (f) THE GAP-CLASS TRIPWIRE: who else writes a covered identity leaf? ────────────────
            foreach (var v in SweepPresentationWriters(covered)) yield return v;
        }

        /// <summary>Arm (d) — EXECUTE the shipped registration and ask the real table, rather than reading
        /// the call site. Registering is a dictionary write and needs no live game.</summary>
        private static IEnumerable<string> RegisteredOp()
        {
            var opField = typeof(PersonnelSync).GetField("OpCustomize", BindingFlags.NonPublic | BindingFlags.Static);
            var families = typeof(IntentRail).GetField("_families", BindingFlags.NonPublic | BindingFlags.Static);
            if (opField == null || families == null)
            {
                yield return "L138 premise-changed: PersonnelSync.OpCustomize or IntentRail._families no longer " +
                             "resolves — the law cannot tell whether a captured gesture has a host handler.";
                yield break;
            }
            byte op = (byte)opField.GetRawConstantValue();
            string threw = null;
            try { PersonnelSync.RegisterIntents(); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw != null)
            {
                yield return "L138 premise-changed: PersonnelSync.RegisterIntents threw in the harness (" +
                             threw + ") — the registration table cannot be executed here.";
                yield break;
            }
            var table = families.GetValue(null) as System.Collections.IDictionary;
            var family = table?[SurfaceIds.GeoPersonnelIntent];
            var ops = family?.GetType().GetField("Ops", All)?.GetValue(family) as System.Collections.IDictionary;
            if (ops == null)
            {
                yield return "L138 captured-gesture-has-no-host-handler: surface 0xAF registers no op table at " +
                             "all. Every personnel intent a client sends is decoded into nothing.";
                yield break;
            }
            if (!ops.Contains(op))
                yield return "L138 captured-gesture-has-no-host-handler: the client captures the customization " +
                             "funnel and sends op=" + op + " on 0xAF, and the host's table has no handler for it. " +
                             "The gesture is then WORSE than the bug it fixes — the client blocks nothing, " +
                             "mutates its own copy, and the packet dies on arrival with a dedup entry to show " +
                             "for it.";
            if (ops.Count != 12)
                yield return "L138 premise-changed: the 0xAF family registers " + ops.Count + " ops, not 12. " +
                             "Op bytes are never renumbered on a live surface — re-read which byte is which " +
                             "before trusting arm (d).";
        }

        /// <summary>Arm (f) — every GAME presentation-layer method that STORES into a covered
        /// <c>CharacterIdentity</c> leaf. Not a reachability proof (the game's own edge out of these writers
        /// is a delegate, which no static walk crosses): a tripwire that demands a re-derivation the moment
        /// a writer appears the premise does not name.</summary>
        private static IEnumerable<string> SweepPresentationWriters(List<RailField> covered)
        {
            var leaves = new HashSet<int>(covered.Where(f => f.Fi != null).Select(f => f.Fi.MetadataToken));
            if (leaves.Count == 0)
            {
                yield return "L138 premise-changed: no covered CharacterIdentity leaf resolves to a FieldInfo, " +
                             "so the presentation-writer sweep can see nothing.";
                yield break;
            }
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var t in typeof(GeoCharacter).Assembly.GetTypes())
            {
                var ns = t.Namespace ?? "";
                // The presentation layer, plus the two model types the premise names. A store from a type
                // outside this set cannot be reached by a player's click and is not this law's business.
                if (!ns.Contains(".View.") && t != typeof(GeoCharacter) && t != typeof(CharacterIdentity)) continue;
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly))
                    if (StoresAny(m, leaves)) { found.Add(t.Name); break; }
            }
            foreach (var name in found)
                if (!KnownWriters.ContainsKey(name))
                    yield return "L138 unnamed-identity-writer: " + name + " stores into a rail-covered " +
                                 "CharacterIdentity leaf and this law's premise does not name it. Re-derive " +
                                 "whether a CLIENT can reach it and whether its write funnels into one of the " +
                                 "three seams the mod captures (UIState{Soldier,Vehicle}Customization." +
                                 "RefreshUnitDisplay, UIModuleActorCycle.RenameCharacter). A writer that " +
                                 "reaches none of them is a covered leaf a player can move with nobody else " +
                                 "ever hearing about it.";
            foreach (var known in KnownWriters.Keys)
                if (!found.Contains(known))
                    yield return "L138 premise-changed: " + known + " no longer stores any covered " +
                                 "CharacterIdentity leaf. The funnel this law was built on has moved — re-derive " +
                                 "where customization is written before trusting the capture points.";
        }

        /// <summary>Does this method <c>stfld</c> one of the given fields? Walks the IL with the real
        /// operand-size table — a naive byte scan for 0x7D matches operand bytes and invents writers, and a
        /// tripwire that cries wolf is a tripwire that gets deleted. Anything unparseable ABANDONS the
        /// method (under-reporting is survivable for a premise guard; a false red is not). Program's own
        /// walker cannot answer this — it yields call targets only, never field tokens.</summary>
        private static bool StoresAny(MethodBase m, HashSet<int> fields)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) return false;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) return false;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) return false;
                if (op == OpCodes.Stfld && fields.Contains(BitConverter.ToInt32(il, i))) return true;
                i += size;
            }
            return false;
        }

        private static int OperandSize(OperandType t, byte[] il, int at)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return -1;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }
    }
}
