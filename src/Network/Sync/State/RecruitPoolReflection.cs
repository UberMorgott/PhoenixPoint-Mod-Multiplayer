using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the recruit-pool channel (#10, PS3). No compile-time game refs — every
    /// member resolved by name and cached (mirrors <see cref="PersonnelReflection"/>). Decompile anchors
    /// (taxonomy 2026-07-05 §6, re-verified 2026-07-06):
    /// • <c>GeoHaven</c> is a COMPONENT on the site actor (GeoSite.cs:353 <c>GetComponent&lt;GeoHaven&gt;()</c>)
    ///   with a <c>Site</c> back-reference (:166); the live slot is the auto-property
    ///   <c>AvailableRecruit { get; private set; }</c> (:229) — <c>InstanceData.NewRecruit</c> is only the
    ///   save-record, so the client stamps the PROPERTY (what the vanilla screens + TFTV overlay read).
    /// • <c>GeoPhoenixFaction._nakedRecruits</c> — <c>Dictionary&lt;GeoUnitDescriptor, ResourcePack&gt;</c> (:112);
    ///   <c>_capturedUnits</c> — <c>List&lt;GeoUnitDescriptor&gt;</c> (:114).
    /// • Price lines: <c>ResourcePack.Values</c> = <c>List&lt;ResourceUnit&gt;</c>, ResourceUnit = struct
    ///   { ResourceType Type; float Value } (ResourcePack.cs:18 / ResourceUnit.cs).
    ///
    /// HOST reads pools → descriptor blobs (<see cref="PersonnelBlob"/> — same game-Serializer seam, the
    /// descriptor is [SerializeType] and save-proven inside ExtendedInstanceData). CLIENT applies
    /// VALUE-ONLY: stamp the haven slot / clear+refill the live faction collections via
    /// <see cref="RecruitPoolReconcile"/> — never native SpawnNewRecruit/CaptureUnit (frozen mirror).
    /// All reflection is null-safe best-effort: a miss logs and degrades (skips that record), never throws
    /// into the channel loop.
    /// </summary>
    public static class RecruitPoolReflection
    {
        private static bool _ensured;
        private static Type _geoHavenType;                 // PhoenixPoint.Geoscape.Entities.GeoHaven
        private static PropertyInfo _havenSiteProp;        // GeoHaven.Site
        private static PropertyInfo _availableRecruitProp; // GeoHaven.AvailableRecruit
        private static MethodInfo _availableRecruitSet;    // its private set accessor (auto-prop, no logic)
        private static FieldInfo _availableRecruitBack;    // <AvailableRecruit>k__BackingField fallback
        private static MethodInfo _refreshVisualsMethod;   // GeoSite.RefreshVisuals() (map marker repaint)
        private static FieldInfo _nakedRecruitsField;      // GeoPhoenixFaction._nakedRecruits
        private static FieldInfo _capturedUnitsField;      // GeoPhoenixFaction._capturedUnits
        private static Type _resourcePackType;             // PhoenixPoint.Common.Core.ResourcePack
        private static Type _resourceUnitType;             // PhoenixPoint.Common.Core.ResourceUnit
        private static FieldInfo _rpValuesField;           // ResourcePack.Values
        private static FieldInfo _ruTypeField;             // ResourceUnit.Type
        private static FieldInfo _ruValueField;            // ResourceUnit.Value
        private static MethodInfo _getIdentityMethod;      // GeoUnitDescriptor.GetIdentity() (lazy appearance gen)

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;   // one attempt; every user null-guards
            try
            {
                _geoHavenType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoHaven");
                if (_geoHavenType != null)
                {
                    _havenSiteProp = AccessTools.Property(_geoHavenType, "Site");
                    _availableRecruitProp = AccessTools.Property(_geoHavenType, "AvailableRecruit");
                    _availableRecruitSet = _availableRecruitProp?.GetSetMethod(true);
                    _availableRecruitBack = AccessTools.Field(_geoHavenType, "<AvailableRecruit>k__BackingField");
                }
                var facType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
                if (facType != null)
                {
                    _nakedRecruitsField = AccessTools.Field(facType, "_nakedRecruits");
                    _capturedUnitsField = AccessTools.Field(facType, "_capturedUnits");
                }
                _resourcePackType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
                _resourceUnitType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit");
                if (_resourcePackType != null) _rpValuesField = AccessTools.Field(_resourcePackType, "Values");
                if (_resourceUnitType != null)
                {
                    _ruTypeField = AccessTools.Field(_resourceUnitType, "Type");
                    _ruValueField = AccessTools.Field(_resourceUnitType, "Value");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolReflection.Ensure failed (pool sync degraded): " + ex.Message); }
        }

        /// <summary>HOST: force the recruit descriptor's appearance to MATERIALIZE before it is serialized for
        /// the #10 blob. A recruit's visual identity (<c>GeoUnitDescriptor._identity</c> — face/hair/body/colours,
        /// [SerializeMember], GeoUnitDescriptor.cs:200) is LAZILY generated on the first <c>GetIdentity()</c> call
        /// (:383-407 → <c>GenerateIdentity()</c> :514, a RANDOM roll) and is normally NULL when the pool is
        /// snapshotted (the host hasn't rendered the recruit yet). A null <c>_identity</c> serializes as null, so
        /// the CLIENT re-rolls its OWN random appearance on render → host and client show DIFFERENT faces. Calling
        /// <c>GetIdentity()</c> here (the exact call the host's recruit screen makes) stamps the concrete identity
        /// into the descriptor so the blob carries it; the client's <c>GetIdentity()</c> then returns the mirrored
        /// value (:385-387) instead of rolling. Host-side, idempotent, best-effort.</summary>
        private static void MaterializeIdentity(object descriptor)
        {
            if (descriptor == null) return;
            try
            {
                if (_getIdentityMethod == null || _getIdentityMethod.DeclaringType == null
                    || !_getIdentityMethod.DeclaringType.IsInstanceOfType(descriptor))
                    _getIdentityMethod = AccessTools.Method(descriptor.GetType(), "GetIdentity", Type.EmptyTypes);
                _getIdentityMethod?.Invoke(descriptor, null);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolReflection.MaterializeIdentity failed: " + ex.Message); }
        }

        /// <summary>Dirty-seam helper: a haven instance → its owning SiteId (the #10 key), or -1.</summary>
        public static int GetHavenSiteId(object haven)
        {
            if (haven == null) return -1;
            try
            {
                Ensure();
                var site = _havenSiteProp?.GetValue(haven, null);
                return site != null ? GeoSiteReflection.GetSiteId(site) : -1;
            }
            catch { return -1; }
        }

        /// <summary>The site's live <c>GeoHaven</c> component, or null (not a haven / unbound).</summary>
        private static object GetHavenComponent(object site)
        {
            if (_geoHavenType == null || !(site is Component c)) return null;
            try { return c.GetComponent(_geoHavenType); }
            catch { return null; }
        }

        /// <summary>HOST (#10 flush): read one haven's recruit slot. Resolved=false → the flush core counts
        /// it Failed (site/haven/serializer miss — dropped, not deferred); Blob=null = slot cleared.</summary>
        public static RecruitPoolFlush.HavenSource ReadHavenRecruit(GeoRuntime rt, int siteId)
        {
            var res = new RecruitPoolFlush.HavenSource();
            try
            {
                Ensure();
                var haven = GetHavenComponent(GeoSiteReflection.ResolveSiteById(rt, siteId));
                if (haven == null || _availableRecruitProp == null) return res;
                var recruit = _availableRecruitProp.GetValue(haven, null);
                if (recruit == null) { res.Resolved = true; return res; }   // honest cleared slot
                MaterializeIdentity(recruit);   // stamp appearance so the blob carries it (BUG-B: else client re-rolls)
                var blob = PersonnelBlob.Write(recruit);
                if (blob == null) return res;                                // serializer miss → Failed
                res.Resolved = true;
                res.Blob = blob;
                return res;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] RecruitPoolReflection.ReadHavenRecruit(" + siteId + ") failed: " + ex.Message);
                return res;
            }
        }

        /// <summary>HOST (#10 flush): the FULL naked-recruit pool as blob+cost records, or null when any
        /// entry fails to serialize (all-or-nothing — a partial full-set would DELETE the missing entries
        /// client-side; the caller logs and drops this flush, the next seam re-arms).</summary>
        public static List<RecruitNakedRecord> SnapshotNakedRecruits(GeoRuntime rt)
        {
            try
            {
                Ensure();
                var fac = rt?.PhoenixFaction();
                if (fac == null || _nakedRecruitsField == null) return null;
                if (!(_nakedRecruitsField.GetValue(fac) is IDictionary dict)) return null;
                var res = new List<RecruitNakedRecord>(dict.Count);
                foreach (DictionaryEntry entry in dict)
                {
                    MaterializeIdentity(entry.Key);   // stamp appearance so the blob carries it (BUG-B: else client re-rolls)
                    var blob = PersonnelBlob.Write(entry.Key);
                    if (blob == null) return null;
                    res.Add(new RecruitNakedRecord(blob, ReadCostEntries(entry.Value)));
                }
                return res;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] RecruitPoolReflection.SnapshotNakedRecruits failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>HOST (#10 flush): the FULL containment pool as descriptor blobs, or null on any
        /// serialize failure (same all-or-nothing contract as the naked snapshot).</summary>
        public static List<byte[]> SnapshotCapturedUnits(GeoRuntime rt)
        {
            try
            {
                Ensure();
                var fac = rt?.PhoenixFaction();
                if (fac == null || _capturedUnitsField == null) return null;
                if (!(_capturedUnitsField.GetValue(fac) is IList list)) return null;
                var res = new List<byte[]>(list.Count);
                foreach (var unit in list)
                {
                    var blob = PersonnelBlob.Write(unit);
                    if (blob == null) return null;
                    res.Add(blob);
                }
                return res;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] RecruitPoolReflection.SnapshotCapturedUnits failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>ResourcePack → wire cost lines (empty on any miss — price is display-only).</summary>
        private static RecruitCostEntry[] ReadCostEntries(object resourcePack)
        {
            try
            {
                if (resourcePack == null || _rpValuesField == null || _ruTypeField == null || _ruValueField == null)
                    return new RecruitCostEntry[0];
                if (!(_rpValuesField.GetValue(resourcePack) is IList values)) return new RecruitCostEntry[0];
                var res = new List<RecruitCostEntry>(values.Count);
                foreach (var unit in values)
                {
                    if (unit == null) continue;
                    res.Add(new RecruitCostEntry(Convert.ToInt32(_ruTypeField.GetValue(unit)),
                                                 Convert.ToSingle(_ruValueField.GetValue(unit))));
                }
                return res.ToArray();
            }
            catch { return new RecruitCostEntry[0]; }
        }

        // ─── CLIENT apply (value-only stamps on the frozen mirror) ─────────────────────────────────────

        /// <summary>CLIENT: stamp one haven's <c>AvailableRecruit</c> (blob null = clear). Value-only —
        /// the auto-prop setter has zero logic, no <c>CheckShouldSpawnRecruit</c>/sim cascade; ends with
        /// the native <c>Site.RefreshVisuals()</c> map-marker kick (the same call SpawnNewRecruit/
        /// RemoveRecruit make — display-only). Unresolvable site/haven logs + skips; a blob that fails to
        /// decode keeps the PREVIOUS slot (degrade-to-notify, never a false clear).</summary>
        public static void ApplyHavenRecruit(GeoRuntime rt, int siteId, byte[] blobOrNull)
        {
            try
            {
                Ensure();
                var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                var haven = GetHavenComponent(site);
                if (haven == null)
                {
                    Debug.Log("[Multiplayer] RecruitPoolReflection: haven for site " + siteId + " not resolved — recruit record skipped");
                    return;
                }
                object recruit = null;
                if (blobOrNull != null)
                {
                    recruit = PersonnelBlob.ReadDescriptor(blobOrNull);
                    if (recruit == null)
                    {
                        Debug.LogError("[Multiplayer] RecruitPoolReflection: recruit blob decode failed for site " + siteId
                                       + " — haven keeps previous slot");
                        return;
                    }
                }
                if (_availableRecruitSet != null) _availableRecruitSet.Invoke(haven, new[] { recruit });
                else if (_availableRecruitBack != null) _availableRecruitBack.SetValue(haven, recruit);
                else
                {
                    Debug.LogError("[Multiplayer] RecruitPoolReflection: AvailableRecruit setter unresolved — haven recruit not mirrored");
                    return;
                }
                Debug.Log("[Multiplayer] RecruitPoolReflection: haven " + siteId + " recruit "
                          + (recruit == null ? "cleared" : "stamped"));
                try
                {
                    if (_refreshVisualsMethod == null && site != null)
                        _refreshVisualsMethod = AccessTools.Method(site.GetType(), "RefreshVisuals", Type.EmptyTypes);
                    _refreshVisualsMethod?.Invoke(site, null);
                }
                catch { /* marker repaint is best-effort */ }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolReflection.ApplyHavenRecruit failed: " + ex.Message); }
        }

        /// <summary>CLIENT: clear+refill the live <c>_nakedRecruits</c> dict from the mirrored FULL set.
        /// An entry whose blob fails to decode is skipped with a log (the rest applies — the next full-set
        /// emit heals); costs rebuild as real ResourcePacks so the native recruitment screen prices render.</summary>
        public static void ApplyNakedRecruits(GeoRuntime rt, List<RecruitNakedRecord> records)
        {
            try
            {
                Ensure();
                var fac = rt?.PhoenixFaction();
                if (fac == null || _nakedRecruitsField == null) return;
                if (!(_nakedRecruitsField.GetValue(fac) is IDictionary dict))
                {
                    Debug.Log("[Multiplayer] RecruitPoolReflection: _nakedRecruits not reachable — naked pool skipped");
                    return;
                }
                var entries = new List<KeyValuePair<object, object>>(records.Count);
                foreach (var rec in records)
                {
                    var desc = PersonnelBlob.ReadDescriptor(rec.Blob);
                    if (desc == null)
                    {
                        Debug.LogError("[Multiplayer] RecruitPoolReflection: naked recruit blob decode failed — entry skipped");
                        continue;
                    }
                    entries.Add(new KeyValuePair<object, object>(desc, BuildResourcePack(rec.Cost)));
                }
                int applied = RecruitPoolReconcile.ApplyNaked(dict, entries);
                Debug.Log("[Multiplayer] RecruitPoolReflection: naked pool mirrored (" + applied + "/" + records.Count + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolReflection.ApplyNakedRecruits failed: " + ex.Message); }
        }

        /// <summary>CLIENT: clear+refill the live <c>_capturedUnits</c> list from the mirrored FULL set.
        /// Containment CAP is derived from bases (recomputed natively) — only membership mirrors here.</summary>
        public static void ApplyCapturedUnits(GeoRuntime rt, List<byte[]> blobs)
        {
            try
            {
                Ensure();
                var fac = rt?.PhoenixFaction();
                if (fac == null || _capturedUnitsField == null) return;
                if (!(_capturedUnitsField.GetValue(fac) is IList list))
                {
                    Debug.Log("[Multiplayer] RecruitPoolReflection: _capturedUnits not reachable — containment skipped");
                    return;
                }
                var descs = new List<object>(blobs.Count);
                foreach (var blob in blobs)
                {
                    var desc = PersonnelBlob.ReadDescriptor(blob);
                    if (desc == null)
                    {
                        Debug.LogError("[Multiplayer] RecruitPoolReflection: captured blob decode failed — entry skipped");
                        continue;
                    }
                    descs.Add(desc);
                }
                int applied = RecruitPoolReconcile.ApplyCaptured(list, descs);
                Debug.Log("[Multiplayer] RecruitPoolReflection: containment mirrored (" + applied + "/" + blobs.Count + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolReflection.ApplyCapturedUnits failed: " + ex.Message); }
        }

        /// <summary>Wire cost lines → a real ResourcePack (via the params <c>ResourcePack(ResourceUnit[])</c>
        /// ctor — the only 1-arg ctor, so Activator binds unambiguously). Null on any miss: the native
        /// price readers (<c>GetNakedRecruitCost</c>) treat a missing pack as free — display-only risk.</summary>
        private static object BuildResourcePack(RecruitCostEntry[] cost)
        {
            try
            {
                if (_resourcePackType == null || _resourceUnitType == null || _ruTypeField == null || _ruValueField == null)
                    return null;
                var arr = Array.CreateInstance(_resourceUnitType, cost?.Length ?? 0);
                for (int i = 0; i < (cost?.Length ?? 0); i++)
                {
                    object box = Activator.CreateInstance(_resourceUnitType);
                    _ruTypeField.SetValue(box, Enum.ToObject(_ruTypeField.FieldType, cost[i].ResourceType));
                    _ruValueField.SetValue(box, cost[i].Value);
                    arr.SetValue(box, i);
                }
                return Activator.CreateInstance(_resourcePackType, new object[] { arr });
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] RecruitPoolReflection.BuildResourcePack failed: " + ex.Message);
                return null;
            }
        }
    }
}
