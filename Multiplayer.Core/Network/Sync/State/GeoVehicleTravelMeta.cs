using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 — one vehicle's mirrored TRAVEL METADATA: the display-feeding native nav state the sim-frozen
    /// client can NOT derive on its own (its <c>GeoNavComponent</c> never runs, so <c>DestinationSites</c> /
    /// <c>CurrentSite</c> / <c>Travelling</c> stay frozen at join while the pivot moves via the 0xA5 position
    /// mirror). The native geoscape route-line renderer (<c>UIStateVehicleSelected.DrawCurrentPath</c> →
    /// <c>GeoscapeView.DrawVehiclePathLinks</c>/<c>UpdateVehicleFirstPathLink</c>) reads EXACTLY these fields:
    ///   * <c>Travelling</c>  — gate: the line only draws while travelling.
    ///   * <c>CurrentSite</c> — the line's ORIGIN when parked at a site (null in transit → origin = live
    ///     WorldPosition, which IS mirrored). A stale non-null CurrentSite pins the origin to the wrong site.
    ///   * <c>DestinationSites</c> — the remaining waypoints (drawn as the yellow ConfirmedSiteLink chain).
    /// Mirroring these host→client (display-only, NEVER driving the frozen sim) makes the native yellow line
    /// draw from the same authoritative state the host reads. Sites are carried by <c>GeoSite.SiteId</c> (stable
    /// int), the vehicle by the composite (OwnerId, VehicleId) key shared with <see cref="GeoVehiclePos"/>.
    ///
    /// Pure value type + wire codec (no UnityEngine / SyncEngine dependency) so the round-trip and change
    /// signature are directly unit-testable (mirrors <see cref="GeoVehiclePos"/> / <see cref="GeoVehicleSnapshot"/>).
    /// The engine glue (host read + client apply) lives in <c>GeoVehicleTravelMirror</c> (game-bound reflection).
    /// </summary>
    public readonly struct GeoVehicleTravelMeta : IEquatable<GeoVehicleTravelMeta>
    {
        public readonly int OwnerId;        // StableOwnerKey(owner faction def name) — shared with GeoVehiclePos
        public readonly int VehicleId;      // per-faction VehicleID
        public readonly bool Travelling;    // GeoVehicle.Travelling
        public readonly int CurrentSiteId;  // GeoSite.SiteId of CurrentSite, or -1 (null / in transit)
        public readonly int[] DestSiteIds;  // remaining DestinationSites' SiteIds, in order (never null)
        public readonly GeoVehicleHealthTail Health; // WA-3 aircraft HP/repair tail; null = not carried (never a clear)

        public GeoVehicleTravelMeta(int ownerId, int vehicleId, bool travelling, int currentSiteId, int[] destSiteIds,
                                    GeoVehicleHealthTail health = null)
        {
            OwnerId = ownerId;
            VehicleId = vehicleId;
            Travelling = travelling;
            CurrentSiteId = currentSiteId;
            DestSiteIds = destSiteIds ?? Array.Empty<int>();
            Health = health;
        }

        /// <summary>The composite mirror key (VehicleID alone is only per-faction unique) — shared key-space
        /// with <see cref="GeoVehiclePos"/> so both surfaces resolve the SAME live vehicle.</summary>
        public long Key => GeoVehiclePos.MakeKey(OwnerId, VehicleId);

        // Native GeoVehicle Animator "State" integer (decompile-verified GeoVehicle.cs): the flight-visual state
        // machine that orients the turbine/engine tracer VFX. PARKED=0 (idle hover config), TRAVELLING=1 (forward-
        // flight config: turbines aimed back → trail streams behind). Natively set to 1 by InitiateTravelling()
        // (:583, invoked from inside GeoNavComponent.NavigateRoutine :100) and back to 0 on arrival (:344 / :513).
        public const int AnimStateParked = 0;
        public const int AnimStateTravelling = 1;

        /// <summary>PURE: map the mirrored <see cref="Travelling"/> flag to the native GeoVehicle Animator "State"
        /// integer. On a sim-frozen co-op CLIENT the native <c>NavigateRoutine</c> never runs, so
        /// <c>InitiateTravelling()</c> (the SOLE writer of State=1) never fires and the Animator stays PARKED while
        /// the position mirror flies the vehicle — leaving the turbine tracer VFX in its parked orientation (renders
        /// IN FRONT instead of trailing). The travel-meta mirror writes this value to close that gap. Unit-testable
        /// without Unity.</summary>
        public static int AnimatorTravelState(bool travelling)
            => travelling ? AnimStateTravelling : AnimStateParked;

        /// <summary>HOST re-ship window (poll count) for a newly-appeared DAMAGED vehicle's HP tail — see
        /// <c>GeoVehicleTravelMirror</c>. A stolen / interception-damaged craft ACQUIRED mid-session ships its
        /// HP tail once, but its GeoVehicleChannel #6 identity mirror may not have SPAWNED the client vehicle yet
        /// when this change-only tail lands (<c>ApplyTravelMeta.ResolveVehicle</c> no-ops), so the value never
        /// re-delivers and the mirror shows full BaseStats HP. Re-shipping the tail for a few polls outlasts the
        /// #6 spawn delivery. ponytail: fixed count, not an ack — the hourly-repair re-ship + the #6/0xA6
        /// reconverge heal the rare spawn slower than the window.</summary>
        public const int ReshipWindowPolls = 3;

        /// <summary>PURE: does a vehicle seen for the FIRST time in the host travel poll need a re-ship window?
        /// Only a genuinely-NEW (unknown-key) vehicle carrying a NON-pristine HP tail (damaged / repairing) —
        /// exactly the mid-session-acquired damaged craft (stolen / post-interception). An already-KNOWN
        /// vehicle's HP change is caught by the normal signature change-detect (its mirror already exists on the
        /// client); a pristine new craft has nothing to deliver. Unit-testable without Unity.</summary>
        public static bool NeedsReshipWindow(bool known, GeoVehicleHealthTail health)
            => !known && health != null && !health.IsPristine;

        public bool Equals(GeoVehicleTravelMeta o)
        {
            if (OwnerId != o.OwnerId || VehicleId != o.VehicleId || Travelling != o.Travelling
                || CurrentSiteId != o.CurrentSiteId) return false;
            if (DestSiteIds.Length != o.DestSiteIds.Length) return false;
            for (int i = 0; i < DestSiteIds.Length; i++)
                if (DestSiteIds[i] != o.DestSiteIds[i]) return false;
            return Health == null ? o.Health == null : Health.Equals(o.Health);
        }

        public override bool Equals(object obj) => obj is GeoVehicleTravelMeta o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = OwnerId;
                h = (h * 397) ^ VehicleId;
                h = (h * 397) ^ (Travelling ? 1 : 0);
                h = (h * 397) ^ CurrentSiteId;
                foreach (var id in DestSiteIds) h = (h * 397) ^ id;
                h = (h * 397) ^ (Health?.GetHashCode() ?? 0);
                return h;
            }
        }

        /// <summary>Order-stable change signature: the HOST skips a vehicle whose travel metadata is unchanged
        /// since the last flush (parked/unchanged = 0 bytes), so 0xA6 only ships on a genuine travel transition
        /// (start / waypoint passed / stop). Distinct from the 0xA5 position signature (which fires ~every poll
        /// while moving) — metadata changes rarely.</summary>
        public static string Signature(GeoVehicleTravelMeta v)
        {
            var sb = new StringBuilder(24 + v.DestSiteIds.Length * 6);
            sb.Append(v.Travelling ? '1' : '0').Append('|').Append(v.CurrentSiteId).Append('|');
            for (int i = 0; i < v.DestSiteIds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(v.DestSiteIds[i].ToString(CultureInfo.InvariantCulture));
            }
            // WA-3 health tail — part of the change signature so an HP tick (hourly RepairFactionAircrafts,
            // GeoLevelController.cs:815) or a repair-flag flip re-ships the vehicle even while parked. A
            // tail-less read appends nothing (signature identical to the pre-WA-3 format).
            if (v.Health != null)
                sb.Append("|h").Append(v.Health.Hp).Append('/').Append(v.Health.MaxHp)
                  .Append('/').Append(v.Health.IsRepairing ? '1' : '0');
            return sb.ToString();
        }
    }

    /// <summary>
    /// PURE optional-tail record of one aircraft's HP/repair display state — WA-3 of the 2026-07-05
    /// popup-mirror spec §5 (audit gap 5d, GeoVehicle.cs:105/47/138-150). Null = not carried (host read
    /// miss / older payload — NEVER a clear). Decompile-verified 2026-07-05:
    ///   • <c>Hp</c>/<c>MaxHp</c> — <c>GeoVehicle.Stats.HitPoints</c>/<c>MaxHitPoints</c> (public int FIELDS
    ///     on <c>GeoVehicleStats</c>). Host reads the fields; the client writes them DIRECTLY (wallet-style
    ///     silent value write): the native writer <c>SetHitpoints</c> (GeoVehicle.cs:708) fires
    ///     <c>OnMaintenanceChanged</c> + <c>OnReachingMaintenanceLimit</c>/<c>OnAircraftFullyRepairing</c>
    ///     — SIM cascade on the frozen client.
    ///   • <c>IsRepairing</c> — <c>GeoVehicle.IsRepairing =&gt; _maintenancePointsToRepair &gt; 0</c>
    ///     (GeoVehicle.cs:150). The client stamps the private backing int to a 1/0 sentinel (display-only;
    ///     its consumer <c>ScheduleRepair</c>/hourly repair never runs on the frozen client).
    /// </summary>
    public sealed class GeoVehicleHealthTail : IEquatable<GeoVehicleHealthTail>
    {
        public readonly int Hp;
        public readonly int MaxHp;
        public readonly bool IsRepairing;

        public GeoVehicleHealthTail(int hp, int maxHp, bool isRepairing)
        {
            Hp = hp;
            MaxHp = maxHp;
            IsRepairing = isRepairing;
        }

        /// <summary>True iff there is nothing to mirror: full HP and not repairing — the host poll's
        /// initial-suppress check (a pristine parked vehicle stays 0 bytes, matching the pre-WA-3 walk).</summary>
        public bool IsPristine => Hp >= MaxHp && !IsRepairing;

        public bool Equals(GeoVehicleHealthTail other)
            => other != null && Hp == other.Hp && MaxHp == other.MaxHp && IsRepairing == other.IsRepairing;

        public override bool Equals(object obj) => obj is GeoVehicleHealthTail o && Equals(o);

        public override int GetHashCode()
        {
            unchecked { return ((Hp * 397) ^ MaxHp) * 397 ^ (IsRepairing ? 1 : 0); }
        }

        public override string ToString() => $"Health({Hp}/{MaxHp} repairing={IsRepairing})";
    }

    /// <summary>
    /// Decoded GeoVehicle travel-metadata batch (Inc4 S2 — surface <see cref="SurfaceIds.GeoVehicleTravel"/> 0xA6):
    /// each CHANGED vehicle's {travelling, currentSiteId, destSiteIds} pushed host→all so the frozen client's
    /// native route-line renderer reads correct nav state. Pure data + wire codec — unit-testable, no game refs.
    ///
    /// Wire payload (inside the 0x67 envelope, surface GeoVehicleTravel):
    ///   [u32 seq][u16 count]{ [i32 OwnerId][i32 VehicleId][u8 travelling][i32 currentSiteId][u16 destCount][i32 destSiteId]* }*
    /// The leading seq is the host's per-surface <see cref="SurfaceSeq"/> value (client drops a stale/dup seq).
    ///
    /// WA-3 EXTRAS BLOCK (versioned optional tail — the GeoSiteSnapshot extras precedent): appended AFTER the
    /// record array, and ONLY when ≥1 vehicle carries a health tail — a no-tail payload stays byte-identical
    /// to the pre-WA-3 wire. An older decoder never reads past the record array (trailing bytes ignored); a
    /// newer decoder reads the block only when bytes remain (older payload → all tails null). Layout:
    ///   [u16 extrasCount] { [i32 ownerId][i32 vehicleId] [u16 recLen] [u8 tailFlags] [payloads in bit order…] }*
    ///   recLen = byte length of tailFlags + payloads (parse-known-then-skip: future tails take new HIGHER
    ///   bits with payloads appended after the known ones, so an old decoder skips per record).
    ///   tailFlags: bit0 = health tail → payload [i32 hp][i32 maxHp]; bit4 = IsRepairing VALUE (no payload).
    ///              bits 1-3/5-7 RESERVED for future tails.
    ///   An extras record whose composite key is absent from the record array is skipped (join-by-key).
    /// </summary>
    public static class GeoVehicleTravelSnapshot
    {
        private const byte TailHasHealth = 1 << 0;    // payload: [i32 hp][i32 maxHp]
        private const byte TailIsRepairing = 1 << 4;  // value bit (meaningful only with TailHasHealth)

        public static byte[] Encode(uint seq, IList<GeoVehicleTravelMeta> vehicles)
        {
            vehicles = vehicles ?? new List<GeoVehicleTravelMeta>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write((ushort)vehicles.Count);
                foreach (var v in vehicles)
                {
                    w.Write(v.OwnerId);
                    w.Write(v.VehicleId);
                    w.Write((byte)(v.Travelling ? 1 : 0));
                    w.Write(v.CurrentSiteId);
                    var dests = v.DestSiteIds ?? Array.Empty<int>();
                    w.Write((ushort)dests.Length);
                    foreach (var id in dests) w.Write(id);
                }
                // WA-3 extras block — written ONLY when ≥1 vehicle carries a tail, so a no-tail payload stays
                // byte-identical to the pre-WA-3 wire (existing pins hold; older decoders ignore the block).
                int extras = 0;
                foreach (var v in vehicles) if (v.Health != null) extras++;
                if (extras > 0)
                {
                    w.Write((ushort)extras);
                    foreach (var v in vehicles)
                    {
                        if (v.Health == null) continue;
                        w.Write(v.OwnerId);
                        w.Write(v.VehicleId);
                        byte flags = TailHasHealth;
                        if (v.Health.IsRepairing) flags |= TailIsRepairing;
                        // recLen = flags byte + [i32 hp][i32 maxHp].
                        w.Write((ushort)(1 + 4 + 4));
                        w.Write(flags);
                        w.Write(v.Health.Hp);
                        w.Write(v.Health.MaxHp);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a travel-metadata batch. Returns false (no partial accept) on any truncation — the
        /// reliable transport guarantees full delivery, so a short buffer is a clean drop.</summary>
        public static bool TryDecode(byte[] data, out uint seq, out List<GeoVehicleTravelMeta> vehicles)
        {
            seq = 0; vehicles = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    int n = r.ReadUInt16();
                    var list = new List<GeoVehicleTravelMeta>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int owner = r.ReadInt32();
                        int id = r.ReadInt32();
                        bool travelling = r.ReadByte() != 0;
                        int currentSiteId = r.ReadInt32();
                        int destCount = r.ReadUInt16();
                        // Guard the dest count against the remaining buffer so a corrupt count can't allocate wildly.
                        if ((long)destCount * 4 > ms.Length - ms.Position) return false;
                        var dests = new int[destCount];
                        for (int d = 0; d < destCount; d++) dests[d] = r.ReadInt32();
                        list.Add(new GeoVehicleTravelMeta(owner, id, travelling, currentSiteId, dests));
                    }
                    // WA-3 extras block — read ONLY if bytes remain (an older payload without it decodes with
                    // all tails null). A truncated block throws → whole payload rejected (no-partial-accept
                    // contract kept). Records for unknown keys are skipped (join-by-key, never a throw).
                    if (ms.Position < ms.Length)
                    {
                        int ne = r.ReadUInt16();
                        Dictionary<long, GeoVehicleHealthTail> tails = null;
                        for (int i = 0; i < ne; i++)
                        {
                            int owner = r.ReadInt32();
                            int id = r.ReadInt32();
                            int recLen = r.ReadUInt16();
                            var rec = r.ReadBytes(recLen);
                            if (rec.Length != recLen)
                                throw new EndOfStreamException("GeoVehicleTravelSnapshot: truncated extras record");
                            var tail = DecodeHealthTail(rec);
                            if (tail != null)
                                (tails ?? (tails = new Dictionary<long, GeoVehicleHealthTail>()))
                                    [GeoVehiclePos.MakeKey(owner, id)] = tail;
                        }
                        if (tails != null)
                            for (int i = 0; i < list.Count; i++)
                            {
                                var v = list[i];
                                if (!tails.TryGetValue(v.Key, out var tail)) continue;
                                list[i] = new GeoVehicleTravelMeta(v.OwnerId, v.VehicleId, v.Travelling,
                                    v.CurrentSiteId, v.DestSiteIds, tail);
                            }
                    }
                    vehicles = list;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Parse one extras record's known tails ([u8 tailFlags][payloads in ascending bit order]). Payloads
        /// of UNKNOWN (future, higher) bits trail the known ones and are simply left unread — the record slice
        /// is length-prefixed, so an old decoder degrades to "known tails only". A KNOWN bit whose payload is
        /// truncated throws → whole payload rejected by the caller.
        /// </summary>
        private static GeoVehicleHealthTail DecodeHealthTail(byte[] rec)
        {
            using (var ms = new MemoryStream(rec))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                byte flags = r.ReadByte();
                if ((flags & TailHasHealth) == 0) return null;
                int hp = r.ReadInt32();
                int maxHp = r.ReadInt32();
                return new GeoVehicleHealthTail(hp, maxHp, (flags & TailIsRepairing) != 0);
            }
        }
    }
}
