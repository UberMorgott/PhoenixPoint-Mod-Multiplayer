using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT-ONLY HARD CHOKEPOINT that blocks EVERY client-LOCAL geoscape-event RAISE at the single
    /// true funnel: <c>GeoscapeEventSystem.OnGeoscapeEvent(BaseEventData, BaseEventContext)</c>
    /// (private, GeoscapeEventSystem.cs:610). ALL geoscape event raises pass through here — both the
    /// eventus-handler path (<c>_eventus.RegisterHandler(typeof(GeoscapeEventData), OnGeoscapeEvent)</c>
    /// GeoscapeEventSystem.cs:546) and the direct <c>TriggerGeoscapeEvent</c> path (:328). Gating the
    /// funnel itself is immune to the <c>SuppressEvents</c> flag's value/timing window (the prior leak):
    /// no flag re-assert race, no per-raise path that slips past.
    ///
    /// ROOT CAUSE (RCA, in-game DLL E1B46739): the client geoscape sim is fully LIVE — the time-sync
    /// clock overwrite advances <c>Timing.Now</c>, so the client runs <c>LevelHourlyUpdateCrt</c>,
    /// travel/arrival and <c>PhoenixFaction_OnSiteFirstTimeVisited</c>, which picks a RANDOM
    /// <c>EncounterID</c> independently of the host. Host raises event A, client raises event B for the
    /// same site → the client showed TWO dialogs (its own B + the host-broadcast A). This prefix skips
    /// the raise entirely on the client (returns false → native body never runs → no local event ever
    /// triggers/records/displays). The client then shows ONLY host-broadcast events.
    ///
    /// SAFE FOR HOST-BROADCAST EVENTS (VERIFIED 2026-06-17): the client shows the host's events via
    /// <c>SyncEngine.OnEventRaised</c> → <c>EventDisplay.Show</c>, which builds the
    /// <c>UIStateGeoscapeEvent</c> DIRECTLY (reflected ctor) and queues it through
    /// <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c> (EventDisplay.cs:82-101). The reconstruction
    /// (<c>EventReflection.BuildEvent</c>/<c>BuildResultEvent</c>) uses <c>new GeoscapeEvent(data, ctx)</c>
    /// directly (EventReflection.cs:25/412/507) — NEITHER path routes through <c>OnGeoscapeEvent</c> or
    /// <c>TriggerGeoscapeEvent</c>. So this chokepoint never blocks a host-broadcast dialog. The
    /// belt-and-suspenders <c>!SyncApplyScope.IsApplying</c> guard is kept anyway: if any future
    /// engine-driven apply ever did funnel through here, it would pass.
    ///
    /// SCOPE: ONLY event RAISING — NOT the full client sim-freeze (the 13-producer geoscape freeze is a
    /// separate roadmap item needing full host->client geoscape state replication; deliberately NOT done
    /// here). HOST is byte-unchanged (host keeps raising + broadcasting + showing its own events). Gated
    /// strictly <c>IsActiveSession &amp;&amp; !IsHost &amp;&amp; !IsApplying</c>. Best-effort try/catch — never
    /// throws into game code; on any failure the native body runs (fail-open, matching the prior patches).
    ///
    /// Belt to the existing suspenders (additive, both kept):
    ///   • <c>EventSuppressClientGeoscapePatch</c> sets <c>SuppressEvents = true</c> at source.
    ///   • <c>EventRaisedDisplayPatch.Prefix</c> blocks <c>GeoscapeView.OnGeoscapeEventRaised</c>.
    /// This new prefix is the HARD guarantee: it gates the raise funnel one level deeper than both.
    /// </summary>
    [HarmonyPatch]
    public static class ClientEventRaiseChokepointPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventSystem");
            if (t == null) return false;
            // Prefer the explicit 2-arg signature; fall back to name-only (OnGeoscapeEvent is the sole
            // overload on the type, so the name-only lookup is unambiguous if the param types don't resolve).
            var dataT = AccessTools.TypeByName("Base.Eventus.BaseEventData");
            var ctxT = AccessTools.TypeByName("Base.Eventus.BaseEventContext");
            if (dataT != null && ctxT != null)
                _target = AccessTools.Method(t, "OnGeoscapeEvent", new[] { dataT, ctxT });
            if (_target == null)
                _target = AccessTools.Method(t, "OnGeoscapeEvent");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        /// <summary>
        /// Returns false (skip the native raise) only on a CLIENT in an active co-op session that is NOT
        /// inside a sync-apply. Host (and any exception) → true → native runs unchanged.
        /// </summary>
        public static bool Prefix()
        {
            try
            {
                if (SyncApplyScope.IsApplying) return true;   // engine-driven apply → never block
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsActiveSession && !engine.IsHost)
                    return false;                             // client: no local event raise ever
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientEventRaiseChokepointPatch.Prefix failed: " + ex.Message); }
            return true;                                       // host (and any failure): native runs
        }
    }
}
