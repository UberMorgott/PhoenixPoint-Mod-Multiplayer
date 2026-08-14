using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Entities;
using HarmonyLib;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels.ActorDeployment;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE 8-SOLDIER DEPLOYMENT CAP, OPTIONALLY LIFTED — one value, read by BOTH places that enforce it.
    ///
    /// Vanilla ships <c>TacMissionTypeDef.MaxPlayerUnits = 8</c> (TacMissionTypeDef.cs:66) and enforces it in
    /// exactly one place, the UI: <c>UIStateRosterDeployment._squadMaxDeployment</c> (:60) feeds
    /// <c>CheckForDeployment</c>:374-377, which greys START MISSION when the squad's summed
    /// <c>OccupingSpace</c> exceeds it. There is no second check in <c>TacMission</c> or
    /// <c>GeoMission.Launch</c>. The mod added a second one of its own —
    /// <see cref="MissionSync.Facts.MaxUnits"/> / <see cref="MissionSync.Validate"/> — because a client's
    /// launch is re-validated against the HOST's facts, and THAT is why this is a config value rather than a
    /// hardcoded number: lift the UI cap alone and every client launch is refused by the host's validator.
    /// Both enforcement points call <see cref="For"/>, so they cannot drift apart (RailCheck L372).
    ///
    /// PARITY, NOT PERSUASION. The value rides the ordinary mod-settings parity manifest
    /// (<c>ParityManifest.DeployCapSettingId</c>) — our own settings are otherwise skipped as local
    /// preferences — so <c>ParityConfigSync.ApplyHostSettings</c> pushes the HOST's number onto every client
    /// at join, in memory, restored at teardown. Host and client therefore enforce the same cap by
    /// construction, and a value that fails to apply stays a visible manifest diff.
    ///
    /// CEILING 16, NOT 99, AND THE FLOOR IS VANILLA. Deployment positions are NOT a slot list: a
    /// <c>TacticalDeployZone</c> is a box collider and its positions are every walkable floor cell inside it
    /// (<c>TacticalDeployZone.cs</c>:307-336 <c>PointsOnXZGrid</c> + <c>CastAllFloorsAt</c>), validated per
    /// actor at :287-305 (<c>CanStandAt</c> + exclusion zones). Overflow does not throw — <c>SpawnActor</c>
    /// :204-218 finds no position and returns <c>null</c>, and the soldier simply IS NOT ON THE MAP. Nothing
    /// guarantees a zone fits 16, let alone 99, so the ceiling is deliberately modest and every refusal is
    /// logged loudly by <see cref="DeployZoneRefusalLog"/>. The floor is the mission's own value: a mission
    /// that already allows more than the configured number keeps its own, and the option can never make a
    /// squad SMALLER than vanilla.
    ///
    /// TFTV IS NOT FOUGHT. With Aircraft Rework ON, TFTV's <c>GetDeploymentSources</c> postfix collapses the
    /// sources to a single aircraft (<c>AircraftReworkMissionDeployment.cs</c>:182-218), so there is simply
    /// nothing extra to enrol and a raised cap changes nothing — no error, no branch of ours.
    /// </summary>
    internal static class DeployCap
    {
        /// <summary>The most this option will ever allow, whatever the config says. See the class doc:
        /// there is no upper guarantee from the deploy zones, so this is a modest number and not 99.</summary>
        internal const int Ceiling = 16;

        /// <summary>PURE (RailCheck L372 executes it). <c>configured &lt;= 0</c> = the option is OFF and the
        /// answer is the mission's own vanilla cap, byte for byte. Otherwise the configured number, clamped
        /// to <see cref="Ceiling"/> from above and to the mission's own cap from below.</summary>
        internal static int Effective(int vanillaCap, int configured) =>
            Math.Max(vanillaCap, Math.Min(Ceiling, configured));

        /// <summary>The live setting, 0 when the mod config is not up yet (= OFF = vanilla).</summary>
        internal static int Configured() => MultiplayerMain.Instance?.Config?.MaxDeployUnits ?? 0;

        /// <summary>THE one cap. Both enforcement points route through here.</summary>
        internal static int For(TacMissionTypeDef def) =>
            def == null ? 0 : Effective(def.MaxPlayerUnits, Configured());
    }

    /// <summary>
    /// The UI half of the lift. <c>UIStateRosterDeployment.CheckForDeployment</c>:369-379 is the ONLY writer
    /// of START MISSION's interactability and of the "n / 8" slot label, and it reads the mission def
    /// directly, so the raised cap has to be re-applied after it — as a POSTFIX, which also runs when TFTV's
    /// own Prefix on this method returns false (<c>AircraftReworkMissionDeployment.cs</c>:136-179), exactly
    /// as <see cref="DeployButtonCountdownLock"/> does one class over.
    ///
    /// ORDER-INDEPENDENT BY CONSTRUCTION. Two of our postfixes now write this button and Harmony does not
    /// order them, so this one folds <see cref="DeployCountdown.ButtonLive"/> in as well: whichever runs
    /// last, the answer is the same one — the native verdict recomputed against the raised cap, minus a
    /// countdown in flight.
    /// </summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "CheckForDeployment")]
    internal static class DeployCapUiLift
    {
        private static void Postfix(UIStateRosterDeployment __instance, List<GeoCharacter> squad)
        {
            if (DeployCap.Configured() <= 0) return; // option OFF: the game's own line stands, untouched
            try
            {
                var def = __instance?.Mission?.MissionDef;
                if (def == null || squad == null) return;
                // No "was anything actually lifted for THIS mission?" early-out, deliberately: reading
                // def.MaxPlayerUnits here to answer it is the raw cap back in the method that decides, which
                // is the drift L372 arm (c) forbids. When nothing was lifted, For() answers the mission's own
                // number and the lines below rewrite exactly what the game just wrote.
                int cap = DeployCap.For(def);

                var module = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View?
                             .GeoscapeModules?.DeploymentMissionBriefingModule;
                if (module == null) return;

                // The game's own three terms (:371-376), recomputed against the raised cap.
                int volume = squad.Sum(s => s.OccupingSpace);
                int vehicles = squad.Count(c => c.TemplateDef != null &&
                                                (c.TemplateDef.IsVehicle || c.TemplateDef.IsMutog));
                module.SetCurrentDeployment(volume, cap);
                bool native = squad.Any() && vehicles < 2 && volume <= cap;
                module.DeployButton.SetInteractable(
                    DeployCountdown.ButtonLive(native, DeployCountdown.CountdownRunning));
                module.DeployButton.ResetButtonAnimations();
            }
            catch (Exception ex)
            {
                // Presentation seam: a failure here leaves the button as the game painted it — the vanilla
                // cap — which refuses a big squad rather than shipping a broken one.
                MpLog.LogError("[MP][deploy] could not apply the lifted deployment cap to START MISSION: " + ex);
            }
        }
    }

    /// <summary>
    /// EVERY ACTOR A DEPLOY ZONE REFUSES IS NAMED. This is the price of the option above and the reason its
    /// ceiling is 16: <c>TacticalDeployZone.SpawnActor</c>:204-218 answers "no room" by returning
    /// <c>null</c>, and with <c>canFail: true</c> it does not even call <c>TacMission.ReportProblem</c> — the
    /// soldier is silently absent from the battle. Silent absence is this project's dominant bug class, so
    /// the refusal gets a log line naming the actor, the zone and the requester whether the game reports it
    /// or not.
    ///
    /// Rare by construction (once per actor per deployment), so verbosity here costs nothing. Bound by
    /// <c>TargetMethod</c> rather than a six-entry type array: <c>SpawnActor</c> is overloaded and
    /// <c>AccessTools.Method</c> does an EXACT parameter match, which is how a patch silently binds nothing.
    /// </summary>
    [HarmonyPatch]
    internal static class DeployZoneRefusalLog
    {
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(TacticalDeployZone))
                       .FirstOrDefault(m => m.Name == nameof(TacticalDeployZone.SpawnActor) &&
                                            m.GetParameters().Any(p => p.Name == "canFail"));

        private static void Postfix(TacticalDeployZone __instance, TacticalActorBase __result,
                                    ComponentSetDef actorSetDef, bool canFail, object requester)
        {
            if (__result != null) return;
            MpLog.LogError("[MP][deploy] DEPLOY ZONE REFUSED AN ACTOR — it will NOT be on the map. zone=" +
                           (__instance == null ? "<null>" : __instance.name) + " actor=" +
                           (actorSetDef == null ? "<null>" : actorSetDef.name) + " requester=" +
                           (requester == null ? "<null>" : requester.ToString()) + " canFail=" + canFail +
                           " — no walkable, unexcluded cell was free inside the zone (TacticalDeployZone" +
                           ".cs:204-218). If the deployment cap was lifted above the mission's own " +
                           "MaxPlayerUnits (" + DeployCap.Configured() + " configured, ceiling " +
                           DeployCap.Ceiling + "), that is the first thing to suspect.");
        }
    }
}
