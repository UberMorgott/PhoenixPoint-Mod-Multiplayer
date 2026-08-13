using System;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    /// <summary>
    /// SOMEBODY IS PREPPING A DROP, AND THIS IS THE DOOR IN.
    ///
    /// A PRESENTATION SEAM (P4c, L158). It READS <see cref="DeployPrep.State"/> — the mirrored <c>"M#prep"</c>
    /// mod root — and writes nothing but its own <c>SetActive</c> and one gesture, <see cref="DeployPrep.Join"/>,
    /// which is a purely local <c>GeoscapeView.LaunchMission</c>. No rail write, no <c>[SerializeMember]</c>
    /// leaf, and NOTHING reads this file back.
    ///
    /// IT IS NOT A QUORUM (P13, L84/L114/L424). A peer that never presses it blocks nothing: the launch is
    /// still first-to-press, and <c>DeployCountdown</c> still drops the battle on the host's own clock. The
    /// button is an OFFER, and <see cref="DeployPrep.ShowsButton"/> has no term for what any other peer did.
    ///
    /// WHERE IT LIVES, and why not a native <c>PhoenixGeneralButton</c> clone. Same answer
    /// <see cref="CountdownPanel"/> already argued and shipped for this exact screen position: every native
    /// always-on-top geoscape surface is a QUEUED view state going through <c>GeoscapeViewSwitchQuery</c>,
    /// and each of them TAKES the screen and pauses the game — which for a persistent, ignorable offer is the
    /// window-history defect, not a feature. So it hangs on the mod's own overlay canvas
    /// (<c>MultiplayerUI.EnsureBarCanvas</c>, sortingOrder 5000) and is SKINNED with the native theme
    /// through <see cref="LobbyTheme"/> / <see cref="UiToolkit"/> / <see cref="CountdownPanel.SkinButton"/>,
    /// which is the same amber the rest of the game's UI lights up with. It sits BELOW the countdown plate's
    /// footprint so the two can be on screen together during the five-second drop.
    ///
    /// ONLY ON THE BARE GEOSCAPE. The gate is the game's own idle test — <c>GeoscapeView.CurrentViewState is
    /// UIStateNothingSelected</c>, which the game itself uses at <c>GeoscapeView.cs:1580</c>. Every popup,
    /// modal, submenu and site/vehicle selection is a DIFFERENT view state, so all of them hide the button
    /// with no per-screen list to maintain. <c>UIStateVehicleSelected</c> is deliberately NOT idle: an
    /// aircraft is selected, the site context menu is up, and a second offer over it is clutter.
    ///
    /// IT OUTLIVES THE SCREEN IT OPENS. The offer used to be withdrawn by <c>ExitState</c>, so pressing Back
    /// — "we are not ready to drop yet, let us do other things first" — took the door away from everybody,
    /// and the peer who pressed it was left with no gesture at all. What it reads is
    /// <see cref="DeployPrep.LiveSiteRef"/>, which is the announcement AND the game's own "is there still
    /// anything to enrol here" question, so the button stands for as long as the aircraft does and takes
    /// itself down when the drop launches, is cancelled, or its last carrier flies off.
    ///
    /// REACTIVITY IS BY CONSTRUCTION (postulate 1). <see cref="Sync"/> re-reads the mirrored root every frame
    /// from <c>MultiplayerUI.Update</c> — the idiom <see cref="PlayerPanel"/> and <see cref="CountdownPanel"/>
    /// are both already in-game confirmed on — so an announcement lands on an ALREADY-OPEN geoscape within
    /// one frame of the rail batch, and a withdrawal takes the button away the same way. There is no edge to
    /// raise and none to forget, and the player never leaves or re-enters anything.
    /// </summary>
    internal sealed class PrepJoinButton : MonoBehaviour
    {
        /// <summary>English literal, matching every other user-facing string this mod ships
        /// (<c>"CANCEL"</c>, <c>"MISSION STARTS IN …"</c>) — there is no localization table in the mod to
        /// key into.</summary>
        private const string Label = "DEPLOYMENT PREP";

        private static int Width => LobbyTheme.Scale(260);
        private static int Height => LobbyTheme.Scale(34);
        /// <summary>Clear of the countdown plate, which hangs from the top edge at -60 and is
        /// <c>Pad*3 + TitleH + ButtonH</c> = 104 tall. Both may be up at once for five seconds.</summary>
        private static int Top => LobbyTheme.Scale(180);

        private Button _button;
        private bool _loggedFailure;

        internal void Attach(Transform barCanvas)
        {
            if (barCanvas == null) return;
            _button = UiToolkit.CreateButton(barCanvas.gameObject, "MultiplayerPrepJoin", Label,
                                             new Vector2(0f, -Top), new Vector2(Width, Height),
                                             new Vector2(0.5f, 1f), DeployPrep.Join);
            if (_button == null) return;
            CountdownPanel.SkinButton(_button);   // the raycaster the canvas needs is added by CountdownPanel.Attach
            _button.gameObject.SetActive(false);
        }

        /// <summary>The containment point L158 names: one try/catch, because this runs from
        /// <c>MultiplayerUI.Update</c> and a throw here would unwind into the game's own loop. Failure hides
        /// the button and logs once — what is lost is this peer's shortcut into a deployment nobody was
        /// waiting on him for, never anyone's battle.</summary>
        internal void Sync()
        {
            try { SyncCore(); }
            catch (Exception e)
            {
                if (_button != null) _button.gameObject.SetActive(false);
                if (_loggedFailure) return;
                _loggedFailure = true;
                Debug.LogError("[Multiplayer] deployment-prep join button FAILED and is hidden for the rest of " +
                               "this run. Every peer's own deployment screen and every launch are unaffected: " + e);
            }
        }

        private void SyncCore()
        {
            if (_button == null) return;
            var engine = NetworkEngine.Instance;
            bool show = DeployPrep.ShowsButton(
                engine != null && engine.IsActiveSession,
                DeployPrep.LiveSiteRef(),
                GenericApplier.GeoLevel()?.View?.CurrentViewState is UIStateNothingSelected);
            if (_button.gameObject.activeSelf != show) _button.gameObject.SetActive(show);
        }
    }
}
