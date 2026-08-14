using System;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using UnityEngine;
using Multiplayer.Network.MessageLayer;

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

        // ─── ORDINAL VALUE-ELEMENT RECORD (the reusable half) ──────────────────────────────────────
        //
        // The class of gap this closes: state hidden in a NESTED collection whose DECLARED element type
        // the blob codec cannot rebuild — an interface (no Activator, and EncodeValue refuses a runtime
        // subtype), so the whole field classified Excluded and everything under it vanished in SILENCE.
        // When such an element's ENTIRE state is a def address plus a few ints, the collection can ride
        // ORDINALLY: one record per element, in list order. Same record as a storage-dict entry above,
        // plus the def GUID — a dict entry reads its def off the KEY, a list element must carry it.
        //
        // First occupant: AmmoManager.LoadedMagazines (List<ICommonItem>). Its exclusion is what made
        // AmmoManager contentless (RailMeta.HasBlobContent), so CommonItemData.Ammo shipped as TagNull
        // and every loaded weapon arrived with Ammo == null — where CurrentCharges silently falls back
        // to _charges (CommonItemData.cs:33), i.e. a storage-fresh gun reads FULL on the client and
        // empty on the host. Next occupant: return true for its element type here — nothing else.

        /// <summary>Declared element types that ride as an ordinal VALUE RECORD instead of a graph blob.
        /// The contract: instances are fully described by (def, count, charges, malfunction), and are
        /// rebuilt through the PUBLIC ctor — so blob husk-gating does not apply to them.</summary>
        internal static bool IsValueElementType(Type declared) => declared == typeof(ICommonItem);

        internal struct ItemRec
        {
            internal string Guid;
            internal int Count, Charges, Malfunction;
        }

        internal static ItemRec RecOf(object item)
        {
            var it = (ICommonItem)item;
            var cid = it.CommonItemData;
            // A record carries no nested collection. Ammo INSIDE ammo is not a shape the game ships
            // (a magazine def has no CompatibleAmmunition, so SetOwnerItem never builds it an
            // AmmoManager) — if it ever appears, say so instead of dropping it quietly.
            if (cid.Ammo != null && cid.Ammo.LoadedMagazines != null && cid.Ammo.LoadedMagazines.Count > 0)
                MpLog.LogError("[Multiplayer][rail] value-record " + it.ItemDef?.name + " carries " +
                               cid.Ammo.LoadedMagazines.Count + " NESTED magazines — the record cannot ship them");
            return new ItemRec
            {
                Guid = it.ItemDef?.Guid,
                Count = cid.Count,
                Charges = cid.CurrentCharges, // Ammo == null here → the item's own _charges
                Malfunction = cid.Malfunction,
            };
        }

        internal static void WriteRec(BinaryWriter w, ItemRec rec)
        {
            w.Write(rec.Guid ?? "");
            w.Write(rec.Count);
            w.Write(rec.Charges);
            w.Write(rec.Malfunction);
        }

        internal static ItemRec ReadRec(BinaryReader r) => new ItemRec
        {
            Guid = WireString.ReadKey(r),
            Count = r.ReadInt32(),
            Charges = r.ReadInt32(),
            Malfunction = r.ReadInt32(),
        };

        /// <summary>Record → live element via the PUBLIC ctor (Ammo left null — a magazine has none).
        /// Null = unresolvable def, which mod parity (law 10) says cannot happen: LOUD, never silent.
        /// The caller turns it into the Unresolved sentinel so the applier drops the hole.</summary>
        internal static object FromRec(ItemRec rec)
        {
            var def = ResolveDef(rec.Guid);
            if (def == null)
            {
                MpLog.LogError("[Multiplayer][rail] value-record: unknown ItemDef guid '" + rec.Guid +
                               "' — element DROPPED (mod parity broken?)");
                return null;
            }
            return new GeoItem(def, rec.Count, rec.Charges, null, rec.Malfunction);
        }
    }
}
