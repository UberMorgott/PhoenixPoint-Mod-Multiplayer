using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Parity;

namespace RailCheck
{
    /// <summary>
    /// L108 — TWO PHOENIX POINT BUILDS MUST NOT REACH ONE CAMPAIGN.
    ///
    /// Mandate law 10 makes parity BLOCKING because the save-graph shape has to match on every peer. The
    /// manifest carried the mod set, the DLC set and per-mod settings — everything EXCEPT the game itself,
    /// while the only thing standing in for a build gate was <c>MultiplayerUI.CoopGuardBlocks</c>, which
    /// returned false unconditionally and sat at two call sites that run BEFORE any peer exists (start
    /// hosting / parse a join code), so it had no second version to compare and never could have. A peer a
    /// Steam update ahead therefore joined clean: the join rides the native save loader (mandate L6) across
    /// a boundary the game keys its own saves on (<c>SavegameMetaData.BuildRevisionNumber</c>), defs and
    /// fields move under the diff rail, and the campaign diverges with nothing said — the silent failure
    /// this repo's whole verification posture exists to refuse.
    ///
    /// EXECUTED, not inspected — every type on this path is pure:
    ///   (a) two peers on the SAME build produce no diff. Blocking a bad join is the point; blocking a good
    ///       one is a bug, and this arm is what stops the gate being "always red";
    ///   (b) two peers on DIFFERENT builds produce a diff that NAMES BOTH builds — a mismatch a player
    ///       cannot read is a mismatch they cannot fix;
    ///   (c) an UNKNOWN build on either side does NOT diff. "" means the other peer's Multiplayer build
    ///       predates the field, which its own ModRef version already diffs — refusing twice for one cause
    ///       would reject peers whose install is fine;
    ///   (d) the version survives the JOIN wire, and a manifest WITHOUT the trailing field still decodes
    ///       (as unknown) instead of throwing at a peer whose only problem is an old mod;
    ///   (e) the seams: the collector must read the GAME's own <c>RuntimeBuildInfo.BuildVersion</c> rather
    ///       than a string this mod invents, and <c>Compare</c> must actually read the field.
    /// ponytail: (e) scans raw IL for the callee's metadata token, same lenient trade as L105-L107.
    /// </summary>
    internal static class L108_GameBuildParity
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private static ParityManifest Peer(string gameVersion) => ParityManifest.Build(
            new[] { "DLC1" },
            new[] { ("Morgott.Multiplayer", "0.9.0") },
            null,
            gameVersion);

        internal static IEnumerable<string> Check()
        {
            foreach (var violation in SubjectGuard(new[] { typeof(ParityComparer) }))
                yield return violation;
            foreach (var violation in PositiveControls(typeof(ParityComparer)))
                yield return violation;

            const string A = "1.30.2.1234";
            const string B = "1.30.2.9999";

            // ── (a) same build, same mods → a legitimate peer is not rejected ──
            var same = ParityComparer.Compare(Peer(A), Peer(A));
            if (same.Count != 0)
                yield return "L108 good-join-blocked: two peers on the SAME build and mod set produced " +
                             same.Count + " parity diff(s) (" + ParityComparer.Format(same).Replace("\n", " | ") +
                             "). A gate that refuses a matching install locks every player out of co-op, which " +
                             "is a worse failure than the desync it guards against.";

            // ── (b) different builds → blocked, and legible ────────────────
            var crossed = ParityComparer.Compare(Peer(A), Peer(B));
            var text = ParityComparer.Format(crossed);
            if (crossed.Count == 0)
                yield return "L108 build-mismatch-passes: a host on Phoenix Point " + A + " and a client on " +
                             B + " produce NO parity diff, so the client readies up and the campaign starts " +
                             "across two different game builds. Join rides the native save loader across a " +
                             "boundary the game keys saves on, and the divergence that follows is silent.";
            else if (!text.Contains(A) || !text.Contains(B))
                yield return "L108 build-mismatch-unreadable: the parity diff for a build mismatch does not name " +
                             "both versions (got: " + text.Replace("\n", " | ") + "). The roster badge is the " +
                             "only place a player learns why READY is locked; without both numbers there is " +
                             "nothing to act on.";
            if (ParityComparer.ReadyAllowed(text))
                yield return "L108 mismatch-can-ready: a build mismatch leaves ReadyAllowed true, so the soft " +
                             "gate never engages and the mismatch is a warning nobody is stopped by.";

            // ── (c) unknown on either side must not reject ─────────────────
            foreach (var pair in new[] { ("", A), (A, ""), ("", "") })
                if (ParityComparer.Compare(Peer(pair.Item1), Peer(pair.Item2)).Count != 0)
                    yield return "L108 unknown-build-blocked: a manifest with an UNKNOWN game version (host='" +
                                 pair.Item1 + "' client='" + pair.Item2 + "') was refused. Unknown means the peer's " +
                                 "Multiplayer build predates this field — already diffed by its mod version — or " +
                                 "that RuntimeBuildInfo threw on OUR side; neither is evidence the other install " +
                                 "is wrong, and rejecting on it locks out a peer who is fine.";

            // ── (d) the wire, including the pre-field shape ────────────────
            var bytes = MessageSerializer.SerializeParityManifest(Peer(A));
            var back = MessageSerializer.DeserializeParityManifest(bytes);
            if (back == null || back.GameVersion != A)
                yield return "L108 wire-drops-build: the game version does not survive the JOIN manifest round " +
                             "trip (got '" + (back == null ? "<null>" : back.GameVersion) + "'). Both peers then " +
                             "read each other as unknown and arm (c) turns the whole gate off silently.";
            var legacy = Peer("");
            var legacyBytes = MessageSerializer.SerializeParityManifest(legacy);
            var truncated = legacyBytes.Take(legacyBytes.Length - 1).ToArray(); // the manifest as an older build sent it
            ParityManifest old = null;
            string threw = null;
            try { old = MessageSerializer.DeserializeParityManifest(truncated); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw != null || old == null || old.GameVersion != "")
                yield return "L108 legacy-manifest-rejected: a manifest without the trailing build field — what a " +
                             "peer running an older Multiplayer sends — does not decode as UNKNOWN (" +
                             (threw ?? "GameVersion='" + (old == null ? "<null>" : old.GameVersion) + "'") + "). " +
                             "The host then reports 'manifest missing' instead of the mod-version mismatch that " +
                             "is actually wrong, and the player is told the wrong thing to fix.";

            // ── (e) the seams ─────────────────────────────────────────────
            var collector = Type.GetType("Multiplayer.Network.ParityManifestCollector, Multiplayer");
            var collect = collector == null ? null : collector.GetMethod("Collect", All);
            var buildInfo = Type.GetType("Base.Build.RuntimeBuildInfo, Assembly-CSharp");
            var buildVersion = buildInfo == null ? null : buildInfo.GetProperty("BuildVersion", All)?.GetGetMethod();
            var compare = typeof(ParityComparer).GetMethod("Compare", All);
            var field = typeof(ParityManifest).GetField("GameVersion", All);
            if (collect == null || buildVersion == null || compare == null || field == null)
            {
                yield return "L108 seam-gone: ParityManifestCollector.Collect, RuntimeBuildInfo.BuildVersion, " +
                             "ParityComparer.Compare or ParityManifest.GameVersion no longer exists — the arms " +
                             "above are exercising a gate that is wired to nothing.";
                yield break;
            }
            if (!CallsAcrossAssemblies(collect, buildVersion))
                yield return "L108 build-invented: ParityManifestCollector.Collect does not read " +
                             "Base.Build.RuntimeBuildInfo.BuildVersion. The identity peers compare must be the " +
                             "GAME's own — the same string the main menu shows and saves are keyed on — not a " +
                             "constant this mod ships, which would be equal on two different game builds.";
            if (!References(compare, field.MetadataToken))
                yield return "L108 compare-ignores-build: ParityComparer.Compare never reads " +
                             "ParityManifest.GameVersion, so the field rides the wire and is thrown away. Every " +
                             "arm above would still be green while a cross-build join sails through.";
        }

        private static IEnumerable<string> SubjectGuard(Type[] subjects)
        {
            if (subjects == null || subjects.Length == 0)
                yield return "L108 premise-changed: an empty subject set was accepted, so game-build parity can pass without inspecting ParityComparer.";
            else if (Array.Exists(subjects, t => t == null))
                yield return "L108 premise-changed: an unresolved subject was accepted, so a missing parity comparer can make the law vacuous.";
        }

        private static IEnumerable<string> PositiveControls(Type subject)
        {
            if (!HasViolation(SubjectGuard(new Type[0])))
                yield return "L108 control-empty-subject: the executable subject guard did not reject an empty set.";
            if (!HasViolation(SubjectGuard(new Type[] { null })))
                yield return "L108 control-unresolved-subject: the executable subject guard did not reject an unresolved type.";
            if (HasViolation(SubjectGuard(new[] { subject })))
                yield return "L108 control-valid-subject: ParityComparer was rejected by the subject guard, so the executable checks never reached production code.";
        }

        private static bool HasViolation(IEnumerable<string> violations) => violations.GetEnumerator().MoveNext();

        /// <summary>Does <paramref name="caller"/> CALL <paramref name="callee"/> in another assembly? The
        /// raw-token scan L105-L107 use cannot answer this: a cross-assembly call site carries a MemberRef
        /// token minted in the CALLER's module, which never equals the callee's own def token. So the
        /// operand is resolved through the caller's module at real <c>call</c>/<c>callvirt</c> opcodes.
        /// Lenient by construction — a byte that only looks like an opcode fails to resolve and is skipped.</summary>
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
        /// ponytail note on the class. Same-assembly members only (see <see cref="CallsAcrossAssemblies"/>).</summary>
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
