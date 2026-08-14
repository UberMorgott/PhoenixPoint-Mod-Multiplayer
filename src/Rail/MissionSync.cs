using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Events.Eventus;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// MISSION-LAUNCH gesture family (surface 0xB8, law 1) — the "squad INTENT" that
    /// <see cref="GeoWindowCoverage"/>'s <c>UIStateRosterDeployment</c> declaration named as the missing
    /// piece: "shared deployment (all peers pick from one roster, host commits)". Without it a client
    /// could open the deployment screen and press Deploy, and the click died silently — the native chain
    /// ends in <c>GeoLevelController.LaunchTacticalGame</c>, which <c>TacticalEntry.TacLaunchGate</c>
    /// BLOCKS on a client by construction (the battle is built once on the host and shipped to every peer
    /// as a mid-tactical save, law 1). The screen closed back to the plain geoscape and the mission never
    /// started for anybody.
    ///
    /// ONE op, at the ONE model funnel: <c>GeoMission.Launch(GeoSquad)</c> (GeoMission.cs:226). Every
    /// route into a battle bottoms out there and the funnel is what makes this generic rather than a
    /// per-screen patch — <c>UIStateRosterDeployment.DeploySquad</c>:331 (the deployment screen's own
    /// button), <c>GeoscapeView.LaunchMission</c>:1043 (its SkipDeploymentSelection / SkipDeploymentScreen
    /// arm, which never opens a screen at all), <c>HavenFacilityController</c>:149,
    /// <c>HavenInteractionController</c>:226, <c>UIModuleSiteEncounters</c>:612 and
    /// <c>StealAircraftAbility</c>:93 all reach it. Capture is block-first
    /// (<see cref="IntentRail.ShouldRunNative"/>): the client never runs <c>PrepareTacticalGame</c>, never
    /// stamps <c>GlobalTime</c> and never rolls <c>GenerateMissionThreatLevel</c> — all three are
    /// authoritative and all three ride back as ordinary state.
    ///
    /// THE WIRE CARRIES IDENTITY ONLY: the mission's SITE root key plus the chosen soldiers' root keys
    /// ("U#&lt;GeoTacUnitId&gt;", IdentityResolver.cs:146). NOT the mission — a client's <c>GeoMission</c>
    /// is a structural mirror of the host's own (log: <c>structural create 'S#388…ActiveMission'</c>), and
    /// the host reads <c>site.ActiveMission</c> off its OWN graph rather than trusting a reference that
    /// could name a mission it already cancelled. NOT the squad object either: <c>GeoSquad.Units</c> holds
    /// live <c>GeoCharacter</c> refs (GeoSquad.cs:11), so the host rebuilds the squad from ITS instances.
    ///
    /// The peer that clicked keeps its deployment screen up and simply waits: the host's launch curtains
    /// every peer through <c>SaveTransferCoordinator</c> within the round trip, and that teardown takes
    /// the screen with it. A REJECT leaves the screen live and closable by its own Back button — which is
    /// why <see cref="MissionCancelGate"/> exists next to this.
    /// </summary>
    public static class MissionSync
    {
        internal static bool StartDurableOffer(DurableInboxStore store, OccurrenceId offer,
            OccurrenceId preparation, Func<HostLedger, bool> validate, out string refusal)
        {
            var engine = new DurableMissionOfferEngine(store, delta =>
            {
                BroadcastDeploymentTransition(delta);
                OpenUiRepaint.MarkDirty();
            });
            return engine.TryStart(offer, preparation, validate, out refusal);
        }

        internal static void BroadcastDeploymentTransition(DeploymentTransitionDelta delta)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, SyncProtocol.EncodeEnvelope(
                SurfaceIds.GeoWindowIntent, SyncKind.StateDelta, DurableInboxCodec.EncodeDeploymentTransition(delta))));
        }

        internal static void BroadcastPreparationEdit(PreparationEditDelta delta)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, SyncProtocol.EncodeEnvelope(
                SurfaceIds.GeoWindowIntent, SyncKind.StateDelta, DurableInboxCodec.EncodePreparationEdit(delta))));
        }

        internal static void BroadcastSourceRevalidation(SourceRevalidationBatchDelta delta)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, SyncProtocol.EncodeEnvelope(
                SurfaceIds.GeoWindowIntent, SyncKind.StateDelta, DurableInboxCodec.EncodeSourceRevalidation(delta))));
        }

        internal static bool ApplyPreparationEditDelta(DurableInboxStore store, PreparationEditDelta delta,
            Action<PreparationEditDelta> repaint)
        {
            if (store == null || delta == null || repaint == null || !store.InstallPreparationEdit(delta)) return false;
            repaint(delta); return true;
        }
        private static readonly object SourceDeltaGate = new object();
        private const int MaxScheduledSourceDeltas = 1024;
        private static readonly List<SourceRevalidationBatchDelta> ScheduledSourceDeltas =
            new List<SourceRevalidationBatchDelta>();
        internal static void ScheduleSourceRevalidationDelta(SourceRevalidationBatchDelta delta)
        {
            if (delta == null) return;
            lock (SourceDeltaGate)
            {
                if (ScheduledSourceDeltas.Count >= MaxScheduledSourceDeltas)
                { MpLog.LogWarning("[MP][inbox] source-revalidation buffer full; bounded delta refused"); return; }
                ScheduledSourceDeltas.Add(delta);
            }
        }
        internal static bool HasScheduledSourceDelta(string source)
        { lock (SourceDeltaGate) return ScheduledSourceDeltas.Any(x =>
            string.Equals(x.DepartedSource, source, StringComparison.Ordinal)); }
        internal static void ClearScheduledSourceRevalidationDeltas()
        { lock (SourceDeltaGate) ScheduledSourceDeltas.Clear();
          lock (DepartureGate) DepartureCaptures.Clear();
          DepartureGenerationRail.Clear();
          DepartureAnchorRail.Clear();
          GenericApplier.ClearDepartureWatermarks(); }
        internal static int ApplyScheduledSourceRevalidationDeltas()
        {
            int applied = 0; bool progressed;
            do
            {
                progressed = false; SourceRevalidationBatchDelta[] pending;
                lock (SourceDeltaGate) pending = ScheduledSourceDeltas.ToArray();
                var store = DurableInboxSession.ActiveStore;
                if (store == null) return applied;
                foreach (var delta in pending)
                {
                    if (!GenericApplier.HasSettledDeparture(delta.DepartedSource, delta.DepartureWatermark))
                        continue;
                    var readiness = store.SourceRevalidationReadiness(delta); string why = null;
                    if (readiness == SourceDeltaReadiness.Future) continue;
                    bool duplicate = readiness == SourceDeltaReadiness.Installed;
                    bool success = duplicate || readiness == SourceDeltaReadiness.Ready &&
                        store.InstallSourceRevalidationBatch(delta, out why);
                    if (!success && readiness == SourceDeltaReadiness.Ready)
                    { MpLog.LogWarning("[MP][inbox] scheduled source-revalidation delta refused: " + why); continue; }
                    lock (SourceDeltaGate) ScheduledSourceDeltas.Remove(delta);
                    progressed = true;
                    if (!success)
                    { MpLog.LogWarning("[MP][inbox] conflicting source-revalidation delta dropped"); continue; }
                    DurableSourceRevalidationEngine.RetryTerminalTeardown(store,
                        delta.Items.Where(x => x.Terminal).Select(x => x.Occurrence));
                    if (!duplicate)
                    {
                        RepaintSourceRevalidation(delta);
                        applied++;
                    }
                }
            } while (progressed);
            return applied;
        }
        internal static Action SourceRevalidationRepaintProbe;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RepaintSourceRevalidation(SourceRevalidationBatchDelta delta)
        {
            try
            {
                var geo = GenericApplier.StartedGeoLevel();
                if (geo != null) foreach (var item in delta.Items)
                    UiEventMap.FirePreparationEdit(new HashSet<object>(), geo, item.Occurrence,
                        item.PreparationRevision);
                else OpenUiRepaint.MarkDirty();
            }
            catch (System.Security.SecurityException) { OpenUiRepaint.MarkDirty(); }
            SourceRevalidationRepaintProbe?.Invoke();
        }

        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoWindowIntent || payload == null || payload.Length < 2 ||
                payload[0] != DurableInboxCodec.DeploymentTransitionOp) return false;
            if (payload[1] == 2)
            {
                PreparationEditDelta edit; string why;
                if (!DurableInboxCodec.TryDecodePreparationEdit(payload, out edit, out why))
                { MpLog.LogWarning("[MP][inbox] malformed preparation-edit StateDelta refused: " + why); return true; }
                if (engine == null || engine.IsHost || engine.Session == null ||
                    !IsAuthoritativeTransitionSender(engine.Session.HostPeerId, senderPeerId)) return true;
                var clientStore = DurableInboxSession.ActiveStore;
                if (clientStore != null) ApplyPreparationEditDelta(clientStore, edit, applied =>
                {
                    var geo = GenericApplier.StartedGeoLevel(); var touched = new HashSet<object>();
                    if (geo != null) foreach (var identity in applied.TouchedStableIdentities)
                    { var value = IdentityResolver.Resolve(geo, identity, null); if (value != null) touched.Add(value); }
                    if (geo != null) UiEventMap.FirePreparationEdit(touched, geo, applied.Occurrence, applied.PreparationRevision);
                });
                return true;
            }
            if (payload[1] == 3)
            {
                SourceRevalidationBatchDelta sourceDelta; string why;
                if (!DurableInboxCodec.TryDecodeSourceRevalidation(payload, out sourceDelta, out why))
                { MpLog.LogWarning("[MP][inbox] malformed source-revalidation StateDelta refused: " + why); return true; }
                if (engine == null || engine.IsHost || engine.Session == null ||
                    !IsAuthoritativeTransitionSender(engine.Session.HostPeerId, senderPeerId)) return true;
                ScheduleSourceRevalidationDelta(sourceDelta);
                ApplyScheduledSourceRevalidationDeltas(); // handles delta-after-rail; rail flush handles delta-before-rail
                return true;
            }
            DeploymentTransitionDelta delta; string decodeRefusal;
            if (!DurableInboxCodec.TryDecodeDeploymentTransition(payload, out delta, out decodeRefusal)) return false;
            if (engine == null || engine.IsHost || engine.Session == null ||
                !IsAuthoritativeTransitionSender(engine.Session.HostPeerId, senderPeerId)) return true;
            var store = DurableInboxSession.ActiveStore; if (store == null) return true;
            if (store.InstallDeploymentTransition(delta))
            {
                string refusal;
                if (store.AuthorizeCarrierRemoval(delta.Offer, delta.Reason, delta.TombstoneRevision, out refusal))
                    store.Carriers.RemoveAll(delta.Offer, delta.Reason, out refusal);
                OpenUiRepaint.MarkDirty();
            }
            return true;
        }
        internal static bool IsAuthoritativeTransitionSender(ulong? hostPeerId, ulong senderPeerId) =>
            hostPeerId.HasValue && hostPeerId.Value == senderPeerId;

        /// <summary>Common source-departure adapter.  The host reaches it after native StartTravel has
        /// cleared CurrentSite; a client reaches it after the whole CurrentSite/Travelling/DestinationSites
        /// rail batch.  Both therefore derive from the same settled native graph and enter the same durable
        /// reducer.  No mission Cancel callback is reachable from this path.</summary>
        internal sealed class DepartureBinding
        {
            internal DepartureBinding(OccurrenceId occurrence, GeoMission mission, DeploymentSourceSnapshot before,
                bool uniquelyBound) { Occurrence = occurrence; Mission = mission; Before = before; UniquelyBound = uniquelyBound; }
            internal OccurrenceId Occurrence { get; } internal GeoMission Mission { get; }
            internal DeploymentSourceSnapshot Before { get; } internal bool UniquelyBound { get; }
        }
        internal sealed class DepartureCapture
        {
            internal DepartureCapture(string vehicleIdentity, GeoVehicle vehicle, GeoSite site,
                IEnumerable<DepartureBinding> bindings)
            { VehicleIdentity = vehicleIdentity; Vehicle = vehicle; Site = site; Bindings = bindings.ToArray(); }
            internal string VehicleIdentity { get; } internal GeoVehicle Vehicle { get; } internal GeoSite Site { get; }
            internal IReadOnlyList<DepartureBinding> Bindings { get; }
        }
        private static readonly object DepartureGate = new object();
        private static readonly Dictionary<GeoVehicle, Stack<DepartureCapture>> DepartureCaptures =
            new Dictionary<GeoVehicle, Stack<DepartureCapture>>();

        internal static void CaptureVehicleDeparture(GeoVehicle vehicle)
        {
            var store = DurableInboxSession.ActiveStore;
            var geo = GenericApplier.StartedGeoLevel();
            var network = NetworkEngine.Instance;
            if (geo == null || vehicle == null || vehicle.CurrentSite == null ||
                network == null || !network.IsActiveSession || !network.IsHost) return;
            string vehicleIdentity = IdentityResolver.RootRef(vehicle);
            if (string.IsNullOrEmpty(vehicleIdentity)) return;
            var site = vehicle.CurrentSite; var mission = site.ActiveMission;
            string stable = DurableWindowRegistry.StableMissionSubject(mission);
            var bindings = Array.Empty<DepartureBinding>();
            if (store != null && mission != null && !string.IsNullOrEmpty(stable))
            {
                bool missionUnique = UniqueSourceBinding(mission.MissionDef != null &&
                    mission.MissionDef.DeployOnlyFromActiveVehicle, null, vehicleIdentity);
                var before = DeploymentSources(mission, geo.PhoenixFaction, missionUnique ? vehicle : null);
                if (before.ContainsSource(vehicleIdentity)) bindings = store.Ledger.AllEntries
                .Where(x => BindsDeparture(x.Occurrence, x.Lifecycle, stable))
                .Select(x => x.Occurrence).Distinct()
                .Select(x => new DepartureBinding(x, mission, before,
                    UniqueSourceBinding(mission.MissionDef != null && mission.MissionDef.DeployOnlyFromActiveVehicle,
                        x, vehicleIdentity))).ToArray();
            }
            var capture = new DepartureCapture(vehicleIdentity, vehicle, site, bindings);
            lock (DepartureGate)
            {
                Stack<DepartureCapture> stack;
                if (!DepartureCaptures.TryGetValue(vehicle, out stack))
                    DepartureCaptures.Add(vehicle, stack = new Stack<DepartureCapture>());
                stack.Push(capture);
            }
        }

        /// <summary>PURE (RailCheck L402): does this ledger entry name the mission the departing aircraft was
        /// parked on, so that the aircraft leaving must be revalidated against it?
        ///
        /// The stable subject IS the site: <c>StableMissionSubject</c> is
        /// <c>RootRef(mission.Site) + "|" + MissionDef.Guid</c>, and the mission here is
        /// <c>site.ActiveMission</c>. This used to demand a SECOND, bare <c>RootRef(site)</c> subject as well,
        /// which pinned nothing extra and merely required the three-subject shape that only the arrival-watch
        /// occurrences carry. Deployment windows and the modal briefs are minted with the single stable
        /// subject (<see cref="DurableWindowRegistry.CaptureDeployment"/>, <c>CaptureModal</c>), so that term
        /// excluded exactly the windows this machinery exists for: the aircraft flew off, the sources were
        /// recomputed, and the deployment window stayed open on every peer because it had never been bound
        /// to the departure at all.</summary>
        internal static bool BindsDeparture(OccurrenceId occurrence, InboxLifecycle lifecycle,
            string stableMissionSubject) =>
            lifecycle != InboxLifecycle.Removed && !string.IsNullOrEmpty(stableMissionSubject) &&
            (string.Equals(occurrence.EventId, "DeploymentPreparing", StringComparison.Ordinal) ||
             DurableWindowRegistry.IsMissionOfferOccurrence(occurrence)) &&
            occurrence.SubjectIds.Contains(stableMissionSubject, StringComparer.Ordinal);

        internal static bool UniqueSourceBinding(bool deployOnlyFromActiveVehicle, OccurrenceId? occurrence,
            string vehicleIdentity) => deployOnlyFromActiveVehicle &&
            (!occurrence.HasValue || occurrence.Value.SubjectIds.Contains(vehicleIdentity, StringComparer.Ordinal));

        internal static void CommitCapturedVehicleDeparture(GeoVehicle vehicle)
        {
            var capture = TakeDepartureCapture(vehicle);
            if (capture == null) return;
            ulong departureWatermark;
            if (!DepartureGenerationRail.TryAdvance(capture.VehicleIdentity, out departureWatermark)) return;
            DiffEngine.FlushNow(); // the exact generation rides the ordinary vehicle rail snapshot
            if (capture.Bindings.Count == 0) return; // ordinary departure still advanced the shared generation
            var store = DurableInboxSession.ActiveStore; var geo = GenericApplier.StartedGeoLevel();
            if (store == null || geo == null) return;
            var plans = capture.Bindings.Select(binding => new SourceRevalidationPlan(binding.Occurrence,
                capture.VehicleIdentity, binding.Before,
                ReferenceEquals(capture.Site.ActiveMission, binding.Mission)
                    ? DeploymentSources(binding.Mission, geo.PhoenixFaction, null)
                    : new DeploymentSourceSnapshot(new Dictionary<string, IEnumerable<string>>()),
                binding.UniquelyBound, departureWatermark)).ToArray();
            SourceRevalidationBatchDelta delta; string refusal;
            if (new DurableSourceRevalidationEngine(store, (o, m) => OpenUiRepaint.MarkDirty())
                .TryRevalidateBatch(plans, out delta, out refusal))
            { BroadcastSourceRevalidation(delta); OpenUiRepaint.MarkDirty(); }
            else if (!string.IsNullOrEmpty(refusal))
                MpLog.LogWarning("[MP][inbox] source departure batch refused: " + refusal);
        }

        internal static void AbandonCapturedVehicleDeparture(GeoVehicle vehicle) => TakeDepartureCapture(vehicle);

        private static DepartureCapture TakeDepartureCapture(GeoVehicle vehicle)
        {
            DepartureCapture capture = null;
            lock (DepartureGate)
            {
                Stack<DepartureCapture> stack;
                if (vehicle != null && DepartureCaptures.TryGetValue(vehicle, out stack) && stack.Count != 0)
                { capture = stack.Pop(); if (stack.Count == 0) DepartureCaptures.Remove(vehicle); }
            }
            return capture;
        }

        private static DeploymentSourceSnapshot DeploymentSources(GeoMission mission, GeoFaction faction,
            IGeoCharacterContainer priority)
        {
            var result = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
            if (mission == null || faction == null) return new DeploymentSourceSnapshot(result);
            foreach (var source in mission.GetDeploymentSources(faction, priority) ?? new List<IGeoCharacterContainer>())
            {
                string identity = IdentityResolver.RootRef(source);
                if (string.IsNullOrEmpty(identity)) continue;
                result[identity] = (source.GetAllCharacters() ?? Enumerable.Empty<GeoCharacter>())
                    .Select(IdentityResolver.RootRef).Where(x => !string.IsNullOrEmpty(x));
            }
            return new DeploymentSourceSnapshot(result);
        }

        private static void WriteOccurrence(BinaryWriter w, OccurrenceId o)
        { WriteBounded(w,o.EventId); WriteBounded(w,o.TriggerId); if(o.SubjectIds.Count>1024)throw new InvalidDataException();
          w.Write((ushort)o.SubjectIds.Count); foreach (var s in o.SubjectIds) WriteBounded(w,s); }
        private static OccurrenceId ReadOccurrence(BinaryReader r)
        { string e=ReadBounded(r), t=ReadBounded(r); int n=r.ReadUInt16(); if(n<=0||n>1024) throw new InvalidDataException();
          var s=new string[n]; for(int i=0;i<n;i++) s[i]=ReadBounded(r); return new OccurrenceId(e,t,s); }
        private static void WriteBounded(BinaryWriter w,string value)
        { var bytes=new System.Text.UTF8Encoding(false,true).GetBytes(value??""); if(bytes.Length==0||bytes.Length>4096)throw new InvalidDataException();
          w.Write((ushort)bytes.Length);w.Write(bytes); }
        private static string ReadBounded(BinaryReader r)
        { int n=r.ReadUInt16();if(n<=0||n>4096)throw new InvalidDataException("invalid bounded string");var b=r.ReadBytes(n);
          if(b.Length!=n)throw new EndOfStreamException();return new System.Text.UTF8Encoding(false,true).GetString(b); }

        internal static bool TryResolveDurableMissionOffer(GeoMission mission, out DurableInboxStore store,
            out MembershipId member, out OccurrenceId offer)
        {
            store = DurableInboxSession.ActiveStore; member = default(MembershipId); offer = default(OccurrenceId);
            if (store == null || mission == null || !WindowQueueSync.TryLocalMember(store, out member)) return false;
            var localMember = member;
            string subject = DurableWindowRegistry.StableMissionSubject(mission);
            if (string.IsNullOrEmpty(subject)) return false;
            var matches = store.Ledger.AllEntries.Where(x => x.Membership.Equals(localMember) &&
                x.Lifecycle != InboxLifecycle.Removed && DurableWindowRegistry.IsMissionOfferOccurrence(x.Occurrence) &&
                x.Occurrence.SubjectIds.Contains(subject, StringComparer.Ordinal)).Select(x => x.Occurrence).Distinct().ToArray();
            if (matches.Length != 1) return false; offer = matches[0]; return true;
        }

        internal static bool HandleDurableMissionOfferAnswer(ModalType modalType, ModalResult result,
            GeoMission mission)
        {
            DurableInboxStore store; MembershipId member; OccurrenceId offer;
            if (!GeoWindowCoverage.IsPerPeerAnswer(modalType, mission) ||
                !TryResolveDurableMissionOffer(mission, out store, out member, out offer)) return false;
            return HandleDurableMissionOfferAnswer(store, member, offer, result, mission);
        }

        internal static bool HandleDurableMissionOfferAnswer(DurableInboxStore store, MembershipId member,
            OccurrenceId offer, ModalResult result, GeoMission mission)
        {
            if (store == null || mission == null || !DurableWindowRegistry.IsMissionOfferOccurrence(offer) ||
                !store.Ledger.AllEntries.Any(x => x.Membership.Equals(member) && x.Occurrence.Equals(offer))) return false;
            if (result != ModalResult.Confirm)
                return WindowQueueSync.CancelDurableMissionOffer(store, member, offer);
            var network = NetworkEngine.Instance;
            if (network != null && network.IsActiveSession && !network.IsHost)
            {
                IntentRail.Send(SurfaceIds.GeoMissionIntent, OpStartDurableOffer,
                    "start durable offer " + offer.TriggerId, w =>
                    { WriteOccurrence(w, offer); WriteBounded(w,member.PlayerGuid); });
                return true;
            }
            var preparation = new OccurrenceId("DeploymentPreparing", "deployment:" + offer.TriggerId,
                offer.SubjectIds);
            string refusal;
            return StartDurableOffer(store, offer, preparation, ledger =>
                ReferenceEquals(mission.Site?.ActiveMission, mission) &&
                offer.SubjectIds.Contains(DurableWindowRegistry.StableMissionSubject(mission), StringComparer.Ordinal),
                out refusal);
        }

        /// <summary>PURE (RailCheck L404). HAS THIS MISSION'S START ALREADY BEEN COMMITTED BY SOMEBODY?
        /// The successor of a confirmed offer is the shared <c>DeploymentPreparing</c> occurrence over the
        /// SAME stable mission subject (<see cref="HandleDurableMissionOfferAnswer"/>:407), and it is minted
        /// once, host-authoritatively, under <c>DurableInboxStore.WithTransitionGate</c>. So its presence in
        /// the replicated ledger is the exact fact "the launch for this mission has already happened once".</summary>
        internal static bool MissionStartAlreadyCommitted(IEnumerable<OccurrenceId> ledgerOccurrences,
            string stableMissionSubject) =>
            ledgerOccurrences != null && !string.IsNullOrEmpty(stableMissionSubject) &&
            ledgerOccurrences.Any(x => string.Equals(x.EventId, "DeploymentPreparing", StringComparison.Ordinal) &&
                x.SubjectIds.Contains(stableMissionSubject, StringComparer.Ordinal));

        internal static bool MissionStartAlreadyCommitted(DurableInboxStore store, GeoMission mission) =>
            store != null && MissionStartAlreadyCommitted(store.Ledger.AllEntries.Select(x => x.Occurrence),
                DurableWindowRegistry.StableMissionSubject(mission));

        /// <summary>PURE (RailCheck L403/L404). Does the GAME'S own <c>ModalResultCallback</c> run?
        ///
        /// <paramref name="startCommitted"/> is the 2026-08-10 arm and the whole of "the launch applies
        /// EXACTLY ONCE no matter how many peers confirm". Every peer keeps its OWN live copy of a
        /// mission-start confirmation and none of them is locked read-only, so two peers CAN confirm the same
        /// mission — the second one arrives after the offer has already transitioned to
        /// <c>DeploymentPreparing</c>, its own ledger entry is tombstoned, <see cref="TryResolveDurableMissionOffer"/>
        /// no longer resolves it, and the pre-2026-08-10 fallthrough (<c>return isConfirm</c>) then ran the
        /// native <c>LaunchMission</c>:1043 a SECOND time — a second <c>GeoMission.Launch</c> on the host, or a
        /// second 0xB8 launch intent from a client. The successor's existence is the idempotence key.</summary>
        internal static bool RunNativeMissionOfferAnswer(bool inSession, bool perPeerClass, bool isConfirm,
            bool durableResolved, Func<bool> durableAction, bool startCommitted = false)
        {
            if (!inSession || !perPeerClass) return true;
            if (durableResolved && durableAction != null && durableAction()) return false;
            if (isConfirm && startCommitted) return false; // the launch already happened once — idempotent
            return isConfirm; // legacy/unresolved Confirm keeps native LaunchMission; Cancel keeps the prior local-only gate
        }
        internal const byte OpLaunch = 1;  // [siteRef][n:u16][charRef × n]

        /// <summary>The countdown veto (<see cref="DeployCountdown"/>), on the surface the Deploy press
        /// itself crosses on and in the same direction. EMPTY BODY on purpose: the wire carries WHO by
        /// carrying nothing — a veto names no state, so there is nothing for the host to re-resolve and
        /// nothing a stale mirror could get wrong.</summary>
        internal const byte OpCancelLaunch = 2;  // (no body)

        /// <summary>The HAVEN-INFILTRATION half of the same family (2026-08-08). Every steal/sabotage mission
        /// a player starts by pressing a button ON a haven is MINTED, not chosen: the click runs
        /// <c>GeoHaven.PrepareHavenMission</c>, which constructs a <c>GeoMission</c> and writes
        /// <c>Site.SetActiveMission</c> (GeoHaven.cs:1091). On a client that is a structural create on its OWN
        /// graph that the host-now-vs-host-before diff can never correct — see <see cref="HavenMissionGate"/>.
        /// Body <c>[siteRef][missionTypeDef guid][zoneIndex:i16]</c>; the zone is an INDEX into
        /// <c>haven.Zones</c> because a haven zone has no rail identity of its own.</summary>
        internal const byte OpPrepareHaven = 3;
        internal const byte OpStartDurableOffer = 4;

        /// <summary>The deployment-prep DOOR, both directions of one announcement (<see cref="DeployPrep"/>).
        /// Body <c>[siteRef]</c> on both. On this surface and not on a new one for the same reason the
        /// countdown's veto is op 2 here: the geoscape band 0xA0-0xBF has no free id left, and both ops are
        /// about the same site's mission as the launch itself.</summary>
        internal const byte OpPrepOpen = 5;
        internal const byte OpPrepClose = 6;

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoMissionIntent, "mission",
                new Dictionary<byte, IntentRail.OpHandler>
                {
                    [OpLaunch] = HandleLaunch,
                    [OpCancelLaunch] = DeployCountdown.HandleCancel,
                    [OpPrepareHaven] = HandlePrepareHaven,
                    [OpStartDurableOffer] = HandleStartDurableOffer,
                    [OpPrepOpen] = DeployPrep.HandleOpen,
                    [OpPrepClose] = DeployPrep.HandleClose,
                });
        }

        private static void HandleStartDurableOffer(NetworkEngine engine, ulong senderPeerId, uint nonce,
            byte op, BinaryReader r)
        {
            OccurrenceId offer; MembershipId member;
            try { offer = ReadOccurrence(r); member = new MembershipId(ReadBounded(r));
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("trailing durable Start bytes"); }
            catch (Exception ex) { RejectDurableStart(senderPeerId, null, "malformed bounded request: " + ex.Message); return; }
            var store = DurableInboxSession.ActiveStore;
            ClientInfo sender;
            string siteRef = offer.SubjectIds.FirstOrDefault(x => x.StartsWith("S#", StringComparison.Ordinal));
            if (engine?.Session == null || !engine.Session.Clients.TryGetValue(senderPeerId, out sender))
            { RejectDurableStart(senderPeerId, siteRef, "sender is not an authenticated session client"); return; }
            if (!string.Equals(sender.PlayerGuid.ToString("D"), member.PlayerGuid, StringComparison.OrdinalIgnoreCase))
            { RejectDurableStart(senderPeerId, siteRef, "membership identity does not belong to the sender"); return; }
            if (store == null) { RejectDurableStart(senderPeerId, siteRef, "durable inbox store is unavailable"); return; }
            if (!store.Ledger.Members.Contains(member))
            { RejectDurableStart(senderPeerId, siteRef, "membership is not a ledger member"); return; }
            if (!store.Ledger.AllEntries.Any(x => x.Occurrence.Equals(offer) && x.Membership.Equals(member)))
            { RejectDurableStart(senderPeerId, siteRef, "offer entitlement is absent"); return; }
            var geo = GenericApplier.StartedGeoLevel();
            if (geo == null) { RejectDurableStart(senderPeerId, siteRef, "host geoscape is unavailable"); return; }
            var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
            if (site == null) { RejectDurableStart(senderPeerId, siteRef, "offer site identity does not resolve"); return; }
            var mission = site.ActiveMission;
            if (mission == null) { RejectDurableStart(senderPeerId, siteRef, "site has no active mission"); return; }
            string stable = DurableWindowRegistry.StableMissionSubject(mission);
            if (string.IsNullOrEmpty(stable) || !offer.SubjectIds.Contains(stable, StringComparer.Ordinal))
            { RejectDurableStart(senderPeerId, siteRef, "active mission identity does not match the offer"); return; }
            var preparation = new OccurrenceId("DeploymentPreparing", "deployment:" + offer.TriggerId, offer.SubjectIds);
            string refusal; if (!StartDurableOffer(store, offer, preparation, ledger =>
                ReferenceEquals(site.ActiveMission, mission) && offer.SubjectIds.Contains(stable, StringComparer.Ordinal), out refusal))
                RejectDurableStart(senderPeerId, siteRef, "persistence/transition refused: " + (refusal ?? "unknown"));
        }

        internal static string DurableStartRefusalText(string why)
        {
            var text = "durable mission Start refused: " + (string.IsNullOrWhiteSpace(why) ? "unspecified" : why);
            return text.Length <= 512 ? text : text.Substring(0, 512);
        }
        internal static void EmitDurableStartRefusal(string why, Action<string, bool> emit)
        { if (emit == null) throw new ArgumentNullException(nameof(emit)); emit(DurableStartRefusalText(why), true); }
        private static void RejectDurableStart(ulong peer, string siteRef, string why) =>
            IntentRail.Reject(SurfaceIds.GeoMissionIntent, peer, DurableStartRefusalText(why), true,
                string.IsNullOrEmpty(siteRef) ? null : siteRef);

        // NOTIFY: a refused LAUNCH is the one geoscape gesture with no vanilla surface to fall back on. The
        // player picked a squad, pressed Launch and the deployment simply does not happen; the usual cause is
        // co-op arbitration (another peer already took this site / the mission stopped being runnable under
        // them), which nothing in the game's own UI greys out because single-player cannot produce it.
        private static void Reject(ulong peer, string siteRef, string why) =>
            IntentRail.Reject(SurfaceIds.GeoMissionIntent, peer, (siteRef ?? "S#?") + " — " + why,
                              true /* notify */, string.IsNullOrEmpty(siteRef) ? null : siteRef);

        // ─── THE ONE VALIDATOR (pure — host facts only, law 3) ──────────────

        /// <summary>Every fact the launch is allowed to be checked against, all of it read off the HOST's
        /// own graph: a client mirror can be arbitrarily stale, so each gate the game itself applies is
        /// repeated here rather than trusted from the wire.</summary>
        internal struct Facts
        {
            internal bool SiteResolved;      // the site root key resolved to a live GeoSite
            // TWO facts, never one. They were collapsed into a single "no runnable mission any more" refusal,
            // and on 2026-08-08 that ambiguity cost a whole session: the host said it, the owner read it as
            // "the mission expired", and the truth was that the client had MINTED a mission the host never had
            // (HavenMissionGate). GeoMission.IsRunnable has ZERO overrides in the shipped assembly, so the
            // second fact is very nearly a constant — meaning the first one is almost always the real answer,
            // and it is the one that names a rail defect rather than a race.
            internal bool MissionExists;     // site.ActiveMission is non-null ON THE HOST
            internal bool MissionRunnable;   // ...and GeoMission.IsRunnable (LaunchMissionAbility:34)
            internal int UnitsRequested;     // how many soldier refs the wire carried
            internal int UnitsResolved;      // how many of them named a live GeoCharacter on the host
            internal bool AllOwnedByPlayer;  // every resolved unit belongs to the shared player faction
            internal bool HasStandalone;     // at least one IsTacticalStandaloneActor (LaunchMissionAbility:38)
            internal int Volume;             // sum of OccupingSpace (UIStateRosterDeployment.CheckForDeployment:372)
            internal int MaxUnits;           // DeployCap.For(MissionDef) — the SAME value the UI enforces (:374-375)
            internal int VehicleCount;       // vehicles + mutogs in the squad (:373, refused at 2+ by :376)
        }

        /// <summary>null = accept, otherwise the human reason the launch was refused. Never blank — a
        /// silently eaten Deploy click is the exact bug this family exists to kill.</summary>
        internal static string Validate(Facts f)
        {
            if (!f.SiteResolved) return "no such site on the host — stale mirror";
            if (!f.MissionExists)
                return "the host's site has no ActiveMission AT ALL — either it is gone (already launched, " +
                       "cancelled or expired) or it was never on the host to begin with, which means the peer " +
                       "that sent this minted one locally";
            if (!f.MissionRunnable)
                return "that site's mission exists on the host but is NOT RUNNABLE (GeoMission.IsRunnable)";
            if (f.UnitsRequested == 0) return "the squad is empty — nothing to deploy";
            if (f.UnitsResolved != f.UnitsRequested)
                return "only " + f.UnitsResolved + " of " + f.UnitsRequested + " chosen soldiers exist on the host — " +
                       "stale roster (dismissed, dead, or another peer moved them)";
            if (!f.AllOwnedByPlayer)
                return "a chosen soldier is not on the shared player faction — only Phoenix soldiers deploy from a peer";
            if (!f.HasStandalone)
                return "no soldier in the squad can stand on the battlefield by itself (NotEnoughSoldiersForMission)";
            if (f.Volume > f.MaxUnits)
                return "the squad takes " + f.Volume + " deployment slots but the mission allows " + f.MaxUnits;
            if (f.VehicleCount > 1)
                return "two vehicles/mutogs in one squad — the deployment screen refuses that (:376)";
            return null;
        }

        // ─── CLIENT: the capture seam (law 4a), block-first ─────────────────

        /// <summary>Capture at the MODEL funnel. <c>Launch</c> is a single non-virtual method with one
        /// signature, so one patch covers every caller; the optional parameter is named exactly as the
        /// game names it, since Harmony binds by name.</summary>
        [HarmonyPatch(typeof(GeoMission), nameof(GeoMission.Launch))]
        internal static class LaunchCapturePatch
        {
            private static bool Prefix(GeoMission __instance, GeoSquad squad) => CaptureLaunch(__instance, squad);
        }

        private static bool CaptureLaunch(GeoMission mission, GeoSquad squad)
        {
            // THE FIVE-SECOND DROP, asked FIRST because it is the only gate that can refuse the HOST's own
            // launch. On a client it answers "run native" and the block-first capture below is unchanged;
            // on the host it holds the launch, arms the shared countdown, and re-issues this same call when
            // the count reaches zero. Not a quorum: nobody has to act for it to complete (DeployCountdown).
            if (!DeployCountdown.Gate(mission, squad)) return false;
            if (IntentRail.ShouldRunNative()) return true;
            string siteRef = null;
            try
            {
                siteRef = IdentityResolver.RootRef(mission?.Site);
                // Launch's own default: a null argument means "use the squad the mission already holds"
                // (GeoMission.cs:227-235), so read it the same way rather than refusing a legal call.
                var units = (squad ?? mission?.Squad)?.Units;
                if (siteRef == null || units == null || units.Count == 0)
                {
                    MpLog.LogWarning("[MP][mission] CLIENT launch DROPPED — " +
                                     (siteRef == null ? "the mission's site has no rail identity" : "no squad to deploy") +
                                     "; nothing was sent and nothing ran locally");
                    OpenUiRepaint.MarkDirty();
                    return false;
                }
                var refs = new List<string>(units.Count);
                foreach (var c in units)
                {
                    var r = IdentityResolver.RootRef(c);
                    if (r == null)
                    {
                        MpLog.LogWarning("[MP][mission] CLIENT launch DROPPED — soldier '" +
                                         (c == null ? "<null>" : c.DisplayName) + "' has no rail identity, so the host " +
                                         "cannot be told who to deploy; reconverging the open screen");
                        OpenUiRepaint.MarkDirty();
                        return false;
                    }
                    refs.Add(r);
                }
                IntentRail.Send(SurfaceIds.GeoMissionIntent, OpLaunch,
                    "launch " + siteRef + " squad=" + refs.Count,
                    w =>
                    {
                        w.Write(siteRef);
                        w.Write((ushort)refs.Count);
                        foreach (var r in refs) w.Write(r);
                    });
            }
            catch (Exception ex)
            {
                // Nothing ran and nothing shipped: no delta will ever repaint the launch the screen already
                // drew, so reconverge from the un-mutated local model exactly as the reject path does.
                MpLog.LogError("[MP][mission] CLIENT launch capture failed for " + (siteRef ?? "S#?") +
                               " — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }

        // ─── HOST: the applier (decode/dedup/reject discipline = IntentRail) ─────

        private static void HandleLaunch(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string siteRef = null;
            try
            {
                siteRef = WireString.ReadKey(r);
                int n = r.ReadUInt16();
                var refs = new List<string>(n);
                for (int i = 0; i < n; i++) refs.Add(WireString.ReadKey(r));

                var geo = GenericApplier.GeoLevel();
                if (geo == null) { Reject(senderPeerId, siteRef, "no geoscape"); return; }

                var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                var mission = site?.ActiveMission;           // the HOST's own mission, never a wire reference
                var units = refs.Select(x => IdentityResolver.Resolve(geo, x, null) as GeoCharacter)
                                .Where(c => c != null).ToList();

                string why = Validate(new Facts
                {
                    SiteResolved = site != null,
                    MissionExists = mission != null,
                    MissionRunnable = mission != null && mission.IsRunnable,
                    UnitsRequested = n,
                    UnitsResolved = units.Count,
                    AllOwnedByPlayer = units.All(c => ReferenceEquals(c.Faction, geo.PhoenixFaction)),
                    HasStandalone = units.Any(c => c.TemplateDef != null && c.TemplateDef.IsTacticalStandaloneActor),
                    Volume = units.Sum(c => c.OccupingSpace),
                    // THE SAME cap the deployment UI enforces, from the ONE function both call (L372). Read
                    // straight off MissionDef.MaxPlayerUnits, this refused every client launch the moment
                    // the optional cap lift was switched on: the client's screen allowed 12, the host's
                    // validator still said 8.
                    MaxUnits = DeployCap.For(mission?.MissionDef),
                    VehicleCount = units.Count(c => c.TemplateDef != null &&
                                                    (c.TemplateDef.IsVehicle || c.TemplateDef.IsMutog)),
                });
                if (why != null) { Reject(senderPeerId, siteRef, "launch: " + why); return; }

                // The host runs the SAME native method the client's click was blocked from, with a squad
                // built out of the host's OWN instances (GeoSquad.cs:23). Everything it produces —
                // GlobalTime, the threat roll, the tactical level itself — is host-computed by construction,
                // and every peer joins through TacticalEntry's save transfer, not through this call.
                mission.Launch(new GeoSquad(units));
                // "APPLIED" ONLY IF IT WAS. DeployCountdown.Gate sits in front of this call and can refuse it
                // three ways; the old line printed APPLIED for all of them, and in the 2026-08-08 22:52 window
                // it said so SEVEN times for launches that never happened (nonces 213-219). A log that lies is
                // worse than no log — it is what made the double-launch race unreadable for a whole session.
                string outcome = DeployCountdown.IsHolding(mission)
                                     ? "HELD for the countdown (the drop starts when it reaches zero)"
                                     : DeployCountdown.WasAlreadyLaunched(mission)
                                         ? "REFUSED — this mission has already launched; nothing ran"
                                         : DeployCountdown.CountdownRunning
                                             ? "REFUSED — a countdown is already running; nothing ran"
                                             : "APPLIED";
                MpLog.Log("[MP][mission] HOST intent " + outcome + " op=launch " + siteRef +
                          " squad=" + units.Count + " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, siteRef, "launch (throw) " + ex.Message); }
        }

        /// <summary>HOST: mint the haven mission the client's click was blocked from minting, by running the
        /// GAME's own method off the HOST's own graph. Every gate below is one the game itself applies at the
        /// same click (<c>StealAircraftAbility.GetDisabledState</c>:45-53) — re-run here rather than trusted
        /// from the wire, and WIDENED NOWHERE: a client may not reach a mission single-player cannot.
        ///
        /// The wire carries no mission and no zone OBJECT, only three identities the two peers derive the same
        /// way: the site root key, the def GUID, and the zone's INDEX into <c>haven.Zones</c>. A zone has no
        /// rail identity (<c>IdentityResolver</c>'s <c>HavenZoneRef</c> is an ALIAS path into that same list),
        /// and re-deriving it host-side by mission tag is NOT equivalent — <c>zone.Def.AvailableMissionsTags</c>
        /// is a LIST and several zones of one haven can carry the same tag, so a tag lookup can pick a
        /// different zone than the one the player's brief modal was built from. Index -1 means the caller
        /// passed no zone at all (<c>HavenInteractionController.MissionSelected</c>:219 does exactly that), and
        /// the host reproduces that by passing null so the GAME does its own derivation — identical to solo.
        ///
        /// The outcome is not shipped: <c>PrepareHavenMission</c> ends in <c>Site.SetActiveMission</c>
        /// (GeoHaven.cs:1091) and that rides to every peer as the ordinary <c>S#&lt;id&gt;…ActiveMission</c>
        /// structural create the launch family already depends on.</summary>
        private static void HandlePrepareHaven(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op,
                                               BinaryReader r)
        {
            string siteRef = null;
            try
            {
                siteRef = WireString.ReadKey(r);
                string defGuid = WireString.ReadKey(r);
                int zoneIdx = r.ReadInt16();

                var geo = GenericApplier.GeoLevel();
                if (geo == null) { Reject(senderPeerId, siteRef, "prepare-haven: no geoscape"); return; }

                var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                if (site == null)
                { Reject(senderPeerId, siteRef, "prepare-haven: no such site on the host — stale mirror"); return; }
                var haven = site.GetComponent<GeoHaven>();
                if (haven == null)
                { Reject(senderPeerId, siteRef, "prepare-haven: that site carries no GeoHaven on the host"); return; }
                var def = GameUtl.GameComponent<DefRepository>()?.GetDef(defGuid) as TacMissionTypeDef;
                if (def == null)
                { Reject(senderPeerId, siteRef, "prepare-haven: unknown mission type def " + defGuid); return; }

                // ── the GAME's own gates, off HOST state. Nothing widened. ──
                if (site.Type != GeoSiteType.Haven)
                { Reject(senderPeerId, siteRef, "prepare-haven: that site is not a haven on the host"); return; }
                if (site.State != GeoSiteState.Functioning)
                {
                    Reject(senderPeerId, siteRef, "prepare-haven: the haven is " + site.State +
                                                  " on the host, not Functioning");
                    return;
                }
                // WIDENED 2026-08-09 to `|| site.ActiveMission != null`, and it is cosmetic on purpose. If the
                // host's site already HOLDS a mission of another def, PrepareHavenMission runs past its own
                // early-out (:1056 matches on def) and ends in Site.SetActiveMission — and the site's own
                // Create*Mission guards THROW "Site already has a mission!" (GeoSite.cs:496/508/…). The client
                // then got a "(throw)" reject with an engine exception in it instead of a sentence, which is
                // what hid this whole area for a session. Vanilla throws at the same click, so nothing is
                // widened or narrowed for the player — only the refusal is now readable.
                if (site.HasActiveEncounter || site.ActiveMission != null)
                {
                    Reject(senderPeerId, siteRef, "prepare-haven: the haven already has " +
                                                  (site.HasActiveEncounter ? "an active encounter"
                                                                           : "a DIFFERENT mission (" +
                                                                             (site.ActiveMission.MissionDef == null
                                                                                  ? "?" : site.ActiveMission.MissionDef.name) +
                                                                             ")") +
                                                  " on the host — one site holds one mission, and single-player " +
                                                  "throws at this same click");
                    return;
                }

                GeoHavenZone zone = null;
                if (zoneIdx >= 0)
                {
                    var zones = haven.Zones.ToList();
                    if (zoneIdx >= zones.Count)
                    {
                        Reject(senderPeerId, siteRef, "prepare-haven: zone index " + zoneIdx + " is past the " +
                                                      "host's " + zones.Count + " zones — stale mirror. The index " +
                                                      "is NOT re-derived by mission tag here: several zones can " +
                                                      "carry one tag, so a lookup could mint the mission against " +
                                                      "a zone the player never saw");
                        return;
                    }
                    zone = zones[zoneIdx];
                }

                // The one per-def gate the game applies, asked only of the defs it applies to. PrepareHavenMission
                // itself THROWS "does not have any owned aircrafts!" (GeoHaven.cs:1075) on this exact condition.
                var stealTag = site.GeoLevel?.SharedData?.SharedGameTags?.StealAircraftMissionTag;
                if (stealTag != null && def.Tags != null && def.Tags.Contains(stealTag) &&
                    haven.GetAvailableStealAircraftMission() == null)
                {
                    Reject(senderPeerId, siteRef, "prepare-haven: the haven owns no aircraft to steal on the host");
                    return;
                }

                var mission = haven.PrepareHavenMission(def, zone);
                MpLog.Log("[MP][mission] HOST intent APPLIED op=prepare-haven " + siteRef + " def=" + def.name +
                          " zone=" + (zoneIdx < 0 ? "<derived by the game>" : zoneIdx.ToString()) +
                          " mission=" + (mission == null ? "<null>" : mission.GetType().Name) +
                          " nonce=" + nonce + " peer=" + senderPeerId +
                          " — Site.SetActiveMission rides out as the ordinary structural create");
            }
            catch (Exception ex) { Reject(senderPeerId, siteRef, "prepare-haven (throw) " + ex.Message); }
        }
    }

    /// <summary>
    /// THE ALTITUDE IS THE FUNNEL, NOT THE ABILITY (2026-08-08).
    ///
    /// THE REPORT. On a client the owner flew to a haven, pressed STEAL AN AIRCRAFT from the infiltration
    /// brief, and the squad screen answered that the aircraft was "busy with something else". The host's
    /// verbatim refusal, 200 ms earlier: <c>S#79 — launch: that site has no runnable mission any more</c> →
    /// <c>HOST mission REJECT peer=1</c>. Then the client repainted <c>[MP][site] repaint S#79
    /// state=Functioning activeMission=StealAircraftAN_CustomMissionTypeDef</c> — a mission the host did not
    /// have — and the backstop sealed it: <c>CRC backstop: root 'S#79' DIVERGED on peer 1</c>.
    ///
    /// WHAT ACTUALLY HAPPENED. <c>StealAircraftAbility.ActivateInternal</c>:92 calls
    /// <c>GeoHaven.PrepareHavenMission</c> with the default <c>wireMission: true</c>, and that method
    /// CONSTRUCTS a <c>GeoMission</c> and writes <c>Site.SetActiveMission(geoMission)</c> (GeoHaven.cs:1091).
    /// Nothing in this mod patched, gated or intented that path, so the client minted a mission on its own
    /// graph, then asked the host to launch a mission the host had never heard of. The host read its own
    /// <c>site.ActiveMission</c> (null) and refused — correctly. <c>GeoMission.IsRunnable</c> has ZERO
    /// overrides in the shipped assembly, so <c>MissionRunnable == false</c> could only ever have meant
    /// <c>mission == null</c>; <see cref="MissionSync.Validate"/> now says which.
    ///
    /// AND IT IS NOT ABOUT AIRCRAFT. The same funnel serves <c>HavenFacilityController</c>:110 (every haven
    /// infiltration the haven-details screen offers) and <c>HavenInteractionController</c>:219 (the mission
    /// buttons on the haven panel). One prefix at <c>PrepareHavenMission</c> covers all three; a patch on
    /// <c>StealAircraftAbility</c> would have covered one and left the class open — which is exactly the
    /// mistake RailCheck L42 could not see, because its receiver filter only ever swept <c>GeoVehicle</c>.
    ///
    /// THE THREE ARMS.
    ///   • <c>wireMission == false</c> → NATIVE, on every peer. <c>PrepareDummyMissions</c>:1096 is the only
    ///     caller that passes it and it exists to populate the infiltration BRIEF modal; it writes nothing to
    ///     the site, so blocking it would empty the modal the player picks from and break the click before it
    ///     ever became a mission.
    ///   • host / solo / inside an apply (<see cref="IntentRail.ShouldRunNative"/>) → NATIVE.
    ///   • client → the game's OWN early-out is reproduced first: <c>if (Site.ActiveMission != null &amp;&amp;
    ///     Site.ActiveMission.MissionDef == missionTypeDef) return Site.ActiveMission;</c> (:1056) writes
    ///     nothing, so once the host's mission has ARRIVED the second pass simply runs native and hands back
    ///     the host's own instance. Before that, the call is blocked, the intent is sent, and the arrival
    ///     watch is armed. <c>__result</c> is deliberately left NULL rather than set to a mission of a
    ///     DIFFERENT def — the callers pass the return straight into <c>ToDeploymentState</c>, and opening the
    ///     squad screen on the wrong mission is worse than opening none (see
    ///     <see cref="DeploymentNeedsAMission"/>).
    ///
    /// NO QUORUM (P13). The client waits on the HOST, which answers by itself, and the watch reads only this
    /// peer's own site graph. No peer is counted, nothing waits for a human to press anything, and the wait is
    /// bounded by <see cref="MissionArrivalNav.ArrivalWindowSeconds"/> with a LOUD give-up.
    ///
    /// REACTIVITY. The host's <c>SetActiveMission</c> is a structural create on <c>S#&lt;id&gt;…ActiveMission</c>,
    /// so every OTHER peer's open haven panel repaints on the default <c>UiEventMap</c> arm
    /// (<c>[MP][site] repaint S#&lt;id&gt;</c>). The CLICKING peer's own screen is not a repaint at all — it is
    /// opened by <see cref="MissionArrivalNav"/> when the mission lands.
    /// </summary>
    [HarmonyPatch(typeof(GeoHaven), nameof(GeoHaven.PrepareHavenMission))]
    internal static class HavenMissionGate
    {
        /// <summary>PURE (RailCheck L271). MAY THIS PEER MINT THE MISSION ITSELF? True = run the game's own
        /// code; false = block, ask the host, and wait for the mission to arrive as ordinary state.
        ///
        /// Three ways it is yes, and they are not interchangeable:
        ///   • <paramref name="runNative"/> — host, solo, or inside a delta apply
        ///     (<see cref="IntentRail.ShouldRunNative"/>). The authority always mints.
        ///   • <paramref name="wireMission"/> false — <c>PrepareDummyMissions</c>:1096 builds the infiltration
        ///     BRIEF the player picks from and writes nothing to the site. Blocking it would empty the modal
        ///     and kill the click before it could ever become a mission, so this arm must stay open on a
        ///     client. It is the one place a client legitimately constructs <c>GeoMission</c> objects, and it
        ///     is safe because the ctor only fills the instance (GeoMission.cs:40-46) — no registry, no site.
        ///   • <paramref name="missionAlreadyHere"/> — the game's OWN early-out (GeoHaven.cs:1056) already
        ///     answers this call with the mission the site holds, and that path writes nothing. This is what
        ///     makes the second pass, after the host's mission arrives, run natively and hand back the host's
        ///     own instance instead of minting a second local one.</summary>
        internal static bool MintsLocally(bool runNative, bool wireMission, bool missionAlreadyHere)
            => runNative || !wireMission || missionAlreadyHere;

        private static bool Prefix(GeoHaven __instance, TacMissionTypeDef missionTypeDef, GeoHavenZone zone,
                                   bool wireMission, ref GeoMission __result)
        {
            var site = __instance == null ? null : __instance.Site;
            var existing = site == null ? null : site.ActiveMission;
            // The game's own early-out (:1056): the host's mission is already here, and native writes nothing.
            // ReferenceEquals, not ==: both operands are UnityEngine.Objects and that operator answers "is the
            // native half alive" before it compares instance ids (RailCheck L113).
            bool alreadyHere = existing != null && missionTypeDef != null &&
                               ReferenceEquals(existing.MissionDef, missionTypeDef);
            if (MintsLocally(IntentRail.ShouldRunNative(), wireMission, alreadyHere)) return true;

            string siteRef = null;
            try
            {
                siteRef = IdentityResolver.RootRef(site);
                __result = null;   // never hand back a mission of a different def
                if (siteRef == null || missionTypeDef == null || string.IsNullOrEmpty(missionTypeDef.Guid))
                {
                    MpLog.LogWarning("[MP][mission] CLIENT haven mission DROPPED — " +
                                     (siteRef == null ? "the haven's site has no rail identity"
                                                      : "the mission type def has no guid") +
                                     "; nothing was sent and nothing was minted locally. The host is unchanged, " +
                                     "so no delta will repaint this — reconverging the open screen");
                    OpenUiRepaint.MarkDirty();
                    return false;
                }

                // THE ZONE RIDES AS AN INDEX, never as a tag. GeoHavenZone has no root identity of its own
                // (IdentityResolver.HavenZoneRef is an alias path into haven.Zones), and several zones of one
                // haven can list the same MissionTypeTagDef in AvailableMissionsTags — so a host-side tag
                // lookup could mint the mission against a zone the player never saw. -1 = the caller passed
                // none, which the host reproduces by passing null so the GAME derives it exactly as in solo.
                int zoneIdx = -1;
                if (zone != null)
                {
                    zoneIdx = __instance.Zones.ToList().IndexOf(zone);
                    if (zoneIdx < 0)
                    {
                        MpLog.LogWarning("[MP][mission] CLIENT haven mission at " + siteRef + " DROPPED — the " +
                                         "chosen zone is not in this haven's own Zones list, so there is no index " +
                                         "to name it by and a tag lookup could pick a different zone. Nothing was " +
                                         "sent and nothing was minted locally");
                        OpenUiRepaint.MarkDirty();
                        return false;
                    }
                }

                string vehicleRef = IdentityResolver.RootRef(site.GeoLevel?.View?.GetVehicleOnSite(site));
                MissionArrivalNav.WatchHavenMission(siteRef, vehicleRef);
                IntentRail.Send(SurfaceIds.GeoMissionIntent, MissionSync.OpPrepareHaven,
                    "prepare-haven " + siteRef + " def=" + missionTypeDef.name + " zone=" + zoneIdx,
                    w =>
                    {
                        w.Write(siteRef);
                        w.Write(missionTypeDef.Guid);
                        w.Write((short)zoneIdx);
                    });
                MpLog.Log("[MP][mission] CLIENT haven mission BLOCKED at " + siteRef + " — PrepareHavenMission " +
                          "writes Site.SetActiveMission (GeoHaven.cs:1091) and a mission minted here is a " +
                          "structural create on this peer's OWN graph the host-vs-host diff can never correct " +
                          "(2026-08-08: 'launch: that site has no runnable mission' + CRC DIVERGED). Asked the " +
                          "host instead; the squad screen opens when its mission lands");
            }
            catch (Exception ex)
            {
                __result = null;
                MpLog.LogError("[MP][mission] CLIENT haven mission capture failed for " + (siteRef ?? "S#?") +
                               " — nothing was minted locally; reconverging the open screen: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }
    }

    /// <summary>
    /// The three-line belt under <see cref="HavenMissionGate"/>. <c>GeoscapeView.ToDeploymentState</c>:592
    /// builds <c>new UIStateRosterDeployment(mission, …)</c> UNCONDITIONALLY (:595), and all three callers hand
    /// it a <c>PrepareHavenMission</c>/<c>ActiveMission</c> return without checking it —
    /// <c>StealAircraftAbility</c>:93, <c>HavenFacilityController</c>:111 and <c>GeoscapeView.LaunchMission</c>
    /// :1050. A blocked prefix returns null there, and a squad screen holding a null mission is a NullReference
    /// somewhere downstream at best and a screen for a mission nobody has at worst.
    ///
    /// Not role-gated, because this is not a game rule being relaxed: in solo <c>PrepareHavenMission</c> never
    /// returns null (it THROWS instead, GeoHaven.cs:1085), so refusing null changes nothing outside a session
    /// — and it makes the one thing that used to be silent say its name.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ToDeploymentState))]
    internal static class DeploymentNeedsAMission
    {
        /// <summary>FIRST, ahead of <c>NativeRaiserToken.DeploymentCapture</c>. This is the legitimacy check
        /// on the whole call, and it is free; the capture allocates and mints a trigger id. The capture is
        /// void and runs whatever this returns — see the note on its own prefix for why that is correct.</summary>
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(GeoMission mission)
        {
            if (mission != null) return true;
            MpLog.LogWarning("[MP][mission] deployment screen NOT opened — the mission is NULL. Something handed " +
                             "ToDeploymentState:592 a blocked or missing mission; on a client that is normally " +
                             "HavenMissionGate holding the click while the host mints it, and MissionArrivalNav " +
                             "opens the screen when it lands. Opening it now would build UIStateRosterDeployment " +
                             "around nothing");
            return false;
        }
    }

    /// <summary>
    /// PRESENTATION seam (law 4c) at the encounter that STARTS a mission — the piece without which
    /// <see cref="MissionSync"/> was unreachable on a client, and the reason the 0xB8 family never once
    /// fired in the 2026-08-01 run: the client never got as far as a deployment screen, so nothing ever
    /// called <c>GeoMission.Launch</c> for the capture to see.
    ///
    /// The native route from "answer the mission encounter" to the deployment screen is ONE call inside
    /// <c>UIModuleSiteEncounters.SelectChoice</c>:598-613 — <c>ev.CompleteEvent(...)</c>, then
    /// <c>ChoiceReward.ApplyResult.StartMission</c>, then <c>FinishEncounter()</c> +
    /// <c>Context.View.LaunchMission(startMission, _geoEvent.Context.Vehicle)</c>:612. On a CLIENT the
    /// whole method is unreachable: <c>EventPopup.EventChoiceClientLock</c> blocks
    /// <c>OnChoiceSelected</c> and turns the click into a 0xB4 answer intent, and even if it did run,
    /// <c>EventSync.EventCompleteArbiter</c> skips <c>CompleteEvent</c> and hands back the empty
    /// <c>ChoiceReward</c> stub, so <c>StartMission</c> is null and <c>SelectChoice</c> returns false.
    /// The client therefore fell into the :586-596 arm — result page, OK, back to the plain geoscape —
    /// which is exactly the reported symptom: no briefing, no soldier assignment, host-only launches.
    ///
    /// That lost call is pure LOCAL NAVIGATION, not game logic: <c>LaunchMission</c> either opens
    /// <c>UIStateRosterDeployment</c> (declared LocalOnly in <see cref="GeoWindowCoverage"/> — every peer
    /// navigates its own) or, in the SkipDeploymentSelection arm, calls <c>mission.Launch</c>, which is
    /// the capture above. So the client may simply re-issue it, and every peer holding the window gets the
    /// same screen the host gets. Two peers may deploy the same mission at once; the first 0xB8 to reach
    /// the host wins and the second is refused by <see cref="Validate"/>'s "no runnable mission any more"
    /// arm — law 5's first-to-act-wins, no ownership table.
    ///
    /// AND IT IS NOT A CLIENT SEAM (2026-08-06, law L141). The bail read "solo/host ran SelectChoice
    /// natively" off <c>engine.IsHost</c>, and that is false for a HOST THAT LOST THE EVENT RACE: its click
    /// is REPLAYED (<c>EventPopup.ResolutionIsNotOurs</c> → <c>ReplayResolution</c> → return false), so not
    /// one line of <c>SelectChoice</c>:598-613 ran there either — including :612. Reported live: the host
    /// pressed the mission choice and went into the battle without ever seeing the squad screen. The term is
    /// "this peer's native SelectChoice did not run", carried by <c>EventPopup.ClickWasReplayed</c>, and the
    /// second term <see cref="ShouldOpenDeployment"/> adds keeps the widened predicate from double-queueing
    /// the screen the losing host is usually already headed for.
    ///
    /// Hooked at <c>FinishEncounter</c> (:618) rather than at the click, because that is the one point
    /// BOTH native arms reach and, more importantly, the point by which the host's answer has come back:
    /// the client's result page is drawn from the arriving record delta (<c>EventPopup</c>'s "resolved by
    /// THIS peer's own click"), and the mission rides in as the <c>ActiveMission</c> structural create in
    /// that same stream. The mission is read off the SITE, never off the reward — the client's reward is
    /// the stub — and the choice is read from the HOST's record index, since <c>GeoscapeEvent
    /// .SelectedChoice</c> is only written inside the <c>CompleteEvent</c> the client skipped.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleSiteEncounters), "FinishEncounter")]
    internal static class MissionEncounterNav
    {
        private static readonly FieldInfo GeoEventField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_geoEvent");
        private static readonly FieldInfo SwitchQueryField = AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");

        /// <summary>PURE (RailCheck L141). THE OUTCOME this seam owes every peer is "headed for the
        /// pre-mission squad screen"; this answers the only question left, WHO STILL OWES ITSELF THE CALL.
        ///
        /// "MY NATIVE SelectChoice DID NOT RUN", not "I am a client", is the term — that was the defect. The
        /// old predicate was <c>inSession &amp;&amp; !isHost</c>, commented "solo/host ran SelectChoice
        /// natively", which is false for a HOST THAT LOST THE EVENT RACE: its click is replayed
        /// (<c>EventPopup.ResolutionIsNotOurs</c> → <c>ReplayResolution</c> → return false), so :612's
        /// <c>LaunchMission</c> never ran there either. A client's click is ALWAYS relayed, so
        /// <paramref name="clickWasReplayed"/> is only ever asked of the host.
        ///
        /// <paramref name="alreadyHeaded"/> is the other half, and it is what keeps the widened predicate from
        /// becoming a SECOND bug. A losing host is normally already headed: <c>EventSync.HandleAnswer</c>'s
        /// native tail runs <c>geo.View.LaunchMission</c> the moment it applies the winning answer, and
        /// <c>GeoscapeView.ToDeploymentState</c>:592 QUEUES the state
        /// (<c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>, priority int.MaxValue) rather than entering it —
        /// it is served only when the peer's own encounter window finishes. MEASURED in the reported session
        /// (host log 36869/632,002 s): `Queuerd state switch …UIStateRosterDeployment with priority
        /// 2147483647`, 4 s before that host clicked its stale picker. Re-issuing blind would leave a SECOND
        /// request in a queue that is part of the SAVE (<c>GeoscapeViewSwitchQuery.GetRestorableData</c>) —
        /// a deployment screen for a finished mission, waiting for the player after the battle.</summary>
        internal static bool ShouldOpenDeployment(bool inSession, bool isHost, bool clickWasReplayed, bool alreadyHeaded)
            => inSession && !NativeSelectChoiceRan(isHost, clickWasReplayed) && !alreadyHeaded;

        /// <summary>PURE. Did <c>UIModuleSiteEncounters.SelectChoice</c>:598-613 execute ON THIS PEER for this
        /// resolution — the single fact the old bail got wrong.</summary>
        internal static bool NativeSelectChoiceRan(bool isHost, bool clickWasReplayed) => isHost && !clickWasReplayed;

        /// <summary>Is this peer already in, or queued for, the pre-mission squad screen? Asked of the game's
        /// OWN queue (<c>TryGetStateSwitchRequestForState</c>) and current state, so it stays true for the
        /// whole window between HandleAnswer's queue and the state actually being entered.
        ///
        /// TFTV-SAFE WITHOUT KNOWING TFTV: the screen the player edits his squad on with TFTV installed IS
        /// <c>UIStateRosterDeployment</c> — TFTV adds no view state of its own, it decorates that one
        /// (<c>TFTVUI/Geoscape/MissionDeployment.cs</c>:36/:101 EnterState/ExitState,
        /// <c>TFTVHarmonyGeoscapeUI.cs</c>:249 OnDeploySquad, <c>TFTVAircraftRework/
        /// AircraftReworkMissionDeployment.cs</c>:26/:133 OnEnrollmentChanged/CheckForDeployment) and only
        /// flips <c>SkipDeploymentSelection</c> on some mission defs. So this file names ZERO TFTV types,
        /// needs no <c>TftvLateBinder</c> arm, and behaves identically with and without TFTV loaded.</summary>
        internal static bool AlreadyHeadedForDeployment(GeoscapeView view)
        {
            if (view == null) return false;
            if (view.CurrentViewState is UIStateRosterDeployment) return true;
            return SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q &&
                   q.TryGetStateSwitchRequestForState<UIStateRosterDeployment>(out _);
        }

        /// <summary>PURE (RailCheck L144). May a peer that resolved a mission-start encounter OFF THE RAIL
        /// leave its own encounter window standing? NO — and that, not the <see cref="ShouldOpenDeployment"/>
        /// predicate above, is what the 2026-08-06 host actually hit.
        ///
        /// <c>ToDeploymentState</c>:596 QUEUES the squad screen, and <c>GeoscapeViewSwitchQuery
        /// .ProcessQueriedStateSwitch</c> RETURNS IMMEDIATELY while <c>_currentStateSwitchRequest != null</c>.
        /// The open encounter window IS that request — it was pushed by the same queue (`Queuerd state switch
        /// …UIStateGeoscapeEvent with priority 0`) — so the ONLY thing that ever serves the deployment request
        /// is <c>FinishQueriedState</c>, and the only thing that calls it here is
        /// <c>UIModuleSiteEncounters.FinishEncounter</c>:618. Native knows this and pairs the two in one
        /// breath at <c>SelectChoice</c>:611-612: <c>FinishEncounter(); Context.View.LaunchMission(…)</c>.
        /// <see cref="EventSync"/>'s native tail copied :612 and not :611, so the losing host queued a screen
        /// behind a window nothing would ever close and sat on a stale picker until another peer's launch
        /// curtained it. MEASURED (host log, one clock): 118,239 s `Queuerd state switch
        /// …UIStateRosterDeployment with priority 2147483647` — then NOT ONE view-state line until 121,075 s
        /// `Entering Geoscape UI state: UIStateLoading`, the battle. The client, which reaches :611 through
        /// <see cref="MissionEncounterNav"/>'s own <c>FinishEncounter</c> postfix, entered the screen nine
        /// frames after queueing it.</summary>
        internal static bool MustFinishOwnEncounterWindow(bool missionStarted, bool ownWindowShowsThisEvent)
            => missionStarted && ownWindowShowsThisEvent;

        /// <summary>The missing :611. Calls the GAME's own <c>FinishEncounter</c> rather than
        /// <c>view.FinishQueriedState()</c> directly: that method also runs <c>AudioPlayer.EndEvent()</c>
        /// (:620 → <c>NarrationSound.EndEvent</c>:67), so skipping it would carry the encounter narration into
        /// the squad screen. Guarded on the CURRENT view state showing THIS event, so it can never pop a state
        /// it does not own — <c>FinishCurrentStateSwitch</c> also runs <c>SwitchToPreviousState</c>, and a
        /// blind second call would pop one screen too many.</summary>
        internal static void FinishOwnEncounterWindow(GeoscapeView view, string eventId, bool missionStarted)
        {
            var open = (view?.CurrentViewState as UIStateGeoscapeEvent)?.Event;
            bool mine = !string.IsNullOrEmpty(eventId) &&
                        string.Equals(open?.EventID, eventId, StringComparison.Ordinal);
            if (!MustFinishOwnEncounterWindow(missionStarted, mine)) return;
            var module = view.GeoscapeModules?.SiteEncountersModule;
            if (module == null || FinishEncounterMethod == null) return;
            FinishEncounterMethod.Invoke(module, null);
            MpLog.Log("[MP][mission] closing this peer's own encounter window for '" + eventId + "' — the " +
                      "squad screen was QUEUED behind it (ToDeploymentState:596, priority int.MaxValue) and " +
                      "GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch serves nothing while this window is " +
                      "the current request; SelectChoice:611 makes the same call before :612's LaunchMission");
        }

        private static readonly MethodInfo FinishEncounterMethod =
            AccessTools.Method(typeof(UIModuleSiteEncounters), "FinishEncounter");

        /// <summary>PURE (RailCheck L191). WHY there is no squad screen to open — and it is a different
        /// answer on the two roles, which is what this guard got wrong.
        ///
        /// MEASURED, host log 2026-08-07, ONE clock: 23:10:12.981 `HOST answered 'PROG_NJ0_MISS' … peer=1`,
        /// 23:10:13.052 `structural create 'S#104…ActiveMission' sent`, 23:10:26.624 `structural DESTROY
        /// 'S#104…ActiveMission' sent`, 23:10:30.105 the host's own stale picker `REPLAYED`, and 23:10:31.254
        /// the host telling ITSELF "the host's mission has not arrived on this peer yet … reach it from the
        /// aircraft's Launch button once it lands". It had arrived — the host MINTED it 17 s earlier — and it
        /// was gone 4.6 s before the click. Nothing would ever land.
        ///
        /// THE HOST CANNOT BE WAITING FOR STATE IT AUTHORS. On this peer <c>ActiveMission == null</c> has
        /// exactly one meaning — the mission is GONE (launched by whoever won the race, cancelled, or
        /// expired) — and returning without a screen is then CORRECT, not a race to wait out. Only on a
        /// CLIENT is "not arrived yet" a possible reading, because only there is the mission somebody else's
        /// structural create. The guard's behaviour is unchanged; what changes is that it stops sending the
        /// next reader (and the owner) hunting an arrival race that cannot exist on the peer that printed it.
        /// It is the same mistake as making the host discard restored windows only it holds — one peer, one
        /// role, and the answer follows from which one it is.</summary>
        internal static string NoDeploymentReason(bool isHost, bool missionMissing) =>
            isHost
                ? "this peer AUTHORS the mission, so it cannot be waiting for it to arrive: " +
                  (missionMissing ? "the site has no ActiveMission" : "the mission is not runnable") +
                  " means it is GONE — already launched, cancelled or expired — and there is nothing left to " +
                  "open. This is the correct outcome, not a race."
                : "the host's mission has not arrived on this peer yet (ActiveMission " +
                  (missionMissing ? "missing" : "not runnable") + "); reach it from the aircraft's Launch " +
                  "button once it lands";

        /// <summary>EVERY EXIT OF THIS POSTFIX SAYS WHY (RailCheck L197 arm). It had FOUR unlogged returns,
        /// and on the peer that did not answer the 2026-08-07 mission event it emitted NOTHING AT ALL — not
        /// the success, not the warning, not the catch — so which of the four it took was unknowable from the
        /// log and the squad screen simply never appeared. Silent swallow is this repo's dominant bug class;
        /// a bail nobody can see is a bug nobody can find.</summary>
        private static void Postfix(UIModuleSiteEncounters __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                MpLog.Log("[MP][mission] deployment not opened because this peer is not in a session — solo, " +
                          "where UIModuleSiteEncounters.SelectChoice:598-613 ran end to end and :612 already " +
                          "opened the squad screen natively.");
                return;
            }
            try
            {
                var ev = GeoEventField?.GetValue(__instance) as GeoscapeEvent;
                var choices = ev?.EventData?.Choices;
                // The LIVE record, not the dialog's own ref: on a mirroring peer ev.Record can still be the
                // placeholder RaiseMirrored minted (EventPopup:568), whose SelectedChoice is nobody's answer.
                var rec = EventPopup.LiveRecord(ev?.EventID, ev?.Record);
                int idx = rec == null ? -1 : rec.SelectedChoice;
                if (choices == null || idx < 0 || idx >= choices.Count)
                {
                    MpLog.Log("[MP][mission] deployment for '" + (ev?.EventID ?? "?") + "' not opened because " +
                              "this peer has no answered choice to read (choices=" +
                              (choices == null ? "none" : choices.Count.ToString()) + ", selected=" + idx +
                              ") — the record delta has not landed here yet. MissionArrivalNav still owns it.");
                    return;
                }
                // Unity default-constructs an EMPTY OutcomeStartMission, so non-null is not the signal —
                // MissionTypeDef is (GeoEventChoiceOutcome:315, same test EventPopup.StartsMission uses).
                if (choices[idx].Outcome?.StartMission?.MissionTypeDef == null)
                {
                    MpLog.Log("[MP][mission] deployment for '" + ev.EventID + "' not opened because the answered " +
                              "choice (" + idx + ") starts no mission — correct, and the ordinary case for " +
                              "every event window that is not a mission-start.");
                    return;
                }

                var view = __instance.Context?.View;
                if (!ShouldOpenDeployment(true, engine.IsHost, EventPopup.ClickWasReplayed(ev),
                                          AlreadyHeadedForDeployment(view)))
                {
                    MpLog.Log("[MP][mission] deployment for '" + ev.EventID + "' not opened because this peer " +
                              "does not owe itself the call — " +
                              (NativeSelectChoiceRan(engine.IsHost, EventPopup.ClickWasReplayed(ev))
                                  ? "its own SelectChoice:612 already ran"
                                  : "it is already in, or queued for, UIStateRosterDeployment"));
                    return;
                }

                var site = ev.Context?.Site;
                var mission = site?.ActiveMission;
                if (mission == null || !mission.IsRunnable)
                {
                    // Never silent, and never role-blind — see NoDeploymentReason.
                    MpLog.LogWarning("[MP][mission] deployment for '" + ev.EventID + "' at " +
                                     (IdentityResolver.RootRef(site) ?? "S#?") + " NOT opened — " +
                                     NoDeploymentReason(engine.IsHost, mission == null));
                    return;
                }

                view.LaunchMission(mission, ev.Context.Vehicle);
                MpLog.Log("[MP][mission] " + (engine.IsHost ? "HOST" : "CLIENT") + " squad screen opened for '" +
                          ev.EventID + "' at " + (IdentityResolver.RootRef(site) ?? "S#?") + " — re-issuing the " +
                          "LaunchMission call SelectChoice:612 could not make here (this peer's click " +
                          (engine.IsHost ? "was replayed: another peer's answer won the race" : "is always relayed") +
                          "); the squad picked rides out as a 0xB8 launch intent");
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][mission] deployment navigation failed — this peer stays on the " +
                               "geoscape and can still reach the mission from the aircraft's Launch button: " + ex);
            }
        }
    }

    /// <summary>
    /// THE SQUAD SCREEN OPENS ON THE MISSION'S ARRIVAL, NOT ON A DIALOG'S TEARDOWN (2026-08-08).
    ///
    /// THE REPORT. Two peers were shown the same mission-start event; one answered, the other did not. The
    /// answering peer reached <c>UIStateRosterDeployment</c>; the other never did, and its
    /// <see cref="MissionEncounterNav"/> postfix emitted NO LINE AT ALL — not the success, not the warning,
    /// not the catch — so it bailed at one of four unlogged returns and the screen was simply gone. Those
    /// four returns now all speak (see there), but LOGGING A BAIL IS NOT A FIX FOR IT.
    ///
    /// WHY THE FUNNEL WAS THE WRONG THING TO DEPEND ON. <c>UIModuleSiteEncounters.FinishEncounter</c> is a
    /// LOCAL DIALOG event: it fires only if this peer's own window was built, was clicked through and tore
    /// down, and only reads state that this peer's own dialog happens to be holding at that instant — a
    /// mirrored instance whose <c>Record</c> may still be the placeholder <c>RaiseMirrored</c> minted, on a
    /// peer whose record delta rides a different surface at a different cadence. Every one of those is a
    /// race, and each one loses the whole screen.
    ///
    /// SO THE TRIGGER IS THE STATE, WHICH ARRIVES ON EVERY PEER BY ITSELF: the event's own record resolving
    /// (whoever answered it) plus the host's <c>S#&lt;id&gt;…ActiveMission</c> structural create landing on
    /// this peer's own site. Both are replicated facts on the rail, both converge without anybody clicking
    /// anything, and the watch is polled from <c>SyncEngine.Tick</c> — the same driver
    /// <c>EventPopup.DrainHeldRaises</c> and <c>ReplenishSync.ClientArrivalTick</c> already run on.
    ///
    /// IT IS NOT A QUORUM AND CANNOT BECOME ONE (P13). Every term is this peer's own: its own record, its
    /// own site graph, its own view queue. It counts no peers, waits for no acknowledgement and asks nobody
    /// to press anything — a peer whose partners are all AFK opens its squad screen exactly as fast.
    ///
    /// IT DOES NOT YANK, AND IT DOES NOT DOUBLE-QUEUE. <c>GeoscapeView.LaunchMission</c> →
    /// <c>ToDeploymentState</c>:596 QUEUES the screen, and <see cref="WindowOrder"/>'s open-screen hold keeps
    /// it in the queue until this peer is back on the map — the owner's 2026-08-07 ruling, unchanged.
    /// <see cref="MissionEncounterNav.ShouldOpenDeployment"/> and
    /// <see cref="MissionEncounterNav.AlreadyHeadedForDeployment"/> are REUSED rather than re-derived, so the
    /// peer that already answered (or already got there through the funnel) is never queued twice.
    ///
    /// AND IT WATCHES ONLY WINDOWS IT WAS SHOWN. <see cref="Watch"/> is armed from the raise itself and only
    /// when some choice can start a mission, so the sites that sprout missions all over the geoscape all game
    /// long can never navigate anybody: a mission this peer was not offered has no watch to fire.
    /// </summary>
    internal static class MissionArrivalNav
    {
        private sealed class Watched
        {
            /// <summary>NULL = there is no event behind this watch. The answer term is then THIS PEER'S OWN
            /// CLICK — a haven infiltration the player confirmed himself (<see cref="HavenMissionGate"/>) —
            /// which is already true by the time the watch is armed, so <see cref="Step"/> skips the record and
            /// choice terms and waits on nothing but its own <c>site.ActiveMission</c>.</summary>
            internal string EventId;
            internal string SiteRef;
            internal string VehicleRef;
            internal bool AutoOpenEntitled;
            /// <summary>Which <c>GeoscapeEventRecord.TriggerCount</c> THIS window is answered from —
            /// <c>EventPopup.RaiseTriggerCount</c> read off the live record at arm time, the same number
            /// <c>EventPopup</c> binds to the window itself. Without it the watch read a record left over
            /// from an EARLIER trigger as this window's answer: live 2026-08-11, the host re-offered
            /// <c>PROG_SY0_MISS</c> (<c>answeredFrom=#2</c>) and one client still held <c>Completed#1</c>
            /// when the raise landed (<c>PP-Instance3 11348</c>) — it skipped the dialog and went straight
            /// to the preparation screen, while the client whose <c>Reset#1</c> delta had already landed
            /// (<c>PP-Instance2 11430</c>) got the dialog. Two peers, two stages, one raise.</summary>
            internal int RaiseTriggerCount;
            internal float ResolvedAt;   // realtimeSinceStartup of the first tick the record read Completed
        }

        // Bounded like EventPopup._held for the same reason: a peer sees a handful of mission windows, and a
        // map that only grows is a leak wearing a feature's name. Keyed by event id — a re-raise replaces.
        private const int MaxWatched = 32;

        /// <summary>How long a resolved mission event may wait for its <c>ActiveMission</c> to arrive before
        /// the watch gives up LOUDLY. The structural create rides the same diff cycle as the record delta, so
        /// this is orders of magnitude over the real gap — long enough to cover a peer that is mid-load, short
        /// enough that a mission which is never coming does not sit here for the rest of the session.</summary>
        internal const float ArrivalWindowSeconds = 60f;

        private static readonly Dictionary<string, Watched> _watched = new Dictionary<string, Watched>();

        internal static void Reset() { _watched.Clear(); }

        /// <summary>Arm the watch for a window this peer was actually shown and, for automatic navigation,
        /// explicitly addressed by the host. A remote client's unicast never arms the host.</summary>
        internal static void Watch(string eventId, GeoscapeEventData data, string siteRef, string vehicleRef,
                                   bool autoOpenEntitled = false)
        {
            if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(siteRef)) return;
            if (!EventPopup.AnyChoiceStartsMission(data)) return;
            Arm(eventId, eventId, siteRef, vehicleRef, 0f, autoOpenEntitled);
        }

        /// <summary>THE EVENTLESS ARM (2026-08-08). A haven infiltration has no <c>GeoscapeEvent</c> anywhere in
        /// it: the player pressed a button on the haven panel, <see cref="HavenMissionGate"/> blocked the local
        /// mint and asked the host, and the ONLY thing left to wait for is the host's mission landing on this
        /// peer's own site. So the answer term — "somebody resolved the window" — is not deferred, it is
        /// ALREADY TRUE, and the clock starts HERE, at the click, rather than at a record that will never exist.
        ///
        /// A repeat press must not extend the bound, or a player leaning on the button could wait forever: an
        /// existing watch keeps its ORIGINAL <c>ResolvedAt</c>, so the 60 s give-up is measured from the first
        /// click on that site and stays monotone.</summary>
        internal static void WatchHavenMission(string siteRef, string vehicleRef)
        {
            if (string.IsNullOrEmpty(siteRef)) return;
            string key = "haven:" + siteRef;
            float startedAt = Time.realtimeSinceStartup;
            Watched prior;
            if (_watched.TryGetValue(key, out prior) && prior != null && prior.ResolvedAt > 0f)
                startedAt = prior.ResolvedAt;
            Arm(key, null, siteRef, vehicleRef, startedAt, true);
        }

        private static void Arm(string key, string eventId, string siteRef, string vehicleRef, float resolvedAt,
                                bool autoOpenEntitled)
        {
            if (!_watched.ContainsKey(key) && _watched.Count >= MaxWatched)
            {
                MpLog.LogWarning("[MP][mission] not watching '" + key + "' for its squad screen — " +
                                 MaxWatched + " mission windows are already being watched, which means the " +
                                 "watches are not retiring; this peer can still reach the mission from the " +
                                 "aircraft's Launch button");
                return;
            }
            var live = eventId == null ? null : EventPopup.LiveRecord(eventId, null);
            _watched[key] = new Watched
            {
                EventId = eventId, SiteRef = siteRef, VehicleRef = vehicleRef, ResolvedAt = resolvedAt,
                AutoOpenEntitled = autoOpenEntitled,
                RaiseTriggerCount = live == null ? 0
                    : EventPopup.RaiseTriggerCount(live.State, live.TriggerCount)
            };
        }

        /// <summary>PURE (RailCheck L197). Has the mission this window promised ARRIVED on this peer?
        /// Both halves are replicated state that converges on its own — the answer (whoever gave it) and the
        /// mission the host minted from it.</summary>
        internal static bool MissionHasArrived(bool answerResolved, bool choiceStartsMission, bool missionRunnable)
            => answerResolved && choiceStartsMission && missionRunnable;

        /// <summary>Only the addressed non-host peer owns the one automatic local navigation.</summary>
        internal static bool MayAutoOpen(bool autoOpenEntitled, bool isHost)
            => autoOpenEntitled && !isHost;

        /// <summary>PURE (RailCheck L197). Has a resolved window waited too long for a mission that is never
        /// coming? Monotone in <paramref name="now"/> and bounded by a constant, so it cannot become an
        /// unbounded wait — the same argument <c>WindowOrder.SettleExpired</c> rests on.</summary>
        internal static bool ArrivalGivenUp(float resolvedAt, float now)
            => resolvedAt > 0f && now - resolvedAt >= ArrivalWindowSeconds;

        /// <summary>PURE (RailCheck L272). IS THE ANSWER TERM SETTLED for this watch? The watch has two terms —
        /// "somebody answered" and "the mission arrived" — and only the first one differs between the arms.
        ///
        /// An EVENT-BACKED watch waits for a record to read <c>Completed</c> with a real choice, because the
        /// window may still be open on somebody's screen. An EVENTLESS one has no record and never will: it
        /// was armed by <see cref="HavenMissionGate"/> from THIS PEER'S OWN CLICK on a haven, and a click that
        /// has already happened cannot be waited for. Asking <c>EventPopup.LiveRecord</c> about a null id
        /// would leave that watch waiting on a record nothing will ever write — the screen would simply never
        /// open, silently, which is the failure mode this whole seam exists to end.
        ///
        /// Both arms still wait on the SECOND term (<see cref="MissionHasArrived"/>) and both are still bounded
        /// by <see cref="ArrivalGivenUp"/>; the eventless arm's clock is stamped when it is armed, at the
        /// click, so the 60 s give-up covers it exactly as it covers a resolved event.</summary>
        internal static bool AnswerIsIn(string eventId, bool recordResolved)
            => eventId == null || recordResolved;

        // Kept as a pure compatibility seam for the durable occurrence laws. MissionArrivalNav no longer
        // mints one itself; DeployPrep owns the stable per-mission publication identity.
        internal static string HostTrigger(string existing) =>
            string.IsNullOrEmpty(existing) ? "arrival:" + Guid.NewGuid().ToString("N") : existing;

        /// <summary>Driven from <c>SyncEngine.Tick</c>. One decision per watched window per frame; every exit
        /// retires the watch or says why it is still waiting.</summary>
        internal static void Tick(NetworkEngine engine)
        {
            if (_watched.Count == 0) return;
            if (engine == null || !engine.IsActiveSession) { _watched.Clear(); return; }
            var geo = GameUtl.CurrentLevel() == null
                ? null : GameUtl.CurrentLevel().GetComponent<GeoLevelController>();
            var view = geo?.View;
            if (view == null) return;   // no geoscape to navigate yet; the watch keeps waiting

            // Snapshot: the loop retires entries as it goes.
            foreach (var id in new List<string>(_watched.Keys))
            {
                try { Step(engine, geo, view, id, _watched[id]); }
                catch (Exception ex)
                {
                    MpLog.LogError("[MP][mission] arrival watch for '" + id + "' failed and remains armed for " +
                                   "a retry — no durable occurrence or native carrier is discarded: " + ex);
                }
            }
        }

        private static void Step(NetworkEngine engine, GeoLevelController geo, GeoscapeView view,
                                 string key, Watched w)
        {
            int idx = -1;
            bool recordResolved = false;
            List<GeoEventChoice> choices = null;
            // THE ANSWER TERM (AnswerIsIn). With an event behind the watch it is somebody's resolved record;
            // without one (a haven infiltration this peer confirmed itself) it is the click, already true — so
            // the record and choice terms are never even LOOKED UP for that arm, because there is no record to
            // look up. Everything below is shared, and the eventless arm's clock was stamped when it was armed.
            if (w.EventId != null)
            {
                var rec = EventPopup.LiveRecord(w.EventId, null);
                choices = geo.EventSystem?.GetEventByID(w.EventId, canFail: true)?.GeoscapeEventData?.Choices;
                idx = rec == null ? -1 : rec.SelectedChoice;
                // ...FROM THIS WINDOW'S OWN TRIGGER. IsResolvedForRaise is the same trigger-count test
                // EventPopup already uses to decide whether a window's buttons are frozen (L44); reusing it
                // here is what stops a stale Completed record from an EARLIER trigger being read as the
                // answer to a RE-OFFER (see Watched.RaiseTriggerCount).
                recordResolved = rec != null && rec.State == GeoscapeEventRecordState.Completed &&
                                 EventPopup.IsResolvedForRaise(rec.State, rec.TriggerCount,
                                                               w.RaiseTriggerCount, false) &&
                                 choices != null && idx >= 0 && idx < choices.Count;
            }
            if (!AnswerIsIn(w.EventId, recordResolved)) return;  // still open on somebody's screen
            if (w.ResolvedAt <= 0f) w.ResolvedAt = Time.realtimeSinceStartup;

            if (choices != null && !EventPopup.StartsMission(choices[idx]))
            {
                _watched.Remove(key);
                MpLog.Log("[MP][mission] arrival watch for '" + key + "' retired — the answer (choice " + idx +
                          ") starts no mission, so there is no squad screen owed to anybody.");
                return;
            }
            string because = w.EventId != null
                ? "the answer resolved (choice " + idx + ")"
                : "this peer's own haven-infiltration confirm was the answer";

            var site = IdentityResolver.Resolve(geo, w.SiteRef, null) as GeoSite;
            var mission = site?.ActiveMission;
            if (!MissionHasArrived(true, true, mission != null && mission.IsRunnable))
            {
                if (!ArrivalGivenUp(w.ResolvedAt, Time.realtimeSinceStartup)) return;
                _watched.Remove(key);
                MpLog.LogWarning("[MP][mission] arrival watch for '" + key + "' at " + w.SiteRef + " GIVEN UP — " +
                                 ArrivalWindowSeconds + "s after " + because + " the site still has " +
                                 (site == null ? "not resolved at all" : mission == null
                                     ? "no ActiveMission" : "a mission that is not runnable") +
                                 ". " + MissionEncounterNav.NoDeploymentReason(engine.IsHost, mission == null));
                return;
            }

            _watched.Remove(key);
            if (!MayAutoOpen(w.AutoOpenEntitled, engine.IsHost))
            {
                DeployPrep.Announce(mission);
                MpLog.Log("[MP][brief] deployment-door-published '" + key + "' at " + w.SiteRef + " — " +
                          because + "; this peer has no local auto-open entitlement.");
                return;
            }
            var container = IdentityResolver.Resolve(geo, w.VehicleRef, null) as GeoVehicle
                            ?? view.GetVehicleOnSite(site);
            DeployPrep.SuppressNextEntitledEnter(DurableWindowRegistry.StableMissionSubject(mission));
            view.LaunchMission(mission, container);
            MpLog.Log("[MP][brief] entitled-local-arrival opened '" + key + "' at " + w.SiteRef +
                      " exactly once; watch retired before LaunchMission. DeployPrep will be announced by " +
                      "UIStateRosterDeployment.EnterState, not duplicated here.");
        }
    }

    /// <summary>
    /// Sim gating (law 4b) at the mission-CANCEL funnel, the sibling of the launch capture and the reason
    /// a client may now sit on the deployment screen at all. <c>UIStateRosterDeployment.ToPreviousScreen</c>
    /// (:256-268) — the Back button, the Close button and the Cancel key alike — calls
    /// <c>_mission.Cancel()</c> before it pops the screen, and <c>GeoMission.Cancel</c> (GeoMission.cs:253)
    /// writes <c>Site.ActiveMission = null</c> and can <c>Site.DestroySite()</c>. On a projector client
    /// (law 3) that is a structural mutation the diff can NEVER correct — the diff is host-now vs
    /// host-before, so a mission only the client deleted is never mentioned again and the CRC backstop can
    /// only report it (DiffEngine.HandleCrcReport's "still diverged" arm). One peer glancing at the
    /// deployment screen and pressing Back would delete the mission from its own campaign.
    ///
    /// Blocking is also the RIGHT co-op semantic, not merely the safe one: backing out of a screen is a
    /// navigation gesture, and one peer declining to launch must not cancel the mission for the others.
    ///
    /// AND SINCE 2026-08-09 THAT SEMANTIC IS ROLE-SYMMETRIC, because the role was never the point — see
    /// <see cref="Runs"/>. The gate used to wave the HOST through wholesale, so the very deletion `0b1549c`
    /// closed at the mission BRIEF walked straight back in through the squad screen's Back button on the one
    /// peer that is always present. What is refused is now the GESTURE (<see cref="BackingOutOfSquadScreen"/>)
    /// rather than the peer: a host that really means to retire a mission still does, natively, through every
    /// other caller — expiry, KeepEncounter, Complete, a destroyed site — and it reaches every client as the
    /// ordinary structural destroy.
    ///
    /// Discovered, not enumerated: <c>Cancel</c> is VIRTUAL with seven overrides in the shipped assembly
    /// (GeoCustomMission, GeoAncientSiteMission, GeoPhoenixBaseDefenseMission, GeoSabotageZoneMission and
    /// the three Steal* missions), and several do not call <c>base.Cancel()</c> — so a prefix on the base
    /// declaration alone would leave most real missions ungated. Sweeping the type hierarchy covers a DLC's
    /// or a mod's own subclass for free, the same way <c>VehicleGestureGate</c> resolves its rows by name.
    /// Void method; nothing dereferences a blocked call.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionCancelGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary><c>AccessTools.GetTypesFromAssembly</c> and not <c>Assembly.GetTypes()</c>: with other
        /// mods loaded the game assembly reliably has a few types that fail to load, and the raw call
        /// throws <c>ReflectionTypeLoadException</c> — which PatchAll turns into one swallowed warning that
        /// kills every LATER patch in the same pass (RailCheck L23, the same trap an unbound
        /// AccessTools.Method sets). Harmony's helper returns the loadable ones instead.</summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in AccessTools.GetTypesFromAssembly(typeof(GeoMission).Assembly))
            {
                if (t == null || !typeof(GeoMission).IsAssignableFrom(t)) continue;
                var m = t.GetMethod("Cancel", BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                    null, Type.EmptyTypes, null);
                if (m != null && !m.IsAbstract) yield return m;
            }
        }

        /// <summary>PURE (RailCheck L370). Does the GAME'S OWN <c>Cancel</c> run?
        ///
        /// THE HOST ARM IS THE 2026-08-09 FIX, and it is the same rule <see cref="PerPeerModalAnswer.Runs"/>
        /// already applies one funnel over. `0b1549c` made a DECLINED BRIEF per-peer and left the sibling
        /// caller — the squad screen's own Back button — waving the host through, so the identical deletion
        /// simply arrived one screen later. Measured in the 3-peer soak of 2026-08-09: the host opened the
        /// squad screen for S#42 at 02:42:13.537 and backed out; both clients logged
        /// <c>[MP][site] repaint S#42 … activeMission=none</c> at 02:42:17.501/.502, and the host then refused
        /// a client's launch at 02:44:02.782 with "the host's site has no ActiveMission AT ALL". One player
        /// leaving a screen deleted the mission for the team, exactly as before, through the door the brief
        /// fix did not cover.
        ///
        /// THE TERM IS THE GESTURE, NOT THE ROLE. A host that really means to retire a mission still does —
        /// expiry, <c>ShowMissionBriefing</c>:1891's KeepEncounter arm, <c>Complete</c>, a destroyed site —
        /// because only ONE caller is navigation: <c>UIStateRosterDeployment.ToPreviousScreen</c>:256, which
        /// runs <c>_mission.Cancel()</c> as its first statement (:258) while its own screen is still the
        /// current view state. That is what <see cref="BackingOutOfSquadScreen"/> asks.
        ///
        /// NO QUORUM AND NO WAIT (P13): refusing a deletion makes nobody depend on anybody. The declining
        /// peer still leaves — L91 arm (d) pins the reason, that ToPreviousScreen's own
        /// <c>ResetViewState</c>/<c>SwitchToPreviousState</c> + <c>FinishQueriedState</c> sit BESIDE the call
        /// we block and never inside it — and every other peer keeps a mission it can still fly.</summary>
        internal static bool Runs(bool inSession, bool applying, bool isHost, bool backingOutOfSquadScreen)
            => !inSession || applying || (isHost && !backingOutOfSquadScreen);

        /// <summary>Is this cancel the SQUAD SCREEN'S BACK BUTTON? <c>ToPreviousScreen</c> is wired to the
        /// close button (UIStateRosterDeployment.cs:99), the back button (:148) and the Cancel key
        /// (<c>OnCancel</c>:248-253), and all three reach <c>_mission.Cancel()</c> at :258 — BEFORE the state
        /// is popped, so the screen is still <c>GeoscapeView.CurrentViewState</c>:193 and still holds this
        /// very mission (<c>Mission</c>:58). Asking the live screen rather than setting a scope flag keeps
        /// this at the one funnel that already owns the concern instead of adding a second one.</summary>
        private static bool BackingOutOfSquadScreen(GeoMission mission) =>
            mission != null &&
            GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View?.CurrentViewState
                is UIStateRosterDeployment dep && ReferenceEquals(dep.Mission, mission);

        private static bool Prefix(GeoMission __instance)
        {
            var engine = NetworkEngine.Instance;
            bool inSession = engine != null && engine.IsActiveSession;
            bool host = inSession && engine.IsHost;
            // Only asked inside a session: the probe walks the live view, and solo must cost nothing.
            bool backingOut = inSession && BackingOutOfSquadScreen(__instance);
            if (Runs(inSession, SyncApplyScope.Active, host, backingOut)) return true;

            // Never silent (the dominant bug class): say WHOSE cancel was refused and by WHICH arm — the two
            // are different bugs when this is next read. Log-once per site+role; cancelling is a per-mission
            // gesture, not a per-frame one.
            string who = IdentityResolver.RootRef(__instance?.Site) ?? "S#?";
            if (_logged.Add(who + (host ? "|host" : "|client")))
                MpLog.Log("[MP][mission] " + (host ? "HOST" : "CLIENT") + " cancel of the mission at " + who +
                          " BLOCKED — it deletes shared campaign state (Site.ActiveMission/DestroySite, " +
                          "GeoMission.cs:253-265) every peer is playing" + (host
                              ? ", and this one came from the squad screen's own Back button " +
                                "(UIStateRosterDeployment.ToPreviousScreen:256), which is NAVIGATION. The host " +
                                "leaves the screen as it always did and the mission stays flyable for everyone; " +
                                "a host that really means to retire a mission still can, through every other " +
                                "caller"
                              : "; backing out of the deployment screen is navigation, not a cancellation for " +
                                "every peer"));
            return false;
        }
    }

    /// <summary>
    /// A MISSION WITH NO BRIEF ON THIS PEER SAYS SO.
    ///
    /// <c>GeoscapeView.ShowMissionBriefing</c>:1883-1905 ends in <c>if (missionBriefModal != ModalType.None)
    /// OpenModalPersistent(...)</c> — and the <c>None</c> arm is SILENT for a null mission and for a
    /// <c>GeoCustomMission</c> that took the <c>KeepEncounterID</c> exit at :1890. In the captured session one
    /// peer never received a brief at all while the others did, which is what a MOD-PARITY mismatch looks
    /// like from the outside: <c>GetMissionBriefModal</c>:1724 is a type chain other mods patch (TFTV does),
    /// so two peers running different mod sets can disagree about whether a mission has a brief — and the
    /// peer that says "no" opens nothing, ships nothing on 0xB7, and prints nothing.
    ///
    /// A postfix, not a gate: this changes NOTHING (P4c). It only ends the silence, which is this repo's
    /// dominant bug class.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ShowMissionBriefing))]
    internal static class BriefFallthroughIsNamed
    {
        private static readonly MethodInfo BriefModal =
            AccessTools.Method(typeof(GeoscapeView), "GetMissionBriefModal", new[] { typeof(GeoMission) });

        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static void Postfix(GeoscapeView __instance, GeoMission mission)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || BriefModal == null) return;
            try
            {
                if (mission != null && (ModalType)BriefModal.Invoke(__instance, new object[] { mission })
                                       != ModalType.None) return;
                string what = mission == null ? "<null mission>" : mission.GetType().Name;
                if (!_logged.Add(what)) return;
                MpLog.LogWarning("[MP][mission] NO BRIEF WINDOW on this peer for " + what + " at " +
                                 (IdentityResolver.RootRef(mission?.Site) ?? "S#?") + " — GetMissionBriefModal " +
                                 "answered None, so ShowMissionBriefing:1905 opened nothing, 0x" +
                                 SurfaceIds.GeoModalRaise.ToString("X2") + " shipped nothing, and this player " +
                                 "sees no mission brief while other peers may. Either the mission legitimately " +
                                 "has none (a GeoCustomMission that took the KeepEncounterID exit at :1890), or " +
                                 "the peers are running DIFFERENT MODS — that method is a type chain TFTV and " +
                                 "others patch. Logged once per mission type");
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][mission] could not tell whether " +
                               (mission == null ? "<null>" : mission.GetType().Name) +
                               " has a brief window on this peer: " + ex);
            }
        }
    }

    /// <summary>
    /// EVERY PEER ANSWERS THE DEPLOYMENT WINDOW FOR ITSELF, AND ONLY FOR ITSELF.
    ///
    /// THE REPORT (2026-08-08). A player declined a mission brief and the mission was gone for the whole
    /// team; the others kept a window over nothing, and pressing Confirm on it was refused by
    /// <see cref="MissionSync.Validate"/> ("the host's site has no ActiveMission AT ALL"). The route is one
    /// line of the game's own: <c>GeoscapeView.ModalResultCallback</c>:825-826 answers a mission brief's
    /// Cancel with <c>geoMission.Cancel()</c>, and <c>GeoMission.Cancel</c>:253-265 writes
    /// <c>Site.ActiveMission = null</c>, may <c>Site.DestroySite()</c> and replaces <c>Reward</c>.
    ///
    /// <see cref="MissionCancelGate"/> ALREADY BLOCKS THAT — ON A CLIENT ONLY (:1059 lets the host run
    /// native). The destructive call in the report ran on the HOST, which is precisely the peer that gate
    /// waves through. So the missing half is here, at the callback rather than at the model: a NON-CONFIRM
    /// answer to a window of the per-peer class does nothing to anybody, on either role.
    ///
    /// IT IS THE ANSWER THAT IS PER-PEER, NOT THE MISSION. Confirm is untouched and still reaches the
    /// game's own <c>LaunchMission</c>:1043 → this peer's squad screen (host) or the 0xB8 launch intent
    /// (client), so one player still takes the whole team into the battle. Declining means "I am busy
    /// building" and leaves every other peer's window live and answerable. NO QUORUM (P13): nothing here
    /// waits for anybody — a peer that never answers is simply a peer with a window open.
    ///
    /// WHY THE PREDICATE AND NOT A LIST. <see cref="GeoWindowCoverage.IsPerPeerAnswer"/> asks the GAME which
    /// modal a mission's brief/outcome is (<c>GetMissionBriefModal</c>:1724 / <c>GetMissionOutcomeModal</c>
    /// :1800), so all 21 types are covered, TFTV's patch of that method is inherited, and every OTHER
    /// <c>Cancel</c> caller is untouched — <c>ShowMissionBriefing</c>:1890's KeepEncounter arm, mission
    /// expiry, <c>OnSiteMissionCancelled</c>:1930 and the deployment screen's own Back button.
    ///
    /// REACTIVITY: nothing new. The window this refuses to act on is one the peer is closing through the
    /// game's own <c>UIStateGeoModal.FinishDialog</c>:82 → <c>FinishQueriedState</c> →
    /// <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-63, so the copy leaves this peer's screen
    /// exactly as it always did and no other peer's screen is touched at all.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ModalResultCallback))]
    internal static class PerPeerModalAnswer
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>PURE (RailCheck L351). Does this answer RUN? Confirm always does — it is the gesture that
        /// starts the mission and it is gated downstream, not here. Everything else (Cancel, and the Close
        /// the game synthesises at <c>UIStateGeoModal.ExitState</c>:121-123 for a window torn down without a
        /// click) is refused for the per-peer class, because on this class "not now" is a statement about
        /// ONE player and the native arm is a deletion for all of them.</summary>
        internal static bool Runs(bool inSession, bool perPeerClass, bool isConfirm)
            => !inSession || !perPeerClass || isConfirm;

        private static bool Prefix(ModalType modalType, ModalResult result, object modalData)
        {
            var engine = NetworkEngine.Instance;
            bool inSession = engine != null && engine.IsActiveSession;
            if (!inSession) return true;
            try
            {
                bool perPeer = GeoWindowCoverage.IsPerPeerAnswer(modalType, modalData);
                if (!perPeer) return true;
                // Both native arms are suppressed for a durable offer. Confirm may reach LaunchMission only
                // after the successor journal exists; Cancel is only this member's lifecycle and never the
                // native GeoMission.Cancel at ModalResultCallback:825.
                bool resolved = MissionSync.TryResolveDurableMissionOffer(modalData as GeoMission,
                    out var durableStore, out var durableMember, out var durableOffer);
                // A SECOND PEER CONFIRMING THE SAME START. Its own copy was never frozen (owner decision
                // 2026-08-10, GeoWindowCoverage.IsMissionStartConfirmation), so the click is legitimate — but
                // the launch behind it is shared campaign state and must apply once. The successor
                // DeploymentPreparing occurrence is the idempotence key; see RunNativeMissionOfferAnswer.
                bool startCommitted = GeoWindowCoverage.IsMissionStartConfirmation(modalType, modalData) &&
                    MissionSync.MissionStartAlreadyCommitted(DurableInboxSession.ActiveStore,
                        modalData as GeoMission);
                if (MissionSync.RunNativeMissionOfferAnswer(true, true, result == ModalResult.Confirm, resolved, () =>
                    MissionSync.HandleDurableMissionOfferAnswer(durableStore, durableMember, durableOffer,
                        result, modalData as GeoMission), startCommitted)) return true;
                if (startCommitted && result == ModalResult.Confirm)
                {
                    if (_logged.Add(modalType + ":start-already-committed"))
                        MpLog.Log("[MP][mission] '" + modalType + "' confirmed by this peer AFTER the mission " +
                                  "start was already committed — the deployment preparation window is already " +
                                  "in every peer's queue, so this Confirm closes THIS copy and launches " +
                                  "nothing a second time (logged once per type)");
                    return false;
                }
                if (Runs(true, perPeer, false)) return true;
                if (_logged.Add(modalType + ":" + result))
                    MpLog.Log("[MP][mission] '" + modalType + "' answered " + result + " on THIS PEER ONLY — the " +
                              "game's own arm here is GeoMission.Cancel (ModalResultCallback:825-826), which " +
                              "nulls Site.ActiveMission and can DestroySite, i.e. one player declining would " +
                              "delete the mission for the whole team. The window is closed for this peer and " +
                              "stays live and answerable on every other one (logged once per type+result)");
                return false;
            }
            catch (Exception ex)
            {
                // Never block on a question we could not ask: falling through is vanilla behaviour.
                MpLog.LogError("[MP][mission] per-peer answer check threw for '" + modalType + "' — running the " +
                               "game's own callback, so a Cancel here may still cancel the mission for every " +
                               "peer: " + ex);
                return true;
            }
        }
    }
}
