using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L326 — A UNIT DESTROYED ON THE RAIL LEAVES EVERY PEER'S SCREEN. NOBODY KEEPS EDITING A SOLDIER
    /// WHO NO LONGER EXISTS, AND NOBODY WATCHES ONE STAND THERE AFTER HE WAS DISMISSED.
    ///
    /// THE REPORT (2026-08-08, three peers). A client dismissed a soldier. On the acting client he
    /// vanished and the screen changed; on the other peers he stayed put; afterwards a live character was
    /// drawn wearing his name. THE RAIL WAS NEVER AT FAULT — the host logged `HOST intent APPLIED op=fire
    /// char=U#4`, sent `structural destroy 'U#4'`, and BOTH clients logged `structural destroy 'U#4'
    /// applied`. The model converged everywhere. Everything that went wrong went wrong afterwards, in the
    /// two things a destroy is supposed to do to an OPEN screen and did not:
    ///
    ///   (1) IT ASKED FOR NO REPAINT. `GenericApplier.ApplyStructural`'s CREATE arm adds the unit to
    ///       `touched` so `UiEventMap.Fire` runs; the DESTROY arm added nothing and called nothing, and
    ///       `if (touched.Count > 0)` is therefore false on every destroy — no `[MP][uirepaint]` line
    ///       follows one anywhere in three peers' logs, while every create has one.
    ///
    ///   (2) IT LEFT THE SCREEN POINTING AT THE CORPSE. The binding is a FIELD, not a read:
    ///       `UIStateEditSoldier._currentCharacter` is snapshotted from the actor cycle (:159/:336) and
    ///       `UpdateState`:470 feeds it to UpdateSoldierEquipment → UpdatePreferredLoadout:551 →
    ///       GeoPhoenixFaction:1233 → PostmissionReplenishManager:131-133, which throws `… who is not
    ///       listed!` for a character the mirrored preferred-loadout table no longer knows. The throw is
    ///       INSIDE UpdateState, so every line after :470 — the screen's own rebuild included — is
    ///       unreachable FOREVER. 220 throws on the acting client until the game was closed, zero on the
    ///       peers. "A live character wearing a dismissed character's name" is a FROZEN screen; no
    ///       identity was ever transplanted, and a repaint on its own could not have unfrozen it.
    ///       Vanilla's own rebind tail (:443-451) cannot rescue a client either: it rotates through
    ///       `_characters`, captured at :83, which still holds the dismissed unit because the client's
    ///       kill was blocked by design.
    ///
    /// WHAT IS ASSERTED IS THE END STATE, NOT THE CALL. (b) EXECUTES the shipped decision
    /// `OpenUiRepaint.ReleaseBinding` and demands the outcome: the destroyed unit is GONE from the open
    /// screen's roster in every case, and when that screen was the one bound to it the answer is a live
    /// successor — or an explicit "nothing survives", which is the reset. (e) EXECUTES the relevance
    /// table and demands that a squad-roster change can still reach the screen at all. (a) executes the
    /// PREMISE that makes the whole law worth having: the game really does re-read that field every frame.
    /// (c)-(d) are the wiring those outcomes hang from — a pure decision nothing calls is a comment.
    ///
    /// FALSIFY: delete the `ReleaseScreenBoundTo` call from the destroy arm → (c); delete the
    /// `MarkDirty()` next to it → (d); make `ReleaseBinding` return the roster unpruned → (b); put
    /// `typeof(GeoVehicle)` back into the UIStateEditSoldier row of `IgnoredKinds` → (e).
    ///
    /// STATED LIMIT: no console harness can stand up a GeoscapeView, so "the screen actually switched" is
    /// asserted as the release reaching the game's OWN two doors (ToEditUnitState / ResetViewState) with
    /// the decision that drives them executed for real. The in-game gate is the other half.
    /// </summary>
    internal static class L326_ADestroyedSoldierLeavesNobodysScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var modAsm = typeof(OpenUiRepaint).Assembly;
            var applyStructural = typeof(GenericApplier).GetMethod("ApplyStructural", All);
            var release = typeof(OpenUiRepaint).GetMethod("ReleaseScreenBoundTo", All);
            var decide = typeof(OpenUiRepaint).GetMethod("ReleaseBinding", All);
            var markDirty = typeof(OpenUiRepaint).GetMethods(All)
                .FirstOrDefault(m => m.Name == "MarkDirty" && m.GetParameters().Length == 0);
            var boundField = typeof(UIStateEditSoldier).GetField("_currentCharacter", All);
            var rosterField = typeof(UIStateEditSoldier).GetField("_characters", All);
            var updateState = typeof(UIStateEditSoldier).GetMethod("UpdateState", All);
            var toEdit = typeof(GeoscapeView).GetMethod("ToEditUnitState", All);
            var reset = typeof(GeoscapeView).GetMethod("ResetViewState", All);

            if (applyStructural == null || release == null || decide == null || markDirty == null ||
                boundField == null || rosterField == null || updateState == null ||
                toEdit == null || reset == null)
            {
                yield return "L326 premise-changed: one of GenericApplier.ApplyStructural, " +
                             "OpenUiRepaint.{ReleaseScreenBoundTo,ReleaseBinding,MarkDirty()}, " +
                             "UIStateEditSoldier.{_currentCharacter,_characters,UpdateState} or " +
                             "GeoscapeView.{ToEditUnitState,ResetViewState} no longer resolves. The seam that " +
                             "gets a destroyed soldier off an open screen has moved, and this law is asserting " +
                             "things about a shape that is not there — re-read it before assuming a dismissal " +
                             "still leaves anybody's screen.";
                yield break;
            }

            // ── (a) PREMISE, EXECUTED: a stale binding really is consumed, every frame ───────────
            // This is what makes the release worth doing rather than leaving to the next repaint. If the
            // screen ever stops re-reading the field, the freeze is gone and so is half this law's reason.
            if (!Program.ReadsField(updateState, boundField))
                yield return "L326 binding-no-longer-read-per-frame: UIStateEditSoldier.UpdateState no longer " +
                             "reads _currentCharacter. That per-frame re-read IS the reported bug — it is what " +
                             "carried a destroyed GeoCharacter into UpdatePreferredLoadout:551 and threw out of " +
                             "the screen's own rebuild 220 times. If the screen stopped holding the character " +
                             "this way, find what replaced it: the release below is now aiming at the wrong field.";
            if (rosterField.FieldType != typeof(List<GeoCharacter>))
                yield return "L326 roster-is-no-longer-a-mutable-list: UIStateEditSoldier._characters is " +
                             rosterField.FieldType.Name + ", not List<GeoCharacter>. The release PRUNES that " +
                             "list in place — that is what stops a unit already dead on every peer from being " +
                             "cycled back onto the screen — and an immutable or renamed roster silently ends up " +
                             "as `roster == null`, i.e. a release that resets to the geoscape instead of " +
                             "rebinding, or prunes nothing at all.";

            // ── (b) THE OUTCOME, EXECUTED: nobody is left bound to the destroyed unit ────────────
            var a = Character(); var b = Character(); var c = Character();
            if (a == null || b == null || c == null)
            {
                yield return "L326 premise-changed: GeoCharacter can no longer be instantiated uninitialized, " +
                             "so the release decision cannot be executed here and only its wiring would be " +
                             "checked. Do not accept this law green in that state.";
                yield break;
            }

            // bound to the destroyed one, two others alive → rebind to the game's own successor
            var roster = new List<GeoCharacter> { a, b, c };
            GeoCharacter next;
            bool rebind = OpenUiRepaint.ReleaseBinding(roster, b, b, out next);
            if (!rebind || !ReferenceEquals(next, c))
                yield return "L326 bound-screen-not-rebound: releasing the destroyed unit a screen was showing " +
                             "answered rebind=" + rebind + " next=" + Name(next, a, b, c) + ", expected rebind " +
                             "onto the element that slid into the removed slot (the game's own " +
                             "IndexOf(current).IncRotate(Count), UIStateEditSoldier.cs:443-444). A screen left " +
                             "bound to a destroyed character re-reads it every frame and throws out of its own " +
                             "UpdateState — permanently, because the throw is upstream of the rebuild.";
            if (roster.Any(x => ReferenceEquals(x, b)))
                yield return "L326 corpse-still-in-the-roster: the destroyed unit is still in the open screen's " +
                             "own character list after the release. That list is what the screen cycles through, " +
                             "so the player can walk straight back onto a unit that no longer exists on any peer.";

            // last one standing → an explicit "nothing left", which the caller turns into ResetViewState
            var solo = new List<GeoCharacter> { a };
            if (!OpenUiRepaint.ReleaseBinding(solo, a, a, out next) || next != null || solo.Count != 0)
                yield return "L326 empty-roster-not-reported: releasing the LAST character did not answer " +
                             "'rebind, to nobody'. The caller reads a null successor as GeoscapeView." +
                             "ResetViewState() (:413); anything else either leaves the player on a screen with " +
                             "no subject or hands ToEditUnitState a character to bind that is not there.";

            // somebody else's screen: no transition, but the corpse still leaves the list
            var other = new List<GeoCharacter> { a, b };
            if (OpenUiRepaint.ReleaseBinding(other, b, a, out next) || next != null ||
                other.Any(x => ReferenceEquals(x, b)))
                yield return "L326 unbound-screen-transitioned-or-kept-the-corpse: a screen showing SOMEBODY " +
                             "ELSE must not be transitioned out from under the player, and must still lose the " +
                             "destroyed unit from its roster. Getting this wrong either yanks a peer off the " +
                             "soldier they were editing for an unrelated dismissal, or leaves the dead unit " +
                             "one arrow-press away.";

            // a screen that keeps no roster at all must still be released, not crash the apply loop
            if (!OpenUiRepaint.ReleaseBinding(null, a, a, out next) || next != null)
                yield return "L326 rosterless-screen-not-released: a bound screen with no character list " +
                             "answered 'stay put'. The apply arm runs inside the structural try/catch, so the " +
                             "cost of getting this wrong is a swallowed destroy — the silent class this repo " +
                             "keeps paying for — not a visible error.";

            // ── (c) the decision is actually wired to the destroy, and to the game's own doors ───
            var applyCallees = Program.Callees(applyStructural, modAsm).ToList();
            if (!applyCallees.Any(m => m.MetadataToken == release.MetadataToken))
                yield return "L326 destroy-never-releases: GenericApplier.ApplyStructural no longer calls " +
                             "OpenUiRepaint.ReleaseScreenBoundTo. GeoLevelController.DestroyTacUnit is a " +
                             "one-line dictionary erase (:1560-1563) that raises no event and notifies no view, " +
                             "so without this call NOTHING tells an open screen its subject is gone — which is " +
                             "exactly the state the 2026-08-08 session was left in.";
            var releaseCallees = Program.Callees(release, typeof(GeoscapeView).Assembly).ToList();
            if (!releaseCallees.Any(m => m.MetadataToken == toEdit.MetadataToken) ||
                !releaseCallees.Any(m => m.MetadataToken == reset.MetadataToken))
                yield return "L326 release-uses-no-native-door: ReleaseScreenBoundTo no longer reaches BOTH " +
                             "GeoscapeView.ToEditUnitState (:506) and GeoscapeView.ResetViewState (:413). Those " +
                             "two ARE the transition — they are what the game's own dismissal uses (:443-451). " +
                             "Deciding a successor and then not switching to it leaves the field pointing at a " +
                             "destroyed character with the decision logged as if it had been acted on.";

            // ── (d) …and the destroy asks for the repaint every other structural arm asks for ────
            if (!applyCallees.Any(m => m.MetadataToken == markDirty.MetadataToken))
                yield return "L326 destroy-asks-for-no-repaint: ApplyStructural no longer reaches " +
                             "OpenUiRepaint.MarkDirty(). Every other structural arm marks unconditionally " +
                             "(GenericApplier :517 :536 :624 :652 :717); the destroy arm not doing so is why a " +
                             "dismissed soldier stayed painted on the peers that were merely watching, with no " +
                             "[MP][uirepaint] line after any `structural destroy … applied` in three peers' logs.";

            // ── (e) EXECUTED: the roster's own kind can still reach the screen it belongs to ─────
            HashSet<System.Type> ignored;
            if (UiNativeRepaint.IgnoredKinds.TryGetValue(typeof(UIStateEditSoldier), out ignored) &&
                ignored.Contains(typeof(GeoVehicle)))
                yield return "L326 the-roster-screen-ignores-the-roster: UIStateEditSoldier declares GeoVehicle " +
                             "irrelevant to itself. A soldier's membership of an aircraft rides INSIDE " +
                             "GeoVehicle.SerializationData, so a squad-roster change IS a GeoVehicle delta — " +
                             "declaring it irrelevant makes OpenUiRepaint.MarkDirty(Type,GeoLevelController) " +
                             "(:327-341) return without marking, and the only trace is one " +
                             "`SKIP GeoVehicle on UIStateEditSoldier` line. Measured: a dismissal on one peer " +
                             "left the soldier standing on another peer's open screen.";
        }

        private static GeoCharacter Character()
        {
            try { return (GeoCharacter)FormatterServices.GetUninitializedObject(typeof(GeoCharacter)); }
            catch { return null; }
        }

        private static string Name(GeoCharacter x, GeoCharacter a, GeoCharacter b, GeoCharacter c)
        {
            if (x == null) return "null";
            if (ReferenceEquals(x, a)) return "a";
            if (ReferenceEquals(x, b)) return "b(the destroyed one)";
            return ReferenceEquals(x, c) ? "c" : "an unknown character";
        }
    }
}
