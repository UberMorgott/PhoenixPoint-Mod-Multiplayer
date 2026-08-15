using System.Collections.Generic;
using Base.Serialization.General;

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
    }
}
