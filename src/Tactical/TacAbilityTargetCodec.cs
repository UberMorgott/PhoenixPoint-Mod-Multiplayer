using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Core;
using Base.Entities;
using Base.Utils.Maths;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Entities.Statuses;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.UI;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE <c>TacticalAbilityTarget</c> CODEC — an EXPLICIT DECLARED FIELD SET, never reflection over the type.
    /// Reflection is not merely slower here, it is unsound: the payload holds LIVE references
    /// (<c>IDamageReceiver</c>, <c>ItemContainer</c>, <c>InventoryComponent</c>, <c>GameObject</c>, a
    /// <c>TacticalAbility</c>, and a recursive <c>List&lt;TacticalAbilityTarget&gt;</c>), so a generic walk
    /// would either serialize a scene graph or silently write nulls.
    ///
    /// Every public instance field of <c>TacticalAbilityTarget</c> is named in exactly ONE of
    /// <see cref="Rides"/> / <see cref="Dropped"/>. That is the coverage law (RailCheck L65-codec): a field
    /// ADDED to the game type — by a patch or by TFTV — lands in neither list and turns the harness RED, so
    /// "the codec quietly stopped carrying something" is not a state this repo can reach. Dropping is allowed;
    /// dropping SILENTLY is not.
    ///
    /// The wire is <c>[mask:u16][riding fields in declared order]</c>. The mask is not decoration: every
    /// position field on the type defaults to <c>InvalidPosition</c> (NaN) and <c>HasPositionToApply</c> is
    /// exactly "not NaN", so the bit IS the has-flag — "move to nowhere" and "move to the origin" are
    /// different orders. A3b turns a Dropped row into a Rides row and takes the next bit; no new surface, no
    /// envelope change, no renumbering (a bit, like an op byte, is never reused).
    /// </summary>
    internal static class TacAbilityTargetCodec
    {
        internal const ushort BitPositionToApply = 1 << 0;
        // ─── A3b: the attack riders. New bits only; no existing bit moved or reused. ───
        internal const ushort BitActor = 1 << 1;
        internal const ushort BitShootTargetActor = 1 << 2;
        internal const ushort BitDamageReceiver = 1 << 3;
        internal const ushort BitActorGridPosition = 1 << 4;
        internal const ushort BitShootFromPos = 1 << 5;
        internal const ushort BitDirection = 1 << 6;
        internal const ushort BitCone = 1 << 7;
        internal const ushort BitAttackType = 1 << 8;
        internal const ushort BitObstructionsCheckRadius = 1 << 9;
        // ─── A7: the two ITEM riders. New bits only; no existing bit moved or reused. ───
        internal const ushort BitEquipment = 1 << 10;
        internal const ushort BitTacticalItem = 1 << 11;

        /// <summary>Every bit this build can decode. A set bit outside it means the sender declared a field
        /// this reader does not know — the rest of the stream is then misaligned, so it is a THROW, not a
        /// best-effort read. (Mod parity is blocking, law 10, so this can only be a bug, never a version skew.)</summary>
        internal const ushort KnownBits = BitPositionToApply | BitActor | BitShootTargetActor | BitDamageReceiver |
                                          BitActorGridPosition | BitShootFromPos | BitDirection | BitCone |
                                          BitAttackType | BitObstructionsCheckRadius | BitEquipment | BitTacticalItem;

        /// <summary>The fields that ACTUALLY ride, in wire order.
        /// A3a: movement needed exactly one thing — <c>MoveAbility.Move</c>:105-144 reads
        /// <c>target.PositionToApply</c> (:118) and nothing else off the payload except the followup pair.
        /// A3b adds the ATTACK fields, each grounded in a real read inside the shot path rather than assumed:
        /// <c>AttackType</c> gates overwatch/return-fire branches and the shot COUNT
        /// (<c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1511/1515/1551/1597/1695/1756 and
        /// <c>ShootAbility.ShouldApplyCosts</c>:145 — the flag that decides whether the shot costs AP at all);
        /// <c>ShootFromPos</c> is the stepout decision and the actual navigation destination before firing
        /// (:1556, :1625, :1633); <c>Actor</c>/<c>ShootTargetActor</c>/<c>DamageReceiver</c> are what
        /// <c>GetTargetActor</c>:153-164 and :1571/:1858 read to know WHO is being shot;
        /// <c>ActorGridPosition</c> is the TARGET's tile (<c>TacticalAbilityTarget</c>:64-70 sets it from the
        /// target actor, NOT from the shooter — A3a's drop reason said "the SOURCE tile" and was WRONG) and is
        /// the first branch of <c>GetActorPosition</c>:214-226; <c>Direction</c>/<c>Cone</c> shape cone and
        /// line weapons and <c>Cone.Tip</c> is the last-resort working position (:186-189);
        /// <c>ObstructionsCheckRadius</c> is a line-of-fire tolerance that defaults to +Inf and would silently
        /// differ the moment any caller narrows it.
        ///
        /// A7 PROMOTES THE TWO ITEM FIELDS out of <see cref="Dropped"/>, because a real shipped ability READS
        /// them off the payload and the drop was silently changing what the mirror did:
        /// <c>ReloadAbility.ChooseEquipmentAndAmmo</c>:111-114 takes <c>target.Equipment</c> and
        /// <c>target.TacticalItem</c> as THE weapon and THE magazine and only falls back to
        /// <c>SelectedEquipment</c> + "first compatible clip" (:133-138) when they are null — so a mirrored
        /// reload was reloading whatever that peer happened to be holding; and
        /// <c>DropItemAbility.Activate</c>:36 dereferences <c>tacticalAbilityTarget.TacticalItem</c>
        /// UNCONDITIONALLY once a non-null target was passed (its null-target fallback at :19-35 never runs for
        /// us, because the codec always hands it a target object), which is a plain NRE on every mirroring
        /// peer. <c>ShootAbility</c>:196-221 also stores the aimed-at body item there.</summary>
        internal static readonly string[] Rides =
        {
            "PositionToApply", "Actor", "ShootTargetActor", "DamageReceiver", "ActorGridPosition",
            "ShootFromPos", "Direction", "Cone", "AttackType", "ObstructionsCheckRadius",
            "Equipment", "TacticalItem",
        };

        /// <summary>The fields that deliberately do NOT ride, each with the reason. This list is the other
        /// half of the coverage law — it is what makes a drop a DECISION instead of an omission.</summary>
        internal static readonly Dictionary<string, string> Dropped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "GameObject",           "a live scene object; the peer's own equivalent is reached through the actor key, never shipped" },
            { "CoverDirection",       "presentation: which way the actor hugs cover on arrival (law 5 names it local-only)" },
            { "ItemContainer",        "A4 (inventory) — a live container reference" },
            { "InventoryComponent",   "A4 (inventory) — a live component reference" },
            { "MultiAbilityTargets",  "recursive list; no rider is a multi-target ability, and shipping it would need the whole codec to nest" },
            { "FollowupAbility",      "a live ability reference. Move+shoot chains: the followup is a SECOND command and rides as its own intent, not as a passenger" },
            { "FollowupAbilityTarget","same — the followup's own payload travels with the followup's own command" },
            { "UseShootOriginCache",  "a per-peer performance hint (a projectile-origin transform cache), never shared state" },
        };

        /// <summary>
        /// A7 — THE DROPS THAT SOMETHING ACTUALLY READS, and what the replaying peer does instead.
        ///
        /// <see cref="Dropped"/> says a field does not ride; it never said whether anything MISSES it. RailCheck
        /// L76a now answers that mechanically — it closes each rider ability's own <c>Activate</c> over its
        /// callees and its coroutine state machines and reports every dropped field the replay path LOADS —
        /// and the answer for two of them was "the reload picks a different gun" and "DropItemAbility
        /// dereferences a null", which is why <c>Equipment</c> and <c>TacticalItem</c> now ride.
        ///
        /// These five are the remainder: read, still dropped, each with the CONSEQUENCE written down instead
        /// of discovered in a log at midnight. A drop that something reads must be in this table, and a row
        /// here that nothing reads any more is a violation too — the declaration may not outlive its reason.
        /// </summary>
        internal static readonly Dictionary<string, string> DroppedButRead =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "FollowupAbility",
              "CaterpillarMoveAbility chains through it. The mirror plays the move alone; the followup reaches " +
              "every peer as its OWN command a moment later, which is the declared design (a chained order is " +
              "two orders, not a passenger)" },
            { "GameObject",
              "ApplyEffectAbility reaches it through TacticalAbilityTarget.ToEffectTarget:278-282. The mirror's " +
              "EffectTarget.GameObject is null, so the effect resolves against PositionToApply / the actor key " +
              "instead of a scene object — correct for every actor-targeted effect, and a scene-object-targeted " +
              "one lands at the same coordinates rather than on the same instance. THAT HOLDS FOR EFFECTS AND " +
              "NOT FOR ATTACKS (2026-08-01): BashAbility.ApplyPayloadEffects:566 dereferences the field with no " +
              "null test, and the NRE broke the coroutine chain so ClearPlayingAction never ran and the actor " +
              "stayed in ExecutingAbilities for the rest of the battle. An attack target's scene object is now " +
              "RE-DERIVED per peer in TacticalCommandSync.FillLiveTargetObject via the game's own " +
              "IAttackAbility.GetAttackActorTarget — still not shipped, which is why the drop stands" },
            { "InventoryComponent",
              "ApplyStatusAbility reads it to reach an inventory's owner. The mirror falls through to the same " +
              "actor by key (TacticalAbilityTarget.GetWorkingPosition:181 uses it only as a POSITION source, " +
              "after the actor and receiver it is given)" },
            { "ItemContainer",
              "ApplyStatusAbility reads it the same way. A crate is keyed on the wire by A6 " +
              "(ItemContainer IS a TacticalActorBase), so the container this names is reachable — it is the " +
              "live REFERENCE that is not shipped, and the mirror resolves position instead" },
            { "MultiAbilityTargets",
              "BashAbility reads the recursive list. A mirrored multi-target activation replays against its " +
              "PRIMARY target only; shipping it would make the codec nest itself, and no rider today is " +
              "authored as a multi-target ability" },
        };

        /// <summary>A5 — THE DROP HAS TO BE AUDIBLE, not merely declared. While the rider set was a whitelist of
        /// five classes their payloads were known and the <see cref="Dropped"/> list was a design note. A5
        /// inverts the set, so abilities nobody analysed now cross — and one whose payload really does carry a
        /// dropped field would be replayed WITHOUT it, silently aiming at something else. This says so once per
        /// field, the same shape as every other "this peer knowingly did less" notice in the arc. The list is
        /// the reference-typed entries that a real activation can actually populate; the value-typed and
        /// presentation ones (CoverDirection, UseShootOriginCache, GameObject) are reached another way or are
        /// local by law 5.</summary>
        private static readonly HashSet<string> _saidDropped = new HashSet<string>(StringComparer.Ordinal);

        internal static void ResetDropNotices() => _saidDropped.Clear();

        private static void NoteDroppedField(string field, object value)
        {
            if (value == null || !_saidDropped.Add(field)) return;
            string why;
            Dropped.TryGetValue(field, out why);
            MpLog.LogWarning("[Multiplayer][tac] an activation carried TacticalAbilityTarget." + field +
                             ", which this codec DROPS (" + (why ?? "no declared reason") + "). The order still " +
                             "crosses, but every other peer replays it without that field — first occurrence only.");
        }

        /// <summary>THE ITEM ADDRESS (A7), and it is A6's container address plus the item's own def guid:
        /// <c>(actorKey, Inventory|Equipments, defGuid)</c>. An <c>Item</c> carries exactly one back-pointer,
        /// <c>Item.InventoryComponent</c>:45 (set at <c>OnAddedToInventory</c>:82-85), and
        /// <c>Equipment.EquipmentComponent</c>:19 is that same field cast — so the owning container IS the
        /// address A6 already ships, reused rather than reinvented.
        ///
        /// THE CEILING THAT USED TO BE HERE IS CLOSED. The key was (actorKey, kind, defGuid) alone, so two
        /// items of the same def in one container were ONE address and the far side resolved whichever its
        /// list held first — a soldier carrying a full magazine and a spent one had a reload that aimed at a
        /// coin toss, and the peers landed on different clips. The address now carries the two fields that
        /// tell them apart, both defined once in <see cref="TacticalInventorySync"/> and shared with A6's
        /// layout so there is ONE rule and not two that can drift:
        ///   • <c>ChargeOf</c> — the per-item state (<c>CommonItemData.CurrentCharges</c>), which is what
        ///     actually makes the half-empty clip a DIFFERENT item from the full one;
        ///   • <c>OrdinalOf</c> — the position among the items sharing that (def, charge), which separates
        ///     the remaining genuinely-interchangeable ones into distinct addresses.
        /// The old objection to an ordinal ("it would name a different clip whenever the two lists were
        /// sorted differently") is answered rather than ignored: the ordinal only ever orders WITHIN an
        /// equivalence class whose members match in every field the address can see, so a differently sorted
        /// peer picks a different member of THAT class — never a different charge. Container order is still
        /// not forced, and must not be: forcing it would unmount a weapon nobody touched.
        ///
        /// AND THE SECOND RUNG: A BODY PART IS IN NO INVENTORY AT ALL. <c>Item.InventoryComponent</c> is the
        /// only anchor the rung above has, and a limb never entered one — it hangs in an <c>ItemSlot</c>
        /// (<c>TacticalItem.ParentItemSlot</c>:121). The rider therefore used to drop every limb, and the
        /// compensation in <see cref="Read"/> only refills it when the DamageReceiver IS the limb. It is not,
        /// on the main aiming path: <c>Weapon.GetShootTargets</c>:841-844 sets <c>DamageReceiver</c> to the
        /// target ACTOR and returns at :853, so a shot aimed at a head arrived naming the whole actor and the
        /// receiving peer replayed it against whatever <c>GetWorkingPosition</c> picked. Health never diverged
        /// (damage is authoritative and rides its own record) — the INTENT did, and so did what every other
        /// screen showed. The second rung addresses it the way the game itself does, as (owning actor, slot
        /// name, def guid), and resolution reuses <c>TacticalDamageSync.ResolveItem</c> rather than inventing a
        /// third scheme.</summary>
        private static bool ItemAddress(Item item, out int actorKey, out byte kind, out string defGuid,
                                        out int charge, out int ordinal, out string slot)
        {
            actorKey = 0; kind = 0; defGuid = null; charge = -1; ordinal = 0; slot = "";
            if (item == null) return false;
            defGuid = item.ItemDef == null ? null : item.ItemDef.Guid;
            if (string.IsNullOrEmpty(defGuid)) return false;
            if (TacticalInventorySync.AddressOf(item.InventoryComponent, out actorKey, out kind))
            {
                charge = TacticalInventorySync.ChargeOf(item);
                ordinal = TacticalInventorySync.OrdinalOf(item);
                return true;
            }
            var part = item as TacticalItem;                                 // L113: `as`, see AddressOf
            var parent = ReferenceEquals(part, null) ? null : part.ParentItemSlot;
            if (ReferenceEquals(parent, null)) return false;
            actorKey = TacticalActorKey.Of(parent.GetActor());
            if (actorKey == 0) return false;
            slot = parent.GetSlotName() ?? "";
            if (slot.Length == 0) return false;                              // "" means the actor; a limb is not one
            kind = TacticalInventorySync.KindBodyPart;
            return true;
        }

        /// <summary>The other half. Null + a sentence on any failure — never a "closest match", because a
        /// reload aimed at the wrong weapon is exactly the divergence this rider exists to stop.</summary>
        private static Item ResolveItem(BinaryReader r, string field, TacticalLevelController tlc, List<string> unresolved)
        {
            int key = r.ReadInt32();
            byte kind = r.ReadByte();
            string guid = WireString.ReadKey(r);
            int charge = r.ReadInt32();
            int ordinal = r.ReadInt32();
            string slot = WireString.ReadKey(r);
            string why;
            var owner = TacticalActorKey.Resolve(tlc, key, out why);
            if (owner == null)
            {
                if (unresolved != null) unresolved.Add(field + " owner (key " + key + "): " + why);
                return null;
            }
            if (kind == TacticalInventorySync.KindBodyPart)
            {
                // THE GAME'S OWN TWO-PART LOOKUP, borrowed rather than re-written — TacticalDamageSync.ResolveItem
                // is CharacterBodyState.GetItem(slotName, def) with the refusal sentence already attached.
                var limb = TacticalDamageSync.ResolveItem(owner, slot, guid, out why) as Item;
                if (limb == null && unresolved != null)
                    unresolved.Add(field + " body part in slot '" + slot + "': " + why);
                return limb;
            }
            var container = TacticalInventorySync.ContainerOf(owner, kind);
            if (container == null)
            {
                if (unresolved != null)
                    unresolved.Add(field + ": " + owner.name + " has no container of kind " + kind + " on this peer");
                return null;
            }
            var resolved = TacticalInventorySync.ResolveIn(container, guid, charge, ordinal);
            if (resolved != null) return resolved;
            if (unresolved != null)
                unresolved.Add(field + ": no item #" + ordinal + " with def guid " + guid + " (charge " + charge +
                               ") in " + owner.name + "'s container " + kind + " on this peer — this container " +
                               "holds fewer matching items here than on the sender. Nothing here asked whether " +
                               "the def EXISTS on this peer, so this says nothing about mod parity.");
            return null;
        }

        /// <summary>A7 — an item field that RIDES but has no shared address still has to be audible.
        ///
        /// WHICH ITEMS THESE ARE, measured rather than assumed: <see cref="ItemAddress"/> has TWO rungs — an
        /// inventory item is (owning actor, container kind, def guid, charge, ordinal) off
        /// <c>Item.InventoryComponent</c>:45, and a BODY PART is (owning actor, KindBodyPart, def guid, slot
        /// name) off <c>TacticalItem.ParentItemSlot</c>:121. What is left after both is an item that is in no
        /// inventory AND hangs in no named slot, or whose slot has no actor with a shared key.
        ///
        /// NOT REFUSED, and not given a second addressing scheme either: the limb already has a shared address
        /// on this very target — <c>DamageReceiver</c> rides as (actor key, slot name) through
        /// <c>TacticalActorKey.SlotOf</c>/<c>ResolveReceiver</c>, and <see cref="Read"/> now replays
        /// <c>Weapon.GetShootTarget</c>:792-795's own rule off it, so the peers land on the same item by the
        /// host's derivation instead of by luck. This line is what remains: the cases where even that yields
        /// nothing. COUNTED through the rail's own digest rather than said once, because "first occurrence
        /// only" is exactly what left the real frequency unknown.</summary>
        private static void NoteUnkeyableItem(string field, Item item)
        {
            if (item == null) return;
            var line = "[Multiplayer][tac] an activation carried TacticalAbilityTarget." + field +
                       " (" + (item.ItemDef == null ? item.GetType().Name : item.ItemDef.name) +
                       ") that has NO shared address — it is in no keyed container, which " +
                       "is what a body part looks like. The order still crosses; the receiving peer re-derives " +
                       "that field from the DamageReceiver slot, and falls back to its own aim point only if " +
                       "that yields nothing.";
            if (RailMeta.CountMiss(line)) MpLog.LogWarning(line);
        }

        internal static void Write(BinaryWriter w, TacticalAbilityTarget t)
        {
            int eqKey = 0, itKey = 0;
            byte eqKind = 0, itKind = 0;
            string eqGuid = null, itGuid = null, eqSlot = "", itSlot = "";
            int eqCharge = -1, itCharge = -1, eqOrd = 0, itOrd = 0;
            bool eqRides = false, itRides = false;
            if (t != null)
            {
                eqRides = ItemAddress(t.Equipment, out eqKey, out eqKind, out eqGuid, out eqCharge, out eqOrd, out eqSlot);
                itRides = ItemAddress(t.TacticalItem, out itKey, out itKind, out itGuid, out itCharge, out itOrd, out itSlot);
                if (!eqRides) NoteUnkeyableItem("Equipment", t.Equipment);
                if (!itRides) NoteUnkeyableItem("TacticalItem", t.TacticalItem);
                NoteDroppedField("ItemContainer", t.ItemContainer);
                NoteDroppedField("InventoryComponent", t.InventoryComponent);
                NoteDroppedField("MultiAbilityTargets", t.MultiAbilityTargets);
                NoteDroppedField("FollowupAbility", t.FollowupAbility);
            }
            ushort mask = 0;
            if (t != null)
            {
                if (t.HasPositionToApply) mask |= BitPositionToApply;
                if (t.Actor != null) mask |= BitActor;
                if (t.ShootTargetActor != null) mask |= BitShootTargetActor;
                if (t.DamageReceiver != null) mask |= BitDamageReceiver;
                if (!t.ActorGridPosition.IsNaN()) mask |= BitActorGridPosition;
                if (!t.ShootFromPos.IsNaN()) mask |= BitShootFromPos;
                if (t.Direction != Vector3.zero) mask |= BitDirection;
                if (t.Cone.Height != 0f || t.Cone.Radius != 0f) mask |= BitCone;
                if (t.AttackType != AttackType.Regular) mask |= BitAttackType;
                if (!float.IsPositiveInfinity(t.ObstructionsCheckRadius)) mask |= BitObstructionsCheckRadius;
                if (eqRides) mask |= BitEquipment;
                if (itRides) mask |= BitTacticalItem;
            }
            w.Write(mask);
            if (t == null) return;
            if ((mask & BitPositionToApply) != 0) WriteVec(w, t.PositionToApply);
            if ((mask & BitActor) != 0) w.Write(TacticalActorKey.Of(t.Actor));
            if ((mask & BitShootTargetActor) != 0) w.Write(TacticalActorKey.Of(t.ShootTargetActor));
            if ((mask & BitDamageReceiver) != 0)
            {
                w.Write(TacticalActorKey.Of(t.DamageReceiver.GetActor()));
                w.Write(TacticalActorKey.SlotOf(t.DamageReceiver));
            }
            if ((mask & BitActorGridPosition) != 0) WriteVec(w, t.ActorGridPosition);
            if ((mask & BitShootFromPos) != 0) WriteVec(w, t.ShootFromPos);
            if ((mask & BitDirection) != 0) WriteVec(w, t.Direction);
            if ((mask & BitCone) != 0)
            {
                WriteVec(w, t.Cone.Tip); WriteVec(w, t.Cone.Forward);
                w.Write(t.Cone.Height); w.Write(t.Cone.Radius);
            }
            if ((mask & BitAttackType) != 0) w.Write((byte)t.AttackType);
            if ((mask & BitObstructionsCheckRadius) != 0) w.Write(t.ObstructionsCheckRadius);
            // The slot name is the BODY-PART rung's half of the address (kind == KindBodyPart) and "" for every
            // inventory item. Written unconditionally rather than behind the kind, because a field that is
            // sometimes there is a field the reader can misalign on.
            if ((mask & BitEquipment) != 0) { w.Write(eqKey); w.Write(eqKind); w.Write(eqGuid); w.Write(eqCharge); w.Write(eqOrd); w.Write(eqSlot ?? ""); }
            if ((mask & BitTacticalItem) != 0) { w.Write(itKey); w.Write(itKind); w.Write(itGuid); w.Write(itCharge); w.Write(itOrd); w.Write(itSlot ?? ""); }
        }

        /// <summary>Decode against the RECEIVING peer's own world: every actor-shaped field is a key that is
        /// resolved here, and a key that does not resolve is a LOUD null rather than a silently absent target
        /// (a shot at nobody aims at the map origin). <paramref name="unresolved"/> collects those sentences
        /// so the caller can refuse the whole command with them instead of half-playing it.</summary>
        internal static TacticalAbilityTarget Read(BinaryReader r, TacticalLevelController tlc, List<string> unresolved = null)
        {
            ushort mask = r.ReadUInt16();
            if ((mask & ~KnownBits) != 0)
                throw new InvalidDataException("ability-target mask 0x" + mask.ToString("X4") + " declares field bits " +
                                               "this build cannot decode (known 0x" + KnownBits.ToString("X4") + ") — " +
                                               "the payload after it is misaligned and must not be guessed at");
            var t = new TacticalAbilityTarget();
            if ((mask & BitPositionToApply) != 0) t.PositionToApply = ReadVec(r);
            if ((mask & BitActor) != 0) t.Actor = ResolveActor(r.ReadInt32(), "Actor", tlc, unresolved);
            if ((mask & BitShootTargetActor) != 0) t.ShootTargetActor = ResolveActor(r.ReadInt32(), "ShootTargetActor", tlc, unresolved);
            if ((mask & BitDamageReceiver) != 0)
            {
                int key = r.ReadInt32();
                string slot = WireString.ReadKey(r);
                var owner = ResolveActor(key, "DamageReceiver", tlc, unresolved);
                string why;
                var recv = TacticalActorKey.ResolveReceiver(owner, slot, out why);
                if (recv == null && owner != null && unresolved != null)
                    unresolved.Add("DamageReceiver slot '" + slot + "': " + why);
                t.DamageReceiver = recv;
            }
            if ((mask & BitActorGridPosition) != 0) t.ActorGridPosition = ReadVec(r);
            if ((mask & BitShootFromPos) != 0) t.ShootFromPos = ReadVec(r);
            if ((mask & BitDirection) != 0) t.Direction = ReadVec(r);
            if ((mask & BitCone) != 0)
            {
                var cone = new Cone { Tip = ReadVec(r) };
                cone.Forward = ReadVec(r);
                cone.Height = r.ReadSingle();
                cone.Radius = r.ReadSingle();
                t.Cone = cone;
            }
            if ((mask & BitAttackType) != 0) t.AttackType = (AttackType)r.ReadByte();
            if ((mask & BitObstructionsCheckRadius) != 0) t.ObstructionsCheckRadius = r.ReadSingle();
            if ((mask & BitEquipment) != 0) t.Equipment = ResolveItem(r, "Equipment", tlc, unresolved) as Equipment;
            if ((mask & BitTacticalItem) != 0) t.TacticalItem = ResolveItem(r, "TacticalItem", tlc, unresolved) as TacticalItem;
            // THE LIMB THE HOST AIMED AT, DERIVED THE HOST'S OWN WAY rather than left null. A body part is in no
            // inventory, so ItemAddress cannot key it (see NoteUnkeyableItem) and the field does not ride — but
            // the SAME limb rides as DamageReceiver's (actor key, slot name). Weapon.GetShootTarget:792-795 sets
            // both fields off one receiver by exactly this rule, so replaying it here reproduces the host's
            // choice instead of letting GetWorkingPosition:181 skip to a different aim point on this screen.
            if (t.TacticalItem == null && t.DamageReceiver != null)
            {
                t.TacticalItem = t.DamageReceiver as TacticalItem;
                var slot = t.DamageReceiver as ItemSlot;
                if (t.TacticalItem == null && slot != null)
                    foreach (var candidate in slot.GetAllDirectItems())
                        if (candidate != null && Attached(candidate)) { t.TacticalItem = candidate; break; }
            }
            return t;
        }

        /// <summary>ONE LINE, AND IT IS A SEPARATE METHOD ON PURPOSE — the same containment
        /// <c>TacticalActorKey.ContentKeyOf</c> uses, for the same reason. <c>Addon.IsVisible</c>:195-203 is
        /// <c>_visualRootGameObject.activeSelf</c>, i.e. a one-line wrapper around an ECALL, and under
        /// <c>-c Release</c> the JIT inlines it into whoever calls it — which makes THAT method impossible to
        /// JIT outside the Unity player ("ECall methods must be packaged into a system module"), whatever its
        /// arguments and whether or not the branch is even taken (L113). Inlined into <see cref="Read"/> it
        /// took down every law that decodes a payload: L358 read it as the body-part address being
        /// misaligned, and the codec round-trip law CRASHED, which aborts the whole run and proves nothing
        /// after it. A wire codec is executed by the laws, so it has to stay compilable with no engine
        /// present; the engine questions sit one call away.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Attached(TacticalItem item) => item.IsVisible;

        private static TacticalActorBase ResolveActor(int key, string field, TacticalLevelController tlc, List<string> unresolved)
        {
            string why;
            var actor = TacticalActorKey.Resolve(tlc, key, out why);
            if (actor == null && unresolved != null) unresolved.Add(field + " (key " + key + "): " + why);
            return actor;
        }

        private static void WriteVec(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVec(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
