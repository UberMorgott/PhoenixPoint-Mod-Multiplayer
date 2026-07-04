using System.Collections.Generic;
using System.IO;

namespace Multiplayer.Network.Sync
{
    /// <summary>Maps a stable action id to the reader that reconstructs it from payload bytes.</summary>
    public static class SyncedActionRegistry
    {
        private static readonly Dictionary<ushort, ActionReader> _readers =
            new Dictionary<ushort, ActionReader>();

        public static void Register(ushort id, ActionReader reader) => _readers[id] = reader;

        public static bool IsRegistered(ushort id) => _readers.ContainsKey(id);

        /// <summary>Reconstruct an action, or null for an unknown id.</summary>
        public static ISyncedAction Read(ushort id, BinaryReader r)
            => _readers.TryGetValue(id, out var reader) ? reader(r) : null;
    }
}
