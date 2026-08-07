using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L171 — A CONSUMED OFFER IS NOT PURCHASABLE ON ANY PEER, AND THE OPEN SHOP SAYS SO WITHOUT BEING REOPENED.
    ///
    /// THE REPORT (live co-op, 2026-08-07): "the client bought a soldier; all other goods vanished from the
    /// shop; the buttons did nothing; only re-entering the shop recovered it — and I could re-buy something
    /// the host had already bought." No money moved, which was correct and was the only thing that worked:
    /// the host refused every one of those clicks, silently, to its own console.
    ///
    /// THE ROOT WAS THE WIRE IDENTITY. The purchase intent carried <c>[eventId][index:i32][offerKey]</c> and
    /// the host resolved <c>choices[index]</c>. A row index is not an identity — it OUTLIVES the row it
    /// pointed at and starts naming whatever slid into the slot. Three failures came out of that one shape,
    /// verbatim from <c>multiplayer.log</c>:
    ///   • <c>buy: row 10 is outside the host's 4-offer shop</c> — the client's list and the host's had
    ///     different LENGTHS (<c>ApplyOffers</c> drops rows this build cannot rebuild), so every index after
    ///     a dropped row was shifted and a legitimate purchase was refused.
    ///   • <c>row 0 is 'I:29998034…' here, not the 'U:{3FBC2BB0…}' that was clicked</c> — the key check that
    ///     was supposed to make the index safe could only ever REFUSE; it never corrected the address.
    ///   • Three intents with the SAME id and index (nonces 143/144/145) — a button that outlived one offer
    ///     push kept sending, and its click resolved through <c>MarketplaceChoices.IndexOf(selectedChoice)</c>
    ///     over a list <c>ApplyOffers</c> had already replaced element-for-element.
    ///
    /// SO THE LAW ASSERTS THE OUTCOME, NOT THE CALL (the failure mode L96/L102 were written for). It does not
    /// check that a resolver is called, or what the intent's byte layout is called. It EXECUTES
    /// <see cref="MarketplaceSync.ResolveOffer"/> over real <c>GeoEventChoice</c> lists and demands the two
    /// directions that matter:
    ///   (a) POSITIVE CONTROL — a key the host still holds resolves to THAT row, from any position.
    ///   (b) FALSIFICATION — remove the row (the host sold it) and the same key resolves to NOTHING. Not to
    ///       its neighbour, which is exactly what the index scheme returned. This is "a consumed offer is not
    ///       purchasable on any peer" stated so that the old code FAILS it: arm (b) reproduces the live
    ///       sequence — buy row 1, then click row 1 again — and asserts the second click addresses nothing.
    ///
    /// ARM (e) KEEPS THE PRICE OFF THE WIRE while the key is ambiguous. <c>OfferKey</c> is def-level and the
    /// shop may roll one <c>ItemDef</c> twice (<c>GeoMarketplace.GenerateRandomChoice</c>:272 only retires an
    /// option marked <c>DisallowDuplicates</c>), so a key can match two rows at two prices. Shipping the price
    /// to disambiguate would have been the obvious fix and is forbidden — L99 arm (d): a price on the wire is
    /// a price the host did not derive. Cheapest-first is the arm that resolves the ambiguity with data the
    /// host already has, and it is order-independent, so two peers cannot disagree about which row was sold.
    ///
    /// ARM (f) IS THE REACTIVITY HALF (P11 / the REACTIVITY mandate). The shop is a QUEUED window, so the
    /// universal Exit+Enter fallback deliberately skips it — the ONLY repaints that can exist are the
    /// <c>UiNativeRepaint.Table</c> entry and the direct calls out of this file. The live recovery was "leave the
    /// screen and come back", which is the defect the mandate names by that exact description. Both directions
    /// of the list change are asserted: a MIRRORED list (client, <c>ApplyOffers</c> — L99 arm h owns that one)
    /// and a HOST-SIDE change (<c>HostBroadcastOffers</c>, the one funnel every host reroll and every applied
    /// purchase passes through). Before this law the host-side repaint was one call bolted onto
    /// <c>HandleBuy</c>, so a timed reroll repainted nothing at all on the host's own open screen.
    ///
    /// ARM (h) IS THE DEAD BUTTON, AND IT IS ANSWERED IN THE PANEL. A refusal the player cannot see is this
    /// repo's dominant bug class — but the cure is not a popup. Losing a race for a shared offer is the
    /// ORDINARY outcome of an optimistic click in a shared shop; at shop rates a prompt per refusal is the
    /// error-box storm L123 was written to stop, and it throws the player out of the very menu that is about
    /// to correct itself. What answers the click is the SHOP: the family's reconverge re-pushes the host's
    /// real list, <c>ApplyOffers</c> replaces the rows, the repaint redraws them, and the row the player
    /// clicked disappears under the cursor. So the arm has two halves — the reconverge is EXECUTED for real
    /// (a declared-but-unwired one looks identical in source), and <c>MarketplaceSync.Reject</c> must NOT
    /// reach the notifying overload. Take the reconverge away and the refusal genuinely does need a popup;
    /// that is the trade this arm pins down in both directions.
    /// </summary>
    internal static class L171_ConsumedOfferIsGoneEverywhere
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var resolve = typeof(MarketplaceSync).GetMethod("ResolveOffer", AllMembers);
            if (resolve == null)
            {
                yield return "L171 premise-changed: MarketplaceSync.ResolveOffer is gone. This law's whole subject " +
                             "is that a purchase resolves an offer by IDENTITY over the host's live list; with no " +
                             "resolver the address is positional again by default, which is the bug it was " +
                             "written for. Restate the law against whatever replaced it — do not delete it";
                yield break;
            }

            // Executed arms need real rows. If the shop's own row type stops being constructible here the
            // executable arms would pass by doing nothing, which is the vacuity this guard exists to catch.
            List<GeoEventChoice> probe = null;
            string probeFailure = null;
            try { probe = new List<GeoEventChoice> { Row("R:A", 100f), Row("R:B", 200f), Row("R:C", 300f) }; }
            catch (Exception ex)
            {
                probeFailure =
                    "L171 premise-changed: a bare GeoEventChoice can no longer be built here (" + ex.GetType().Name +
                    ": " + ex.Message + "), so every executable arm below would pass without testing anything. " +
                    "The row shape changed — re-derive Row() from GeoMarketplace.GenerateChoice";
            }
            if (probe == null) { yield return probeFailure; yield break; }
            if (MarketplaceSync.OfferKey(probe[1]) != "R:B")
            { yield return "L171 premise-changed: OfferKey no longer names a research row 'R:<id>', so the probe " +
                           "rows below carry keys this law's own arms cannot reason about"; yield break; }

            // ─── (a) POSITIVE CONTROL: a live key resolves to ITS OWN row. ───
            if (!ReferenceEquals(MarketplaceSync.ResolveOffer(probe, "R:B"), probe[1]))
                yield return "L171 positive-control: ResolveOffer cannot even find a row that IS in the list. " +
                             "Every arm below is meaningless and no purchase can ever be addressed";

            // ─── (b) THE FALSIFICATION: a consumed offer addresses NOTHING. ───
            // The live sequence, replayed: the host sells row 1, and the peer whose screen still draws it
            // clicks row 1 again. Under the old scheme index 1 was now 'R:C' and the click bought a soldier
            // nobody asked for; the key check could only refuse, never correct.
            var afterSale = new List<GeoEventChoice>(probe);
            afterSale.Remove(probe[1]);
            var stale = MarketplaceSync.ResolveOffer(afterSale, "R:B");
            if (stale != null)
                yield return "L171 consumed-offer-still-purchasable: after the host sold 'R:B' and removed it, " +
                             "that key still resolves to " + MarketplaceSync.OfferKey(stale) + ". A row the host " +
                             "no longer holds must be unbuyable on EVERY peer the instant it is consumed — " +
                             "including on the peer whose open shop still draws it, which is the whole reported " +
                             "bug (\"I could re-buy something the host had already bought\")";
            if (!ReferenceEquals(MarketplaceSync.ResolveOffer(afterSale, "R:C"), probe[2]))
                yield return "L171 survivor-lost: 'R:C' outlived the sale of its neighbour but no longer resolves. " +
                             "Consuming one row must not un-address the others — that is the shop emptying itself, " +
                             "the other half of the report (\"all other goods vanished\")";

            // ─── (c) POSITION-INDEPENDENT: the same key, any order, the same row. ───
            var reversed = Enumerable.Reverse(probe).ToList();
            if (!ReferenceEquals(MarketplaceSync.ResolveOffer(reversed, "R:B"), probe[1]))
                yield return "L171 address-is-positional: reversing the list changed what a key resolves to. The " +
                             "address must survive a list whose ORDER or LENGTH differs from the one the clicking " +
                             "peer mirrored — ApplyOffers drops rows this build cannot rebuild (law 10), and every " +
                             "row after a dropped one is shifted";

            // ─── (d) A SHORTER MIRROR STILL BUYS THE RIGHT THING. The 'row 10 is outside the host's
            //         4-offer shop' line, as an assertion. ───
            var shortMirror = new List<GeoEventChoice> { probe[2] };   // this peer rebuilt only the last row
            if (!ReferenceEquals(MarketplaceSync.ResolveOffer(probe, MarketplaceSync.OfferKey(shortMirror[0])), probe[2]))
                yield return "L171 short-mirror-misaddresses: a key taken off a peer whose list holds ONE row does " +
                             "not resolve to the matching row in the host's three-row list. That peer's row 0 is " +
                             "the host's row 2 — under a row index it bought the host's row 0, or was refused for " +
                             "naming a row past the end";

            // ─── (e) AMBIGUOUS KEY → THE HOST'S OWN CHEAPEST ROW, from either order. The arm that keeps
            //         the price off the wire (L99 d). ───
            var twin = new List<GeoEventChoice> { Row("R:A", 500f), Row("R:A", 300f) };
            var cheap = MarketplaceSync.ResolveOffer(twin, "R:A");
            var cheapReversed = MarketplaceSync.ResolveOffer(Enumerable.Reverse(twin).ToList(), "R:A");
            if (!ReferenceEquals(cheap, twin[1]) || !ReferenceEquals(cheapReversed, twin[1]))
                yield return "L171 ambiguous-key-nondeterministic: two rows share a key at 500 and 300 and " +
                             "ResolveOffer does not take the 300 one from BOTH orders (got " + Price(cheap) + " / " +
                             Price(cheapReversed) + "). The shop may roll one ItemDef twice, so a key genuinely " +
                             "can be ambiguous; resolving it by list order makes the sale depend on creation " +
                             "order, and resolving it by shipping the price is L99 arm (d)'s violation. Cheapest " +
                             "is the only tie-break that is both deterministic and derived from host data alone";

            // ─── (f) NO POSITION CROSSES THE WIRE. The shape that produced every arm above. ───
            var handleBuy = typeof(MarketplaceSync).GetMethod("HandleBuy", AllMembers);
            var readInt32 = typeof(BinaryReader).GetMethod(nameof(BinaryReader.ReadInt32));
            var writeInt32 = typeof(BinaryWriter).GetMethod("Write", new[] { typeof(int) });
            if (handleBuy == null)
                yield return "L171 host-handler-gone: MarketplaceSync.HandleBuy no longer exists — nothing on the " +
                             "host resolves a client's purchase at all";
            else if (CalledBy(handleBuy).Any(m => Same(m, readInt32)))
                yield return "L171 host-reads-a-position: MarketplaceSync.HandleBuy reads an Int32 off the intent. " +
                             "The only Int32 this op ever carried was the row index, and a row index survives the " +
                             "row it named. The client sends an event id and an offer KEY; the host resolves it";
            var capture = typeof(MarketplaceSync).GetNestedType("BuyCapturePatch", AllMembers);
            if (capture == null)
                yield return "L171 capture-gone: MarketplaceSync.BuyCapturePatch is gone — L99 owns why that is " +
                             "fatal; this law adds only that whatever replaces it must not write a position";
            else if (CaptureClosure(capture).Any(m => Same(m, writeInt32)))
                yield return "L171 client-names-a-position: the purchase capture writes an Int32. Positions are " +
                             "not addresses — the clicking peer's list can be shorter, re-ordered or one push " +
                             "stale, and a row index silently becomes a different purchase in all three cases";

            // ─── (g) REACTIVITY: a HOST-SIDE list change repaints the host's own open shop, and the
            //         generic rail batch can reach the shop too. ───
            var broadcast = typeof(MarketplaceSync).GetMethod("HostBroadcastOffers", AllMembers);
            var repaint = typeof(MarketplaceSync).GetMethod("RepaintOpenMarketplace", AllMembers);
            if (broadcast == null || repaint == null)
                yield return "L171 repaint-seam-gone: MarketplaceSync.HostBroadcastOffers / " +
                             "RepaintOpenMarketplace no longer both exist — the shop is a queued window, so these " +
                             "ARE the repaint; there is no fallback behind them";
            else if (!CalledBy(broadcast).Any(m => Same(m, repaint)))
                yield return "L171 host-shop-unpainted: HostBroadcastOffers does not reach RepaintOpenMarketplace. " +
                             "It is the ONE funnel every host-side list change passes — the timed reroll, the " +
                             "post-mission reroll, an applied client purchase, the reject reconverge — so a host " +
                             "standing in the shop keeps the sold or rerolled goods until it walks out and back " +
                             "in. Requiring a re-enter is the defect the REACTIVITY mandate names, not a nit";
            foreach (var v in NativeRepaintArm(repaint)) yield return v;

            // ─── (h) THE REFUSAL ANSWERS IN THE PANEL, AND NEVER IN A WINDOW. ───
            // Two halves of one rule, and the first is why the second can exist: a refused purchase must
            // still TELL the player something, and what tells them is the shop correcting itself under the
            // cursor. EXECUTED — the family's registered reconverge is invoked for real, because a reconverge
            // that is declared but unwired looks identical in source to one that works.
            foreach (var v in ReconvergeArm()) yield return v;

            var reject = typeof(MarketplaceSync).GetMethod("Reject", AllMembers);
            var notifyOverload = typeof(IntentRail).GetMethod(nameof(IntentRail.Reject), BindingFlags.Public |
                BindingFlags.Static, null,
                new[] { typeof(byte), typeof(ulong), typeof(string), typeof(bool), typeof(string[]) }, null);
            if (reject == null || notifyOverload == null)
                yield return "L171 premise-changed: MarketplaceSync.Reject and/or IntentRail's notify overload no " +
                             "longer resolve, so how a refused purchase answers the player is unprovable here";
            else if (CalledBy(reject).Any(m => Same(m, notifyOverload)))
                yield return "L171 refusal-raises-a-modal: MarketplaceSync.Reject forwards to IntentRail's NOTIFY " +
                             "overload, which puts a prompt on the refused peer's screen. Losing a race for a " +
                             "shared offer is the ORDINARY outcome of an optimistic click in a shared shop, not " +
                             "an event — at shop rates that is the error-box storm L123 was written to stop, and " +
                             "it throws the player out of the very panel the reconverge just corrected. The " +
                             "refusal is already visible where it belongs: the host's real list comes back, the " +
                             "clicked row disappears, and the reason still crosses the wire and is still logged " +
                             "on both peers. L123 owns this rule globally; this arm keeps the shop from being " +
                             "the exception that re-opens it";
        }

        /// <summary>EXECUTED: register the real family and invoke the REAL reconverge. This is the arm that
        /// lets the shop refuse without a popup — the reconverge is the whole answer to a refused click, so a
        /// family that registers none leaves the refused peer staring at a row the host does not have, which
        /// is the dead button the report opened with. Off the host it is a no-op that must not throw.</summary>
        private static IEnumerable<string> ReconvergeArm()
        {
            string violation = null;
            try
            {
                MarketplaceSync.RegisterIntents();
                var families = typeof(IntentRail).GetField("_families", BindingFlags.NonPublic | BindingFlags.Static)
                                                 ?.GetValue(null) as System.Collections.IDictionary;
                var family = families?[SurfaceIds.GeoMarketplaceIntent];
                var reconverge = family?.GetType().GetField("Reconverge")?.GetValue(family) as Delegate;
                if (families == null || family == null)
                    violation = "L171 premise-changed: nothing is registered on surface 0xBE after " +
                                "MarketplaceSync.RegisterIntents(), so the reject reconverge cannot be read";
                else if (reconverge == null)
                    violation = "L171 refusal-answers-nothing: the shared-offer family registers NO reconverge. A " +
                                "reject changes no host state, so the value rail emits nothing and the offer list " +
                                "does not ride it — without the forced re-push the refused peer keeps the row it " +
                                "clicked, forever, and the click reads as a dead button. That is the whole reason " +
                                "this refusal needs no popup; remove the reconverge and it needs one again";
                else
                {
                    // It must reach the offer push, and it must survive being called with no session.
                    if (!CalledBy(reconverge.Method).Any(m => m.DeclaringType == typeof(MarketplaceSync) &&
                                                              m.Name == "HostBroadcastOffers"))
                        violation = "L171 reconverge-pushes-nothing: the family's reconverge does not reach " +
                                    "MarketplaceSync.HostBroadcastOffers. Re-emitting the VALUE rail cannot carry " +
                                    "the offer list (docs/rail-baseline.txt:14-18, bridge-unresolved), so only " +
                                    "that push corrects the refused peer's shop";
                    else reconverge.DynamicInvoke();
                }
            }
            catch (Exception ex)
            {
                violation = "L171 reconverge-throws: invoking the shared-offer reconverge threw (" +
                            ex.GetType().Name + ": " + ex.Message + "). It runs inside IntentRail.Reject on the " +
                            "host, so a throw here aborts the reject nudge and the refused peer is never " +
                            "corrected NOR told — silent swallow on the one path whose job is to speak";
            }
            if (violation != null) yield return violation;
        }

        /// <summary>The <c>UiNativeRepaint</c> entry is the shop's only other repaint carrier, and it is a
        /// DECLARATION — a missing key is not a compile error and produces no log line, so a generic rail
        /// batch would simply leave the screen stale. Read the live table and the arm's own IL.</summary>
        private static IEnumerable<string> NativeRepaintArm(MethodInfo repaint)
        {
            var table = typeof(UiNativeRepaint).GetField("Table", BindingFlags.NonPublic | BindingFlags.Public |
                                                             BindingFlags.Static)?.GetValue(null)
                        as System.Collections.IDictionary;
            if (table == null)
            {
                yield return "L171 premise-changed: UiNativeRepaint.Table is not a readable dictionary, so whether the " +
                             "shop has a declared repaint arm cannot be checked";
                yield break;
            }
            var arm = table[typeof(UIStateMarketplaceGeoscapeEvent)] as Delegate;
            if (arm == null)
            {
                yield return "L171 shop-has-no-native-repaint-arm: UiNativeRepaint.Table has no entry for " +
                             "UIStateMarketplaceGeoscapeEvent. The Exit+Enter fallback SKIPS this screen on " +
                             "purpose (a queued window would replay its presentation and re-post the opening " +
                             "narration), so with no arm a rail batch that changes the shared wallet or the goods " +
                             "repaints nothing and the open shop stays stale until reopened";
                yield break;
            }
            if (repaint != null && !CalledBy(arm.Method).Any(m => Same(m, repaint)))
                yield return "L171 native-repaint-arm-repaints-nothing: the UiNativeRepaint arm for " +
                             "UIStateMarketplaceGeoscapeEvent does not reach MarketplaceSync" +
                             ".RepaintOpenMarketplace. A registered arm that rebuilds nothing is worse than a " +
                             "missing one — it reports the screen as covered";
        }

        // ── A bare shop row, built the way GeoMarketplace.GenerateChoice:315 builds one: a research row,
        //    because its key is a plain string and needs no DefRepository. ──
        private static GeoEventChoice Row(string key, float price)
        {
            var c = new GeoEventChoice
            {
                Requirments = new GeoEventChoiceRequirements(),
                Outcome = new GeoEventChoiceOutcome()
            };
            c.Requirments.Resources.Add(new ResourceUnit(ResourceType.Materials, price));
            c.Outcome.GiveResearches.Add(key.Substring(2));
            return c;
        }

        private static string Price(GeoEventChoice c) =>
            c == null ? "null" : c.Requirments.Resources[0].Value.ToString();

        // ── IL probe, the flat token scan L94/L99 already justified: the methods under test are short
        //    handlers and lambdas, and an unaligned hit resolves to nothing or to something unrelated. ──
        private static IEnumerable<MethodBase> CalledBy(MethodBase caller)
        {
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;   // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null) yield return c;
            }
        }

        /// <summary>The patch class AND its compiler-generated nested types — the intent body is a lambda,
        /// which lives in a display class, not in the prefix.</summary>
        private static IEnumerable<MethodBase> CaptureClosure(Type patchClass)
        {
            foreach (var t in new[] { patchClass }.Concat(patchClass.GetNestedTypes(AllMembers)))
            {
                MethodBase[] methods;
                try { methods = t.GetMethods(AllMembers).Cast<MethodBase>().ToArray(); }
                catch { continue; }
                foreach (var m in methods)
                    foreach (var c in CalledBy(m))
                        yield return c;
            }
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
