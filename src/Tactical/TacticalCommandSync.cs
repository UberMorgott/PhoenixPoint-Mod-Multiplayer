using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Base.Core;
using Base.Utils.Maths;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
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
            _built = false;
            _nextDerived = -1;
        }

        /// <summary>Build the derived-key map for THIS battle. Idempotent by design — it runs at the FIRST
        /// turn edge and never again, because after that the peers' alien positions legitimately diverge (the
        /// AI turn is host-only, A2's <c>ClientAiGate</c>, so aliens never move on a client at all). Rebuilding
        /// later would therefore produce two different maps and silently point every alien key at the wrong
        /// monster — which is why this is a hard one-shot and not a lazy "build on first need".</summary>
        internal static void BuildBattleKeys(TacticalLevelController tlc)
        {
            if (_built) return;
            if (tlc == null || tlc.Map == null) return;
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
            if (tlc == null || tlc.Map == null)
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
            if (tacActor == null || tacActor.BodyState == null)
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

        /// <summary>Every bit this build can decode. A set bit outside it means the sender declared a field
        /// this reader does not know — the rest of the stream is then misaligned, so it is a THROW, not a
        /// best-effort read. (Mod parity is blocking, law 10, so this can only be a bug, never a version skew.)</summary>
        internal const ushort KnownBits = BitPositionToApply | BitActor | BitShootTargetActor | BitDamageReceiver |
                                          BitActorGridPosition | BitShootFromPos | BitDirection | BitCone |
                                          BitAttackType | BitObstructionsCheckRadius;

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
        /// differ the moment any caller narrows it.</summary>
        internal static readonly string[] Rides =
        {
            "PositionToApply", "Actor", "ShootTargetActor", "DamageReceiver", "ActorGridPosition",
            "ShootFromPos", "Direction", "Cone", "AttackType", "ObstructionsCheckRadius",
        };

        /// <summary>The fields that deliberately do NOT ride, each with the reason. This list is the other
        /// half of the coverage law — it is what makes a drop a DECISION instead of an omission.</summary>
        internal static readonly Dictionary<string, string> Dropped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "GameObject",           "a live scene object; the peer's own equivalent is reached through the actor key, never shipped" },
            { "CoverDirection",       "presentation: which way the actor hugs cover on arrival (law 5 names it local-only)" },
            { "TacticalItem",         "A4 — the body-part item a snap-to-bodyparts shot aimed at; it needs an item key, and the aim point it feeds GetWorkingPosition:181 is already reached through the shipped DamageReceiver's own slot" },
            { "Equipment",            "the TARGET's equipment (equipment-targeting abilities, not the firing weapon — that is the ability's own Source); no A3b rider targets equipment" },
            { "ItemContainer",        "A4 (inventory) — a live container reference" },
            { "InventoryComponent",   "A4 (inventory) — a live component reference" },
            { "MultiAbilityTargets",  "recursive list; no rider is a multi-target ability, and shipping it would need the whole codec to nest" },
            { "FollowupAbility",      "a live ability reference. Move+shoot chains: the followup is a SECOND command and rides as its own intent, not as a passenger" },
            { "FollowupAbilityTarget","same — the followup's own payload travels with the followup's own command" },
            { "UseShootOriginCache",  "a per-peer performance hint (a projectile-origin transform cache), never shared state" },
        };

        internal static void Write(BinaryWriter w, TacticalAbilityTarget t)
        {
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
        internal const byte OpIntentActivate = 1;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>THE DECLARED RIDER SET — which abilities this arc actually carries. A3a is movement and
        /// nothing else; A3b adds the attack abilities here and to <see cref="TacAbilityTargetCodec.Rides"/>,
        /// with no new surface. <c>is</c> rather than an exact type match on purpose: <c>CaterpillarMoveAbility</c>
        /// (the ground-vehicle move) derives from <c>MoveAbility</c> and is the same order with the same payload.
        /// Everything OUTSIDE the set stays exactly as arc A2 left it — local and unrelayed — and says so once
        /// per ability def, because "my grenade did nothing on the other screen" with no log line is this
        /// project's dominant bug class.
        ///
        /// A4 ADDS EVACUATION, and it needs NOTHING else: <c>ExitMissionAbility.Activate</c>:32-36 and
        /// <c>EvacuateMountedActorsAbility.Activate</c>:57-69 both call <c>base.Activate(parameter)</c>, so the
        /// A3a capture already sees them, and what they do — <c>HideActorInExitZone</c>:25-30 applies
        /// <c>EvacuatedStatusDef</c>, unapplies every OTHER status, and hands the actor to the exit zone's
        /// <c>VehicleComponent.ApplyMountedStatus</c> — is exactly the native HIDE the mandate requires.
        /// Relaying the ORDER therefore makes every peer run the game's own hide; NOTHING in this repo
        /// destroys an evacuated actor, which is the v1 regression (d41b8f8: an empty BattleSummary, per-frame
        /// NREs in UIStateCharacterSelected, a dead evac button). The game itself never destroys a live actor
        /// either — <c>ActorSpawner.DestroyActor</c> has exactly ONE caller in the whole assembly,
        /// <c>DieAbility.PostProcessDeath</c>:63-74, and only when the def says not to keep the body.</summary>
        internal static bool IsRider(TacticalAbility ability) =>
            ability is MoveAbility || ability is ShootAbility || ability is BashAbility ||
            ability is ExitMissionAbility || ability is EvacuateMountedActorsAbility;

        /// <summary>HOST: the peer whose intent is currently being replayed natively, so the mirror can skip
        /// the peer that already played it locally. 0 = the host's own gesture (mirror to everyone). Scoped by
        /// a try/finally around ONE synchronous native call, which is why a plain field is enough — the move
        /// coroutine it starts runs later, on the game loop, with this already cleared.</summary>
        private static ulong _replayOriginPeer;

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
        }

        /// <summary>Log-once sets for the two "A3a knowingly does not cover this" notices. Per BATTLE, so the
        /// next mission reports its own gaps instead of inheriting a silence.</summary>
        private static readonly HashSet<string> _saidUncovered = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _saidKeyless = new HashSet<string>(StringComparer.Ordinal);

        // ~2 s at 60 fps. Not a deadline: a held settle is CORRECT while the actor is still moving, so the
        // hold keeps waiting and only says so periodically — but a hold nobody can see is the bug class.
        private const int SettleWarnFrames = 120;

        /// <summary>Per-BATTLE state, dropped at tactical teardown and at session teardown (alongside
        /// <c>TacticalTurnSync.Reset</c>). A leaked pending settle would snap an actor in the NEXT battle to a
        /// position from the previous one.</summary>
        internal static void Reset()
        {
            Seq.Reset();
            _pending.Clear();
            _saidUncovered.Clear();
            _saidKeyless.Clear();
            _replayOriginPeer = 0;
            TacticalActorKey.Reset();   // A3b: the derived alien keys belong to ONE battle and to no other
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
            // The SHARED PLAYER TEAM only. Enemy abilities are the host's own AI acting on the host, and A2
            // already holds every client inside the whole AI turn (ClientAiGate), so relaying them is arc A5's
            // job — not a gap here, and emphatically not something to shout about once per alien.
            var faction = actor.TacticalFaction;
            if (faction == null || !faction.IsControlledByPlayer) return;

            if (!IsRider(ability))
            {
                // The honest boundary, said out loud exactly once per ability def per battle: a player
                // gesture that reaches no other peer is invisible divergence, which is this project's
                // dominant bug class.
                string name = ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name;
                if (_saidUncovered.Add(name))
                    Debug.LogWarning("[Multiplayer][tac] '" + name + "' is NOT a declared rider — it ran " +
                                     "LOCALLY on this peer only and no other peer will see it. A3a/A3b/A4 carry " +
                                     "movement, shooting (incl. thrown and melee weapons), bash and evacuation.");
                return;
            }

            int key = TacticalActorKey.Of(actor);
            string guid = ability.AbilityDef == null ? null : ability.AbilityDef.Guid;
            var target = parameter as TacticalAbilityTarget;
            // A3b: the payload now names OTHER actors too, and an unkeyable one would ride as key 0 and be
            // refused on the far side AFTER the order was already accepted — so it is refused HERE, where the
            // gesture still belongs to somebody, and said out loud.
            string unkeyable = target == null ? null : FirstUnkeyableTargetField(target);
            if (key == 0 || string.IsNullOrEmpty(guid) || target == null || unkeyable != null)
            {
                string who = actor.name + " / " + (ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name);
                if (_saidKeyless.Add(who))
                    Debug.LogError("[Multiplayer][tac] command NOT relayed for " + who + " — " +
                                   (key == 0 ? "the commanded actor has no shared key (no GeoTacUnitId and no derived battle key)"
                                    : string.IsNullOrEmpty(guid) ? "the ability def has no guid"
                                    : target == null ? "the activation carried no TacticalAbilityTarget"
                                    : "the payload's " + unkeyable + " has no shared key, so no peer could tell " +
                                      "WHICH actor is being targeted") +
                                   ". This peer acted alone; no other peer will follow.");
                return;
            }

            if (engine.IsHost)
            {
                // THE FUMBLE PRE-ROLL (law L66d). TacticalAbility.Activate:1109 rolls the fumble INSIDE the
                // native body — after this prefix — and TacticalAbility.PlayAction:988-993 (and TFTV's
                // EnqueueAction fumble fix, TFTVVanillaFixes.cs:4003-4033) consume it SYNCHRONOUSLY before
                // Activate returns. So a fumble shipped after the order would always arrive too late, and a
                // mirror left to roll its own would play a different shot. Instead the host consumes the ONE
                // native roll here and memoizes it, so :1109 gets the same value and the bit rides WITH the
                // order. Clients never roll at all (FumbleCheckGate).
                bool fumbled = FumbleGate.RollForHost(ability);
                Send(OpActivate, "mirror " + actor.name + " " + Fmt(target.PositionToApply) + (fumbled ? " FUMBLED" : ""),
                     _replayOriginPeer, w => { WriteCommand(w, key, guid, target); w.Write(fumbled); });
            }
            else
                IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentActivate,
                                "command " + actor.name + " " + Fmt(target.PositionToApply),
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
        private static void HostSettle(TacticalActorBase actor)
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
                 " wp=" + wp.ToString("0.##"), 0,
                 w => { w.Write(key); w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(ap); w.Write(wp); });
        }

        private static void WriteCommand(BinaryWriter w, int actorKey, string abilityGuid, TacticalAbilityTarget target)
        {
            w.Write(actorKey);
            w.Write(abilityGuid);
            TacAbilityTargetCodec.Write(w, target);
        }

        private static string Fmt(Vector3 v) => v.IsNaN() ? "<none>" : v.ToString("0.#");

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

        private static void HandleActivate(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int key = r.ReadInt32();
            string guid = r.ReadString();
            var tlc = Tlc();
            // A throw here funnels into IntentRail's reject path. A key that does not resolve is NOT a throw —
            // it is a named refusal, so the losing peer is told which actor the host could not find.
            var unresolved = new List<string>();
            var target = TacAbilityTargetCodec.Read(r, tlc, unresolved);
            if (unresolved.Count > 0)
            {
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId,
                                  "command for actor " + key + ": the host cannot name the target — " +
                                  string.Join("; ", unresolved.ToArray()));
                return;
            }

            string why;
            var actor = TacticalActorKey.Resolve(tlc, key, out why) as TacticalActor;
            var faction = actor == null ? null : actor.TacticalFaction;
            TacticalAbility ability = actor == null ? null
                : actor.GetAbilityFiltered<TacticalAbility>(a => a.AbilityDef != null && a.AbilityDef.Guid == guid);

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
                if (actor != null && !actor.HasExecutingAbility()) HostSettle(actor);
                return;
            }

            // Native, and mirrored to everyone EXCEPT the peer that already played it locally. The origin is
            // scoped around this one synchronous call: the capture postfix inside reads it, and the move
            // coroutine it starts runs later with the field already cleared.
            _replayOriginPeer = senderPeerId;
            try { ability.Activate(target); }
            finally { _replayOriginPeer = 0; }
            Debug.Log("[Multiplayer][tac] HOST command from peer=" + senderPeerId + " ACCEPTED — " + actor.name +
                      " " + (ability.AbilityDef == null ? "?" : ability.AbilityDef.name) + " → " +
                      Fmt(target.PositionToApply) + " nonce=" + nonce);
        }

        // ─── CLIENT: apply ─────────────────────────────────────────────────

        /// <summary>Consumes <see cref="SurfaceIds.TacCommand"/> only; every other surface (including this
        /// family's own 0x83 intent, which <see cref="IntentRail"/> owns) falls through untouched.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacCommand) return false;
            if (engine == null || engine.IsHost) return true;   // the host never mirrors its own commands
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
                        int actorKey = r.ReadInt32();
                        string abilityGuid = r.ReadString();
                        var unresolved = new List<string>();
                        var target = TacAbilityTargetCodec.Read(r, Tlc(), unresolved);
                        bool fumbled = r.ReadBoolean();
                        ApplyActivate(actorKey, abilityGuid, target, fumbled, unresolved);
                    }
                    else if (op == OpSettle) QueueSettle(r.ReadInt32(),
                                                        new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                                                        r.ReadSingle(), r.ReadSingle());
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
            // The host's fumble is DECLARED before the native body runs, because Activate:1109 rolls it and
            // PlayAction:988-993 consumes it inside the same synchronous call — there is no later moment.
            FumbleGate.Declare(ability, fumbled);
            using (SyncApplyScope.Enter()) ability.Activate(target);
        }

        private static void QueueSettle(int key, Vector3 pos, float ap, float wp)
        {
            _pending[key] = new PendingSettle { Pos = pos, Ap = ap, Wp = wp, WaitedFrames = 0 };
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
                if (actor.HasExecutingAbility())
                {
                    var held = kv.Value;
                    if (++held.WaitedFrames % SettleWarnFrames == 0)
                        Debug.LogWarning("[Multiplayer][tac] holding the settle for " + actor.name + " — it has " +
                                         "been executing an ability for " + (held.WaitedFrames / 60) + "s. The " +
                                         "correction is still pending, not lost.");
                    _pending[kv.Key] = held;
                    continue;
                }
                ApplySettle(actor, kv.Value);
                (done ?? (done = new List<int>())).Add(kv.Key);
            }
            if (done != null) foreach (var k in done) _pending.Remove(k);
            if (lost != null) foreach (var k in lost) _pending.Remove(k);
        }

        /// <summary>Overwrite with the host's truth through the NATIVE writers — <c>SetTransform</c> is what
        /// raises <c>ActorMoved</c>/<c>ActorMovedInNewTile</c> (<c>TacticalActorBase</c>:665-685), which is how
        /// vision and voxel state stay consistent; a reflection poke at the transform would skip all of it.
        /// The actor's own rotation is kept: facing is presentation and law 5 names it local-only.</summary>
        private static void ApplySettle(TacticalActor actor, PendingSettle s)
        {
            using (SyncApplyScope.Enter())
            {
                actor.SetTransform(s.Pos, actor.Rot);
                var stats = actor.CharacterStats;
                if (stats != null)
                {
                    stats.ActionPoints.Set(s.Ap);
                    stats.WillPoints.Set(s.Wp);
                }
            }
            Debug.Log("[Multiplayer][tac] CLIENT settled " + actor.name + " @ " + Fmt(s.Pos) +
                      " ap=" + s.Ap.ToString("0.##") + " wp=" + s.Wp.ToString("0.##"));
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
}
