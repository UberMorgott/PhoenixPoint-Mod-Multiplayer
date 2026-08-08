using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.UI;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L251 — MY PING IS GREEN TO ME AND EVERY OTHER PEER'S IS BLUE TO ME, ON THE MARKER AND ON THE ARROW.
    ///
    /// THE RULE (owner's ask 2026-08-08): the colour answers WHO PINGED, and it is decided at the VIEWER, not
    /// at the sender. Each peer sees its own pings green and everyone else's blue — so the same ping is green
    /// on one screen and blue on the other four, and that is correct, not a desync.
    ///
    /// WHY IT CANNOT BE CHECKED BY LOOKING AT THE WIRE. Nothing on the wire says who sent a ping: the packet
    /// is byte-identical for every sender, deliberately, so builds cannot disagree about its shape. The whole
    /// answer is WHICH DOOR the payload came through — <c>ShowLocal</c> is called only by the peer whose key
    /// was pressed, <c>Show</c> only by the packet drain. One boolean, two entry points, and if the two ever
    /// pass the SAME constant every ping on every screen silently becomes one colour with nothing thrown and
    /// nothing logged. That is the failure this law is written against, and no other law can see it: L182 arm
    /// (c) checks that <c>Update</c> uses the local door and that both showers call <c>Tint</c> — both stay
    /// green if the two doors agree on the wrong value, or if the two COLOURS are the same colour.
    ///
    /// THE ARMS:
    ///   (a) <c>own-colour-is-not-green</c> / <c>peer-colour-is-not-blue</c> — the OUTCOME arm, read off the
    ///       actual values: <c>PingMarkers.Own</c> is green-dominant, <c>PingMarkers.Peer</c> is
    ///       blue-dominant, and the two are far enough apart to tell at a glance. Asserting the VALUES and
    ///       not merely "two fields exist" is the point — the request named two specific colours.
    ///   (b) <c>the-two-doors-agree</c> — <c>Show</c> hands <c>ShowCore</c> <c>false</c> and <c>ShowLocal</c>
    ///       hands it <c>true</c>, read off the IL byte immediately before each call. Swap them and every
    ///       peer sees its own pings in the other-peer colour.
    ///   (c) <c>arrow-ignores-who-pinged</c> — the off-screen arrow is drawn in BOTH colours: <c>OnGUI</c>
    ///       reads <c>Live.Mine</c> and loads both colour fields. The arrow is the half a player sees when
    ///       the ping is off screen, which is exactly when "who is pointing" matters most, and it used to be
    ///       drawn in a fixed white.
    ///   (d) POSITIVE CONTROL, EXECUTED — the same green/blue predicates are run over a deliberately swapped
    ///       pair (blue offered as "own", green as "peer"). Both must be flagged, or arm (a) is a predicate
    ///       that says yes to anything and its green above means nothing.
    ///
    /// Falsify: set <c>Own</c> to blue → (a); swap the <c>true</c>/<c>false</c> in <c>Show</c>/<c>ShowLocal</c>
    /// → (b); draw the arrow in <c>Color.white</c> again → (c); make <see cref="IsGreen"/> return true
    /// unconditionally → (d).
    /// </summary>
    internal static class L251_PingColourAnswersWhoPinged
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>Green-dominant and unmistakably so: the green channel leads both others by a clear
        /// margin. A margin, not a bare <c>&gt;</c>, because (0.51, 0.52, 0.51) passes a bare one and reads
        /// as grey on a globe.</summary>
        private static bool IsGreen(Color c) => c.g >= 0.5f && c.g - c.r >= 0.25f && c.g - c.b >= 0.25f;

        private static bool IsBlue(Color c) => c.b >= 0.5f && c.b - c.r >= 0.25f && c.b - c.g >= 0.25f;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PingMarkers);
            var ownField = seam.GetField("Own", AllMembers);
            var peerField = seam.GetField("Peer", AllMembers);
            var show = seam.GetMethod("Show", AllMembers);
            var showLocal = seam.GetMethod("ShowLocal", AllMembers);
            var showCore = seam.GetMethod("ShowCore", AllMembers);
            var onGui = seam.GetMethod("OnGUI", AllMembers);
            var mine = seam.GetNestedType("Live", BindingFlags.NonPublic)?.GetField("Mine", AllMembers);

            if (ownField == null || peerField == null || show == null || showLocal == null ||
                showCore == null || onGui == null || mine == null ||
                ownField.FieldType != typeof(Color) || peerField.FieldType != typeof(Color))
            {
                yield return "L251 premise-changed: one of PingMarkers.Own / PingMarkers.Peer (both must be " +
                             "UnityEngine.Color) / Show / ShowLocal / ShowCore / OnGUI / Live.Mine no longer " +
                             "resolves. The owner-colour mechanism is two colour constants, two entry doors " +
                             "and one flag carried to the arrow — every arm below is asserting about a shape " +
                             "the build no longer has.";
                yield break;
            }

            var own = (Color)ownField.GetValue(null);
            var peer = (Color)peerField.GetValue(null);
            foreach (var v in Judge(own, peer, "PingMarkers")) yield return v;

            // ── arm (b): the two doors pass opposite constants. The byte before the call IS the argument —
            // `mine` is the last one pushed, so ldc.i4.0 / ldc.i4.1 sits immediately in front of the call.
            var atShow = MineArgAt(show, showCore);
            var atLocal = MineArgAt(showLocal, showCore);
            if (atShow != 0)
                yield return "L251 the-two-doors-agree: PingMarkers.Show does not pass `mine: false` to " +
                             "ShowCore (found " + Describe(atShow) + "). Show is the door an ARRIVING packet " +
                             "comes through, so everything it draws belongs to somebody else and must be " +
                             "blue. Nothing on the wire carries a sender, so this constant is the only thing " +
                             "in the build that answers 'is this mine' — get it wrong and every ping on every " +
                             "screen is one colour, with nothing thrown and nothing logged.";
            if (atLocal != 1)
                yield return "L251 the-two-doors-agree: PingMarkers.ShowLocal does not pass `mine: true` to " +
                             "ShowCore (found " + Describe(atLocal) + "). ShowLocal is reached only from " +
                             "Update, i.e. only on the peer whose own key was pressed; if it stops saying so, " +
                             "a player never sees a single green ping and has no way to tell his own marker " +
                             "from the other four players'.";

            // ── arm (c): the arrow carries the same answer.
            if (!Loads(onGui, ownField) || !Loads(onGui, peerField))
                yield return "L251 arrow-ignores-who-pinged: PingMarkers.OnGUI does not load both Own and " +
                             "Peer. The off-screen arrow is what a player sees when the ping is NOT on his " +
                             "screen — the one moment 'who is pointing at this' cannot be read off the marker, " +
                             "because the marker is not visible. Drawing it in a fixed colour (it shipped " +
                             "white) throws the answer away exactly where it is needed.";
            if (!ReadsField(onGui, mine))
                yield return "L251 arrow-ignores-who-pinged: PingMarkers.OnGUI never reads Live.Mine, so the " +
                             "arrow cannot be picking its colour per ping. Two peers pinging at once must " +
                             "produce two arrows of different colours on every screen.";

            // ── arm (d): the predicates must be able to say NO.
            var control = Judge(Color.blue, Color.green, "FakeColours").ToList();
            foreach (var want in new[] { "own-colour-is-not-green", "peer-colour-is-not-blue" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L251 control-not-red: the green/blue predicates were handed a swapped pair " +
                                 "(blue as own, green as peer) and did not flag " + want + ". A predicate that " +
                                 "accepts any colour makes arm (a) decorative — it would stay green through a " +
                                 "build where both pings drew grey.";
        }

        private static IEnumerable<string> Judge(Color own, Color peer, string label)
        {
            if (!IsGreen(own))
                yield return "L251 own-colour-is-not-green: " + label + "'s own-ping colour is " + own +
                             ", which is not recognisably green. The owner asked for green-mine / blue-yours " +
                             "by name; a colour that only differs from the peer one in hue-theory is a " +
                             "distinction nobody makes mid-turn on a lit battlefield.";
            if (!IsBlue(peer))
                yield return "L251 peer-colour-is-not-blue: " + label + "'s peer-ping colour is " + peer +
                             ", which is not recognisably blue.";
            if (own == peer)
                yield return "L251 own-colour-is-not-green: " + label + "'s two ping colours are identical (" +
                             own + "), so the own/peer distinction is drawn but invisible.";
        }

        /// <summary>The constant handed to <paramref name="callee"/> as its LAST argument, read off the byte
        /// in front of the call: 0 for <c>ldc.i4.0</c>, 1 for <c>ldc.i4.1</c>, -1 for "no such call", -2 for
        /// "the call is there but the argument is not a literal bool".</summary>
        private static int MineArgAt(MethodBase caller, MethodBase callee)
        {
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return -1;
            var found = -1;
            for (var i = 1; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != callee) continue;
                if (il[i - 1] == 0x16) return 0;
                if (il[i - 1] == 0x17) return 1;
                found = -2;
            }
            return found;
        }

        private static string Describe(int arg)
            => arg == 0 ? "`false`" : arg == 1 ? "`true`" :
               arg == -1 ? "no call to ShowCore at all" : "a non-literal argument";

        private static bool Loads(MethodBase m, FieldInfo field) => Touches(m, field, 0x7E);   // ldsfld

        private static bool ReadsField(MethodBase m, FieldInfo field) => Touches(m, field, 0x7B, 0x7C);

        private static bool Touches(MethodBase m, FieldInfo field, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (Array.IndexOf(opcodes, il[i]) < 0) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f == field) return true;
            }
            return false;
        }
    }
}
