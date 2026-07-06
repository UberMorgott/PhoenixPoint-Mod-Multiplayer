using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// GROUND-SURFACE / VOLUME mirror (spec TS3, surface <c>tac.surface</c> 0x94, host→all). Closes the ground
    /// hazard blind spot: fire / goo / acid / MIST volumes the frozen client cannot see today (soldiers take
    /// "phantom" damage; mist changes LoS). On-ACTOR fire/goo already ride the 0x8F status delta (inert icon) —
    /// TS3 owns the GROUND volume. Establishes the voxel-effect-mirror pattern TS6 reuses.
    ///
    /// ONE NATIVE FUNNEL. Every fire/goo/mist SPAWN and REMOVAL funnels through the single leaf
    /// <c>TacticalVoxel.SetVoxelType(TacticalVoxelType, visualsDelay, voxelValue)</c> — SpawnTacticalVoxelEffect /
    /// RemoveTacticalVoxelEffect / SpawnMistAbility / Fire+Goo self-extinguish all call it (verified in the
    /// decompile; structural destruction uses a DIFFERENT system → TS3 and TS6 stay disjoint concerns). The host
    /// postfixes that leaf (<see cref="Multiplayer.Harmony.Tactical.VoxelSetTypeCapturePatch"/>), which calls
    /// <see cref="HostCaptureVoxelChange"/> to COALESCE the changed cells; the flush heartbeat drains them here
    /// (<see cref="HostFlushSurfaces"/>) and broadcasts 0x94.
    ///
    /// CLIENT apply (<see cref="HandleSurface"/>): re-apply the SAME native leaf at the mirrored cells under the
    /// remote-apply scope → display + LoS are naturally correct (the client runs the REAL native ground effect).
    /// DAMAGE the volume deals to actors stays host-authoritative (rides tac.damage 0x88 / 0x8F) — the client
    /// volume is PRESENTATION + LoS only. The two damage/status leaves the frozen client can still reach at
    /// replay/move time (goo-status apply, fire-damage-on-enter) are neutered by the CLIENT-mirror guards in
    /// <see cref="Multiplayer.Harmony.Tactical.ClientSurfaceInertGuards"/> (per-turn ticks are already frozen by
    /// the turn-suppress patches; the guards are the defense-in-depth + the two synchronous-at-replay paths).
    ///
    /// All broadcasts host→ALL (3+ player safe), carry LiveSeq (last-writer-wins), per-item try/catch, resolve by
    /// voxel TYPE (TFTV-tolerant — no def-guid resolution to fail), degrade-to-notify on any failure. Pure wire +
    /// coalesce logic is the engine-free, unit-tested <see cref="TacticalSurfaceCodec"/>; this class is the only
    /// reflection boundary.
    /// </summary>
    public static class TacticalSurfaceSync
    {
        // Host pending coalesce buffer: captured cell changes since the last flush (drained on the heartbeat).
        // Single-threaded (the Unity game thread) — no locking. Bounded by the flush cadence.
        private static readonly List<TacticalSurfaceCodec.CapturedCell> _pending =
            new List<TacticalSurfaceCodec.CapturedCell>();

        /// <summary>Safety cap on cells packed into ONE 0x94 message (keeps the encoded payload under the u16
        /// envelope cap: 4096 * 12 B + framing ≈ 49 KB &lt; 65535). A larger flush window splits across messages.</summary>
        private const int MaxCellsPerMessage = 4096;

        /// <summary>Clear the host pending buffer (mission exit / re-deploy). Idempotent.</summary>
        public static void Reset() { _pending.Clear(); }

        // ─── HOST: capture (called from the SetVoxelType postfix) ────────────────────────────────────────

        /// <summary>HOST postfix hook on <c>TacticalVoxel.SetVoxelType</c>: record the voxel's RESULTING type +
        /// world position (keyed by grid indices for last-write-wins dedup) into the pending flush buffer. Gates:
        /// host + active session + not client-mirroring + deploy already captured (pre-deploy map-gen voxels are
        /// excluded) + not inside a remote apply (our own client replay must never re-capture). No-op off-host /
        /// single-player. Never throws (fail-quiet — a capture miss only drops a display mirror, never desyncs).</summary>
        public static void HostCaptureVoxelChange(object voxel)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (!TacticalDeploySync.HostHasBroadcastDeploy) return;   // exclude pre-deploy map generation
            if (TacticalActorStateSync.IsApplyingRemote) return;      // defense-in-depth (host self-apply → no re-capture)
            if (voxel == null) return;
            try
            {
                if (!ReadVoxelType(voxel, out byte voxelType)) return;
                Vector3 pos = ReadVoxelPos(voxel);
                if (!ReadVoxelIndices(voxel, out int kx, out int ky, out int kz)) return;
                _pending.Add(new TacticalSurfaceCodec.CapturedCell(kx, ky, kz, pos.x, pos.y, pos.z, voxelType));
            }
            catch { /* fail-quiet: a missed capture only drops a display mirror, never desyncs */ }
        }

        /// <summary>HOST (folded into the 0x8F flush heartbeat): coalesce the pending cell changes into wire ops
        /// (dedup per cell, group by type, split at the op cap) and broadcast them as 0x94 messages (packed under
        /// the envelope cap). Idle window = 0 pending = no-op. Runs on the host only.</summary>
        public static void HostFlushSurfaces(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (_pending.Count == 0) return;
            try
            {
                var ops = TacticalSurfaceCodec.CoalesceAndGroup(_pending, TacticalSurfaceCodec.MaxCellsPerOp);
                _pending.Clear();
                if (ops.Count == 0) return;

                // Pack ops into messages so no single 0x94 exceeds the envelope cap. Greedy: start a new message
                // when adding the next op would push the running cell total over MaxCellsPerMessage.
                var batchOps = new List<TacticalSurfaceCodec.SurfaceOp>();
                int cellsInBatch = 0;
                foreach (var op in ops)
                {
                    int opCells = op.Cells != null ? op.Cells.Count : 0;
                    if (batchOps.Count > 0 && cellsInBatch + opCells > MaxCellsPerMessage)
                    {
                        SendBatch(engine, batchOps);
                        batchOps = new List<TacticalSurfaceCodec.SurfaceOp>();
                        cellsInBatch = 0;
                    }
                    batchOps.Add(op);
                    cellsInBatch += opCells;
                }
                if (batchOps.Count > 0) SendBatch(engine, batchOps);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostFlushSurfaces failed: " + ex); }
        }

        private static void SendBatch(NetworkEngine engine, List<TacticalSurfaceCodec.SurfaceOp> ops)
        {
            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacSurface);
            byte[] payload = TacticalSurfaceCodec.EncodeSurface(new TacticalSurfaceCodec.SurfaceBatch(seq, ops));
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacSurface, payload);
            int cells = 0;
            foreach (var op in ops) cells += op.Cells != null ? op.Cells.Count : 0;
            Debug.Log("[Multiplayer][tac] HOST broadcast tac.surface seq=" + seq + " ops=" + ops.Count + " cells=" + cells);
        }

        // ─── CLIENT: apply ──────────────────────────────────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>tac.surface</c> 0x94): seq-guard, then replay each op at the mirrored cells
        /// via the SAME native <c>SetVoxelType</c> leaf under the remote-apply scope (so the client runs the REAL
        /// ground effect → correct display + LoS; the inert guards neuter its damage/status). Idempotent: re-applying
        /// a present volume is a native no-op stack; a remove on an already-empty cell is a no-op. No-op on host.</summary>
        public static void HandleSurface(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalSurfaceCodec.TryDecodeSurface(payload, out var batch))
            { Debug.LogError("[Multiplayer][tac] tac.surface decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacSurface, batch.Seq)) return;

            try
            {
                object matrix = ResolveVoxelMatrix();
                if (matrix == null)
                {
                    Debug.LogError("[Multiplayer][tac] HandleSurface: no TacticalVoxelMatrix on the current level — skip");
                    return;   // do NOT mark seq — a later flush re-sends the surface once the level is ready
                }

                int applied = 0;
                using (Network.Sync.SyncApplyScope.Enter())
                using (TacticalActorStateSync.EnterApplyScope())
                {
                    foreach (var op in batch.Ops)
                    {
                        if (op.Cells == null) continue;
                        object enumVal = ToVoxelTypeEnum(op.VoxelType);
                        if (enumVal == null) continue;   // unknown type value → skip this op (degrade-to-notify)
                        foreach (var cell in op.Cells)
                        {
                            try
                            {
                                object voxel = InvokeGetVoxel(matrix, new Vector3(cell.X, cell.Y, cell.Z));
                                if (voxel == null) continue;   // deterministic map → normally resolves; skip-on-null
                                InvokeSetVoxelType(voxel, enumVal);
                                applied++;
                            }
                            catch (Exception ex)
                            { Debug.LogError("[Multiplayer][tac] tac.surface per-cell apply failed: " + ex); }
                        }
                    }
                    InvokeUpdateVoxelMatrix(matrix);
                }
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacSurface, batch.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.surface seq=" + batch.Seq +
                          " ops=" + batch.Ops.Count + " cellsApplied=" + applied);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleSurface failed: " + ex); }
        }

        // ─── Reflection boundary ─────────────────────────────────────────────────────────────────────────

        private static Type _voxelType;         // PhoenixPoint.Tactical.Levels.Mist.TacticalVoxel
        private static Type _voxelMatrixType;    // PhoenixPoint.Tactical.Levels.Mist.TacticalVoxelMatrix
        private static Type _voxelTypeEnum;      // PhoenixPoint.Tactical.Levels.Mist.TacticalVoxelType
        private static MethodInfo _getVoxelType;     // TacticalVoxel.GetVoxelType()
        private static MethodInfo _setVoxelType;     // TacticalVoxel.SetVoxelType(TacticalVoxelType, float, float)
        private static MethodInfo _getVoxel;         // TacticalVoxelMatrix.GetVoxel(Vector3)
        private static MethodInfo _updateVoxelMatrix; // TacticalVoxelMatrix.UpdateVoxelMatrix()
        private static PropertyInfo _posProp;        // TacticalVoxel.Position (Vector3)
        private static PropertyInfo _indicesProp;    // TacticalVoxel.Indices (Vector3Int)

        private static Type VoxelType()
            => _voxelType ?? (_voxelType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.TacticalVoxel"));
        private static Type VoxelMatrixType()
            => _voxelMatrixType ?? (_voxelMatrixType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.TacticalVoxelMatrix"));
        private static Type VoxelTypeEnum()
            => _voxelTypeEnum ?? (_voxelTypeEnum = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Mist.TacticalVoxelType"));

        private static bool ReadVoxelType(object voxel, out byte voxelType)
        {
            voxelType = 0;
            if (_getVoxelType == null) _getVoxelType = AccessTools.Method(voxel.GetType(), "GetVoxelType");
            object v = _getVoxelType?.Invoke(voxel, null);
            if (v == null) return false;
            voxelType = (byte)Convert.ToInt32(v);   // TacticalVoxelType enum → its underlying int → byte
            return true;
        }

        private static Vector3 ReadVoxelPos(object voxel)
        {
            if (_posProp == null) _posProp = AccessTools.Property(voxel.GetType(), "Position");
            object p = _posProp?.GetValue(voxel, null);
            return p is Vector3 v ? v : Vector3.zero;
        }

        private static bool ReadVoxelIndices(object voxel, out int kx, out int ky, out int kz)
        {
            kx = ky = kz = 0;
            if (_indicesProp == null) _indicesProp = AccessTools.Property(voxel.GetType(), "Indices");
            object i = _indicesProp?.GetValue(voxel, null);
            if (i is Vector3Int vi) { kx = vi.x; ky = vi.y; kz = vi.z; return true; }
            return false;
        }

        /// <summary>Convert a wire voxel-type byte to the native <c>TacticalVoxelType</c> enum value, or null if the
        /// enum type can't be resolved (degrade-to-notify).</summary>
        private static object ToVoxelTypeEnum(byte voxelType)
        {
            var t = VoxelTypeEnum();
            if (t == null) return null;
            try { return Enum.ToObject(t, (int)voxelType); }
            catch { return null; }
        }

        private static object InvokeGetVoxel(object matrix, Vector3 pos)
        {
            if (_getVoxel == null)
                _getVoxel = AccessTools.Method(VoxelMatrixType(), "GetVoxel", new[] { typeof(Vector3) });
            return _getVoxel?.Invoke(matrix, new object[] { pos });
        }

        private static void InvokeSetVoxelType(object voxel, object enumVal)
        {
            if (_setVoxelType == null)
                _setVoxelType = AccessTools.Method(VoxelType(), "SetVoxelType",
                    new[] { VoxelTypeEnum(), typeof(float), typeof(float) });
            // SetVoxelType(voxelType, visualsDelay:0, voxelValue:0) — display replay, no fire-intensity carry.
            _setVoxelType?.Invoke(voxel, new object[] { enumVal, 0f, 0f });
        }

        private static void InvokeUpdateVoxelMatrix(object matrix)
        {
            if (_updateVoxelMatrix == null)
                _updateVoxelMatrix = AccessTools.Method(VoxelMatrixType(), "UpdateVoxelMatrix");
            _updateVoxelMatrix?.Invoke(matrix, null);
        }

        /// <summary>Resolve the live <c>TacticalVoxelMatrix</c> on the current level — the same
        /// <c>GameUtl.CurrentLevel() → GetComponent</c> path the native effects use (and the repo's
        /// <c>LiveTacticalLevelController</c>). Null if the current level is not tactical / has no matrix.</summary>
        private static object ResolveVoxelMatrix()
        {
            var mt = VoxelMatrixType();
            if (mt == null) return null;
            try
            {
                var level = Network.Sync.GeoRuntime.Instance.CurrentLevel();
                if (level is Component comp) return comp.GetComponent(mt);
                return null;
            }
            catch { return null; }
        }
    }
}
