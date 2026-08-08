using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using Multiplayer.UI;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities;

namespace RailCheck
{
    /// <summary>
    /// L250 — THE PING NEVER PAINTS THROUGH THE GAME'S SHARED HIGHLIGHT LATCH, AND THE PINGED ACTOR IS STILL
    /// MARKED BY SOMETHING.
    ///
    /// WHY THIS LAW EXISTS (owner's request 2026-08-08, and the answer to it). The ask was to mark a pinged
    /// actor with the game's own full-body colour flash — the white paint a stamina-restore target wears, the
    /// coloured one a heal target wears. That paint was FOUND, read, and REJECTED, and this law is what keeps
    /// it rejected, because the request is a natural one and will be made again by somebody who has not read
    /// <c>HighlightControllerComponent</c>.
    ///
    /// WHAT THE PAINT ACTUALLY IS. <c>IHighlightable.Highlight(highlight, friendly, ability: true)</c> — the
    /// same entry point this feature already refused for the selection case. The ability BRANCH is the good
    /// half of it: per-actor cloned materials (<c>HighlightControllerComponent.cs:179-181</c>), and it skips
    /// the global <c>Shader.SetGlobalColor("BodypartHighlightColor")</c> the selection branch writes
    /// (<c>:166-170</c>). But the ENTRY IS SHARED AND LATCHED: <c>Highlight</c> opens with
    /// <c>if (_isHighlighted == highlight) return false;</c> (<c>:144-153</c>) over ONE bool per body part,
    /// and the game's own ability targeting rides that same bool — <c>UIStateShoot.cs:1445</c> raises it with
    /// <c>ability: true</c>, <c>:1463</c> lowers it. Two consequences, both silent:
    ///   · a ping on an actor the local player is already aiming at returns FALSE and draws nothing;
    ///   · the ping's expiry lowers the latch under the game, after which the game's own clear at
    ///     <c>UIStateShoot.cs:1463</c> no-ops and the actor keeps a paint nobody will take off.
    /// AND IT COULD NOT HAVE CARRIED THE COLOUR: the ability look is one scene-wide shader asset,
    /// <c>LightingSettingsCharacters.AbilityHighlightShader</c>, applied at
    /// <c>HighlightControllerComponent.cs:245-249</c>, with no per-call colour on that branch at all — so
    /// green-mine / blue-yours (L251) is not merely awkward there, it is impossible.
    ///
    /// THE ARMS:
    ///   (a) <c>ping-paints-through-the-shared-latch</c> — no method of <see cref="PingMarkers"/> reaches
    ///       <c>Highlight</c> or <c>SetSpecialShader</c> on anything implementing <c>IHighlightable</c>, nor
    ///       on <c>HighlightControllerComponent</c> itself. Not scoped to the arrival closure the way L160's
    ///       camera ban is: the latch is wrong on a click too, so this ban is total.
    ///   (b) <c>pinged-actor-loses-its-mark</c> — the OUTCOME arm, and the reason this is not just a ban. An
    ///       actor ping must still MARK the actor: <c>PingMarkers.ShowTac</c> resolves the named actor
    ///       (<c>TacticalActorKey.Resolve</c>) and hands the result to <c>Track</c>. Without this, "we do not
    ///       paint" could be satisfied by drawing nothing at all, which is the defect wearing the fix's
    ///       clothes.
    ///   (c) POSITIVE CONTROL, EXECUTED — the same scan runs over <see cref="FakeSeam"/>, which calls the
    ///       banned paint twice. It must go red, or arm (a)'s green is a scan that resolved no call edges.
    ///
    /// Falsify: call <c>((IHighlightable)actor).Highlight(true, false, true)</c> from <c>ShowTac</c> → (a);
    /// drop the <c>Track</c> call from <c>ShowTac</c>'s object branch → (b); empty <see cref="FakeSeam"/> →
    /// (c); rename the latch field this law names → <c>premise-changed</c>, which is the "go and re-read it,
    /// the game may have made this safe" signal.
    /// </summary>
    internal static class L250_PingPaintStaysOffTheSharedHighlight
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The two members that swap an actor's materials wholesale. <c>CustomizeColor</c> is
        /// deliberately NOT here — it is the armour-customisation path, writes property blocks onto the
        /// ORIGINAL materials, and would be a different (also wrong) bug.</summary>
        private static readonly string[] PaintVerbs = { "Highlight", "SetSpecialShader" };

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var showTac = seam.GetMethod("ShowTac", AllMembers);
            var hcc = typeof(HighlightControllerComponent);
            var latch = hcc.GetField("_isHighlighted", AllMembers);
            var gate = hcc.GetMethod("Highlight", AllMembers);
            var iface = typeof(IHighlightable).GetMethod("Highlight");
            var shader = typeof(LightingSettingsCharacters).GetField("AbilityHighlightShader");

            if (showTac == null || latch == null || gate == null || iface == null || shader == null ||
                !typeof(IHighlightable).IsAssignableFrom(typeof(TacticalActor)))
            {
                yield return "L250 premise-changed: one of PingMarkers.ShowTac, " +
                             "HighlightControllerComponent._isHighlighted (the shared latch), " +
                             "HighlightControllerComponent.Highlight, IHighlightable.Highlight or " +
                             "LightingSettingsCharacters.AbilityHighlightShader no longer resolves — or " +
                             "TacticalActor no longer implements IHighlightable. Those five ARE the reason " +
                             "the ability-target paint was refused for pings. If the game has changed them, " +
                             "the refusal has to be re-argued from the new code rather than inherited: " +
                             "re-read HighlightControllerComponent.Highlight and decide again.";
                yield break;
            }

            // ── premise, second half: the latch is still what makes the entry unsafe. A build where
            // Highlight stopped reading _isHighlighted would be a build where two callers no longer swallow
            // each other, and this whole law would deserve deleting rather than obeying.
            if (!Reads(gate, latch))
                yield return "L250 premise-changed: HighlightControllerComponent.Highlight no longer reads " +
                             "_isHighlighted. The shared latch is the entire reason a ping may not use the " +
                             "game's ability-target paint (a ping on an already-highlighted actor was " +
                             "swallowed; the ping's expiry cleared a paint the game still wanted). If the " +
                             "latch is gone, go and re-read the method — the paint may now be safe and the " +
                             "owner has asked for it twice.";

            foreach (var v in Scan(seam, "PingMarkers")) yield return v;

            // ── arm (b): the actor ping still marks the actor.
            foreach (var must in new[] { "Resolve", "Track" })
                if (!Reaches(showTac, null, must))
                    yield return "L250 pinged-actor-loses-its-mark: PingMarkers.ShowTac no longer calls " +
                                 must + ". An actor ping must resolve the actor the packet names " +
                                 "(TacticalActorKey.Resolve) and hand the marker it built to Track, which is " +
                                 "the single funnel that gives it an expiry and sounds the native cue. " +
                                 "Refusing the game's shared highlight (arm (a)) is only correct while " +
                                 "SOMETHING else still marks the target — otherwise a pinged soldier is " +
                                 "flagged by nothing at all and the feature is quietly gone.";

            // ── arm (c): the scan must be able to SEE the violation.
            if (!Scan(typeof(FakeSeam), "FakeSeam").Any())
                yield return "L250 control-not-red: FakeSeam paints an actor through IHighlightable.Highlight " +
                             "and SetSpecialShader and the scan did not flag either. Arm (a) cannot tell a " +
                             "ping that marks from one that hijacks the game's highlight state, so its green " +
                             "above means nothing.";
        }

        private static IEnumerable<string> Scan(Type seam, string label)
        {
            foreach (var t in AllTypes(seam))
                foreach (var m in AllMethodsOf(t))
                    foreach (var callee in CalleesOf(m))
                    {
                        if (!PaintVerbs.Contains(callee.Name)) continue;
                        var owner = callee.DeclaringType;
                        if (owner == null) continue;
                        if (owner != typeof(HighlightControllerComponent) &&
                            !typeof(IHighlightable).IsAssignableFrom(owner)) continue;
                        yield return "L250 ping-paints-through-the-shared-latch: " + label + "." + m.Name +
                                     " calls " + owner.Name + "." + callee.Name + ". That is the game's " +
                                     "ability-target paint, and its entry is latched on ONE shared bool " +
                                     "(HighlightControllerComponent.cs:144-153) that the game's own targeting " +
                                     "already drives (UIStateShoot.cs:1445/:1463). A ping on an actor the " +
                                     "local player is aiming at is swallowed and draws nothing; the ping's " +
                                     "expiry lowers the latch under the game and leaves a paint nobody takes " +
                                     "off. Neither failure logs a line. Mark the actor with the beacon this " +
                                     "class owns instead — it follows the actor, expires on our clock, and is " +
                                     "the only one of the two that can carry the owner colour (L251).";
                    }
        }

        /// <summary>ARM (c). Never instantiated, never registered — it exists only to be walked.</summary>
        private sealed class FakeSeam
        {
            internal static void Paint(TacticalActor actor)
            {
                ((IHighlightable)actor).Highlight(highlight: true, friendly: false, ability: true);
                ((IHighlightable)actor).SetSpecialShader(null);
            }
        }

        // ─── IL helpers (same primitives as L153/L158/L160/L182; Program.cs is not partial) ────────────

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

        /// <summary>Does the method touch that field — ldfld (0x7B) or ldflda (0x7C).</summary>
        private static bool Reads(MethodBase m, FieldInfo field)
        {
            foreach (var tok in TokensAfter(m, 0x7B, 0x7C))
            {
                FieldInfo f = null;
                try { f = m.Module.ResolveField(tok); } catch { }
                if (f == field) return true;
            }
            return false;
        }

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
