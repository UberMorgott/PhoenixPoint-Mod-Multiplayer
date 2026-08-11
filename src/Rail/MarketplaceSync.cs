using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Tactical.Entities;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE KAOS MARKETPLACE (DLC5) — the shop worked on the HOST ONLY (live 3-instance test 2026-08-04):
    /// a client saw an assortment but every click did nothing at all. Two independent gaps, one file,
    /// because they are two halves of one shop: the OFFERS (0xBF, host→all) and the PURCHASE (0xBE,
    /// client→host intent).
    ///
    /// ─── GAP 1: THE ASSORTMENT WAS NEVER REPLICATED, AND EVERY PEER ROLLED ITS OWN ───
    /// <c>GeoMarketplace</c> IS a rail root ("MK", IdentityResolver.cs:251), but its bridge covers 2 of 4
    /// fields — <c>docs/rail-baseline.txt</c>:14-18 records <c>MarketplaceOptions</c> and
    /// <c>UpdateOptionsNextUpdate</c> as EXCLUDED "bridge-unresolved", because the bridge record
    /// (<c>GeoMarketplaceInstanceData</c>, produced by <c>RecordInstanceData</c>:70) names members the live
    /// class does not have: the offers live in <c>MarketplaceChoices</c> (a <c>List&lt;GeoEventChoice&gt;</c>
    /// of a completely different shape) and the timer in the private <c>_updateOptionsNextTime</c>. So the
    /// value rail carries the shop's mission flags and NOT ONE OFFER.
    ///
    /// The offers are ROLLED LOCALLY, per peer: <c>UpdateOptions</c>:218 is three <c>Random.Range</c> calls
    /// (:223 how many, :242 which option, :243 what price). At JOIN the two lists still agree — the client's
    /// geoscape is the host's save and <c>LevelStartLoadedGame</c>:107 rebuilds them from the shipped
    /// <c>InstanceData</c> (law 1) — and then they diverge on the first reroll, because
    /// <c>GeoLevelController</c>:865 drives <c>UpdateOptions(Timing)</c> on EVERY peer's own sim clock and
    /// :756 rerolls again after every mission. "I do not know whether that assortment is even the same
    /// across windows" — after the first reroll it is not.
    ///
    /// THE FIX IS THE ONE RULE THE REST OF THE RAIL ALREADY USES: the host owns the roll, the client mirrors
    /// it. The client's <c>UpdateOptions</c> is BLOCKED (a client that rolls its own shop is minting shared
    /// state, law 3) and the host BROADCASTS the list it rolled. What crosses is the ADDRESS of each offer —
    /// a def key and a price — never a <c>GeoEventChoice</c>: the client rebuilds each row through the
    /// game's OWN <c>GenerateItemChoice</c>/<c>GenerateResearchChoice</c> (:292/:300, the same two the load
    /// path calls at :126/:132/:138), so the rows are the game's, in this peer's own locale, off this peer's
    /// own defs (law 2).
    ///
    /// ─── GAP 2: A CLIENT'S CLICK WAS BLOCKED AND NOTHING WAS SENT ───
    /// <c>EventPopup.MarketplaceChoiceClientLock</c> refused the purchase outright, and said why: with no
    /// replicated offer list, "buy offer N" indexes into a list the host does not share. Gap 1 removes that
    /// premise, so the refusal becomes an INTENT. It is captured one layer ABOVE that lock, at
    /// <c>TheMarketplaceChoicesController.OnButtonChoiceSelected</c>:53 — THE single funnel every row click
    /// reaches (the button's own <c>ChoiceSelected</c> event, and the controller-pad path
    /// <c>SelectFirstChoiceOption</c>:92 which invokes the same <c>onClick</c>) — so the intent is sent
    /// before the module's <c>OnChoiceSelected</c>:210 can touch anything, and that lock stays as an
    /// unreachable belt rather than a second decision.
    ///
    /// BLOCK-FIRST, and the money is why: <c>UIModuleTheMarketplace.OnChoiceSelected</c> takes the price out
    /// of the SHARED wallet at :215, two lines BEFORE the funnel that grants the goods (:219) — the same
    /// shape as the repair leak <see cref="ReplenishSync"/> closed today. The wire carries only
    /// (event id, offer key — GAP 3 took the row index off); the HOST re-derives the price from its own row,
    /// asks the GAME'S own
    /// requirement test (<c>GeoEventChoice.PassRequirements</c>, the very predicate the native button uses
    /// to grey itself out at SiteBaseChoicesController:60) and then runs the game's own three lines. ONE op
    /// for every kind of goods — the marketplace sells items, vehicles and researches through one
    /// <c>GeoEventChoice</c>, so an op per kind would be a macaroni factory for a distinction the game
    /// itself does not make.
    ///
    /// REACTIVITY: the purchase changes the shop for EVERYONE, so both the host's own click and an applied
    /// client intent re-broadcast the list, and every peer with the window open repaints it through the
    /// module's own <c>SetEncounter</c>. It cannot ride the universal repaint: the marketplace is a QUEUED
    /// window, and <c>OpenUiRepaint</c> refuses to Exit+Enter a queued one-shot presentation (it would
    /// replay it) unless the screen declares a <c>UiNativeRepaint.Table</c> entry — which is a line in a file
    /// this track does not own, so the repaint is driven from the applier here instead.
    ///
    /// ─── GAP 3 (2026-08-07): THE ROW INDEX WAS THE WIRE IDENTITY, AND A ROW INDEX IS NOT AN IDENTITY ───
    /// Live report: a client bought a soldier, every other row vanished, the buttons went dead, and only
    /// re-entering the shop recovered it — and the same offer could be clicked again after the host had
    /// already sold it. The intent used to carry <c>[eventId][index:i32][offerKey]</c> and the host resolved
    /// <c>choices[index]</c>, then merely CHECKED the key there. Three ways that is not an address:
    ///   • <see cref="ApplyOffers"/> DROPS rows it cannot rebuild (a def parity gap, law 10) — its own
    ///     comment claimed "buying by row index stays correct only because the host re-checks the offer key",
    ///     which is false: every row after the dropped one is shifted, so the key check does not correct the
    ///     address, it merely REFUSES a purchase the player legitimately made. Live:
    ///     <c>buy: row 10 is outside the host's 4-offer shop</c>.
    ///   • The click's index came from <c>MarketplaceChoices.IndexOf(selectedChoice)</c>, and
    ///     <see cref="ApplyOffers"/> replaces every element with a freshly generated one. A button that
    ///     out-lived one push therefore resolved to −1 and the click was DROPPED with a log line and no word
    ///     to the player — the literal "buttons did nothing".
    ///   • A refusal was invisible: the client's list was never corrected, so a refused click left the same
    ///     rows on screen and the player had no way to tell a dead button from a slow one. The answer is
    ///     the PANEL, not a popup — <see cref="Reject"/>'s reconverge re-pushes the host's real list and the
    ///     clicked row disappears. Losing a race for a shared offer is the ordinary outcome of an optimistic
    ///     click here, and a modal per ordinary refusal is the error-box storm L123 exists to stop.
    /// NOW: the click sends the offer KEY and nothing positional, and the host RESOLVES that key over its own
    /// live list (<see cref="ResolveOffer"/>). A consumed offer resolves to nothing on every peer the moment
    /// the host removes it — that is the outcome L171 asserts, in both directions. Still no price on the
    /// wire (L99 arm d): where a key is ambiguous the host takes its OWN CHEAPEST matching row, so the buyer
    /// can never be charged more than the row it clicked and the host derives the cost either way.
    /// </summary>
    internal static class MarketplaceSync
    {
        /// <summary>The ONE purchase op — item, vehicle or research, all of them.
        /// Body: [eventId:string][offerKey:string]. No price: the client names WHAT it wants, never what it
        /// costs. NO ROW INDEX EITHER, see <see cref="ResolveOffer"/>.</summary>
        internal const byte OpBuy = 1;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>Teardown only, same contract as <see cref="CutsceneMirror.Reset"/>: the seq stream is
        /// per-session on both sides, and a stale host counter would make the next session's first offer
        /// list look older than the dead session's last one.</summary>
        internal static void Reset() { Seq.Reset(); ClearHeldOffers(); } // a held list belongs to the dead session's seq stream

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoMarketplaceIntent, "marketplace",
                new Dictionary<byte, IntentRail.OpHandler>
                {
                    [OpBuy] = (engine, peer, nonce, op, r) => HandleBuy(peer, nonce, r),
                    // 0xBE is the SHARED-OFFER family's surface, not the marketplace's alone — the
                    // geoscape band 0xA0-0xBF is fully allocated (0xBF was the last), and a second
                    // shared-stock panel is an OP, never a new surface. See TradeSync's class doc.
                    [TradeSync.OpTrade] = (engine, peer, nonce, op, r) => TradeSync.HandleTrade(peer, nonce, r),
                },
                // Family reconverge: a rejected purchase leaves the gesturing client looking at a shop it
                // thinks it just changed. The offer list does not ride the value rail, so the generic
                // subtree re-emit cannot carry it — re-push the host's own list.
                reconverge: () => HostBroadcastOffers("reject reconverge"));
        }

        private static GeoMarketplace Market() => GenericApplier.GeoLevel()?.Marketplace;

        // ─── The game's own private funnels, borrowed rather than re-implemented ───

        /// <summary>The two row generators (<c>GeoMarketplace</c>:300/:292) and the module's own list
        /// rebuild (<c>UIModuleTheMarketplace</c>:188). All private, all NATIVE: a mirrored row is built by
        /// the same code that builds a host row, so price rounding, vehicle unwrapping and the display text
        /// are the game's, not a second copy of them here.</summary>
        private static readonly MethodInfo GenerateItemChoice =
            AccessTools.Method(typeof(GeoMarketplace), "GenerateItemChoice", new[] { typeof(ItemDef), typeof(float) });
        private static readonly MethodInfo GenerateResearchChoice =
            AccessTools.Method(typeof(GeoMarketplace), "GenerateResearchChoice", new[] { typeof(ResearchDef), typeof(float) });
        private static readonly FieldInfo ModuleGeoEvent =
            AccessTools.Field(typeof(UIModuleTheMarketplace), "_geoEvent");
        private static readonly MethodInfo ModuleSetEncounter =
            AccessTools.Method(typeof(UIModuleTheMarketplace), "SetEncounter", new[] { typeof(GeoscapeEvent) });

        // ─── THE OFFER KEY: one address for every kind of goods ───

        /// <summary>The peer-stable address of what a row SELLS, derived from the outcome the game already
        /// built — the same three-way test <c>RecordInstanceData</c>:90-101 and <c>LevelStartLoadedGame</c>
        /// :119-139 use, so a key can name exactly what the save can name. Def GUIDs for items and vehicle
        /// templates (law 2), the research ID for a research because that is what <c>GiveResearches</c>
        /// holds and what <c>GetResearchById</c> takes. Null = an outcome shape this shop has never
        /// produced, which is a row no peer can address.</summary>
        internal static string OfferKey(GeoEventChoice choice)
        {
            var outcome = choice?.Outcome;
            if (outcome == null) return null;
            if (outcome.Items.Count > 0) return "I:" + outcome.Items[0].ItemDef?.Guid;
            if (outcome.GiveResearches.Count > 0) return "R:" + outcome.GiveResearches[0];
            if (outcome.Units.Count > 0) return "U:" + outcome.Units[0]?.Guid;
            return null;
        }

        /// <summary>THE ADDRESS RESOLVER — a key names a ROW, never a position. Pure and total: no live
        /// level, no reflection, so RailCheck L171 executes the real thing instead of a copy of it.
        ///
        /// The OUTCOME it exists for: a consumed offer resolves to NOTHING. The host removes the row it
        /// sold, and from that instant no peer — not even the one whose screen still draws it — can address
        /// it, whatever moved into its old slot. That is the half a row index cannot express: an index
        /// survives the row it pointed at and silently starts naming its neighbour.
        ///
        /// CHEAPEST FIRST when a key is ambiguous. <c>OfferKey</c> is def-level, and the shop is free to roll
        /// the same <c>ItemDef</c> twice (<c>GeoMarketplace.GenerateRandomChoice</c>:272 only retires an
        /// option when <c>DisallowDuplicates</c>), so two rows can share a key at different prices. Taking
        /// the cheapest is deterministic AND it is the arm that keeps the price off the wire (L99 arm d):
        /// the goods are identical, the host derives the cost from the row it picked, and the buyer can
        /// never be charged more than the row it actually clicked.</summary>
        internal static GeoEventChoice ResolveOffer(IList<GeoEventChoice> choices, string key)
        {
            if (choices == null || string.IsNullOrEmpty(key)) return null;
            GeoEventChoice best = null;
            for (int i = 0; i < choices.Count; i++)
            {
                var c = choices[i];
                if (!string.Equals(OfferKey(c), key, StringComparison.Ordinal)) continue;
                if (best == null || PriceOf(c) < PriceOf(best)) best = c;
            }
            return best;
        }

        /// <summary>Key + price → the row, through the game's own generator. Never a hand-built
        /// <c>GeoEventChoice</c>.</summary>
        private static GeoEventChoice BuildChoice(GeoLevelController geo, GeoMarketplace market, string key, float price)
        {
            if (market == null || string.IsNullOrEmpty(key) || key.Length < 2) return null;
            if (GenerateItemChoice == null || GenerateResearchChoice == null)
            {
                // The generators are the whole mechanism; without them this would have to re-implement the
                // shop's arithmetic, which is exactly what this file refuses to do.
                Debug.LogError("[MP][market] the game's own row generators are gone (GeoMarketplace" +
                               ".GenerateItemChoice/GenerateResearchChoice) — the mirrored shop cannot be " +
                               "rebuilt, so this peer keeps the rows it had");
                return null;
            }
            string id = key.Substring(2);
            var repo = GameUtl.GameComponent<DefRepository>();
            switch (key[0])
            {
                case 'I':
                    var item = repo?.GetDef(id) as ItemDef;
                    return item == null ? null : GenerateItemChoice.Invoke(market, new object[] { item, price }) as GeoEventChoice;
                case 'R':
                    var research = geo?.GetResearchById(id);
                    return research == null ? null : GenerateResearchChoice.Invoke(market, new object[] { research, price }) as GeoEventChoice;
                case 'U':
                    // A vehicle offer is stored as its TacCharacterDef and rebuilt from that def's ItemDef —
                    // GeoMarketplace:126 verbatim (GenerateItemChoice puts the template back into Units).
                    var unit = repo?.GetDef(id) as TacCharacterDef;
                    return unit?.ItemDef == null ? null : GenerateItemChoice.Invoke(market, new object[] { unit.ItemDef, price }) as GeoEventChoice;
                default:
                    return null;
            }
        }

        private static float PriceOf(GeoEventChoice choice)
        {
            var res = choice?.Requirments?.Resources;
            return res == null || res.Count == 0 ? 0f : res[0].Value;
        }

        // ─── HOST: the roll is the host's, and every roll is broadcast ───

        /// <summary>THE seam for the assortment. <c>UpdateOptions()</c> (the private no-arg one) is the ONE
        /// place the shop is rolled — <c>OnLevelStart</c>:53, <c>AfterMissionComplete</c>:66,
        /// <c>UpdateOptionsWithRespectToTime</c>:206, the timed <c>UpdateOptions(Timing)</c>:213 and the
        /// console cheat all funnel through it — so one patch covers every reroll that exists.
        ///
        /// CLIENT: blocked. Its own roll would replace the host's shop with a different one, silently, on
        /// its own sim clock. Blocked, the list stays exactly as the save or the last broadcast left it.
        /// The LOAD path is untouched (<c>LevelStartLoadedGame</c> does not come through here), so a joining
        /// client still rebuilds the host's shipped offers natively.
        /// HOST: postfix broadcast — the new list, to everyone.</summary>
        [HarmonyPatch(typeof(GeoMarketplace), "UpdateOptions", new Type[0])]
        internal static class OfferRollPatch
        {
            private static bool Prefix()
            {
                if (IntentRail.ShouldRunNative()) return true;
                Debug.Log("[MP][market] client offer REROLL blocked — the host owns the assortment and " +
                          "ships it on 0xBF; a locally rolled shop is a different shop");
                return false;
            }

            private static void Postfix()
            {
                if (!IntentRail.ShouldRunNative()) return;   // the client's roll never ran
                HostBroadcastOffers("reroll");
            }
        }

        /// <summary>Push the host's CURRENT offer list to every peer. A full snapshot, so it is idempotent
        /// and a peer that missed one push is corrected by the next. Called on every reroll, after every
        /// purchase (the row is consumed) and from the reject reconverge.
        ///
        /// It also REPAINTS THE HOST'S OWN open shop, because this is the ONE place every host-side change
        /// to the list funnels through (law 11 / the REACTIVITY mandate: the clients repaint in
        /// <see cref="ApplyOffers"/>, and this is the same instant on this side). It used to be one
        /// call bolted onto <see cref="HandleBuy"/>, which left the timed reroll repainting nothing at all
        /// — a host standing in the shop when it rerolled kept the old goods until it walked out.</summary>
        internal static void HostBroadcastOffers(string why)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var market = Market();
            var choices = market?.MarketplaceChoices;
            if (choices == null) return;   // no Kaos Engines DLC / no shop in this campaign — nothing to say
            try
            {
                uint seq = Seq.Next(SurfaceIds.GeoMarketplaceOffers);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    var rows = choices.Select(c => new { Key = OfferKey(c), Price = PriceOf(c) })
                                      .Where(x => !string.IsNullOrEmpty(x.Key)).ToList();
                    if (rows.Count != choices.Count)
                        Debug.LogWarning("[MP][market] " + (choices.Count - rows.Count) + " of " + choices.Count +
                                         " host offers carry an outcome shape no key can name — those rows " +
                                         "cannot be shipped and the clients' shop will be shorter than the host's");
                    w.Write(rows.Count);
                    foreach (var row in rows) { w.Write(row.Key); w.Write(row.Price); }
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoMarketplaceOffers, SyncKind.StateDelta, inner)));
                Debug.Log("[MP][market] HOST shipped " + choices.Count + " offer(s) seq=" + seq + " (" + why + ")");
                RepaintOpenMarketplace();   // the host may be standing in the shop it just changed
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][market] HOST offer broadcast FAILED — the clients keep a shop that is no " +
                               "longer the host's: " + ex);
            }
        }

        // ─── CLIENT: mirror the list, then repaint the open shop ───

        /// <summary>Returns true when the surface was consumed. Client-only: the host already has the list.</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoMarketplaceOffers) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                uint seq;
                var rows = new List<KeyValuePair<string, float>>();
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    int count = MessageSerializer.ReadBoundedCount(r, 5); // key 1 + price 4
                    for (int i = 0; i < count; i++)
                    {
                        string key = WireString.ReadKey(r);
                        rows.Add(new KeyValuePair<string, float>(key, r.ReadSingle()));
                    }
                }
                if (!Seq.ShouldApply(SurfaceIds.GeoMarketplaceOffers, seq)) return true;
                if (ApplyOffers(rows)) Seq.Mark(SurfaceIds.GeoMarketplaceOffers, seq);
                else HoldOffers(seq, rows);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] MarketplaceSync inbound failed: " + ex); }
            return true;
        }

        /// <summary>Replace this peer's shop with the host's. Returns false (seq left unmarked, so a re-send
        /// still lands) when this peer has no geoscape to put a shop in.</summary>
        private static bool ApplyOffers(List<KeyValuePair<string, float>> rows)
        {
            var geo = GenericApplier.GeoLevel();
            var market = geo?.Marketplace;
            if (market?.MarketplaceChoices == null)
            {
                Debug.Log("[MP][market] offer list DROPPED — this peer has no live marketplace (in a battle, " +
                          "mid-load, or no Kaos Engines); HELD and re-applied when this peer's shop comes up");
                return false;
            }
            int dropped = 0;
            var rebuilt = new List<GeoEventChoice>(rows.Count);
            foreach (var row in rows)
            {
                var choice = BuildChoice(geo, market, row.Key, row.Value);
                if (choice == null) { dropped++; continue; }
                rebuilt.Add(choice);
            }
            if (dropped > 0)
                Debug.LogError("[MP][market] " + dropped + " of " + rows.Count + " offers could not be rebuilt on " +
                               "this peer — a def the host has and this build does not (mod parity, law 10). The " +
                               "shop is SHORTER here; buying the rows that DID rebuild stays correct because the " +
                               "intent carries the offer KEY and nothing positional (ResolveOffer). It did not " +
                               "when the wire carried a row index — every row after a dropped one was shifted.");
            market.MarketplaceChoices.Clear();
            market.MarketplaceChoices.AddRange(rebuilt);
            ClearHeldOffers();    // whatever was held is now older than what just landed
            Debug.Log("[MP][market] mirrored " + rebuilt.Count + " offer(s) from the host");
            RepaintOpenMarketplace();
            return true;
        }

        // ─── THE HELD PUSH: a one-shot broadcast a peer could not take, and the moment it can ───

        /// <summary>The last offer list this peer had to drop, kept until its shop exists. Value = default
        /// (null rows) when there is nothing held. Cleared by any list that DOES apply, and by
        /// <see cref="Reset"/> at teardown with the seq stream it belongs to.</summary>
        private static KeyValuePair<uint, List<KeyValuePair<string, float>>> _pending;

        /// <summary>Keep a list this peer could not place. Named (rather than an assignment at the drop site)
        /// so RailCheck L189 can drive the hold and prove the drop path still reaches it.</summary>
        internal static void HoldOffers(uint seq, List<KeyValuePair<string, float>> rows) =>
            _pending = new KeyValuePair<uint, List<KeyValuePair<string, float>>>(seq, rows);

        internal static void ClearHeldOffers() => _pending = default;

        internal static bool HasHeldOffers => _pending.Value != null;

        /// <summary>THE FALSIFIED PROMISE. <see cref="ApplyOffers"/> used to drop a list it could not place
        /// and say "the next push converges it" — but a push happens only on a reroll, a purchase or a reject,
        /// so in a 16-minute session (log 2026-08-07) there was exactly ONE, `seq=1` at host 23:09:06.209, and
        /// the client was GUARANTEED to be mid-load when it arrived (its own SendPrepared for that same load
        /// is 23:09:15.484, host+7.5 s = 23:09:13.7). The client's shop stayed 14 offers behind for the rest
        /// of the session and neither peer's log ever said so again. A one-shot push with no retry is not a
        /// convergence mechanism; it is a race the receiver always loses.
        ///
        /// The retry belongs on the RECEIVER, because the receiver is the only peer that knows when the drop
        /// condition ended — and it ends in exactly one place. <c>GeoMarketplace.OnLevelStart</c>
        /// (decompile GeoMarketplace.cs:41-60) is where <c>MarketplaceChoices</c> is constructed at all, on
        /// every geoscape level start: the first campaign load, and the return from every battle. Whatever the
        /// reason the list was dropped — battle, mid-load, shop not built yet — this is the frame after which
        /// it can land. The host is not asked for anything, so a peer that was away converges without costing
        /// every other peer a re-broadcast, and the seq guard still decides which list wins.
        ///
        /// This is not a new idea in this codebase, which is the point: <see cref="EventPopup"/> already holds
        /// a raise its peer cannot carry (<c>_held</c> + <c>DrainHeldRaises</c>:432) and replays it when the
        /// geoscape can take a window — all three of the 2026-08-07 session's HELD raises did land. The same
        /// hold is still MISSING from the two remaining one-shot host→all pushes with this shape:
        /// <c>GeoModalMirror</c>:591 and <c>CutsceneMirror</c>:201 both drop on "this peer has no live …" and
        /// nothing re-sends. A one-shot push is a fine wire form; a one-shot push with no hold is a race.</summary>
        [HarmonyPatch(typeof(GeoMarketplace), nameof(GeoMarketplace.OnLevelStart))]
        internal static class HeldOfferRetryPatch
        {
            private static void Postfix()
            {
                if (!HasHeldOffers) return;
                var rows = _pending.Value;
                uint seq = _pending.Key;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) { ClearHeldOffers(); return; }
                if (!Seq.ShouldApply(SurfaceIds.GeoMarketplaceOffers, seq)) { ClearHeldOffers(); return; }
                Debug.Log("[MP][market] re-applying the offer list held from seq=" + seq +
                          " — this peer's marketplace is live again");
                if (ApplyOffers(rows)) Seq.Mark(SurfaceIds.GeoMarketplaceOffers, seq);
            }
        }

        /// <summary>Reactivity (law 11) for a window the universal repaint deliberately will not touch — see
        /// the class remark. Repainted through the module's OWN rebuild (<c>UpdateVisuals</c>:161 public +
        /// the private <c>SetEncounter</c>:188 → <c>UpdateList</c>), never <c>ShowEncounter</c>: that one
        /// re-posts the shop's opening narration sound on every push. Nothing happens when the shop is not
        /// the open screen.</summary>
        internal static void RepaintOpenMarketplace()
        {
            try
            {
                var view = GenericApplier.GeoLevel()?.View;
                if (!(view?.CurrentViewState is UIStateMarketplaceGeoscapeEvent)) return;
                var module = view.GeoscapeModules?.TheMarketplaceModule;
                var ev = module == null ? null : ModuleGeoEvent?.GetValue(module) as GeoscapeEvent;
                if (module == null || ev == null || ModuleSetEncounter == null)
                {
                    Debug.LogWarning("[MP][market] open shop NOT repainted — the module's own rebuild is not " +
                                     "reachable (module=" + (module == null) + " event=" + (ev == null) + "); the " +
                                     "rows are already correct in the model and reopening the shop shows them");
                    return;
                }
                // law 8: the native rebuild fires UI events our own capture seams listen to.
                using (SyncApplyScope.Enter())
                {
                    module.UpdateVisuals();
                    ModuleSetEncounter.Invoke(module, new object[] { ev });
                }
            }
            catch (Exception ex)
            { Debug.LogError("[MP][market] repaint of the open shop threw — screen kept: " + ex); }
        }

        // ─── THE PURCHASE: capture (client) / re-broadcast (host) at ONE funnel ───

        /// <summary>THE seam for buying. Every row click reaches
        /// <c>TheMarketplaceChoicesController.OnButtonChoiceSelected</c>:53 — the button's own
        /// <c>ChoiceSelected</c> event (SiteBaseChoicesController:63/:73) and the controller-pad
        /// <c>SelectFirstChoiceOption</c>:92 both land here — and it sits ABOVE
        /// <c>UIModuleTheMarketplace.OnChoiceSelected</c>:210, whose FIRST act is to spend the shared wallet
        /// (:215). Block-first: on a client nothing local runs at all, and the click becomes an address.
        /// The HOST postfix re-ships the list, because its own purchase just consumed a row that every other
        /// peer still shows.</summary>
        [HarmonyPatch(typeof(TheMarketplaceChoicesController), nameof(TheMarketplaceChoicesController.OnButtonChoiceSelected))]
        internal static class BuyCapturePatch
        {
            private static bool Prefix(GeoEventChoice selectedChoice)
            {
                if (IntentRail.ShouldRunNative()) return true;

                var geo = GenericApplier.GeoLevel();
                var module = geo?.View?.GeoscapeModules?.TheMarketplaceModule;
                var ev = module == null ? null : ModuleGeoEvent?.GetValue(module) as GeoscapeEvent;
                // The address is read off the CLICKED ROW ITSELF — never off its position in a list the
                // mirror rebuilds underneath the button. IndexOf used to live here and returned −1 for any
                // button that out-lived one offer push, which is how a live click became a dead one.
                string key = OfferKey(selectedChoice);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(ev?.EventID))
                {
                    // Never silent, and never a local fallback: a purchase this peer cannot address is a
                    // purchase the host cannot re-derive, and running it here would spend the shared wallet.
                    Debug.LogWarning("[MP][market] client purchase DROPPED — the clicked row is not addressable " +
                                     "(key=" + (key ?? "none") + " event=" + (ev?.EventID ?? "none") +
                                     "); nothing was spent locally either");
                    return false;
                }
                string eventId = ev.EventID;
                IntentRail.Send(SurfaceIds.GeoMarketplaceIntent, OpBuy, "buy " + key,
                                w => { w.Write(eventId); w.Write(key); });
                return false;
            }

            private static void Postfix()
            {
                if (!IntentRail.ShouldRunNative()) return;   // the client's click never reached the native body
                HostBroadcastOffers("purchase");
            }
        }

        /// <summary>THE VEHICLE COUNT LEAVES THE PURCHASE PATH.
        ///
        /// <c>GeoscapeEvent.CompleteMarketplaceEvent</c>:77 builds the reward's context with
        /// <c>Context.Site.Vehicles.SingleOrDefault()</c> — a vanilla single-player assumption that there is
        /// at most ONE aircraft parked where the event fires. <c>Enumerable.SingleOrDefault</c> THROWS on
        /// two, so the second aircraft at the marketplace breaks every purchase for everyone, on the host's
        /// own click too. Co-op does not create the bug, it makes it ordinary: the whole point of a shared
        /// campaign is several aircraft in the same place.
        ///
        /// The fix patches the PICK and nothing else — <c>GenerateFactionReward</c> and
        /// <c>ChoiceReward.Apply</c> stay the game's, because re-implementing a reward apply is how a mod
        /// starts disagreeing with the game it mirrors. One <c>call</c> operand is swapped for
        /// <see cref="PickVehicle"/>, which (a) cannot throw at any count and (b) is DETERMINISTIC across
        /// peers: it orders by <c>GeoVehicle.VehicleID</c> — law 2's own stable aircraft id, identical on
        /// every peer — never by list order, which is <c>GeoMap.Vehicles</c> filtered by a <c>Where</c> and
        /// therefore an artefact of creation order, not of the shared model.
        ///
        /// Ungated on purpose: this is not a mirroring decision, it is the game's arithmetic being wrong at
        /// a count vanilla never reached, and a peer that picked differently from the host would be exactly
        /// the divergence the determinism is for. Its only caller is the marketplace module
        /// (<c>UIModuleTheMarketplace</c>:219), so the blast radius is this shop.</summary>
        [HarmonyPatch(typeof(GeoscapeEvent), nameof(GeoscapeEvent.CompleteMarketplaceEvent))]
        internal static class VehiclePickPatch
        {
            /// <summary>Internal: RailCheck L111 calls it directly — the assertion is that the PICK is
            /// total and order-independent, which is a question about this method, not about the patch.</summary>
            /// ponytail: the null guard is <c>ReferenceEquals</c>, NOT <c>v == null</c> — GeoVehicle is a
            /// UnityEngine.Object, whose overloaded <c>==</c> also answers true for a DESTROYED one, which
            /// would collapse every sort key to the same value and hand the pick straight back to list
            /// order. RailCheck L111 caught exactly that.
            internal static GeoVehicle PickVehicle(IEnumerable<GeoVehicle> vehicles) =>
                vehicles?.OrderBy(v => ReferenceEquals(v, null) ? int.MaxValue : v.VehicleID).FirstOrDefault();

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var target = AccessTools.Method(typeof(VehiclePickPatch), nameof(PickVehicle));
                int swapped = 0;
                foreach (var ci in instructions)
                {
                    if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi &&
                        mi.DeclaringType == typeof(Enumerable) && mi.Name == "SingleOrDefault")
                    {
                        swapped++;
                        yield return new CodeInstruction(OpCodes.Call, target) { labels = ci.labels, blocks = ci.blocks };
                    }
                    else yield return ci;
                }
                // Law 1 (silent swallow): a transpiler that matched nothing leaves the throw in place and
                // says nothing at all, which is the worst of both worlds — the patch looks applied.
                if (swapped != 1)
                    Debug.LogError("[MP][market] CompleteMarketplaceEvent's vehicle pick was NOT replaced (" +
                                   swapped + " SingleOrDefault call(s) found, expected 1) — the game's own " +
                                   "single-aircraft assumption is still live and a second aircraft parked at " +
                                   "the marketplace will throw on every peer's purchase");
            }
        }

        /// <summary>HOST: re-derive everything from HOST state and run the game's own three lines
        /// (<c>UIModuleTheMarketplace.OnChoiceSelected</c>:215/:219/:223). The client named a row; the price,
        /// the eligibility test and the reward all come from here.
        ///
        /// The context is built exactly as the game builds one for this window
        /// (<c>GeoscapeView.ToMarketplace</c>:736 — a fresh <c>GeoscapeEvent</c> over the marketplace site
        /// and the Phoenix faction), because the host generally has no marketplace window open at all and
        /// therefore no live event to borrow.
        /// The vehicle-count hazard at <c>CompleteMarketplaceEvent</c>:77 is closed by
        /// <see cref="VehiclePickPatch"/> — no purchase path reads how many aircraft are parked.</summary>
        private static void HandleBuy(ulong peer, uint nonce, BinaryReader r)
        {
            string eventId = WireString.ReadKey(r);
            string key = WireString.ReadKey(r);

            var geo = GenericApplier.GeoLevel();
            var market = geo?.Marketplace;
            var choices = market?.MarketplaceChoices;
            if (geo == null || choices == null)
            { Reject(peer, "buy: no marketplace on the host"); return; }

            // IDENTITY, NOT POSITION. A row the host has already sold resolves to nothing here no matter
            // what moved into its slot, and a client whose mirrored list is shorter than the host's still
            // addresses the right row. The refusal answers IN THE PANEL, never in a window: Reject's
            // reconverge re-pushes the host's real list, so the row the player clicked vanishes under the
            // cursor. Losing a race for a shared offer is ordinary here, and ordinary does not get a modal.
            var choice = ResolveOffer(choices, key);
            if (choice == null)
            { Reject(peer, "the shop no longer offers this — another peer bought it, or the assortment " +
                           "rerolled (" + key + ")"); return; }

            var faction = geo.PhoenixFaction;
            var site = geo.Map?.ActiveSites?.FirstOrDefault(s => s.Type == GeoSiteType.Marketplace);
            var data = geo.EventSystem?.GetEventByID(eventId, canFail: true)?.GeoscapeEventData;
            if (faction == null || site == null || data == null)
            { Reject(peer, "buy: the host cannot rebuild this shop's context (site=" + (site == null) +
                           " event '" + eventId + "'=" + (data == null) + ")"); return; }

            var geoEvent = new GeoscapeEvent(data, new GeoscapeEventContext(site, faction));
            // The GAME'S own eligibility test — the same predicate that greys the native button out
            // (SiteBaseChoicesController:60). It is what stops a client's screen from deciding it can afford
            // something the host cannot.
            if (!choice.PassRequirements(faction, geoEvent.Context))
            { Reject(peer, "buy refused by the game's own requirement test — the shared wallet cannot pay " +
                           PriceOf(choice) + " for '" + key + "'"); return; }

            if (choice.Requirments != null) faction.Wallet.Take(choice.Requirments.Resources, OperationReason.Purchase);
            geoEvent.CompleteMarketplaceEvent(choice, faction);
            choices.Remove(choice);
            Debug.Log("[MP][market] HOST bought '" + key + "' for " + PriceOf(choice) + " on behalf of peer=" +
                      peer + " nonce=" + nonce);
            // The wallet and the goods ride the value rail; the consumed ROW does not, so it is pushed here.
            // HostBroadcastOffers repaints the host's own open shop too — one seam for every list change.
            HostBroadcastOffers("purchase by peer " + peer);
        }

        /// <summary>A refused purchase changes nothing on the host, so the value rail has nothing to ship —
        /// the faction root carries the wallet the client may have painted as spent, and the family
        /// reconverge (registered above) re-pushes the offer list the value rail cannot carry.
        ///
        /// NEVER <c>notify</c>. A refused purchase must be VISIBLE, and in this panel it already is: the
        /// reconverge above re-pushes the host's real list, <see cref="ApplyOffers"/> replaces the rows and
        /// <see cref="RepaintOpenMarketplace"/> redraws them, so the row the player clicked DISAPPEARS under
        /// the cursor and the shop reconciles to the host's. That is the answer — the panel showing the
        /// truth — and it is why a modal would be pure noise on top of it. Losing a race for a shared offer
        /// is the ORDINARY outcome of an optimistic click in a shared shop, not an event: at shop rates a
        /// prompt per refusal is the error-box storm L123 was written to stop, and it yanks the player out
        /// of the very menu the reconverge just corrected. The reason still crosses the wire and is still
        /// logged on both peers (IntentRail's nudge), so nothing is swallowed.</summary>
        private static void Reject(ulong peer, string why) =>
            IntentRail.Reject(SurfaceIds.GeoMarketplaceIntent, peer, why, "F#", "MK");
    }
}
