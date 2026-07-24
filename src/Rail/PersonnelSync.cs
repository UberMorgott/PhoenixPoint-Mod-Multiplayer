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
    ///     below). In a session it is a pure SKIP on every peer: per-click seams already put everything
    ///     in the model, and the absolute pool write is exactly what reverted foreign SP spends when a
    ///     peer sat on a stale stage (review risk 2026-07-25). Skip = no double apply, by construction.
    ///   • <c>ConsumeAbilityCost</c> (:428) — the staged SP debit of a perk/second-class buy; with the
    ///     commit skipped the HOST applies its stage delta to the model right here (mutoid branch :432
    ///     already hits the wallet natively). Client never reaches it — both callers are blocked below.
    ///   • <c>BuyAbility</c> (:389) — ability/perk purchase (human LearnAbility + mutoid AddAbility).
    ///   • <c>ChoseSecondSpecialization</c> (:813) — the second-class purchase (AddSecondaryClass).
    ///   • <c>UIStateGeoRoster.OnActionSlotTransferInitiated</c> (:292) — the ONE roster base⇄vehicle
    ///     transfer seam (drag and action-menu both funnel through it).
    ///
    /// HOST replay runs the identical native model calls those seams make: <c>ModifyBaseStat</c> (:369-373),
    /// <c>LearnAbility</c>/<c>AddAbility</c> (:416/:408), <c>AddSecondaryClass</c> (:820) — with the SP
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
                    FBoughtSlot == null || FBoughtAbility == null || FBoughtSource == null || FBoughtLevel == null)
                    Debug.LogError("[MP][personnel] FIELD BIND FAILED on UIModuleCharacterProgression — " +
                                   "stat/ability intents CANNOT be captured; client edits will not sync.");
                else
                    Debug.Log("[MP][personnel] view-model fields bound");
            }
            return FCharacter != null && FBoughtSlot != null && FCurSkillPoints != null;
        }

        public static void Reset() => ResetForReloadBoundary();

        /// <summary>Mid-session reload boundary: nothing geoscape-bound is cached here (the seam is
        /// stateless per intent; dedup/nonce live in <see cref="IntentRail"/>) — same rca-3 contract as
        /// <see cref="ResearchSync.ResetForReloadBoundary"/>.</summary>
        public static void ResetForReloadBoundary() { }

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
        /// Session-wide neutralization of the native stage→model flush, BOTH peers. Everything the flush
        /// would transfer already reached the model at gesture time (host: <see cref="StatClickPatch"/> +
        /// <see cref="ConsumeCostApplyPatch"/>; client: the op=1 replay), so letting it run could only
        /// (a) double-apply the staged diff or (b) write the ABSOLUTE SP pools (:375/:378) from a stage
        /// that went stale while a foreign spend landed — the host-side revert found in review. An exit
        /// with no gestures was already zero traffic and stays so (nothing staged, nothing sent).
        ///
        /// Composes with <see cref="EquipFlushGate"/>, which also prefixes this method (it blocks inside
        /// an apply and during session teardown; Harmony skips later prefixes once one returns false).
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.CommitStatChanges))]
        internal static class CommitStatsNeutralizePatch
        {
            private static bool Prefix()
            {
                var engine = NetworkEngine.Instance;
                return engine == null || !engine.IsActiveSession; // solo native; in-session: skip on host AND client
            }
        }

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
        /// stage already floors decrements at its screen-entry value (:907); host-side the floor is the
        /// model's own (display ≥ 0, base ≥ 0 so the ModifyBaseStat clamp can never out-run the refund).
        /// ponytail: refunds land in personal SkillPoints (and a faction-pool-funded +1 undone moves that
        /// SP personal) — the exchange rate is identical both ways so nothing is minted; track a per-visit
        /// pool split if it ever matters.</summary>
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
                for (int v = display[i] + 1; v <= want; v++) total += progression.GetBaseStatCost(stats[i], v);
                for (int v = display[i]; v > want; v--) total -= progression.GetBaseStatCost(stats[i], v);
            }
            if (!any) return false; // no-op intent (client view was already behind)
            if (total > 0 && !Charge(character, total))
            { Reject(peer, (int)character.Id, "cannot afford " + total + " SP"); return false; }
            if (total < 0) Refund(character, -total);
            for (int i = 0; i < 3; i++)
                if (delta[i] != 0) progression.ModifyBaseStat(stats[i], delta[i]);
            return true;
        }

        /// <summary>Inverse of <see cref="Charge"/> for the undo gesture — mutoids get mutagens back in
        /// the wallet, humans get personal SkillPoints (see the ApplyStats ponytail note on the split).</summary>
        private static void Refund(GeoCharacter character, int amount)
        {
            if (amount <= 0) return;
            if (character.IsMutoid)
            {
                character.Faction?.Wallet?.Give(new ResourceUnit(ResourceType.Mutagen, amount), OperationReason.Refund);
                return;
            }
            character.Progression.SkillPoints += amount;
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
            if (phoenix != null) phoenix.Skillpoints = pool - overflow;
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
