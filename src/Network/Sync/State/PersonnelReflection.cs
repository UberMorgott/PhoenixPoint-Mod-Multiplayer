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
                IndexContainers(_vehiclesProp?.GetValue(fac, null) as IEnumerable, index);
                IndexContainers(_sitesProp?.GetValue(fac, null) as IEnumerable, index);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelReflection.BuildCharacterIndex failed: " + ex.Message); }
            return index;
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

        /// <summary>Reference/value copy of one serialized field decoded → existing. False = field missing
        /// (caller decides whether that is fatal; most are load-bearing).</summary>
        private static bool CopyField(Type t, string name, object decoded, object existing)
        {
            var f = CachedField(t, name);
            if (f == null) return false;
            f.SetValue(existing, f.GetValue(decoded));
            return true;
        }

        /// <summary>Clear + refill the existing PUBLIC list field from the decoded one (keeps the existing
        /// list INSTANCE — safer for any captured reference than a wholesale swap).</summary>
        private static void CopyListContents(Type t, string name, object decoded, object existing)
        {
            var f = CachedField(t, name);
            if (!(f?.GetValue(existing) is IList target) || !(f.GetValue(decoded) is IList source)) return;
            target.Clear();
            foreach (var o in source) target.Add(o);
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
        /// The decoded progression is adopted WITHOUT rewiring its events onto the existing character:
        /// the frozen client never drives progression natively (every change arrives as the next blob),
        /// and the wired handlers reach global AchievmentTracker/StatisticsManager sim — inert by
        /// construction. Any failure logs + returns false; the soldier keeps its previous state.
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
                if (!CopyField(t, "_progression", decoded, existing))
                    Debug.LogError("[Multiplayer] PersonnelReflection: GeoCharacter._progression not found — progression not mirrored");
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
