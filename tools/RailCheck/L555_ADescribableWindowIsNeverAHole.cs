using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Common.Utils;

namespace RailCheck
{
    /// <summary>
    /// L555 — A DESCRIBABLE WINDOW IS NEVER A HOLE, AND EVERY HOLE STATES A MACHINE-CHECKABLE REASON.
    ///
    /// THE REPORT (2026-08-15, third defect of the day): the Pandoran-reconnaissance window appeared on
    /// the host and on nobody else. It is vanilla <c>ModalType.PandoranRevealResult</c> (TFTV only
    /// DECORATES it — <c>AircraftReworkGeoscape.Scanning.cs</c>:672-675 rewrites the description text and
    /// :766 patches <c>GeoAlienFaction.TryRevealAlienBase</c>; it adds no ModalType and no view state), so
    /// the missing window was OUR declaration and not a third-party blind spot: the coverage table declared
    /// it <see cref="WindowSync.Gap"/>.
    ///
    /// THE STRUCTURAL DEFECT BEHIND IT is not that one entry. It is that <c>Gap</c> was a HAND-WRITTEN
    /// verdict with a PROSE reason and nothing anywhere compared it against the one question that actually
    /// decides whether a window can travel: <c>GeoModalMirror.Describe</c> either resolves the raiser's
    /// <c>modalData</c> to a rail root / a described shape, or it falls to
    /// <see cref="GeoModalMirror.DataShape.Unsupported"/> and <c>HostBroadcast</c> ships nothing. That is a
    /// READABLE PROPERTY OF THE PAYLOAD, not a policy choice — so a hole over a describable payload is an
    /// announced hole that never needed to exist, and its prose reason rots silently (this one's did: it
    /// claimed 0xBB "carries Resources + Items, not ... revealed sites", while
    /// <c>MissionOutcomeMirror.RowKinds</c> row 3 has shipped <c>RevealedSites</c> the whole time).
    ///
    /// THE ASYMMETRY THIS LAW INSTALLS. A hole may exist ONLY where the payload cannot be described, and it
    /// must say WHICH payload — the runtime class the raiser hands to <c>OpenModal</c> — so the claim is
    /// re-checkable by machine on every run instead of by a human re-reading a paragraph. Holes become
    /// DERIVED. A window somebody adds tomorrow, from a DLC or from another mod, is classified the day it
    /// ships and cannot be parked behind a sentence.
    ///
    /// ARMS:
    ///   (a) gap-without-a-witness — EXECUTED over the real coverage tables: every <c>Gap</c> declaration
    ///       must carry the payload CLASS its raiser hands over (<c>WindowRule.GapDataClass</c>), and no
    ///       non-Gap declaration may carry one. A hole with no witness is a paragraph, not a claim.
    ///   (b) describable-window-declared-a-hole — EXECUTED: <c>GeoModalMirror.CanDescribeClass</c> must be
    ///       FALSE for every declared witness. This is the arm that would have caught the reported defect
    ///       on the day it shipped — a <c>GeoSite</c> is a rail root, so the reveal was always describable.
    ///   (c) hole-reason-is-not-a-window-list — the arm that keeps the derivation a DERIVATION. The
    ///       predicate must take a <c>Type</c> and return <c>bool</c> (a signature in which a per-ModalType
    ///       enumeration is unexpressible), must not mention <c>ModalType</c> or the coverage table at all,
    ///       and must reach the repo's one definition of "the rail can name this",
    ///       <c>IdentityResolver.IsRefAddressableType</c>, rather than keeping a private copy of it.
    ///       It must also be TOTAL and non-constant: <c>null</c> and an unknown third-party class answer
    ///       "not describable" (the safe, loud default) without throwing, while a rail root answers yes.
    ///   (d) describe-drifted-from-the-derivation — the anti-rot arm. Every type <c>Describe</c> and
    ///       <c>EntityRefOf</c> actually test with <c>isinst</c> must be accepted by
    ///       <c>CanDescribeClass</c>. Adding a shape to <c>Describe</c> therefore cannot silently leave the
    ///       hole predicate behind, which is exactly how the stale reasons above were written.
    ///   (e) hole-is-announced-with-its-witness — EXECUTED + IL: the runtime announcement for a hole must
    ///       be built by one pure function that NAMES the witness class, and
    ///       <c>GeoWindowCoverage.AnnounceModal</c> must reach it. A hole the player and the log cannot
    ///       see is the silent swallow this whole file exists to forbid.
    ///
    /// ROLES SEPARATED (§C.3): every arm is a role-free pure call or a statement about the shipped
    /// assembly. Nothing here waits on a peer — no quorum.
    ///
    /// Falsify (compile-valid src mutations, each named): declare <c>ModalType.PandoranRevealResult</c>
    /// <c>Gap</c> again with <c>typeof(GeoSite)</c> as its witness → (b); drop the witness argument from
    /// any surviving Gap declaration → (a); write
    /// <c>CanDescribeClass(Type t) =&gt; t == typeof(GeoResearchCompleteData);</c> → (c) and (d);
    /// remove the <c>HoleAnnouncement</c> call from <c>AnnounceModal</c> → (e).
    /// </summary>
    internal static class L555_ADescribableWindowIsNeverAHole
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var witnessField = typeof(WindowRule).GetField("GapDataClass", Any);
            var canDescribe = typeof(GeoModalMirror).GetMethod("CanDescribeClass", Any);
            var announcement = typeof(GeoWindowCoverage).GetMethod("HoleAnnouncement", Any);

            // ── (c) THE DERIVATION IS A DERIVATION ───────────────────────────
            Func<Type, bool> describable = null;
            if (canDescribe == null)
            {
                yield return "L555 hole-reason-is-not-a-window-list: GeoModalMirror.CanDescribeClass did not " +
                             "resolve. 'Can this window travel?' must be a readable property of the payload's " +
                             "own class — the same question GeoModalMirror.Describe already answers for a live " +
                             "object — so that a hole can be DERIVED instead of hand-declared with a paragraph " +
                             "nothing re-checks.";
            }
            else
            {
                var ps = canDescribe.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(Type) ||
                    canDescribe.ReturnType != typeof(bool))
                {
                    yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass must be " +
                                 "(Type) -> bool. A signature that can see a ModalType, a window name or the " +
                                 "coverage table can express a per-window list, and a per-window list is stale " +
                                 "the day the game, a DLC or another mod adds a window — silently, because the " +
                                 "unlisted window simply keeps whatever verdict somebody typed.";
                }
                else
                {
                    describable = t => (bool)canDescribe.Invoke(null, new object[] { t });
                    if (Il.MentionsAnyString(canDescribe, new[] { "ModalType", "GeoWindowCoverage" }))
                        yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass mentions the " +
                                     "window namespace (ModalType / the coverage table). It must read the " +
                                     "PAYLOAD's class and nothing else.";
                    var addressable = typeof(IdentityResolver).GetMethod("IsRefAddressableType", Any);
                    if (addressable == null || !Il.References(canDescribe, addressable))
                        yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass does not reach " +
                                     "IdentityResolver.IsRefAddressableType, so it is deciding 'the rail can " +
                                     "name this' by a private second copy of the rail's own addressing rule. " +
                                     "Two copies of law 2 drift, and the drift is a window declared a hole " +
                                     "over a payload the rail has been able to name all along.";

                    // TOTAL, NON-CONSTANT, SAFE-BY-DEFAULT — asked of the payload space, not of a window.
                    bool threw = false;
                    bool nullVerdict = false, unknownVerdict = false, rootVerdict = false, missionVerdict = false;
                    try
                    {
                        nullVerdict = describable(null);
                        unknownVerdict = describable(typeof(L555_ADescribableWindowIsNeverAHole));
                        rootVerdict = describable(typeof(GeoSite));
                        missionVerdict = describable(typeof(GeoMission));
                    }
                    catch { threw = true; }
                    if (threw)
                        yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass threw on part of " +
                                     "the payload space. A class this build has never seen — a DLC's or another " +
                                     "mod's window — must get a VERDICT, not an exception.";
                    else
                    {
                        if (nullVerdict)
                            yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass says a NULL " +
                                         "payload class is describable. Null means 'nothing raises this, so no " +
                                         "payload exists to describe' — the safe answer is no, and it must be " +
                                         "loud rather than an accidental green.";
                        if (unknownVerdict)
                            yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass says an " +
                                         "arbitrary unknown class is describable. Describe would fall to " +
                                         "DataShape.Unsupported for it and HostBroadcast would ship nothing, so " +
                                         "the verdict must be no.";
                        if (!rootVerdict)
                            yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass says a GeoSite " +
                                         "— a rail ROOT that IdentityResolver.RootRef names on rung one — is not " +
                                         "describable. The predicate has gone constant-false, which would make " +
                                         "every hole legal and this law vacuous.";
                        if (!missionVerdict)
                            yield return "L555 hole-reason-is-not-a-window-list: CanDescribeClass says a " +
                                         "GeoMission is not describable, but GeoModalMirror.EntityRefOf names one " +
                                         "by the owner-slot path its site already addresses it with " +
                                         "(S#<id>.SerializationData.ActiveMission).";
                    }
                }
            }

            // ── (d) THE DERIVATION TRACKS Describe ITSELF ───────────────────
            if (describable != null)
            {
                foreach (var t in ShapeCasedTypes())
                    if (!describable(t))
                        yield return "L555 describe-drifted-from-the-derivation: GeoModalMirror.Describe (or " +
                                     "EntityRefOf) has a case for '" + t.FullName + "', so the host CAN put that " +
                                     "payload on the wire, but CanDescribeClass says it cannot be described. A " +
                                     "shape added to Describe must reach the hole predicate in the same commit, " +
                                     "or the next hole declared over that payload is legal and wrong.";
            }

            // ── (a)+(b) THE TABLES THEMSELVES ───────────────────────────────
            foreach (var v in CheckTable(witnessField, describable,
                                         GeoWindowCoverage.DeclaredModals
                                             .OrderBy(kv => (int)kv.Key)
                                             .Select(kv => new KeyValuePair<string, WindowRule>(
                                                 "modal '" + kv.Key + "'", kv.Value))))
                yield return v;
            foreach (var v in CheckTable(witnessField, describable,
                                         GeoWindowCoverage.Declared
                                             .OrderBy(kv => kv.Key.Name, StringComparer.Ordinal)
                                             .Select(kv => new KeyValuePair<string, WindowRule>(
                                                 "state '" + kv.Key.Name + "'", kv.Value))))
                yield return v;

            // VACUITY GUARD: arms (a), (b) and (e) all read the HOLES, so a table with none in it would
            // report a serene green while checking nothing. Say so instead.
            if (!GeoWindowCoverage.DeclaredModals.Values.Any(r => r != null && r.Sync == WindowSync.Gap))
                yield return "L555 premise-changed: the modal coverage table declares no hole at all, so arms " +
                             "(a), (b) and (e) examined nothing and this law's green means only that there was " +
                             "nothing to look at. Either every window now travels — in which case delete the " +
                             "Gap verdict itself rather than leaving a law guarding an empty set — or the " +
                             "table this law reads has moved.";

            // ── (e) THE HOLE IS ANNOUNCED WITH ITS WITNESS ──────────────────
            if (announcement == null)
            {
                yield return "L555 hole-is-announced-with-its-witness: GeoWindowCoverage.HoleAnnouncement did " +
                             "not resolve. A hole must be announced in ONE pure sentence that names the payload " +
                             "class it rests on, so the log line and the law read the same claim — a gap the " +
                             "player can see must never be a gap only the player can see.";
            }
            else
            {
                var rule = new WindowRule { Sync = WindowSync.Gap, Why = "probe" };
                if (witnessField != null) witnessField.SetValue(rule, typeof(GeoSite));
                string said = null;
                try { said = announcement.Invoke(null, new object[] { "probe-window", rule }) as string; }
                catch { }
                if (said == null || said.IndexOf("GeoSite", StringComparison.Ordinal) < 0)
                    yield return "L555 hole-is-announced-with-its-witness: HoleAnnouncement did not name the " +
                                 "declared payload class in its sentence, so the runtime line says a window is " +
                                 "missing without saying what it is missing over — which is the same unreviewable " +
                                 "paragraph the declaration used to be.";
                var announceModal = typeof(GeoWindowCoverage).GetMethod("AnnounceModal", Any);
                if (announceModal == null || !Il.References(announceModal, announcement))
                    yield return "L555 hole-is-announced-with-its-witness: GeoWindowCoverage.AnnounceModal does " +
                                 "not reach HoleAnnouncement, so the derived reason is computed and then not " +
                                 "said. A discard is never silent (L546), and neither is a hole.";
            }
        }

        private static IEnumerable<string> CheckTable(FieldInfo witnessField, Func<Type, bool> describable,
                                                      IEnumerable<KeyValuePair<string, WindowRule>> rows)
        {
            foreach (var row in rows)
            {
                var rule = row.Value;
                if (rule == null) continue;
                if (witnessField == null)
                {
                    if (rule.Sync == WindowSync.Gap)
                        yield return "L555 gap-without-a-witness: " + row.Key + " is declared a HOLE, and " +
                                     "WindowRule has no GapDataClass for it to name the payload its raiser " +
                                     "hands over. The hole rests on a prose sentence nothing re-checks, which " +
                                     "is how it survived a rail that had long since learned to carry the state " +
                                     "behind it.";
                    continue;
                }
                var witness = witnessField.GetValue(rule) as Type;
                if (rule.Sync != WindowSync.Gap)
                {
                    if (witness != null)
                        yield return "L555 gap-without-a-witness: " + row.Key + " is declared " + rule.Sync +
                                     " and still carries a hole witness ('" + witness.FullName + "'). A witness " +
                                     "is the evidence for a HOLE; on anything else it is a leftover that will be " +
                                     "read as one.";
                    continue;
                }
                if (witness == null)
                {
                    yield return "L555 gap-without-a-witness: " + row.Key + " is declared a HOLE with no " +
                                 "payload class. Name the runtime class the raiser hands to OpenModal — or " +
                                 "typeof(void) when nothing in the shipped assembly raises it at all — so the " +
                                 "claim 'this cannot travel' is one a machine re-checks every run.";
                    continue;
                }
                if (describable != null && describable(witness))
                    yield return "L555 describable-window-declared-a-hole: " + row.Key + " is declared a HOLE " +
                                 "over a '" + witness.FullName + "', which GeoModalMirror.Describe CAN put on " +
                                 "the wire — the rail names it, and a peer rebuilds the window off its own " +
                                 "graph. An announced hole over a describable payload is a window withheld by " +
                                 "declaration alone: exactly the Pandoran-reveal defect. Declare it Mirrored " +
                                 "(or LocalOnly where this peer's own game truly raises its own) and let it " +
                                 "travel.";
            }
        }

        /// <summary>Every type the host's own describe path actually TESTS FOR — read out of the IL of
        /// <c>Describe</c> and <c>EntityRefOf</c> rather than copied, so the anti-drift arm cannot itself
        /// drift. A C# type-pattern switch compiles to <c>isinst</c> (0x75) plus a type token.</summary>
        private static IEnumerable<Type> ShapeCasedTypes()
        {
            var seen = new List<Type>();
            foreach (var name in new[] { "Describe", "EntityRefOf" })
            {
                var m = typeof(GeoModalMirror).GetMethod(name, Any);
                var il = Il.Body(m);
                if (il == null) continue;
                for (int i = 0; i + 5 <= il.Length; i++)
                {
                    if (il[i] != 0x75) continue;   // isinst
                    Type t = null;
                    try { t = m.Module.ResolveType(BitConverter.ToInt32(il, i + 1)); } catch { }
                    if (t != null && !t.IsInterface && !seen.Contains(t)) seen.Add(t);
                }
            }
            return seen.OrderBy(t => t.FullName, StringComparer.Ordinal);
        }

    }
}
