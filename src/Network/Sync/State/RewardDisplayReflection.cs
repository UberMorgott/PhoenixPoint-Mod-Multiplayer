using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// HOST read + CLIENT render bridge for the geoscape-event REWARD DISPLAY (the structured delta lines the
    /// native <c>UIModuleSiteEncounters.ShowReward</c> draws). The mod has NO compile-time game references, so
    /// every member is resolved by name and cached. This file is NOT linked into the test project (it binds
    /// live game types); only the pure <see cref="RewardDisplaySnapshot"/> codec is unit-tested.
    ///
    /// HOST (<see cref="BuildFromReward"/>): reads a resolved <c>GeoscapeEvent.ChoiceReward</c>
    /// (<c>GeoFactionReward</c>, GeoFactionReward.cs) + its <c>.ApplyResult</c>
    /// (<c>GeoFactionRewardApplyResult</c>, GeoFactionRewardApplyResult.cs) into a wire snapshot — exactly the
    /// fields ShowReward renders (UIModuleSiteEncounters.cs:363-513), NOTHING ELSE. Display strings (faction /
    /// leader name, soldier name) are pre-resolved host-side so the client never re-resolves a GeoCharacter /
    /// GeoHavenLeader entity (impossible to fake reliably) — it just formats the same text.
    ///
    /// CLIENT (<see cref="Render"/>): after the synthetic result page is shown, locates the live
    /// <c>UIModuleSiteEncounters</c> (GeoLevelController.View → GeoscapeView.GeoscapeModules →
    /// GeoscapeModulesData.SiteEncountersModule) and replays each reward line through the module's OWN private
    /// <c>AddRewardText(string)</c> + its localized text-key patterns + <c>PositiveRewardTextPattern</c> — the
    /// SAME native renderer the host used. It calls NO <c>GeoFactionReward.Apply</c> and mutates NO state.
    /// An entity that can't be resolved (rare site/zone) DROPS that single line (one debug log), never NREs.
    ///
    /// Verified against the decompile (2026-06-17):
    ///   • GeoFactionReward fields: Units(List&lt;GeoCharacter&gt;), Resources(ResourcePack),
    ///     RevealedSites(List&lt;GeoSite&gt;) — ShowReward reads these off geoEvent.ChoiceReward (the ORIGINAL).
    ///   • GeoFactionRewardApplyResult fields: Resources(ResourcePack), Items(ItemStorage),
    ///     Diplomacy(List&lt;RewardDiplomacyChange&gt;), Units(List&lt;RewardNewUnit&gt;),
    ///     RevealedSites(List&lt;GeoSite&gt;), DamagedSoldiers/TiredSoldiers(Dictionary&lt;GeoCharacter,int&gt;),
    ///     AllSoldiersDamage/AllSoldiersTiredness/FactionSkillPoints(int),
    ///     ChangeHavenPopulation(List&lt;KeyValuePair&lt;GeoHaven,int&gt;&gt;),
    ///     DamageZones(List&lt;KeyValuePair&lt;GeoHavenZone,int&gt;&gt;),
    ///     ChangeMaxDiplomacyState(List&lt;RewardMaxDiplomacyStateChange&gt;),
    ///     SpawnedHavenDefensesAt(List&lt;GeoSite&gt;), NewPhoenixBase(GeoPhoenixBase).
    ///   • RewardDiplomacyChange { IDiplomaticParty Party; IDiplomaticParty Target; int Value; }.
    ///   • ResourcePack : IEnumerable&lt;ResourceUnit{ ResourceType Type; float Value; int RoundedValue }&gt;.
    ///   • module privates: AddRewardText(string) :626; text keys EncounterLeaderDiplomacyChangedTextKey /
    ///     EncounterFactionDiplomacyChangedTextKey / RecruitSingleSoldierTextKey / AddSkillPointsTextKey /
    ///     AircraftSoldiersInjuredTextKey / AircraftSoldiersTiredTextKey / AllSoldierInjuredTextKey /
    ///     AllSoldierTiredTextKey / HavenPopulationChangeTextKey / HavenZoneDamageTextKey /
    ///     NewDiplomaticStateTextKey / MultipleHavensAttackedTextKey / NewPhoenixBaseTextKey /
    ///     SiteRevealedTextKey (LocalizedTextBind public fields); PositiveRewardTextPattern /
    ///     NegativeRewardTextPattern (public auto-props); ResourcesList(NamedListDef) :36.
    /// </summary>
    public static class RewardDisplayReflection
    {
        // ─── host read ───
        private static bool _hostReady;
        private static FieldInfo _frUnits, _frResources, _frRevealedSites;          // GeoFactionReward
        private static FieldInfo _arResources, _arItems, _arDiplomacy;               // GeoFactionRewardApplyResult
        private static FieldInfo _arDamagedSoldiers, _arTiredSoldiers, _arAllDamage, _arAllTired, _arSkillPoints;
        private static FieldInfo _arChangeHavenPop, _arDamageZones, _arMaxDiplo, _arSpawnedHavens, _arNewBase;
        private static FieldInfo _dipParty, _dipTarget, _dipValue;                   // RewardDiplomacyChange
        private static FieldInfo _resType;                                           // ResourceUnit.Type (public FIELD, not a property)
        private static PropertyInfo _resRounded;                                     // ResourceUnit.RoundedValue (read-only property)
        private static PropertyInfo _itemsItems;                                     // ItemStorage.Items (IReadOnlyDictionary)
        private static Type _havenLeaderType, _geoFactionType;
        private static FieldInfo _siteIdField;                                       // GeoSite.SiteId

        // ─── client render ───
        // The module instance is handed in by the ShowEncounter Postfix (__instance); no live-fetch needed.
        private static bool _clientReady;
        private static MethodInfo _addRewardText;     // UIModuleSiteEncounters.AddRewardText(string)
        private static PropertyInfo _posPattern, _negPattern;
        private static FieldInfo _resourcesListField; // ResourcesList (NamedListDef)
        // text keys
        private static FieldInfo _kLeaderDiplo, _kFactionDiplo, _kRecruit, _kSkill, _kInjured, _kTired,
            _kAllInjured, _kAllTired, _kHavenPop, _kZone, _kMaxDiplo, _kHavensAttacked, _kNewBase, _kSiteRevealed;
        private static MethodInfo _localize;          // LocalizedTextBind.Localize()
        private static MethodInfo _namedListGetDef;   // NamedListDef.GetDef<ViewElementDef>(string)
        private static FieldInfo _viewDisplayName1; // ViewElementDef.DisplayName1 (LocalizedTextBind, public FIELD)
        private static MethodInfo _itemDefGetDisplay;  // ItemDef.GetDisplayName() (LocalizedTextBind)
        private static Type _viewElementDefType;

        // ───────────────────────── HOST ─────────────────────────

        private static void EnsureHost()
        {
            if (_hostReady) return;
            var frType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.GeoFactionReward");
            var arType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.GeoFactionRewardApplyResult");
            var dipType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.RewardDiplomacyChange");
            var resUnitType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit");
            var storageType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.ItemStorage");
            _havenLeaderType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.GeoHavenLeader");
            _geoFactionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (frType == null || arType == null) return;

            _frUnits = AccessTools.Field(frType, "Units");
            _frResources = AccessTools.Field(frType, "Resources");
            _frRevealedSites = AccessTools.Field(frType, "RevealedSites");
            var arField = AccessTools.Field(frType, "ApplyResult"); // ApplyResult is a public field on GeoFactionReward

            _arResources = AccessTools.Field(arType, "Resources");
            _arItems = AccessTools.Field(arType, "Items");
            _arDiplomacy = AccessTools.Field(arType, "Diplomacy");
            _arDamagedSoldiers = AccessTools.Field(arType, "DamagedSoldiers");
            _arTiredSoldiers = AccessTools.Field(arType, "TiredSoldiers");
            _arAllDamage = AccessTools.Field(arType, "AllSoldiersDamage");
            _arAllTired = AccessTools.Field(arType, "AllSoldiersTiredness");
            _arSkillPoints = AccessTools.Field(arType, "FactionSkillPoints");
            _arChangeHavenPop = AccessTools.Field(arType, "ChangeHavenPopulation");
            _arDamageZones = AccessTools.Field(arType, "DamageZones");
            _arMaxDiplo = AccessTools.Field(arType, "ChangeMaxDiplomacyState");
            _arSpawnedHavens = AccessTools.Field(arType, "SpawnedHavenDefensesAt");
            _arNewBase = AccessTools.Field(arType, "NewPhoenixBase");

            if (dipType != null)
            {
                _dipParty = AccessTools.Field(dipType, "Party");
                _dipTarget = AccessTools.Field(dipType, "Target");
                _dipValue = AccessTools.Field(dipType, "Value");
            }
            if (resUnitType != null)
            {
                // ResourceUnit.Type is a public FIELD (ResourceUnit.cs:12), NOT a property — AccessTools.Property
                // returned null here, silently zeroing every resource line on the client result card (BUG-B; same
                // class as commit 1205066). RoundedValue IS a read-only property (:17).
                _resType = AccessTools.Field(resUnitType, "Type");
                _resRounded = AccessTools.Property(resUnitType, "RoundedValue");
            }
            if (storageType != null) _itemsItems = AccessTools.Property(storageType, "Items");
            if (geoSiteType != null) _siteIdField = AccessTools.Field(geoSiteType, "SiteId");

            // Stash the ApplyResult accessor as a "property-like" via a tiny wrapper field read.
            _frApplyResultField = arField;

            _hostReady = _frApplyResultField != null && _arResources != null && _arDiplomacy != null;
        }

        private static FieldInfo _frApplyResultField; // GeoFactionReward.ApplyResult (public field)

        /// <summary>
        /// HOST: read a resolved <c>GeoscapeEvent.ChoiceReward</c> (a <c>GeoFactionReward</c>) + its
        /// <c>.ApplyResult</c> into a wire snapshot of the lines ShowReward renders. Returns an empty snapshot
        /// (never null) on any failure so the dismiss still broadcasts (reward-less card). Every field read is
        /// individually guarded — a partial reward degrades to the lines that DID read.
        /// </summary>
        public static RewardDisplaySnapshot BuildFromReward(object choiceReward)
        {
            var snap = new RewardDisplaySnapshot();
            try
            {
                EnsureHost();
                if (!_hostReady || choiceReward == null) return snap;
                object ar = null;
                try { ar = _frApplyResultField.GetValue(choiceReward); } catch { ar = null; }

                // Resources — native renders geoEvent.ChoiceReward.Resources (ResourcePack of ResourceUnit).
                TryReadResources(_frResources?.GetValue(choiceReward), snap);
                // If the original reward had none but the apply-result does, fall back to that.
                if (snap.Resources.Count == 0 && ar != null) TryReadResources(_arResources?.GetValue(ar), snap);

                if (ar != null)
                {
                    TryReadDiplomacy(_arDiplomacy?.GetValue(ar) as IEnumerable, snap);
                    TryReadItems(_arItems?.GetValue(ar), snap);
                    snap.DamagedSoldiersSum = SumDict(_arDamagedSoldiers?.GetValue(ar) as IDictionary);
                    snap.TiredSoldiersSum = SumDict(_arTiredSoldiers?.GetValue(ar) as IDictionary);
                    snap.AllSoldiersDamage = ToInt(_arAllDamage?.GetValue(ar));
                    snap.AllSoldiersTiredness = ToInt(_arAllTired?.GetValue(ar));
                    snap.FactionSkillPoints = ToInt(_arSkillPoints?.GetValue(ar));
                    snap.SpawnedHavenDefensesCount = CountEnum(_arSpawnedHavens?.GetValue(ar) as IEnumerable);
                    TryReadHavenPop(_arChangeHavenPop?.GetValue(ar) as IEnumerable, snap);
                    TryReadZones(_arDamageZones?.GetValue(ar) as IEnumerable, snap);
                    TryReadMaxDiplo(_arMaxDiplo?.GetValue(ar) as IEnumerable, snap);
                    snap.NewPhoenixBaseSiteId = NewBaseSiteId(_arNewBase?.GetValue(ar));
                }

                // Units — native renders geoEvent.ChoiceReward.Units (List<GeoCharacter>); carry NAMES only.
                TryReadUnitNames(_frUnits?.GetValue(choiceReward) as IEnumerable, snap);
                // RevealedSites — native renders geoEvent.ChoiceReward.RevealedSites (List<GeoSite>); carry ids.
                TryReadSiteIds(_frRevealedSites?.GetValue(choiceReward) as IEnumerable, snap);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] RewardDisplayReflection.BuildFromReward failed: " + ex.Message); }
            return snap;
        }

        private static void TryReadResources(object resourcePack, RewardDisplaySnapshot snap)
        {
            try
            {
                if (!(resourcePack is IEnumerable units)) return;
                if (_resType == null || _resRounded == null)
                {
                    // Promoted from a silent return (BUG-B): a missing reflection bind here zeroes ALL reward
                    // resource lines on the client, so make it loud instead of invisible.
                    Debug.LogWarning("[Multipleer] reward.Resources SKIPPED: ResourceUnit reflection unbound (_resType="
                                     + (_resType != null) + " _resRounded=" + (_resRounded != null) + ")");
                    return;
                }
                foreach (var u in units)
                {
                    int rounded = ToInt(_resRounded.GetValue(u, null));
                    if (rounded == 0) continue; // native skips zero (ResourcePack.IsEmpty semantics)
                    int type = Convert.ToInt32(_resType.GetValue(u));   // field get (ResourceUnit.Type is a field)
                    snap.Resources.Add(new RewardResourceLine(type, rounded));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.Resources read failed: " + ex.Message); }
        }

        private static void TryReadDiplomacy(IEnumerable diplomacy, RewardDisplaySnapshot snap)
        {
            try
            {
                if (diplomacy == null || _dipParty == null) return;
                foreach (var d in diplomacy)
                {
                    if (d == null) continue;
                    var party = _dipParty.GetValue(d);
                    var target = _dipTarget.GetValue(d);
                    int value = ToInt(_dipValue.GetValue(d));
                    DescribeParty(party, out byte pk, out string pName);
                    DescribeParty(target, out byte tk, out string tName);
                    snap.Diplomacy.Add(new RewardDiplomacyLine(pk, pName, tk, tName, value));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.Diplomacy read failed: " + ex.Message); }
        }

        // kind 0 = GeoFaction (name = ToString()), 1 = GeoHavenLeader (name = GetName()), 2 = none/unknown.
        private static void DescribeParty(object party, out byte kind, out string name)
        {
            kind = 2; name = "";
            if (party == null) return;
            try
            {
                if (_havenLeaderType != null && _havenLeaderType.IsInstanceOfType(party))
                {
                    kind = 1;
                    var m = AccessTools.Method(party.GetType(), "GetName");
                    name = m?.Invoke(party, null) as string ?? "";
                }
                else if (_geoFactionType != null && _geoFactionType.IsInstanceOfType(party))
                {
                    kind = 0;
                    name = party.ToString() ?? ""; // GeoFaction.ToString() == Name.Localize()
                }
                else { kind = 2; name = party.ToString() ?? ""; }
            }
            catch { kind = 2; name = ""; }
        }

        private static void TryReadItems(object itemStorage, RewardDisplaySnapshot snap)
        {
            try
            {
                if (itemStorage == null || _itemsItems == null) return;
                if (!(_itemsItems.GetValue(itemStorage, null) is IEnumerable kvps)) return;
                foreach (var kvp in kvps)
                {
                    // kvp is KeyValuePair<ItemDef, GeoItem>; read Key(ItemDef).Guid + Value.CommonItemData.Count.
                    var key = kvp.GetType().GetProperty("Key")?.GetValue(kvp, null);     // ItemDef
                    var val = kvp.GetType().GetProperty("Value")?.GetValue(kvp, null);    // GeoItem
                    string guid = DefReflection.GetGuid(key) ?? "";
                    int count = 1;
                    try
                    {
                        var cid = AccessTools.Property(val.GetType(), "CommonItemData")?.GetValue(val, null);
                        var cnt = cid != null ? AccessTools.Property(cid.GetType(), "Count")?.GetValue(cid, null) : null;
                        count = cnt != null ? Convert.ToInt32(cnt) : 1;
                    }
                    catch { count = 1; }
                    if (!string.IsNullOrEmpty(guid)) snap.Items.Add(new RewardItemLine(guid, count));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.Items read failed: " + ex.Message); }
        }

        private static void TryReadUnitNames(IEnumerable units, RewardDisplaySnapshot snap)
        {
            try
            {
                if (units == null) return;
                foreach (var u in units)
                {
                    if (u == null) continue;
                    var m = AccessTools.Method(u.GetType(), "GetName");
                    string name = m?.Invoke(u, null) as string ?? "";
                    snap.Units.Add(name);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.Units read failed: " + ex.Message); }
        }

        private static void TryReadSiteIds(IEnumerable sites, RewardDisplaySnapshot snap)
        {
            try
            {
                if (sites == null || _siteIdField == null) return;
                foreach (var s in sites)
                {
                    if (s == null) continue;
                    var id = _siteIdField.GetValue(s);
                    if (id is int i) snap.RevealedSites.Add(i);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.RevealedSites read failed: " + ex.Message); }
        }

        private static void TryReadHavenPop(IEnumerable changeHavenPop, RewardDisplaySnapshot snap)
        {
            try
            {
                if (changeHavenPop == null) return;
                foreach (var kvp in changeHavenPop)
                {
                    // KeyValuePair<GeoHaven, int>; GeoHaven.Site.SiteId + value.
                    var haven = kvp.GetType().GetProperty("Key")?.GetValue(kvp, null);
                    int delta = ToInt(kvp.GetType().GetProperty("Value")?.GetValue(kvp, null));
                    int siteId = HavenSiteId(haven);
                    snap.HavenPopulation.Add(new RewardHavenPopLine(siteId, delta));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.ChangeHavenPopulation read failed: " + ex.Message); }
        }

        private static int HavenSiteId(object haven)
        {
            try
            {
                if (haven == null) return -1;
                var site = AccessTools.Property(haven.GetType(), "Site")?.GetValue(haven, null);
                if (site == null || _siteIdField == null) return -1;
                var id = _siteIdField.GetValue(site);
                return id is int i ? i : -1;
            }
            catch { return -1; }
        }

        private static void TryReadZones(IEnumerable damageZones, RewardDisplaySnapshot snap)
        {
            try
            {
                if (damageZones == null) return;
                foreach (var kvp in damageZones)
                {
                    // KeyValuePair<GeoHavenZone, int>; native reads zone.Def.ViewElementDef.DisplayName1.
                    var zone = kvp.GetType().GetProperty("Key")?.GetValue(kvp, null);
                    int dmg = ToInt(kvp.GetType().GetProperty("Value")?.GetValue(kvp, null));
                    string viewGuid = ZoneViewDefGuid(zone);
                    int havenSiteId = ZoneHavenSiteId(zone);
                    if (!string.IsNullOrEmpty(viewGuid)) snap.DamageZones.Add(new RewardZoneLine(havenSiteId, viewGuid, dmg));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.DamageZones read failed: " + ex.Message); }
        }

        private static string ZoneViewDefGuid(object zone)
        {
            try
            {
                var def = AccessTools.Property(zone.GetType(), "Def")?.GetValue(zone, null);
                var view = def != null ? AccessTools.Field(def.GetType(), "ViewElementDef")?.GetValue(def)
                                          ?? AccessTools.Property(def.GetType(), "ViewElementDef")?.GetValue(def, null)
                                       : null;
                return DefReflection.GetGuid(view) ?? "";
            }
            catch { return ""; }
        }

        private static int ZoneHavenSiteId(object zone)
        {
            try
            {
                var haven = AccessTools.Property(zone.GetType(), "Haven")?.GetValue(zone, null);
                return HavenSiteId(haven);
            }
            catch { return -1; }
        }

        private static void TryReadMaxDiplo(IEnumerable changeMaxDiplo, RewardDisplaySnapshot snap)
        {
            try
            {
                if (changeMaxDiplo == null) return;
                foreach (var c in changeMaxDiplo)
                {
                    if (c == null) continue;
                    // RewardMaxDiplomacyStateChange.Faction (GeoFaction) → native renders Faction.ToString().
                    var fac = AccessTools.Field(c.GetType(), "Faction")?.GetValue(c)
                              ?? AccessTools.Property(c.GetType(), "Faction")?.GetValue(c, null);
                    snap.MaxDiplomacyFactionGuids.Add(fac?.ToString() ?? "");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] reward.ChangeMaxDiplomacyState read failed: " + ex.Message); }
        }

        private static int NewBaseSiteId(object newBase)
        {
            try
            {
                if (newBase == null) return -1;
                // GeoPhoenixBase.Site (GeoSite) → SiteId.
                var site = AccessTools.Property(newBase.GetType(), "Site")?.GetValue(newBase, null);
                if (site == null || _siteIdField == null) return -1;
                var id = _siteIdField.GetValue(site);
                return id is int i ? i : -1;
            }
            catch { return -1; }
        }

        private static int SumDict(IDictionary dict)
        {
            try
            {
                if (dict == null) return 0;
                int sum = 0;
                foreach (var v in dict.Values) sum += ToInt(v);
                return sum;
            }
            catch { return 0; }
        }

        private static int CountEnum(IEnumerable e)
        {
            try { if (e == null) return 0; int n = 0; foreach (var _ in e) n++; return n; }
            catch { return 0; }
        }

        private static int ToInt(object o) { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }

        // ───────────────────────── CLIENT ─────────────────────────

        private static void EnsureClient(GeoRuntime rt)
        {
            if (_clientReady) return;
            var moduleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var locBindType = AccessTools.TypeByName("Base.UI.LocalizedTextBind");
            var namedListType = AccessTools.TypeByName("Base.Defs.NamedListDef");
            _viewElementDefType = AccessTools.TypeByName("PhoenixPoint.Common.UI.ViewElementDef");
            var itemDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
            if (moduleType == null) return;

            _addRewardText = AccessTools.Method(moduleType, "AddRewardText", new[] { typeof(string) });
            _posPattern = AccessTools.Property(moduleType, "PositiveRewardTextPattern");
            _negPattern = AccessTools.Property(moduleType, "NegativeRewardTextPattern");
            _resourcesListField = AccessTools.Field(moduleType, "ResourcesList");

            _kLeaderDiplo = AccessTools.Field(moduleType, "EncounterLeaderDiplomacyChangedTextKey");
            _kFactionDiplo = AccessTools.Field(moduleType, "EncounterFactionDiplomacyChangedTextKey");
            _kRecruit = AccessTools.Field(moduleType, "RecruitSingleSoldierTextKey");
            _kSkill = AccessTools.Field(moduleType, "AddSkillPointsTextKey");
            _kInjured = AccessTools.Field(moduleType, "AircraftSoldiersInjuredTextKey");
            _kTired = AccessTools.Field(moduleType, "AircraftSoldiersTiredTextKey");
            _kAllInjured = AccessTools.Field(moduleType, "AllSoldierInjuredTextKey");
            _kAllTired = AccessTools.Field(moduleType, "AllSoldierTiredTextKey");
            _kHavenPop = AccessTools.Field(moduleType, "HavenPopulationChangeTextKey");
            _kZone = AccessTools.Field(moduleType, "HavenZoneDamageTextKey");
            _kMaxDiplo = AccessTools.Field(moduleType, "NewDiplomaticStateTextKey");
            _kHavensAttacked = AccessTools.Field(moduleType, "MultipleHavensAttackedTextKey");
            _kNewBase = AccessTools.Field(moduleType, "NewPhoenixBaseTextKey");
            _kSiteRevealed = AccessTools.Field(moduleType, "SiteRevealedTextKey");

            if (locBindType != null) _localize = AccessTools.Method(locBindType, "Localize");
            if (namedListType != null)
            {
                // NamedListDef declares BOTH GetDef(string) and GetDef<T>(string); AccessTools.Method /
                // Type.GetMethod(name, Type[]) matches both and throws AmbiguousMatchException (which the outer
                // catch swallowed → zero reward lines). Pick the GENERIC overload unambiguously.
                var generic = MethodOverloadResolver.FindGenericSingleStringMethod(namedListType, "GetDef");
                if (generic != null && _viewElementDefType != null)
                    _namedListGetDef = generic.MakeGenericMethod(_viewElementDefType);
            }
            if (_viewElementDefType != null) _viewDisplayName1 = AccessTools.Field(_viewElementDefType, "DisplayName1");
            if (itemDefType != null) _itemDefGetDisplay = AccessTools.Method(itemDefType, "GetDisplayName");

            // The module is handed in by the deterministic ShowEncounter Postfix (RewardRenderPatch), so the
            // live-module-fetch fields are NOT required for readiness — only the renderer's own members are.
            _clientReady = _addRewardText != null && _localize != null;
        }

        // ─── deterministic one-shot pending slot (armed by SyncEngine, consumed by the ShowEncounter hook) ───
        // The synthetic result GeoscapeEvent is correlated by REFERENCE IDENTITY (its EventID is "" so it can't
        // be matched by id). A single slot: arming overwrites any stale prior (last dismiss wins); a stale
        // reference can never match a different event's ShowEncounter, so a lingering slot is harmless.
        private static readonly object _pendingLock = new object();
        private static object _pendingEvent;                 // the exact synthetic GeoscapeEvent instance
        private static RewardDisplaySnapshot _pendingReward;  // its reward lines

        /// <summary>Client: arm the reward render for a specific synthetic result event instance (one-shot).</summary>
        public static void SetPending(object syntheticEvent, RewardDisplaySnapshot reward)
        {
            if (syntheticEvent == null || reward == null || reward.IsEmpty) return;
            lock (_pendingLock) { _pendingEvent = syntheticEvent; _pendingReward = reward; }
        }

        /// <summary>
        /// Client (ShowEncounter Postfix): if <paramref name="shownEvent"/> IS our armed synthetic result event
        /// (reference identity), consume + clear the slot and return its reward. Returns null (no-op) for the
        /// original choice dialog or any unrelated event — so the render only ever lands on our page, exactly once.
        /// </summary>
        public static RewardDisplaySnapshot TryConsume(object shownEvent)
        {
            if (shownEvent == null) return null;
            lock (_pendingLock)
            {
                if (!ReferenceEquals(shownEvent, _pendingEvent)) return null;
                var reward = _pendingReward;
                _pendingEvent = null; _pendingReward = null;   // one-shot
                return reward;
            }
        }

        /// <summary>Client: drop any armed-but-unconsumed reward (e.g. a new event raised first). Idempotent.</summary>
        public static void ClearPending()
        {
            lock (_pendingLock) { _pendingEvent = null; _pendingReward = null; }
        }

        /// <summary>
        /// CLIENT: render the reward delta lines onto <paramref name="module"/> (the freshly-built
        /// <c>UIModuleSiteEncounters</c> handed in by the ShowEncounter Postfix) through its native
        /// <c>AddRewardText</c> — the SAME renderer the host used (no <c>Apply</c>, no state mutation, no
        /// hand-drawn UI). Each line is best-effort: an unresolved entity DROPS that line and the rest render.
        /// </summary>
        public static void Render(GeoRuntime rt, object module, RewardDisplaySnapshot snap)
        {
            if (snap == null || snap.IsEmpty || module == null) return;
            try
            {
                EnsureClient(rt);
                if (!_clientReady) { Debug.Log("[Multipleer] RewardDisplayRender SKIP (reflection not ready)"); return; }

                string pos = _posPattern?.GetValue(module, null) as string ?? "{0}";
                string neg = _negPattern?.GetValue(module, null) as string ?? "{0}";
                int lines = 0;

                // Diplomacy (UIModuleSiteEncounters.cs:369-395).
                foreach (var d in snap.Diplomacy)
                {
                    string valTxt = string.Format(d.Value > 0 ? pos : neg, d.Value.ToString("+#;-#"));
                    string text = null;
                    if (d.PartyKind == 1) // leader party → 2-arg key (targetName, valTxt)
                        text = FormatKey(module, _kLeaderDiplo, d.TargetKey, valTxt);
                    else if (d.PartyKind == 0) // faction party → 3-arg key (partyName, targetName, valTxt)
                        text = FormatKey(module, _kFactionDiplo, d.PartyKey, d.TargetKey, valTxt);
                    else // unknown PartyKind → don't drop it silently (visibility for an unmapped diplomacy line)
                        Debug.LogWarning("[Multipleer] reward.Diplomacy line with unmapped PartyKind=" + d.PartyKind
                                         + " (target=" + d.TargetKey + " value=" + d.Value + ") — not rendered");
                    if (Add(module, text)) lines++;
                }

                // Items (UIModuleSiteEncounters.cs:397-403): "<displayName> x <+count> ".
                foreach (var it in snap.Items)
                {
                    string display = ItemDisplayName(it.ItemDefGuid);
                    if (string.IsNullOrEmpty(display)) { Debug.Log("[Multipleer] reward item dropped (unresolved def " + it.ItemDefGuid + ")"); continue; }
                    string text = display + " x " + string.Format(pos, it.Count.ToString()) + " ";
                    if (Add(module, text)) lines++;
                }

                // Units (UIModuleSiteEncounters.cs:405-411): RecruitSingleSoldierTextKey(name).
                foreach (var u in snap.Units)
                    if (Add(module, FormatKey(module, _kRecruit, u))) lines++;

                // Resources (UIModuleSiteEncounters.cs:413-419): "<resourceDisplayName> <+rounded>".
                foreach (var r in snap.Resources)
                {
                    string resName = ResourceDisplayName(module, r.ResourceType);
                    if (string.IsNullOrEmpty(resName)) { Debug.Log("[Multipleer] reward resource dropped (unresolved type " + r.ResourceType + ")"); continue; }
                    string text = resName + " " + string.Format(pos, r.RoundedValue.ToString("+#;-#"));
                    if (Add(module, text)) lines++;
                }

                // Revealed sites (UIModuleSiteEncounters.cs:421-431): SiteRevealedTextKey(siteTypeOrName).
                foreach (var siteId in snap.RevealedSites)
                {
                    string siteName = RevealedSiteName(rt, siteId);
                    if (string.IsNullOrEmpty(siteName)) { Debug.Log("[Multipleer] reward revealed-site dropped (unresolved id " + siteId + ")"); continue; }
                    if (Add(module, FormatKey(module, _kSiteRevealed, siteName))) lines++;
                }

                // Soldier injured / tired sums (UIModuleSiteEncounters.cs:437-457).
                if (snap.DamagedSoldiersSum > 0) { if (Add(module, FormatKey(module, _kInjured, snap.DamagedSoldiersSum))) lines++; }
                if (snap.TiredSoldiersSum > 0) { if (Add(module, FormatKey(module, _kTired, snap.TiredSoldiersSum))) lines++; }
                if (snap.AllSoldiersDamage > 0) { if (Add(module, FormatKey(module, _kAllInjured, snap.AllSoldiersDamage))) lines++; }
                if (snap.AllSoldiersTiredness > 0) { if (Add(module, FormatKey(module, _kAllTired, snap.AllSoldiersTiredness))) lines++; }

                // Spawned haven defenses (UIModuleSiteEncounters.cs:459-463).
                if (snap.SpawnedHavenDefensesCount > 0) { if (Add(module, LocalizeKey(_kHavensAttacked?.GetValue(module)))) lines++; }

                // Faction skill points (UIModuleSiteEncounters.cs:464-468).
                if (snap.FactionSkillPoints > 0) { if (Add(module, FormatKey(module, _kSkill, snap.FactionSkillPoints))) lines++; }

                // Haven zone damage (UIModuleSiteEncounters.cs:469-478): HavenZoneDamageTextKey(zoneName, dmg).
                foreach (var z in snap.DamageZones)
                {
                    string zoneName = ViewDefDisplayName(z.ZoneViewDefGuid);
                    if (string.IsNullOrEmpty(zoneName)) { Debug.Log("[Multipleer] reward zone dropped (unresolved view " + z.ZoneViewDefGuid + ")"); continue; }
                    if (Add(module, FormatKey(module, _kZone, zoneName, z.Damage))) lines++;
                }

                // Haven population change (UIModuleSiteEncounters.cs:480-488): HavenPopulationChangeTextKey(siteName, delta).
                foreach (var h in snap.HavenPopulation)
                {
                    string siteName = RevealedSiteName(rt, h.HavenSiteId);
                    if (string.IsNullOrEmpty(siteName)) { Debug.Log("[Multipleer] reward haven-pop dropped (unresolved id " + h.HavenSiteId + ")"); continue; }
                    if (Add(module, FormatKey(module, _kHavenPop, siteName, h.Delta))) lines++;
                }

                // Max-diplomacy state (UIModuleSiteEncounters.cs:490-497): NewDiplomaticStateTextKey(factionName).
                foreach (var facName in snap.MaxDiplomacyFactionGuids)
                    if (!string.IsNullOrEmpty(facName) && Add(module, FormatKey(module, _kMaxDiplo, facName))) lines++;

                // New Phoenix base (UIModuleSiteEncounters.cs:509-513): NewPhoenixBaseTextKey(siteName).
                if (snap.NewPhoenixBaseSiteId >= 0)
                {
                    string siteName = RevealedSiteName(rt, snap.NewPhoenixBaseSiteId);
                    if (!string.IsNullOrEmpty(siteName) && Add(module, FormatKey(module, _kNewBase, siteName))) lines++;
                }

                Debug.Log("[Multipleer] RewardDisplayRender drew " + lines + " reward line(s) via native AddRewardText");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] RewardDisplayReflection.Render failed: " + ex.Message); }
        }

        private static bool Add(object module, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return false;
                _addRewardText.Invoke(module, new object[] { text });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] AddRewardText failed (line dropped): " + ex.Message); return false; }
        }

        private static string LocalizeBind(object bind)
        {
            try { return bind == null ? null : _localize.Invoke(bind, null) as string; }
            catch { return null; }
        }

        private static string LocalizeKey(object bind) => LocalizeBind(bind);

        // string.Format(key.Localize(), args...) with each arg ToString()'d (ints rendered plain).
        private static string FormatKey(object module, FieldInfo keyField, params object[] args)
        {
            try
            {
                var bind = keyField?.GetValue(module);
                string fmt = LocalizeBind(bind);
                if (string.IsNullOrEmpty(fmt)) return null;
                return string.Format(fmt, args);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] FormatKey failed: " + ex.Message); return null; }
        }

        // Resource name like native :417 — ResourcesList.GetDef<ViewElementDef>(type.ToString()).DisplayName1.Localize().
        private static string ResourceDisplayName(object module, int resourceTypeRaw)
        {
            try
            {
                if (_resourcesListField == null || _namedListGetDef == null || _viewDisplayName1 == null) return null;
                var list = _resourcesListField.GetValue(module);
                if (list == null) return null;
                // ResourceType enum name from its raw value.
                var resEnum = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType");
                string typeName = resEnum != null ? Enum.GetName(resEnum, Enum.ToObject(resEnum, resourceTypeRaw)) : resourceTypeRaw.ToString();
                if (string.IsNullOrEmpty(typeName)) return null;
                var viewDef = _namedListGetDef.Invoke(list, new object[] { typeName });
                if (viewDef == null) return null;
                var bind = _viewDisplayName1.GetValue(viewDef);
                return LocalizeBind(bind);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResourceDisplayName failed: " + ex.Message); return null; }
        }

        private static string ItemDisplayName(string itemDefGuid)
        {
            try
            {
                var def = DefReflection.GetDefByGuid(itemDefGuid);
                if (def == null || _itemDefGetDisplay == null) return null;
                var bind = _itemDefGetDisplay.Invoke(def, null);
                return LocalizeBind(bind);
            }
            catch { return null; }
        }

        private static string ViewDefDisplayName(string viewDefGuid)
        {
            try
            {
                var def = DefReflection.GetDefByGuid(viewDefGuid);
                if (def == null || _viewDisplayName1 == null) return null;
                var bind = _viewDisplayName1.GetValue(def);
                return LocalizeBind(bind);
            }
            catch { return null; }
        }

        // Resolve a revealed/haven/base GeoSite by id and return its localized site name (native uses the site's
        // type/name; the site channel keeps the client site fresh). Drops the line if unresolved.
        private static string RevealedSiteName(GeoRuntime rt, int siteId)
        {
            try
            {
                if (siteId < 0) return null;
                var site = GeoSiteReflection.ResolveSiteById(rt, siteId);
                if (site == null) return null;
                var nameProp = AccessTools.Property(site.GetType(), "LocalizedSiteName");
                string name = nameProp?.GetValue(site, null) as string;
                if (!string.IsNullOrEmpty(name)) return name;
                // Fallback: site type string (mirrors native non-encounter branch).
                var typeProp = AccessTools.Property(site.GetType(), "Type");
                return typeProp?.GetValue(site, null)?.ToString();
            }
            catch { return null; }
        }
    }
}
