using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network.Sync.State;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Reflection bridge for GeoVehicle SITE EXPLORATION — the game-bound half of the exploration-progress mirror
    /// (surface <see cref="SurfaceIds.GeoVehicleExplore"/> 0xA7). The mod has NO compile-time game references, so
    /// every member is resolved by name via <see cref="AccessTools"/> and cached (bind-once per session). Vehicle /
    /// site resolution + the composite key are REUSED from <see cref="VehicleTravelReflection"/>.
    ///
    /// Verified against the decompile (2026-07-05):
    ///   • gate: <c>GeoVehicle.IsExploringSite</c> (bool PROP :236 = <c>_explorationUpdateable != null</c>).
    ///   • bar: <c>GeoVehicle._explorationVisuals</c> (private FIELD :77, type
    ///     <c>PhoenixPoint.Geoscape.View.GeoActorProgressionVisualController</c>) — the per-vehicle progress-bar
    ///     controller. Its public <c>Progression</c> float getter is the exact host fill (unclamped → we clamp01).
    ///   • create: PRIVATE <c>GeoVehicle.ExploreCurrentSite(TimeUnit start, TimeUnit end)</c> :448 — instantiates
    ///     <c>VehicleDef.ExplorationVisualsPrefab</c> onto <c>CurrentSite.Surface</c> + <c>SetProgression</c>. We
    ///     reuse it to spawn the SAME native bar on the client (most-native), then feed subsequent updates straight
    ///     to <c>GeoActorProgressionVisualController.SetProgression(start,end,timing)</c> (no re-schedule/no leak).
    ///   • destroy: PRIVATE <c>GeoVehicle.EndExploreCurrentSite()</c> :460 — destroys the bar + stops the updateable.
    ///   • time: <c>ActorComponent.Timing</c> (PROP) → <c>Base.Core.Timing.Now</c> (PROP, TimeUnit); the client's is
    ///     PAUSED so <c>Now</c> is a frozen constant. <c>Base.Core.TimeUnit</c> is a struct with static
    ///     <c>FromSeconds(float)</c> + <c>operator +/-</c> — we anchor Start/End around the frozen Now so the native
    ///     <c>Progression = (Now - Start)/(End - Start)</c> evaluates to exactly the host fraction.
    ///
    /// All reflection is null-safe: a missing member DEGRADES (best-effort) rather than throwing.
    /// </summary>
    public static class GeoVehicleExploreReflection
    {
        // Nominal exploration WINDOW (seconds) the client anchors the bar around the frozen Now. Only the RATIO
        // matters (Progression = offBefore/(offBefore+offAfter) = fraction), so any positive window renders the
        // host fraction; 1 h keeps the offsets small (float-exact) and _progressMinutes sane (60).
        private const float WindowSeconds = 3600f;

        private static bool _ready;
        private static Type _geoVehicleType, _visualsType, _timeUnitType, _timingType;
        private static PropertyInfo _isExploringProp;     // GeoVehicle.IsExploringSite (bool)
        private static FieldInfo _explorationVisualsField; // GeoVehicle._explorationVisuals (GeoActorProgressionVisualController)
        private static MethodInfo _exploreCurrentSite;    // GeoVehicle.ExploreCurrentSite(TimeUnit,TimeUnit) — private
        private static MethodInfo _endExploreCurrentSite; // GeoVehicle.EndExploreCurrentSite() — private, no-arg
        private static PropertyInfo _progressionProp;     // GeoActorProgressionVisualController.Progression (float)
        private static MethodInfo _setProgression;        // GeoActorProgressionVisualController.SetProgression(TimeUnit,TimeUnit,Timing)
        private static PropertyInfo _timingProp;          // ActorComponent.Timing (Base.Core.Timing)
        private static PropertyInfo _nowProp;             // Timing.Now (TimeUnit)
        private static MethodInfo _fromSeconds;           // TimeUnit.FromSeconds(float) — static
        private static MethodInfo _opAdd, _opSub;         // TimeUnit operator + / - (static)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            _geoVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            _timeUnitType = AccessTools.TypeByName("Base.Core.TimeUnit");
            if (_geoVehicleType == null || _timeUnitType == null) return;

            _isExploringProp = AccessTools.Property(_geoVehicleType, "IsExploringSite");
            _explorationVisualsField = AccessTools.Field(_geoVehicleType, "_explorationVisuals");
            _visualsType = _explorationVisualsField?.FieldType
                           ?? AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoActorProgressionVisualController");
            // Disambiguate the TimeUnit-pair ExploreCurrentSite (vs any future overload) by exact param match.
            _exploreCurrentSite = AccessTools.Method(_geoVehicleType, "ExploreCurrentSite", new[] { _timeUnitType, _timeUnitType });
            _endExploreCurrentSite = AccessTools.Method(_geoVehicleType, "EndExploreCurrentSite");

            if (_visualsType != null)
            {
                _progressionProp = AccessTools.Property(_visualsType, "Progression");
                _timingType = AccessTools.TypeByName("Base.Core.Timing");
                if (_timingType != null)
                    _setProgression = AccessTools.Method(_visualsType, "SetProgression",
                        new[] { _timeUnitType, _timeUnitType, _timingType });
            }

            _timingProp = AccessTools.Property(_geoVehicleType, "Timing");
            if (_timingType == null) _timingType = AccessTools.TypeByName("Base.Core.Timing");
            if (_timingType != null) _nowProp = AccessTools.Property(_timingType, "Now");
            _fromSeconds = AccessTools.Method(_timeUnitType, "FromSeconds", new[] { typeof(float) });
            _opAdd = AccessTools.Method(_timeUnitType, "op_Addition", new[] { _timeUnitType, _timeUnitType });
            _opSub = AccessTools.Method(_timeUnitType, "op_Subtraction", new[] { _timeUnitType, _timeUnitType });

            // Host READ needs only the gate + visuals + current-site (via VehicleTravelReflection). Client APPLY
            // additionally needs the create/update/destroy + time members; _ready gates the read path, apply guards
            // each member individually so a partial bind still degrades gracefully.
            _ready = _isExploringProp != null && _explorationVisualsField != null;
        }

        // ─── HOST: read a live vehicle's exploration progress into a pure meta ────────────────────────────────

        /// <summary>HOST: read a vehicle's exploration state into a pure <see cref="GeoVehicleExploreMeta"/> —
        /// {exploring, exploredSiteId, bar-fill 0..1}. Not exploring → {false, -1, 0}. False if the key can't be
        /// read.</summary>
        public static bool TryReadExploreMeta(GeoRuntime rt, object vehicle, out GeoVehicleExploreMeta meta)
        {
            meta = default;
            try
            {
                Ensure(rt);
                if (!_ready || vehicle == null) return false;
                if (!VehicleTravelReflection.TryReadVehicleKey(vehicle, out int ownerId, out int vehicleId)) return false;

                bool exploring = false;
                try { exploring = (bool)_isExploringProp.GetValue(vehicle, null); } catch { exploring = false; }

                if (!exploring)
                {
                    meta = new GeoVehicleExploreMeta(ownerId, vehicleId, false, -1, 0f);
                    return true;
                }

                int siteId = -1;
                VehicleTravelReflection.TryReadCurrentSiteId(rt, vehicle, out siteId);

                float progress = 0f;
                try
                {
                    var vis = _explorationVisualsField.GetValue(vehicle);
                    if (vis != null && _progressionProp != null)
                        progress = Convert.ToSingle(_progressionProp.GetValue(vis, null));
                }
                catch { progress = 0f; }
                if (progress < 0f) progress = 0f; else if (progress > 1f) progress = 1f;

                meta = new GeoVehicleExploreMeta(ownerId, vehicleId, true, siteId, progress);
                return true;
            }
            catch { return false; }
        }

        // ─── CLIENT (sim frozen): render the native bar at the host fraction ──────────────────────────────────

        /// <summary>CLIENT: reflect <paramref name="meta"/> onto the resolved live vehicle's NATIVE exploration bar.
        /// Exploring → ensure the bar exists (reuse <c>ExploreCurrentSite</c> to spawn it, else
        /// <c>SetProgression</c> to update it) with Start/End anchored around the frozen <c>Timing.Now</c> so the
        /// native <c>Progression</c> renders exactly <c>meta.Progress</c>. Not exploring → tear the bar down
        /// (<c>EndExploreCurrentSite</c>). Every step individually guarded. Returns true if the vehicle resolved.</summary>
        public static bool ApplyExploreMeta(GeoRuntime rt, GeoVehicleExploreMeta meta)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return false;
                object vehicle = VehicleTravelReflection.ResolveVehicle(rt, meta.OwnerId, meta.VehicleId);
                if (vehicle == null) return false;

                object visuals = null;
                try { visuals = _explorationVisualsField.GetValue(vehicle); } catch { visuals = null; }

                if (!meta.Exploring)
                {
                    // Host says done/stopped → destroy the native bar if we have one (idempotent otherwise).
                    if (visuals != null && _endExploreCurrentSite != null)
                    {
                        try { _endExploreCurrentSite.Invoke(vehicle, null); }
                        catch (Exception ex) { Debug.LogError("[Multipleer][geo] explore end failed: " + ex.Message); }
                    }
                    return true;
                }

                // Exploring: the bar parents to CurrentSite.Surface — make sure CurrentSite is set (self-sufficient
                // even if the 0xA6 travel-meta arrival was missed/reordered; display-only, never stomps a good value).
                VehicleTravelReflection.EnsureCurrentSiteBacking(rt, vehicle, meta.SiteId);

                if (!TryBuildStartEnd(vehicle, meta.Progress, out object timing, out object start, out object end))
                    return true;   // couldn't read frozen Now / build TimeUnits → skip (retry next poll)

                if (visuals == null)
                {
                    // First sighting → spawn the SAME native bar the host uses (handles prefab / parenting / colours /
                    // SetProgression). It also schedules a completion updateable on the frozen client Timing, which
                    // never fires (Now pinned) — harmless, and needed so EndExploreCurrentSite can later tear down.
                    if (_exploreCurrentSite != null)
                    {
                        try { _exploreCurrentSite.Invoke(vehicle, new[] { start, end }); }
                        catch (Exception ex) { Debug.LogError("[Multipleer][geo] explore create failed: " + ex.Message); }
                    }
                }
                else if (_setProgression != null)
                {
                    // Subsequent poll → just re-anchor the existing bar's Start/End for the new fraction (no
                    // re-schedule, so no leaked updateable). The bar's per-frame Update holds the value between polls.
                    try { _setProgression.Invoke(visuals, new[] { start, end, timing }); }
                    catch (Exception ex) { Debug.LogError("[Multipleer][geo] explore update failed: " + ex.Message); }
                }
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] GeoVehicleExploreReflection.ApplyExploreMeta failed: " + ex.Message); return false; }
        }

        /// <summary>Build the vehicle's frozen <c>Timing</c> plus Start/End TimeUnits so that
        /// <c>Progression = (Now - Start)/(End - Start) = f</c>: Start = Now - f·W, End = Now + (1-f)·W (window W).
        /// The precise TimeUnit <c>Now</c> is kept as-is; only the small (≤W) offsets pass through float. False if
        /// any time member is unbound / unreadable.</summary>
        private static bool TryBuildStartEnd(object vehicle, float f, out object timing, out object start, out object end)
        {
            timing = null; start = null; end = null;
            if (_timingProp == null || _nowProp == null || _fromSeconds == null || _opAdd == null || _opSub == null)
                return false;
            timing = _timingProp.GetValue(vehicle, null);
            if (timing == null) return false;
            object now = _nowProp.GetValue(timing, null);
            if (now == null) return false;
            if (f < 0f) f = 0f; else if (f > 1f) f = 1f;
            object offBefore = _fromSeconds.Invoke(null, new object[] { f * WindowSeconds });
            object offAfter = _fromSeconds.Invoke(null, new object[] { (1f - f) * WindowSeconds });
            start = _opSub.Invoke(null, new[] { now, offBefore });   // Now - f·W
            end = _opAdd.Invoke(null, new[] { now, offAfter });      // Now + (1-f)·W
            return start != null && end != null;
        }
    }
}
