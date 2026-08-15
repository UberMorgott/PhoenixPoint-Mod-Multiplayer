using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace;
using Base.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE PEER'S RECOMPUTE ENGINE (L557) — the general defect behind "the manufacture row is missing".
    ///
    /// WHAT WAS REPORTED, AND WHAT IT ACTUALLY WAS. The geoscape agenda strip's manufacture row was built
    /// correctly on every peer — the diag proved identical row signatures on host and client, faction
    /// bound — and then DIED every tick. <c>UIModuleFactionAgendaTracker.UpdateData</c> disposes any
    /// element whose remaining time is <c>&lt;= TimeUnit.Zero</c> (dispose loop :191-199 over the
    /// per-element test :271-309), and <c>TimeUnit.Invalid</c> is <c>TimeSpan.MinValue</c> — so an
    /// UNCOMPUTABLE duration is also "already elapsed" and the row is destroyed and rebuilt forever.
    /// <c>ItemManufacturing.GetTotalTime</c> (:405-417) returns exactly that <c>Invalid</c> when
    /// <c>_faction.ResourceIncome.GetTotalResouce(Production).Value &lt;= 0f</c>. The client's production
    /// income was zero.
    ///
    /// THE CLASS — AND THE PREMISE THAT HAD TO BE CORRECTED. The first reading blamed
    /// <see cref="ClientSimGate"/> skipping the hourly tick. It does skip it, but that is NOT what stales
    /// this rollup: <c>GeoFaction.UpdateProduction</c> (GeoFaction.cs:694-701) is not called by the tick at
    /// all. The tick only CONSUMES the result (<c>ResourceIncome.Apply(Wallet)</c>,
    /// GeoLevelController.cs:795). <c>UpdateProduction</c> is driven by an EVENT —
    /// <c>site.ProductionChanged</c> (subscribed GeoFaction.cs:676, handler :1774-1777). The real defect
    /// is one rung more general, and it is a property of the rail itself:
    ///
    ///     THE RAIL WRITES MODEL STATE AS FIELDS. A native change EVENT therefore NEVER FIRES ON A PEER.
    ///     Every stored aggregate whose only refresh is such an event is frozen at its last local value —
    ///     usually the zero it was constructed with — and every value derived from it silently degrades.
    ///
    /// That is not a manufacture problem or a production problem. This file's sibling
    /// <see cref="OpenUiRepaint"/> already recorded the same shape one surface over (:624-631: the
    /// top-right strip's diplomacy percentages "come from a stored field ... which the rail writes
    /// DIRECTLY, so none of the module's native subscriptions ever fire on a client") — but it fixed the
    /// PAINT, because there the value was fine and only the repaint was missing. Here the VALUE is wrong,
    /// so repainting it faster paints zero faster.
    ///
    /// TWO CLASSES, AND EVERY MEMBER IS ONE OF THEM (<see cref="Kind"/>):
    ///   • <see cref="Kind.Recompute"/> — a PURE ROLLUP over state the rail already mirrors. It mints no
    ///     information, so re-running it on the peer is exact by construction. The engine does exactly
    ///     that, when the rail touches an input the row declares.
    ///   • <see cref="Kind.Carried"/> — the handler mints information the peer cannot derive (an RNG
    ///     roll, a host-only decision) or advances the simulation. It must NOT run here; its result
    ///     travels as ordinary rail state.
    ///
    /// CARRY OR RECOMPUTE — WHY RECOMPUTE WINS WHEREVER IT APPLIES. A rollup is a FUNCTION of bytes the
    /// rail already ships. Carrying it too would put a second source of truth for one fact on the wire —
    /// the divergence law 6 exists to forbid — and would owe a new codec per aggregate forever. Re-running
    /// the GAME'S OWN handler costs zero wire and cannot drift from the game's definition of the rollup.
    /// Carrying is right only where an input is genuinely NOT mirrored, and such a row cannot be declared
    /// <see cref="Kind.Recompute"/> here at all: it has no mirrored inputs to name.
    ///
    /// TOTAL, NOT A LIST. <see cref="Table"/> is not "the aggregates somebody remembered". L557 DERIVES
    /// the set from <c>IdentityResolver.RootKinds</c> — the rail's own declaration of what it writes — by
    /// reading, out of the shipped game assembly, every handler those types subscribe to their own model
    /// events. Every member of that derived set must be classified here. One that is not is RED in the
    /// harness and <see cref="Unclassified"/> in the log — never silent, which is the runtime half for the
    /// case that only exists at runtime (another mod patching a handler in).
    ///
    /// NO QUORUM. <see cref="ClientTick"/> reads this peer's own role and this peer's own touched-path
    /// set and recomputes locally. Nothing waits on a peer or on a human.
    /// </summary>
    public static class DerivedAggregateRefresh
    {
        /// <summary>What a native model-change handler DOES, and therefore what a peer owes it.</summary>
        internal enum Kind
        {
            /// <summary>A pure rollup over state the rail already mirrors. Re-run it on the peer.</summary>
            Recompute,
            /// <summary>It mints information the peer cannot derive, or advances the simulation. Never run
            /// it here; its result arrives as ordinary rail state.</summary>
            Carried,
        }

        internal sealed class Row
        {
            internal readonly Type Owner;
            /// <summary>The native EVENT HANDLER — the thing the rail's field writes stop from ever
            /// firing, and the name L557's derivation produces. It is NOT what gets invoked: a handler
            /// takes the event's arguments, which a peer has no business inventing.</summary>
            internal readonly string Handler;
            /// <summary>The PARAMETERLESS rebuild the handler exists to call
            /// (<c>OnSiteProductionChanged</c> → <c>UpdateProduction</c>), which is what this engine
            /// actually invokes. Parameterless is the whole safety argument: a method that takes no
            /// argument is a pure function of state the object already holds, so re-running it on a peer
            /// invents nothing. Null for <see cref="Kind.Carried"/>.</summary>
            internal readonly string Rebuild;
            internal readonly Kind Class;
            /// <summary>Rail path prefixes of the INPUTS this rollup reads (<c>IdentityResolver</c> root
            /// keys). Arming is BY INPUT, so a batch that touched nothing it reads costs nothing. Empty
            /// for <see cref="Kind.Carried"/>: a carried row is never armed here at all.</summary>
            internal readonly string[] InputPrefixes;
            /// <summary>Why, in one line. The prose is not the claim — L557 re-derives the claim — but a
            /// row nobody can review is a row that rots.</summary>
            internal readonly string Reason;

            internal Row(Type owner, string handler, Kind kind, string rebuild, string[] inputs, string reason)
            {
                Owner = owner; Handler = handler; Class = kind; Rebuild = rebuild;
                InputPrefixes = inputs ?? new string[0]; Reason = reason;
            }
        }

        private static Row Recompute(Type owner, string handler, string rebuild, string[] inputs, string why) =>
            new Row(owner, handler, Kind.Recompute, rebuild, inputs, why);

        private static Row Carried(Type owner, string handler, string why) =>
            new Row(owner, handler, Kind.Carried, null, new string[0], why);

        /// <summary>
        /// THE CLASSIFICATION — every handler L557's derivation produces, and nothing else. Each verdict
        /// was read out of the decompile rather than inferred from the name, and each carries the
        /// file:line that decided it, because a name is a very poor guide here: <c>UpdateStats</c> sounds
        /// like the purest rollup in the set and is CARRIED, since it drops captured units through
        /// <c>GetRandomElement()</c>.
        ///
        /// THE BAR FOR <see cref="Kind.Recompute"/> IS DELIBERATELY HIGH. Anything that mints an entity or
        /// an id, rolls RNG, reads the clock, gives or takes from a wallet, reveals a site, or raises a
        /// further game event is CARRIED — even when a rollup is most of what it does. Re-running such a
        /// handler on a peer is the client-side simulation <see cref="ClientSimGate"/> exists to refuse,
        /// and getting that wrong is far worse than a missed refresh: a missed refresh shows a stale
        /// number, a wrong Recompute DIVERGES the campaign.
        /// </summary>
        internal static readonly Row[] Table =
        {
            // ── RECOMPUTE — pure rollups over state this peer already mirrors ────────────────────────
            // THE REPORTED DEFECT. GeoFaction.cs:1774-1777 → :694-701 is `from s in Sites select
            // s.SiteProduction` → ResourceIncome.SetOutput. Nothing else. Sites are rail roots ("S#"),
            // so every input is already here and the rebuild is exact.
            Recompute(typeof(GeoFaction), "OnSiteProductionChanged", "UpdateProduction", new[] { "S#", "F#" },
                      "GeoFaction.cs:694-701 — pure Sites->SiteProduction rollup into ResourceIncome; the " +
                      "aggregate ItemManufacturing.GetTotalTime:405-417 turns into TimeUnit.Invalid without"),
            // The soldier sheet's own re-derivation. GeoCharacter.cs:711-717 and :725-728 are each exactly
            // `UpdateStats()`, and :1187-1212 only re-derives tags and stats from progression, items and
            // fatigue — all of it rail state on "U#". Corruption is set with triggerStatChangeEvent:false
            // (:1212), so the rebuild raises no stat event of its own.
            Recompute(typeof(GeoCharacter), "OnAbilityAdded", "UpdateStats", new[] { "U#" },
                      "GeoCharacter.cs:711-717 — body is UpdateStats(); :1187-1212 re-derives tags/stats " +
                      "from progression, items and fatigue only"),
            Recompute(typeof(GeoCharacter), "OnHungerDaysChanged", "UpdateStats", new[] { "U#" },
                      "GeoCharacter.cs:725-728 — body is UpdateStats(), same pure re-derivation"),
            // View-only, and still in this table for the same reason the others are: the rail writes the
            // site's tags as fields, so the site's own visuals never re-derive on a peer.
            Recompute(typeof(GeoSite), "GameTags_Changed", "RefreshVisuals", new[] { "S#" },
                      "GeoSite.cs:1047-1050 — body is RefreshVisuals() -> _visuals.Refresh() (:915-918); " +
                      "re-derives the site's own visuals from its model, writes no model state"),

            // ── CARRIED — host-minted or simulation-advancing; the result arrives as rail state ───────
            Carried(typeof(GeoAlienFaction), "AlienBase_StateChanged",
                    "GeoAlienFaction.cs:1186,1190 — unsubscribes StateChanged and adds evolution progress"),
            Carried(typeof(GeoAlienFaction), "OnHavenInfestedStateChanged",
                    "GeoAlienFaction.cs:930,935 — destroys a vehicle and raises behemoth disruption"),
            Carried(typeof(GeoCharacter), "OnNewSpecializationAdded",
                    "GeoCharacter.cs:666-667 — drives achievement tracking and PhoenixStatisticsManager"),
            Carried(typeof(GeoFaction), "<OnAfterFactionsLevelStart>b__279_1",
                    "GeoFaction.cs:496-500 — mints a new SiteAttackSchedule and reveals sites"),
            Carried(typeof(GeoFaction), "OnCharacterAdded",
                    "GeoFaction.cs:1712-1718 — reassigns character.Faction and raises CharacterAdded"),
            Carried(typeof(GeoFaction), "OnGenerateSiteAssaultMission",
                    "GeoFaction.cs:927,931 — creates base/ancient-site assault missions"),
            Carried(typeof(GeoFaction), "OnResearchCompleted",
                    "GeoFaction.cs:831-832 — raises the completion event and mutates allied factions"),
            Carried(typeof(GeoFaction), "OnSiteAdded",
                    "GeoFaction.cs:943 — mints a new SiteAttackSchedule"),
            Carried(typeof(GeoFaction), "OnSiteStateChanged",
                    "GeoFaction.cs:1785,1789 — INCREMENTAL _havens add/remove; there is no parameterless " +
                    "rebuild of that list to re-run, so it has to travel as state"),
            Carried(typeof(GeoFaction), "OnVehicleArrived",
                    "GeoFaction.cs:1880-1898 — SetInspected, Refill, engages aircraft, raises VehicleArrived"),
            Carried(typeof(GeoLevelController), "<LaunchInterceptionGame>b__246_0",
                    "GeoLevelController.cs:1210-1214 — tactical interception-game lifecycle teardown"),
            Carried(typeof(GeoMarketplace), "OnCurrentObjectiveCompleted",
                    "GeoMarketplace.cs:199 -> :205,:223 — rerolls options with Random.Range and the clock"),
            Carried(typeof(GeoMarketplace), "OnSiteVisited",
                    "GeoMarketplace.cs:163-165 — sets an event variable and pushes a cutscene state"),
            Carried(typeof(GeoMarketplace), "OnVehicleArrived",
                    "GeoMarketplace.cs:173-175 — sets an event variable and pushes a cutscene state"),
            Carried(typeof(GeoPhoenixFaction), "HarvestSite_OnResourceHarvested",
                    "GeoPhoenixFaction.cs:1894-1895 — Wallet.Give, an authoritative resource grant"),
            Carried(typeof(GeoPhoenixFaction), "OnGameTagsChanged",
                    "GeoPhoenixFaction.cs:1742 -> :1733 — reveals non-functional bases"),
            Carried(typeof(GeoPhoenixFaction), "OnScannerRevealAbilitiesUpdated",
                    "GeoPhoenixFaction.cs:1999 -> GeoPhoenixBase.cs:671 — reveals hidden Pandoran bases"),
            Carried(typeof(GeoPhoenixFaction), "OnSiteStateChanged",
                    "GeoPhoenixFaction.cs:1832-1839 — uninits the site and reassigns site.Owner"),
            Carried(typeof(GeoPhoenixFaction), "OnStorageChanged",
                    "GeoPhoenixFaction.cs:1429 -> :1586 — raises StorageFullChanged"),
            Carried(typeof(GeoPhoenixFaction), "OnVehicleAdded",
                    "GeoPhoenixFaction.cs:1390-1394 -> :1768 — adds abilities to the vehicle"),
            Carried(typeof(GeoPhoenixFaction), "OnVehicleVisitedSite",
                    "GeoPhoenixFaction.cs:1402-1411 — creates an ancient-site mission and reloads equipment"),
            // The trap in this table: the name says rollup, the body drops captured units at RANDOM.
            Carried(typeof(GeoPhoenixFaction), "UpdateStats",
                    "GeoPhoenixFaction.cs:1456,1469 — TrimCapturedUnitsToCapacity drops units through " +
                    "GetRandomElement() (:1596) and fires OnStatsChanged; the capacity SUM it also does is " +
                    "not separable from the RNG, so the whole handler is host-only"),
            Carried(typeof(GeoscapeEventSystem), "<OnLevelStart>b__37_1",
                    "GeoscapeEventSystem.cs:142 — mints event records stamped with the clock"),
            Carried(typeof(GeoscapeEventSystem), "Faction_ResearchCompleted",
                    "GeoscapeEventSystem.cs:490 — RaiseEvent drives encounter spawning"),
            Carried(typeof(GeoscapeEventSystem), "Faction_VehicleVisitedSite",
                    "GeoscapeEventSystem.cs:417 — RaiseEvent(SiteVisited)"),
            Carried(typeof(GeoscapeEventSystem), "GeoscapeEventSystem_VariableSet",
                    "GeoscapeEventSystem.cs:514 — RaiseEvent(VariableChanged)"),
            Carried(typeof(GeoscapeEventSystem), "Geoscape_OnHourTicked",
                    "GeoscapeEventSystem.cs:520 — RaiseEvent(TimePassed), a clock-driven sim advance"),
            Carried(typeof(GeoscapeEventSystem), "OnBehemothInteractedHaven",
                    "GeoscapeEventSystem.cs:477 — RaiseEvent(BehemothDestroyedHaven)"),
            Carried(typeof(GeoscapeEventSystem), "PhoenixFaction_OnSiteFirstTimeVisited",
                    "GeoscapeEventSystem.cs:440-452 — Random.Range rolls, mints EncounterID, creates an ambush"),
            Carried(typeof(Timing), "OnParentReset",
                    "Timing.cs:366-372 — rewrites clock baselines and re-raises the reset down the tree"),
        };

        /// <summary>Is this handler classified? Pure and internal so RailCheck and RailSim execute the
        /// real decision with no live level.</summary>
        internal static Row Classify(Type owner, string handler)
        {
            if (owner == null || handler == null) return null;
            foreach (var row in Table)
                if (string.Equals(row.Handler, handler, StringComparison.Ordinal) &&
                    row.Owner != null && row.Owner.IsAssignableFrom(owner))
                    return row;
            return null;
        }

        /// <summary>Does this batch's touched-path set arm this row? Same prefix semantics, and the same
        /// CONSERVATIVE DIRECTION, as <see cref="OpenUiRepaint.SurfaceRepaints"/>: an unknown path arms,
        /// an undeclared row arms. Erring toward a recompute costs one pure rollup; erring the other way
        /// is the stale zero this file exists to remove.</summary>
        internal static bool Arms(Row row, ICollection<string> touchedPaths)
        {
            if (row == null || row.Class != Kind.Recompute) return false;
            if (touchedPaths == null || touchedPaths.Count == 0) return false;
            if (row.InputPrefixes.Length == 0) return true;              // undeclared inputs = arm
            foreach (var path in touchedPaths)
            {
                if (path == null) return true;                          // unknown path = arm
                foreach (var prefix in row.InputPrefixes)
                    if (prefix != null && path.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        /// <summary>
        /// THE PEER-SIDE ENTRY. Runs once per rail frame, on a client, BEFORE
        /// <see cref="OpenUiRepaint.FlushIfDirty"/> — that ordering is the whole reactivity story: the
        /// batch's touched paths are still known here, so an armed rollup is rebuilt and the flush one
        /// line later paints the CORRECTED value, in the SAME frame, into whatever strip is already open.
        /// No screen re-entry, no reload (P11).
        ///
        /// NOT A SIM STEP, and that is why it may run on the peer that <see cref="ClientSimGate"/> refuses
        /// everything else to: every method invoked here is PARAMETERLESS, i.e. a pure function of state
        /// this peer already holds. It mints no information, so it cannot diverge from the host.
        ///
        /// NO QUORUM: this peer's own role, this peer's own touched paths, this peer's own model.
        /// </summary>
        public static void ClientTick(NetworkEngine engine)
        {
            if (Table.Length == 0) return;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            var geo = GenericApplier.GeoLevel();
            if (geo == null) return;
            var touched = OpenUiRepaint.TouchedPaths;
            bool rebuilt = false;
            foreach (var row in Table)
            {
                if (!Arms(row, touched)) continue;
                foreach (var target in Targets(geo, row))
                {
                    var m = target.GetType().GetMethod(row.Rebuild, Any);
                    var args = DefaultArgs(m);
                    if (args == null)
                    {
                        Unclassified(row.Owner.Name, row.Rebuild);
                        continue;
                    }
                    try
                    {
                        // law 8: the rebuild re-reads the model and can raise native events a capture
                        // seam would otherwise hear and ship back as this peer's own gesture.
                        using (SyncApplyScope.Enter()) m.Invoke(target, args);
                        rebuilt = true;
                    }
                    catch (Exception ex)
                    {
                        if (_announced.Add("throw:" + row.Owner.Name + "." + row.Rebuild))
                            MpLog.LogError("[Multiplayer][rail] " + row.Owner.Name + "." + row.Rebuild +
                                           " threw while rebuilding a rollup this peer's rail staled: " +
                                           ex.Message + " (logged once)");
                    }
                }
            }
            // The correction is only half a fix until it is on screen (P11). One pathless mark: a rebuilt
            // rollup feeds durations and capacities across every strip, and the row's own input prefixes
            // describe what it READ, not what it wrote — so there is no narrower honest claim to make.
            if (rebuilt) OpenUiRepaint.MarkDirty();
        }

        /// <summary>Which live objects does this row rebuild? Resolved from the level, by the row's owner
        /// type. A row whose owner nothing here can reach is ANNOUNCED rather than skipped — an
        /// unreachable rollup is the stale number with a table entry in front of it, which is worse than
        /// no table entry at all because it reads as covered.</summary>
        private static IEnumerable<object> Targets(GeoLevelController geo, Row row)
        {
            if (typeof(GeoFaction).IsAssignableFrom(row.Owner))
            {
                foreach (var faction in geo.Factions)
                    if (row.Owner.IsInstanceOfType(faction)) yield return faction;
                yield break;
            }
            // Sites and characters hang off the factions (GeoFaction.cs:135 / :205); the level has no flat
            // list of either, so the faction sweep IS the enumeration. Distinct is unnecessary — a site
            // has one owner and a character one faction.
            if (typeof(GeoSite).IsAssignableFrom(row.Owner))
            {
                foreach (var faction in geo.Factions)
                    foreach (var site in faction.Sites)
                        if (row.Owner.IsInstanceOfType(site)) yield return site;
                yield break;
            }
            if (typeof(GeoCharacter).IsAssignableFrom(row.Owner))
            {
                foreach (var faction in geo.Factions)
                    foreach (var character in faction.Characters)
                        if (row.Owner.IsInstanceOfType(character)) yield return character;
                yield break;
            }
            if (row.Owner.IsInstanceOfType(geo)) { yield return geo; yield break; }
            if (geo.Marketplace != null && row.Owner.IsInstanceOfType(geo.Marketplace))
            { yield return geo.Marketplace; yield break; }
            Unclassified(row.Owner.Name, row.Rebuild);
        }

        /// <summary>The argument list for a rebuild, or NULL if this method may not be driven from here.
        /// The bar is NOT "takes no parameters" but the thing that bar was standing in for: THE CALLER
        /// SUPPLIES NO INFORMATION. A parameter with a default is the GAME'S OWN answer, not ours, so
        /// <c>GeoCharacter.UpdateStats(bool recalculateBodparts = false)</c> qualifies while any method
        /// with a required argument does not — a required argument is a value this peer would have to
        /// invent, and an invented input is exactly the divergence the whole engine avoids.</summary>
        private static object[] DefaultArgs(MethodInfo m)
        {
            if (m == null) return null;
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (!ps[i].HasDefaultValue) return null;
                args[i] = ps[i].DefaultValue;
            }
            return args;
        }

        private static readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Law 1 (never a silent swallow): a model-change handler this engine cannot classify is
        /// ANNOUNCED, once, by name.</summary>
        internal static void Unclassified(string owner, string method)
        {
            if (owner == null || method == null) return;
            if (!_announced.Add(owner + "." + method)) return;
            MpLog.LogWarning("[Multiplayer][rail] " + owner + "." + method + " refreshes state from a native " +
                             "model event that this peer's rail writes as a FIELD, so it never fires here, and " +
                             "it is not classified in DerivedAggregateRefresh.Table — if it rebuilds a stored " +
                             "aggregate, every value derived from it is stale on this peer (logged once per " +
                             "handler; please report)");
        }
    }
}
