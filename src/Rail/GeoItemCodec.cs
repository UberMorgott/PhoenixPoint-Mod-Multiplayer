using System;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Structural codec (law 3) for the ONE BaseDef-keyed GeoItem dictionary the generic rail carries:
    /// <c>ItemStorage._storageItems</c> (<c>Dictionary&lt;ItemDef, GeoItem&gt;</c>). The generic rail
    /// excludes it by default (non-simple key, no stable-id value); <see cref="FieldClass.GeoItemDict"/>
    /// re-includes it, keyed by <c>ItemDef.Guid</c> — so it covers EVERY walk-reachable ItemStorage
    /// (GeoFaction.ItemStorage = the shared Phoenix inventory manufacturing/scrap/free-space read, plus
    /// GeoSite.ItemStorage) at once, not one hand-sync per storage.
    ///
    /// Faction storages AUTO-UNLOAD ammo on AddItem (<c>_isFactionStorage</c>) so the stored GeoItem's
    /// <c>Ammo</c> is null → three ints (Count / CurrentCharges / Malfunction) fully describe an entry.
    /// A NON-auto-unload storage could hold a loaded weapon whose nested ammo these three ints would drop;
    /// the host walk excludes such a storage (see <see cref="OwnerAutoUnloads"/>) rather than mirror it wrong.
    /// Client reconstruction uses the PUBLIC GeoItem ctor and the client writes its own <c>_storageItems</c>
    /// dict DIRECTLY (never AddItem/RemoveItem — those fire StorageChanged/ItemAdded + ammo unload).
    /// </summary>
    internal static class GeoItemCodec
    {
        private static readonly FieldInfo IsFactionStorageFi = AccessTools.Field(typeof(ItemStorage), "_isFactionStorage");

        /// <summary>The dictionary shape this codec owns: a BaseDef-derived key mapping to a GeoItem.</summary>
        internal static bool Handles(Type keyType, Type valType) =>
            typeof(BaseDef).IsAssignableFrom(keyType) && valType == typeof(GeoItem);

        /// <summary>Wire subkey of a dict entry = the def GUID (law 2 stable key; one entry per def).</summary>
        internal static string SubKey(object key) => ((BaseDef)key).Guid;

        /// <summary>True when the owning ItemStorage auto-unloads ammo (stored Ammo == null) so 3 ints are
        /// lossless. Unknown owner → assume safe (every shipped ItemStorage defaults to faction storage).</summary>
        internal static bool OwnerAutoUnloads(object owner)
        {
            if (IsFactionStorageFi == null || !(owner is ItemStorage)) return true;
            try { return (bool)IsFactionStorageFi.GetValue(owner); }
            catch { return true; }
        }

        internal static byte[] Encode(object geoItemObj)
        {
            var cid = ((GeoItem)geoItemObj).CommonItemData;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(cid.Count);           // -> _count
                w.Write(cid.CurrentCharges);  // -> _charges (Ammo==null on faction storage)
                w.Write(cid.Malfunction);     // -> _malfunction
                return ms.ToArray();
            }
        }

        /// <summary>Resolve the def keying a dict entry (its wire subkey), or null when unknown on this client.</summary>
        internal static ItemDef ResolveDef(string defGuid) =>
            GameUtl.GameComponent<DefRepository>()?.GetDef(defGuid) as ItemDef;

        /// <summary>Reconstruct the GeoItem value from its 3 ints via the PUBLIC ctor (Ammo left null — faction
        /// storage; SetOwnerItem may re-create an empty AmmoManager for ammo-compatible defs, harmless on a
        /// projector client). The client writes this straight into its _storageItems dict, never via AddItem.</summary>
        internal static GeoItem Decode(byte[] bytes, ItemDef def)
        {
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
            {
                int count = r.ReadInt32();
                int charges = r.ReadInt32();
                int malfunction = r.ReadInt32();
                return new GeoItem(def, count, charges, null, malfunction);
            }
        }
    }
}
