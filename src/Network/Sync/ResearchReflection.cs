using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Reflection bridge to <c>PhoenixPoint.Geoscape.Entities.Research.Research</c>. The mod has NO
    /// compile-time game references, so every member is resolved by name and cached.
    ///
    /// Verified against the decompile (2026-06-15):
    ///   • start:    <c>Research.AddResearchToQueue(ResearchElement research)</c> (Research.cs:370).
    ///   • complete: <c>Research.CompleteResearch(ResearchElement research)</c> (Research.cs:576).
    ///   • resolver: <c>Research.GetResearchById(string researchId)</c> (Research.cs:763) →
    ///               <c>AllResearchesArray.FirstOrDefault(r => r.ResearchID == researchId)</c>.
    ///   • id getter: <c>ResearchElement.ResearchID</c> (public readonly string, ResearchElement.cs:136).
    ///   • the <c>Research</c> instance is reached via <c>GeoFaction.Research</c> (GeoFaction.cs:79).
    /// </summary>
    public static class ResearchReflection
    {
        private static bool _ready;
        private static Type _researchType;        // ...Research.Research
        private static Type _researchElementType; // ...Research.ResearchElement
        private static MethodInfo _addToQueue;     // Research.AddResearchToQueue(ResearchElement)
        private static MethodInfo _complete;       // Research.CompleteResearch(ResearchElement)
        private static MethodInfo _getById;        // Research.GetResearchById(string)
        private static FieldInfo _researchIdField; // ResearchElement.ResearchID
        private static PropertyInfo _factionResearchProp; // GeoFaction.Research

        private static void Ensure()
        {
            if (_ready) return;
            _researchType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            _researchElementType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.ResearchElement");
            if (_researchType == null || _researchElementType == null) return;

            _addToQueue = AccessTools.Method(_researchType, "AddResearchToQueue", new[] { _researchElementType });
            _complete = AccessTools.Method(_researchType, "CompleteResearch", new[] { _researchElementType });
            _getById = AccessTools.Method(_researchType, "GetResearchById", new[] { typeof(string) });
            _researchIdField = AccessTools.Field(_researchElementType, "ResearchID");

            _ready = _addToQueue != null && _complete != null && _getById != null && _researchIdField != null;
        }

        /// <summary>Read <c>ResearchElement.ResearchID</c> off a live element (interceptor side).</summary>
        public static string GetId(object researchElement)
        {
            if (researchElement == null) return null;
            try { Ensure(); return _researchIdField?.GetValue(researchElement) as string; }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchReflection.GetId failed: " + ex.Message); return null; }
        }

        /// <summary>The live player-faction <c>Research</c> instance, or null.</summary>
        private static object GetFactionResearch(GeoRuntime rt)
        {
            var fac = rt?.PhoenixFaction();
            if (fac == null) return null;
            try
            {
                if (_factionResearchProp == null || _factionResearchProp.DeclaringType == null
                    || !_factionResearchProp.DeclaringType.IsInstanceOfType(fac))
                    _factionResearchProp = AccessTools.Property(fac.GetType(), "Research");
                return _factionResearchProp?.GetValue(fac, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchReflection.GetFactionResearch failed: " + ex.Message); return null; }
        }

        private static object Resolve(object research, string id)
        {
            if (research == null || string.IsNullOrEmpty(id)) return null;
            return _getById.Invoke(research, new object[] { id });
        }

        /// <summary>Resolve <paramref name="researchId"/> → ResearchElement and queue it (Apply side).</summary>
        public static void AddToQueue(GeoRuntime rt, string researchId)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetFactionResearch(rt);
                var element = Resolve(research, researchId);
                if (element == null) return;
                _addToQueue.Invoke(research, new[] { element });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchReflection.AddToQueue failed: " + ex.Message); }
        }

        /// <summary>Resolve <paramref name="researchId"/> → ResearchElement and complete it (Apply side).</summary>
        public static void Complete(GeoRuntime rt, string researchId)
        {
            try
            {
                Ensure();
                if (!_ready) return;
                var research = GetFactionResearch(rt);
                var element = Resolve(research, researchId);
                if (element == null) return;
                _complete.Invoke(research, new[] { element });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ResearchReflection.Complete failed: " + ex.Message); }
        }
    }
}
