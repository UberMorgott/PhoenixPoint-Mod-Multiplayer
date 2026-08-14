using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Parity;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L372 — THE SCREEN AND THE VALIDATOR ENFORCE THE SAME DEPLOYMENT CAP, AND SO DO HOST AND CLIENT.
    ///
    /// THE SHAPE OF THE BUG THIS FORBIDS. Vanilla enforces <c>TacMissionTypeDef.MaxPlayerUnits</c> (=8,
    /// TacMissionTypeDef.cs:66) in exactly ONE place — <c>UIStateRosterDeployment.CheckForDeployment</c>
    /// :374-377, via <c>_squadMaxDeployment</c>:60. There is no second check anywhere in <c>TacMission</c> or
    /// <c>GeoMission.Launch</c>. But THIS MOD added one: a client's launch never runs locally, it crosses as
    /// an intent and is re-validated against the HOST's own facts (<c>MissionSync.Validate</c>, arm
    /// <c>f.Volume &gt; f.MaxUnits</c>). So the cap now lives in two places, and the optional lift moves it.
    /// Raise the UI's copy and forget the validator's and the feature is worse than absent: the client's
    /// screen happily enrols twelve soldiers, START MISSION lights up, and every single launch is refused by
    /// the host with "the squad takes 12 deployment slots but the mission allows 8". Raise the validator's
    /// and forget the UI's and the option does nothing at all.
    ///
    /// THE OUTCOME THIS LAW ASSERTS, not the calls that produce it:
    ///   (a) THE CAP IS EXECUTED, not described. <c>DeployCap.Effective</c> is pure and this law RUNS it over
    ///       the corners that matter: OFF (0) answers the mission's own cap byte for byte; a configured value
    ///       under the ceiling answers itself; 99 is CLAMPED to 16, because the ceiling is the whole reason
    ///       this option is safe (deploy zones are box colliders full of walkable cells, not slot lists, and
    ///       an actor with no free cell is SILENTLY not spawned — TacticalDeployZone.cs:204-218); and a
    ///       configured value BELOW the mission's own cap never shrinks a squad.
    ///   (b) BOTH ENFORCEMENT POINTS READ THAT ONE FUNCTION. The UI postfix on <c>CheckForDeployment</c> and
    ///       <c>MissionSync.CaptureLaunch</c>'s host-side facts must each reach <c>DeployCap.For</c>. Move one
    ///       and not the other — which is exactly the failure above — and this arm names the one that moved.
    ///   (c) AND NEITHER OF THEM STILL READS THE RAW FIELD. Reaching <c>For</c> is not enough if the old
    ///       <c>MissionDef.MaxPlayerUnits</c> load is still sitting there deciding the answer, so the two
    ///       methods must not touch <c>MaxPlayerUnits</c> directly at all — the ONE place allowed to is
    ///       <c>DeployCap.For</c> itself.
    ///   (d) HOST AND CLIENT CANNOT HOLD DIFFERENT NUMBERS. The cap must be a PARITY field: the collector
    ///       must consult <c>ParityManifest.IsSyncedOwnSetting</c> (our own settings are otherwise skipped
    ///       wholesale as local preferences), that predicate must accept the cap and REFUSE a preference —
    ///       both directions, since "return true" would auto-apply the host's ping key over the client's —
    ///       the named field must really exist on <c>MultiplayerConfig</c>, and its type must be one
    ///       <c>ParityAutoApply</c> can actually parse and apply. A field the auto-apply cannot set is a
    ///       manifest diff that never converges.
    ///   (e) THE CEILING IS REAL AND MODEST. 16, and never TFTV's 99 (<c>TFTVConfig.cs</c>:64
    ///       <c>UnLimitedDeployment</c>) — there is no upper guarantee from any deploy zone, so a big number
    ///       here is soldiers silently missing from the battle.
    ///
    /// Positive control: <c>FakeSeam.UiOnly</c> stands in for the half-applied fix (the UI cap lifted, the
    /// validator left on the raw field) and arm (b) must flag it — an arm that cannot see the shipped shape
    /// of the bug is not checking anything.
    ///
    /// Falsify: make <c>Effective</c> return <c>configured</c> unclamped → (a) red on the 99 row and the
    /// below-vanilla row. Put <c>MissionDef.MaxPlayerUnits</c> back into <c>MissionSync</c>'s facts → (b)+(c)
    /// red. Drop the <c>IsSyncedOwnSetting</c> call from the collector, or make it <c>=&gt; true</c> → (d)
    /// red. Raise <c>Ceiling</c> to 99 → (e) red.
    /// </summary>
    internal static class L372_OneDeploymentCapForTheScreenAndTheValidator
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The half-applied fix, as code, so arm (b) is run against a seam that really has it.</summary>
        private static class FakeSeam
        {
            internal static int UiOnly() => DeployCap.For(null);
            internal static int ValidatorLeftBehind() => 8;
        }

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(MissionSync).Assembly;
            var cap = mod.GetType("Multiplayer.Network.Sync.DeployCap");
            var uiLift = mod.GetType("Multiplayer.Network.Sync.DeployCapUiLift");
            var collector = mod.GetType("Multiplayer.Network.ParityManifestCollector");
            var config = mod.GetType("Multiplayer.MultiplayerConfig");

            var effective = cap?.GetMethod("Effective", All);
            var forDef = cap?.GetMethod("For", All);
            var ceiling = cap?.GetField("Ceiling", All);
            var uiPostfix = uiLift?.GetMethod("Postfix", All);
            var captureFacts = typeof(MissionSync).GetMethod("HandleLaunch", All)
                            ?? typeof(MissionSync).GetMethod("ApplyLaunch", All);
            var collect = collector?.GetMethod("Collect", All);
            var isSynced = typeof(ParityManifest).GetMethod("IsSyncedOwnSetting", All);
            var isScalar = typeof(ParityAutoApply).GetMethod("IsScalar", All);
            var maxPlayerUnits = typeof(PhoenixPoint.Common.Levels.Missions.TacMissionTypeDef)
                                 .GetField("MaxPlayerUnits", All);

            if (effective == null || forDef == null || ceiling == null || uiPostfix == null ||
                captureFacts == null || collect == null || isSynced == null || isScalar == null ||
                config == null || maxPlayerUnits == null)
            {
                yield return "L372 premise-changed: the deployment-cap family no longer resolves " +
                             "(DeployCap.Effective/For/Ceiling, DeployCapUiLift.Postfix, MissionSync's " +
                             "host-side launch handler, ParityManifestCollector.Collect, " +
                             "ParityManifest.IsSyncedOwnSetting, ParityAutoApply.IsScalar, " +
                             "MultiplayerConfig, TacMissionTypeDef.MaxPlayerUnits). Every arm below would " +
                             "pass vacuously, so 'one cap for the screen and the validator' is UNCHECKED " +
                             "rather than satisfied";
                yield break;
            }

            // ═══ (a) THE CAP, EXECUTED ═══
            // vanillaCap, configured, expected, why
            var rows = new[]
            {
                new object[] { 8, 0, 8, "the option OFF must be vanilla byte for byte" },
                new object[] { 8, 12, 12, "a configured value under the ceiling is the cap" },
                new object[] { 8, 16, 16, "the ceiling itself is allowed" },
                new object[] { 8, 99, 16, "99 must be CLAMPED to the ceiling — TFTV's UnLimitedDeployment " +
                                          "number is exactly what this option refuses to be" },
                new object[] { 8, 4, 8, "the option may never make a squad SMALLER than vanilla" },
                new object[] { 20, 16, 20, "a mission that already allows more keeps its own cap" },
            };
            foreach (var r in rows)
            {
                int got; string threw = null;
                try { got = Convert.ToInt32(effective.Invoke(null, new object[] { r[0], r[1] })); }
                catch (Exception ex) { got = int.MinValue; threw = (ex.InnerException ?? ex).Message; }
                if (threw != null)
                    yield return "L372 cap-threw: DeployCap.Effective(" + r[0] + ", " + r[1] + ") threw '" +
                                 threw + "' — the one function both enforcement points call cannot answer";
                else if (got != (int)r[2])
                    yield return "L372 cap-wrong: DeployCap.Effective(vanilla=" + r[0] + ", configured=" +
                                 r[1] + ") answers " + got + ", expected " + r[2] + " — " + r[3] +
                                 ". Deployment positions are the walkable cells inside a box collider, not a " +
                                 "slot list, and an actor with no free cell is SILENTLY not spawned " +
                                 "(TacticalDeployZone.cs:204-218), so this clamp is the whole safety of the " +
                                 "option";
            }

            // ═══ (b) BOTH ENFORCEMENT POINTS READ IT ═══
            if (!Reaches(uiPostfix, forDef, mod))
                yield return "L372 screen-cap-unrouted: DeployCapUiLift.Postfix no longer reaches " +
                             "DeployCap.For, so the deployment screen is back on the mission's raw " +
                             "MaxPlayerUnits (UIStateRosterDeployment:60/374-377) while the host's validator " +
                             "may not be — the option either does nothing or, worse, lets the host accept a " +
                             "squad no screen will let anyone build";
            if (!Reaches(captureFacts, forDef, mod))
                yield return "L372 validator-cap-unrouted: " + Name(captureFacts) + " no longer reaches " +
                             "DeployCap.For when it fills MissionSync.Facts.MaxUnits. A client's launch is " +
                             "re-validated against the HOST's facts, so a raised screen cap plus a vanilla " +
                             "validator cap means every client launch is refused with 'the squad takes N " +
                             "deployment slots but the mission allows 8' and the player cannot tell why";
            // Positive control: the half-applied fix must be visible to the very arm that exists to catch it.
            if (Reaches(AccessTools0("ValidatorLeftBehind"), forDef, mod))
                yield return "L372 positive-control-broken: the FakeSeam validator that deliberately does " +
                             "NOT consult DeployCap.For appears to reach it, so arm (b) cannot tell the " +
                             "half-applied fix from the whole one";
            if (!Reaches(AccessTools0("UiOnly"), forDef, mod))
                yield return "L372 positive-control-broken: the FakeSeam UI half that deliberately DOES " +
                             "consult DeployCap.For does not appear to reach it — the reachability probe " +
                             "itself is broken, so arm (b) passes vacuously";

            // ═══ (c) AND NEITHER STILL READS THE RAW FIELD ═══
            foreach (var m in new[] { uiPostfix, captureFacts })
                if (LoadsField(m, maxPlayerUnits))
                    yield return "L372 raw-cap-still-read: " + Name(m) + " still loads " +
                                 "TacMissionTypeDef.MaxPlayerUnits directly. Reaching DeployCap.For is not " +
                                 "enough while the raw field is still in the method deciding the answer — " +
                                 "the ONE place allowed to read it is DeployCap.For itself, which is what " +
                                 "makes the screen and the validator provably the same number";

            // ═══ (d) HOST AND CLIENT CANNOT DISAGREE ═══
            if (!Reaches(collect, isSynced, mod))
                yield return "L372 cap-not-a-parity-field: ParityManifestCollector.Collect no longer " +
                             "consults ParityManifest.IsSyncedOwnSetting, so THIS mod's settings are back to " +
                             "being skipped wholesale and the deployment cap never crosses. Host and client " +
                             "then hold whatever each user typed, and the one that differs has every launch " +
                             "refused by a validator it cannot see";
            bool acceptsCap = false, refusesPreference = true;
            {
                string threw = null;
                try
                {
                    acceptsCap = (bool)isSynced.Invoke(null, new object[] { ParityManifest.DeployCapSettingId });
                    refusesPreference = (bool)isSynced.Invoke(null, new object[] { "PingMarkerKey" })
                                     || (bool)isSynced.Invoke(null, new object[] { "AutoEndTurnWhenAllReady" });
                }
                catch (Exception ex) { threw = (ex.InnerException ?? ex).Message; }
                if (threw != null)
                    yield return "L372 parity-predicate-threw: IsSyncedOwnSetting threw '" + threw + "'";
                else
                {
                    if (!acceptsCap)
                        yield return "L372 cap-not-synced: IsSyncedOwnSetting('" +
                                     ParityManifest.DeployCapSettingId + "') is false, so the one setting " +
                                     "that MUST converge on the host's value does not cross at all";
                    if (refusesPreference)
                        yield return "L372 preference-synced: IsSyncedOwnSetting accepts a LOCAL preference " +
                                     "(PingMarkerKey / AutoEndTurnWhenAllReady). Those are per-player and " +
                                     "ParityConfigSync would silently overwrite the client's own — a " +
                                     "predicate that says yes to everything is the whole-mod skip removed " +
                                     "with nothing put in its place";
                }
            }
            var field = config.GetField(ParityManifest.DeployCapSettingId, All);
            if (field == null)
                yield return "L372 cap-field-missing: MultiplayerConfig has no field named '" +
                             ParityManifest.DeployCapSettingId + "'. ModConfigField.ID is the field NAME " +
                             "(ModConfigField.cs:38), so a rename here silently drops the cap out of every " +
                             "manifest while IsSyncedOwnSetting keeps answering true about a field nobody has";
            else
            {
                bool scalar = false;
                try { scalar = (bool)isScalar.Invoke(null, new object[] { field.FieldType }); }
                catch { }
                if (!scalar)
                    yield return "L372 cap-not-appliable: MultiplayerConfig." +
                                 ParityManifest.DeployCapSettingId + " is a " + field.FieldType.Name +
                                 ", which ParityAutoApply.IsScalar refuses. It would ride the manifest, diff " +
                                 "forever and never be applied — a mismatch the player is told about and " +
                                 "cannot fix from the host side";
            }

            // ═══ (e) THE CEILING IS REAL AND MODEST ═══
            var ceilingValue = Convert.ToInt32(ceiling.GetRawConstantValue() ?? ceiling.GetValue(null));
            if (ceilingValue != 16)
                yield return "L372 ceiling-moved: DeployCap.Ceiling is " + ceilingValue + ", not 16. There " +
                             "is NO upper guarantee from any deploy zone — TFTV's own UnLimitedDeployment " +
                             "sets 99 (TFTVAircraftRework/AircraftReworkMissionDeployment.cs:163) and can afford " +
                             "to because with Aircraft Rework on " +
                             "it collapses the sources to one aircraft anyway. Here a number the map cannot " +
                             "seat is soldiers that silently never appear in the battle";
        }

        private static MethodBase AccessTools0(string name) => typeof(FakeSeam).GetMethod(name, All);

        private static string Name(MethodBase m) =>
            (m?.DeclaringType == null ? "?" : m.DeclaringType.FullName) + "." + (m?.Name ?? "?");

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null && Program.Callees(from, asm).Any(c => Same(c, target));

        /// <summary>Does this method's own IL load that field? Reachability alone cannot answer "is the raw
        /// cap still deciding the answer" — the old <c>ldfld MaxPlayerUnits</c> can sit right beside a call
        /// to the new function.</summary>
        private static bool LoadsField(MethodBase m, FieldInfo f)
        {
            if (m == null || f == null) return false;
            try { return Program.FieldRefs(m).Any(x => x != null && x.MetadataToken == f.MetadataToken &&
                                                       x.Module == f.Module); }
            catch { return false; }
        }
    }
}
