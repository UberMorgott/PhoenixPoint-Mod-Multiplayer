using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE ONE CROSS-SURFACE ORDER KEY. <see cref="SurfaceSeq"/> is per-SURFACE by design (an outcome on
    /// one surface must never suppress an outcome on another), which is exactly why a 0xB6 event raise and
    /// a 0xAC value batch carry incomparable seqs — and presentation order therefore fell back to LOCAL
    /// ARRIVAL ORDER. Measured 2026-08-05: a research completion and a geoscape event left the host in one
    /// frame and reached a client 384 ms apart, in the opposite order. Contents were right on every peer;
    /// only the ORDER differed, and no key existed that could have compared them.
    ///
    /// ONE COUNTER, MINTED AT THE ONE ENCODER (<see cref="SyncProtocol.EncodeEnvelope"/>) — so every
    /// outbound rail message carries an ordinal regardless of surface, by construction rather than by a
    /// per-family opt-in. Nothing here knows what a window is: this is a monotonic number and an ambient,
    /// and the window layer (<see cref="WindowOrder"/>) is its only consumer today.
    ///
    /// INHERITANCE IS THE CRUX. A window a peer produces LOCALLY — a research-complete modal raised by the
    /// native raiser from inside a 0xAC apply — is not caused by a message of its own, so it has no ordinal
    /// to take. <see cref="SurfaceRouter.OnInbound"/> therefore publishes the ordinal of the message it is
    /// APPLYING for the whole synchronous dispatch, and anything born during that dispatch inherits it
    /// (<see cref="ForNewWindow"/>). That is universal by placement: it covers every present and future
    /// family that produces presentation from an apply, with no per-family wiring.
    ///
    /// <see cref="Observe"/> keeps a receiving peer's own counter at or above the sender's, so a window born
    /// OUTSIDE any apply (this peer's own gesture) sorts after everything it has already been told about,
    /// on any peer, rather than colliding with a host ordinal it never minted.
    ///
    /// ponytail: u32, wrapped by nothing. At the rail's real rate (≤10 batches/s plus raises) that is ~13
    /// years of continuous play; a session that outlives it would need a wrap-aware comparator, and the
    /// counter resets per session anyway (<see cref="Reset"/> from SyncEngine.DetachAllChannels).
    /// </summary>
    public static class RailOrdinal
    {
        private static uint _next = 1;
        private static uint _current;   // 0 = not inside an apply

        /// <summary>PROVISIONAL KEYS AWAITING THE ORDINAL THAT WILL CARRY THEIR CAUSE. A window this peer
        /// raises OUTSIDE any apply (research-complete, raised natively in the sim) has no message of its
        /// own yet — its cause leaves in the NEXT envelope, and that is the ordinal every client will
        /// inherit when it applies it. Registering the key here and back-filling it at the next
        /// <see cref="Mint"/> makes host and client agree BY CONSTRUCTION instead of by coincidence.
        /// Bounded: only stamps taken during an active session register, an envelope is minted every diff
        /// tick, and the list is capped anyway.</summary>
        private static readonly List<Action<uint>> _provisional = new List<Action<uint>>(8);

        /// <summary>Register a back-fill for a key stamped outside an apply. Never called from inside one —
        /// there the ordinal is already authoritative (<see cref="ForNewWindow"/> inherits it).</summary>
        internal static void Provisional(Action<uint> backfill)
        {
            if (backfill == null) return;
            lock (_provisional)
            {
                if (_provisional.Count >= 64) _provisional.RemoveAt(0);
                _provisional.Add(backfill);
            }
        }

        /// <summary>HOST/SENDER: take the next ordinal for one outbound envelope. Called from the ONE
        /// encoder, so a new surface cannot forget to carry it. Also the moment every provisional window
        /// key learns its real ordinal — see <see cref="_provisional"/>.</summary>
        internal static uint Mint()
        {
            uint o = _next;
            _next = o + 1;
            Action<uint>[] waiting = null;
            lock (_provisional)
                if (_provisional.Count > 0) { waiting = _provisional.ToArray(); _provisional.Clear(); }
            if (waiting != null)
                foreach (var backfill in waiting)
                    try { backfill(o); } catch { }   // never throw into the encoder
            return o;
        }

        /// <summary>RECEIVER: never mint below something already seen, so a locally-born window sorts after
        /// everything this peer has been told about.</summary>
        internal static void Observe(uint inbound)
        {
            if (inbound >= _next) _next = inbound + 1;
        }

        /// <summary>The ordinal of the rail message being applied right now, or 0 outside an apply.
        /// Public because RailCheck executes the inheritance rule against it.</summary>
        public static uint Current => _current;

        /// <summary>The order key a window born HERE, NOW must carry: inside an apply it INHERITS the
        /// applying message's ordinal (so the window and its cause sort as one thing on every peer);
        /// outside one it takes the next unminted value (after everything seen or sent so far).</summary>
        public static uint ForNewWindow() => _current != 0 ? _current : _next;

        /// <summary>Publish <paramref name="ordinal"/> as the applying message for the scope's lifetime.
        /// A struct so the per-message cost is a stack slot, not an allocation; nested applies restore the
        /// previous value rather than clearing, so a re-entrant dispatch cannot orphan an inner window.</summary>
        internal static Scope Applying(uint ordinal) => new Scope(ordinal);

        internal struct Scope : IDisposable
        {
            private readonly uint _prev;
            private readonly bool _entered;

            internal Scope(uint ordinal)
            {
                _prev = _current;
                _entered = true;
                _current = ordinal;
            }

            public void Dispose()
            {
                if (_entered) _current = _prev;
            }
        }

        /// <summary>Session teardown (SyncEngine.DetachAllChannels), same contract as the seq streams.</summary>
        public static void Reset()
        {
            _next = 1;
            _current = 0;
            lock (_provisional) _provisional.Clear();
        }
    }
}
