using System.Collections.Generic;
using Multipleer.Network.Sync.State;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// One registry for every synced surface. Replaces the split between
    /// <see cref="SyncedActionRegistry"/> and <see cref="StateChannelRegistry"/>: each surface is
    /// keyed by its stable byte <c>surfaceId</c> and declares the <see cref="SyncKind"/>s it accepts,
    /// its action reader (for action surfaces) and/or its state channel (Phase 2), plus the geoscape
    /// screen its apply should refresh. Phase 1 registers only action surfaces.
    ///
    /// IMPORTANT — overlapping id spaces: action surface ids (<see cref="SurfaceIds"/> action block)
    /// and state-channel surface ids (the channel block) live in SEPARATE numbering spaces and
    /// overlap numerically (e.g. <c>StartResearch=1</c> shares byte 1 with <c>InventoryChannel=1</c>).
    /// They are disambiguated by the surface's KIND CLASS — an action surface accepts only
    /// ActionRequest/ActionApply, a channel surface only StateSnapshot/StateDelta. The registry
    /// therefore keeps a SEPARATE map per kind class so a channel never clobbers an action of the
    /// same numeric id. Resolve with <see cref="Get(byte, SyncKind)"/> when the kind is known;
    /// the bare <see cref="Get(byte)"/> resolves the action surface (the only kind routed in Phase 1).
    /// </summary>
    public sealed class SurfaceRegistry
    {
        /// <summary>A single registered surface.</summary>
        public sealed class SurfaceEntry
        {
            public byte SurfaceId { get; }
            private readonly HashSet<SyncKind> _kinds;
            public ActionReader Reader { get; }
            // Typed object (not IStateChannel) so SurfaceRegistry stays linkable into Multipleer.Tests:
            // IStateChannel.AttachHost references SyncEngine→NetworkEngine (Unity), which the test
            // assembly cannot compile. Phase 1 never reads Channel; Phase 2 casts back at the channel
            // call site (in SurfaceRouter/SyncEngine, which DO hold the transport).
            public object Channel { get; }
            public GeoUiRefresh.Screen? Screen { get; }

            public SurfaceEntry(byte surfaceId, IEnumerable<SyncKind> kinds, ActionReader reader,
                object channel, GeoUiRefresh.Screen? screen)
            {
                SurfaceId = surfaceId;
                _kinds = new HashSet<SyncKind>(kinds);
                Reader = reader;
                Channel = channel;
                Screen = screen;
            }

            public bool Accepts(SyncKind kind) => _kinds.Contains(kind);
        }

        private static readonly SyncKind[] ActionKinds = { SyncKind.ActionRequest, SyncKind.ActionApply };
        private static readonly SyncKind[] StateKinds = { SyncKind.StateSnapshot, SyncKind.StateDelta };

        // Separate maps per kind class: action ids and channel ids overlap numerically (by design),
        // so a single bare-id dictionary would clobber. Resolution picks the map by SyncKind class.
        private readonly Dictionary<byte, SurfaceEntry> _actions = new Dictionary<byte, SurfaceEntry>();
        private readonly Dictionary<byte, SurfaceEntry> _channels = new Dictionary<byte, SurfaceEntry>();

        /// <summary>Register an action surface: accepts ActionRequest + ActionApply.</summary>
        public void RegisterAction(byte surfaceId, ActionReader reader, GeoUiRefresh.Screen? screen)
            => _actions[surfaceId] = new SurfaceEntry(surfaceId, ActionKinds, reader, null, screen);

        /// <summary>Register a state-channel surface: accepts StateSnapshot + StateDelta (Phase 2).
        /// Lives in a separate id space from action surfaces, so the same numeric id may already hold
        /// an action surface — they do not collide. <paramref name="channel"/> is typed object (it is
        /// an <c>IStateChannel</c>) to keep this type test-linkable; the channel call site casts back.</summary>
        public void RegisterChannel(byte surfaceId, object channel, GeoUiRefresh.Screen? screen)
            => _channels[surfaceId] = new SurfaceEntry(surfaceId, StateKinds, null, channel, screen);

        private static bool IsStateKind(SyncKind kind)
            => kind == SyncKind.StateSnapshot || kind == SyncKind.StateDelta;

        /// <summary>True if either an action or a channel surface is registered at this id.</summary>
        public bool IsRegistered(byte surfaceId)
            => _actions.ContainsKey(surfaceId) || _channels.ContainsKey(surfaceId);

        /// <summary>The action surface for an id, or null. (Bare lookup resolves the action space —
        /// the only kind routed in Phase 1. Use <see cref="Get(byte, SyncKind)"/> when the kind is
        /// known so a channel surface on the same id resolves correctly.)</summary>
        public SurfaceEntry Get(byte surfaceId)
            => _actions.TryGetValue(surfaceId, out var e) ? e : null;

        /// <summary>The surface for an id resolved by kind class, or null. State kinds resolve the
        /// channel space, action kinds the action space — so overlapping ids never clash.</summary>
        public SurfaceEntry Get(byte surfaceId, SyncKind kind)
        {
            var map = IsStateKind(kind) ? _channels : _actions;
            return map.TryGetValue(surfaceId, out var e) ? e : null;
        }
    }
}
