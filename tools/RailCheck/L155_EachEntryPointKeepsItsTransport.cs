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
    /// WHAT THIS LAW CANNOT SEE, said plainly so a green L155 is not read as "join works". Its arms (a)
    /// through (e) execute a
    /// PURE decision function over fabricated targets. It does not connect, does not know whether STUN
    /// discovery succeeded, whether the host's endpoint is reachable, or whether the Steam invite path
    /// resolves a lobby — all runtime, all needing the game. It also cannot check that the two CALLERS pass
    /// the origin they should (<c>MultiplayerUI</c> is a MonoBehaviour; the two call sites are the Steam
    /// invite callback in <c>WireSteamInvite</c> and the gate's join screen, <c>OnGateJoin</c> — where the
    /// paste box lives since the lobby's own <c>OnLobbyJoinPrompt</c> was deleted with its JOIN button).
    /// What makes
    /// that survivable is that <c>origin</c> is a REQUIRED parameter with no default: a new entry point
    /// cannot inherit the wrong technology by omission, it has to state one and fails to compile otherwise.
    /// Arm (f) closes one corner of that caller-side gap — by IL, not by behaviour — for the one entry
    /// point whose affordance the player sees before any plan exists: the INVITE VIA STEAM button.
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
    ///   (e) <c>short-code-lost-its-tcp-leg</c> — an 11-symbol ConnectCode (<c>JoinKind.StunCode</c>) yields
    ///       [DirectIP, StunUDP] in that order, under BOTH origins. It names an ENDPOINT, and TCP-to-it
    ///       then punch-to-it is one technology, so this does not weaken (a)/(c). Until 2026-08-10 this arm
    ///       of <c>Build</c> emitted the punch ALONE — no TCP leg at all, so every pasted short code ran the
    ///       save transfer over best-effort UDP even on a LAN, which is the ZeroTier truncation described
    ///       above with no fallback to lose. It matters more now than it did: the lobby's published code is
    ///       a ConnectCode and nothing else (<c>MultiplayerUI.GetSessionInviteCode</c>), so this arm is the
    ///       ONLY plan a code-joiner ever gets. Guarded: the arm first asserts <c>JoinTarget.Stun</c> still
    ///       classifies as <c>StunCode</c>, because re-routing that classification would leave the
    ///       assertions passing against a branch nobody meant to test.
    ///   (f) <c>steam-affordance-ungated</c> — the OTHER half of entry point #1, and the only arm that
    ///       reads IL instead of executing Build: <c>LobbyPanel.Refresh</c> → <c>SteamProbe.IsAlive</c> →
    ///       <c>SteamInvite.IsSteamAlive</c>, both hops, so INVITE VIA STEAM is greyed by the SAME
    ///       predicate that tells Build whether a Steam leg may exist. (Two hops because the UI may not
    ///       touch SteamInvite directly — its <c>Lobby?</c> statics make the type unloadable where
    ///       Facepunch is absent, so SteamProbe holds the catch.) Every other arm asks whether the plan
    ///       names the right technology; this one asks whether the button that starts that technology is
    ///       offered when it cannot work. RUNS FIRST, sharing no guard with the arms below. Guarded
    ///       twice itself — every member must resolve, and the IL walk must reach <c>RefreshControls</c>,
    ///       the call that CLOSES Refresh, so a reader that died early reports itself instead of
    ///       accusing the gate.
    ///   (g) <c>entry-point-names-steaminvite</c> — the entry points must not be able to DIE on a machine
    ///       with no Steam at all. <c>SteamInvite</c> holds <c>Lobby?</c> statics, so where Facepunch is
    ///       absent (GOG / Epic) the type cannot load and the JIT fails while COMPILING any method that
    ///       names it — surfacing at that method's CALL site, one frame up and outside its own try. On
    ///       2026-08-10 <c>StartHostAndOpenLobby</c> still named <c>SteamInvite.HostPublish</c> in its own
    ///       body, so on every non-Steam install CREATE SESSION threw before <c>NetworkEngine.Create()</c>
    ///       ran: hosting was dead, and not one line of it was Steam's business.
    ///       <c>OnGateJoinFriend</c> and the invite-overlay button had the same shape. So the rule, pinned
    ///       here by IL: NO method of <c>MultiplayerUI</c> may reference a <c>SteamInvite</c> member except
    ///       <c>WireSteamInviteCore</c>, the one NoInlining core written to be the isolated fault (its
    ///       caller <c>WireSteamInvite</c> holds the catch). Everything else goes through
    ///       <c>SteamProbe</c>. Guarded by its own positive control — WireSteamInviteCore must still be
    ///       seen naming SteamInvite, or the IL reader found nothing and the sweep is vacuous. Its one
    ///       blind spot, stated: <c>Program.Callees</c> reads method references, so a body that touches
    ///       only a SteamInvite FIELD would not be seen.
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
    /// the StunCode arm, or put it after the StunUDP one → (e); hard-code the lobby's Steam gate to true,
    /// or swap it for a second predicate of its own → (f).
    /// </summary>
    internal static class L155_EachEntryPointKeepsItsTransport
    {
        // A unified code with EVERYTHING in it — the only shape where the entry point can change the plan.
        private const ulong HostSteamId = 76561198000000123UL;
        private const string HostIp = "203.0.113.7";
        private const int HostPort = 40000;

        internal static IEnumerable<string> Check()
        {
            // ARM (f) RUNS FIRST, BEFORE ANY OTHER GUARD. Both guards below (Build's signature, and
            // JoinTarget.Stun's kind) end this method with `yield break`, so from behind them arm (f)
            // would vanish silently whenever an unrelated premise moved — the report would name Build or
            // StunCode and say nothing at all about the Steam affordance, which is the failure mode the
            // whole "an unguarded arm passes while checking nothing" rule exists to stop. It shares no
            // premise with them, so it has no business sharing their exit.
            foreach (var v in SteamAffordanceGated()) yield return v;

            // ARM (g), for the same reason: it shares no premise with Build's signature or StunCode's kind,
            // so it must not share their exit either. It is also the one arm about the mod being ABLE TO RUN
            // rather than about which technology it picks.
            foreach (var v in EntryPointsKeepSteamOutOfTheirBodies()) yield return v;

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
                             "other branch of Build. The 11-symbol ConnectCode is the whole free-services " +
                             "join path — re-point this arm at whatever kind it classifies as now.";
                yield break;
            }
            foreach (var origin in new[] { JoinOrigin.PastedCode, JoinOrigin.SteamInvite })
                foreach (var v in Expect("short-code-lost-its-tcp-leg", stunTarget, true, origin,
                         new[] { TransportType.DirectIP, TransportType.StunUDP },
                         "An 11-symbol code names an ENDPOINT, and both legs to that endpoint are the same " +
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

        /// <summary>
        /// ARM (f) — THE INVITE BUTTON IS GATED ON THE SAME SIGNAL AS THE INVITE'S TRANSPORT.
        ///
        /// Entry point #1 in this law's list is "the lobby's INVITE VIA STEAM button → Steam P2P", and
        /// every other arm checks the second half of that pair (does the PLAN name Steam) while nothing
        /// checked the first (may the player press it at all). The failure that leaves is silent and
        /// entirely visual: with Steam not running the button still draws fully live, so the player
        /// presses the one control the card offers for getting a friend in, and gets a message box
        /// instead of an overlay — while the join leg for that same technology was correctly refused all
        /// along, because JoinPlan.Build is handed steamAlive and arm (b) proves it obeys it. ONE notion
        /// of "Steam is alive" for the button that starts an invite and the leg that finishes one.
        ///
        /// IL, NOT BEHAVIOUR, and that is a real limit worth stating: this asserts that the per-frame
        /// pass REACHES the liveness predicate, not that the boolean is applied the right way round. A
        /// gate wired backwards would pass here and be obvious in one glance at the lobby. What it does
        /// stop is the regression that is NOT obvious — the gate quietly becoming a constant (hard-coded
        /// true, or a second predicate invented beside it), which looks fine in every screenshot taken on
        /// a machine that has Steam running, i.e. every developer's.
        ///
        /// GUARDED: every member must resolve, AND the walk over Refresh must reach RefreshControls, the
        /// call that closes that method. "Has any callee at all" was too weak to be the guard it claimed
        /// to be — see the comment on that check.
        /// </summary>
        private static IEnumerable<string> SteamAffordanceGated()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

            var mod = typeof(JoinPlan).Assembly;
            var panel = mod.GetType("Multiplayer.UI.LobbyPanel");
            var refresh = panel?.GetMethod("Refresh", All);
            var probe = mod.GetType("Multiplayer.Transport.SteamProbe");
            var probeIsAlive = probe?.GetMethod("IsAlive", All);
            var isSteamAlive = typeof(SteamInvite).GetMethod("IsSteamAlive", All);
            // A callee the lobby's per-frame pass reaches LATE — the anti-vacuity landmark, see below.
            var refreshControls = panel?.GetMethod("RefreshControls", All);

            if (panel == null || refresh == null || probe == null || probeIsAlive == null ||
                isSteamAlive == null || refreshControls == null)
            {
                yield return "L155 premise-changed: LobbyPanel.{Refresh,RefreshControls}, " +
                             "SteamProbe.IsAlive or SteamInvite.IsSteamAlive no longer resolves, so " +
                             "nothing checks that the lobby's Steam entry point is offered only when " +
                             "there is a Steam to invite through. Re-point this arm at whatever the " +
                             "lobby's per-frame pass and the Steam-liveness predicate are called now — " +
                             "do not delete it.";
                yield break;
            }

            // ANTI-VACUITY, AND IT HAS TO BE A LATE LANDMARK RATHER THAN "ANY CALLEE AT ALL". The IL
            // reader abandons a method on the first opcode it cannot decode (Program.CallSites yields
            // break, it does not throw), so a walk that died two instructions in still reports plenty of
            // callees while missing everything after them — the reachability answer would then be a FALSE
            // RED blaming the gate for a broken scanner. RefreshControls is called at the very END of
            // Refresh, so seeing it proves the walk reached the bottom of the method.
            if (!Program.Callees(refresh, mod).Any(c => c.MetadataToken == refreshControls.MetadataToken &&
                                                        c.Module == refreshControls.Module))
            {
                yield return "L155 premise-changed: the IL walk over LobbyPanel.Refresh never reached " +
                             "RefreshControls, the call that closes that method — so the scan stopped " +
                             "early (an opcode it cannot decode) and every reachability answer below " +
                             "would accuse the gate of something the reader simply did not see. Fix the " +
                             "reader, or pick a new landmark at the end of Refresh.";
                yield break;
            }

            // TWO HOPS, because the probe is deliberately a separate type: LobbyPanel cannot call
            // SteamInvite directly (its Lobby? statics make the type unloadable where Facepunch is
            // absent, which is why SteamProbe exists at all), so the chain to pin is
            // Refresh → SteamProbe.IsAlive → SteamInvite.IsSteamAlive. Checking only the first hop would
            // let SteamProbe.IsAlive become `return true`; only the second would let the lobby stop
            // asking. The walk is scoped to the owning type at each hop.
            if (!Reaches(refresh, probeIsAlive, mod, panel))
                yield return "L155 steam-affordance-ungated: LobbyPanel.Refresh no longer reaches " +
                             "SteamProbe.IsAlive, so INVITE VIA STEAM is drawn the same whether or " +
                             "not Steam is running. It is the one control on the session card for getting " +
                             "a friend in, so a player with Steam down presses a button that looks live " +
                             "and gets a message box — while the JOIN leg of that same technology is " +
                             "refused correctly, because Build is handed the same signal (arm (b)). The " +
                             "gate must stay the SAME predicate, not a hard-coded true and not a second " +
                             "opinion written beside it: two notions of 'Steam is alive' drift, and the " +
                             "one that drifts is always the one nobody can see failing.";

            if (!Reaches(probeIsAlive, isSteamAlive, mod, probe))
                yield return "L155 steam-affordance-ungated: SteamProbe.IsAlive no longer reaches " +
                             "SteamInvite.IsSteamAlive, so the probe every UI surface trusts has stopped " +
                             "asking Steam anything. Its whole job is to be the ONE guarded answer to " +
                             "'is Steam running' — a constant here greys nothing (or greys everything) " +
                             "while the lobby, the join screen and JoinPlan all keep believing they share " +
                             "a signal with each other.";
        }

        /// <summary>
        /// ARM (g) — THE JIT BOUNDARY IS WHERE THE MOD SAYS IT IS.
        ///
        /// See the class summary. One sweep over every method MultiplayerUI declares (nested closures
        /// included, because a click handler is a lambda): the only one allowed to reference a SteamInvite
        /// member is WireSteamInviteCore, which exists to be that fault and is called from inside a try.
        /// Anything else naming the type is a method that cannot be COMPILED on a GOG/Epic box, and the
        /// throw lands at its caller where nothing of ours is waiting for it.
        /// </summary>
        private static IEnumerable<string> EntryPointsKeepSteamOutOfTheirBodies()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

            var mod = typeof(JoinPlan).Assembly;
            var ui = mod.GetType("Multiplayer.UI.MultiplayerUI");
            var core = ui?.GetMethod("WireSteamInviteCore", All);
            if (ui == null || core == null)
            {
                yield return "L155 premise-changed: MultiplayerUI or its WireSteamInviteCore no longer " +
                             "resolves, so nothing checks that the Steam glue stays behind the one " +
                             "NoInlining core that is allowed to name SteamInvite. Re-point this arm at " +
                             "whatever the isolated core is called now — do not delete it: without it a " +
                             "single SteamInvite reference in a UI method kills that whole entry point on " +
                             "every non-Steam install, and never on a developer's machine.";
                yield break;
            }

            var bodies = new List<MethodBase>();
            foreach (var t in new[] { ui }.Concat(ui.GetNestedTypes(All)))
                bodies.AddRange(t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)));

            bool coreStillNamesIt = false;
            foreach (var m in bodies)
            {
                List<MethodBase> calls;
                try { calls = Program.Callees(m, mod).ToList(); } catch { continue; }
                if (!calls.Any(c => c.DeclaringType == typeof(SteamInvite))) continue;
                if (m.MetadataToken == core.MetadataToken && m.Module == core.Module)
                { coreStillNamesIt = true; continue; }

                yield return "L155 entry-point-names-steaminvite: " + m.DeclaringType.Name + "." + m.Name +
                             " references a SteamInvite member in its own body. SteamInvite's Lobby? " +
                             "statics make the type unloadable where Facepunch is absent, and the JIT fails " +
                             "while COMPILING this method — the throw surfaces at its CALL site, one frame " +
                             "up, outside any try this method holds. That is not a lost Steam feature, it " +
                             "is the whole method dying: StartHostAndOpenLobby named HostPublish here and " +
                             "CREATE SESSION did nothing at all for every GOG/Epic player, because " +
                             "NetworkEngine.Create() on the first line never ran. Route it through a " +
                             "SteamProbe NoInlining shim (SteamInvite.cs:393-408), or put it in " +
                             "WireSteamInviteCore, whose caller holds the catch.";
            }

            if (!coreStillNamesIt)
                yield return "L155 sweep-is-vacuous: NO method of MultiplayerUI was seen referencing " +
                             "SteamInvite at all — not even WireSteamInviteCore, which exists to. Either " +
                             "the IL reader found nothing (in which case every verdict above is trivially " +
                             "green and this arm guards nothing), or the wiring core was renamed and this " +
                             "arm needs re-pointing at it.";
        }

        /// <summary>Does <paramref name="from"/> reach <paramref name="target"/>, walking only through
        /// methods declared on <paramref name="within"/> (or its compiler-generated nested closures — a
        /// per-frame gate can live behind a small private helper, and a click behind a lambda)? Breadth
        /// first over Program.Callees, the same IL reader L403 uses on this very type.</summary>
        private static bool Reaches(MethodBase from, MethodBase target, Assembly mod, Type within)
        {
            var seen = new HashSet<int>();
            var queue = new Queue<MethodBase>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                if (!seen.Add(m.MetadataToken)) continue;

                foreach (var callee in Program.Callees(m, mod))
                {
                    if (callee.MetadataToken == target.MetadataToken && callee.Module == target.Module)
                        return true;
                    var owner = callee.DeclaringType;
                    if (owner != null && (owner == within || owner.DeclaringType == within))
                        queue.Enqueue(callee);
                }
            }
            return false;
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
