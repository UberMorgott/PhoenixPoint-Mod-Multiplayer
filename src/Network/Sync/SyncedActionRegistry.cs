using System.Collections.Generic;
using System.IO;

namespace Multiplayer.Network.Sync
{
    /// <summary>Maps a stable action id to the reader that reconstructs it from payload bytes.</summary>
    public static class SyncedActionRegistry
    {
        private static readonly Dictionary<ushort, ActionReader> _readers =
            new Dictionary<ushort, ActionReader>();

        // The dictionary itself guards concurrent writers (xunit parallel test classes race
        // RegisterAll vs direct Register; in-game registration is single-threaded — the lock is free).
        public static void Register(ushort id, ActionReader reader)
        {
            lock (_readers) _readers[id] = reader;
        }

        public static bool IsRegistered(ushort id)
        {
            lock (_readers) return _readers.ContainsKey(id);
        }

        /// <summary>Reconstruct an action, or null for an unknown id.</summary>
        public static ISyncedAction Read(ushort id, BinaryReader r)
        {
            ActionReader reader;
            lock (_readers) _readers.TryGetValue(id, out reader);
            return reader != null ? reader(r) : null;
        }
    }
}
