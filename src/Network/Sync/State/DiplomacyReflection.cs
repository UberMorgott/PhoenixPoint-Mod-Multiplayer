using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the host-authoritative faction-DIPLOMACY state channel (#4). The mod has NO
    /// compile-time game references, so every member is resolved by name and cached. This is a VALUE-ONLY
    /// mirror, like the wallet echo: the client overwrites each relation's stored reputation int to the
    /// host value WITHOUT firing the change cascade (pure mirror).
    ///
    /// Verified against the decompile (2026-06-17):
    ///   • factions: <c>GeoLevelController.Factions : IList&lt;GeoFaction&gt;</c> (public readonly field,
    ///     GeoLevelController.cs:85).
    ///   • faction key/def: <c>GeoFaction.Def : GeoFactionDef</c> (BaseDef, has stable Guid). A relation's
    ///     <c>WithParty</c> key is the faction's <c>Def.PPFactionDef</c> for faction-vs-faction relations
    ///     (GeoFaction.cs:217 <c>IDiplomaticPartyKey IDiplomaticParty.Key => Def.PPFactionDef</c>);
    ///     <c>PPFactionDef : BaseDef, IDiplomaticPartyKey</c> (PPFactionDef.cs:12), so it has a Guid too.
    ///   • diplomacy: <c>GeoFaction.Diplomacy : FactionDiplomacy</c> (a <c>PartyDiplomacy</c>, GeoFaction.cs:201)
    ///     → <c>PartyDiplomacy.Relations : IEnumerable&lt;Relation&gt;</c> (PartyDiplomacy.cs:91); each
    ///     <c>Relation.WithParty : IDiplomaticPartyKey</c> (:36) and <c>Relation.Diplomacy : int</c> (:38,
    ///     internal setter fires OnDiplomacyChanged). For the client overwrite we write the private
    ///     <c>_diplomacy</c> backing field (PartyDiplomacy.Relation._diplomacy, :26) DIRECTLY — bypassing
    ///     the setter → no <c>OnDiplomacyChanged</c> cascade (pure value mirror), exactly the
    ///     reward-free-echo discipline used elsewhere.
    /// </summary>
    public static class DiplomacyReflection
    {
        private static bool _ready;
        private static FieldInfo _factionsField;     // GeoLevelController.Factions (IList<GeoFaction>)
        private static PropertyInfo _factionDefProp; // GeoFaction.Def (GeoFactionDef)
        private static PropertyInfo _diplomacyProp;   // GeoFaction.Diplomacy (PartyDiplomacy)
        private static PropertyInfo _relationsProp;   // PartyDiplomacy.Relations (IEnumerable<Relation>)
        private static PropertyInfo _withPartyProp;   // Relation.WithParty (IDiplomaticPartyKey)
        private static PropertyInfo _relationDiploProp; // Relation.Diplomacy (int getter)
        private static FieldInfo _relationDiploField;   // Relation._diplomacy (int backing field, for overwrite)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            _factionsField = AccessTools.Field(geo.GetType(), "Factions");
            if (_factionsField == null) return;

            // Resolve the GeoFaction / PartyDiplomacy / Relation members lazily off the first live faction.
            if (!(_factionsField.GetValue(geo) is IEnumerable factions)) return;
            object firstFac = null;
            foreach (var f in factions) { firstFac = f; break; }
            if (firstFac == null) return;

            _factionDefProp = AccessTools.Property(firstFac.GetType(), "Def");
            _diplomacyProp = AccessTools.Property(firstFac.GetType(), "Diplomacy");
            if (_diplomacyProp == null) return;

            var diplomacy = _diplomacyProp.GetValue(firstFac, null);
            if (diplomacy == null) return; // factions exist but diplomacy not built yet → retry next frame
            _relationsProp = AccessTools.Property(diplomacy.GetType(), "Relations");
            if (_relationsProp == null) return;

            // Resolve Relation members off a live relation if present; else off the generic Relation type.
            var relType = AccessTools.TypeByName("PhoenixPoint.Common.Core.PartyDiplomacy+Relation");
            object firstRel = null;
            if (_relationsProp.GetValue(diplomacy, null) is IEnumerable rels)
                foreach (var rel in rels) { firstRel = rel; if (relType == null) relType = rel.GetType(); break; }
            if (relType == null && firstRel != null) relType = firstRel.GetType();
            if (relType != null)
            {
                _withPartyProp = AccessTools.Property(relType, "WithParty");
                _relationDiploProp = AccessTools.Property(relType, "Diplomacy");
                _relationDiploField = AccessTools.Field(relType, "_diplomacy");
            }

            _ready = _factionDefProp != null && _withPartyProp != null
                     && _relationDiploProp != null && _relationDiploField != null;
        }

        /// <summary>
        /// Host: snapshot every faction-to-faction relation as (ownerGuid, withGuid, value). Relations
        /// whose WithParty key is not a Def (no Guid) are skipped. Null if unavailable.
        /// </summary>
        public static DiplomacySnapshot Snapshot(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return null;
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                if (!(_factionsField.GetValue(geo) is IEnumerable factions)) return null;

                var snap = new DiplomacySnapshot();
                foreach (var fac in factions)
                {
                    if (fac == null) continue;
                    string ownerGuid = DefReflection.GetGuid(_factionDefProp.GetValue(fac, null));
                    if (string.IsNullOrEmpty(ownerGuid)) continue;
                    var diplomacy = _diplomacyProp.GetValue(fac, null);
                    if (diplomacy == null) continue;
                    if (!(_relationsProp.GetValue(diplomacy, null) is IEnumerable rels)) continue;
                    foreach (var rel in rels)
                    {
                        if (rel == null) continue;
                        var withKey = _withPartyProp.GetValue(rel, null);
                        string withGuid = DefReflection.GetGuid(withKey); // null if the key isn't a BaseDef
                        if (string.IsNullOrEmpty(withGuid)) continue;
                        int value;
                        try { value = (int)_relationDiploProp.GetValue(rel, null); } catch { continue; }
                        snap.Relations.Add((ownerGuid, withGuid, value));
                    }
                }
                return snap;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] DiplomacyReflection.Snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: overwrite each relation's reputation int to the host value by writing the private
        /// <c>_diplomacy</c> backing field directly (bypassing the setter → no OnDiplomacyChanged cascade:
        /// pure value mirror). Relations not present on the client are skipped (structurally identical
        /// campaign → they exist; a missing one is simply ignored, never created).
        /// </summary>
        public static void Apply(GeoRuntime rt, DiplomacySnapshot target)
        {
            if (target == null || target.Relations.Count == 0) return;
            try
            {
                Ensure(rt);
                if (!_ready) return;
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                if (!(_factionsField.GetValue(geo) is IEnumerable factions)) return;

                // Index client factions by Def guid once.
                var byGuid = new Dictionary<string, object>();
                foreach (var fac in factions)
                {
                    if (fac == null) continue;
                    string g = DefReflection.GetGuid(_factionDefProp.GetValue(fac, null));
                    if (!string.IsNullOrEmpty(g) && !byGuid.ContainsKey(g)) byGuid[g] = fac;
                }

                foreach (var (ownerGuid, withGuid, value) in target.Relations)
                {
                    if (!byGuid.TryGetValue(ownerGuid, out var fac)) continue;
                    var diplomacy = _diplomacyProp.GetValue(fac, null);
                    if (diplomacy == null) continue;
                    if (!(_relationsProp.GetValue(diplomacy, null) is IEnumerable rels)) continue;
                    foreach (var rel in rels)
                    {
                        if (rel == null) continue;
                        var withKey = _withPartyProp.GetValue(rel, null);
                        string g = DefReflection.GetGuid(withKey);
                        if (g != withGuid) continue;
                        try { _relationDiploField.SetValue(rel, value); }
                        catch (Exception ex) { Debug.LogError("[Multipleer] DiplomacyReflection.Apply set failed: " + ex.Message); }
                        break; // one relation per (owner, with) pair
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] DiplomacyReflection.Apply failed: " + ex.Message); }
        }
    }
}
