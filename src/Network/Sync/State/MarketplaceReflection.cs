using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the AB DLC5 Kaos "The Marketplace" OFFER LIST, folded into the objectives
    /// channel (#7) — the same channel already mirrors the marketplace's <c>NumberOfDLC5MissionsCompleted</c>
    /// event variable, so carrying the offer list here lands both in ONE atomic snapshot (no tear where the
    /// client's offer-tier variable advances but its offers don't). The frozen client never re-runs the host's
    /// RNG <c>GeoMarketplace.UpdateOptions</c>, so its <c>MarketplaceChoices</c> drift from the host's after
    /// any host regen (mission complete / time tick); this mirrors the host list value-for-value.
    ///
    /// Verified against the decompile (2026-07-06,
    /// Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace.GeoMarketplace):
    ///   • <c>GeoMarketplace</c> is a component on the <c>GeoLevelController</c> (GetComponent, UIModuleTheMarketplace:141).
    ///   • <c>MarketplaceChoices : List&lt;GeoEventChoice&gt;</c> (:31) is the live offer list the trade UI reads.
    ///   • each offer = <c>GeoMarketplaceInstanceData.MarketplaceOption</c> {Offer, Price}, extracted exactly as
    ///     <c>RecordInstanceData</c> (:70): Price = <c>Requirments.Resources[0].Value</c>; Offer =
    ///     <c>Outcome.Items[0].ItemDef</c> / <c>Outcome.Units[0]</c> (TacCharacterDef) / a ResearchDef via
    ///     <c>Outcome.GiveResearches[0]</c> (a research id string).
    ///   • client rebuild replays the native LOAD path (<c>LevelStartLoadedGame</c> :107): the private
    ///     <c>GenerateItemChoice(ItemDef, float)</c> / <c>GenerateResearchChoice(ResearchDef, float)</c> (:300/292)
    ///     build a fresh <c>GeoEventChoice</c> with no side effects — NOT the whole LevelStartLoadedGame (which
    ///     re-subscribes mission objectives and can RNG-regen offers on a client, the very drift we avoid).
    ///
    /// CLIENT apply is VALUE-ONLY + idempotent: it compares the live offer list to the mirrored one and no-ops
    /// when identical (the channel re-emits the whole set every flush + hourly — a rebuild each time would churn
    /// the GeoEventChoice objects the open trade UI holds). Every path is try/caught — best-effort, never throws.
    /// </summary>
    public static class MarketplaceReflection
    {
        private static bool _probed;
        private static Type _marketplaceType;      // GeoMarketplace
        private static MethodInfo _getComponent;   // GeoLevelController.GetComponent(Type)
        private static PropertyInfo _choicesProp;  // GeoMarketplace.MarketplaceChoices (List<GeoEventChoice>)
        private static PropertyInfo _reqProp;      // GeoEventChoice.Requirments
        private static PropertyInfo _outcomeProp;  // GeoEventChoice.Outcome
        private static MemberInfo _resourcesMember; // GeoEventChoiceRequirements.Resources (List<ResourceUnit>)
        private static MemberInfo _resValueMember; // ResourceUnit.Value (float)
        private static MemberInfo _itemsMember;    // GeoEventChoiceOutcome.Items (List<ItemUnit>)
        private static MemberInfo _unitsMember;    // GeoEventChoiceOutcome.Units (List<TacCharacterDef>)
        private static MemberInfo _giveResMember;  // GeoEventChoiceOutcome.GiveResearches (List<string>)
        private static MemberInfo _itemDefMember;  // ItemUnit.ItemDef
        private static PropertyInfo _unitItemDefProp; // TacCharacterDef.ItemDef
        private static MethodInfo _genItemChoice;  // GeoMarketplace.GenerateItemChoice(ItemDef, float)
        private static MethodInfo _genResChoice;   // GeoMarketplace.GenerateResearchChoice(ResearchDef, float)
        private static MethodInfo _getResearchById; // GeoLevelController.GetResearchById(string) → ResearchDef

        private static void Ensure(GeoRuntime rt)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _marketplaceType = AccessTools.TypeByName("Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace.GeoMarketplace");
                var choiceType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
                var itemDefType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemDef");
                var researchDefType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchDef");
                var tacCharType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacCharacterDef");
                var itemUnitType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Items.ItemUnit");
                var geo = rt?.GeoLevel();
                if (_marketplaceType == null || choiceType == null || geo == null) return;

                _getComponent = AccessTools.Method(geo.GetType(), "GetComponent", new[] { typeof(Type) });
                _getResearchById = AccessTools.Method(geo.GetType(), "GetResearchById", new[] { typeof(string) });
                _choicesProp = AccessTools.Property(_marketplaceType, "MarketplaceChoices");
                _reqProp = AccessTools.Property(choiceType, "Requirments");   // sic — game spelling
                _outcomeProp = AccessTools.Property(choiceType, "Outcome");
                if (_reqProp != null)
                {
                    _resourcesMember = Member(_reqProp.PropertyType, "Resources");
                    var resUnitType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit")
                                      ?? AccessTools.TypeByName("ResourceUnit");
                    if (resUnitType != null) _resValueMember = Member(resUnitType, "Value");
                }
                if (_outcomeProp != null)
                {
                    _itemsMember = Member(_outcomeProp.PropertyType, "Items");
                    _unitsMember = Member(_outcomeProp.PropertyType, "Units");
                    _giveResMember = Member(_outcomeProp.PropertyType, "GiveResearches");
                }
                if (itemUnitType != null) _itemDefMember = Member(itemUnitType, "ItemDef");
                if (tacCharType != null) _unitItemDefProp = AccessTools.Property(tacCharType, "ItemDef");
                if (itemDefType != null)
                    _genItemChoice = AccessTools.Method(_marketplaceType, "GenerateItemChoice", new[] { itemDefType, typeof(float) });
                if (researchDefType != null)
                    _genResChoice = AccessTools.Method(_marketplaceType, "GenerateResearchChoice", new[] { researchDefType, typeof(float) });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MarketplaceReflection.Ensure failed: " + ex.Message); }
        }

        private static MemberInfo Member(Type t, string name)
            => (MemberInfo)AccessTools.Property(t, name) ?? AccessTools.Field(t, name);

        private static object ReadMember(MemberInfo m, object target)
        {
            if (m == null || target == null) return null;
            if (m is PropertyInfo p) return p.GetValue(target, null);
            if (m is FieldInfo f) return f.GetValue(target);
            return null;
        }

        /// <summary>The live <c>GeoMarketplace</c> component, or null (no DLC5 / mid-load).</summary>
        private static object GetMarketplace(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null || _getComponent == null || _marketplaceType == null) return null;
            try { return _getComponent.Invoke(geo, new object[] { _marketplaceType }); }
            catch { return null; }
        }

        // ─── host snapshot ────────────────────────────────────────────────────────────────

        /// <summary>Host: append the current The-Marketplace offer list to <paramref name="snap"/> (no-op when
        /// no DLC5 / no offers). Extracts each <c>GeoEventChoice</c> as {kind, guid/id, price}, exactly the
        /// native <c>RecordInstanceData</c> mapping.</summary>
        public static void SnapshotOffers(GeoRuntime rt, ObjectivesSnapshot snap)
        {
            if (snap == null) return;
            try
            {
                Ensure(rt);
                var market = GetMarketplace(rt);
                if (market == null || _choicesProp == null) return;
                if (!(_choicesProp.GetValue(market, null) is IList choices)) return;
                foreach (var choice in choices)
                {
                    if (choice == null) continue;
                    var rec = ReadOffer(choice);
                    if (rec != null) snap.MarketplaceOffers.Add(rec);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MarketplaceReflection.SnapshotOffers failed: " + ex.Message); }
        }

        /// <summary>One live <c>GeoEventChoice</c> → offer record, or null when its offer/price can't be read.</summary>
        private static ObjectivesSnapshot.MarketplaceOfferRecord ReadOffer(object choice)
        {
            try
            {
                float price = 0f;
                var req = _reqProp?.GetValue(choice, null);
                if (ReadMember(_resourcesMember, req) is IList res && res.Count > 0)
                {
                    object v = ReadMember(_resValueMember, res[0]);
                    if (v != null) price = Convert.ToSingle(v);
                }

                var outcome = _outcomeProp?.GetValue(choice, null);
                if (outcome == null) return null;

                // Precedence mirrors RecordInstanceData (research > unit > item for a single-outcome choice).
                if (ReadMember(_giveResMember, outcome) is IList gr && gr.Count > 0 && gr[0] is string researchId
                    && !string.IsNullOrEmpty(researchId))
                    return new ObjectivesSnapshot.MarketplaceOfferRecord
                        { Kind = ObjectivesSnapshot.OfferResearch, OfferGuid = researchId, Price = price };

                if (ReadMember(_unitsMember, outcome) is IList units && units.Count > 0 && units[0] != null)
                {
                    string g = DefReflection.GetGuid(units[0]);
                    if (!string.IsNullOrEmpty(g))
                        return new ObjectivesSnapshot.MarketplaceOfferRecord
                            { Kind = ObjectivesSnapshot.OfferUnit, OfferGuid = g, Price = price };
                }

                if (ReadMember(_itemsMember, outcome) is IList items && items.Count > 0 && items[0] != null)
                {
                    var itemDef = ReadMember(_itemDefMember, items[0]);
                    string g = DefReflection.GetGuid(itemDef);
                    if (!string.IsNullOrEmpty(g))
                        return new ObjectivesSnapshot.MarketplaceOfferRecord
                            { Kind = ObjectivesSnapshot.OfferItem, OfferGuid = g, Price = price };
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] MarketplaceReflection.ReadOffer failed: " + ex.Message); }
            return null;
        }

        // ─── client apply ─────────────────────────────────────────────────────────────────

        /// <summary>Client: rebuild <c>GeoMarketplace.MarketplaceChoices</c> from the mirrored offer list.
        /// Value-only: no-op when the live list already matches (idempotent). An offer whose def/research
        /// can't resolve on this client is skipped (degrade). No-op when no DLC5 / marketplace not ready.</summary>
        public static void ApplyOffers(GeoRuntime rt, ObjectivesSnapshot snap)
        {
            if (snap == null || snap.MarketplaceOffers.Count == 0) return;   // nothing to mirror (also: pre-AB payload)
            try
            {
                Ensure(rt);
                var market = GetMarketplace(rt);
                if (market == null || _choicesProp == null || _genItemChoice == null) return;
                if (!(_choicesProp.GetValue(market, null) is IList choices)) return;

                // Value-only skip: compare the live offer list to the mirrored one (kind + key + price, ordered).
                if (SameOffers(choices, snap.MarketplaceOffers)) return;

                choices.Clear();
                int built = 0, skipped = 0;
                foreach (var o in snap.MarketplaceOffers)
                {
                    object choice = BuildChoice(rt, market, o);
                    if (choice == null) { skipped++; continue; }
                    choices.Add(choice);
                    built++;
                }
                Debug.Log("[Multiplayer] MarketplaceReflection.ApplyOffers rebuilt MarketplaceChoices built="
                          + built + " skipped=" + skipped + " (host offers=" + snap.MarketplaceOffers.Count + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MarketplaceReflection.ApplyOffers failed: " + ex.Message); }
        }

        /// <summary>True when the live choice list already equals the mirrored offer set (kind+key+price, order).</summary>
        private static bool SameOffers(IList liveChoices, List<ObjectivesSnapshot.MarketplaceOfferRecord> target)
        {
            if (liveChoices.Count != target.Count) return false;
            for (int i = 0; i < target.Count; i++)
            {
                var cur = ReadOffer(liveChoices[i]);
                var want = target[i];
                if (cur == null) return false;
                if (cur.Kind != want.Kind
                    || !string.Equals(cur.OfferGuid ?? "", want.OfferGuid ?? "", StringComparison.Ordinal)
                    || cur.Price != want.Price)
                    return false;
            }
            return true;
        }

        /// <summary>Build one <c>GeoEventChoice</c> for an offer via the native private generators, or null
        /// when the offered def/research doesn't resolve on this client.</summary>
        private static object BuildChoice(GeoRuntime rt, object market, ObjectivesSnapshot.MarketplaceOfferRecord o)
        {
            try
            {
                switch (o.Kind)
                {
                    case ObjectivesSnapshot.OfferItem:
                    {
                        var itemDef = DefReflection.GetDefByGuid(o.OfferGuid);
                        if (itemDef == null || _genItemChoice == null) return null;
                        return _genItemChoice.Invoke(market, new object[] { itemDef, o.Price });
                    }
                    case ObjectivesSnapshot.OfferUnit:
                    {
                        var tacDef = DefReflection.GetDefByGuid(o.OfferGuid);
                        var itemDef = tacDef != null ? _unitItemDefProp?.GetValue(tacDef, null) : null;   // GenerateItemChoice(tacCharacterDef.ItemDef, …)
                        if (itemDef == null || _genItemChoice == null) return null;
                        return _genItemChoice.Invoke(market, new object[] { itemDef, o.Price });
                    }
                    case ObjectivesSnapshot.OfferResearch:
                    {
                        var geo = rt?.GeoLevel();
                        var researchDef = geo != null && _getResearchById != null
                            ? _getResearchById.Invoke(geo, new object[] { o.OfferGuid }) : null;
                        if (researchDef == null || _genResChoice == null) return null;
                        return _genResChoice.Invoke(market, new object[] { researchDef, o.Price });
                    }
                    default: return null;   // unknown kind (future host) → skip
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] MarketplaceReflection.BuildChoice failed: " + ex.Message); return null; }
        }
    }
}
