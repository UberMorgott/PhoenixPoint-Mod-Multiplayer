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

        /// <summary>L67e — THE OUTCOME ARM. Every refusal above is, by construction, an actor that is alive on
        /// the host and absent here: the two peers' rosters have diverged and this peer is fighting a smaller
        /// battle. L67's other arms assert the MECHANISM (a key is minted, a spawn op ships on 0x84, the client
        /// rebuilds through the game's spawner) and that is exactly why they stayed green through the Umbra bug
        /// — every mechanism fired, and the actor still existed on one screen only. So the ledger is read at the
        /// turn edge and a non-empty one is a FAILURE announced as such, not a shrug logged once at arrival and
        /// scrolled past. Returns null when the rosters agree, so the caller has nothing to decide.</summary>
        internal static string RosterDivergence()
        {
            if (_refused.Count == 0) return null;
            var parts = new List<string>(_refused.Count);
            foreach (var kv in _refused) parts.Add("key " + kv.Key + " — " + kv.Value);
            parts.Sort(StringComparer.Ordinal);
            return string.Join("; ", parts.ToArray());
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
                if (i > 0) ReportIfIndistinguishable(keyless[i - 1], keyless[i]);
                int key = _nextDerived--;
                _derived[keyless[i]] = key;
                _byDerived[key] = keyless[i];
            }
            _built = true;
            Debug.Log("[Multiplayer][tac] derived battle keys for " + keyless.Count + " actor(s) the geoscape " +
                      "never named (ordinals over battle-start position).");
            // THE ORDERING SEAM IS THIS FLAG, AND THE FLUSH READS IT FROM THE TICK — NOT FROM HERE.
            // Calling TacticalCommandSync.FlushPendedSelections() inline was the obvious wiring and it broke
            // L19: this method's only caller is TacNewTurnHook.Postfix, a postfix on the MODEL method
            // TacMission.OnNewTurn, so an intent emitted anywhere below it is a result-ship by the law's IL
            // walk. The flush therefore rides SyncEngineStub.Tick, which is where the rail already emits
            // (the turn-epoch ceiling resnapshot does the same), and it costs one static bool read per frame.
            // One frame of extra latency on a wait that was already 0.2 s long.
        }

        /// <summary>The battle-start tie diagnostic, deliberately in its OWN non-inlined method.
        ///
        /// Everything hazardous about <see cref="BuildBattleKeys"/> lives in here: <c>UnityEngine.Object.name</c>
        /// and <c>Pos</c> are native ECalls, and so is every Unity <c>==</c>. They are perfectly safe in the
        /// game, but an ECall that the JIT INLINES into its caller makes that CALLER un-compilable outside the
        /// player ("ECall methods must be packaged into a system module"). Under <c>-c Debug</c> nothing
        /// inlines, so an ECall behind a branch that never runs costs nothing; under <c>-c Release</c> the
        /// whole enclosing method dies on entry. That is exactly how BuildBattleKeys took the Release harness
        /// down while Debug stayed green — RailCheck L66 invokes it for real, and the crash named a method
        /// whose first line had not executed.
        ///
        /// <c>NoInlining</c> is the fix rather than rewriting the diagnostic: it keeps the ECalls inside a
        /// method that is only PREPARED when the tie actually happens (never in a headless run, where the
        /// actor list is empty), while the message itself stays exactly as loud in-game. Same reasoning as
        /// <c>RailCheck.Program.Run</c>, which carries the attribute for the same class of reason.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReportIfIndistinguishable(TacticalActorBase a, TacticalActorBase b)
        {
            if (CanonicalOrder(a, b) != 0) return;
            Debug.LogError("[Multiplayer][tac] two key-less actors are indistinguishable at battle start (" +
                           SafeName(b) + " and " + SafeName(a) + " at " + b.Pos +
                           ") — their derived keys depend on enumeration order and the peers may disagree " +
                           "about which is which. Every shot naming either of them is suspect.");
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
            { typeof(EndTurnAbility),
              "ambient: TacticalAbility.OnPlayingActionEnd:1060-1065 raises it on the actor of EVERY ability " +
              "whose def says EndsTurn — so the ENGINE ends the turn as a CONSEQUENCE of the action that just " +
              "played, on every peer that played it, off AP the mirror already carries. IsAutonomous cannot see " +
              "it (EndTurnAbility has no TacticalAbilityTarget at all, so there is no AttackType to read), which " +
              "is how an Overwatch that drained the last AP shipped a SECOND intent behind its own: the host had " +
              "already raised its own EndTurn while replaying the overwatch, its gate refused the duplicate, and " +
              "the acting client took a reject + a full resend + a forced settle for a turn that had ended " +
              "correctly everywhere (live 2026-08-05, nonce=18 after nonce=17). A peer's DELIBERATE end-turn is " +
              "a different funnel entirely — TacticalFaction.RequestEndTurn, captured by ClientEndTurnGate" },
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
            { typeof(OpenCrateAbility),
              "ambient AND uncarriable, and it is the second half that makes it a MUST. AMBIENT: " +
              "OpenCrateAbility.AbilityAdded:38-42 subscribes TacticalActor.AbilityExecutedEvent and " +
              "OnActorAbilityExecuted:70-97 raises the ability on EVERY peer whose copy of that soldier just " +
              "finished an IMoveAbility — which a mirror does, off the move that is already replicated — so the " +
              "lid opens everywhere with no relay at all. UNCARRIABLE: its parameter is a CrateComponent " +
              "(OnActorAbilityExecuted:96 Activate(crateComponent)), not a TacticalAbilityTarget, so " +
              "TacAbilityTargetCodec has nothing to write and the wire said '<no target>' — and OpenCrate:55-63 " +
              "opens with the HARD cast (CrateComponent)action.Param and dereferences it. Measured 2026-08-06, " +
              "23:18:19.606: 'MIRROR play Soldier_4 OpenCrate_AbilityDef' → 'Parameter: <>' → " +
              "NullReferenceException in <OpenCrate>d__10.MoveNext → 'Broken coroutine call chain' → the " +
              "PlayingAction never completed, so ClearPlayingAction never ran and the actor never left " +
              "ExecutingAbilities ('holding the settle … 2s/4s/6s/8s', forced at 10s). The HOST takes the same " +
              "hit replaying a client's intent (23:20:24.464, nonce=113) and the host has NO settle ceiling — " +
              "ClientTick is a client's. One stuck actor then makes TacticalView." +
              "IsWaitingForActiveAbilitiesAndMapUpdate:864 true for the whole map, and every " +
              "TacticalLevelController turn-pump wait (:1245/:1260/:1280/:1296/:1318/:1329) spins on it: that " +
              "is the enemy turn that never started (L178)" },
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

        /// <summary>Non-zero exactly while the HOST is replaying a named peer's intent through the native
        /// path. Read by <see cref="TacticalActorDrive"/>: on the host that is the ONLY way an order can be
        /// somebody else's, because a host click and a host replay both reach <c>Activate</c> natively and are
        /// otherwise indistinguishable.</summary>
        internal static ulong ReplayOriginPeer => _replayOriginPeer;

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

            /// <summary>The host's STATUS SET for that actor, as <see cref="TacticalStatusSet"/> keys. It rides
            /// the settle rather than a surface of its own because the settle is already the host's closer for
            /// one actor's state AND it already sweeps every keyed live actor at the turn edge — so the set is
            /// re-asserted routinely instead of only when a peer notices a hole in the 0x84 stream.</summary>
            public List<string> Statuses;

            /// <summary>The host's ABILITY-TRAIT set for that actor (<c>TacticalActor.AbilityTraits</c>:103).
            /// The same argument as <see cref="Statuses"/>, for the field class that had it worse: "this
            /// soldier's turn is over" is not a field at all but a trait — <c>TacticalActor</c>:198
            /// <c>HasEndedTurn => HasAbilityTrait("terminal")</c>, written only by
            /// <c>TacticalAbility.ApplyAbilityTraits</c>:915-919 as a side effect of replaying the ability.
            /// So it rode NOTHING: a peer that ended a soldier's turn locally could not be corrected by any
            /// host message, and a dropped or refused order left that soldier permanently actionable on the
            /// other peers. Carried on the settle for the same two reasons the statuses are: it is already
            /// the host's closer for one actor, and the turn-edge sweep re-asserts it for every keyed live
            /// actor.</summary>
            public List<string> Traits;

            /// <summary>The host's SELECTED EQUIPMENT for that actor, as an item def guid ("" = the host has
            /// nothing selected, which <c>RagdollDieAbility</c>:162 makes a real value). Null = the host had no
            /// equipment component to read, so there is nothing to reconcile.
            ///
            /// The same argument as <see cref="Statuses"/> and <see cref="Traits"/>, for the field that was
            /// losable in BOTH directions across a mission boundary. The selection rode as an EVENT only
            /// (<c>OpSelectEquipment</c>), and an event is exactly what a peer that is not ready yet drops:
            /// the 2026-08-07 logs show the host relaying five per-actor switches while the client had no
            /// tactical map at all to resolve a key against, and BOTH peers refusing to relay their own —
            /// <c>Soldier_6/7/8</c> on the host at 23:14:02, <c>Rancid/Cinder/Kain</c> on the client at
            /// 23:14:23 — because <c>EquipmentComponent</c>:56 raises the enter-play selection before this
            /// peer has built its battle key map. Nothing retried, so a lost switch was permanent, and the
            /// relay's own message correctly predicted the <c>EquipmentNotSelected</c> refusals that follow.
            /// Riding the settle makes it re-asserted routinely instead: the turn-edge sweep covers every keyed
            /// live actor, so the host's answer lands on every peer at the next turn edge at the latest,
            /// whichever direction the event was lost in.</summary>
            public string Selected;

            /// <summary>The host's PER-TURN USE COUNTS for that actor, as ability-def guid → count, non-zero
            /// entries only. The fourth field to move onto the settle for the same reason as
            /// <see cref="Statuses"/>, <see cref="Traits"/> and <see cref="Selected"/> — and the one nothing
            /// replicated at all. <c>TacticalActor._abilityUsesThisTurn</c>:113 is keyed by
            /// <c>TacticalAbilityDef</c>, so one entry covers every weapon's copy of an ability, and it is
            /// cleared only at the turn edge (:1194): a use this peer spent on an order the host refused
            /// blocked that ability on EVERY weapon for the rest of the turn, and switching weapons could not
            /// clear it by design. An ABSENT entry means ZERO, not "leave it alone" — see
            /// <see cref="HostUsesFor"/>.</summary>
            public Dictionary<string, int> Uses;
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
        internal const float DeferCeilingSeconds = 10f;

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

        /// <summary>CLIENT: actors whose clicked order has been sent and whose animation is waiting for the
        /// host's echo, with the frames each has waited. The acting peer no longer plays its own click
        /// (<see cref="PublishClickedOrder"/>), so this is the ONE thing standing between the button press and
        /// the animation — and the only peer it waits on is the HOST, which answers by itself. No other peer
        /// is consulted, so an AFK peer cannot lengthen it by a single frame.</summary>
        private static readonly Dictionary<int, int> _awaitingEcho = new Dictionary<int, int>();

        /// <summary>12 s at 60 fps, deliberately LONGER than the host's own <see cref="DeferCeilingSeconds"/>
        /// (10 s): a host that is legitimately HOLDING this peer's order behind that same peer's previous one
        /// gives up first and answers with a reject + forced settle, which clears the wait through
        /// <see cref="QueueSettle"/>. Only an echo that is never coming at all reaches this ceiling — and it
        /// is released LOUDLY rather than left to freeze the soldier for the rest of the battle.</summary>
        internal const int EchoCeilingFrames = 720;

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
            // Same contract, receiving side: Reset runs at tactical TEARDOWN (TacticalTurnSync's Playing→other
            // transition) and at session teardown — never on the way IN — so a record held while this peer was
            // still loading survives to be drained, and one still held when the battle ends is discarded.
            _heldRecords.Clear();
            _saidHeldOverflow = false;
            _heldFrames = 0;
            _pendedSelections.Clear();   // an un-keyable selection belongs to ONE battle (live component refs)
            _saidUncovered.Clear();
            _mirrorSkipsCameraWait.Clear();   // live ability refs: never let them outlive the battle
            _queuedMirrors.Clear();           // same: a watched record belongs to the battle it queued in
            _awaitingEcho.Clear();            // same: an echo wait belongs to ONE battle, never the next one
            _relayedAim.Clear();              // L104(j): keys belong to ONE battle
            _saidKeyless.Clear();
            _replayOriginPeer = 0;
            TacticalActorDrive.Reset();       // live ability refs: a drive mark belongs to ONE battle
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
            if (coord != null && coord.SessionStarted) return engine;
            // Not solo — a live co-op session with no SessionBegin. OnAbilityActivated returns on the very
            // next line, before any log, so THIS is where a client silently loses its whole battle. Named,
            // throttled, shared with TacticalDamageSync.LiveEngine (same swallow, same message).
            TacticalDamageSync.WarnDeadSession();
            return null;
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
            // itself rather than dropping the whole command. (The aim arm below needs `key`, so it comes
            // after this block, not before it.)
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
            // AND THE AIM BRANCH, one layer down (law L104(j)). Same arm point, deliberately NOT gated on
            // TrackWithCamera: the aim entry has nothing to do with the camera.
            ArmRelayedAim(key);

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
                // WHO GETS THIS MIRROR is decided by the ONE rule (L230), not by "somebody already played it".
                // The exclusion exists because a SPECULATIVE acting peer played the order at its own click and
                // a second copy would play it twice. An order that WAITS for the echo never played it, so
                // excluding its origin is exactly how that peer would be left standing still forever — the
                // wait would run to its ceiling on every single shot.
                ulong exclude = OrderWaitsForTheEcho(true, true, ability is IMoveAbility) ? 0UL : _replayOriginPeer;
                Send(OpActivate, "mirror " + actor.name + " " + name + " " + Where(target) +
                     (IsAutonomous(target) ? " [" + target.AttackType + "]" : "") + (fumbled ? " FUMBLED" : ""),
                     exclude, w => { WriteCommand(w, key, guid, target); w.Write(fumbled); });
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

        // ─── A9: THE CLICK NO LONGER PLAYS ITSELF ──────────────────────────

        /// <summary>
        /// A9 — EVERY PEER STARTS THE ANIMATION FROM THE HOST'S RECORD, INCLUDING THE PEER THAT CLICKED.
        ///
        /// THE DEFECT, in the owner's words: "I throw a grenade and on one instance it has already exploded
        /// while on another it is only just leaving the hand". The cause is not the wire, it is that the same
        /// action had THREE different start times by construction — the acting peer began at its own click
        /// (A3a's speculative local play), the host began when the intent landed (+½ RTT) and the other peers
        /// began when the mirror landed (+½ RTT again). Nothing corrected the gap because nothing was wrong:
        /// each peer was playing the order it had, as soon as it had it.
        ///
        /// SO THE SPECULATION IS RETIRED for everything that is not a move. The acting peer publishes the
        /// order and plays NOTHING; its animation starts from the host's mirror, decoded by
        /// <see cref="ApplyActivate"/> — the same function, from the same bytes, that every watching peer
        /// plays from. The cost is deliberate and was accepted: the acting player waits his own ping before
        /// his soldier moves. The spread across peers goes from [0 … RTT₁+RTT₂] to [½RTT … RTT].
        ///
        /// THE HOST IS NOT EXEMPT, and that is the half of the defect that would otherwise survive: a host
        /// click reaches <c>Activate</c> natively, takes the <see cref="RelayMirror"/> branch and never
        /// touches <see cref="Validate"/> at all (the asymmetry <see cref="HostGateFilter"/> documents). Here
        /// the host's own click is SERIALISED TO THE WIRE FORMAT AND FED TO <see cref="HandleActivate"/> with
        /// peer 0 — the very function that answers a client's order. It is arbitrated by the same
        /// <see cref="Validate"/>, held by the same <see cref="BusyWithOwnOrder"/> queue, played from the same
        /// decoded (and therefore equally lossy) target, and mirrored from the same one place. The host still
        /// cannot receive its own broadcast, so "plays from the record" means it publishes and plays inside one
        /// synchronous call; there is no earlier moment and no other peer is waited on.
        ///
        /// THIS IS NOT A QUORUM. The only peer ever waited on is the HOST, which answers by itself with no
        /// human action anywhere in the loop; an AFK peer cannot add a frame. And the wait is BOUNDED — see
        /// <see cref="TickEchoWaits"/>, which drops it out loud rather than freezing a soldier.
        ///
        /// GENERIC BY THE GAME'S OWN FUNNEL, NOT BY A LIST OF ABILITIES. The seam is
        /// <c>TacticalViewState.ActivateAbility</c>:259 — the ONE method every player click passes through,
        /// with exactly one override in the whole game (<c>UIStateShoot</c>:1385) which calls base. Shoot,
        /// free-aim (<c>UIStateFreeCam</c>:464), first-person multi-target, overwatch
        /// (<c>UIStateOverwatchAbilitySelected</c>), grenade/cone/throw and every def-driven ability
        /// (<c>UIStateAbilitySelected</c>), melee and reload (<c>UIStateCharacterSelected</c>) all route
        /// there. No <c>is ShootAbility</c>, no per-weapon branch, nothing to grow per alien.
        ///
        /// ONE EXCLUSION, BY THE GAME'S OWN MARKER INTERFACE and not by a concrete class: <c>IMoveAbility</c>
        /// (<c>MoveAbility</c>:20 <c>: TacticalAbility, IMoveAbility, IDelayedCacheAbility</c>), so every
        /// subclass the game and TFTV mint is covered — the trap a <c>typeof(MoveAbility)</c> test walks into.
        /// A move is excluded for a reason that is written down elsewhere in this file rather than invented
        /// here: <c>FollowupAbility</c> and <c>FollowupAbilityTarget</c> are in
        /// <see cref="TacAbilityTargetCodec.Dropped"/>, so a move that carries a follow-up attack
        /// (<c>UIStateCharacterSelected.MoveAndActivateAbility</c>:945) loses that attack on the wire —
        /// deferring the move would therefore delete the acting player's own follow-up shot, not merely delay
        /// it. Move also has the whole settle/closer architecture built to correct its divergence, and it is
        /// the one rider whose divergence is already cosmetic and self-correcting.
        /// </summary>
        internal static bool OrderWaitsForTheEcho(bool inSharedBattle, bool abilityIsRider, bool abilityIsMove)
            => inSharedBattle && abilityIsRider && !abilityIsMove;

        /// <summary>The player-click gate. TRUE means the local activation is SUPPRESSED and the order was
        /// published instead; FALSE means nothing was published and the click plays natively exactly as it
        /// always has (solo, a declared-local ability, a move, or a payload this peer cannot name).</summary>
        internal static bool PublishClickedOrder(TacticalAbility ability, TacticalAbilityTarget target)
        {
            var engine = LiveEngine();
            if (engine == null) return false;                 // solo, or connected but not in a co-op game
            if (SyncApplyScope.Active) return false;          // law 8: a mirror is driving this, not a click
            var actor = ability == null ? null : ability.TacticalActorBase;
            if (actor == null) return false;

            // ONE PREFIX, ONE DECISION (L146's gate, folded in). This used to be a SECOND, unprioritised
            // Harmony prefix on this same TacticalViewState.ActivateAbility (BusyActorBelongsToThePeerThatStartedIt).
            // Harmony stops at the first prefix that returns false and the order between two unprioritised patch
            // classes is undeclared — so on any given load one of the two silently did not run, and the two
            // "your click was suppressed" stories were indistinguishable on screen. Asked BEFORE the rider test
            // on purpose: the refusal is about WHO is driving this soldier, which is true of a move and of a
            // non-rider too, and asked AFTER the SyncApplyScope test because a mirror is never refused (law 8).
            string refusal = TacticalActorDrive.RefuseLocalCommand(ability);
            if (refusal != null)
            {
                Debug.Log("[Multiplayer][tac] this peer's command was REFUSED locally — " + refusal +
                          ". First-to-act-wins: that soldier is released the moment its own action ends, which " +
                          "no human has to do (postulate 2). Every other soldier stays commandable.");
                SessionNotifier.ShowToast(refusal, modalFallback: true);
                // AND THE SCREEN COMES BACK (L231). Returning true suppresses the native body's state switch as
                // well, so a refusal that did not release left the player standing in the targeting state his
                // click never left, with a toast telling him about a button that no longer did anything.
                ReleaseLocalUiHolding(actor, "a command this peer is not the one driving");
                return true;
            }

            if (!OrderWaitsForTheEcho(true, IsRider(ability), ability is IMoveAbility)) return false;

            int key = TacticalActorKey.Of(actor);
            string guid = ability.AbilityDef == null ? null : ability.AbilityDef.Guid;
            string name = ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name;
            string unkeyable = target == null ? null : FirstUnkeyableTargetField(target);
            if (key == 0 || string.IsNullOrEmpty(guid) || unkeyable != null)
            {
                // UNSHIPPABLE, so it falls back to the pre-A9 behaviour: this peer acts alone rather than
                // standing still forever waiting for an echo of an order nobody could send. Said out loud —
                // the same sentence OnAbilityActivated would have printed one layer down.
                if (_saidKeyless.Add("echo:" + actor.name + "/" + name))
                    Debug.LogError("[Multiplayer][tac] ECHO bypass for " + actor.name + " / " + name + " — " +
                                   (key == 0 ? "the commanded actor has no shared key"
                                    : string.IsNullOrEmpty(guid) ? "the ability def has no guid"
                                    : "the payload's " + unkeyable + " has no shared key") +
                                   ". This click is played LOCALLY and no other peer will follow it, so this " +
                                   "soldier's animation is out of step here by design rather than by accident.");
                return false;
            }

            if (engine.IsHost)
            {
                // THE HOST ANSWERS ITS OWN ORDER THROUGH THE FUNCTION THAT ANSWERS A PEER'S. Serialised and
                // re-read on purpose: the host then plays the same lossy target every client plays, so a field
                // the codec drops cannot make the host's shot differ from everybody else's.
                byte[] body;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    WriteCommand(w, key, guid, target);
                    body = ms.ToArray();
                }
                Debug.Log("[Multiplayer][tac] ECHO host " + actor.name + " " + name + " " + Where(target) +
                          " — published and played from that record, through the same arbitration a peer's " +
                          "order takes (no Validate bypass).");
                using (var ms = new MemoryStream(body))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                    HandleActivate(engine, 0, 0, OpIntentActivate, r);
                // AND THE HOST GETS ITS OWN SCREEN BACK. Every other release in this file is a CLIENT path —
                // HandleInbound and ClientTick both return early for the host — and HandleActivate's four exits
                // (unresolved, deferred, refused, accepted) contained none, so since A9 suppressed the native
                // state switch the host had NO exit from a targeting state at all, on success as much as on
                // failure. HandleActivate is synchronous, so this one line stands after all four of them.
                ReleaseLocalUiHolding(actor, "the host's own order, answered through the same arbitration a peer's takes");
                return true;
            }

            if (_awaitingEcho.ContainsKey(key))
            {
                // A SECOND CLICK INSIDE THE PING WINDOW. Still not sent — two orders for one soldier on the
                // wire is two shots from one actor when both mirrors land, which is what L83 exists to
                // prevent, so QUEUEING it is the wrong answer and stays rejected.
                //
                // What changes is that the refusal is now VISIBLE (law L231). Since L230 the click's native
                // activation is suppressed WITH its state switch, so returning true here left the player
                // standing in the targeting state their click never left, with nothing on screen to say the
                // click had been thrown away — they pressed again, and the second press was thrown away too.
                // Releasing the hold puts the screen back where the game itself puts it when an activation
                // does not happen, which reads as "not yet" instead of as a dead button.
                if (_saidUncovered.Add("echo2:" + key + "/" + name))
                    Debug.LogWarning("[Multiplayer][tac] ECHO busy — " + actor.name + " already has an order " +
                                     "waiting for the host's mirror, so this " + name + " click was REFUSED " +
                                     "rather than sent twice, and this peer's UI is released so the refusal is " +
                                     "visible. It is bounded: the wait gives up after " +
                                     (EchoCeilingFrames / 60) + "s and says so.");
                ReleaseLocalUiHolding(actor, "an order for this soldier already waiting for the host's mirror");
                return true;
            }

            IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentActivate,
                            "command " + actor.name + " " + name + " " + Where(target),
                            w => WriteCommand(w, key, guid, target));
            _awaitingEcho[key] = 0;
            Debug.Log("[Multiplayer][tac] ECHO wait " + actor.name + " " + name + " " + Where(target) +
                      " — this peer plays NOTHING now; its animation starts when the host's mirror lands, " +
                      "from the same record every other peer plays from.");
            return true;
        }

        /// <summary>The host's answer for this actor arrived (a mirror, or a settle carrying a refusal), so the
        /// wait is over. Called from BOTH ends on purpose: an order that is refused produces no mirror at all,
        /// and a wait that only a mirror could clear would sit out its whole ceiling on every refusal.</summary>
        private static void NoteEchoArrived(int key)
        {
            if (key != 0) _awaitingEcho.Remove(key);
        }

        /// <summary>THE BOUND. A lost or never-sent echo may not leave a soldier frozen for the rest of the
        /// battle, and it may not fail quietly — those are the two halves of this repo's dominant bug class in
        /// one place. Pumped from <see cref="ClientTick"/>; the host never waits for anything here.</summary>
        private static void TickEchoWaits()
        {
            if (_awaitingEcho.Count == 0) return;
            List<int> expired = null;
            foreach (var kv in new List<KeyValuePair<int, int>>(_awaitingEcho))
            {
                if (kv.Value + 1 < EchoCeilingFrames) { _awaitingEcho[kv.Key] = kv.Value + 1; continue; }
                (expired ?? (expired = new List<int>())).Add(kv.Key);
            }
            if (expired == null) return;
            foreach (var key in expired)
            {
                _awaitingEcho.Remove(key);
                string why;
                var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
                Debug.LogError("[Multiplayer][tac] ECHO LOST for " + (actor == null ? "actor " + key : actor.name) +
                               " — an order was published " + (EchoCeilingFrames / 60) + "s ago and the host " +
                               "never mirrored it back, so that soldier never played the action every other " +
                               "peer may already have played. The wait is RELEASED here (the soldier is " +
                               "clickable again); it is NOT replayed locally, because a locally-replayed order " +
                               "the host also ran is a second shot from one actor (law L83).");
                // "Clickable again" was only half true, and the other half was the lock-up (law L231):
                // A9 suppressed the native state switch at the click, so this peer is still standing in
                // UIStateShoot with no exit. Releasing the wait without releasing the SCREEN leaves a player
                // whose every button does nothing, for the rest of the battle.
                ReleaseLocalUiHolding(actor, "an echo that never arrived");
            }
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
                // THE EMITTING HALF OF THE SAME ORDERING BUG, and it is one this peer causes to ITSELF.
                // EquipmentComponent:56 raises the enter-play selection while the battle key map does not exist
                // yet — measured on the host, 2026-08-08: Soldier_7/8/9 were un-relayable at 12:22:46.198-46.320
                // and "derived battle keys for 67 actor(s)" landed at 12:22:46.511, 0.2 s later. So it PENDS
                // rather than drops, and BuildBattleKeys flushes it the instant the key exists — the one moment
                // that resolves the ordering, on every peer, host and client alike, with no new pump.
                //
                // Building the map EARLIER is the wrong fix and stays rejected: it is a hard one-shot over the
                // complete battle-start board (see BuildBattleKeys), and running it while actors are still
                // entering play would give the peers two different ordinal sets.
                if (!TacticalActorKey.Built) { _pendedSelections.Add(component); return; }
                if (_saidKeyless.Add("sel:" + SafeActorName(actor)))
                    Debug.LogWarning("[Multiplayer][tac] a weapon switch on " + SafeActorName(actor) + " cannot be " +
                                     "relayed — that actor has no shared key at all, and the battle key map IS " +
                                     "built, so this is not the mission-entry window: it entered play on this peer " +
                                     "alone. The host's own answer for this actor rides every settle, so the peers " +
                                     "converge at the next one and no ability is left refused for " +
                                     "EquipmentNotSelected — but a CLICK is lost here.");
                return;
            }
            RelaySelection(engine, actor, key, equipment);
        }

        private static void RelaySelection(NetworkEngine engine, TacticalActorBase actor, int key, Equipment equipment)
        {
            string guid = equipment == null || equipment.ItemDef == null ? "" : equipment.ItemDef.Guid;
            string what = "select " + actor.name + " -> " + EqName(equipment);
            if (engine.IsHost)
                Send(OpSelectEquipment, what, _replayOriginPeer, w => { w.Write(key); w.Write(guid); });
            else if (actor.TacticalFaction != null && actor.TacticalFaction.IsControlledByPlayer)
                IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentSelectEquipment, what,
                                w => { w.Write(key); w.Write(guid); });
        }

        /// <summary>Selections raised before this peer could name their actor. A SET, not a queue: only the
        /// component's CURRENT selection is worth relaying, so it is re-read at flush time and a soldier that
        /// switched twice in the window costs one entry. Bounded by the actor count, and cleared with the
        /// battle (<see cref="Reset"/>) because these are live component references.</summary>
        private static readonly HashSet<EquipmentComponent> _pendedSelections = new HashSet<EquipmentComponent>();

        /// <summary>Driven every frame from <c>SyncEngineStub.Tick</c> on EVERY peer, and it fires on the
        /// first frame after <see cref="TacticalActorKey.BuildBattleKeys"/> has run — a standing condition on
        /// the flag rather than a call from inside the builder, because that builder is only ever reached
        /// from <c>TacNewTurnHook.Postfix</c> and L19 forbids an intent below a model postfix. Two static
        /// reads on the frames where there is nothing to do.
        ///
        /// NoInlining for the reason <c>ReportIfIndistinguishable</c> spells out: this reaches
        /// <c>UnityEngine.Object.name</c>, and an inlined ECall makes its caller un-compilable in the
        /// headless harness (L113).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void FlushPendedSelections()
        {
            if (_pendedSelections.Count == 0) return;
            // Not yet keyable: keep holding. Flushing here would hit the "built WITHOUT keying it" arm below
            // and drop every pended selection as if the actor were local-only.
            if (!TacticalActorKey.Built) return;
            var batch = new List<EquipmentComponent>(_pendedSelections);
            _pendedSelections.Clear();
            var engine = LiveEngine();
            if (engine == null) return;
            foreach (var comp in batch)
            {
                var actor = comp == null ? null : comp.Actor as TacticalActorBase;
                if (actor == null) continue;
                int key = TacticalActorKey.Of(actor);
                if (key == 0)
                {
                    if (_saidKeyless.Add("pendsel:" + SafeActorName(actor)))
                        Debug.LogWarning("[Multiplayer][tac] " + SafeActorName(actor) + "'s weapon selection was " +
                                         "held for the battle key map and that map has now been built WITHOUT " +
                                         "keying it — this actor is on this peer alone, so the selection is " +
                                         "dropped here rather than held forever.");
                    continue;
                }
                Debug.Log("[Multiplayer][tac] relaying " + SafeActorName(actor) + "'s enter-play weapon selection " +
                          "now that the battle key map exists — it was raised before this peer could name it.");
                RelaySelection(engine, actor, key, comp.SelectedEquipment);
            }
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

        /// <summary>Make this actor hold what the host's is holding. Silent when they already agree — the
        /// settle sweeps every keyed live actor at every turn edge, so the overwhelmingly common case is "no
        /// change" and it must not narrate. A difference IS reported, because it is a switch that was lost on
        /// the wire and the repair is the only evidence that it happened. Runs inside the caller's
        /// <c>SyncApplyScope</c>, so <see cref="OnEquipmentSelected"/> stands down and nothing echoes back.</summary>
        private static void ReconcileSelection(TacticalActor actor, string guid)
        {
            if (guid == null) return;                       // the host had no equipment component to read
            EquipmentComponent comp; Equipment eq; string why;
            if (!ResolveEquipment(actor, guid, out comp, out eq, out why))
            {
                if (_saidKeyless.Add("resel:" + SafeActorName(actor) + ":" + guid))
                    Debug.LogError("[Multiplayer][tac] the host's settle says " + SafeActorName(actor) + " is holding " +
                                   "an item this peer cannot resolve — " + why + ". That soldier keeps the weapon " +
                                   "this screen shows, and any ability sourced from the host's one is refused here.");
                return;
            }
            if (ReferenceEquals(comp.SelectedEquipment, eq)) return;   // already agreed: the ordinary case
            string had = EqName(comp.SelectedEquipment);
            comp.SetSelectedEquipment(eq);
            TacticalUiRepaint.MarkDirty();
            Debug.LogWarning("[Multiplayer][tac] weapon selection RECONCILED at the host's settle — " + actor.name +
                             " was holding " + had + " and the host has " + EqName(eq) + ". A switch was lost on the " +
                             "wire in one direction or the other (both peers drop one at mission entry, before the " +
                             "battle key map exists), and this settle is what repairs it.");
        }

        private static void ApplySelectEquipment(int key, string guid)
        {
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
            if (actor == null)
            {
                Debug.LogWarning("[Multiplayer][tac] the host's weapon switch for actor " + key + " cannot be " +
                                 "applied here — " + why + ". That soldier keeps the weapon this screen shows until " +
                                 "the host's next settle for it, which carries the selection and repairs this.");
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

        /// <summary>L104(j) — ACTORS WHOSE AIM BRANCH IS NO LONGER LOCAL, by shared key. Armed at the SAME two
        /// points as <c>_mirrorSkipsCameraWait</c> (the acting path and the mirror path), so every peer holds
        /// the same set for the same order. Per-BATTLE, cleared in <see cref="Reset"/>; deliberately never
        /// removed per-actor, because an actor can only get into vanilla's aim LOOP by having shot before,
        /// and that shot was relayed — so by the moment the branch could disagree, every peer is armed.</summary>
        private static readonly HashSet<int> _relayedAim = new HashSet<int>();

        private static void ArmRelayedAim(int key) { if (key != 0) _relayedAim.Add(key); }

        /// <summary>Is this actor's aim branch under a relayed order? Read by
        /// <see cref="RelayedAimBranchIsTheSameOnEveryPeer"/>, on a getter the game calls per shot.</summary>
        internal static bool UnderRelayedAim(TacticalActorBase actor) =>
            _relayedAim.Count != 0 && IsRelayedAimKey(TacticalActorKey.Of(actor));

        /// <summary>The arm predicate with the Unity half taken out, so RailCheck can execute the decision
        /// itself rather than assert that a call to it exists — an actor cannot be built headless, a key can.
        /// Key 0 = no shared identity: an actor no peer can name is one no peer can agree with either, so it
        /// keeps the game's own answer.</summary>
        internal static bool IsRelayedAimKey(int key) => key != 0 && _relayedAim.Contains(key);

        /// <summary>
        /// THE TURN-EDGE SWEEP (law L123) — settle EVERY keyed live actor, once per host faction turn.
        ///
        /// <see cref="HostSettle"/> had exactly two callers: the end-of-action rider
        /// (<see cref="OnAbilityActionEnded"/>) and the reject path. Both are about an actor the host is
        /// ANIMATING. Nothing corrected an actor the host is NOT animating, so any divergence that got in by
        /// some other door stayed in for the rest of the battle. Live: the user shot an enemy, it cloaked and
        /// ran; on the host it went one way and on the clients another (a rogue local AI run leaked the
        /// move — see the open thread on <c>ClientAiGate</c>). The client then legitimately saw an enemy
        /// where the host had nobody, so every aimed shot was refused — <c>HOST tac-cmd REJECT peer=1 — the
        /// game's own gate refuses this ability: Нет подходящей цели</c>, the soldier wound up and cancelled
        /// five times in 45 s — and the lock broke on the exact settle <c>HOST settle Fishman_20 @
        /// (9.5,0,-12.5)</c>, with the very next shot accepted.
        ///
        /// This heals that class of divergence REGARDLESS of which funnel leaked it, which is why it is here
        /// and not a guard on the funnel: the turn edge is the one moment every peer agrees on, and the sweep
        /// costs ~140 actors × 25 B once per faction turn, entirely off the hot path.
        /// </summary>
        internal static void HostSettleAllLive(string when)
        {
            var engine = LiveEngine();
            if (engine == null || !engine.IsHost) return;
            var tlc = Tlc();
            if (ReferenceEquals(tlc, null) || ReferenceEquals(tlc.Map, null)) return;   // L113
            int settled = 0;
            foreach (var actor in tlc.Map.GetActors<TacticalActor>())
            {
                if (!actor.IsAlive) continue;                    // a corpse has no position worth arguing about
                if (TacticalActorKey.Of(actor) == 0) continue;   // unkeyed: no peer could name it anyway
                HostSettle(actor);
                settled++;
            }
            Debug.Log("[Multiplayer][tac] turn-edge settle sweep at " + when + " — " + settled +
                      " keyed live actor(s). Every peer's copy of an actor the host is not animating is " +
                      "corrected here; nothing else corrects it at all.");
        }

        /// <summary>Ship one actor's authoritative position + AP + WP to every peer. Broadcast to ALL,
        /// including whoever gestured: the acting peer is the one whose speculative local play most needs
        /// correcting.</summary>
        internal static void HostSettle(TacticalActorBase actor, bool forced = false)
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
            var statuses = TacticalStatusSet.Collect(tacActor);
            var traits = tacActor.AbilityTraits;   // Send runs writeBody synchronously; no copy needed
            var equips = tacActor.Equipments;
            bool hasEquip = equips != null;
            var selected = hasEquip ? equips.SelectedEquipment : null;
            string selGuid = selected == null || selected.ItemDef == null ? "" : selected.ItemDef.Guid;
            var uses = CollectAbilityUses(tacActor);
            Send(OpSettle, "settle " + tacActor.name + " @ " + Fmt(pos) + " ap=" + ap.ToString("0.##") +
                 " wp=" + wp.ToString("0.##") + (forced ? " FORCED" : ""), 0,
                 w => { w.Write(key); w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(ap); w.Write(wp);
                        w.Write(forced); TacticalStatusSet.Write(w, statuses); WriteTraits(w, traits);
                        w.Write(hasEquip); if (hasEquip) w.Write(selGuid); WriteUses(w, uses); });
        }

        /// <summary>THE PER-TURN USE COUNTER, WHICH NOTHING REPLICATED (2026-08-08 RCA, symptom 2).
        ///
        /// <c>TacticalAbility.Activate</c>:1092 calls <c>IncrementUsesThisTurn</c>, and the counter it feeds
        /// — <c>TacticalActor._abilityUsesThisTurn</c>:113 — is a
        /// <c>Dictionary&lt;TacticalAbilityDef,int&gt;</c>. Keyed by DEF, so every weapon's Overwatch shares
        /// ONE entry, and it is cleared only at the turn edge (<c>TacticalActor</c>:1194). With
        /// <c>UsesPerTurn</c> defaulting to 1, a client that played an order SPECULATIVELY spent the turn's
        /// use while the host — which refused before activating — kept 0; the client's own gate then answered
        /// "cannot be used again this turn" for every weapon he switched to, because a def-keyed counter
        /// cannot be cleared by changing weapons, by design.
        ///
        /// It rides the settle for the reason L131 (statuses), L137 (traits) and L186 (selection) already do:
        /// the settle is the host's closer for one actor AND sweeps every keyed live actor at every turn
        /// edge, so the authoritative value is RE-ASSERTED routinely and repairs this counter whatever leaked
        /// it — not only the reject case a targeted <c>ResetUsesThisTurn</c> would have covered. The game
        /// itself treats the field as actor state worth persisting
        /// (<c>TacActorInstanceData.AbilityUsesThisTurn</c>:30, written at <c>TacticalActor</c>:693 and
        /// restored at :606), which is this repo's own test for what belongs on the settle.
        ///
        /// Only non-zero entries ride, and one per DEF: several instances of one def (one per weapon) read
        /// the same dictionary slot, so shipping each would be the same number several times.</summary>
        private static List<KeyValuePair<string, int>> CollectAbilityUses(TacticalActor actor)
        {
            var list = new List<KeyValuePair<string, int>>();
            HashSet<string> seen = null;
            foreach (var ab in actor.GetAbilities<TacticalAbility>())
            {
                if (ab == null || ab.AbilityDef == null) continue;
                string g = ab.AbilityDef.Guid;
                if (string.IsNullOrEmpty(g)) continue;
                int n = actor.GetAbilityUsesThisTurn(ab);
                if (n <= 0) continue;
                if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                if (!seen.Add(g)) continue;
                list.Add(new KeyValuePair<string, int>(g, n));
            }
            return list;
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

        /// <summary>The trait codec. Plain strings and no key: a trait names nothing but itself, so unlike a
        /// status it needs no resolution on the far side and cannot fail to rebuild. Always consumes its
        /// bytes — the stream is shared with whatever follows it in the same message.</summary>
        internal static void WriteTraits(BinaryWriter w, IList<string> traits)
        {
            w.Write(traits == null ? 0 : traits.Count);
            if (traits == null) return;
            foreach (var t in traits) w.Write(t ?? "");
        }

        internal static List<string> ReadTraits(BinaryReader r)
        {
            int n = r.ReadInt32();
            var traits = new List<string>(n < 0 ? 0 : n);
            for (int i = 0; i < n; i++) traits.Add(r.ReadString());
            return traits;
        }

        /// <summary>The per-turn use codec, the same count-prefixed shape as the trait one and appended at the
        /// TAIL of the settle so the existing positional read chain is untouched. Always consumes its bytes.
        /// Def GUID, not a def reference: a def is not a shared address either peer can send.</summary>
        internal static void WriteUses(BinaryWriter w, IList<KeyValuePair<string, int>> uses)
        {
            w.Write(uses == null ? 0 : uses.Count);
            if (uses == null) return;
            foreach (var kv in uses) { w.Write(kv.Key ?? ""); w.Write(kv.Value); }
        }

        internal static Dictionary<string, int> ReadUses(BinaryReader r)
        {
            int n = r.ReadInt32();
            var d = new Dictionary<string, int>(n < 0 ? 0 : n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++) { string g = r.ReadString(); d[g] = r.ReadInt32(); }
            return d;
        }

        /// <summary>Pure: the host's use count for one ability def. ABSENT MEANS ZERO — and that IS the
        /// repair, because the spurious use a refused order left behind is precisely an entry the host does
        /// NOT have. An apply that only wrote the entries it was sent would never clear one, which is the
        /// silent-swallow shape this whole arc exists to close. RailCheck L242 executes this.</summary>
        internal static int HostUsesFor(Dictionary<string, int> hostCounts, string guid)
        {
            int n;
            return hostCounts != null && guid != null && hostCounts.TryGetValue(guid, out n) ? n : 0;
        }

        /// <summary>Drive this peer's per-turn use counters to the host's, through the game's own public
        /// writers (<c>TacticalActor.ResetAbilityUsesThisTurn</c>:1265 / <c>IncrementAbilityUsesThisTurn</c>
        /// :1253) — no reflection at the private dictionary. Iterates the ACTOR'S abilities and not the
        /// dictionary's keys, so an entry the host does not have is driven to 0 rather than skipped. Instances
        /// sharing one def are self-deduplicating: the first repairs the slot, the rest then agree.</summary>
        private static void ReconcileAbilityUses(TacticalActor actor, Dictionary<string, int> host)
        {
            if (actor == null || host == null) return;
            foreach (var ab in actor.GetAbilities<TacticalAbility>())
            {
                if (ab == null || ab.AbilityDef == null) continue;
                int want = HostUsesFor(host, ab.AbilityDef.Guid);
                int have = actor.GetAbilityUsesThisTurn(ab);
                if (have == want) continue;
                actor.ResetAbilityUsesThisTurn(ab);
                for (int i = 0; i < want; i++) actor.IncrementAbilityUsesThisTurn(ab);
                // Law 11 / postulate 1: CanUseThisTurn is what greys the ability bar, and nothing native
                // repaints it for a model change this peer did not click.
                TacticalUiRepaint.MarkDirty();
                Debug.LogWarning("[Multiplayer][tac] per-turn uses of " + ab.AbilityDef.name + " on " + actor.name +
                                 " reconciled at the host's settle — " + have + " -> " + want + ". The counter is " +
                                 "keyed by DEF (TacticalActor:113), so a use this peer spent on an order the host " +
                                 "refused blocked that ability on EVERY weapon until the turn edge, and no weapon " +
                                 "switch could clear it.");
            }
        }

        /// <summary>Do these two trait lists say different things? <c>OrdinalIgnoreCase</c> because that is
        /// the comparer the game's own readers use (<c>TacticalActor.HasAbilityTrait</c>:1330). Pure, so
        /// RailCheck L137 can execute the reconcile OUTCOME rather than assert a call exists.</summary>
        internal static bool TraitsDiffer(IList<string> local, IList<string> host)
        {
            if (local == null || host == null) return !ReferenceEquals(local, host);
            if (local.Count != host.Count) return true;
            for (int i = 0; i < local.Count; i++)
                if (!string.Equals(local[i], host[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Make this actor's trait set BE the host's, through the game's OWN replace-set
        /// (<c>TacticalActor.SetAbilityTraits</c>:1317 = <c>Clear</c> + <c>AddRange</c>). No plan and no
        /// per-trait apply, unlike the status set: a trait is an inert string with no <c>OnApply</c>, so the
        /// whole reconcile is the assignment. Silent when the two already agree — the turn-edge sweep settles
        /// every keyed live actor and that is the overwhelmingly common case.</summary>
        private static void ApplyTraits(TacticalActor actor, List<string> host)
        {
            if (actor == null || host == null) return;
            var local = actor.AbilityTraits;
            if (!TraitsDiffer(local, host)) return;
            string was = local == null ? "" : string.Join(",", local.ToArray());
            actor.SetAbilityTraits(host);
            // Law 11 / postulate 1: HasEndedTurn gates the whole ability bar, and nothing native repaints it
            // for a model change this peer did not click. The AP/WP comparator inside TacticalUiRepaint does
            // not see a trait, so a settle that only hands a soldier back would leave him greyed out on the
            // very screen that is watching him.
            TacticalUiRepaint.MarkDirty();
            Debug.LogWarning("[Multiplayer][tac] ability traits of " + actor.name + " reconciled at the host's " +
                             "settle — [" + was + "] -> [" + string.Join(",", host.ToArray()) + "]. 'terminal' " +
                             "in either list IS that soldier's turn (TacticalActor:198 HasEndedTurn), so a " +
                             "difference here was a soldier one peer could still move and another could not.");
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
        /// <summary>THE ONE FIRST-COME-FIRST-SERVED SENTENCE, named once because three seams say it: the
        /// host's arbitration below, the reject that carries it back to the losing CLIENT, and
        /// <see cref="TacticalActorDrive.RefuseLocalCommand"/>, which refuses the LOCAL half of the same race
        /// (the host's own click, or a client's speculative play) before it can start. Two peers racing for one
        /// soldier must read the same words whichever side of the wire they are on.</summary>
        internal const string BusyRefusal =
            "that actor is already executing an ability — another peer commanded it first (first-to-act-wins)";

        internal static string Validate(bool actorFound, bool actorAlive, bool actorIsPlayerControlled,
                                        bool factionIsPlayingTurn, bool abilityFound, bool abilityIsRider,
                                        bool actorBusy, string abilityDisabledReason, bool targetIsOffered,
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
                return BusyRefusal;
            if (!string.IsNullOrEmpty(abilityDisabledReason))
                return "the game's own gate refuses this ability: " + abilityDisabledReason;
            // WHICH target, not WHETHER there is one. GetDisabledState's NoValidTarget arm
            // (TacticalAbility:464, HasValidTargets) only says SOME legal target exists — so the point or the
            // actor the client picked was accepted verbatim by the host and every peer wrote it: the exit tile
            // of a vehicle was effectively client-authoritative, and boarding never checked the chosen vehicle
            // against the ability's own set (capacity is computed locally, VehicleComponent.IsFull:37). This
            // arm makes the game's OWN enumeration the authority for the choice as well as for its existence.
            if (!targetIsOffered)
                return "the chosen target is not one this ability offers on the host — the two peers disagree " +
                       "about the board, and the host's own target list is what an order may name";
            if (actionPoints < actionPointCost)
                return "not enough AP: " + actionPoints.ToString("0.##") + " left, " +
                       actionPointCost.ToString("0.##") + " needed";
            if (willPoints < willPointCost)
                return "not enough WP: " + willPoints.ToString("0.##") + " left, " +
                       willPointCost.ToString("0.##") + " needed";
            return null;
        }

        /// <summary>THE EQUIPMENT AN ABILITY SPEAKS FOR. Deliberately the SAME pair the game's own gate reads
        /// (<c>TacticalAbility.GetDisabledStateInternal</c>:414-417 takes <c>OverrideEquipment</c> and falls
        /// back to <c>EquipmentSource</c>) and the same pair <see cref="PreSelectSourceEquipment"/> reads, so
        /// all three agree about WHICH weapon is being judged. Null for an ability a soldier owns himself
        /// rather than through an item.</summary>
        private static Equipment SourceOf(Base.Entities.Abilities.Ability a)
        {
            var t = a as TacticalAbility;
            return t == null ? null : (t.OverrideEquipment ?? t.EquipmentSource);
        }

        /// <summary>THE DISAMBIGUATOR, PURE — so RailCheck L240 can execute the OUTCOME rather than assert
        /// that a call exists. TRUE = this candidate is the instance the acting peer meant. A source of
        /// <c>null</c> never wins by selection: an actor-owned ability is unique anyway, so it must fall
        /// through to the plain def match instead of tying with whatever happens to be in his hands.</summary>
        internal static bool CandidateMatchesSelection(bool defMatches, object source, object selected)
            => defMatches && source != null && ReferenceEquals(source, selected);

        /// <summary>ONE DEF GUID IS NOT ONE ABILITY, AND THE HOST WAS ANSWERING FOR THE WRONG WEAPON
        /// (2026-08-08 RCA).
        ///
        /// <c>Overwatch_AbilityDef</c> and <c>Reload_AbilityDef</c> mint ONE INSTANCE PER WEAPON that grants
        /// them (<c>OverwatchAbility.OverwatchWeapon => GetSource&lt;Weapon&gt;()</c>;
        /// <c>ReloadAbility</c>'s source is the <c>TacticalItem</c> it reloads), and
        /// <c>ActorComponent.GetAbilityFiltered</c>:211-221 returns the FIRST match. So a guid-only lookup
        /// always handed back the PRIMARY's instance, whatever the peer was holding. Measured on a soldier
        /// with broken arms who could not hold his rifle, client 3: <c>02:45:42.816 select → PX_Pistol</c>
        /// ACCEPTED (seq=259) → <c>02:45:43.965 command Reload_AbilityDef</c> →
        /// <c>.970 CLIENT weapon switch applied → PX_SniperRifle</c> → <c>.970 reject … Недостаточно
        /// свободных рук</c>. Five reloads refused that way; the sixth was accepted only because an inventory
        /// move had removed the rifle and first-match finally landed on the pistol. Overwatch was offered and
        /// refused for the same rifle by the same mechanism (<c>NoSuitableEquipment</c> via
        /// <c>ShootAbility.GetWeaponDisabledState</c> / <c>!Weapon.IsUsable</c>).
        ///
        /// THE GAME'S OWN DISAMBIGUATOR IS THE SELECTION, not the def: <c>TacticalAbility.Activate</c>:1087-1090
        /// selects the source equipment of the instance the player activated, and
        /// <c>GetDisabledStateInternal</c>:435 judges that instance's own equipment. Resolving by selection
        /// therefore RELAXES NOTHING — the rifle a broken arm cannot hold stays refused; it only stops the host
        /// answering a question about the pistol with the rifle's answer. It also makes
        /// <see cref="PreSelectSourceEquipment"/> a no-op for a peer's own click, which is what stops a refused
        /// order from publishing a weapon nobody chose on the following 0x82 settle (L186 propagated that
        /// wrong pre-select flawlessly — see L243).
        ///
        /// ponytail: selection is the disambiguator only while every rider is activated FROM the selected
        /// weapon. A def declaring <c>UsableOnNonSelectedEquipment</c> falls through to first-match exactly as
        /// before; giving it a right answer would need the source equipment's shared address to ride in the
        /// intent itself, which is a wire change no observed bug asks for yet.</summary>
        private static TacticalAbility ResolveAbility(TacticalActor actor, string guid)
        {
            if (actor == null) return null;
            var comp = actor.Equipments;
            var selected = comp == null ? null : comp.SelectedEquipment;
            // The guid comparison stays INSIDE both lambdas rather than in a shared helper: L80 asserts that
            // this resolution reads AbilityDef.Guid (an ldfld the scan can see), and a comparison one call
            // away would leave that law green over a lookup by list position.
            var bySelection = actor.GetAbilityFiltered<TacticalAbility>(
                a => CandidateMatchesSelection(a.AbilityDef != null && a.AbilityDef.Guid == guid,
                                               SourceOf(a), selected));
            if (bySelection != null) return bySelection;

            var any = actor.GetAbilityFiltered<TacticalAbility>(
                a => a.AbilityDef != null && a.AbilityDef.Guid == guid);
            // NEVER SILENTLY (this repo's dominant bug class): the guid AND the weapon that answered for it.
            // Only when the answer really is an equipment-sourced instance that is NOT what he is holding —
            // an actor-owned ability has no weapon to disagree with and must not narrate.
            var fell = SourceOf(any);
            if (fell != null && !ReferenceEquals(fell, selected) &&
                _saidUncovered.Add("resolve:" + TacticalActorKey.Of(actor) + "/" + guid + "/" + EqName(fell)))
                Debug.LogWarning("[Multiplayer][tac] ability guid " + guid + " on " + SafeActorName(actor) +
                                 " resolved to the instance sourced from " + EqName(fell) + ", but he has " +
                                 EqName(selected) + " selected. No instance of that def belongs to the selected " +
                                 "weapon, so this is first-match-by-guid — the answer (offered, refused, " +
                                 "charges spent) is about " + EqName(fell) + " and not about what is in his " +
                                 "hands. Expected only for a def that is UsableOnNonSelectedEquipment.");
            return any;
        }

        /// <summary>Pure: does the host move this soldier's selection before validating his order? Since
        /// <see cref="ResolveAbility"/> the resolved instance is the SELECTED weapon's whenever the peer has
        /// one, so the answer for a peer's own click is NO and this belt is a no-op — which is precisely what
        /// stops a REFUSED order from publishing a weapon nobody chose (L243). RailCheck executes it.</summary>
        internal static bool SelectionMoves(bool usableOnNonSelectedEquipment, bool hasSource, bool sourceIsSelected)
            => hasSource && !sourceIsSelected && !usableOnNonSelectedEquipment;

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
            if (ability == null) return;
            var eq = ability.OverrideEquipment ?? ability.EquipmentSource;
            if (!SelectionMoves(ability.UsableOnNonSelectedEquipment, eq != null, eq != null && eq.IsSelected)) return;
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

        /// <summary>Two targets are the SAME choice, as a pure decision so RailCheck L132 can execute it.
        ///
        /// The choice has exactly two axes, and which one is being made is decided by the CHOSEN target, never
        /// by the ability: naming an ACTOR (board that vehicle, shoot that alien) or naming a POSITION (exit
        /// onto that tile, throw at that point). A target that names neither — a direction, an inventory item,
        /// the actor itself — is not a choice between offers at all, and the game's own disabled-state gate
        /// stays its only authority.
        ///
        /// Position is compared with a grid tolerance because the two peers RE-DERIVE the offer from their own
        /// physics casts (<c>GetTargetPositions</c> yields <c>castResult.Point + up*0.05</c>), so identical
        /// tiles differ in the last decimals; the tactical grid is 1 unit, so half a unit can never reach the
        /// neighbouring tile.</summary>
        internal static bool TargetMatches(object offeredActor, Vector3 offeredPos, bool offeredHasPos,
                                           object chosenActor, Vector3 chosenPos, bool chosenHasPos)
        {
            if (chosenActor != null) return ReferenceEquals(offeredActor, chosenActor);   // L113: identity
            if (!chosenHasPos) return true;
            return offeredHasPos && (offeredPos - chosenPos).sqrMagnitude <= 0.25f;
        }

        /// <summary>Does the ability itself offer the target the client picked? ONE seam, every ability, and
        /// the enumeration is the game's own <c>TacticalAbility.GetTargets()</c> — no list of abilities here
        /// and no re-implementation of any ability's rules (<c>EnterVehicleAbility.GetTargets</c>:135-150 is
        /// where <c>CanEnter</c>/<c>IsFull</c> live, <c>ExitVehicleAbility</c>:100-113 is where
        /// <c>CanExit</c>+<c>CanStandAt</c> live, and both answer here for free).
        ///
        /// ONLY FOR AN ABILITY THAT DECLARES ITS OWN SET, and that is structural rather than a whitelist: an
        /// ability which does not override <c>GetTargets</c> inherits <c>GetDefaultTargets</c>, whose position
        /// branch floor-casts the whole reachable area — for <c>MoveAbility</c>, whose real authority is the
        /// PATHFINDER and not an enumeration (<c>GetTargetsData</c>, and it logs an error if asked while the
        /// actor is executing), that is thousands of casts per intent for an answer the ability never used.
        /// So the rule is "an ability that publishes a target list is held to it", which every modded ability
        /// joins by overriding the same method.
        ///
        /// A THROW ACCEPTS. This is an extra gate on top of the game's own; it must never become a new way for
        /// an order to be refused by our own bug. It became exactly that: a free-aim GROUND shoot — grenade,
        /// launcher, cone weapon, FPS-mode shot — is not drawn from any published list, so holding it to one
        /// refused every client throw while the client's own arc said it was legal. See
        /// <see cref="GroundTargetingExemptsTheOrder"/> for the engine's own statement to that effect.</summary>
        internal static bool TargetIsOffered(TacticalAbility ability, TacticalAbilityTarget chosen)
        {
            if (ability == null || chosen == null) return true;
            if (!DeclaresOwnTargets(ability.GetType())) return true;
            var shoot = ability as ShootAbility;
            if (GroundTargetingExemptsTheOrder(
                    shoot != null,
                    ability.TacticalAbilityDef?.TargetingDataDef?.Origin?.TargetResult ?? TargetResult.None,
                    shoot?.Weapon?.WeaponDef.DamagePayload.DamageDeliveryType))
                return true;
            // BOTH questions are properties of the two SETS, so both are asked at this altitude — see
            // ChoiceIsOffered. Asking "is this a choice at all?" inside the loop body (where it used to live,
            // in TargetMatches) means an ability that publishes NOTHING never asks it: the loop body never
            // runs, and every order it ever sends is refused.
            bool namesATarget = chosen.GetTargetActor() != null || chosen.HasPositionToApply;
            bool published = false, matched = false;
            if (namesATarget)
            {
                try
                {
                    foreach (var offered in ability.GetTargets())
                    {
                        if (offered == null) continue;
                        published = true;
                        if (TargetMatches(offered.GetTargetActor(), offered.PositionToApply, offered.HasPositionToApply,
                                          chosen.GetTargetActor(), chosen.PositionToApply, chosen.HasPositionToApply))
                        { matched = true; break; }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][tac] could not enumerate the targets of " +
                                   (ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name) +
                                   " to check the one a peer picked — the order is ACCEPTED on the game's own gate " +
                                   "alone: " + ex);
                    return true;
                }
            }
            return ChoiceIsOffered(namesATarget, published, matched);
        }

        /// <summary>THE WHOLE VERDICT WITH THE ENUMERATION TAKEN OUT — pure, so RailCheck L132 executes the
        /// OUTCOME an order is owed instead of asserting that a call to a gate exists (the gate below was
        /// green for four days while it refused every overwatch a client ever sent).
        ///
        /// An empty offer set is NOT "the host offers nothing here, refuse". It means the ability publishes no
        /// list at all, and there is then nothing to hold the order to — the game's own <c>GetDisabledState</c>
        /// stays the only authority, exactly as for an ability that never overrides <c>GetTargets</c>. The
        /// shipped instance is <c>OverwatchAbility.GetTargets</c>:42-45, a bare <c>yield break</c> whose
        /// <c>HasValidTargets</c>:19 is hardcoded <c>true</c> — so the game itself does not read that
        /// enumeration as a statement about validity, and neither may we. The trigger is STRUCTURAL, not
        /// overwatch's: every ability that overrides <c>GetTargets</c> without publishing a free-aim choice —
        /// shipped, modded or future — was refused identically, which is why the answer is here and not a
        /// type in a skip list.
        ///
        /// The gate the law is FOR is the third line: an ability that DOES publish a list is held to it.</summary>
        internal static bool ChoiceIsOffered(bool chosenNamesATarget, bool abilityPublishedAnyTarget, bool matched)
        {
            if (!chosenNamesATarget) return true;        // a direction, an inventory item, the actor itself
            if (!abilityPublishedAnyTarget) return true; // no list to be held to
            return matched;
        }

        /// <summary>DOES THE ABILITY PUBLISH A CHOICE AT ALL, OR IS THE PLAYER AIMING FREELY — pure, so
        /// RailCheck L169 executes the OUTCOME instead of asserting that a call exists (L132's lesson).
        ///
        /// THE ENGINE'S OWN STATEMENT, NOT OURS. <c>UIStateAbilitySelected.EnterState</c>:198-202 calls
        /// <c>GetTargets()</c> and then THROWS THE RESULT AWAY for exactly this case —
        /// <c>if (_selectedAbility is ShootAbility ability &amp;&amp; TacticalViewState.IsTargetingGround(ability))
        /// source = Enumerable.Empty&lt;TacticalAbilityTarget&gt;();</c> — and :468-470 enters the shoot state
        /// with a NULL valid-shoot list. The player then aims at a raw cursor point
        /// (<c>UIStateShoot.UpdateGroundTargetState</c>:1180 <c>new TacticalAbilityTarget(vector)</c>), and the
        /// arc's whole verdict is <c>GetShootTarget(...) != null</c> (:1182), never list membership. Meanwhile
        /// <c>GetTargets()</c> for that same ability is a grid FLOOR-CAST SWEEP
        /// (<c>TacticalAbility.GetTargetPositions</c>:605-660): a free cursor point is not drawn from that grid
        /// and is under no obligation to land within <see cref="TargetMatches"/>'s half-unit tolerance of a
        /// surviving member. So the enumeration is not the authority here BY THE GAME'S OWN CODE, and our host
        /// was applying a rule the game does not have — five refused grenades on one soldier in 25 seconds,
        /// each at a point 0.1-0.3 units from the last, which is a human nudging a free cursor.
        ///
        /// THE PREDICATE IS COPIED, NOT INVENTED. <c>TacticalViewState.IsTargetingGround</c>:200-219 is
        /// <c>protected static</c> and unreachable from here, so its body is reproduced below line for line,
        /// including the two arms a summary would drop: a weapon whose delivery type is <c>Sphere</c> is ground
        /// targeting too, and so is a shoot ability with NO weapon at all (the game's <c>?.</c> yields null and
        /// falls to <c>return true</c>).
        ///
        /// NARROWED TO <c>ShootAbility</c>, EXACTLY AS <c>UIStateAbilitySelected</c>:199 NARROWS IT, and that
        /// is the whole reason the first parameter exists rather than being folded into the caller. The
        /// <c>TargetResult.Position</c> arm alone is true of <c>ExitVehicleAbility</c> — the ability this gate
        /// was written for (<see cref="Validate"/>:1731-1736, where the exit tile was effectively
        /// client-authoritative). Widening the exemption to every position-targeting ability re-opens that
        /// hole; the law's positive control is that arm.
        ///
        /// ponytail: this exempts a whole class of order from OUR gate rather than answering the question the
        /// game answers. If it ever proves too wide, replace it with the game's own single-point authority run
        /// on the host — <c>shootAbility.GetShootTarget(chosen, null, ability.OriginTargetData) != null</c>,
        /// literally the call the client's arc is drawn from (<c>UIStateShoot</c>:1182) — asked once per intent
        /// instead of once per grid cell. Not done now because it is a live engine call inside the arbiter,
        /// where the exemption is a decision the harness can run to exhaustion.</summary>
        internal static bool GroundTargetingExemptsTheOrder(bool isShootAbility, TargetResult originTargetResult,
                                                           DamageDeliveryType? deliveryType)
        {
            if (!isShootAbility) return false;
            // ── TacticalViewState.IsTargetingGround:204-218, verbatim ──
            if (originTargetResult == TargetResult.Position || originTargetResult == TargetResult.ActorAndPosition)
                return true;
            if (deliveryType.HasValue && deliveryType != DamageDeliveryType.Parabola &&
                deliveryType != DamageDeliveryType.Cone)
                return deliveryType == DamageDeliveryType.Sphere;
            return true;
        }

        /// <summary>THE FILTER THE ENGINE'S OWN CONFIRM USES, ASKED THE SAME WAY ON THE HOST — pure, so
        /// RailCheck L210 executes the OUTCOME instead of asserting that a call exists.
        ///
        /// <see cref="GroundTargetingExemptsTheOrder"/> took the grid sweep out of OUR gate and left the game's
        /// own <c>GetDisabledState()</c> standing, on the reasoning that the engine's verdict is the one to
        /// trust. It is — but we were not asking it the way the engine does. For a shoot the engine NEVER asks
        /// the unfiltered question at the moment of confirm: <c>UIStateShoot.ConfirmShoot</c>:1295 and
        /// <c>UIStateFreeCam.ConfirmShoot</c>:466 both gate on
        /// <c>_ability.IsEnabled(IgnoredAbilityDisabledStatesFilter.IgnoreNoValidTargetsFilter)</c>. The plain
        /// call keeps the arm at <c>TacticalAbility.GetDisabledStateInternal</c>:464-466, and for a shoot that
        /// arm is <c>ShootAbility.HasValidTargets</c>:41 — <c>GetTargets().Any()</c>, the SAME grid floor-cast
        /// sweep :2005 already establishes a free cursor point is not drawn from. So the host was refusing every
        /// free-aim shot on the enumeration it had just been told not to read: peer=2's Soldier_1/2/3 and
        /// peer=1's Soldier_1/2, five refusals in 67 seconds, all "Нет подходящей цели"
        /// (<c>NoValidTarget</c>), while the HOST'S OWN free-aim shot 7 seconds later logged that same disabled
        /// state at its confirm and fired anyway for 76 damage — because the host's click takes the
        /// RelayMirror branch and never reaches <see cref="Validate"/>. That asymmetry IS the defect.
        ///
        /// NOTHING ELSE IS WAIVED, and the filter is passed INTO the engine rather than subtracted from its
        /// answer, because <c>GetDisabledStateInternal</c> returns the FIRST failing arm: post-filtering a
        /// <c>NoValidTarget</c> result would silently accept the <c>OffMap</c>, <c>ActorStunned</c> and
        /// <c>NotEnoughActionPoints</c> arms that sit BEHIND it at :467-475 and never got evaluated.
        ///
        /// THIS CANNOT WIDEN THE TARGET-CHOICE GATE, and the coupling is exact rather than fortunate:
        /// <c>NoValidTarget</c> holds iff <c>GetTargets()</c> is empty, and an empty enumeration is precisely
        /// <c>ChoiceIsOffered</c>'s <c>abilityPublishedAnyTarget == false</c> — "no list to be held to", which
        /// already returns true. Every order this newly admits is one <see cref="TargetIsOffered"/> was already
        /// admitting; an actor-targeted shot at somebody the host cannot see still publishes a list and is
        /// still refused by that gate.
        ///
        /// NARROWED BY THE BASE TYPE, NOT BY A CONCRETE ONE. <c>is ShootAbility</c> covers every subclass the
        /// game and TFTV mint, which a <c>GetType() ==</c> test or an <c>AccessTools.Method</c> signature match
        /// would not. It is the same narrowing <c>UIStateAbilitySelected</c>:199 uses.</summary>
        internal static IgnoredAbilityDisabledStatesFilter HostGateFilter(bool isShootAbility)
        {
            return isShootAbility ? IgnoredAbilityDisabledStatesFilter.IgnoreNoValidTargetsFilter : null;
        }

        private static readonly Dictionary<Type, bool> _declaresTargets = new Dictionary<Type, bool>();

        private static bool DeclaresOwnTargets(Type abilityType)
        {
            bool declares;
            if (_declaresTargets.TryGetValue(abilityType, out declares)) return declares;
            var m = AccessTools.Method(abilityType, "GetTargets",
                                       new[] { typeof(TacticalTargetData), typeof(TacticalActorBase), typeof(Vector3) });
            declares = m != null && m.DeclaringType != typeof(TacticalAbility);
            _declaresTargets[abilityType] = declares;
            return declares;
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
            // BY SELECTION, NOT BY FIRST MATCH — several instances share one def guid (see ResolveAbility).
            TacticalAbility ability = ResolveAbility(actor, guid);

            PreSelectSourceEquipment(ability);

            string disabled = null;
            if (ability != null)
            {
                // The engine's own confirm-time question, not the display-time one — see HostGateFilter.
                var state = ability.GetDisabledState(HostGateFilter(ability is ShootAbility));
                if (state != AbilityDisabledState.NotDisabled) disabled = state.ToString();
            }
            var stats = actor == null ? null : actor.CharacterStats;

            string refusal = Validate(actor != null, actor != null && actor.IsAlive,
                                      faction != null && faction.IsControlledByPlayer,
                                      faction != null && faction.IsPlayingTurn,
                                      ability != null, ability != null && IsRider(ability),
                                      actor != null && actor.HasExecutingAbility(), disabled,
                                      TargetIsOffered(ability, target),
                                      stats == null ? 0f : (float)stats.ActionPoints,
                                      ability == null ? 0f : ability.ActionPointCost,
                                      stats == null ? 0f : (float)stats.WillPoints,
                                      ability == null ? 0f : ability.WillPointCost)
                             ?? why;   // a resolve failure has its own, more specific sentence

            if (refusal != null)
            {
                // No geoscape path prefix: a tactical reject touches nothing on the value rail, and the reject
                // NUDGE is what repaints the gesturing client's own screen.
                // Named, not keyed: since L123 this string is shipped to the refused peer and put on its
                // screen, so it is read by a player and not only by whoever opens the host's log.
                // NOTIFY, for this refusal only. IntentRail's own doc reserves the popup for a MOD-PROTOCOL
                // refusal that leaves the player with a dead gesture and no other feedback — "another peer
                // already took this soldier" is the textbook case, and it was the one arriving silently: the
                // reason reached the client's LOG and never its screen, so the second player saw a wind-up,
                // a cancel and no word. Every other arm keeps the quiet form (vanilla greys those controls).
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId,
                                  "command for " + SafeActorName(actor) + ": " + refusal,
                                  ReferenceEquals(refusal, BusyRefusal));
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

        /// <summary>RECORDS THAT ARRIVED BEFORE THIS PEER HAD A BATTLE TO APPLY THEM TO, kept as the raw bytes
        /// they came in — the decoder is re-entered on replay, so exactly one place knows the payload layout
        /// (the same posture <see cref="DeferredCommand"/> already takes for a held order).
        ///
        /// MEASURED, NOT ARGUED (2026-08-08, three instances). The host starts broadcasting on 0x82 the moment
        /// IT enters play: <c>HOST select Soldier_2..6</c> at 12:22:45.868-46.134. Both clients received those
        /// five records at 12:22:45.8-46.1 — while still DOWNLOADING the save blob (<c>OnSaveChunk FIRST</c>
        /// 12:22:47.657, <c>ClientLoadCrt</c> 12:22:47.658, the tactical load itself only starting at
        /// 12:22:48.788), standing in <c>UIStateRosterDeployment</c> on the geoscape with no tactical level at
        /// all. <see cref="TacticalActorKey.Resolve"/> answered "no tactical map on this peer" for every one of
        /// them and the whole squad's weapon selection was dropped, on each client, every battle.
        ///
        /// AN ORDERING BUG, NOT A RACE: the host's broadcast BEGINS before a joining peer's battle EXISTS, so
        /// there is no instant at which the arrival could have worked. Nothing about it is specific to a weapon
        /// switch, either — every 0x82 op is keyed by actor and every one of them resolves against that same
        /// missing map — which is why the gate is on the SURFACE and not inside one op's applier.</summary>
        private static readonly List<byte[]> _heldRecords = new List<byte[]>();

        /// <summary>The hold is BOUNDED TWICE, and deliberately: this is the one mechanism in this file that
        /// can stop a peer applying the host's battle, so "it is still waiting" may never become permanent.
        /// Past EITHER bound the whole hold is released and every record takes the pre-hold path — applied for
        /// real, refused with its own sentence if it cannot resolve — which is exactly today's behaviour, so
        /// the worst case of the fix is the status quo rather than a peer frozen out of its own battle.</summary>
        private const int HeldRecordCeiling = 2048;

        /// <summary>~60 s of RUNNING frames. A frame count and not a clock on purpose: it is pumped from
        /// <see cref="ClientTick"/>, so it advances slowly through a heavy tactical load (which is the legitimate
        /// wait) and quickly once this peer is running and simply never joined the battle (which is not).</summary>
        private const int HoldCeilingFrames = 3600;

        private static bool _saidHeldOverflow;
        private static int _heldFrames;

        /// <summary>Consumes <see cref="SurfaceIds.TacCommand"/> only; every other surface (including this
        /// family's own 0x83 intent, which <see cref="IntentRail"/> owns) falls through untouched.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacCommand) return false;
            if (engine == null || engine.IsHost) return true;   // the host never mirrors its own commands
            // HOLD, NEVER DROP. The `_heldRecords.Count > 0` arm is part of the condition and not an
            // optimisation: the frame the map appears, a NEW record must not overtake the ones already waiting.
            // 0x82 carries ONE seq stream precisely so a settle can never pass its own activate
            // (SurfaceIds:36), and the drain is the only thing allowed to empty the queue.
            if (_heldRecords.Count > 0 || BattleNotReadyHere())
            {
                HoldRecord(payload);
                return true;
            }
            ApplyInbound(payload);
            return true;
        }

        /// <summary>TRUE while this peer cannot name an actor yet — BOTH pre-battle windows, because a record
        /// keyed by an actor is unappliable in either one and the difference is invisible from the wire.
        ///
        ///  • NO TACTICAL LEVEL: the save is still transferring or the level is still loading. Measured above.
        ///  • NO BATTLE KEY MAP: the level is up but <see cref="TacticalActorKey.BuildBattleKeys"/> has not run
        ///    here, so every NEGATIVE (derived) key resolves to nothing. That window is not small — on
        ///    2026-08-08 the host built at 12:22:46.511 and the two clients at 12:22:58.088 and 12:22:58.650,
        ///    twelve seconds in which any mirrored enemy action would have been refused. It cost nothing that
        ///    run only because the host happened to act after the barrier; nothing makes that a rule.
        ///
        /// <c>ReferenceEquals</c> for the reason L113 gives and <see cref="TacticalActorKey.Resolve"/> already
        /// relies on.</summary>
        private static bool BattleNotReadyHere()
        {
            var tlc = Tlc();
            if (ReferenceEquals(tlc, null) || ReferenceEquals(tlc.Map, null)) return true;
            return !TacticalActorKey.Built;
        }

        private static void HoldRecord(byte[] payload)
        {
            if (payload == null) return;
            if (_heldRecords.Count < HeldRecordCeiling) { _heldRecords.Add(payload); return; }
            if (!_saidHeldOverflow)
            {
                _saidHeldOverflow = true;
                Debug.LogError("[Multiplayer][tac] " + HeldRecordCeiling + " battle records are waiting for a " +
                               "battle this peer can name actors in — the hold is FULL, and every record from " +
                               "here on is applied immediately, which for an actor-keyed op means refused. This " +
                               "peer has not joined the host's battle.");
            }
            ApplyInbound(payload);
        }

        /// <summary>Pumped from <see cref="ClientTick"/> every frame, INCLUDING the frames where this peer has
        /// no battle at all — which is exactly the window the hold exists for. Replayed in ARRIVAL order
        /// through the same decoder, so law 7's seq is evaluated once, here, against the same stream it always
        /// was; nothing is marked for a record that was never applied.</summary>
        private static void DrainHeldRecords()
        {
            if (_heldRecords.Count == 0) return;
            if (BattleNotReadyHere())
            {
                if (++_heldFrames < HoldCeilingFrames) return;
                Debug.LogError("[Multiplayer][tac] " + _heldRecords.Count + " battle record(s) have been held for " +
                               (_heldFrames / 60) + "s waiting for a battle this peer can name actors in, and are " +
                               "being applied ANYWAY. Whatever cannot resolve is refused with its own sentence " +
                               "from here — the hold is a head start, never a place a record disappears into.");
            }
            var batch = _heldRecords.ToArray();
            _heldRecords.Clear();
            _saidHeldOverflow = false;
            _heldFrames = 0;
            Debug.Log("[Multiplayer][tac] replaying " + batch.Length + " battle record(s) held while this peer " +
                      "could not name an actor — the host broadcasts from the moment IT enters play, which on " +
                      "2026-08-08 was before a joining peer's save transfer had even finished.");
            foreach (var p in batch) ApplyInbound(p);
        }

        private static void ApplyInbound(byte[] payload)
        {
            _recordArrived = Time.realtimeSinceStartup;
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Seq.ShouldApply(SurfaceIds.TacCommand, seq)) return;  // stale re-delivery (law 7)
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
                                                        r.ReadSingle(), r.ReadSingle(), r.ReadBoolean(),
                                                        TacticalStatusSet.Read(r), ReadTraits(r),
                                                        r.ReadBoolean() ? r.ReadString() : null,
                                                        ReadUses(r));
                    else if (op == OpSelectEquipment) ApplySelectEquipment(r.ReadInt32(), r.ReadString());
                    else
                    {
                        Debug.LogError("[Multiplayer][tac] unknown host→all command op " + op + " (seq=" + seq +
                                       ") — this peer can no longer follow the shared battle.");
                        return;
                    }
                    Seq.Mark(SurfaceIds.TacCommand, seq);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] command inbound FAILED — this peer's battle has diverged from " +
                               "the host's: " + ex);
            }
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
        /// <summary>THE SETUP THE MIRRORED PATH SKIPPED — and it is not on the ability, it is on the VIEW.
        ///
        /// The local input path does not activate through <c>Activate</c> alone. It goes through
        /// <c>TacticalViewState.ActivateAbility</c> (<c>TacticalViewState.cs</c>:259-277), which does TWO
        /// things: <c>ability.Activate(target)</c> (:270) and then <c>SwitchToState(new UIStateWaiting(),
        /// ClearStackAndPush)</c> (:274-275) — it takes the local view OUT of the state that was holding that
        /// soldier BEFORE the engine starts driving him. The mirror path called only the first, and nothing
        /// else in the game closes the gap: <c>TacticalView</c> subscribes to <c>AbilityExecutedEvent</c>
        /// (<c>TacticalView.cs</c>:259) but <c>OnAbilityExecuted</c>:542-575 only refreshes prompts, and
        /// neither <c>UIStateShoot</c> nor <c>UIStateFreeCam</c> subscribes to ANY cancel/end callback — they
        /// exit on local INPUT only. So a mirrored order left this peer's UI running over an actor the engine
        /// had just taken, for as long as that order lasted. Both live reports are that one gap.
        ///
        ///  • THE CRASH (defect 1). <c>UIModuleFreeFirstPersonShooting.Update</c>:132-137 gates on nothing but
        ///    <c>gameObject.activeInHierarchy &amp;&amp; _context != null</c>, and <c>_context</c> is written
        ///    once (<c>InitFirstPersonShootingLayout</c>:141) and NEVER cleared. Left running, its
        ///    <c>UpdateAccuracyIndicator</c>:259-321 dereferences <c>CurrentBehavior as FirstPersonCamera</c>
        ///    (:316-317, an <c>as</c> cast with no null test) and <c>_context.View.SelectedActor</c>
        ///    (:264-265, nulled by <c>TacticalView</c>:1150/:1175) — both of which a mirrored activation and a
        ///    forced settle move out from under it. That is the report verbatim: the SAME NRE 797 times, one
        ///    per frame, then a native crash. The game already ships the fix as that state's own teardown
        ///    (<c>UIStateFreeCam.ExitState</c>:447 → <c>ClearFirstPersonShootingLayout</c>:170 turns the
        ///    module's GameObject off, which is exactly what stops <c>Update</c>).
        ///  • THE 10-SECOND STALL (defect 2). <c>UIStateCharacterSelected</c> reads <c>ValidMoves</c>:158-160 —
        ///    i.e. <c>ActorMoveAbility.GetTargetsData()</c> — every frame it is drawing (:142-148, :1594-1597),
        ///    and <c>MoveAbility.GetTargetsData</c>:170-173 is the exact line that logs "shouldn't be called
        ///    while X is executing abilities. This will invalidate the situation cache at wrong time." The
        ///    engine names its own damage. That call re-enters the path-request machinery WHILE the actor's
        ///    navigation is live: <c>GetTargetsDataInRange</c>:207-236 → <c>MoveAbilityTargets.CacheTargets</c>
        ///    :17-28, which <c>Clear()</c>s and <c>Calculate()</c>s a request AND mutates the STATIC
        ///    <c>NavigationSettings.Defaults</c> singleton (<c>TacticalPathRequest</c>:34-38 assigns that
        ///    reference; <c>NavigationSettings</c>:46 declares it <c>static readonly</c>), turning
        ///    <c>PathRequestPostProcess</c> off — read at <c>TacticalPathRequest</c>:64, where it clears
        ///    <c>PointInfos</c> and returns. On the ACTING peer this is structurally impossible, because its
        ///    view left for <c>UIStateWaiting</c> before <c>Activate</c> ever ran. On a mirror it ran every
        ///    frame for 403 frames while the actor covered 0.14 of 1.41 units and never terminated.
        ///
        /// THE RELEASE IS THE GAME'S OWN AND IT IS ONE CALL FOR EVERY ABILITY (postulate 3):
        /// <c>TacticalView.ResetViewState</c> (<c>TacticalView.cs</c>:262-268) switches the stack to
        /// <c>UIStateInitial</c>, which runs <c>ExitState</c> on whatever was holding the actor — free-aim,
        /// shoot, character-selected, or anything a mod adds later. No per-state code, no module poked by
        /// hand, no ability named anywhere in this seam.
        ///
        /// WHY <c>ResetViewState</c> AND NOT <c>ToWaitViewState</c>:270-276, which is what the local path
        /// uses: <c>UIStateWaiting</c> waits for the ability to FINISH, so on a mirror it would park this
        /// player behind another human's action — the precise thing postulate 2 forbids, and the stall this
        /// change exists to remove. <c>UIStateInitial</c> drops the stack and hands input straight back on the
        /// same frame (postulate 1).
        ///
        /// NARROW, BY THE ONLY PREDICATE THAT MATTERS: <c>view.SelectedActor</c> IS this actor. A peer whose
        /// UI is holding somebody else — or nobody — is not disturbed, because otherwise every mirrored order
        /// anywhere on the map would yank this player's selection, which is just a different way of making the
        /// game unplayable. Identity by <c>ReferenceEquals</c>, never Unity's <c>==</c> (L113).</summary>
        /// <summary>MAY THIS MIRRORED COMMAND BE PLAYED AT ALL — pure, so RailCheck L140 can execute it.
        ///
        /// A mirrored order carries its actor-shaped fields as shared KEYS, never as positions
        /// (<see cref="TacAbilityTargetCodec"/> writes <c>Actor</c>, <c>ShootTargetActor</c> and
        /// <c>DamageReceiver</c> through <c>TacticalActorKey</c>), and <c>Read</c> collects a sentence for every
        /// key that did not resolve on THIS peer. Either failure means the same thing: the target this peer
        /// would rebuild is not the target the host shot at. Replaying it anyway is worse than not replaying it,
        /// because <c>TacticalAbilityTarget.GetWorkingPosition</c>:175-192 does not fail loudly — it walks a
        /// nine-step fallback chain and, if every step is empty, returns <c>InvalidPosition</c> (NaN). The 2026
        /// -08-06 crash log shows that tail four times over
        /// (<c>Trying to get working position from TacticalAbilityTarget that has no valid one set</c>).
        ///
        /// NOT a bounds test on the position, deliberately: a free-aim shot's <c>PositionToApply</c> IS a
        /// far-off ray point by construction — the host's own log for the crashing shot carries
        /// <c>(-806.8, 66.4, -615.6)</c> on a map whose tiles are ±20, and the host played it natively without
        /// incident because the SAME target also names <c>Soldier_4</c>. Refusing on distance would refuse every
        /// legal free-aim shot in the game. What must hold is not "the position is sane" but "everything this
        /// target names, this peer can name too".</summary>
        internal static bool CommandMustBeRefused(bool actorResolved, int unresolvedFieldCount) =>
            !actorResolved || unresolvedFieldCount > 0;

        /// <summary>The release decision with the Unity half taken out, so RailCheck L139 can EXECUTE it rather
        /// than assert that a call to it exists (L137's lesson: a law that checks the call stayed green for four
        /// days while the thing it named was broken). Two axes and no more: is there a tactical view at all,
        /// and is the actor it is holding THIS one. Nothing about the ability, nothing about the order — a
        /// mirrored Move and a mirrored Overwatch reach this seam identically (postulate 3).
        ///
        /// The `!holds` half is not a formality, it is the postulate-2 half: a peer whose UI is holding someone
        /// else must be left alone, or a busy host turns every other player's selection over once per order.</summary>
        internal static bool LocalUiMustRelease(bool viewExists, bool viewHoldsThisActor) =>
            viewExists && viewHoldsThisActor;

        /// <summary>WHAT A SUPPRESSED CLICK IS LEFT STANDING IN once the host has answered it — the outcome
        /// L232 executes to exhaustion rather than asserting that some call exists (L137's lesson: a law that
        /// checks the call was green for four days while the thing it named was broken). TRUE is the bug: A9
        /// suppressed the native activation WITH its state switch, and nothing put the view back, so that
        /// player stands in <c>UIStateShoot</c> with no button that exits for the rest of the battle.
        ///
        /// THE ANSWERS A SUPPRESSED CLICK CAN GET, and there is no fifth:
        ///  • THIS PEER IS THE HOST — it answers its own order synchronously inside
        ///    <see cref="PublishClickedOrder"/>, so one release standing after <see cref="HandleActivate"/>
        ///    covers all four of its exits, the ACCEPTED one included. Before this existed the host had no
        ///    exit at all: <c>HandleInbound</c> and <see cref="ClientTick"/> both return early for the host,
        ///    so every release site in this file was a client path.
        ///  • A MIRROR CAME BACK (0x82) — <see cref="ApplyActivate"/> releases on every one of its exits.
        ///  • ONLY A SETTLE CAME BACK — a REFUSED order is mirrored by nothing at all, so the settle is the
        ///    whole answer and <see cref="ApplySettle"/> releases on this peer's own armed wait. Not on the
        ///    actor being busy: since A9 the acting peer plays nothing locally, so the actor is IDLE in
        ///    exactly the case that needs releasing, which is how this hole stayed open.
        ///  • NOTHING CAME BACK — <see cref="TickEchoWaits"/>' ceiling is the bound, and it releases BY THE
        ///    VIEW when the key no longer names an actor.
        ///
        /// The one TRUE row is the honest transient: a client whose order is still in flight and inside the
        /// ceiling. It is what stops this law passing vacuously.</summary>
        internal static bool SuppressedClickIsStranded(bool clickWasSuppressed, bool isHost, bool mirrorArrived,
                                                      bool settleArrived, bool ceilingExpired)
        {
            if (!clickWasSuppressed) return false;   // played natively; the game leaves its own state
            if (isHost) return false;                // released after HandleActivate, all four exits
            if (mirrorArrived) return false;         // ApplyActivate, all four exits
            if (settleArrived) return false;         // ApplySettle, on this peer's own armed wait
            return !ceilingExpired;                  // still in flight, and bounded
        }

        /// <summary>What the local UI is LEFT holding once a mirrored order for this actor has been through the
        /// seam — the outcome L139 asserts, as a pure function so it can be run to exhaustion. TRUE is the bug:
        /// the view is still holding an actor the engine is now driving, which is the state
        /// <c>MoveAbility.GetTargetsData</c>:170-173 declares invalid in its own words ("This will invalidate
        /// the situation cache at wrong time") and the state <c>UIModuleFreeFirstPersonShooting.Update</c>:132
        /// keeps drawing from.</summary>
        internal static bool LocalUiStillHoldsAfterMirror(bool viewHeldThisActor, bool releaseReached) =>
            viewHeldThisActor && !releaseReached;

        /// <summary>MAY THE MOVE-RANGE SWEEP RUN RIGHT NOW — pure, so RailCheck L168 executes the outcome.
        ///
        /// <see cref="ReleaseLocalUiHolding"/> above is a ONE-SHOT: it fires at the instant a mirrored order
        /// arrives, and only if <c>view.SelectedActor</c> happens to be that actor at that instant. A player
        /// who RE-SELECTS the busy soldier a second later walks straight back into
        /// <c>UIStateCharacterSelected</c> and nothing releases him again — which is why the owner's log holds
        /// 470 of the engine's own <c>GetTargetsData() shouldn't be called while … is executing abilities</c>
        /// over four and a half minutes on two host-owned actors, not one per order. This is the STANDING half:
        /// it holds for as long as the other peer's order runs, however many times the player re-selects.
        ///
        /// WHY THE POLL AND NOT THE SELECTION. Taking the player's selection away every time he clicks a busy
        /// teammate is hostile and it fights the state stack from inside a transition; withholding the SWEEP
        /// costs him only the move overlay for that soldier — which is the honest signal, since
        /// <see cref="TacticalActorDrive.RefuseLocalCommand"/> is going to refuse the move anyway (L146). It
        /// also fixes every OTHER caller of <c>GetTargetsData</c> at the same seam rather than this one UI
        /// state, and the one place all of them route through is the engine's own error line.
        ///
        /// THE HARM IS NOT THE LOG LINE. <c>MoveAbility.GetTargetsData</c>:170-173 logs and then carries ON
        /// with no early return, into <c>GetTargetsDataInRange</c>:207-236 → <c>MoveAbilityTargets.CacheTargets</c>
        /// :17-28, which <c>Clear()</c>s and <c>Calculate()</c>s a path request WHILE the actor's navigation is
        /// live AND assigns the STATIC <c>NavigationSettings.Defaults</c> singleton
        /// (<c>TacticalPathRequest</c>:34-38; <c>NavigationSettings</c>:46 declares it <c>static readonly</c>),
        /// turning <c>PathRequestPostProcess</c> off for everybody — read at <c>TacticalPathRequest</c>:64,
        /// where it clears <c>PointInfos</c> and returns. That is the mirrored soldier who covered 0.14 of 1.41
        /// units in 403 frames and had to be force-settled.
        ///
        /// <paramref name="engineSaysItIsExecuting"/> IS THE ENGINE'S OWN QUESTION, asked with the engine's own
        /// two exceptions (<c>PanicAbility</c>, <c>AIEvaluationAbility</c>) so this withholds exactly the calls
        /// the engine itself declares invalid and not one more. The third axis is what makes it OURS rather
        /// than a behaviour change: this peer's own order, or a solo game, keeps the sweep verbatim.</summary>
        internal static bool MovePollMustBeWithheld(bool inSharedBattle, bool engineSaysItIsExecuting,
                                                    bool drivenByAnotherPeer) =>
            inSharedBattle && engineSaysItIsExecuting && drivenByAnotherPeer;

        /// <summary>THE SAME QUESTION, ASKED FROM TWO SEAMS. The withhold (the prefix on
        /// <c>MoveAbility.GetTargetsData</c>) and the feed (the postfix on
        /// <c>MoveAbilitySceneViewElement.ValidMoves</c>) MUST agree frame for frame, or the overlay is fed an
        /// empty list on a frame the sweep ran, or left null on a frame it did not. One method, both callers —
        /// L310 asserts both route through it, because two copies of a three-axis predicate is exactly how they
        /// drift apart.</summary>
        internal static bool SweepIsWithheldFor(MoveAbility ability)
        {
            try
            {
                var actor = ability == null ? null : ability.TacticalActor;
                if (actor == null) return false;
                // The engine's OWN condition, with the engine's OWN two exceptions (GetTargetsData:165-172).
                var ignored = new TacticalAbility[]
                {
                    actor.GetAbility<PanicAbility>(),
                    actor.GetAbility<AIEvaluationAbility>()
                };
                var engine = NetworkEngine.Instance;
                return MovePollMustBeWithheld(engine != null && engine.IsActiveSession,
                                              actor.HasExecutingAbility(ignored),
                                              TacticalActorDrive.DrivenByAnotherPeer(actor));
            }
            catch { return false; }   // a presentation gate alters NOTHING when it cannot answer (P4c)
        }

        /// <summary>MUST THE MOVE OVERLAY BE HANDED AN EMPTY SWEEP INSTEAD OF THE ENGINE'S NULL — pure, so
        /// RailCheck L310 executes the outcome rather than asserting a call.
        ///
        /// THE WITHHELD EMPTY ARRAY WAS NOT ENOUGH, AND IT WAS THE PROXIMATE CAUSE. <c>MoveAbility</c>:26
        /// declares <c>HasValidTargets =&gt; GetTargetsData().Any()</c>, and <c>TacticalAbility</c>:465-468
        /// turns <c>!HasValidTargets</c> into <c>AbilityDisabledState.NoValidTarget</c> — so the instant
        /// <see cref="MovePollMustBeWithheld"/> answers "withhold", the ability reports NOT ENABLED, and
        /// <c>MoveAbilitySceneViewElement.ValidMoves</c>:69-79 answers <c>null</c> rather than a list.
        ///
        /// That property is re-read INSIDE a running coroutine, AFTER its yields:
        /// <c>UpdateMoveAreas</c>:237, :243, :253, :259, whose only guard (<c>HasValidMoves</c>, :223) runs once
        /// before the first yield. So a sweep that was legal when the draw started and withheld one frame later
        /// hands <c>Enumerable.Where</c> a null source — <c>ArgumentNullException: … Parameter name: source</c>
        /// on <c>&lt;UpdateMoveAreas&gt;d__36.MoveNext</c>, four times in one client's log (02:41:56, 02:45:15,
        /// 02:47:20, 02:48:41), each followed by Unity's <c>Broken coroutine call chain</c>, which ABORTS the
        /// chain — everything downstream of that coroutine stops for the rest of the battle.
        ///
        /// WHY FEED AND NOT STOP. Stopping the coroutine cannot fix the frame that throws: the withheld sweep is
        /// reached from <em>inside</em> the same <c>ValidMoves</c> read (:73 evaluates <c>IsEnabled()</c> first),
        /// so by the time our seam can see it, the getter is already committed to returning <c>null</c> to a
        /// caller two instructions away. Feeding an EMPTY list there is the whole fix and it needs no
        /// cancellation machinery: the loop then finds no moves (:240 <c>flag</c> false), <c>UpdateMoveArea</c>
        /// :268-271 yield-breaks on an empty list, and the coroutine ends normally having drawn nothing over the
        /// <c>ClearGroundMarkers</c> it already did at :227 — which IS the blank overlay the withhold promises.
        /// The native lifecycle needs no help either: every LATER restart (<c>UIStateCharacterSelected</c>:1165
        /// <c>StartDrawing</c> → <c>DrawTargetMarkers</c>, :214, :255, :757) re-enters <c>UpdateMoveAreas</c>:223,
        /// where <c>HasValidMoves</c> is now false and it yield-breaks BEFORE its first yield.
        ///
        /// IT IS NOT SPECIFIC TO A MIRRORED MOVE. The seam is the property, not the release: it holds for every
        /// path that can take this peer's UI off an actor mid-sweep — <c>ApplyActivate</c>'s release for ANY
        /// mirrored ability, the FORCED settle's release, and the standing case L168 exists for, where nothing
        /// released anything and the player simply re-selected a soldier another peer drives.
        ///
        /// <paramref name="engineAnsweredNull"/> keeps the engine's own nulls untouched: outside the withhold
        /// (a solo game, this peer's own order, an actor with no AP left) <c>null</c> is the game's answer and
        /// its callers were written for it.</summary>
        internal static bool MoveOverlayMustNotSeeNull(bool sweepWithheld, bool engineAnsweredNull) =>
            sweepWithheld && engineAnsweredNull;

        /// <summary>The HELD targeting states — the ones A9's suppressed state switch strands a player in, and
        /// the ones the game gives no button out of once the answer to his click never comes. Matched by walking
        /// the BASE chain, so the two the game already ships are covered by construction
        /// (<c>UIStateFreeCam : UIStateShoot</c>, <c>UIStateFirstPersonMultiTargetSelection : UIStateFreeCam</c>)
        /// along with anything a mod derives — the same shape, and for the same reason, as
        /// <c>TacticalUiRepaint.IsAbilityBarState</c>.
        ///
        /// <c>UIStateCharacterSelected</c> is deliberately ABSENT even though a rider can be clicked from it: it
        /// is also the ordinary browsing state, a player leaves it with one click, and clearing it for a peer
        /// that merely had a soldier selected is postulate 2 broken in the other direction.</summary>
        private static readonly HashSet<string> HeldTargetingStates = new HashSet<string>
        {
            "UIStateShoot", "UIStateAbilitySelected", "UIStateOverwatchAbilitySelected"
        };

        private static bool ViewIsHeldInTargeting(PhoenixPoint.Tactical.View.TacticalView view)
        {
            var state = view == null ? null : view.CurrentState;
            for (var t = state == null ? null : state.GetType();
                 t != null && t != typeof(PhoenixPoint.Tactical.View.TacticalViewState); t = t.BaseType)
                if (HeldTargetingStates.Contains(t.Name)) return true;
            return false;
        }

        internal static void ReleaseLocalUiHolding(TacticalActorBase actor, string why)
        {
            try
            {
                var tlc = Tlc();
                var view = tlc == null ? null : tlc.View;
                // AN ACTOR THIS PEER CANNOT NAME IS STILL AN ANSWER THAT FAILED. The two sites that reach here
                // with a null actor are the LOST ECHO and the mirror whose key did not resolve — both of them
                // are the failure in which the key stopped resolving, so bailing on the name left exactly the
                // peer that CLICKED standing in a state A9 gave no exit, for the rest of the battle. There the
                // release is decided BY THE VIEW instead, and only out of a HELD TARGETING state: that is the
                // state no button leaves, and it keeps a peer who is merely browsing untouched.
                bool holdsThisActor = view != null && actor != null && ReferenceEquals(view.SelectedActor, actor);
                if (!LocalUiMustRelease(view != null,
                                        holdsThisActor || (actor == null && ViewIsHeldInTargeting(view))))
                    return;
                var before = view.CurrentState;
                view.ResetViewState();
                if (ReferenceEquals(before, view.CurrentState)) return;   // already neutral — nothing to report
                Debug.Log("[Multiplayer][tac] released this peer's UI from " + SafeActorName(actor) + " (" + why +
                          ") — it was held in " + (before == null ? "<none>" : before.GetType().Name) +
                          ", the state the game's own activation path leaves before an ability runs.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][tac] could not release this peer's UI from " +
                                 SafeActorName(actor) + " (" + why + "): " + ex.Message + ". The order still " +
                                 "plays; a local aim HUD may keep drawing over an actor the engine now owns.");
            }
        }

        private static void ApplyActivate(int key, string guid, TacticalAbilityTarget target, bool fumbled,
                                          List<string> unresolved)
        {
            // THE ECHO THIS PEER WAS WAITING FOR (A9). Cleared BEFORE the refusal branch below, not after: a
            // record that arrives and cannot be played is still an answer, and leaving the wait armed would
            // freeze that soldier for the full ceiling on top of an already-named failure.
            NoteEchoArrived(key);
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
            // ONE VERDICT, TWO SENTENCES. The decision is pure so RailCheck L140 can execute it; the branch
            // below only chooses which of the two failures to name. Playing a command whose actor-shaped keys
            // did not resolve would aim it somewhere else entirely — TacticalAbilityTarget.GetWorkingPosition
            // :175-192 falls through its whole chain and ends at InvalidPosition with an engine error — so the
            // order is REFUSED here rather than half-played. The DAMAGE still arrives on 0x84 and is
            // authoritative; only this peer's animation is missing.
            if (CommandMustBeRefused(actor != null, unresolved == null ? 0 : unresolved.Count))
            {
                if (actor == null)
                    Debug.LogError("[Multiplayer][tac] host command for actor " + key + " cannot be played here — " +
                                   why + ". That soldier will stand still on this screen while it acts on the host's.");
                else
                    Debug.LogError("[Multiplayer][tac] host command for " + actor.name + " NOT played here — " +
                                   string.Join("; ", unresolved.ToArray()) + ". The host's damage still applies; " +
                                   "only the animation is missing on this screen.");
                // AND THE CLICKING PEER GETS ITS SCREEN BACK (law L231). Since A9 the native activation —
                // state switch included — is SUPPRESSED at the click, so the ONLY thing that ever leaves
                // UIStateShoot / UIStateAbilitySelected is this release. A failure branch that returns
                // without it leaves that player aiming at a soldier nothing will ever start, with no button
                // that exits: the order is gone, the wait is already cleared, and nothing else is coming.
                ReleaseLocalUiHolding(actor, "a mirrored order that cannot be played here");
                return;
            }
            // THE SAME RESOLUTION AS THE HOST'S, and it must be: a HOST-initiated pistol reload carries the
            // same non-unique guid, so first-match here would replay it as the rifle's on every client and
            // hand every mirroring peer a weapon switch the acting player never made.
            var ability = ResolveAbility(actor, guid);
            if (ability == null)
            {
                Debug.LogError("[Multiplayer][tac] " + actor.name + " has no ability with guid " + guid +
                               " on this peer — mod parity should have made that impossible (law 10). The order " +
                               "is dropped and this peer's battle has diverged.");
                ReleaseLocalUiHolding(actor, "a mirrored order naming an ability this peer does not have");
                return;
            }
            if (!IsRider(ability))
            {
                Debug.LogError("[Multiplayer][tac] the host mirrored '" + ability.AbilityDef.name + "', which is " +
                               "not a declared rider — the two peers disagree about what this arc carries.");
                ReleaseLocalUiHolding(actor, "a mirrored order this peer does not treat as a rider");
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
            ArmRelayedAim(key);   // law L104(j), same arm as the acting path's
            FillLiveTargetObject(ability, target);
            // BEFORE the engine takes this actor, not after — the local input path leaves its state BEFORE
            // Activate runs (TacticalViewState.ActivateAbility:270 vs :274-275 is the same order), and after
            // is too late for the frame the FPS HUD is already drawing.
            ReleaseLocalUiHolding(actor, "a mirrored " +
                                  (ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name));
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
            {
                Debug.LogError("[Multiplayer][tac] MIRROR QUEUED, not played — " + actor.name + " " +
                               (ability.AbilityDef == null ? "?" : ability.AbilityDef.name) + " is waiting behind " +
                               (actor.ExecutingAbilities.Count == 0
                                    ? "<nothing — it never started at all>"
                                    : actor.ExecutingAbilities[0].GetType().Name) +
                               " and will begin only when that ends (law L78).");
                // AND SOMETHING WATCHES THAT PROMISE (2026-08-05). "Will begin when that ends" was the whole
                // guarantee and nothing checked it: on 2026-08-05 a mirrored Overwatch queued behind an
                // IdleAbility that never ended and was simply gone, with this one line as its only trace and
                // the player's screen stuck behind it. A record now carries a deadline.
                WatchQueuedMirror(actor, ability);
            }
        }

        /// <summary>A mirrored order the engine ENQUEUED instead of playing, with the frames it has waited.
        /// Live ability refs, so it is cleared with the battle exactly like <c>_mirrorSkipsCameraWait</c>.</summary>
        private struct QueuedMirror
        {
            internal TacticalActor Actor;
            internal TacticalAbility Ability;
            internal string Name;
            internal int WaitedFrames;
        }

        private static readonly List<QueuedMirror> _queuedMirrors = new List<QueuedMirror>();

        // ~10 s at 60 fps — the same ceiling a held settle gets, and for the same reason: past it, "it is
        // still waiting its turn" has stopped being a credible description of a record that is never coming.
        // ponytail: DIAGNOSTIC ONLY, deliberately. The obvious "then force-play it" is wrong here — the
        // 2026-08-05 record was a DUPLICATE (the acting client shipped a second intent behind its own
        // EndTurn), and re-activating a duplicate Overwatch is the second shot from one actor that L83
        // exists to prevent. Unwedging the ACTOR is the recovery, and that already exists:
        // <see cref="ClientTick"/>'s SettleHoldCeilingFrames forces the settle, and <see cref="ApplySettle"/>
        // cancels the actor's action channel, which is what hands the soldier back to input.
        private const int QueuedMirrorCeilingFrames = 600;

        private static void WatchQueuedMirror(TacticalActor actor, TacticalAbility ability)
        {
            if (actor == null || ability == null) return;
            _queuedMirrors.Add(new QueuedMirror
            {
                Actor = actor, Ability = ability, WaitedFrames = 0,
                Name = ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name,
            });
        }

        /// <summary>Pumped from <see cref="ClientTick"/> — mirrors only ever queue on a receiving peer, and
        /// the host receives none. A record that starts is dropped silently; one that does not is named.</summary>
        private static void TickQueuedMirrors()
        {
            for (int i = _queuedMirrors.Count - 1; i >= 0; i--)
            {
                var q = _queuedMirrors[i];
                // Unity's == is the right operator here: this is a LIVENESS question, not the identity one
                // L113 reserves ReferenceEquals for. A destroyed actor's queued order is nobody's problem.
                if (q.Actor == null || q.Actor.ExecutingAbilities.Contains(q.Ability))
                {
                    _queuedMirrors.RemoveAt(i);
                    continue;
                }
                if (++q.WaitedFrames < QueuedMirrorCeilingFrames) { _queuedMirrors[i] = q; continue; }
                _queuedMirrors.RemoveAt(i);
                Debug.LogError("[Multiplayer][tac] a mirrored " + q.Name + " on " + q.Actor.name + " NEVER " +
                               "STARTED — it has been queued for " + (q.WaitedFrames / 60) + "s behind " +
                               (q.Actor.ExecutingAbilities.Count == 0
                                    ? "<nothing, so it was dropped rather than queued>"
                                    : q.Actor.ExecutingAbilities[0].GetType().Name) +
                               ", which means this peer never played an action every other peer did (law " +
                               "L78). It is NOT force-played: it may be a duplicate, and replaying one is a " +
                               "second shot from one actor. The actor is the thing to unwedge — the settle " +
                               "ceiling in ClientTick does that, and this line is the evidence that it had to.");
            }
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

        /// <summary>THE WHOLE HOLD DECISION, PURE — and it is the rail's only guarantee that a MIRRORED
        /// ability always reaches a terminal state.
        ///
        /// A mirrored ability can hang on a receiving peer for reasons that have nothing to do with the order:
        /// <c>EnterVehicleCrt</c>:104 and <c>ExitVehicleCrt</c>:75 both park on
        /// <c>AnimEvents.WaitForEvent("OpenedDoor")</c> with no ceiling of their own, and a broken coroutine
        /// chain (the 2026-08-01 bash NRE) never resumes either. <c>PlayingAction.CompleteAction</c> then never
        /// runs, <c>ClearPlayingAction</c> never runs, the actor stays in <c>ExecutingAbilities</c> forever and
        /// <see cref="Validate"/>'s actorBusy arm refuses every later order for that soldier — a soldier
        /// bricked for the rest of the battle, per ability, with no way back.
        ///
        /// It is answered ONCE, HERE, for every ability rather than by a timeout bolted onto each one: the
        /// host's settle is the closer for EVERY rider (<see cref="OnAbilityActionEnded"/>) and the turn-edge
        /// sweep re-issues one for every keyed live actor, so a peer holding a settle for a busy actor is the
        /// one place that sees "this ability is not slow, it is stuck" for all of them. Past the ceiling the
        /// settle is applied anyway, and <see cref="ApplySettle"/> ends the ability through the game's own
        /// teardown (<c>ActionComponent.CancelActions</c> → <c>ClearPlayingAction</c> → the same exit a
        /// completed action takes) and then reconciles the actor's status set to the host's, so the terminal
        /// state is the host's state and not whatever half the torn coroutine had reached.
        ///
        /// The frame count is a fallback and it CONVERGES rather than diverges — it applies the host's own
        /// position, AP, WP and status set, which is the definition of not diverging. Pure so RailCheck L133
        /// can execute the hold to exhaustion and assert it terminates.</summary>
        internal static bool SettleMustBeForced(bool actorBusy, bool alreadyForced, int waitedFrames)
        {
            if (alreadyForced) return true;
            if (!actorBusy) return true;
            return waitedFrames >= SettleHoldCeilingFrames;
        }

        private static void QueueSettle(int key, Vector3 pos, float ap, float wp, bool forced,
                                        List<string> statuses, List<string> traits, string selected,
                                        Dictionary<string, int> uses)
        {
            // THE DISARM IS NOT HERE, AND THAT WAS THE LOCK-UP. A settle is the other half of "the host has
            // answered" — a REFUSED order produces no 0x82 mirror at all — but disarming the echo wait at
            // ARRIVAL removed the 12 s ceiling while the release still sat downstream in ApplySettle behind a
            // guard that A9 made permanently false. Wait cleared, no mirror, no release: the clicking peer
            // stood in its targeting state for the rest of the battle. The disarm now lives WHERE THE RELEASE
            // IS, so the two cannot come apart again.
            _pending[key] = new PendingSettle { Pos = pos, Ap = ap, Wp = wp, WaitedFrames = 0, Forced = forced,
                                                Statuses = statuses, Traits = traits, Selected = selected,
                                                Uses = uses, Epoch = TacticalDamageSync.StatEpoch };
        }

        /// <summary>The standing settle applier (driven from <c>SyncEngine.Tick</c>, client-only inside). A
        /// STANDING condition, not a one-shot at arrival: the settle for a move typically lands while this peer
        /// is still walking the same soldier, and snapping then is erased by that walk's own navigation without
        /// a single log line.</summary>
        internal static void ClientTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
            // BEFORE the settle guard, not inside it: a queued mirror and a pending settle are independent,
            // and gating the watchdog on _pending would make it run only while a settle happened to be in
            // flight — which is precisely when the bug it watches for does NOT need reporting.
            TickQueuedMirrors();
            // Same reasoning, same place: an echo wait is independent of both, and it is the one that leaves a
            // soldier unclickable while it runs (A9).
            TickEchoWaits();
            // BEFORE the settle guard, and before every early return below it: the whole point of the hold is
            // that it is drained on the frames where this peer has NOTHING pending and no battle yet.
            DrainHeldRecords();
            if (_pending.Count == 0) return;
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
                    if (SettleMustBeForced(true, false, held.WaitedFrames))
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
                try { ApplySettle(kv.Key, actor, kv.Value); }
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
        private static void ApplySettle(int key, TacticalActor actor, PendingSettle s)
        {
            using (SyncApplyScope.Enter())
            {
                // THE HOST HAS ANSWERED, AND THE ANSWER MAY BE "NO" (A9). A refused order is mirrored by
                // NOTHING, so this settle is the only thing that ever comes back for it — and the release below
                // could not serve it, because it hangs off HasExecutingAbility() and since A9 the acting peer
                // plays nothing locally, so its actor is IDLE in exactly the case that needs releasing. That
                // guard's premise died with the speculative play; this one asks the question that survived it.
                //
                // CONDITIONED ON THIS PEER'S OWN WAIT and on nothing else. A settle for an actor reaches every
                // client, so releasing on the settle alone would clear the screen of a bystander who merely has
                // that soldier selected — the postulate-2 half LocalUiMustRelease exists for. An armed
                // _awaitingEcho entry is the one fact that says THIS peer is the one standing in a targeting
                // state its click never left.
                bool wasAwaitingHere = key != 0 && _awaitingEcho.ContainsKey(key);
                NoteEchoArrived(key);
                if (wasAwaitingHere)
                    ReleaseLocalUiHolding(actor, "the host's settle answering an order it never mirrored back");
                if (actor.HasExecutingAbility())
                {
                    // AND THE CANCEL MAY NOT LAND UNDER A LIVE AIM HUD. The cancel below is a TEAR (see the
                    // paragraph further down), and it tears the CAMERA too: a local free-aim state holding
                    // this actor keeps its module updating afterwards over a camera behavior that is no longer
                    // a FirstPersonCamera — UIModuleFreeFirstPersonShooting.UpdateAccuracyIndicator:316-317
                    // then NREs once per frame forever, which is the 797-NRE storm that ended in a native
                    // crash. Same one call, same reason, same seam as the mirrored activation: let the local
                    // UI go FIRST, through the game's own ExitState, and there is nothing left to tear.
                    // Only in this branch — a settle for an actor that is executing NOTHING tears nothing, so
                    // it must not disturb a player who simply has that soldier selected.
                    ReleaseLocalUiHolding(actor, s.Forced ? "a FORCED settle cancelling its actions"
                                                          : "a settle cancelling its actions");
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
                // AND THE TERMINAL STATE IS A DEFINED ONE. The cancel above is the game's own teardown and it
                // is also a TEAR: a mirrored ability that never ended is killed wherever it happens to be
                // standing (PlayingAction.SetState(Cancelled):56-61 stops the updateable outright), so its
                // half of the work is done and the other half never runs — an EnterVehicleCrt torn between
                // its nav-obstacle disable and its ApplyMountedStatus leaves an actor that is neither on foot
                // nor aboard. Ending the ability was never the whole answer; ending it in the state the HOST
                // is in is. The status set arrives on this same settle, so the repair lands in the same apply
                // as the tear that needed it, and it is the same line that heals a status lost to a dropped
                // order or a refused one.
                TacticalStatusSet.Reconcile(actor, Tlc(), s.Statuses, "the host's settle");
                // AND THE TURN ITSELF. The same argument one line up, for the field the tear leaves in the
                // worst state of all: the cancel above ends the mirrored ability wherever it stands, so the
                // ApplyAbilityTraits its native run would have done never happens — and on the REFUSED path
                // there was no host run to copy in the first place. Either way "this soldier has ended his
                // turn" was decided locally and permanently. The host's list is the answer.
                ApplyTraits(actor, s.Traits);
                // AND THE WEAPON IN HIS HANDS, by the same argument again. It rode only as an event, and an
                // event that arrives before this peer has a map — or is raised before this peer has a key for
                // the actor — is simply gone, in whichever direction it was travelling. Here it is re-asserted.
                ReconcileSelection(actor, s.Selected);
                // AND WHAT HE HAS ALREADY SPENT THIS TURN, by the same argument once more. A REFUSED order is
                // the case that needs it: this peer played it speculatively and Activate:1092 charged the
                // turn's use, while the host — which refused BEFORE activating — never did. The counter is
                // def-keyed and cleared only at the turn edge, so without this the ability stays dead on
                // every weapon for the rest of the turn with no way for the player to clear it.
                ReconcileAbilityUses(actor, s.Uses);
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
    /// A9's ONE SEAM (law L230): <c>TacticalViewState.ActivateAbility</c>:259 — the single method every
    /// PLAYER CLICK passes through, and the only one that can tell a click apart from the engine's own
    /// activations without enumerating abilities. Blocking here and not at <c>TacticalAbility.Activate</c> is
    /// forced, not preferred: <c>Activate</c> is VIRTUAL, so a prefix that skips the base body still lets
    /// <c>ShootAbility.Activate</c>:165-174 run its own <c>PlayAction(Shoot)</c> — the same reason
    /// <see cref="AutonomousReactionExecuteGate"/> sits on the non-virtual <c>Execute</c> wrappers. This is the
    /// caller, so returning false suppresses the whole activation.
    ///
    /// It covers every clicked action at once because the game funnels them all here: <c>UIStateShoot</c>
    /// (the one override in the game, and it calls base), <c>UIStateFreeCam</c>:464 free-aim,
    /// <c>UIStateFirstPersonMultiTargetSelection</c>, <c>UIStateOverwatchAbilitySelected</c>,
    /// <c>UIStateAbilitySelected</c> (every def-driven ability: grenades, cones, throws, alien specials) and
    /// <c>UIStateCharacterSelected</c> (melee, reload, move).
    ///
    /// SUPPRESSING THE STATE SWITCH TOO IS DELIBERATE. The native body also leaves the targeting state for
    /// <c>UIStateWaiting</c>; letting the view park for an ability that has not started would show a wait for
    /// nothing. The release happens on the mirror instead, where it already lives —
    /// <see cref="TacticalCommandSync.ReleaseLocalUiHolding"/> runs from <c>ApplyActivate</c> BEFORE the
    /// engine takes the actor, and a second click in the meantime is dropped by the echo gate itself.
    ///
    /// <c>Prepare</c> rather than a null <c>TargetMethod</c>: <c>AccessTools.Method</c> does EXACT parameter
    /// matching, a returned null aborts <c>PatchAll</c> and kills every later patch in the pass (RailCheck
    /// L23), and a silent skip is this repo's dominant bug class. It says so and stands down.
    /// </summary>
    [HarmonyPatch]
    internal static class ClickedOrderWaitsForTheEcho
    {
        internal static readonly MethodBase Seam = AccessTools.Method(
            typeof(PhoenixPoint.Tactical.View.TacticalViewState), "ActivateAbility",
            new[] { typeof(TacticalAbility), typeof(TacticalAbilityTarget), typeof(Base.UI.StateStackAction),
                    typeof(Func<TacticalAbility, bool>) });

        private static bool Prepare()
        {
            if (Seam != null) return true;
            Debug.LogError("[Multiplayer][tac] ECHO SEAM NOT BOUND — TacticalViewState.ActivateAbility" +
                           "(TacticalAbility, TacticalAbilityTarget, StateStackAction, Func<TacticalAbility,bool>) " +
                           "did not resolve, so every clicked order will play LOCALLY at the click again and " +
                           "attack animations will start at a different moment on every peer (law L230).");
            return false;
        }

        private static MethodBase TargetMethod() => Seam;

        private static bool Prefix(TacticalAbility ability, TacticalAbilityTarget target)
            => !TacticalCommandSync.PublishClickedOrder(ability, target);
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
    /// THE AIM BRANCH, ONE LAYER BELOW THE CAMERA WAIT (law L104(j), 2026-08-05).
    ///
    /// MEASURED, not argued: order skew between peers is negligible (actor→host +18 ms, →peer2 +28 ms), and
    /// there is no confirmation round trip to blame — <c>TacticalViewState.ActivateAbility</c>:270 calls
    /// <c>ability.Activate</c> synchronously, and <c>HOST mission END outcome=Won</c> reached the KILLER
    /// FIRST (+15 ms) against the non-killer's +37 ms. What differs is a 495-502 ms block (778-781 ms on a
    /// heavy weapon) that mirror peers play and the acting peer skips, or the reverse: "in some windows
    /// someone fires half a second earlier", and run-then-shoot makes observers fire INSTANTLY while the
    /// acting peer plays the full aim.
    ///
    /// <c>TacticalLevelController</c>:1645 gates that entire block (:1647-1678) on
    /// <c>TacticalActor.CurrentlyAiming</c> — which is
    /// <c>Animator.GetInteger("TravelType") == 7 || Animator.GetInteger("ShootSegmentType") == 5</c>
    /// (TacticalActor.cs:228). A LOCAL ANIMATOR INTEGER. It is not in the order, it cannot be, and two peers
    /// answering it differently take opposite sides of a half-second branch for the same shot.
    /// <c>TacticalAimPoseSync</c>:361 makes that certain rather than likely — it defers ANY stance message,
    /// including the CLEAR, while <c>nav.IsNavigating</c>, so a mirror still walking keeps
    /// <c>SetAimParams(AimLoop)</c> (:385) and fires with no wind-up at all, while :346 does not exempt the
    /// emitter and the acting peer's own clear writes <c>SetNullNavParams</c> (:365) onto its own soldier,
    /// which then plays the FULL wind-up.
    ///
    /// SO THE FIX IS NOT IN THE STANCE TABLE. A table that defers while walking can never be authoritative at
    /// fire time; papering over it there would move the race, not end it. Instead the branch is forced to ONE
    /// answer under a relayed activation — exactly the shape <see cref="MirroredPlayMatchesHostPacing"/>
    /// already uses one layer up, and armed at the same two points as the L104 camera token. Universal, zero
    /// wire bytes, and it demotes the aim table back to what it is: cosmetic.
    ///
    /// THE ACCEPTED COST, stated rather than hidden: forcing FALSE means every peer PLAYS the wind-up, so a
    /// player who was already holding aim on a target loses vanilla's instant follow-up shot. That is the
    /// price of one answer; forcing TRUE would be the opposite bug (nobody ever aims) and is worse.
    ///
    /// MISSION STATISTICS ARE THE SAME RULE, NOT A SECOND PATCH. The 2-3 s late summary on the peer that made
    /// the killing blow is its own presentation queue being longer — its shot plus the kill cinematic that
    /// <see cref="TacticalCameraPolicy"/> suppresses on watchers — and the native summary waits on
    /// <c>TacticalView.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate</c>, already named by L104(f).
    /// Shortening the shot to one shared length shortens that queue on every peer alike.
    /// </summary>
    [HarmonyPatch(typeof(TacticalActor), nameof(TacticalActor.CurrentlyAiming), MethodType.Getter)]
    internal static class RelayedAimBranchIsTheSameOnEveryPeer
    {
        private static void Postfix(TacticalActor __instance, ref bool __result)
        {
            if (!__result) return;                                    // already the forced answer
            if (TacticalCommandSync.UnderRelayedAim(__instance)) __result = false;
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

    /// <summary>THE STANDING HALF of the local-UI release (<see cref="TacticalCommandSync.MovePollMustBeWithheld"/>
    /// carries the reasoning). ONE seam for every caller, sited on the engine's own error line rather than on
    /// the <c>UIStateCharacterSelected.ValidMoves</c>:153-160 that happened to be the reported one — the game
    /// already answers <c>null</c> there whenever the move ability is not enabled, so an empty sweep is a value
    /// its callers were always written to receive.</summary>
    [HarmonyPatch(typeof(MoveAbility), nameof(MoveAbility.GetTargetsData))]
    internal static class MoveRangeIsNotSweptWhileAnotherPeerDrivesTheActor
    {
        private static readonly MoveAbilityTargetData[] Nothing = new MoveAbilityTargetData[0];

        // ONE LINE PER EPISODE, not per frame: the withholding lasts as long as the other peer's order and the
        // poll is once a frame, so an undeduplicated record would bury the log it is meant to explain. Removed
        // on the first sweep that runs again, which is the order ending — so a second order logs a second line.
        private static readonly HashSet<TacticalActorBase> _withheld = new HashSet<TacticalActorBase>();

        private static bool Prefix(MoveAbility __instance, ref IEnumerable<MoveAbilityTargetData> __result)
        {
            TacticalActor actor;
            try { actor = __instance == null ? null : __instance.TacticalActor; }
            catch { return true; }   // a presentation gate alters NOTHING when it cannot answer (P4c)
            if (actor == null) return true;
            if (!TacticalCommandSync.SweepIsWithheldFor(__instance))
            {
                _withheld.Remove(actor);
                return true;
            }
            if (_withheld.Add(actor))
                Debug.Log("[Multiplayer][tac] move-range sweep WITHHELD for " + SafeName(actor) +
                          " while another peer's order drives him — the game's own GetTargetsData says it " +
                          "must not run now (it invalidates the situation cache and turns the static " +
                          "NavigationSettings.PathRequestPostProcess off mid-navigation), and it says so " +
                          "without stopping. His move overlay is blank until that order ends; every other " +
                          "soldier is unaffected and nothing else about this peer's screen changes.");
            __result = Nothing;
            return false;
        }

        private static string SafeName(TacticalActorBase actor)
        {
            try { return actor.name; } catch { return "<an actor>"; }
        }
    }

    /// <summary>THE OTHER HALF OF THE WITHHOLD, and the half that keeps Unity's coroutine chain alive
    /// (<see cref="TacticalCommandSync.MoveOverlayMustNotSeeNull"/> carries the full reasoning).
    ///
    /// The sibling above answers an EMPTY sweep, which the engine immediately reads as
    /// <c>AbilityDisabledState.NoValidTarget</c> (<c>MoveAbility</c>:26 → <c>TacticalAbility</c>:465-468) — so
    /// <c>ValidMoves</c>:69-79 starts answering <c>null</c> to a coroutine that already passed its only guard
    /// (<c>UpdateMoveAreas</c>:223) and re-reads the property after every yield (:237, :243, :253, :259). One
    /// null there is an <c>ArgumentNullException</c> out of <c>Enumerable.Where</c> and Unity's
    /// <c>Broken coroutine call chain</c>, which aborts the chain for the rest of the battle. This postfix hands
    /// that read an EMPTY list instead, so the coroutine finishes normally having drawn nothing.
    ///
    /// A POSTFIX ON THE GETTER, not a rewrite of it: the engine's own null is left alone everywhere else, and
    /// the getter is the one place ALL of those reads route through — so this covers every path that takes this
    /// peer's UI off an actor mid-sweep (any mirrored ability's release, the forced settle's release, and the
    /// standing L168 case where the player merely re-selected), not the mirrored Move that was reported.</summary>
    [HarmonyPatch(typeof(MoveAbilitySceneViewElement), "ValidMoves", MethodType.Getter)]
    internal static class TheMoveOverlayIsNeverHandedANullSweep
    {
        // NEVER a fabricated target: an empty list draws nothing, a populated one would paint move tiles the
        // player cannot use and TacticalActorDrive.RefuseLocalCommand would refuse (L146). L310 arm (d).
        private static readonly List<MoveAbilityTargetData> Empty = new List<MoveAbilityTargetData>();

        // ONE LINE PER EPISODE, like the withhold's own: the getter is read several times a frame and the
        // withholding lasts as long as the other peer's order. Cleared on the first non-null answer, which is
        // that order ending — so a second order logs a second line.
        private static readonly HashSet<TacticalActor> _fed = new HashSet<TacticalActor>();

        private static void Postfix(MoveAbilitySceneViewElement __instance,
                                    ref List<MoveAbilityTargetData> __result)
        {
            MoveAbility ability;
            TacticalActor actor;
            try
            {
                ability = __instance == null ? null : __instance.GetActorMoveAbility;
                actor = ability == null ? null : ability.TacticalActor;
            }
            catch { return; }   // a presentation gate alters NOTHING when it cannot answer (P4c)
            if (actor == null) return;
            if (!TacticalCommandSync.MoveOverlayMustNotSeeNull(TacticalCommandSync.SweepIsWithheldFor(ability),
                                                               __result == null))
            {
                if (__result != null) _fed.Remove(actor);
                return;
            }
            __result = Empty;
            if (_fed.Add(actor))
                Debug.Log("[Multiplayer][tac] move overlay fed an EMPTY sweep for " + SafeName(actor) +
                          " instead of the null the game answers while his move ability is disabled — the " +
                          "withheld sweep is what disables it (MoveAbility:26 -> TacticalAbility:465-468), and " +
                          "MoveAbilitySceneViewElement.UpdateMoveAreas re-reads ValidMoves after its yields " +
                          "(:237, :243, :253, :259) with only one guard at :223, so the null landed in " +
                          "Enumerable.Where and Unity aborted the whole coroutine chain. His overlay draws " +
                          "nothing until that order ends; nothing else on this screen changes.");
        }

        private static string SafeName(TacticalActorBase actor)
        {
            try { return actor.name; } catch { return "<an actor>"; }
        }
    }
}
