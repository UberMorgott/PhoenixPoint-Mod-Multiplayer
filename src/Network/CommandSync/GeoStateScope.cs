namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-3: the per-record scope tag for the 0x35 GeoStateDiff envelope. Byte values are STABLE
    // (serialized on the wire) — never renumber. Records are self-delimited, so a reader can SKIP an
    // unknown scope for forward-compat across INC-3 slices. INC-3a produces/applies ONLY Vehicle (1);
    // the rest are reserved: Site (2) / MarketPrice (3) -> INC-3b, FactionTraffic (4) / FactionState (5)
    // -> INC-3c, Checksum (255) -> per-vehicle divergence detector + targeted full re-push self-heal.
    public enum GeoStateScope : byte
    {
        Vehicle = 1,
        Site = 2,           // reserved INC-3b (full GeoSiteInstaceData blob)
        MarketPrice = 3,    // reserved INC-3b
        FactionTraffic = 4, // reserved INC-3c
        FactionState = 5,   // reserved INC-3c
        Checksum = 255      // reserved INC-3a/5 (CRC divergence detector)
    }
}
