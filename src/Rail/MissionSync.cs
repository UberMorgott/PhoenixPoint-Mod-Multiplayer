using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// MISSION-LAUNCH gesture family (surface 0xB8, law 1) — the "squad INTENT" that
    /// <see cref="GeoWindowCoverage"/>'s <c>UIStateRosterDeployment</c> declaration named as the missing
    /// piece: "shared deployment (all peers pick from one roster, host commits)". Without it a client
    /// could open the deployment screen and press Deploy, and the click died silently — the native chain
    /// ends in <c>GeoLevelController.LaunchTacticalGame</c>, which <c>TacticalEntry.TacLaunchGate</c>
    /// BLOCKS on a client by construction (the battle is built once on the host and shipped to every peer
    /// as a mid-tactical save, law 1). The screen closed back to the plain geoscape and the mission never
    /// started for anybody.
    ///
    /// ONE op, at the ONE model funnel: <c>GeoMission.Launch(GeoSquad)</c> (GeoMission.cs:226). Every
    /// route into a battle bottoms out there and the funnel is what makes this generic rather than a
    /// per-screen patch — <c>UIStateRosterDeployment.DeploySquad</c>:331 (the deployment screen's own
    /// button), <c>GeoscapeView.LaunchMission</c>:1043 (its SkipDeploymentSelection / SkipDeploymentScreen
    /// arm, which never opens a screen at all), <c>HavenFacilityController</c>:149,
    /// <c>HavenInteractionController</c>:226, <c>UIModuleSiteEncounters</c>:612 and
    /// <c>StealAircraftAbility</c>:93 all reach it. Capture is block-first
    /// (<see cref="IntentRail.ShouldRunNative"/>): the client never runs <c>PrepareTacticalGame</c>, never
    /// stamps <c>GlobalTime</c> and never rolls <c>GenerateMissionThreatLevel</c> — all three are
    /// authoritative and all three ride back as ordinary state.
    ///
    /// THE WIRE CARRIES IDENTITY ONLY: the mission's SITE root key plus the chosen soldiers' root keys
    /// ("U#&lt;GeoTacUnitId&gt;", IdentityResolver.cs:146). NOT the mission — a client's <c>GeoMission</c>
    /// is a structural mirror of the host's own (log: <c>structural create 'S#388…ActiveMission'</c>), and
    /// the host reads <c>site.ActiveMission</c> off its OWN graph rather than trusting a reference that
    /// could name a mission it already cancelled. NOT the squad object either: <c>GeoSquad.Units</c> holds
    /// live <c>GeoCharacter</c> refs (GeoSquad.cs:11), so the host rebuilds the squad from ITS instances.
    ///
    /// The peer that clicked keeps its deployment screen up and simply waits: the host's launch curtains
    /// every peer through <c>SaveTransferCoordinator</c> within the round trip, and that teardown takes
    /// the screen with it. A REJECT leaves the screen live and closable by its own Back button — which is
    /// why <see cref="MissionCancelGate"/> exists next to this.
    /// </summary>
    public static class MissionSync
    {
        internal const byte OpLaunch = 1;  // [siteRef][n:u16][charRef × n]

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoMissionIntent, "mission",
                new Dictionary<byte, IntentRail.OpHandler> { [OpLaunch] = HandleLaunch });
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        private static void Reject(ulong peer, string siteRef, string why) =>
            IntentRail.Reject(SurfaceIds.GeoMissionIntent, peer, (siteRef ?? "S#?") + " — " + why,
                              string.IsNullOrEmpty(siteRef) ? null : siteRef);

        // ─── THE ONE VALIDATOR (pure — host facts only, law 3) ──────────────

        /// <summary>Every fact the launch is allowed to be checked against, all of it read off the HOST's
        /// own graph: a client mirror can be arbitrarily stale, so each gate the game itself applies is
        /// repeated here rather than trusted from the wire.</summary>
        internal struct Facts
        {
            internal bool SiteResolved;      // the site root key resolved to a live GeoSite
            internal bool MissionRunnable;   // site.ActiveMission exists and GeoMission.IsRunnable (LaunchMissionAbility:34)
            internal int UnitsRequested;     // how many soldier refs the wire carried
            internal int UnitsResolved;      // how many of them named a live GeoCharacter on the host
            internal bool AllOwnedByPlayer;  // every resolved unit belongs to the shared player faction
            internal bool HasStandalone;     // at least one IsTacticalStandaloneActor (LaunchMissionAbility:38)
            internal int Volume;             // sum of OccupingSpace (UIStateRosterDeployment.CheckForDeployment:372)
            internal int MaxUnits;           // MissionDef.MaxPlayerUnits (:374-375)
            internal int VehicleCount;       // vehicles + mutogs in the squad (:373, refused at 2+ by :376)
        }

        /// <summary>null = accept, otherwise the human reason the launch was refused. Never blank — a
        /// silently eaten Deploy click is the exact bug this family exists to kill.</summary>
        internal static string Validate(Facts f)
        {
            if (!f.SiteResolved) return "no such site on the host — stale mirror";
            if (!f.MissionRunnable)
                return "that site has no runnable mission any more (already launched, cancelled, or expired)";
            if (f.UnitsRequested == 0) return "the squad is empty — nothing to deploy";
            if (f.UnitsResolved != f.UnitsRequested)
                return "only " + f.UnitsResolved + " of " + f.UnitsRequested + " chosen soldiers exist on the host — " +
                       "stale roster (dismissed, dead, or another peer moved them)";
            if (!f.AllOwnedByPlayer)
                return "a chosen soldier is not on the shared player faction — only Phoenix soldiers deploy from a peer";
            if (!f.HasStandalone)
                return "no soldier in the squad can stand on the battlefield by itself (NotEnoughSoldiersForMission)";
            if (f.Volume > f.MaxUnits)
                return "the squad takes " + f.Volume + " deployment slots but the mission allows " + f.MaxUnits;
            if (f.VehicleCount > 1)
                return "two vehicles/mutogs in one squad — the deployment screen refuses that (:376)";
            return null;
        }

        // ─── CLIENT: the capture seam (law 4a), block-first ─────────────────

        /// <summary>Capture at the MODEL funnel. <c>Launch</c> is a single non-virtual method with one
        /// signature, so one patch covers every caller; the optional parameter is named exactly as the
        /// game names it, since Harmony binds by name.</summary>
        [HarmonyPatch(typeof(GeoMission), nameof(GeoMission.Launch))]
        internal static class LaunchCapturePatch
        {
            private static bool Prefix(GeoMission __instance, GeoSquad squad) => CaptureLaunch(__instance, squad);
        }

        private static bool CaptureLaunch(GeoMission mission, GeoSquad squad)
        {
            if (IntentRail.ShouldRunNative()) return true;
            string siteRef = null;
            try
            {
                siteRef = IdentityResolver.RootRef(mission?.Site);
                // Launch's own default: a null argument means "use the squad the mission already holds"
                // (GeoMission.cs:227-235), so read it the same way rather than refusing a legal call.
                var units = (squad ?? mission?.Squad)?.Units;
                if (siteRef == null || units == null || units.Count == 0)
                {
                    Debug.LogWarning("[MP][mission] CLIENT launch DROPPED — " +
                                     (siteRef == null ? "the mission's site has no rail identity" : "no squad to deploy") +
                                     "; nothing was sent and nothing ran locally");
                    OpenUiRepaint.MarkDirty();
                    return false;
                }
                var refs = new List<string>(units.Count);
                foreach (var c in units)
                {
                    var r = IdentityResolver.RootRef(c);
                    if (r == null)
                    {
                        Debug.LogWarning("[MP][mission] CLIENT launch DROPPED — soldier '" +
                                         (c == null ? "<null>" : c.DisplayName) + "' has no rail identity, so the host " +
                                         "cannot be told who to deploy; reconverging the open screen");
                        OpenUiRepaint.MarkDirty();
                        return false;
                    }
                    refs.Add(r);
                }
                IntentRail.Send(SurfaceIds.GeoMissionIntent, OpLaunch,
                    "launch " + siteRef + " squad=" + refs.Count,
                    w =>
                    {
                        w.Write(siteRef);
                        w.Write((ushort)refs.Count);
                        foreach (var r in refs) w.Write(r);
                    });
            }
            catch (Exception ex)
            {
                // Nothing ran and nothing shipped: no delta will ever repaint the launch the screen already
                // drew, so reconverge from the un-mutated local model exactly as the reject path does.
                Debug.LogError("[MP][mission] CLIENT launch capture failed for " + (siteRef ?? "S#?") +
                               " — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }

        // ─── HOST: the applier (decode/dedup/reject discipline = IntentRail) ─────

        private static void HandleLaunch(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string siteRef = null;
            try
            {
                siteRef = r.ReadString();
                int n = r.ReadUInt16();
                var refs = new List<string>(n);
                for (int i = 0; i < n; i++) refs.Add(r.ReadString());

                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, siteRef, "no geoscape"); return; }

                var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                var mission = site?.ActiveMission;           // the HOST's own mission, never a wire reference
                var units = refs.Select(x => IdentityResolver.Resolve(geo, x, null) as GeoCharacter)
                                .Where(c => c != null).ToList();

                string why = Validate(new Facts
                {
                    SiteResolved = site != null,
                    MissionRunnable = mission != null && mission.IsRunnable,
                    UnitsRequested = n,
                    UnitsResolved = units.Count,
                    AllOwnedByPlayer = units.All(c => ReferenceEquals(c.Faction, geo.PhoenixFaction)),
                    HasStandalone = units.Any(c => c.TemplateDef != null && c.TemplateDef.IsTacticalStandaloneActor),
                    Volume = units.Sum(c => c.OccupingSpace),
                    MaxUnits = mission?.MissionDef == null ? 0 : mission.MissionDef.MaxPlayerUnits,
                    VehicleCount = units.Count(c => c.TemplateDef != null &&
                                                    (c.TemplateDef.IsVehicle || c.TemplateDef.IsMutog)),
                });
                if (why != null) { Reject(senderPeerId, siteRef, "launch: " + why); return; }

                // The host runs the SAME native method the client's click was blocked from, with a squad
                // built out of the host's OWN instances (GeoSquad.cs:23). Everything it produces —
                // GlobalTime, the threat roll, the tactical level itself — is host-computed by construction,
                // and every peer joins through TacticalEntry's save transfer, not through this call.
                mission.Launch(new GeoSquad(units));
                Debug.Log("[MP][mission] HOST intent APPLIED op=launch " + siteRef + " squad=" + units.Count +
                          " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, siteRef, "launch (throw) " + ex.Message); }
        }
    }

    /// <summary>
    /// PRESENTATION seam (law 4c) at the encounter that STARTS a mission — the piece without which
    /// <see cref="MissionSync"/> was unreachable on a client, and the reason the 0xB8 family never once
    /// fired in the 2026-08-01 run: the client never got as far as a deployment screen, so nothing ever
    /// called <c>GeoMission.Launch</c> for the capture to see.
    ///
    /// The native route from "answer the mission encounter" to the deployment screen is ONE call inside
    /// <c>UIModuleSiteEncounters.SelectChoice</c>:598-613 — <c>ev.CompleteEvent(...)</c>, then
    /// <c>ChoiceReward.ApplyResult.StartMission</c>, then <c>FinishEncounter()</c> +
    /// <c>Context.View.LaunchMission(startMission, _geoEvent.Context.Vehicle)</c>:612. On a CLIENT the
    /// whole method is unreachable: <c>EventPopup.EventChoiceClientLock</c> blocks
    /// <c>OnChoiceSelected</c> and turns the click into a 0xB4 answer intent, and even if it did run,
    /// <c>EventSync.EventCompleteArbiter</c> skips <c>CompleteEvent</c> and hands back the empty
    /// <c>ChoiceReward</c> stub, so <c>StartMission</c> is null and <c>SelectChoice</c> returns false.
    /// The client therefore fell into the :586-596 arm — result page, OK, back to the plain geoscape —
    /// which is exactly the reported symptom: no briefing, no soldier assignment, host-only launches.
    ///
    /// That lost call is pure LOCAL NAVIGATION, not game logic: <c>LaunchMission</c> either opens
    /// <c>UIStateRosterDeployment</c> (declared LocalOnly in <see cref="GeoWindowCoverage"/> — every peer
    /// navigates its own) or, in the SkipDeploymentSelection arm, calls <c>mission.Launch</c>, which is
    /// the capture above. So the client may simply re-issue it, and every peer holding the window gets the
    /// same screen the host gets. Two peers may deploy the same mission at once; the first 0xB8 to reach
    /// the host wins and the second is refused by <see cref="Validate"/>'s "no runnable mission any more"
    /// arm — law 5's first-to-act-wins, no ownership table.
    ///
    /// Hooked at <c>FinishEncounter</c> (:618) rather than at the click, because that is the one point
    /// BOTH native arms reach and, more importantly, the point by which the host's answer has come back:
    /// the client's result page is drawn from the arriving record delta (<c>EventPopup</c>'s "resolved by
    /// THIS peer's own click"), and the mission rides in as the <c>ActiveMission</c> structural create in
    /// that same stream. The mission is read off the SITE, never off the reward — the client's reward is
    /// the stub — and the choice is read from the HOST's record index, since <c>GeoscapeEvent
    /// .SelectedChoice</c> is only written inside the <c>CompleteEvent</c> the client skipped.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleSiteEncounters), "FinishEncounter")]
    internal static class MissionEncounterNav
    {
        private static readonly FieldInfo GeoEventField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_geoEvent");

        private static void Postfix(UIModuleSiteEncounters __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return; // solo/host ran SelectChoice natively
            try
            {
                var ev = GeoEventField?.GetValue(__instance) as GeoscapeEvent;
                var choices = ev?.EventData?.Choices;
                int idx = ev?.Record == null ? -1 : ev.Record.SelectedChoice;
                if (choices == null || idx < 0 || idx >= choices.Count) return;
                // Unity default-constructs an EMPTY OutcomeStartMission, so non-null is not the signal —
                // MissionTypeDef is (GeoEventChoiceOutcome:315, same test EventPopup.StartsMission uses).
                if (choices[idx].Outcome?.StartMission?.MissionTypeDef == null) return;

                var site = ev.Context?.Site;
                var mission = site?.ActiveMission;
                if (mission == null || !mission.IsRunnable)
                {
                    // Never silent: the choice DID start a mission, so a missing one means the structural
                    // create has not landed yet and this peer is about to sit on a geoscape with no screen.
                    Debug.LogWarning("[MP][mission] CLIENT deployment for '" + ev.EventID + "' at " +
                                     (IdentityResolver.RootRef(site) ?? "S#?") + " NOT opened — the host's mission " +
                                     "has not arrived on this peer yet (ActiveMission " +
                                     (mission == null ? "missing" : "not runnable") + "); reach it from the " +
                                     "aircraft's Launch button once it lands");
                    return;
                }

                __instance.Context.View.LaunchMission(mission, ev.Context.Vehicle);
                Debug.Log("[MP][mission] CLIENT deployment screen opened for '" + ev.EventID + "' at " +
                          (IdentityResolver.RootRef(site) ?? "S#?") + " — re-issuing the LaunchMission call " +
                          "SelectChoice:612 could not make here; the squad picked rides out as a 0xB8 launch intent");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][mission] CLIENT deployment navigation failed — this peer stays on the " +
                               "geoscape and can still reach the mission from the aircraft's Launch button: " + ex);
            }
        }
    }

    /// <summary>
    /// Sim gating (law 4b) at the mission-CANCEL funnel, the sibling of the launch capture and the reason
    /// a client may now sit on the deployment screen at all. <c>UIStateRosterDeployment.ToPreviousScreen</c>
    /// (:256-268) — the Back button, the Close button and the Cancel key alike — calls
    /// <c>_mission.Cancel()</c> before it pops the screen, and <c>GeoMission.Cancel</c> (GeoMission.cs:253)
    /// writes <c>Site.ActiveMission = null</c> and can <c>Site.DestroySite()</c>. On a projector client
    /// (law 3) that is a structural mutation the diff can NEVER correct — the diff is host-now vs
    /// host-before, so a mission only the client deleted is never mentioned again and the CRC backstop can
    /// only report it (DiffEngine.HandleCrcReport's "still diverged" arm). One peer glancing at the
    /// deployment screen and pressing Back would delete the mission from its own campaign.
    ///
    /// Blocking is also the RIGHT co-op semantic, not merely the safe one: backing out of a screen is a
    /// navigation gesture, and one peer declining to launch must not cancel the mission for the others.
    /// A host that really means to cancel still does, natively, and it reaches every client as the
    /// ordinary structural destroy.
    ///
    /// Discovered, not enumerated: <c>Cancel</c> is VIRTUAL with seven overrides in the shipped assembly
    /// (GeoCustomMission, GeoAncientSiteMission, GeoPhoenixBaseDefenseMission, GeoSabotageZoneMission and
    /// the three Steal* missions), and several do not call <c>base.Cancel()</c> — so a prefix on the base
    /// declaration alone would leave most real missions ungated. Sweeping the type hierarchy covers a DLC's
    /// or a mod's own subclass for free, the same way <c>VehicleGestureGate</c> resolves its rows by name.
    /// Void method; nothing dereferences a blocked call.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionCancelGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary><c>AccessTools.GetTypesFromAssembly</c> and not <c>Assembly.GetTypes()</c>: with other
        /// mods loaded the game assembly reliably has a few types that fail to load, and the raw call
        /// throws <c>ReflectionTypeLoadException</c> — which PatchAll turns into one swallowed warning that
        /// kills every LATER patch in the same pass (RailCheck L23, the same trap an unbound
        /// AccessTools.Method sets). Harmony's helper returns the loadable ones instead.</summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in AccessTools.GetTypesFromAssembly(typeof(GeoMission).Assembly))
            {
                if (t == null || !typeof(GeoMission).IsAssignableFrom(t)) continue;
                var m = t.GetMethod("Cancel", BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                    null, Type.EmptyTypes, null);
                if (m != null && !m.IsAbstract) yield return m;
            }
        }

        private static bool Prefix(GeoMission __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            if (SyncApplyScope.Active) return true;                                      // an apply may reach it

            // Never silent (the dominant bug class): say whose cancel was refused and why. Log-once per
            // site — cancelling is a per-mission gesture, not a per-frame one.
            string who = IdentityResolver.RootRef(__instance?.Site) ?? "S#?";
            if (_logged.Add(who))
                Debug.Log("[MP][mission] CLIENT cancel of the mission at " + who + " BLOCKED — it deletes shared " +
                          "campaign state (Site.ActiveMission/DestroySite) the host owns; backing out of the " +
                          "deployment screen is navigation, not a cancellation for every peer");
            return false;
        }
    }
}
