using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;

namespace Multiplayer.Harmony
{
    // Scripted mission events part 2 (c)+(d): CLIENT-ONLY defensive guards on the TFTV custom-mission
    // Harmony hooks that SELF-RUN on the co-op client's frozen sim — the theturned-tftv-compat pattern
    // (narrow guards on OUR side, TFTV never hard-referenced), same shape as ClientTftvAircraftFreezePatch.
    //
    // WHY these five, exactly (audited 2026-07-07 against refs/TFTV-src + our mirror pipeline): the client
    // mirror deliberately drives four native paths that TFTV's mission scripts hook, so those hooks fire on
    // the CLIENT with full script side effects (RNG rolls, local spawns, campaign-static writes, win-condition
    // checks, forced ShowStoryPanel popups) on top of host-authoritative mirror state:
    //   1. TacticalFactionVision.OnFactionStartTurn — our MirrorPlayTurnCrt calls it every mirrored turn
    //      (MirrorSuppressPatches). TFTV postfix runs PalaceTacticalNewTurn / PhoenixBaseDefenseVSAliensTurnStart /
    //      AncientsNewTurnCheck / human-enemies volleys → per-turn mission scripts incl. spawns + story panels.
    //   2. TacticalLevelController.ActorEnteredPlay — fires for every 0x92 materialize + deploy hydrate.
    //      TFTV postfix re-rolls human-enemy rank/name buffs (RNG ≠ host), TryToTurnIntoRevenant transforms,
    //      and CheckFinalMissionWinConditionWhereDeployingItem (mission END must stay TS4/host-owned).
    //   3. TacticalLevelController.ActorDied — fires when an inbound tac.damage kill runs the native death
    //      cascade on the mirror. TFTV pre/postfix writes campaign state (revenant/Osiris death records,
    //      infestation outro event) and rolls human-enemies tactics — host-only decisions.
    //   4. TacticalActorBase.ApplyDamage + StatusComponent.AddStatus — fire on every mirrored damage apply /
    //      status reconcile. TFTV postfixes run the base-defense containment check and the console-activation
    //      mission scripts (BaseDefenseConsoleActivated / PalaceConsoleActivated / TalkingPoint / Propaganda) —
    //      a host console Interact would otherwise re-run its script when the status mirrors to the client.
    //
    // DECISION per the degrade-to-notify precedent: SUPPRESS on the active-session client (host runs the
    // scripts once; every script OUTCOME reaches the client through the existing surfaces — spawns 0x92,
    // statuses/stats 0x8F, objectives/zone-unlocks 0x99, conclusion 0x95). Forced TFTV story popups therefore
    // simply never trigger client-side (no hook → no ShowStoryPanel) — a blocking script popup can never wedge
    // one side. Known display trade-off, accepted: TFTV flavor applied at enter-play (delirium-perk bar icons,
    // human-enemy rank names) is skipped on the client mirror rather than re-rolled WRONG (client-relayed
    // intents can't use those abilities anyway).
    //
    // NOT guarded (audited, deterministic + self-healing via the host delta, over-suppression riskier):
    // PRMBetterClasses InfiltratorSkills wild-Umbra faction fix, TFTVDrills PounceProtocol status refresh.
    //
    // TFTV absent → Prepare() returns false → Harmony skips each class (zero impact). Suppression reuses the
    // unit-tested pure gate ClientTftvAircraftFreezeGate.ShouldRunTftvNormally (single-player / no session /
    // HOST → TFTV runs normally; active-session client → suppress). Auto-registers via MultiplayerMain PatchAll.

    internal static class TftvTacticalScriptGuardUtil
    {
        /// <summary>Shared prefix decision: true = let the TFTV hook run (SP / no session / host).</summary>
        internal static bool RunTftv()
        {
            var engine = NetworkEngine.Instance;
            return ClientTftvAircraftFreezeGate.ShouldRunTftvNormally(
                engineExists: engine != null,
                isActive: engine != null && engine.IsActive,
                isHost: engine != null && engine.IsHost);
        }

        /// <summary>Resolve a static method on a TFTV nested patch class (CLR nested syntax
        /// <c>Namespace.Outer+Nested</c>). Null when TFTV (or the member) is absent → patch skipped.</summary>
        internal static MethodBase Resolve(string nestedTypeName, string methodName)
        {
            var t = AccessTools.TypeByName(nestedTypeName);
            return t != null ? AccessTools.Method(t, methodName) : null;
        }
    }

    /// <summary>Guard 1: TFTV per-turn mission scripts (palace / base defense / ancients / human enemies).</summary>
    [HarmonyPatch]
    public static class ClientTftvVisionTurnStartScriptGuard
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = TftvTacticalScriptGuardUtil.Resolve(
                "TFTV.TFTVHarmonyTactical+TFTV_TacticalFactionVision_OnFactionStartTurn_Patch", "Postfix");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => TftvTacticalScriptGuardUtil.RunTftv();
    }

    /// <summary>Guard 2: TFTV spawn-time script logic on the 0x92 materialize / deploy-hydrate enter-play.</summary>
    [HarmonyPatch]
    public static class ClientTftvActorEnteredPlayScriptGuard
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = TftvTacticalScriptGuardUtil.Resolve(
                "TFTV.TFTVHarmonyTactical+TFTV_TacticalLevelController_ActorEnteredPlay_Patch", "Postfix");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => TftvTacticalScriptGuardUtil.RunTftv();
    }

    /// <summary>Guard 3a: TFTV death-time campaign/mission writes (prefix half — infestation outro,
    /// touched-by-the-void roll, revenant kill record).</summary>
    [HarmonyPatch]
    public static class ClientTftvActorDiedScriptGuardPrefix
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = TftvTacticalScriptGuardUtil.Resolve(
                "TFTV.TFTVHarmonyTactical+TFTV_TacticalLevelController_ActorDied_Patch", "Prefix");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => TftvTacticalScriptGuardUtil.RunTftv();
    }

    /// <summary>Guard 3b: TFTV death-time campaign/mission writes (postfix half — revenant/Osiris dead
    /// records, cyclops resistance, human-enemies tactics roll).</summary>
    [HarmonyPatch]
    public static class ClientTftvActorDiedScriptGuardPostfix
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = TftvTacticalScriptGuardUtil.Resolve(
                "TFTV.TFTVHarmonyTactical+TFTV_TacticalLevelController_ActorDied_Patch", "Postfix");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => TftvTacticalScriptGuardUtil.RunTftv();
    }

    /// <summary>Guard 4: TFTV base-defense containment check on every mirrored damage apply.</summary>
    [HarmonyPatch]
    public static class ClientTftvApplyDamageScriptGuard
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = TftvTacticalScriptGuardUtil.Resolve(
                "TFTV.TFTVHarmonyTactical+TFTV_TacticalActorBase_ApplyDamage_Patch", "Postfix");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => TftvTacticalScriptGuardUtil.RunTftv();
    }

    /// <summary>Guard 5: TFTV console-activation mission scripts on every mirrored status add.
    /// (Note TFTV's class is spelled <c>..._patch</c> lowercase — pinned verbatim from the source.)</summary>
    [HarmonyPatch]
    public static class ClientTftvStatusAddScriptGuard
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = TftvTacticalScriptGuardUtil.Resolve(
                "TFTV.TFTVHarmonyTactical+TFTV_StatusComponent_AddStatus_patch", "Postfix");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => TftvTacticalScriptGuardUtil.RunTftv();
    }
}
