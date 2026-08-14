using System;
using System.IO;
using System.Linq;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// HAVEN RESOURCE TRADE — the SECOND rider of the shared-offer family (surface 0xBE, op 2; the Kaos
    /// marketplace was the first, <see cref="MarketplaceSync"/>).
    ///
    /// ─── THE FAMILY, not the panel ───
    /// A SHARED-OFFER panel is any screen where a finite stock the peers SHARE is consumed by one peer's
    /// commit: the marketplace assortment, a haven's tradeable resources, a haven's recruit, the base
    /// Recruits tab. They are one family because they share one failure shape and one fix:
    ///   • the stock is HOST-AUTHORITATIVE — a client that commits locally mints shared state (law 3);
    ///   • the commit is captured at the MODEL funnel, never at a screen (a screen covers the one caller
    ///     it draws — <see cref="ReplenishSync"/>'s OK button is the standing proof, law L110);
    ///   • the consumption replicates as ORDINARY state, so every peer's open panel repaints on arrival
    ///     through the universal repaint (law 11) — never lazily on next open;
    ///   • a commit that LOSES the race is refused OUT LOUD, never a silent no-op and never a phantom
    ///     purchase that exists only on the clicking peer's screen.
    /// Joining the family is a DECLARATION, not new plumbing: name the funnel, name the panel, and
    /// RailCheck L111 holds the wiring to it. What is deliberately NOT abstracted is the ADDRESS — no
    /// framework can guess what names a row of a shop, a resource pair or a recruit, and inventing one
    /// would be the macaroni factory law 12 forbids.
    ///
    /// ─── WHY TRADE WAS THE HOLE ───
    /// The haven's stock IS already on the value rail (<c>RailMeta</c>:959 bridges
    /// <c>GeoHaven.OfferedResources</c> → the live <c>StockedResources</c>), and the wallet has always been
    /// the "F#" root. So the HOST→client direction already worked. The CLIENT→host direction did not exist
    /// at all: <c>GeoHaven.TradeResource</c>:715 was unpatched, so a client's Trade button ran the whole
    /// exchange against its OWN mirror — haven stock down, SHARED wallet applied — while the host's state
    /// never moved. Nothing logged it, and the next diff silently reverted the trade the player watched
    /// happen. Two peers in the same trade screen saw two different havens.
    ///
    /// ─── THE SEAM IS THE MODEL FUNNEL, AND IT HAS TWO CALLERS ───
    /// <c>GeoHaven.TradeResource</c> is the only place the exchange happens, and BOTH gestures reach it:
    /// the full trade screen (<c>UIStateTrade.ConfirmTrade</c>:67, with the slider's
    /// <c>NumberOrTrades</c>) and the haven-details quick trade
    /// (<c>HavenInteractionController</c>:210, which takes the <c>offerAmount = 1</c> default). Capturing
    /// the screen would have covered one of the two and left the other leaking — L110's bug exactly.
    ///
    /// ─── REACTIVITY ───
    /// Nothing bespoke: <c>UIStateTrade</c> is a plain pushed state (<c>GeoscapeView</c>:742
    /// <c>SwitchToState(..., PushOnTop)</c>), NOT a queued one-shot window, so the universal repaint's own
    /// re-enter covers it — the haven's stock and the wallet arriving on the value rail mark the screen
    /// dirty and <c>UIStateTrade.EnterState</c>:39 re-Inits the module from
    /// <c>GetResourceTradingData()</c>, which is the same reseed the game's own <c>ConfirmTrade</c>:78 does
    /// after a trade. It needs no <c>UiNativeRepaint.Table</c> entry, and it must not have one for its own
    /// sake: <c>ExitState</c> only Deinits and unsubscribes — it writes NOTHING back into the model — so
    /// the Exit+Enter fallback cannot eat a delta here.
    /// </summary>
    internal static class TradeSync
    {
        /// <summary>Op 2 of the shared-offer family. Body:
        /// [siteId:i32][havenOffers:u8][havenWants:u8][amount:i32]. The client names the HAVEN and the
        /// RESOURCE PAIR — never quantities, never a price: every number is re-derived from the host's own
        /// offer table, so a stale screen cannot buy at a stale rate.</summary>
        internal const byte OpTrade = 2;

        /// <summary>CLIENT: block and address. HOST/solo: run the game's own exchange.</summary>
        [HarmonyPatch(typeof(GeoHaven), nameof(GeoHaven.TradeResource))]
        internal static class TradeCapturePatch
        {
            private static bool Prefix(GeoHaven __instance, HavenTradingEntry offer, int offerAmount)
            {
                if (IntentRail.ShouldRunNative()) return true;

                int siteId = __instance?.Site?.SiteId ?? -1;
                if (siteId < 0)
                {
                    // Never silent, and never a local fallback: a haven this peer cannot address is a
                    // haven the host cannot re-derive, and trading here would move the SHARED wallet.
                    MpLog.LogWarning("[MP][trade] client trade DROPPED — the haven has no site id, so nothing " +
                                     "can name it on the wire; nothing was exchanged locally either");
                    return false;
                }
                IntentRail.Send(SurfaceIds.GeoMarketplaceIntent, OpTrade,
                                "trade S#" + siteId + " " + offer.HavenOffers + "->" + offer.HavenWants +
                                " x" + offerAmount,
                                w =>
                                {
                                    w.Write(siteId);
                                    w.Write((byte)offer.HavenOffers);
                                    w.Write((byte)offer.HavenWants);
                                    w.Write(offerAmount);
                                });
                return false;
            }
        }

        /// <summary>HOST: re-derive the whole trade from HOST state and run the game's own
        /// <c>TradeResource</c>. The client named a haven and a pair; the ratio, the stock and the
        /// affordability all come from here.</summary>
        internal static void HandleTrade(ulong peer, uint nonce, BinaryReader r)
        {
            int siteId = r.ReadInt32();
            var offers = (ResourceType)r.ReadByte();
            var wants = (ResourceType)r.ReadByte();
            int amount = r.ReadInt32();

            var geo = GenericApplier.GeoLevel();
            var site = geo?.Map?.AllSites?.FirstOrDefault(s => s != null && s.SiteId == siteId);
            var haven = site == null ? null : site.GetComponent<GeoHaven>();
            var faction = geo?.PhoenixFaction;
            if (haven == null || faction == null)
            { Reject(peer, siteId, "no haven at site " + siteId + " on the host"); return; }
            if (amount < 1)
            { Reject(peer, siteId, "an exchange of " + amount + " is not a trade"); return; }

            // The HOST's own offer table — the same GetResourceTrading():608 the trade screen is built
            // from. A pair that is not in it is a pair this haven stopped trading (its zone output fell to
            // zero, or the stock ran dry) since the click left the client.
            var trading = haven.GetResourceTrading();
            int idx = trading == null ? -1 : trading.FindIndex(e => e.HavenOffers == offers && e.HavenWants == wants);
            if (idx < 0)
            { Reject(peer, siteId, "this haven no longer trades " + offers + " for " + wants); return; }

            var entry = trading[idx];
            int gives = entry.HavenOfferQuantity * amount;   // what the haven hands over
            int costs = entry.HavenReceiveQuantity * amount; // what the shared wallet pays
            // THE RACE, refused out loud: another peer emptied this stock while the click was in flight.
            if (entry.ResourceStock < gives)
            { Reject(peer, siteId, "the haven has " + entry.ResourceStock + " " + offers + " left, not the " +
                                   gives + " that were on screen — another peer traded first"); return; }
            // The GAME'S own affordability test on the SHARED wallet.
            if (!faction.Wallet.HasResources(new ResourceUnit(wants, costs)))
            { Reject(peer, siteId, "the shared wallet cannot pay " + costs + " " + wants + " — it was spent " +
                                   "elsewhere while this trade was in flight"); return; }

            haven.TradeResource(faction, entry, amount);
            MpLog.Log("[MP][trade] HOST traded " + gives + " " + offers + " for " + costs + " " + wants +
                      " at S#" + siteId + " on behalf of peer=" + peer + " nonce=" + nonce);
            // The haven's stock and the wallet ride the value rail to every peer; the host applies no
            // delta to itself, so its OWN open trade screen is marked here (law 11).
            OpenUiRepaint.MarkDirty();
        }

        /// <summary>A refused trade changed nothing on the host, so the value rail has nothing to ship —
        /// re-emit the faction root (the wallet the client may have painted as spent) and the haven's own
        /// site root (the stock it may have painted as consumed).</summary>
        private static void Reject(ulong peer, int siteId, string why) =>
            IntentRail.Reject(SurfaceIds.GeoMarketplaceIntent, peer, "trade: " + why, "F#", "S#" + siteId);
    }
}
