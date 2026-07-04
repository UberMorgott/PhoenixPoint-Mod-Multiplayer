namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The kind of payload carried by a <see cref="SyncProtocol"/> envelope. One inbound chokepoint
    /// (<see cref="SurfaceRouter"/>) dispatches on this: action request (client→host), action apply
    /// (host→all), or a per-channel state snapshot / delta (host→all). Wire value is a single byte.
    /// </summary>
    public enum SyncKind : byte
    {
        ActionRequest = 0,
        ActionApply = 1,
        StateSnapshot = 2,
        StateDelta = 3
    }
}
