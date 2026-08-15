using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewControllers.Modal;

namespace RailCheck
{
    /// <summary>
    /// L106 — A MIRRORED MODAL MUST REBUILD TO A REAL DATA OBJECT ON THE PEER.
    ///
    /// L49 keeps <c>GeoWindowCoverage.DeclaredModals</c> TOTAL over <c>ModalType</c> and asserts the client's
    /// copy cannot run game logic. Neither of those notices the failure this law exists for: a kind declared
    /// <c>Mirrored</c> whose <c>modalData</c> nothing can DESCRIBE. That combination is silent by
    /// construction — <c>GeoModalMirror.HostBroadcast</c> logs an error and returns, the host's own window
    /// opens exactly as always, and the peer simply never gets one. It is how the mission brief and the
    /// soldier join sat declared, reviewed and un-shipped: L49 was green through both, for months.
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME, EXECUTED, not the presence of a call:
    ///   (a) every <c>Mirrored</c> ModalType is in <see cref="MirroredData"/> and vice versa — the table
    ///       records what the RAISER hands OpenModal, so declaring a kind Mirrored without knowing what its
    ///       data is fails here rather than in a co-op session;
    ///   (b) <c>Describe</c> over an instance of that data class never returns <c>Unsupported</c> — the one
    ///       value that means "no window for the peer";
    ///   (c) the two ANCHORS the generic arm was built for (a mission brief's <c>GeoMission</c>, a soldier
    ///       join's <c>GeoCharacter</c>) come out as <c>EntityRef</c>, so "reachable" cannot be satisfied by
    ///       quietly re-growing a bespoke shape per window;
    ///   (d) for every <c>EntityRef</c> payload the FULL round trip runs headless —
    ///       describe → encode → decode → <c>EntityData</c> — and must yield the peer's own object back,
    ///       with the wire preserving every field it was handed;
    ///   (e) the refusal arms: an unresolved ref, a ref that lands on a different class, and a payload with
    ///       no class at all must each return NULL data and a stated reason, never a throw and never a
    ///       window built over the wrong object;
    ///   (f) the seam — <c>BuildData</c> must actually route <c>EntityRef</c> through <c>EntityData</c> and
    ///       resolve off the peer's graph (<c>IdentityResolver.Resolve</c>), and <c>HostBroadcast</c> must
    ///       resolve the name it derived back on the host. Without (f) every arm above is a pure function
    ///       nobody calls.
    ///
    /// The samples are UNINITIALIZED instances (<c>FormatterServices</c>): the rail names an entity from id
    /// members alone, so a zeroed object exercises the real derivation without a live level — and the parts
    /// that DO need one (resolving a path against a graph) are exactly the parts split out as pure
    /// <c>EntityData</c> so they can be executed here at all.
    /// ponytail: (f) scans raw IL bytes for the callee's metadata token instead of decoding opcodes — same
    /// trade as L105's helper, lenient by construction and never too strict.
    /// </summary>
    internal static class L106_ModalEntityRaise
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>What the GAME's raiser hands <c>OpenModal</c>/<c>OpenModalPersistent</c> as the
        /// <c>object modalData</c> for each MIRRORED kind. Null = the raiser passes null. Read off the
        /// raisers, never off this rail's own table: GeoscapeView.cs:1984 (research complete), :1990
        /// (diplomacy share), :1965 (base activated, null), :1903 (every mission brief — the ten kinds
        /// GetMissionBriefModal:1724 picks between), HavenMissionUtil.cs:59 (soldier join).</summary>
        private static readonly Dictionary<ModalType, Type> MirroredData = new Dictionary<ModalType, Type>
        {
            // GeoResearchComplete RETURNED 2026-08-15 with its Mirrored verdict. It left on 2026-08-05
            // because the window had TWO producers on a client (this raise and ResearchSync's native present
            // off the mirrored state) and arrived twice, 225 ms apart; §A.9/L520 has since DELETED that
            // second producer, so the host's publication is the only one left and withholding it left the
            // window on the host's screen alone (L546). L49's two-producers-one-window arm still holds —
            // there is exactly one producer again, and it is this one.
            [ModalType.GeoResearchComplete] = typeof(GeoResearchCompleteData),
            [ModalType.DiplomacyResearchBrief] = typeof(DiplomacyResearchRewardData),
            [ModalType.GeoPhoenixBaseOutcome] = null,
            [ModalType.FactionSoldierJoin] = typeof(GeoCharacter),
            [ModalType.GeoHavenAttackBrief] = typeof(GeoMission),
            [ModalType.GeoAlienBaseBrief] = typeof(GeoMission),
            [ModalType.GeoScavengeBrief] = typeof(GeoMission),
            [ModalType.GeoPhoenixBaseDefenseBrief] = typeof(GeoMission),
            [ModalType.GeoAmbushBrief] = typeof(GeoMission),
            [ModalType.GeoPhoenixBaseInfestationBrief] = typeof(GeoMission),
            [ModalType.AncientSiteAttackBrief] = typeof(GeoMission),
            [ModalType.AncientSiteDefenceBrief] = typeof(GeoMission),
            [ModalType.BehemothAttackBrief] = typeof(GeoMission),
            [ModalType.InfestedHavenBrief] = typeof(GeoMission),
        };

        /// <summary>The kinds whose whole point is the GENERIC arm. Without this, "every Mirrored kind is
        /// describable" stays green while somebody hand-writes a fourth and a fifth bespoke shape.</summary>
        private static readonly ModalType[] EntityAnchors =
        {
            ModalType.GeoHavenAttackBrief, ModalType.FactionSoldierJoin,
        };

        internal static IEnumerable<string> Check()
        {
            foreach (var violation in SubjectGuard(new[] { typeof(GeoModalMirror) }))
                yield return violation;
            foreach (var violation in PositiveControls(typeof(GeoModalMirror)))
                yield return violation;

            var mirrored = GeoWindowCoverage.DeclaredModals
                .Where(kv => kv.Value != null && kv.Value.Sync == WindowSync.Mirrored)
                .Select(kv => kv.Key).OrderBy(m => (int)m).ToList();

            // ── (a) the table and the declaration must be the same set ─────
            if (mirrored.Count == 0)
            {
                yield return "L106 nothing-mirrored: no ModalType is declared Mirrored, so every arm of this " +
                             "law is a statement about the empty set.";
                yield break;
            }
            foreach (var m in mirrored.Where(m => !MirroredData.ContainsKey(m)))
                yield return "L106 mirrored-data-unknown: ModalType." + m + " is declared Mirrored but this law " +
                             "does not know what its raiser passes as modalData, so nothing checks that the peer " +
                             "can rebuild it. A kind declared Mirrored whose data no shape describes is refused " +
                             "at HostBroadcast and the peer silently never gets the window.";
            foreach (var m in MirroredData.Keys.Where(m => !mirrored.Contains(m)).OrderBy(m => (int)m))
                yield return "L106 mirrored-data-stale: this law still carries the modalData class for ModalType." +
                             m + ", which is no longer declared Mirrored — the table is describing a window that " +
                             "is not on the rail any more, and the kind that replaced it is unchecked.";

            foreach (var modal in mirrored.Where(MirroredData.ContainsKey))
            {
                var dataType = MirroredData[modal];
                object sample = null;
                if (dataType != null)
                {
                    sample = Sample(dataType, out string why);
                    if (sample == null)
                    {
                        yield return "L106 sample-unbuildable: no instance of " + dataType.Name + " could be made " +
                                     "for ModalType." + modal + " (" + why + "), so the round trip below never " +
                                     "ran and this kind is unchecked.";
                        continue;
                    }
                }

                // ── (b) describable at all ─────────────────────────────────
                var p = GeoModalMirror.Describe(sample);
                p.ModalType = (int)modal;
                p.Priority = 99;
                if (p.Shape == GeoModalMirror.DataShape.Unsupported)
                {
                    yield return "L106 mirrored-undescribable: ModalType." + modal + " is declared Mirrored but " +
                                 "GeoModalMirror.Describe returns Unsupported for its " + dataType.Name + ". " +
                                 "HostBroadcast then logs and returns, the host's own window opens as always, and " +
                                 "the peer gets NOTHING — the declaration says the opposite of what ships.";
                    continue;
                }

                // ── (c) the generic arm, not a fourth bespoke shape ────────
                if (EntityAnchors.Contains(modal) && p.Shape != GeoModalMirror.DataShape.EntityRef)
                    yield return "L106 anchor-not-generic: ModalType." + modal + " rides shape " + p.Shape +
                                 " instead of EntityRef. Its modalData IS a rail entity; describing it by hand " +
                                 "is how this family grew a case per window and left the next one uncovered.";

                if (p.Shape != GeoModalMirror.DataShape.EntityRef) continue;

                // ── (d) the wire, and then the peer's rebuild ──────────────
                if (string.IsNullOrEmpty(p.Ref))
                    yield return "L106 entity-unnamed: ModalType." + modal + " produced an EntityRef payload with " +
                                 "an EMPTY ref, which resolves to nothing on every peer.";
                if (p.Keys == null || p.Keys.Length != 1 || p.Keys[0] != sample.GetType().FullName)
                    yield return "L106 entity-classless: ModalType." + modal + "'s payload does not carry exactly " +
                                 "the modalData's class. The path grammar is type-blind, so the class is the only " +
                                 "thing that stops a ref landing on a different kind of object and throwing " +
                                 "inside the data-bind's cast.";
                // A ROOT names itself; a SUB-entity is named THROUGH the owner slot that holds it. Both are the
                // rail's own grammar, and asserting the shape of the derivation is what catches an invented key.
                var root = IdentityResolver.RootRef(sample);
                if (root != null && p.Ref != root)
                    yield return "L106 entity-renamed: ModalType." + modal + " ships '" + p.Ref + "' for an entity " +
                                 "the rail already names '" + root + "'. A second naming scheme resolves against " +
                                 "nothing the walk ever created.";
                if (root == null && !LooksLikeOwnerPath(p.Ref))
                    yield return "L106 subentity-unrooted: ModalType." + modal + " names a non-root entity '" +
                                 p.Ref + "', which is not a root ref plus a member path — IdentityResolver.Resolve " +
                                 "walks segments off a root key, so nothing else can ever resolve.";

                var back = GeoModalMirror.Decode(GeoModalMirror.Encode(7u, p), out uint seq);
                if (seq != 7u || back.ModalType != p.ModalType || back.Shape != p.Shape || back.Ref != p.Ref ||
                    back.Priority != p.Priority || (back.Keys ?? new string[0]).Length != p.Keys.Length ||
                    !(back.Keys ?? new string[0]).SequenceEqual(p.Keys))
                    yield return "L106 wire-lossy: the 0xB7 payload for ModalType." + modal + " does not survive " +
                                 "Encode/Decode intact. A dropped ref is a window resolved against nothing and a " +
                                 "dropped priority is two peers on different windows — neither shows up in a log.";

                string refusal;
                var rebuilt = GeoModalMirror.EntityData(back, sample, out refusal);
                if (refusal != null || !ReferenceEquals(rebuilt, sample))
                    yield return "L106 rebuild-null: ModalType." + modal + "'s payload does NOT rebuild to the " +
                                 "object the peer resolved (" + (refusal ?? "returned something else") + "). The " +
                                 "modal would be raised with null data and the prefab's data-bind dereferences it " +
                                 "unguarded inside EnterState.";

                // ── (e) the three refusals — no throw, no window, a reason ──
                GeoModalMirror.EntityData(back, null, out refusal);
                if (refusal == null)
                    yield return "L106 unresolved-accepted: ModalType." + modal + " accepts a ref this peer could " +
                                 "NOT resolve and hands the modal null data — the half-built prefab over the " +
                                 "designers' placeholder text, logged as a successful raise.";
                GeoModalMirror.EntityData(back, new object(), out refusal);
                if (refusal == null)
                    yield return "L106 wrong-class-accepted: ModalType." + modal + " accepts a ref that resolved to " +
                                 "a DIFFERENT class on this peer. The data-bind casts before it reads, so this is " +
                                 "a throw inside EnterState rather than a wrong picture.";
                var classless = back;
                classless.Keys = new string[0];
                GeoModalMirror.EntityData(classless, sample, out refusal);
                if (refusal == null)
                    yield return "L106 classless-accepted: ModalType." + modal + " accepts a payload carrying no " +
                                 "class name, so the class check above can be defeated by simply not sending one.";
            }

            // ── (f) the seams: the pure halves must be the ones that RUN ───
            var mirrorType = typeof(GeoModalMirror);
            var build = mirrorType.GetMethod("BuildData", All);
            var describe = mirrorType.GetMethod("Describe", All);
            var broadcast = mirrorType.GetMethod("HostBroadcast", All);
            var entityData = mirrorType.GetMethod("EntityData", All);
            var entityRefOf = mirrorType.GetMethod("EntityRefOf", All);
            var resolve = typeof(IdentityResolver).GetMethod("Resolve", All);
            if (build == null || describe == null || broadcast == null || entityData == null ||
                entityRefOf == null || resolve == null)
            {
                yield return "L106 seam-gone: GeoModalMirror.BuildData / Describe / HostBroadcast / EntityData / " +
                             "EntityRefOf or IdentityResolver.Resolve no longer exists — the arms above are " +
                             "testing functions that are no longer wired to anything.";
                yield break;
            }
            if (!References(describe, entityRefOf))
                yield return "L106 describe-bypasses-naming: GeoModalMirror.Describe does not call EntityRefOf, so " +
                             "the generic arm cannot be reached and every entity-shaped modal falls through to " +
                             "Unsupported.";
            if (!References(build, entityData))
                yield return "L106 build-bypasses-refusal: GeoModalMirror.BuildData does not call EntityData, so " +
                             "the resolve/class/refusal outcomes this law proves are a pure function nobody runs.";
            if (!References(build, resolve))
                yield return "L106 build-trusts-the-wire: GeoModalMirror.BuildData does not call " +
                             "IdentityResolver.Resolve. An EntityRef modal MUST be rebuilt off THIS peer's own " +
                             "mirrored graph; anything else means host state is being read out of the payload.";
            if (!References(broadcast, resolve))
                yield return "L106 host-name-unchecked: GeoModalMirror.HostBroadcast does not resolve the ref it " +
                             "derived back on the host's own graph. A derivation can mint a syntactically perfect " +
                             "key for an entity the rail never registered (a fresh GeoTacUnitId is 0, and \"U#0\" " +
                             "names nobody — or somebody else), and the peer would build a window over it.";
        }

        private static IEnumerable<string> SubjectGuard(Type[] subjects)
        {
            if (subjects == null || subjects.Length == 0)
                yield return "L106 premise-changed: an empty subject set was accepted, so modal entity raising can pass without inspecting GeoModalMirror.";
            else if (Array.Exists(subjects, t => t == null))
                yield return "L106 premise-changed: an unresolved subject was accepted, so a missing modal mirror can make the law vacuous.";
        }

        private static IEnumerable<string> PositiveControls(Type subject)
        {
            if (!HasViolation(SubjectGuard(new Type[0])))
                yield return "L106 control-empty-subject: the executable subject guard did not reject an empty set.";
            if (!HasViolation(SubjectGuard(new Type[] { null })))
                yield return "L106 control-unresolved-subject: the executable subject guard did not reject an unresolved type.";
            if (HasViolation(SubjectGuard(new[] { subject })))
                yield return "L106 control-valid-subject: GeoModalMirror was rejected by the subject guard, so the executable checks never reached production code.";
        }

        private static bool HasViolation(IEnumerable<string> violations) => violations.GetEnumerator().MoveNext();

        /// <summary>A root ref is one segment; a sub-entity ref is a root plus the member path to the slot
        /// that owns it. Anything else cannot be walked by IdentityResolver.Resolve at all.</summary>
        private static bool LooksLikeOwnerPath(string r) =>
            !string.IsNullOrEmpty(r) && r.IndexOf('.') > 0 && r.IndexOf('#') > 0 &&
            r.IndexOf('#') < r.IndexOf('.');

        /// <summary>An instance the rail can be asked to NAME, without a live level: uninitialized memory is
        /// enough because naming reads id members only. An abstract data class (GeoMission) is stood in for by
        /// the first concrete subclass the game ships — which is also what the raiser hands over.</summary>
        private static object Sample(Type t, out string why)
        {
            why = null;
            if (t.IsAbstract)
            {
                Type[] all;
                try { all = t.Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(x => x != null).ToArray(); }
                t = all.FirstOrDefault(x => !x.IsAbstract && !x.IsGenericTypeDefinition && t.IsAssignableFrom(x));
                if (t == null) { why = "no concrete subclass in the game assembly"; return null; }
            }
            object o;
            try { o = FormatterServices.GetUninitializedObject(t); }
            catch (Exception ex) { why = ex.GetType().Name; return null; }
            // The owner has to exist or the sub-entity rung is unnameable BY DESIGN and this would be
            // asserting the refusal path instead of the naming one.
            var site = t.GetProperty("Site", All);
            if (site != null && site.CanWrite && site.CanRead && site.GetValue(o, null) == null)
            {
                try { site.SetValue(o, FormatterServices.GetUninitializedObject(site.PropertyType), null); }
                catch (Exception ex) { why = "owner slot: " + ex.GetType().Name; return null; }
            }
            return o;
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Raw 4-byte metadata
        /// token scan — see the ponytail note on the class.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            int token = callee.MetadataToken;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
