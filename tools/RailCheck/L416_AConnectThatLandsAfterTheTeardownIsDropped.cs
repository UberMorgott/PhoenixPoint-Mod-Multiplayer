using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Transport;

namespace RailCheck
{
    /// <summary>
    /// L416 — A CONNECT THAT LANDS AFTER THE TEARDOWN IS DROPPED, NOT QUEUED.
    ///
    /// THE FAILURE (d078369). <c>ConnectWorker</c> honoured <c>_connectAborted</c> on its TIMEOUT and CATCH
    /// arms but not on the SUCCESS arm, and <c>Shutdown</c> closes a pending client only if the result is
    /// ALREADY queued — its writer <c>Join(1000)</c> runs after that cleanup. So a worker that passed the
    /// pre-check with the flag still false could finish <c>EndConnect</c> after <c>Shutdown</c> returned and
    /// queue a live connected <c>TcpClient</c> that nobody owns: leaked until finalization, with the remote
    /// host holding a phantom peer until its heartbeat expired, and <c>_pendingConnectResult</c> latched so
    /// the next <c>Update</c> raised <c>OnStateChanged</c>/<c>OnPeerConnected</c> on a torn-down transport.
    ///
    /// THE RULE. The check belongs INSIDE <c>QueueConnectResult</c>'s lock, not at the <c>EndConnect</c>
    /// call site: <c>Shutdown</c> raises the flag BEFORE it takes that lock, so whichever side loses the
    /// race is the side that closes the socket. An out-of-lock check only narrows the window. And the check
    /// must come BEFORE the latch — a guard that runs after <c>_pendingConnectResult = true</c> has already
    /// armed the surfacing it was meant to prevent.
    ///
    /// THE ARMS:
    ///   (a) <c>success-arm-ignores-the-abort</c> — <c>QueueConnectResult</c>'s IL must reference
    ///       <c>_connectAborted</c> at all. This arm fails CLOSED: an IL walk that reads nothing reports the
    ///       flag as absent, so it cannot pass vacuously.
    ///   (b) <c>abort-checked-after-the-latch</c> — and the first reference to <c>_connectAborted</c> must
    ///       precede the first reference to <c>_pendingConnectResult</c> in IL order. Order is the whole
    ///       fix: the flag is only worth reading while the result can still be dropped.
    ///   (c) <c>disconnect-leaves-the-connect-running</c> — <c>Disconnect</c> must WRITE
    ///       <c>_connectAborted</c>. A Disconnect that is not followed by a Shutdown otherwise leaves an
    ///       in-flight connect running, and it lands on a transport the caller has already let go.
    ///       <c>Connect</c> clears the flag again, so a reconnect on the same instance stays fine.
    ///
    /// GUARD: <c>premise-changed</c> if the transport type, <c>QueueConnectResult</c>, <c>Disconnect</c>, or
    /// either field stops resolving.
    ///
    /// Falsify: delete the <c>if (_connectAborted)</c> line from <c>QueueConnectResult</c> → (a); move it
    /// below <c>_pendingConnectResult = true</c> → (b); drop <c>_connectAborted = true</c> from
    /// <c>Disconnect</c> → (c).
    /// </summary>
    internal static class L416_AConnectThatLandsAfterTheTeardownIsDropped
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var transport = typeof(DirectTransport);
            var queue = transport.GetMethod("QueueConnectResult", All);
            var disconnect = transport.GetMethod("Disconnect", All);
            var aborted = transport.GetField("_connectAborted", All);
            var pending = transport.GetField("_pendingConnectResult", All);

            if (queue == null || disconnect == null || aborted == null || pending == null)
            {
                yield return "L416 premise-changed: DirectTransport.{QueueConnectResult, Disconnect, " +
                             "_connectAborted, _pendingConnectResult} no longer all resolve. The connect " +
                             "teardown race is decided at exactly that seam; re-point this law before " +
                             "assuming a connect completing after Shutdown can no longer latch a live socket " +
                             "onto a dead transport.";
                yield break;
            }

            // IL order, not source order: FieldRefs walks the body front to back, so the first index of each
            // field answers "which happens first" without needing offsets.
            var refs = Program.FieldRefs(queue).ToList();
            int atAbort = refs.FindIndex(f => Same(f, aborted));
            int atLatch = refs.FindIndex(f => Same(f, pending));

            if (atAbort < 0)
                yield return "L416 success-arm-ignores-the-abort: QueueConnectResult never reads " +
                             "_connectAborted. Its success arm then queues whatever the worker finished with, " +
                             "including a connect that completed after Shutdown returned: a live TcpClient " +
                             "nobody owns (leaked until finalization, the remote host holding a phantom peer " +
                             "until its heartbeat expires) and a latched _pendingConnectResult that the next " +
                             "Update surfaces as OnStateChanged/OnPeerConnected on a torn-down transport.";
            else if (atLatch >= 0 && atLatch < atAbort)
                yield return "L416 abort-checked-after-the-latch: QueueConnectResult touches " +
                             "_pendingConnectResult before it reads _connectAborted. The guard has to run " +
                             "while the result can still be dropped — once the latch is armed, the next " +
                             "Update surfaces the connection whatever the flag then says, because " +
                             "SurfacePendingConnect is called unconditionally.";

            if (!Program.FieldRefs(disconnect, OpCodes.Stfld).Any(f => Same(f, aborted)))
                yield return "L416 disconnect-leaves-the-connect-running: DirectTransport.Disconnect never " +
                             "writes _connectAborted. Only Shutdown then aborts an in-flight connect, so a " +
                             "Disconnect that is not followed by one leaves the worker running and " +
                             "QueueConnectResult latches its live socket onto a transport the caller has " +
                             "already let go. Connect clears the flag again, so raising it here costs a " +
                             "reconnect nothing.";
        }

        private static bool Same(FieldInfo a, FieldInfo b)
            => a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
