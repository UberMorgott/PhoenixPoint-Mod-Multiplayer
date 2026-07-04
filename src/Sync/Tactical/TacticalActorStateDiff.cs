using System.Collections.Generic;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) core for the generic per-actor STATE-DELTA spine (surface <c>tac.actorstate</c>
    /// 0x8F, state-spine design §4/§9, Inc T1). Three responsibilities, all unit-tested in isolation (the
    /// engine glue <c>TacticalActorStateSync</c> binds the live game types and is NOT in the test assembly):
    ///
    ///   1. <see cref="StatusReconcileDiff.Compute"/> — the status-set RECONCILE diff. Given the CLIENT's
    ///      current status set and the host's incoming set (each keyed by {defGuid, sourceNetId}), compute the
    ///      minimal {ToAdd, ToRemove}. Re-applying the same set yields an empty diff (idempotent) — so the
    ///      client only ApplyStatus the genuinely-MISSING statuses and UnapplyStatus the absent ones; a status
    ///      already present is left untouched (its host-only <c>OnApply</c> never re-runs — spec risk #1).
    ///
    ///   2. <see cref="IsSyncableStatusType"/> — the status INCLUDE/EXCLUDE policy (spec risk #1 + §7). T1
    ///      syncs buffs/debuffs/stances/disables (Paralysed/Frenzy/Shield/Panic/Silenced/stat-mods/…) whose
    ///      <c>OnApply</c> only sets timers/stat-mods (safe, cosmetic state — re-applying on the client IS the
    ///      desired mirror). It EXCLUDES (a) <c>OverwatchStatus</c> — already owned by tac.overwatch.state
    ///      (0x8D), and (b) the DAMAGE-OVER-TIME family (<c>IDamageOverTimeStatus</c>: DamageOverTime/Fire/Acid/
    ///      Infected/Bleed/Zombified/ParalysisDamageOverTime) — their damage rides tac.damage (0x88), and a
    ///      client re-<c>OnApply</c> would re-arm a LOCAL DoT that double-damages.
    ///
    ///   3. <see cref="Signature"/> — a stable per-actor change signature (ap, wp, sorted status set) so the
    ///      host flush broadcasts ONLY actors whose signature drifted (idle actor = 0 bytes), mirroring
    ///      <c>TacticalVisionSync.BuildSignature</c>.
    /// </summary>
    public static class TacticalActorStateDiff
    {
        /// <summary>Sentinel for a status whose <c>Source</c> is not a resolvable actor (weapon/global/null).</summary>
        public const int SourceNetIdNone = -1;

        /// <summary>One synced status on the wire/in a set: its def guid + the netId of its source actor
        /// (-1 = none) + a carried value (the status Duration, informational). Identity = {DefGuid, SourceNetId}.</summary>
        public struct StatusRec
        {
            public string DefGuid;
            public int SourceNetId;
            public float Value;
            public StatusRec(string defGuid, int sourceNetId, float value)
            { DefGuid = defGuid ?? ""; SourceNetId = sourceNetId; Value = value; }
        }

        /// <summary>The reconcile diff: which statuses to add on the client, which to remove.</summary>
        public sealed class StatusReconcileDiff
        {
            /// <summary>Statuses in the incoming set that are ABSENT on the client (apply them).</summary>
            public readonly List<StatusRec> ToAdd = new List<StatusRec>();
            /// <summary>{DefGuid, SourceNetId} pairs known on the client but ABSENT from the incoming set (remove them).</summary>
            public readonly List<StatusRec> ToRemove = new List<StatusRec>();
            public bool HasChanges => ToAdd.Count > 0 || ToRemove.Count > 0;

            /// <summary>Compute the reconcile diff from the client's <paramref name="current"/> status set to
            /// the host's <paramref name="incoming"/> set. Identity is {DefGuid, SourceNetId}; the carried Value
            /// does NOT trigger an add/remove (a present status is left as-is — re-applying would re-run OnApply).
            /// Null is treated as empty. Re-applying the same set → empty diff (idempotent).</summary>
            public static StatusReconcileDiff Compute(IEnumerable<StatusRec> current, IEnumerable<StatusRec> incoming)
            {
                var diff = new StatusReconcileDiff();
                var cur = ToKeyMap(current);
                var inc = ToKeyMap(incoming);

                foreach (var kv in inc)
                    if (!cur.ContainsKey(kv.Key)) diff.ToAdd.Add(kv.Value);
                foreach (var kv in cur)
                    if (!inc.ContainsKey(kv.Key)) diff.ToRemove.Add(kv.Value);
                return diff;
            }

            private static Dictionary<string, StatusRec> ToKeyMap(IEnumerable<StatusRec> recs)
            {
                var map = new Dictionary<string, StatusRec>();
                if (recs == null) return map;
                foreach (var r in recs)
                {
                    string key = KeyOf(r);
                    map[key] = r;   // last-wins on a duplicate {defGuid,source} (the set should not contain dups)
                }
                return map;
            }
        }

        /// <summary>Reconcile diff entry-point (delegates to the nested type so callers read clearly).</summary>
        public static StatusReconcileDiff Compute(IEnumerable<StatusRec> current, IEnumerable<StatusRec> incoming)
            => StatusReconcileDiff.Compute(current, incoming);

        /// <summary>Stable identity key for a status: {DefGuid '|' SourceNetId}. Value is intentionally NOT part
        /// of the key so a duration tick / value drift never forces a remove+re-add (which would re-run OnApply).</summary>
        public static string KeyOf(StatusRec r) => (r.DefGuid ?? "") + "|" + r.SourceNetId;

        // ─── Feature B: VISUAL-ONLY status mirror policy (VisibleOnHealthbar → mirror as inert icon) ───────
        //
        // POLICY SHIFT vs the old type-name allowlist: the generic delta now mirrors a status purely for its
        // HEALTHBAR ICON. The decision is the engine's own healthbar-visibility flag — TacStatusDef.
        // VisibleOnHealthbar, an enum {Hidden=0, VisibleWhenSelected=1, AlwaysVisible=5} — NOT the C# type. A
        // status that shows an icon on the host (visibility != Hidden) is mirrored; everything else (Hidden, or
        // a non-tactical status with no such field) is default-DENY. The mirrored status is made INERT on the
        // client: the spine pre-sets Status.Applied=true before ApplyStatus (engine deserialize path → subclass
        // OnApply skips its gameplay effects), and ClientStatusMirrorGuards skips the per-turn StartTurn/EndTurn/
        // ApplyEffect ticks + the effect-reverting OnUnapply — so NO effect runs (no double DoT damage, AP drain,
        // faction flip, or stat double-apply). Identity stays {DefGuid, SourceNetId}; the reconcile diff +
        // signature below are unchanged.

        /// <summary>The TacStatusDef.HealthBarVisibility enum value for "Hidden" (the engine default 0). The
        /// engine glue reads the live enum and passes its int value here.</summary>
        public const int HealthBarVisibilityHidden = 0;

        /// <summary>Status types EXCLUDED from the visual mirror even when visible-on-healthbar. Two reasons:
        /// (1) a DEDICATED sync surface already owns the cosmetic replication — mirroring here too would create a
        /// duplicate instance / fight that surface (<c>OverwatchStatus</c>, owned by tac.overwatch.state 0x8D,
        /// which (re)creates + SetCone the cone status on the client); (2) FACTION-SAFETY — the status flips the
        /// actor's faction, so it must never run live on the client even as an "inert" mirror.
        /// <c>MindControlStatus</c> leaks an UNCONDITIONAL ActorDeathEvent subscription regardless of the Applied
        /// pre-set, and <c>ZombifiedStatus</c> would flip the faction if the pre-set ever fails — a wrong-faction
        /// actor on the client is far worse than a missing badge, so the icon is dropped for both. The DoT family
        /// is deliberately NOT excluded: its damage rides tac.damage (0x88) but its ICON (Bleed/Fire/Acid/…) is
        /// exactly what Feature B mirrors — made inert by the apply-time Applied pre-set + the ApplyEffect guard,
        /// so the icon shows with no client-side tick.</summary>
        private static readonly HashSet<string> SurfaceOwnedStatusTypeNames = new HashSet<string>
        {
            "OverwatchStatus",
            "MindControlStatus",   // flips faction + leaks an unconditional ActorDeathEvent subscription
            "ZombifiedStatus",     // flips faction if the Applied pre-set ever fails
        };

        /// <summary>PURE mirror decision: a status is mirrored as an INERT healthbar icon iff its host
        /// VisibleOnHealthbar is NOT Hidden (it draws an icon on the host) AND it is not owned by a dedicated
        /// sync surface (<see cref="SurfaceOwnedStatusTypeNames"/>). Default-DENY: a Hidden status / a non-
        /// tactical status with no visibility (caller passes Hidden) / a surface-owned status is not mirrored.
        /// Unit-tested.</summary>
        public static bool ShouldMirrorStatus(int healthBarVisibility, string simpleTypeName = null)
        {
            if (healthBarVisibility == HealthBarVisibilityHidden) return false;
            if (!string.IsNullOrEmpty(simpleTypeName) && SurfaceOwnedStatusTypeNames.Contains(simpleTypeName))
                return false;
            return true;
        }

        /// <summary>PURE magnitude→accumulator mapping for the inert status mirror (bug C). The host's carried
        /// <c>Status.Value</c> is the DISPLAY level; the client seeds <c>DamageAccumulation.InitialAmount</c> so the
        /// mirrored icon shows that level. BleedStatus.Value = (int)InitialAmount → InitialAmount = value (no
        /// DamagePerTurn). DamageOverTimeStatus.Value = InitialAmount / DamagePerTurn → InitialAmount =
        /// value × DamagePerTurn. A NaN / non-positive <paramref name="damagePerTurn"/> (a non-DoT status such as
        /// Bleed, which has no such property) maps 1:1. (BleedStatus.cs:29,116; DamageOverTimeStatus.cs:21,25,184)</summary>
        public static float StatusMagnitudeToInitialAmount(float value, float damagePerTurn)
        {
            if (float.IsNaN(damagePerTurn) || damagePerTurn <= 1e-05f) return value;
            return value * damagePerTurn;
        }

        /// <summary>Magnitude-drift tolerance for the Inc2-follow-up in-place status-magnitude refresh. The
        /// reconcile diff identity {DefGuid, SourceNetId} ignores <c>Value</c>, so a magnitude change on an
        /// ALREADY-present status is invisible to ToAdd/ToRemove; this epsilon gates the SEPARATE in-place refresh
        /// so a sub-epsilon float jitter never re-writes the mirror's <c>DamageAccumulation</c> (no churn). Bleed
        /// and DoT display levels are whole numbers, so 0.01 is far below any real change.</summary>
        public const float StatusMagnitudeEpsilon = 0.01f;

        /// <summary>PURE refresh decision: should an ALREADY-mirrored status's display magnitude be refreshed IN
        /// PLACE because the host value DRIFTED (bleed stacking from a 2nd shot, a DoT ticking down each turn)?
        /// True iff |mirrored − incoming| &gt; <paramref name="eps"/>; equal / sub-epsilon → no-op. The engine
        /// glue then re-derives <c>DamageAccumulation.InitialAmount</c> via <see cref="StatusMagnitudeToInitialAmount"/>
        /// and sets the field directly (NEVER DoT <c>SetValue</c>) — see TacticalActorStateSync.RefreshMirrorMagnitude.
        /// Unit-tested.</summary>
        public static bool ShouldRefreshMagnitude(float mirroredValue, float incomingValue, float eps = StatusMagnitudeEpsilon)
            => System.Math.Abs(mirroredValue - incomingValue) > eps;

        // ─── Feature D: actor-level absolute HEALTH mirror decision (DEATH-SAFE) ──────────────────────────

        /// <summary>HP-equality tolerance so a sub-epsilon float jitter never re-applies the actor HP (and never
        /// re-fires the native Health StatChangeEvent → UI churn). Shares the bodypart epsilon — whole-HP game.</summary>
        public const float HealthEpsilon = BodyPartHpEpsilon;

        /// <summary>The death threshold the engine uses for the actor Health stat: <c>TacticalActorBase.</c>
        /// <c>OnHealthChange</c> calls <c>Die()</c> iff a <c>StatChangeType.Value</c> change crosses
        /// <c>prevValue &gt;= 1E-05</c> → <c>Value &lt; 1E-05</c>. So setting HP to ANY value &gt;= this is
        /// death-safe; setting to &lt; this (i.e. ~0) would trigger death. We mirror only strictly-positive HP.</summary>
        public const float HealthDeathThreshold = 1e-05f;

        /// <summary>The PURE death-safe actor-HP mirror decision. Given the client mirror's current HP and the
        /// host's incoming absolute HP, decide whether to SET it (and to what value):
        ///   • incoming &lt;= 0 (≤ <see cref="HealthDeathThreshold"/>) → DO NOT apply. DEATH is owned by
        ///     tac.damage (0x88); setting HP through ~0 here would fire the engine's OnHealthChange → Die() and
        ///     double-trigger death/effects. <paramref name="apply"/>=false.
        ///   • incoming &gt; 0 but equal to the current within <see cref="HealthEpsilon"/> → no-op (already
        ///     converged; avoid re-firing StatChangeEvent). <paramref name="apply"/>=false.
        ///   • incoming &gt; 0 and drifted from current → APPLY the incoming value (heal or drift correction).
        ///     Setting a strictly-positive HP can NEVER cross to &lt; threshold, so it is death-safe.
        /// <paramref name="valueToSet"/> is the clamped value to write (only meaningful when apply=true; it is
        /// the raw incoming HP, which is already &gt; 0). Default-safe: any non-positive incoming → skip.</summary>
        public static bool ShouldApplyHealthMirror(float currentClientHp, float incomingHp, out float valueToSet)
        {
            valueToSet = currentClientHp;
            // DEATH-SAFE: a non-positive incoming HP is NEVER applied via this path (tac.damage owns death).
            if (incomingHp <= HealthDeathThreshold) return false;
            // Already converged (within epsilon) → no-op, do not re-fire the stat-change event.
            if (System.Math.Abs(currentClientHp - incomingHp) <= HealthEpsilon) return false;
            valueToSet = incomingHp;
            return true;
        }

        // ─── Inc1 full-state: position-delta WALK-vs-TELEPORT decision (PURE, distance-driven) ────────────

        /// <summary>How the client should PRESENT an incoming absolute-position delta for a mirrored actor.</summary>
        public enum PositionApplyMode
        {
            /// <summary>Already converged (≤ <see cref="PositionEpsilon"/>) — do nothing (no Navigate, no
            /// SetPosition) so an idle re-apply never churns the transform or re-fires ActorMovedEvent.</summary>
            None,
            /// <summary>Drive the NATIVE walk animation (<c>TacticalNavigationComponent.Navigate</c>) — a
            /// plausible single move: the soldier WALKS to the new cell instead of teleporting.</summary>
            Walk,
            /// <summary>Snap instantly (<c>ActorComponent.SetPosition</c>) — either a SUB-CELL nudge (a path
            /// of 0/1 nodes would over-run the native ExecutePoints animator) OR a LARGE/disconnected jump
            /// (first-seen pos, post-packet-loss catch-up, evac/relocate) that must not animate an absurd
            /// cross-map walk.</summary>
            Teleport,
        }

        /// <summary>Below this distance the incoming pos is treated as already-there → no-op (avoid re-firing
        /// the native ActorMovedEvent on sub-epsilon float jitter). Far below one grid cell.</summary>
        public const float PositionEpsilon = 0.05f;

        /// <summary>The WALK floor: at/above this distance the delta animates a native walk; below it (but above
        /// <see cref="PositionEpsilon"/>) the path is degenerate (0/1 nodes) and would over-run the native
        /// ExecutePoints animator, so it SNAPS. Equal to the move rail's <c>MoveAnimateMinDist</c> (PP tactical
        /// grid cell ≈ 1 world unit) so the delta path and the tac.move.start rail make the SAME choice.</summary>
        public const float PositionWalkMinDist = 1.0f;

        /// <summary>The TELEPORT ceiling: at/below this distance a plausible move animates; ABOVE it the delta is
        /// a large/disconnected jump (first-seen pos, post-loss catch-up, evac/relocate) → SNAP instead of
        /// animating an absurdly long cross-map walk. Generous (well past any single legitimate move range) so a
        /// normal move always walks; only a genuine teleport-scale gap snaps.</summary>
        public const float PositionTeleportMaxDist = 40.0f;

        /// <summary>PURE walk-vs-teleport decision for an incoming absolute-position delta, given ONLY the
        /// distance from the actor's current mirror pos to the incoming pos (the engine glue computes it via
        /// <c>Vector3.Distance</c>, keeping this function Unity-free + unit-testable). Bands:
        ///   • dist ≤ <see cref="PositionEpsilon"/>                       → <see cref="PositionApplyMode.None"/>
        ///   • <see cref="PositionEpsilon"/> &lt; dist &lt; <see cref="PositionWalkMinDist"/> → Teleport (sub-cell)
        ///   • <see cref="PositionWalkMinDist"/> ≤ dist ≤ <see cref="PositionTeleportMaxDist"/> → Walk
        ///   • dist &gt; <see cref="PositionTeleportMaxDist"/>            → Teleport (disconnected jump)
        /// A NaN/negative distance is treated as Teleport (safest: snap rather than animate an unknown path).</summary>
        public static PositionApplyMode DecidePositionApply(float distance)
        {
            if (float.IsNaN(distance)) return PositionApplyMode.Teleport;
            if (distance <= PositionEpsilon) return PositionApplyMode.None;
            if (distance < PositionWalkMinDist) return PositionApplyMode.Teleport;       // sub-cell nudge → snap
            if (distance > PositionTeleportMaxDist) return PositionApplyMode.Teleport;   // disconnected jump → snap
            return PositionApplyMode.Walk;
        }

        // ─── Inc2: facing-vector change decision (PURE, per-component epsilon) ─────────────────────────────

        /// <summary>Facing-vector equality tolerance: below this per-component delta the forward is "unchanged" so a
        /// sub-epsilon jitter never re-broadcasts (host signature) nor re-applies (client) — avoids re-firing the
        /// native ActorMovedEvent. Tactical actors are yaw-only and the forward is unit-length, so 0.01 is far below
        /// any real turn.</summary>
        public const float FacingEpsilon = 0.01f;

        /// <summary>PURE: did the forward vector change beyond <see cref="FacingEpsilon"/> on any component? Unity-free
        /// (the glue passes the 3 components) so the host signature and the client skip make the SAME decision.</summary>
        public static bool FacingChanged(float ax, float ay, float az, float bx, float by, float bz)
            => System.Math.Abs(ax - bx) > FacingEpsilon
            || System.Math.Abs(ay - by) > FacingEpsilon
            || System.Math.Abs(az - bz) > FacingEpsilon;

        // ─── Feature B PART 1: per-bodypart-HP RECONCILE diff (limb-disable mirror) ───────────────────────

        /// <summary>One bodypart HP entry: the slot name (stable host↔client key) + the part's absolute HP.</summary>
        public struct BodyPartHpRec
        {
            public string SlotName;
            public float Hp;
            public BodyPartHpRec(string slotName, float hp) { SlotName = slotName ?? ""; Hp = hp; }
        }

        /// <summary>HP-equality tolerance so a sub-epsilon float jitter never re-applies (and never re-fires the
        /// native StatChangeEvent → UI churn). A whole-HP game so 0.01 is far below any real change.</summary>
        public const float BodyPartHpEpsilon = 0.01f;

        /// <summary>PURE limb-HP diff: given the CLIENT's current per-slot HP (<paramref name="current"/>, keyed
        /// by slot name) and the host's <paramref name="incoming"/> set, return the entries to APPLY on the
        /// client — a part the client lacks, or whose HP drifted beyond <see cref="BodyPartHpEpsilon"/>. A
        /// part already at the host HP is a no-op (not returned), so re-applying the same set yields an empty
        /// list (idempotent) — and the native StatChangeEvent only fires when an HP actually changes. A part the
        /// host does not mention is left untouched (we only ever push host HP down/over; absent ≠ remove, since a
        /// disabled limb stays at 0). Unit-tested.</summary>
        public static List<BodyPartHpRec> ComputeBodyPartHpDiff(
            IDictionary<string, float> current, IEnumerable<BodyPartHpRec> incoming)
        {
            var toApply = new List<BodyPartHpRec>();
            if (incoming == null) return toApply;
            foreach (var inc in incoming)
            {
                if (string.IsNullOrEmpty(inc.SlotName)) continue;
                bool have = current != null && current.TryGetValue(inc.SlotName, out float cur);
                if (!have || System.Math.Abs(GetOrZero(current, inc.SlotName) - inc.Hp) > BodyPartHpEpsilon)
                    toApply.Add(inc);
            }
            return toApply;
        }

        private static float GetOrZero(IDictionary<string, float> map, string key)
            => (map != null && map.TryGetValue(key, out float v)) ? v : 0f;

        // ─── Status INCLUDE/EXCLUDE policy (spec risk #1 / §7) — DEFAULT-DENY allowlist ─────────────────
        //
        // SAFE-BY-CONSTRUCTION: the generic delta carries a status ONLY if its simple type name is on a VETTED
        // allowlist. This is deliberately default-DENY (not a blacklist) so an unreviewed status — a future
        // engine status, a DLC status, a TFTV/mod-added status — NEVER auto-syncs. Re-running a status's
        // OnApply on the client diverges for many types, e.g.:
        //   • MindControlStatus — flips the actor's faction + subscribes ActorDeathEvent (host-only side effects).
        //   • StunStatus        — reduces AP + fires a suppression event.
        //   • PanicStatus       — drives an AI/behaviour state machine.
        //   • StatsModifyStatus — re-adds its AP/WP stat delta ON TOP of the host's absolute AP/WP value.
        //   • OverwatchStatus   — owned by tac.overwatch.state (0x8D) → double cosmetic cone.
        //   • IDamageOverTimeStatus (DamageOverTime/Fire/Acid/Infected/Bleed/Zombified/ParalysisDoT) — damage
        //     rides tac.damage (0x88); a client re-OnApply re-arms a LOCAL ticking DoT → double damage.
        // These KNOWN-UNSAFE types must NEVER be added to the allowlist. Status sync stays GATED OFF
        // (TacticalActorStateSync.SyncStatuses == false) until a later increment populates a vetted allowlist
        // AND 2-INSTANCE VERIFIES it (MindControl + Stun are the must-test divergence cases).

        /// <summary>VETTED allowlist of status simple type names safe to mirror via the generic delta. Empty in
        /// T1 — status sync is GATED OFF (<c>TacticalActorStateSync.SyncStatuses == false</c>), so no status
        /// reaches the wire. Populate (one vetted type at a time, 2-instance verified) before flipping
        /// SyncStatuses on.
        ///
        /// TODO: vetted allowlist before enabling SyncStatuses. NEVER allow the KNOWN-UNSAFE types:
        ///   MindControlStatus, StunStatus, PanicStatus, OverwatchStatus, and ALL IDamageOverTimeStatus
        ///   (DamageOverTimeStatus/FireStatus/AcidStatus/InfectedStatus/BleedStatus/ZombifiedStatus/
        ///   ParalysisDamageOverTimeStatus) — they re-run host-only OnApply side effects on the client.</summary>
        private static readonly HashSet<string> AllowedStatusTypeNames = new HashSet<string>
        {
            // (empty — status sync gated off in T1; add vetted types here, 2-instance verified, before enabling)
        };

        /// <summary>True ONLY if a status of this simple type name is on the VETTED allowlist (default-DENY). A
        /// null/empty/unreviewed name is denied. PURE so the policy is unit-tested. Enabling a type requires
        /// adding it to <see cref="AllowedStatusTypeNames"/> AND 2-instance verification.</summary>
        public static bool IsSyncableStatusType(string simpleTypeName)
        {
            if (string.IsNullOrEmpty(simpleTypeName)) return false;
            return AllowedStatusTypeNames.Contains(simpleTypeName);
        }

        // ─── Per-actor change signature ─────────────────────────────────────────────────────────────────

        /// <summary>Order-stable per-actor change signature over {ap, wp, status set}. Two states with the same
        /// AP/WP and the same status set (regardless of enumeration order) compare equal, so the host flush
        /// skips a re-broadcast when nothing drifted. Statuses are sorted by {DefGuid, SourceNetId} and include
        /// the carried Value (a duration change IS a visible change worth re-broadcasting). Mirrors
        /// <c>TacticalVisionSync.BuildSignature</c>.</summary>
        public static string Signature(float ap, float wp, IEnumerable<StatusRec> statuses)
        {
            var list = new List<StatusRec>();
            if (statuses != null) list.AddRange(statuses);
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.DefGuid ?? "", b.DefGuid ?? "");
                if (c != 0) return c;
                return a.SourceNetId.CompareTo(b.SourceNetId);
            });
            var sb = new StringBuilder();
            sb.Append(ap.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
            sb.Append(wp.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('#');
            foreach (var s in list)
                sb.Append(s.DefGuid ?? "").Append(':').Append(s.SourceNetId).Append(':')
                  .Append(s.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }
    }
}
