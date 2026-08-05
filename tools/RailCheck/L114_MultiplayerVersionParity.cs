using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Parity;

namespace RailCheck
{
    /// <summary>
    /// L114 — TWO MULTIPLAYER MOD VERSIONS MUST NOT SHARE A CAMPAIGN, AND THE PLAYER MUST BE TOLD WHICH.
    ///
    /// L108 gated the GAME build. This gates OUR OWN. Two Multiplayer builds do not agree on the wire —
    /// packet ids, envelope surfaces and manifest shape all move between versions — so a version mismatch
    /// is upstream of every other symptom: whatever else goes wrong is downstream noise, and the player
    /// chasing it is chasing the wrong thing. The version already rode the JOIN inside the ordinary mod
    /// list, but only as one anonymous "Mod version differs" line among DLC, settings and every other mod,
    /// discovered by clicking a badge after already sitting in a lobby.
    ///
    /// EXECUTED, not inspected — every type on this path is pure:
    ///   (a) two peers on the SAME Multiplayer version are NOT blocked and get NO notice. Blocking a bad
    ///       join is the point; blocking a good one locks everyone out of co-op;
    ///   (b) different versions block AND name BOTH numbers, in the host's roster diff and in the client's
    ///       join notice. A mismatch a player cannot read is a mismatch they cannot fix;
    ///   (c) exactly ONE line names the mod. The dedicated line and the generic mod-loop line would
    ///       otherwise both fire and say the same thing twice, in two different phrasings;
    ///   (d) an UNKNOWN version on either side neither blocks nor notifies — "" means we failed to READ a
    ///       version, which is not evidence the other install is wrong;
    ///   (e) NO QUORUM (the afc111a regression, re-armed): the block is a pure function of ONE peer's own
    ///       diff text, and the start gate does not read parity at all — one mismatched peer must never
    ///       freeze the lobby for everybody else;
    ///   (f) the seams: the client leg must actually compute the notice on the accept and the UI must
    ///       actually drain it, or every arm above passes while nothing is ever shown.
    /// ponytail: (f) scans raw IL for the callee's metadata token, same lenient trade as L105-L108.
    /// </summary>
    internal static class L114_MultiplayerVersionParity
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>A peer running Multiplayer <paramref name="mpVersion"/> and otherwise identical to
        /// every other peer here (same game build, same DLC, same other mods) — so any diff that shows up
        /// is attributable to the one thing this law is about.</summary>
        private static ParityManifest Peer(string mpVersion) => ParityManifest.Build(
            new[] { "DLC1" },
            new[] { (ParityManifest.MultiplayerModId, mpVersion), ("some.other.mod", "2.0") },
            null,
            "1.30.2.1234");

        internal static IEnumerable<string> Check()
        {
            const string Host = "0.9.0";
            const string Old = "0.8.1";

            // ── (a) same version → a legitimate peer is never blocked ──────
            var same = ParityComparer.Compare(Peer(Host), Peer(Host));
            if (same.Count != 0)
                yield return "L114 match-blocked: two peers on the SAME Multiplayer version produced " +
                             same.Count + " parity diff(s) (" + ParityComparer.Format(same).Replace("\n", " | ") +
                             "). A version gate that refuses matching installs locks every player out of " +
                             "co-op, which is a worse failure than the desync it guards against.";
            if (!ParityComparer.ReadyAllowed(ParityComparer.Format(same)))
                yield return "L114 match-cannot-ready: two peers on the SAME Multiplayer version leave " +
                             "ReadyAllowed false — the matching peer can never ready up and the campaign " +
                             "never starts.";
            if (ParityComparer.VersionNoticeForClient(Peer(Host), Peer(Host)).Length != 0)
                yield return "L114 match-notified: a client on the SAME Multiplayer version as the host is " +
                             "shown a version-mismatch box at the door. Being told to update when nothing " +
                             "is wrong sends the player to fix an install that is already correct.";

            // ── (b) different versions → blocked, and legible on BOTH legs ─
            var crossed = ParityComparer.Compare(Peer(Host), Peer(Old));
            var text = ParityComparer.Format(crossed);
            if (crossed.Count == 0)
                yield return "L114 mismatch-passes: a host on Multiplayer v" + Host + " and a client on v" +
                             Old + " produce NO parity diff, so the client readies up and a campaign starts " +
                             "across two mod builds that do not agree on the wire.";
            else if (!text.Contains(Host) || !text.Contains(Old))
                yield return "L114 mismatch-unreadable: the parity diff for a Multiplayer version mismatch " +
                             "does not name both versions (got: " + text.Replace("\n", " | ") + "). The badge " +
                             "is the only place the host learns why that peer's READY is locked.";
            if (ParityComparer.ReadyAllowed(text))
                yield return "L114 mismatch-can-ready: a Multiplayer version mismatch leaves ReadyAllowed " +
                             "true, so the gate never engages and the mismatch is a warning nobody is " +
                             "stopped by.";
            var notice = ParityComparer.VersionNoticeForClient(Peer(Host), Peer(Old));
            if (notice.Length == 0)
                yield return "L114 client-not-told: a client whose Multiplayer version differs from the " +
                             "host's gets NO join notice. Its READY is locked with nothing said, which is " +
                             "the silent failure this repo refuses.";
            else if (!notice.Contains(Host) || !notice.Contains(Old))
                yield return "L114 notice-unreadable: the client's join notice does not name both versions " +
                             "(got: " + notice.Replace("\n", " | ") + "). 'Versions differ' with no numbers " +
                             "leaves the player unable to tell which side is behind.";

            // ── (c) said once, not twice ───────────────────────────────────
            var named = crossed.Count(d => d.Contains(ParityManifest.MultiplayerModId) ||
                                           d.IndexOf("Multiplayer mod version", StringComparison.Ordinal) >= 0);
            if (named != 1)
                yield return "L114 duplicate-line: a Multiplayer version mismatch produced " + named +
                             " diff lines about this mod instead of exactly 1 (" +
                             text.Replace("\n", " | ") + "). The dedicated line and the generic mod loop " +
                             "are both firing, so the badge says the same thing twice in two phrasings.";

            // ── (d) unknown on either side must not block or notify ────────
            foreach (var pair in new[] { ("", Host), (Host, ""), ("", "") })
            {
                if (ParityComparer.MultiplayerVersionMismatch(Peer(pair.Item1), Peer(pair.Item2)))
                    yield return "L114 unknown-blocked: an UNKNOWN Multiplayer version (host='" + pair.Item1 +
                                 "' client='" + pair.Item2 + "') was treated as a MISMATCH. '' means we " +
                                 "failed to read a version, not that the other install is wrong — rejecting " +
                                 "on it locks out a peer who is fine.";
                if (ParityComparer.VersionNoticeForClient(Peer(pair.Item1), Peer(pair.Item2)).Length != 0)
                    yield return "L114 unknown-notified: an UNKNOWN Multiplayer version (host='" + pair.Item1 +
                                 "' client='" + pair.Item2 + "') pops the update-your-mod box at the door, " +
                                 "sending a player with a correct install to 'fix' it.";
            }
            // A manifest with no mod list at all (the collector threw) must read as unknown, not crash.
            if (ParityComparer.MultiplayerVersion(null).Length != 0 ||
                ParityComparer.MultiplayerVersion(new ParityManifest()).Length != 0 ||
                ParityComparer.MultiplayerVersionMismatch(null, Peer(Host)))
                yield return "L114 empty-manifest-mismatch: a manifest with no mod list does not read as " +
                             "UNKNOWN. A peer whose collector threw would then be reported as running the " +
                             "wrong version and told to update an install that is correct.";

            // ── (e) per-peer only — no quorum, ever ────────────────────────
            if (!ParityComparer.ReadyAllowed(""))
                yield return "L114 clean-peer-gated: ReadyAllowed('') is false, so a peer with NO diffs of " +
                             "its own is blocked. The gate must key on that peer's own text and nothing else.";
            var canStart = Type.GetType("Multiplayer.Network.LobbyController, Multiplayer")?
                               .GetProperty("CanStart", All)?.GetGetMethod(true);
            var readyAllowed = typeof(ParityComparer).GetMethod("ReadyAllowed", All);
            if (canStart == null)
                yield return "L114 start-gate-gone: LobbyController.CanStart no longer exists — the arm that " +
                             "proves one peer's mismatch cannot freeze the lobby is wired to nothing.";
            else if (CallsAcrossAssemblies(canStart, readyAllowed))
                yield return "L114 parity-quorum: LobbyController.CanStart reads ParityComparer.ReadyAllowed, " +
                             "so ONE mismatched peer closes the START gate for the whole lobby. That is the " +
                             "afc111a regression: parity gates the peer that has it, never the session.";

            // ── (f) the seams ─────────────────────────────────────────────
            var session = Type.GetType("Multiplayer.Network.SessionManager, Multiplayer");
            var onAccept = session?.GetMethod("HandleConnectionAccepted", All);
            var noticeSetter = session?.GetProperty("VersionMismatchNotice", All)?.GetSetMethod(true);
            var noticeGetter = session?.GetProperty("VersionMismatchNotice", All)?.GetGetMethod(true);
            var build = typeof(ParityComparer).GetMethod("VersionNoticeForClient", All);
            var compare = typeof(ParityComparer).GetMethod("Compare", All);
            var mismatch = typeof(ParityComparer).GetMethod("MultiplayerVersionMismatch", All);
            var ui = Type.GetType("Multiplayer.UI.MultiplayerUI, Multiplayer");
            var uiUpdate = ui?.GetMethod("Update", All);
            if (onAccept == null || noticeSetter == null || noticeGetter == null || build == null ||
                compare == null || mismatch == null || uiUpdate == null)
            {
                yield return "L114 seam-gone: SessionManager.HandleConnectionAccepted, " +
                             "SessionManager.VersionMismatchNotice, ParityComparer.VersionNoticeForClient/" +
                             "Compare/MultiplayerVersionMismatch or MultiplayerUI.Update no longer exists — " +
                             "the arms above are exercising a gate that is wired to nothing.";
                yield break;
            }
            if (!References(compare, mismatch.MetadataToken))
                yield return "L114 compare-ignores-version: ParityComparer.Compare never calls " +
                             "MultiplayerVersionMismatch, so the version rides the JOIN and is thrown away. " +
                             "Every arm above stays green while a cross-version join sails through.";
            if (!CallsAcrossAssemblies(onAccept, build) || !CallsAcrossAssemblies(onAccept, noticeSetter))
                yield return "L114 accept-skips-check: SessionManager.HandleConnectionAccepted does not " +
                             "compute VersionNoticeForClient into VersionMismatchNotice. The accept is the " +
                             "FIRST moment the client holds both versions and the last one before the lobby " +
                             "opens; checked anywhere later, the player has already invested the time.";
            if (!CallsAcrossAssemblies(uiUpdate, noticeGetter))
                yield return "L114 notice-never-shown: MultiplayerUI.Update never reads " +
                             "SessionManager.VersionMismatchNotice, so the mismatch is computed at the door " +
                             "and silently dropped — the client walks into the lobby, finds READY dead, and " +
                             "is told nothing.";
        }

        /// <summary>Does <paramref name="caller"/> CALL <paramref name="callee"/>? Resolves the operand at
        /// real call/callvirt sites through the caller's module (a MemberRef token never equals the
        /// callee's own def token). Lenient: a byte that only looks like an opcode fails to resolve and is
        /// skipped. Same helper, same trade, as L108.</summary>
        private static bool CallsAcrossAssemblies(MethodBase caller, MethodBase callee)
        {
            byte[] il = null;
            try { il = caller.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            var module = caller.Module;
            for (int i = 0; i + 5 <= il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue; // call / callvirt
                try
                {
                    var m = module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                    if (m != null && m.MetadataToken == callee.MetadataToken && m.Module == callee.Module)
                        return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Does <paramref name="m"/>'s IL mention that metadata token? Raw 4-byte scan — see the
        /// ponytail note on the class. Same-assembly members only.</summary>
        private static bool References(MethodBase m, int token)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
