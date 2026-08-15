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
    /// INHERITANCE IS THE CRUX for anything born inside an apply. <see cref="SurfaceRouter.OnInbound"/>
    /// publishes the ordinal of the message it is APPLYING for the whole synchronous dispatch, and anything
    /// born during that dispatch can read it from <see cref="Current"/>. WINDOWS NO LONGER DO (L523): a
    /// window's order is the host's journal position (<see cref="WindowJournal"/>), minted once at the one
    /// capture seam, so no peer derives a window key from a counter of its own.
    ///
    /// <see cref="Observe"/> keeps a receiving peer's own counter at or above the sender's, so a value this
    /// peer mints sorts after everything it has already been told about rather than colliding with a host
    /// ordinal it never minted.
    ///
    /// ponytail: u32, wrapped by nothing. At the rail's real rate (≤10 batches/s plus raises) that is ~13
    /// years of continuous play; a session that outlives it would need a wrap-aware comparator, and the
    /// counter resets per session anyway (<see cref="Reset"/> from SyncEngine.DetachAllChannels).
    /// </summary>
    public static class RailOrdinal
    {
        private static uint _next = 1;
        private static uint _current;   // 0 = not inside an apply

        /// <summary>HOST/SENDER: take the next ordinal for one outbound envelope. Called from the ONE
        /// encoder, so a new surface cannot forget to carry it.
        ///
        /// THE PROVISIONAL WINDOW BACK-FILL IS GONE (L523). It back-filled the WHOLE pending list with the
        /// ONE ordinal this call minted, so the host's research window and the host's event window took the
        /// same key and tied to insert order — the measured mechanism of P1. Window order is the host's
        /// journal position now (<see cref="WindowJournal"/>); this counter orders rail MESSAGES only.</summary>
        internal static uint Mint()
        {
            uint o = _next;
            _next = o + 1;
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
        }
    }
}
