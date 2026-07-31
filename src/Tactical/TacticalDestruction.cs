using System;
using System.Collections.Generic;
using System.IO;
using Base;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels.Destruction;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// TACTICAL ARC A6, PART 2 — THE DESTRUCTIBLE ENVIRONMENT. Walls, floors, windows and props break on the
    /// host; without this arc they stay solid on every other peer, so cover, line of fire and navigation are
    /// computed against a building one player already knocked down.
    ///
    /// NO NEW SURFACE. It is op <see cref="TacticalDamageSync.OpEnvDamage"/> on 0x84, for the reason A4 gave
    /// for spawn and death: 0x84 is the discrete-event stream with the contiguity check and the resnapshot,
    /// and ORDER against the actor-damage records on the same stream is load-bearing — the wall a grenade
    /// removes and the soldier the same grenade wounds must not swap places on the wire.
    ///
    /// THE GAP IT CLOSES. A3b's receiver address is <c>(actorKey, IDamageReceiver.GetSlotName())</c>, and a
    /// destructible answers <c>GetActor()</c> with NULL and <c>GetSlotName()</c> with the constant
    /// "DestructableObject" (<c>DestructableDamageReceiver</c>:38-71) — every wall in the mission shares one
    /// address and none of them has an actor. So the existing 0x84 path could never have carried environment
    /// damage; it needed its own address, which is what this op is. The client-side neuter, meanwhile, was
    /// ALREADY total: <c>AccumulationClientGate</c> skips <c>DamageAccumulation.ApplyAddedDamage</c>, which is
    /// what every shot, burst and explosion reaches the destructible receivers through.
    ///
    /// THE IDENTITY, AND WHY v1's WAS DEAD MISSION-WIDE. v1 (`fc661b7`) keyed the right thing —
    /// <c>DestructableBase.GuidInScene</c>, the game's OWN save key (<c>FindDestructableObject</c>:312-321 is
    /// a <c>[SerializeCustomCreate]</c> that resolves exactly it) — and then RESOLVED it the one way that
    /// cannot be relied on: <c>SceneObjectIdsComponent.GetObjectById(id, scene)</c>, whose
    /// <c>GetForScene</c>:44-46 is <c>GameObject.FindGameObjectsWithTag("SceneObjectIds")
    /// .FirstOrDefault(h =&gt; h.scene == scene)</c>. That needs an ACTIVE tagged GameObject sitting in
    /// EXACTLY the scene asked for, and map generation moves it: <c>MapPlot</c>:230-243 reparents each
    /// parcel's ids component (a reparent changes a GameObject's scene), merges it into the PLOT's component
    /// and DESTROYS it. So the surviving registry lives in the plot's scene, while v1 asked in the level's —
    /// and the game's own reader asks in <c>SceneManager.GetActiveScene()</c>, a third answer again. Every
    /// batch missed on both clients, and v1's answer was a full-scan fallback over
    /// <c>Resources.FindObjectsOfTypeAll</c> with the root cause left undug.
    ///
    /// SO THIS ARC NEVER ASKS A SCENE ANYTHING. It builds its own index the way the GAME'S OWN SAVEGAME
    /// enumerates destructibles — <c>TacLevelSavegame</c>:49
    /// <c>Map.NavigableRoot.transform.GetComponentsInChildrenStable&lt;DestructableBase&gt;()</c> — and keys
    /// it by each object's own <c>GuidInScene</c>. Both peers walk the same hierarchy of the same generated
    /// map and read the same baked guids, so the index is symmetric by construction and there is no scene
    /// lookup left to point at the wrong registry. It is also the honest test of the identity: if a guid
    /// cannot be read or is not unique, <see cref="Index"/> says so out loud at battle start rather than
    /// failing silently on the first grenade.
    ///
    /// THE RECEIVER ADDRESS IS THE AIM POINT, not the impact point. One explosion damages MANY tiles through
    /// <c>GetDamageReceiversInSphere</c> and they all share a single <c>ImpactHit.Point</c>, so the impact
    /// point names a tile only for single-ray hits. The tile's aim-point transform sits at
    /// <c>GridToWorld</c>:140-148 = tile CENTRE (+0.5, +0.5*tileHeight), and
    /// <c>Destructable.GetDamageReceiverForHit</c>:826-831 is <c>FloorToInt</c> of exactly that mapping's
    /// inverse — so feeding the aim point back through the game's own lookup returns the same tile, half a
    /// tile clear of any boundary, with no grid arithmetic of our own to drift. <c>Breakable</c>:573-577
    /// ignores the position entirely and returns its single receiver, so one address covers both kinds.
    ///
    /// FALLS ARE DERIVED, NEVER REPLICATED. <c>TacticalLevelController.CheckForFallAbilitiesToActivate</c>
    /// :1916-1935 runs from <c>OnMapUpdate</c>:1913 on EVERY peer and activates <c>FallNoSupportAbility</c> on
    /// every actor of every faction that has lost its support. That is A5's autonomy rule exactly: each peer
    /// raises its own off the same replicated board, so relaying one would drop the actor twice.
    /// <c>FallNoSupportAbility</c> is already a declared local in
    /// <see cref="TacticalCommandSync.LocalAbilities"/> and law L69 keeps it there. The fall's DAMAGE is not
    /// derived — it is ordinary actor damage, neutered on non-authoritative peers and shipped as an ordinary
    /// 0x84 record like every other hit.
    ///
    /// WHAT IS DELIBERATELY NOT HERE: a window smashed by a body walking through it. That reaches
    /// <c>Breakable.ApplyDamage(origin, direction, force)</c> straight from
    /// <c>TacticalNavigationComponent</c>:609-613 during traversal, never through a
    /// <c>DamageAccumulation</c> — and A3a already mirrors the MOVE, so every peer runs the same real
    /// traversal and breaks the same glass locally. v1 needed a second capture for it only because v1's
    /// client never ran the move at all.
    /// </summary>
    public static class TacticalDestruction
    {
        /// <summary>The battle's destructibles by their own <c>GuidInScene</c>. Rebuilt per battle, never
        /// per message: the set is fixed at map generation (nothing adds a destructible mid-mission) and a
        /// wall that has been reduced to rubble is still the same object with the same guid.</summary>
        private static readonly Dictionary<string, DestructableBase> _byGuid =
            new Dictionary<string, DestructableBase>(StringComparer.Ordinal);
        private static bool _indexed;
        private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

        internal static void Reset()
        {
            _byGuid.Clear();
            _indexed = false;
            _said.Clear();
        }

        private static void SayOnce(string key, string message)
        {
            if (_said.Add(key)) Debug.LogError(message);
        }

        // ─── IDENTITY ──────────────────────────────────────────────────────

        /// <summary>Read one destructible's own save key. <c>GuidInScene</c> is a property that reaches
        /// <c>SceneObjectIdsComponent.GetForScene(go.scene, canBeMissing: false)</c> and THROWS
        /// <c>InvalidOperationException</c> when that scene holds no tagged registry, so it is never called
        /// bare — a throwing wall must be a counted, named failure and not an exception that takes the whole
        /// index (or the capture postfix) with it.</summary>
        private static string GuidOf(DestructableBase d)
        {
            try
            {
                var id = d.GuidInScene;
                return id.IsValid ? id.GuidString : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Build the index the way the game's own savegame enumerates destructibles
        /// (<c>TacLevelSavegame</c>:49), so both peers index the same objects in the same way. Returns false
        /// when there is no map to walk yet.</summary>
        private static bool Index()
        {
            if (_indexed) return true;
            var tlc = TacticalDamageSync.Tlc();
            var map = tlc == null ? null : tlc.Map;
            var root = map == null ? null : map.NavigableRoot;
            if (root == null) return false;

            int idless = 0, collisions = 0, total = 0;
            foreach (var d in root.transform.GetComponentsInChildrenStable<DestructableBase>())
            {
                total++;
                string guid = GuidOf(d);
                if (string.IsNullOrEmpty(guid)) { idless++; continue; }
                DestructableBase other;
                if (_byGuid.TryGetValue(guid, out other) && !ReferenceEquals(other, d)) collisions++;
                _byGuid[guid] = d;
            }
            _indexed = true;
            Debug.Log("[Multiplayer][tac] indexed " + _byGuid.Count + " destructible(s) of " + total +
                      " under the navigable root, by the same GuidInScene the game's own save uses.");
            // Both of these are the v1 failure class caught at battle start instead of at the first grenade.
            if (idless > 0)
                Debug.LogError("[Multiplayer][tac] " + idless + " of " + total + " destructible(s) have NO readable " +
                               "GuidInScene, so the host cannot name them and they will never break on any other " +
                               "peer. Their scene holds no SceneObjectIds registry — the shape MapPlot:230-243 " +
                               "leaves behind when a parcel's registry was merged and destroyed.");
            if (collisions > 0)
                Debug.LogError("[Multiplayer][tac] " + collisions + " destructible guid collision(s) — " +
                               "SceneObjectIdsComponent.MergeWith:24-34 mints a FRESH random guid when a combined " +
                               "one collides, and a random guid is different on every peer. Damage to those objects " +
                               "will land on the wrong wall or nowhere at all.");
            return true;
        }

        /// <summary>The destructible the host named, or null with a reason. Never a "closest match".</summary>
        private static DestructableBase Resolve(string guid, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(guid)) { why = "the record carries no guid at all"; return null; }
            if (!Index()) { why = "this peer has no tactical map to look in"; return null; }
            DestructableBase d;
            if (_byGuid.TryGetValue(guid, out d) && d != null) return d;
            why = "no destructible under this peer's navigable root carries GuidInScene '" + guid + "' (the " +
                  "index holds " + _byGuid.Count + ")";
            return null;
        }

        // ─── HOST: capture ─────────────────────────────────────────────────

        /// <summary>The host's <c>DestructableDamageReceiver.ApplyDamage</c> postfix. Ships the destructible's
        /// own guid, the receiver's aim point (which tile) and the host's already-resolved
        /// <c>DamageResult</c> through the codec A3b already round-trip-tests.</summary>
        internal static void OnEnvironmentDamage(DestructableDamageReceiver receiver, DamageResult result)
        {
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null || !engine.IsHost) return;
            if (MirrorApplyScope.Active) return;          // never re-ship what we are re-applying
            if (receiver == null) return;
            if (result.HealthDamage <= 0f) return;        // a no-op hit changes nothing to mirror

            var destructable = receiver.Destructable();
            if (destructable == null) return;
            string guid = GuidOf(destructable);
            if (string.IsNullOrEmpty(guid))
            {
                SayOnce("envkey-" + TacticalActorLifecycle.SafeName(destructable),
                    "[Multiplayer][tac] damage to '" + TacticalActorLifecycle.SafeName(destructable) + "' is NOT " +
                    "relayed — it has no readable GuidInScene, so no other peer can be told which object broke. " +
                    "That piece of cover stays solid on every other screen for the rest of the mission.");
                return;
            }
            var aim = receiver.GetAimPoint();
            if (aim == null)
            {
                SayOnce("envaim-" + guid,
                    "[Multiplayer][tac] damage to destructible " + guid + " is NOT relayed — its receiver has no " +
                    "aim point, so no other peer can be told WHICH tile of it broke.");
                return;
            }
            var p = aim.position;
            TacticalDamageSync.Send(TacticalDamageSync.OpEnvDamage,
                "env " + TacticalActorLifecycle.SafeName(destructable) + " hp-" + result.HealthDamage.ToString("0.##"),
                w =>
                {
                    w.Write(guid);
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    DamageResultCodec.Write(w, result);
                });
        }

        // ─── PEERS: apply ──────────────────────────────────────────────────

        /// <summary>Re-apply the host's hit to the same tile of the same object, through the game's OWN
        /// receiver, so the whole native cascade — the gibs, the doomed-tile sweep, the map update and the
        /// fall abilities it raises — runs here exactly as it did there.</summary>
        internal static void ApplyEnvDamage(BinaryReader r)
        {
            string guid = r.ReadString();
            var aim = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var notes = new List<string>();
            var result = DamageResultCodec.Read(r, TacticalDamageSync.Tlc(), notes);

            string why;
            var destructable = Resolve(guid, out why);
            if (destructable == null)
            {
                SayOnce("envlost-" + guid,
                    "[Multiplayer][tac] the host broke a destructible this peer CANNOT find — " + why + ". That " +
                    "cover stays solid here while it is rubble on the host, so line of fire and pathing now " +
                    "disagree. This is the v1 mission-wide failure (fc661b7) and law L69 exists to make it " +
                    "impossible; if this line appears, the index at battle start reported why.");
                return;
            }
            // The game's own lookup, fed the address it derived the receiver from. Direction is unused by both
            // implementations (Destructable:826-831 floors the position into its grid, Breakable:573-577
            // returns its single receiver) and is passed as zero rather than invented.
            var receiver = destructable.GetDamageReceiverForHit(aim, Vector3.zero);
            if (receiver == null)
            {
                SayOnce("envtile-" + guid,
                    "[Multiplayer][tac] destructible " + guid + " has no damage receiver at " + aim + " on this " +
                    "peer — that tile is already gone here, or the two peers' grids disagree. The host's hit is " +
                    "dropped.");
                return;
            }
            if (notes.Count > 0)
                Debug.LogWarning("[Multiplayer][tac] environment damage for " + guid + " arrived with parts this " +
                                 "peer could not rebuild: " + string.Join("; ", notes.ToArray()));

            using (SyncApplyScope.Enter())
            using (MirrorApplyScope.Enter())
                receiver.ApplyDamage(result);
        }
    }

    /// <summary>Reach the receiver's private destructible without a second reflection site in the arc.</summary>
    internal static class DestructableReceiverAccess
    {
        private static System.Reflection.FieldInfo _field;

        internal static DestructableBase Destructable(this DestructableDamageReceiver receiver)
        {
            if (receiver == null) return null;
            if (_field == null)
            {
                _field = AccessTools.Field(typeof(DestructableDamageReceiver), "_destructable");
                if (_field == null)
                {
                    Debug.LogError("[Multiplayer][tac] DestructableDamageReceiver._destructable did not resolve — " +
                                   "no environment damage can be named on the wire and every wall the host breaks " +
                                   "stays solid on every other peer.");
                    return null;
                }
            }
            return _field.GetValue(receiver) as DestructableBase;
        }
    }

    /// <summary>
    /// HOST CAPTURE on the one environment funnel. <c>DestructableDamageReceiver.ApplyDamage</c> is where a
    /// destructible's health actually moves and where its destroy handler fires (:78-93), and every combat
    /// route — single shot, burst, explosion, fire — reaches it through
    /// <c>DamageAccumulation.ApplyAddedDamage</c>, which is already the client neuter. No prefix is needed
    /// here for that reason: the neuter one level up means a non-authoritative peer never arrives.
    /// </summary>
    [HarmonyPatch(typeof(DestructableDamageReceiver), nameof(DestructableDamageReceiver.ApplyDamage))]
    internal static class EnvironmentDamageSeam
    {
        private static void Postfix(DestructableDamageReceiver __instance, DamageResult damageResult)
            => TacticalDestruction.OnEnvironmentDamage(__instance, damageResult);
    }
}
