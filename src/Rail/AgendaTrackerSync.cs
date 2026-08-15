using System.Collections.Generic;
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

        /// <summary>Host-side capture: mirrors every row the widget's own UpdateData computed into
        /// AgendaTrackerSync.State.Rows. UpdateData(UIFactionDataTrackerElement) runs on every peer
        /// (local UI, not patched out) — guard on IsHost so clients never write. __result true means
        /// the row finished/disposed this tick (UIModuleFactionAgendaTracker.cs:304-308); stop mirroring
        /// it. element.CurrentTimeLeft is set by element.UpdateData(...) inside the native method body
        /// (UIModuleFactionAgendaTracker.cs:303, UIFactionDataTrackerElement.cs:85) before this postfix
        /// runs, so it already reflects the value just computed.</summary>
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
                };
            }
        }
    }
}
