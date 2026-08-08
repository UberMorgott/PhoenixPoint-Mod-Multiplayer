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
        /// camera and never changes anyone's selection. <c>KeyCode.None</c> disables the feature.
        ///
        /// DEFAULT H, AND IT IS VERIFIED FREE, not guessed (2026-08-07). The game's bindings live in the
        /// <c>PhoenixInput</c> <see cref="Base.Input.InputMapDef"/> — an asset, not code, which is why no
        /// grep of the decompile answers this. Read out of
        /// <c>PhoenixPointWin64_Data/sharedassets0.assets</c> (the <c>Defs/Input/*Controls</c>
        /// <c>InputSetDef</c>s sit beside it), every single-character <c>InputKey.Name</c> in that map is:
        ///   a c d e f g i m n q r s t w x y z  and digits 1-7, 9.
        /// So the free letters are b h j k l o p u. On top of that TFTV injects its own actions into the
        /// SAME def: "v" -> DisplayPerceptionCircles (`refs/TFTV-src/TFTV/TFTVDefsInjectedOnlyOnce.cs:759`)
        /// and digits 1-9 -> SelectAircraft1..9 (:735), which is why V — the obvious co-op ping key — is
        /// NOT the default. H is free in both, is free in tactical/geoscape/deployment alike (the ban is
        /// per key, not per set), and reads as "here".</summary>
        public KeyCode PingMarkerKey = KeyCode.H;

        /// <summary>Convenience only: when the advisory ready tally reaches everybody, the HOST runs the
        /// game's own end-turn instead of somebody pressing a second button. Read live at the moment the
        /// tally lands (<c>TacticalReadySync.HostBroadcastTally</c>), so toggling it mid-battle takes effect
        /// on the very next tally — nothing caches it and no screen needs reopening.
        ///
        /// NOT A QUORUM, AND THE DIFFERENCE IS THE WHOLE POINT. End Turn stays pressable by anyone at any
        /// moment whatever the tally says (RailCheck L119 EXECUTES both tactical arbiters against a hostile
        /// tally to prove it), so a table where everyone else is AFK is finished by one player pressing End
        /// Turn exactly as today — this setting only removes the second press when the table is genuinely
        /// full. It is the HOST's copy that decides, because the host is the one that performs the turn.
        ///
        /// Off = today's behaviour, byte for byte.</summary>
        public bool AutoEndTurnWhenAllReady = true;
    }
}
