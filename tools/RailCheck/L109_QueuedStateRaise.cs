using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Base.Core;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L109 — A NON-MODAL QUEUED WINDOW MUST END UP ENTERED ON THE PEER, AND MUST NOT WEDGE ITS QUEUE WHEN
    /// IT CANNOT.
    ///
    /// 0xB7 could only ever raise a <c>UIStateGeoModal</c>. <c>UIStateAssetDeployment</c> — "where does this
    /// newly manufactured vehicle / recruited soldier go" — is a plain <c>GeoscapeViewState</c>, so it was
    /// outside the surface entirely, and both of its raisers sit behind gates a client never passes
    /// (<c>Manufacture.Update()</c> inside <c>LevelHourlyUpdateCrt</c>, which <c>ClientSimGate</c> blocks
    /// WHOLE; <c>GeoPhoenixFaction.AddRecruit</c>:708). One screen got the prompt, and its <c>ExitState</c>:66
    /// is the ONLY way out of the queue slot it holds — so an idle host stopped every later window for
    /// itself and nobody else could answer for it (law 91). L48 was green through all of it: a LocalOnly
    /// declaration is a decision, not a defect, and nothing checked whether the decision was still true.
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME, EXECUTED wherever a live level is not needed:
    ///   (a) the DECLARATION — <c>UIStateAssetDeployment</c> must be declared <c>Mirrored</c>, or
    ///       <c>HostBroadcast</c> and <c>RaiseMirrored</c> both refuse and every arm below is inert;
    ///   (b) DESCRIBABLE — a real <c>GeoDeployAssetFactionCharacterBind</c> must come out as
    ///       <c>AssetDeploy</c>, never <c>Unsupported</c> (the one value that means "no window for the peer",
    ///       logged host-side and invisible to everyone else);
    ///   (c) the WIRE, including the <c>Kind</c> byte that IS the generic axis: a dropped kind is a peer
    ///       building a modal out of a deployment payload, and a dropped <c>Num</c> is the wrong title over
    ///       the right asset (NotEnoughSpace / Manufactured are two bits of the prompt's own text);
    ///   (d) the REFUSALS — an asset this peer cannot resolve, and a def it does not know, must each yield a
    ///       stated reason so nothing is queued at all. THIS IS THE NO-WEDGE HALF: a refused raise never
    ///       reaches <c>QueryStateSwitch</c>, so the peer's queue is untouched rather than holding a state
    ///       whose <c>EnterState</c> throws on a null bind;
    ///   (e) the OPTIONAL entity — a manufactured aircraft has no <c>GeoCharacter</c> at all, so an empty ref
    ///       must be ACCEPTED and must not make the raise wait for an entity nobody named
    ///       (<c>NamesEntity</c>), while a named one must wait. A shape that parks on nothing is a window
    ///       that expires instead of opening;
    ///   (f) BOUNDED when the asset never lands — the REAL <see cref="ModalParkQueue"/> driven with a real
    ///       AssetDeploy payload must expire it loudly within <see cref="ModalParkQueue.MaxBatches"/>. The
    ///       second half of no-wedge: waiting forever holds every later window behind it;
    ///   (g) ANSWERABLE — <c>WindowQueueSync.IdentityOf</c> must NAME the prompt (without an identity the
    ///       0xB9 deploy op can never match and the host's own window stays up forever) and must give
    ///       DIFFERENT strings for different prompts, since an identity that names only the KIND is the
    ///       2026-08-01 regression that had one peer's click dismissing another's unrelated window;
    ///   (h) the SEAMS, or every arm above is a pure function nobody calls: the coverage gate must broadcast,
    ///       <c>BuildData</c> must route the shape, <c>NeedsPark</c> must ask <c>NamesEntity</c>,
    ///       <c>RaiseMirrored</c> must actually construct a <c>UIStateAssetDeployment</c>, the deploy op must
    ///       be registered, the client seam must send an intent, and the HOST must answer through the game's
    ///       own <c>DeployAtSite</c> rather than a hand-rolled deploy.
    /// ponytail: (h) scans raw IL for the callee's metadata token, same lenient trade as L105/L106/L107.
    /// </summary>
    internal static class L109_QueuedStateRaise
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private const string AircraftGuid = "AIRCRAFT-GUID";
        private const string ItemGuid = "ITEM-GUID";

        internal static IEnumerable<string> Check()
        {
            // ── (a) the declaration the whole arm hangs on ─────────────────
            var rule = GeoWindowCoverage.RuleFor(typeof(UIStateAssetDeployment));
            if (rule == null || rule.Sync != WindowSync.Mirrored)
            {
                yield return "L109 undeclared: GeoWindowCoverage does not declare UIStateAssetDeployment " +
                             "Mirrored (" + (rule == null ? "undeclared" : rule.Sync.ToString()) + "). Both " +
                             "HostBroadcast and RaiseMirrored gate on that rule, so the prompt reaches ONE screen " +
                             "again and its queue slot is drainable only by the peer that got it (law 91).";
                yield break;
            }

            var bind = Bind(asset: NewChar(), aircraft: true, related: true, manufactured: true, spaceFull: false,
                            out string why);
            if (bind == null)
            {
                yield return "L109 sample-unbuildable: no GeoDeployAssetFactionCharacterBind could be made (" + why +
                             "), so nothing below ran and this window is unchecked.";
                yield break;
            }

            // ── (b) describable at all ─────────────────────────────────────
            var p = GeoModalMirror.Describe(bind);
            p.Kind = GeoModalMirror.StateKind.AssetDeployment;
            p.Priority = 0;
            if (p.Shape != GeoModalMirror.DataShape.AssetDeploy)
            {
                yield return "L109 undescribable: GeoModalMirror.Describe returns " + p.Shape + " for a " +
                             "GeoDeployAssetFactionCharacterBind. HostBroadcast then logs and returns, the host's " +
                             "own prompt opens exactly as always, and the peer gets NOTHING — the declaration says " +
                             "the opposite of what ships.";
                yield break;
            }
            if (p.Keys == null || p.Keys.Length != 2 || p.Keys[0] != AircraftGuid || p.Keys[1] != ItemGuid)
                yield return "L109 defs-unnamed: the payload does not carry {aircraftDefGuid, relatedItemDefGuid} " +
                             "in that order (got [" + string.Join(",", p.Keys ?? new string[0]) + "]). The peer " +
                             "rebuilds the bind positionally, so a reorder puts the aircraft's def in the item slot " +
                             "and the prompt draws the wrong art over the wrong asset.";
            if ((p.Num & 1) == 0)
                yield return "L109 flags-dropped: Manufactured did not survive into Raise.Num. The two flags are " +
                             "the prompt's own title (UIModuleGeoAssetDeployment:99-113 picks between \"new " +
                             "aircraft\" / \"not enough space\" / mutog / ground vehicle off them), so losing one " +
                             "is a window that says something the host's never said.";

            // ── (c) the wire, Kind byte first ──────────────────────────────
            var back = GeoModalMirror.Decode(GeoModalMirror.Encode(9u, p), out uint seq);
            if (seq != 9u || back.Kind != p.Kind || back.Shape != p.Shape || back.Ref != p.Ref ||
                back.Num != p.Num || back.Priority != p.Priority ||
                !(back.Keys ?? new string[0]).SequenceEqual(p.Keys ?? new string[0]))
                yield return "L109 wire-lossy: the 0xB7 payload for a queued non-modal window does not survive " +
                             "Encode/Decode intact (kind " + p.Kind + "->" + back.Kind + "). The Kind byte IS the " +
                             "generic axis: dropped, the peer runs the modal branch over a deployment payload and " +
                             "hands UIStateGeoModal a bind it will cast and dereference inside EnterState.";

            // ── (d) the refusals — no window rather than a broken one ──────
            if (GeoModalMirror.DataRefusal(GeoModalMirror.DataShape.AssetDeploy, false, 2, 2) == null)
                yield return "L109 unresolved-asset-accepted: a payload naming an asset this peer CANNOT resolve is " +
                             "accepted. UIStateAssetDeployment.EnterState:61 hands the bind straight to " +
                             "ShowDeployDialog, which reads bind.Asset.TemplateDef unguarded — the throw lands " +
                             "inside EnterState, and the state is already in the queue slot by then.";
            if (GeoModalMirror.DataRefusal(GeoModalMirror.DataShape.AssetDeploy, true, 2, 1) == null)
                yield return "L109 unknown-def-accepted: a payload naming a def this peer does not know is accepted, " +
                             "so the prompt would offer a different asset than the host is deploying and the answer " +
                             "would place the wrong thing.";

            // ── (e) the OPTIONAL entity: an aircraft names none ────────────
            var noAsset = Bind(asset: null, aircraft: true, related: true, manufactured: true, spaceFull: true, out why);
            var pa = GeoModalMirror.Describe(noAsset);
            if (noAsset == null || pa.Shape != GeoModalMirror.DataShape.AssetDeploy || pa.Ref != "")
                yield return "L109 aircraft-forced-entity: a manufactured AIRCRAFT (GeoscapeView.cs:1312 passes " +
                             "character null) did not describe as an AssetDeploy with an empty ref — it has no " +
                             "GeoCharacter to name, so demanding one refuses the most common raiser outright.";
            if (GeoModalMirror.DataRefusal(GeoModalMirror.DataShape.AssetDeploy, true, 2, 2) != null)
                yield return "L109 nameless-refused: an AssetDeploy payload that names no entity and whose defs all " +
                             "resolve is REFUSED. That is every manufactured aircraft, i.e. the window this arm was " +
                             "built for, dropped with a reason that reads like a bug report about the host.";
            if (GeoModalMirror.NamesEntity(pa))
                yield return "L109 parks-on-nothing: NamesEntity says an empty-ref payload names an entity, so the " +
                             "raise would park waiting for something nobody addressed and expire unshown.";
            if (!GeoModalMirror.NamesEntity(p))
                yield return "L109 asset-never-waits: NamesEntity says an AssetDeploy payload that DOES name a " +
                             "GeoCharacter names nothing, so it never parks. A manufactured ground vehicle is minted " +
                             "by CreateCharacterFromTemplate in the same call stack that opens the prompt, so the " +
                             "raise always beats its own structural create and would be refused every single time.";

            // ── (f) bounded when the asset never lands ─────────────────────
            var q = new ModalParkQueue();
            var shown = new List<uint>();
            var expired = new List<int>();
            q.Park(50u, p);
            for (int i = 0; i < ModalParkQueue.MaxBatches; i++)
                q.Pump(_ => false, (r, s) => shown.Add(s), (r, s, w) => expired.Add(w));
            if (shown.Count != 0 || expired.Count != 1 || expired[0] != ModalParkQueue.MaxBatches || q.Count != 0)
                yield return "L109 unbounded-wait: an asset-deployment raise whose entity NEVER arrives did not " +
                             "expire loudly after " + ModalParkQueue.MaxBatches + " batches (shown=" + shown.Count +
                             " expired=" + expired.Count + " parked=" + q.Count + "). A parked raise holds every " +
                             "later window behind it (strict FIFO), so an unbounded wait is the queue wedge this " +
                             "law exists to end, moved from the host to the peer.";

            // ── (g) answerable, and per INSTANCE ───────────────────────────
            string one = null, two = null;
            var state = State(bind, out why);
            var other = State(Bind(asset: null, aircraft: true, related: true, manufactured: false, spaceFull: false,
                                   out _), out _);
            if (state == null)
                yield return "L109 state-unbuildable: no UIStateAssetDeployment instance could be made (" + why +
                             "), so the identity the 0xB9 deploy op matches on is unchecked.";
            else
            {
                one = WindowQueueSync.IdentityOf(state);
                two = other == null ? null : WindowQueueSync.IdentityOf(other);
                if (string.IsNullOrEmpty(one))
                    yield return "L109 unanswerable: WindowQueueSync.IdentityOf returns nothing for a mirrored " +
                                 "asset-deployment prompt, so no peer's click can ever be matched to the host's own " +
                                 "window — the raise half would ship and the host's prompt would still sit there " +
                                 "until the host itself clicked it.";
                else if (two != null && one == two)
                    yield return "L109 identity-by-kind: two DIFFERENT asset-deployment prompts share the identity '" +
                                 one + "'. An identity that names the kind and not the instance is exactly the " +
                                 "2026-08-01 regression: one peer's answer landed on whatever window the host " +
                                 "happened to hold.";
            }

            // ── (h) the seams ──────────────────────────────────────────────
            var mirror = typeof(GeoModalMirror);
            var wqs = typeof(WindowQueueSync);
            var gatePost = typeof(GeoWindowCoverageGate).GetMethod("Postfix", All);
            var broadcastQueued = mirror.GetMethod("HostBroadcastQueued", All);
            var broadcast = mirror.GetMethod("HostBroadcast", All);
            var buildData = mirror.GetMethod("BuildData", All);
            var deployBind = mirror.GetMethod("DeployBind", All);
            var needsPark = mirror.GetMethod("NeedsPark", All);
            var namesEntity = mirror.GetMethod("NamesEntity", All);
            var raise = mirror.GetMethod("RaiseMirrored", All);
            var stateCtor = typeof(UIStateAssetDeployment)
                .GetConstructor(new[] { typeof(GeoDeployAssetFactionCharacterBind) });
            var register = wqs.GetMethod("RegisterIntents", All);
            var handleDeploy = wqs.GetMethod("HandleDeploy", All);
            var capture = wqs.GetNestedType("DeployAtSiteCapture", All)?.GetMethod("Prefix", All);
            var send = typeof(IntentRail).GetMethod("Send", All);
            var deployAtSite = typeof(UIStateAssetDeployment).GetMethod("DeployAtSite", All);
            if (gatePost == null || broadcastQueued == null || broadcast == null || buildData == null ||
                deployBind == null || needsPark == null || namesEntity == null || raise == null ||
                stateCtor == null || register == null || handleDeploy == null || capture == null ||
                send == null || deployAtSite == null)
            {
                yield return "L109 premise-changed: one of GeoWindowCoverageGate.Postfix / GeoModalMirror" +
                             ".HostBroadcastQueued|HostBroadcast|BuildData|DeployBind|NeedsPark|NamesEntity|" +
                             "RaiseMirrored / WindowQueueSync.RegisterIntents|HandleDeploy|DeployAtSiteCapture" +
                             ".Prefix / IntentRail.Send / UIStateAssetDeployment.DeployAtSite no longer exists — " +
                             "the arms above are testing functions wired to nothing.";
                yield break;
            }
            if (!References(gatePost, broadcastQueued))
                yield return "L109 gate-silent: GeoWindowCoverageGate.Postfix does not call " +
                             "GeoModalMirror.HostBroadcastQueued. QueryStateSwitch is the ONE queue every pushed " +
                             "window passes; without that call no non-modal window is ever broadcast and the prompt " +
                             "is back on one screen.";
            if (!References(broadcastQueued, broadcast))
                yield return "L109 queued-broadcast-inert: HostBroadcastQueued does not reach HostBroadcast, so the " +
                             "gate calls something that ships nothing.";
            if (!References(buildData, deployBind))
                yield return "L109 build-unrouted: GeoModalMirror.BuildData does not call DeployBind, so an " +
                             "AssetDeploy payload falls through to the faction/research branch and rebuilds null.";
            if (!References(needsPark, namesEntity))
                yield return "L109 park-shape-hardcoded: NeedsPark does not call NamesEntity. The deferral has to " +
                             "ask ONE question about the payload; a shape test written out again is the second " +
                             "table that disagrees with the first, and the shape it forgets never parks.";
            // Cross-assembly (the state and the funnel live in Assembly-CSharp), so these two ask Program's
            // real IL walker — a raw metadata-token scan cannot see a MemberRef into another assembly, and
            // the ctor is a `newobj`, which the name-based Reaches helper deliberately does not record.
            if (!Calls(raise, ".ctor"))
                yield return "L109 state-never-built: RaiseMirrored does not construct a UIStateAssetDeployment, so " +
                             "the payload arrives, resolves, and becomes nothing — the peer 'received' a window it " +
                             "never entered, which is a success in every log line this family writes.";
            if (!References(register, handleDeploy))
                yield return "L109 op-unregistered: WindowQueueSync.RegisterIntents does not register HandleDeploy, " +
                             "so every peer's answer reaches the host as an unknown op and is REJECTED — the copy " +
                             "closes on the answering peer and the asset is never deployed for anybody.";
            if (!References(capture, send))
                yield return "L109 click-uncaptured: DeployAtSiteCapture.Prefix does not call IntentRail.Send. A " +
                             "client's click then either runs GeoPhoenixFaction.DeployAsset locally (law 3, the " +
                             "peers diverge on a soldier) or does nothing at all (law 91, the window cannot be " +
                             "answered by anyone but the host).";
            if (!Calls(handleDeploy, "DeployAtSite"))
                yield return "L109 host-hand-deploys: WindowQueueSync.HandleDeploy does not call the game's own " +
                             "UIStateAssetDeployment.DeployAtSite. That method is DeployAsset PLUS " +
                             "FinishQueriedState; re-implementing either half leaves the host's own prompt in its " +
                             "queue slot after the asset has already moved.";
        }

        /// <summary>A bind with real def guids, built the way GeoscapeView.PrepareDeployAsset:1310 does.
        /// Uninitialized instances are enough: the rail names an asset from its id member and a def from its
        /// Guid field, and neither needs a live level.</summary>
        private static GeoDeployAssetFactionCharacterBind Bind(GeoCharacter asset, bool aircraft, bool related,
                                                               bool manufactured, bool spaceFull, out string why)
        {
            why = null;
            try
            {
                return new GeoDeployAssetFactionCharacterBind
                {
                    Asset = asset,
                    Aircraft = aircraft ? Def<ComponentSetDef>(AircraftGuid) : null,
                    RelatedItemDef = related ? Def<ItemDef>(ItemGuid) : null,
                    Manufactured = manufactured,
                    NotEnoughSpace = spaceFull,
                };
            }
            catch (Exception ex) { why = ex.GetType().Name; return null; }
        }

        private static UIStateAssetDeployment State(GeoDeployAssetFactionCharacterBind bind, out string why)
        {
            why = null;
            if (bind == null) { why = "no bind"; return null; }
            try
            {
                var s = (UIStateAssetDeployment)FormatterServices
                    .GetUninitializedObject(typeof(UIStateAssetDeployment));
                typeof(UIStateAssetDeployment).GetField("_deployBind", All).SetValue(s, bind);
                return s;
            }
            catch (Exception ex) { why = ex.GetType().Name; return null; }
        }

        private static GeoCharacter NewChar()
        {
            try { return (GeoCharacter)FormatterServices.GetUninitializedObject(typeof(GeoCharacter)); }
            catch { return null; }
        }

        private static T Def<T>(string guid) where T : Base.Defs.BaseDef
        {
            var d = (T)FormatterServices.GetUninitializedObject(typeof(T));
            d.Guid = guid;
            return d;
        }

        /// <summary>Does <paramref name="m"/> invoke a member of <c>UIStateAssetDeployment</c> by that name?
        /// Program's real IL walker, because the callee lives in the GAME assembly.</summary>
        private static bool Calls(MethodBase m, string name) =>
            m != null && Program.Callees(m, typeof(UIStateAssetDeployment).Assembly)
                                .Any(c => c.DeclaringType == typeof(UIStateAssetDeployment) && c.Name == name);

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Raw 4-byte metadata
        /// token scan — see the ponytail note on the class.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            int token = callee.MetadataToken;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
