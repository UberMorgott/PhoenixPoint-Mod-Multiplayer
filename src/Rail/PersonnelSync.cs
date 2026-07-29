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
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Tactical.Entities.Abilities;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Migration step 5 — PERSONNEL family under the CLIENT-POSTURE LAW (block-first, codified at
    /// <see cref="IntentRail.ShouldRunNative"/>): every model funnel below BLOCKS on a client and ships
    /// an intent; the HOST runs the SAME native method; the outcome reaches every peer through the
    /// generic value rail 0xAC (CharacterProgression mirrors SkillPoints / _baseStats / _abilities /
    /// _abilityTracks / _secondarySpecializationDef; membership rides the container _tacUnits
    /// LeafLists; GeoPhoenixFaction.Skillpoints + wallets are covered Leafs). Nothing is echoed by
    /// hand, and there is NO host→client surface here — intent-only.
    ///
    /// The seams (each = ONE native funnel, verified by call-site sweep over the decompile):
    ///   • <c>ChangeCharacterStat</c> (UIModuleCharacterProgression.cs:875) — the one stat-click
    ///     funnel (all six buttons route through ChangeStrengthStat/ChangeWillStat/ChangeSpeedStat
    ///     :848/:857/:866). The native body stays alive on BOTH peers as pure VIEW staging
    ///     (presentation carve-out of the law — instant numbers, native affordability/undo gates).
    ///     Client ships the accepted gesture as op=1 and waits for the mirror; HOST lands the same
    ///     gesture directly in <see cref="ApplyStats"/> (law 11 — clients see it the same frame).
    ///     ONE path, and neither peer's click touches the panel's _starting* undo baseline.
    ///   • <c>CommitStatChanges</c> (:367) — natively the stage→model flush (stat deltas + ABSOLUTE
    ///     SP pools :375/:378). Client: blocked (<see cref="CommitStatsClientBlockPatch"/> — the law;
    ///     per-click intents already carried everything, and the absolute pool write from a stale
    ///     stage is the foreign-spend revert this family kept re-fixing). Host in a session: the
    ///     delta half is neutralized by aligning the baseline to the stage (the model already has
    ///     every click), the absolute half runs native — this is the ONE point where the visit
    ///     baseline is dropped, exactly as native drops it. Solo: fully native.
    ///   • <c>BuyAbility</c> (:389) / <c>ChoseSecondSpecialization</c> (:813) — purchase funnels;
    ///     client blocks + ships op=2/3, host replays the native model calls (LearnAbility/AddAbility/
    ///     AddSecondaryClass) with the SP economy re-derived from HOST numbers (<see cref="Charge"/>).
    ///     Host's own buys run the untouched native bodies (their internal ConsumeAbilityCost +
    ///     CommitStatChanges now flush natively).
    ///   • <c>GeoSite.AddCharacter/RemoveCharacter</c> (:983/:989) + the <c>GeoVehicle</c> twins
    ///     (:759/:766) — roster membership on its MODEL funnel (see the membership guard patches).
    ///   • <c>GeoCharacter.ResetCharacterProgression</c> (:604) — the free respec (SkillResetPatch).
    ///   • <c>GeoHaven.TakeRecruit</c> (:810) / <c>GeoPhoenixFaction.KillCharacter</c> (:1377 override
    ///     — Dismissed → op=7 intent, EVERY other reason blocked on a client, see DismissCapturePatch)
    ///     / <c>GeoPhoenixFaction.HireNakedRecruit</c> (:662, content-keyed) — hire and fire funnels.
    ///   • TFTV <c>TrainingFacilityRework.RedeployDismissedOperative</c> /
    ///     <c>FinalizeRecruitTrainingForUI</c> / <c>PromoteCivilianToOperative</c> — TFTV's base→base
    ///     moves (native GeoSite transfer requires a docked vehicle, so op=4 cannot carry them) and the
    ///     civilian→operative CREATION (op=4's container guard deliberately drops creations); late-bound
    ///     via TftvLateBinder, host re-runs the SAME TFTV method (ops 9/10/11).
    ///
    /// WHAT THE WIRE DELIBERATELY DOES NOT CARRY: any balance. A client's own numbers would make it
    /// authoritative over a shared resource (law 3) — the host re-derives every cost and pool from its
    /// own state (<see cref="Charge"/>/<see cref="Refund"/>).
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
        private const byte OpHireNaked = 8;   // base Recruits-tab hire       → Wallet.Take + HireNakedRecruit (content-keyed)
        private const byte OpTftvRedeploy = 9;    // TFTV RedeployDismissedOperative(level, char, targetBase) — base→base move
        private const byte OpTftvTrainDeploy = 10; // TFTV FinalizeRecruitTrainingForUI(level, char, early) — training deploy/relocate
        private const byte OpTftvPromote = 11;     // TFTV PromoteCivilianToOperative(level, char, targetBase, spec) — civilian→operative CREATION

        // ─── Reflection: UIModuleCharacterProgression's private view-model — READ-ONLY here ──
        // _character = the soldier the panel is bound to; _bought* = the staged ability purchase the
        // capture ships. The stage itself (_current*/_starting*) is native presentation the law leaves
        // alone — nothing in this file reads or writes it anymore.
        private static readonly FieldInfo FCharacter = AccessTools.Field(typeof(UIModuleCharacterProgression), "_character");
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
                if (FCharacter == null || FBoughtSlot == null || FBoughtAbility == null ||
                    FBoughtSource == null || FBoughtLevel == null)
                    Debug.LogError("[MP][personnel] FIELD BIND FAILED on UIModuleCharacterProgression — " +
                                   "stat/ability intents CANNOT be captured; client edits will not sync.");
                else
                    Debug.Log("[MP][personnel] view-model fields bound");
            }
            // All-or-nothing: a partial bind must read as NOT bound.
            return FCharacter != null && FBoughtSlot != null && FBoughtAbility != null &&
                   FBoughtSource != null && FBoughtLevel != null;
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
                [OpHireNaked] = HandleHireNakedIntent, // content-keyed — a GeoUnitDescriptor has no id
                [OpTftvRedeploy] = HandleTftvMove,     // TFTV base→base moves / promote — host runs the SAME TFTV method
                [OpTftvTrainDeploy] = HandleTftvMove,
                [OpTftvPromote] = HandleTftvMove,
            };
            IntentRail.Register(SurfaceIds.GeoPersonnelIntent, "personnel", ops);
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── Harmony seams (law 4a, intent-capture only — this file owns no other patch) ──

        /// <summary>
        /// Per-CLICK stat seam (law 11 + the client-posture law) — the ONE seam for BOTH peers. The
        /// native body always runs first: it is pure VIEW staging (updates the panel's _current*
        /// copies, no model write) = instant numbers, native undo floor at the visit baseline (:907),
        /// affordability greying (:892-903). This postfix acts only on an ACCEPTED gesture
        /// (__result ±1; 0 = native refused = zero traffic and no model write):
        ///   • CLIENT ships op=1 with a single-stat ±1 delta; the model moves when the host's delta
        ///     mirrors back.
        ///   • HOST lands the SAME gesture in <see cref="ApplyStats"/> — the exact method a client's
        ///     intent reaches — then flushes so clients see it this frame (N3).
        /// NEITHER peer touches the panel's _starting* baseline any more (the native
        /// <c>CommitStatChanges</c> ends with _starting* := _current*, :370/:372/:374 — running it per
        /// click is what made the minus button grey out on the clicking peer and killed undo). The
        /// baseline now lives for the whole visit and survives rail repaints through the declarative
        /// <c>UiNativeRepaint.StageBaselines</c> checkpoint. A reject converges via the "U#" re-emit.
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), "ChangeCharacterStat")]
        internal static class StatClickPatch
        {
            private static void Postfix(UIModuleCharacterProgression __instance, CharacterBaseAttribute baseStat, int __result)
            {
                if (__result == 0) return;               // native refused the click
                if (SyncApplyScope.Active) return;       // law 8: a reseed-driven call is not a gesture
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return; // solo: the native flush owns the model
                if (!BindOk()) return;
                try
                {
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    if (character?.Progression == null) return;
                    int dStr = baseStat == CharacterBaseAttribute.Strength ? __result : 0;
                    int dWill = baseStat == CharacterBaseAttribute.Will ? __result : 0;
                    int dSpeed = baseStat == CharacterBaseAttribute.Speed ? __result : 0;
                    if (!engine.IsHost)
                    {
                        IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpSpendStats,
                            "stats U#" + (int)character.Id + " dStr=" + dStr + " dWill=" + dWill + " dSpeed=" + dSpeed,
                            w => { w.Write((int)character.Id); w.Write(dStr); w.Write(dWill); w.Write(dSpeed); });
                        return;
                    }
                    // peer 0 = the host itself: a reject has no client to nudge, and its "U#" re-emit
                    // still reconverges everyone else. Only reachable if a remote delta landed between
                    // this panel's last reseed and the click — then the host's own stage is the stale
                    // one, so ask for the repaint that reseeds it.
                    if (ApplyStats(0, character, dStr, dWill, dSpeed)) DiffEngine.FlushOnHostGesture();
                    else OpenUiRepaint.MarkDirty();
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] stat click seam failed: " + ex); }
            }
        }

        /// <summary>
        /// THE LAW at the stage→model flush (the MODEL chokepoint — one prefix covers every caller:
        /// screen exit UIStateEditSoldier.cs:232, soldier switch :363, post-buy :715, and the two
        /// internal calls from BuyAbility:405 / ChoseSecondSpecialization:822).
        ///   • CLIENT: the whole commit (stat deltas + ABSOLUTE SP pool writes :375/:378) is a model
        ///     write and is BLOCKED — the per-click seam already shipped every gesture as its own
        ///     intent; letting it run could only double-apply or revert a foreign spend from a stale
        ///     stage.
        ///   • HOST in a session: the model already carries every staged click (<see cref="ApplyStats"/>
        ///     per gesture), while the panel's baseline is deliberately still the VISIT floor so undo
        ///     stays possible — so the native delta half `_current* - _starting*` would apply the whole
        ///     visit a SECOND time. Align the baseline to the stage first: the delta half becomes a
        ///     no-op and the ABSOLUTE half stays native, which is what a native ability purchase pays
        ///     with (ConsumeAbilityCost debits the stage pools, :405 flushes them). This is the ONLY
        ///     place the baseline is dropped, and it is exactly where native drops it too.
        ///   • Solo: fully native, nothing aligned.
        /// Composes with StatCommitApplyGate's prefix on this same method, which blocks the flush inside a
        /// mirror apply + during session teardown on EITHER peer (the flush-legitimacy law) — hence
        /// the scope check here: a repaint-internal commit must not eat the baseline either.
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.CommitStatChanges))]
        internal static class CommitStatsClientBlockPatch
        {
            private static bool Prefix(UIModuleCharacterProgression __instance)
            {
                if (!IntentRail.ShouldRunNative()) return false; // client: blocked
                if (SyncApplyScope.Active) return true;          // repaint-internal: StatCommitApplyGate blocks it
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsActiveSession) UiNativeRepaint.AlignStageBaseline(__instance);
                return true;
            }
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
                if (IntentRail.ShouldRunNative()) return true;
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
                if (IntentRail.ShouldRunNative()) return true;
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

        /// <summary>Roster-membership guard on the MODEL funnel — the four container mutators every
        /// membership change funnels through (GeoSite.cs:983/:989, GeoVehicle.cs:759/:766; AddUnit/
        /// RemoveUnit/ClearUnits route into them, GeoSite.cs:999/:1011/:1023). Replaces the UI-level
        /// <c>OnActionSlotTransferInitiated</c> capture (48388d2): field logs showed ZERO [MP][intent]
        /// sends from that seam while the client ran the native move locally — and with membership
        /// double-represented (site._tacUnits + vehicle._tacUnits, no cross-list transaction) a
        /// client-local native write diverges the two lists until the host re-ships them whole
        /// (duplicate on one side, vanished local-only member on the other). At model level nothing
        /// slips past: drag, action menu, or any mod path all end in these four methods.
        ///
        /// PAIR ENCODING (why a half-move can never reach the host): the native transfer is
        /// <c>source.RemoveCharacter</c> + <c>destination.AddCharacter</c>, synchronous in ONE call
        /// (UIStateGeoRoster.cs:294-295). The client blocks BOTH natives; ONLY the Add emits op=4
        /// [charId, dstRef]. <see cref="ApplyReassign"/> re-derives the source from HOST state and
        /// replays the full native pair — a lone blocked Remove ships NOTHING (client state untouched,
        /// still a pure mirror) and a lone Add IS a complete transfer by construction. Host, solo and
        /// rail applies pass through (<see cref="ShouldRunNative"/>; the rail's ApplyList and native
        /// save-load both write _tacUnits directly anyway — GeoSite.cs:1566, GeoVehicle.cs:1087).</summary>
        [HarmonyPatch(typeof(GeoSite), nameof(GeoSite.AddCharacter))]
        internal static class SiteAddGuardPatch
        {
            private static bool Prefix(GeoSite __instance, GeoCharacter character) =>
                CaptureMembershipAdd(__instance, character);
        }

        [HarmonyPatch(typeof(GeoVehicle), nameof(GeoVehicle.AddCharacter))]
        internal static class VehicleAddGuardPatch
        {
            private static bool Prefix(GeoVehicle __instance, GeoCharacter character) =>
                CaptureMembershipAdd(__instance, character);
        }

        [HarmonyPatch(typeof(GeoSite), nameof(GeoSite.RemoveCharacter))]
        internal static class SiteRemoveGuardPatch
        {
            private static bool Prefix(GeoSite __instance, GeoCharacter character) =>
                CaptureMembershipRemove(__instance, character);
        }

        [HarmonyPatch(typeof(GeoVehicle), nameof(GeoVehicle.RemoveCharacter))]
        internal static class VehicleRemoveGuardPatch
        {
            private static bool Prefix(GeoVehicle __instance, GeoCharacter character) =>
                CaptureMembershipRemove(__instance, character);
        }

        /// <summary>Client Add half = the WHOLE move on the wire. Never a silent drop OR a silent stale
        /// view: the UI tail has already moved the slot visually by the time this funnel blocks
        /// (UIStateGeoRoster.cs:296-299 ChangeSlotGroup runs after the blocked Add), so a dropped
        /// gesture repaints the open screen from the un-mutated model — the same client half the
        /// reject nudge uses (empty envelope → MarkDirty in IntentRail.HandleInbound), fired locally
        /// because no host round-trip exists to carry it.</summary>
        private static bool CaptureMembershipAdd(IGeoCharacterContainer destination, GeoCharacter character)
        {
            if (IntentRail.ShouldRunNative()) return true;
            try
            {
                var dstRef = IdentityResolver.RootRef(destination);
                if (character == null || dstRef == null)
                {
                    Debug.LogWarning("[MP][personnel] CLIENT membership add DROPPED — unaddressable char=" +
                                     (character == null ? "null" : "U#" + (int)character.Id) +
                                     " dst=" + (dstRef ?? (destination == null ? "null" : destination.GetType().Name)));
                    OpenUiRepaint.MarkDirty(); // heal the roster tail's optimistic slot move
                    return false;
                }
                // Only a unit already in a LOCAL container is a TRANSFER. Client-reachable CREATION
                // flows (TrainMutoidInBase / DeployAsset → AddRecruitToContainerFinal,
                // GeoPhoenixFaction.cs:673-741) hit this same funnel with a CLIENT-allocated id —
                // shipping it would let a stale id counter resolve an unrelated EXISTING host unit
                // and ApplyReassign would silently move the wrong soldier on every peer.
                var geo = GeoLevel();
                if (geo == null || FindCharacterContainer(geo, character) == null)
                {
                    Debug.LogWarning("[MP][personnel] CLIENT membership add DROPPED — U#" + (int)character.Id +
                                     " -> " + dstRef + " not in any local container (creation flow, not a transfer)");
                    OpenUiRepaint.MarkDirty(); // heal the roster tail's optimistic slot move
                    return false;
                }
                IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpReassign,
                    "reassign U#" + (int)character.Id + " -> " + dstRef,
                    w => { w.Write((int)character.Id); w.Write(dstRef); });
            }
            catch (Exception ex) { Debug.LogError("[MP][personnel] reassign capture failed: " + ex); }
            return false;
        }

        /// <summary>Client Remove half: block native, ship NOTHING — the gesture's own Add (same
        /// synchronous call) carries the move and the host re-derives the source itself. Logged so a
        /// LONE remove (a flow that should not run on a client) stays visible, never silent.</summary>
        private static bool CaptureMembershipRemove(IGeoCharacterContainer source, GeoCharacter character)
        {
            if (IntentRail.ShouldRunNative()) return true;
            Debug.Log("[MP][personnel] CLIENT membership remove blocked (paired Add carries the move) char=" +
                      (character == null ? "null" : "U#" + (int)character.Id) +
                      " src=" + (IdentityResolver.RootRef(source) ?? "?"));
            return false;
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
                if (IntentRail.ShouldRunNative()) return true;
                __result = null; // both callers ignore it; the mirrored outcome repaints the haven UI
                try
                {
                    var siteRef = __instance.Site == null ? null : IdentityResolver.RootRef(__instance.Site);
                    var vehicleRef = vehicle == null ? null : IdentityResolver.RootRef(vehicle);
                    if (siteRef == null || vehicleRef == null) { OpenUiRepaint.MarkDirty(); return false; } // unaddressable → drop + repaint (never a silent stale view)
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpHire,
                        "hire " + siteRef + " via " + vehicleRef,
                        w => { w.Write(siteRef); w.Write(vehicleRef); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] hire capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>The kill funnel under the law, on the MODEL funnel <c>GeoPhoenixFaction.KillCharacter</c>
        /// (the override, GeoPhoenixFaction.cs:1377 — TFTV patches this same target,
        /// TFTVBaseRework\PersonnelDismissal.cs:158, so the HOST replay re-runs TFTV's prefix natively;
        /// client-side ordering of the two prefixes stays untested). On a client NO death reason may
        /// execute locally:
        ///   • <c>Dismissed</c> — the ONE user gesture (UIStateEditSoldier:425, UIStateEditVehicle:556)
        ///     → op=7 intent, host replays the same override + the scrap tail.
        ///   • every OTHER reason — blocked outright: letting it run half-executed the kill (the
        ///     membership guards block RemoveCharacter GeoFaction.cs:1603/:1616, but DestroyTacUnit
        ///     :1604/:1617 still ran → a destroyed husk parked inside the mirrored containers until the
        ///     host's list landed; the override would also stamp _level.DeadSoldiers locally :1381).
        ///     Blocking the whole override is the funnel choice that covers ALL its callers atomically —
        ///     a DestroyTacUnit-only gate would still let CharacterDied/StripCharacterEquipment
        ///     half-run. The host's own kill for the same cause mirrors back through the rail
        ///     (_tacUnits + DeadSoldiers are covered).
        /// Known accepted drift: the vehicle-scrap UI callback also Gives ScrapPrice LOCALLY after this
        /// block (UIStateEditVehicle:563) — wallet Leafs are absolute, the host echo overwrites it.</summary>
        [HarmonyPatch(typeof(GeoPhoenixFaction), nameof(GeoPhoenixFaction.KillCharacter))]
        internal static class DismissCapturePatch
        {
            private static bool Prefix(GeoCharacter unit, CharacterDeathReason reason)
            {
                if (IntentRail.ShouldRunNative()) return true;
                if (reason != CharacterDeathReason.Dismissed)
                {
                    Debug.Log("[MP][personnel] CLIENT KillCharacter blocked (reason=" + reason +
                              ") — host outcome mirrors via the rail");
                    return false;
                }
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

        /// <summary>Intent capture for the base Recruits-tab hire, on the MODEL funnel
        /// <c>GeoPhoenixFaction.HireNakedRecruit</c> (:662-671; sole gesture caller
        /// UIStateRosterRecruits:301). ADDRESSING (op=8): a GeoUnitDescriptor has NO id anywhere — the
        /// game's own save re-identifies recruits purely by graph position, and the rail carries the
        /// haven twin as a whole VALUE (rail-baseline:493 Descend NewRecruit), so keyed elements are
        /// impossible by construction. The wire ships a CONTENT key instead:
        /// (Identity.Name :138, UnitType.TemplateDef.Guid :36, Level :206) — two descriptors with the
        /// same triple are interchangeable generated recruits, so first-match on the HOST's own dict is
        /// exact. Stale client list (host regenerated / other peer won the race) → no match → clean
        /// reject. The UI's own Wallet.Take (:300) already ran locally before this funnel — wallet
        /// Leafs are absolute, the host echo overwrites it.</summary>
        [HarmonyPatch(typeof(GeoPhoenixFaction), nameof(GeoPhoenixFaction.HireNakedRecruit))]
        internal static class NakedHireCapturePatch
        {
            private static bool Prefix(GeoUnitDescriptor character, IGeoCharacterContainer toContainer)
            {
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    var siteRef = toContainer == null ? null : IdentityResolver.RootRef(toContainer);
                    if (character == null || siteRef == null) { OpenUiRepaint.MarkDirty(); return false; } // unaddressable → drop + repaint (UI wallet debit already ran locally)
                    string name = character.Identity?.Name ?? "";
                    string templateGuid = character.UnitType?.TemplateDef?.Guid ?? "";
                    int level = character.Level;
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpHireNaked,
                        "hireNaked '" + name + "' lvl=" + level + " -> " + siteRef,
                        w => { w.Write(name); w.Write(templateGuid); w.Write(level); w.Write(siteRef); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] naked-hire capture failed: " + ex); }
                return false;
            }
        }

        // ─── TFTV seams (late-bound — TFTV loads AFTER us; see TftvLateBinder) ──────────────

        /// <summary>TFTV's TrainingFacilityRework moves characters base→base directly
        /// (Site.RemoveCharacter + Site.AddCharacter, TrainingFacilityRework.cs:514-515/:922-923).
        /// The generic op=4 reassign cannot carry that: the native transfer gate requires a docked
        /// vehicle (CanTransferBetweenContainer), so the host correctly rejects site→site — TFTV
        /// base-moves were dead on clients. THE LAW gives them their own seam: block TFTV's own entry
        /// method on the client, ship the character + TFTV's own arguments, and the HOST runs the SAME
        /// TFTV method natively (its internal SP charge, markers, sessions and site moves all run on
        /// host state; the container lists mirror back via the rail). Both [HarmonyPatch] classes are
        /// TFTV-gated: Prepare() is false while TFTV's assembly is absent, PatchAll skips them
        /// silently, and TftvLateBinder re-runs them one frame after TFTV loads (never TypeByName at
        /// ModInit — the load-order trap). TFTV absent → fully inert.</summary>
        private const string TftvTrainingTypeName = "TFTV.TFTVBaseRework.TrainingFacilityRework";

        /// <summary>TFTV's personnel registry (<c>TFTVBaseRework\Data.cs:262</c>), same assembly as the
        /// type above — carries both the host's promote gate and the row cleanup the native caller does.</summary>
        private const string TftvPersonnelTypeName = "TFTV.TFTVBaseRework.PersonnelData";

        /// <summary>Capture of <c>RedeployDismissedOperative(level, character, targetBase)</c> — the
        /// UI's "redeploy dismissed operative" click (TFTVBaseRework\UI.cs:1422). Null return reads as
        /// "refused" to TFTV's UI; the host echo relocates the soldier and repaints.</summary>
        [HarmonyPatch]
        internal static class TftvRedeployCapturePatch
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvTrainingTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvTrainingTypeName), "RedeployDismissedOperative");

            private static bool Prefix(GeoCharacter character, GeoPhoenixBase targetBase, ref GeoCharacter __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                __result = null;
                try
                {
                    var siteRef = targetBase == null ? null : IdentityResolver.RootRef(targetBase.Site);
                    if (character == null || siteRef == null) { OpenUiRepaint.MarkDirty(); return false; }
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpTftvRedeploy,
                        "tftvRedeploy U#" + (int)character.Id + " -> " + siteRef,
                        w => { w.Write((int)character.Id); w.Write(siteRef); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] TFTV redeploy capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Capture of <c>FinalizeRecruitTrainingForUI(level, character, early)</c> — the UI's
        /// training deploy click (TFTVBaseRework\UI.cs:1665). The target base is NOT on the wire: the
        /// method derives it host-side (first base), law 3.</summary>
        [HarmonyPatch]
        internal static class TftvTrainDeployCapturePatch
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvTrainingTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvTrainingTypeName), "FinalizeRecruitTrainingForUI");

            private static bool Prefix(GeoCharacter character, bool early, ref GeoCharacter __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                __result = null;
                try
                {
                    if (character == null) { OpenUiRepaint.MarkDirty(); return false; }
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpTftvTrainDeploy,
                        "tftvTrainDeploy U#" + (int)character.Id + " early=" + early,
                        w => { w.Write((int)character.Id); w.Write(early); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] TFTV train-deploy capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Capture of <c>PromoteCivilianToOperative(level, character, targetBase, mainClass)</c>
        /// — the UI's immediate-deploy click (TFTVBaseRework\UI.cs:1640). A CREATION flow, not a
        /// transfer: the reassign seam (op=4) deliberately drops it (the created operative is not in
        /// any local container), so without this seam it was DEAD on clients. Wire = the CIVILIAN's id
        /// + target site ref + spec guid. ID-HAZARD GUARD (same law as CaptureMembershipAdd): the
        /// civilian must already sit in a LOCAL container — container membership only ever arrives via
        /// the rail, so a container member's id IS a host-known id; a client-allocated id (a client-side
        /// creation flow) can never ship and thus never resolve an unrelated existing host unit. The
        /// CREATED operative never rides the wire at all — the host allocates it and it mirrors back
        /// via the structural create applier. Null return reads as "refused" to TFTV's UI (its
        /// PersonnelData bookkeeping stays untouched, same posture as ops 9/10).</summary>
        [HarmonyPatch]
        internal static class TftvPromoteCapturePatch
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvTrainingTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvTrainingTypeName), "PromoteCivilianToOperative");

            private static bool Prefix(GeoCharacter character, GeoPhoenixBase targetBase,
                                       SpecializationDef mainClass, ref GeoCharacter __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                __result = null;
                try
                {
                    var siteRef = targetBase == null ? null : IdentityResolver.RootRef(targetBase.Site);
                    if (character == null || siteRef == null || mainClass == null)
                    { OpenUiRepaint.MarkDirty(); return false; }
                    var geo = GeoLevel();
                    if (geo == null || FindCharacterContainer(geo, character) == null)
                    {
                        Debug.LogWarning("[MP][personnel] CLIENT TFTV promote DROPPED — U#" + (int)character.Id +
                                         " not in any local container (client-allocated id must never ship)");
                        OpenUiRepaint.MarkDirty();
                        return false;
                    }
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpTftvPromote,
                        "tftvPromote U#" + (int)character.Id + " -> " + siteRef + " spec=" + mainClass.Guid,
                        w => { w.Write((int)character.Id); w.Write(siteRef); w.Write(mainClass.Guid); });
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] TFTV promote capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>HOST replay of the TFTV base→base ops + the civilian promote: resolve the character
        /// (+ target base for op=9/11, + spec for op=11), then invoke the SAME TFTV static method the
        /// client blocked — TFTV's own gates (redeploy SP cost, dismissed marker, session lookup,
        /// dismissed-civilian refusal) all re-run against HOST state; a null return is TFTV's own
        /// refusal → reject (nudge + "U#" re-emit). TFTV missing on the host cannot happen in a real
        /// session (ParityManifest blocks the join) — rejected anyway.
        ///
        /// MIRRORING THE NATIVE CALLER'S TAIL: invoking the statics directly skips whatever their UI
        /// callers do afterwards, so each tail statement is decided per op — MODEL work is replayed,
        /// clicker-local PRESENTATION is not (running it here would drive the HOST player's screen):
        ///   • op=9  <c>UI.cs:1425</c> RemovePersonnel → REPLAY. <c>:1426</c> RefreshResourceInfo,
        ///     <c>:1434</c> refresh, <c>:1435</c> CloseModal → clicker-local.
        ///   • op=10 <c>UI.cs:1672</c> RemovePersonnel → REPLAY. <c>:1674</c> CloseModal,
        ///     <c>:1676</c> _deploymentUIActive, <c>:1682</c> PrepareDeployAsset → clicker-local (that
        ///     one literally opens a deployment UI). <c>:1679</c> faction.RemoveCharacter is model, but
        ///     it is only the SETUP half of that deploy-asset handoff — replaying it without :1682 would
        ///     orphan the operative, so it is skipped too. TFTV already placed the operative at
        ///     <c>Bases.FirstOrDefault()</c> (TrainingFacilityRework.cs:549), so it lands in a real base;
        ///     the clicker just loses the "pick which base" step.
        ///   • op=11 <c>UI.cs:1643</c> TrainingSpec + <c>:1644</c> RemovePersonnel → REPLAY.
        ///     <c>:1650</c> refresh, <c>:1651</c> CloseModal → clicker-local.
        /// Without the RemovePersonnel replay the row outlives its GeoCharacter — a ghost on the host's
        /// personnel screen that re-runs the whole flow when clicked.
        /// op=11 additionally needs a civilian gate TFTV's static does not do for itself (its own is
        /// only the dismissed marker) — see there.</summary>
        private static void HandleTftvMove(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return; }
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "unresolved character"); return; }
                if (!ReferenceEquals(character.Faction, geo.PhoenixFaction))
                { Reject(senderPeerId, charId, "not a Phoenix soldier"); return; }
                var tftv = AccessTools.TypeByName(TftvTrainingTypeName);
                if (tftv == null) { Reject(senderPeerId, charId, "TFTV not loaded on host"); return; }
                var personnel = AccessTools.TypeByName(TftvPersonnelTypeName);
                // Resolved BEFORE the replay: op=11 consumes the source character, so afterwards the id
                // no longer finds its row. Rows exist only for hidden civilians (Data.cs:1044/1108) and
                // dismissed operatives (:447) — a plain operative legitimately has none.
                var row = AccessTools.Method(personnel, "GetPersonnelByUnitId")?.Invoke(null, new object[] { charId });

                object result;
                SpecializationDef promoteSpec = null;
                if (op == OpTftvRedeploy || op == OpTftvPromote)
                {
                    string siteRef = r.ReadString();
                    var target = IdentityResolver.Resolve(geo, siteRef, null) is GeoSite site
                        ? geo.PhoenixFaction.Bases.FirstOrDefault(b => ReferenceEquals(b.Site, site))
                        : null;
                    if (target == null)
                    { RejectReassign(senderPeerId, charId, "unresolved target base " + siteRef, null, siteRef); return; }
                    if (op == OpTftvPromote)
                    {
                        // The spec guid is wire input: only a real SpecializationDef may reach TFTV's
                        // method (it stamps the new operative's main class).
                        if (!(ResolveDef(r.ReadString()) is SpecializationDef spec))
                        { Reject(senderPeerId, charId, "unknown spec (promote)"); return; }
                        promoteSpec = spec;
                        // TFTV's own method guards ONLY the dismissed marker (TrainingFacilityRework.cs:872)
                        // — it never verifies the unit IS a civilian, and its tail RemoveCharacter()s the
                        // source (:733). Ungated, any stale/desynced/hostile Phoenix charId would DELETE a
                        // veteran and mint a level-1 copy of their Identity. Gate on TFTV's own record —
                        // exactly the set the personnel screen itself offers to promote.
                        if (row == null)
                        { Reject(senderPeerId, charId, "not TFTV personnel (promote)"); return; }
                        // Strict native equivalence: the screen routes a row still IN TRAINING to the
                        // finalize flow instead (UI.cs:1359/:1374), so no legitimate client emits promote
                        // for one. Refuse rather than leave an orphaned RecruitSession behind.
                        if (AccessTools.Field(row.GetType(), "Assignment")?.GetValue(row)?.ToString() == "Training")
                        { Reject(senderPeerId, charId, "row is in training (promote)"); return; }
                        result = AccessTools.Method(tftv, "PromoteCivilianToOperative")
                            ?.Invoke(null, new object[] { geo, character, target, spec });
                    }
                    else
                        result = AccessTools.Method(tftv, "RedeployDismissedOperative")
                            ?.Invoke(null, new object[] { geo, character, target });
                }
                else
                {
                    bool early = r.ReadBoolean();
                    result = AccessTools.Method(tftv, "FinalizeRecruitTrainingForUI")
                        ?.Invoke(null, new object[] { geo, character, early });
                }
                if (result == null) { Reject(senderPeerId, charId, "TFTV refused (op=" + op + ")"); return; }
                // The native callers' MODEL tail — all three drop the PersonnelInfo row on success
                // (UI.cs:1425 / :1672 / :1644); op=11 also stamps the chosen spec first (:1643). Everything
                // else those callers do is clicker-local presentation (see the summary above).
                if (row != null)
                {
                    if (promoteSpec != null) AccessTools.Field(row.GetType(), "TrainingSpec")?.SetValue(row, promoteSpec);
                    AccessTools.Method(personnel, "RemovePersonnel")?.Invoke(null, new object[] { geo.PhoenixFaction, row });
                }
                Debug.Log("[MP][personnel] HOST intent APPLIED op=" + op + " (TFTV move) char=U#" + charId +
                          " nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { Reject(senderPeerId, charId, "(throw) " + ex.Message); }
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
                if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character))
                { Reject(senderPeerId, charId, "unresolved character"); return; }
                // Reassign moves ground vehicles/mutogs too — no Progression (same relaxation as op=fire).
                if (character.Progression == null && op != OpReassign)
                { Reject(senderPeerId, charId, "no progression"); return; }
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

        /// <summary>Replay of the Recruits-tab hire, BOTH native halves in gesture order: the UI's
        /// wallet debit (UIStateRosterRecruits:300) and the model hire (HireNakedRecruit :662-671),
        /// with cost and gates re-derived from HOST state (GetNakedRecruitCost :644, CanRecruitCharacter
        /// :614 — the same capacity+wallet gate every hire flow uses). Content-key resolution: see
        /// <see cref="NakedHireCapturePatch"/>. RACE: sequential dispatch — the loser's recruit is
        /// already out of the host dict → no match → reject. KNOWN LIMIT (handed to the coverage owner):
        /// GeoFactionInstanceData.NewNakedRecruits is EXCLUDED from the rail (rail-baseline:330,
        /// non-simple dict) and the client sim-gate never regenerates — so peers' recruit LISTS only
        /// change on join until that dict rides as a replaced value; a reject cannot heal them either
        /// (no covered subtree to re-emit — nudge-only).</summary>
        private static void HandleHireNakedIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string name = null;
            try
            {
                name = r.ReadString();
                string templateGuid = r.ReadString();
                int level = r.ReadInt32();
                string siteRef = r.ReadString();
                var geo = GeoLevel();
                if (geo == null) { RejectNaked(senderPeerId, name, "no geoscape"); return; }
                var phoenix = geo.PhoenixFaction;
                GeoUnitDescriptor recruit = null;
                foreach (var d in phoenix.NakedRecruits.Keys)
                {
                    if (d != null && string.Equals(d.Identity?.Name, name, StringComparison.Ordinal) &&
                        (d.UnitType?.TemplateDef?.Guid ?? "") == templateGuid && d.Level == level)
                    { recruit = d; break; }
                }
                if (recruit == null)
                { RejectNaked(senderPeerId, name, "not on host list (regenerated or already taken)"); return; }
                if (!(IdentityResolver.Resolve(geo, siteRef, null) is GeoSite site) ||
                    !ReferenceEquals(site.Owner, phoenix))
                { RejectNaked(senderPeerId, name, "unresolved or foreign site " + siteRef); return; }
                var cost = phoenix.GetNakedRecruitCost(recruit);
                if (!phoenix.CanRecruitCharacter(recruit, cost))
                { RejectNaked(senderPeerId, name, "capacity or resources short"); return; }

                phoenix.Wallet.Take(cost, OperationReason.Purchase); // native UI half (:300)
                phoenix.HireNakedRecruit(recruit, site);             // native model half (:662)
                Debug.Log("[MP][personnel] HOST intent APPLIED op=hireNaked '" + name + "' nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { RejectNaked(senderPeerId, name, "(throw) " + ex.Message); }
        }

        private static void RejectNaked(ulong peer, string name, string why) =>
            IntentRail.Reject(SurfaceIds.GeoPersonnelIntent, peer, "hireNaked '" + (name ?? "?") + "' — " + why);

        /// <summary>Replay of one stat GESTURE (per-click ±1 since 2026-07-25; the shape still takes the
        /// three-delta wire body unchanged). Cost and cap are checked on the DISPLAY scale, exactly like
        /// the native per-click gate (ChangeCharacterStat:879-881/:909 — CanModifyBaseStat(display±1) +
        /// GetBaseStatCost), where display = GetProgressionBaseStats() + Bonus* (RefreshStats:515-518) =
        /// base stat + Σ bodypart aspect + augment bonus. Charging on the BASE scale undercharged (cost is
        /// value-dependent: CharacterProgression.cs:274-294, Strength=value/2, Will/Speed=value) and let
        /// the display value pass the sheet cap. The increments still land on the base stat — one click
        /// is ±1 on both scales.
        ///
        /// A DECREASE is the undo round-trip: refund per step = GetBaseStatCost of the value being
        /// stepped down FROM — the native decrement's own math (:909), the exact mirror of the
        /// increment's cost, so +1 then −1 is SP-neutral to the last point. The gesturing peer's native
        /// stage floors decrements at the VISIT baseline (:907), which survives rail repaints through
        /// UiNativeRepaint.StageBaselines — so the whole visit stays undoable on both peers.
        /// Host-side the floors are NATIVE only: CanModifyBaseStat's [0, sheet max] plus base ≥ 0
        /// (keeps the ModifyBaseStat clamp from out-running a refund's cost walk). The session
        /// purchase ledgers died with the co-management refit — the cost math is exactly symmetric, so
        /// no refund can mint SP; what a decrement CAN do is convert base stats into SP at fair price,
        /// which native only prevents per-visit and co-op does not police. Refunds land on the
        /// personal pool (<see cref="Refund"/>).</summary>
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
        /// covered wallet). Zero traffic when the native gate would refuse (no allowance).</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.ResetCharacterProgression))]
        internal static class SkillResetPatch
        {
            private static bool Prefix(GeoCharacter __instance, ref bool __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
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
        }

        /// <summary>Inverse of <see cref="Charge"/> for the undo round-trip — mutoids get mutagens back
        /// in the wallet; humans get the PERSONAL pool. ponytail: native refunds faction-first capped by
        /// the visit's faction spill (ChangeCharacterStat:915-931) — that memory died with the session
        /// ledgers, so a wire undo of a faction-spilled charge lands personal: total conserved, no mint,
        /// worst case faction SP converts into one soldier's. Re-ledger the spill if it ever matters.</summary>
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

        /// <summary>Replay of the skill-reset: the SAME native funnel the capture blocked client-side
        /// (see <see cref="SkillResetPatch"/> for the full ground). No Charge/Refund here — the native
        /// body IS the refund, computed from the host's own tracks; its false return (allowance already
        /// spent / no progression) is the reject.</summary>
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
