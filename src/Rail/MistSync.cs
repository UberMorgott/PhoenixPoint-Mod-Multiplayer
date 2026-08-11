using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Base.Core;
using Base.Serialization.General;
using HarmonyLib;
using PhoenixPoint.Geoscape;
using PhoenixPoint.Geoscape.Levels;
using Unity.Collections;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Mist coverage — the SECOND mod-state rail root ("M#mist"), riding the same walk/diff/apply as
    /// "M#cart". No new surface id, no channel, no codec: <see cref="MistState"/> is a plain
    /// serializer-visible class the generic value rail mirrors like any other root, and the oversize
    /// string rides <c>DiffEngine.FragmentForWire</c> → <c>GenericApplier.Reassemble</c> unchanged.
    ///
    /// WHY it must ride at all (RailCheck L59): mist is NOT derivable on a client. The truth is the
    /// CPU-side <c>MistRendererSystem._mistData</c>, which gameplay reads directly (<c>IsInMist</c>,
    /// decompile MistRendererSystem.cs:482-495) — but the surface it is read back FROM is a per-frame
    /// GPU accumulator: <c>MistFrameUpdate</c> (:398-409) blits the spread shader 4× EVERY FRAME at the
    /// peer's own frame rate. Two peers with different frame rates diverge continuously, so no amount
    /// of mirrored inputs (site states, repeller ranges, the clock) reproduces the same coverage.
    ///
    /// WHAT ships is exactly what the SAVE ships, and only the irreducible half of it:
    ///   • <c>MistData</c> — <c>_mistData</c> deflated + base64, the game's own encoding
    ///     (<c>RecordInstanceData</c>:790-806 = ToArray → <c>Compress</c> → <c>Convert.ToBase64String</c>).
    ///   • <c>HoursPassed</c> — the spread clock the shader global keys on.
    /// NOT shipped (Arc-4 rule — both re-derive from already-mirrored state):
    ///   • <c>RepellerData</c>, redrawn every frame from the live <c>MistRepeller.Range.Range</c> values
    ///     the value rail already mirrors (<c>RepellerFrameUpdate</c>:447-480);
    ///   • <c>ActiveMistGenerators</c>, driven by <c>OnSiteStateChanged</c> (:631) off the mirrored
    ///     <c>GeoSite.State</c>.
    ///
    /// Applied through the game's OWN loader — <c>ProcessInstanceData</c> (:808-873) — never a
    /// re-implemented blit: a null <c>RepellerData</c> is skipped by its guard at :822, and handing it
    /// the client's own generator set makes :831 a no-op.
    ///
    /// Cost discipline (Arc 5 deleted 95 ms hitches — do not reintroduce one): the ONLY main-thread work
    /// is <c>NativeArray.ToArray()</c>, a memcpy of the native buffer that must not be raced against a
    /// level unload. Deflate + base64 (the expensive half) run on a worker and hand the result back
    /// through <see cref="Shipment"/>. <c>WaitForDataUpdate()</c> (:356) is NEVER called — it stalls the
    /// GPU pipeline.
    /// </summary>
    internal static class MistSync
    {
        internal const string RootKey = "M#mist";

        /// <summary>Host walks its instance; on a client this is the generic applier's write target.</summary>
        internal static readonly MistState State = new MistState();

        /// <summary>Realtime FLOOR between two host recomputes, not a polling rate: <c>_mistData</c> is
        /// only refreshed once per GAME hour (<c>UpdateMist</c>:352 requests the readback), so the
        /// <c>_hoursPassed</c> gate below is what actually decides — this just caps the cost when the
        /// campaign runs at high time scale.</summary>
        internal static readonly float RecomputeIntervalSeconds = 10f;

        private static readonly FieldInfo MistDataField = AccessTools.Field(typeof(MistRendererSystem), "_mistData");
        private static readonly FieldInfo HoursPassedField = AccessTools.Field(typeof(MistRendererSystem), "_hoursPassed");

        /// <summary>Worker → main-thread handoff. ONE reference store, so the two values can never be
        /// read half-updated by the walk (which only ever runs on the main thread).</summary>
        private sealed class Shipment { public string Data; public int Hours; public int Epoch; }
        private static volatile Shipment _ready;

        /// <summary>Which reload boundary the in-flight encode belongs to. <see cref="ResetForReloadBoundary"/>
        /// can clear <see cref="_ready"/> but cannot reach a ThreadPool item already running, so a worker
        /// queued BEFORE the boundary would otherwise land after it and re-publish the PRE-boundary array —
        /// and with <see cref="_lastShippedHours"/> already advanced by a post-boundary worker, the
        /// <c>hours == _lastShippedHours</c> gate then blocks every further ship until the game hour turns.
        /// Written and read on the main thread only; a worker merely carries the value it captured.</summary>
        private static int _epoch;

        private static float _nextTickAt;
        private static int _lastShippedHours = -1;   // host: the _hoursPassed we last encoded
        private static string _lastApplied;          // client: the MistData we last handed the loader

        internal static void Register() => IdentityResolver.RegisterModRoot(RootKey, State);

        /// <summary>Mod-root contract (IdentityResolver.cs:205-206): mod state must be EMPTY at every
        /// reload boundary, or the post-reload BASELINE walk captures a value it never emits while the
        /// client's own instance stays empty — permanent divergence that only a full resend heals. Mist
        /// is never empty, so clear it and re-ship: at that instant the client is correct from the save
        /// it just loaded, so nothing is lost in the gap.</summary>
        internal static void ResetForReloadBoundary()
        {
            State.MistData = null;
            State.HoursPassed = 0;
            _ready = null;
            _lastShippedHours = -1;
            _lastApplied = null;
            _nextTickAt = 0f;
            _epoch++;   // orphan whatever encode is still in flight — see _epoch
        }

        internal static void Reset() => ResetForReloadBoundary();

        internal static void Tick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession) return;
            if (engine.IsHost) HostTick(); else ClientTick();
        }

        private static void HostTick()
        {
            var ready = _ready;
            if (ready != null)
            {
                _ready = null;
                if (ready.Epoch == _epoch)
                {
                    State.MistData = ready.Data;
                    State.HoursPassed = ready.Hours;
                }
                else
                    // A pre-boundary worker landed on top of a valid post-boundary shipment. Dropping it is
                    // half the fix; the other half is re-arming, or _lastShippedHours would keep the gate
                    // below shut on the clobbered hour until the campaign clock moved on.
                    _lastShippedHours = -1;
            }

            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + RecomputeIntervalSeconds;

            var comp = GenericApplier.GeoLevel()?.MistRenderComponent;
            if (comp == null || MistDataField == null || HoursPassedField == null) return;

            int hours = (int)HoursPassedField.GetValue(comp);
            if (hours == _lastShippedHours) return; // the CPU array is only rewritten on the hour tick

            var native = (NativeArray<byte>)MistDataField.GetValue(comp);
            if (!native.IsCreated) return; // level unloading — the buffer is disposed under us
            var t0 = Time.realtimeSinceStartup;
            var raw = native.ToArray();
            var copyMs = (Time.realtimeSinceStartup - t0) * 1000f;
            _lastShippedHours = hours;
            int epoch = _epoch;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _ready = new Shipment
                    {
                        Data = Convert.ToBase64String(MistRendererSystem.Compress(raw)),
                        Hours = hours,
                        Epoch = epoch,
                    };
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][rail] MistSync encode failed: " + ex); }
            });
            Debug.Log("[Multiplayer][rail] MistSync: queued mist hour " + hours + " (" + raw.Length +
                      "B raw, main-thread copy " + copyMs.ToString("0.00") + " ms)");
        }

        private static void ClientTick()
        {
            var data = State.MistData;
            if (data == null || data == _lastApplied) return;
            var geo = GenericApplier.GeoLevel();
            var comp = geo == null ? null : geo.MistRenderComponent;
            if (comp == null) return;
            _lastApplied = data;
            // Init:177 hands ProcessInstanceData to the level Timing the same way; Start instead of Call
            // because Call ends in WaitFor(), which THROWS outside a coroutine (TimingScheduler.cs:258-263)
            // and this runs from NetworkEngine.Update.
            geo.Timing.Start(comp.ProcessInstanceData(new MistRendererSystem.MistRendererInstanceData
            {
                MistData = data,
                RepellerData = null,                                    // guard at :822 skips it
                HoursPassed = State.HoursPassed,
                ActiveMistGenerators = comp.ActiveGenerators.ToList(),  // :831 becomes a self-assign
            }), NextUpdate.Now);
        }
    }

    /// <summary>
    /// Mist coverage as mod-state (root "M#mist", contract at
    /// <see cref="IdentityResolver.RegisterModRoot"/>) — the same two members the save carries in
    /// <c>MistRendererSystem.MistRendererInstanceData</c> (decompile :23-33), minus the two that
    /// re-derive from already-mirrored state. <c>MistData</c> is a ~40 KB (early) to ~900 KB (late)
    /// base64 string and rides the generic oversize path.
    /// </summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class MistState
    {
        public string MistData;
        public int HoursPassed;
    }
}
