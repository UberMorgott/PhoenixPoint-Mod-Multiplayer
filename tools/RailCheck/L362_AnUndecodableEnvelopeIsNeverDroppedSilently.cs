using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L362 — AN ENVELOPE THIS PEER CANNOT DECODE IS NAMED BEFORE IT IS DISCARDED.
    ///
    /// <c>SurfaceRouter.OnInbound</c> is the ONE inbound chokepoint, and an envelope can end its life there in
    /// three ways: held for the turn edge (logged by the holder), consumed by a hook that declines it (the
    /// hooks say so), or failing <c>SyncProtocol.TryDecodeEnvelope</c> — which was a bare <c>return</c>, with
    /// nothing written anywhere. The only silent one of the three.
    ///
    /// WHAT THAT COST, precisely: in the 2026-08-08 capture a 0x84 record was lost and the hop it was lost at
    /// could not be established, BECAUSE this drop leaves no trace. The recoveries this rail has are all armed
    /// by a hole the OWNING SURFACE can see in its own seq stream; a message that dies before it is attributed
    /// to a surface arms nothing at all.
    ///
    /// NOT A RELIABILITY CHANGE. The channel is reliable and ordered by contract (<c>SurfaceSeq</c>:11-13, over
    /// TCP), so a failure here means a MALFORMED envelope, not a lost one — there is nothing to retransmit and
    /// everything to explain. Rate-limited because a decoder that fails once usually fails on everything after
    /// it, and a per-frame error is its own kind of silence.
    ///
    /// Falsify (each verified RED, then restored): restore the bare `return` → (a) drop-is-silent; log at
    /// Log/Warning level instead of Error → (a) (the sink records the type); make the rate limit swallow the
    /// FIRST occurrence → (a).
    /// </summary>
    internal static class L362_AnUndecodableEnvelopeIsNeverDroppedSilently
    {
        internal static IEnumerable<string> Check()
        {
            // A payload that is definitely not an envelope: too short for a header, and no valid magic.
            var garbage = new byte[] { 0x01, 0x02, 0x03 };
            byte surfaceId; SyncKind kind; uint ordinal; byte[] payload;
            if (SyncProtocol.TryDecodeEnvelope(garbage, out surfaceId, out kind, out ordinal, out payload))
            {
                yield return "L362 premise-changed: SyncProtocol.TryDecodeEnvelope now ACCEPTS three arbitrary " +
                             "bytes as an envelope. This law can no longer reach the drop it exists to check — and " +
                             "a decoder that accepts garbage is a much larger problem than the drop was.";
                yield break;
            }

            var prevHandler = Debug.unityLogger.logHandler;
            var prevTac = SurfaceRouter.TacticalInbound;
            var prevGeo = SurfaceRouter.ClientBehindTurnEdge;
            var heard = new List<string>();
            string threw = null;
            try
            {
                SurfaceRouter.TacticalInbound = null;
                SurfaceRouter.ClientBehindTurnEdge = null;   // never hold: the hold is a DIFFERENT, logged, exit
                Debug.unityLogger.logHandler = new Listener(heard);
                new SurfaceRouter().OnInbound(1UL, garbage);
            }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name; }
            finally
            {
                Debug.unityLogger.logHandler = prevHandler;
                SurfaceRouter.TacticalInbound = prevTac;
                SurfaceRouter.ClientBehindTurnEdge = prevGeo;
            }

            if (threw != null)
            {
                yield return "L362 router-throws: an undecodable envelope made the ONE inbound chokepoint throw (" +
                             threw + "). Forward-compatibility requires this method to drop what it cannot read, " +
                             "never to fail on it — one malformed message would otherwise end the session.";
                yield break;
            }
            // ── (a) THE DROP IS AUDIBLE ──────────────────────────────────────────────
            if (heard.Count == 0)
                yield return "L362 drop-is-silent: an envelope that failed to decode was discarded with nothing " +
                             "written anywhere. It is the only unlogged exit on the rail's single inbound " +
                             "chokepoint, and it is why a lost 0x84 record in the 2026-08-08 capture could not be " +
                             "pinned to a hop: every recovery this rail has is armed by a hole its own surface can " +
                             "SEE, and a message dropped before it has a surface arms none of them.";
        }

        /// <summary>Records what the rail says, at Error level and above — the harness's own sink discards
        /// everything, which is right for every other law and useless for this one.</summary>
        private sealed class Listener : UnityEngine.ILogHandler
        {
            private readonly List<string> _heard;
            internal Listener(List<string> heard) { _heard = heard; }

            public void LogFormat(LogType t, UnityEngine.Object c, string fmt, params object[] a)
            {
                if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert) _heard.Add(fmt);
            }

            public void LogException(Exception e, UnityEngine.Object c) { }
        }
    }
}
