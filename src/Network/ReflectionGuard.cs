using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PhoenixPoint.Modding;

namespace Multiplayer.Network
{
    /// <summary>
    /// One curated CRITICAL reflection binding: a human-readable <c>Type.Member</c> label plus a
    /// resolver that mirrors, byte-for-byte, the exact <c>AccessTools</c> lookup the real sync code
    /// uses for it. The resolver returns the resolved <see cref="MemberInfo"/> (a <c>Type</c>,
    /// <c>FieldInfo</c>, <c>PropertyInfo</c> or method are all <see cref="MemberInfo"/>) or null.
    /// </summary>
    internal sealed class GuardedBinding
    {
        public readonly string Label;
        public readonly Func<MemberInfo> Resolve;

        public GuardedBinding(string label, Func<MemberInfo> resolve)
        {
            Label = label;
            Resolve = resolve;
        }
    }

    /// <summary>
    /// Phase 5 reflection version-guard (game-facing half). The mod reaches into Phoenix Point
    /// internals through ~800 <c>AccessTools</c> binding sites; a game update that renames or removes a
    /// member makes a lookup silently return null, which today surfaces as a mid-game DESYNC instead of
    /// a clear error. This guard resolves a CURATED set of the highest-impact bindings — the ones whose
    /// silent loss breaks co-op sync entirely — at startup and, if any fail, logs ONE prominent error
    /// naming every broken binding so the incompatibility is diagnosable up front.
    ///
    /// The list is intentionally small and hand-picked (existence checks by name across the four load-
    /// bearing pillars), NOT the full binding set. Each entry mirrors a resolution already proven in
    /// the shipping, in-game-verified code, so a green install reports "version OK" with zero false
    /// positives. All verdict/report/validation logic lives in the pure, unit-tested
    /// <see cref="ReflectionGuardCore"/>; this file owns only the AccessTools resolution and the log
    /// firing, and it NEVER throws.
    /// </summary>
    public static class ReflectionGuard
    {
        // Name-only resolvers (no parameter-type pinning) so each entry is a robust "does this member
        // still EXIST?" check — the version-drift question the guard cares about — and cannot false-
        // positive on method-overload ambiguity. Every type is resolved by full name via
        // AccessTools.TypeByName, exactly as the real reflection code does; a null type short-circuits
        // to a null member (reported as unresolved) rather than throwing.
        private static MemberInfo Ty(string typeName) => AccessTools.TypeByName(typeName);

        private static MemberInfo Prop(string typeName, string member)
        {
            var t = AccessTools.TypeByName(typeName);
            return t == null ? null : AccessTools.Property(t, member);
        }

        private static MemberInfo Field(string typeName, string member)
        {
            var t = AccessTools.TypeByName(typeName);
            return t == null ? null : AccessTools.Field(t, member);
        }

        /// <summary>
        /// The curated critical bindings, grouped by the co-op pillar each one anchors. Order is
        /// preserved in reporting. All labels/resolvers mirror the live reflection code (see the
        /// per-pillar source files noted below); keep this list in sync when a critical binding moves.
        /// </summary>
        private static readonly GuardedBinding[] Critical =
        {
            // -- Transport / session bootstrap: co-op save-transfer + load-state apply
            //    (SaveTransferCoordinator.ApplyPrepareLoadGameState; PhoenixPoint.Common.Saves.PhoenixSaveManager)
            new GuardedBinding("PhoenixSaveManager.LatestLoad",     () => Prop("PhoenixPoint.Common.Saves.PhoenixSaveManager", "LatestLoad")),
            new GuardedBinding("PhoenixSaveManager._currentGameId", () => Field("PhoenixPoint.Common.Saves.PhoenixSaveManager", "_currentGameId")),
            new GuardedBinding("PhoenixSaveManager._enabledDlc",    () => Field("PhoenixPoint.Common.Saves.PhoenixSaveManager", "_enabledDlc")),

            // -- Geoscape action rail: GeoLevelController spine + GeoAbility relay + vehicle identity
            //    (GeoRuntime, GeoAbilityRelayReflection, EventReflection)
            new GuardedBinding("GeoLevelController.PhoenixFaction", () => Prop("PhoenixPoint.Geoscape.Levels.GeoLevelController", "PhoenixFaction")),
            new GuardedBinding("GeoLevelController.Map",            () => Field("PhoenixPoint.Geoscape.Levels.GeoLevelController", "Map")),
            new GuardedBinding("GeoAbility.BaseDef",                () => Prop("PhoenixPoint.Geoscape.Entities.Abilities.GeoAbility", "BaseDef")),
            new GuardedBinding("GeoAbility.GeoActor",               () => Prop("PhoenixPoint.Geoscape.Entities.Abilities.GeoAbility", "GeoActor")),
            new GuardedBinding("GeoVehicle.VehicleID",              () => Field("PhoenixPoint.Geoscape.Entities.GeoVehicle", "VehicleID")),
            new GuardedBinding("GeoVehicle.Owner",                  () => Prop("PhoenixPoint.Geoscape.Entities.GeoVehicle", "Owner")),
            new GuardedBinding("GeoSite.SiteId",                    () => Field("PhoenixPoint.Geoscape.Entities.GeoSite", "SiteId")),

            // -- Tactical deploy / state spine: mission handoff + snapshot serialization
            //    (TacticalDeploySync reflection block)
            new GuardedBinding("TacticalLevelController",           () => Ty("PhoenixPoint.Tactical.Levels.TacticalLevelController")),
            new GuardedBinding("TacLevelInstanceData",              () => Ty("PhoenixPoint.Tactical.Levels.TacLevelInstanceData")),
            new GuardedBinding("TacticalGameParams",                () => Ty("PhoenixPoint.Common.Levels.Params.TacticalGameParams")),
            new GuardedBinding("TacticalActorBase",                 () => Ty("PhoenixPoint.Tactical.Entities.TacticalActorBase")),
            new GuardedBinding("Serializer",                        () => Ty("Base.Serialization.General.Serializer")),

            // -- Event-window mirror: geoscape event surface replicated host → client
            //    (EventReflection)
            new GuardedBinding("GeoscapeEvent.EventID",             () => Field("PhoenixPoint.Geoscape.Events.GeoscapeEvent", "EventID")),
            new GuardedBinding("GeoscapeEvent.EventData",           () => Prop("PhoenixPoint.Geoscape.Events.GeoscapeEvent", "EventData")),
            new GuardedBinding("GeoLevelController.EventSystem",    () => Field("PhoenixPoint.Geoscape.Levels.GeoLevelController", "EventSystem")),
            new GuardedBinding("GeoscapeEventData.Choices",         () => Field("PhoenixPoint.Geoscape.Events.Eventus.GeoscapeEventData", "Choices")),
        };

        /// <summary>The curated bindings' labels, in list order (exposed for validation + tests).</summary>
        public static IReadOnlyList<string> CriticalLabels
        {
            get
            {
                var labels = new List<string>(Critical.Length);
                foreach (var b in Critical)
                    labels.Add(b.Label);
                return labels;
            }
        }

        /// <summary>
        /// Startup compatibility verdict, latched by <see cref="RunStartupSelfCheck"/>: true until the
        /// self-check finds an unresolved critical binding, then false for the process lifetime.
        /// Defaults to true so the co-op host/join gate is INERT unless the guard actually ran AND
        /// failed (a green install — all curated bindings resolve — and any code path that reads this
        /// before the self-check both see "compatible", i.e. zero behavior change).
        /// </summary>
        public static bool IsCompatible { get; private set; } = true;

        /// <summary>
        /// The stored multi-line startup report naming every unresolved binding (see
        /// <see cref="ReflectionGuardCore.BuildStartupReport"/>), or null while compatible. Latched
        /// alongside <see cref="IsCompatible"/> so the co-op gate can surface the SAME diagnosable text
        /// the startup log showed, at host/join time, without recomputing it.
        /// </summary>
        public static string FailureReport { get; private set; }

        /// <summary>
        /// Startup self-check: resolve every curated critical binding and, if any fail to resolve,
        /// log ONE prominent error naming each broken binding and latch <see cref="IsCompatible"/>
        /// false + <see cref="FailureReport"/> so the co-op host/join gate can refuse networking.
        /// No-op-visible (a single "version OK" info line) when all resolve. NEVER throws — a fault
        /// here just means no self-check this run, and the mod proceeds exactly as before.
        /// </summary>
        public static void RunStartupSelfCheck(ModLogger logger)
        {
            if (logger == null)
                return;

            try
            {
                // Dev-facing integrity check of the curated list itself (blank/duplicate labels).
                var labelProblems = ReflectionGuardCore.ValidateLabels(CriticalLabels);
                if (labelProblems.Count > 0)
                    logger.LogWarning("[Multiplayer][guard] curated binding list has issues: "
                                      + string.Join("; ", labelProblems));

                var results = new List<CriticalBinding>(Critical.Length);
                foreach (var binding in Critical)
                {
                    bool resolved;
                    try { resolved = binding.Resolve() != null; }
                    catch { resolved = false; } // a resolver fault = treat as unresolved, never propagate
                    results.Add(new CriticalBinding(binding.Label, resolved));
                }

                var unresolved = ReflectionGuardCore.UnresolvedMembers(results);
                if (unresolved.Count == 0)
                {
                    logger.LogInfo($"[Multiplayer][guard] all {results.Count} critical reflection bindings resolved - version OK");
                    return;
                }

                // Latch the incompatibility so the co-op host/join gate refuses networking and reuses
                // this exact report as the user-facing reason (see MultiplayerUI co-op entry points).
                FailureReport = ReflectionGuardCore.BuildStartupReport(unresolved);
                IsCompatible = false;
                logger.LogError(FailureReport);
            }
            catch (Exception e)
            {
                // The guard must never crash the game; downgrade any unexpected fault to a warning.
                try { logger.LogWarning("[Multiplayer][guard] self-check errored (non-fatal): " + e.Message); }
                catch { /* logging itself failed — nothing more we can safely do */ }
            }
        }
    }
}
