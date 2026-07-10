using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the personnel roster channel (#9) + the #6 vehicle crew tail (PS1). The mod
    /// has NO compile-time game references, so every member is resolved by name and cached (mirrors
    /// <see cref="GeoSiteReflection"/> / <see cref="GeoVehicleIdentityReflection"/>). Decompile anchors
    /// (taxonomy 2026-07-05 §1/§2): both containers keep their roster in a private
    /// <c>IList&lt;GeoCharacter&gt; _tacUnits</c> (GeoVehicle.cs:65 / GeoSite.cs:65); the shared id is
    /// <c>GeoCharacter.Id</c> → <c>GeoTacUnitId._id</c> (private readonly int — the SAME id the
    /// SoldierAssignment 0x41 rail keys on); the Phoenix containers are <c>GeoFaction.Sites</c>/<c>
    /// Vehicles</c> (GeoFaction.cs:135/137, map-ownership views).
    ///
    /// HOST side reads rosters (site walk for #9, per-vehicle crew for the #6 poll); CLIENT side applies
    /// VALUE-ONLY: mutate the container's <c>_tacUnits</c> list directly via <see cref="RosterReconcile"/>
    /// — NEVER native <c>AddCharacter</c>/<c>RemoveCharacter</c> (their OnCharacterAdded/space-recompute
    /// cascade is sim on the frozen mirror). All reflection is null-safe best-effort: a miss logs and
    /// degrades (skips that container/record), never throws into the apply loop.
    /// </summary>
    public static class PersonnelReflection
    {
        private static PropertyInfo _sitesProp;        // GeoFaction.Sites (IEnumerable<GeoSite>)
        private static PropertyInfo _vehiclesProp;     // GeoFaction.Vehicles (IEnumerable<GeoVehicle>)
        private static PropertyInfo _vehicleOwnerProp; // GeoVehicle.Owner (GeoFaction)
        private static PropertyInfo _charIdProp;       // GeoCharacter.Id (GeoTacUnitId)
        private static FieldInfo _tacIdField;          // GeoTacUnitId._id (private readonly int)
        // _tacUnits is declared separately on GeoVehicle AND GeoSite (same name) → cache per concrete type.
        private static readonly Dictionary<Type, FieldInfo> _tacUnitsByType = new Dictionary<Type, FieldInfo>();

        private static void EnsureFaction(object faction)
        {
            if (_sitesProp != null || faction == null) return;
            // Declared on GeoFaction; the live instance is GeoPhoenixFaction — AccessTools walks the base chain.
            _sitesProp = AccessTools.Property(faction.GetType(), "Sites");
            _vehiclesProp = AccessTools.Property(faction.GetType(), "Vehicles");
        }

        /// <summary>The container's live <c>_tacUnits</c> roster list, or null (miss degrades).</summary>
        private static IList GetTacUnits(object container)
        {
            if (container == null) return null;
            var t = container.GetType();
            FieldInfo f;
            if (!_tacUnitsByType.TryGetValue(t, out f))
            {
                f = AccessTools.Field(t, "_tacUnits");
                _tacUnitsByType[t] = f;   // cache the miss too (null) — no re-probe per call
            }
            try { return f?.GetValue(container) as IList; }
            catch { return null; }
        }

        /// <summary>A soldier's shared <c>GeoUnitId</c> (GeoCharacter.Id → GeoTacUnitId._id), 0 = unresolved
        /// (0 == GeoTacUnitId.None — never a valid roster soldier id).</summary>
        public static long ReadUnitId(object geoCharacter)
        {
            if (geoCharacter == null) return 0;
            try
            {
                if (_charIdProp == null) _charIdProp = AccessTools.Property(geoCharacter.GetType(), "Id");
                object gid = _charIdProp?.GetValue(geoCharacter, null);
                if (gid == null) return 0;
                if (_tacIdField == null) _tacIdField = AccessTools.Field(gid.GetType(), "_id");
                if (_tacIdField != null) return (int)_tacIdField.GetValue(gid);
                return Convert.ToInt32(gid);   // fallback: implicit int conversion (TacticalActorAdapter precedent)
            }
            catch { return 0; }
        }

        /// <summary>HOST (#6 crew poll): the ordered GeoUnitIds of a PHOENIX-owned vehicle's <c>_tacUnits</c>.
        /// False for a non-Phoenix vehicle (crew scope = the shared faction only, spec §1) or when the
        /// roster list is unreachable — the caller then tracks no crew for that vehicle.</summary>
        public static bool TryReadCrewIds(GeoRuntime rt, object vehicle, out long[] ids)
        {
            ids = null;
            try
            {
                if (vehicle == null) return false;
                var fac = rt?.PhoenixFaction();
                if (fac == null) return false;
                if (_vehicleOwnerProp == null) _vehicleOwnerProp = AccessTools.Property(vehicle.GetType(), "Owner");
                object owner = _vehicleOwnerProp?.GetValue(vehicle, null);
                if (!ReferenceEquals(owner, fac)) return false;
                var list = GetTacUnits(vehicle);
                if (list == null) return false;
                ids = ReadIds(list);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] PersonnelReflection.TryReadCrewIds failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>HOST (#9 flush): every Phoenix site's ordered roster ids (full set — the client
        /// reconciles each container to the exact mirrored membership). Empty when not in geoscape.</summary>
        public static List<PersonnelSiteRoster> SnapshotSiteRosters(GeoRuntime rt)
        {
            var res = new List<PersonnelSiteRoster>();
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return res;
                EnsureFaction(fac);
                if (!(_sitesProp?.GetValue(fac, null) is IEnumerable sites)) return res;
                foreach (var site in sites)
                {
                    if (site == null) continue;
                    int siteId = GeoSiteReflection.GetSiteId(site);
                    if (siteId < 0) continue;
                    var list = GetTacUnits(site);
                    if (list == null) continue;
                    res.Add(new PersonnelSiteRoster(siteId, ReadIds(list)));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.SnapshotSiteRosters failed: " + ex.Message); }
            return res;
        }

        /// <summary>CLIENT: index every live Phoenix soldier across ALL containers (vehicles + sites):
        /// GeoUnitId → instance, and instance → its current roster list (for remove-from-old-before-add).
        /// Built once per channel apply; <see cref="RosterReconcile"/> Contains-guards stale entries.</summary>
        public sealed class CharacterIndex
        {
            public readonly Dictionary<long, object> ById = new Dictionary<long, object>();
            public readonly Dictionary<object, IList> ContainerOf = new Dictionary<object, IList>();
        }

        public static CharacterIndex BuildCharacterIndex(GeoRuntime rt)
        {
            var index = new CharacterIndex();
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return index;
                EnsureFaction(fac);
                EnsureOrphanPoolBinding(fac);
                IndexContainers(_vehiclesProp?.GetValue(fac, null) as IEnumerable, index);
                IndexContainers(_sitesProp?.GetValue(fac, null) as IEnumerable, index);
                MergeOrphanPool(index);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.BuildCharacterIndex failed: " + ex.Message); }
            return index;
        }

        // ─── Client orphan pool (cross-channel transfer race, RCA 2026-07-09) ───
        // Site rosters ride #9 and vehicle crews ride #6 as SEPARATE applies, each with a fresh
        // container-scan index: a site→vehicle transfer's #9 apply removes the soldier from his site
        // first, and the #6 crew apply ticks later can no longer resolve his id — the instance ends in
        // NO container. Removed-and-unclaimed instances park in PersonnelOrphanPool; the merge below
        // makes every subsequent index (and therefore every #6/#9 reconcile + PS2 live-state resolve)
        // see them, and the reconcile that places one back into a container adopts it (pool entry
        // dropped). The #6 crew map re-emits in FULL every flush, so an already-orphaned unit heals on
        // the next flush that references it. Pool is populated ONLY by the client apply paths
        // (ReconcileInto); on the host it is always empty, so the merge is a no-op there.

        // Rebind-by-instance (the PersonnelChannel.AttachHost / WalletWatcher idiom): a fresh
        // GeoPhoenixFaction (geoscape reload, tactical round-trip, new session) means every parked
        // instance belongs to a DEAD level — never adopt one across the boundary.
        private static object _orphanPoolFaction;

        private static void EnsureOrphanPoolBinding(object faction)
        {
            if (ReferenceEquals(faction, _orphanPoolFaction)) return;
            ResetOrphanPool("faction rebind");
            _orphanPoolFaction = faction;
        }

        /// <summary>Session/reload seam: drop every parked orphan + the faction binding. Ids never
        /// re-claimed (dismissed/dead soldiers — the host's rosters never reference them again, so a
        /// parked instance can never resurrect one) are logged ONCE here and dropped.</summary>
        public static void ResetOrphanPool(string reason)
        {
            _orphanPoolFaction = null;
            var dropped = PersonnelOrphanPool.Reset();
            if (dropped.Count > 0)
                Debug.Log("[Multiplayer] PersonnelReflection: orphan pool reset (" + reason + ") — dropped "
                          + dropped.Count + " never-reclaimed GeoUnitId(s) [" + string.Join(",", dropped) + "]");
        }

        // An id NO live container claims resolves to its parked instance (deliberately no ContainerOf
        // entry — it sits in no container, so remove-from-old is a no-op); an id a container claims
        // again is superseded — the live instance won, drop the pool entry.
        private static void MergeOrphanPool(CharacterIndex index)
        {
            if (PersonnelOrphanPool.Count == 0) return;
            foreach (var kv in PersonnelOrphanPool.SnapshotEntries())
            {
                if (index.ById.ContainsKey(kv.Key))
                {
                    PersonnelOrphanPool.Evict(kv.Key);
                    Debug.Log("[Multiplayer] PersonnelReflection: GeoUnitId " + kv.Key
                              + " live in a container again — orphan pool entry dropped (superseded)");
                }
                else index.ById[kv.Key] = kv.Value;
            }
        }

        private static void IndexContainers(IEnumerable containers, CharacterIndex index)
        {
            if (containers == null) return;
            foreach (var c in containers)
            {
                var list = GetTacUnits(c);
                if (list == null) continue;
                foreach (var ch in list)
                {
                    long id = ReadUnitId(ch);
                    if (id == 0 || ch == null) continue;
                    if (!index.ById.ContainsKey(id)) index.ById[id] = ch;
                    if (!index.ContainerOf.ContainsKey(ch)) index.ContainerOf[ch] = list;
                }
            }
        }

        /// <summary>CLIENT (#9 hire gap): materialize soldiers the client has never seen. A HIRED recruit is a
        /// brand-new <c>GeoCharacter</c>, so its id resolves to nothing — the membership reconcile and the PS2
        /// live-state apply BOTH skip it ("not live on this client"). Its whole-<c>GeoCharacter</c> blob rides
        /// the SAME snapshot's state block, so decode it and add the live instance to the site the membership
        /// names — value-only <c>_tacUnits</c> add, the same non-native path <see cref="RosterReconcile"/> uses
        /// (the frozen mirror never runs the native <c>AddCharacter</c> sim cascade). The decoded instance keeps
        /// its serialized <c>_factionDef</c>, so <c>GeoCharacter.Faction</c> resolves against the live level.
        /// Returns the ids actually materialized so <see cref="PersonnelChannel"/> skips re-applying their state
        /// (the decoded instance IS that state). Best-effort: any per-soldier miss logs + leaves the id
        /// unresolved (the pre-fix skip) so the next flush re-arms.</summary>
        public static HashSet<long> MaterializeNewcomers(GeoRuntime rt, List<PersonnelSiteRoster> sites,
                                                         List<PersonnelSoldierState> states, CharacterIndex index)
        {
            var materialized = new HashSet<long>();
            if (sites == null || states == null || index == null) return materialized;
            var placements = PersonnelNewcomerPlan.ResolvePlacements(sites, index.ById.Keys);
            if (placements.Count == 0) return materialized;   // no membership-listed id is a newcomer
            foreach (var st in states)
            {
                if (st?.Blob == null || index.ById.ContainsKey(st.UnitId)) continue;   // already live
                if (!placements.TryGetValue(st.UnitId, out int siteId)) continue;      // membership never placed it
                try
                {
                    var soldier = PersonnelBlob.Read(st.Blob);
                    if (soldier == null)
                    { Debug.LogError("[Multiplayer] PersonnelReflection: newcomer GeoUnitId " + st.UnitId + " blob decode failed — not materialized"); continue; }
                    long decodedId = ReadUnitId(soldier);
                    if (decodedId != 0 && decodedId != st.UnitId)
                    { Debug.LogError("[Multiplayer] PersonnelReflection: newcomer blob id mismatch (" + decodedId + " != " + st.UnitId + ") — not materialized"); continue; }
                    var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                    var list = site != null ? GetTacUnits(site) : null;
                    if (list == null)
                    { Debug.Log("[Multiplayer] PersonnelReflection: newcomer GeoUnitId " + st.UnitId + " site " + siteId + " unresolved — deferred (re-arms next flush)"); continue; }
                    if (!list.Contains(soldier)) list.Add(soldier);    // value-only add; ApplySiteRoster then orders it
                    index.ById[st.UnitId] = soldier;
                    index.ContainerOf[soldier] = list;
                    materialized.Add(st.UnitId);
                    Debug.Log("[Multiplayer] PersonnelReflection: materialized hired GeoUnitId " + st.UnitId + " into site " + siteId);
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.MaterializeNewcomers(" + st.UnitId + ") failed: " + ex.Message); }
            }
            return materialized;
        }

        /// <summary>CLIENT (#9): reconcile one mirrored site roster onto the resolved site's <c>_tacUnits</c>
        /// (value-only). Unresolvable site / list / soldier ids log + skip (degrade-to-notify).</summary>
        public static void ApplySiteRoster(GeoRuntime rt, PersonnelSiteRoster rec, CharacterIndex index)
        {
            if (rec == null || index == null) return;
            try
            {
                var site = GeoSiteReflection.ResolveSiteById(rt, rec.SiteId);
                if (site == null)
                {
                    Debug.Log("[Multiplayer] PersonnelReflection: site " + rec.SiteId + " not resolved — roster record skipped");
                    return;
                }
                var list = GetTacUnits(site);
                if (list == null)
                {
                    Debug.Log("[Multiplayer] PersonnelReflection: site " + rec.SiteId + " has no _tacUnits — roster record skipped");
                    return;
                }
                ReconcileInto(list, rec.UnitIds, index, "site " + rec.SiteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.ApplySiteRoster failed: " + ex.Message); }
        }

        /// <summary>CLIENT (#6 crew tail): reconcile a mirrored crew id set onto the vehicle's
        /// <c>_tacUnits</c> (value-only). Builds its own index (separate channel apply).</summary>
        public static void ApplyVehicleCrew(GeoRuntime rt, object vehicle, long[] ids)
        {
            if (vehicle == null || ids == null) return;
            try
            {
                var list = GetTacUnits(vehicle);
                if (list == null)
                {
                    Debug.Log("[Multiplayer] PersonnelReflection: vehicle has no _tacUnits — crew record skipped");
                    return;
                }
                ReconcileInto(list, ids, BuildCharacterIndex(rt), "vehicle crew");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.ApplyVehicleCrew failed: " + ex.Message); }
        }

        private static void ReconcileInto(IList target, long[] ids, CharacterIndex index, string label)
        {
            var outcome = RosterReconcile.Apply(target, ids,
                id => index.ById.TryGetValue(id, out var ch) ? ch : null,
                ch => index.ContainerOf.TryGetValue(ch, out var l) ? l : null);
            // Cross-channel transfer gap: an instance this reconcile REMOVED now sits in no container
            // (a same-apply transfer went through remove-from-old instead and never lands here) — park
            // it so the #6/#9 apply that references its id ticks later can still resolve it. If it is
            // in fact still claimed somewhere (corrupt double-membership), the next index's container
            // scan supersedes the entry.
            foreach (var inst in outcome.RemovedInstances)
            {
                long id = ReadUnitId(inst);
                if (id == 0) continue;   // unresolvable id can never be referenced again — not poolable
                PersonnelOrphanPool.Park(id, inst);
                index.ContainerOf.Remove(inst);
                Debug.Log("[Multiplayer] PersonnelReflection: " + label + " — GeoUnitId " + id
                          + " removed with no live container, parked in orphan pool (awaiting adoption)");
            }
            // Adoption: every mirrored id that resolved is IN this container now — drop its pool entry
            // (a pooled instance resolves via the BuildCharacterIndex merge, so this is where an
            // orphaned soldier re-enters a live roster).
            if (PersonnelOrphanPool.Count > 0)
                foreach (var id in ids)
                    if (PersonnelOrphanPool.Evict(id) && index.ById.TryGetValue(id, out var adopted))
                    {
                        index.ContainerOf[adopted] = target;
                        Debug.Log("[Multiplayer] PersonnelReflection: " + label + " — GeoUnitId " + id
                                  + " adopted from orphan pool");
                    }
            if (outcome.Unresolved.Count > 0)
                Debug.Log("[Multiplayer] PersonnelReflection: " + label + " — " + outcome.Unresolved.Count
                          + " GeoUnitId(s) not live on this client, skipped [" + string.Join(",", outcome.Unresolved) + "]");
            if (outcome.Changed)
                Debug.Log("[Multiplayer] PersonnelReflection: " + label + " reconciled (+" + outcome.Added
                          + "/-" + outcome.Removed + (outcome.Reordered ? " reorder" : "") + ") count=" + target.Count);
        }

        private static long[] ReadIds(IList tacUnits)
        {
            var ids = new List<long>(tacUnits.Count);
            foreach (var ch in tacUnits)
            {
                long id = ReadUnitId(ch);
                if (id != 0) ids.Add(id);   // id 0 (None/unresolved) can never be mirrored — skip silently
            }
            return ids.ToArray();
        }

        // ─── PS2 live-state apply (whole-GeoCharacter value copy onto the EXISTING instance) ───

        // (Type.FullName + "." + fieldName) → FieldInfo, misses cached too (GetTacUnits pattern).
        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();
        private static MethodInfo _setItemsMethod;            // GeoCharacter.SetItems(armour, equipment, inventory, freeReload)
        private static MethodInfo _aggregateBodyPartsMethod;  // GeoCharacter.AggregateBodyPartHealth() (private)

        private static FieldInfo CachedField(Type t, string name)
        {
            string key = t.FullName + "." + name;
            if (!_fieldCache.TryGetValue(key, out var f))
            {
                f = AccessTools.Field(t, name);
                _fieldCache[key] = f;   // cache the miss too
            }
            return f;
        }

        // ─── PS2 faction-SP tail: the shared GeoPhoenixFaction.Skillpoints pool (a raw public int field) ───

        /// <summary>HOST: read the faction's shared skill-point pool (<c>GeoPhoenixFaction.Skillpoints</c>),
        /// or null when the faction isn't live (mid-load) or the field can't be resolved.</summary>
        public static int? ReadFactionSkillpoints(GeoRuntime rt)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                var f = CachedField(fac.GetType(), "Skillpoints");
                return f != null ? (int?)(int)f.GetValue(fac) : null;
            }
            catch { return null; }
        }

        /// <summary>CLIENT: value-only mirror of the shared faction SP pool onto the live
        /// <c>GeoPhoenixFaction</c> — a plain field write (the pool has no native mutator), so it can never
        /// re-enter a sim path. No-op when the faction isn't live / the field is unresolved.</summary>
        public static void ApplyFactionSkillpoints(GeoRuntime rt, int value)
        {
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return;
                CachedField(fac.GetType(), "Skillpoints")?.SetValue(fac, value);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.ApplyFactionSkillpoints failed: " + ex.Message); }
        }

        /// <summary>Reference/value copy of one serialized field decoded → existing. False = field missing
        /// (caller decides whether that is fatal; most are load-bearing).</summary>
        private static bool CopyField(Type t, string name, object decoded, object existing)
        {
            var f = CachedField(t, name);
            if (f == null) return false;
            f.SetValue(existing, f.GetValue(decoded));
            return true;
        }

        /// <summary>Clear + refill the existing list field from the decoded one (keeps the existing list
        /// INSTANCE — safer for any captured reference than a wholesale swap). Returns false only when the
        /// member itself does not resolve (a shape/rename break the caller can diag); a null/non-IList value
        /// on either side is a no-op that still reports the member as present (true).</summary>
        private static bool CopyListContents(Type t, string name, object decoded, object existing)
        {
            var f = CachedField(t, name);
            if (f == null) return false;
            if (!(f.GetValue(existing) is IList target) || !(f.GetValue(decoded) is IList source)) return true;
            target.Clear();
            foreach (var o in source) target.Add(o);
            return true;
        }

        /// <summary>Value-copy one member, NEVER-SILENT: names the member if it fails to resolve so a
        /// game-update rename breaks loudly in the log (the reflection-contract guard on the frozen mirror).</summary>
        private static void CopyMember(Type t, string name, object decoded, object existing)
        {
            if (!CopyField(t, name, decoded, existing))
                Debug.LogError("[Multiplayer] PersonnelReflection: " + t.Name + "." + name + " not found — not mirrored");
        }

        private static void CopyListMember(Type t, string name, object decoded, object existing)
        {
            if (!CopyListContents(t, name, decoded, existing))
                Debug.LogError("[Multiplayer] PersonnelReflection: " + t.Name + "." + name + " not found — not mirrored");
        }

        /// <summary>CLIENT (#9 PS2): mirror the decoded soldier's CharacterProgression into the EXISTING live
        /// progression instance by VALUE — never the old whole-object by-ref swap. TFTV repaints the open
        /// progression panel per frame recomputing base+bonus off this object graph (Stats.cs:496-628); a
        /// mid-frame top-level swap gave torn base-vs-effective reads that never settled ('1+3' split, flicker,
        /// wrong per-point cost labels — RCA 2026-07-10). Keeping the instance also preserves its live
        /// StatModifiedCallback / OnAbilityAdded / OnNewSpecializationAdded wiring (no orphan, no rewire).
        /// Copies every MUTABLE [SerializeMember] of CharacterProgression (decompile CharacterProgression.cs):
        /// _baseStats / _abilities / _abilityTracks lists, SkillPoints, _secondarySpecializationDef, and the
        /// readonly-but-mutable LevelProgression inner state. MainSpecDef + BaseStatSheet are readonly Def
        /// identity set once at construction (a soldier's class/stat-sheet never changes) → intentionally not
        /// copied. Fallback: a soldier with NO live progression (new-recruit path) adopts the decoded instance
        /// whole (the only remaining swap, logged).</summary>
        private static void MirrorProgression(Type geoT, object decoded, object existing)
        {
            var pf = CachedField(geoT, "_progression");
            if (pf == null)
            {
                Debug.LogError("[Multiplayer] PersonnelReflection: GeoCharacter._progression not found — progression not mirrored");
                return;
            }
            object deProg = pf.GetValue(decoded);
            object exProg = pf.GetValue(existing);
            if (deProg == null)
            {
                Debug.LogError("[Multiplayer] PersonnelReflection: decoded _progression is null — progression left unchanged (no blind null overwrite)");
                return;
            }
            if (exProg == null)
            {
                pf.SetValue(existing, deProg);   // last-resort swap: new recruit with no live progression to copy into
                Debug.Log("[Multiplayer] PersonnelReflection: soldier had no live progression — adopted decoded instance (new-recruit fallback)");
                return;
            }
            var pt = exProg.GetType();   // CharacterProgression
            CopyListMember(pt, "_baseStats", deProg, exProg);
            CopyListMember(pt, "_abilities", deProg, exProg);
            CopyListMember(pt, "_abilityTracks", deProg, exProg);
            CopyMember(pt, "SkillPoints", deProg, exProg);
            CopyMember(pt, "_secondarySpecializationDef", deProg, exProg);
            MirrorLevelProgression(pt, deProg, exProg);
        }

        /// <summary>CAVEAT (decompile LevelProgression.cs): CharacterProgression.LevelProgression is a readonly
        /// ref holding MUTABLE Level/Experience — value-copy its inner [SerializeMember] state (Experience, Def,
        /// HasNewLevel) into the EXISTING instance so level/XP keep mirroring while its LevelUpCallback wiring
        /// (set in CharacterProgression.Init) survives. Level is derived (Def.GetLevel(Experience)) — no field.
        /// Adopt whole only if the existing side has none.</summary>
        private static void MirrorLevelProgression(Type progT, object deProg, object exProg)
        {
            var lf = CachedField(progT, "LevelProgression");
            if (lf == null)
            {
                Debug.LogError("[Multiplayer] PersonnelReflection: CharacterProgression.LevelProgression not found — level/XP not mirrored");
                return;
            }
            object deLvl = lf.GetValue(deProg);
            object exLvl = lf.GetValue(exProg);
            if (deLvl == null) return;
            if (exLvl == null) { lf.SetValue(exProg, deLvl); return; }   // adopt inner (no live instance to copy into)
            var lt = exLvl.GetType();   // LevelProgression
            CopyMember(lt, "Experience", deLvl, exLvl);
            CopyMember(lt, "Def", deLvl, exLvl);
            CopyMember(lt, "HasNewLevel", deLvl, exLvl);
        }

        /// <summary>Invoke <c>CopyFrom(sameStatType, bool triggerStatChangeEvent:false)</c> on a StatusStat
        /// (copies Min/Max/Value + modifications exactly, silently — StatusStat.cs:141-152). Overload picked
        /// by assignability: StatusStat also carries a BaseStat-typed override that self-dispatches, so
        /// either match is correct.</summary>
        private static void CopyStatFrom(object existingStat, object decodedStat)
        {
            if (existingStat == null || decodedStat == null) return;
            foreach (var m in existingStat.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "CopyFrom") continue;
                var pars = m.GetParameters();
                if (pars.Length != 2 || pars[1].ParameterType != typeof(bool)) continue;
                if (!pars[0].ParameterType.IsInstanceOfType(decodedStat)) continue;
                m.Invoke(existingStat, new object[] { decodedStat, false });
                return;
            }
        }

        /// <summary>Stamina + hunger: CopyFrom when both sides carry fatigue (keeps the existing instance's
        /// owner + event wiring); reference-adopt when the existing side has none. Decoded null → leave.</summary>
        private static void ApplyFatigue(Type t, object existing, object decoded)
        {
            var ff = CachedField(t, "_fatigue");
            if (ff == null) return;
            object deF = ff.GetValue(decoded);
            if (deF == null) return;
            object exF = ff.GetValue(existing);
            if (exF == null) { ff.SetValue(existing, deF); return; }   // adopt (inert mirror — no rewire)
            var fs = CachedField(deF.GetType(), "_stamina");
            CopyStatFrom(fs?.GetValue(exF), fs?.GetValue(deF));
            var fh = CachedField(deF.GetType(), "_hunger");
            if (fh != null) fh.SetValue(exF, fh.GetValue(deF));
        }

        /// <summary>
        /// CLIENT (#9 PS2): copy the decoded transient soldier's WHOLE live state onto the existing
        /// instance, value-only. This is the spec's R1 FALLBACK path — decompile-verified
        /// (GeoCharacter.cs:1006-1062), the native <c>RecreateFromAnotherCharacter</c> is a static FACTORY
        /// returning a NEW instance (identity churn on the mirror) and drops state on the floor
        /// (OtherStats/AdditionalDeploymentTags self-AddRange bug, corruption + hunger never copied), so
        /// it is NOT usable as an in-place apply. Here instead:
        ///   1. serialized value/reference fields mirror directly (identity, corruption, bonus stats,
        ///      progression graph, loadout mirrors, OtherStats, deployment tags, fatigue);
        ///   2. ONE native per-character recompute rides <c>SetItems(armour, equipment, inventory,
        ///      freeReload:false)</c> (GeoCharacter.cs:831-880 — clears+refills the three GeoItem lists,
        ///      re-derives item abilities, CarryWeight + UpdateStats(recalculateBodparts:true) off the NEW
        ///      progression/corruption/bonus; decompile-verified per-character only, no faction/level/sim
        ///      cascade). freeReload FALSE: the blob carries the host's exact ammo/charges — a free reload
        ///      would falsify them (deliberate deviation from the spec's freeReload:true sketch);
        ///   3. bodypart-HP snapshot + native <c>AggregateBodyPartHealth</c> (the ApllyTacticalResult order);
        ///   4. exact host HP LAST via StatusStat.CopyFrom (UpdateStats ratio-preserves into the new Max
        ///      first; CopyFrom then lands Min/Max/Value exactly, silent).
        /// Progression is mirrored by VALUE into the existing live CharacterProgression instance (see
        /// <see cref="MirrorProgression"/>) — NOT the old whole-object by-ref swap, which gave TFTV's
        /// per-frame panel repaint torn base-vs-effective reads (RCA 2026-07-10). Any failure logs +
        /// returns false; the soldier keeps its previous state.
        /// </summary>
        public static bool ApplySoldierState(object existing, object decoded)
        {
            if (existing == null || decoded == null) return false;
            try
            {
                var t = existing.GetType();
                CopyField(t, "_identity", decoded, existing);
                CopyField(t, "_corruptionValue", decoded, existing);
                CopyField(t, "_bonusCharacterStats", decoded, existing);
                MirrorProgression(t, decoded, existing);
                CopyField(t, "_armourLoadoutItems", decoded, existing);
                CopyField(t, "_equipmentLoadoutItems", decoded, existing);
                CopyField(t, "_inventoryLoadoutItems", decoded, existing);
                CopyListContents(t, "OtherStats", decoded, existing);
                CopyListContents(t, "AdditionalDeploymentTags", decoded, existing);
                ApplyFatigue(t, existing, decoded);

                if (_setItemsMethod == null) _setItemsMethod = AccessTools.Method(t, "SetItems");
                if (_setItemsMethod != null)
                {
                    _setItemsMethod.Invoke(existing, new object[]
                    {
                        CachedField(t, "_armourItems")?.GetValue(decoded),
                        CachedField(t, "_equipmentItems")?.GetValue(decoded),
                        CachedField(t, "_inventoryItems")?.GetValue(decoded),
                        false
                    });
                }
                else Debug.LogError("[Multiplayer] PersonnelReflection: GeoCharacter.SetItems not found — items/stats not mirrored");

                CopyField(t, "_bodypartHealth", decoded, existing);
                if (_aggregateBodyPartsMethod == null) _aggregateBodyPartsMethod = AccessTools.Method(t, "AggregateBodyPartHealth");
                _aggregateBodyPartsMethod?.Invoke(existing, null);

                var hf = CachedField(t, "_health");
                CopyStatFrom(hf?.GetValue(existing), hf?.GetValue(decoded));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] PersonnelReflection.ApplySoldierState failed (soldier keeps previous state): " + ex.Message);
                return false;
            }
        }
    }
}
