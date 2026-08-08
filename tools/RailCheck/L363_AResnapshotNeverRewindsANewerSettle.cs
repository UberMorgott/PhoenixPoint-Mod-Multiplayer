using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L363 — A RESNAPSHOT MAY NOT WRITE STATS OLDER THAN THE SETTLE THIS PEER HAS ALREADY APPLIED.
    ///
    /// 0x82 (settles) and 0x84 (resolved attacks, and the resnapshot that repairs them) are INDEPENDENT seq
    /// streams — deliberately, since an outcome on one surface must never suppress an outcome on another — so
    /// nothing in either stream can order a message against a message from the other. <c>ApplySettle</c> has
    /// had a staleness guard since L105 (<c>StatsAreStale</c>, against the death epoch); <c>ApplyResnap</c> had
    /// none at all. A resnapshot the host stamped at T could therefore land after a settle it stamped at T+2s
    /// and REWIND that actor's action and will points to numbers the host has already moved past — an
    /// invisible rewind, because both writes are legitimate host values and <c>Correct</c> reports the second
    /// one as a repair.
    ///
    /// <c>RailOrdinal</c> is the answer that already existed: one monotonic number minted for EVERY outbound
    /// envelope at the single encoder (<c>SyncProtocol</c>:45), which makes it the only key the two surfaces
    /// share. The settle's ordinal is captured at ARRIVAL (a held settle applies from the tick, outside any
    /// inbound dispatch, where the ambient ordinal is 0) and the resnapshot compares against it per actor.
    ///
    /// HEALTH IS DELIBERATELY NOT GATED: a settle never carries health, so an older resnapshot is still the
    /// newest word on it. AP and WP are the two fields both surfaces write, and the only two a late
    /// resnapshot can rewind.
    ///
    /// Falsify (each verified RED, then restored): make <c>ResnapIsStale</c> return false always → (a)
    /// control/never-stale; drop the <c>ordinal != 0</c> guard → (a) unstamped-called-stale; stop recording the
    /// ordinal in <c>ApplySettle</c> → (b); stop consulting it in <c>ApplyResnap</c> → (c).
    /// </summary>
    internal static class L363_AResnapshotNeverRewindsANewerSettle
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalDamageSync);
            var cmd = typeof(TacticalCommandSync);
            var stale = sync.GetMethod("ResnapIsStale", All);
            var last = cmd.GetMethod("LastSettleOrdinal", All);
            var settledAt = cmd.GetField("_settledAt", All);
            var applySettle = cmd.GetMethod("ApplySettle", All);
            var applyResnap = sync.GetMethod("ApplyResnap", All);
            if (stale == null || last == null || settledAt == null || applySettle == null || applyResnap == null)
            {
                yield return "L363 premise-changed: TacticalDamageSync.ResnapIsStale / " +
                             "TacticalCommandSync.LastSettleOrdinal / _settledAt / ApplySettle / ApplyResnap no " +
                             "longer all resolve. They are the whole cross-surface ordering; without them a " +
                             "resnapshot can rewind a newer settle and every arm below passes while it does.";
                yield break;
            }

            var map = settledAt.GetValue(null) as System.Collections.IDictionary;
            if (map == null)
            {
                yield return "L363 ledger-gone: TacticalCommandSync._settledAt is not a live dictionary, so nothing " +
                             "remembers which settle this peer last applied to an actor and the comparison below " +
                             "has nothing to compare against.";
                yield break;
            }

            const int Settled = 770077, Never = 770078;
            object had = map.Contains(Settled) ? map[Settled] : null;
            var found = new List<string>();
            try
            {
                map[Settled] = 500u;
                Func<uint, int, bool> ask = (o, k) => (bool)stale.Invoke(null, new object[] { o, k });

                // ── (a) EVERY CORNER OF THE RULE, EXECUTED ───────────────────────────
                if (!ask(400u, Settled))
                    found.Add("L363 rewind-allowed: a resnapshot stamped BEFORE the settle already applied to that " +
                              "actor is not called stale, so its ap/wp overwrite numbers the host has since moved " +
                              "past. Nothing reports it — both values are the host's, and the rewind reads as a " +
                              "repair.");
                if (ask(600u, Settled))
                    found.Add("L363 repair-refused: a resnapshot stamped AFTER the last settle is called stale. The " +
                              "resnapshot is the only recovery a discrete-event surface has; refusing the fresh ones " +
                              "turns the guard into the outage.");
                if (ask(0u, Settled))
                    found.Add("L363 unstamped-called-stale: a resnapshot with NO ordinal (applied outside an inbound " +
                              "dispatch, where RailOrdinal.Current is 0) is treated as older than everything. " +
                              "Ordinal 0 is 'unknown', not 'ancient'.");
                if (ask(400u, Never))
                    found.Add("L363 unsettled-actor-refused: an actor this peer has never settled has nothing for a " +
                              "resnapshot to be older THAN, and its stats were refused anyway.");
            }
            finally
            {
                if (had == null) map.Remove(Settled); else map[Settled] = had;
            }
            foreach (var f in found) yield return f;

            // ── (b) THE LEDGER IS WRITTEN WHERE THE SETTLE IS APPLIED ────────────────
            if (!Program.FieldRefs(applySettle).Any(f => f.Name == "_settledAt"))
                yield return "L363 settle-unrecorded: ApplySettle does not record the ordinal it applied. The ledger " +
                             "then stays empty, every resnapshot passes the check vacuously, and this whole law is " +
                             "green over a rail with no ordering at all — which is the vacuity trap L105's sibling " +
                             "arms already fell into once.";
            var ordinalField = typeof(TacticalCommandSync).GetNestedType("PendingSettle", All)?.GetField("Ordinal", All);
            if (ordinalField == null || ordinalField.FieldType != typeof(uint))
                yield return "L363 arrival-stamp-gone: PendingSettle carries no uint Ordinal. It has to be captured " +
                             "at ARRIVAL: a held settle applies from the standing tick, outside any inbound " +
                             "dispatch, where RailOrdinal.Current is 0 — read at apply time it would stamp every " +
                             "held settle as unknown and the guard would never fire for the settles most likely to " +
                             "be raced.";

            // ── (c) AND THE RESNAPSHOT ACTUALLY ASKS ─────────────────────────────────
            var asks = Program.Callees(applyResnap, sync.Assembly).ToList();
            if (!asks.Any(c => c.Name == "ResnapIsStale"))
                yield return "L363 resnap-does-not-ask: ApplyResnap never consults the rule. Proven and unused is " +
                             "the same as absent.";
            if (!asks.Any(c => c.Name == "get_Current" && c.DeclaringType == typeof(RailOrdinal)))
                yield return "L363 resnap-unstamped: ApplyResnap never reads RailOrdinal.Current, so whatever it " +
                             "compares, it is not this message's own place in the cross-surface order — and no " +
                             "other key the two surfaces both carry exists.";
        }
    }
}
