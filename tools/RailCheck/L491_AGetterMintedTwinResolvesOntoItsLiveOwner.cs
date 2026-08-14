using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>
    /// L491 — A GETTER-MINTED TWIN RESOLVES ONTO ITS LIVE OWNER.
    ///
    /// THE FAILURE (2026-08-14 session, both clients). <c>GeoHavenDefenseMission</c> declares no serialized
    /// fields of its own: its whole state persists through ONE hand-written property
    /// (decompile GeoHavenDefenseMission.cs:80-91 — <c>get => RecordInstanceData()</c>,
    /// <c>set => ProcessInstanceData(value)</c>), so the rail carries it as 12 leaves under
    /// <c>S#&lt;id&gt;.SerializationData.ActiveMission.InstanceData</c> (docs/rail-baseline.txt:280-292):
    /// AttackedZoneDef, Status, both deployments, AttackingSites, MissionSeed. Every one of them was lost
    /// on every client. The resolver's twin arm keyed on <c>IInstanceData</c>, which this DTO does not
    /// implement, so the walk CALLED the getter — and <c>RecordInstanceData</c> reads
    /// <c>ObjectiveType =&gt; _attackedZoneDef.HavenDefenseObjective</c> (:63), which NREs on a client whose
    /// <c>_attackedZoneDef</c> has not landed. The path answered null and the applier logged
    /// <c>entity not found: S#120.SerializationData.ActiveMission.InstanceData</c>, 16x per client. It can
    /// never heal: the field that would let the getter succeed rides the path the getter breaks. Had the
    /// getter succeeded it would have been WORSE — the write lands in the throwaway DTO with no line at
    /// all. TFTV read the same never-landed field and threw on the client that hovered the haven
    /// (<c>AttackedZone =&gt; Haven.Zones.First(z =&gt; z.Def == _attackedZoneDef)</c> :51).
    ///
    /// THE RULE. Every member whose getter MINTS its value must resolve onto the LIVE owner
    /// (<c>IdentityResolver.IsRecordedTwin</c> → <c>RailType.GetBridged</c>), never onto the minted object.
    /// The reachable set is SWEPT here, not listed: every game type carrying a hand-written property typed
    /// as its own record DTO (<c>RailMeta.FindBridge</c>) must be accepted by that predicate. A future
    /// class with the same shape is covered the day it ships.
    ///
    /// THE ARMS:
    ///   (a) <c>sweep</c> — every getter-minted twin found in Assembly-CSharp must be accepted.
    ///   (b) <c>premise</c> (executable guard, anti-vacuity) — the sweep must find the shape at all, and
    ///       must find <c>GeoHavenDefenseMission.InstanceData</c> among it.
    ///   (c) <c>storage-gate</c> — the same (live, DTO) pair asked about a FIELD member must be REFUSED.
    ///       A field (or auto-property) is real storage: its getter hands back the object it holds, so a
    ///       write into it lands, and redirecting it would lose a genuine object write.
    ///   (d) <c>landing</c> — a redirect that resolves NO member of the DTO onto the live type lands
    ///       nothing; at least one member of each swept DTO must resolve through
    ///       <c>RailMeta.ResolveLive</c> (per-member gaps stay visible at runtime as the applier's
    ///       "dto-twin gap" line).
    ///
    /// Falsify: narrow <c>IsRecordedTwin</c> back to the <c>IInstanceData</c> test → (a); drop the
    /// storage gate so any DTO-typed member is redirected → (c).
    /// </summary>
    internal static class L491_AGetterMintedTwinResolvesOntoItsLiveOwner
    {
        internal static IEnumerable<string> Check()
        {
            var swept = new List<(Type Live, Type Dto, PropertyInfo Member)>();
            foreach (var t in GameTypes())
            {
                PropertyInfo[] props;
                try { props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                                              BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                Type bridge = null;
                bool probed = false;
                foreach (var pi in props)
                {
                    var pt = pi.PropertyType;
                    // Cheap pre-filter so FindBridge (two assembly probes + a method lookup per type) runs
                    // only for candidates: the game names every record DTO *InstanceData (or the shipped
                    // typo *InstaceData). FindBridge is still the ANSWER — this is only the door to it.
                    if (pt == null || !pt.IsClass ||
                        (!pt.Name.EndsWith("InstanceData", StringComparison.Ordinal) &&
                         !pt.Name.EndsWith("InstaceData", StringComparison.Ordinal))) continue;
                    if (pi.GetGetMethod(true) == null || pi.GetSetMethod(true) == null) continue;
                    if (t.GetField("<" + pi.Name + ">k__BackingField",
                            BindingFlags.Instance | BindingFlags.NonPublic) != null) continue; // real storage
                    if (!probed) { probed = true; try { bridge = RailMeta.FindBridge(t); } catch { bridge = null; } }
                    if (bridge != null && pt == bridge) swept.Add((t, pt, pi));
                }
            }

            // ── arm (b): the executable guard. A sweep that finds nothing would pass arms (a)/(c)/(d)
            // forever, and the one member this law exists for must be inside what it found.
            if (swept.Count == 0)
            {
                yield return "L491 premise-changed: the Assembly-CSharp sweep found NO getter-minted record " +
                             "twin at all. Either RailMeta.FindBridge stopped resolving *InstanceData siblings " +
                             "or the game renamed the shape — this law then proves nothing; re-point it before " +
                             "believing the rail carries mission state.";
                yield break;
            }
            if (!swept.Any(s => s.Live == typeof(GeoHavenDefenseMission) &&
                                s.Dto == typeof(GeoHavenDefenseMissionInstanceData)))
            {
                yield return "L491 premise-changed: GeoHavenDefenseMission.InstanceData is no longer swept as a " +
                             "getter-minted twin (found " + swept.Count + " other pairs). That member is the " +
                             "entire persistence of a haven-defence mission; if the game changed its shape, " +
                             "re-ground this law on the new one.";
                yield break;
            }

            foreach (var s in swept)
            {
                // ── arm (a): the claim.
                if (!IdentityResolver.IsRecordedTwin(s.Live, s.Member, s.Dto))
                    yield return "L491 sweep: " + s.Live.Name + "." + s.Member.Name + " mints a " + s.Dto.Name +
                                 " on every read (its getter records a throwaway), but IdentityResolver" +
                                 ".IsRecordedTwin REFUSES it — so the path resolves onto the minted object " +
                                 "instead of the live owner and every entry under it is either dropped as " +
                                 "\"entity not found\" or written into a copy nobody reads.";

                // ── arm (c): the storage gate is real, asked with the SAME pair.
                var anyField = s.Dto.GetFields(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault();
                if (anyField != null && IdentityResolver.IsRecordedTwin(s.Live, anyField, s.Dto) &&
                    !typeof(Base.Core.IInstanceData).IsAssignableFrom(s.Dto))
                    yield return "L491 storage-gate: IsRecordedTwin accepts a FIELD member for the pair (" +
                                 s.Live.Name + ", " + s.Dto.Name + "). A field holds the object it returns, so " +
                                 "a write into it lands — redirecting it onto the live owner drops a genuine " +
                                 "object write.";

                // ── arm (d): the redirect has somewhere to land.
                var resolved = s.Dto.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Cast<MemberInfo>()
                    .Concat(s.Dto.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    .Count(m => RailMeta.ResolveLive(s.Live, m.Name, RailMeta.MemberType(m), out _, out _) != null);
                if (resolved == 0)
                    yield return "L491 landing: not one member of " + s.Dto.Name + " resolves onto " + s.Live.Name +
                                 " (RailMeta.ResolveLive). The resolver redirects the path onto the live owner, " +
                                 "so with an empty twin table the subtree lands NOTHING — the same silent loss " +
                                 "the redirect exists to end.";
            }
        }

        /// <summary>Assembly-CSharp's types, tolerant of the load failures a console host hits on Unity
        /// modules the harness does not reference (the same shape every reflecting law here handles).</summary>
        private static IEnumerable<Type> GameTypes()
        {
            Type[] types;
            try { types = typeof(GeoHavenDefenseMission).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            foreach (var t in types)
                if (t != null && t.IsClass) yield return t;
        }
    }
}
