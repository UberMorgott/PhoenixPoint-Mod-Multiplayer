using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
using PhoenixPoint.Geoscape.View.ViewModules;
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
    /// The three UI methods below ARE the family's chokepoints, not "one button" — every geoscape entry
    /// point funnels through them (verified by call-site sweep over the decompile):
    ///   • <c>CommitStatChanges</c> (:367) — the ONE write of base stats + personal SP + faction pool +
    ///     mutagen. Reached from UIStateEditSoldier:232 (leave screen), :363 (switch soldier), :715
    ///     (after a buy), and from the two module methods below.
    ///   • <c>BuyAbility</c> (:389) — ability/perk purchase (human LearnAbility + mutoid AddAbility).
    ///   • <c>ChoseSecondSpecialization</c> (:813) — the second-class purchase (AddSecondaryClass).
    ///
    /// HOST replay runs the identical native model calls those three make: <c>ModifyBaseStat</c> (:369-373),
    /// <c>LearnAbility</c>/<c>AddAbility</c> (:416/:408), <c>AddSecondaryClass</c> (:820) — with the SP
    /// economy re-derived from the HOST's own numbers (<see cref="Charge"/>), never from the wire.
    ///
    /// WHAT THE WIRE DELIBERATELY DOES NOT CARRY: the shared pool value. <c>GeoPhoenixFaction.Skillpoints</c>
    /// is rail-UNCOVERED today (persisted only via ExtendedInstanceData, declared `object`,
    /// bridge-unresolved), so a stat spend debits it host-side and clients will not SEE the new pool total
    /// until that field gets rail coverage. Shipping the client's pool value here would have made the
    /// client authoritative over a shared resource (law 3) to paper over a coverage gap — so it does not.
    /// </summary>
    public static class PersonnelSync
    {
        // Intent ops (GeoPersonnelIntent inner payload) — each maps onto the native commit it replaces.
        private const byte OpSpendStats = 1;  // CommitStatChanges           → ModifyBaseStat ×3 + SP debit
        private const byte OpBuyAbility = 2;  // BuyAbility                  → LearnAbility / AddAbility
        private const byte OpSecondSpec = 3;  // ChoseSecondSpecialization   → AddSecondaryClass

        private static readonly IntentDedup Intents = new IntentDedup();
        private static uint _nextIntentNonce;

        // ─── Reflection: UIModuleCharacterProgression's staged view-model (all private) ──
        // The module stages the player's pending edit in _current*; _character is the soldier it is bound
        // to. We read them to build the intent and never write any of them.
        private static readonly FieldInfo FCharacter = AccessTools.Field(typeof(UIModuleCharacterProgression), "_character");
        private static readonly FieldInfo FCurStrength = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentStrengthStat");
        private static readonly FieldInfo FCurWill = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentWillStat");
        private static readonly FieldInfo FCurSpeed = AccessTools.Field(typeof(UIModuleCharacterProgression), "_currentSpeedStat");
        // …and the pre-edit snapshot it is diffed against. BOTH halves are DERIVED display values
        // (RefreshStats:515-518 seeds them from GetProgressionBaseStats() + the Bonus* stats, i.e.
        // GetBaseStat(x) + Σ bodyPart aspect + augment bonus), so only their DIFFERENCE is meaningful
        // at model level — which is exactly what native CommitStatChanges:369-373 feeds ModifyBaseStat.
        private static readonly FieldInfo FStartStrength = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingStrengthStat");
        private static readonly FieldInfo FStartWill = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingWillStat");
        private static readonly FieldInfo FStartSpeed = AccessTools.Field(typeof(UIModuleCharacterProgression), "_startingSpeedStat");
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
                    FStartStrength == null || FStartWill == null || FStartSpeed == null ||
                    FBoughtSlot == null || FBoughtAbility == null || FBoughtSource == null || FBoughtLevel == null)
                    Debug.LogError("[MP][personnel] FIELD BIND FAILED on UIModuleCharacterProgression — " +
                                   "stat/ability intents CANNOT be captured; client edits will not sync.");
                else
                    Debug.Log("[MP][personnel] view-model fields bound");
            }
            return FCharacter != null && FBoughtSlot != null && FStartStrength != null;
        }

        public static void Reset()
        {
            ResetForReloadBoundary();
            Intents.Reset();
        }

        /// <summary>Mid-session reload boundary: nothing geoscape-bound is cached here (the seam is
        /// stateless per intent), and the nonce stream persists symmetrically — same rca-3 contract as
        /// <see cref="ResearchSync.ResetForReloadBoundary"/>.</summary>
        public static void ResetForReloadBoundary() { }

        public static void ResetIntentDedupForPeer(ulong peerId) => Intents.ResetPeer(peerId);

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

        private static void Send(byte[] inner, string what)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoPersonnelIntent, SyncKind.ActionRequest, inner);
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][personnel] CLIENT intent " + what + " nonce=" + _nextIntentNonce);
            }
            catch (Exception ex) { Debug.LogError("[MP][personnel] intent send failed: " + ex); }
        }

        // ─── Harmony seams (law 4a, intent-capture only — this file owns no other patch) ──

        /// <summary>
        /// Intent capture for the STAT/SP commit. On a client the native body NEVER runs during a session,
        /// even when nothing changed: it would still write <c>SkillPoints</c> and the faction pool from the
        /// module's staged copy, which is exactly the stale view→model flush that reverted applied intents
        /// on the equip screen (see <see cref="EquipFlushGate"/>). Blocking leaves the staged edit visible
        /// until the rail's universal repaint re-enters the screen and re-seeds it from the mirrored model.
        ///
        /// Composes with <see cref="EquipFlushGate"/>, which already prefixes this method: Harmony skips
        /// later prefixes once one returns false, and both orders are correct here (that gate blocks
        /// exactly when we must not emit either — inside an apply, or during session teardown).
        /// </summary>
        [HarmonyPatch(typeof(UIModuleCharacterProgression), nameof(UIModuleCharacterProgression.CommitStatChanges))]
        internal static class CommitStatsCapturePatch
        {
            private static bool Prefix(UIModuleCharacterProgression __instance)
            {
                if (ShouldRunNative()) return true;
                if (!BindOk()) return false; // never write locally, even when we cannot read the gesture
                try
                {
                    var character = FCharacter.GetValue(__instance) as GeoCharacter;
                    var progression = character?.Progression;
                    if (progression == null) return false;

                    // Native's OWN no-op test (IsCharacterChanged:358) — the same one UIStateEditSoldier:361
                    // gates its commit with, so a screen exit that changed nothing asks for nothing.
                    if (!__instance.IsCharacterChanged()) return false;

                    // Ship the DELTA, never the absolute: _current*/_starting* are derived DISPLAY values
                    // (base stat + Σ bodypart aspect + augment bonus, RefreshStats:515-518), so an absolute
                    // is on a different scale than progression.GetBaseStat and the host would reject it as
                    // out of range. Their difference is exactly what native feeds ModifyBaseStat (:369-373)
                    // and it is offset-free. ponytail: a client whose view is stale by an unseen host spend
                    // can still ask for one increment too many — the host's own Charge()/CanModifyBaseStat
                    // is the authority that refuses it, which is the same guard native relies on.
                    int dStr = (int)FCurStrength.GetValue(__instance) - (int)FStartStrength.GetValue(__instance);
                    int dWill = (int)FCurWill.GetValue(__instance) - (int)FStartWill.GetValue(__instance);
                    int dSpeed = (int)FCurSpeed.GetValue(__instance) - (int)FStartSpeed.GetValue(__instance);

                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms, Encoding.UTF8))
                    {
                        w.Write(++_nextIntentNonce);
                        w.Write(OpSpendStats);
                        w.Write((int)character.Id);
                        w.Write(dStr);
                        w.Write(dWill);
                        w.Write(dSpeed);
                        Send(ms.ToArray(), "stats U#" + (int)character.Id + " dStr=" + dStr + " dWill=" + dWill + " dSpeed=" + dSpeed);
                    }
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] stat capture failed: " + ex); }
                return false;
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

                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms, Encoding.UTF8))
                    {
                        w.Write(++_nextIntentNonce);
                        w.Write(OpBuyAbility);
                        w.Write((int)character.Id);
                        w.Write((int)source);
                        w.Write(slotLevel);
                        w.Write(buttonLevel);
                        w.Write(ability == null ? "" : ability.Guid);
                        Send(ms.ToArray(), "ability U#" + (int)character.Id + " track=" + source + " lvl=" + slotLevel);
                    }
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

                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms, Encoding.UTF8))
                    {
                        w.Write(++_nextIntentNonce);
                        w.Write(OpSecondSpec);
                        w.Write((int)character.Id);
                        w.Write(specialization.Guid);
                        Send(ms.ToArray(), "secondSpec U#" + (int)character.Id + " spec=" + specialization.Guid);
                    }
                }
                catch (Exception ex) { Debug.LogError("[MP][personnel] second-spec capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: dedup → resolve → validate → execute the SAME native methods ──

        /// <summary>Returns true when the surface was consumed (GeoPersonnelIntent).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoPersonnelIntent) return false;
            if (engine == null || !engine.IsHost) return true; // intents are host-only inbound
            int charId = -1;
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint nonce = r.ReadUInt32();
                    byte op = r.ReadByte();
                    charId = r.ReadInt32();
                    if (!Intents.IsNew(senderPeerId, SurfaceIds.GeoPersonnelIntent, nonce)) return true; // double-send

                    var geo = GeoLevel();
                    if (geo == null) { Reject(senderPeerId, charId, "no geoscape"); return true; }
                    if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter character) ||
                        character.Progression == null)
                    { Reject(senderPeerId, charId, "unresolved character"); return true; }
                    // Ownership: the rail resolves ANY character — never let a client intent drive an
                    // NPC-faction soldier's progression.
                    if (!ReferenceEquals(character.Faction, geo.PhoenixFaction))
                    { Reject(senderPeerId, charId, "not a Phoenix soldier"); return true; }

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
                        case OpSecondSpec:
                            ok = ApplySecondSpec(senderPeerId, character, r.ReadString());
                            break;
                        default:
                            ok = false;
                            Reject(senderPeerId, charId, "unknown op " + op);
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
            }
            catch (Exception ex)
            {
                // Native validation throws (AddSecondaryClass) double as the reject path — silent + logged.
                Debug.LogWarning("[MP][personnel] HOST intent REJECT (throw) char=U#" + charId + ": " + ex.Message);
            }
            return true;
        }

        /// <summary>Replay of <c>CommitStatChanges</c>:369-375 at model level. The wire carries the same
        /// INCREMENTS native feeds <c>ModifyBaseStat</c> (see the capture patch for why an absolute cannot
        /// travel: the module's numbers are bonus-inflated display values). Cost and cap are checked on the
        /// DISPLAY scale, exactly like the native per-click gate (ChangeCharacterStat:879-881/:909 —
        /// CanModifyBaseStat(display+1) + GetBaseStatCost(display+1)), where display =
        /// GetProgressionBaseStats() + Bonus* (RefreshStats:515-518) = base stat + Σ bodypart aspect +
        /// augment bonus. Charging on the BASE scale undercharged (cost is value-dependent:
        /// CharacterProgression.cs:274-294, Strength=value/2, Will/Speed=value) and let the display value
        /// pass the sheet cap. The increments still land on the base stat — one click is +1 on both scales.
        /// A DECREASE is never accepted (the native UI cannot commit one either — it clamps at the starting
        /// value, UIModuleCharacterProgression.cs:907).</summary>
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
            int total = 0;
            for (int i = 0; i < 3; i++)
            {
                int want = display[i] + delta[i];
                if (delta[i] < 0) { Reject(peer, (int)character.Id, "stat decrease " + stats[i]); return false; }
                // Bounds the cost loop below as well: CanModifyBaseStat rejects anything above the sheet
                // maximum (and a display+delta overflow wraps negative → also rejected), so a hostile
                // delta=int.MaxValue cannot spin here.
                if (delta[i] > 0 && !progression.CanModifyBaseStat(stats[i], want))
                { Reject(peer, (int)character.Id, "stat out of range " + stats[i] + "=" + want); return false; }
                for (int v = display[i] + 1; v <= want; v++) total += progression.GetBaseStatCost(stats[i], v);
            }
            if (total == 0) return false; // no-op intent (client view was already behind)
            if (!Charge(character, total)) { Reject(peer, (int)character.Id, "cannot afford " + total + " SP"); return false; }
            for (int i = 0; i < 3; i++)
                if (delta[i] != 0) progression.ModifyBaseStat(stats[i], delta[i]);
            return true;
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

        private static void Reject(ulong peer, int charId, string why) =>
            Debug.LogWarning("[MP][personnel] HOST intent REJECT char=U#" + charId + " peer=" + peer + " — " + why);
    }
}
