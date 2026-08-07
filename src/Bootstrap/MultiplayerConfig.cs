using PhoenixPoint.Modding;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// In-game mod settings. The game discovers this type by reflection over the mod assembly
    /// (<c>ModAssembly.cs:38</c>) and renders every public instance field in the mod-options UI — a
    /// <see cref="KeyCode"/> becomes an arrow-picker, which is how a hotkey gets rebound here without
    /// the mod owning a keybind screen. Read via <c>MultiplayerMain.Instance.Config</c>.
    ///
    /// The game's own input actions are asset-side and a NEW rebindable action is not reachable from a
    /// mod, so a polled <see cref="KeyCode"/> is the working pattern in this workspace
    /// (<c>FreeCamera/src/FreeCameraConfig.cs:105</c>). Polling is view-state-agnostic, which is also why
    /// the ping key works during deployment.
    ///
    /// NOT PART OF CO-OP PARITY. <c>ParityManifestCollector</c> deliberately skips this mod's own
    /// settings: parity exists to catch CONTENT divergence between peers, and a keybind is a local
    /// preference. Without that skip the auto-apply would overwrite a client's chosen key with the
    /// host's, silently (KeyCode is an enum, so <c>ParityAutoApply.IsScalar</c> says yes).
    /// </summary>
    public class MultiplayerConfig : ModConfig
    {
        /// <summary>Drops a ping marker at the cursor for every peer — an object under the cursor is
        /// pinged as an object (the marker follows it), empty ground as a point. Never moves anyone's
        /// camera and never changes anyone's selection. <c>KeyCode.None</c> disables the feature.</summary>
        public KeyCode PingMarkerKey = KeyCode.V;
    }
}
