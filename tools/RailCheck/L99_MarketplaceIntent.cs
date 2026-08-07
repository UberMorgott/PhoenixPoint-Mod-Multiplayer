using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters;

namespace RailCheck
{
    /// <summary>
    /// L99 — THE KAOS MARKETPLACE IS A SHOP, NOT A HOST PRIVILEGE.
    ///
    /// THE REPORT (live 3-instance test, 2026-08-04): "It works only on the host. On a client it does show an
    /// assortment — though I do not know whether that assortment is even the same across windows — but BUYING
    /// does not work. I click to buy something, be it a research, an item, whatever, and nothing happens."
    /// Both halves were real and independent: the offer list was never replicated (it is EXCLUDED from the
    /// value rail as bridge-unresolved, docs/rail-baseline.txt:14-18, and every peer rolls its own with
    /// <c>Random.Range</c>), and the client's click was refused with no intent behind the refusal.
    ///
    /// THE ARM THAT MATTERS MOST IS (c). <c>UIModuleTheMarketplace.OnChoiceSelected</c>:215 takes the price
    /// out of the SHARED wallet two lines before :219 grants the goods — the identical shape as the
    /// <c>GeoCharacter.RepairItem</c> leak closed the same day. So the client capture is tested for
    /// REACHABILITY, not for intent: the whole transitive mod-side closure of the capture prefix must not be
    /// able to touch <c>Wallet</c> or <c>CompleteMarketplaceEvent</c> at all. A capture that merely happens
    /// not to spend today, from a call chain that could, is the bug waiting to be re-shipped.
    ///
    /// ARM (d) IS THE OTHER HALF OF LAW 3, FROM THE OTHER END: a client may name WHAT it wants and never
    /// WHAT IT COSTS. It is stated mechanically as "no float crosses this surface" — the capture writes none
    /// and the host handler reads none — because a price on the wire is a price the host did not derive.
    ///
    /// ARM (g) IS EXECUTED, NOT ASSERTED: it registers the family for real and counts the ops. ONE op sells
    /// items, vehicles and researches, because the game sells all three through one <c>GeoEventChoice</c>;
    /// a second op is the per-goods-type macaroni factory the mandate forbids, and counting the live table is
    /// the only way to notice one being added.
    ///
    /// ARM (f) IS THE ASSORTMENT: the shop's roll is the HOST's. Unblocked, a client re-rolls its own shop on
    /// its own sim clock (GeoLevelController:865) and after every mission (:756), which is exactly how two
    /// peers came to be looking at different goods under one campaign.
    /// </summary>
    internal static class L99_MarketplaceIntent
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var capture = typeof(MarketplaceSync).GetNestedType("BuyCapturePatch", AllMembers);
            var roll = typeof(MarketplaceSync).GetNestedType("OfferRollPatch", AllMembers);
            if (capture == null || roll == null)
            {
                yield return "L99 seams-gone: MarketplaceSync no longer declares BuyCapturePatch and/or " +
                             "OfferRollPatch — the shop has no capture at all, so a client's click resolves " +
                             "locally (shared wallet) or not at all, and every peer rolls its own assortment";
                yield break;
            }

            // ─── (a) THE CLICK IS CAPTURED AT THE FUNNEL ABOVE THE WALLET. ───
            if (!PatchTargets(capture).Any(t => t.Type == typeof(TheMarketplaceChoicesController) &&
                                                t.Method == nameof(TheMarketplaceChoicesController.OnButtonChoiceSelected)))
                yield return "L99 buy-captured-too-low: BuyCapturePatch does not patch TheMarketplaceChoicesController" +
                             ".OnButtonChoiceSelected. That override is THE funnel every row click reaches (the " +
                             "button's own ChoiceSelected event and the controller-pad SelectFirstChoiceOption:92), " +
                             "and it sits ABOVE UIModuleTheMarketplace.OnChoiceSelected — whose FIRST act is " +
                             "Wallet.Take:215. Capturing any lower means the shared wallet is already spent when " +
                             "the intent is decided";

            var prefix = capture.GetMethod("Prefix", AllMembers);
            var rollPrefix = roll.GetMethod("Prefix", AllMembers);

            // ─── (b) IT CAN ACTUALLY BLOCK, AND IT SENDS. ───
            if (prefix == null || prefix.ReturnType != typeof(bool))
                yield return "L99 buy-not-blocking: BuyCapturePatch has no bool-returning Prefix, so it cannot skip " +
                             "the native click. A non-blocking capture on this funnel is not a capture — the client " +
                             "would spend the shared wallet AND send the intent, applying the purchase twice";
            else if (!Closure(prefix).Any(m => m.DeclaringType == typeof(IntentRail) && m.Name == nameof(IntentRail.Send)))
                yield return "L99 buy-swallowed: BuyCapturePatch.Prefix never reaches IntentRail.Send — the client's " +
                             "click is blocked and NOTHING is sent, which is precisely the reported bug (\"I click to " +
                             "buy something and nothing happens\") wearing a refusal for a fix";

            // ─── (c) THE CLIENT CANNOT REACH THE WALLET. The law-3 arm. ───
            if (prefix != null)
                foreach (var forbidden in Closure(prefix)
                             .Where(m => m.DeclaringType == typeof(Wallet) ||
                                         (m.DeclaringType == typeof(GeoscapeEvent) &&
                                          m.Name == nameof(GeoscapeEvent.CompleteMarketplaceEvent)))
                             .Select(m => m.DeclaringType.Name + "." + m.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                    yield return "L99 client-reaches-wallet: the marketplace capture's own call closure reaches " +
                                 forbidden + " on the CLIENT side. The shared wallet and the reward are the host's " +
                                 "alone (law 3) — this is the GeoCharacter.RepairItem leak with a different funnel, " +
                                 "and it diverges every peer's money silently because the diff is host-now vs " +
                                 "host-before and never mentions a change only the client made";

            // ─── (d) NO PRICE CROSSES — IN EITHER DIRECTION. ───
            var writeFloat = typeof(BinaryWriter).GetMethod("Write", new[] { typeof(float) });
            var writeDouble = typeof(BinaryWriter).GetMethod("Write", new[] { typeof(double) });
            var readSingle = typeof(BinaryReader).GetMethod(nameof(BinaryReader.ReadSingle));
            if (CaptureClosure(capture).Any(m => Same(m, writeFloat) || Same(m, writeDouble)))
                yield return "L99 client-names-a-price: the purchase intent writes a float. The client sends an " +
                             "ADDRESS (event id + offer key; the row index left the wire with L166) and nothing " +
                             "else — the moment a price rides the wire, the buyer's screen decides what the shop " +
                             "charges. Where a key is ambiguous the host takes its own CHEAPEST matching row " +
                             "(MarketplaceSync.ResolveOffer), which is how the disambiguation stays off the wire";
            var handleBuy = typeof(MarketplaceSync).GetMethod("HandleBuy", AllMembers);
            if (handleBuy == null)
                yield return "L99 host-handler-gone: MarketplaceSync.HandleBuy no longer exists — nothing on the " +
                             "host executes a client's purchase, so buying is host-only again";
            else if (Closure(handleBuy).Any(m => Same(m, readSingle)))
                yield return "L99 host-trusts-a-price: MarketplaceSync.HandleBuy reads a float off the intent. The " +
                             "host re-derives the cost from ITS OWN row (PriceOf over the choice it holds); reading " +
                             "one means a client can name what it pays";

            // ─── (e) THE HOST VALIDATES AND GRANTS WITH THE GAME'S OWN METHODS. ───
            if (handleBuy != null)
            {
                var closure = Closure(handleBuy).ToList();
                if (!closure.Any(m => m.DeclaringType == typeof(GeoEventChoice) &&
                                      m.Name == nameof(GeoEventChoice.PassRequirements)))
                    yield return "L99 host-hand-rolls-eligibility: MarketplaceSync.HandleBuy never calls " +
                                 "GeoEventChoice.PassRequirements — the game's OWN test, the very predicate that " +
                                 "greys the native button out (SiteBaseChoicesController:60). Any other affordability " +
                                 "check is a second copy of the shop's rules, and the two will drift";
                if (!closure.Any(m => m.DeclaringType == typeof(GeoscapeEvent) &&
                                      m.Name == nameof(GeoscapeEvent.CompleteMarketplaceEvent)))
                    yield return "L99 host-hand-rolls-the-grant: MarketplaceSync.HandleBuy never calls " +
                                 "GeoscapeEvent.CompleteMarketplaceEvent — the shop's ONE native grant funnel " +
                                 "(GeoscapeEvent.cs:74). Granting the goods any other way re-implements " +
                                 "GenerateFactionReward and will hand out something the host's own click would not";
            }

            // ─── (f) THE ASSORTMENT IS ROLLED BY THE HOST AND MIRRORED, NEVER ROLLED TWICE. ───
            var updateOptions = typeof(GeoMarketplace).GetMethod("UpdateOptions", AllMembers, null, Type.EmptyTypes, null);
            if (updateOptions == null)
                yield return "L99 premise-changed: GeoMarketplace.UpdateOptions() (the no-arg roll) is gone — this " +
                             "law's whole reasoning is that ONE private method rolls the shop for every caller, and " +
                             "it can no longer prove the client's roll is blocked";
            else if (!PatchTargets(roll).Any(t => t.Type == typeof(GeoMarketplace) && t.Method == "UpdateOptions"))
                yield return "L99 offers-unowned: OfferRollPatch does not patch GeoMarketplace.UpdateOptions. " +
                             "Unblocked, every client re-rolls its own shop on its OWN sim clock " +
                             "(GeoLevelController:865) and after every mission (:756) — which is how two peers ended " +
                             "up shopping in two different stores under one campaign";
            if (rollPrefix == null || rollPrefix.ReturnType != typeof(bool))
                yield return "L99 client-rolls-its-own-shop: OfferRollPatch has no bool-returning Prefix, so a " +
                             "client's roll still runs. The offer list is EXCLUDED from the value rail " +
                             "(docs/rail-baseline.txt:14-18, bridge-unresolved), so nothing corrects a locally " +
                             "rolled shop afterwards — it just stays different";

            // ─── (g) EXECUTED: ONE OP FOR EVERY KIND OF GOODS. ───
            foreach (var v in OpCountArm()) yield return v;

            // ─── (h) A MIRRORED LIST REPAINTS THE OPEN SHOP (law 11). ───
            var apply = typeof(MarketplaceSync).GetMethod("ApplyOffers", AllMembers);
            var repaint = typeof(MarketplaceSync).GetMethod("RepaintOpenMarketplace", AllMembers);
            if (apply == null || repaint == null)
                yield return "L99 apply-gone: MarketplaceSync.ApplyOffers / RepaintOpenMarketplace no longer exist — " +
                             "the mirrored offer list has no applier and/or no repaint";
            else
            {
                if (!CallsMethod(apply, repaint))
                    yield return "L99 offers-apply-unpainted: ApplyOffers does not call RepaintOpenMarketplace. The " +
                                 "marketplace is a QUEUED window, which OpenUiRepaint deliberately refuses to " +
                                 "Exit+Enter (it would replay the presentation) and which has no UiNativeRepaint" +
                                 ".Table entry — so a peer standing in the shop keeps the OLD rows, including the one " +
                                 "somebody else just bought, until it reopens. That is law 11 with no other carrier";
                var showEncounter = typeof(PhoenixPoint.Geoscape.View.ViewModules.UIModuleTheMarketplace)
                    .GetMethod("ShowEncounter", AllMembers);
                if (showEncounter != null && CallsMethod(repaint, showEncounter))
                    yield return "L99 repaint-replays-the-shop: RepaintOpenMarketplace calls ShowEncounter, which " +
                                 "re-posts OpenEncounterSoundEvent (UIModuleTheMarketplace:151-154) — the shop's " +
                                 "opening narration on EVERY offer push. Repaint through UpdateVisuals + " +
                                 "SetEncounter, which rebuild the rows and nothing else";
            }
        }

        /// <summary>EXECUTED arm: register the real family and read the real op table. Reflection into
        /// IntentRail's own registry rather than a claim about the source, because the failure this guards
        /// against — "buyResearch(2), buyVehicle(3)" — is added in exactly the place a source-shaped
        /// assertion would keep passing.
        ///
        /// WHAT IS ASSERTED IS THE OP SET, NOT A COUNT. 0xBE stopped being the marketplace's private
        /// surface when haven trade joined it (the geoscape band 0xA0-0xBF is fully allocated, so a second
        /// SHARED-OFFER panel is an op and never a new surface), and a bare count could only be kept green
        /// by refusing the family its second rider. The declared set below is the family roster: every op
        /// on it names a DISTINCT native commit funnel — <c>CompleteMarketplaceEvent</c> for the shop,
        /// <c>GeoHaven.TradeResource</c> for the haven — and an op that is NOT on it is red no matter what
        /// it is called, which is exactly the "buyVehicle(3)" this arm was written for. Widening the roster
        /// is therefore a reviewed line here, in the law, next to the reason it is allowed.</summary>
        private static readonly byte[] DeclaredOps = { MarketplaceSync.OpBuy, TradeSync.OpTrade };

        private static IEnumerable<string> OpCountArm()
        {
            string violation = null;
            var ops = new List<byte>();
            try
            {
                MarketplaceSync.RegisterIntents();
                var families = typeof(IntentRail).GetField("_families", BindingFlags.NonPublic | BindingFlags.Static)
                                                 ?.GetValue(null) as System.Collections.IDictionary;
                if (families == null)
                    violation = "L99 premise-changed: IntentRail._families is not a readable dictionary, so the op " +
                                "table cannot be counted — the one-op-per-shop law is unprovable and must be restated";
                else
                {
                    var family = families[SurfaceIds.GeoMarketplaceIntent];
                    var opsDict = family?.GetType().GetField("Ops")?.GetValue(family) as System.Collections.IDictionary;
                    if (opsDict == null)
                        violation = "L99 family-unregistered: nothing is registered on surface 0xBE after " +
                                    "MarketplaceSync.RegisterIntents() — a client's purchase intent would reach the " +
                                    "host and be dropped as an unknown surface";
                    else foreach (byte k in opsDict.Keys) ops.Add(k);
                }
            }
            catch (Exception ex)
            {
                violation = "L99 family-unregisterable: MarketplaceSync.RegisterIntents() threw (" +
                            ex.GetType().Name + ": " + ex.Message + ") — the purchase family cannot be armed at all";
            }
            if (violation != null) { yield return violation; yield break; }
            var undeclared = ops.Where(o => !DeclaredOps.Contains(o)).OrderBy(o => o).ToList();
            var missing = DeclaredOps.Where(o => !ops.Contains(o)).OrderBy(o => o).ToList();
            if (undeclared.Count > 0)
                yield return "L99 op-per-goods-kind: the shared-offer family registers UNDECLARED op(s) [" +
                             string.Join(",", undeclared.Select(o => o.ToString())) + "] on 0xBE. The shop sells " +
                             "items, vehicles and researches through ONE GeoEventChoice and ONE native funnel, so " +
                             "ONE op covers the whole shop — an op per kind of goods is the per-subsystem macaroni " +
                             "factory the mandate exists to prevent. A genuinely new SHARED-OFFER PANEL (a second " +
                             "commit funnel, not a second kind of goods) is welcome here, but it is declared in " +
                             "L99.DeclaredOps with its funnel named, never merely registered";
            if (missing.Count > 0)
                yield return "L99 family-rider-missing: op(s) [" + string.Join(",", missing.Select(o => o.ToString())) +
                             "] are declared shared-offer riders but nothing is registered for them on 0xBE. That " +
                             "peer's commit reaches the host and is dropped as an unknown op — the click does " +
                             "nothing anywhere, which is law 91's silent no-op wearing a registration";
        }

        // ── Harmony targets, read off the attribute's own constructor arguments (L94's rule: a renamed
        //    Harmony-internal field must not silently turn an arm green). ──
        private static IEnumerable<(Type Type, string Method)> PatchTargets(Type t)
        {
            foreach (var cad in CustomAttributeData.GetCustomAttributes(t))
            {
                if (cad.AttributeType != typeof(HarmonyPatch)) continue;
                var a = cad.ConstructorArguments;
                if (a.Count >= 2 && a[0].Value is Type target && a[1].Value is string name)
                    yield return (target, name);
            }
        }

        /// <summary>Every method of the patch class AND its compiler-generated nested types — the intent body
        /// is written by a lambda, which lives in a display class, not in the prefix.</summary>
        private static IEnumerable<MethodBase> CaptureClosure(Type patchClass)
        {
            foreach (var t in new[] { patchClass }.Concat(patchClass.GetNestedTypes(AllMembers)))
                foreach (var m in SafeMethods(t))
                    foreach (var c in CalledBy(m))
                        yield return c;
        }

        /// <summary>Transitive call closure, DESCENDING ONLY into this file's own methods (and the display
        /// classes its lambdas compile into); everything else — game methods and the shared rail primitives
        /// — is REPORTED and not walked. Deliberate scope: the arms below are about what THIS file's client
        /// path can reach, and descending into IntentRail would drag the whole DiffEngine in behind it and
        /// turn arm (c) into noise about code that has its own laws.</summary>
        private static IEnumerable<MethodBase> Closure(MethodBase root)
        {
            var seen = new HashSet<MethodBase>();
            var queue = new Queue<MethodBase>();
            queue.Enqueue(root);
            seen.Add(root);
            while (queue.Count > 0)
            {
                foreach (var callee in CalledBy(queue.Dequeue()))
                {
                    if (!seen.Add(callee)) continue;
                    yield return callee;
                    if (IsOurs(callee.DeclaringType)) queue.Enqueue(callee);
                }
            }
        }

        /// <summary>Declared by MarketplaceSync itself, or nested anywhere inside it (patch classes, lambda
        /// display classes).</summary>
        private static bool IsOurs(Type t)
        {
            for (var cur = t; cur != null; cur = cur.DeclaringType)
                if (cur == typeof(MarketplaceSync)) return true;
            return false;
        }

        // ── IL probes, the flat token scan L94 already justified: the methods under test are short Harmony
        //    prefixes and handlers, and an unaligned hit resolves to nothing or to something unrelated. ──
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

        private static bool CallsMethod(MethodBase caller, MethodBase target) =>
            target != null && CalledBy(caller).Any(c => Same(c, target));

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static IEnumerable<MethodBase> SafeMethods(Type t)
        {
            try
            {
                return t.GetMethods(AllMembers).Cast<MethodBase>()
                        .Concat(t.GetConstructors(AllMembers).Cast<MethodBase>());
            }
            catch { return Enumerable.Empty<MethodBase>(); }
        }
    }
}
