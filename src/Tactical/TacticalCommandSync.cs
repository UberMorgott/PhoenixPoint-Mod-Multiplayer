using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE ACTOR KEY for the tactical band. Law 2 names <c>GeoTacUnitId</c> as a legal entityId and this is
    /// it, read straight off the actor: <c>TacticalActorBase.GeoUnitId</c>.
    ///
    /// Why it is RELOAD-STABLE, which is the whole requirement (v1's arbiter died on reload because it keyed
    /// on session-local occurrence ids): the id is not derived, it is SERIALIZED. It round-trips through the
    /// save graph as <c>TacActorBaseInstanceData.GeoUnitId</c> — written at
    /// <c>TacticalActorBase.RecordInstanceData</c>:494 and restored at <c>ProcessInstanceData</c>:399 — so it
    /// survives save/load, and it survives the A1 ENTRY PATH for free: both peers build their battle from the
    /// SAME mid-tactical save blob, so both restore byte-identical ids with no handshake and no side table.
    /// It is also the game's own cross-layer identity (<c>GeoLevelController.GetTacUnitById</c>, used all over
    /// <c>GeoMission</c>), so a soldier keeps one key from the geoscape roster through the battle and back.
    ///
    /// NOT universal on its own, and A3b is where that bit: an actor that never came from the geoscape (a
    /// procedurally spawned Pandoran) carries <c>GeoTacUnitId.None</c> == 0, and MANY of them do at once —
    /// see <c>HavenMissionUtil</c>:46 filtering on exactly that. A3a refused 0 outright because it only ever
    /// keyed PLAYER-faction soldiers (deployed <c>GeoCharacter</c>s, always a real id —
    /// <c>TacCharacterData</c>:131 <c>GeoUnitId = Id</c>). A3b must name the alien on the receiving end of a
    /// shot, so 0 needs an answer.
    ///
    /// THE ANSWER IS A SECOND, *DERIVED* KEY — and the first thing to record is what it is NOT, because two
    /// obvious candidates were checked against the decompile and both are wrong:
    ///   • THERE IS NO SERIALIZED PER-ACTOR IDENTITY to reuse. <c>ActorInstanceData</c> carries
    ///     Pos/Rot/Source/SourceTemplate/TimingData and no id; <c>TacActorBaseInstanceData</c> adds def,
    ///     faction, participant, <c>GeoUnitId</c> and tags — nothing unique for a spawned Pandoran. The
    ///     <c>SceneObjectId</c> on <c>ActorComponent</c> is a scene-authoring guid, not a spawn identity.
    ///   • STAMPING A SYNTHETIC <c>GeoUnitId</c> ON ALIENS (which WOULD ride the save for free) is refused:
    ///     <c>GeoMission</c>:788-795 skips exactly <c>GeoTacUnitId.None</c> and <c>Debug.LogError</c>s any
    ///     other id it cannot find in the geoscape, and <c>PhoenixStatisticsManager</c>:814-878 keys soldier
    ///     stats off the same field. A fake id turns every alien into a mission-completion error.
    /// So the key is derived from state the peers PROVABLY share: the ORDINAL of the actor in the key-less
    /// set, ordered canonically by its BATTLE-START POSITION. Built ONCE per battle
    /// (<see cref="BuildBattleKeys"/>, driven from the first turn edge — <c>TacNewTurnHook</c>, which fires on
    /// every peer running the native turn machine) and then CACHED per actor object, which is what makes it
    /// survive the two things that kill an ordinal: an actor leaving play (<c>BaseMap</c>:51 removes it from
    /// the list, renumbering everything after it) and actors moving. Both peers build from the SAME
    /// mid-tactical save blob (A1), so the positions they sort by are byte-identical, and tiles are exclusive
    /// (actors are dynamic nav obstacles), so the sort has no ties to break — a tie is reported LOUDLY rather
    /// than resolved. Derived keys are NEGATIVE so they can never collide with a real <c>GeoTacUnitId</c>
    /// (the geoscape mints those from a positive counter), and 0 still means "no shared identity".
    ///
    /// A4 CLOSES THE CEILING A3b LEFT: an actor that ENTERS play after the build cannot be keyed by ANY
    /// derived scheme, because a derived key is a function of a shared snapshot and that actor was in no
    /// snapshot. Its key is therefore HOST-ASSIGNED (<see cref="AssignHostKey"/>) and ships WITH the spawn
    /// event; every other peer <see cref="Adopt"/>s it verbatim. The two schemes share ONE counter
    /// (<c>_nextDerived</c>) so they can never mint the same number: the build runs first on every peer
    /// (<see cref="Built"/> gates assignment), consumes -1..-N over the identical shared board, and every
    /// mid-battle spawn continues from -(N+1). Adoption never consumes the counter — the key is given, not
    /// minted — which is what keeps a peer that adopts BEFORE its own build from shifting its own ordinals.
    /// </summary>
    internal static class TacticalActorKey
    {
        private static readonly Dictionary<TacticalActorBase, int> _derived =
            new Dictionary<TacticalActorBase, int>();
        private static readonly Dictionary<int, TacticalActorBase> _byDerived = new Dictionary<int, TacticalActorBase>();
        private static bool _built;

        /// <summary>A5 — KEYS THIS PEER HAS PERMANENTLY REFUSED, with the reason. A4 left one honest ceiling:
        /// a spawn whose <c>ComponentSetDef</c> is RUNTIME-GENERATED (a deployed <c>GeoCharacter</c>, a
        /// mod-built template) carries no guid any other peer can look up, so the actor really does exist on
        /// the host alone. That is not fixable from here — the rebuild would need the character's whole
        /// loadout, and the geoscape level it lives in is not even loaded during a battle — so A5 makes the
        /// FAILURE first-class instead: the key is registered with its reason, and every later command, hit or
        /// settle naming it says "the host spawned an actor this peer cannot rebuild" instead of the generic
        /// and actively misleading "either the peers built different key maps or a spawn record never
        /// arrived". A refusal that names its own cause is a documented boundary; one that names the wrong
        /// cause sends the next reader hunting a lost packet that never existed.</summary>
        private static readonly Dictionary<int, string> _refused = new Dictionary<int, string>();

        /// <summary>The next free derived ordinal, shared by the battle-start build and by every mid-battle
        /// host assignment (A4). One counter, so the two schemes can never mint the same number.</summary>
        private static int _nextDerived = -1;

        /// <summary>Whether <see cref="BuildBattleKeys"/> has run for this battle. A command that needs a
        /// derived key before the map exists is refused with that reason, never resolved by accident.</summary>
        internal static bool Built => _built;

        internal static void Reset()
        {
            _derived.Clear();
            _byDerived.Clear();
            _refused.Clear();
            _built = false;
            _nextDerived = -1;
        }

        /// <summary>Record that this peer can never resolve <paramref name="key"/>, and why. Idempotent.</summary>
        internal static void Refuse(int key, string why)
        {
            if (key != 0 && !string.IsNullOrEmpty(why)) _refused[key] = why;
        }

        /// <summary>Build the derived-key map for THIS battle. Idempotent by design — it runs at the FIRST
        /// turn edge and never again, because after that the peers' alien positions legitimately diverge (the
        /// AI turn is host-only, A2's <c>ClientAiGate</c>, so aliens never move on a client at all). Rebuilding
        /// later would therefore produce two different maps and silently point every alien key at the wrong
        /// monster — which is why this is a hard one-shot and not a lazy "build on first need".</summary>
        internal static void BuildBattleKeys(TacticalLevelController tlc)
        {
            if (_built) return;
            // ReferenceEquals, not == (L113). Two reasons, and the second is why Release used to die here:
            // (1) the question is "was I handed a controller", not "is its native half alive"; (2) Unity's
            // == reaches the GetCachedPtr ECall, which the JIT INLINES into this method under -c Release —
            // and an inlined ECall cannot be compiled outside the player, so the whole method threw
            // "ECall methods must be packaged into a system module" before its first line ran. Debug never
            // saw it because nothing inlines there and the harness always passes null.
            if (ReferenceEquals(tlc, null) || ReferenceEquals(tlc.Map, null)) return;
            var keyless = new List<TacticalActorBase>();
            foreach (var a in tlc.Map.GetActors<TacticalActorBase>())
                // A4: an actor that already carries an ADOPTED host key is deliberately NOT in the ordinal
                // set. The host built its map before that actor existed, so including it here would shift
                // every ordinal after it and point this peer's alien keys at different monsters.
                if ((int)a.GeoUnitId == 0 && !_derived.ContainsKey(a)) keyless.Add(a);
            keyless.Sort(CanonicalOrder);
            for (int i = 0; i < keyless.Count; i++)
            {
                if (i > 0 && CanonicalOrder(keyless[i - 1], keyless[i]) == 0)
                    Debug.LogError("[Multiplayer][tac] two key-less actors are indistinguishable at battle start (" +
                                   keyless[i].name + " and " + keyless[i - 1].name + " at " + keyless[i].Pos +
                                   ") — their derived keys depend on enumeration order and the peers may disagree " +
                                   "about which is which. Every shot naming either of them is suspect.");
                int key = _nextDerived--;
                _derived[keyless[i]] = key;
                _byDerived[key] = keyless[i];
            }
            _built = true;
            Debug.Log("[Multiplayer][tac] derived battle keys for " + keyless.Count + " actor(s) the geoscape " +
                      "never named (ordinals over battle-start position).");
        }

        /// <summary>Position first (the peers' save-restored floats are bit-identical, so this is a total
        /// order in practice), name as the LAST resort so a reported tie is still deterministic rather than
        /// left to list order.</summary>
        private static int CanonicalOrder(TacticalActorBase a, TacticalActorBase b)
        {
            int c = a.Pos.x.CompareTo(b.Pos.x);
            if (c != 0) return c;
            c = a.Pos.z.CompareTo(b.Pos.z);
            if (c != 0) return c;
            c = a.Pos.y.CompareTo(b.Pos.y);
            if (c != 0) return c;
            return string.CompareOrdinal(a.name, b.name);
        }

        /// <summary>A4 — THE HOST MINTS the key for an actor that entered play mid-battle, at the moment it
        /// entered. This is the only scheme that can work for such an actor: a DERIVED key is a function of a
        /// board both peers snapshotted, and a mid-battle spawn was in nobody's snapshot. A real
        /// <c>GeoUnitId</c> still wins (a deployed <c>GeoCharacter</c> already has a cross-layer identity and
        /// stamping a second one over it would break <c>PhoenixStatisticsManager</c>); otherwise the next free
        /// ordinal is taken and the number rides on the spawn event for every other peer to
        /// <see cref="Adopt"/>. Idempotent: a second call for the same actor returns the same key.</summary>
        internal static int AssignHostKey(TacticalActorBase actor)
        {
            if (ReferenceEquals(actor, null)) return 0;
            int geo = (int)actor.GeoUnitId;
            if (geo != 0) return geo;
            int existing;
            if (_derived.TryGetValue(actor, out existing)) return existing;
            int key = _nextDerived--;
            _derived[actor] = key;
            _byDerived[key] = actor;
            return key;
        }

        /// <summary>A4 — take the host's number verbatim, and DO NOT touch the counter. The key is GIVEN, not
        /// minted, and moving the counter for it would shift this peer's own battle-start ordinals whenever a
        /// spawn record lands before <see cref="BuildBattleKeys"/> runs here. It is safe to leave the counter
        /// alone because a host-assigned key is ALWAYS below the build range: the host builds first (assignment
        /// is gated on <see cref="Built"/>), so it has already consumed -1..-N over the same board this peer is
        /// about to consume -1..-N over, and everything it hands out afterwards starts at -(N+1). A collision
        /// would therefore mean the two peers disagree about the battle-start roster, which is why the one that
        /// is detected here is reported as loudly as it is.</summary>
        internal static void Adopt(TacticalActorBase actor, int key)
        {
            if (ReferenceEquals(actor, null) || key >= 0) return;   // 0 = no identity, positive = its own GeoUnitId
            TacticalActorBase other;
            if (_byDerived.TryGetValue(key, out other) && !ReferenceEquals(other, actor))
                Debug.LogError("[Multiplayer][tac] the host's spawn key " + key + " is ALREADY taken on this peer by " +
                               SafeName(other) + " — the peers disagree about this battle's starting roster, so two " +
                               "actors now answer to one key and every command or result naming it reaches the " +
                               "wrong one.");
            _derived[actor] = key;
            _byDerived[key] = actor;
        }

        internal static int Of(TacticalActorBase actor)
        {
            // ReferenceEquals, not ==, for the reason ResolveReceiver already spells out: Unity's overloaded
            // == answers "is this object DESTROYED", and a destroyed actor still has a key — a corpse the game
            // just destroyed (DieAbility.PostProcessDeath:71) is exactly what a trailing damage or settle
            // record names, and answering 0 for it would drop that record with a "no shared key" line about an
            // actor that has one.
            if (ReferenceEquals(actor, null)) return 0;
            int geo = (int)actor.GeoUnitId;
            if (geo != 0) return geo;
            int derived;
            return _derived.TryGetValue(actor, out derived) ? derived : 0;
        }

        /// <summary>Null + a human reason on any failure — never a silent "closest match". The positive scan
        /// is over EVERY actor on the map (not just the player's) precisely so a collision with a key-less
        /// alien is detected instead of being invisible.</summary>
        internal static TacticalActorBase Resolve(TacticalLevelController tlc, int key, out string why)
        {
            why = null;
            if (key == 0)
            {
                why = "actor key 0 = no shared identity — this actor carries GeoTacUnitId.None, was not present when this battle's " +
                      "derived keys were built, and carries no host-assigned spawn key either (A4 assigns one " +
                      "to every mid-battle spawn, so a key-0 actor here entered play on this peer ALONE)";
                return null;
            }
            // The DERIVED branch is answered BEFORE the map is consulted, and deliberately: a derived key is
            // resolved out of this battle's own key map, not by scanning the level, so "there is no map here"
            // is never the reason a peer gives for failing to find an alien.
            if (key < 0)
            {
                // The MAP is consulted before the built flag, and that order is A4's: a host-assigned spawn key
                // is adopted the moment its spawn record lands, which can legitimately be BEFORE this peer
                // reaches its own first turn edge. Asking "is the map built?" first would refuse a key that is
                // sitting right there.
                TacticalActorBase derived;
                if (_byDerived.TryGetValue(key, out derived)) return derived;
                if (_refused.TryGetValue(key, out why)) return null;   // A5: the documented refusal wins
                if (!_built)
                {
                    why = "derived actor key " + key + " arrived before this peer built its battle key map — " +
                          "the peers are not at the same point in the battle";
                    return null;
                }
                why = "no actor carries derived key " + key + " on this peer — either the peers built different " +
                      "key maps or a host spawn record never arrived, so every actor named on the wire is suspect";
                return null;
            }
            if (ReferenceEquals(tlc, null) || ReferenceEquals(tlc.Map, null))   // L113, same reason as BuildBattleKeys
            {
                why = "no tactical map on this peer to resolve actor key " + key + " against";
                return null;
            }
            TacticalActorBase found = null;
            int hits = 0;
            foreach (var a in tlc.Map.GetActors<TacticalActorBase>())
                if ((int)a.GeoUnitId == key) { hits++; found = a; }
            if (hits == 0)
            {
                if (_refused.TryGetValue(key, out why)) return null;   // A5: the documented refusal wins
                why = "no actor with GeoUnitId " + key + " exists on this peer — the peers are looking at " +
                      "different rosters";
                return null;
            }
            if (hits > 1)
            {
                why = hits + " actors share GeoUnitId " + key + " on this peer, so the key names none of them";
                return null;
            }
            return found;
        }

        /// <summary>THE RECEIVER KEY (law L66c): an <c>IDamageReceiver</c> is named by its actor plus
        /// <c>IDamageReceiver.GetSlotName()</c> — the interface's OWN identity string, not something invented
        /// here. <c>TacticalActorBase.GetSlotName</c>:764 returns "" (the whole actor),
        /// <c>ItemSlot.GetSlotName</c>:215 returns <c>ItemSlotDef.SlotName</c> and
        /// <c>DamageReceiverImplementation.GetSlotName</c>:73 forwards to its slot. It is stable because it is
        /// a DEF field (identical on every peer by mod parity, law 10), it is UNIQUE per actor because the
        /// game itself asserts that — <c>TacticalActor.ValidateActor</c>:1178-1186 logs an error for duplicate
        /// health-slot names — and it ROUND-TRIPS through the game's own resolver,
        /// <c>CharacterBodyState.GetSlot(string)</c>:152-155.</summary>
        internal static string SlotOf(IDamageReceiver receiver) => receiver == null ? null : (receiver.GetSlotName() ?? "");

        /// <summary>The other half of the round trip. "" is the actor itself — which is unambiguous because
        /// <c>ItemSlotDef.SlotName</c> is never empty for a real body part; anything else must resolve to a
        /// real slot on that actor or it is a loud failure, never a fallback to the actor (which would apply
        /// arm damage to the torso and hide the drift). The <c>ItemSlot</c> IS the receiver
        /// (<c>ItemSlot</c>:18 implements <c>IDamageReceiver</c>), so the mirror re-enters the host's exact
        /// path — <c>ItemSlot.ApplyDamage</c>:120-128, statistics and the zero-health disable included.</summary>
        internal static IDamageReceiver ResolveReceiver(TacticalActorBase actor, string slotName, out string why)
        {
            why = null;
            // ReferenceEquals, not ==: TacticalActorBase is a MonoBehaviour, and Unity's overloaded == answers
            // "is this object destroyed" rather than "is this reference null". The question here is the latter
            // — a destroyed actor never gets this far, because TacticalActorKey.Resolve only ever hands back
            // actors it just found on the live map.
            if (ReferenceEquals(actor, null)) { why = "no actor"; return null; }
            if (string.IsNullOrEmpty(slotName)) return actor;
            var tacActor = actor as TacticalActor;
            // L113: `as` already answers the only question here (is it this subtype), and Unity's == would
            // additionally reject a DESTROYED actor — the corpse a trailing damage record names.
            if (ReferenceEquals(tacActor, null) || ReferenceEquals(tacActor.BodyState, null))
            {
                why = SafeName(actor) + " has no BodyState, so it cannot own a body part named '" + slotName + "'";
                return null;
            }
            var slot = tacActor.BodyState.GetSlot(slotName);
            if (slot == null)
            {
                why = SafeName(actor) + " has no body-part slot named '" + slotName + "' on this peer — mod parity " +
                      "should have made that impossible (law 10)";
                return null;
            }
            return slot;
        }

        /// <summary><c>UnityEngine.Object.name</c> is a native ECall: it is perfectly safe in the game and
        /// THROWS in the headless harness (RailCheck), which executes these refusal paths for real. A refusal
        /// message that cannot be built is a refusal that becomes a crash, so the name degrades instead.</summary>
        private static string SafeName(TacticalActorBase actor)
        {
            try { return actor.name; }
            catch { return actor.GetType().Name; }
        }
    }

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
            Debug.LogWarning("[Multiplayer][tac] an activation carried TacticalAbilityTarget." + field +
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
        /// not forced, and must not be: forcing it would unmount a weapon nobody touched.</summary>
        private static bool ItemAddress(Item item, out int actorKey, out byte kind, out string defGuid,
                                        out int charge, out int ordinal)
        {
            actorKey = 0; kind = 0; defGuid = null; charge = -1; ordinal = 0;
            if (item == null) return false;
            defGuid = item.ItemDef == null ? null : item.ItemDef.Guid;
            if (string.IsNullOrEmpty(defGuid)) return false;
            if (!TacticalInventorySync.AddressOf(item.InventoryComponent, out actorKey, out kind)) return false;
            charge = TacticalInventorySync.ChargeOf(item);
            ordinal = TacticalInventorySync.OrdinalOf(item);
            return true;
        }

        /// <summary>The other half. Null + a sentence on any failure — never a "closest match", because a
        /// reload aimed at the wrong weapon is exactly the divergence this rider exists to stop.</summary>
        private static Item ResolveItem(BinaryReader r, string field, TacticalLevelController tlc, List<string> unresolved)
        {
            int key = r.ReadInt32();
            byte kind = r.ReadByte();
            string guid = r.ReadString();
            int charge = r.ReadInt32();
            int ordinal = r.ReadInt32();
            string why;
            var owner = TacticalActorKey.Resolve(tlc, key, out why);
            if (owner == null)
            {
                if (unresolved != null) unresolved.Add(field + " owner (key " + key + "): " + why);
                return null;
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
                               ") in " + owner.name + "'s container " + kind + " on this peer — mod parity should " +
                               "have made the def impossible to miss (law 10), so this is a container that holds " +
                               "fewer of them here than on the sender");
            return null;
        }

        /// <summary>A7 — an item field that RIDES but has no shared address (an item in no container at all,
        /// or one whose owner this peer cannot key) still has to be audible: the order crosses without it and
        /// the far side silently picks its own weapon. Same shape as <see cref="NoteDroppedField"/>.</summary>
        private static void NoteUnkeyableItem(string field, Item item)
        {
            if (item == null || !_saidDropped.Add("unkeyed:" + field)) return;
            Debug.LogWarning("[Multiplayer][tac] an activation carried TacticalAbilityTarget." + field +
                             " that has NO shared address (it is in no keyed container), so it cannot ride. The " +
                             "order still crosses, but every other peer resolves that field for itself — " +
                             "first occurrence only.");
        }

        internal static void Write(BinaryWriter w, TacticalAbilityTarget t)
        {
            int eqKey = 0, itKey = 0;
            byte eqKind = 0, itKind = 0;
            string eqGuid = null, itGuid = null;
            int eqCharge = -1, itCharge = -1, eqOrd = 0, itOrd = 0;
            bool eqRides = false, itRides = false;
            if (t != null)
            {
                eqRides = ItemAddress(t.Equipment, out eqKey, out eqKind, out eqGuid, out eqCharge, out eqOrd);
                itRides = ItemAddress(t.TacticalItem, out itKey, out itKind, out itGuid, out itCharge, out itOrd);
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
            if ((mask & BitEquipment) != 0) { w.Write(eqKey); w.Write(eqKind); w.Write(eqGuid); w.Write(eqCharge); w.Write(eqOrd); }
            if ((mask & BitTacticalItem) != 0) { w.Write(itKey); w.Write(itKind); w.Write(itGuid); w.Write(itCharge); w.Write(itOrd); }
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
                string slot = r.ReadString();
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
            return t;
        }

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

    /// <summary>
    /// TACTICAL ARC A3a — THE GENERIC PER-SOLDIER COMMAND SEAM, with MOVEMENT as its first and only rider.
    ///
    /// THE MODEL (mandate L5, rewritten 2026-07-31): one shared battle, NO ownership model. Any peer commands
    /// ANY soldier, two peers may hold the same one, and all peers act SIMULTANEOUSLY during the player turn.
    /// That is native-safe: <c>ExecutingAbilities</c> is a per-actor list with no global lock
    /// (<c>TacticalActorBase</c>:54) and <c>TacticalLevelController.CheckForFallAbilitiesToActivate</c>:1917-1936
    /// already awaits N concurrent per-actor activations.
    ///
    /// ONE FUNNEL, not 36 surfaces: <c>TacticalAbility.Activate(object parameter = null)</c>
    /// (<c>TacticalAbility</c>:1078) is the single entry every command passes through — the UI calls it
    /// directly, and <c>Execute</c>/<c>ExecuteAndWait</c> (:1159/:1169) are thin wrappers over it. It takes one
    /// all-optional payload, <c>TacticalAbilityTarget</c> (:1086 <c>LastAbilityTarget = parameter as ...</c>),
    /// which is why ONE op with a field-masked codec covers move now and shoot/grenade/heal later. TFTV does
    /// not patch it (only derived classes), so the funnel survives with the mod installed.
    ///
    /// The capture is a POSTFIX on the BASE method, not a prefix and not a per-ability patch: derived
    /// overrides call <c>base.Activate(parameter)</c> (<c>MoveAbility</c>:44, <c>CaterpillarMoveAbility</c>:41),
    /// so one patch sees every activation, and running AFTER the native body means the acting peer's own click
    /// plays instantly with no round trip. RailCheck L65-rider proves the base call really is made by every
    /// rider subclass — an override that silently stopped calling base would make this seam vanish.
    ///
    /// THE THREE ROLES, all off that one postfix:
    ///   • ACTING CLIENT — plays its own click locally for presentation and emits the ORDER as an intent on
    ///     0x83. Speculative on purpose (law 5's "no rewind engine"): its local play is view-only, and the
    ///     host's settle is the rollback.
    ///   • HOST — plays it natively (it IS the authority) and MIRRORS the order to the other peers on 0x82, so
    ///     every screen shows the same soldier walking. What is mirrored is the ORDER, never the pose: the
    ///     Arc-4 geoscape precedent (fbc9065, in-game confirmed) transfers because the solver is deterministic.
    ///   • MIRROR PEERS — run the same native <c>Activate</c> inside a <see cref="SyncApplyScope"/>, so law 8
    ///     holds (an apply never re-enters capture) and the presentation is the game's own, not a hand-rolled
    ///     animation.
    ///
    /// THE CLOSER is the part that cannot be re-derived. Move's AP cost is NOT reproducible: the def's
    /// <c>ActionPointCost</c> is 0 so <c>ApplyCosts</c>:931-944 charges nothing at activation, and the real
    /// charge lands once at end of traversal (<c>TacticalNavigationComponent</c>:800) against a distance that
    /// depends on interrupts — overwatch, <c>StopReason.EnemySeen</c> — which are timing-dependent per peer.
    /// Position rides along because actors are dynamic nav obstacles (<c>MoveAbility</c>:108 disables the
    /// mover's own) so two peers' obstacle sets differ under simultaneous commands and their paths legally
    /// can. So the host, at the ONE generic action-end funnel (<c>TacticalAbility.ClearPlayingAction</c>:1039,
    /// non-virtual, reached by every ability), ships final position + AP + WP and every peer OVERWRITES.
    /// Divergence is therefore cosmetic and self-correcting on arrival, which is what makes shipping the order
    /// instead of the path safe.
    ///
    /// A5 — THE ENEMY RIDES THIS SAME SEAM, and needed NO new surface, NO new op and no enemy-specific
    /// channel. An enemy action is not a new concept: every one of the twelve
    /// <c>PhoenixPoint.Tactical.AI.Actions</c> classes reaches <c>TacticalAbility.ExecuteAndWait</c>, which is
    /// three lines over <c>Activate</c> (<c>TacticalAbility</c>:1168-1176 → :1078) — the very funnel this seam
    /// already captures — and AI movement is the SAME <c>MoveAbility</c> a player click uses
    /// (<c>AIActionMoveAndAttack</c>:27, <c>AIActionMoveToPosition</c>:25). There is no bypass: nothing under
    /// <c>Tactical.AI.Actions</c> touches Navigate/SetTransform/ApplyDamage/SpawnActor directly. So A5 is one
    /// changed decision — the HOST now mirrors EVERY faction, not just the player's — plus the two things that
    /// decision makes necessary: the rider whitelist becomes a declared DROP list (the AI executes
    /// data-configured ability defs, which no whitelist can enumerate) and autonomous reactions are pinned
    /// LOCAL (see <see cref="IsAutonomous"/>) so that mirrored enemy movement cannot make both peers fire the
    /// same overwatch.
    ///
    /// WHY THE HOST KEEPS THE AI: the decision itself consumes the global generator BEFORE any ability
    /// activates (<c>AIFaction.SelectTarget</c>:395 <c>WeightedRandomElement</c>, <c>AIActionYuggothAbility</c>
    /// :76-80), and its INPUTS diverge by construction on a client, so a re-deriving peer picks a different
    /// TARGET, not merely a different roll. The client's AI stays held by A2's <c>ClientAiGate</c>; A5 adds the
    /// runtime detector for the day it stops holding (<see cref="RelayClientRanAi"/>).
    ///
    /// ARBITRATION is <see cref="Validate"/>, a PURE function of replicated state shaped exactly like
    /// <c>EventSync.Validate</c> — no ownership table, no claim ledger, no in-memory arbiter of any kind (v1's
    /// was in-memory and every reload wiped it). The host executes intents in ARRIVAL ORDER; the first spends,
    /// the second is re-validated against the post-first host state and fails on "already executing" or on the
    /// AP check. The loser gets <c>IntentRail.Reject</c> + nudge and a settle that snaps his speculative local
    /// play back to the host's truth.
    /// </summary>
    public static class TacticalCommandSync
    {
        // Wire ops on SurfaceIds.TacCommand (host→all) and SurfaceIds.TacCommandIntent (client→host).
        private const byte OpActivate = 1;
        private const byte OpSettle = 2;
        /// <summary>A7 — SELECTING A WEAPON IS NOT AN ABILITY, which is why it never crossed. The model
        /// funnel is <c>EquipmentComponent.SetSelectedEquipment</c>:242 (it fires the game's own
        /// <c>EquipmentChangedEvent</c>:266) and the tactical UI reaches it from three view states —
        /// <c>UIStateCharacterSelected</c>:748/751, <c>UIStateShoot</c>:854/862,
        /// <c>UIStateAbilitySelected</c>:725/736 — none of which activates anything, so A3a's prefix on
        /// <c>TacticalAbility.Activate</c> can never see the click. Ops 3 (host→all) / 4 (client→host) on the
        /// families that already exist; NO new surface.</summary>
        private const byte OpSelectEquipment = 3;
        // A8 (op 5 host→all / op 6 client→host) RELAYED THE MANUAL-AIM STANCE AND IS REVERTED — 2026-08-01,
        // one build after it shipped. Ops 5 and 6 are left UNUSED rather than reassigned: a peer running the
        // A8 build must not have its aim messages decoded as something else. See the revert commit for the RCA;
        // the short form is that IdleAbility.ForceRefresh is NOT the pose setter it looks like — the pose is
        // consumed by IdleAbility.RefreshIdle:324 → DoAimOrPeek:166-185, which runs
        // TacticalNav.ExecutePoints and PHYSICALLY NAV-MOVES the actor. Relaying an aim therefore made every
        // mirror walk (and a jetpack soldier FLY) on someone else's screen. Any future attempt at this must
        // reproduce CurrentlyAiming without going through the idle ability's nav path.
        internal const byte OpIntentActivate = 1;
        internal const byte OpIntentSelectEquipment = 4;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>
        /// A5 INVERTS THE RIDER SET, and the inversion IS the arc's generic content. A3a..A4 kept a WHITELIST
        /// of five ability classes; A5 had to carry the enemy AI, and the enemy AI does not use five classes —
        /// all twelve <c>PhoenixPoint.Tactical.AI.Actions</c> classes reach one funnel with a DATA-CONFIGURED
        /// ability def (<c>AIActionExecuteAbility</c>:32, <c>AIActionMoveAndExecuteAbility</c>:61 execute
        /// whatever <c>AIActionExecuteAbilityDef.AbilityDefs</c> names — which is how every Chiron/Siren/Scylla
        /// special, every worm/egg spawn and every TFTV alien ability is authored). A whitelist cannot enumerate
        /// a def-driven set, and growing it per alien is precisely the per-subsystem hand-sync the mandate
        /// forbids. So the list is now the DROP list, in the same shape and for the same reason as
        /// <see cref="TacAbilityTargetCodec.Dropped"/>: dropping is allowed, dropping SILENTLY is not.
        ///
        /// THE SET IS THE GAME'S OWN, not one invented here. Five of the seven are exactly the classes
        /// <c>TacticalLevelController.AbilityExecuted</c>:1183 excludes from its panic sweep — the engine's own
        /// answer to "which activations are ambient machinery rather than an action". The other two are this
        /// repo's own arcs: death belongs to A4 and falling is raised per-peer from each peer's own map update.
        /// <c>IsAssignableFrom</c> and not an exact match: <c>TacticalHurtReactionAbility</c> is abstract with
        /// four shipped subclasses (<c>RepositionAbility</c>, <c>SpawnMistAbility</c>,
        /// <c>StartPreparingAbility</c>, <c>YuggothShieldsAbility</c>) and a whitelist by exact type would have
        /// relayed every one of them.
        /// </summary>
        internal static readonly Dictionary<Type, string> LocalAbilities = new Dictionary<Type, string>
        {
            { typeof(IdleAbility),
              "presentation: the idle pose and the cover hug on arrival, which law 5 names local-only by name" },
            { typeof(EnterPlayAbility),
              "the actor's own entry animation; A4 replicates the SPAWN (0x84 op 3) and every peer then plays " +
              "this for the actor it just built" },
            { typeof(PanicAbility),
              "ambient: TacticalLevelController.ExecuteQueuedAbilitiesSequence:1225 raises it on every peer from " +
              "the same replicated willpower" },
            { typeof(AIEvaluationAbility),
              "ambient: the AI's own evaluation pass, and a client runs no AI at all (ClientAiGate)" },
            { typeof(TacticalHurtReactionAbility),
              "ambient: fired from OnActorDamaged:110-129 by damage that is ALREADY replicated (0x84), so every " +
              "peer raises it for itself off the same hit" },
            { typeof(DieAbility),
              "A4 owns death: every peer dies through the game's own Health.Set(0) -> OnHealthChange:616-622 -> " +
              "Die trigger, and its parameter is a DeathReport rather than a TacticalAbilityTarget" },
            { typeof(FallNoSupportAbility),
              "ambient: TacticalLevelController.CheckForFallAbilitiesToActivate:1917-1930 activates it for EVERY " +
              "actor of EVERY faction from each peer's own OnMapUpdate, so relaying it would fall twice" },
            { typeof(InventoryAbility),
              "A6: InventoryAbility.Activate:11-15 ends in ToInventoryViewState() — relaying it YANKS every " +
              "other peer's screen into an inventory nobody there opened. Opening the screen is presentation; " +
              "what the session COMMITS rides 0x84 op 5 (TacticalInventorySync)" },
        };

        /// <summary>
        /// A7 — THE NON-ABILITY TACTICAL FUNNELS, declared for the same reason
        /// <see cref="LocalAbilities"/> and <see cref="TacAbilityTargetCodec.Dropped"/> are: the whole arc keys
        /// on ONE seam (<c>TacticalAbility.Activate</c>), and a model mutation a player can click that is NOT
        /// an ability therefore crosses nothing at all — silently, which is this repo's dominant bug class and
        /// was exactly the 2026-07-31 weapon-switch report.
        ///
        /// Keyed <c>Type.Name + "." + method</c>, matching what RailCheck L76b discovers: every method a
        /// <c>PhoenixPoint.Tactical.View.ViewStates</c> class calls directly on a
        /// <c>PhoenixPoint.Tactical.Entities</c>* model type whose IL writes an instance field. Each one must
        /// be either seamed (a Harmony prefix of ours) or named HERE with the reason it may stay local; the
        /// harness fails a funnel that is neither, and a row that no view state reaches any more.
        /// </summary>
        internal static readonly Dictionary<string, string> LocalFunnels =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "TacticalActorViewBase.DoCameraChaseParam",
              "presentation: where THIS peer's camera looks. Law 5 names the camera local-only by name, and " +
              "law L75's whole point is that six peers watch six different soldiers" },
            { "TacticalActorViewBase.ShowNameNotification",
              "presentation: the floating label over a soldier on this screen. Whatever it announces (a hit, a " +
              "status, a death) is already replicated as MODEL state on 0x84; relaying the label too would " +
              "double it" },
            { "TacticalActorViewBase.UpdateHealthbarPredictions",
              "presentation: the PREDICTED damage this peer is currently aiming at. Law 5 names hover/preview " +
              "aiming local-only by name — it is a function of where THIS peer's cursor is, and the real " +
              "damage arrives resolved on 0x84" },
        };

        /// <summary>The declared reason this ability never crosses the wire, or null when it rides. Null is the
        /// DEFAULT — that is the inversion.</summary>
        internal static string LocalReason(TacticalAbility ability)
        {
            if (ability == null) return "there is no ability";
            var t = ability.GetType();
            foreach (var kv in LocalAbilities)
                if (kv.Key.IsAssignableFrom(t))
                    return IsOrderedHurtReaction(ability) ? null : kv.Value;
            return null;
        }

        /// <summary>
        /// AMBIENT IS A PROPERTY OF THE ACTIVATION, NOT OF THE CLASS — the same distinction
        /// <see cref="IsAutonomous"/> already draws with <c>AttackType</c>, applied to the one drop-list row
        /// that is a FAMILY rather than a behaviour.
        ///
        /// THE REPORT (2026-08-04, 3 instances). A melee specialist's Dash sprinted on the acting client and
        /// on EVERY other window the soldier stood still; a later reload settled him BACK. Measured, not
        /// argued: <c>22:23:17.758 'Dash_AbilityDef' is DECLARED LOCAL — ambient: fired from
        /// OnActorDamaged:110-129 …</c>. Dash is <c>RepositionAbility</c>, which is a
        /// <c>TacticalHurtReactionAbility</c> — a row <see cref="LocalAbilities"/> added deliberately
        /// (its own comment names <c>RepositionAbility</c> as one of the four subclasses it means to sweep in)
        /// on the premise that the family is only ever raised by damage. THE PREMISE IS HALF TRUE: the family
        /// is ALSO an ordinary clickable ability, and a per-class drop cannot tell the two apart.
        ///
        /// THE DISCRIMINATOR IS THE GAME'S OWN, read off the seam itself.
        /// <c>TacticalHurtReactionAbility.Activate</c>:43-53 branches on
        /// <c>TacticalHurtReactionAbilityDef.TriggerOnDamage</c>: false takes
        /// <c>PlayAction(HurtReaction_Implementation, parameter)</c> — the CALLER'S target, i.e. an order —
        /// and true takes <c>PlayAction(HurtReactionCrt, GetHurtReactionTarget(), ActorReactions)</c>, which
        /// ignores the parameter entirely (<c>RepositionAbility.GetHurtReactionTarget</c>:99-107 picks a
        /// RANDOM one, so an ambient reposition was never relayable anyway). The flag also decides whether the
        /// ability is wired to damage at all: <c>SubscribeEvents</c>:146-152 hooks
        /// <c>Health.StatChangeEvent</c> only when it is true, so a <c>TriggerOnDamage:false</c> ability
        /// CANNOT be ambient — the only way it ever activates is somebody ordering it.
        ///
        /// Generic by construction, not a Dash special case: it un-drops every subclass and every modded one,
        /// and an autonomous activation is still caught downstream by <see cref="IsAutonomous"/>.
        /// </summary>
        internal static bool IsOrderedHurtReaction(TacticalAbility ability)
        {
            var hurt = ability as TacticalHurtReactionAbility;
            var def = hurt == null ? null : hurt.TacticalHurtReactionAbilityDef;
            return def != null && !def.TriggerOnDamage;
        }

        /// <summary>Everything rides except a DECLARED local. Kept under its A3a name because it is still the
        /// same question — "does this arc carry this ability" — and because <see cref="Validate"/>,
        /// <see cref="ApplyActivate"/> and RailCheck all ask it.</summary>
        internal static bool IsRider(TacticalAbility ability) => LocalReason(ability) == null;

        /// <summary>
        /// AN AUTONOMOUS ACTIVATION — one the GAME raised off board state, not one a peer ordered. It is the
        /// HOST'S, exactly like an AI action: the host mirrors it on 0x82 like any other rider and every other
        /// peer is BLOCKED from raising its own (<see cref="BlockAutonomousReaction"/>). A client still never
        /// emits one as an intent — nobody ordered it, so there is nothing to ask for.
        ///
        /// THE MARKER IS THE GAME'S OWN: <c>TacticalAbilityTarget.AttackType</c>. Overwatch
        /// (<c>TacticalLevelController.ExecuteOverwatch</c>:1375 builds its target with
        /// <c>AttackType.Overwatch</c>), return fire (:1434/:1494, <c>AttackType.ReturnFire</c> — note
        /// <c>ReturnFireAbility.Activate</c>:82-85 THROWS, so what actually activates is the retaliation
        /// ShootAbility/BashAbility), zone of control (<c>TriggerAbilityZoneOfControlStatus</c>:231,
        /// <c>AttackType.ZoneControl</c>) and synced fire (<c>MassShootTargetActorEffect</c>:68,
        /// <c>AttackType.Synced</c>) are EXACTLY the four the engine itself refuses to chain further reactions
        /// from (<c>TacticalLevelController.GetReturnFireAbilities</c>:1401). So "not Regular" is not a
        /// heuristic — it is the engine's own word for "nobody ordered this".
        ///
        /// A5 ORIGINALLY PINNED THESE LOCAL on the premise that "every peer raises its own off the same
        /// replicated board". THE PREMISE IS FALSE, MEASURED (law L83, 2026-08-01): a bandit's RETURN FIRE
        /// fired on the host and on NEITHER client. Both clients ran the same
        /// <c>FireWeaponAtTargetCrt</c>:1877-1880 for the mirrored shot and entered
        /// <c>TacticalLevelController.ReturnFire</c>:1458 — and left it three frames later
        /// (<c>wait while [RETURN_FIRE]</c> → <c>wait ended</c>, i2 frames 25228→25231, i3 25087→25092, vs the
        /// host's 25630→25876), i.e. <c>GetReturnFireAbilities</c>:1398 found NOTHING while the host found one.
        /// The damage still crossed on 0x84 and was applied verbatim (Soldier_3 238→225→210 on all three), so
        /// what the user saw was a soldier losing 28 hp with nobody shooting at him. THAT is the whole failure
        /// mode of "every peer decides for itself": the reaction's inputs are not one replicated number but the
        /// whole targeting/vision/equipment closure <c>GetReturnFireAbilities</c> walks, and it only has to
        /// disagree ONCE. So the premise is retired rather than repaired — the host decides, like it already
        /// does for the AI's target (<c>AIFaction.SelectTarget</c>:395), and for the same reason.
        ///
        /// WHAT MAKES ONE GATE ENOUGH: all four raisers hand the reaction to the ability through the two
        /// NON-VIRTUAL wrappers <c>TacticalAbility.Execute</c>:1158 / <c>ExecuteAndWait</c>:1168 — return fire
        /// at <c>TacticalLevelController.ReturnFire</c>:1498, overwatch at <c>ExecuteOverwatch</c>:1385, zone
        /// of control at <c>TriggerAbilityZoneOfControlStatus.ExecuteAbility</c>:129, synced fire at
        /// <c>MassShootTargetActorEffect.FaceAndShootAtTarget</c>:77 — so blocking THERE stops the whole
        /// activation. Blocking at <c>Activate</c> would not: it is VIRTUAL, and skipping the base body still
        /// lets <c>ShootAbility.Activate</c>:165-174 run its own <c>PlayAction(Shoot)</c>. The surrounding
        /// native coroutine still runs on the blocked peer, so the overwatch status is still unapplied
        /// (<c>ExecuteOverwatch</c>:1387) — nothing is stranded.
        /// </summary>
        internal static bool IsAutonomous(TacticalAbilityTarget target) =>
            target != null && target.AttackType != AttackType.Regular;

        // ─── THE RELAY DECISION, pure so the whole arc is falsifiable headless ───

        internal const string RelayMirror = "mirror";
        internal const string RelayIntent = "intent";
        internal const string RelayLocalAutonomous = "local-autonomous";
        internal const string RelayLocalDeclared = "local-declared";
        internal const string RelayClientRanAi = "client-ran-ai";

        /// <summary>WHO RELAYS WHAT, as a PURE function of four booleans — no statics, no game types — so
        /// A5's whole containment story is executable in RailCheck (law L68) instead of argued in a comment.
        /// Shaped exactly like <see cref="Validate"/> and for the same reason.
        ///
        /// <see cref="RelayClientRanAi"/> is not a relay decision at all, it is a DETECTOR: a client reaching
        /// this seam with a non-player faction's ordered ability means its own AI decided something, which
        /// <c>ClientAiGate</c> is supposed to make impossible. It is reported as loudly as it is because the
        /// AI's decisions consume the global <c>UnityEngine.Random</c> BEFORE any ability activates
        /// (<c>AIFaction.SelectTarget</c>:395 <c>WeightedRandomElement</c>), so a re-deriving peer picks
        /// different TARGETS, not merely different rolls.</summary>
        internal static string RelayDecision(bool isHost, bool factionIsPlayerControlled, bool abilityIsRider,
                                             bool activationIsAutonomous)
        {
            if (!abilityIsRider) return RelayLocalDeclared;
            if (isHost) return RelayMirror;                       // A5: EVERY faction; L83: reactions too
            if (activationIsAutonomous) return RelayLocalAutonomous;   // nobody ordered it: nothing to ask for
            return factionIsPlayerControlled ? RelayIntent : RelayClientRanAi;
        }

        /// <summary>THE OTHER HALF OF L83, and the only place a peer is stopped from acting: a NON-HOST peer
        /// does not raise its own autonomous reaction, because the host's is already on its way. Returns true
        /// to SKIP the native activation. See <see cref="IsAutonomous"/> for why this sits on the two
        /// <c>Execute</c> wrappers and not on <c>Activate</c>, and for the measurement that retired "every peer
        /// raises its own". The <see cref="SyncApplyScope"/> arm is what lets the host's relayed copy through —
        /// the mirror plays it inside that scope (<see cref="ApplyActivate"/>).</summary>
        internal static bool BlockAutonomousReaction(object parameter)
        {
            var engine = LiveEngine();
            if (engine == null || engine.IsHost) return false;   // solo or host: this peer IS the authority
            if (SyncApplyScope.Active) return false;             // this IS the host's reaction being played
            var target = parameter as TacticalAbilityTarget;
            if (!IsAutonomous(target)) return false;
            if (_saidUncovered.Add("block:" + target.AttackType))
                Debug.Log("[Multiplayer][tac] a local " + target.AttackType + " reaction was BLOCKED here — " +
                          "reactions are the host's and arrive on 0x82 like every other action (law L83). " +
                          "Raising this peer's own would be a second shot from the same actor.");
            return true;
        }

        /// <summary>HOST: the peer whose intent is currently being replayed natively, so the mirror can skip
        /// the peer that already played it locally. 0 = the host's own gesture (mirror to everyone). Scoped by
        /// a try/finally around ONE synchronous native call, which is why a plain field is enough — the move
        /// coroutine it starts runs later, on the game loop, with this already cleared.</summary>
        private static ulong _replayOriginPeer;

        /// <summary>When the 0x82 record now being decoded reached this peer, for the mirror telemetry's
        /// arrival delta. A plain field: the decode and the play are one synchronous call.</summary>
        private static float _recordArrived;

        /// <summary>CLIENT: settles waiting for their actor to go idle, keyed by actor key. A settle applied
        /// while the peer is still playing the mirrored move would be overwritten by that move's own
        /// navigation and vanish without a trace — the silent-swallow class — so it is HELD instead.</summary>
        private static readonly Dictionary<int, PendingSettle> _pending = new Dictionary<int, PendingSettle>();

        private struct PendingSettle
        {
            public Vector3 Pos;
            public float Ap;
            public float Wp;
            public int WaitedFrames;

            /// <summary>L105: TacticalDamageSync.StatEpoch when this settle ARRIVED. A settle held behind a
            /// mirrored ability routinely outlives the death it was captured before.</summary>
            public int Epoch;

            /// <summary>The host sent this one because it REFUSED an order for that actor. The refusing peer
            /// is precisely the peer whose actor is mid-speculation, so the ordinary "wait until it goes
            /// idle" hold would sit on the correction for as long as the speculative ability runs — and if
            /// that ability never ends (a refused throw leaves one executing), forever, with only a periodic
            /// warning. A forced settle applies immediately and says so.</summary>
            public bool Forced;
        }

        /// <summary>HOST: which peer's order started the ability an actor is currently executing — 0 = the
        /// host's own click. Keyed by actor key, written in <see cref="OnAbilityActivated"/>'s host branch.</summary>
        private static readonly Dictionary<int, ulong> _cmdOwner = new Dictionary<int, ulong>();

        /// <summary>HOST: orders HELD because the SAME peer's previous order for that soldier is still playing
        /// here. See the hold in <see cref="HandleActivate"/> for why that is lag and not a conflict. Ordered:
        /// oldest first, released only onto a free soldier, so the arrival order survives (law 7).</summary>
        private static readonly List<DeferredCommand> _deferred = new List<DeferredCommand>();

        /// <summary>A held order, kept as the RAW body bytes it arrived as: the release re-enters the same
        /// decoder, so there is exactly one place that knows the payload layout and the target's live refs are
        /// re-resolved fresh at the moment it actually runs.</summary>
        private struct DeferredCommand
        {
            public ulong Peer;
            public uint Nonce;
            public byte Op;
            public int Key;
            public byte[] Body;
            public float Since;
        }

        /// <summary>How long a held order waits for that soldier to finish the same peer's previous one. A
        /// mirrored cross-map move is a few seconds; past this the ability is not slow, it is stuck — and a
        /// correction that waits forever is a swallowed correction (A7's settle ceiling, same reasoning).</summary>
        private const float DeferCeilingSeconds = 10f;

        /// <summary>Log-once sets for the two "A3a knowingly does not cover this" notices. Per BATTLE, so the
        /// next mission reports its own gaps instead of inheriting a silence.</summary>
        private static readonly HashSet<string> _saidUncovered = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _saidKeyless = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>One-shot tokens: abilities whose NEXT <c>WaitForCameraChase</c> is a mirrored play and must
        /// not block on this peer's camera. Consumed by the first read, so nothing has to clean it up.</summary>
        private static readonly HashSet<TacticalAbility> _mirrorSkipsCameraWait = new HashSet<TacticalAbility>();

        internal static bool ConsumeCameraWaitSkip(TacticalAbility ability)
        {
            return ability != null && _mirrorSkipsCameraWait.Remove(ability);
        }

        // ~2 s at 60 fps. Not a deadline: a held settle is CORRECT while the actor is still moving, so the
        // hold keeps waiting and only says so periodically — but a hold nobody can see is the bug class.
        private const int SettleWarnFrames = 120;

        // ~10 s at 60 fps — the point past which "the actor is still moving" stops being a credible reason.
        // ponytail: a frame count, not a measured deadline; raise it if a legitimate cross-map move ever
        // trips it (the line it prints says which actor and how long, so the evidence arrives with the bug).
        private const int SettleHoldCeilingFrames = 600;

        /// <summary>Per-BATTLE state, dropped at tactical teardown and at session teardown (alongside
        /// <c>TacticalTurnSync.Reset</c>). A leaked pending settle would snap an actor in the NEXT battle to a
        /// position from the previous one.</summary>
        internal static void Reset()
        {
            Seq.Reset();
            _pending.Clear();
            _cmdOwner.Clear();
            _deferred.Clear();   // a held order belongs to ONE battle; releasing it into the next is a ghost order
            _saidUncovered.Clear();
            _mirrorSkipsCameraWait.Clear();   // live ability refs: never let them outlive the battle
            _saidKeyless.Clear();
            _replayOriginPeer = 0;
            _burstFrame = 0;
            _burstCount = 0;
            TacticalActorKey.Reset();   // A3b: the derived alien keys belong to ONE battle and to no other
            TacAbilityTargetCodec.ResetDropNotices();   // A5: each battle reports its own dropped fields
            FumbleGate.Reset();
        }

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpIntentActivate] = HandleActivate,
                // A3b's only client→host message: "I lost a resolved-attack record, resend the battlefield".
                // It rides THIS family rather than a surface of its own — 0x84 is the one new surface the arc
                // is allowed, and a recovery request is an intent like any other.
                [TacticalDamageSync.OpIntentResnap] = TacticalDamageSync.HandleResnapRequest,
                // A6: a client's committed inventory batch. Same family for the same reason — the batch is a
                // client asking the host to make something true, which is what this family is.
                [TacticalInventorySync.OpIntentInventory] = TacticalInventorySync.HandleInventoryIntent,
                // A7: "I switched this soldier's weapon". Same family for the same reason as the batch above.
                [OpIntentSelectEquipment] = HandleSelectEquipment,
            };
            IntentRail.Register(SurfaceIds.TacCommandIntent, "tac-cmd", ops);
        }

        private static TacticalLevelController Tlc()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<TacticalLevelController>();
        }

        private static NetworkEngine LiveEngine()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return null;
            var coord = engine.SaveTransfer;
            return coord != null && coord.SessionStarted ? engine : null;
        }

        // ─── THE ONE CAPTURE: every command, on every peer ─────────────────

        /// <summary>The <c>TacticalAbility.Activate</c> prefix, all three roles. Runs BEFORE the native body,
        /// so the order is on the wire at the same instant the local actor starts moving — and so this is a
        /// capture, not a result-ship (law 19). It never blocks: A3a's acting peer plays its own click, and
        /// the settle is what makes that speculation safe.</summary>
        internal static void OnAbilityActivated(TacticalAbility ability, object parameter)
        {
            var engine = LiveEngine();
            if (engine == null) return;                  // solo, or connected but not in a co-op game
            if (SyncApplyScope.Active) return;           // law 8: this activation IS a mirror being applied
            var actor = ability == null ? null : ability.TacticalActorBase;
            if (actor == null) return;
            var faction = actor.TacticalFaction;
            if (faction == null) return;

            var target = parameter as TacticalAbilityTarget;
            string name = ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name;
            string relay = RelayDecision(engine.IsHost, faction.IsControlledByPlayer, IsRider(ability),
                                         IsAutonomous(target));

            if (relay == RelayLocalAutonomous)
            {
                // A CLIENT reaching here with an autonomous target means BlockAutonomousReaction did not see
                // it — a raiser that bypasses both Execute wrappers. Loud: the host is mirroring its own copy,
                // so this peer is about to show the same actor shooting twice.
                if (_saidUncovered.Add("auto:" + name))
                    Debug.LogError("[Multiplayer][tac] this CLIENT raised its own " + target.AttackType +
                                   " with '" + name + "' on " + actor.name + " — reactions are the host's and " +
                                   "arrive on 0x82 (law L83), so this actor will shoot TWICE here. The raiser " +
                                   "reached Activate without passing TacticalAbility.Execute/ExecuteAndWait, " +
                                   "which is the one place the block lives.");
                return;
            }
            if (relay == RelayLocalDeclared)
            {
                if (_saidUncovered.Add(name))
                    Debug.Log("[Multiplayer][tac] '" + name + "' is DECLARED LOCAL — " + LocalReason(ability) +
                              ". It ran on this peer only, by design.");
                return;
            }
            if (relay == RelayClientRanAi)
            {
                // Law L68a's runtime detector. ClientAiGate holds the whole AI turn, so a client reaching here
                // with an AI faction's ORDERED ability means it decided something the host never did.
                if (_saidKeyless.Add("ai:" + name))
                    Debug.LogError("[Multiplayer][tac] this CLIENT activated '" + name + "' on " + actor.name +
                                   ", whose faction is AI-controlled — the client ran enemy AI of its own. Enemy " +
                                   "actions are the host's and arrive on 0x82; a locally-decided one picks a " +
                                   "DIFFERENT target (the AI draws from UnityEngine.Random before it activates " +
                                   "anything). Nothing is relayed and the two battles have parted ways.");
                return;
            }

            int key = TacticalActorKey.Of(actor);
            string guid = ability.AbilityDef == null ? null : ability.AbilityDef.Guid;
            // A3b: the payload now names OTHER actors too, and an unkeyable one would ride as key 0 and be
            // refused on the far side AFTER the order was already accepted — so it is refused HERE, where the
            // gesture still belongs to somebody, and said out loud. A NULL target is NOT a refusal any more
            // (A5): the game legitimately activates with no payload — AIActionMoveAndEscape:46 evacuates with
            // ExecuteAndWait(null) — and the order is still meaningful, so the wire carries "no target" as
            // itself rather than dropping the whole command.
            string unkeyable = target == null ? null : FirstUnkeyableTargetField(target);
            if (key == 0 || string.IsNullOrEmpty(guid) || unkeyable != null)
            {
                string who = actor.name + " / " + name;
                if (_saidKeyless.Add(who))
                    Debug.LogError("[Multiplayer][tac] command NOT relayed for " + who + " — " +
                                   (key == 0 ? "the commanded actor has no shared key (no GeoTacUnitId and no derived battle key)"
                                    : string.IsNullOrEmpty(guid) ? "the ability def has no guid"
                                    : "the payload's " + unkeyable + " has no shared key, so no peer could tell " +
                                      "WHICH actor is being targeted") +
                                   ". This peer acted alone; no other peer will follow.");
                return;
            }

            // ─── THE ANCHOR (law L104) ─────────────────────────────────────
            // AN ACTION'S VISIBLE START MUST NOT BE SET BY WHERE THIS PEER'S CAMERA HAPPENS TO BE POINTING.
            // TacticalAbility wraps EVERY action in WaitingForCameraBlendingAction:969-976, which — only when
            // TrackWithCamera — first spins WaitForCameraChase:953-966 on CameraDirector.Chasing. ApplyActivate
            // already exempts every MIRROR from that wait, so a watcher starts the shot NOW; the ACTING peer
            // did not, and it is the one peer that pays, because it is the peer holding this soldier SELECTED
            // and therefore the only one whose hint survives TacticalCameraPolicy.AllowAbilityHint. That is the
            // whole "the other windows are half a second ahead of the one I clicked on" report: the acting
            // peer's action was waiting out its own camera flight while everyone else's had already begun.
            // TacticalCameraPolicy.SnapToAbilitySubject removes the DISTANCE, but only for a PlanarScrollCamera
            // (:163) — a shot that resolves to the orbit behaviour keeps its full flight, which is why shooting
            // is exactly where the complaint survived that fix.
            //
            // Armed for EVERY relayed activation — the host's own click, the host replaying a peer's intent, a
            // client's speculative play — so the moment the actor starts aiming/shooting is the moment the
            // order exists, on every peer, for every rider and not just for shooting. The cinematic still
            // fires and still tracks: only the ACTION stops waiting for it, which is precisely what the game
            // itself does for every ability with TrackWithCamera == false. Same arm as the mirror's (:1803),
            // so the one-shot token is always consumed and never goes stale.
            if (ability.TrackWithCamera) _mirrorSkipsCameraWait.Add(ability);

            if (relay == RelayMirror)
            {
                // THE FUMBLE PRE-ROLL (law L66d). TacticalAbility.Activate:1109 rolls the fumble INSIDE the
                // native body — after this prefix — and TacticalAbility.PlayAction:988-993 (and TFTV's
                // EnqueueAction fumble fix, TFTVVanillaFixes.cs:4003-4033) consume it SYNCHRONOUSLY before
                // Activate returns. So a fumble shipped after the order would always arrive too late, and a
                // mirror left to roll its own would play a different shot. Instead the host consumes the ONE
                // native roll here and memoizes it, so :1109 gets the same value and the bit rides WITH the
                // order. Clients never roll at all (FumbleCheckGate).
                bool fumbled = FumbleGate.RollForHost(ability);
                // WHOSE ORDER IS NOW RUNNING ON THIS SOLDIER. Every accepted order — the host's own click and
                // every replay of a peer's intent — passes through here with _replayOriginPeer already naming
                // its origin (0 = the host), so the ownership the hold in HandleActivate consults is written at
                // the one point that cannot drift out of step with what is actually executing.
                _cmdOwner[key] = _replayOriginPeer;
                Send(OpActivate, "mirror " + actor.name + " " + name + " " + Where(target) +
                     (IsAutonomous(target) ? " [" + target.AttackType + "]" : "") + (fumbled ? " FUMBLED" : ""),
                     _replayOriginPeer, w => { WriteCommand(w, key, guid, target); w.Write(fumbled); });
            }
            else
                IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentActivate,
                                "command " + actor.name + " " + name + " " + Where(target),
                                w => WriteCommand(w, key, guid, target));
        }

        /// <summary>Which actor-shaped payload field (if any) this peer cannot name on the wire. Null = all of
        /// them are keyable. Never "close enough": a 0 key names nothing.</summary>
        private static string FirstUnkeyableTargetField(TacticalAbilityTarget t)
        {
            if (t.Actor != null && TacticalActorKey.Of(t.Actor) == 0) return "Actor '" + t.Actor.name + "'";
            if (t.ShootTargetActor != null && TacticalActorKey.Of(t.ShootTargetActor) == 0)
                return "ShootTargetActor '" + t.ShootTargetActor.name + "'";
            if (t.DamageReceiver != null)
            {
                var owner = t.DamageReceiver.GetActor();
                if (owner == null || TacticalActorKey.Of(owner) == 0)
                    return "DamageReceiver's owning actor";
            }
            return null;
        }

        // ─── A7: THE OTHER TACTICAL FUNNEL — SELECTING A WEAPON ────────────

        /// <summary>The <c>EquipmentComponent.SetSelectedEquipment</c> prefix. It NEVER blocks, and that is a
        /// decision, not an oversight: the method is also the game's own internal repair path
        /// (<c>EquipmentComponent</c>:56 on entering play, :102/:118 when an item is added or removed, :272
        /// when the held weapon is destroyed, <c>RagdollDieAbility</c>:162 clearing it on death), so a
        /// block-first posture would leave a client holding NOTHING the first time a weapon broke. The
        /// posture is A3a's instead — the acting peer plays its own click and the host's echo is the
        /// authority — and it is safe here because the write is idempotent (the native body no-ops when the
        /// selection is unchanged) and carries no cost.
        ///
        /// The derived selections above therefore ride too, harmlessly: they are raised from state that is
        /// already replicated, so every peer computes the same one and the mirror is a no-op on arrival.</summary>
        internal static void OnEquipmentSelected(EquipmentComponent component, Equipment equipment)
        {
            var engine = LiveEngine();
            if (engine == null) return;
            if (SyncApplyScope.Active) return;                       // law 8: this IS a mirror being applied
            if (component == null) return;
            if (ReferenceEquals(component.SelectedEquipment, equipment)) return;   // the native body no-ops
            var actor = component.Actor as TacticalActorBase;
            if (actor == null) return;
            int key = TacticalActorKey.Of(actor);
            if (key == 0)
            {
                if (_saidKeyless.Add("sel:" + SafeActorName(actor)))
                    Debug.LogError("[Multiplayer][tac] a weapon switch on " + SafeActorName(actor) + " cannot be " +
                                   "relayed — that actor has no shared key. Every other peer keeps showing the " +
                                   "weapon it was already holding, and the host will refuse any ability whose " +
                                   "source is the new one (EquipmentNotSelected).");
                return;
            }
            string guid = equipment == null || equipment.ItemDef == null ? "" : equipment.ItemDef.Guid;
            string what = "select " + actor.name + " -> " + EqName(equipment);
            if (engine.IsHost)
                Send(OpSelectEquipment, what, _replayOriginPeer, w => { w.Write(key); w.Write(guid); });
            else if (actor.TacticalFaction != null && actor.TacticalFaction.IsControlledByPlayer)
                IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentSelectEquipment, what,
                                w => { w.Write(key); w.Write(guid); });
        }

        /// <summary>The equipment named by <paramref name="guid"/> in this actor's own equipment component,
        /// or null with a sentence. "" is a real value — <c>RagdollDieAbility</c>:162 selects nothing.</summary>
        private static bool ResolveEquipment(TacticalActorBase actor, string guid, out EquipmentComponent comp,
                                             out Equipment equipment, out string why)
        {
            comp = null; equipment = null; why = null;
            var tac = actor as TacticalActor;
            comp = tac == null ? null : tac.Equipments;
            if (comp == null) { why = SafeActorName(actor) + " has no equipment component"; return false; }
            if (string.IsNullOrEmpty(guid)) return true;                    // deliberate "select nothing"
            foreach (var it in comp.Items)
            {
                var eq = it as Equipment;
                if (eq != null && eq.ItemDef != null && eq.ItemDef.Guid == guid) { equipment = eq; return true; }
            }
            why = SafeActorName(actor) + " carries no equipment with def guid " + guid + " on this peer — mod " +
                  "parity should have made that impossible (law 10)";
            return false;
        }

        private static void HandleSelectEquipment(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int key = r.ReadInt32();
            string guid = r.ReadString();
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
            var faction = actor == null ? null : actor.TacticalFaction;
            EquipmentComponent comp = null; Equipment eq = null; string resolve = null;
            if (actor != null) ResolveEquipment(actor, guid, out comp, out eq, out resolve);

            string refusal = actor == null ? why
                           : !actor.IsAlive ? "that actor is dead — a corpse holds nothing"
                           : faction == null || !faction.IsControlledByPlayer
                             ? "that actor's faction is not player-controlled — a peer switches weapons on the " +
                               "shared player team, never on the AI's units"
                           : resolve;
            if (refusal != null)
            {
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId,
                                  "weapon switch for actor " + key + ": " + refusal);
                return;
            }
            // Native, and the prefix above turns it into the host→all mirror for every OTHER peer.
            _replayOriginPeer = senderPeerId;
            try { comp.SetSelectedEquipment(eq); }
            finally { _replayOriginPeer = 0; }
            Debug.Log("[Multiplayer][tac] HOST weapon switch from peer=" + senderPeerId + " ACCEPTED — " +
                      actor.name + " -> " + EqName(eq) + " nonce=" + nonce);
        }

        private static void ApplySelectEquipment(int key, string guid)
        {
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
            if (actor == null)
            {
                Debug.LogError("[Multiplayer][tac] the host's weapon switch for actor " + key + " cannot be " +
                               "applied here — " + why + ". That soldier keeps the weapon this screen shows.");
                return;
            }
            EquipmentComponent comp; Equipment eq;
            if (!ResolveEquipment(actor, guid, out comp, out eq, out why))
            {
                Debug.LogError("[Multiplayer][tac] the host's weapon switch cannot be applied — " + why);
                return;
            }
            using (SyncApplyScope.Enter()) comp.SetSelectedEquipment(eq);
            // A weapon switch is not an ability, so the AbilityExecuted postfix that drives the tactical
            // repaint never fires for it — the model changes and every observer's weapon panel keeps the old
            // one, which reads exactly like "the switch never crossed" (law 11). MarkDirty only sets a flag;
            // the flush is TacticalUiRepaint's own Update postfix, so this is safe inside the apply scope.
            TacticalUiRepaint.MarkDirty();
            Debug.Log("[Multiplayer][tac] CLIENT weapon switch applied — " + actor.name + " -> " +
                      EqName(eq));
        }

        /// <summary><c>UnityEngine.Object.name</c> is a native ECall that throws headless; a refusal message
        /// that cannot be built is a refusal that becomes a crash.</summary>
        private static string SafeActorName(TacticalActorBase actor)
        {
            if (ReferenceEquals(actor, null)) return "<null>";
            try { return actor.name; } catch { return actor.GetType().Name; }
        }

        /// <summary>An <c>Item</c> is NOT a Unity object, so it has no <c>name</c>; its def does.</summary>
        private static string EqName(Item item) =>
            item == null ? "<none>" : (item.ItemDef == null ? item.GetType().Name : item.ItemDef.name);

        /// <summary>The <c>TacticalAbility.ClearPlayingAction</c> postfix — the host's CLOSER. That method is
        /// the one non-virtual funnel every playing action ends through (:1039, it is what calls the virtual
        /// <c>OnPlayingActionEnd</c>), so it fires for a completed move AND for an interrupted one, which is
        /// exactly the case whose AP cost no peer can reproduce.</summary>
        internal static void OnAbilityActionEnded(TacticalAbility ability)
        {
            var engine = LiveEngine();
            if (engine == null || !engine.IsHost) return;
            if (!IsRider(ability)) return;
            HostSettle(ability.TacticalActorBase);
        }

        /// <summary>Ship one actor's authoritative position + AP + WP to every peer. Broadcast to ALL,
        /// including whoever gestured: the acting peer is the one whose speculative local play most needs
        /// correcting.</summary>
        private static void HostSettle(TacticalActorBase actor, bool forced = false)
        {
            var tacActor = actor as TacticalActor;
            if (tacActor == null) return;                 // no CharacterStats to settle (structural targets, etc.)
            int key = TacticalActorKey.Of(tacActor);
            if (key == 0) return;                         // never keyed on the wire; OnAbilityActivated already said so
            var stats = tacActor.CharacterStats;
            if (stats == null) return;
            var pos = tacActor.Pos;
            float ap = stats.ActionPoints;
            float wp = stats.WillPoints;
            Send(OpSettle, "settle " + tacActor.name + " @ " + Fmt(pos) + " ap=" + ap.ToString("0.##") +
                 " wp=" + wp.ToString("0.##") + (forced ? " FORCED" : ""), 0,
                 w => { w.Write(key); w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(ap); w.Write(wp);
                        w.Write(forced); });
        }

        /// <summary>A5 adds the HAS-TARGET flag, and it is not thrift: the codec writes mask 0 for a null
        /// target and reads it back as an EMPTY one, so without the flag "the game passed null" and "the game
        /// passed a blank payload" arrive identical. <c>TacticalAbility.Activate</c>:1086 assigns
        /// <c>LastAbilityTarget = parameter as TacticalAbilityTarget</c> from it, which later readers branch on
        /// (<c>TacAchievementTracker</c>:181 dereferences it), so the mirror must pass exactly what the host
        /// passed. Abilities really do activate with null: <c>AIActionMoveAndEscape</c>:46 evacuates with
        /// <c>ExecuteAndWait(null)</c>.</summary>
        private static void WriteCommand(BinaryWriter w, int actorKey, string abilityGuid, TacticalAbilityTarget target)
        {
            w.Write(actorKey);
            w.Write(abilityGuid);
            w.Write(target != null);
            if (target != null) TacAbilityTargetCodec.Write(w, target);
        }

        private static TacticalAbilityTarget ReadCommandTarget(BinaryReader r, List<string> unresolved) =>
            r.ReadBoolean() ? TacAbilityTargetCodec.Read(r, Tlc(), unresolved) : null;

        private static string Fmt(Vector3 v) => v.IsNaN() ? "<none>" : v.ToString("0.#");

        private static string Where(TacticalAbilityTarget t) =>
            t == null ? "<no target>" : Fmt(t.PositionToApply);

        private static void Send(byte op, string what, ulong excludePeer, Action<BinaryWriter> writeBody)
        {
            var engine = NetworkEngine.Instance;
            try
            {
                uint seq = Seq.Next(SurfaceIds.TacCommand);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    w.Write(op);
                    writeBody(w);
                    inner = ms.ToArray();
                }
                var msg = new NetworkMessage(PacketType.SyncEnvelope,
                                             SyncProtocol.EncodeEnvelope(SurfaceIds.TacCommand, SyncKind.StateDelta, inner));
                if (excludePeer == 0) engine.BroadcastToAll(msg);
                else engine.BroadcastExcept(excludePeer, msg);
                Debug.Log("[Multiplayer][tac] HOST " + what + " seq=" + seq +
                          (excludePeer == 0 ? "" : " (skipping the peer that played it)"));
            }
            catch (Exception ex)
            {
                // A dropped mirror leaves the other peers watching a soldier that never moves; a dropped
                // settle leaves them permanently diverged. Never silent.
                Debug.LogError("[Multiplayer][tac] HOST " + what + " FAILED to reach the wire — the peers are now " +
                               "showing different battles: " + ex);
            }
        }

        // ─── HOST: the intent ──────────────────────────────────────────────

        /// <summary>The whole acceptance decision as a PURE function of facts read off the HOST's own state:
        /// null = accept, otherwise the human reason. Pure — no static reads, no game types, no ownership
        /// table — so the race it arbitrates is testable headless (RailCheck L65-arbiter). v1's arbiter was an
        /// in-memory claim table that every reload silently emptied; there is deliberately nothing here to
        /// reload.
        ///
        /// TWO PEERS, ONE SOLDIER: the host runs intents in arrival order, so the second one is validated
        /// against state the first already changed. <paramref name="actorBusy"/> catches the same-turn race
        /// (the soldier is still walking), and the AP pair catches the across-turn one (the first move drained
        /// it). Note that <paramref name="actionPointCost"/> is legitimately 0 for a move — the def charges
        /// nothing at activation — which is why <paramref name="abilityDisabledReason"/> is carried too: it is
        /// the game's OWN gate and for movement it is <c>NeedsMovementLeft</c> at <c>ActionPoints &lt; 1</c>
        /// (<c>MoveAbility.GetDisabledStateInternal</c>:94-97). Both arms matter; neither subsumes the other.</summary>
        internal static string Validate(bool actorFound, bool actorAlive, bool actorIsPlayerControlled,
                                        bool factionIsPlayingTurn, bool abilityFound, bool abilityIsRider,
                                        bool actorBusy, string abilityDisabledReason,
                                        float actionPoints, float actionPointCost,
                                        float willPoints, float willPointCost)
        {
            if (!actorFound)
                return "no such actor on the host";
            if (!actorAlive)
                return "that actor is dead — a corpse takes no orders";
            if (!actorIsPlayerControlled)
                return "that actor's faction is not player-controlled — a peer commands the shared player team, " +
                       "never the AI's units";
            if (!factionIsPlayingTurn)
                return "it is not that faction's turn on the host";
            if (!abilityFound)
                return "that actor has no ability with the named def guid";
            if (!abilityIsRider)
                return "that ability is not a declared rider — only movement and the attack abilities cross the wire";
            if (actorBusy)
                return "that actor is already executing an ability — another peer commanded it first " +
                       "(first-to-act-wins)";
            if (!string.IsNullOrEmpty(abilityDisabledReason))
                return "the game's own gate refuses this ability: " + abilityDisabledReason;
            if (actionPoints < actionPointCost)
                return "not enough AP: " + actionPoints.ToString("0.##") + " left, " +
                       actionPointCost.ToString("0.##") + " needed";
            if (willPoints < willPointCost)
                return "not enough WP: " + willPoints.ToString("0.##") + " left, " +
                       willPointCost.ToString("0.##") + " needed";
            return null;
        }

        /// <summary>THE GAME'S OWN AUTO-SELECT, RUN ONE STEP EARLY — and it is a BELT, not the fix.
        ///
        /// <c>TacticalAbility.Activate</c>:1087-1090 selects the ability's own source equipment before it does
        /// anything else ("if the source is an unselected Equipment and the def is not
        /// <c>UsableOnNonSelectedEquipment</c>, select it"). But <see cref="Validate"/> asks the game's gate
        /// <c>GetDisabledState()</c> BEFORE that call, and one arm of that gate —
        /// <c>GetDisabledStateInternal</c>:435, <c>EquipmentNotSelected</c> — refuses exactly the case
        /// :1087-1090 is about to repair. So the host was rejecting orders the native path would have made
        /// legal the instant it ran them: the 2026-07-31 grenade and reload rejects, verbatim, message for
        /// message ("Предмет не выбран" IS <c>AbilityDisabledState.EquipmentNotSelected</c>, whose
        /// <c>ToString</c>:182-185 localizes the key).
        ///
        /// The ROOT cause of that divergence is the weapon switch that never crossed (A7's
        /// <see cref="OnEquipmentSelected"/> seam); this exists because an arbiter must not refuse an order on
        /// a state the very next line rewrites, whatever else drifts. Running the game's own write here also
        /// costs nothing: the capture prefix turns it into the ordinary host→all mirror, so every peer's
        /// soldier draws the same weapon.</summary>
        private static void PreSelectSourceEquipment(TacticalAbility ability)
        {
            if (ability == null || ability.UsableOnNonSelectedEquipment) return;
            var eq = ability.OverrideEquipment ?? ability.EquipmentSource;
            if (eq == null || eq.IsSelected) return;
            var actor = ability.TacticalActor;
            var comp = actor == null ? null : actor.Equipments;
            if (comp == null || !comp.Items.Contains(eq)) return;
            // Only for the shared player team. A command naming an AI actor is about to be refused by
            // Validate anyway, and a refused intent must leave no trace on host state (law 3).
            if (actor.TacticalFaction == null || !actor.TacticalFaction.IsControlledByPlayer) return;
            comp.SetSelectedEquipment(eq);
            Debug.Log("[Multiplayer][tac] HOST pre-selected " + EqName(eq) + " on " + SafeActorName(actor) +
                      " before validating its order — the game's own Activate:1087-1090 was about to do the " +
                      "same, and GetDisabledState would otherwise have refused with EquipmentNotSelected. " +
                      "Seeing this often means a peer's weapon switch is not reaching the host.");
        }

        /// <summary>THE DROPPED FIELD THAT WAS NOT SAFE TO DROP (law L66/A3b, 2026-08-01).
        ///
        /// <c>GameObject</c> is a live scene object, so by law 2/L3 it cannot ride, and
        /// <c>TacAbilityTargetCodec.DroppedButRead</c> declared the consequence as "the effect resolves against
        /// PositionToApply instead". That reading was true for <c>ApplyEffectAbility</c> and FALSE for
        /// <c>BashAbility</c>, which dereferences it with no null test at all:
        /// <c>ApplyPayloadEffects</c>:566 <c>hit.Collider = target.GameObject.GetComponent&lt;Collider&gt;()</c>,
        /// reached from <c>BashCrt</c>:478. The NRE breaks the coroutine chain
        /// (<c>PlayingAction.CompleteAction</c> never resumes), so <c>ClearPlayingAction</c> never runs and the
        /// actor stays in <c>ExecutingAbilities</c> for the rest of the battle — after which
        /// <see cref="Validate"/>'s actorBusy arm refuses EVERY later order for that soldier. One mirrored bash
        /// therefore bricked a soldier permanently on the host and on the non-acting client, but never on the
        /// peer that clicked (its own target is native and complete). That is the 2026-08-01 "the client deals
        /// no damage at all and the round is broken from then on" report, exactly.
        ///
        /// A live scene object is not shipped, it is RE-DERIVED, and the game already owns the call:
        /// <c>IAttackAbility.GetAttackActorTarget</c> builds THIS peer's own target for the same actor off its
        /// own physics cast (<c>BashAbility</c>:670 → <c>TryGetSpecificActorTargetData</c>:610). Only
        /// <c>GameObject</c> is copied over — the host's <c>DamageReceiver</c> keeps riding, so every peer still
        /// aims at the same body part. Of the five <c>IAttackAbility</c> implementations <c>BashAbility</c> is
        /// the ONLY one that reads the field, so this is a no-op for the shoot path that already works.
        /// The aim-point fallback exists because a mirror is presentation and the host's damage is authoritative
        /// on 0x84 either way: a slightly wrong collider costs nothing next to a soldier bricked for the battle.</summary>
        private static void FillLiveTargetObject(TacticalAbility ability, TacticalAbilityTarget target)
        {
            var attack = ability as IAttackAbility;
            if (attack == null || target == null || target.Actor == null || target.GameObject != null) return;
            var own = attack.GetAttackActorTarget(target.Actor, target.AttackType);
            if (own != null && own.GameObject != null) { target.GameObject = own.GameObject; return; }
            var aim = target.DamageReceiver == null ? null : target.DamageReceiver.GetAimPoint();
            target.GameObject = aim == null ? target.Actor.gameObject : aim.gameObject;
            Debug.Log("[Multiplayer][tac] " + SafeActorName(target.Actor) + " could not be re-targeted locally for " +
                      "a mirrored " + (ability.AbilityDef == null ? "attack" : ability.AbilityDef.name) +
                      " — falling back to its own scene object so the replay cannot throw and strand the actor.");
        }

        private static void HandleActivate(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            long bodyStart = r.BaseStream.Position;
            int key = r.ReadInt32();
            string guid = r.ReadString();
            var tlc = Tlc();
            // A throw here funnels into IntentRail's reject path. A key that does not resolve is NOT a throw —
            // it is a named refusal, so the losing peer is told which actor the host could not find.
            var unresolved = new List<string>();
            var target = ReadCommandTarget(r, unresolved);
            if (unresolved.Count > 0)
            {
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId,
                                  "command for actor " + key + ": the host cannot name the target — " +
                                  string.Join("; ", unresolved.ToArray()));
                return;
            }

            // PIPELINE LAG IS NOT A CONFLICT (2026-08-01 RCA, law 5). The acting peer plays its own click
            // SPECULATIVELY, so it FINISHES an order while the host is still animating that very same order;
            // its next order for that soldier then lands on a host that is legitimately busy — and Validate's
            // actorBusy arm called that "another peer commanded it first" and refused it. Measured, not argued:
            // the host started peer 2's mirrored Move at frame 38580, refused peer 2's follow-up BashStrike at
            // 38672, and the move ended at 38673 — ONE FRAME late. That is the whole "melee deals no damage"
            // report: melee is the one attack that must walk up first, so its order is always the one that
            // arrives during the move it followed; refused, it never reaches the host, no 0x84 damage record is
            // ever resolved, and the acting client (whose own damage is neutered) plays a full swing that hurts
            // nobody. It "works next round" because a turn edge leaves every soldier idle.
            //
            // First-to-act-wins is about a DIFFERENT peer; the same peer queuing behind its OWN order is simply
            // ahead of our presentation, so the order is HELD and re-dispatched when that soldier goes idle.
            // Running it now instead would be worse than refusing: BashAbility.Activate:190 -> PlayAction:998
            // passes cancelCurrent:true (ActionComponent:57), which would CANCEL the host's in-flight move
            // mid-walk and settle every peer onto a position the order never reached.
            if (BusyWithOwnOrder(key, senderPeerId))
            {
                long bodyEnd = r.BaseStream.Position;
                r.BaseStream.Position = bodyStart;
                _deferred.Add(new DeferredCommand
                {
                    Peer = senderPeerId,
                    Nonce = nonce,
                    Op = op,
                    Key = key,
                    Body = r.ReadBytes((int)(bodyEnd - bodyStart)),
                    Since = Time.realtimeSinceStartup,
                });
                return;
            }

            string why;
            var actor = TacticalActorKey.Resolve(tlc, key, out why) as TacticalActor;
            var faction = actor == null ? null : actor.TacticalFaction;
            TacticalAbility ability = actor == null ? null
                : actor.GetAbilityFiltered<TacticalAbility>(a => a.AbilityDef != null && a.AbilityDef.Guid == guid);

            PreSelectSourceEquipment(ability);

            string disabled = null;
            if (ability != null)
            {
                var state = ability.GetDisabledState();
                if (state != AbilityDisabledState.NotDisabled) disabled = state.ToString();
            }
            var stats = actor == null ? null : actor.CharacterStats;

            string refusal = Validate(actor != null, actor != null && actor.IsAlive,
                                      faction != null && faction.IsControlledByPlayer,
                                      faction != null && faction.IsPlayingTurn,
                                      ability != null, ability != null && IsRider(ability),
                                      actor != null && actor.HasExecutingAbility(), disabled,
                                      stats == null ? 0f : (float)stats.ActionPoints,
                                      ability == null ? 0f : ability.ActionPointCost,
                                      stats == null ? 0f : (float)stats.WillPoints,
                                      ability == null ? 0f : ability.WillPointCost)
                             ?? why;   // a resolve failure has its own, more specific sentence

            if (refusal != null)
            {
                // No geoscape path prefix: a tactical reject touches nothing on the value rail, and the reject
                // NUDGE is what repaints the gesturing client's own screen.
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId,
                                  "command for actor " + key + ": " + refusal);
                // Snap his speculative local play back — but only if the actor is idle HERE. If it is busy, the
                // command that won is still running and its own end-of-action settle is the corrector; a settle
                // taken mid-flight would ship a position the host itself is about to leave.
                // FORCED (2026-07-31 RCA): the rejected peer is the one whose actor is stuck mid-speculation,
                // so its ClientTick would HOLD this correction behind HasExecutingAbility() — forever, if that
                // ability never ends, which is exactly what "everything went dead" looked like.
                if (actor != null && !actor.HasExecutingAbility()) HostSettle(actor, forced: true);
                return;
            }

            // Native, and mirrored to everyone EXCEPT the peer that already played it locally. The origin is
            // scoped around this one synchronous call: the capture postfix inside reads it, and the move
            // coroutine it starts runs later with the field already cleared.
            _replayOriginPeer = senderPeerId;
            FillLiveTargetObject(ability, target);
            try { ability.Activate(target); }
            finally { _replayOriginPeer = 0; }
            Debug.Log("[Multiplayer][tac] HOST command from peer=" + senderPeerId + " ACCEPTED — " + actor.name +
                      " " + (ability.AbilityDef == null ? "?" : ability.AbilityDef.name) + " → " +
                      Where(target) + " nonce=" + nonce);
        }

        /// <summary>HOST: is this soldier busy with an ability THIS peer's own previous order started? Every
        /// other answer — idle, or busy with an order that came from somewhere else — falls straight through to
        /// the ordinary arbitration, so first-to-act-wins is untouched. <c>HasExecutingAbility</c> already
        /// ignores <c>IdleAbility</c> (<c>TacticalActorBase</c>:695-704), so the cover hug never holds an
        /// order.</summary>
        private static bool BusyWithOwnOrder(int key, ulong peer)
        {
            var actor = TacticalActorKey.Resolve(Tlc(), key, out _) as TacticalActor;
            ulong owner;
            return actor != null && actor.HasExecutingAbility() &&
                   _cmdOwner.TryGetValue(key, out owner) && owner == peer;
        }

        /// <summary>HOST: release the orders held behind their own peer's previous one, oldest first and only
        /// onto a free soldier — so two held orders for one soldier still run in the order they arrived.
        /// The release re-enters <see cref="HandleActivate"/> with the bytes as they came, which is what keeps
        /// the arbitration, the reject and the mirror in exactly one place.</summary>
        /// <summary>HOST: does this peer still owe somebody an order it has ACCEPTED but not started? The
        /// end-turn gate reads it — see <c>EndTurnWaitsForHeldOrders</c> in <c>TacticalTurnSync</c>. Bounded by
        /// <see cref="DeferCeilingSeconds"/>: <see cref="HostTick"/> refuses a hold that old out loud, so the
        /// gate can never park a turn forever.</summary>
        internal static bool HasHeldOrders => _deferred.Count > 0;

        internal static void HostTick(NetworkEngine engine)
        {
            if (_deferred.Count == 0) return;
            if (engine == null || !engine.IsHost) { _deferred.Clear(); return; }
            for (int i = 0; i < _deferred.Count; )
            {
                var d = _deferred[i];
                if (BusyWithOwnOrder(d.Key, d.Peer))
                {
                    if (Time.realtimeSinceStartup - d.Since < DeferCeilingSeconds) { i++; continue; }
                    _deferred.RemoveAt(i);
                    // A hold nobody can see is this repo's dominant bug class. Say it, refuse it, and let the
                    // reject nudge snap that peer's speculative play back.
                    Debug.LogError("[Multiplayer][tac] a held order for actor " + d.Key + " from peer=" + d.Peer +
                                   " gave up after " + DeferCeilingSeconds + "s — that soldier never finished the " +
                                   "SAME peer's previous order, so an ability is stuck on the host, not merely slow.");
                    IntentRail.Reject(SurfaceIds.TacCommandIntent, d.Peer,
                                      "command for actor " + d.Key + ": that soldier is still executing this same " +
                                      "peer's previous order after " + DeferCeilingSeconds + "s");
                    continue;
                }
                _deferred.RemoveAt(i);
                try
                {
                    using (var ms = new MemoryStream(d.Body))
                    using (var r = new BinaryReader(ms, Encoding.UTF8))
                        HandleActivate(engine, d.Peer, d.Nonce, d.Op, r);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][tac] a held order for actor " + d.Key + " from peer=" + d.Peer +
                                   " threw when it was released: " + ex);
                }
            }
        }

        // ─── CLIENT: apply ─────────────────────────────────────────────────

        /// <summary>Consumes <see cref="SurfaceIds.TacCommand"/> only; every other surface (including this
        /// family's own 0x83 intent, which <see cref="IntentRail"/> owns) falls through untouched.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacCommand) return false;
            if (engine == null || engine.IsHost) return true;   // the host never mirrors its own commands
            _recordArrived = Time.realtimeSinceStartup;
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Seq.ShouldApply(SurfaceIds.TacCommand, seq)) return true;  // stale re-delivery (law 7)
                    if (op == OpActivate)
                    {
                        NoteCatchUpBurst();
                        int actorKey = r.ReadInt32();
                        string abilityGuid = r.ReadString();
                        var unresolved = new List<string>();
                        var target = ReadCommandTarget(r, unresolved);
                        bool fumbled = r.ReadBoolean();
                        ApplyActivate(actorKey, abilityGuid, target, fumbled, unresolved);
                    }
                    else if (op == OpSettle) QueueSettle(r.ReadInt32(),
                                                        new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                                                        r.ReadSingle(), r.ReadSingle(), r.ReadBoolean());
                    else if (op == OpSelectEquipment) ApplySelectEquipment(r.ReadInt32(), r.ReadString());
                    else
                    {
                        Debug.LogError("[Multiplayer][tac] unknown host→all command op " + op + " (seq=" + seq +
                                       ") — this peer can no longer follow the shared battle.");
                        return true;
                    }
                    Seq.Mark(SurfaceIds.TacCommand, seq);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] command inbound FAILED — this peer's battle has diverged from " +
                               "the host's: " + ex);
            }
            return true;
        }

        private static int _burstFrame;
        private static int _burstCount;

        /// <summary>CATCH-UP, MEASURED INSTEAD OF ARGUED (law L104). The transport is reliable and ordered and
        /// drains its WHOLE backlog in one pump (<c>DirectTransport.Update</c>:462-473), so a peer that stalled
        /// applies every missed order inside a single frame — it re-plays the HISTORY, it does not jump to now.
        /// What that costs is bounded by the game itself and NOT by how long the stall was: per soldier
        /// <c>MoveAbility.Activate</c> takes <c>PlayAction</c> → <c>ActionComponent.PlayAction</c>:57-60
        /// <c>CancelActions(channel)</c> (every earlier move on that actor is cancelled) and
        /// <c>ShootAbility.Activate</c>:173 takes <c>EnqueueAction(soloAfterCurrent:true)</c> →
        /// <c>PlayActionAfterCurrent</c>:80-91 (the queued tail is cancelled), so one soldier collapses to
        /// "whatever is running + the newest". AUTHORITATIVE state never replays at all: settles are a per-actor
        /// dictionary (<see cref="QueueSettle"/>, last write wins) and damage/spawn/death are applied from the
        /// host's snapshot verbatim. So the residue is one stale animation per SOLDIER, and this line is what
        /// says how deep it actually got. One line per burst, never per message.</summary>
        private static void NoteCatchUpBurst()
        {
            if (!CatchUpBurst(Time.frameCount, ref _burstFrame, ref _burstCount)) return;
            Debug.LogWarning("[Multiplayer][tac] CATCH-UP BURST — more than one host order arrived in the same " +
                             "frame, so this peer had fallen behind and is replaying what it missed. The game's " +
                             "own action channel collapses each soldier to its newest order and the settle/damage " +
                             "records are already at NOW; what you SEE is one stale animation per soldier (law L104).");
        }

        /// <summary>The burst decider, PURE so RailCheck L104 can execute it case by case rather than read a
        /// counter's IL. True EXACTLY ONCE per frame — on the second order of that frame, the moment "one
        /// order arrived" stops being the ordinary case. A <c>&gt;= 2</c> here would be one line per message,
        /// which is the log volume that buried a whole live run in 23642 lines of one family.</summary>
        internal static bool CatchUpBurst(int frame, ref int lastFrame, ref int count)
        {
            if (frame != lastFrame) { lastFrame = frame; count = 0; }
            return ++count == 2;
        }

        /// <summary>Play the host's order with the game's own code, inside an apply scope so the capture
        /// postfix does not echo it straight back as a fresh intent (law 8).</summary>
        private static void ApplyActivate(int key, string guid, TacticalAbilityTarget target, bool fumbled,
                                          List<string> unresolved)
        {
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
            if (actor == null)
            {
                Debug.LogError("[Multiplayer][tac] host command for actor " + key + " cannot be played here — " +
                               why + ". That soldier will stand still on this screen while it acts on the host's.");
                return;
            }
            if (unresolved != null && unresolved.Count > 0)
            {
                // Playing a shot whose target this peer could not name would aim it somewhere else entirely
                // (GetWorkingPosition falls through to the map origin). The DAMAGE still arrives on 0x84 and is
                // authoritative — only this peer's animation is missing.
                Debug.LogError("[Multiplayer][tac] host command for " + actor.name + " NOT played here — " +
                               string.Join("; ", unresolved.ToArray()) + ". The host's damage still applies; " +
                               "only the animation is missing on this screen.");
                return;
            }
            var ability = actor.GetAbilityFiltered<TacticalAbility>(a => a.AbilityDef != null && a.AbilityDef.Guid == guid);
            if (ability == null)
            {
                Debug.LogError("[Multiplayer][tac] " + actor.name + " has no ability with guid " + guid +
                               " on this peer — mod parity should have made that impossible (law 10). The order " +
                               "is dropped and this peer's battle has diverged.");
                return;
            }
            if (!IsRider(ability))
            {
                Debug.LogError("[Multiplayer][tac] the host mirrored '" + ability.AbilityDef.name + "', which is " +
                               "not a declared rider — the two peers disagree about what this arc carries.");
                return;
            }
            MirrorTelemetry(actor, ability);
            // The host's fumble is DECLARED before the native body runs, because Activate:1109 rolls it and
            // PlayAction:988-993 consumes it inside the same synchronous call — there is no later moment.
            FumbleGate.Declare(ability, fumbled);
            // A MIRROR NEVER WAITS FOR THIS PEER'S CAMERA (law 5: camera is local-only, never relayed — so it
            // must not decide WHEN a replicated action starts). Armed only when TrackWithCamera is true,
            // because that is exactly the arm that reaches WaitForCameraChase (TacticalAbility:971), so the
            // token is always consumed and never goes stale.
            if (ability.TrackWithCamera) _mirrorSkipsCameraWait.Add(ability);
            FillLiveTargetObject(ability, target);
            using (SyncApplyScope.Enter()) ability.Activate(target);
            // DID IT START, OR ONLY GET IN LINE? PlayingAction.SetState(Playing) calls StartPlayingAction
            // SYNCHRONOUSLY (PlayingAction:47-53 -> TacticalActorBase.AddExecutingAbility:709), so the moment
            // Activate returns a PLAYED order is in ExecutingAbilities and an ENQUEUED one is not —
            // ActionComponent.CheckForActionToPlay:120-128 starts only the FIRST action in the channel.
            // This is the "the other screens only start once my animation finished" report, measured rather
            // than argued: EnqueueAction(soloAfterCurrent: true) at ShootAbility.Activate:167 is
            // INDISTINGUISHABLE from PlayAction on an idle actor, so it bites only when the mirror lands on a
            // busy one — which is why a run of six orders on idle soldiers showed nothing. Deliberately NOT
            // deduplicated: how OFTEN this fires is the measurement, and it costs one line only when broken.
            if (!actor.ExecutingAbilities.Contains(ability))
                Debug.LogError("[Multiplayer][tac] MIRROR QUEUED, not played — " + actor.name + " " +
                               (ability.AbilityDef == null ? "?" : ability.AbilityDef.name) + " is waiting behind " +
                               (actor.ExecutingAbilities.Count == 0
                                    ? "<nothing — it never started at all>"
                                    : actor.ExecutingAbilities[0].GetType().Name) +
                               " and will begin only when that ends (law L78).");
        }

        /// <summary>ONE line, measured at the moment a mirrored order is about to play, answering the three
        /// questions the 2026-07-31 RCA had to argue instead of read (MpDiag-gated: one line per mirrored
        /// activation is diagnostic volume, and the failures it explains are all reported loudly elsewhere).
        ///
        ///  • WHY AN ENEMY ACTION IS INVISIBLE AND FAST. <c>TacticalActorViewBase.SetShownModeInternal</c>:363
        ///    disables every renderer of a non-<c>Revealed</c> actor (<c>RefreshAddonRenderers</c>:464) AND
        ///    adds <c>TimingScale 4f</c> (<c>RefreshTimeScale</c>:423-435) — so a mirrored action on an actor
        ///    this peer has not revealed plays invisibly, at 4x. Health bars share the gate
        ///    (<c>ShouldRenderUI</c>:395-403: <c>Revealed</c> AND <c>!CurrentFaction.IsControlledByAI</c>).
        ///  • WHETHER THE PLAY IS SEQUENCED OR ENQUEUED. <c>ShootAbility.Activate</c>:167 branches on
        ///    <c>TacticalLevelController.AnyAIEvaluationAbilityExecuting</c>:259, which is TRUE on the host
        ///    during its AI turn and FALSE on a client (<c>ClientAiGate</c> holds the AI there), so the host
        ///    takes <c>PlayAction</c> and the client takes <c>EnqueueAction(soloAfterCurrent:true)</c>.
        ///  • WHETHER A CAMERA BLEND IS ABOUT TO COST A WAIT. <c>PlayAction</c>:998 wraps every action in
        ///    <c>CreateWaitingForCameraBlendingAction</c>, whose wait runs only <c>if (TrackWithCamera)</c>
        ///    (:971) and then spins while <c>CameraDirector.Chasing</c> (:953-966). Both are printed, so the
        ///    next run MEASURES the "shots are sequential" complaint instead of reasoning about it.</summary>
        private static void MirrorTelemetry(TacticalActor actor, TacticalAbility ability)
        {
            try
            {
                var tlc = Tlc();
                var view = actor.TacticalActorViewBase;
                var faction = tlc == null ? null : tlc.CurrentFaction;
                var director = actor.CameraDirector;
                string name = ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name;
                // ALWAYS ON, but once per DISTINCT answer. Ungated it is one line per mirrored activation —
                // the volume MpDiag exists to suppress (a live run logged 23642 lines of one family) — and
                // gated it would be absent from exactly the next run that has to answer the question. The
                // shape of the answer is what matters, not how many times it repeats, so the key IS the answer.
                string key = "tel:" + name + "/" + (view == null ? "?" : view.ShownMode.ToString()) + "/" +
                             (faction != null && faction.IsControlledByAI) + "/" + ability.TrackWithCamera;
                if (!_saidUncovered.Add(key)) return;
                Debug.Log("[Multiplayer][tac] MIRROR play " + actor.name + " " + name +
                          " shownMode=" + (view == null ? "<no view>" : view.ShownMode.ToString()) +
                          " currentFaction=" + (faction == null || faction.TacticalFactionDef == null
                                                ? "<none>" : faction.TacticalFactionDef.name) +
                          " factionIsAi=" + (faction != null && faction.IsControlledByAI) +
                          " aiEval=" + (tlc != null && tlc.AnyAIEvaluationAbilityExecuting) +
                          " trackWithCamera=" + ability.TrackWithCamera +
                          " cameraChasing=" + (director != null && director.Chasing) +
                          " arrival+" + ((Time.realtimeSinceStartup - _recordArrived) * 1000f).ToString("0.#") + "ms");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer][tac] mirror telemetry failed: " + ex.Message); }
        }

        private static void QueueSettle(int key, Vector3 pos, float ap, float wp, bool forced)
        {
            _pending[key] = new PendingSettle { Pos = pos, Ap = ap, Wp = wp, WaitedFrames = 0, Forced = forced,
                                                Epoch = TacticalDamageSync.StatEpoch };
        }

        /// <summary>The standing settle applier (driven from <c>SyncEngine.Tick</c>, client-only inside). A
        /// STANDING condition, not a one-shot at arrival: the settle for a move typically lands while this peer
        /// is still walking the same soldier, and snapping then is erased by that walk's own navigation without
        /// a single log line.</summary>
        internal static void ClientTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || engine.IsHost || _pending.Count == 0) return;
            var tlc = Tlc();
            if (tlc == null) { _pending.Clear(); return; }   // left the battle: nothing left to correct

            List<int> done = null;
            List<int> lost = null;
            foreach (var kv in new List<KeyValuePair<int, PendingSettle>>(_pending))
            {
                string why;
                var actor = TacticalActorKey.Resolve(tlc, kv.Key, out why) as TacticalActor;
                if (actor == null)
                {
                    Debug.LogError("[Multiplayer][tac] settle for actor " + kv.Key + " DROPPED — " + why +
                                   ". That actor keeps whatever position and AP this peer computed for itself.");
                    (lost ?? (lost = new List<int>())).Add(kv.Key);
                    continue;
                }
                if (actor.HasExecutingAbility() && !kv.Value.Forced)
                {
                    var held = kv.Value;
                    ++held.WaitedFrames;
                    if (held.WaitedFrames >= SettleHoldCeilingFrames)
                    {
                        // THE HOLD HAS A CEILING (2026-07-31 RCA). "Still moving" is a correct reason to wait
                        // and a wrong reason to wait forever: an ability that never ends on this peer — a
                        // refused throw, a stranded coroutine — turned this hold into a correction that was
                        // swallowed with nothing but a repeating warning to show for it.
                        held.Forced = true;
                        Debug.LogError("[Multiplayer][tac] the settle for " + actor.name + " waited " +
                                       (held.WaitedFrames / 60) + "s for it to stop executing an ability and is " +
                                       "being applied ANYWAY. That ability is stuck on this peer; the host's " +
                                       "position and AP win.");
                    }
                    _pending[kv.Key] = held;
                    if (!held.Forced)
                    {
                        if (held.WaitedFrames % SettleWarnFrames == 0)
                            Debug.LogWarning("[Multiplayer][tac] holding the settle for " + actor.name + " — it has " +
                                             "been executing an ability for " + (held.WaitedFrames / 60) + "s. The " +
                                             "correction is still pending, not lost.");
                        continue;
                    }
                }
                if (kv.Value.Forced && actor.HasExecutingAbility())
                    Debug.LogWarning("[Multiplayer][tac] applying a FORCED settle to " + actor.name + " while it is " +
                                     "still executing an ability — the host refused that order, so this peer's " +
                                     "speculative play is being overruled mid-flight.");
                // ONE ACTOR'S FAILURE MAY NOT WEDGE THE QUEUE (2026-08-04 RCA). A throw out of ApplySettle
                // used to escape this loop AND this whole Tick: the entry was never added to `done`, so it
                // sat at the head of _pending and every LATER settle — every actor, for the rest of the
                // battle — was head-of-line blocked behind it, and the frames after this call in
                // SyncEngine.Tick (the damage resnapshot, the lifecycle ship, the law-11 repaint flush) never
                // ran either. It was not even quiet: it was 15305 NREs in Player.log, none of them in the
                // mod's own log, which is this repo's silent-swallow shape wearing a stack trace.
                // The entry is DROPPED, not retried: a settle that throws once throws every frame, and the
                // 0x84 resnapshot + CRC backstop is what re-converges that actor.
                try { ApplySettle(actor, kv.Value); }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][tac] the settle for " + actor.name + " THREW and is being " +
                                   "dropped — that actor keeps this peer's own position and AP until the next " +
                                   "resnapshot. Every other actor's settle still applies: " + ex);
                }
                (done ?? (done = new List<int>())).Add(kv.Key);
            }
            if (done != null) foreach (var k in done) _pending.Remove(k);
            if (lost != null) foreach (var k in lost) _pending.Remove(k);
        }

        /// <summary>Overwrite with the host's truth through the NATIVE writers — <c>SetTransform</c> is what
        /// raises <c>ActorMoved</c>/<c>ActorMovedInNewTile</c> (<c>TacticalActorBase</c>:665-685), which is how
        /// vision and voxel state stay consistent; a reflection poke at the transform would skip all of it.
        /// The actor's own rotation is kept: facing is presentation and law 5 names it local-only.
        ///
        /// THE LOCAL PLAY DIES BEFORE THE HOST'S POSITION IS WRITTEN (2026-08-01 RCA). Reaching here with an
        /// ability still running only ever happens for a FORCED settle — the host refused that order, or the
        /// 10 s ceiling fired — and there a bare <c>SetTransform</c> LOSES to that ability's own navigation:
        /// <c>TacticalNavigationComponent.UpdateActorTransformFromPathSample</c>:679 →
        /// <c>SetPositionIfDelta</c>:521 rewrites the transform on the very next path sample, with no log line.
        /// Measured, three instances: a JetJump the host refused ("cannot be used again this turn") was
        /// force-settled back onto the roof at 17:40:16.394 and the speculative jump carried that peer's actor
        /// on to (-17.5, 0, 4.5) anyway — its next Move activated from there four minutes later while the host
        /// and the other mirror both activated the SAME order from (-12.5, 3.7, -8.5). Position was the only
        /// thing lost: AP and WP are plain stat writes nothing re-samples, which is exactly the "the mirror
        /// applied the cost but not the movement" report.
        /// Cancelling is the game's own teardown, not a poke: <c>NavigationComponent.CancelNavigation</c>:156-160
        /// cancels the navigation ACTION and zeroes the speed — it never moves the actor — which ends the
        /// ability's <c>WaitUntilFinished</c>:172-176 and lets its own <c>OnPlayingActionEnd</c> clean up
        /// (<c>JetJumpAbility</c>:119-133 removes its extra landing nav areas there). An idling actor is never
        /// touched: <c>HasExecutingAbility</c> ignores <c>IdleAbility</c> (<c>TacticalActorBase</c>:695-704), so
        /// the cover hug on arrival keeps running.
        ///
        /// AND THE ACTOR IS HANDED BACK. Cancelling navigation only ends a NAVIGATING ability; a refused
        /// stance/status/overwatch order navigates nothing, so d061b0a left it in
        /// <c>ExecutingAbilities</c> — and <c>HasExecutingAbility</c> is what the UI reads to decide the
        /// soldier can be commanded, so an order the host refused could leave that soldier dead to input for
        /// the rest of the battle with the settle's own position and AP applied on top. The channel cancel is
        /// the general case of the same teardown: <c>ActionComponent.CancelActions(ActorActions)</c>:134-146
        /// sets every action in the actor's own channel to <c>Cancelled</c>, which runs the ability's
        /// <c>ClearPlayingAction</c>:1039-1058 — the SAME exit a completed action takes, including
        /// <c>RemoveExecutingAbility</c>:1049, <c>OnPlayingActionEnd</c> and <c>AbilityExecuted</c>. It also
        /// drops an ENQUEUED follow-up, which is right: the host refused the order, its follow-up was never
        /// authorised either. Only reachable on a FORCED settle — the ordinary path holds while
        /// <c>HasExecutingAbility</c> is true (<see cref="ClientTick"/>) — and the idle is not a victim: the
        /// same guard means a real ability is running, and an ability starting already cancels the idle
        /// underneath it (<c>TacticalAbility.PlayAction</c>:998 passes <c>cancelCurrent: true</c>).</summary>
        private static void ApplySettle(TacticalActor actor, PendingSettle s)
        {
            using (SyncApplyScope.Enter())
            {
                if (actor.HasExecutingAbility())
                {
                    if (actor.TacticalNav != null) actor.TacticalNav.CancelNavigation();
                    if (actor.ActionComponent != null) actor.ActionComponent.CancelActions(ActionChannel.ActorActions);
                }
                actor.SetTransform(s.Pos, actor.Rot);
                var stats = actor.CharacterStats;
                if (stats != null)
                {
                    // L105: captured on the host BEFORE a death this peer has since replayed natively, so
                    // writing them rewinds a kill bonus every peer granted itself. Position is never stale —
                    // no death moves an actor — and still applies.
                    if (TacticalDamageSync.StatsAreStale(s.Epoch))
                        Debug.LogWarning("[Multiplayer][tac] the settle for " + actor.name + " is OLDER than a " +
                                         "death this peer already replayed — its ap=" + s.Ap.ToString("0.##") +
                                         " wp=" + s.Wp.ToString("0.##") + " predate that kill's own will-point " +
                                         "grant, so only its position is applied. The host's next settle for " +
                                         "this actor re-asserts the numbers.");
                    else
                    {
                        stats.ActionPoints.Set(s.Ap);
                        stats.WillPoints.Set(s.Wp);
                    }
                }
                RefreshVisionTowards(actor);
            }
            Debug.Log("[Multiplayer][tac] CLIENT settled " + actor.name + " @ " + Fmt(s.Pos) +
                      " ap=" + s.Ap.ToString("0.##") + " wp=" + s.Wp.ToString("0.##"));
        }

        /// <summary>
        /// RE-RUN THIS PEER'S OWN NATIVE VISION over the settled actor. NOT a vision transfer — no known-state
        /// crosses the wire in either direction; this calls the game's own LOS test
        /// (<c>TacticalFactionVision.UpdateVisibilityOfAllTowardsActor</c>:546-560 →
        /// <c>ReUpdateVisibilityTowardsActorImpl</c>:651-662 → the physics cast) on the replicated board, at the
        /// authoritative position the settle just wrote, with the SAME base range the native movement path uses
        /// (<c>OnActorMoved</c>:298 passes <c>TacticalLevelControllerDef.DetectionRange</c>). It can therefore
        /// never reveal anything this peer's own line of sight does not already support.
        ///
        /// THE INPUT IT REPAIRS. Native vision is sampled ONLY on <c>ActorMovedEvent</c>, which
        /// <c>TacticalActorBase.SetTransform</c>:665-673 raises once per navigation sample
        /// (<c>TacticalNavigationComponent.UpdateActorTransformFromPathSample</c>:679 →
        /// <c>SetPositionIfDelta</c>:521) — and only when the position actually CHANGED
        /// (<c>TacticalLevelController.ActorMoved</c>:1157-1163 tests <c>Utl.Equals(actor.Pos, prevPos)</c>
        /// first). Two things make that sampling thinner on a peer than on the host, both of them one-sided:
        ///  • an actor this peer has not revealed carries <c>TimingScale 4f</c>
        ///    (<c>TacticalActorViewBase.RefreshTimeScale</c>:423-437, added for every non-Revealed/non-Located
        ///    actor), so its mirrored walk consumes ~4x fewer frames and its path is LOS-tested ~4x more
        ///    coarsely — the reveal window the host caught mid-walk can fall entirely between two samples here;
        ///  • a settle that lands on the position this peer already computed raises NO event at all, so the
        ///    host's final word for that actor was never put to a vision test.
        /// Once a mid-walk reveal is missed nothing re-tests it until the next faction-turn edge
        /// (<c>OnFactionStartTurn</c>:154-175 is the only full recompute), so a peer stays dark for a whole
        /// turn — and every peer misses it the same way, which is why the two clients agree with each other and
        /// not with the host. Being dark is not cosmetic: <c>SetShownModeInternal</c>:363 disables every
        /// renderer of a non-Revealed actor (<c>RefreshAddonRenderers</c>:453-472) and the health bar shares the
        /// gate (<c>ShouldRenderUI</c>:395-403), so the same miss is the invisible enemy and the missing HP bar.
        ///
        /// Idempotent by construction: <c>KnownCounters.IncrementCounterTo</c>:55-67 only ever raises a counter
        /// to a maximum, so re-running this over an already-revealed actor changes nothing. Every faction but
        /// the actor's own, in the shape <c>ShootAbility.Activate</c>:157-163 uses — the reveal is a property of
        /// the board, not of who is currently playing.
        ///
        /// BOTH DIRECTIONS, AND THE FIRST VERSION ONLY HAD ONE (2026-08-04). Native vision is a RELATION and
        /// <c>TacticalFactionVision.OnActorMoved</c>:273-306 recomputes it from BOTH ends: an actor of MY OWN
        /// faction takes :279-286 → <c>UpdateVisibilityForImpl</c> ("what this actor now SEES", a sweep out
        /// over every actor on the map), and a FOREIGN one takes :294-301 →
        /// <c>ReUpdateVisibilityTowardsActorImpl</c> ("who now sees IT"). The repair shipped only the second,
        /// because that is the only one the game exposes publicly — so a settle for one of THIS peer's own
        /// soldiers re-tested who could see him and never re-tested what HE could see. That is the whole
        /// report: a sniper walked on the acting client, spotted a bandit through its own native
        /// <c>ActorMovedEvent</c>, and every mirroring peer — whose only word on that walk is the settle —
        /// kept the bandit in fog for rounds (measured: <c>MIRROR play Scab Move_AbilityDef
        /// shownMode=Hidden</c> on both clients, 22:23).
        ///
        /// The missing half is assembled from the SAME native method rather than a new one, by inverting the
        /// loop: "my faction re-tests toward each foreign actor" covers "my settled soldier now sees them",
        /// with the game's own LOS cast, the game's own counter arithmetic and the same monotone
        /// idempotence — nothing here can reveal what this peer's line of sight does not support, which is
        /// what law L81 bans.
        /// ponytail: the inverted sweep re-tests the WHOLE faction per foreign actor, not just the one that
        /// moved, because no public per-actor entry exists (<c>UpdateVisibilityForImpl</c> is private). Cost is
        /// |ownActors| x |foreignActors| LOS casts per settle, off the frame hot path; if that ever shows up in
        /// the rail cost line, narrow it to the settled actor with the public
        /// <c>CheckVisibleLineBetweenActors</c> + <c>IncrementKnownCounter</c> pair.
        ///
        /// THE CEILING THIS USED TO DECLARE IS RAISED. <c>KnownState.Located</c> — in detection range, no
        /// line of sight, the orange "something is there" beacon — really is unreachable through
        /// <c>UpdateVisibilityOfAllTowardsActor</c>:546, whose whole body is
        /// <c>ReUpdateVisibilityTowardsActorImpl</c>:651-662 and which raises <c>Revealed</c> and nothing
        /// else. So the beacon was simply LOST between settles: a mirroring peer knew an enemy was there
        /// only once it could SEE it. <see cref="LocateByDistance"/> adds the missing half, transcribed from
        /// the game's own rule rather than invented — <c>GatherKnowableActors</c>:640-647 locates an actor
        /// when it is a live, uncloaked <c>TacticalActor</c> within
        /// <c>TacticalLevelControllerDef.DetectionRange</c>, and the raise is the same public
        /// <c>IncrementKnownCounter(actor, Located, 1, notify)</c>:444 that
        /// <c>UpdateVisibilityForImpl</c>:576-579 uses for exactly that list. Hearing
        /// (<c>ReUpdateHearingImpl</c>:664-684, per-soldier <c>HearingRange</c>) stays out: it is a second,
        /// independent rule and this repair is not the place to grow one.
        ///
        /// THE REVERSE RISK — a beacon that outlives its enemy — IS BOUNDED, AND THE BOUND IS THE GAME'S OWN,
        /// not a hope. Two facts make it so. (1) The counter is a MAX, not a tally:
        /// <c>KnownCounters.IncrementCounterTo</c>:55-67 is <c>if (num &lt; counter) _counters[type] =
        /// counter</c>, so raising Located to 1 twice leaves 1, and a mirrored raise landing on top of this
        /// peer's own native one adds nothing to decay. (2) The decay is the faction-turn edge —
        /// <c>OnFactionStartTurn</c>:154-165 runs <c>DecrementMyCountersForFaction</c> then a full
        /// <c>UpdateVisibilityAll</c> — and EVERY peer runs it. Vanilla's own beacon is raised by precisely
        /// the same monotone call on <c>OnActorMoved</c>:281 and cannot be lowered mid-turn either. So this
        /// arm does not make a peer's beacon staler than a single-player one; it makes it exist.
        /// ponytail: the LOS re-test that would make the raise strictly "located INSTEAD of revealed"
        /// (<c>GatherKnowableActors</c>'s else-if chain) is skipped — it is a second full visibility cast per
        /// pair, and Located+Revealed together is a state vanilla reaches all the time (one soldier in range
        /// without sight, another with it).
        /// </summary>
        /// <summary>THE toActor MUST BE A PERCEIVABLE ONE, AND THE GAME DOES NOT CHECK (2026-08-04 RCA — this is what
        /// killed the whole settle path). <c>UpdateVisibilityOfAllTowardsActor</c>:549-554 guards only the
        /// LOOKER (<c>if (!(actor.TacticalPerceptionBase == null))</c>); the actor being looked AT is passed
        /// straight through to <c>CheckVisibleLineBetweenActors</c>:755 →
        /// <c>GetSizeAndStealthVisibilityMultiplier</c>:842, whose first line dereferences
        /// <c>actor.TacticalPerceptionBase.TacticalPerceptionBaseDef</c> with no test at all. Natively that is
        /// safe because the only caller is <c>OnActorMoved</c>, which can only ever name an actor that walked.
        /// The inverted sweep below names EVERY member of a foreign faction, and <c>TacticalActorBase</c> is
        /// also what crates, ground piles and destructibles are (A6) — none of which carry a perception
        /// component. So it threw a plain NRE on the first such entry of the first settle, and kept throwing:
        /// measured 15305 identical NREs and ZERO applied settles across a whole battle on both clients, while
        /// the host shipped 94. Same guard as the game's own, on the side the game forgot.</summary>
        private static bool CanBeSeen(TacticalActorBase actor) =>
            !ReferenceEquals(actor, null) && actor.TacticalPerceptionBase != null;

        private static void RefreshVisionTowards(TacticalActorBase actor)
        {
            var tlc = actor == null ? null : actor.TacticalLevel;
            if (tlc == null || tlc.TacticalLevelControllerDef == null || !CanBeSeen(actor)) return;
            float range = tlc.TacticalLevelControllerDef.DetectionRange;
            var own = actor.TacticalFaction;
            foreach (var faction in tlc.Factions)
            {
                if (faction == own || faction.Vision == null) continue;
                faction.Vision.UpdateVisibilityOfAllTowardsActor(actor, range, notifyChange: true);
                LocateByDistance(faction, actor, range);
                if (own == null || own.Vision == null) continue;
                foreach (var foreign in faction.Actors)
                    if (CanBeSeen(foreign))
                    {
                        own.Vision.UpdateVisibilityOfAllTowardsActor(foreign, range, notifyChange: true);
                        LocateByDistance(own, foreign, range);
                    }
            }
        }

        /// <summary>The DISTANCE half of knowing where somebody is, transcribed from
        /// <c>TacticalFactionVision.GatherKnowableActors</c>:640-647: a live, uncloaked, non-evacuated
        /// <c>TacticalActor</c> within <c>DetectionRange</c> of ANY of the looking faction's actors is
        /// LOCATED by that faction. The raise is the game's own public counter call, at the same value
        /// <c>UpdateVisibilityForImpl</c>:576-579 uses.
        ///
        /// It returns on the FIRST locator on purpose: <c>IncrementCounterTo</c>:55-67 takes a maximum, not
        /// a sum, so a second locator would change nothing — and walking the rest of the faction per foreign
        /// actor is the cost this settle path is already careful about.</summary>
        private static void LocateByDistance(TacticalFaction looking, TacticalActorBase target, float range)
        {
            if (looking == null || looking.Vision == null || !CanBeSeen(target)) return;
            var ta = target as TacticalActor;
            if (ta == null || !ta.IsAlive || ta.IsCloaked) return;
            if (ta.Status != null && ta.Status.HasStatus<EvacuatedStatus>()) return;
            foreach (var looker in looking.Actors)
            {
                if (!CanBeSeen(looker) || looker.IsDead || ReferenceEquals(looker, ta)) continue;
                if (!Utl.LesserThanOrEqualTo((looker.Pos - ta.Pos).magnitude, range)) continue;
                looking.Vision.IncrementKnownCounter(ta, KnownState.Located, 1, notifyChange: true);
                return;
            }
        }
    }

    /// <summary>
    /// THE capture seam (law 4a), on the ONE generic funnel: <c>TacticalAbility.Activate(object)</c>
    /// (<c>TacticalAbility</c>:1078). On the BASE method, so the single patch covers every derived ability
    /// that calls <c>base.Activate</c> — which every rider does, and RailCheck L65-rider keeps proving.
    ///
    /// A PREFIX, and the ordering is the point, not a detail. A3a deliberately lets the acting peer's own
    /// click PLAY locally (law 5's speculative presentation: the closer is the authority, so there is no
    /// rewind engine to build) — but the ORDER still leaves before the local mutation, exactly where a
    /// block-first family would put its block. A postfix would emit AFTER the native write and be a
    /// result-ship (RailCheck L19), which is a real distinction and not a naming one: from here the wire sees
    /// the command at the same instant the local actor does, so the other peers start the same move on the
    /// same frame instead of one round trip behind the animation. The prefix returns void, which Harmony
    /// treats as never-skipping — the native body always runs.
    ///
    /// Parameter types are named EXACTLY: <c>AccessTools</c>/<c>HarmonyPatch</c> do no widening and skip no
    /// optional parameter, and a mistyped guess resolves to null, which <c>PatchAll</c> turns into one warning
    /// <c>MultiplayerMain</c> swallows — killing every later patch in the same pass (RailCheck L23).
    /// </summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.Activate), new[] { typeof(object) })]
    internal static class AbilityActivateCapture
    {
        private static void Prefix(TacticalAbility __instance, object parameter)
            => TacticalCommandSync.OnAbilityActivated(__instance, parameter);
    }

    /// <summary>
    /// L83 — THE REACTION GATE. A non-host peer does not raise its own overwatch / return fire /
    /// zone-of-control / synced shot: the host raises all four and mirrors them on 0x82 like any other action,
    /// so a locally-raised one would be a second shot from the same actor. See
    /// <see cref="TacticalCommandSync.IsAutonomous"/> for the measurement that made the host the authority
    /// here, and for why the block sits on these two NON-VIRTUAL wrappers rather than on the virtual
    /// <c>Activate</c> the capture uses (skipping a virtual's base body leaves the override's own
    /// <c>PlayAction</c> running).
    ///
    /// Two patch classes and not one <c>TargetMethods</c>: the wrappers return different types, so each skip
    /// has to hand Harmony a different <c>__result</c> — an empty enumerator for the coroutine form (every
    /// caller wraps it in <c>Timing.Call</c>, which completes immediately) and <c>NextUpdate.ThisFrame</c> for
    /// the immediate form, which is exactly what the native body returns when nothing began (:1170).
    /// </summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.Execute), new[] { typeof(object) })]
    internal static class AutonomousReactionExecuteGate
    {
        private static IEnumerator<NextUpdate> Nothing() { yield break; }

        private static bool Prefix(object parameter, ref IEnumerator<NextUpdate> __result)
        {
            if (!TacticalCommandSync.BlockAutonomousReaction(parameter)) return true;
            __result = Nothing();
            return false;
        }
    }

    /// <summary>The immediate half of <see cref="AutonomousReactionExecuteGate"/> —
    /// <c>MassShootTargetActorEffect.FaceAndShootAtTarget</c>:77 is the one raiser that uses it.</summary>
    [HarmonyPatch(typeof(TacticalAbility), nameof(TacticalAbility.ExecuteAndWait), new[] { typeof(object) })]
    internal static class AutonomousReactionExecuteAndWaitGate
    {
        private static bool Prefix(object parameter, ref NextUpdate __result)
        {
            if (!TacticalCommandSync.BlockAutonomousReaction(parameter)) return true;
            __result = NextUpdate.ThisFrame;
            return false;
        }
    }

    /// <summary>
    /// A7 — THE SECOND TACTICAL FUNNEL. Switching a soldier's weapon is NOT an ability, so nothing about it
    /// reaches <see cref="AbilityActivateCapture"/>: the model write is
    /// <c>EquipmentComponent.SetSelectedEquipment</c>:242-268, clicked straight out of three view states
    /// (<c>UIStateCharacterSelected</c>:748/751, <c>UIStateShoot</c>:854/862,
    /// <c>UIStateAbilitySelected</c>:725/736). Until this seam existed, a weapon switch stayed on the peer
    /// that clicked it, and — far worse than a cosmetic gap — the HOST then refused that peer's next order
    /// with the game's own <c>EquipmentNotSelected</c> gate
    /// (<c>TacticalAbility.GetDisabledStateInternal</c>:435 tests <c>isEquipmentOfSelectedGroup</c>:481-499
    /// against the HOST's selection), which is the 2026-07-31 "threw a grenade, nothing happened, everything
    /// went dead" report.
    ///
    /// A PREFIX so the capture reads the OLD selection and can tell a real change from the native no-op
    /// (:244 returns immediately when the value is unchanged). It returns void — never blocks — for the
    /// reasons argued at <see cref="TacticalCommandSync.OnEquipmentSelected"/>.
    /// </summary>
    [HarmonyPatch(typeof(EquipmentComponent), nameof(EquipmentComponent.SetSelectedEquipment))]
    internal static class EquipmentSelectCapture
    {
        private static void Prefix(EquipmentComponent __instance, Equipment equipment)
            => TacticalCommandSync.OnEquipmentSelected(__instance, equipment);
    }

    /// <summary>
    /// THE closer seam, on the ONE generic action-END funnel: <c>TacticalAbility.ClearPlayingAction</c>
    /// (:1039). Chosen over <c>OnPlayingActionEnd</c> because that one is VIRTUAL — a derived override that
    /// forgot to call base would silently remove the closer — while this is the non-virtual method that calls
    /// it, reached by every ability, for a completed action AND for a cancelled one.
    ///
    /// Host-only inside: the client's own action ends produce nothing authoritative. By this point the move's
    /// navigation has finished and its AP has been charged (<c>TacticalNavigationComponent</c>:800 runs inside
    /// <c>Navigate</c>, which <c>MoveAbility.Move</c>:119 awaits before the action can end), so the values read
    /// here are final.
    /// </summary>
    [HarmonyPatch]
    internal static class AbilityActionEndCapture
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TacticalAbility), "ClearPlayingAction",
                               new[] { typeof(Base.Entities.PlayingAction) });

        private static void Postfix(TacticalAbility __instance)
            => TacticalCommandSync.OnAbilityActionEnded(__instance);
    }

    /// <summary>
    /// A3b — THE FUMBLE IS THE HOST'S, AND IT RIDES WITH THE ORDER (law L66d).
    ///
    /// WHY IT CANNOT BE SHIPPED AFTERWARDS. <c>TacticalAbility.Activate</c>:1109 rolls
    /// <c>FumbledAction = FumbleActionCheck()</c> off the GLOBAL <c>UnityEngine.Random</c>
    /// (<c>FumbleActionCheck</c>:1124-1131, <c>Random.Range(0,100) &lt; EquipmentDef.FumblePerc</c>), and the
    /// value is CONSUMED inside the same synchronous call — <c>PlayAction</c>:988-993 diverts to
    /// <c>PlayFumbleAction</c>, and with TFTV installed <c>EnqueueAction</c> does too
    /// (<c>TFTVVanillaFixes</c>:4003-4033, the fix that makes shoot fumbles actually fire; vanilla no-ops
    /// them). By the time any later message could arrive, the shot has already been queued. So the bit has to
    /// be IN the order, which means the host has to know it BEFORE the native body runs.
    ///
    /// THE MECHANISM, and it is deliberately RNG-neutral: the host's capture prefix consumes the ONE native
    /// roll early (<see cref="FumbleGate.RollForHost"/> calls the real method through this very patch, which
    /// finds nothing pending and lets the original run) and MEMOIZES it; the native call at :1109 then finds
    /// the memo and returns it without a second draw. Exactly one roll per activation, same as vanilla.
    /// Every non-host peer NEVER rolls: it returns the host's declared bit if the order carried one, and
    /// false otherwise — a client that rolled its own would fumble on a shot the host landed.
    /// <c>JetJumpAbility</c>:136-146 is the only override of the check; it is not a rider, so it is left to
    /// roll natively on the host and to return false on a client like everything else.
    /// </summary>
    internal static class FumbleGate
    {
        private static readonly Dictionary<TacticalAbility, bool> _pending = new Dictionary<TacticalAbility, bool>();
        private static MethodInfo _check;

        internal static void Reset() => _pending.Clear();

        /// <summary>HOST: take the native roll NOW and memoize it for <c>Activate</c>:1109.</summary>
        internal static bool RollForHost(TacticalAbility ability)
        {
            if (ability == null) return false;
            if (_check == null)
            {
                _check = AccessTools.Method(typeof(TacticalAbility), "FumbleActionCheck", new Type[0]);
                if (_check == null)
                {
                    Debug.LogError("[Multiplayer][tac] TacticalAbility.FumbleActionCheck did not resolve — the " +
                                   "fumble cannot be pre-rolled, so it will not ride with the order and every " +
                                   "peer will roll its own. Shots will differ between screens.");
                    return false;
                }
            }
            bool rolled;
            try { rolled = (bool)_check.Invoke(ability, null); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] fumble pre-roll THREW — the order ships 'not fumbled' and the " +
                               "host may still fumble: " + ex);
                return false;
            }
            _pending[ability] = rolled;
            return rolled;
        }

        /// <summary>MIRROR: the host's answer for the activation that is about to run.</summary>
        internal static void Declare(TacticalAbility ability, bool fumbled)
        {
            if (ability != null) _pending[ability] = fumbled;
        }

        internal static bool TryConsume(TacticalAbility ability, out bool value)
        {
            value = false;
            if (ability == null || !_pending.TryGetValue(ability, out value)) return false;
            _pending.Remove(ability);
            return true;
        }
    }

    [HarmonyPatch]
    internal static class FumbleCheckGate
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TacticalAbility), "FumbleActionCheck", new Type[0]);

        private static bool Prefix(TacticalAbility __instance, ref bool __result)
        {
            if (FumbleGate.TryConsume(__instance, out __result)) return false;   // memoized host roll / declared mirror bit
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;   // solo or host: roll natively
            __result = false;   // a client never draws from the global RNG; the host's bit is authoritative
            return false;
        }
    }

    /// <summary>
    /// THE MIRRORED SHOT THAT WAITED ITS TURN (law L78). <c>ShootAbility.Activate</c>:167 chooses between
    /// <c>PlayAction</c> (now) and <c>EnqueueAction(soloAfterCurrent: true)</c> (after whatever is already
    /// playing, and alone), and one arm of that condition is
    /// <c>TacticalLevelController.AnyAIEvaluationAbilityExecuting</c>:259 —
    /// <c>_aiEvaluationUpdateable != null</c>. On the HOST a mirrored order runs inside its own AI turn, so
    /// the flag is TRUE and the shot plays immediately. On a CLIENT the AI turn is held (A5's
    /// <c>ClientAiGate</c>), that field is null, and the SAME order takes the queue instead — which is
    /// exactly the reported "the other screens only start playing it once my animation has finished, and
    /// then everything at once". The peers never disagreed about the order; they disagreed about this getter.
    ///
    /// So answer it the way the host would, and ONLY while a mirrored activation is genuinely on the stack:
    /// <c>SyncApplyScope</c> is entered at the single mirror call site (<c>PlayMirroredCommand</c>, the
    /// <c>using</c> around <c>ability.Activate</c>) and closes with that synchronous call. Scoped that
    /// tightly, the flag's two other readers cannot observe the lie — <c>ExecuteAIEvaluationAbilities</c>:1236
    /// and <c>AnyGlobalEffectExecuting</c>:267 are reached from coroutine drivers, never from inside one
    /// synchronous <c>Activate</c>. A postfix, not a prefix: when the client legitimately has an evaluation
    /// running the native answer is already TRUE and must survive.
    ///
    /// NARROWED TO THE AI TURN (law L104, 2026-08-05), because "match the host" is only PlayAction while the
    /// host is actually running its AI. During a PLAYER turn the host's own answer here is FALSE — its
    /// <c>_aiEvaluationUpdateable</c> is null — so a blanket lie made a WATCHER the only peer taking
    /// <c>PlayAction(cancelCurrent: true)</c> while the acting peer and the host both took
    /// <c>EnqueueAction(soloAfterCurrent: true)</c>. That is the second half of "I move behind a wall and
    /// immediately shoot, and the other windows do it noticeably faster than mine": a watcher CANCELLED the
    /// move mid-walk and fired at once, while the peer who clicked correctly finished the walk first. It was
    /// not merely faster, it was wrong — cancelling a move leaves that peer at a position the order never
    /// reached until the settle drags it back (the same hazard law 5 spells out for held melee orders).
    /// The narrowing is derived from REPLICATED state (whose turn it is), so every peer computes the same
    /// answer for the same order without a byte on the wire.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController),
                  nameof(TacticalLevelController.AnyAIEvaluationAbilityExecuting), MethodType.Getter)]
    internal static class MirroredPlayMatchesHostPacing
    {
        private static void Postfix(TacticalLevelController __instance, ref bool __result)
        {
            if (__result || !SyncApplyScope.Active) return;
            var faction = __instance == null ? null : __instance.CurrentFaction;
            if (faction != null && faction.IsControlledByAI) __result = true;
        }
    }

    /// <summary>
    /// THE MIRRORED ORDER THAT WAITED FOR THE WRONG PEER'S CAMERA. <c>MirroredPlayMatchesHostPacing</c> above
    /// only decides PlayAction vs EnqueueAction — but BOTH paths wrap the action in
    /// <c>CreateWaitingForCameraBlendingAction</c>, and <c>WaitingForCameraBlendingAction</c>:969-974 then
    /// spins in <c>WaitForCameraChase</c>:952-966 while <c>CameraDirector.Chasing</c>, for every ability with
    /// <c>TrackWithCamera</c>. So no choice between those two branches could ever have fixed it.
    ///
    /// That wait is the wrong gate for a mirror, for a reason that does not depend on any bug report:
    /// <c>Chasing</c> is <c>PlanarScrollCamera.IsDoingChase</c>:256 — ONE GLOBAL camera state per peer, not
    /// per actor — so a replicated action's start time was being decided by where THIS peer's camera happens
    /// to be pointing. Law 5 names camera local-only and never relayed; a local-only thing must not decide
    /// WHEN a shared action begins. It is also the only mechanism in this arc that can make two receivers of
    /// the SAME order start at different times, which a per-actor action queue cannot.
    ///
    /// Skipping it costs nothing but the camera blend: the coroutine is a pure wait, so the mirrored action
    /// simply starts now.
    ///
    /// AND THE ACTING PEER TAKES THE SAME EXEMPTION (law L104, 2026-08-05). Leaving its own click on the
    /// native wait did not make it "single player" — it made it the SLOW one, because the acting peer is the
    /// peer holding that soldier selected and therefore the ONLY peer whose camera hint survives
    /// <c>TacticalCameraPolicy.AllowAbilityHint</c>. Every watcher started the shot immediately and the peer
    /// who clicked watched its own camera fly in first: "on the other windows this happens noticeably faster
    /// than on mine". The token is now armed in <see cref="TacticalCommandSync.OnAbilityActivated"/> for every
    /// RELAYED activation as well, so a shared action begins at the moment the order exists on all peers
    /// alike, and this class's name is now half the story — it is the ANCHOR, not a mirror concession.
    /// </summary>
    [HarmonyPatch(typeof(TacticalAbility), "WaitForCameraChase")]
    internal static class MirroredPlayDoesNotWaitForThisPeersCamera
    {
        private static bool Prefix(TacticalAbility __instance, ref IEnumerator<NextUpdate> __result)
        {
            if (!TacticalCommandSync.ConsumeCameraWaitSkip(__instance)) return true;
            __result = NoWait();
            return false;
        }

        private static IEnumerator<NextUpdate> NoWait() { yield break; }
    }
}
