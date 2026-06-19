using System.Collections.Generic;
using System.Text;

namespace Multipleer.Sync.Tactical
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
