using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.DataObjects;

namespace RailCheck
{
    /// <summary>
    /// L149 — BOTH HELMET TOGGLES ARE THE SAME ARGUMENT TO THE SAME FUNNEL, AND A MIRRORED IDENTITY CHANGE
    /// REPAINTS UNDERNEATH WHICHEVER ONE THIS PEER IS HOLDING.
    ///
    /// THE ASK was "there are TWO menus — the character customization screen with a show/hide helmet tick,
    /// and before that, on the screen with the inventory and skills, another show-helmet toggle, an icon,
    /// probably from TFTV. Everything must work reactively." The literal reading — make the helmet state
    /// cross the wire — IS WRONG, and this law exists mostly to stop the next reader implementing it.
    ///
    /// THE TOGGLE ITSELF IS NOT REPLICATED STATE — but WHERE A MOD CHOOSES TO PERSIST ITS PREFERENCE CAN BE,
    /// and the original wording of this law ("neither toggle is replicated state and neither should be")
    /// papered over that distinction. Corrected 2026-08-08; see the STATED LIMIT at the bottom, which is
    /// now a fixed defect rather than an accepted one. The vanilla tick writes NOTHING to the
    /// character: `UIStateSoldierCustomization.EnterState`:24 binds a Unity `Toggle`, `:51 UpdateHelmetShown`
    /// just re-runs `RefreshUnitDisplay`, and `:44` passes `!isOn` as the `showHelmet` ARGUMENT to
    /// `UIModuleActorCycle.DisplaySoldier`, which filters `Head`/`HeadAttachment` items out of the addon list
    /// for that one call (:627-629, `IsHelmetOrAttachment`:903). Nothing is stored, nothing is serialized —
    /// `CharacterIdentity` has no helmet field at all, which arm (a) proves rather than asserts. TFTV's icon
    /// is the same argument reached from the other side: a `PhoenixGeneralButton` cloned onto the edit screen
    /// (TFTVUI/Personnel/Loadouts.cs:156-238) driving `ShowWithoutHelmet.HelmetsOff`, applied by a PREFIX on
    /// that same four-argument `DisplaySoldier` overload (ShowWithoutHelmet.cs:319-321) which forces
    /// `showHelmet = false`. Which helmet a player is looking at is that PLAYER'S OWN VIEW; replicating it
    /// would mean one peer reaching across and changing what another peer sees, which is not reactivity, it
    /// is a bug with a nicer name.
    ///
    /// SO THE PROPERTY THAT MUST HOLD is the one that was actually broken: a MIRRORED IDENTITY change must
    /// repaint through that funnel no matter which toggle is set. It did not — `DisplaySoldier`'s early-out
    /// (:636-644) skips the rebuild whenever the addon set is unchanged, so a recolour never landed while a
    /// helmet removal, which DOES change the addon set, did. That is the whole of "later the helmet started
    /// working", and L148 fixes it by emptying `AddonsCharacterBuilder.Addons` so the funnel rebuilds.
    ///
    /// TFTV IS COVERED BY NAMING NOTHING. Our reseed clears a vanilla list and lets the GAME call
    /// `DisplaySoldier` — so TFTV's prefix runs on that call exactly as it runs on a native one, whatever
    /// order the mods loaded in. There is no patch of ours on a TFTV type, therefore no `TftvLateBinder` arm
    /// to bind late (the failure mode of `harmony-crossmod-patch-late-bind`) and nothing to degrade when TFTV
    /// is absent: with no TFTV the same vanilla call simply has no prefix. Arm (c) is what keeps that true —
    /// the moment our identity path names a TFTV type, it acquires a load-order dependency and this law goes
    /// red before the field does.
    ///
    /// ARMS: (a) the helmet is not identity state (executed against the real `CharacterIdentity`);
    /// (b) the funnel is the exact four-argument overload both toggles and TFTV's prefix target;
    /// (c) our identity path names no TFTV type and needs no late bind.
    /// FALSIFY: give `CharacterIdentity` a helmet-ish field → (a); change the overload → (b); reference any
    /// TFTV type from the identity reseed → (c).
    ///
    /// THE STATED LIMIT WAS A DEFECT, AND IT IS FIXED (2026-08-08). A mod may persist its preference into the
    /// SHARED campaign store — TFTV does, `GeoLevelController.EventSystem.SetVariable("TFTV_HelmetsOff", …)`
    /// (TFTVUI/Personnel/ShowWithoutHelmet.cs:31/:107) — and `GeoscapeEventSystem._customVariables` is
    /// rail-covered host-authoritative state (docs/rail-baseline.txt:473). This law used to call that
    /// harmless. It was not: the direction that worked was host→client only. A CLIENT's write landed in its
    /// own dictionary, crossed to nobody, and was reverted by the next mirrored diff — which writes the dict
    /// RAW, so the revert produced no exception and no log line. The user's report ("the helmet toggle does
    /// nothing on the other peers") was that hole, not a repaint bug.
    ///
    /// The fix is a gap-CLASS seam and names no mod and no variable: `EventVariableCapture` (src/Rail/
    /// EventSync.cs) blocks `GeoscapeEventSystem.SetVariable(string,int)` on a client and ships (name, value)
    /// as 0xB4 op 2; the host replays the game's OWN setter, and the existing `CustomVariables` coverage
    /// carries the result to every peer. So the CONSEQUENCE the old limit described stands and is now
    /// deliberate: a preference stored in campaign state is GLOBAL, and one peer flipping it changes what
    /// every peer sees. That is the storage choice's own semantics, made to work in both directions instead
    /// of one. A mod wanting a per-player preference must keep it out of the campaign store.
    /// </summary>
    internal static class L149_HelmetTogglesShareOneReactiveFunnel
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var reseed = typeof(UiEventMap).GetMethod("ReseedIdentityDisplay", All);
            if (reseed == null)
            {
                yield return "L149 premise-changed: UiEventMap.ReseedIdentityDisplay no longer resolves. This " +
                             "law is about the funnel that reseed un-blocks; with the reseed gone there is no " +
                             "shared reactive path for either helmet toggle to sit on.";
                yield break;
            }

            // ── (a) the helmet is a VIEW ARGUMENT, never identity state ──────────────────────────
            // Executed against the real type: if a helmet field ever appears on CharacterIdentity it becomes
            // rail-covered persisted state, and this law's entire reasoning ("nothing to replicate") inverts.
            var helmetish = typeof(CharacterIdentity)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.Name.IndexOf("helmet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            f.Name.IndexOf("headgear", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(f => f.Name)
                .ToArray();
            if (helmetish.Length > 0)
                yield return "L149 helmet-became-identity-state: CharacterIdentity now carries " +
                             string.Join(", ", helmetish) + ". The whole argument of this law is that the helmet " +
                             "is a per-view ARGUMENT to DisplaySoldier and belongs to the player looking at the " +
                             "screen, not to the soldier — which is why nothing replicates it and why nobody " +
                             "should make it. If the game made it persisted state, it is now a rail leaf like any " +
                             "other, it needs an intent seam under L138, and this reasoning has to be rewritten " +
                             "rather than patched around.";

            // ── (b) the funnel is the exact overload both toggles reach ──────────────────────────
            var funnel = typeof(UIModuleActorCycle).GetMethods(All).FirstOrDefault(m =>
                m.Name == "DisplaySoldier" && m.GetParameters().Length == 4 &&
                m.GetParameters()[0].ParameterType == typeof(GeoCharacter) &&
                m.GetParameters().Skip(1).All(p => p.ParameterType == typeof(bool)));
            var displayData = typeof(UIModuleActorCycle).GetMethods(All).FirstOrDefault(m =>
                m.Name == "DisplaySoldier" && m.GetParameters().Length == 4 &&
                m.GetParameters()[0].ParameterType == typeof(UnitDisplayData));
            if (funnel == null || displayData == null)
                yield return "L149 funnel-signature-changed: UIModuleActorCycle.DisplaySoldier no longer has both " +
                             "the (GeoCharacter,bool,bool,bool) and (UnitDisplayData,bool,bool,bool) forms. The " +
                             "first is the exact signature TFTV's helmet prefix targets " +
                             "(ShowWithoutHelmet.cs:319-321) and the second is where the addon-set early-out lives, " +
                             "so a change here breaks the vanilla toggle, TFTV's toggle and our repaint at once — " +
                             "and TFTV's patch would fail to bind rather than misbehave, i.e. silently.";
            else if (funnel.GetParameters()[3].Name != "showHelmet")
                yield return "L149 helmet-argument-renamed: the fourth parameter of DisplaySoldier is now '" +
                             funnel.GetParameters()[3].Name + "', not 'showHelmet'. Harmony binds a prefix's " +
                             "parameters BY NAME, so TFTV's helmet override stops receiving it — the toggle goes " +
                             "dead with no exception and no log line.";

            // ── (c) our identity path must name no foreign mod ───────────────────────────────────
            var foreign = Program.Callees(reseed, typeof(UiEventMap).Assembly)
                .Concat(Program.Callees(reseed, typeof(GeoCharacter).Assembly))
                .Select(c => c.DeclaringType)
                .Where(t => t != null && (t.Assembly.GetName().Name.IndexOf("TFTV", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          (t.Namespace ?? "").IndexOf("TFTV", StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(t => t.FullName)
                .Distinct()
                .ToArray();
            if (foreign.Length > 0)
                yield return "L149 identity-repaint-names-a-foreign-mod: ReseedIdentityDisplay now reaches " +
                             string.Join(", ", foreign) + ". Coverage of TFTV's helmet toggle is supposed to be " +
                             "free — we clear a vanilla list and let the GAME call DisplaySoldier, so TFTV's own " +
                             "prefix decorates that call whatever order the mods loaded in. Naming a TFTV type " +
                             "buys nothing and costs a load-order dependency: a patch on another mod's types " +
                             "silently never binds if it loads after us (memory harmony-crossmod-patch-late-bind, " +
                             "14 dead guards found 2026-07-12), and it also has to degrade when TFTV is absent. " +
                             "If a TFTV type is genuinely needed, it goes through TftvLateBinder — never here.";
        }
    }
}
