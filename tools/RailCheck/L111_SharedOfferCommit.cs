using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L111 — THE SHARED-OFFER FAMILY: A COMMIT BY ANY PEER CONSUMES THE OFFER FOR ALL OF THEM, AND NO
    /// COMMIT PATH READS HOW MANY AIRCRAFT ARE PARKED.
    ///
    /// A SHARED-OFFER panel is any screen where a finite stock the peers SHARE is consumed by one peer's
    /// commit — the Kaos marketplace assortment, a haven's tradeable resources, a haven's recruit, the base
    /// Recruits tab. They are ONE family, not four screens, because they share one failure shape: the
    /// clicking peer's client runs the exchange against its OWN mirror, the host's state never moves, and
    /// everyone else keeps looking at stock that is already gone. The user's words for it: "people sit in
    /// the trade screen, one of them trades, and nothing changes for the other."
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME, per rider, never the patch:
    ///   (a) NO PEER COMMITS LOCALLY — every declared commit FUNNEL (the model method, never a screen) is
    ///       captured by a prefix that can BLOCK (returns bool), asks <c>IntentRail.ShouldRunNative</c> so
    ///       it blocks only on a client, and SENDS. A block with no send is a button that does nothing
    ///       anywhere (law 91); a send with no block is the divergence doubled (L110's arm (d), same shape).
    ///   (b) THE HOST REPLAYS NATIVELY AND REFUSES OUT LOUD — the host handler reaches the game's own
    ///       funnel (so the consumption is REAL host state, which is the only thing the rail can ship to
    ///       everyone) and reaches <c>IntentRail.Reject</c> (so a commit that LOSES the race is refused
    ///       loudly instead of vanishing — a silent no-op is indistinguishable from a phantom purchase).
    ///   (c) THE OPEN PANEL REPAINTS ON ARRIVAL — the panel is either a <c>UiNativeRepaint.Table</c> citizen
    ///       (its own native rebuild) or its <c>EnterState</c> RE-READS the stock from the model, which is
    ///       what makes the universal Exit+Enter repaint a repaint rather than a no-op. A screen that
    ///       snapshots its stock at construction is the "correct on next open" this law forbids.
    ///   (d) THE VEHICLE COUNT IS OUT OF THE PURCHASE PATH — <c>GeoscapeEvent.CompleteMarketplaceEvent</c>
    ///       picks the aircraft with <c>Enumerable.SingleOrDefault</c>, which THROWS at two, so a second
    ///       aircraft parked at the marketplace broke every purchase for everybody including the host. The
    ///       arm runs the real transpiler over a real <c>SingleOrDefault</c> call and then RUNS the
    ///       replacement pick at 0/1/2/3 vehicles in shuffled order: total, and order-independent.
    ///
    /// PEER MEMBERSHIP is deliberately NOT re-asserted here — L91's arms (b)/(c) already sweep the whole
    /// <c>Multiplayer.Network.Sync</c> namespace for a decision or a field taking a peer COLLECTION, and a
    /// second copy of that detector is the two-tables-disagree shape this repo keeps paying for. Arm (e)
    /// below proves the delegation is not vacuous: every family type must actually live in a namespace L91
    /// sweeps.
    ///
    /// ponytail: arms (a)/(b) walk IL with Program's own walker, so an unparseable callee UNDER-reports —
    /// the failure mode is a missed red, never a false one.
    /// </summary>
    internal static class L111_SharedOfferCommit
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>THE FAMILY ROSTER. Joining is a line here plus the wiring this law then demands — that
        /// is what "a declaration, not new plumbing" means in this repo. <c>HostHandler</c>/<c>Panel</c> are
        /// null for the two personnel riders on purpose: their host replay and their screens are
        /// <see cref="PersonnelSync"/>'s arc and are asserted by its own laws; what THIS law owns for them
        /// is the one invariant the whole family shares — nobody commits shared stock locally.</summary>
        private sealed class Rider
        {
            public string Name;
            public MethodBase Funnel;          // the game's own model funnel a commit must pass
            public Type Capture;               // our Harmony class sitting on it
            public MethodBase HostHandler;     // the host's replay of a client's intent
            public Type Panel;                 // the screen a peer can be sitting in while it happens
            public MethodBase PanelModelRead;  // what that screen's EnterState re-reads (null = Table citizen)
        }

        private static Rider[] Roster() => new[]
        {
            new Rider
            {
                Name = "kaos marketplace",
                Funnel = AccessTools.Method(typeof(TheMarketplaceChoicesController),
                                            nameof(TheMarketplaceChoicesController.OnButtonChoiceSelected)),
                Capture = typeof(MarketplaceSync.BuyCapturePatch),
                HostHandler = AccessTools.Method(typeof(MarketplaceSync), "HandleBuy"),
                Panel = typeof(UIStateMarketplaceGeoscapeEvent),
                PanelModelRead = null,   // a UiNativeRepaint.Table citizen (a queued window, so it must be)
            },
            new Rider
            {
                Name = "haven resource trade",
                Funnel = AccessTools.Method(typeof(GeoHaven), nameof(GeoHaven.TradeResource)),
                Capture = typeof(TradeSync.TradeCapturePatch),
                HostHandler = AccessTools.Method(typeof(TradeSync), "HandleTrade"),
                Panel = typeof(UIStateTrade),
                // UIStateTrade.EnterState:39 re-Inits the module from GetResourceTradingData() — the same
                // reseed the game's own ConfirmTrade:78 does after a trade. That read is what makes the
                // universal re-enter a real repaint, so the fallback needs no Table entry here.
                PanelModelRead = AccessTools.Method(typeof(GeoHaven), nameof(GeoHaven.GetResourceTradingData)),
            },
            new Rider
            {
                Name = "haven recruit",
                Funnel = AccessTools.Method(typeof(GeoHaven), nameof(GeoHaven.TakeRecruit)),
                Capture = typeof(PersonnelSync.HavenHireCapturePatch),
            },
            new Rider
            {
                Name = "base recruits tab",
                Funnel = AccessTools.Method(typeof(GeoPhoenixFaction), nameof(GeoPhoenixFaction.HireNakedRecruit)),
                Capture = typeof(PersonnelSync.NakedHireCapturePatch),
            },
        };

        internal static IEnumerable<string> Check()
        {
            var riders = Roster();
            var railAsm = typeof(MarketplaceSync).Assembly;

            foreach (var rider in riders)
            {
                if (rider.Funnel == null || rider.Capture == null)
                {
                    yield return "L111 rider-premise-changed: the '" + rider.Name + "' commit funnel or its " +
                                 "capture class no longer resolves. A shared-offer panel whose funnel moved is a " +
                                 "panel every peer can now commit locally again — re-find the funnel before " +
                                 "assuming this rider is still covered";
                    continue;
                }

                // ── (a) the capture sits ON the funnel, blocks, and sends ──
                var target = CustomAttributeData.GetCustomAttributes(rider.Capture)
                    .Where(c => c.AttributeType == typeof(HarmonyPatch))
                    .SelectMany(c => c.ConstructorArguments)
                    .ToList();
                bool onFunnel =
                    target.Any(a => a.Value as Type == rider.Funnel.DeclaringType) &&
                    (target.Any(a => (a.Value as string) == rider.Funnel.Name) || target.Count == 1);
                if (!onFunnel)
                    yield return "L111 capture-off-the-funnel [" + rider.Name + "]: the capture class does not " +
                                 "patch " + rider.Funnel.DeclaringType.Name + "." + rider.Funnel.Name + ". A seam " +
                                 "anywhere else covers the caller it wraps and no other — GeoHaven.TradeResource " +
                                 "alone has TWO gesture callers (UIStateTrade.ConfirmTrade:67 and " +
                                 "HavenInteractionController:210), and a seam on either one leaks the other";

                var prefix = rider.Capture.GetMethod("Prefix", All);
                if (prefix == null)
                {
                    yield return "L111 capture-has-no-prefix [" + rider.Name + "]: " + rider.Capture.Name +
                                 " declares no Prefix, so nothing stops a client running the game's own " +
                                 "exchange against its own mirror. The stock moves on ONE screen and the host " +
                                 "never hears about it";
                    continue;
                }
                if (prefix.ReturnType != typeof(bool))
                    yield return "L111 capture-cannot-block [" + rider.Name + "]: Prefix does not return bool, so " +
                                 "it cannot stop the native commit. The client would emit the intent AND consume " +
                                 "the stock locally — the divergence doubled rather than fixed";
                var calls = Program.Callees(prefix, railAsm).ToList();
                if (!calls.Any(c => c.DeclaringType == typeof(IntentRail) && c.Name == "ShouldRunNative"))
                    yield return "L111 capture-unconditional [" + rider.Name + "]: Prefix does not ask " +
                                 "IntentRail.ShouldRunNative, so it blocks on the HOST and in SOLO too — the one " +
                                 "peer allowed to run the game's own commit would stop doing it, and the offer " +
                                 "would never be consumed by anybody";
                if (!calls.Any(c => c.DeclaringType == typeof(IntentRail) && c.Name == "Send"))
                    yield return "L111 capture-silent [" + rider.Name + "]: Prefix does not call IntentRail.Send. " +
                                 "A block with no intent is a commit button that does nothing for anybody (law " +
                                 "91) and it looks exactly like a working screen — the phantom this family exists " +
                                 "to kill";

                if (rider.HostHandler == null) continue;

                // ── (b) the host replays the NATIVE funnel, and refuses out loud ──
                var handlerCalls = Program.Callees(rider.HostHandler, railAsm).ToList();
                var nativeCalls = Program.Callees(rider.HostHandler, rider.Funnel.Module.Assembly).ToList();
                if (!nativeCalls.Any(c => c.DeclaringType == rider.Funnel.DeclaringType &&
                                          c.Name == rider.Funnel.Name) &&
                    !ReachesNativeCommit(rider, nativeCalls))
                    yield return "L111 host-does-not-replay [" + rider.Name + "]: the host handler never reaches " +
                                 "the game's own commit. Whatever it does instead is a SECOND implementation of " +
                                 "the exchange, and only real host state can be diffed to the other peers — a " +
                                 "hand-rolled consumption is absent everywhere but the host";
                // TWO hops, not one: every handler in this repo routes its refusals through a private
                // one-line Reject helper, and matching a method merely NAMED "Reject" would go green for a
                // helper that had been gutted to a log line. The closure ends at IntentRail.Reject or it
                // does not end.
                var reachable = handlerCalls
                    .Concat(handlerCalls.SelectMany(c => Program.Callees(c, railAsm)))
                    .ToList();
                if (!reachable.Any(c => c.DeclaringType == typeof(IntentRail) && c.Name == "Reject"))
                    yield return "L111 race-loss-is-silent [" + rider.Name + "]: the host handler never reaches " +
                                 "IntentRail.Reject. A commit that arrives after another peer took the last of " +
                                 "the stock then returns nothing at all: the clicking peer's screen keeps the " +
                                 "offer, its wallet is never corrected, and no line says why";

                // ── (c) the open panel repaints on arrival ──
                if (rider.Panel == null) continue;
                bool tableCitizen = UiNativeRepaint.Table.ContainsKey(rider.Panel);
                if (rider.PanelModelRead == null)
                {
                    if (!tableCitizen)
                        yield return "L111 panel-has-no-repaint [" + rider.Name + "]: " + rider.Panel.Name +
                                     " is declared a UiNativeRepaint.Table citizen and is not in the table. It is " +
                                     "a QUEUED window, which the universal repaint refuses to Exit+Enter (that " +
                                     "would replay the presentation), so a table entry is the ONLY repaint it can " +
                                     "ever have — without it the consumed offer stays on every other peer's screen";
                }
                else
                {
                    var enter = rider.Panel.GetMethod("EnterState", All);
                    if (enter == null)
                        yield return "L111 panel-premise-changed [" + rider.Name + "]: " + rider.Panel.Name +
                                     " has no EnterState, so whether the universal re-enter repaints it is " +
                                     "unprovable — re-derive this screen's repaint before trusting it";
                    else if (!tableCitizen &&
                             !Program.Callees(enter, rider.PanelModelRead.Module.Assembly)
                                     .Any(c => c.DeclaringType == rider.PanelModelRead.DeclaringType &&
                                               c.Name == rider.PanelModelRead.Name))
                        yield return "L111 panel-snapshots-its-stock [" + rider.Name + "]: " + rider.Panel.Name +
                                     ".EnterState no longer reads " + rider.PanelModelRead.DeclaringType.Name +
                                     "." + rider.PanelModelRead.Name + ", and the screen is not a " +
                                     "UiNativeRepaint.Table citizen either. The universal Exit+Enter would then " +
                                     "re-enter a screen that paints a stock it captured earlier — a repaint that " +
                                     "repaints nothing, which is 'correct on next open' with extra steps";
                }
            }

            // ── (d) the vehicle COUNT is out of the purchase path ──
            foreach (var v in VehiclePickArm()) yield return v;

            // ── (e) the peer-membership delegation to L91 is not vacuous ──
            foreach (var rider in riders)
            {
                var t = rider.Capture;
                if (t == null) continue;
                var ns = (t.DeclaringType ?? t).Namespace;
                if (ns != "Multiplayer.Network.Sync" && ns != "Multiplayer.Tactical")
                    yield return "L111 family-outside-L91-sweep [" + rider.Name + "]: " + t.Name + " lives in '" +
                                 ns + "', which L91's RailNamespaces does not sweep. This law delegates the " +
                                 "no-peer-membership half to L91's arms (b)/(c); a rider outside that sweep is a " +
                                 "commit path where nobody is checking that a host decision about one peer stays " +
                                 "blind to the others (law 91)";
            }
        }

        /// <summary>The marketplace's host handler reaches the native commit through
        /// <c>GeoscapeEvent.CompleteMarketplaceEvent</c>, not through the CAPTURED funnel (which is a UI
        /// controller — the host never re-plays a client's button press, it re-derives the purchase). The
        /// rider's declared funnel is the CAPTURE point; this names the COMMIT point for the one rider
        /// where they differ.</summary>
        private static bool ReachesNativeCommit(Rider rider, List<MethodBase> nativeCalls) =>
            rider.Name == "kaos marketplace" &&
            nativeCalls.Any(c => c.DeclaringType == typeof(GeoscapeEvent) &&
                                 c.Name == nameof(GeoscapeEvent.CompleteMarketplaceEvent));

        private static IEnumerable<string> VehiclePickArm()
        {
            var complete = AccessTools.Method(typeof(GeoscapeEvent),
                                              nameof(GeoscapeEvent.CompleteMarketplaceEvent));
            var pick = AccessTools.Method(typeof(MarketplaceSync.VehiclePickPatch), "PickVehicle");
            var transpiler = AccessTools.Method(typeof(MarketplaceSync.VehiclePickPatch), "Transpiler");
            var single = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SingleOrDefault" && m.GetParameters().Length == 1 &&
                                     m.IsGenericMethodDefinition)
                ?.MakeGenericMethod(typeof(GeoVehicle));
            if (complete == null || pick == null || transpiler == null || single == null)
            {
                yield return "L111 pick-premise-changed: GeoscapeEvent.CompleteMarketplaceEvent, " +
                             "MarketplaceSync.VehiclePickPatch.PickVehicle/Transpiler or " +
                             "Enumerable.SingleOrDefault<GeoVehicle> no longer resolves. Whether a second " +
                             "aircraft at the marketplace still throws is now unproven";
                yield break;
            }

            // PREMISE: the game really does pick the aircraft with a count-sensitive call. If it stopped,
            // the transpiler matches nothing and this law is asserting something about vanished IL.
            int singles = Program.Callees(complete, typeof(Enumerable).Assembly)
                                 .Count(c => c.DeclaringType == typeof(Enumerable) && c.Name == "SingleOrDefault");
            if (singles != 1)
                yield return "L111 pick-premise-changed: CompleteMarketplaceEvent contains " + singles +
                             " Enumerable.SingleOrDefault call(s), not the 1 the transpiler is written for. " +
                             "Either the game stopped picking the aircraft that way (delete this arm) or it " +
                             "grew a second count-sensitive pick the transpiler will silently half-cover";

            // THE SWAP, run for real: the transpiler is a pure IEnumerable→IEnumerable, so it is testable
            // as one. Anything else here would be asserting the patch instead of the outcome.
            List<CodeInstruction> outp = null;
            string threw = null;
            try
            {
                var input = new List<CodeInstruction>
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, single),
                    new CodeInstruction(OpCodes.Ret),
                };
                outp = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { input })).ToList();
            }
            catch (Exception ex) { threw = ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message; }
            if (threw != null)
                yield return "L111 pick-transpiler-throws: the vehicle-pick transpiler threw on a minimal " +
                             "instruction stream (" + threw + "). A throwing transpiler leaves the ORIGINAL " +
                             "method in place — the single-aircraft assumption stays live and the patch looks " +
                             "applied";
            else
            {
                if (outp.Any(ci => ci.operand is MethodInfo mi && mi.DeclaringType == typeof(Enumerable) &&
                                   mi.Name == "SingleOrDefault"))
                    yield return "L111 vehicle-count-still-read: the transpiler left Enumerable.SingleOrDefault " +
                                 "in the purchase path. It throws at two aircraft, so the number of vehicles " +
                                 "parked at the marketplace still decides whether ANY peer can buy anything — on " +
                                 "the host's own click too";
                if (!outp.Any(ci => ci.operand is MethodInfo mi && mi.MetadataToken == pick.MetadataToken &&
                                    mi.Module == pick.Module))
                    yield return "L111 pick-not-substituted: the transpiler emitted no call to " +
                                 "VehiclePickPatch.PickVehicle. Whatever it did instead, the deterministic pick " +
                                 "is not in the purchase path and two peers can resolve the same purchase " +
                                 "against different aircraft";
            }

            // THE PICK ITSELF: total at every count, and INDEPENDENT OF LIST ORDER. Order is the whole
            // point — GeoSite.Vehicles:239 is GeoMap.Vehicles filtered by a Where, so its order is an
            // artefact of creation order, and "first by chance" is a different aircraft on every peer.
            object[] byOrder;
            string pickThrew = null;
            try
            {
                byOrder = new[] { 0, 1, 2, 3 }.Select(n =>
                {
                    var vs = Enumerable.Range(0, n).Select(i => Vehicle(100 - i * 7)).ToList();
                    var forward = pick.Invoke(null, new object[] { vs });
                    var reversed = pick.Invoke(null, new object[] { Enumerable.Reverse(vs).ToList() });
                    return (object)(ReferenceEquals(forward, reversed) ? forward : "DIVERGED@" + n);
                }).ToArray();
            }
            catch (Exception ex)
            {
                byOrder = null;
                pickThrew = ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message;
            }
            if (pickThrew != null)
                yield return "L111 pick-throws: PickVehicle threw at 0-3 vehicles (" + pickThrew + "). The whole " +
                             "reason it exists is that the game's own pick throws at two — a replacement that " +
                             "also throws swapped one crash for another";
            else if (byOrder.Any(o => o as string != null && ((string)o).StartsWith("DIVERGED")))
                yield return "L111 pick-order-dependent: PickVehicle returned a DIFFERENT aircraft for the same " +
                             "set in reversed order (" + string.Join(",", byOrder.Select(o => o as string ?? "ok")) +
                             "). Two peers enumerate GeoSite.Vehicles in creation order, which is not shared " +
                             "state — an order-dependent pick applies the same purchase to two different aircraft";
        }

        /// <summary>A GeoVehicle with nothing but its id — the pick reads only VehicleID, and a real one
        /// needs a live Unity scene the harness has no business building.</summary>
        private static GeoVehicle Vehicle(int id)
        {
            var v = (GeoVehicle)FormatterServices.GetUninitializedObject(typeof(GeoVehicle));
            typeof(GeoVehicle).GetField("VehicleID", All).SetValue(v, id);
            return v;
        }
    }
}
