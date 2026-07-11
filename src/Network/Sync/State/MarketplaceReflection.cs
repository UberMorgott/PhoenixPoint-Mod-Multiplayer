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
        private static MemberInfo _resourcesMember; // GeoEventChoiceRequirements.Resources (ResourcePack)
        private static MemberInfo _packValuesMember; // ResourcePack.Values (List<ResourceUnit>)
        private static MemberInfo _resValueMember; // ResourceUnit.Value (float)
        private static MemberInfo _itemsMember;    // GeoEventChoiceOutcome.Items (List<ItemUnit>)
        private static MemberInfo _unitsMember;    // GeoEventChoiceOutcome.Units (List<TacCharacterDef>)
        private static MemberInfo _giveResMember;  // GeoEventChoiceOutcome.GiveResearches (List<string>)
        private static MemberInfo _itemDefMember;  // ItemUnit.ItemDef
        private static PropertyInfo _unitItemDefProp; // TacCharacterDef.ItemDef
        private static MethodInfo _genItemChoice;  // GeoMarketplace.GenerateItemChoice(ItemDef, float)
        private static MethodInfo _genResChoice;   // GeoMarketplace.GenerateResearchChoice(ResearchDef, float)
        private static MethodInfo _getResearchById; // GeoLevelController.GetResearchById(string) → ResearchDef

        // ─── host buy + client UI refresh (lazily probed via EnsureBuy) ───
        private static bool _buyProbed;
        private static MethodInfo _walletHasResources; // Wallet.HasResources(ResourcePack) → bool
        private static MethodInfo _walletTake;         // Wallet.Take(ResourcePack, OperationReason) → bool
        private static object _purchaseReason;         // OperationReason.Purchase
        private static object _marketplaceSiteTypeValue; // GeoSiteType.Marketplace
        private static ConstructorInfo _ctxCtor;       // new GeoscapeEventContext(GeoSite, GeoFaction, GeoVehicle)
        private static FieldInfo _ctxSiteField;         // GeoscapeEventContext.Site
        private static FieldInfo _ctxVehicleField;      // GeoscapeEventContext.Vehicle
        private static MethodInfo _genReward;          // GeoEventChoiceOutcome.GenerateFactionReward(GeoFaction, ctx, string)
        private static MemberInfo _reEnableMember;      // GeoEventChoiceOutcome.ReEneableEvent (bool)
        private static MethodInfo _rewardApply;        // GeoFactionReward.Apply(GeoFaction, GeoSite, GeoVehicle)
        private static MemberInfo _mapMember;           // GeoLevelController.Map
        private static MemberInfo _eventSystemMember;   // GeoLevelController.EventSystem
        private static MemberInfo _viewMember;          // GeoLevelController.View
        private static PropertyInfo _activeSitesProp;   // GeoMap.ActiveSites (IList<GeoSite>)
        private static PropertyInfo _siteTypeProp;      // GeoSite.Type (GeoSiteType)
        private static MemberInfo _siteVehiclesMember;  // GeoSite.Vehicles (IEnumerable<GeoVehicle>)
        private static MemberInfo _mpSettingsMember;    // GeoLevelController.TheMarketplaceSettings (field :175)
        private static MemberInfo _mpEventMember;       // TheMarketplaceSettingsDef.MarketplaceEvent (GeoscapeEventDef, :87)
        private static MemberInfo _defEventIdMember;    // GeoscapeEventDef.EventID (string, :81)
        private static MethodInfo _enableEvent;        // GeoscapeEventSystem.EnableGeoscapeEvent(string)
        private static MemberInfo _modulesMember;       // GeoscapeView.GeoscapeModules
        private static MemberInfo _marketModuleMember;  // GeoscapeModulesData.TheMarketplaceModule
        private static FieldInfo _moduleGeoEventField;  // UIModuleTheMarketplace._geoEvent
        private static MethodInfo _moduleUpdateList;   // UIModuleTheMarketplace.UpdateList(GeoscapeEvent)

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
                    // ResourcePack is IEnumerable<ResourceUnit> only (NOT IList) — its indexable list is the
                    // public `List<ResourceUnit> Values` field (ResourcePack.cs:18); ReadOffer unwraps it.
                    var resPackType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
                    if (resPackType != null) _packValuesMember = Member(resPackType, "Values");
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
                // Resources is a ResourcePack (IEnumerable<ResourceUnit>, NOT IList) — unwrap its Values list.
                var pack = ReadMember(_resourcesMember, req);
                if ((pack as IList ?? ReadMember(_packValuesMember, pack)) is IList res && res.Count > 0)
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
                // Reactive mirror: the offer list changed (not SameOffers) — repaint an OPEN marketplace window
                // now (native UpdateList), instead of waiting for the next open. Guarded no-op when UI is closed.
                RefreshOpenUI(rt);
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

        /// <summary>Client: read the clicked <c>GeoEventChoice</c>'s identity {kind, guid, price} for the buy
        /// intent — the SAME <see cref="ReadOffer"/> mapping the host matches on, so client-sent == host-matched.
        /// False when the offer can't be read (no relay → the frozen client must not simulate it locally).</summary>
        public static bool TryReadOffer(GeoRuntime rt, object choice, out byte kind, out string guid, out float price)
        {
            kind = 0; guid = null; price = 0f;
            try
            {
                Ensure(rt);
                var rec = ReadOffer(choice);
                if (rec == null) return false;
                kind = rec.Kind; guid = rec.OfferGuid; price = rec.Price;
                return !string.IsNullOrEmpty(guid);
            }
            catch { return false; }
        }

        // ─── host buy ─────────────────────────────────────────────────────────────────────

        /// <summary>Host: apply a client's marketplace BUY. Finds the live offer matching {kind, guid, price}
        /// (index-independent — the host list may have shifted) and runs the SAME native sequence
        /// <c>UIModuleTheMarketplace.OnChoiceSelected</c> does: affordability check → <c>Wallet.Take</c> →
        /// reward (<c>GenerateFactionReward().Apply</c>, exactly <c>GeoscapeEvent.CompleteMarketplaceEvent</c>,
        /// UI-free) → remove from <c>MarketplaceChoices</c>. Returns true when applied; a stale/absent/unaffordable
        /// offer is a logged no-op (false) — the client's list refreshes via the #7 mirror. Best-effort, never throws.</summary>
        public static bool TryBuy(GeoRuntime rt, byte kind, string guid, float price)
        {
            try
            {
                Ensure(rt);
                EnsureBuy(rt);
                var market = GetMarketplace(rt);
                if (market == null || _choicesProp == null) return false;
                if (!(_choicesProp.GetValue(market, null) is IList choices)) return false;

                // Match by VALUE (kind + guid + price) — a bare index would target the wrong offer after any shift.
                object match = null;
                foreach (var choice in choices)
                {
                    if (choice == null) continue;
                    var rec = ReadOffer(choice);
                    if (rec != null && rec.Kind == kind
                        && string.Equals(rec.OfferGuid ?? "", guid ?? "", StringComparison.Ordinal)
                        && rec.Price == price)
                    { match = choice; break; }
                }
                if (match == null)
                {
                    Debug.Log("[Multiplayer] MarketplaceReflection.TryBuy no matching offer (kind=" + kind
                              + " guid=" + guid + " price=" + price + ") — stale client list, #7 mirror will refresh it");
                    return false;
                }

                var faction = rt?.PhoenixFaction();
                var wallet = rt?.Wallet();
                if (faction == null || wallet == null) return false;

                // Affordability: marketplace choices carry ONLY a Resources cost, so Wallet.HasResources is the full
                // requirements check (no diplomacy/research). Wallet.Take clamps to available + always returns true,
                // so this pre-check is what actually rejects an unaffordable buy on the authoritative host wallet.
                var req = _reqProp?.GetValue(match, null);
                var resources = ReadMember(_resourcesMember, req);
                if (resources != null && _walletHasResources != null
                    && !(bool)_walletHasResources.Invoke(wallet, new[] { resources }))
                {
                    Debug.Log("[Multiplayer] MarketplaceReflection.TryBuy rejected — insufficient funds (price=" + price + ")");
                    return false;
                }
                if (resources != null && _walletTake != null && _purchaseReason != null)
                    _walletTake.Invoke(wallet, new[] { resources, _purchaseReason });

                ApplyReward(rt, faction, match);
                choices.Remove(match);
                Debug.Log("[Multiplayer] MarketplaceReflection.TryBuy applied (kind=" + kind + " guid=" + guid + " price=" + price + ")");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MarketplaceReflection.TryBuy failed: " + ex.Message); return false; }
        }

        /// <summary>Replicate <c>GeoscapeEvent.CompleteMarketplaceEvent</c> (all public game calls, no UI/event
        /// instance): <c>GenerateFactionReward(faction, ctx, eventId).Apply(faction, ctx.Site, ctx.Vehicle)</c>,
        /// then re-enable the encounter (<c>ReEneableEvent</c> is always true for a marketplace choice).</summary>
        private static void ApplyReward(GeoRuntime rt, object faction, object choice)
        {
            var geo = rt?.GeoLevel();
            var outcome = _outcomeProp?.GetValue(choice, null);
            if (geo == null || outcome == null || _genReward == null || _ctxCtor == null || _rewardApply == null) return;

            var mpSite = FindMarketplaceSite(geo);
            object vehicle = mpSite != null ? FirstVehicle(mpSite) : null;
            object startingBase = AccessTools.Property(faction.GetType(), "StartingBase")?.GetValue(faction, null);
            // The live marketplace GeoscapeEvent's EventID — grounded: GeoscapeEventSystem.IsEventTheMarketplace
            // (GeoscapeEventSystem.cs:407-410) defines it as TheMarketplaceSettings.MarketplaceEvent.EventID; the
            // native CompleteMarketplaceEvent's ReEneableEvent re-enable uses exactly this id (NOT the site's
            // EncounterID field, which is not guaranteed to match).
            string eventId = ReadMember(_defEventIdMember,
                ReadMember(_mpEventMember, ReadMember(_mpSettingsMember, geo))) as string;

            object ctx = _ctxCtor.Invoke(new[] { startingBase, faction, vehicle });   // (StartingBase, PhoenixFaction, marketplace vehicle)
            object reward = _genReward.Invoke(outcome, new[] { faction, ctx, (object)(eventId ?? "") });
            if (reward == null) return;
            _rewardApply.Invoke(reward, new[] { faction, _ctxSiteField?.GetValue(ctx), _ctxVehicleField?.GetValue(ctx) });

            bool reEnable = _reEnableMember != null && ReadMember(_reEnableMember, outcome) is bool b && b;
            if (reEnable && _enableEvent != null && _eventSystemMember != null && !string.IsNullOrEmpty(eventId))
            {
                var es = ReadMember(_eventSystemMember, geo);
                if (es != null) _enableEvent.Invoke(es, new object[] { eventId });
            }
        }

        /// <summary>The live marketplace <c>GeoSite</c> (Type == Marketplace), or null (GeoMarketplace.OnLevelStart idiom).</summary>
        private static object FindMarketplaceSite(object geo)
        {
            if (_mapMember == null || _activeSitesProp == null || _siteTypeProp == null || _marketplaceSiteTypeValue == null) return null;
            var map = ReadMember(_mapMember, geo);
            if (map == null || !(_activeSitesProp.GetValue(map, null) is IList sites)) return null;
            foreach (var site in sites)
            {
                if (site == null) continue;
                if (Equals(_siteTypeProp.GetValue(site, null), _marketplaceSiteTypeValue)) return site;
            }
            return null;
        }

        /// <summary>First vehicle at the site (SingleOrDefault-equivalent — a marketplace site has 0-1 player vehicle), or null.</summary>
        private static object FirstVehicle(object site)
        {
            if (_siteVehiclesMember == null) return null;
            if (ReadMember(_siteVehiclesMember, site) is IEnumerable ve)
                foreach (var v in ve) return v;
            return null;
        }

        // ─── client UI refresh ──────────────────────────────────────────────────────────────

        /// <summary>Client: if the marketplace window is OPEN, repaint its offer buttons via the native private
        /// <c>UpdateList(_geoEvent)</c> — the exact call <c>OnChoiceSelected</c> makes after mutating the list.
        /// Guarded no-op when the module is null / closed / not yet shown. Best-effort, never throws.</summary>
        public static void RefreshOpenUI(GeoRuntime rt)
        {
            try
            {
                EnsureBuy(rt);
                var geo = rt?.GeoLevel();
                if (geo == null || _viewMember == null || _modulesMember == null || _marketModuleMember == null) return;
                var view = ReadMember(_viewMember, geo);
                var modules = view != null ? ReadMember(_modulesMember, view) : null;
                var module = modules != null ? ReadMember(_marketModuleMember, modules) : null;
                if (!(module is Component comp) || !comp.gameObject.activeInHierarchy) return;   // marketplace UI not open
                var geoEvent = _moduleGeoEventField?.GetValue(module);
                if (geoEvent == null || _moduleUpdateList == null) return;
                _moduleUpdateList.Invoke(module, new[] { geoEvent });
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] MarketplaceReflection.RefreshOpenUI failed: " + ex.Message); }
        }

        private static void EnsureBuy(GeoRuntime rt)
        {
            if (_buyProbed) return;
            _buyProbed = true;
            try
            {
                var geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                var geoFactionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
                var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                var geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
                var ctxType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventContext");
                var walletType = AccessTools.TypeByName("PhoenixPoint.Common.Core.Wallet");
                var resPackType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
                var opReasonType = AccessTools.TypeByName("PhoenixPoint.Common.Core.OperationReason");
                var rewardType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Core.GeoFactionReward");
                var siteTypeEnum = AccessTools.TypeByName("PhoenixPoint.Common.Core.GeoSiteType");
                var mapType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoMap");
                var eventSystemType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventSystem");
                var moduleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTheMarketplace");
                var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
                var modulesType = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
                var geoEventType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");

                if (walletType != null && resPackType != null)
                {
                    _walletHasResources = AccessTools.Method(walletType, "HasResources", new[] { resPackType });
                    if (opReasonType != null)
                        _walletTake = AccessTools.Method(walletType, "Take", new[] { resPackType, opReasonType });
                }
                if (opReasonType != null) try { _purchaseReason = Enum.Parse(opReasonType, "Purchase"); } catch { }
                if (siteTypeEnum != null) try { _marketplaceSiteTypeValue = Enum.Parse(siteTypeEnum, "Marketplace"); } catch { }

                if (ctxType != null && geoSiteType != null && geoFactionType != null && geoVehicleType != null)
                {
                    _ctxCtor = AccessTools.Constructor(ctxType, new[] { geoSiteType, geoFactionType, geoVehicleType });
                    _ctxSiteField = AccessTools.Field(ctxType, "Site");
                    _ctxVehicleField = AccessTools.Field(ctxType, "Vehicle");
                }
                if (_outcomeProp != null && geoFactionType != null && ctxType != null)
                {
                    _genReward = AccessTools.Method(_outcomeProp.PropertyType, "GenerateFactionReward",
                        new[] { geoFactionType, ctxType, typeof(string) });
                    _reEnableMember = Member(_outcomeProp.PropertyType, "ReEneableEvent");
                }
                if (rewardType != null && geoFactionType != null && geoSiteType != null && geoVehicleType != null)
                    _rewardApply = AccessTools.Method(rewardType, "Apply", new[] { geoFactionType, geoSiteType, geoVehicleType });

                if (geoLevelType != null)
                {
                    _mapMember = Member(geoLevelType, "Map");
                    _eventSystemMember = Member(geoLevelType, "EventSystem");
                    _viewMember = Member(geoLevelType, "View");
                }
                if (mapType != null) _activeSitesProp = AccessTools.Property(mapType, "ActiveSites");
                if (geoSiteType != null)
                {
                    _siteTypeProp = AccessTools.Property(geoSiteType, "Type");
                    _siteVehiclesMember = Member(geoSiteType, "Vehicles");
                }
                var mpSettingsType = AccessTools.TypeByName("PhoenixPoint.Common.Core.TheMarketplaceSettingsDef");
                var geoEventDefType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventDef");
                if (geoLevelType != null) _mpSettingsMember = Member(geoLevelType, "TheMarketplaceSettings");
                if (mpSettingsType != null) _mpEventMember = Member(mpSettingsType, "MarketplaceEvent");
                if (geoEventDefType != null) _defEventIdMember = Member(geoEventDefType, "EventID");
                if (eventSystemType != null) _enableEvent = AccessTools.Method(eventSystemType, "EnableGeoscapeEvent", new[] { typeof(string) });
                if (viewType != null) _modulesMember = Member(viewType, "GeoscapeModules");
                if (modulesType != null) _marketModuleMember = Member(modulesType, "TheMarketplaceModule");
                if (moduleType != null)
                {
                    _moduleGeoEventField = AccessTools.Field(moduleType, "_geoEvent");
                    if (geoEventType != null) _moduleUpdateList = AccessTools.Method(moduleType, "UpdateList", new[] { geoEventType });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MarketplaceReflection.EnsureBuy failed: " + ex.Message); }
        }
    }
}
