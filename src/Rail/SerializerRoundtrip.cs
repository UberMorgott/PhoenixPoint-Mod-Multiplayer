using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Rail
{
    /// <summary>
    /// Surface-neutral native-Serializer blob roundtrip, extracted verbatim from the proven
    /// TacticalDeploySync path (old repo src/Sync/Tactical/TacticalDeploySync.cs:1490-1649,
    /// in-game verified 2026-06-18). Two hard-won facts baked in:
    ///   • MUST use the engine's ONE configured Serializer
    ///     (GameUtl.GameComponent&lt;SerializationComponent&gt;().Serializer) — a `new Serializer(null)`
    ///     has a null Context, NREs in BaseDef.ResolveOrCreateBaseDef on any Def ref, and silently
    ///     yields an EMPTY graph.
    ///   • The Write/Read coroutines yield nested Timing.Current work — drive them with
    ///     Timing.RunUntilComplete, and call from INSIDE a running IUpdateable (network callbacks are
    ///     not: use RunOnTiming / SchedulingGate to defer onto the level's Timing first).
    /// </summary>
    public static class SerializerRoundtrip
    {
        // ─── Reflection cache ──────────────────────────────────────────────
        private static Type _serializerType;   // Base.Serialization.General.Serializer
        private static Type _byRefType;        // Base.Utils.ByRef<>
        private static Type _timeSliceType;    // Base.Utils.TimeSlice
        private static bool _reflectionReady;
        // The engine's ONE configured Serializer (Context = SerializationComponent + InitCustomTypes +
        // ValidateSerializedObject). Resolved lazily, cached for the session.
        private static MethodInfo _gameComponentSerComp;  // GameUtl.GameComponent<SerializationComponent>()
        private static PropertyInfo _serCompSerializerProp;// SerializationComponent.Serializer getter

        private static bool _inSelfRoundtrip;

        private static void EnsureReflection()
        {
            if (_reflectionReady) return;
            _serializerType = AccessTools.TypeByName("Base.Serialization.General.Serializer");
            _byRefType = AccessTools.TypeByName("Base.Utils.ByRef`1");
            _timeSliceType = AccessTools.TypeByName("Base.Utils.TimeSlice");
            _reflectionReady = _serializerType != null;
        }

        // Diagnostics only (log line), never on the wire — so the in-assembly Crc32 does the job and
        // there is no second hand-rolled hash to keep honest.
        private static string BytesHash(byte[] b)
            => b == null ? "null" : "len=" + b.Length + " crc=" + Multiplayer.Util.Crc32.Compute(b).ToString("X8");

        private static string GraphTypeNames(object[] graph)
        {
            if (graph == null) return "null";
            var names = new List<string>(graph.Length);
            for (int i = 0; i < graph.Length; i++) names.Add(graph[i]?.GetType().Name ?? "null");
            return string.Join(",", names.ToArray());
        }

        /// <summary>
        /// Resolve the engine's ONE configured <c>Serializer</c> instance — the SAME object every working
        /// game caller round-trips through (<c>NavConsoleCommands</c>, <c>SerializationCommands</c>,
        /// <c>NamedValueStore</c> all use <c>GameUtl.GameComponent&lt;SerializationComponent&gt;().Serializer</c>).
        /// Returns null if the SerializationComponent isn't reachable yet (caller logs + skips).
        /// </summary>
        public static object ResolveGameSerializer()
        {
            try
            {
                if (_gameComponentSerComp == null)
                {
                    var serCompType = AccessTools.TypeByName("Base.Serialization.SerializationComponent");
                    var gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl");
                    if (serCompType == null || gameUtlType == null)
                    {
                        Debug.LogError("[Multiplayer][rail] ResolveGameSerializer: SerializationComponent/GameUtl type not found");
                        return null;
                    }
                    var gc = AccessTools.Method(gameUtlType, "GameComponent");
                    _gameComponentSerComp = gc != null ? gc.MakeGenericMethod(serCompType) : null;
                    _serCompSerializerProp = AccessTools.Property(serCompType, "Serializer");
                }
                if (_gameComponentSerComp == null || _serCompSerializerProp == null)
                {
                    Debug.LogError("[Multiplayer][rail] ResolveGameSerializer: GameComponent<>/Serializer accessor not found");
                    return null;
                }
                object serComp = _gameComponentSerComp.Invoke(null, null);
                object serializer = serComp != null ? _serCompSerializerProp.GetValue(serComp, null) : null;
                if (serializer == null)
                    Debug.LogError("[Multiplayer][rail] ResolveGameSerializer: SerializationComponent/Serializer is null (not initialized yet?)");
                return serializer;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ResolveGameSerializer failed: " + ex); return null; }
        }

        /// <summary>Serialize a root-object graph to bytes via the native game Serializer.
        /// quiet=true skips the probe logs + the host self-roundtrip (per-entity flushes would spam /
        /// double the serializer work). MUST run inside a running IUpdateable (see RunOnTiming).</summary>
        public static byte[] SerializeGraph(object[] graph, bool quiet = false)
        {
            EnsureReflection();
            if (_serializerType == null || _byRefType == null || _timeSliceType == null) return null;
            try
            {
                // PROBE: host input graph (element count + concrete type names).
                if (!quiet)
                    Debug.Log("[Multiplayer][rail] Write input graph count=" + (graph?.Length ?? -1) +
                              " types=[" + GraphTypeNames(graph) + "]");
                object serializer = ResolveGameSerializer();
                if (serializer == null) { Debug.LogError("[Multiplayer][rail] SerializeGraph: no game Serializer — skipping"); return null; }
                // ByRef<byte[]> dest. Base.Utils.ByRef<T> has a SINGLE ctor `ByRef(T value = default)` —
                // an optional param, NO parameterless ctor — so Activator.CreateInstance(Type) (no args)
                // throws MissingMethodException. Pass the argument explicitly (default(byte[]) == null).
                Type byRefBytes = _byRefType.MakeGenericType(typeof(byte[]));
                object dest = Activator.CreateInstance(byRefBytes, new object[] { null });
                // TimeSlice slice = new TimeSlice(large) → effectively unbounded single pump.
                object slice = Activator.CreateInstance(_timeSliceType, new object[] { 3600f });

                var write = AccessTools.Method(_serializerType, "Write",
                    new[] { typeof(IEnumerable<object>), typeof(string), byRefBytes, _timeSliceType });
                if (write == null) { Debug.LogError("[Multiplayer][rail] Serializer.Write(byte[]) overload not found"); return null; }

                IEnumerable<object> objects = graph;
                // Drive the native serializer coroutine via the engine's own synchronous runner
                // (Base.Core.Timing.RunUntilComplete) — a bare while(MoveNext()) has no active scheduler
                // ⇒ nested Timing.Current work never runs ⇒ dest stays null.
                var coroutine = (IEnumerator<NextUpdate>)write.Invoke(serializer, new object[] { objects, ".b", dest, slice });
                Timing.RunUntilComplete(coroutine);

                var valueField = byRefBytes.GetField("Value");
                byte[] outBytes = valueField?.GetValue(dest) as byte[];

                // PROBE: output length + checksum (host-sent bytes, compare to client-received bytesHash).
                if (!quiet)
                    Debug.Log("[Multiplayer][rail] Write output destLen=" + (outBytes?.Length ?? -1) +
                              " bytesHash " + BytesHash(outBytes));

                // PROBE: in-process host self-roundtrip — proves the Read contract on our own bytes.
                if (outBytes != null && !_inSelfRoundtrip && !quiet)
                {
                    _inSelfRoundtrip = true;
                    try
                    {
                        Type elemType = (graph != null && graph.Length > 0 && graph[0] != null) ? graph[0].GetType() : null;
                        object self = DeserializeGraph(outBytes, elemType);
                        Debug.Log("[Multiplayer][rail] HOST self-roundtrip result=" +
                                  (self == null ? "NULL" : self.GetType().Name) + " expectedType=" +
                                  (elemType?.Name ?? "any"));
                    }
                    catch (Exception sx) { Debug.LogError("[Multiplayer][rail] HOST self-roundtrip threw: " + sx); }
                    finally { _inSelfRoundtrip = false; }
                }

                return outBytes;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] SerializeGraph failed: " + ex); return null; }
        }

        /// <summary>Deserialize bytes back into a graph and return the first element assignable to
        /// <paramref name="expectedType"/> (null = first non-null). Returns a NEW instance graph —
        /// callers field-copy onto live objects. MUST run inside a running IUpdateable.</summary>
        public static object DeserializeGraph(byte[] bytes, Type expectedType, bool quiet = false)
        {
            EnsureReflection();
            if (_serializerType == null || _byRefType == null || _timeSliceType == null) return null;
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                if (!quiet)
                    Debug.Log("[Multiplayer][rail] Read input bytesHash " + BytesHash(bytes) +
                              " expectedType=" + (expectedType?.Name ?? "any"));
                // MUST be the same engine-configured Serializer the writer used (Context =
                // SerializationComponent), else silent empty graph. See ResolveGameSerializer.
                object serializer = ResolveGameSerializer();
                if (serializer == null) { Debug.LogError("[Multiplayer][rail] DeserializeGraph: no game Serializer — returning null"); return null; }
                // ByRef<IEnumerable<object>> outRef — same single-optional-ctor trap as SerializeGraph.
                Type byRefEnum = _byRefType.MakeGenericType(typeof(IEnumerable<object>));
                object outRef = Activator.CreateInstance(byRefEnum, new object[] { null });
                object slice = Activator.CreateInstance(_timeSliceType, new object[] { 3600f });

                // Read(ByRef<IEnumerable<object>> objects, TimeSlice slice, string formatExt, byte[] srcData, string section=null)
                var read = AccessTools.Method(_serializerType, "Read",
                    new[] { byRefEnum, _timeSliceType, typeof(string), typeof(byte[]), typeof(string) });
                if (read == null) { Debug.LogError("[Multiplayer][rail] Serializer.Read(byte[]) overload not found"); return null; }

                var coroutine = (IEnumerator<NextUpdate>)read.Invoke(serializer, new object[] { outRef, slice, ".b", bytes, null });
                Timing.RunUntilComplete(coroutine);

                var valueField = byRefEnum.GetField("Value");
                var result = valueField?.GetValue(outRef) as IEnumerable<object>;

                List<object> graphList = result == null ? null : new List<object>(result);
                if (!quiet)
                    Debug.Log("[Multiplayer][rail] Read graph " +
                              (result == null ? "NULL (ByRef.Value==null → empty graph)"
                                              : ("count=" + graphList.Count + " types=[" +
                                                 string.Join(",", graphList.ConvertAll(o => o?.GetType().Name).ToArray()) + "]")));
                if (result == null) return null;
                foreach (var o in graphList)
                    if (o != null && (expectedType == null || expectedType.IsInstanceOfType(o))) return o;
                return null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] DeserializeGraph failed: " + ex); return null; }
        }

        // ─── Scheduling gate (TacticalHydrateSchedulingGate pattern) ───────
        // Network inbound callbacks run OUTSIDE any IUpdateable ⇒ the native Read coroutine's
        // Timing.Current read throws InvalidOperationException. Defer the apply onto a live Timing
        // (level Timing or ambient Timing.Current); only fall back inline when none resolves.

        /// <summary>Run <paramref name="body"/> inside a running IUpdateable: start it as a coroutine on a
        /// resolvable Timing (preferred), else invoke it inline as a best-effort fallback. Returns true
        /// when deferred onto a Timing.</summary>
        public static bool RunOnTiming(object timingOwner, Func<IEnumerator<NextUpdate>> body)
        {
            object timing = ResolveTiming(timingOwner);
            if (timing != null && InvokeTimingStart(timing, body()))
                return true;
            Debug.LogError("[Multiplayer][rail] RunOnTiming: no Timing resolvable — running inline (serializer may throw)");
            var crt = body();
            while (crt.MoveNext()) { }
            return false;
        }

        /// <summary>Resolve a <c>Base.Core.Timing</c>: prefer <paramref name="timingOwner"/>'s own
        /// <c>Timing</c> property (e.g. a level controller), else the ambient <c>Timing.Current</c>.</summary>
        public static object ResolveTiming(object timingOwner)
        {
            object timing = null;
            if (timingOwner != null)
            {
                var prop = AccessTools.Property(timingOwner.GetType(), "Timing");
                try { timing = prop?.GetValue(timingOwner, null); } catch { timing = null; }
            }
            if (timing == null)
            {
                try { timing = Timing.Current; } catch { timing = null; }
            }
            return timing;
        }

        /// <summary>Invoke the simplest <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …optional)</c> overload:
        /// first param is the coroutine, all trailing params optional.</summary>
        public static bool InvokeTimingStart(object timing, IEnumerator crt)
        {
            try
            {
                MethodInfo best = null;
                foreach (var m in timing.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "Start") continue;
                    var pars = m.GetParameters();
                    if (pars.Length < 1) continue;
                    if (!typeof(IEnumerator).IsAssignableFrom(pars[0].ParameterType)) continue;
                    bool restOptional = true;
                    for (int i = 1; i < pars.Length; i++) if (!pars[i].IsOptional) { restOptional = false; break; }
                    if (!restOptional) continue;
                    if (best == null || pars.Length < best.GetParameters().Length) best = m;
                }
                if (best == null) { Debug.LogError("[Multiplayer][rail] InvokeTimingStart: no Start overload found"); return false; }
                var bp = best.GetParameters();
                var args = new object[bp.Length];
                args[0] = crt;
                for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;
                best.Invoke(timing, args);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] InvokeTimingStart failed: " + ex); return false; }
        }
    }
}
