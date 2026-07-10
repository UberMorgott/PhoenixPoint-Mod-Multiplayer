using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// PS4 client-edit INTENT RELAY interceptors (personnel-sync spec §5). A co-op CLIENT manages its OWN
    /// soldiers; the sim is frozen so a local roster/equipment/recruit edit can neither take effect nor reach
    /// the host — instead each geoscape edit chokepoint, on a client, SUPPRESSES the local mutation and relays
    /// a permission-gated <see cref="ISyncedAction"/> intent (nonce-deduped by the generic SendActionRequest
    /// path). The host <c>Validate</c>s (category permission via <see cref="PermissionGate"/> + per-soldier
    /// ownership) and runs the NATIVE mutation authoritatively inside <c>SyncApplyScope</c>; the existing
    /// PS1/PS2/PS3 dirty hooks fire → the result mirrors back on #6/#9/#10 to ALL clients (the initiator sees
    /// its own edit only once it round-trips). No host-side broadcast of these actions is needed (unlike
    /// <c>MoveVehiclePatch</c>): the state channels are the sole writer.
    ///
    /// Chokepoints (each single native method, decompile-verified 2026-07-06):
    ///   • <c>GeoCharacter.SetItems</c> → <see cref="EquipSoldierAction"/> (full final loadout — a null arg is
    ///     filled from the soldier's current list); augment has its OWN chokepoint (AugmentGesturePatches.cs
    ///     PREFIX on <c>UIModuleMutationSection.ApplyMutation</c>) → <see cref="AugmentSoldierAction"/>
    ///     carrying the chosen augment's def guid, host-applied via the full native-equivalent chain;
    ///   • <c>GeoPhoenixFaction.HireNakedRecruit</c> → <see cref="HireRecruitAction"/>;
    ///   • <c>GeoCharacter.Rename</c> → <see cref="RenameSoldierAction"/>;
    ///   • <c>GeoFaction.KillCharacter(_, Dismissed)</c> → <see cref="DismissSoldierAction"/>;
    ///   • <c>GeoVehicle/GeoSite.AddCharacter</c> → <see cref="TransferSoldierAction"/> (the paired
    ///     <c>RemoveCharacter</c> is suppressed without a second intent — the Add carries the transfer);
    ///   • <c>GeoPhoenixFaction.KillCapturedUnit</c> → <see cref="KillCapturedUnitAction"/> (containment
    ///     kill button; research live-alien costs never reach it on a client — research start is suppressed
    ///     at AddResearchToQueue);
    ///   • <c>GeoPhoenixFaction.HarvestCapturedUnit</c> → <see cref="HarvestCapturedUnitAction"/> (dismantle
    ///     for food/mutagens; suppressing it here also keeps its inner KillCapturedUnit from double-relaying);
    ///   • <c>UIModuleCharacterProgression.BuyAbility</c> → <see cref="LevelUpAbilityAction"/> (SP ability
    ///     buy — the UI method IS the commit chokepoint: the SP deduction is a raw field write with no
    ///     patchable native beneath it, so the relay hooks the one method that couples cost + LearnAbility);
    ///   • <c>UIModuleCharacterProgression.CommitStatChanges</c> → <see cref="SpendStatPointsAction"/> per
    ///     changed stat (positive deltas only; the host re-derives every point's cost natively). The prefix
    ///     also rolls the module's local current-values back to starting so a repeated commit call in the
    ///     same screen session can never double-relay. Mutoid (mutagen-cost) progression is suppressed
    ///     without relay on a client (wallet-funded path, out of the SP intent family);
    ///   • <c>UIModuleCharacterProgression.ChoseSecondSpecialization</c> → suppressed WITHOUT relay on a
    ///     client (denial notify) so AddSecondaryClass never half-applies locally; the relay intent
    ///     (SecondSpecialization = 70) is a tracked follow-up (COOP-SYNC-ROADMAP.md).
    ///
    /// Composes with the PS1/PS2 dirty Postfixes on the same methods: on a client our Prefix returns false
    /// (suppress) and those Postfixes are IsHost-gated no-ops; on the host our Prefix passes through (IsHost /
    /// IsApplying) so the native runs and marks dirty. Game types are NEVER hard-referenced — targets resolve
    /// via AccessTools; Prepare() false → PatchAll skips silently.
    /// </summary>
    internal static class PersonnelEditRelay
    {
        /// <summary>True only when the LOCAL peer must relay this edit as an intent (co-op client, active
        /// session, not an engine-driven apply/replay). False → the caller returns true (run the native): the
        /// host is authoritative and single-player is untouched.</summary>
        internal static bool ShouldRelay()
        {
            if (SyncApplyScope.IsApplying) return false;          // host apply / client mirror → run native
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return false;  // single-player
            if (engine.IsHost) return false;                       // host authoritative → #6/#9/#10 mirror the result
            return true;                                            // client → suppress + relay
        }

        /// <summary>Permission + ownership gate, then send the built intent. Always returns false (the local
        /// frozen edit is suppressed either way — a denial surfaces via <see cref="PermissionGate.Notify"/>).</summary>
        internal static bool Relay(ActionCategory cat, long unitId, bool checkOwnership, Func<ISyncedAction> build)
        {
            try
            {
                if (!PermissionGate.Check(cat)
                    || (checkOwnership && !PersonnelEditReflection.OwnsSoldier(ClientIdentity.PlayerGuid, unitId)))
                {
                    // Deny visibility (field RCA round 3): Notify is a bare event invoke — without this line a
                    // denied intent left ZERO log signature (indistinguishable from a dead relay path).
                    Debug.Log("[Multiplayer] PersonnelEditRelay: intent DENIED cat=" + cat + " unit=" + unitId
                              + " (permission/ownership) — not sent");
                    PermissionGate.Notify(cat);
                    return false;
                }
                var action = build();
                if (action != null) NetworkEngine.Instance?.Sync?.SendActionRequest(action);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditRelay.Relay failed: " + ex.Message); }
            return false;
        }

        /// <summary>FIX 3 commit-seam backstop: after a native progression commit seam (CommitStatChanges /
        /// BuyAbility) completes on THIS peer — the host runs it natively, the client suppresses it in the Prefix
        /// yet Harmony still runs the Postfix (postfixes are unaffected by a false-returning prefix) — re-drive
        /// the OPEN soldier's progression panel once, unconditionally. Covers the gap the deleted owed-drain
        /// postfixes left: a SAME-unit apply that arrived UNSTAMPED lands at PartialRepaint, which keeps the edit
        /// buffer but never re-drives the open soldier (stale until the next stamped apply). Idempotent (the
        /// commit already reset the local buffer); single-player untouched (gated on an active session).</summary>
        internal static void CommitSeamBackstop()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;   // single-player: no remote applies to backstop
                GeoUiRefresh.RedriveOpenProgressionPanel(GeoRuntime.Instance);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditRelay.CommitSeamBackstop failed: " + ex.Message); }
        }

        /// <summary>Relay one <see cref="SpendStatPointsAction"/> per positive base-stat delta
        /// (CharacterBaseAttribute: Strength=0, Will=1, Speed=2 — decompile enum order). Shared by the
        /// commit-seam prefix (<see cref="CommitStatChangesProgressionRelayPatch"/>) and the conflict-repaint
        /// harvest (<see cref="HarvestPendingStatSpend"/>) — both send the SAME wire the host re-prices natively.</summary>
        internal static void RelayStatDeltas(long unitId, int dStr, int dWill, int dSpeed)
        {
            if (dStr > 0) Relay(ActionCategory.ControlSoldiers, unitId, true,
                () => new SpendStatPointsAction(unitId, 0, dStr));
            if (dWill > 0) Relay(ActionCategory.ControlSoldiers, unitId, true,
                () => new SpendStatPointsAction(unitId, 1, dWill));
            if (dSpeed > 0) Relay(ActionCategory.ControlSoldiers, unitId, true,
                () => new SpendStatPointsAction(unitId, 2, dSpeed));
        }

        /// <summary>Positive-only int delta between two named int fields of <paramref name="inst"/>
        /// (<c>_current* − _starting*</c>); 0 when either is unreadable/non-int. Shared by the commit-seam
        /// prefix and the harvest below.</summary>
        internal static int Delta(Type t, object inst, string currentField, string startingField)
        {
            object cur = AccessTools.Field(t, currentField)?.GetValue(inst);
            object start = AccessTools.Field(t, startingField)?.GetValue(inst);
            return cur is int c && start is int s ? c - s : 0;
        }

        /// <summary>HOST spender's own +/- click (thin-client): apply the signed delta authoritatively at the model
        /// (<see cref="PersonnelEditReflection.SpendStatPoints"/> — spend re-priced/pool-gated, refund ledger-bounded),
        /// then pin the OPEN progression panel's buffer to the fresh model (local-instant display) and re-drive the
        /// host's OTHER open surfaces (roster list, shared-pool label). The client counterpart just relays the intent
        /// and converges on the #9 echo (~RTT). Best-effort; a miss must never break the click.</summary>
        internal static void ApplyHostSelfStatClick(object module, Type t, long unitId, int statId, int signedDelta)
        {
            try
            {
                var rt = GeoRuntime.Instance;
                PersonnelEditReflection.SpendStatPoints(rt, unitId, statId, signedDelta);   // authoritative model apply (spend/refund)
                RebaseModuleToLiveModel(module, t, statId);                                  // pin the OPEN panel buffer to the fresh model
                GeoUiRefresh.SetProgressionStamp(new[] { unitId }, factionSpChanged: true); // re-drive host's other open surfaces
                GeoUiRefresh.RefreshNeedsKick(rt);
                Debug.Log("[Multiplayer] ChangeCharacterStatThinClient: HOST self stat click applied unit=" + unitId
                          + " stat=" + statId + " delta=" + signedDelta);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditRelay.ApplyHostSelfStatClick failed: " + ex.Message); }
        }

        /// <summary>Pin the OPEN progression panel's edit buffer to the LIVE model after a host self-click, so the
        /// panel shows the authoritative result instantly (the native buffer math was fully suppressed by the
        /// prefix, so the caller's <c>_current* += 0</c> leaves the buffer where we set it here). Reads the model
        /// directly (never trusts the buffer): the clicked stat's live value, the soldier SP pool, and the shared
        /// faction pool — pinning <c>_current* == _starting*</c> for each. Consequence (accepted, thin-client spec
        /// §4): with current==starting the native minus button renders disabled — refunds ride the intent path, not
        /// the local gate. buffer==model here is ALSO what makes the later native host CommitStatChanges a no-op
        /// write-back. Best-effort; a miss leaves the panel to converge on the next stamped re-drive.</summary>
        private static void RebaseModuleToLiveModel(object module, Type t, int statId)
        {
            try
            {
                object character = AccessTools.Field(t, "_character")?.GetValue(module);
                object prog = character != null ? AccessTools.Property(character.GetType(), "Progression")?.GetValue(character, null) : null;
                if (prog != null)
                {
                    // (a) clicked stat: pin both baselines to the live model value (post authoritative apply).
                    var getBaseStat = AccessTools.Method(prog.GetType(), "GetBaseStat");
                    var cbaType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Characters.CharacterBaseAttribute");
                    if (getBaseStat != null && cbaType != null)
                    {
                        int liveStat = Convert.ToInt32(getBaseStat.Invoke(prog, new[] { Enum.ToObject(cbaType, statId) }));
                        switch (statId)
                        {
                            case 0: SetIntField(t, module, "_currentStrengthStat", liveStat); SetIntField(t, module, "_startingStrengthStat", liveStat); break;
                            case 1: SetIntField(t, module, "_currentWillStat", liveStat);     SetIntField(t, module, "_startingWillStat", liveStat);     break;
                            case 2: SetIntField(t, module, "_currentSpeedStat", liveStat);    SetIntField(t, module, "_startingSpeedStat", liveStat);    break;
                        }
                    }
                    // (b) soldier SP pool.
                    var spField = AccessTools.Field(prog.GetType(), "SkillPoints");
                    if (spField != null)
                    {
                        int liveSp = Convert.ToInt32(spField.GetValue(prog));
                        SetIntField(t, module, "_currentSkillPoints", liveSp);
                        SetIntField(t, module, "_startingSkillPoints", liveSp);
                    }
                }
                // (c) shared faction SP pool.
                object pf = AccessTools.Field(t, "_phoenixFaction")?.GetValue(module);
                var facField = pf != null ? AccessTools.Field(pf.GetType(), "Skillpoints") : null;
                if (facField != null)
                {
                    int liveFac = Convert.ToInt32(facField.GetValue(pf));
                    SetIntField(t, module, "_currentFactionPoints", liveFac);
                    SetIntField(t, module, "_startingFactionPoints", liveFac);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditRelay.RebaseModuleToLiveModel failed: " + ex.Message); }
        }

        private static void SetIntField(Type t, object inst, string field, int value)
            => AccessTools.Field(t, field)?.SetValue(inst, value);

        /// <summary>HARVEST-BEFORE-WIPE (commit-seam race RCA 2026-07-10): a ConflictRepaint in
        /// <see cref="GeoUiRefresh.FullRedriveProgression"/> is about to <c>SetCharacterProgression</c> the OPEN
        /// soldier's panel — resetting <c>_current*Stat</c> to <c>_starting*Stat</c> and DISCARDING the local
        /// pending stat allocation before the deferred <c>CommitStatChanges</c> seam ever relays it (log-proven:
        /// zero SpendStatPoints intents a whole session, because the host's periodic same-unit #9 re-broadcast
        /// wiped the buffer every few seconds). COMMIT it first, reading the pending deltas straight off the
        /// module fields the same way the commit-seam prefix does:
        ///   • co-op CLIENT → relay one SpendStatPoints intent per positive delta (host applies + re-broadcasts
        ///     → the panel converges to the committed values);
        ///   • HOST → apply each positive delta through the authoritative re-priced <c>SpendStatPoints</c> (the
        ///     SAME landing as the client relay) — NEVER the raw native <c>CommitStatChanges</c>, which writes the
        ///     stale panel buffer straight into the shared SP pool and would re-inflate it over a concurrent
        ///     remote spend; the ModifyBaseStat dirty seam mirrors the result on #9. Mutoid stat spend is out of
        ///     the SP intent family → host no-op (like a client).
        /// The unconfirmed <c>_boughtAbilitySlot</c> is a pre-confirm SELECTION (OnTrackSlotPointerClicked arms
        /// it + opens a confirmation popup — decompile UIModuleCharacterProgression.cs:1031/1035), never a commit,
        /// so it is NOT harvested — the caller's ClearBoughtAbility correctly discards it. Idempotent: the caller
        /// runs SetCharacterProgression right after (resets the buffer), so a later repaint sees no pending edit
        /// and cannot re-harvest. Best-effort; a miss must never break the repaint.</summary>
        internal static void HarvestPendingStatSpend(object progModule)
        {
            try
            {
                if (progModule == null) return;
                var engine = NetworkEngine.Instance;
                bool active = engine != null && engine.IsActiveSession;
                bool isHost = active && engine.IsHost;
                var t = progModule.GetType();
                int dStr = Delta(t, progModule, "_currentStrengthStat", "_startingStrengthStat");
                int dWill = Delta(t, progModule, "_currentWillStat", "_startingWillStat");
                int dSpeed = Delta(t, progModule, "_currentSpeedStat", "_startingSpeedStat");
                bool pandoran = AccessTools.Field(t, "_hasPandoranProgression")?.GetValue(progModule) is bool p && p;
                var mode = StatSpendHarvest.Decide(active, isHost, pandoran, dStr, dWill, dSpeed);
                if (mode == StatSpendHarvest.Mode.None) return;
                object character = AccessTools.Field(t, "_character")?.GetValue(progModule);
                long unitId = PersonnelReflection.ReadUnitId(character);
                if (mode == StatSpendHarvest.Mode.HostCommit)
                {
                    // Do NOT raw-commit via native CommitStatChanges: it writes the panel buffer straight into
                    // SkillPoints/faction Skillpoints with no re-price or clamp, and harvest fires exactly when
                    // that buffer is STALE (a remote spend just moved the shared pool) — a native commit would
                    // re-inflate the pool to the pre-conflict snapshot and erase the concurrent remote spend.
                    // Route each positive delta through the SAME authoritative apply the client relay lands on
                    // (SpendStatPoints): re-prices each point vs the LIVE pool + clamps (short pool writes
                    // nothing); mutoid → no-op. Following SetCharacterProgression re-reads the live state.
                    var rt = GeoRuntime.Instance;
                    if (dStr > 0) PersonnelEditReflection.SpendStatPoints(rt, unitId, 0, dStr);    // Strength
                    if (dWill > 0) PersonnelEditReflection.SpendStatPoints(rt, unitId, 1, dWill);   // Will
                    if (dSpeed > 0) PersonnelEditReflection.SpendStatPoints(rt, unitId, 2, dSpeed); // Speed
                    Debug.Log("[Multiplayer] PersonnelEditRelay.HarvestPendingStatSpend: HOST applied pending stat spend"
                              + " via authoritative re-priced SpendStatPoints — unit=" + unitId
                              + " dStr=" + dStr + " dWill=" + dWill + " dSpeed=" + dSpeed);
                }
                else   // ClientRelay
                {
                    Debug.Log("[Multiplayer] PersonnelEditRelay.HarvestPendingStatSpend: CLIENT harvest before conflict repaint"
                              + " unit=" + unitId + " dStr=" + dStr + " dWill=" + dWill + " dSpeed=" + dSpeed
                              + " — relaying so the allocation commits instead of being discarded");
                    RelayStatDeltas(unitId, dStr, dWill, dSpeed);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditRelay.HarvestPendingStatSpend failed: " + ex.Message); }
        }

        /// <summary>Destination container key for a transfer: (kind 1, VehicleID) for a GeoVehicle, else
        /// (kind 0, SiteId) for a GeoSite/base.</summary>
        internal static void ContainerKey(object container, out int kind, out int id)
        {
            kind = 0; id = -1;
            try
            {
                var vf = AccessTools.Field(container.GetType(), "VehicleID");
                if (vf != null) { kind = 1; id = Convert.ToInt32(vf.GetValue(container)); return; }
                id = GeoSiteReflection.GetSiteId(container);
            }
            catch { }
        }
    }

    // NOTE (v2 rebuild 2026-07-08): the equip intent no longer rides a GeoCharacter.SetItems flush-diff prefix.
    // The old SetItemsEditRelayPatch + LoadoutRelayDedup are DELETED — they inferred edits by diffing the
    // per-frame SetItems flush and stormed ~60 intents/s (the FPS-collapse layer). Equip now captures at the
    // SOURCE gesture seams in EquipGesturePatches.cs (one intent per user action) with the client flush
    // suppressed. Augment (AugmentSoldierAction) now has its own gesture chokepoint on the augmentation screen
    // in AugmentGesturePatches.cs (PREFIX on UIModuleMutationSection.ApplyMutation).

    [HarmonyPatch]
    public static class HireRecruitEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "HireNakedRecruit") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.HireNakedRecruit relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoUnitDescriptor recruit; __1 = IGeoCharacterContainer destination (a base Site).
        public static bool Prefix(object __0, object __1)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                var rt = GeoRuntime.Instance;
                int destSiteId = GeoSiteReflection.GetSiteId(__1);
                if (!PersonnelEditReflection.ResolveRecruitSource(rt, __0, out int kind, out int id))
                {
                    Debug.Log("[Multiplayer] HireRecruitEditRelayPatch: recruit source unresolved — hire suppressed (no relay)");
                    return false;   // frozen client can't hire locally; nothing to relay
                }
                return PersonnelEditRelay.Relay(ActionCategory.Recruitment, 0, false,
                    () => new HireRecruitAction(kind, id, destSiteId));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HireRecruitEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class RenameEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            _target = t != null ? AccessTools.Method(t, "Rename", new[] { typeof(string) }) : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoCharacter.Rename relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance, string __0)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                long unitId = PersonnelReflection.ReadUnitId(__instance);
                string newName = __0;
                return PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new RenameSoldierAction(unitId, newName));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RenameEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class DismissEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            // GeoPhoenixFaction OVERRIDES KillCharacter (GeoPhoenixFaction.cs:1377 → base.KillCharacter); the
            // co-op dismiss dispatches to the override, so patch it (not the base GeoFaction MethodInfo).
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "KillCharacter") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.KillCharacter dismiss relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoCharacter unit; __1 = CharacterDeathReason. Only a DISMISS is a client edit; every other
        // death is host-driven (tactical / geoscape sim) and never originates on the frozen client.
        public static bool Prefix(object __0, object __1)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                if (__1 == null || __1.ToString() != "Dismissed") return true;   // not a dismiss → run native
                long unitId = PersonnelReflection.ReadUnitId(__0);
                return PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new DismissSoldierAction(unitId));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DismissEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class KillCapturedUnitRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "KillCapturedUnit") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.KillCapturedUnit relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoUnitDescriptor captive (containment kill button, UIStateRosterAliens.cs:256). On the host
        // (incl. a relayed apply inside SyncApplyScope) this passes through and the existing
        // PhoenixKillCapturedUnitPoolDirtyPatch Postfix mirrors the removal on #10.
        public static bool Prefix(object __0)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                if (!PersonnelEditReflection.ResolveCapturedSource(GeoRuntime.Instance, __0, out int ordinal, out string guid))
                {
                    Debug.Log("[Multiplayer] KillCapturedUnitRelayPatch: captive unresolved — kill suppressed (no relay)");
                    return false;   // frozen client can't mutate containment locally; nothing to relay
                }
                return PersonnelEditRelay.Relay(ActionCategory.Recruitment, 0, false,
                    () => new KillCapturedUnitAction(ordinal, guid));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] KillCapturedUnitRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class HarvestCapturedUnitRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "HarvestCapturedUnit") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.HarvestCapturedUnit relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoUnitDescriptor captive; __1 = ResourceType (Supplies = food / Mutagen — dismantle buttons,
        // UIStateRosterAliens.cs:275/296). Suppressing here on a client also keeps the inner
        // KillCapturedUnit (GeoPhoenixFaction.cs:893) from firing a second relay.
        public static bool Prefix(object __0, object __1)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                if (!PersonnelEditReflection.ResolveCapturedSource(GeoRuntime.Instance, __0, out int ordinal, out string guid))
                {
                    Debug.Log("[Multiplayer] HarvestCapturedUnitRelayPatch: captive unresolved — harvest suppressed (no relay)");
                    return false;
                }
                int resourceType = Convert.ToInt32(__1);
                return PersonnelEditRelay.Relay(ActionCategory.Recruitment, 0, false,
                    () => new HarvestCapturedUnitAction(ordinal, guid, resourceType));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HarvestCapturedUnitRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class BuyAbilityProgressionRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "BuyAbility") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.BuyAbility relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // BuyAbility is the ONE native commit chokepoint coupling the SP cost (ConsumeAbilityCost +
        // CommitStatChanges — raw SkillPoints field writes, unpatchable below UI level) with
        // CharacterProgression.LearnAbility (UIModuleCharacterProgression.cs:389-426). The relay keys the
        // slot as (trackSource, index in AbilitiesByLevel) + ability-def guid fingerprint; the host
        // re-validates and re-prices natively. A second confirm click before the #9 round-trip is safe:
        // the host's CanLearnAbility rejects the duplicate (already learned).
        public static bool Prefix(object __instance)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                var t = __instance.GetType();
                object slot = AccessTools.Field(t, "_boughtAbilitySlot")?.GetValue(__instance);
                if (slot == null) return true;   // native no-ops on a null slot too
                if (AccessTools.Field(t, "_hasPandoranProgression")?.GetValue(__instance) is bool pandoran && pandoran)
                {
                    Debug.Log("[Multiplayer] BuyAbilityProgressionRelayPatch: mutoid (mutagen-cost) progression not relayed — buy suppressed");
                    ClearBoughtSlot(t, __instance);
                    return false;
                }
                object character = AccessTools.Field(t, "_character")?.GetValue(__instance);
                long unitId = PersonnelReflection.ReadUnitId(character);
                object track = AccessTools.Property(slot.GetType(), "AbilityTrack")?.GetValue(slot, null);
                var slots = track != null ? AccessTools.Field(track.GetType(), "AbilitiesByLevel")?.GetValue(track) as Array : null;
                int slotIndex = -1;
                if (slots != null)
                    for (int i = 0; i < slots.Length; i++)
                        if (ReferenceEquals(slots.GetValue(i), slot)) { slotIndex = i; break; }
                int trackSource = track != null ? Convert.ToInt32(AccessTools.Field(track.GetType(), "Source")?.GetValue(track) ?? -1) : -1;
                // An empty (personal-pick) slot carries the chosen def in _boughtAbility (BuyAbility :393-396).
                object ability = AccessTools.Field(slot.GetType(), "Ability")?.GetValue(slot)
                                 ?? AccessTools.Field(t, "_boughtAbility")?.GetValue(__instance);
                string guid = DefReflection.GetGuid(ability);
                // DIAG (orphan RCA 2026-07-09 evidence gap): one entry line per real buy click with the
                // resolved slot signature, so a dropped/misrouted SP spend on a live soldier is traceable.
                Debug.Log("[Multiplayer] BuyAbilityProgressionRelayPatch: entry unitId=" + unitId
                          + " trackSource=" + trackSource + " slotIndex=" + slotIndex + " guid=" + guid);
                if (slotIndex < 0 || trackSource < 0 || string.IsNullOrEmpty(guid))
                {
                    Debug.Log("[Multiplayer] BuyAbilityProgressionRelayPatch: slot unresolved — buy suppressed (no relay)");
                    ClearBoughtSlot(t, __instance);
                    return false;
                }
                bool relayed = PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new LevelUpAbilityAction(unitId, (byte)trackSource, slotIndex, guid));
                // Native BuyAbility clears _boughtAbilitySlot after LearnAbility (UIModuleCharacterProgression.cs:418);
                // we suppressed the native call, so mirror that clear here — a still-set slot keeps GeoUiRefresh's
                // progression re-drive permanently skipped ("pending local allocation"), so the host-echoed learn
                // and its SP deduction never repaint on the client (stale SP → the user then over-spends stats the
                // host rejects with sp=0). Cleared on BOTH allow and deny so the pending pick never sticks.
                ClearBoughtSlot(t, __instance);
                return relayed;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BuyAbilityProgressionRelayPatch failed: " + ex.Message); return true; }
        }

        // FIX 3 commit-seam backstop — runs on host (native) and client (Prefix suppressed, Postfix still fires).
        public static void Postfix() => PersonnelEditRelay.CommitSeamBackstop();

        /// <summary>Reset the module's pending-ability pick after a client-suppressed buy — the native cancel idiom
        /// (UIModuleCharacterProgression.ClearBoughtAbility :452 nulls _boughtAbilitySlot + refreshes the tracks).
        /// Best-effort: a miss must never break the suppress path.</summary>
        private static void ClearBoughtSlot(Type t, object module)
        {
            try { AccessTools.Method(t, "ClearBoughtAbility")?.Invoke(module, null); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BuyAbilityProgressionRelayPatch.ClearBoughtSlot failed: " + ex.Message); }
        }
    }

    [HarmonyPatch]
    public static class ChangeCharacterStatThinClientPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "ChangeCharacterStat") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.ChangeCharacterStat thin-client " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // int ChangeCharacterStat(CharacterBaseAttribute baseStat, int cur, int start, bool increase) — the ONE
        // native chokepoint every +/- button routes through (ChangeStrengthStat/ChangeWillStat/ChangeSpeedStat →
        // UIModuleCharacterProgression.cs:848/857/866 → :875). THIN-CLIENT (user mandate, final after 4 hybrid
        // attempts): in an active co-op session the +/- button is a PURE INPUT — the native buffer math is FULLY
        // suppressed on ALL peers (return false), so there is NO optimistic local compute to disagree with the
        // authoritative echo (the crooked-SP failure mode that reverted the earlier POSTFIX per-click relay,
        // 39597c9). We capture only {unitId, statId, ±1 from `increase`}; the HOST applies it at the model + pins
        // its own panel to the result (local-instant); a CLIENT relays the signed intent (host applies, #9 echo
        // re-drives, ~RTT — accepted). Mutoid (mutagen-cost) progression is NOT intercepted (item 7): native runs
        // locally and the existing commit seam handles it. __0 = baseStat (CBA int), __3 = increase (bool);
        // __result forced to 0 so the caller's `_current* += __result` never mutates the buffer.
        public static bool Prefix(object __instance, object __0, bool __3, ref int __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // single-player: native owns the buffer + commit
            if (SyncApplyScope.IsApplying) return true;                    // engine-driven re-drive, not a user click
            try
            {
                var t = __instance.GetType();
                if (AccessTools.Field(t, "_hasPandoranProgression")?.GetValue(__instance) is bool pandoran && pandoran)
                {
                    Debug.Log("[Multiplayer] ChangeCharacterStatThinClientPatch: mutoid (mutagen-cost) progression — not intercepted, native local path");
                    return true;   // item 7: keep current behavior for mutoid
                }
                object character = AccessTools.Field(t, "_character")?.GetValue(__instance);
                long unitId = PersonnelReflection.ReadUnitId(character);
                int statId = Convert.ToInt32(__0);
                int signed = __3 ? 1 : -1;
                __result = 0;   // suppress the optimistic buffer change (native caller does `_current* += __result`)
                if (signed == 1)
                {
                    // Optimistic minus-affordance counter: lift the net on the local +1 click so the (pinned) minus
                    // button can be forced interactable before the host round-trip. Decrement is CONFIRMED-only
                    // (StatEditAffordance.ObserveLiveStat on the panel re-drive) — never here.
                    StatEditAffordance.RecordPlus(unitId, statId);
                    Debug.Log("[Multiplayer] StatEditAffordance: +click counted unit=" + unitId + " stat=" + statId
                              + " net=" + StatEditAffordance.Net(unitId, statId));
                }
                if (engine.IsHost)
                {
                    Debug.Log("[Multiplayer] ChangeCharacterStatThinClientPatch: HOST stat click unit=" + unitId
                              + " stat=" + statId + " delta=" + signed + " → apply+redrive (native suppressed)");
                    PersonnelEditRelay.ApplyHostSelfStatClick(__instance, t, unitId, statId, signed);
                }
                else
                {
                    Debug.Log("[Multiplayer] ChangeCharacterStatThinClientPatch: CLIENT stat click unit=" + unitId
                              + " stat=" + statId + " delta=" + signed + " → relay intent (native suppressed)");
                    PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                        () => new SpendStatPointsAction(unitId, (byte)statId, signed));
                }
                return false;   // suppress the native buffer math on all peers
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ChangeCharacterStatThinClientPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class StatMinusButtonAffordancePatch
    {
        private static MethodBase _target;
        private static MethodInfo _setInteractable;
        // DIAG dedup: (unitId*4 + statId) → last logged forced-decision, so the reason is logged ONCE per change,
        // never per frame (TFTV re-runs the native gate every frame — an unguarded log would flood).
        private static readonly Dictionary<long, bool> _lastLogged = new Dictionary<long, bool>();

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            // NOTE the native typo: "Interactabilty" (missing the second 'i') — decompile-verified.
            _target = t != null ? AccessTools.Method(t, "SetStatButtonInteractabilty") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.SetStatButtonInteractabilty minus-affordance " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // void SetStatButtonInteractabilty(PhoenixGeneralButton statButton, CharacterBaseAttribute stat, bool incrementButton).
        // The native MINUS branch gates on `_current*Stat > _starting*Stat` — permanently false under thin-client
        // (buffer pinned == model). When co-op active and the optimistic counter shows net refundable +clicks,
        // force the minus button interactable so the refund click can actually fire (→ our ChangeCharacterStat
        // PREFIX → −1 intent; host ledger-validates). Non-co-op / mutoid untouched: net is 0 (mutoid never
        // RecordPlus'd, native local path), so the force never triggers and native's value stands.
        // __0 = statButton, __1 = stat (CharacterBaseAttribute), __2 = incrementButton.
        public static void Postfix(object __instance, object __0, object __1, bool __2)
        {
            if (__2) return;   // plus button — native gate is correct
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;   // single-player: native owns the gate
                object character = AccessTools.Field(__instance.GetType(), "_character")?.GetValue(__instance);
                long unitId = PersonnelReflection.ReadUnitId(character);
                int statId = Convert.ToInt32(__1);
                bool force = StatEditAffordance.Net(unitId, statId) > 0;
                if (!force) { LogChange(unitId, statId, forced: false); return; }
                if (_setInteractable == null && __0 != null)
                    _setInteractable = AccessTools.Method(__0.GetType(), "SetInteractable", new[] { typeof(bool) });
                _setInteractable?.Invoke(__0, new object[] { true });
                LogChange(unitId, statId, forced: true);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] StatMinusButtonAffordancePatch failed: " + ex.Message); }
        }

        // Never-silent, once-per-change: log the minus enabled/disabled verdict only when it flips for this (unit,stat).
        private static void LogChange(long unitId, int statId, bool forced)
        {
            long key = unitId * 4 + statId;
            if (_lastLogged.TryGetValue(key, out bool prev) && prev == forced) return;
            _lastLogged[key] = forced;
            Debug.Log("[Multiplayer] StatEditAffordance: minus button " + (forced ? "FORCED interactable" : "left to native (net=0)")
                      + " unit=" + unitId + " stat=" + statId + " net=" + StatEditAffordance.Net(unitId, statId));
        }
    }

    [HarmonyPatch]
    public static class CommitStatChangesProgressionRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "CommitStatChanges") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.CommitStatChanges relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // CommitStatChanges is the native stat-spend commit (screen exit / soldier switch / confirm —
        // UIStateEditSoldier.cs:232/363/715, UIModuleCharacterProgression.cs:367-387). THIN-CLIENT: the +/- clicks
        // are already relayed/applied per click (ChangeCharacterStatThinClientPatch fully suppressed the native
        // buffer math), so this seam has NOTHING to relay — the stat buffer never accumulated a delta.
        //   • HOST / single-player (ShouldRelay=false → return true): native commit RUNS. It is a safe no-op for
        //     stats/SP/faction because the buffer always equals the model (clicks suppressed + the host rebases its
        //     panel to the model after each click, and every remote apply reconciles the open panel) → ModifyBaseStat(0)
        //     and the SkillPoints/faction write-backs write the model value back to itself. Leaving native to run is
        //     also what keeps the nested BuyAbility path working: BuyAbility → ConsumeAbilityCost (buffer) →
        //     CommitStatChanges writes the ability SP cost to the model (:405/375) — suppressing here would grant
        //     free abilities. Mutoid commits its mutagen cost natively.
        //   • CLIENT (frozen mirror): suppress the local commit's model writes and roll the buffer back to starting
        //     (idempotent repeat-commit guard). Nothing to relay. Never-silent DIAG (incl. mutoid path taken).
        public static bool Prefix(object __instance)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;   // HOST/SP: native commit runs (no-op write-back; nested BuyAbility SP cost preserved)
            try
            {
                var t = __instance.GetType();
                bool pandoran = AccessTools.Field(t, "_hasPandoranProgression")?.GetValue(__instance) is bool p && p;
                object character = AccessTools.Field(t, "_character")?.GetValue(__instance);
                long unitId = PersonnelReflection.ReadUnitId(character);
                Debug.Log("[Multiplayer] CommitStatChangesProgressionRelayPatch: CLIENT commit suppressed (thin-client — per-click owns stat sync) unit="
                          + unitId + (pandoran ? " [mutoid: native buffer edit dropped on frozen client]" : ""));
                // Roll local session state back to starting (idempotent repeat-commit guard).
                Reset(t, __instance, "_currentStrengthStat", "_startingStrengthStat");
                Reset(t, __instance, "_currentWillStat", "_startingWillStat");
                Reset(t, __instance, "_currentSpeedStat", "_startingSpeedStat");
                Reset(t, __instance, "_currentSkillPoints", "_startingSkillPoints");
                Reset(t, __instance, "_currentFactionPoints", "_startingFactionPoints");
                Reset(t, __instance, "_currentMutagens", "_startingMutagens");
                return false;   // suppress the frozen local commit (stat + SP + faction-pool + mutagen writes)
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CommitStatChangesProgressionRelayPatch failed: " + ex.Message); return true; }
        }

        // Commit-seam backstop — runs on host (native) and client (Prefix suppressed, Postfix still fires): re-drive
        // the open progression panel once so a same-unit apply that landed unstamped can't leave it stale.
        public static void Postfix() => PersonnelEditRelay.CommitSeamBackstop();

        private static void Reset(Type t, object inst, string currentField, string startingField)
        {
            var cf = AccessTools.Field(t, currentField);
            var sf = AccessTools.Field(t, startingField);
            if (cf != null && sf != null) cf.SetValue(inst, sf.GetValue(inst));
        }
    }

    [HarmonyPatch]
    public static class ChoseSecondSpecializationSuppressPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "ChoseSecondSpecialization") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.ChoseSecondSpecialization suppress " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // Second-specialization buy (dual-class popup, UIModuleCharacterProgression.cs:813-824) runs
        // AddSecondaryClass + ConsumeAbilityCost + CommitStatChanges in one native call. On a client the
        // inner CommitStatChanges is already suppressed (prefix above), which would leave a PHANTOM local
        // second class with its SP cost silently discarded — so suppress the WHOLE buy without relay
        // (denial notify gives feedback; the popup is closed the way the native entry does, :815). The
        // proper intent (SecondSpecialization = 70) is a tracked follow-up in COOP-SYNC-ROADMAP.md.
        public static bool Prefix(object __instance)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                Debug.Log("[Multiplayer] ChoseSecondSpecializationSuppressPatch: second-specialization buy not relayed yet — suppressed on client");
                PermissionGate.Notify(ActionCategory.ControlSoldiers);
                var popup = AccessTools.Field(__instance.GetType(), "DualClassPopupWindow")?.GetValue(__instance) as GameObject;
                popup?.SetActive(false);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ChoseSecondSpecializationSuppressPatch failed: " + ex.Message); }
            return false;   // never run the frozen local AddSecondaryClass
        }
    }

    /// <summary>Transfer ADD side: a client soldier added to a Phoenix container = an assign/transfer intent
    /// (hire's internal AddCharacter never reaches here — HireNakedRecruit is suppressed one level up). Relays
    /// the destination; the host re-derives the source (current container) and runs Remove+Add.</summary>
    internal static class TransferAddRelay
    {
        internal static MethodBase Resolve(string typeName)
        {
            var containerT = AccessTools.TypeByName(typeName);
            var charT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            var m = containerT != null && charT != null ? AccessTools.Method(containerT, "AddCharacter", new[] { charT }) : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: " + typeName + ".AddCharacter transfer relay " + (m != null ? "bound" : "NOT FOUND"));
            return m;
        }
        internal static bool Prefix(object container, object character)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                long unitId = PersonnelReflection.ReadUnitId(character);
                PersonnelEditRelay.ContainerKey(container, out int kind, out int id);
                return PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new TransferSoldierAction(unitId, kind, id));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] TransferAddRelay failed: " + ex.Message); return true; }
        }
    }

    /// <summary>Transfer REMOVE side: on a client, suppress the local frozen remove (the paired Add relays the
    /// transfer; a standalone remove self-heals via the #9/#6 mirror). Host / apply → pass through.</summary>
    internal static class TransferRemoveRelay
    {
        internal static MethodBase Resolve(string typeName)
        {
            var containerT = AccessTools.TypeByName(typeName);
            var charT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            var m = containerT != null && charT != null ? AccessTools.Method(containerT, "RemoveCharacter", new[] { charT }) : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: " + typeName + ".RemoveCharacter transfer suppress " + (m != null ? "bound" : "NOT FOUND"));
            return m;
        }
        internal static bool Prefix()
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            return false;   // suppress the local frozen remove
        }
    }

    [HarmonyPatch]
    public static class GeoVehicleAddCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferAddRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoVehicle"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix(object __instance, object __0) => TransferAddRelay.Prefix(__instance, __0);
    }

    [HarmonyPatch]
    public static class GeoSiteAddCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferAddRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoSite"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix(object __instance, object __0) => TransferAddRelay.Prefix(__instance, __0);
    }

    [HarmonyPatch]
    public static class GeoVehicleRemoveCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferRemoveRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoVehicle"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix() => TransferRemoveRelay.Prefix();
    }

    [HarmonyPatch]
    public static class GeoSiteRemoveCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferRemoveRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoSite"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix() => TransferRemoveRelay.Prefix();
    }
}
