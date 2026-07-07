using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Pure resolver for a client-keyed CONTAINMENT captive. Captured units have no per-unit id
    /// (<c>GeoUnitDescriptor</c> carries no GeoUnitId), so an intent keys one by its ORDINAL in the
    /// mirrored <c>_capturedUnits</c> list (the #10 full-set emit preserves host order — the naked-pool
    /// precedent) plus a TemplateDef-guid FINGERPRINT for drift detection. Resolution:
    ///   1. ordinal in range AND fingerprint matches → that unit (the common, no-drift case);
    ///   2. ordinal drifted (host capacity trim / concurrent kill reordered the list since the last
    ///      mirror) → first unit with the same fingerprint (same template ⇒ same volume/yield — for a
    ///      kill/harvest it is semantically the unit the client saw);
    ///   3. no fingerprint match → -1: the captive is GONE on the authority → the caller rejects the
    ///      intent as a logged no-op (never kill a different-template unit on a stale ordinal).
    /// </summary>
    public static class ContainmentTarget
    {
        /// <summary>Resolve (ordinal, templateGuid) against a live list of <paramref name="count"/> units
        /// whose fingerprint is read via <paramref name="guidAt"/>. Returns the resolved index, or -1 =
        /// unknown captive (reject as logged no-op). A null/empty fingerprint never matches (fail closed).</summary>
        public static int Resolve(int count, Func<int, string> guidAt, int ordinal, string templateGuid)
        {
            if (count <= 0 || guidAt == null || string.IsNullOrEmpty(templateGuid)) return -1;
            if (ordinal >= 0 && ordinal < count && templateGuid == guidAt(ordinal)) return ordinal;
            for (int i = 0; i < count; i++)
                if (templateGuid == guidAt(i)) return i;
            return -1;
        }
    }
}
