using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Core;
using Base.Entities;
using Base.Levels.SceneObjectIds;
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
    /// THE ANSWER IS A SECOND, *DERIVED* KEY, AND IT IS DERIVED FROM THE ACTOR ALONE — never from the SET the
    /// actor is in. That distinction is the whole of this file's history:
    ///
    /// UNTIL 2026-08-12 THE DERIVED KEY WAS AN ORDINAL over the key-less actors, sorted by battle-start
    /// position. Ordinals are fragile BY CONSTRUCTION: the key of actor #40 is a function of how many actors
    /// the peer COUNTED before it, so one extra or missing actor shifts every key after it and each one then
    /// resolves to a different object with no failure anywhere. Live 2026-08-11 that is exactly what happened
    /// — the host built keys for 118 actors, the client (13 s later, post-reveal) for 119, and the host's key
    /// -103 named a <c>TacAIWaypoint</c> on the client. Downstream: dropped settles, "ROSTER DIVERGENCE …
    /// SMALLER battle" x51, holes in the resolved-attack stream, eight forced 124-actor resnapshots, missing
    /// loot. A 68-vs-68 mission on the same build had zero errors. No tie-breaking rescues an ordinal; only
    /// not being one does.
    ///
    /// AND THE GAME ALREADY SHIPS THE IDENTITY, which this file's own comment used to deny ("the
    /// <c>SceneObjectId</c> on <c>ActorComponent</c> is a scene-authoring guid, not a spawn identity" — WRONG,
    /// and the origin of the bug: every actor that shifted those ordinals was scene-AUTHORED).
    /// <c>ActorComponent.GetActorCreateData</c>:316-333 asserts a hard partition on every actor the game
    /// saves — <c>IsSpawned</c> XOR <c>IdInScene.IsValid</c>, each side <c>Debug.LogError</c>ing the other —
    /// so the two branches below are exhaustive:
    ///   • NOT SPAWNED ⇒ it is placed in the scene and carries a valid <c>SceneObjectId</c>, a serialized
    ///     guid string (<c>SceneObjectId</c>:16-19) written at save (<c>:321</c>) and resolved back at load
    ///     (<c>:348</c>). Waypoints, crates, objectives, consoles, key structures and deploy/exit zones are
    ///     ALL of this kind — the very actors that shifted the ordinals. Its key is a hash of that guid, so
    ///     it depends on NOTHING any peer counted: an actor one peer has not revealed yet shifts no other
    ///     key, and the key is mintable without the receiver ever having seen the actor.
    ///   • SPAWNED at battle start ⇒ it came out of the SHARED mid-tactical save blob (A1), which is also
    ///     what makes it addressable: its <c>Name</c> round-trips through the save
    ///     (<c>ActorComponent</c>:322/343) and its position is restored from the same bytes on every peer
    ///     (<c>ProcessInstanceData</c>:390-393). Its key is a hash of exactly those — again a function of the
    ///     ACTOR, not of the roster, so a peer that sees one alien fewer keeps every other alien's key.
    /// STAMPING A SYNTHETIC <c>GeoUnitId</c> instead (which WOULD ride the save for free) stays refused:
    /// <c>GeoMission</c>:788-795 skips exactly <c>GeoTacUnitId.None</c> and <c>Debug.LogError</c>s any other
    /// id it cannot find in the geoscape, and <c>PhoenixStatisticsManager</c>:814-878 keys soldier stats off
    /// the same field. A fake id turns every alien into a mission-completion error.
    ///
    /// Derived keys are NEGATIVE so they can never collide with a real <c>GeoTacUnitId</c> (the geoscape
    /// mints those from a positive counter), and 0 still means "no shared identity". A hash CAN collide;
    /// what it may not do is collide QUIETLY, so a second actor landing on a live key un-registers BOTH and
    /// <see cref="Refuse"/>s the number — every record naming it then says so instead of reaching a lookalike.
    ///
    /// A4 CLOSES THE CEILING: an actor that ENTERS play after the build has no shared antecedent at all — it
    /// is in no save blob and in no scene — so its key is HOST-ASSIGNED (<see cref="AssignHostKey"/>) and
    /// ships WITH the spawn event for every other peer to <see cref="Adopt"/> verbatim. That counter is not
    /// an ordinal in the fragile sense: its value is TRANSMITTED, never independently re-derived, so nothing
    /// a peer counts can move it. It lives in -1..-(<see cref="ContentKeyBase"/>-1), a range the content
    /// hashes are below by construction, so the two schemes cannot meet.
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

        /// <summary>The subset of <see cref="_refused"/> this peer refused for its OWN local reason (two of
        /// its battle-start actors hashing to one key). Same ledger, different CAUSE — see
        /// <see cref="LocalKeyClashes"/>.</summary>
        private static readonly HashSet<int> _localClash = new HashSet<int>();

        /// <summary>The next free HOST-ASSIGNED key (A4 only — the battle-start build derives, it never
        /// counts). Its value rides the spawn record, so it is a host-local unique number, not a quantity any
        /// other peer re-derives.</summary>
        private static int _nextDerived = -1;

        /// <summary>Whether <see cref="BuildBattleKeys"/> has run for this battle. A command that needs a
        /// derived key before the map exists is refused with that reason, never resolved by accident.</summary>
        internal static bool Built => _built;

        internal static void Reset()
        {
            _derived.Clear();
            _byDerived.Clear();
            _refused.Clear();
            _localClash.Clear();
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
        internal static string RosterDivergence() => Join(null);

        /// <summary>The refusals this peer caused ENTIRELY BY ITSELF — two of its own battle-start actors
        /// hashing to one derived key. They belong in the ledger (the key still names nobody), but they are
        /// NOT a smaller roster and NOT a lost packet: both actors are standing right here. Reported apart
        /// from <see cref="HostSideRefusals"/> because a cause named wrongly sends the next reader hunting a
        /// packet that never existed — the same rule <see cref="_refused"/>'s own header states.</summary>
        internal static string LocalKeyClashes() => Join(true);

        /// <summary>The refusals that really are "the host has an actor this peer cannot rebuild".</summary>
        internal static string HostSideRefusals() => Join(false);

        private static string Join(bool? local)
        {
            var parts = new List<string>(_refused.Count);
            foreach (var kv in _refused)
                if (local == null || _localClash.Contains(kv.Key) == local.Value)
                    parts.Add("key " + kv.Key + " — " + kv.Value);
            if (parts.Count == 0) return null;
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
            int keyed = 0;
            foreach (var a in tlc.Map.GetActors<TacticalActorBase>())
            {
                // A4: an actor that already carries an ADOPTED host key keeps it — the host handed that
                // number out, and re-deriving over it would answer a question that was already answered.
                if ((int)a.GeoUnitId != 0 || _derived.ContainsKey(a)) continue;
                int key = ContentKeyOf(a);
                if (key == 0) continue;                       // ContentKeyOf already said why, out loud
                // A number two actors already proved ambiguous is never handed to a third. Without this the
                // clash pair is dropped from the map and the NEXT actor to hash onto it takes the key over.
                if (_refused.ContainsKey(key)) continue;
                TacticalActorBase clash;
                if (_byDerived.TryGetValue(key, out clash) && !ReferenceEquals(clash, a))
                {
                    // BOTH are dropped, not just the newcomer: a hash collision means the number names two
                    // objects, so answering with EITHER is a wrong hit. The key is refused instead, which is
                    // what makes every later record naming it print a cause rather than reach a lookalike.
                    Debug.LogError("[Multiplayer][tac] two battle-start actors hash to the SAME derived key " +
                                   key + " — neither one may answer for it, so the key is REFUSED here and " +
                                   "every command, hit and settle naming it says so instead of reaching " +
                                   "whichever the map hands back first.");
                    _derived.Remove(clash);
                    _byDerived.Remove(key);
                    _localClash.Add(key);
                    Refuse(key, "two battle-start actors on this peer hash to derived key " + key +
                                ", so it names neither of them");
                    continue;
                }
                _derived[a] = key;
                _byDerived[key] = a;
                keyed++;
            }
            _built = true;
            Debug.Log("[Multiplayer][tac] derived battle keys for " + keyed + " actor(s) the geoscape never " +
                      "named (scene guid where the actor is placed, name+position where it was spawned into " +
                      "the shared entry save — never an ordinal over the roster).");
            // THE ORDERING SEAM IS THIS FLAG, AND THE FLUSH READS IT FROM THE TICK — NOT FROM HERE.
            // Calling TacticalCommandSync.FlushPendedSelections() inline was the obvious wiring and it broke
            // L19: this method's only caller is TacNewTurnHook.Postfix, a postfix on the MODEL method
            // TacMission.OnNewTurn, so an intent emitted anywhere below it is a result-ship by the law's IL
            // walk. The flush therefore rides SyncEngineStub.Tick, which is where the rail already emits
            // (the turn-epoch ceiling resnapshot does the same), and it costs one static bool read per frame.
            // One frame of extra latency on a wait that was already 0.2 s long.
        }

        /// <summary>THE DERIVATION, in its OWN non-inlined method, and the two branches are the game's own
        /// partition (<c>ActorComponent</c>:325-331 logs an error for either one being false):
        ///
        ///   • PLACED IN THE SCENE ⇒ its <c>SceneObjectId</c>, read with the game's OWN call and the game's own
        ///     arguments — <c>SceneObjectIdsComponent.GetIdInScene(gameObject, gameObject.scene, true)</c>, i.e.
        ///     <c>ActorComponent</c>:321 verbatim. Asking in the ACTOR'S OWN scene is the load-bearing half:
        ///     map generation reparents each parcel's ids component into the plot's and destroys the parcel one
        ///     (<c>MapPlot</c>:230-243), so the level's scene and the active scene both answer wrong — that is
        ///     the very miss <see cref="TacticalDestruction"/>'s header dissects. The save path proves this
        ///     spelling works: if it did not, every scene actor would trip :331 on every save.
        ///   • SPAWNED ⇒ name + battle-start position, both restored from the shared entry save on every peer.
        ///
        /// A missing scene id FALLS THROUGH to the spawned shape rather than to 0: it is still content-derived
        /// and still count-independent, and an addressable-with-a-warning actor beats an unaddressable one.
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
        private static int ContentKeyOf(TacticalActorBase a)
        {
            var go = a.gameObject;
            if (!a.IsSpawned)
            {
                // ponytail: the game's own static, which re-runs FindGameObjectsWithTag per actor. ~120 tag
                // scans, once per battle, at a turn edge. Cache per scene only if a profile ever names it.
                var id = SceneObjectIdsComponent.GetIdInScene(go, go.scene, true);
                if (id.IsValid) return ContentKey("scene:" + id.GuidString);
                Debug.LogWarning("[Multiplayer][tac] " + SafeName(a) + " is placed in the scene and has NO scene " +
                                 "id — the shape ActorComponent:329-331 calls an error on every save. It is keyed " +
                                 "by name+position instead, which is still independent of what any peer counted " +
                                 "but moves if this actor moves before the other peer builds its keys.");
            }
            return ContentKey("spawn:" + SafeName(a) + "@" + Mathf.RoundToInt(a.Pos.x * 10f) + "," +
                              Mathf.RoundToInt(a.Pos.y * 10f) + "," + Mathf.RoundToInt(a.Pos.z * 10f));
        }

        /// <summary>The rule itself, as a PURE function of the string it hashes — so a law can EXECUTE it
        /// rather than read it. The ECalls (<c>name</c>, <c>Pos</c>, the scene lookup) stay in the caller.
        ///
        /// FNV-1a and not <c>string.GetHashCode</c>: the framework's string hash is explicitly an
        /// implementation detail and can be randomised per process, so it is not a value two peers may
        /// compare. 30 bits of it are kept and pushed below <see cref="ContentKeyBase"/>, so a content key is
        /// always negative (never a real <c>GeoTacUnitId</c>), never 0 ("no shared identity") and never
        /// inside the host-assigned range.</summary>
        internal static int ContentKey(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s ?? "") { h ^= (uint)c & 0xFFu; h *= 16777619u; h ^= (uint)c >> 8; h *= 16777619u; }
            return -(ContentKeyBase + (int)(h & 0x3FFFFFFFu));
        }

        /// <summary>The first content key. Host-assigned keys count down from -1, so a battle would need a
        /// million mid-battle spawns to reach this — and <see cref="AssignHostKey"/> says so out loud if it
        /// ever does, rather than minting a number a crate already answers to.</summary>
        internal const int ContentKeyBase = 1000000;

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
            if (key <= -ContentKeyBase)
                Debug.LogError("[Multiplayer][tac] the host has now assigned " + ContentKeyBase + " mid-battle " +
                               "spawn keys and has run into the CONTENT-KEY range — key " + key + " may already " +
                               "belong to a battle-start actor, and both would answer to it.");
            _derived[actor] = key;
            _byDerived[key] = actor;
            return key;
        }

        /// <summary>A4 — take the host's number verbatim, and DO NOT touch the counter. The key is GIVEN, not
        /// minted. Since the battle-start keys became content hashes the two ranges cannot meet at all
        /// (<see cref="ContentKeyBase"/>), so a collision detected here means the host handed the same number
        /// to two spawns — which is why it is reported as loudly as it is.</summary>
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
                // THE REFUSAL IS ASKED FIRST, and that order is load-bearing: a refused key names NOBODY, and
                // a later actor that hashes onto it registers in the map (the clash pair was removed from it),
                // after which the map would happily answer with a third actor for a number two others already
                // proved ambiguous. A5 says a refusal is permanent for the battle, so it outranks the map.
                if (_refused.TryGetValue(key, out why)) return null;
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

        /// <summary>THE SAME RESOLVE PLUS THE CAST EVERY TACTICAL CALLER MAKES — and the reason a dozen of
        /// them were lying about why they refused a record.
        ///
        /// <c>Resolve(...) as TacticalActor</c> throws away the difference between "this peer never heard of
        /// that key" and "this peer HAS that object and it is not a <c>TacticalActor</c>". The first fills
        /// <c>why</c> in every branch; the second leaves it null, so the caller printed <c>— .</c> and named
        /// NOTHING. Live 2026-08-08, rescue mission, client-derived battle key -69: ~30 host records (settle,
        /// command, weapon switch) refused for the whole mission with an empty reason, the peers' evacuated
        /// counts came out 3 on the host and 2 on the client, and the XP followed
        /// (<c>TacticalFaction.GiveExperienceForObjectives</c> is computed locally on each peer from LOCAL
        /// objective state).
        ///
        /// IT WAS INVISIBLE TO THE DETECTOR THAT EXISTS, which is the worse half: <see cref="Refuse"/> DROPS
        /// an empty reason, so <see cref="RosterDivergence"/> — law L67e, the arm written precisely to catch
        /// "this peer is fighting a smaller battle" — stayed green through a mission-long divergence. Textbook
        /// silent swallow: an early return with nothing in any log.
        ///
        /// So the cast lives HERE, it NAMES the type it actually got, and it FILES the refusal. Filing is
        /// correct for this failure and only this one: an object's type never changes, so a key that resolves
        /// to a non-actor is a PERMANENT structural divergence, which is exactly what the ledger is for. The
        /// transient failures are deliberately left unfiled — <see cref="Resolve"/> consults <c>_refused</c>
        /// as the winning answer (A5), so filing "the map is not built yet" would permanently refuse a key
        /// that becomes resolvable one frame later.</summary>
        internal static TacticalActor ResolveActor(TacticalLevelController tlc, int key, out string why)
        {
            var found = Resolve(tlc, key, out why);
            if (ReferenceEquals(found, null)) return null;   // Resolve already named the reason
            var actor = found as TacticalActor;
            if (actor != null) return actor;
            // GetType().Name, not the Unity name: no ECall, so this path is as safe in the headless harness
            // as it is in the player (same reason SafeName exists one method down).
            why = "actor key " + key + " names a " + found.GetType().Name + " on this peer, not a TacticalActor" +
                  " — it carries no CharacterStats, no abilities and no faction turn, so it can be neither " +
                  "commanded, settled nor status-reconciled here while the host counts it as a soldier";
            Refuse(key, why);
            return null;
        }

        /// <summary>THE RECEIVER KEY (law L66c): an <c>IDamageReceiver</c> is named by its actor plus
        /// <c>IDamageReceiver.GetSlotName()</c> — the interface's OWN identity string, not something invented
        /// here. <c>TacticalActorBase.GetSlotName</c>:764 returns "" (the whole actor),
        /// <c>ItemSlot.GetSlotName</c>:215 returns <c>ItemSlotDef.SlotName</c> and
        /// <c>DamageReceiverImplementation.GetSlotName</c>:73 forwards to its slot. It is stable because it is
        /// a DEF field (identical on every peer by mod parity, law 10), it is UNIQUE per actor because the
        /// game itself asserts that — <c>TacticalActor.ValidateActor</c>:1178-1186 logs an error for duplicate
        /// health-slot names — and it ROUND-TRIPS through the game's own resolver,
        /// <c>CharacterBodyState.GetSlot(string)</c>:152-155.
        ///
        /// THE THIRD SHAPE ANSWERS THE WRONG THING, AND THAT WAS A 0%-DELIVERY BUG. This repo believed
        /// <c>TacticalItem</c> forwarded <c>GetSlotName()</c> to its slot — it does not.
        /// <c>DamageReceiverImplementation.GetSlotName</c>:71-74 is the method that forwards to <c>_itemSlot</c>,
        /// and <c>TacticalItem</c> does NOT delegate to it: <c>TacticalItem.GetSlotName</c>:634-637 is
        /// <c>return "";</c>, hardcoded. So every carried item reported "the whole actor", <c>ItemGuidOf</c> saw
        /// an empty slot and refused, and NOT ONE item-damage record ever crossed — the four log lines that
        /// looked like four events were four DEFS collapsed by one <c>SayOnce</c>.
        ///
        /// The rung goes HERE and not at the call sites because every caller (the damage capture, the status
        /// target tag, the resnapshot's item block, the ability-target rider) needs the same answer, and four
        /// copies of it is four chances to drift. <c>TacticalItem.ParentItemSlot</c>:121 is
        /// <c>base.ParentSlot as ItemSlot</c> — the slot the item actually hangs in — and its
        /// <c>GetSlotName()</c>:215 is the very <c>ItemSlotDef.SlotName</c> the rest of this address rule is
        /// built on, so the item and its slot now agree by derivation instead of by belief.</summary>
        internal static string SlotOf(IDamageReceiver receiver)
        {
            if (receiver == null) return null;
            var item = receiver as TacticalItem;
            if (ReferenceEquals(item, null)) return receiver.GetSlotName() ?? "";   // L113: `as` asked it already
            var parent = item.ParentItemSlot;
            return ReferenceEquals(parent, null) ? "" : (parent.GetSlotName() ?? "");
        }

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
                // NO PARITY CLAIM HERE. Nothing on this path asked the def repository anything, so "the mods
                // differ" and "this actor's body simply has no such slot" are indistinguishable from where we
                // stand — and blaming the join for the second one cost a whole investigation on 2026-08-08.
                // Say what was actually observed and stop.
                why = SafeName(actor) + " has no body-part slot named '" + slotName + "' on this peer (its " +
                      "BodyState resolved, and none of its slots answers to that name)";
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
}
