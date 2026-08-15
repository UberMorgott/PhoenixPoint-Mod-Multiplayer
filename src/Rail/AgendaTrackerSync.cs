using System.Collections.Generic;
using Base.Core;
using Base.Serialization.General;
using HarmonyLib;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace Multiplayer.Network.Sync
{
    /// <summary>One row of the Faction Agenda Tracker HUD widget (top-right geoscape corner),
    /// as the host's own UI already computed it — TrackerType/label/remaining time, nothing
    /// client-recomputable. See docs/superpowers/specs/2026-08-16-multiplayer-agenda-tracker-sync-design.md.</summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class AgendaRow
    {
        public string TrackerType;
        public string Label;
        public int RemainingSeconds;
        public bool CanFinish;
        public string ErrorText;
    }

    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class AgendaState
    {
        public Dictionary<string, AgendaRow> Rows = new Dictionary<string, AgendaRow>();
    }

    internal static class AgendaTrackerSync
    {
        internal const string RootKey = "M#agenda";

        /// <summary>Host writes it, the generic value rail mirrors it, every client's tracker
        /// widget reads it back instead of recomputing countdowns locally.</summary>
        internal static readonly AgendaState State = new AgendaState();

        internal static void Register() => IdentityResolver.RegisterModRoot(RootKey, State);

        /// <summary>Same mod-root contract as MistSync/AssignSync: state must be EMPTY at every
        /// reload boundary (IdentityResolver.cs:205-206) — the transferred save has its own agenda
        /// rows, so nothing is lost in the gap.</summary>
        internal static void ResetForReloadBoundary() => State.Rows.Clear();

        /// <summary>Stable row key for a tracker element's TrackedObject. Reuses the SAME identity
        /// each system's own intent-capture code already addresses that task by — not a new scheme:
        /// Research → ResearchElement.ResearchID; Manufacturing → ManufactureQueueItem's
        /// ManufacturableItem.RelatedItemDef.Guid; Facility → GeoSite.SiteId + GeoPhoenixFacility.FacilityId;
        /// Vehicle (travel/exploration) → IdentityResolver's "V#<id>@<ownerGuid>" root ref.
        /// Returns null for an unrecognized TrackedObject shape (row is left unsynced, native computation applies).</summary>
        internal static string RowKey(object trackedObject, out string trackerType)
        {
            switch (trackedObject)
            {
                case ResearchElement research:
                    trackerType = "Research";
                    return "Research:" + research.ResearchID;
                case ItemManufacturing.ManufactureQueueItem queueItem:
                {
                    var def = queueItem.ManufacturableItem?.RelatedItemDef;
                    trackerType = def == null ? null : "Manufacturing";
                    return def == null ? null : "Manufacturing:" + def.Guid;
                }
                case GeoPhoenixFacility facility:
                    trackerType = "Facility";
                    return "Facility:" + facility.PxBase?.Site?.SiteId + ":" + facility.FacilityId;
                case GeoVehicle vehicle:
                    trackerType = vehicle.Travelling ? "AircraftTravel" : "AircraftExploration";
                    return (vehicle.Travelling ? "AircraftTravel:" : "AircraftExploration:")
                        + IdentityResolver.RootRef(vehicle);
                default:
                    trackerType = null;
                    return null;
            }
        }

        /// <summary>Stash for the current tick's real canFinish/errorText, written by
        /// AgendaRowErrorCapturePatch and consumed by AgendaRowCapturePatch immediately after.
        /// Safe as a bare static pair: UIModuleFactionAgendaTracker.UpdateData calls
        /// element.UpdateData(timeUnit, flag, errorText) synchronously BEFORE its own disposal
        /// check and return (UIModuleFactionAgendaTracker.cs:303-308) — so the inner postfix below
        /// always fires, then clears, before the outer postfix runs for the same element, on the
        /// single Unity main thread (no reentrancy).</summary>
        private static bool _pendingCanFinish;
        private static string _pendingErrorText;

        /// <summary>Host-side capture (inner method): UIFactionDataTrackerElement.UpdateData receives
        /// canFinish/errorText as its own parameters — the only place they're available, since
        /// UIFactionDataTrackerElement never stores them as fields (UIFactionDataTrackerElement.cs:83-94,
        /// only consumed locally to set TrackedTime.text). Stash them for AgendaRowCapturePatch.</summary>
        [HarmonyPatch(typeof(UIFactionDataTrackerElement), "UpdateData", new[] { typeof(TimeUnit), typeof(bool), typeof(string) })]
        internal static class AgendaRowErrorCapturePatch
        {
            private static void Postfix(bool canFinish, string errorText)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;

                _pendingCanFinish = canFinish;
                _pendingErrorText = errorText;
            }
        }

        /// <summary>Host-side capture: mirrors every row the widget's own UpdateData computed into
        /// AgendaTrackerSync.State.Rows. UpdateData(UIFactionDataTrackerElement) runs on every peer
        /// (local UI, not patched out) — guard on IsHost so clients never write. __result true means
        /// the row finished/disposed this tick, matching the native condition timeUnit &lt;= Zero &amp;&amp; flag
        /// (UIModuleFactionAgendaTracker.cs:304-308); stop mirroring it. element.CurrentTimeLeft and
        /// _pendingCanFinish/_pendingErrorText are set by element.UpdateData(...) inside the native
        /// method body (UIModuleFactionAgendaTracker.cs:303, UIFactionDataTrackerElement.cs:85) before
        /// this postfix runs, so they already reflect the value just computed this tick.</summary>
        [HarmonyPatch(typeof(UIModuleFactionAgendaTracker), "UpdateData", new[] { typeof(UIFactionDataTrackerElement) })]
        internal static class AgendaRowCapturePatch
        {
            private static void Postfix(UIFactionDataTrackerElement element, bool __result)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;

                string key = RowKey(element.TrackedObject, out string trackerType);
                if (key == null) return;

                if (__result)
                {
                    // Row finished/disposed this tick — stop mirroring it.
                    State.Rows.Remove(key);
                    return;
                }

                State.Rows[key] = new AgendaRow
                {
                    TrackerType = trackerType,
                    Label = element.TrackedName != null ? element.TrackedName.text : null,
                    RemainingSeconds = (int)element.CurrentTimeLeft.TimeSpan.TotalSeconds,
                    CanFinish = _pendingCanFinish,
                    ErrorText = _pendingErrorText,
                };
            }
        }

        /// <summary>Client-side apply: skips native per-row computation and applies the host-synced
        /// row instead, when one exists. Same native method as AgendaRowCapturePatch (Postfix,
        /// host-only) — safe to patch the same method with both: each side's own IsHost/!IsHost guard
        /// is the actual invariant (not Harmony's Prefix/Postfix ordering — Postfixes always run even
        /// when an unrelated Prefix elsewhere returns false and skips the original method), so capture
        /// only ever writes when IsHost and apply only ever reads/short-circuits when !IsHost.</summary>
        [HarmonyPatch(typeof(UIModuleFactionAgendaTracker), "UpdateData", new[] { typeof(UIFactionDataTrackerElement) })]
        internal static class AgendaRowApplyPatch
        {
            private static bool Prefix(UIFactionDataTrackerElement element, ref bool __result)
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // host: run native

                string key = RowKey(element.TrackedObject, out _);
                if (key == null || !State.Rows.TryGetValue(key, out var row))
                    return true; // no synced row yet (e.g. just joined) — fall back to native computation

                var timeLeft = TimeUnit.FromSeconds(row.RemainingSeconds);
                bool disposed = row.RemainingSeconds <= 0 && row.CanFinish;
                element.UpdateData(timeLeft, row.CanFinish, row.ErrorText);
                __result = disposed;
                return false; // skip native computation entirely
            }
        }
    }
}
