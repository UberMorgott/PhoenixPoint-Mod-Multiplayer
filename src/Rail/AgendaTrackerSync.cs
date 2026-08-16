using System.Collections.Generic;
using Base.Core;
using Base.Serialization.General;
using HarmonyLib;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewServices;
using UnityEngine;

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

        private static float _nextHostTickAt;

        /// <summary>Host-side capture, UI-INDEPENDENT: reads the campaign model directly instead of
        /// riding UIModuleFactionAgendaTracker's own row list. The widget's 1 Hz coroutine
        /// (UpdateModuleDataCrt) only runs while it is Init'd, which only happens while the host is
        /// looking at UIStateNothingSelected/UIStateVehicleSelected (Init/Uninit called from those
        /// states only) — the moment the host opens Research/Manufacturing/BaseLayout, Uninit() stops
        /// the coroutine and the old postfix-based capture (AgendaRowCapturePatch /
        /// AgendaRowErrorCapturePatch, removed 2026-08-16) went silent, freezing "M#agenda" stale on
        /// every client regardless of task type (field-tested desync). Same per-row shape as
        /// UIModuleFactionAgendaTracker.UpdateData(element) (UIModuleFactionAgendaTracker.cs:271-309)
        /// and its InitialSetup row enumeration (:144-177), just sourced from the model.
        /// Label simplification (deliberate): the native widget formats each label through a
        /// per-TrackerType ViewElementDef.DisplayName1 (a linked UI asset on the live widget
        /// instance, e.g. "{0}" wrapping + ToUpper() — UIFactionDataTrackerElement.Init:51-81) that a
        /// headless host tick has no reachable handle on. Research/Manufacturing/Facility rows use the
        /// item's own localized name with no format wrapper/uppercasing instead — a plainer label
        /// beats a garbled or wrong one. Vehicle is the one row type whose label never went through
        /// that wrapper (UIModuleFactionAgendaTracker.cs:225), so it is replicated exactly.</summary>
        public static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (Time.realtimeSinceStartup < _nextHostTickAt) return;
            _nextHostTickAt = Time.realtimeSinceStartup + 1f;

            var faction = GenericApplier.GeoLevel()?.PhoenixFaction;
            if (faction == null) return;

            var freshKeys = new HashSet<string>();

            var research = faction.Research?.Current;
            if (research != null)
            {
                string key = RowKey(research, out string trackerType);
                if (key != null)
                {
                    freshKeys.Add(key);
                    var timeLeft = faction.Research.GetTotalTimeLeft(research);
                    State.Rows[key] = new AgendaRow
                    {
                        TrackerType = trackerType,
                        Label = research.GetLocalizedName(),
                        RemainingSeconds = (int)timeLeft.TimeSpan.TotalSeconds,
                        CanFinish = true,
                        ErrorText = null,
                    };
                }
            }

            var manufacture = faction.Manufacture;
            var queueItem = manufacture?.Current;
            if (queueItem != null)
            {
                string key = RowKey(queueItem, out string trackerType);
                if (key != null)
                {
                    freshKeys.Add(key);
                    var timeLeft = manufacture.GetTotalTimeLeft(queueItem);
                    var reason = manufacture.CanFinishConstruction(queueItem);
                    bool canFinish = reason == ItemManufacturing.ManufactureFailureReason.None;
                    State.Rows[key] = new AgendaRow
                    {
                        TrackerType = trackerType,
                        Label = queueItem.ManufacturableItem?.Name?.Localize(),
                        RemainingSeconds = (int)timeLeft.TimeSpan.TotalSeconds,
                        CanFinish = canFinish,
                        // Simplified same as the label: the native errorText is a localized short
                        // string looked up via the widget's own linked TooltipSettingsDef
                        // (UIModuleFactionAgendaTracker.cs:290) — unreachable headlessly. The raw
                        // enum name is legible enough for a blocked-manufacture row.
                        ErrorText = canFinish ? null : reason.ToString(),
                    };
                }
            }

            if (faction is GeoPhoenixFaction phoenix)
            {
                foreach (var pxBase in phoenix.Bases)
                {
                    if (pxBase?.Layout == null) continue;
                    foreach (var facility in pxBase.Layout.Facilities)
                    {
                        if (facility == null) continue;
                        if (!facility.IsRepairing && !(facility.IsBuilding && facility.ConstructionTime != TimeUnit.Zero))
                            continue;

                        string key = RowKey(facility, out string trackerType);
                        if (key == null) continue;
                        freshKeys.Add(key);
                        var timeLeft = (1f - facility.ConstructionPercentage) * facility.ConstructionTime;
                        State.Rows[key] = new AgendaRow
                        {
                            TrackerType = trackerType,
                            Label = facility.ViewElementDef?.DisplayName1.Localize(),
                            RemainingSeconds = (int)timeLeft.TimeSpan.TotalSeconds,
                            CanFinish = true,
                            ErrorText = null,
                        };
                    }
                }
            }

            foreach (var vehicle in faction.Vehicles)
            {
                if (vehicle == null) continue;
                var timeLeft = VehicleActionsViewService.GetCurrentActionTime(vehicle);
                if (timeLeft <= TimeUnit.Zero) continue;

                string key = RowKey(vehicle, out string trackerType);
                if (key == null) continue;
                freshKeys.Add(key);
                State.Rows[key] = new AgendaRow
                {
                    TrackerType = trackerType,
                    Label = (vehicle.Name + " " + VehicleActionsViewService.GetVehicleStateText(vehicle)).ToUpper(),
                    RemainingSeconds = (int)timeLeft.TimeSpan.TotalSeconds,
                    // Only ever added while timeLeft > Zero (the InitialSetup condition, above) — the
                    // native flag (canFinish = timeUnit <= TimeUnit.Zero) is therefore always false for
                    // a row this sweep writes; the row is removed below the tick it stops qualifying.
                    CanFinish = false,
                    ErrorText = null,
                };
            }

            // Removal sweep: a row not rebuilt this tick belongs to a task that finished, was
            // cancelled, or lost its source (e.g. a scrapped facility) — stop mirroring it, same as
            // the removed AgendaRowCapturePatch did on __result == true.
            List<string> stale = null;
            foreach (var existingKey in State.Rows.Keys)
                if (!freshKeys.Contains(existingKey))
                    (stale ?? (stale = new List<string>())).Add(existingKey);
            if (stale != null)
                foreach (var k in stale) State.Rows.Remove(k);
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
