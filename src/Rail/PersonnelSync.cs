using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewControllers.Roster;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Tactical.Entities.Abilities;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Migration step 5 — PERSONNEL PROGRESSION intent seam (law 4a). Same shape as
    /// <see cref="ResearchSync"/>: the client BLOCKS the native mutation and sends an intent; the host
    /// re-runs the SAME native model methods; the result reaches every peer through the generic value
    /// rail (0xAC), which already covers the whole payload — <c>CharacterProgression</c> mirrors
    /// <c>SkillPoints</c> (Leaf), <c>_baseStats</c> (LeafList), <c>_abilities</c> (LeafList),
    /// <c>_abilityTracks</c> (EntityList) and <c>_secondarySpecializationDef</c> (Leaf). Nothing is
    /// echoed by hand and there is NO host→client surface here: this file is intent-only.
    ///
    /// SEAM CHOICE — why the UI commit methods and not the model ones. The task's "chokepoints" are only
    /// half patchable: <c>CharacterProgression.SkillPoints</c> (:24) and <c>GeoPhoenixFaction.Skillpoints</c>
    /// (:95) are public FIELDS, not properties, so Harmony cannot see them written, and the writes happen
    /// inline in <c>UIModuleCharacterProgression.CommitStatChanges</c> (:375/:378). A prefix on
    /// <c>ModifyBaseStat</c> alone would therefore block the stat half of a spend and let the SP half
    /// through, which is worse than not patching at all. Meanwhile <c>AddAbility</c> is reached by the
    /// client's OWN mirror paths (<c>GeoUnitDescriptor</c>:508 deserialize, <c>GeoCharacter</c>:1048 clone,
    /// <c>ResetAbilities</c>:309), so a blanket block there would corrupt roster mirroring.
    ///
    /// The UI methods below ARE the family's chokepoints, not "one button" — every geoscape entry
    /// point funnels through them (verified by call-site sweep over the decompile):
    ///   • <c>ChangeCharacterStat</c> (:875) — the ONE stat up/down CLICK funnel (all six buttons route
    ///     through ChangeStrengthStat/ChangeWillStat/ChangeSpeedStat :848/:857/:866); returns ±1 on a
    ///     staged gesture, 0 on a refused one. Law 11: each accepted click is its own op=1 intent
    ///     (client) / immediate model apply (host) — reactivity per GESTURE, never deferred to the
    ///     screen-exit commit (the pre-2026-07-25 lazy path the user reported as the stat-spend bug).
    ///   • <c>CommitStatChanges</c> (:367) — natively the ONE stage→model flush (base stats + ABSOLUTE
    ///     SP pools, :375/:378; reached from UIStateEditSoldier:232/:363/:715 and the two methods
    ///     below). In a session the model flush is SKIPPED on every peer (per-click seams already put
    ///     everything in the model; the absolute pool write is exactly what reverted foreign SP spends
    ///     when a peer sat on a stale stage — review risk 2026-07-25), but its baseline tail
    ///     (_starting* := _current*, :370-374/:384-386) is REPLICATED, else RefreshStats (:520-521)
    ///     reseeds the stage from pre-gesture pools. Skip = no double apply, by construction.
    ///   • <c>ConsumeAbilityCost</c> (:428) — the staged SP debit of a perk/second-class buy; with the
    ///     commit skipped the HOST applies its stage delta to the model right here (mutoid branch :432
    ///     already hits the wallet natively). Client never reaches it — both callers are blocked below.
    ///   • <c>BuyAbility</c> (:389) — ability/perk purchase (human LearnAbility + mutoid AddAbility).
    ///   • <c>ChoseSecondSpecialization</c> (:813) — the second-class purchase (AddSecondaryClass).
    ///   • <c>UIStateGeoRoster.OnActionSlotTransferInitiated</c> (:292) — the ONE roster base⇄vehicle
    ///     transfer seam (drag and action-menu both funnel through it).
    ///   • <c>GeoCharacter.ResetCharacterProgression</c> (:604) — the skill-reset (respec); here the
    ///     MODEL funnel is patchable directly, so the seam sits below the UI (see SkillResetPatch).
    ///   • <c>GeoHaven.TakeRecruit</c> (:810) / <c>GeoPhoenixFaction.KillCharacter</c> (:1377,
    ///     Dismissed only) — hire and fire, both on their MODEL funnels (HavenHireCapturePatch /
    ///     DismissCapturePatch; naked-recruit hire is BLOCKED client-side pending a stable key).
    ///
    /// HOST replay runs the identical native model calls those seams make: <c>ModifyBaseStat</c> (:369-373),
    /// <c>LearnAbility</c>/<c>AddAbility</c> (:416/:408), <c>AddSecondaryClass</c> (:820),
    /// <c>ResetCharacterProgression</c> (GeoCharacter.cs:604) — with the SP
    /// economy re-derived from the HOST's own numbers (<see cref="Charge"/>), never from the wire.
    ///
    /// UNDO ("туда-сюда") — native staging stays ALIVE on both peers: ChangeCharacterStat's own gates
    /// (floor at the screen-entry value :907, affordability :892-903, minus-button interactability
    /// :795-808) keep gating clicks, so each minus gesture is a legal −1 the host applies as the
    /// symmetric refund (cost of stepping down from v == cost of stepping up to v, GetBaseStatCost).
    /// +1 then −1 nets to zero on every peer; leaving an untouched screen sends nothing. The staged
    /// floor survives the gesture's own echo because <see cref="ProgressionPanelInSync"/> lets the
    /// repaint skip the reseed when the stage already shows the post-delta model.
    ///
    /// WHAT THE WIRE DELIBERATELY DOES NOT CARRY: any balance. Both halves of the SP economy are
    /// rail-covered Leafs (rail-baseline.txt: <c>CharacterProgression.SkillPoints</c> :95,
    /// <c>GeoPhoenixFaction.Skillpoints</c> :316), so a host-side debit reaches every client through the
    /// 0xAC diff. Shipping a client's own numbers would make it authoritative over a shared resource
    /// (law 3) — the host re-derives cost and pool from its own state (<see cref="Charge"/>).
    /// </summary>
    public static class PersonnelSync
    {
        // Intent ops (GeoPersonnelIntent inner payload) — each maps onto the native commit it replaces.
        private const byte OpSpendStats = 1;  // one stat click (±1)         → ModifyBaseStat + SP debit/refund
        private const byte OpBuyAbility = 2;  // BuyAbility                  → LearnAbility / AddAbility
        private const byte OpSecondSpec = 3;  // ChoseSecondSpecialization   → AddSecondaryClass
        private const byte OpReassign = 4;    // OnActionSlotTransferInitiated → RemoveCharacter/AddCharacter
        private const byte OpSkillReset = 5;  // UseAllowedAbilityReset       → ResetCharacterProgression (free respec, _hasSkillReset-gated)
        private const byte OpHire = 6;        // haven recruit purchase       → GeoHaven.TakeRecruit
        private const byte OpFire = 7;        // dismiss / scrap              → KillCharacter(Dismissed) (+ vehicle ScrapPrice tail)

        // ─── Reflection: UIModuleCharacterProgression's staged view-model (all private) ──
        // The module stages the player's pending edit in _current*; _character is the soldier it is bound
        // to. We read them to build the intent and never write any of them.
        private static readonly FieldInfo FCharacter = AccessTools.Field(typeof(UIModuleCharacterProgression), "_character");
        private static readonly FieldInfo FCurStrength = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentStrengthStat");
        private static readonly FieldInfo FCurWill = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentWillStat");
        private static readonly FieldInfo FCurSpeed = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentSpeedStat");
        // The _current* stats are DERIVED display values (RefreshStats:515-518 seeds them from
        // GetProgressionBaseStats() + the Bonus* stats, i.e. GetBaseStat(x) + Σ bodyPart aspect +
        // augment bonus) — same scale ApplyStats validates on. The staged pool copies are read to
        // (a) diff a click's pool cost on the host and (b) detect stage==model for the reseed skip.
        private static readonly FieldInfo FCurSkillPoints = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentSkillPoints");
        private static readonly FieldInfo FCurFactionPoints = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentFactionPoints");
        private static readonly FieldInfo FCurMutagens = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentMutagens");
        private static readonly FieldInfo FBoughtSlot = AccessTools.Field(typeof(UIModuleCharacterProgression), "_boughtAbilitySlot");
        private static readonly FieldInfo FBoughtAbility = AccessTools.Field(typeof(UIModuleCharacterProgression), "_boughtAbility");
        private static readonly FieldInfo FBoughtSource = AccessTools.Field(typeof(UIModuleCharacterProgression), "_boughtAbilitySource");
        private static readonly FieldInfo FBoughtLevel = AccessTools.Field(typeof(UIModuleCharacterProgression), "_boughtAbilityLevel");
        // Stage BASELINES (_starting*) — native CommitStatChanges' tail resets them to _current*
        // (:370/:372/:374 stats, :384-386 pools) and RefreshStats reseeds the pool stage FROM them
        // (:520-521). With the model flush skipped in-session, that tail is replicated by
        // <see cref="CommitStatsNeutralizePatch"/> — the only place these are written.
        private static readonly FieldInfo FStartStrength = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingStrengthStat");
        private static readonly FieldInfo FStartWill = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingWillStat");
        private static readonly FieldInfo FStartSpeed = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingSpeedStat");
        private static readonly FieldInfo FStartSkillPoints = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingSkillPoints");
        private static readonly FieldInfo FStartFactionPoints = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingFactionPoints");
        private static readonly FieldInfo FStartMutagens = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingMutagens");

        /// <summary>Loud bind check — a decompile-name drift makes every FieldInfo above null SILENTLY and
        /// the client would then fall through to writing the model locally. Checked once, at first use.</summary>
        private static bool _bindChecked;

        private static bool BindOk()
        {
            if (!_bindChecked)
            {
                _bindChecked = true;
                if (FCharacter == null || FCurStrength == null || FCurWill == null || FCurSpeed == null ||
                    FCurSkillPoints == null || FCurFactionPoints == null || FCurMutagens == null ||
                    FBoughtSlot == null || FBoughtAbility == null || FBoughtSource == null || FBoughtLevel == null ||
                    FStartStrength == null || FStartWill == null || FStartSpeed == null ||
                    FStartSkillPoints == null || FStartFactionPoints == null || FStartMutagens == null)
                    Debug.LogError("[MP][personnel] FIELD BIND FAILED on UIModuleCharacterProgression — " +
                                   "stat/ability intents CANNOT be captured; client edits will not sync.");
                else
                    Debug.Log("[MP][personnel] view-model fields bound");
            }
            // All-or-nothing: callers (ProgressionPanelInSync, the commit-tail replication) dereference the
            // full set after one positive answer, so a partial bind must read as NOT bound.
            return FCharacter != null && FCurStrength != null && FCurWill != null && FCurSpeed != null &&
                   FCurSkillPoints != null && FCurFactionPoints != null && FCurMutagens != null &&
                   FBoughtSlot != null && FBoughtAbility != null && FBoughtSource != null && FBoughtLevel != null &&
                   FStartStrength != null && FStartWill != null && FStartSpeed != null &&
                   FStartSkillPoints != null && FStartFactionPoints != null && FStartMutagens != null;
        }

        public static void Reset() => ResetForReloadBoundary();

        /// <summary>Mid-session reload boundary (same rca-3 contract as
        /// <see cref="ResearchSync.ResetForReloadBoundary"/>): the wire-op ledgers key on GeoTacUnitIds of
        /// the dying geoscape — drop them with it (the reloaded save re-establishes what "committed"
        /// means); dedup/nonce live in <see cref="IntentRail"/>, reset there.</summary>
        public static void ResetForReloadBoundary()
        {
            WireStatNet.Clear();
            WireFactionTaken.Clear();
            _floorCharId = -1; // visit floor keys on the dying geoscape's ids too
        }

        /// <summary>Arm the 0xAF surface on the generic intent engine: transport + dedup + reject
        /// discipline in <see cref="IntentRail"/>, ALL personnel validation/native replay stays below.
        /// No family reconverge: rejects pass the touched character subtree ("U#&lt;charId&gt;") per
        /// call site — every personnel outcome rides the value rail, so a scoped re-emit reaches it.</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpSpendStats] = HandleIntentOp,
                [OpBuyAbility] = HandleIntentOp,
                [OpSecondSpec] = HandleIntentOp,
                [OpReassign] = HandleIntentOp,
                [OpSkillReset] = HandleIntentOp,
                [OpHire] = HandleHireIntent,   // no charId — the recruit does not exist yet
                [OpFire] = HandleFireIntent,   // charId, but no Progression requirement (vehicles/mutogs)
            };
            IntentRail.Register(SurfaceIds.GeoPersonnelIntent, "personnel", ops);
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── CLIENT: the one intent-capture decision (law 4a + law 8) ──────

        /// <summary>TRUE = let the native commit run (host, solo, or inside an apply). FALSE = this peer is
        /// a client and must not write the model; the caller sends an intent instead (or nothing, when the
        /// gesture turned out to be a no-op).</summary>
        private static bool ShouldRunNative()
        {
            DiffEngine.FlushOnHostGesture();
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            return SyncApplyScope.Active; // law 8: applying a delta never echoes an intent
        }

        // ─── Harmony seams (law 4a, intent-capture only — this file owns no other patch) ──

        /// <summary>
        /// Per-CLICK seam (law 11): every stat up/down gesture the native stage ACCEPTED (__result ±1;
        /// 0 = native's own floor/affordability gates :892-931 refused it — zero traffic) becomes model
        /// truth immediately. The native body always runs first — it is pure VIEW staging (updates
        /// _current* copies, no model write), and keeping it alive keeps the whole native UX: instant
        /// numbers, minus-button floor at screen entry (:795-808), affordability greying.
        ///   • CLIENT: ship the gesture as op=1 with a single-stat ±1 delta (existing wire shape). The
        ///     model stays untouched until the host's delta mirrors back (law 3) — the stage is only a
        ///     local preview, and <see cref="ProgressionPanelInSync"/> keeps the echo from wiping it.
        ///   • HOST: apply the exact staged delta to the model here — ModifyBaseStat(±1) plus the pool
        ///     delta the native body just staged (prefix snapshot diff; mutoid clicks stage mutagens
        ///     → wallet). The rail's diff tick then carries it to every client: host spends are as
        ///     reactive as client ones.
        /// No double apply: <see cref="CommitStatsNeutralizePatch"/> skips the native stage→model flush
        /// for the whole session, so a click is applied exactly once — here (host) or via the intent
        /// replay (client) — and never again at screen exit / soldier switch / post-buy.
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), "ChangeCharacterStat")]
        internal static class StatClickPatch
        {
            private static void Prefix(UIModuleCharacterProgression __instance, out int[] __state)
            {
                __state = SnapshotPools(__instance);
            }

            private static void Postfix(UIModuleCharacterProgression __instance, CharacterBaseAttribute baseStat,
                                        int[] __state, int __result)
            {
                if (__result == 0 || __state == null) return;
                DiffEngine.FlushOnHostGesture();
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return; // solo: fully native
                if (SyncApplyScope.Active) return;                     // law 8: never echo an apply
                if (!BindOk()) return;
                try
                {
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    if (character?.Progression == null) return;

                    if (!engine.IsHost)
                    {
                        int dStr = baseStat == CharacterBaseAttribute.Strength ? __result : 0;
                        int dWill = baseStat == CharacterBaseAttribute.Will ? __result : 0;
                        int dSpeed = baseStat == CharacterBaseAttribute.Speed ? __result : 0;
                        IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpSpendStats,
                            "stats U#" + (int)character.Id + " dStr=" + dStr + " dWill=" + dWill + " dSpeed=" + dSpeed,
                            w => { w.Write((int)character.Id); w.Write(dStr); w.Write(dWill); w.Write(dSpeed); });
                        return;
                    }

                    character.Progression.ModifyBaseStat(baseStat, __result); // fires the native derived-stat recompute
                    ApplyStagedPoolDelta(__instance, character, __state);
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] stat click seam failed: " + ex); }
            }
        }

        /// <summary>
        /// HOST model apply for the staged SP debit of a perk / second-class buy (ConsumeAbilityCost:435-441
        /// — human branch stages _currentSkillPoints/_currentFactionPoints; the mutoid branch :432 already
        /// hits the wallet natively, its stage diff is zero here). Needed because the commit that natively
        /// flushed this stage is skipped for the session. Client never runs the native callers (BuyAbility /
        /// ChoseSecondSpecialization are blocked below), so this is host-only by construction — the IsHost
        /// gate is the belt.
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), "ConsumeAbilityCost")]
        internal static class ConsumeCostApplyPatch
        {
            private static void Prefix(UIModuleCharacterProgression __instance, out int[] __state)
            {
                __state = SnapshotPools(__instance);
            }

            private static void Postfix(UIModuleCharacterProgression __instance, int[] __state)
            {
                DiffEngine.FlushOnHostGesture();
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                if (SyncApplyScope.Active || __state == null || !BindOk()) return;
                try
                {
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    if (character?.Progression == null) return;
                    ApplyStagedPoolDelta(__instance, character, __state);
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] consume-cost seam failed: " + ex); }
            }
        }

        /// <summary>[SP, factionSP, mutagens] staged copies — null when the fields did not bind.</summary>
        private static int[] SnapshotPools(UIModuleCharacterProgression module)
        {
            if (!BindOk() || FCurFactionPoints == null || FCurMutagens == null) return null;
            return new[]
            {
                (int)FCurSkillPoints.GetValue(module),
                (int)FCurFactionPoints.GetValue(module),
                (int)FCurMutagens.GetValue(module),
            };
        }

        /// <summary>Move what the native body just STAGED into the model, relatively (never absolutes —
        /// the absolute flush is the stale-stage revert this family kills). Wallet mutagens mirror the
        /// native commit's own delta form (:380-382).</summary>
        private static void ApplyStagedPoolDelta(UIModuleCharacterProgression module, GeoCharacter character, int[] before)
        {
            int dSp = (int)FCurSkillPoints.GetValue(module) - before[0];
            int dFp = (int)FCurFactionPoints.GetValue(module) - before[1];
            int dMut = (int)FCurMutagens.GetValue(module) - before[2];
            if (dSp != 0) character.Progression.SkillPoints += dSp;
            if (dFp != 0 && character.Faction is GeoPhoenixFaction phoenix) phoenix.Skillpoints += dFp;
            if (dMut < 0) character.Faction.Wallet.Take(new ResourceUnit(ResourceType.Mutagen, -dMut), OperationReason.Purchase);
            else if (dMut > 0) character.Faction.Wallet.Give(new ResourceUnit(ResourceType.Mutagen, dMut), OperationReason.Refund);
        }

        /// <summary>
        /// Session-wide neutralization of the native stage→model FLUSH, BOTH peers — but ONLY the flush.
        /// Everything it would transfer already reached the model at gesture time (host:
        /// <see cref="StatClickPatch"/> + <see cref="ConsumeCostApplyPatch"/>; client: the op=1 replay),
        /// so letting it run could only (a) double-apply the staged diff or (b) write the ABSOLUTE SP
        /// pools (:375/:378) from a stage that went stale while a foreign spend landed — the host-side
        /// revert found in review. The native body's baseline TAIL is kept alive though: CommitStatChanges
        /// also resets the stage floor (_starting* := _current*, stats :370/:372/:374, pools :384-386),
        /// and RefreshStats reseeds the pool stage FROM those baselines (:520-521). A pure skip left them
        /// stale, so after a host perk buy ConfirmationHandler (UIStateEditSoldier:716) re-showed PRE-buy
        /// SP, the next click gated against the inflated stage, and its snapshot diff drove model
        /// SkillPoints negative (review 2026-07-25 HIGH). An exit with no gestures stays zero traffic
        /// (stage == baseline already; the copy is a no-op).
        ///
        /// Composes with <see cref="EquipFlushGate"/>, which also prefixes this method (it blocks inside
        /// an apply and during session teardown; Harmony skips later prefixes once one returns false) —
        /// the tail replication re-checks those same two conditions itself, so the composed behavior does
        /// not depend on prefix order.
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.CommitStatChanges))]
        internal static class CommitStatsNeutralizePatch
        {
            private static bool Prefix(UIModuleCharacterProgression __instance)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return true; // solo: fully native
                if (!SessionEnd.InProgress && !SyncApplyScope.Active && BindOk())
                {
                    // Native baseline tail, verbatim minus the model writes.
                    FStartStrength.SetValue(__instance, FCurStrength.GetValue(__instance));
                    FStartWill.SetValue(__instance, FCurWill.GetValue(__instance));
                    FStartSpeed.SetValue(__instance, FCurSpeed.GetValue(__instance));
                    FStartSkillPoints.SetValue(__instance, FCurSkillPoints.GetValue(__instance));
                    FStartFactionPoints.SetValue(__instance, FCurFactionPoints.GetValue(__instance));
                    FStartMutagens.SetValue(__instance, FCurMutagens.GetValue(__instance));
                }
                return false; // in-session: the stage→model flush stays skipped on host AND client
            }
        }

        /// <summary>
        /// The undo floor SURVIVES mid-visit reseeds (in-game RCA 2026-07-25: spend→undo→spend→
        /// "уже не откатить"). The minus gate and its refund walk live on the panel's stage floor
        /// (_starting* stats — ChangeCharacterStat:907 gate, :795-808 interactability), and native
        /// RefreshStats OVERWRITES that floor with the current display (:516-518) on every full reseed.
        /// Solo that happens once per visit (entry / soldier switch / post-buy), but in a session ANY
        /// foreign delta that moves one of ProgressionPanelInSync's six compared values lands a full
        /// reseed on the open panel (UiEventMap edit-soldier entry → SelectCharacterProgression), so the
        /// FIRST foreign batch after a spend silently reset the floor to the spent value — the next
        /// minus click failed the :907 gate with ZERO traffic (repro log: U#6 nonces 9/+1, 10/−1, 11/+1
        /// all HOST-APPLIED, no nonce 12 ever sent; no host REJECT anywhere — the wire ledgers were
        /// innocent). Fix at the ONE floor writer: remember the visit-entry floor per bound character
        /// and restore it after every later RefreshStats for the SAME character, clamped to the fresh
        /// display (min — a respec/foreign drop lowers the floor, never raises it). Session-scoped and
        /// view-only; solo path untouched. Both peers: the host's own open panel loses its floor to
        /// client-driven repaints the same way. The HOST's wire floor (<see cref="WireStatNet"/>) stays
        /// the trust boundary — a restored floor can only re-ALLOW the gesture, every refund is still
        /// host-derived. ponytail: after a reseed the stage's faction-refund memory
        /// (_startingFactionPoints gap) is gone, so a host-own-click undo of a faction-spilled spend
        /// refunds personal — total conserved, no mint; wire refunds keep the exact split via
        /// WireFactionTaken. Upgrade path if it ever matters: ledger host own-click spills too.
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.RefreshStats))]
        internal static class StatFloorKeepPatch
        {
            private static void Postfix(UIModuleCharacterProgression __instance)
            {
                try
                {
                    var engine = NetworkEngine.Instance;
                    if (engine == null || !engine.IsActiveSession) { _floorCharId = -1; return; } // solo: native semantics
                    if (!BindOk()) return;
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    if (character == null || character.TemplateDef == null || !character.TemplateDef.IsHuman)
                    { _floorCharId = -1; return; } // vehicle panel has no stat stage
                    int id = (int)character.Id;
                    if (id != _floorCharId)
                    {
                        // New bind = the visit floor is born here, from the values native just seeded.
                        _floorCharId = id;
                        _floorStats = new[]
                        {
                            (int)FStartStrength.GetValue(__instance),
                            (int)FStartWill.GetValue(__instance),
                            (int)FStartSpeed.GetValue(__instance),
                        };
                        return;
                    }
                    // Same character, later reseed: native just reset the floor to the display — restore.
                    FStartStrength.SetValue(__instance, Math.Min(_floorStats[0], (int)FStartStrength.GetValue(__instance)));
                    FStartWill.SetValue(__instance, Math.Min(_floorStats[1], (int)FStartWill.GetValue(__instance)));
                    FStartSpeed.SetValue(__instance, Math.Min(_floorStats[2], (int)FStartSpeed.GetValue(__instance)));
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] stat-floor keep failed: " + ex); }
            }
        }

        private static int _floorCharId = -1;   // GeoTacUnitId the kept floor belongs to; -1 = none
        private static int[] _floorStats;       // [Str,Will,Speed] display values at visit entry

        /// <summary>
        /// Reseed skip for the repaint seam (UiNativeRepaint's edit-soldier / edit-vehicle entries): TRUE
        /// when the progression panel's staged copies already equal the model for the bound character —
        /// i.e. the arriving delta is the echo of this machine's own gesture and a reseed would repaint
        /// ZERO difference while destroying the only thing it holds: the staged floor (_starting*) that
        /// keeps the minus button lit (:795-808) and the decrement gate open (:907). Any foreign change
        /// (someone else's spend, a perk landing, an augment shifting Bonus*) mismatches ≥1 value and the
        /// full native reseed runs, floor reset included — the same reset native itself does after a buy
        /// (UIStateEditSoldier:716). ponytail: a ZERO-cost ability buy moves no compared number and would
        /// be skipped — no vanilla ability is SP-free; drop the skip for abilities-count too if a mod ships one.
        /// </summary>
        internal static bool ProgressionPanelInSync(UIModuleCharacterProgression module, GeoCharacter current)
        {
            try
            {
                if (module == null || current == null || !BindOk()) return false;
                if (!ReferenceEquals(FCharacter.GetValue(module), current)) return false;
                if (current.TemplateDef == null || !current.TemplateDef.IsHuman) return false; // vehicle panel never stages
                var progression = current.Progression;
                if (progression == null || current.Faction == null) return false;
                var baseStats = current.GetProgressionBaseStats();
                if ((int)FCurStrength.GetValue(module) != (int)(baseStats.Endurance + current.BonusStrength)) return false;
                if ((int)FCurWill.GetValue(module) != (int)(baseStats.Willpower + current.BonusWillpower)) return false;
                if ((int)FCurSpeed.GetValue(module) != (int)(baseStats.Speed + current.BonusSpeed)) return false;
                if ((int)FCurSkillPoints.GetValue(module) != progression.SkillPoints) return false;
                var phoenix = current.Faction as GeoPhoenixFaction;
                if ((int)FCurFactionPoints.GetValue(module) != (phoenix == null ? 0 : phoenix.Skillpoints)) return false;
                if ((int)FCurMutagens.GetValue(module) != current.Faction.Wallet[ResourceType.Mutagen].RoundedValue) return false;
                return true;
            }
            catch { return false; } // any doubt → full reseed, never a stale panel
        }

        /// <summary>Intent capture for an ability/perk purchase. The bought slot is addressed by its TRACK
        /// (source) + LEVEL — <c>AbilityTrack.GetAbilityLevel</c>/<c>GetAbilitySlotForLevel</c> are exact
        /// inverses (AbilityTrack.cs:38-63), so this is a stable address, not an index into a live list.
        /// The ability guid rides along because a mutoid slot can be EMPTY until the buy stamps it
        /// (UIModuleCharacterProgression.cs:393-395).</summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.BuyAbility))]
        internal static class BuyAbilityCapturePatch
        {
            private static bool Prefix(UIModuleCharacterProgression __instance)
            {
                if (ShouldRunNative()) return true;
                if (!BindOk()) return false;
                try
                {
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    var progression = character?.Progression;
                    var slot = FBoughtSlot.GetValue(__instance) as AbilityTrackSlot;
                    if (progression == null || slot == null) return false; // native no-ops on a null slot too

                    var source = (AbilityTrackSource)FBoughtSource.GetValue(__instance);
                    var ability = FBoughtAbility.GetValue(__instance) as TacticalAbilityDef;
                    int buttonLevel = (int)FBoughtLevel.GetValue(__instance);
                    var track = progression.GetAbilityTrack(source);
                    int slotLevel = track == null ? 0 : track.GetAbilityLevel(slot); // 0 = not in this track
                    if (slotLevel <= 0)
                    {
                        Debug.LogWarning("[MP][personnel] CLIENT ability buy dropped — slot not on track " + source);
                        return false;
                    }

                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpBuyAbility,
                        "ability U#" + (int)character.Id + " track=" + source + " lvl=" + slotLevel,
                        w =>
                        {
                            w.Write((int)character.Id);
                            w.Write((int)source);
                            w.Write(slotLevel);
                            w.Write(buttonLevel);
                            w.Write(ability == null ? "" : ability.Guid);
                        });
                    // Mirror the native tail (:418) so the confirm button releases and no stale slot can be
                    // re-bought. Public, view-only — it nulls _boughtAbilitySlot and refreshes the widgets.
                    __instance.ClearBoughtAbility();
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] ability capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Intent capture for the second-specialization purchase. Native's first act is closing the
        /// dual-class popup (:815); we do that too, otherwise blocking the method strands it on screen.</summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.ChoseSecondSpecialization))]
        internal static class SecondSpecCapturePatch
        {
            private static bool Prefix(UIModuleCharacterProgression __instance, SpecializationDef specialization)
            {
                if (ShouldRunNative()) return true;
                if (!BindOk()) return false;
                try
                {
                    if (__instance.DualClassPopupWindow != null) __instance.DualClassPopupWindow.SetActive(false);
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    if (character == null || specialization == null) return false;

                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpSecondSpec,
                        "secondSpec U#" + (int)character.Id + " spec=" + specialization.Guid,
                        w => { w.Write((int)character.Id); w.Write(specialization.Guid); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] second-spec capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Intent capture for the roster base⇄vehicle transfer. ONE chokepoint (decompile
        /// UIStateGeoRoster.cs:292-300): drag and action-menu transfers both funnel into
        /// <c>OnActionSlotTransferInitiated</c> → <c>source.RemoveCharacter</c> +
        /// <c>destination.AddCharacter</c> on the <c>_tacUnits</c> lists (GeoVehicle.cs:759-770 /
        /// GeoSite.cs:983-993) — both rail-covered LeafLists, so the host outcome reaches every peer on
        /// the value rail and UIStateGeoRoster's existing UiNativeRepaint entry repaints the open
        /// roster. CLIENT: block the whole method (the module's slot-move view tail included — the
        /// roster re-Inits from the mirrored containers when the delta lands) and ship
        /// [charId, dstRef], dstRef = the container's root ref ("S#&lt;id&gt;"/"V#&lt;id&gt;").</summary>
        [HarmonyPatch(typeof(UIStateGeoRoster), "OnActionSlotTransferInitiated")]
        internal static class RosterTransferCapturePatch
        {
            private static bool Prefix(GeoRosterItem slot, IGeoCharacterContainer destination)
            {
                if (ShouldRunNative()) return true;
                try
                {
                    var character = slot == null ? null : slot.Character;
                    var dstRef = IdentityResolver.RootRef(destination);
                    if (character == null || dstRef == null) return false; // unaddressable → drop the gesture
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpReassign,
                        "reassign U#" + (int)character.Id + " -> " + dstRef,
                        w => { w.Write((int)character.Id); w.Write(dstRef); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] reassign capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Intent capture for the haven recruit purchase, on the MODEL funnel
        /// <c>GeoHaven.TakeRecruit</c> (GeoHaven.cs:810-836 — cost derived+charged :817-818, recruit
        /// spawned :824, reward container :827, AvailableRecruit cleared :829; both UI callers route
        /// here: HavenFacilityItemController:576, HavenInteractionController:257). Wire = the two
        /// stable root refs only — the recruit itself has no id yet and the HOST's own
        /// <c>AvailableRecruit</c>/<c>GetRecruitCost</c> are the only truth (law 3).</summary>
        [HarmonyPatch(typeof(GeoHaven), nameof(GeoHaven.TakeRecruit))]
        internal static class HavenHireCapturePatch
        {
            private static bool Prefix(GeoHaven __instance, GeoVehicle vehicle, ref IGeoCharacterContainer __result)
            {
                if (ShouldRunNative()) return true;
                __result = null; // both callers ignore it; the mirrored outcome repaints the haven UI
                try
                {
                    var siteRef = __instance.Site == null ? null : IdentityResolver.RootRef(__instance.Site);
                    var vehicleRef = vehicle == null ? null : IdentityResolver.RootRef(vehicle);
                    if (siteRef == null || vehicleRef == null) return false; // unaddressable → drop the gesture
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpHire,
                        "hire " + siteRef + " via " + vehicleRef,
                        w => { w.Write(siteRef); w.Write(vehicleRef); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] hire capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Intent capture for dismiss/scrap, on the MODEL funnel
        /// <c>GeoPhoenixFaction.KillCharacter</c> — gated to <c>reason == Dismissed</c>, the ONE reason
        /// only user gestures pass (UIStateEditSoldier:425, UIStateEditVehicle:556, UIStateViewVehicle's
        /// twin callback); every other death reason (combat, events, host sim) stays fully native so
        /// mirror/tactical paths are untouched. Patching the OVERRIDE (GeoPhoenixFaction.cs:1377), not
        /// the base — TFTV patches this same target (TFTVBaseRework\PersonnelDismissal.cs:158), so the
        /// host replay re-runs TFTV's civilian-conversion prefix natively and its outcome mirrors; with
        /// TFTV installed the CLIENT-side ordering of the two prefixes is untested (flagged in report).
        /// Known accepted drift: the vehicle-scrap UI callback also Gives ScrapPrice LOCALLY after this
        /// block (UIStateEditVehicle:563) — wallet Leafs are absolute, so the host echo (which performs
        /// the same Give via <see cref="GiveVehicleScrap"/>) overwrites it within the echo.</summary>
        [HarmonyPatch(typeof(GeoPhoenixFaction), nameof(GeoPhoenixFaction.KillCharacter))]
        internal static class DismissCapturePatch
        {
            private static bool Prefix(GeoCharacter unit, CharacterDeathReason reason)
            {
                if (reason != CharacterDeathReason.Dismissed) return true; // gesture-only seam
                if (ShouldRunNative()) return true;
                try
                {
                    if (unit != null)
                        IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpFire,
                            "fire U#" + (int)unit.Id, w => w.Write((int)unit.Id));
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] fire capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>CLIENT block (no intent yet) for the base Recruits-tab hire
        /// (<c>GeoPhoenixFaction.HireNakedRecruit</c>, :662-671, UIStateRosterRecruits:301): a
        /// GeoUnitDescriptor has NO stable wire id, so this flow cannot ride the intent rail until the
        /// structural layer keys the recruit lists. Blocking beats the alternative — a locally spawned
        /// soldier no other peer knows = permanent identity divergence (law 3). The UI's own
        /// Wallet.Take (:300) still runs locally; wallet Leafs are absolute, the next host echo
        /// overwrites it. ponytail: naked-recruit hire op once recruit lists have stable keys.</summary>
        [HarmonyPatch(typeof(GeoPhoenixFaction), nameof(GeoPhoenixFaction.HireNakedRecruit))]
        internal static class NakedHireBlockPatch
        {
            private static bool Prefix()
            {
                if (ShouldRunNative()) return true;
                Debug.LogWarning("[MP][personnel] CLIENT naked-recruit hire blocked — not wired to the rail yet (no stable recruit key)");
                return false;
            }
        }

        // ─── HOST: resolve → validate → execute the SAME native methods (dedup/decode/reject = IntentRail) ──

        private static void HandleIntentOp(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();

                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return; }
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character) ||
                    character.Progression == null)
                { Reject(senderPeerId, charId, "unresolved character"); return; }
                // Ownership: the rail resolves ANY character — never let a client intent drive an
                // NPC-faction soldier's progression.
                if (!ReferenceEquals(character.Faction, geo.PhoenixFaction))
                { Reject(senderPeerId, charId, "not a Phoenix soldier"); return; }

                bool ok;
                switch (op)
                {
                    case OpSpendStats:
                        ok = ApplyStats(senderPeerId, character, r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                        break;
                    case OpBuyAbility:
                        ok = ApplyBuyAbility(senderPeerId, character, (AbilityTrackSource)r.ReadInt32(),
                                             r.ReadInt32(), r.ReadInt32(), r.ReadString());
                        break;
                    case OpReassign:
                        ok = ApplyReassign(senderPeerId, geo, character, r.ReadString());
                        break;
                    case OpSkillReset:
                        ok = ApplySkillReset(senderPeerId, character);
                        break;
                    default: // OpSecondSpec (the op set is table-gated upstream)
                        ok = ApplySecondSpec(senderPeerId, character, r.ReadString());
                        break;
                }
                if (ok)
                {
                    Debug.Log("[MP][personnel] HOST intent APPLIED op=" + op + " char=U#" + charId +
                              " nonce=" + nonce + " peer=" + senderPeerId);
                    // Law 11 (host side): we ran the native model methods DIRECTLY, not through the
                    // host's own progression module, so its open screen still shows pre-intent numbers.
                    // Repaint through the EXISTING universal seam — no personnel-specific repaint path.
                    // Dirty-mark only: the flush owns drag/typing defer + per-frame coalescing.
                    // (Clients get theirs from the 0xAC batch via UiEventMap.)
                    OpenUiRepaint.MarkDirty();
                }
            }
            catch (Exception ex)
            {
                // Native validation throws (AddSecondaryClass) double as the reject path — caught HERE,
                // not in IntentRail's dispatch, so the reject still carries the character subtree.
                Reject(senderPeerId, charId, "(throw) " + ex.Message);
            }
        }

        /// <summary>Replay of <c>GeoHaven.TakeRecruit</c> with the native UI's own gates re-checked
        /// host-side (HavenFacilityItemController:420 CanRecruitCharacter, docked-vehicle listing):
        /// cost/recruit/capacity all re-derived from HOST state, nothing priced by the wire. RACE:
        /// intents dispatch sequentially on the host loop — the second hire of the same recruit sees
        /// <c>AvailableRecruit == null</c> (cleared by the first replay's RemoveRecruit :829) and
        /// rejects; the loser's stale haven panel heals via the "S#" subtree re-emit (law 7). No
        /// double-spend: the client never touched its wallet (capture blocked the whole native body).</summary>
        private static void HandleHireIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string siteRef = null;
            try
            {
                siteRef = r.ReadString();
                string vehicleRef = r.ReadString();
                var geo = GeoLevel();
                if (geo == null) { RejectHire(senderPeerId, null, "no geoscape"); return; }
                var phoenix = geo.PhoenixFaction;
                if (!(IdentityResolver.Resolve(geo, siteRef, null) is GeoSite site) ||
                    !(site.GetComponent<GeoHaven>() is GeoHaven haven))
                { RejectHire(senderPeerId, siteRef, "unresolved haven " + siteRef); return; }
                if (!(IdentityResolver.Resolve(geo, vehicleRef, null) is GeoVehicle vehicle) ||
                    !ReferenceEquals(vehicle.Owner, phoenix))
                { RejectHire(senderPeerId, siteRef, "unresolved or foreign vehicle " + vehicleRef); return; }
                if (haven.AvailableRecruit == null)
                { RejectHire(senderPeerId, siteRef, "no recruit available (lost the race?)"); return; }
                if (!ReferenceEquals(vehicle.CurrentSite, site))
                { RejectHire(senderPeerId, siteRef, "vehicle not at haven"); return; }
                if (!phoenix.CanRecruitCharacter(haven.AvailableRecruit, haven.GetRecruitCost(phoenix)))
                { RejectHire(senderPeerId, siteRef, "capacity or resources short"); return; }

                haven.TakeRecruit(vehicle); // the native buy: charge, spawn, reward container, RemoveRecruit
                Debug.Log("[MP][personnel] HOST intent APPLIED op=hire " + siteRef + " nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { RejectHire(senderPeerId, siteRef, "(throw) " + ex.Message); }
        }

        private static void RejectHire(ulong peer, string siteRef, string why) =>
            IntentRail.Reject(SurfaceIds.GeoPersonnelIntent, peer, "hire " + (siteRef ?? "?") + " — " + why, siteRef);

        /// <summary>Replay of the dismiss: the SAME override the capture blocked (TFTV's prefix included,
        /// so its dismissal rework applies host-side), plus the vehicle-scrap wallet tail the native UI
        /// callback owns (UIStateEditVehicle:558-564) — model-level parity for the whole gesture.
        /// Deliberately NOT the stat preamble: vehicles/mutogs have no Progression.</summary>
        private static void HandleFireIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return; }
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "unresolved character (already dismissed?)"); return; }
                if (!ReferenceEquals(character.Faction, geo.PhoenixFaction))
                { Reject(senderPeerId, charId, "not a Phoenix unit"); return; }

                geo.PhoenixFaction.KillCharacter(character, CharacterDeathReason.Dismissed); // strip equipment :1625, container removal, DestroyTacUnit
                GiveVehicleScrap(geo, character);
                Debug.Log("[MP][personnel] HOST intent APPLIED op=fire char=U#" + charId + " nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { Reject(senderPeerId, charId, "(throw) " + ex.Message); }
        }

        /// <summary>The scrap-refund tail of the native dismiss UI (UIStateEditVehicle:558-564),
        /// verbatim at model level: exact-match GroundVehicleItemDef by template, Give ScrapPrice.
        /// FirstOrDefault is null for humans — no-op, same as native.</summary>
        private static void GiveVehicleScrap(GeoLevelController geo, GeoCharacter unit)
        {
            var def = GameUtl.GameComponent<DefRepository>().GetAllDefs<GroundVehicleItemDef>()
                .FirstOrDefault(d => d.VehicleTemplateDef == unit.TemplateDef);
            if (def != null && !def.ScrapPrice.IsEmpty)
                geo.PhoenixFaction.Wallet.Give(def.ScrapPrice, OperationReason.Scrap);
        }

        /// <summary>Replay of one stat GESTURE (per-click ±1 since 2026-07-25; the shape still takes the
        /// three-delta wire body unchanged). Cost and cap are checked on the DISPLAY scale, exactly like
        /// the native per-click gate (ChangeCharacterStat:879-881/:909 — CanModifyBaseStat(display±1) +
        /// GetBaseStatCost), where display = GetProgressionBaseStats() + Bonus* (RefreshStats:515-518) =
        /// base stat + Σ bodypart aspect + augment bonus. Charging on the BASE scale undercharged (cost is
        /// value-dependent: CharacterProgression.cs:274-294, Strength=value/2, Will/Speed=value) and let
        /// the display value pass the sheet cap. The increments still land on the base stat — one click
        /// is ±1 on both scales.
        ///
        /// A DECREASE is the undo half of "туда-сюда": refund per step = GetBaseStatCost of the value
        /// being stepped down FROM — the native decrement's own math (:909), the exact mirror of the
        /// increment's cost, so +1 then −1 is SP-neutral to the last point. The gesturing peer's native
        /// stage already floors decrements at its screen-entry value (:907), but that gate lives on the
        /// CLIENT — host-side the trust-boundary floor is <see cref="WireStatNet"/>: a minus that would
        /// drive a stat's net wire ops below 0 is a refund for a purchase the wire never made (stale
        /// stage after a lost race, or a hostile client farming SP off base stats) and is rejected; the
        /// base ≥ 0 check below additionally keeps the ModifyBaseStat clamp from out-running a refund.
        /// Refunds land faction-first capped by <see cref="WireFactionTaken"/> (see <see cref="Refund"/>)
        /// — the same split the gesturing client's stage predicted, so the echo matches and the staged
        /// floor survives it.</summary>
        private static bool ApplyStats(ulong peer, GeoCharacter character, int addStr, int addWill, int addSpeed)
        {
            var progression = character.Progression;
            var stats = new[] { CharacterBaseAttribute.Strength, CharacterBaseAttribute.Will, CharacterBaseAttribute.Speed };
            var delta = new[] { addStr, addWill, addSpeed };
            var baseStats = character.GetProgressionBaseStats(); // Endurance slot = strength (GeoCharacter.cs:1167-1181)
            var display = new[]
            {
                (int)(baseStats.Endurance + character.BonusStrength),
                (int)(baseStats.Willpower + character.BonusWillpower),
                (int)(baseStats.Speed + character.BonusSpeed),
            };
            int total = 0; // net SP (mutagen for mutoids): >0 charge, <0 refund
            bool any = false;
            for (int i = 0; i < 3; i++)
            {
                if (delta[i] == 0) continue;
                any = true;
                int want = display[i] + delta[i];
                // Bounds both cost loops below as well: CanModifyBaseStat rejects anything outside
                // [0, sheet max] (and a display+delta overflow wraps → also rejected), so a hostile
                // delta=±int.MaxValue cannot spin here.
                if (!progression.CanModifyBaseStat(stats[i], want))
                { Reject(peer, (int)character.Id, "stat out of range " + stats[i] + "=" + want); return false; }
                // The refund walks DISPLAY steps but ModifyBaseStat moves the BASE stat, clamped at 0 —
                // never refund steps the clamp would swallow (display > base by the bonus offset).
                if (delta[i] < 0 && progression.GetBaseStat(stats[i]) + delta[i] < 0)
                { Reject(peer, (int)character.Id, "stat below base floor " + stats[i]); return false; }
                // Law 3 session floor: only what the wire bought may be wire-refunded (native's committed
                // floor :907 lives on the gesturing client's stage — never trust it from here).
                if (delta[i] < 0)
                {
                    WireStatNet.TryGetValue((int)character.Id, out var net);
                    if ((net == null ? 0 : net[i]) + delta[i] < 0)
                    { Reject(peer, (int)character.Id, "stat below wire floor " + stats[i]); return false; }
                }
                for (int v = display[i] + 1; v <= want; v++) total += progression.GetBaseStatCost(stats[i], v);
                for (int v = display[i]; v > want; v--) total -= progression.GetBaseStatCost(stats[i], v);
            }
            if (!any) return false; // no-op intent (client view was already behind)
            if (total > 0 && !Charge(character, total))
            { Reject(peer, (int)character.Id, "cannot afford " + total + " SP"); return false; }
            if (total < 0) Refund(character, -total);
            for (int i = 0; i < 3; i++)
                if (delta[i] != 0) progression.ModifyBaseStat(stats[i], delta[i]);
            int key = (int)character.Id; // ledger moves only on an APPLIED op — rejects above leave it exact
            if (!WireStatNet.TryGetValue(key, out var applied)) WireStatNet[key] = applied = new int[3];
            for (int i = 0; i < 3; i++) applied[i] += delta[i];
            return true;
        }

        // ─── Wire-op session ledgers (HOST, law 3): what the wire bought, and where it was paid from ──
        // Native never lets a decrement pass the committed value (ChangeCharacterStat:907 floors at the
        // screen-entry stage) and refunds the faction pool FIRST, capped by what the visit took from it
        // (:915-931). The host has no per-visit stage for remote peers, so the session-scoped equivalents
        // live here; both key on GeoTacUnitId and die with the geoscape (ResetForReloadBoundary).
        //   WireStatNet[charId] = net wire-applied stat steps [Str,Will,Speed] — floor for wire refunds.
        //   WireFactionTaken[charId] = faction SP the wire's charges spilled into the shared pool —
        //     the faction-first cap for <see cref="Refund"/>.
        private static readonly Dictionary<int, int[]> WireStatNet = new Dictionary<int, int[]>();
        private static readonly Dictionary<int, int> WireFactionTaken = new Dictionary<int, int>();

        /// <summary>The skill-reset seam, BOTH halves on the ONE model funnel
        /// <c>GeoCharacter.ResetCharacterProgression</c> (GeoCharacter.cs:604-615, gated by
        /// _hasSkillReset :606; sole caller of <c>CharacterProgression.ResetAbilities</c>:225 — UI button
        /// UseAllowedAbilityReset:444-450 and any mod path both route through it; TFTV: no direct calls).
        ///
        /// PREFIX = intent capture (law 4a), op=5. Patching the model funnel instead of the UI button
        /// catches every caller; the mirror never calls it (applies write fields raw), and ShouldRunNative's
        /// SyncApplyScope arm keeps law 8 anyway. Wire body = charId ONLY — the reset is free (the
        /// _hasSkillReset allowance is the whole price) and every refund is re-derived host-side from the
        /// HOST's own tracks (ResetAbilities :235-250), so there is nothing else a client could be trusted
        /// to say. CLIENT: block + send; __result=false keeps the caller's reseed branch (:448) cold — the
        /// panel repaints when the host's 0xAC echo lands (every mutation is rail-covered: _abilities /
        /// _abilityTracks / SkillPoints / _secondarySpecializationDef, plus _hasSkillReset Leaf
        /// rail-baseline:141, which also hides ResetSkillsButton on the reseed :490; mutoid refund =
        /// covered wallet). Zero traffic when the native gate would refuse (no allowance).
        ///
        /// POSTFIX = ledger hygiene (host). The reset refunds ability SP straight into the model
        /// (:242/:250) — a session ledger kept from before it no longer describes the character, so DROP
        /// it on the character that reset (never carry a positive wire-net across a respec: a later wire
        /// −1 would pass the floor and mint its step cost on top of the reset's own refund). Clearing is
        /// the strict direction — floor falls back to 0, worst case a legit refund is rejected and law-7
        /// reconverges the client.</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.ResetCharacterProgression))]
        internal static class SkillResetPatch
        {
            private static bool Prefix(GeoCharacter __instance, ref bool __result)
            {
                if (ShouldRunNative()) return true;
                __result = false;
                try
                {
                    if (__instance.Progression == null || !__instance.HasSkillReset) return false; // native gate would refuse — zero traffic
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpSkillReset,
                        "skillReset U#" + (int)__instance.Id,
                        w => w.Write((int)__instance.Id));
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] skill-reset capture failed: " + ex); }
                return false;
            }

            private static void Postfix(GeoCharacter __instance, bool __result)
            {
                if (!__result) return; // _hasSkillReset gate refused — nothing changed
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return; // solo: fully native
                int charId = (int)__instance.Id;
                WireStatNet.Remove(charId);
                WireFactionTaken.Remove(charId);
            }
        }

        /// <summary>Inverse of <see cref="Charge"/> for the undo gesture — mutoids get mutagens back in
        /// the wallet; humans refill the shared faction pool FIRST, capped by what the wire's own charges
        /// took from it (<see cref="WireFactionTaken"/>), remainder personal. That is the native
        /// decrement's exact split (ChangeCharacterStat:915-931: faction up to the _startingFactionPoints
        /// gap, overflow into _currentSkillPoints) — always-personal permanently converted faction SP into
        /// one soldier's, and mismatched the gesturing client's stage prediction (echo → reseed → staged
        /// floor lost).</summary>
        private static void Refund(GeoCharacter character, int amount)
        {
            if (amount <= 0) return;
            if (character.IsMutoid)
            {
                character.Faction?.Wallet?.Give(new ResourceUnit(ResourceType.Mutagen, amount), OperationReason.Refund);
                return;
            }
            int key = (int)character.Id;
            WireFactionTaken.TryGetValue(key, out int taken);
            int toFaction = amount < taken ? amount : taken;
            if (toFaction > 0 && character.Faction is GeoPhoenixFaction phoenix)
            {
                phoenix.Skillpoints += toFaction;
                WireFactionTaken[key] = taken - toFaction;
                amount -= toFaction;
            }
            if (amount > 0) character.Progression.SkillPoints += amount;
        }

        /// <summary>Replay of <c>BuyAbility</c>:391-417 at model level.</summary>
        private static bool ApplyBuyAbility(ulong peer, GeoCharacter character, AbilityTrackSource source,
                                            int slotLevel, int buttonLevel, string abilityGuid)
        {
            var progression = character.Progression;
            int charId = (int)character.Id;
            var track = progression.GetAbilityTrack(source);
            var slot = track == null ? null : track.GetAbilitySlotForLevel(slotLevel);
            if (slot == null) { Reject(peer, charId, "no slot at " + source + " lvl " + slotLevel); return false; }

            bool mutoid = character.IsMutoid;
            var wanted = ResolveDef(abilityGuid) as TacticalAbilityDef;
            // Native :393-395 stamps an EMPTY slot only in the mutoid flow (a human track is pre-filled),
            // and only on the clicker's own screen where the offer was locally legal. For a human the
            // client-sent GUID is arbitrary wire input — never write it into the track (law 3).
            if (slot.Ability == null && !mutoid) { Reject(peer, charId, "empty slot on non-mutoid"); return false; }
            // Law 3: in the mutoid flow `wanted` (raw wire GUID) gets STAMPED into the track (empty-slot
            // stamp below + the second-slot stamp at the end) — accept it only if the host's own offer
            // grid would have shown it on that button (see IsPandoranOffer for the native derivation).
            if (mutoid && !IsPandoranOffer(character, wanted, buttonLevel))
            { Reject(peer, charId, "not a mutoid offer " + abilityGuid); return false; }
            if (slot.Ability == null) slot.Ability = wanted;      // native :393-395 (mutoid empty slot)
            if (slot.Ability == null) { Reject(peer, charId, "unknown ability " + abilityGuid); return false; }
            if (progression.Abilities.Contains(slot.Ability)) { Reject(peer, charId, "already learned " + slot.Ability.name); return false; }

            if (mutoid)
            {
                if (progression.LevelProgression.Level < buttonLevel)
                { Reject(peer, charId, "level " + progression.LevelProgression.Level + " < " + buttonLevel); return false; }
            }
            else if (!progression.CanLearnAbility(slot, progression.Strength, progression.Will, progression.Speed))
            { Reject(peer, charId, "cannot learn " + slot.Ability.name); return false; }

            int cost = mutoid
                ? slot.Ability.CharacterProgressionData.MutagenCost      // native :399
                : progression.GetAbilitySlotCost(slot);                 // native :403
            if (!Charge(character, cost)) { Reject(peer, charId, "cannot afford " + slot.Ability.name); return false; }

            if (!mutoid) { progression.LearnAbility(slot); return true; } // native :416

            progression.AddAbility(slot.Ability);                         // native :408
            // native :409-412 — the mutoid track stamps a SECOND slot, offset by one past the dual-spec
            // level. Grounded on the character's own LevelProgression.Def, which is what the same dual-spec
            // gate reads at :1007. ponytail: a mod repointing CharacterGenerator.LevelProgression at a
            // different def would shift this by one row; the ability itself is already learned either way.
            int specLevel = progression.LevelProgression.Def.SecondSpecializationLevel;
            var stamp = track.GetAbilitySlotForLevel(buttonLevel < specLevel ? buttonLevel : buttonLevel + 1);
            if (stamp != null && wanted != null) stamp.Ability = wanted;
            return true;
        }

        /// <summary>Host-side rebuild of the native mutoid offer grid, so a wire GUID is only ever a
        /// button native itself would have offered. Grounded (SpecializedAbilityTrackPopupElement.Init):
        /// specs = <c>PhoenixFaction.AvailablePandoranSpecialzations</c> minus the vehicle-class-tag one
        /// (:85-93); per spec the offers are its <c>AbilityTrack.AbilitiesByLevel</c> compacted to
        /// non-null slots (:122); the button at row N carries ability index N-1 and
        /// <c>AbilityLevel = N</c> (:129/:160) — the same value our capture ships as buttonLevel.</summary>
        private static bool IsPandoranOffer(GeoCharacter character, TacticalAbilityDef wanted, int buttonLevel)
        {
            if (wanted == null || buttonLevel < 1) return false;
            var vehicleTag = GameUtl.GameComponent<SharedData>().SharedGameTags.VehicleClassTag;
            foreach (var spec in character.Faction.GeoLevel.PhoenixFaction.AvailablePandoranSpecialzations)
            {
                if (spec == null || spec.ClassTag == vehicleTag) continue;
                var offers = spec.AbilityTrack.AbilitiesByLevel.Where(a => a.Ability != null).ToArray();
                if (buttonLevel <= offers.Length && offers[buttonLevel - 1].Ability == wanted) return true;
            }
            return false;
        }

        /// <summary>Replay of <c>ChoseSecondSpecialization</c>:816-821. Validated BEFORE mutating (native
        /// mutates first and would leave the class added but unpaid if the charge failed).</summary>
        private static bool ApplySecondSpec(ulong peer, GeoCharacter character, string specGuid)
        {
            var progression = character.Progression;
            int charId = (int)character.Id;
            if (!(ResolveDef(specGuid) is SpecializationDef spec)) { Reject(peer, charId, "unknown spec " + specGuid); return false; }
            if (progression.SecondarySpecDef != null) { Reject(peer, charId, "second class already learned"); return false; }
            if (progression.MainSpecDef == spec) { Reject(peer, charId, "spec equals main class"); return false; }
            var levels = progression.LevelProgression;
            if (levels.Level < levels.Def.SecondSpecializationLevel)
            { Reject(peer, charId, "level " + levels.Level + " < " + levels.Def.SecondSpecializationLevel); return false; }
            if (!Charge(character, levels.Def.SecondSpecializationSpCost))
            { Reject(peer, charId, "cannot afford second class"); return false; }
            progression.AddSecondaryClass(spec);
            return true;
        }

        /// <summary>Replay of the skill-reset: the SAME native funnel the capture blocked client-side
        /// (see <see cref="SkillResetPatch"/> for the full ground). No Charge/Refund here — the native
        /// body IS the refund, computed from the host's own tracks; its false return (allowance already
        /// spent / no progression) is the reject. Ledger drop rides the patch's own postfix.</summary>
        private static bool ApplySkillReset(ulong peer, GeoCharacter character)
        {
            if (!character.ResetCharacterProgression())
            { Reject(peer, (int)character.Id, "no skill reset allowance"); return false; }
            return true;
        }

        /// <summary>Replay of <c>UIStateGeoRoster.OnActionSlotTransferInitiated</c>:294-295 at model
        /// level, with the native menu's own gates re-checked host-side:
        /// <c>destination.CanTransferBetweenContainer(source)</c> is the listing filter
        /// (GeoRosterTransferActionMenu.cs:58 — vehicle dst: docked, co-located, not travelling; site
        /// dst: only from a vehicle at that site), and volume fit + the one-volume-3-unit rule are the
        /// button gate (TransferActionMenuElement.cs:28-45). The SOURCE is the host's own answer to
        /// "which container holds this character" — never the wire's. Outcome rides the covered
        /// <c>_tacUnits</c> LeafLists; rejects re-emit both container subtrees.</summary>
        private static bool ApplyReassign(ulong peer, GeoLevelController geo, GeoCharacter character, string dstRef)
        {
            int charId = (int)character.Id;
            // dstRef is wire input fed to the resolver — accept only a bare container root ref.
            if (string.IsNullOrEmpty(dstRef) || dstRef.IndexOf('.') >= 0 || (dstRef[0] != 'S' && dstRef[0] != 'V'))
            { Reject(peer, charId, "bad destination '" + dstRef + "'"); return false; }
            if (!(IdentityResolver.Resolve(geo, dstRef, null) is IGeoCharacterContainer destination))
            { Reject(peer, charId, "unresolved destination " + dstRef); return false; }
            var phoenix = geo.PhoenixFaction;
            bool phoenixDst = destination is GeoVehicle dv ? ReferenceEquals(dv.Owner, phoenix)
                            : destination is GeoSite ds && ReferenceEquals(ds.Owner, phoenix);
            if (!phoenixDst)
            { RejectReassign(peer, charId, "destination not Phoenix " + dstRef, null, dstRef); return false; }
            var source = FindCharacterContainer(geo, character);
            if (source == null)
            { RejectReassign(peer, charId, "character in no container", null, dstRef); return false; }
            if (ReferenceEquals(source, destination)) // no-op: the client's view was behind — re-emit heals its mirror
            { RejectReassign(peer, charId, "already there", null, dstRef); return false; }
            var srcRef = IdentityResolver.RootRef(source);
            if (!destination.CanTransferBetweenContainer(source))
            { RejectReassign(peer, charId, "not co-located " + srcRef + "→" + dstRef, srcRef, dstRef); return false; }
            if (destination.MaxCharacterSpace < int.MaxValue)
            {
                if (destination.MaxCharacterSpace - destination.CurrentOccupiedSpace < character.TemplateDef.Volume)
                { RejectReassign(peer, charId, "no space in " + dstRef, srcRef, dstRef); return false; }
                if (character.TemplateDef.Volume == 3 &&
                    destination.GetAllCharacters().Any(c => c.TemplateDef.Volume == 3))
                { RejectReassign(peer, charId, "volume-3 unit already in " + dstRef, srcRef, dstRef); return false; }
            }
            source.RemoveCharacter(character);   // native :294
            destination.AddCharacter(character); // native :295
            return true;
        }

        /// <summary>Which container holds this character — vehicles first, then sites (the same
        /// <c>_tacUnits</c>-backed <c>Units</c> the rail mirrors, GeoVehicle.cs:250 / GeoSite.cs:243).</summary>
        private static IGeoCharacterContainer FindCharacterContainer(GeoLevelController geo, GeoCharacter character)
        {
            var map = geo.Map;
            if (map == null) return null;
            foreach (var v in map.Vehicles) if (v != null && v.Units.Contains(character)) return v;
            foreach (var s in map.AllSites) if (s != null && s.Units.Contains(character)) return s;
            return null;
        }

        /// <summary>Reassign reject: converge BOTH touched container subtrees (the transfer's outcome
        /// lives in their _tacUnits lists, not on the character), null prefixes ignored by contract.</summary>
        private static void RejectReassign(ulong peer, int charId, string why, string srcRef, string dstRef) =>
            IntentRail.Reject(SurfaceIds.GeoPersonnelIntent, peer, "char=U#" + charId + " — " + why, srcRef, dstRef);

        /// <summary>
        /// The SP economy, re-derived from the HOST's own numbers — the reason the wire never carries a
        /// balance. Native rule (ChangeCharacterStat:892-903 and ConsumeAbilityCost:435-441): personal
        /// <c>CharacterProgression.SkillPoints</c> pays first and the SHARED
        /// <c>GeoPhoenixFaction.Skillpoints</c> pool covers the overflow. Mutoids pay MUTAGEN instead
        /// (ConsumeAbilityCost:430-433). Returns false = unaffordable and NOTHING was debited, so every
        /// caller can validate by calling this last.
        /// </summary>
        private static bool Charge(GeoCharacter character, int cost)
        {
            if (cost <= 0) return true;
            var faction = character.Faction;
            if (character.IsMutoid)
            {
                var price = new ResourceUnit(ResourceType.Mutagen, cost);
                if (faction == null || faction.Wallet == null || !faction.Wallet.HasResources(price)) return false;
                faction.Wallet.Take(price, OperationReason.Purchase);
                return true;
            }
            var progression = character.Progression;
            var phoenix = faction as GeoPhoenixFaction;
            int pool = phoenix == null ? 0 : phoenix.Skillpoints;
            if (progression.SkillPoints + pool < cost) return false;
            if (progression.SkillPoints >= cost) { progression.SkillPoints -= cost; return true; }
            int overflow = cost - progression.SkillPoints;
            progression.SkillPoints = 0;
            if (phoenix != null)
            {
                phoenix.Skillpoints = pool - overflow;
                // Ledger the faction spill: Refund's faction-first cap (native lumps stat clicks and
                // ability buys into the same per-visit gap — ConsumeAbilityCost:439-441 spills the same way).
                WireFactionTaken.TryGetValue((int)character.Id, out int taken);
                WireFactionTaken[(int)character.Id] = taken + overflow;
            }
            return true;
        }

        private static BaseDef ResolveDef(string guid) =>
            string.IsNullOrEmpty(guid) ? null : GameUtl.GameComponent<DefRepository>()?.GetDef(guid);

        /// <summary>Reject with the touched character subtree: the gesturing client blocked its local
        /// write (nothing to un-do model-side), but its progression screen still shows the STAGED edit —
        /// re-emitting "U#&lt;charId&gt;" pushes current host values, and the client's ordinary apply +
        /// UiEventMap repaint re-seed that screen (law-7 convergence; never log-only).</summary>
        private static void Reject(ulong peer, int charId, string why) =>
            IntentRail.Reject(SurfaceIds.GeoPersonnelIntent, peer, "char=U#" + charId + " — " + why,
                              charId >= 0 ? "U#" + charId : null);
    }
}
