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
    ///     inventory views (which subscribe to it) repaint. Manufacturing + equip screens are
    ///     PULL-model (no StorageChanged subscription) → also mark the open screen dirty; the flush
    ///     re-enters it, or for UIStateManufacturing routes to its dedicated per-panel rebuild.
    ///     Covers GeoFaction + GeoSite.
    ///   • Unknown kind → logged ONCE — the to-do list for the next event-map entry.
    /// </summary>
    public static class UiEventMap
    {
        private static readonly HashSet<string> _loggedUnknown = new HashSet<string>(StringComparer.Ordinal);

        public static void Fire(HashSet<object> touched, GeoLevelController geo)
        {
            if (touched == null || touched.Count == 0) return;
            bool researchDone = false;
            foreach (var entity in touched)
            {
                try
                {
                    switch (entity)
                    {
                        case Wallet w: // touched is a HashSet — each wallet fires at most once
                            RaiseResourcesChanged(w);
                            break;
                        case Research _:
                        case ResearchElement _:
                            if (!researchDone) { researchDone = true; ResearchSync.RepaintResearchUi(); }
                            break;
                        case Timing _:
                        case TimingInstanceData _: // the TimeAnchor scratch DTO
                            // No-op, GROUNDED (not assumed): Paused/Scale rode the native property setters
                            // during apply, which already fire OnPausedEvent/EffectiveScaleChangedEvent; and
                            // the clock readout genuinely POLLS — UIModuleTimeControl.Update():139 reads
                            // _timing.Now.DateTime every frame and repaints only when it changed (:143-149).
                            // So a latched anchor needs no event of its own to become visible.
                            break;
                        case ItemStorage storage:
                            RaiseStorageChanged(storage);
                            // Manufacturing + equip screens are PULL-model: NO StorageChanged subscription
                            // anywhere in them (decompile: only GeoPhoenixFaction.cs:308 + UIModuleInfoBar
                            // .cs:158 subscribe; UIStateEditSoldier re-reads storage only via EnterState →
                            // RefreshStorage, _refreshStorage set at :101) — so the open screen must also
                            // go through the universal repaint seam. For UIStateManufacturing the flush
                            // routes to its dedicated per-panel rebuild (OpenUiRepaint opt-out branch),
                            // which also re-snapshots the scrap-mode storage copies.
                            OpenUiRepaint.MarkDirty();
                            break;
                        default:
                            // Law 11 universal cover, guaranteed HERE: any kind without a per-kind
                            // native event still repaints the open geoscape screen through the
                            // generic seam — no unmapped type is ever silently repaint-less,
                            // regardless of what the caller does after Fire().
                            OpenUiRepaint.MarkDirty();
                            if (_loggedUnknown.Add(entity.GetType().Name))
                                Debug.Log("[Multiplayer][rail] UiEventMap: no per-kind mapping for " + entity.GetType().Name + " — universal open-screen repaint (logged once)");
                            break;
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] UiEventMap fire failed for " + entity.GetType().Name + ": " + ex.Message); }
            }
        }

        private static readonly System.Reflection.FieldInfo ResourcesChangedField =
            AccessTools.Field(typeof(Wallet), "ResourcesChanged");

        /// <summary>Raise the wallet's own native event (empty diff pack — subscribers re-read totals).
        /// Guarded by SyncApplyScope like <see cref="RaiseStorageChanged"/> — a subscriber (TFTV) reacting
        /// by spending/moving resources must not echo an intent back to the host (law 8).</summary>
        private static void RaiseResourcesChanged(Wallet wallet)
        {
            var del = ResourcesChangedField?.GetValue(wallet) as Delegate;
            if (del == null) return;
            using (SyncApplyScope.Enter())
                del.DynamicInvoke(wallet, new ResourcePack(), OperationReason.None);
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
