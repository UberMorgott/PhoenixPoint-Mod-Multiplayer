using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for the host-authoritative RESEARCH state channel (channel #2). The mod has
    /// NO compile-time game references, so every member is resolved by name and cached. This complements
    /// <see cref="Multipleer.Network.Sync.ResearchReflection"/> (which already binds AddResearchToQueue /
    /// CompleteResearch / GetResearchById and the id field) with the snapshot/reconcile + change-event
    /// surface the channel needs.
    ///
    /// Verified against the decompile (2026-06-16, <c>PhoenixPoint.Geoscape.Entities.Research</c>):
    ///   • path: <c>GeoRuntime.PhoenixFaction()</c> → <c>GeoFaction.Research</c> (public FIELD,
    ///     GeoFaction.cs:79 <c>public Research Research;</c> — bound via AccessTools.Field, mirroring
    ///     <c>ItemStorageReflection.GetStorage</c>) → the <c>Research</c> instance.
    ///   • completed set: <c>Research.Completed : IEnumerable&lt;ResearchElement&gt;</c> (Research.cs:123,
    ///     LINQ over <c>AllResearchesArray</c> where <c>IsCompleted</c>). Each → <c>ResearchID</c>.
    ///   • queue: <c>Research.ResearchQueue : List&lt;ResearchElement&gt;</c> (public property, Research.cs:51).
    ///     Index 0 == <c>Current</c>; order is the live queue order.
    ///   • progress: <c>ResearchElement.ResearchProgress</c> is a PUBLIC <c>float</c> FIELD
    ///     (ResearchElement.cs:139) — read AND written directly (no setter method). The game itself
    ///     assigns it directly (Research.cs:823, :1255).
    ///   • resolver: reuse <c>Research.GetResearchById(string)</c> (Research.cs:763).
    ///   • complete (client apply): <c>Research.CompleteResearch(ResearchElement)</c> (Research.cs:576).
    ///   • add to queue: <c>Research.AddResearchToQueue(ResearchElement)</c> (Research.cs:370).
    ///   • cancel/remove from queue: <c>Research.Cancel(ResearchElement)</c> (Research.cs:461). This is the
    ///     exact method the UI cancel button calls (UIModuleResearch.cs:435 <c>Research.Cancel(...)</c>) —
    ///     used both by the reconcile and the client cancel intent below.
    ///   • host change events (faction-level): <c>GeoFaction.ResearchStartedEventHandler</c> and
    ///     <c>GeoFaction.ResearchCompletedEventHandler</c>, both <c>event Action&lt;GeoFaction, ResearchElement&gt;</c>
    ///     (GeoFaction.cs:309-311). The delegate's params are reference types, but a fully-<c>object</c>
    ///     adapter is still not delegate-compatible, so we emit a <see cref="DynamicMethod"/> matching the
    ///     exact signature (mirrors <c>WalletReflection.SubscribeResourcesChanged</c>). No faction-level
    ///     cancel event exists; cancels are propagated by marking the channel dirty in the cancel
    ///     interceptor (host branch) + the next start/complete/HourTick snapshot — both converge.
    /// </summary>
    public static class ResearchStateReflection
    {
        private static bool _ready;
        private static Type _researchType;        // ...Research.Research
        private static Type _researchElementType; // ...Research.ResearchElement
        private static FieldInfo _factionResearchField;   // GeoFaction.Research (public field, GeoFaction.cs:79)
        private static PropertyInfo _completedProp;       // Research.Completed (IEnumerable<ResearchElement>)
        private static PropertyInfo _queueProp;           // Research.ResearchQueue (List<ResearchElement>)
        private static MethodInfo _getById;               // Research.GetResearchById(string)
        private static MethodInfo _addToQueue;            // Research.AddResearchToQueue(ResearchElement)
        private static MethodInfo _complete;              // Research.CompleteResearch(ResearchElement)
        private static MethodInfo _cancel;                // Research.Cancel(ResearchElement)
        private static FieldInfo _researchIdField;        // ResearchElement.ResearchID
        private static FieldInfo _progressField;          // ResearchElement.ResearchProgress (float)

        private static void Ensure()
        {
            if (_ready) return;
            _researchType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            _researchElementType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
            if (_researchType == null || _researchElementType == null) return;

            _completedProp = AccessTools.Property(_researchType, "Completed");
            _queueProp = AccessTools.Property(_researchType, "ResearchQueue");
            _getById = AccessTools.Method(_researchType, "GetResearchById", new[] { typeof(string) });
            _addToQueue = AccessTools.Method(_researchType, "AddResearchToQueue", new[] { _researchElementType });
            _complete = AccessTools.Method(_researchType, "CompleteResearch", new[] { _researchElementType });
            _cancel = AccessTools.Method(_researchType, "Cancel", new[] { _researchElementType });
            _researchIdField = AccessTools.Field(_researchElementType, "ResearchID");
            _progressField = AccessTools.Field(_researchElementType, "ResearchProgress");

            _ready = _completedProp != null && _queueProp != null && _getById != null
                     && _addToQueue != null && _complete != null && _cancel != null
                     && _researchIdField != null && _progressField != null;
        }

        /// <summary>The live player-faction <c>Research</c> instance, or null.</summary>
        public static object GetResearch(GeoRuntime rt)
        {
            var fac = rt?.PhoenixFaction();
            if (fac == null) return null;
            try
            {
                if (_factionResearchField == null || _factionResearchField.DeclaringType == null
                    || !_factionResearchField.DeclaringType.IsInstanceOfType(fac))
                    _factionResearchField = AccessTools.Field(fac.GetType(), "Research");
                return _factionResearchField?.GetValue(fac);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.GetResearch failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Host: snapshot the authoritative research state — completed research ids + the ordered queue of
        /// (id, progress). Null if unavailable.
        /// </summary>
        public static ResearchSnapshot Snapshot(GeoRuntime rt)
        {
            try
            {
                Ensure();
                if (!_ready) return null;
                var research = GetResearch(rt);
                if (research == null) return null;

                var snap = new ResearchSnapshot();

                if (_completedProp.GetValue(research, null) is IEnumerable completed)
                    foreach (var el in completed)
                    {
                        string id = _researchIdField.GetValue(el) as string;
                        if (!string.IsNullOrEmpty(id)) snap.Completed.Add(id);
                    }

                if (_queueProp.GetValue(research, null) is IEnumerable queue)
                    foreach (var el in queue)
                    {
                        string id = _researchIdField.GetValue(el) as string;
                        if (string.IsNullOrEmpty(id)) continue;
                        float progress = 0f;
                        try { progress = (float)_progressField.GetValue(el); } catch { progress = 0f; }
                        snap.Queue.Add((id, progress));
                    }

                return snap;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Snapshot failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Client: reconcile the live research to EXACTLY match <paramref name="target"/> (idempotent).
        /// Research is monotonic — completed ids that the client hasn't completed are completed (never
        /// un-completed). The queue is then made to match the snapshot: cancel any queued element absent
        /// from the snapshot, add any missing element in order, and set each element's progress to the
        /// snapshot value (direct field write).
        /// </summary>
        public static void Apply(GeoRuntime rt, ResearchSnapshot target)
        {
            if (target == null) return;
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetResearch(rt);
                if (research == null) return;

                // (1) Complete newly-completed research (monotonic).
                foreach (var id in target.Completed)
                {
                    var el = Resolve(research, id);
                    if (el == null) continue;
                    if (IsCompleted(el)) continue;
                    _complete.Invoke(research, new[] { el });
                }

                // (2) Cancel queued elements that are NOT in the snapshot. Snapshot a copy of the live
                // queue first — Cancel mutates the underlying list.
                var wanted = new HashSet<string>();
                foreach (var (id, _) in target.Queue) wanted.Add(id);

                var live = new List<object>();
                if (_queueProp.GetValue(research, null) is IEnumerable q)
                    foreach (var el in q) live.Add(el);
                foreach (var el in live)
                {
                    string id = _researchIdField.GetValue(el) as string;
                    if (string.IsNullOrEmpty(id) || !wanted.Contains(id))
                        _cancel.Invoke(research, new[] { el });
                }

                // (3) Add missing elements (in snapshot order) and set each element's progress. Each
                // element is reconciled in its OWN try/catch: AddResearchToQueue throws on already-
                // completed / wrong-state / unmet-requirements elements (Research.cs:374-386), and one
                // bad element must NOT abort reconciling the rest of the queue.
                foreach (var (id, progress) in target.Queue)
                {
                    try
                    {
                        var el = Resolve(research, id);
                        if (el == null) continue;
                        if (IsCompleted(el)) continue;           // already done → not a queue item
                        // Set progress BEFORE (re)queue so AddResearchToQueue's instant-complete check
                        // (Research.cs:393) sees the authoritative value.
                        try { _progressField.SetValue(el, progress); } catch { /* best-effort */ }
                        if (!InQueue(research, el))
                            _addToQueue.Invoke(research, new[] { el });
                        // Re-affirm the authoritative progress AFTER the requeue. BeginResearching()
                        // (ResearchElement.cs:282) does not reset ResearchProgress, but a direct re-set
                        // guarantees the snapshot value survives regardless of the requeue path.
                        try { _progressField.SetValue(el, progress); } catch { /* re-affirm */ }
                    }
                    catch (Exception elEx)
                    {
                        Debug.LogError("[Multipleer] ResearchStateReflection.Apply requeue '" + id + "' failed (skipped): " + elEx.Message);
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Apply failed: " + ex.Message); }
        }

        /// <summary>Host apply for the client cancel intent: <c>Research.Cancel(resolve(id))</c>.</summary>
        public static void Cancel(GeoRuntime rt, string researchId)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetResearch(rt);
                var el = Resolve(research, researchId);
                if (el == null) return;
                _cancel.Invoke(research, new[] { el });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Cancel failed: " + ex.Message); }
        }

        /// <summary>Read <c>ResearchElement.ResearchID</c> off a live element (interceptor side).</summary>
        public static string GetId(object researchElement)
        {
            if (researchElement == null) return null;
            try { Ensure(); return _researchIdField?.GetValue(researchElement) as string; }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.GetId failed: " + ex.Message); return null; }
        }

        // ─── host change-event subscription (faction-level Start/Complete) ───

        /// <summary>
        /// Subscribe a no-arg callback to BOTH <c>GeoFaction.ResearchStartedEventHandler</c> and
        /// <c>ResearchCompletedEventHandler</c> on the player faction. Returns an opaque token to pass to
        /// <see cref="Unsubscribe"/>, or null. The events are <c>Action&lt;GeoFaction, ResearchElement&gt;</c>;
        /// we emit a DynamicMethod adapter matching that exact signature (mirrors WalletReflection).
        /// </summary>
        public static object SubscribeFactionResearchEvents(GeoRuntime rt, Action onChanged)
        {
            if (onChanged == null) return null;
            try
            {
                var fac = rt?.PhoenixFaction();
                if (fac == null) return null;
                var facType = fac.GetType();
                var startEvt = facType.GetEvent("ResearchStartedEventHandler", BindingFlags.Public | BindingFlags.Instance);
                var doneEvt = facType.GetEvent("ResearchCompletedEventHandler", BindingFlags.Public | BindingFlags.Instance);
                if (startEvt == null && doneEvt == null) return null;

                var token = new FactionEventToken { Faction = fac };
                token.StartHandler = MakeAdapter(startEvt, onChanged);
                if (token.StartHandler != null) { startEvt.AddEventHandler(fac, token.StartHandler); token.StartEvt = startEvt; }
                token.DoneHandler = MakeAdapter(doneEvt, onChanged);
                if (token.DoneHandler != null) { doneEvt.AddEventHandler(fac, token.DoneHandler); token.DoneEvt = doneEvt; }
                return token;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.SubscribeFactionResearchEvents failed: " + ex.Message); return null; }
        }

        public static void Unsubscribe(object token)
        {
            if (!(token is FactionEventToken t) || t.Faction == null) return;
            try
            {
                if (t.StartEvt != null && t.StartHandler != null) t.StartEvt.RemoveEventHandler(t.Faction, t.StartHandler);
                if (t.DoneEvt != null && t.DoneHandler != null) t.DoneEvt.RemoveEventHandler(t.Faction, t.DoneHandler);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.Unsubscribe failed: " + ex.Message); }
        }

        private sealed class FactionEventToken
        {
            public object Faction;
            public EventInfo StartEvt;
            public EventInfo DoneEvt;
            public Delegate StartHandler;
            public Delegate DoneHandler;
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static object Resolve(object research, string id)
        {
            if (research == null || string.IsNullOrEmpty(id)) return null;
            try { return _getById.Invoke(research, new object[] { id }); }
            catch { return null; }
        }

        private static bool IsCompleted(object element)
        {
            // ResearchElement.IsCompleted (bool property). Resolve lazily off the live element type.
            try
            {
                var p = AccessTools.Property(element.GetType(), "IsCompleted");
                return p != null && (bool)p.GetValue(element, null);
            }
            catch { return false; }
        }

        private static bool InQueue(object research, object element)
        {
            try
            {
                if (_queueProp.GetValue(research, null) is IEnumerable q)
                    foreach (var el in q) if (ReferenceEquals(el, element)) return true;
            }
            catch { /* fall through */ }
            return false;
        }

        /// <summary>Emit a DynamicMethod delegate matching <paramref name="evt"/>'s signature that ignores
        /// its args and calls <paramref name="onChanged"/>. Null if the event is null.</summary>
        private static Delegate MakeAdapter(EventInfo evt, Action onChanged)
        {
            if (evt == null) return null;
            try
            {
                Type handlerType = evt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null) return null;
                ParameterInfo[] ps = invoke.GetParameters();

                Type[] dmSig = new Type[ps.Length + 1];
                dmSig[0] = typeof(Action);
                for (int i = 0; i < ps.Length; i++) dmSig[i + 1] = ps[i].ParameterType;

                var dm = new DynamicMethod("Faction_Research_Adapter", typeof(void), dmSig,
                    typeof(ResearchStateReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType, onChanged);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchStateReflection.MakeAdapter failed: " + ex.Message); return null; }
        }
    }
}
