using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L115 — STATE THAT SAYS "FINISHED" BEATS A LOCAL ANIMATION STILL PLAYING.
    ///
    /// THE REPORT (3 instances, 2026-08-05). A client flies to a POI and explores it. The exploration
    /// spinner fills on every peer, but the CLIENTS run roughly half a second behind the host; the host
    /// finishes, the event window opens on the outcome, the players close it — and on the clients the site
    /// is STILL drawn as unexplored. It never self-corrected.
    ///
    /// TWO INDEPENDENT CAUSES, one authoritative write. The host's completion is
    /// <c>GeoFaction.OnVehicleSiteExplored</c> → <c>GeoSite.SetInspected</c>:403, and it mirrors as the
    /// <c>FactionsData</c> leaf (GeoSite.cs:71 <c>_factionsData</c>; baseline row
    /// "EntityList FactionsData -> live _factionsData").
    ///
    ///   1. THE SPINNER OUTLIVED THE DONE. The fill is not mirrored at all — it is CLOSED-FORM on each peer
    ///      (<c>GeoActorProgressionVisualController.Progression</c>:25-35, recomputed in <c>Update</c>:53
    ///      every frame) off a PER-ACTOR epoch (<c>ActorComponent.Timing</c>:85), so the peers' clocks never
    ///      agree to the frame. <c>GenericApplier.ReseedExploration</c> ended it on <c>now &lt; end</c>
    ///      ALONE, so a client that already held "inspected = true" still had to wait out its own timer.
    ///      Worse, nothing even re-asked: <c>MarkOrderChange</c> only marked a <c>GeoVehicle</c> on an
    ///      <see cref="IsOrderLeafNames"/> leaf, and this completion lands on the SITE. Navigation never had
    ///      this bug precisely because ITS completion (<c>Travelling</c>/<c>CurrentSite</c>) lands on the
    ///      mover itself — the asymmetry IS the defect.
    ///   2. THE MARKER NEVER REPAINTED. The value rail writes <c>_factionsData</c> ELEMENTS directly, so it
    ///      never runs <c>SetInspected</c> and <c>SetProperty</c> never fires <c>PropertyChanged</c> — the
    ///      one signal <c>GeoSiteVisualsController</c> subscribes to (:152). Its <c>RefreshSiteVisuals</c>:239
    ///      picks the <c>Unknown</c> material for an un-inspected site and had no reason to re-run. That is
    ///      the "still shown as unexplored" half, and it is generic to <c>Visible</c>/<c>Raided</c> too.
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME — the visual a peer ends up with — not the calls:
    ///   (b) EXECUTES the decider case by case: an inspected site is NOT being explored even while the local
    ///       clock still says mid-flight. That single case is the whole user-visible fix.
    ///   (c) the decision is REACHED: a site-authority leaf marks the vehicles parked there, or (b) is a
    ///       correct answer nobody ever asks for.
    ///   (d) the marker is repainted through the game's OWN <c>Refresh</c>.
    ///   (e) the forced end is the game's own <c>EndExploreCurrentSite</c> — the very method the natural
    ///       completion <c>SiteExplorationCompleted</c>:473 calls — never a hand-rolled Destroy.
    ///   (a) holds the premise: the day the game stops deriving the fill locally, this law goes red and the
    ///       force becomes removable rather than quietly pointless.
    ///
    /// Falsify: drop the <c>inspected</c> short-circuit from <c>ShouldBeExploring</c> → <c>done-loses-to-clock</c>;
    /// stop marking sites in <c>MarkOrderChange</c> → <c>done-untriggered</c>; stop reaching
    /// <c>GeoSite.Vehicles</c> from <c>MarkSiteAuthority</c> → <c>parked-vehicle-unmarked</c>; delete the
    /// <c>GeoSiteVisualsController.Refresh</c> call → <c>marker-not-repainted</c>; end the spinner with
    /// <c>Object.Destroy</c> instead of <c>EndExploreCurrentSite</c> → <c>hand-rolled-teardown</c>; make the
    /// game mirror the fill instead of deriving it → <c>premise-fill-derived</c>.
    /// </summary>
    internal static class L115_AuthoritativeDoneBeatsAnimation
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The vehicle-side order leaves, named here only so the class doc's claim about the
        /// asymmetry ("navigation's completion lands on the mover") stays checkable.</summary>
        private static readonly string[] IsOrderLeafNames = { "DestinationSites", "Travelling", "CurrentSite" };

        internal static IEnumerable<string> Check(Assembly game)
        {
            var applier = typeof(GenericApplier);
            var mod = applier.Assembly;

            var decide = applier.GetMethod("ShouldBeExploring", All);
            var mark = applier.GetMethod("MarkOrderChange", All);
            var markSite = applier.GetMethod("MarkSiteAuthority", All);
            var flush = applier.GetMethod("FlushOrderReseed", All);
            var reseed = applier.GetMethod("ReseedExploration", All);
            var siteLeaf = applier.GetMethod("IsSiteAuthorityLeaf", All);

            var factionsData = typeof(GeoSite).GetField("_factionsData", All);
            var vehiclesProp = typeof(GeoSite).GetProperty("Vehicles", All)?.GetGetMethod(true);
            var refresh = typeof(GeoSiteVisualsController).GetMethod("Refresh", All);
            var endExplore = typeof(GeoVehicle).GetMethod("EndExploreCurrentSite", All);
            var completed = typeof(GeoVehicle).GetMethod("SiteExplorationCompleted", All);
            var progression = typeof(GeoActorProgressionVisualController).GetProperty("Progression", All)?.GetGetMethod(true);

            if (decide == null || mark == null || markSite == null || flush == null || reseed == null ||
                siteLeaf == null || factionsData == null || vehiclesProp == null || refresh == null ||
                endExplore == null || completed == null || progression == null)
            {
                yield return "L115 premise-changed: one of GenericApplier.{ShouldBeExploring,MarkOrderChange," +
                             "MarkSiteAuthority,FlushOrderReseed,ReseedExploration,IsSiteAuthorityLeaf} or " +
                             "GeoSite._factionsData / GeoSite.Vehicles / GeoSiteVisualsController.Refresh / " +
                             "GeoVehicle.EndExploreCurrentSite / SiteExplorationCompleted / " +
                             "GeoActorProgressionVisualController.Progression no longer resolves. The exploration " +
                             "presentation has moved and this law is asserting something about a shape neither the " +
                             "game nor the rail still has — re-read it before assuming the spinner is still forced.";
                yield break;
            }

            // ── (a) premise: the fill really is derived locally, so there IS a local animation to beat ──
            // Progression must read the controller's own clock — if the game ever ships the fill as state,
            // there is nothing to force and this whole law is dead weight rather than subtly wrong.
            if (!Program.Callees(progression, game).Any(c => c.DeclaringType == typeof(Timing)) &&
                !progression.GetMethodBody().LocalVariables.Any(l => l.LocalType == typeof(TimeUnit)))
                yield return "L115 premise-fill-derived: GeoActorProgressionVisualController.Progression no longer " +
                             "computes off a clock, so the exploration fill is not a locally-derived animation any " +
                             "more. Forcing it to completion is then either pointless or actively wrong — re-read " +
                             "the seam instead of keeping this force alive on an assumption the game dropped.";

            // ── (b) THE OUTCOME, executed case by case ──────────────────────
            // Values are plain TimeUnits: "started at hour 1, takes 10 hours, it is now hour 5" — i.e. the
            // exact mid-flight moment the user measured, when host and client disagree.
            var t1 = TimeUnit.FromHours(1f);
            var ten = TimeUnit.FromHours(10f);
            var now5 = TimeUnit.FromHours(5f);
            var now99 = TimeUnit.FromHours(99f);
            Func<bool, bool, TimeUnit, TimeUnit, TimeUnit, bool> ask = (travel, inspected, start, len, now) =>
                (bool)decide.Invoke(null, new object[] { travel, inspected, start, len, now });

            // THE FIX ITSELF: authoritative done, local clock still mid-fill → NOT exploring.
            if (ask(false, true, t1, ten, now5))
                yield return "L115 done-loses-to-clock: ShouldBeExploring still answers TRUE for a site the host " +
                             "has already marked inspected, purely because this peer's own clock has not reached " +
                             "the end yet. That is the reported bug exactly — the client's spinner keeps filling " +
                             "for the ~0.5 s its per-actor epoch lags the host, and the site stays drawn " +
                             "unexplored. Authoritative state must short-circuit the clock, never race it.";
            // …and the ordinary mid-exploration case must survive, or the force becomes a spinner that never runs.
            if (!ask(false, false, t1, ten, now5))
                yield return "L115 fill-never-runs: ShouldBeExploring answers FALSE mid-exploration on an " +
                             "un-inspected site. The done-beats-clock short-circuit has swallowed the normal case " +
                             "and no peer would draw the progress bar at all — the opposite failure, equally broken.";
            // the clock still ends it when no authoritative word has arrived (host disconnect, dropped leaf)
            if (ask(false, false, t1, ten, now99))
                yield return "L115 clock-no-longer-ends: ShouldBeExploring answers TRUE past the derived end with " +
                             "no inspected flag, so a peer that never receives the site delta would spin forever. " +
                             "The clock is the BACKSTOP, not the thing that was removed.";
            // travel still outranks everything (unchanged behaviour, kept honest)
            if (ask(true, false, t1, ten, now5))
                yield return "L115 travel-ignored: ShouldBeExploring answers TRUE while the mirrored order holds a " +
                             "destination. A vehicle in flight is not exploring, and the host ending an exploration " +
                             "early (StartTravel:524/:538, TeleportToSite:506) is obeyed through exactly this test.";

            // ── (c) the decision is REACHED at all ─────────────────────────
            if (!Program.Callees(mark, mod).Any(c => c.MetadataToken == markSite.MetadataToken && c.Module == markSite.Module))
                yield return "L115 done-untriggered: GenericApplier.MarkOrderChange no longer reaches " +
                             "MarkSiteAuthority, so an authoritative write on a GeoSite marks nothing and " +
                             "ReseedExploration is never re-asked. The decider in (b) would be correct and never " +
                             "consulted — which is precisely the state this bug was found in: navigation " +
                             "self-corrects because its completion lands on the mover, exploration's lands on the site.";
            if (!(bool)siteLeaf.Invoke(null, new object[] { "FactionsData" }))
                yield return "L115 leaf-misnamed: IsSiteAuthorityLeaf does not recognise 'FactionsData' — the leaf " +
                             "GeoSite.SetInspected:403 actually mutates (_factionsData, GeoSite.cs:71). A trigger " +
                             "keyed on a name the rail never ships fires never, and silently.";

            // ── (d) + (e) what the peer ENDS UP looking at ─────────────────
            if (!Program.Callees(markSite, game).Any(c => c.MetadataToken == vehiclesProp.MetadataToken && c.Module == vehiclesProp.Module))
                yield return "L115 parked-vehicle-unmarked: MarkSiteAuthority does not ask GeoSite.Vehicles:239, so " +
                             "it cannot know which aircraft is presenting the spinner at that site and marks none " +
                             "of them. The site repaint would land while the spinner over it kept running.";
            if (!Program.Callees(flush, game).Any(c => c.MetadataToken == refresh.MetadataToken && c.Module == refresh.Module))
                yield return "L115 marker-not-repainted: FlushOrderReseed does not call " +
                             "GeoSiteVisualsController.Refresh:202. The value rail writes _factionsData elements " +
                             "directly, so SetProperty never fires PropertyChanged and the controller has no other " +
                             "reason to re-run RefreshSiteVisuals — the site keeps the Unknown material and reads " +
                             "as UNEXPLORED forever, which is the half of the report that outlived the event window.";
            // ponytail: the end goes through a CACHED MethodInfo (the game's halves are private), so a callee
            // walk sees only MethodBase.Invoke. Assert the cached handle itself — that is the thing that
            // decides what actually runs — plus the absence of the hand-rolled alternative.
            var cachedEnd = applier.GetField("EndExploreCurrentSiteMethod", All)?.GetValue(null) as MethodBase;
            var cachedStart = applier.GetField("ExploreCurrentSiteMethod", All)?.GetValue(null) as MethodBase;
            if (cachedEnd == null || cachedEnd.MetadataToken != endExplore.MetadataToken || cachedEnd.Module != endExplore.Module)
                yield return "L115 hand-rolled-teardown: GenericApplier's cached exploration-end handle is " +
                             (cachedEnd == null ? "null" : cachedEnd.DeclaringType?.Name + "." + cachedEnd.Name) +
                             ", not GeoVehicle.EndExploreCurrentSite:460 — the SAME method the natural completion " +
                             "SiteExplorationCompleted:473 calls. Any other teardown (a Destroy on " +
                             "_explorationVisuals, a hidden renderer) ends the animation without ending the game's " +
                             "own updateable, and law 11 requires the repaint to come from the decompile, never " +
                             "from a hand-rolled swap.";
            if (cachedStart == null || cachedStart.DeclaringType != typeof(GeoVehicle) || cachedStart.Name != "ExploreCurrentSite")
                yield return "L115 reseed-start-rehomed: GenericApplier's cached exploration-start handle is not " +
                             "GeoVehicle.ExploreCurrentSite:448, so the spinner this law forces to completion is " +
                             "no longer the one the game's own save-restore (ProcessInstanceData:1126) creates.";
            if (Program.Callees(reseed, typeof(UnityEngine.Object).Assembly)
                       .Any(c => c.DeclaringType == typeof(UnityEngine.Object) && c.Name == "Destroy"))
                yield return "L115 hand-rolled-teardown: ReseedExploration calls UnityEngine.Object.Destroy " +
                             "directly. Killing the visuals behind the game's back leaves _explorationUpdateable " +
                             "running — the model would still say IsExploringSite while nothing is drawn, which " +
                             "is the same desync wearing the opposite face.";
            if (!Program.Callees(completed, game).Any(c => c.MetadataToken == endExplore.MetadataToken && c.Module == endExplore.Module))
                yield return "L115 premise-completion-moved: GeoVehicle.SiteExplorationCompleted no longer calls " +
                             "EndExploreCurrentSite, so forcing that method is no longer equivalent to the game " +
                             "finishing the exploration naturally. The force would end the visuals down a path the " +
                             "native completion does not take.";
        }
    }
}
