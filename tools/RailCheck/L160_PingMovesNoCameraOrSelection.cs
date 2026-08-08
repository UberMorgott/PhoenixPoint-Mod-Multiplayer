using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.UI;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.View;

namespace RailCheck
{
    /// <summary>
    /// L160 — AN ARRIVING PING POINTS. IT DOES NOT DRIVE.
    ///
    /// This is the L97 extension the ping-marker spec asks for, stated over the one seam L158's seam-set
    /// arms cannot reach. L158 says a presentation seam does not BLOCK the game and does not WRITE the
    /// model; it says nothing about a seam that only ever calls presentation methods and still hijacks the
    /// watcher — because moving a camera, entering a view state and selecting an actor are all things the
    /// game's own UI does legitimately, so none of them writes a <c>[SerializeMember]</c> leaf and none of
    /// them is a suppressing prefix. Every arm below would be green under L158 while the feature was
    /// unusable.
    ///
    /// THE RULE (owner's decision 2026-08-04, reaffirmed for pings 2026-08-07): a watcher NEVER loses its
    /// own camera, its own screen or its own selection to a remote peer. A ping is the sharpest test of it
    /// yet, because "show me where he means" is exactly the feature a well-meaning fix would implement by
    /// flying the camera there — and it would be a defect, not a convenience, on a peer mid-shot.
    ///
    /// SCOPED TO THE ARRIVAL CLOSURE, NOT THE WHOLE CLASS (narrowed 2026-08-08, and the narrowing is the
    /// law getting SHARPER, not weaker). The rule this law states is about an ARRIVING packet — read its
    /// title. The scan used to walk every method of <see cref="PingMarkers"/>, which stated something else
    /// and something wrong: that nothing in the file may ever move a camera, including code the WATCHER
    /// HIMSELF drives. The off-screen arrow now takes a click and centres the clicker's own camera on the
    /// pinged spot (<c>PingMarkers.Focus</c>) — a peer who never clicks never moves, which is precisely the
    /// property the rule protects. So the walk starts at the two arrival doors, <c>Show</c> and
    /// <c>ShowLocal</c>, and follows calls only into this class's own methods; everything a packet can reach
    /// is still banned, and code only a mouse can reach is not. L252 arm <c>click-moves-no-camera</c> holds
    /// the other side: the click path must ACTUALLY reach a camera mover, so the two laws together pin the
    /// camera move to exactly one caller.
    ///
    /// THE ARMS, over <see cref="PingMarkers"/> and its compiler-generated nests:
    ///   (a) <c>ping-moves-a-camera</c> — no call reaches a camera mover. <c>GeoscapeView.ChaseTarget</c>
    ///       (:1102) is the named one; the rest of the set is the vocabulary L97 arm (d) already bans plus
    ///       the geoscape's chase/focus verbs.
    ///   (b) <c>ping-enters-a-state</c> — no call reaches the view-state stack: <c>SwitchToState</c>,
    ///       <c>EnterState</c>/<c>ExitState</c>, or any of <c>GeoscapeView</c>'s public <c>To*State</c>
    ///       shortcuts over it (<c>ToMutateState</c>, <c>ToEditUnitState</c>, …). L63's zombie — an Enter
    ///       of an already-popped state that re-fires its cached ability — is the reason this is a HARD
    ///       ban and not a preference, and a ping is delivered at an arbitrary moment in the receiver's
    ///       turn, which is the worst possible moment for it.
    ///   (c) <c>ping-changes-a-selection</c> — no call reaches <c>GeoscapeView.SetSelectedActor</c> /
    ///       <c>SelectActorAndVehicle</c> or <c>TacticalView.SelectedActor</c>'s setter. The two picking
    ///       methods the feature DOES call are <c>GeoscapeView.SelectAtCursor</c> and
    ///       <c>TacticalView.SelectAtCursor</c>, whose names lie: both are pure raycast queries that
    ///       return a struct and select nothing (GeoscapeView.cs:970-1006, TacticalView.cs:700-780). The
    ///       arm therefore bans NAMED MEMBERS, never the substring "Select" — a name-substring ban here
    ///       would be red against correct code on day one and would be deleted, taking the real ban with
    ///       it.
    ///   (d) <c>ping-is-not-polled</c> — the hotkey field <c>MultiplayerConfig.PingMarkerKey</c> still
    ///       exists and <see cref="PingMarkers"/> still reads <c>Input.GetKeyDown</c>. A rebindable
    ///       polled key is the ONLY mod-reachable input path (the game's actions are asset-side), so if
    ///       this goes the feature has no trigger and every other arm is green over a dead button.
    ///   (e) POSITIVE CONTROL, EXECUTED — the same scan is run over <see cref="FakeSeam"/> below, which
    ///       calls one banned member of each of (a)/(b)/(c). All three must go red on it. Without this a
    ///       scan that resolved no call edges at all would pass forever and the law would be a comment.
    ///
    /// Falsify: have <see cref="PingMarkers"/> call <c>view.ChaseTarget(actor, true)</c> → (a);
    /// <c>view.ToMutateState(...)</c> → (b); <c>view.SetSelectedActor(actor)</c> or
    /// <c>tacView.SelectedActor = actor</c> → (c); delete <c>PingMarkerKey</c> or the
    /// <c>Input.GetKeyDown</c> poll → (d); empty <see cref="FakeSeam"/> → (e).
    /// </summary>
    internal static class L160_PingMovesNoCameraOrSelection
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>Camera movers. <c>ChaseTarget</c> is the geoscape's own; <c>Hint</c> is how every
        /// caller reaches <c>CameraDirector</c>; the rest is L97 arm (d)'s vocabulary, kept identical on
        /// purpose so the two laws cannot drift into banning different things.</summary>
        private static readonly string[] CameraMovers =
            { "ChaseTarget", "Hint", "EnterFpsCamera", "FocusOn", "SetCameraTarget", "MoveCameraTo" };

        private static readonly string[] StateEntries =
            { "SwitchToState", "EnterState", "ExitState", "SetState", "PushState",
              "ActivateAttackAbilityState", "SwitchToPreviousState" };

        private static readonly string[] SelectionWriters =
            { "SetSelectedActor", "SelectActorAndVehicle", "set_SelectedActor", "SetSelection", "Deselect" };

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var show = seam.GetMethod("Show", AllMembers);
            var poll = seam.GetMethod("Update", AllMembers);
            var key = typeof(Multiplayer.MultiplayerConfig).GetField("PingMarkerKey", AllMembers);

            // The banned members must actually EXIST, or every arm is a ban on nothing. They are named
            // against the shipped game assembly, so a game patch that renames one has to be noticed here
            // rather than silently turning three arms green.
            var geoView = typeof(GeoscapeView);
            var tacView = typeof(TacticalView);
            var premises = new MemberInfo[]
            {
                geoView.GetMethod("ChaseTarget", AllMembers),
                geoView.GetMethod("SetSelectedActor", AllMembers),
                geoView.GetMethod("SelectActorAndVehicle", AllMembers),
                geoView.GetMethod("ToMutateState", AllMembers),
                tacView.GetProperty("SelectedActor", AllMembers)?.GetSetMethod(true),
                // The two queries the feature legitimately calls. If either stops existing, Capture() is
                // picking with something else and arm (c)'s carve-out is describing code that is gone.
                geoView.GetMethod("SelectAtCursor", AllMembers),
                tacView.GetMethod("SelectAtCursor", AllMembers),
            };

            if (show == null || poll == null || key == null || premises.Any(m => m == null))
            {
                yield return "L160 premise-changed: PingMarkers.Show / PingMarkers.Update / " +
                             "MultiplayerConfig.PingMarkerKey, or one of GeoscapeView.ChaseTarget / " +
                             "SetSelectedActor / SelectActorAndVehicle / ToMutateState / " +
                             "TacticalView.set_SelectedActor / either SelectAtCursor, no longer resolves. " +
                             "The ping seam or the members it is forbidden to touch have moved, and every " +
                             "arm below is asserting about a shape the build no longer has.";
                yield break;
            }

            foreach (var v in Scan(seam, "PingMarkers", "Show", "ShowLocal")) yield return v;

            // ── arm (d): the trigger still exists.
            if (!Reaches(poll, "Input", "GetKeyDown"))
                yield return "L160 ping-is-not-polled: PingMarkers.Update no longer calls Input.GetKeyDown. " +
                             "A polled, rebindable KeyCode is the only input path a mod has here — the game's " +
                             "own actions are asset-side and a new one is not mod-reachable — and polling is " +
                             "also what makes the hotkey work during DEPLOYMENT, where no view state of ours " +
                             "is on the stack. Without it every arm above is green over a button nobody can " +
                             "press.";

            // ── arm (e): the scan must be able to SEE a violation.
            var control = Scan(typeof(FakeSeam), "FakeSeam", "Drive").ToList();
            foreach (var want in new[] { "ping-moves-a-camera", "ping-enters-a-state", "ping-changes-a-selection" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L160 control-not-red: FakeSeam calls a banned member for " + want + " and the " +
                                 "scan did not flag it. The arm cannot distinguish a ping that points from one " +
                                 "that drives, so its green above means nothing — this is exactly how an " +
                                 "unfalsifiable law gets baselined and forgotten.";
        }

        private static IEnumerable<string> Scan(Type seam, string label, params string[] entryPoints)
        {
            foreach (var m in ArrivalClosure(seam, entryPoints))
                    foreach (var callee in CalleesOf(m))
                    {
                        var name = callee.Name;
                        if (CameraMovers.Contains(name))
                            yield return "L160 ping-moves-a-camera: " + label + "." + m.Name + " calls " +
                                         (callee.DeclaringType?.Name ?? "?") + "." + name + ". A marker says " +
                                         "WHERE; it never takes the watcher there. L97 holds the same line for " +
                                         "the aim mirror and it is the same line: a peer mid-shot whose camera " +
                                         "is flown across the map by someone else's key press has lost his turn, " +
                                         "not gained a hint.";
                        else if (StateEntries.Contains(name) || IsToState(name))
                            yield return "L160 ping-enters-a-state: " + label + "." + m.Name + " calls " +
                                         (callee.DeclaringType?.Name ?? "?") + "." + name + ". An arriving " +
                                         "message must never move anyone's state stack — an Enter of an " +
                                         "already-popped state resurrects a zombie that re-fires its cached " +
                                         "ability (law L63), and a ping lands at an arbitrary moment in the " +
                                         "receiver's turn.";
                        else if (SelectionWriters.Contains(name))
                            yield return "L160 ping-changes-a-selection: " + label + "." + m.Name + " calls " +
                                         (callee.DeclaringType?.Name ?? "?") + "." + name + ". The receiver's " +
                                         "current selection and current target are his; a ping that re-selects " +
                                         "for him cancels whatever he had queued and does it with no log line. " +
                                         "Picking is done with SelectAtCursor, which despite its name selects " +
                                         "nothing.";
                    }
        }

        /// <summary>Every method a PACKET can reach: the named entry points plus everything they call,
        /// transitively, that is still declared inside the seam (nests included, so a lambda or an iterator
        /// state machine cannot smuggle a call out of the walk). A callee outside the seam ends the walk —
        /// this law is about what THIS code does, and what the game does with a marker is the game's.</summary>
        private static IEnumerable<MethodBase> ArrivalClosure(Type seam, string[] entryPoints)
        {
            var mine = new HashSet<Type>(AllTypes(seam));
            var seen = new HashSet<MethodBase>();
            var queue = new Queue<MethodBase>();
            foreach (var t in mine)
                foreach (var m in AllMethodsOf(t))
                    if (entryPoints.Contains(m.Name) && seen.Add(m)) queue.Enqueue(m);

            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                yield return m;
                foreach (var c in CalleesOf(m))
                    if (c.DeclaringType != null && mine.Contains(c.DeclaringType) && seen.Add(c)) queue.Enqueue(c);
            }
        }

        /// <summary>GeoscapeView's public shortcuts over the state stack are all <c>To&lt;Something&gt;State</c>
        /// (ToMutateState, ToEditUnitState, ToCutsceneState…). Banning the SHAPE catches the ones that do
        /// not exist yet; banning the seven current names would not.</summary>
        private static bool IsToState(string n) =>
            n.Length > 7 && n.StartsWith("To", StringComparison.Ordinal) &&
            n.EndsWith("State", StringComparison.Ordinal);

        /// <summary>ARM (e). One banned call per arm. Never instantiated, never registered — it exists
        /// only to be walked, and its three calls are the three shapes the ban is written against.</summary>
        private sealed class FakeSeam
        {
            internal static void Drive(GeoscapeView geo, TacticalView tac, GeoActor actor, TacticalActor unit)
            {
                geo.ChaseTarget(actor, instant: true);      // (a)
                geo.ToMutateState(null);                    // (b)
                tac.SelectedActor = unit;                   // (c)
            }
        }

        // ─── IL helpers (same primitives as L153/L158; Program.cs is not partial) ────────────────

        private static IEnumerable<Type> AllTypes(Type root)
        {
            yield return root;
            foreach (var n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var d in AllTypes(n)) yield return d;
        }

        private static IEnumerable<MethodBase> AllMethodsOf(Type t)
            => t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers));

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || c.DeclaringType?.Name == declaringType));

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
