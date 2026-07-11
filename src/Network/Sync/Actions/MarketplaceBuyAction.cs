using System;
using System.IO;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Client-edit intent: BUY one offer from the AB DLC5 Kaos "The Marketplace"
    /// (<c>UIModuleTheMarketplace.OnChoiceSelected</c>). The offer is identified by VALUE — kind + def/research
    /// guid + price — NOT by list index, because the host's live <c>GeoMarketplace.MarketplaceChoices</c> may have
    /// shifted (regen / another player's buy) since the client's mirror was taken. Wire: <c>u8 kind, string guid,
    /// f32 price</c> (mirrors <see cref="ObjectivesSnapshot.MarketplaceOfferRecord"/>).
    ///
    /// Host apply (<see cref="MarketplaceReflection.TryBuy"/>) finds the matching live offer and runs the SAME
    /// native sequence the UI does — affordability check → <c>Wallet.Take</c> → reward
    /// (<c>GeoEventChoiceOutcome.GenerateFactionReward().Apply</c>) → remove from the list. A stale/absent offer
    /// is a logged no-op (the refreshed list arrives via the #7 mirror). <see cref="IHostOnlyApply"/>: the
    /// authoritative result mirrors back on the wallet echo + Inventory/Research/GeoVehicle channels + the #7
    /// marketplace-offer list; the client never simulates the purchase. Category GeoAbility (geoscape economy,
    /// default FullCommander like the ability relays). Runs INSIDE SyncApplyScope — Apply calls the native reward
    /// path directly (not the patched OnChoiceSelected), so no interceptor loops.
    /// </summary>
    public sealed class MarketplaceBuyAction : ISyncedAction, IHostOnlyApply
    {
        private readonly byte _kind;    // ObjectivesSnapshot.OfferItem / OfferResearch / OfferUnit
        private readonly string _guid;  // offered ItemDef / ResearchDef(id) / TacCharacterDef guid
        private readonly float _price;  // Requirments.Resources[0].Value (Materials)

        public MarketplaceBuyAction(byte kind, string guid, float price)
        {
            _kind = kind;
            _guid = guid;
            _price = price;
        }

        public byte Kind => _kind;
        public string Guid => _guid;
        public float Price => _price;

        public ushort ActionId => SyncedActionIds.MarketplaceBuy;
        public ActionCategory Category => ActionCategory.GeoAbility;

        public void Write(BinaryWriter w)
        {
            w.Write(_kind);
            w.Write(_guid ?? "");
            w.Write(_price);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new MarketplaceBuyAction(r.ReadByte(), r.ReadString(), r.ReadSingle());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_guid) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
        {
            if (!MarketplaceReflection.TryBuy(rt, _kind, _guid, _price)) return;   // stale/absent offer → logged no-op
            // Purchase applied. This runs INSIDE SyncApplyScope, where the reward channels' dirty-mark patches are
            // suppressed — so mark EVERY converging channel explicitly for INSTANT propagation (canon: known paths
            // converge instantly; drift polls are only the backstop): #1 inventory (item rewards), #2 research
            // (research rewards), #6 vehicles (ground-vehicle rewards), #7 offer list (no faction event fires for
            // its mutation). Wallet belt: Take fires ResourcesChanged→echo, but force it in case of a stale
            // binding (CompleteEventPatch idiom). All marks are idempotent (coalesced per flush).
            var sync = NetworkEngine.Instance?.Sync;
            if (sync == null) return;
            sync.MarkChannelDirty(1);
            sync.MarkChannelDirty(2);
            sync.MarkChannelDirty(6);
            sync.MarkChannelDirty(7);
            sync.MarkWalletDirty();
        }
    }
}
