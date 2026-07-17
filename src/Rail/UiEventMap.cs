using System;
using System.Collections.Generic;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 reactivity table: per entity KIND, the native event/nudge that repaints already-open UI
    /// the instant a rail delta lands. This is presentation-only knowledge (which native event a view
    /// listens to), NOT sync logic — the rail itself stays subsystem-blind.
    ///   • Wallet → raise its own <c>ResourcesChanged</c> (GeoscapeView relays to FactionResourcesChanged
    ///     → info bar / manufacturing / replenish screens repaint natively).
    ///   • Research / ResearchElement → the proven ResearchSync repaint path (open research screen
    ///     SetupQueue rebuild, else agenda-tracker nudge).
    ///   • Timing → no-op: Paused/Scale rode the native property setters during apply, which already
    ///     fire OnPausedEvent/EffectiveScaleChangedEvent; the clock readout polls Timing.Now per frame.
    ///   • ItemStorage → raise its own <c>StorageChanged</c> (GeoItemDict apply wrote _storageItems
    ///     directly, bypassing AddItem/RemoveItem = no native notify) → free-space info bar +
    ///     inventory/manufacturing views (which subscribe to it) repaint. Covers GeoFaction + GeoSite.
    ///   • Unknown kind → logged ONCE — the to-do list for the next event-map entry.
    /// </summary>
    public static class UiEventMap
    {
        private static readonly HashSet<string> _loggedUnknown = new HashSet<string>(StringComparer.Ordinal);

        public static void Fire(HashSet<object> touched, GeoLevelController geo)
        {
            if (touched == null || touched.Count == 0) return;
            bool researchDone = false;
            var walletsDone = new HashSet<object>();
            foreach (var entity in touched)
            {
                try
                {
                    switch (entity)
                    {
                        case Wallet w:
                            if (walletsDone.Add(w)) RaiseResourcesChanged(w);
                            break;
                        case Research _:
                        case ResearchElement _:
                            if (!researchDone) { researchDone = true; ResearchSync.RepaintResearchUi(); }
                            break;
                        case Timing _:
                            break; // native setter events already fired during apply
                        case ItemStorage storage:
                            RaiseStorageChanged(storage);
                            break;
                        default:
                            if (_loggedUnknown.Add(entity.GetType().Name))
                                Debug.Log("[Multiplayer][rail] UiEventMap: no repaint mapping for " + entity.GetType().Name + " (logged once)");
                            break;
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] UiEventMap fire failed for " + entity.GetType().Name + ": " + ex.Message); }
            }
        }

        private static readonly System.Reflection.FieldInfo ResourcesChangedField =
            AccessTools.Field(typeof(Wallet), "ResourcesChanged");

        /// <summary>Raise the wallet's own native event (empty diff pack — subscribers re-read totals).</summary>
        private static void RaiseResourcesChanged(Wallet wallet)
        {
            var del = ResourcesChangedField?.GetValue(wallet) as Delegate;
            del?.DynamicInvoke(wallet, new ResourcePack(), OperationReason.None);
        }

        /// <summary>Raise the storage's own native change notification (public Action field — pure notify,
        /// no AddItem/RemoveItem gameplay, no ammo unload) so every subscribed view repaints: free-space
        /// info bar re-reads GetStorageUsed, inventory/manufacturing lists rebuild. Guarded by
        /// SyncApplyScope so a re-entrant subscriber can't echo an intent back to the host (law 8).</summary>
        private static void RaiseStorageChanged(ItemStorage storage)
        {
            using (SyncApplyScope.Enter())
                storage.StorageChanged?.Invoke();
        }
    }
}
