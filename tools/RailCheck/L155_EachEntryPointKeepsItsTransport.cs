using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Multiplayer.Transport;
using Multiplayer.Util;
// System.Net carries its own TransportType; this law only ever means the rail's one.
using TransportType = Multiplayer.Transport.TransportType;

namespace RailCheck
{
    /// <summary>
    /// L155 — EACH WAY OF JOINING KEEPS THE TRANSPORT IT NAMES; A PASTED CODE NEVER SILENTLY TRIES STEAM.
    ///
    /// THE REPORT: the owner pressed "copy code" in the lobby, handed the code to the other player, and the
    /// session came up over STEAM anyway. Not a wrong connection — a wrong TECHNOLOGY, chosen behind the
    /// user's back by <c>JoinPlan.Build</c>'s old Unified branch, which put a Steam P2P attempt in FRONT of
    /// the STUN one for every unified code regardless of how that code reached the client.
    ///
    /// THE DECISION THIS LAW PINS (the repo owner's, made 2026-08-07). Each entry point is pinned to exactly
    /// one technology, with no cascading ACROSS technologies:
    ///   • the lobby's "Invite via Steam" button       → Steam P2P
    ///   • a pasted INVITE CODE (the GOG / Epic route) → plain Direct TCP to the endpoint, then the STUN
    ///     hole-punch to the SAME endpoint — that is one technology with and without the punch, not a
    ///     second one. TCP FIRST as of 2026-08-07, and the order is load-bearing rather than cosmetic:
    ///     STUN's "reliable" send is a duplicated UDP datagram with no sequencing, ACK or retransmit
    ///     (<c>StunTransport.Send</c>), so a session that lands on it carries the save transfer's 32 KB
    ///     IP-fragmenting chunks on a best-effort link — one lost fragment fails the transfer outright
    ///     (<c>SaveTransferCoordinator</c> warns exactly this at LaunchTransfer). Loopback never shows it;
    ///     the first real path did, over ZeroTier, where STUN won the race to a host that was directly
    ///     reachable all along and the client's save arrived 131072 of 169071 bytes. Order these by what
    ///     the link can CARRY, not by which one connects first.
    ///   • a DIRECTLY REACHABLE host, addressed by IP or by name → Direct TCP. "Directly reachable" is the
    ///     broad case and NOT the LAN one: LAN, virtual LAN (Hamachi/ZeroTier and the like), a public white
    ///     IP, a forwarding domain, a port-forwarded or UPnP-mapped host (<c>UpnpPortMapper.TryMap</c>) are
    ///     all it, and <c>JoinKind.DirectIp</c> and <c>JoinKind.DirectHost</c> both already map to
    ///     <c>TransportType.DirectIP</c>. A reader who takes this as LAN-only will narrow arm (c) wrongly.
    /// The one exception preserved: a pasted BARE Steam id (<c>JoinKind.SteamId</c>) is an explicit request
    /// for Steam and stays pinned to Steam.
    ///
    /// WHY THE OLD FALLBACK IS NOT WORTH KEEPING, stated so nobody restores it as an "optimisation". Its
    /// claimed value is that a Steam user who pasted a code gets the faster, NAT-free leg for free. But a
    /// user who HAS Steam presses the Steam button; a user who does NOT can never succeed on the Steam leg,
    /// so for them the leg is pure cost — one full connect timeout in front of the attempt they actually
    /// needed, with the connecting box naming a platform they do not own. It is self-consistent from the
    /// other side as well: a GOG/Epic HOST has no Steam lobby, so the code it mints carries no steam id at
    /// all, and the Steam leg it would have driven does not exist. The steam id STAYS in the code and stays
    /// used — the Steam invite path still reads it — so nothing about the code FORMAT or the wire changed
    /// here. Only the plan did.
    ///
    /// WHAT THIS LAW CANNOT SEE, said plainly so a green L155 is not read as "join works". It executes a
    /// PURE decision function over fabricated targets. It does not connect, does not know whether STUN
    /// discovery succeeded, whether the host's endpoint is reachable, or whether the Steam invite path
    /// resolves a lobby — all runtime, all needing the game. It also cannot check that the two CALLERS pass
    /// the origin they should (<c>MultiplayerUI</c> is a MonoBehaviour; the two call sites are the Steam
    /// invite callback in <c>WireSteamInvite</c> and the gate's join screen, <c>OnGateJoin</c> — where the
    /// paste box lives since the lobby's own <c>OnLobbyJoinPrompt</c> was deleted with its JOIN button).
    /// What makes
    /// that survivable is that <c>origin</c> is a REQUIRED parameter with no default: a new entry point
    /// cannot inherit the wrong technology by omission, it has to state one and fails to compile otherwise.
    ///
    /// THE ARMS, all EXECUTED against the real <c>JoinPlan.Build</c>:
    ///   (a) <c>pasted-code-tries-steam</c> — a unified code carrying BOTH a steam id and an endpoint, with
    ///       <c>JoinOrigin.PastedCode</c> and Steam ALIVE (the hostile case: everything the old branch
    ///       needed to insert its Steam leg is present), must yield exactly [DirectIP, StunUDP]. Steam alive
    ///       is the point — asserting this with Steam dead would pass against the OLD code too.
    ///   (b) <c>invite-order-changed</c> — the SAME target with <c>JoinOrigin.SteamInvite</c> must still
    ///       yield [SteamP2P, DirectIP, StunUDP], and with Steam DEAD must drop to [DirectIP, StunUDP].
    ///       The invite path was explicitly not to change; a fix that pinned everything off Steam would
    ///       satisfy (a) and (c) and break the only path Steam users have.
    ///   (c) <c>legacy-kind-unpinned</c> — each single-TECHNOLOGY kind (an IP, a hostname, a bare Steam id)
    ///       yields exactly ONE attempt of its own transport, under BOTH origins. The origin is about a
    ///       unified code only; a legacy kind that started varying with it would mean a pasted bare Steam
    ///       id had lost its Steam attempt. The short code is NOT in this table — see (e).
    ///   (e) <c>short-code-lost-its-tcp-leg</c> — a 10-symbol ConnectCode (<c>JoinKind.StunCode</c>) yields
    ///       [DirectIP, StunUDP] in that order, under BOTH origins. It names an ENDPOINT, and TCP-to-it
    ///       then punch-to-it is one technology, so this does not weaken (a)/(c). Until 2026-08-10 this arm
    ///       of <c>Build</c> emitted the punch ALONE — no TCP leg at all, so every pasted short code ran the
    ///       save transfer over best-effort UDP even on a LAN, which is the ZeroTier truncation described
    ///       above with no fallback to lose. It matters more now than it did: the lobby's published code is
    ///       a ConnectCode and nothing else (<c>MultiplayerUI.GetSessionInviteCode</c>), so this arm is the
    ///       ONLY plan a code-joiner ever gets. Guarded: the arm first asserts <c>JoinTarget.Stun</c> still
    ///       classifies as <c>StunCode</c>, because re-routing that classification would leave the
    ///       assertions passing against a branch nobody meant to test.
    ///   (d) POSITIVE CONTROL. A vacuous sweep is the likeliest silent regression here and it is cheap:
    ///       delete the SteamP2P line entirely and (a) and (c)'s direct/stun cases stay green forever. So
    ///       the law asserts that <c>Build</c> DOES emit SteamP2P where it must — arm (b)'s first attempt
    ///       and the legacy <c>SteamId</c> kind — and that the plans in (a) and (b) actually DIFFER, which
    ///       is the whole content of the change. Plus a signature guard: <c>Build</c> must have exactly one
    ///       overload and it must take <c>JoinOrigin</c>, because re-adding a two-argument overload (or
    ///       defaulting the origin) is how a future call site quietly gets Steam back.
    ///
    /// Falsify: drop the <c>origin == JoinOrigin.SteamInvite</c> clause from JoinPlan's Unified branch → (a);
    /// remove the Steam attempt outright → (b) and (d); make a legacy kind consult the origin → (c);
    /// give <c>Build</c> a defaulted origin or a compatibility overload → (d); delete the DirectIP line from
    /// the StunCode arm, or put it after the StunUDP one → (e).
    /// </summary>
    internal static class L155_EachEntryPointKeepsItsTransport
    {
        // A unified code with EVERYTHING in it — the only shape where the entry point can change the plan.
        private const ulong HostSteamId = 76561198000000123UL;
        private const string HostIp = "203.0.113.7";
        private const int HostPort = 40000;

        internal static IEnumerable<string> Check()
        {
            var builds = typeof(JoinPlan).GetMethods(BindingFlags.Public | BindingFlags.Static |
                                                     BindingFlags.DeclaredOnly)
                                         .Where(m => m.Name == "Build").ToList();
            if (builds.Count != 1 ||
                builds[0].GetParameters().Length != 3 ||
                builds[0].GetParameters()[2].ParameterType != typeof(JoinOrigin) ||
                builds[0].GetParameters()[2].IsOptional)
            {
                yield return "L155 premise-changed: JoinPlan.Build is no longer a single 3-argument method " +
                             "whose last parameter is a REQUIRED JoinOrigin (found " + builds.Count +
                             " overload(s)). The entry point is what pins the transport; the moment it can " +
                             "be omitted or defaulted, a new call site joins over whichever technology the " +
                             "default happens to name and nothing here or in the compiler says so — which " +
                             "is precisely the reported failure, a copied CODE connecting over Steam.";
                yield break;
            }

            var unified = JoinTarget.Unified(HostSteamId, HostIp, HostPort);

            // ── arm (a): the hostile case — steam id present, Steam ALIVE, code PASTED.
            foreach (var v in Expect("pasted-code-tries-steam", unified, true, JoinOrigin.PastedCode,
                     new[] { TransportType.DirectIP, TransportType.StunUDP },
                     "A pasted code is the entry point of a player with no Steam to be invited through. " +
                     "The Steam leg in front of it " +
                     "cannot succeed for the player who needed the paste box in the first place, so it only " +
                     "spends their connect timeout — and for the player who DOES have Steam it hijacks the " +
                     "code they were handed, which is the reported symptom verbatim."))
                yield return v;

            // ── arm (b): the invite path is untouched, both with and without local Steam.
            foreach (var v in Expect("invite-order-changed", unified, true, JoinOrigin.SteamInvite,
                     new[] { TransportType.SteamP2P, TransportType.DirectIP, TransportType.StunUDP },
                     "An accepted Steam invite IS the user pressing Steam. Nothing about this order was to " +
                     "change; if pinning the paste box also pinned the invite away from Steam, the fix has " +
                     "taken the fast NAT-free path away from the only users who can use it."))
                yield return v;
            foreach (var v in Expect("invite-order-changed", unified, false, JoinOrigin.SteamInvite,
                     new[] { TransportType.DirectIP, TransportType.StunUDP },
                     "With local Steam DEAD an invite has no Steam leg to take, and the pre-existing " +
                     "steamAlive guard is what keeps the plan from opening with an attempt that fails by " +
                     "construction. Losing it costs every non-Steam-runtime peer a timeout."))
                yield return v;

            // ── arm (c): the legacy single-format kinds are already pinned and must stay origin-blind.
            var legacy = new[]
            {
                Tuple.Create(JoinTarget.Direct(HostIp, HostPort), TransportType.DirectIP),
                Tuple.Create(JoinTarget.DirectHost("host.example.net", HostPort), TransportType.DirectIP),
                Tuple.Create(JoinTarget.Steam(HostSteamId), TransportType.SteamP2P),
            };
            foreach (var pair in legacy)
                foreach (var origin in new[] { JoinOrigin.PastedCode, JoinOrigin.SteamInvite })
                    foreach (var v in Expect("legacy-kind-unpinned", pair.Item1, true, origin,
                             new[] { pair.Item2 },
                             "A single-format target names its technology outright — an IP or a hostname is " +
                             "a directly reachable host (LAN, virtual LAN, white IP, forwarding domain) and " +
                             "so is Direct TCP, and a bare SteamID64 is an " +
                             "explicit request for Steam and keeps it even when PASTED. (The short code is " +
                             "an endpoint, not a technology, and is asserted separately in arm (e).) " +
                             "These three are the " +
                             "pinning the rest of " +
                             "the change was modelled on; making any of them read the origin would either " +
                             "add back a cross-technology attempt or strip the bare-Steam-id exception."))
                        yield return v;

            // ── arm (e): a SHORT CODE IS AN ENDPOINT AND GETS BOTH LEGS TO IT.
            // GUARD FIRST, because this arm is the one that can go quietly vacuous: it asserts what
            // JoinKind.StunCode produces, and the day ConnectCode is re-routed to classify as something
            // else (Unified, say) this target stops exercising the StunCode branch while every assertion
            // below keeps passing against a branch nobody meant to test.
            var stunTarget = JoinTarget.Stun(new IPEndPoint(IPAddress.Parse(HostIp), HostPort));
            if (stunTarget.Kind != JoinKind.StunCode)
            {
                yield return "L155 premise-changed: JoinTarget.Stun no longer yields JoinKind.StunCode " +
                             "(got " + stunTarget.Kind + "), so the short-code arm below is testing some " +
                             "other branch of Build. The 10-symbol ConnectCode is the whole free-services " +
                             "join path — re-point this arm at whatever kind it classifies as now.";
                yield break;
            }
            foreach (var origin in new[] { JoinOrigin.PastedCode, JoinOrigin.SteamInvite })
                foreach (var v in Expect("short-code-lost-its-tcp-leg", stunTarget, true, origin,
                         new[] { TransportType.DirectIP, TransportType.StunUDP },
                         "A 10-symbol code names an ENDPOINT, and both legs to that endpoint are the same " +
                         "technology — TCP to it, then the punch to it — so this is not the cross-technology " +
                         "cascade the rest of this law forbids. The punch ALONE is what this arm used to " +
                         "return, and that is a data-loss shape, not a preference: StunTransport.Send is a " +
                         "duplicated UDP datagram with no sequencing, ACK or retransmit, the save transfer's " +
                         "32 KB chunks IP-fragment on top of it, and one lost fragment fails the transfer " +
                         "outright — 2026-08-07 over ZeroTier, 131072 of 169071 bytes, against a host that " +
                         "was directly reachable the whole time. Order by what the link can CARRY. Also note " +
                         "the expectation is ORDER-sensitive: [StunUDP, DirectIP] is the same bug."))
                    yield return v;

            // ── arm (d), the POSITIVE CONTROL.
            foreach (var v in PositiveControl(unified)) yield return v;
        }

        /// <summary>ARM (d). Every assertion above is of the form "this transport is absent" or "this exact
        /// list came back" — and a Build that had simply LOST the ability to emit SteamP2P would satisfy the
        /// absence half forever while the presence half was never separately stated. So state it: Steam is
        /// really produced where it must be, and the two origins really disagree.</summary>
        private static IEnumerable<string> PositiveControl(JoinTarget unified)
        {
            var pasted = Run(unified, true, JoinOrigin.PastedCode);
            var invited = Run(unified, true, JoinOrigin.SteamInvite);
            var bareSteam = Run(JoinTarget.Steam(HostSteamId), true, JoinOrigin.PastedCode);

            if (!invited.Any(a => a.Transport == TransportType.SteamP2P) ||
                !bareSteam.Any(a => a.Transport == TransportType.SteamP2P))
                yield return "L155 sweep-is-vacuous: JoinPlan.Build never produced a SteamP2P attempt at all " +
                             "— not for an accepted invite, not for a bare Steam id. Every 'no Steam here' " +
                             "assertion in this law is then trivially true and asserts nothing, and Steam " +
                             "users have lost their entry point entirely rather than kept it pinned.";

            if (Describe(pasted) == Describe(invited))
                yield return "L155 sweep-is-vacuous: the pasted-code plan and the Steam-invite plan for the " +
                             "SAME unified code are identical (" + Describe(pasted) + "). The entire change " +
                             "is that these two differ; if they do not, the origin parameter is decorative " +
                             "and one of the two entry points is running on the other's technology.";

            if (invited.Count == 0 || pasted.Count == 0)
                yield return "L155 plan-degenerate: a unified code carrying BOTH a steam id and an endpoint " +
                             "produced an EMPTY plan (pasted=" + pasted.Count + ", invited=" + invited.Count +
                             "). An empty plan is the caller's 'that code carries no address' dead end, and " +
                             "reaching it for the richest code there is means the join box refuses codes it " +
                             "can plainly connect with.";
        }

        private static IEnumerable<string> Expect(string arm, JoinTarget target, bool steamAlive,
                                                  JoinOrigin origin, TransportType[] want, string why)
        {
            var got = Run(target, steamAlive, origin);
            if (Describe(got) == Describe(want)) yield break;

            yield return "L155 " + arm + ": JoinPlan.Build(" + target.Kind + ", steamAlive=" + steamAlive +
                         ", " + origin + ") = [" + Describe(got) + "], expected [" +
                         Describe(want) + "]. " + why;
        }

        private static List<JoinAttempt> Run(JoinTarget target, bool steamAlive, JoinOrigin origin)
            => JoinPlan.Build(target, steamAlive, origin) ?? new List<JoinAttempt>();

        private static string Describe(IEnumerable<JoinAttempt> plan)
            => string.Join(" → ", plan.Select(a => a.Transport.ToString()));

        private static string Describe(IEnumerable<TransportType> transports)
            => string.Join(" → ", transports.Select(t => t.ToString()));
    }
}
