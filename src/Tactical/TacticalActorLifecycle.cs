using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using Base.Entities;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.ActorDeployment;
using PhoenixPoint.Tactical.Levels.Missions;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// "An actor is being created because the HOST created one, and this is that replay." The counterpart of
    /// <see cref="MirrorApplyScope"/> for the structural half: it is what tells the universal enter-play gate
    /// that this particular spawn is legitimate. Game code is main-thread only, so a depth counter suffices.
    /// </summary>
    internal static class SpawnApplyScope
    {
        private static int _depth;
        internal static bool Active => _depth > 0;
        internal static IDisposable Enter() => new Scope();
        internal static void Reset() => _depth = 0;

        private sealed class Scope : IDisposable
        {
            public Scope() { _depth++; }
            public void Dispose() { _depth--; }
        }
    }

    /// <summary>
    /// TACTICAL ARC A4 — ACTOR LIFECYCLE: mid-battle spawn, death, corpse contents, evacuation.
    ///
    /// NO NEW SURFACE. A4 rides <see cref="SurfaceIds.TacResult"/> (0x84) with two new ops, and that is a
    /// decision rather than thrift: 0x84 is already THE DISCRETE TACTICAL EVENT STREAM — the one surface in
    /// this repo whose messages cannot be healed by re-emitting current state, which is why it alone carries
    /// contiguity detection (<c>HasGap</c>) and a resnapshot recovery path. A spawn and a death have exactly
    /// that shape. And ORDER is load-bearing here in the same way it is for 0x80's turn+end and 0x82's
    /// activate+settle: the spawn of an actor must never be overtaken by the damage record that names it, and
    /// only ONE seq stream can guarantee that. Two surfaces could not. 0x85 stays free.
    ///
    /// THE FOUR FACTS:
    ///
    ///   • SPAWN IDENTITY IS HOST-ASSIGNED, not derived. A3b keyed key-less aliens by their ORDINAL over a
    ///     board both peers snapshotted; an actor created after that snapshot was in nobody's snapshot, so no
    ///     derived scheme can name it — the peers have nothing shared to derive FROM. The host therefore mints
    ///     the key at the instant of creation (<c>TacticalActorKey.AssignHostKey</c>) and it rides ON the spawn
    ///     event; every other peer adopts it verbatim. One counter serves both schemes, so they cannot collide.
    ///
    ///   • THE SPAWN RNG IS HOST-ONLY. <c>TacParticipantSpawn.DeployForTurn</c>:397 is reached from
    ///     <c>TacMission.OnNewTurn</c>:352-355 on EVERY peer running the native turn machine, and it draws from
    ///     <c>SharedData.Random</c> — a per-peer <c>new System.Random()</c> — for the actor
    ///     (<c>GenerateNextActorToDeploy</c>:330 <c>WeightedRandomElement</c>), the zone and the position
    ///     (:670/:672/:682/:684, :614/:620). Worse, its inputs DIVERGE by construction: the reinforcement
    ///     clearance filter measures distance to live enemies (<c>GetAllowedZonePositions</c>:366-391) and the
    ///     aliens only ever move on the host (A2's <c>ClientAiGate</c>). So the two peers cannot be made to
    ///     roll the same wave even with a shared seed — this is replicated, never re-derived. TFTV reaches the
    ///     same funnel from a POSTFIX on <c>TacticalFaction.RequestEndTurn</c>
    ///     (<c>TFTVHarmonyTactical</c>:329 -> <c>CallReinforcementsAbility</c>:58 / its own
    ///     <c>TacticalDeployZone.SpawnActor</c> calls), and a Harmony postfix still runs when our own
    ///     end-turn prefix skipped the original — so on a client TFTV's reinforcements were spawning off a
    ///     BLOCKED end-turn click. That is what <see cref="ActorEnterPlaySeam"/> contains.
    ///
    ///   • DEATH IS STRUCTURAL AND IT IS THE HOST'S. The one funnel is
    ///     <c>TacticalActorBase.OnHealthChange</c>:616-622 -> <c>Die()</c>:624-629, fired by the STAT itself
    ///     when health crosses zero while in play, so the mirror's verbatim <c>ApplyDamage</c> already kills
    ///     natively — the hole A3b logged is the death that arrives by any OTHER route (a status, a scripted
    ///     kill, a mod). That is a <c>Die</c> PREFIX on the host and op <see cref="TacticalDamageSync.OpDeath"/>
    ///     for whoever has not seen it, plus the resnapshot's own dead flag, which A3b could only complain
    ///     about and now FORCES (<c>Health.Set(0)</c> is the game's own trigger — <c>BaseStat.Set</c>:95-107
    ///     raises <c>StatChangeType.Value</c>).
    ///
    ///   • THE CORPSE'S CONTENTS ARE THE HOST'S ROLL. <c>DieAbility.ShouldDestroyItem</c>:187-200 draws
    ///     <c>SharedData.Random.Range(0, 100)</c> per droppable item. A3b made a client answer "keep it"
    ///     without drawing, which stopped the desync of the shared stream but left every corpse holding MORE
    ///     than the host's. A4 pre-rolls the whole list on the host at the <c>Die</c> prefix — the same
    ///     memoize-then-serve shape as <see cref="FumbleGate"/>, and for the same reason: the value is
    ///     consumed inside machinery no later message can get ahead of — and ships it WITH the killing damage
    ///     record, so the mirror declares it BEFORE it applies the hit that starts the death.
    ///
    /// WHAT A4 DELIBERATELY DOES NOT DO: it never DESTROYS an actor anywhere. Evacuation rides the A3a
    /// command seam as an ordinary rider and every peer runs the game's own hide; the only destroy in the
    /// whole assembly is the game's own <c>DieAbility.PostProcessDeath</c>, reached natively on each peer.
    /// </summary>
    public static class TacticalActorLifecycle
    {
        /// <summary>HOST: deaths whose loot list has been pre-rolled but not yet shipped, by actor key. The
        /// killing damage record takes it (same call stack, so it is always there in time); anything left is
        /// a death no damage record carried and <see cref="HostTick"/> ships it as its own op.</summary>
        private static readonly Dictionary<int, List<KeyValuePair<string, bool>>> _pendingDeath =
            new Dictionary<int, List<KeyValuePair<string, bool>>>();

        /// <summary>HOST: every corpse manifest this battle has produced, kept rather than consumed. A4 shipped
        /// the manifest only with the killing hit or the death op, which left the RESNAPSHOT — the one recovery
        /// path this surface has — forcing deaths with no manifest at all, so a recovered corpse kept every item
        /// the host had destroyed (loudly, in <see cref="LootMirror.TryDeclared"/>, but wrongly). The archive is
        /// what makes the recovery path carry the same answers the live path did; it is per-battle and a battle
        /// has tens of deaths, so keeping them costs nothing.</summary>
        private static readonly Dictionary<int, List<KeyValuePair<string, bool>>> _deathManifest =
            new Dictionary<int, List<KeyValuePair<string, bool>>>();

        private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The archived manifest for an actor the host has already buried, or null. Never consumed —
        /// a resnapshot may legitimately be asked for more than once.</summary>
        internal static List<KeyValuePair<string, bool>> ManifestFor(int key)
        {
            List<KeyValuePair<string, bool>> list;
            return _deathManifest.TryGetValue(key, out list) ? list : null;
        }

        internal static void Reset()
        {
            _pendingDeath.Clear();
            _deathManifest.Clear();
            _said.Clear();
            SpawnApplyScope.Reset();
            LootMirror.Reset();
        }

        private static void SayOnce(string key, string message)
        {
            if (_said.Add(key)) Debug.LogError(message);
        }

        // ─── SPAWN ─────────────────────────────────────────────────────────

        /// <summary>The enter-play PREFIX. Returns false to CONTAIN a spawn this peer must not have made.
        /// Containment is refusing to enter play AND deactivating the GameObject — NOT destroying it: whatever
        /// spawned it still holds a valid reference and cannot NRE. Destroying is the v1 shape and is never
        /// done here.
        ///
        /// WHY THE DEACTIVATION IS LOAD-BEARING, and not the tidy-up it looks like (law L121, 2026-08-05 RCA).
        /// This doc used to claim that refusing enter-play alone left the actor "unreferenced by the map… so
        /// nothing can see it, target it or enumerate it", on the strength of
        /// <c>ActorComponent.OnEnterPlay</c>:130-138 being the only caller of <c>BaseMap.RegisterActor</c>.
        /// That was FALSE for the one native enumerator that does not read <c>_actors</c> at all:
        /// <c>BaseMap.FindAllActors</c>:75-80 is
        /// <c>FindGameObjectsWithTag(ActorsTag).Where(go =&gt; go.activeInHierarchy)</c>, and a half-built actor
        /// is still tagged and still active. So <c>TacticalLevelController.OnLevelEnd</c>:748 handed the
        /// contained actor straight to <c>OnExitPlay</c>, <c>TacticalActor.OnExitPlay</c>:804-816 dereferenced
        /// the <c>CharacterStats</c> that <c>PrepareEnterPlay</c> never built, and the NRE killed
        /// <c>Level+&lt;SetCurrentCrt&gt;d__50</c> — the peer that pressed FINISH on the results screen hung in
        /// the loading screen forever while the other two left the battle normally.
        ///
        /// <c>SetActive(false)</c> makes the invariant true through the GAME'S OWN filter rather than through
        /// a second patch per consumer, so it also covers the other <c>FindAllActors</c> caller
        /// (<c>TacticalLevelController</c>:636, which re-enters every found actor at level start).</summary>
        internal static bool OnActorEnteringPlay(ActorComponent component)
        {
            var actor = component as TacticalActor;
            if (actor == null) return true;              // containers / structural targets / off-map: other arcs
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null || engine.IsHost) return true;
            if (!TacticalActorKey.Built) return true;    // battle start: this actor came from the shared save
            if (SpawnApplyScope.Active) return true;     // this IS the host's spawn being replayed here
            SayOnce("client-spawn-" + SafeName(component),
                "[Multiplayer][tac] a spawn this client made ITSELF (" + SafeName(component) + ") is CONTAINED — " +
                "it never enters play, because mid-battle spawns are the host's and arrive on 0x84. Every " +
                "reinforcement wave, hatch and summon is rolled off SharedData.Random against live enemy " +
                "positions the peers legitimately disagree about, so a locally-rolled one is a different " +
                "monster in a different place. If a wave is MISSING on this screen, that is a lost 0x84 spawn " +
                "record, not this line.");
            // OUT OF THE GAME'S OWN ENUMERATOR, not merely out of BaseMap._actors — see the doc above. One
            // line, no destruction, reference still valid for the spawner that is about to get it back.
            component.gameObject.SetActive(false);
            return false;
        }

        /// <summary>The enter-play POSTFIX, host side: mint the key and ship the actor. Harmony runs a postfix
        /// even when a prefix skipped the original, so the FIRST thing this asks is whether the actor really
        /// entered play — <c>InPlay</c> is the game's own answer and it is false for a contained spawn.</summary>
        internal static void OnActorEnteredPlay(ActorComponent component)
        {
            var actor = component as TacticalActor;
            if (actor == null || !actor.InPlay) return;
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null || !engine.IsHost) return;
            if (!TacticalActorKey.Built) return;         // battle start: the entry save carries these for free
            if (SpawnApplyScope.Active) return;          // a host never applies a spawn; belt, not braces

            int key = TacticalActorKey.AssignHostKey(actor);
            var faction = actor.TacticalFaction;
            var set = actor.GetComponent<ComponentSet>();
            string setGuid = set == null || set.SetDef == null ? "" : set.SetDef.Guid;
            string templateGuid = SourceTemplateGuid(actor);
            string factionGuid = faction == null || faction.TacticalFactionDef == null ? "" : faction.TacticalFactionDef.Guid;
            var zone = actor.Source as TacticalDeployZone;

            if (string.IsNullOrEmpty(templateGuid) && string.IsNullOrEmpty(setGuid))
                SayOnce("spawn-defless-" + SafeName(actor),
                    "[Multiplayer][tac] " + SafeName(actor) + " entered play with NEITHER an authored source " +
                    "template NOR a shared ComponentSetDef guid — the shape a deployed GeoCharacter has (its real " +
                    "identity is its geoscape character, and the geoscape level is not loaded during a battle). " +
                    "The spawn record still ships so the other peers say so too, but they cannot rebuild it and " +
                    "this actor will exist on the host alone.");

            TacticalDamageSync.Send(TacticalDamageSync.OpSpawn,
                "spawn " + SafeName(actor) + " key=" + key,
                w =>
                {
                    w.Write(key);
                    w.Write(setGuid);
                    w.Write(templateGuid);
                    w.Write(factionGuid);
                    w.Write((byte)(faction == null ? TacMissionParticipant.None : faction.ParticipantKind));
                    var p = actor.Pos;
                    var r = actor.Rot;
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(r.x); w.Write(r.y); w.Write(r.z); w.Write(r.w);
                    w.Write(zone == null ? "" : zone.name);
                });
        }

        /// <summary>THE ADDRESS A SPAWN IS REBUILDABLE FROM (law L67e). A <c>ComponentSetDef</c> guid is NOT one:
        /// EVERY mid-battle arrival in the assembly builds its set def through
        /// <c>TacActorData.GenerateInstanceComponentSetDef</c>:52 -> <c>DefRepository.CreateRuntimeDef</c>:234,
        /// which stamps <c>Guid.NewGuid()</c> and registers it in THAT PEER'S <c>_guid2Def</c> alone. The guid is
        /// therefore non-empty and perfectly valid on the host and pure noise everywhere else — which is why the
        /// old "no guid at all" warning never fired and the host looked healthy while the actor existed on one
        /// screen only (TFTV's Umbra, live 3-instance test 2026-08-05).
        ///
        /// The AUTHORED template is: <c>ActorInstanceData.SourceTemplate</c> (Base.Entities:19) is a repo def
        /// with a stable guid, and it is stamped by every arrival path that matters —
        /// <c>DeathBelcherAbility</c>:91 (the Umbra), <c>SpawnActorAbility</c>:93 (summons) and
        /// <c>ActorDeployData.InitializeInstanceData</c>:101/117, which is the one funnel behind reinforcement
        /// waves, <c>ResurrectAbility</c>:139-156 and <c>SpawnChildActorStatus</c>:88-98. It is always a
        /// <c>TacActorDef</c>, whose <c>InstanceData</c> (abstract, :16) regenerates the very same set def
        /// locally. Read exactly the way the game reads it in <c>ActorComponent.DoEnterPlay</c>:116, so the
        /// component is guaranteed present at our postfix.</summary>
        private static string SourceTemplateGuid(TacticalActor actor)
        {
            var data = InstanceDataComponent.GetInstanceData<ActorInstanceData>(actor.gameObject);
            var template = data == null ? null : data.SourceTemplate as TacActorDef;
            return template == null ? "" : template.Guid;
        }

        /// <summary>Rebuild the host's actor with the game's OWN spawner. <c>TacticalDeployZone.SpawnActor</c>
        /// :257-270 is the static every deploy path in the assembly funnels through (and the one TFTV calls
        /// directly): it clones the def's instance data, stamps transform + faction + participant, and hands
        /// off to <c>ActorSpawner.SpawnActor</c>. A null instance-data template is passed on purpose — the def
        /// is the state a just-spawned actor has, and anything that moves afterwards (health, AP, statuses)
        /// is already the business of 0x84's damage and resnapshot ops.</summary>
        internal static void ApplySpawn(BinaryReader r)
        {
            int key = r.ReadInt32();
            string setGuid = r.ReadString();
            string templateGuid = r.ReadString();
            string factionGuid = r.ReadString();
            var participant = (TacMissionParticipant)r.ReadByte();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            string zoneName = r.ReadString();

            var repo = GameUtl.GameComponent<DefRepository>();
            var setDef = RebuildSetDef(repo, templateGuid, setGuid);
            var factionDef = string.IsNullOrEmpty(factionGuid) || repo == null ? null : repo.GetDef(factionGuid) as TacticalFactionDef;
            if (setDef == null || factionDef == null)
            {
                // A5 — A FIRST-CLASS REFUSAL, not a log line and a shrug. The key is REGISTERED as unresolvable
                // WITH THIS REASON, and every later record naming it says so, instead of the generic "a spawn
                // record never arrived" that sends the next reader hunting a lost packet. Since L67e this is
                // also NARROW: the only spawn that can still land here is one the host had no authored
                // SourceTemplate for — a deployed GeoCharacter, whose real identity is its geoscape character
                // and whose geoscape level is not loaded during a battle. Everything the game builds from a def
                // (reinforcements, summons, revives, hatchlings, death belchers, TFTV's Umbra) now rebuilds.
                string why = "the host spawned this actor with " +
                             (setDef == null
                                ? (string.IsNullOrEmpty(templateGuid)
                                    ? "NO authored source template at all (SourceTemplate was null — the shape a " +
                                      "deployed GeoCharacter has) and a ComponentSetDef guid '" + setGuid + "' that " +
                                      "is runtime-generated on the host, so no peer can look either of them up"
                                    : "source template guid '" + templateGuid + "' / ComponentSetDef guid '" + setGuid +
                                      "', and NEITHER resolves here — the two peers are running different def sets")
                                : "TacticalFactionDef guid '" + factionGuid + "', which resolves to nothing here") +
                             ", so it exists on the host alone and can never be rebuilt on this peer";
                TacticalActorKey.Refuse(key, why);
                Debug.LogError("[Multiplayer][tac] host spawn key " + key + " REFUSED — " + why +
                               ". It will fight on the host's screen and be absent from this one for the rest of " +
                               "the mission; every command and every hit naming it is refused with this reason.");
                return;
            }

            var tlc = TacticalDamageSync.Tlc();
            if (tlc == null || tlc.Map == null)
            {
                Debug.LogError("[Multiplayer][tac] a host spawn (key " + key + ", " + setDef.name + ") arrived with no " +
                               "tactical map on this peer — the battles have parted ways.");
                return;
            }
            TacticalDeployZone zone = null;
            if (!string.IsNullOrEmpty(zoneName))
                foreach (var z in tlc.Map.GetActors<TacticalDeployZone>())
                    if (z.name == zoneName) { zone = z; break; }
            if (zone == null && !string.IsNullOrEmpty(zoneName))
                Debug.LogWarning("[Multiplayer][tac] the host's spawn source zone '" + zoneName + "' does not exist on " +
                                 "this peer; the actor is built without one (its Source is then null, which only " +
                                 "matters if this peer ever writes a tactical save).");

            TacticalActorBase spawned;
            using (SyncApplyScope.Enter())
            using (SpawnApplyScope.Enter())
                spawned = TacticalDeployZone.SpawnActor(setDef, null, factionDef, participant, pos, rot, zone);
            if (spawned == null)
            {
                Debug.LogError("[Multiplayer][tac] the game's own spawner returned nothing for the host's " +
                               setDef.name + " (key " + key + ") — this peer is now short one actor.");
                return;
            }
            TacticalActorKey.Adopt(spawned, key);
            Debug.Log("[Multiplayer][tac] CLIENT spawned the host's " + setDef.name + " key=" + key + " @ " + pos);
        }

        /// <summary>The client half of law L67e: REGENERATE the host's set def from the authored template rather
        /// than looking up a guid that was minted per-peer. <c>TacActorData.GenerateInstanceComponentSetDef</c>
        /// is the game's own call and the very one the host made, so this is the same def by construction — both
        /// peers run the same build, which the version gate already enforces. The shipped
        /// <c>ComponentSetDef</c> guid stays as the FALLBACK for the case where the host's set def really was an
        /// authored repo def, which is the only case it was ever able to serve.
        ///
        /// It regenerates the DEF, not the instance state: <c>ApplySpawn</c> deliberately passes a null instance
        /// template to the spawner, because everything that moves after the spawn (health, AP, statuses, and the
        /// summoner's donated equipment in <c>SpawnActorAbility</c>:75-90) is already 0x84's damage and
        /// resnapshot business. A template rebuild is the def's fresh state, exactly as before.</summary>
        private static ComponentSetDef RebuildSetDef(DefRepository repo, string templateGuid, string setGuid)
        {
            if (repo == null) return null;
            var template = string.IsNullOrEmpty(templateGuid) ? null : repo.GetDef(templateGuid) as TacActorDef;
            if (template != null && template.InstanceData != null)
                return template.InstanceData.GenerateInstanceComponentSetDef();
            return string.IsNullOrEmpty(setGuid) ? null : repo.GetDef(setGuid) as ComponentSetDef;
        }

        // ─── DEATH + THE CORPSE'S CONTENTS ─────────────────────────────────

        /// <summary>The host's <c>TacticalActorBase.Die</c> prefix. Runs BEFORE the base body reaches
        /// <c>GetPreferredDieAbility().Activate(deathReport)</c>:628, which is what makes the loot list exist
        /// in time for the damage record that is still unwinding underneath (the whole chain — ApplyDamage ->
        /// health -> OnHealthChange -> Die — is synchronous inside the capture postfix's own call).</summary>
        internal static void OnHostDeath(TacticalActorBase actor)
        {
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null || !engine.IsHost) return;
            int key = TacticalActorKey.Of(actor);
            if (key == 0)
            {
                SayOnce("death-unkeyed-" + SafeName(actor),
                    "[Multiplayer][tac] " + SafeName(actor) + " died on the host with NO shared key, so no peer can " +
                    "be told — it stays alive on every other screen for the rest of the mission.");
                return;
            }
            _pendingDeath[key] = _deathManifest[key] = LootMirror.HostPreRoll(actor);
        }

        /// <summary>Take the pending loot list for an actor the killing damage record is about to name; null
        /// when this death did not come through damage (it then rides its own op from
        /// <see cref="HostTick"/>).</summary>
        internal static List<KeyValuePair<string, bool>> TakePendingDeath(int key)
        {
            List<KeyValuePair<string, bool>> list;
            if (!_pendingDeath.TryGetValue(key, out list)) return null;
            _pendingDeath.Remove(key);
            return list;
        }

        /// <summary>The standing HOST emitter (driven from <c>SyncEngine.Tick</c>): a death no damage record
        /// carried — a status kill, a scripted one, a mod's — still has to reach every peer, and it is shipped
        /// one frame later rather than inline so it can never overtake the damage stream it belongs behind.</summary>
        internal static void HostTick(NetworkEngine engine)
        {
            if (_pendingDeath.Count == 0) return;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) { _pendingDeath.Clear(); return; }
            var keys = new List<int>(_pendingDeath.Keys);
            foreach (var key in keys)
            {
                var loot = _pendingDeath[key];
                _pendingDeath.Remove(key);
                TacticalDamageSync.Send(TacticalDamageSync.OpDeath, "death key=" + key, w =>
                {
                    w.Write(key);
                    WriteLoot(w, loot);
                });
            }
        }

        internal static void WriteLoot(BinaryWriter w, List<KeyValuePair<string, bool>> loot)
        {
            w.Write(loot == null ? 0 : loot.Count);
            if (loot == null) return;
            foreach (var kv in loot) { w.Write(kv.Key); w.Write((byte)(kv.Value ? 1 : 0)); }
        }

        internal static List<KeyValuePair<string, bool>> ReadLoot(BinaryReader r)
        {
            int n = r.ReadInt32();
            var list = new List<KeyValuePair<string, bool>>(n);
            for (int i = 0; i < n; i++)
            {
                string guid = r.ReadString();
                bool destroy = r.ReadByte() != 0;
                list.Add(new KeyValuePair<string, bool>(guid, destroy));
            }
            return list;
        }

        internal static void ApplyDeath(BinaryReader r)
        {
            int key = r.ReadInt32();
            var loot = ReadLoot(r);
            LootMirror.Declare(key, loot);
            string why;
            var actor = TacticalActorKey.Resolve(TacticalDamageSync.Tlc(), key, out why);
            if (actor == null)
            {
                Debug.LogError("[Multiplayer][tac] the host reports actor " + key + " DEAD but " + why +
                               " — this peer keeps fighting something the host has already buried.");
                return;
            }
            ForceDeath(actor, "the host's death record");
        }

        /// <summary>Kill through the GAME'S OWN trigger and nothing else: <c>BaseStat.Set</c>:95-107 raises
        /// <c>StatChangeType.Value</c>, <c>TacticalActorBase.OnHealthChange</c>:616-622 sees the crossing to
        /// zero and calls <c>Die()</c> — so the corpse, the drop, the death effect, the unmount, the statistics
        /// and the objectives are all the native ones. There is no second way to die in this repo. Already at
        /// zero means the native death has already run here and this is a re-delivery.</summary>
        internal static void ForceDeath(TacticalActorBase actor, string because)
        {
            if (actor.IsDead) return;
            Debug.LogWarning("[Multiplayer][tac] forcing " + SafeName(actor) + " dead from " + because +
                             " — it was alive here at " + ((float)actor.Health).ToString("0.##") + " hp, which means " +
                             "its death reached this peer by a route the damage stream does not carry.");
            using (SyncApplyScope.Enter()) actor.Health.Set(0f);
            if (!actor.IsDead)
                Debug.LogError("[Multiplayer][tac] " + SafeName(actor) + " survived being set to zero health — the " +
                               "native death trigger did not fire and this peer is now fighting a corpse.");
        }

        internal static string SafeName(UnityEngine.Object o)
        {
            try { return o == null ? "<null>" : o.name; }
            catch { return o == null ? "<null>" : o.GetType().Name; }
        }
    }

    /// <summary>
    /// THE CORPSE'S CONTENTS (law L67d). <c>DieAbility.ShouldDestroyItem</c>:187-200 rolls
    /// <c>SharedData.Random.Range(0, 100) &lt; DestroyOnActorDeathPerc</c> per droppable item, and
    /// <c>SharedData.Random</c> is a per-peer <c>new System.Random()</c> — not comparable between peers at all.
    ///
    /// SHAPE: exactly <see cref="FumbleGate"/>'s. The host takes the ONE native roll early, through this very
    /// patch (which finds nothing memoized and lets the original run), MEMOIZES it per item instance, and the
    /// game's own later call gets the memo instead of a second draw. Every other peer NEVER draws: it answers
    /// from the host's declared list.
    ///
    /// THE ADDRESS IS THE ITEM'S DEF, AND ONLY THE ITEM'S DEF — never its position in the list. That was the
    /// 2026-08-07 divergence, and both peers' logs name it exactly. The host pre-rolls at
    /// <c>TacticalActorBase.Die</c> and answered [clip, clip, rifle]; the client's own
    /// <c>DieAbility.DropItems</c> asked [rifle, clip, clip]. The manifest used to be a QUEUE scanned FORWARD
    /// to the matching guid, so reaching the rifle DISCARDED both clip answers, the queue emptied, and the two
    /// clips the host had destroyed (rolls 43 % and 40 % against a 75 % destroy chance, host Player.log frame
    /// 47909) were KEPT here — which is the extra item the inventory rail reported twelve minutes later.
    /// The order was never guaranteed: <c>GetDroppableItems</c>:202-224 is inventory-then-addons over
    /// <c>GetComponentsInChildren</c> plus a <c>Distinct()</c>, and the host reads it a moment EARLIER in the
    /// death than the mirror does, with the equipped weapon moving between the two enumerations in between.
    ///
    /// SO IT IS A MULTISET KEYED BY DEF GUID: each def holds the host's answers for as many items of that def
    /// as the host rolled, consumed one per question in whatever order the questions arrive. That keeps every
    /// property the queue had — <c>DropItems</c>:139-185 legitimately asks about a SUBSET (it skips body parts
    /// in the mount branch, and asks about nothing at all when the def destroys everything), and an unasked
    /// answer simply goes unused instead of eating the next one — and drops the one it did not have. Running
    /// out for a def the host DID name is a loud "keep it", never a silent one.
    /// </summary>
    internal static class LootMirror
    {
        private static readonly Dictionary<TacticalItem, bool> _hostRolls = new Dictionary<TacticalItem, bool>();
        private static readonly Dictionary<int, Dictionary<string, Queue<bool>>> _declared =
            new Dictionary<int, Dictionary<string, Queue<bool>>>();
        private static MethodInfo _droppable;
        private static MethodInfo _shouldDestroy;

        internal static void Reset()
        {
            _hostRolls.Clear();
            _declared.Clear();
        }

        /// <summary>HOST: roll every droppable item NOW and return the list to ship.</summary>
        internal static List<KeyValuePair<string, bool>> HostPreRoll(TacticalActorBase actor)
        {
            var list = new List<KeyValuePair<string, bool>>();
            var die = actor == null ? null : actor.GetPreferredDieAbility();
            if (die == null) return list;
            if (_droppable == null)
            {
                _droppable = AccessTools.Method(typeof(DieAbility), "GetDroppableItems", new Type[0]);
                _shouldDestroy = AccessTools.Method(typeof(DieAbility), "ShouldDestroyItem", new[] { typeof(TacticalItem) });
            }
            if (_droppable == null || _shouldDestroy == null)
            {
                Debug.LogError("[Multiplayer][tac] DieAbility.GetDroppableItems / ShouldDestroyItem did not resolve — " +
                               "the corpse's contents cannot be pre-rolled, so every client keeps every item the host " +
                               "may have destroyed. AccessTools does EXACT parameter matching; a signature change here " +
                               "is silent otherwise.");
                return list;
            }
            IEnumerable<TacticalItem> items;
            try { items = (IEnumerable<TacticalItem>)_droppable.Invoke(die, null); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] enumerating " + TacticalActorLifecycle.SafeName(actor) +
                               "'s droppable items THREW — the corpse's contents will differ on every peer: " + ex);
                return list;
            }
            foreach (var item in items)
            {
                bool destroy;
                try { destroy = (bool)_shouldDestroy.Invoke(die, new object[] { item }); }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][tac] the host's loot roll for " +
                                   (item == null || item.TacticalItemDef == null ? "<def-less item>" : item.TacticalItemDef.name) +
                                   " THREW; it is shipped as 'keep': " + ex);
                    destroy = false;
                }
                _hostRolls[item] = destroy;
                var def = item == null ? null : item.TacticalItemDef;
                list.Add(new KeyValuePair<string, bool>(def == null ? "" : def.Guid, destroy));
            }
            return list;
        }

        /// <summary>MIRROR: the host's answers for a death that is about to run here.</summary>
        internal static void Declare(int actorKey, List<KeyValuePair<string, bool>> loot)
        {
            if (loot == null || loot.Count == 0) { _declared.Remove(actorKey); return; }
            var byDef = new Dictionary<string, Queue<bool>>(StringComparer.Ordinal);
            foreach (var kv in loot)
            {
                Queue<bool> q;
                if (!byDef.TryGetValue(kv.Key ?? "", out q)) byDef[kv.Key ?? ""] = q = new Queue<bool>();
                q.Enqueue(kv.Value);
            }
            _declared[actorKey] = byDef;
        }

        /// <summary>The one answer <c>ShouldDestroyItem</c> gets from us — memoized host roll, or the host's
        /// declared answer, or false (never a draw).</summary>
        internal static bool TryConsume(int actorKey, TacticalItem item, out bool value)
        {
            value = false;
            if (item != null && _hostRolls.TryGetValue(item, out value)) { _hostRolls.Remove(item); return true; }
            var def = item == null ? null : item.TacticalItemDef;
            return TryDeclared(actorKey, def == null ? "" : def.Guid, out value);
        }

        /// <summary>The PURE half — no game types, no statics beyond the manifest itself — so the rule is
        /// testable headless (RailCheck L67d, L185). An answer is found by the item's DEF, never by its
        /// position among the questions: the two peers enumerate the same droppable set at slightly different
        /// moments of the same death and do NOT enumerate it in the same order, so a positional read discards
        /// the answers it passes over. Running out for a def the host DID name is "keep it", said out loud —
        /// never a silent keep and never a local draw.</summary>
        internal static bool TryDeclared(int actorKey, string itemGuid, out bool value)
        {
            value = false;
            Dictionary<string, Queue<bool>> byDef;
            if (actorKey == 0 || !_declared.TryGetValue(actorKey, out byDef)) return false;
            Queue<bool> q;
            if (byDef.TryGetValue(itemGuid ?? "", out q) && q.Count > 0)
            {
                value = q.Dequeue();
                if (q.Count == 0) byDef.Remove(itemGuid ?? "");
                if (byDef.Count == 0) _declared.Remove(actorKey);
                return true;
            }
            Debug.LogError("[Multiplayer][tac] the host's corpse manifest for actor " + actorKey + " has no answer " +
                           "left for item def '" + itemGuid + "' — this peer KEEPS it rather than guessing, so this " +
                           "corpse holds more than the host's does.");
            return false;
        }
    }

    /// <summary>
    /// THE UNIVERSAL LIFECYCLE SEAM (law L67a/L67b), on the game's own enter-play edge:
    /// <c>ActorComponent.DoEnterPlay</c>:114-124. Chosen over any spawn-site patch because there is no single
    /// spawn SITE — <c>ActorSpawner.SpawnActor&lt;T&gt;</c> is called from deploy zones, resurrect, spawn
    /// abilities, spawn effects, child-actor statuses, death belchers and TFTV directly — but there is exactly
    /// one enter-play edge, and it is non-generic, public and parameterless, which a generic static is not.
    ///
    /// It is armed only AFTER <c>TacticalActorKey.Built</c>, and that boundary is the arc: everything present
    /// at the first turn edge came out of the same entry save on every peer (A1) and is keyed by the
    /// battle-start ordinal, so replicating it would be replicating what both peers already have.
    /// <c>TacticalLevelController</c>:638 calls this same method for every save-restored actor, which is
    /// exactly the case that boundary excludes.
    /// </summary>
    [HarmonyPatch(typeof(ActorComponent), nameof(ActorComponent.DoEnterPlay))]
    internal static class ActorEnterPlaySeam
    {
        private static bool Prefix(ActorComponent __instance) => TacticalActorLifecycle.OnActorEnteringPlay(__instance);

        private static void Postfix(ActorComponent __instance) => TacticalActorLifecycle.OnActorEnteredPlay(__instance);
    }

    /// <summary>
    /// THE DEPLOY SIM-GATE (law 4b), on <c>TacParticipantSpawn.DeployForTurn</c>:397. The universal enter-play
    /// gate above already keeps a client's locally-rolled wave off the map, but the roll itself must not
    /// happen either: <c>DeployForTurn</c> consumes <c>SharedData.Random</c> draws and, whether or not the
    /// actor reaches play, charges <c>DeploymentPointsUsed</c> and increments every matching
    /// <c>ActorDeployLimit.SpawnedCount</c> (<c>ActorSpawned</c>:774-785) — replicated save-graph state that
    /// would then read differently on every peer. Host and solo run native.
    /// </summary>
    [HarmonyPatch(typeof(TacParticipantSpawn), nameof(TacParticipantSpawn.DeployForTurn))]
    internal static class ClientDeployGate
    {
        private static bool Prefix(int turnNumber)
        {
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null || engine.IsHost) return true;
            TacticalDamageSync.SayOnce("client-deploy",
                "[Multiplayer][tac] client deployment for turn " + turnNumber + " is SUPPRESSED — reinforcement " +
                "waves are the host's roll and arrive as 0x84 spawn records. This line is the arc working, not a " +
                "failure; a missing wave on this screen is a lost spawn record.");
            return false;
        }
    }

    /// <summary>
    /// THE DEATH CAPTURE, on the one funnel: <c>TacticalActorBase.Die</c>:624-629. A PREFIX, because the loot
    /// list has to exist before the base body activates the die ability, and because the whole
    /// ApplyDamage -> health -> OnHealthChange -> Die chain is synchronous inside the damage capture that is
    /// still unwinding — so the record it produces can ride out with the very hit that caused it.
    /// The BASE method and not the overrides: <c>TacticalActor.Die</c>:1655-1665,
    /// <c>TacticalActorYuggoth.Die</c>:170-175 and <c>StructuralTarget.Die</c> all call <c>base.Die()</c>,
    /// so one patch is every death.
    /// </summary>
    [HarmonyPatch]
    internal static class ActorDeathSeam
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(TacticalActorBase), "Die", new Type[0]);

        private static void Prefix(TacticalActorBase __instance) => TacticalActorLifecycle.OnHostDeath(__instance);
    }

    /// <summary>
    /// THE EVACUATION CRASH GUARD. <c>ExitMissionAbility.Activate</c>:32-36 calls
    /// <c>HideActorInExitZone(GetZonesContainingActor().FirstOrDefault(...))</c> UNGUARDED, and
    /// <c>EvacuateMountedActorsAbility.Activate</c>:57-69 does the same — so a null zone is an NRE at
    /// <c>exitZone.GetComponent&lt;VehicleComponent&gt;()</c>, AFTER the evacuated status has already been
    /// applied and every other status stripped. That is vanilla's bug, and a mirrored order is exactly how it
    /// gets reached: the order names no zone (zones are local scene objects), so a peer whose actor is a
    /// fraction of a tile short evaluates the same query to nothing. Refusing LOUDLY leaves the actor whole
    /// and visible instead of half-hidden and the view wedged — which is the failure mode v1 shipped.
    /// </summary>
    [HarmonyPatch]
    internal static class EvacuateZoneGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var hide = AccessTools.Method(typeof(ExitMissionAbility), nameof(ExitMissionAbility.HideActorInExitZone),
                                          new[] { typeof(TacticalExitZone) });
            var hideMounted = AccessTools.Method(typeof(EvacuateMountedActorsAbility),
                                                 nameof(EvacuateMountedActorsAbility.HideInExitZone),
                                                 new[] { typeof(TacticalExitZone) });
            if (hide != null) yield return hide;
            if (hideMounted != null) yield return hideMounted;
        }

        private static bool Prefix(TacticalAbility __instance, TacticalExitZone exitZone)
        {
            if (exitZone != null) return true;
            Debug.LogError("[Multiplayer][tac] evacuation for " +
                           TacticalActorLifecycle.SafeName(__instance == null ? null : __instance.TacticalActorBase) +
                           " is REFUSED — no unlocked exit zone contains it on this peer. Vanilla would NRE here " +
                           "with the actor already stripped of every status and marked evacuated; it stays whole " +
                           "and on the map instead. It is still evacuated on the host, so this peer's squad now " +
                           "differs by one soldier.");
            return false;
        }
    }
}
