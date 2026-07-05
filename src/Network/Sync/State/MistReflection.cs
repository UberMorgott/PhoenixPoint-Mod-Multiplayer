using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection boundary for the MIST state channel (#8, WA-1). Game members resolved by name + cached
    /// (mirrors <see cref="GeoVehicleIdentityReflection"/>). The native SERIALIZATION GIFT does all real work:
    ///   • HOST <see cref="TryRecord"/> — <c>GeoLevelController.MistRenderComponent.RecordInstanceData()</c>
    ///     (MistRendererSystem.cs:790) returns the whole field as deflate+base64 strings (<c>MistData</c> /
    ///     <c>RepellerData</c>) + <c>HoursPassed</c> — the EXACT payload the game's own save writes
    ///     (GeoLevelController.cs:404). We decode base64 → raw deflate bytes for the wire (25% smaller).
    ///   • HOST <see cref="TryReadHours"/> — cheap <c>_hoursPassed</c> int read; the hourly producer
    ///     (<c>UpdateMist</c>, :344) increments it, so a changed value = "an in-game hour passed" without any
    ///     Harmony patch or event.
    ///   • CLIENT <see cref="Apply"/> — rebuild a <c>MistRendererInstanceData</c> and drive the SAME native
    ///     <c>ProcessInstanceData</c> coroutine the level-load path uses (:808): it decompresses into
    ///     <c>_mistData</c>/<c>_repellerData</c>, uploads both textures to the render targets and re-stamps the
    ///     spread shader globals — the full redraw. Every side effect happens BEFORE its single
    ///     <c>yield return NextUpdate.NextFrame</c>, so ONE synchronous <c>MoveNext()</c> completes it — no
    ///     dependency on the client's FROZEN sim Timing (the client's own hourly mist producer rides the paused
    ///     clock and never runs — no producers on the client, frozen-sim safe by construction).
    ///     The client's live <c>_activeMistGenerators</c> set is COPIED into the instance data first
    ///     (ProcessInstanceData overwrites that set from it; the wire deliberately does not carry GeoSite refs).
    /// All null-safe: a missing member degrades (logged once at use) rather than throwing.
    /// </summary>
    public static class MistReflection
    {
        private static bool _ready;
        private static FieldInfo _mistComponentField;   // GeoLevelController.MistRenderComponent (public field)
        private static FieldInfo _hoursField;           // MistRendererSystem._hoursPassed (private int)
        private static FieldInfo _activeGensField;      // MistRendererSystem._activeMistGenerators (HashSet<GeoSite>)
        private static MethodInfo _record;              // MistRendererSystem.RecordInstanceData()
        private static MethodInfo _process;             // MistRendererSystem.ProcessInstanceData(MistRendererInstanceData)
        private static Type _instType;                  // MistRendererSystem+MistRendererInstanceData
        private static FieldInfo _instMist;             // MistRendererInstanceData.MistData (string)
        private static FieldInfo _instRep;              // MistRendererInstanceData.RepellerData (string)
        private static FieldInfo _instHours;            // MistRendererInstanceData.HoursPassed (int)
        private static FieldInfo _instGens;             // MistRendererInstanceData.ActiveMistGenerators (List<GeoSite>)

        private static void Ensure(object mistComponent)
        {
            if (_ready || mistComponent == null) return;
            var mistType = mistComponent.GetType();
            _hoursField = AccessTools.Field(mistType, "_hoursPassed");
            _activeGensField = AccessTools.Field(mistType, "_activeMistGenerators");
            _record = AccessTools.Method(mistType, "RecordInstanceData");
            _instType = AccessTools.Inner(mistType, "MistRendererInstanceData");
            if (_instType != null)
            {
                _instMist = AccessTools.Field(_instType, "MistData");
                _instRep = AccessTools.Field(_instType, "RepellerData");
                _instHours = AccessTools.Field(_instType, "HoursPassed");
                _instGens = AccessTools.Field(_instType, "ActiveMistGenerators");
                _process = AccessTools.Method(mistType, "ProcessInstanceData", new[] { _instType });
            }
            _ready = _hoursField != null && _record != null && _instType != null
                     && _instMist != null && _instRep != null && _instHours != null && _process != null;
            if (!_ready)
                Debug.LogError("[Multiplayer][geo] MistReflection: bind incomplete (mist channel disabled) — hours=" + (_hoursField != null)
                               + " record=" + (_record != null) + " instType=" + (_instType != null) + " process=" + (_process != null));
        }

        /// <summary>The live <c>MistRendererSystem</c> component, or null when not in geoscape / mid-load.</summary>
        private static object GetMist(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            try
            {
                if (_mistComponentField == null) _mistComponentField = AccessTools.Field(geo.GetType(), "MistRenderComponent");
                return _mistComponentField?.GetValue(geo);
            }
            catch { return null; }
        }

        /// <summary>HOST cheap poll: the mist system's <c>_hoursPassed</c> counter (increments once per in-game
        /// hour). False when not in geoscape or the member is unresolved.</summary>
        public static bool TryReadHours(GeoRuntime rt, out int hours)
        {
            hours = 0;
            try
            {
                object mist = GetMist(rt);
                if (mist == null) return false;
                Ensure(mist);
                if (!_ready) return false;
                hours = (int)_hoursField.GetValue(mist);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] MistReflection.TryReadHours failed: " + ex.Message); return false; }
        }

        /// <summary>HOST: run the native <c>RecordInstanceData()</c> and return its payload as raw deflate bytes.
        /// False when unavailable or nothing has been recorded yet (empty field pre-readback).</summary>
        public static bool TryRecord(GeoRuntime rt, out int hoursPassed, out byte[] mist, out byte[] repeller)
        {
            hoursPassed = 0; mist = null; repeller = null;
            try
            {
                object mistComp = GetMist(rt);
                if (mistComp == null) return false;
                Ensure(mistComp);
                if (!_ready) return false;
                object inst = _record.Invoke(mistComp, null);
                if (inst == null) return false;
                hoursPassed = (int)_instHours.GetValue(inst);
                string ms = _instMist.GetValue(inst) as string;
                string rs = _instRep.GetValue(inst) as string;
                mist = string.IsNullOrEmpty(ms) ? new byte[0] : Convert.FromBase64String(ms);
                repeller = string.IsNullOrEmpty(rs) ? new byte[0] : Convert.FromBase64String(rs);
                return mist.Length > 0 || repeller.Length > 0;   // nothing readback yet → nothing to mirror
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] MistReflection.TryRecord failed: " + ex.Message); return false; }
        }

        /// <summary>CLIENT: rebuild the instance data (preserving this client's own active-generator set) and
        /// drive the native <c>ProcessInstanceData</c> redraw synchronously (single MoveNext — every texture
        /// upload/shader stamp happens before its one yield). Idempotent: re-applying the same field is a
        /// harmless re-upload. No-op (logged) when the mist system is not live yet; the next hourly emission
        /// heals. Runs inside the caller's <c>SyncApplyScope</c> on the main thread (OnStateSync).</summary>
        public static void Apply(GeoRuntime rt, int hoursPassed, byte[] mist, byte[] repeller)
        {
            try
            {
                object mistComp = GetMist(rt);
                if (mistComp == null) { Debug.Log("[Multiplayer][geo] Mist apply skipped (mist system not live yet)"); return; }
                Ensure(mistComp);
                if (!_ready) return;

                object inst = Activator.CreateInstance(_instType);
                _instMist.SetValue(inst, mist != null && mist.Length > 0 ? Convert.ToBase64String(mist) : null);
                _instRep.SetValue(inst, repeller != null && repeller.Length > 0 ? Convert.ToBase64String(repeller) : null);
                _instHours.SetValue(inst, hoursPassed);
                // Preserve the client's own active mist generators: ProcessInstanceData REPLACES the component's
                // set from this list (the wire carries no GeoSite refs — sites are already mirrored via ch#5 and
                // registered locally by the client's own MistRendererSystem.Init/site-state events).
                try
                {
                    var actives = _activeGensField?.GetValue(mistComp) as IEnumerable;
                    var list = _instGens?.GetValue(inst) as IList;   // field-initialized List<GeoSite>
                    if (actives != null && list != null)
                        foreach (var site in actives) list.Add(site);
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][geo] Mist apply: active-generator copy failed (continuing): " + ex.Message); }

                var crt = _process.Invoke(mistComp, new[] { inst }) as IEnumerator;
                if (crt == null) { Debug.LogError("[Multiplayer][geo] Mist apply: ProcessInstanceData returned no enumerator"); return; }
                crt.MoveNext();                       // full redraw runs before the coroutine's single yield
                (crt as IDisposable)?.Dispose();
                Debug.Log("[Multiplayer][geo] CLIENT mist applied hours=" + hoursPassed
                          + " mistBytes=" + (mist?.Length ?? 0) + " repellerBytes=" + (repeller?.Length ?? 0));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] MistReflection.Apply failed: " + ex.Message); }
        }
    }
}
