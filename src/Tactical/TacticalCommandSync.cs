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
    /// NOT universal, deliberately and loudly: an actor that never came from the geoscape (a procedurally
    /// spawned Pandoran) carries <c>GeoTacUnitId.None</c> == 0, and MANY of them do at once — see
    /// <c>HavenMissionUtil</c>:46 filtering on exactly that. So 0 is refused outright rather than resolved to
    /// an arbitrary one of them, and a duplicate is a refusal too. A3a only ever keys PLAYER-faction soldiers,
    /// which are deployed <c>GeoCharacter</c>s and always carry a real id (<c>TacCharacterData</c>:131
    /// <c>GeoUnitId = Id</c>); when A3b needs to name an alien TARGET it will need a second, minted key —
    /// which is exactly why this lives behind two methods instead of being inlined at the call sites.
    /// </summary>
    internal static class TacticalActorKey
    {
        internal static int Of(TacticalActorBase actor) => actor == null ? 0 : (int)actor.GeoUnitId;

        /// <summary>Null + a human reason on any failure — never a silent "closest match". The scan is over
        /// EVERY actor on the map (not just the player's) precisely so a collision with a key-less alien is
        /// detected instead of being invisible.</summary>
        internal static TacticalActorBase Resolve(TacticalLevelController tlc, int key, out string why)
        {
            why = null;
            if (key == 0)
            {
                why = "actor key 0 is GeoTacUnitId.None — this actor never came from the geoscape, so it has " +
                      "no identity the peers share and it cannot be commanded across the wire";
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
        /// <summary>Every bit this build can decode. A set bit outside it means the sender declared a field
        /// this reader does not know — the rest of the stream is then misaligned, so it is a THROW, not a
        /// best-effort read. (Mod parity is blocking, law 10, so this can only be a bug, never a version skew.)</summary>
        internal const ushort KnownBits = BitPositionToApply;

        /// <summary>The fields that ACTUALLY ride, in wire order. A3a: movement, and movement needs exactly
        /// one thing — <c>MoveAbility.Move</c>:105-144 reads <c>target.PositionToApply</c> (:118) and nothing
        /// else off the payload except the followup pair. Destination-only is therefore sufficient: the path
        /// is not shipped, it is re-derived per peer by <c>TacticalNavigationComponent.Navigate</c>:937-949
        /// over an RNG-free A* (<c>Base.Levels.Nav.Tiled.Pathfinder</c>).</summary>
        internal static readonly string[] Rides = { "PositionToApply" };

        /// <summary>The fields that deliberately do NOT ride, each with the reason. This list is the other
        /// half of the coverage law — it is what makes a drop a DECISION instead of an omission.</summary>
        internal static readonly Dictionary<string, string> Dropped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Actor",                "A3b (the target actor of a shot/heal) — needs an actor key that also covers key-less aliens" },
            { "ShootTargetActor",     "A3b — same key problem as Actor" },
            { "GameObject",           "a live scene object; the peer's own equivalent is reached through the actor key, never shipped" },
            { "ActorGridPosition",    "the SOURCE tile, which every peer already holds: it is the commanded actor's own Pos" },
            { "ShootFromPos",         "A3b — a derived firing origin, recomputed per peer from the actor's stance" },
            { "CoverDirection",       "presentation: which way the actor hugs cover on arrival (law 5 names it local-only)" },
            { "Direction",            "A3b — aim direction for cone/line weapons" },
            { "Cone",                 "A3b — the cone shape of a spread weapon" },
            { "AttackType",           "A3b — Regular / OverwatchShot / etc." },
            { "TacticalItem",         "A3b/A4 — a live item instance; would need an item key, and no rider needs it yet" },
            { "Equipment",            "A3b — the firing equipment; reached through the actor's own slot on each peer" },
            { "DamageReceiver",       "A3b — a live interface reference; damage is host-authoritative and rides its own arc" },
            { "ItemContainer",        "A4 (inventory) — a live container reference" },
            { "InventoryComponent",   "A4 (inventory) — a live component reference" },
            { "MultiAbilityTargets",  "recursive list; no rider is a multi-target ability, and shipping it would need the whole codec to nest" },
            { "FollowupAbility",      "a live ability reference. Move+shoot chains are A3b: the followup is a SECOND command and rides as its own intent, not as a passenger on the move" },
            { "FollowupAbilityTarget","same — the followup's own payload travels with the followup's own command" },
            { "ObstructionsCheckRadius","A3b — a line-of-fire tuning value, recomputed per peer from the same def" },
            { "UseShootOriginCache",  "A3b — a per-peer performance hint, never shared state" },
        };

        internal static void Write(BinaryWriter w, TacticalAbilityTarget t)
        {
            ushort mask = 0;
            bool hasPos = t != null && t.HasPositionToApply;
            if (hasPos) mask |= BitPositionToApply;
            w.Write(mask);
            if (hasPos) { w.Write(t.PositionToApply.x); w.Write(t.PositionToApply.y); w.Write(t.PositionToApply.z); }
        }

        internal static TacticalAbilityTarget Read(BinaryReader r)
        {
            ushort mask = r.ReadUInt16();
            if ((mask & ~KnownBits) != 0)
                throw new InvalidDataException("ability-target mask 0x" + mask.ToString("X4") + " declares field bits " +
                                               "this build cannot decode (known 0x" + KnownBits.ToString("X4") + ") — " +
                                               "the payload after it is misaligned and must not be guessed at");
            var t = new TacticalAbilityTarget();
            if ((mask & BitPositionToApply) != 0)
                t.PositionToApply = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            return t;
        }
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
        /// project's dominant bug class.</summary>
        internal static bool IsRider(TacticalAbility ability) => ability is MoveAbility;

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
        }

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler> { [OpIntentActivate] = HandleActivate };
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
                // A3a's honest boundary, said out loud exactly once per ability def per battle: a player
                // gesture that reaches no other peer is invisible divergence, which is this project's
                // dominant bug class.
                string name = ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name;
                if (_saidUncovered.Add(name))
                    Debug.LogWarning("[Multiplayer][tac] '" + name + "' is NOT a declared A3a rider — it ran " +
                                     "LOCALLY on this peer only and no other peer will see it. A3a carries " +
                                     "movement; attacks are A3b.");
                return;
            }

            int key = TacticalActorKey.Of(actor);
            string guid = ability.AbilityDef == null ? null : ability.AbilityDef.Guid;
            var target = parameter as TacticalAbilityTarget;
            if (key == 0 || string.IsNullOrEmpty(guid) || target == null)
            {
                string who = actor.name + " / " + (ability.AbilityDef == null ? ability.GetType().Name : ability.AbilityDef.name);
                if (_saidKeyless.Add(who))
                    Debug.LogError("[Multiplayer][tac] command NOT relayed for " + who + " — " +
                                   (key == 0 ? "the actor has no GeoTacUnitId (it never came from the geoscape)"
                                    : string.IsNullOrEmpty(guid) ? "the ability def has no guid"
                                    : "the activation carried no TacticalAbilityTarget") +
                                   ". This peer moved alone; no other peer will follow.");
                return;
            }

            if (engine.IsHost)
                Send(OpActivate, "mirror " + actor.name + " → " + Fmt(target.PositionToApply), _replayOriginPeer,
                     w => WriteCommand(w, key, guid, target));
            else
                IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentActivate,
                                "command " + actor.name + " → " + Fmt(target.PositionToApply),
                                w => WriteCommand(w, key, guid, target));
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
                return "that ability is not a declared A3a rider — only movement crosses the wire in this arc";
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
            var target = TacAbilityTargetCodec.Read(r);   // a throw here funnels into IntentRail's reject path

            var tlc = Tlc();
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
                    if (op == OpActivate) ApplyActivate(r.ReadInt32(), r.ReadString(), TacAbilityTargetCodec.Read(r));
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
        private static void ApplyActivate(int key, string guid, TacticalAbilityTarget target)
        {
            string why;
            var actor = TacticalActorKey.Resolve(Tlc(), key, out why) as TacticalActor;
            if (actor == null)
            {
                Debug.LogError("[Multiplayer][tac] host command for actor " + key + " cannot be played here — " +
                               why + ". That soldier will stand still on this screen while it moves on the host's.");
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
                               "not a declared A3a rider — the two peers disagree about what this arc carries.");
                return;
            }
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
}
