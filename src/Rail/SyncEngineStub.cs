using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// STUB replacing the legacy ~2600-line SyncEngine, which stayed in the quarry (law 10: no legacy
    /// channels/codecs). Keeps the NetworkEngine.Sync seam alive and owns the ONE inbound 0x67-envelope
    /// chokepoint (<see cref="SurfaceRouter"/>); the new rail arms Router hooks per surface.
    /// </summary>
    public sealed class SyncEngine
    {
        private readonly NetworkEngine _engine;
        public readonly SurfaceRouter Router = new SurfaceRouter();

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            // Every client-intent surface (0xAB research / 0xAE manufacture / 0xAF personnel / 0xB0 time
            // / 0xB1 base / 0xB3 equip / 0xB4 event / 0xB5 vehicle) rides the ONE generic intent engine —
            // families register their op tables, the engine owns nonce/dedup/dispatch/reject (idempotent
            // re-registration).
            ResearchSync.RegisterIntents();
            ManufactureSync.RegisterIntents();
            EquipSync.RegisterIntents();
            PersonnelSync.RegisterIntents();
            TimeSync.RegisterIntents();
            FacilitySync.RegisterIntents();
            EventSync.RegisterIntents();
            VehicleSync.RegisterIntents();
            MissionSync.RegisterIntents();
            // 0xB9 window queue — peer autonomy over the host's pushed-window queue, which only ever
            // drains on a click (GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch:60).
            WindowQueueSync.RegisterIntents();
            // 0xBC resupply — the post-mission screen's two writes no other family funnels through
            // (GeoCharacter.RepairItem spends the SHARED wallet; the single-item reload).
            ReplenishSync.RegisterIntents();
            // 0xBE the SHARED-OFFER family — op 1 = the Kaos marketplace's ONE buy op for every kind of
            // goods (the shop's offer list itself rides 0xBF, host→all: it is EXCLUDED from the value rail
            // as bridge-unresolved); op 2 = TradeSync's haven resource exchange, whose stock and wallet ARE
            // ordinary value-rail state. Both ops are registered by this one call — the geoscape band
            // 0xA0-0xBF is fully allocated, so a new shared-stock panel is an op, never a surface.
            MarketplaceSync.RegisterIntents();
            // Tactical arc A2's end-turn (0x81) is an ordinary intent family — same engine, same envelope.
            Multiplayer.Tactical.TacticalTurnSync.RegisterIntents();
            // Arc A3a's per-soldier COMMAND (0x83) — likewise ordinary: ONE op carrying
            // (actorKey, abilityDefGuid, TacticalAbilityTarget), which is the whole generic seam.
            Multiplayer.Tactical.TacticalCommandSync.RegisterIntents();
            // Arc A8b's aim POSE (0x88) — a standing value, not an order, so it takes its own surface pair.
            Multiplayer.Tactical.TacticalAimPoseSync.RegisterIntents();
            // No intents, no surface: mist coverage is pure host→client mod-state riding the value rail
            // as root "M#mist" — same symmetric registration on both peers as "M#cart" (law L59).
            MistSync.Register();
            // Same shape, third mod root: the deployment countdown ("M#deploy") is host→all state on the
            // value rail. Its CANCEL is not a surface of its own either — it is op 2 on 0xB8, registered
            // with the launch op above (the geoscape band 0xA0-0xBF has no free id left).
            DeployCountdown.Register();
            // Geoscape rail surfaces ride the one inbound hook (each returns false for foreign ids):
            // the 0xAD manufacture order channel, the intent engine, and the generic value rail
            // (0xAC DiffEngine deltas → GenericApplier). The peer id feeds the host-side intent dedup.
            // (0xAA research retired 2026-07-26 — research rides 0xAC + 0xAB only.)
            // Tactical fast-path (0x80 turn cursor + mission end). Consulted BEFORE the geoscape hook, which
            // is the whole reason the 0x80-0x9F / 0xA0-0xBF band split is a law (L62): a tactical id minted
            // inside the geoscape band would silently eat that geoscape surface here. The 0x81 end-turn
            // INTENT deliberately does NOT hang off this hook — IntentRail owns every intent surface, and it
            // is armed in the chain below (this hook returns false for it, so it falls straight through).
            SurfaceRouter.TacticalInbound = (peer, surfaceId, payload) =>
                Multiplayer.Tactical.TacticalTurnSync.HandleInbound(_engine, peer, surfaceId, payload)
                || Multiplayer.Tactical.TacticalCommandSync.HandleInbound(_engine, peer, surfaceId, payload)
                || Multiplayer.Tactical.TacticalDamageSync.HandleInbound(_engine, peer, surfaceId, payload)
                || Multiplayer.Tactical.TacticalAimPoseSync.HandleInbound(_engine, peer, surfaceId, payload)
                // 0x89 native tactical entry (law L103, EXPERIMENT — NativeTacticalEntry.Enabled defaults to
                // false). Registered unconditionally so the id is CLAIMED in the tactical band whether the
                // experiment is on or off: an unclaimed 0x89 would fall through to the geoscape hook.
                || Multiplayer.Tactical.NativeTacticalEntry.HandleInbound(_engine, peer, surfaceId, payload)
                // 0x8A context-help hint (law L130). The ONE tactical surface that is genuinely
                // bidirectional: a hint fires off each peer's own vision, so any peer may be the sender and
                // the host relays what a client cannot address (its fellow clients).
                || Multiplayer.Tactical.HintMirror.HandleInbound(_engine, peer, surfaceId, payload);
            Router.GeoscapeInbound = (peer, surfaceId, payload) =>
                ManufactureSync.HandleInbound(_engine, peer, surfaceId, payload)
                || DurableWindowRegistry.HandleRoutedPresentation(_engine, peer, surfaceId, payload)
                // 0xC0 clock-phase probe (diagnostic band). Registered unconditionally so the id is CLAIMED
                // whether MpDiag is on or off — an unclaimed surface would fall through to the value rail.
                // Client-side inside, and it applies nothing: it logs one [MP][clockphase] line.
                || ClockPhaseDiag.HandleInbound(_engine, peer, surfaceId, payload)
                || IntentRail.HandleInbound(_engine, peer, surfaceId, payload)
                || GenericApplier.HandleInbound(_engine, peer, surfaceId, payload);
        }

        /// <summary>Unified 0x67 envelope inbound (routed by NetworkEngine.RouteMessage).</summary>
        public void OnSyncEnvelope(ulong senderPeerId, byte[] payload) => Router.OnInbound(senderPeerId, payload);

        // Lifecycle seams NetworkEngine / SessionManager / SaveTransferCoordinator drive.
        // DetachAllChannels = full session teardown (seq streams reset); ResetForReloadBoundary =
        // mid-session reload (rca-3 contract: geoscape refs dropped, seq/nonce counters PERSIST so
        // post-reload deltas keep applying).
        public void Tick()
        {
            // RailCost: one always-on cost line per 10 s (see RailCost). The steps charged by name are the
            // ones that can plausibly own a frame; everything else is covered by the tick total, and
            // frameMax is the control that says whether a hitch is the rail's at all.
            long t0 = RailCost.Now(), t = t0;
            // TURN-EPOCH GATE pump (law L96, see SurfaceRouter.ClientBehindTurnEdge). FIRST in the tick: a
            // backlog held for this peer's own turn edge must be applied before anything else this frame
            // reads the model. Inert on the host and whenever nothing is held.
            int forcedLate = Router.ReleaseHeld();
            if (forcedLate > 0)
            {
                UnityEngine.Debug.LogError("[Multiplayer][tac] turn-epoch hold CEILING reached — " + forcedLate +
                                           " record(s) applied late, after " + SurfaceRouter.HeldFrameCeiling +
                                           " frames, because this peer never crossed the faction-turn edge the " +
                                           "host announced. Applying beats dropping, but the local turn machine " +
                                           "is stuck and the peers are on different turns. Asking the host for a " +
                                           "full actor resnapshot — these records landed in the wrong epoch and " +
                                           "nothing else re-converges what they wrote.");
                // The recovery the discrete streams already own (0x83 op 2), reused rather than reinvented: it
                // re-ships every keyed actor's position/HP/AP/WP/dead flag, which is exactly the state a
                // backlog applied in the wrong epoch can have corrupted. Rate-limited by construction — the
                // ceiling can only be reached once per hold.
                IntentRail.Send(SurfaceIds.TacCommandIntent,
                                Multiplayer.Tactical.TacticalDamageSync.OpIntentResnap,
                                "resnapshot after a turn-epoch hold ceiling");
            }
            ManufactureSync.HostTick(_engine);
            // client-only in effect: release the ONE held appearance edit once the player stopped moving the
            // customization control. A trailing-edge debounce has nothing to ride out to on its own, so the
            // flush lives here rather than on the screen's ExitState — the held value then lands whether or
            // not that screen is ever closed the way the state machine expects.
            PersonnelSync.CustomizeTick();
            MistSync.Tick(_engine); // host: recompute the "M#mist" payload; client: hand it to the native loader
            // host-only inside: one decrement per real second of the deployment countdown, and the launch it
            // releases. Driven HERE and not from any screen, so it completes whether or not a single peer is
            // looking at it — the "waits on no human" half of the no-quorum mandate (P13).
            DeployCountdown.HostTick(_engine);
            t = RailCost.Charge("mist", t);
            DiffEngine.HostTick(_engine);
            t = RailCost.Charge("walk", t);
            TimeSync.ClientTick(_engine); // client-only inside: TimeAnchor drift enforcement (~1 Hz)
            // host-only inside, and OFF unless MpDiag.On: ship the host clock reading so the client can
            // print the ABSOLUTE phase error nothing else in this repo can see (0xC0, ClockPhaseDiag).
            // Its first line is `if (!MpDiag.On) return;`, so a normal session pays one static bool read.
            ClockPhaseDiag.HostTick(_engine);
            // client-only inside: release the client's PLAYER-faction turn once the host has left it. A
            // STANDING condition, not a one-shot in the applier — PlayTurnCrt clears _endTurnRequested on
            // its own first line, so a flag set a frame too early is silently erased and parks the client.
            Multiplayer.Tactical.TacticalTurnSync.ClientTick(_engine);
            // host-only inside: ship the turn-edge settle sweep once the host's OWN faction turn has actually
            // started (IsPlayingTurn), so it carries post-AP-restore numbers instead of last turn's leftovers.
            // See TacticalTurnSync.HostSweepTick — this is what stopped the epoch gate from replaying stale AP
            // over every client's own restore.
            Multiplayer.Tactical.TacticalTurnSync.HostSweepTick(_engine);
            // client-only inside: apply each host settle once ITS actor stops executing. Standing, not
            // one-shot at arrival — a settle that lands while this peer is still playing the mirrored move
            // would be overwritten by that move's own navigation and vanish with no log line.
            Multiplayer.Tactical.TacticalCommandSync.ClientTick(_engine);
            // host-only inside: release an order HELD because the same peer's previous order for that soldier
            // was still animating here. The acting peer plays speculatively and is therefore always a whole
            // animation ahead of us; refusing that follow-up is what made melee deal no damage (2026-08-01).
            Multiplayer.Tactical.TacticalCommandSync.HostTick(_engine);
            // EVERY peer, host and client alike: relay the enter-play weapon selections that were raised
            // before this peer could name their actor, on the first frame after the battle key map exists.
            // Here and not inside BuildBattleKeys — that builder hangs off TacNewTurnHook.Postfix, a postfix
            // on a MODEL method, and L19 condemns any intent reachable from one.
            Multiplayer.Tactical.TacticalCommandSync.FlushPendedSelections();
            Multiplayer.Tactical.TacticalDamageSync.ClientTick(_engine);   // A3b: emit an armed 0x84-gap resnapshot request
            // host-only inside: A4 ships a death no damage record carried (a status kill, a scripted one, a
            // mod's). Deferred by one frame ON PURPOSE — inline it could overtake the damage stream it must
            // stay behind, and a death whose corpse manifest arrives before the corpse is a silent drop.
            Multiplayer.Tactical.TacticalActorLifecycle.HostTick(_engine);
            t = RailCost.Charge("tac", t);
            // client-only inside: replay one raise that arrived while this peer had no GeoscapeView to put it
            // in (still in tactical, mid-load). Not a pump over the mirrored records — the 1 Hz record scan
            // deleted 2026-07-30 is not coming back; this drains only raises 0xB6 actually DELIVERED here.
            EventPopup.DrainHeldRaises(_engine);
            // BOTH roles inside: open the pre-mission squad screen from the MISSION'S ARRIVAL rather than
            // from this peer's own dialog teardown. The peer that did not answer the event reached neither
            // on 2026-08-07 — see MissionArrivalNav for why the local funnel could not be the trigger.
            MissionArrivalNav.Tick(_engine);
            // client-only inside, and the same shape of problem one screen over: the post-mission resupply
            // gate is asked at UIStateInitial.EnterState, a frame or two before the host's own post-mission
            // writes land on 0xAC. Re-asks the GAME'S own GetMissingItems() over a bounded window.
            // client-only inside, and it must stay AHEAD of the line below: a peer whose level went live
            // after the host's post-mission batches already flew has none of that state, and this is the one
            // resync trigger that does not need an inbound message to fire. The resupply re-ask polls
            // GetMissingItems() over a bounded window, so the resend has to be requested before it starts.
            GenericApplier.ClientMissedBatchTick(_engine);
            ReplenishSync.ClientArrivalTick(_engine);
            GenericApplier.ClientCrcTick(_engine); // client-only inside: law-7 drift backstop, one root per second
            // (it charges itself per ROOT — the name in the log says which root cost the frame)
            // (No event pump: an event WINDOW is a live host→client 0xB6 raise, not a derivation over the
            // mirrored records — a peer that was not in the session when it fired never sees it. The 1 Hz
            // record-scan pump that used to live here is what buried every joiner under the campaign's whole
            // event history; deleted 2026-07-30 with the derivation it drove.)
            // Law 11 UNIVERSAL: flush one open-screen re-enter per frame if anything marked dirty —
            // client mirror batches AND host post-intent reseeds (EquipSync/PersonnelSync) both land here.
            t = RailCost.Now();
            OpenUiRepaint.FlushIfDirty();
            RailCost.Charge("repaint", t);
            RailCost.EndFrame(t0);
        }

        public void DetachAllChannels()
        {
            IntentRail.Reset();
            ResearchSync.Reset();
            ManufactureSync.Reset();
            EquipSync.Reset();
            MistSync.Reset();
            VehicleSync.Reset();
            TimeSync.Reset();
            DiffEngine.Reset();
            GenericApplier.Reset();
            EventPopup.Reset();   // 0xB6 raise seq stream (teardown only — see EventPopup.Reset)
            MissionArrivalNav.Reset();  // a watch belongs to the session whose sites it names
            GeoModalMirror.Reset();  // 0xB7 modal raise seq stream, same teardown-only contract
            CutsceneMirror.Reset();  // 0xBA cutscene raise seq stream, same teardown-only contract
            MarketplaceSync.Reset();  // 0xBF offer-list seq stream, same teardown-only contract
            MissionOutcomeMirror.Reset();  // 0xBB reward raise seq stream + any un-consumed stash
            ReplenishSync.Reset();  // the post-mission re-ask count-down must not survive into the next session
            Multiplayer.Tactical.TacticalTurnSync.Reset();  // 0x80 seq + the client's turn cursor / mission-over flag
            Multiplayer.Tactical.TacticalCommandSync.Reset();  // 0x82 seq + pending settles + the per-battle "not covered" notices
            Multiplayer.Tactical.TacticalDamageSync.Reset();   // 0x84 seq + gap cursor + the mirror-apply scope depth
            GeoWindowCoverage.Reset();  // per-session "announced once" set, so a gap is loud in EVERY session
            RailOrdinal.Reset();        // the cross-surface order key stream, same per-session contract as the seqs
            // Rail statics that survive an engine teardown and had no home in this aggregate until the
            // SessionEnd seam went in. Kept HERE, not in SessionEnd: this is the one full-teardown reset
            // list, and TearDown (which SessionEnd drives) is what calls it.
            OpenUiRepaint.Reset();
            SyncApplyScope.Reset();
        }

        public void ResetForReloadBoundary()
        {
            ResearchSync.ResetForReloadBoundary();
            ManufactureSync.ResetForReloadBoundary();
            EquipSync.ResetForReloadBoundary();
            TimeSync.ResetForReloadBoundary();
            DeployCountdown.ResetForReloadBoundary(); // same mod-root contract, same reason as the line below
            MistSync.ResetForReloadBoundary(); // BEFORE DiffEngine: the mod root must be empty when the
                                               // post-reload baseline snapshot is taken (see its remark)
            DiffEngine.ResetForReloadBoundary();
            GenericApplier.ResetForReloadBoundary();
        }
        public void ResetIntentDedupForPeer(ulong peerId) => IntentRail.ResetIntentDedupForPeer(peerId);

        /// <summary>Deliberate no-op: the wallet is ordinary covered state and rides the value rail.</summary>
        public void BroadcastFullWallet() { }

        /// <summary>The HOST re-seed seam, driven by both join paths (SaveTransferCoordinator.OnJoinReady
        /// for a mid-session joiner that reached the live geoscape, HostReseedAfterReveal after a reload)
        /// — both host-guarded at the call site. GAME roots ride the save transfer (law 1), but two things
        /// do NOT: MOD roots (IdentityResolver mod-root contract — the host's M#cart can hold staged items
        /// the joiner's empty store never learns, and dict deltas ship per-subkey so it never converges),
        /// and the kind-id table (DiffEngine._sentKinds is per-SESSION, not per-peer — a joiner would get
        /// deltas whose kindIds it was never taught). The joiner cannot heal itself either: its own
        /// gap-resync is suppressed on the first delta it ever sees (GenericApplier "_lastSeq != 0").
        /// ONE full resend fixes all of it generically — every root, every future mod root.</summary>
        public void BroadcastAllChannels() => DiffEngine.RequestFullResend();
    }
}
