using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
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

        // ── WA-3 forced-state members (audit gap 4e) — ALL optional, never gate _ready: a miss degrades the
        // channel to the proven value-only mirror. Verified against the decompile (2026-07-05):
        //   • Relation.MaxValue / MinValue — internal int fields (PartyDiplomacy.cs:30-33), the ONLY carriers of
        //     a forced state (SetMaxDiplomacyState writes them, FactionDiplomacy.cs:50-79; every game caller
        //     uses the default limitMax=true branch: MaxValue=stateEntry.Range.Max, MinValue=-MaxDiplomacy).
        //   • host read: PartyDiplomacy.GetDiplomacyState(int) (public, PartyDiplomacy.cs:185) over
        //     relation.MaxValue — the exact previousState read SetMaxDiplomacyState itself performs (:57).
        //   • client stamp: PartyDiplomacy.Def (public readonly field) → PartyDiplomacySettingsDef
        //     .GetStateEntry(PartyDiplomacyState) → PartyDiplomacyStateEntry.Range (RangeDataInt public fields
        //     Min/Max) + PartyDiplomacy.MaxDiplomacy (property → Def.MaxDiplomacy).
        private static FieldInfo _relationMaxField;      // Relation.MaxValue (internal int)
        private static FieldInfo _relationMinField;      // Relation.MinValue (internal int)
        private static MethodInfo _getDiplomacyStateInt; // PartyDiplomacy.GetDiplomacyState(int)
        private static FieldInfo _pdDefField;            // PartyDiplomacy.Def (PartyDiplomacySettingsDef)
        private static PropertyInfo _maxDiplomacyProp;   // PartyDiplomacy.MaxDiplomacy (int)
        private static Type _partyDiplomacyStateType;    // PhoenixPoint.Common.Core.PartyDiplomacyState (enum)
        private static MethodInfo _getStateEntry;        // PartyDiplomacySettingsDef.GetStateEntry(PartyDiplomacyState)
        private static FieldInfo _entryRangeField;       // PartyDiplomacyStateEntry.Range (RangeDataInt)
        private static FieldInfo _rangeMaxField;         // RangeDataInt.Max (public int)

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
                _relationMaxField = AccessTools.Field(relType, "MaxValue");
                _relationMinField = AccessTools.Field(relType, "MinValue");
            }

            // WA-3 forced-state members off the live diplomacy instance (FactionDiplomacy : PartyDiplomacy).
            // Optional — a miss leaves the members null and the mirror degrades to value-only.
            var pdType = diplomacy.GetType();
            _getDiplomacyStateInt = AccessTools.Method(pdType, "GetDiplomacyState", new[] { typeof(int) });
            _pdDefField = AccessTools.Field(pdType, "Def");
            _maxDiplomacyProp = AccessTools.Property(pdType, "MaxDiplomacy");
            _partyDiplomacyStateType = AccessTools.TypeByName("PhoenixPoint.Common.Core.PartyDiplomacyState");
            if (_pdDefField != null && _partyDiplomacyStateType != null)
            {
                // EXACT param match (harmony-accesstools-exact-param-match): GetStateEntry(PartyDiplomacyState).
                _getStateEntry = AccessTools.Method(_pdDefField.FieldType, "GetStateEntry", new[] { _partyDiplomacyStateType });
                var entryType = AccessTools.TypeByName("PhoenixPoint.Common.Core.PartyDiplomacyStateEntry");
                if (entryType != null)
                {
                    _entryRangeField = AccessTools.Field(entryType, "Range");
                    if (_entryRangeField != null)
                        _rangeMaxField = AccessTools.Field(_entryRangeField.FieldType, "Max");
                }
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
                        // WA-3: the relation's FORCED diplomacy cap as a PartyDiplomacyState byte —
                        // GetDiplomacyState(relation.MaxValue), the exact previousState read native
                        // SetMaxDiplomacyState performs (FactionDiplomacy.cs:57). Index-aligned with
                        // Relations; any miss carries StateNotCarried (client skips, never guesses).
                        snap.ForcedStates.Add(ReadForcedState(diplomacy, rel));
                    }
                }
                return snap;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyReflection.Snapshot failed: " + ex.Message); return null; }
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

                for (int i = 0; i < target.Relations.Count; i++)
                {
                    var (ownerGuid, withGuid, value) = target.Relations[i];
                    // WA-3 forced-state byte, index-aligned with the record array (empty on a legacy payload).
                    byte forced = i < target.ForcedStates.Count ? target.ForcedStates[i] : DiplomacySnapshot.StateNotCarried;
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
                        // Stamp the forced cap BEFORE the value so the pair lands consistent in one apply
                        // (the value write bypasses the clamping setter anyway — host already clamped it).
                        if (DiplomacySnapshot.ShouldApplyForcedState(forced))
                            StampForcedState(diplomacy, rel, forced);
                        try { _relationDiploField.SetValue(rel, value); }
                        catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyReflection.Apply set failed: " + ex.Message); }
                        break; // one relation per (owner, with) pair
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyReflection.Apply failed: " + ex.Message); }
        }

        // ─── WA-3 forced-state read / stamp (audit gap 4e) ────────────────────────────────────────────

        /// <summary>Host: the relation's forced diplomacy cap as a raw <c>PartyDiplomacyState</c> byte —
        /// <c>PartyDiplomacy.GetDiplomacyState(relation.MaxValue)</c>. <see cref="DiplomacySnapshot.StateNotCarried"/>
        /// on any miss (unbound members / out-of-range MaxValue throws inside the native lookup).</summary>
        private static byte ReadForcedState(object diplomacy, object rel)
        {
            try
            {
                if (_relationMaxField == null || _getDiplomacyStateInt == null) return DiplomacySnapshot.StateNotCarried;
                int maxValue = Convert.ToInt32(_relationMaxField.GetValue(rel));
                int state = Convert.ToInt32(_getDiplomacyStateInt.Invoke(diplomacy, new object[] { maxValue }));
                return state >= 0 && state <= DiplomacySnapshot.MaxValidState
                    ? (byte)state : DiplomacySnapshot.StateNotCarried;
            }
            catch { return DiplomacySnapshot.StateNotCarried; }
        }

        /// <summary>
        /// Client: VALUE-ONLY stamp of a forced diplomacy state onto the resolved relation — the exact writes
        /// of native <c>SetMaxDiplomacyState(other, state, limitMax: true)</c> (FactionDiplomacy.cs:50-62, the
        /// branch every game caller uses): <c>MaxValue = Def.GetStateEntry(state).Range.Max</c>,
        /// <c>MinValue = -MaxDiplomacy</c> — WITHOUT the <c>OnFactionDiplomacyStateChanged</c> invoke and
        /// WITHOUT the <c>Diplomacy</c> setter clamp (no cascade; the host-clamped value rides the same
        /// snapshot). Best-effort: any miss leaves the relation's caps native (value mirror unaffected).
        /// </summary>
        private static void StampForcedState(object diplomacy, object rel, byte state)
        {
            try
            {
                if (_pdDefField == null || _getStateEntry == null || _entryRangeField == null
                    || _rangeMaxField == null || _relationMaxField == null || _relationMinField == null
                    || _maxDiplomacyProp == null || _partyDiplomacyStateType == null) return;
                var def = _pdDefField.GetValue(diplomacy);
                if (def == null) return;
                object stateEnum = Enum.ToObject(_partyDiplomacyStateType, (int)state);
                var entry = _getStateEntry.Invoke(def, new[] { stateEnum });
                if (entry == null) return;   // def has no entry for this state → leave native
                object range = _entryRangeField.GetValue(entry);   // boxed RangeDataInt
                int rangeMax = Convert.ToInt32(_rangeMaxField.GetValue(range));
                int maxDiplomacy = Convert.ToInt32(_maxDiplomacyProp.GetValue(diplomacy, null));
                _relationMaxField.SetValue(rel, rangeMax);
                _relationMinField.SetValue(rel, -maxDiplomacy);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyReflection.StampForcedState failed: " + ex.Message); }
        }

        // ─── WA-3 dirty trigger: OnFactionDiplomacyStateChanged (FactionDiplomacy.cs:31) ─────────────

        /// <summary>
        /// Host: subscribe a no-arg callback to EVERY faction's
        /// <c>FactionDiplomacy.OnFactionDiplomacyStateChanged</c> (fired by <c>SetMaxDiplomacyState</c> —
        /// the forced war/alliance writer, FactionDiplomacy.cs:50/79) so a forced-state flip re-snapshots
        /// channel #4 immediately instead of waiting for the hourly heartbeat. The event's 4-param delegate
        /// is adapted via the same DynamicMethod pattern as <c>ResearchStateReflection.MakeAdapter</c>.
        /// Returns an opaque token for <see cref="UnsubscribeStateChanged"/>, or null when nothing bound.
        /// </summary>
        public static object SubscribeFactionDiplomacyStateChanged(GeoRuntime rt, Action onChanged)
        {
            if (onChanged == null) return null;
            try
            {
                Ensure(rt);
                var geo = rt?.GeoLevel();
                if (geo == null || _factionsField == null || _diplomacyProp == null) return null;
                if (!(_factionsField.GetValue(geo) is IEnumerable factions)) return null;

                var token = new DiplomacyStateEventToken();
                foreach (var fac in factions)
                {
                    if (fac == null) continue;
                    object diplomacy;
                    try { diplomacy = _diplomacyProp.GetValue(fac, null); } catch { continue; }
                    if (diplomacy == null) continue;
                    var evt = diplomacy.GetType().GetEvent("OnFactionDiplomacyStateChanged",
                                                           BindingFlags.Public | BindingFlags.Instance);
                    if (evt == null) continue;   // plain PartyDiplomacy (no faction event) → skip
                    var handler = MakeAdapter(evt, onChanged);
                    if (handler == null) continue;
                    evt.AddEventHandler(diplomacy, handler);
                    token.Entries.Add((diplomacy, evt, handler));
                }
                return token.Entries.Count > 0 ? token : null;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] DiplomacyReflection.SubscribeFactionDiplomacyStateChanged failed: " + ex.Message);
                return null;
            }
        }

        public static void UnsubscribeStateChanged(object token)
        {
            if (!(token is DiplomacyStateEventToken t)) return;
            foreach (var (target, evt, handler) in t.Entries)
            {
                try { evt.RemoveEventHandler(target, handler); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyReflection.UnsubscribeStateChanged failed: " + ex.Message); }
            }
            t.Entries.Clear();
        }

        private sealed class DiplomacyStateEventToken
        {
            public readonly List<(object target, EventInfo evt, Delegate handler)> Entries
                = new List<(object, EventInfo, Delegate)>();
        }

        /// <summary>Emit a DynamicMethod delegate matching <paramref name="evt"/>'s signature that ignores its
        /// args and calls <paramref name="onChanged"/> (the <c>ResearchStateReflection.MakeAdapter</c> pattern).</summary>
        private static Delegate MakeAdapter(EventInfo evt, Action onChanged)
        {
            if (evt == null) return null;
            try
            {
                Type handlerType = evt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null) return null;
                ParameterInfo[] ps = invoke.GetParameters();

                Type[] dmSig = new Type[ps.Length + 1];
                dmSig[0] = typeof(Action);
                for (int i = 0; i < ps.Length; i++) dmSig[i + 1] = ps[i].ParameterType;

                var dm = new DynamicMethod("Faction_DiplomacyState_Adapter", typeof(void), dmSig,
                    typeof(DiplomacyReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType, onChanged);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyReflection.MakeAdapter failed: " + ex.Message); return null; }
        }
    }
}
