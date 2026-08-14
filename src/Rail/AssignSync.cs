using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using Base.Serialization.General;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// TFTV BaseRework's ASSIGNMENTS screen as mod-state (root "M#assign", the fifth one) — the personnel
    /// assignment table and the recruit-training sessions, mirrored host→all on the ordinary 0xAC value rail.
    ///
    /// WHY IT NEEDS A ROOT AT ALL. TFTV keeps this state in its OWN statics, not in the vanilla graph:
    /// <c>PersonnelData._assignments</c> (Data.cs:270), <c>TrainingFacilityRework.RecruitSessions</c>
    /// (TrainingFacilityRework.cs:76) and the two stat dictionaries (:79/:82). It persists only through the
    /// game's mod-save hooks, i.e. inside <c>ModData</c> — which the rail REFUSES by design
    /// (docs/rail-baseline.txt, rationale RailMeta.cs:290-312), with that refusal's own stated exit being
    /// "mods register their own M#&lt;name&gt; rail root". Until then assignments diverged silently, and since
    /// every assigned worker is worth +4 research/production (ResearchAndManufacturing.cs:14,114-117) the two
    /// campaigns' economies drifted apart with nothing in any log.
    ///
    /// DICTIONARIES, NOT PARALLEL LISTS, and that is a wire decision rather than a style one: a
    /// <c>List&lt;T&gt;</c> member emits as ONE whole-field entry, while a <c>Dictionary&lt;int, leaf&gt;</c>
    /// classifies as <c>LeafDict</c> and emits PER-KEY deltas (RailTypes.cs:345-346, <c>IsSimpleKey</c>
    /// accepts int — RailMeta.cs:884-885). Moving one worker then costs one sub-key instead of a full pool
    /// resend. <c>ScrapCartState</c> is the live precedent.
    ///
    /// HOST IS THE ONLY SIMULATOR. The projection below runs once per host tick; on a client every TFTV
    /// automation funnel is muted behind the one door (<see cref="IntentRail.ShouldRunNative"/>) and the four
    /// player gestures are captured as ops 13-15 on the existing 0xAF personnel intent surface — no new
    /// surface id, because the geoscape band 0xA0-0xBF is exhausted and that exhaustion is law-held.
    ///
    /// WHAT IS DELIBERATELY NOT HERE: the created <c>GeoCharacter</c>s (they land in
    /// <c>PhoenixFaction.Characters</c>, which the rail already mirrors as ordinary vanilla entities, so a
    /// finished operative arrives as a normal delta and needs no descriptor codec); the Auto-Assign toggle
    /// (an event-system variable, already covered by <c>EventVariableCapture</c>, the <c>TFTV_HelmetsOff</c>
    /// precedent recorded in L149); and <c>FacilitySlotPools</c>, which live in a
    /// <c>ConditionalWeakTable</c> and are DERIVED — the client recomputes them through TFTV's own
    /// <c>ResyncWorkSlots</c> (P15), which is the single most easily missed step of the apply and the one
    /// L442 exists to hold.
    ///
    /// TFTV IS NEVER REFERENCED BY ASSEMBLY. Every type here is reached with
    /// <c>AccessTools.TypeByName</c>, every patch class is TFTV-gated and therefore listed in
    /// <c>TftvLateBinder._patchClasses</c> (L373 arm (a)). TFTV absent ⇒ fully inert.
    /// </summary>
    internal static class AssignSync
    {
        internal const string RootKey = "M#assign";

        /// <summary>Host walks its instance; on a client this is the generic applier's write target.</summary>
        internal static readonly AssignState State = new AssignState();

        // ─── The TFTV surface, by NAME only (never a reference — see the class remark) ──────────
        internal const string TftvPersonnelTypeName = "TFTV.TFTVBaseRework.PersonnelData";
        internal const string TftvPersonnelInfoTypeName = "TFTV.TFTVBaseRework.PersonnelInfo";
        internal const string TftvAssignmentEnumTypeName = "TFTV.TFTVBaseRework.PersonnelAssignment";
        internal const string TftvTrainingTypeName = "TFTV.TFTVBaseRework.TrainingFacilityRework";
        internal const string TftvWorkersTypeName = "TFTV.TFTVBaseRework.Workers";
        internal const string TftvPanelTypeName = "TFTV.TFTVBaseRework.PersonnelManagementUI";

        // PersonnelAssignment (Data.cs:22-27). Mirrored as a byte; the host re-checks the incoming byte
        // against TFTV's OWN enum before it ever reaches a TFTV method (law 3 — the role is wire input).
        internal const byte RoleUnassigned = 0;
        internal const byte RoleResearch = 1;
        internal const byte RoleManufacturing = 2;
        internal const byte RoleTraining = 3;

        // Intent ops on 0xAF, continuing PersonnelSync's numbering (1-12 are its own).
        internal const byte OpAssignMove = 13;      // [charId:i32][role:u8]      → AssignWorker / UnassignFromWork
        internal const byte OpAssignTrain = 14;     // [charId:i32][spec:key][lvl:i32] → QueueCharacterTrainingAutoFacility
        internal const byte OpAssignFinalize = 15;  // [charId:i32][early:bool]   → FinalizeRecruitTraining

        /// <summary>Every TFTV handle in ONE container, held through a non-MemberInfo field — and that is not
        /// cosmetic. RailCheck L23 sweeps every STATIC <c>MemberInfo</c> field in this namespace and demands
        /// it be bound, which is exactly right for a vanilla handle resolved at cctor time and exactly wrong
        /// here: TFTV loads AFTER this assembly and may not be installed at all, so these are null by design
        /// until the first tick that finds it. The container also makes the bind ALL-OR-NOTHING by
        /// construction — <see cref="_h"/> is published only once every member resolved.</summary>
        private sealed class Tftv
        {
            public Type Personnel, Info, RoleEnum, Training, SessionType, SlotTypeEnum;
            public PropertyInfo Assignments;
            public MethodInfo ResyncWorkSlots, RefreshInfoBar;
            public FieldInfo InfoId, InfoCharacter, InfoAssignment, InfoSpec;
            public FieldInfo Sessions, Applied, Pending;
            public FieldInfo SPersonnelId, SCharacter, SGeoUnitId, SSpec, SStartHour, SDuration,
                             STargetLevel, SCompleted, SStartLevel, SVirtual, SSpPaid, SDismissed;
        }

        private static Tftv _h;
        private static bool _bindLogged;
        private static bool _bindFailed;
        private static bool _mirrorSeen;

        /// <summary>Armed by <see cref="ResetForReloadBoundary"/>, consumed by the first host projection after
        /// it. It cannot be a <c>DiffEngine.ForceReemit</c> call inside the reset itself: SyncEngine runs
        /// <c>AssignSync.ResetForReloadBoundary</c> BEFORE <c>DiffEngine.ResetForReloadBoundary</c>
        /// (SyncEngineStub.cs:276-277), and the latter clears <c>_forcePrefixes</c> — the arm would be wiped
        /// the same frame it was set.</summary>
        private static bool _reemitPending;

        private static Dictionary<string, SpecializationDef> _specsByName;
        private static int _lastAppliedFingerprint, _lastAttemptedFingerprint, _lastUnresolved = -1, _retryTicks;
        private static bool _heldLogged;

        /// <summary>~10 s at 60 fps. Long enough that an ordinary character delta lands inside it (they
        /// arrive within one walk cycle), short enough that a permanently missing id is named while the
        /// player is still looking at the screen it is missing from.</summary>
        private const int RetryTickCeiling = 600;

        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static void Register() => IdentityResolver.RegisterModRoot(RootKey, State);

        /// <summary>Ops 13-15 MERGED into the personnel family's table — <see cref="IntentRail.Register"/>
        /// REPLACES a surface's op table, so this must run after <c>PersonnelSync.RegisterIntents()</c> and
        /// must not be a second <c>Register</c> call (that would silently delete ops 1-12).</summary>
        internal static void RegisterIntents() =>
            IntentRail.RegisterOps(SurfaceIds.GeoPersonnelIntent, new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpAssignMove] = HandleAssignMove,
                [OpAssignTrain] = HandleAssignTrain,
                [OpAssignFinalize] = HandleAssignFinalize,
            });

        /// <summary>Mod-root contract (IdentityResolver.cs:205-206): mod state must be EMPTY at every reload
        /// boundary, or the post-reload BASELINE walk captures a value it then never emits while the client's
        /// own instance stays empty — permanent divergence only a full resend heals. The assignment table is
        /// never empty, so clear it and let the next host tick re-project it: at that instant the client is
        /// correct from the save it just loaded, so nothing is lost in the gap.</summary>
        internal static void ResetForReloadBoundary()
        {
            State.Clear();
            _lastAppliedFingerprint = 0;
            _lastAttemptedFingerprint = 0;
            _lastUnresolved = -1;
            _retryTicks = 0;
            _heldLogged = false;
            _mirrorSeen = false;
            // THE CLEAR IS ONLY HALF THE CONTRACT. Clearing satisfies "empty at the boundary", but the very
            // next host tick re-projects the whole table one line BEFORE the walk (SyncEngineStub.cs:157-159),
            // so an HONEST post-boundary baseline (DiffEngine.cs:1000, same-level save transfer — a join)
            // records the refilled table as "the clients already have this" and emits NOTHING. The client's
            // transferred save carries no TFTV ModData, so its own cleared table never refills and the board
            // freezes for the rest of the campaign. Re-announce the subtree instead; DiffEngine.cs:1006-1008
            // keeps an armed prefix alive across a racing baseline.
            _reemitPending = true;
        }

        internal static void Reset() => ResetForReloadBoundary();

        internal static void Tick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession) return;
            if (!Bind()) return;
            try
            {
                if (engine.IsHost) HostProject(); else ClientApply();
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][assign] tick failed — the ASSIGNMENTS mirror is stale for this frame; " +
                               "the next tick retries: " + ex);
            }
        }

        // ─── The TFTV bind, once, loudly ────────────────────────────────────────────────────────

        /// <summary>Resolve-all-first, and NEVER a partial bind: a decompile-name drift would otherwise leave
        /// half the projection writing nulls into the mirror, which reads on the client as "everybody is
        /// unassigned" — an economy wipe with no exception anywhere. One line either way, once.</summary>
        private static bool Bind()
        {
            if (_h != null) return true;
            // Once PersonnelData is visible, TFTV's assembly has finished loading and this surface cannot
            // acquire the missing members later.  Retrying the full reflection probe every frame used to
            // cost 100-170 ms on TFTV versions whose surface has drifted, freezing every UI window and also
            // inflating main-thread RTT.  Keep the feature inert after the one loud failure instead.
            if (_bindFailed) return false;
            var personnel = AccessTools.TypeByName(TftvPersonnelTypeName);
            if (personnel == null) return false; // TFTV absent or not loaded yet — inert, not an error
            var info = AccessTools.TypeByName(TftvPersonnelInfoTypeName);
            var training = AccessTools.TypeByName(TftvTrainingTypeName);
            var session = training == null ? null : training.GetNestedType("RecruitTrainingSession", All);
            var workers = AccessTools.TypeByName(TftvWorkersTypeName);
            var h = new Tftv
            {
                SlotTypeEnum = workers == null ? null : workers.GetNestedType("FacilitySlotType", All),
                ResyncWorkSlots = AccessTools.Method(personnel, "ResyncWorkSlots"),
                RefreshInfoBar = AccessTools.Method(workers, "RefreshInfoBar"),
                Personnel = personnel,
                Info = info,
                RoleEnum = AccessTools.TypeByName(TftvAssignmentEnumTypeName),
                Training = training,
                SessionType = session,
                Assignments = personnel.GetProperty("Assignments", All),
                InfoId = info?.GetField("Id", All),
                InfoCharacter = info?.GetField("Character", All),
                InfoAssignment = info?.GetField("Assignment", All),
                InfoSpec = info?.GetField("TrainingSpec", All),
                Sessions = training?.GetField("RecruitSessions", All),
                Applied = training?.GetField("_appliedStatLevels", All),
                Pending = training?.GetField("_pendingPostRecruitStatApply", All),
                SPersonnelId = session?.GetField("PersonnelId", All),
                SCharacter = session?.GetField("Character", All),
                SGeoUnitId = session?.GetField("GeoUnitId", All),
                SSpec = session?.GetField("TargetSpecialization", All),
                SStartHour = session?.GetField("StartHour", All),
                SDuration = session?.GetField("DurationHours", All),
                STargetLevel = session?.GetField("TargetLevel", All),
                SCompleted = session?.GetField("Completed", All),
                SStartLevel = session?.GetField("StartLevel", All),
                SVirtual = session?.GetField("VirtualLevelAchieved", All),
                SSpPaid = session?.GetField("SpPaid", All),
                SDismissed = session?.GetField("WasDismissed", All),
            };

            bool ok = h.SlotTypeEnum != null && h.ResyncWorkSlots != null && h.RefreshInfoBar != null &&
                      h.RoleEnum != null && h.Assignments != null && h.InfoId != null && h.InfoCharacter != null &&
                      h.InfoAssignment != null && h.InfoSpec != null && h.Sessions != null && h.Applied != null &&
                      h.Pending != null && h.SPersonnelId != null && h.SCharacter != null && h.SGeoUnitId != null &&
                      h.SSpec != null && h.SStartHour != null && h.SDuration != null && h.STargetLevel != null &&
                      h.SCompleted != null && h.SStartLevel != null && h.SVirtual != null && h.SSpPaid != null &&
                      h.SDismissed != null;
            if (ok) _h = h;
            else _bindFailed = true;
            if (_bindLogged) return ok;
            _bindLogged = true;
            if (ok)
                MpLog.Log("[MP][assign] TFTV BaseRework personnel surface bound — ASSIGNMENTS now rides the " +
                          "\"" + RootKey + "\" mod root.");
            else
                MpLog.LogError("[MP][assign] TFTV BIND FAILED (upstream rename?) — the ASSIGNMENTS screen is " +
                               "NOT replicated: assignments and training sessions diverge silently between " +
                               "peers and the research/production economies drift apart. RailCheck L441's " +
                               "premise arm names the same shape.");
            return ok;
        }

        // ─── HOST: the projection ───────────────────────────────────────────────────────────────

        /// <summary>Pure projection of the three TFTV statics into the mirror, in place. Sorted by id because
        /// TFTV itself iterates <c>Dictionary.Values</c> unsorted where it writes its own snapshot
        /// (Data.cs:791) and where it evicts (:1267) — the wire must not inherit that.
        ///
        /// GHOST SESSIONS ARE MIRRORED VERBATIM. A session whose trainee died is never removed by TFTV and
        /// keeps occupying a slot (<c>AdvanceAllTraining</c>:303 drops only null refs, which a dead
        /// <c>GeoCharacter</c> is not). The client must take the host's set as-is and never reconstruct it.</summary>
        private static void HostProject()
        {
            var live = _h.Assignments.GetValue(null, null) as IDictionary;
            if (live == null) return;

            var liveAssign = new HashSet<int>();
            foreach (var id in SortedIntKeys(live))
            {
                var info = live[id];
                if (info == null) continue;
                liveAssign.Add(id);
                Put(State.AssignRole, id, (byte)Convert.ToInt32(_h.InfoAssignment.GetValue(info)));
                Put(State.AssignSpec, id, (_h.InfoSpec.GetValue(info) as SpecializationDef)?.name ?? "");
            }
            Prune(State.AssignRole, liveAssign);
            Prune(State.AssignSpec, liveAssign);

            // The PRIVATE list, not GetActiveRecruitSessions(): that accessor filters out `Completed`
            // sessions, and a completed-but-not-yet-deployed session is exactly the row the ASSIGNMENTS panel
            // paints a Deploy button for. Projecting through the filter would delete those rows on every
            // client the moment the training clock finished.
            var liveSessions = _h.Sessions.GetValue(null) as IEnumerable;
            var liveSessionIds = new HashSet<int>();
            if (liveSessions != null)
                foreach (var s in liveSessions)
                {
                    if (s == null) continue;
                    int id = (int)_h.SGeoUnitId.GetValue(s);
                    if (!liveSessionIds.Add(id)) continue; // TFTV refuses a second session per unit (:130)
                    Put(State.SessionPersonnelId, id, (int)_h.SPersonnelId.GetValue(s));
                    Put(State.SessionSpec, id, (_h.SSpec.GetValue(s) as SpecializationDef)?.name ?? "");
                    Put(State.SessionStartHour, id, (double)_h.SStartHour.GetValue(s));
                    Put(State.SessionDurationHours, id, (double)_h.SDuration.GetValue(s));
                    Put(State.SessionTargetLevel, id, (int)_h.STargetLevel.GetValue(s));
                    Put(State.SessionStartLevel, id, (int)_h.SStartLevel.GetValue(s));
                    Put(State.SessionVirtualLevel, id, (int)_h.SVirtual.GetValue(s));
                    Put(State.SessionSpPaid, id, (int)_h.SSpPaid.GetValue(s));
                    Put(State.SessionCompleted, id, (bool)_h.SCompleted.GetValue(s));
                    Put(State.SessionWasDismissed, id, (bool)_h.SDismissed.GetValue(s));
                }
            State.PruneSessions(liveSessionIds);

            ProjectIntDict(_h.Applied, State.AppliedStatLevel);
            ProjectIntDict(_h.Pending, State.PendingStatLevel);

            // The other half of the boundary contract (see _reemitPending): announce the re-projected table
            // once, AFTER DiffEngine's own boundary reset has run, so the baseline cannot swallow it.
            if (_reemitPending) { _reemitPending = false; DiffEngine.ForceReemit(RootKey); }
        }

        private static void ProjectIntDict(FieldInfo field, Dictionary<int, int> mirror)
        {
            var live = field.GetValue(null) as IDictionary;
            if (live == null) return;
            var seen = new HashSet<int>();
            foreach (var id in SortedIntKeys(live)) { seen.Add(id); Put(mirror, id, Convert.ToInt32(live[id])); }
            Prune(mirror, seen);
        }

        private static List<int> SortedIntKeys(IDictionary d)
        {
            var keys = new List<int>(d.Count);
            foreach (var k in d.Keys) if (k is int i) keys.Add(i);
            keys.Sort();
            return keys;
        }

        /// <summary>Write only a CHANGED value: the walk diffs per sub-key, and a rewritten-but-equal entry
        /// still costs the comparison it would have skipped.</summary>
        private static void Put<T>(Dictionary<int, T> d, int key, T value)
        {
            T old;
            if (d.TryGetValue(key, out old) && EqualityComparer<T>.Default.Equals(old, value)) return;
            d[key] = value;
        }

        private static void Prune<T>(Dictionary<int, T> d, HashSet<int> live)
        {
            if (d.Count == 0) return;
            List<int> dead = null;
            foreach (var k in d.Keys)
                if (!live.Contains(k)) (dead ?? (dead = new List<int>())).Add(k);
            if (dead != null) foreach (var k in dead) d.Remove(k);
        }

        // ─── CLIENT: the apply ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The mirror onto TFTV's statics, and every step of it is load-bearing:
        ///   1. <c>PersonnelData.Assignments</c> is reconciled IN PLACE — update, add, remove surplus. Never
        ///      cleared: clear-then-refill is the load-shaped pattern this whole design exists to avoid, and
        ///      it would drop the <c>Character</c> references the open panel dereferences.
        ///   2. Ids resolve through the rail's own resolver. An unresolved id is SKIPPED and logged, never
        ///      fatal — the character delta may simply not have landed yet and the next mirror delta retries.
        ///   3. The session list is rebuilt (it is a plain list keyed by nothing the panel holds a reference
        ///      into), with <c>Character</c> and <c>SpecializationDef</c> reattached.
        ///   4. Both stat dictionaries are restored.
        ///   5. <c>ResyncWorkSlots</c> — NOT OPTIONAL. Economy output is computed from
        ///      <c>FacilitySlotPools.UsedSlots</c>, and <c>UsedSlots</c> is only ever derived from
        ///      <c>_assignments</c> here (Data.cs:895-906 → Workers.SetUsedSlots:331). Writing the dictionary
        ///      alone leaves research and production stale — right table, wrong economy, and nothing else in
        ///      the suite would notice. L442 is the arm that does.
        ///   6. <c>Workers.RefreshInfoBar</c> so the used/provided readout and <c>UpdateProduction</c> follow.
        ///
        /// TFTV's own <c>RestoreAssignments</c> is deliberately NOT called: it is not a restore, it runs the
        /// eviction and auto-assign passes and would move people locally. Only its harmless conjunct is
        /// wanted, and step 5 calls that one directly.
        /// </summary>
        private static void ClientApply()
        {
            int fingerprint = State.Fingerprint();
            if (fingerprint == _lastAppliedFingerprint) return;
            var geo = GenericApplier.GeoLevel();
            var phoenix = geo == null ? null : geo.PhoenixFaction;
            if (phoenix == null) return;
            var live = _h.Assignments.GetValue(null, null) as IDictionary;
            if (live == null) return;
            if (State.AssignRole.Count > 0) _mirrorSeen = true;
            // AN EMPTY MIRROR IS NOT NEWS UNTIL IT HAS BEEN FULL ONCE. Before the first "M#assign" delta
            // lands — a joiner, a peer whose host has not projected yet — the root reads as zero rows, and
            // applying that as truth would delete the client's whole table and then ResyncWorkSlots its
            // economy to nothing. Once a non-empty mirror HAS arrived, an empty one is a legitimate host
            // state (the last worker was dismissed) and applies normally.
            if (!AssignAuthority.MirrorIsUsable(_mirrorSeen, State.AssignRole.Count, live.Count))
            {
                // Once: the hold lasts every tick until the root arrives, and the fingerprint gate stays
                // open the whole time (an empty state fingerprints to a non-zero constant).
                if (!_heldLogged)
                    MpLog.Log("[MP][assign] CLIENT is holding an EMPTY \"" + RootKey + "\" mirror off " +
                              live.Count + " live personnel row(s) — the root has not arrived yet, and " +
                              "applying it would wipe the table and zero the economy. Nothing changed; the " +
                              "first real delta applies.");
                _heldLogged = true;
                return;
            }
            _heldLogged = false;

            using (SyncApplyScope.Enter())
            {
                int skipped = 0;
                foreach (var kv in State.AssignRole.OrderBy(x => x.Key))
                {
                    var character = IdentityResolver.Resolve(geo, "U#" + kv.Key, null) as GeoCharacter;
                    if (character == null) { skipped++; continue; }
                    var info = live[kv.Key] ?? Activator.CreateInstance(_h.Info, true);
                    _h.InfoId.SetValue(info, kv.Key);
                    _h.InfoCharacter.SetValue(info, character);
                    _h.InfoAssignment.SetValue(info, Enum.ToObject(_h.RoleEnum, (int)kv.Value));
                    string specName;
                    _h.InfoSpec.SetValue(info, State.AssignSpec.TryGetValue(kv.Key, out specName)
                                                  ? SpecByName(specName) : null);
                    live[kv.Key] = info;
                }
                foreach (var id in SortedIntKeys(live))
                    if (!State.AssignRole.ContainsKey(id)) live.Remove(id);

                int skippedSessions = RebuildSessions(geo);
                RestoreIntDict(_h.Applied, State.AppliedStatLevel);
                RestoreIntDict(_h.Pending, State.PendingStatLevel);

                // Step 5 — the pure recompute. Named HERE, in this method's own IL, on purpose: RailCheck
                // L442 reads exactly that literal, so deleting the line turns the law red.
                Call(_h.ResyncWorkSlots, "ResyncWorkSlots", phoenix);
                Call(_h.RefreshInfoBar, "RefreshInfoBar", phoenix);

                // THE GATE CLOSES ONLY ON A COMPLETE APPLY. A skipped id is an id whose GeoCharacter delta
                // has not landed yet, and that arrival does NOT change AssignState — so committing the
                // fingerprint here would mean the retry the log promises never happens and the row (or the
                // whole training session) is lost for the rest of the campaign.
                // A REPEAT OF AN IDENTICAL RETRY IS NOT A REPAINT. The open ASSIGNMENTS board repaints by
                // DESTROYING and rebuilding its panel (TFTV's RefreshPanel), so marking dirty on every
                // retry tick tears the screen down once per frame for as long as one id stays unresolved.
                // The apply only actually changed something if the mirror moved or a skip resolved.
                // ponytail: a COUNT, not the id set. One id resolving while another disappears in the same
                // tick leaves the count equal and skips that tick's MarkDirty — the model is still applied
                // in full, and the character delta that moved either id marks dirty on its own path. Upgrade
                // to a hash of the unresolved id set if a one-frame stale board is ever actually seen.
                int unresolved = skipped + skippedSessions;
                if (fingerprint != _lastAttemptedFingerprint || unresolved != _lastUnresolved)
                {
                    OpenUiRepaint.MarkDirty();
                    _retryTicks = 0;
                }
                else if (++_retryTicks == RetryTickCeiling)
                    // ponytail: one line at a fixed tick count, not a backoff. The retry itself is cheap and
                    // self-healing; what was missing was ever SAYING that it is not healing. Escalate to a
                    // per-id report if this ever fires for a reason other than a character that never landed.
                    MpLog.LogError("[MP][assign] CLIENT has been retrying the ASSIGNMENTS mirror for " +
                                   RetryTickCeiling + " ticks with " + skipped + " personnel row(s) and " +
                                   skippedSessions + " training session(s) still unresolved. Their " +
                                   "GeoCharacter never arrived on this peer, so those rows and sessions are " +
                                   "missing from the board and the slot pools are computed without them.");
                _lastAttemptedFingerprint = fingerprint;
                _lastUnresolved = unresolved;
                if (AssignAuthority.CommitApplyFingerprint(skipped, skippedSessions))
                    _lastAppliedFingerprint = fingerprint;
            }
        }

        /// <summary>Returns how many sessions were dropped because their trainee has not been mirrored yet —
        /// the apply gate stays open on a non-zero count, or the training would vanish for good.</summary>
        private static int RebuildSessions(GeoLevelController geo)
        {
            var sessions = _h.Sessions.GetValue(null) as IList;
            if (sessions == null) return 0;
            sessions.Clear();
            int skipped = 0;
            foreach (var kv in State.SessionSpec.OrderBy(x => x.Key))
            {
                var character = IdentityResolver.Resolve(geo, "U#" + kv.Key, null) as GeoCharacter;
                if (character == null) { skipped++; continue; }
                var s = Activator.CreateInstance(_h.SessionType, true);
                _h.SGeoUnitId.SetValue(s, kv.Key);
                _h.SCharacter.SetValue(s, character);
                _h.SSpec.SetValue(s, SpecByName(kv.Value));
                _h.SPersonnelId.SetValue(s, Read(State.SessionPersonnelId, kv.Key, kv.Key));
                _h.SStartHour.SetValue(s, Read(State.SessionStartHour, kv.Key, 0d));
                _h.SDuration.SetValue(s, Read(State.SessionDurationHours, kv.Key, 1d));
                _h.STargetLevel.SetValue(s, Read(State.SessionTargetLevel, kv.Key, 1));
                _h.SStartLevel.SetValue(s, Read(State.SessionStartLevel, kv.Key, 1));
                _h.SVirtual.SetValue(s, Read(State.SessionVirtualLevel, kv.Key, 1));
                _h.SSpPaid.SetValue(s, Read(State.SessionSpPaid, kv.Key, 0));
                _h.SCompleted.SetValue(s, Read(State.SessionCompleted, kv.Key, false));
                _h.SDismissed.SetValue(s, Read(State.SessionWasDismissed, kv.Key, false));
                sessions.Add(s);
            }
            if (skipped > 0)
                MpLog.LogWarning("[MP][assign] CLIENT dropped " + skipped + " training session(s) whose " +
                                 "trainee is not on this peer's graph yet. Held, not lost: the apply gate " +
                                 "stays open until every one of them resolves.");
            return skipped;
        }

        /// <summary>Each mirror dictionary is an INDEPENDENT wire entry, so a key present in one and absent
        /// from another is an ordinary mid-flight state, not a defect. Read defensively, never index blind.
        /// The one deliberate default is <c>DurationHours = 1</c>: TFTV divides by it
        /// (<c>ProgressFraction</c>) and a zero would make every progress bar NaN.</summary>
        private static T Read<T>(Dictionary<int, T> d, int key, T fallback)
        {
            T v;
            return d.TryGetValue(key, out v) ? v : fallback;
        }

        /// <summary>One TFTV static call, and it is never a silent no-op: the handle is part of the
        /// all-or-nothing bind, so a null here means the bind contract itself broke and the apply just
        /// finished writing a table nobody recomputed anything from.</summary>
        private static void Call(MethodInfo m, string name, object arg)
        {
            if (m == null)
            {
                MpLog.LogError("[MP][assign] " + name + " did not bind — the mirrored assignment table is " +
                               "written but NOT recomputed into the slot pools, so this peer's research and " +
                               "production stay on their old numbers.");
                return;
            }
            m.Invoke(null, new object[] { arg });
        }

        private static void RestoreIntDict(FieldInfo field, Dictionary<int, int> mirror)
        {
            var live = field.GetValue(null) as IDictionary;
            if (live == null) return;
            live.Clear();
            foreach (var kv in mirror.OrderBy(x => x.Key)) live[kv.Key] = kv.Value;
        }

        /// <summary>By NAME, because that is what TFTV's own records carry (<c>PersonnelAssignmentSave</c>,
        /// <c>RecruitTrainingSessionSave</c>) and what its <c>DefCache</c> is keyed on. Resolved off the
        /// game's own <c>DefRepository</c> rather than TFTV's cache, so the lookup needs no TFTV bind and
        /// answers the same instance.</summary>
        private static SpecializationDef SpecByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_specsByName == null)
            {
                var repo = GameUtl.GameComponent<DefRepository>();
                if (repo == null) return null;
                _specsByName = new Dictionary<string, SpecializationDef>(StringComparer.Ordinal);
                foreach (var d in repo.GetAllDefs<SpecializationDef>())
                    if (d != null && !_specsByName.ContainsKey(d.name)) _specsByName[d.name] = d;
            }
            SpecializationDef spec;
            return _specsByName.TryGetValue(name, out spec) ? spec : null;
        }

        // ─── REPAINT (P11): the ASSIGNMENTS screen's own read-direction rebuild ──────────────────

        /// <summary>The <c>UiNativeRepaint.Table</c> entry for <c>UIStateRosterRecruits</c>. TFTV rebuilds
        /// that screen as ASSIGNMENTS, and its own <c>RefreshPanel()</c> (UI/Panel.cs:27-34) is exactly the
        /// read-direction refresh P11 asks for — it destroys and re-creates the panel from
        /// <c>_cachedState</c>, writing nothing back into the model. Resolve-all-first: if TFTV is absent or
        /// the method moved, answer FALSE and let the universal Exit+Enter fallback run rather than
        /// half-repainting. Reached by NAME from inside the table lambda, so no rail-side reseed helper ever
        /// acquires a load-order dependency on TFTV (L149 arm (c)).</summary>
        internal static bool RepaintAssignmentsPanel()
        {
            var panel = AccessTools.TypeByName(TftvPanelTypeName);
            var refresh = AccessTools.Method(panel, "RefreshPanel");
            // _cachedState IS the resolve-all-first test, not a nicety. RefreshPanel destroys the panel and
            // only rebuilds it when that field is set (UI/Panel.cs:27-34), and CreatePersonnelPanel's own
            // first line is `if (!BaseReworkEnabled) return` (:38). With BaseRework off — or before the
            // screen has ever been entered — a `true` here would suppress the universal Exit+Enter fallback
            // and leave the Recruits screen with NO repaint at all.
            if (refresh == null || AccessTools.Field(panel, "_cachedState")?.GetValue(null) == null) return false;
            refresh.Invoke(null, null);
            return true;
        }

        // ─── CLIENT: the mutes. One door, no local copy of the decision. ─────────────────────────

        /// <summary>THE one gate every mute below returns. A client outside an apply scope simulates nothing
        /// on this surface; the host does, and its result arrives as a mirror.
        /// <see cref="AssignAuthority.ClientMutesAutomation"/> is the pure statement of the same decision and
        /// is what L441 executes.</summary>
        private static bool RunNativeAutomation() => IntentRail.ShouldRunNative();

        /// <summary>The STRICTER gate, for the three funnels that SIMULATE rather than present — see
        /// <see cref="AssignAuthority.ClientMutesSimulation"/> for why law 8's apply-scope carve-out is
        /// exactly wrong there. Still one decision, still stated once, and still executed by L441; it is the
        /// same door minus one term, not a second opinion about who the host is.
        ///
        /// It differs from <see cref="IntentRail.ShouldRunNative"/> in EXACTLY two things, both deliberate:
        /// it drops the apply-scope carve-out (the whole point), and it does not call
        /// <c>DiffEngine.FlushOnHostGesture</c>. The flush exists so a HOST GESTURE ships in the same frame;
        /// nothing here is a gesture — every one of these three funnels is automation being refused — so
        /// there is no host write to flush.</summary>
        private static bool RunNativeSimulation()
        {
            var engine = NetworkEngine.Instance;
            return !AssignAuthority.ClientMutesSimulation(engine != null && engine.IsActiveSession,
                                                          engine == null || engine.IsHost);
        }

        /// <summary>Auto-assign, mass eviction and row insertion — three funnels, one prefix. Muting
        /// <c>TryAutoAssignUnassignedPersonnel</c> alone covers its six callers (Data.cs:517,701,886,1197,
        /// Workers.cs:37, Panel.cs:159); <c>EnforceLivingCapacity</c> evicts on housing pressure; and
        /// <c>AttachCharacter</c> inserts a row on every character attach, which on a client is a mirrored
        /// arrival, not a local event — and its own tail calls the auto-assign funnel, INSIDE the delta
        /// apply scope, which is why these three take <see cref="RunNativeSimulation"/> rather than the
        /// plain door.</summary>
        [HarmonyPatch]
        internal static class TftvPersonnelAutomationMute
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvPersonnelTypeName) != null;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName(TftvPersonnelTypeName);
                foreach (var n in new[] { "TryAutoAssignUnassignedPersonnel", "EnforceLivingCapacity", "AttachCharacter" })
                {
                    var m = AccessTools.Method(t, n);
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix() => RunNativeSimulation();
        }

        /// <summary>TFTV's own postfix on <c>GeoPhoenixFaction.RegenerateNakedRecruits</c> (Data.cs:966) mints
        /// units through <c>GenerateRandomUnit</c> (:996) — i.e. CLIENT-LOCAL ids, which must never exist.
        /// The host's regeneration mirrors back as ordinary state.</summary>
        [HarmonyPatch]
        internal static class TftvRecruitRegenMute
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvPersonnelTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvPersonnelTypeName)
                                       ?.GetNestedType("GeoPhoenixFaction_RegenerateNakedRecruits_PersonnelSync", All),
                                   "Postfix");

            private static bool Prefix() => RunNativeAutomation();
        }

        /// <summary>The training clocks. <c>ForceRecruitProgressUpdate</c> is in the set because
        /// <c>IsRecruitTrainingComplete</c> is NOT a getter — it MUTATES the session (:255) and three UI paths
        /// call it (Panel.cs:739, Helpers.cs:277, Harmony.cs:362), so a client merely LOOKING at the screen
        /// advanced its own copy of the clock.</summary>
        [HarmonyPatch]
        internal static class TftvTrainingClockMute
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvTrainingTypeName) != null;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName(TftvTrainingTypeName);
                foreach (var n in new[] { "DailyUpdateTraining", "DailyUpdateTrainingDeferredFallback", "ForceRecruitProgressUpdate" })
                {
                    var m = AccessTools.Method(t, n);
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix() => RunNativeAutomation();
        }

        /// <summary>The timer-driven completed-deployment popup (UI/Harmony.cs:319) calls
        /// <c>FinalizeRecruitTrainingForUI</c> straight from a UI hook (:281) and would mint a local operative
        /// with different stats; it is also frame-count dependent (:251-263), so it fires at a different
        /// moment on every peer.</summary>
        [HarmonyPatch]
        internal static class TftvCompletedDeploymentMute
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvPanelTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvPanelTypeName), "TryOpenNextCompletedDeployment");

            private static bool Prefix() => RunNativeAutomation();
        }

        // ─── CLIENT: the gestures ───────────────────────────────────────────────────────────────

        /// <summary>Every plus / minus / multi-select / drag-drop on the ASSIGNMENTS board funnels through
        /// <c>Panel.MovePersonnelToColumn</c> (:579-644), which reaches exactly these two model methods. The
        /// wire carries the character id and the target ROLE — never a slot count, never an SP figure: the
        /// host re-derives all of it (law 3). <c>PersonnelInfo</c> is internal, so the prefix reads its
        /// arguments through <c>__args</c> rather than by name — the first use of that Harmony injection in
        /// this repo. 0Harmony 2.2.0.0 supports it; NOT yet confirmed against a live game, so if these two
        /// captures ever read as dead, check the injection before anything else.</summary>
        [HarmonyPatch]
        internal static class TftvAssignWorkerCapture
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvPersonnelTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvPersonnelTypeName), "AssignWorker");

            private static bool Prefix(object[] __args, ref bool __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                __result = false; // TFTV's own "could not assign" answer; the host echo repaints the board
                // NEVER let this throw: a prefix that faults takes TFTV's own method down with it.
                try
                {
                    if (__args == null || __args.Length < 3) { OpenUiRepaint.MarkDirty(); return false; }
                    // FacilitySlotType.Research = 0, Manufacturing = 1 (Workers.cs:139-143).
                    SendMove(__args[0], Convert.ToInt32(__args[2]) == 0 ? RoleResearch : RoleManufacturing);
                }
                catch (Exception ex) { MpLog.LogError("[MP][assign] assign capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>The other half of the same gesture: a move back to the Unassigned column.</summary>
        [HarmonyPatch]
        internal static class TftvUnassignWorkerCapture
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvPersonnelTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvPersonnelTypeName), "UnassignFromWork");

            private static bool Prefix(object[] __args)
            {
                if (IntentRail.ShouldRunNative()) return true;
                SendMove(__args == null || __args.Length < 1 ? null : __args[0], RoleUnassigned);
                return false;
            }
        }

        private static void SendMove(object personnelInfo, byte role)
        {
            try
            {
                if (personnelInfo == null || !Bind()) { OpenUiRepaint.MarkDirty(); return; }
                var character = _h.InfoCharacter.GetValue(personnelInfo) as GeoCharacter;
                if (character == null) { OpenUiRepaint.MarkDirty(); return; }
                int charId = (int)character.Id;
                IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpAssignMove,
                    "assign U#" + charId + " role=" + role,
                    w => { w.Write(charId); w.Write(role); });
            }
            catch (Exception ex) { MpLog.LogError("[MP][assign] move capture failed: " + ex); }
        }

        /// <summary>The training queue click (Helpers.cs:493). The target LEVEL rides because it is the
        /// player's choice, not a balance value — the host re-derives its own legal range, the SP cost and
        /// the slot availability, and refuses whatever does not hold there.</summary>
        [HarmonyPatch]
        internal static class TftvTrainQueueCapture
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvTrainingTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvTrainingTypeName), "QueueCharacterTrainingAutoFacility");

            private static bool Prefix(GeoCharacter character, SpecializationDef spec, int chosenTargetLevel,
                                       ref bool __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                __result = false;
                try
                {
                    if (character == null || spec == null) { OpenUiRepaint.MarkDirty(); return false; }
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpAssignTrain,
                        "train U#" + (int)character.Id + " spec=" + spec.name + " lvl=" + chosenTargetLevel,
                        w => { w.Write((int)character.Id); w.Write(spec.name); w.Write(chosenTargetLevel); });
                }
                catch (Exception ex) { MpLog.LogError("[MP][assign] train capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>The deploy / early-exit click (Helpers.cs:298). There is no CANCEL op because TFTV has no
        /// cancel: the only exit from Training is <c>FinalizeRecruitTraining(early: true)</c> with a partial
        /// SP refund, and a move back to Unassigned is refused upstream. The target base is NOT on the wire —
        /// the host derives it, exactly as TFTV's own UI entry point does.
        ///
        /// NOT A DUPLICATE OF <c>PersonnelSync</c>'s op 10, which captures the DIFFERENT method
        /// <c>FinalizeRecruitTrainingForUI</c> (TrainingFacilityRework.cs:499). The two are separate
        /// implementations reached from separate live UI paths — :499 from the timer-driven deployment popup
        /// (UI/Harmony.cs:281), and the one here from the ASSIGNMENTS panel's own per-base modal
        /// (UI/Helpers.cs:298), which is the only path that lets the player CHOOSE the base. Neither calls
        /// the other, so dropping this capture would leave that modal's click unsynced. The base divergence
        /// is deliberate and stated: the chosen base is a client value and may not price a host placement
        /// (law 3), so the host falls back to TFTV's own default the same way op 10 does.</summary>
        [HarmonyPatch]
        internal static class TftvTrainFinalizeCapture
        {
            private static bool Prepare() => AccessTools.TypeByName(TftvTrainingTypeName) != null;

            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(TftvTrainingTypeName), "FinalizeRecruitTraining");

            private static bool Prefix(GeoCharacter character, bool early, ref GeoCharacter __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                __result = null; // reads as "refused" to TFTV's UI; the host echo deploys and repaints
                try
                {
                    if (character == null) { OpenUiRepaint.MarkDirty(); return false; }
                    IntentRail.Send(SurfaceIds.GeoPersonnelIntent, OpAssignFinalize,
                        "finalize U#" + (int)character.Id + " early=" + early,
                        w => { w.Write((int)character.Id); w.Write(early); });
                }
                catch (Exception ex) { MpLog.LogError("[MP][assign] finalize capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: validate against HOST state only, then run the SAME TFTV method ───────────────

        /// <summary>The decision is <see cref="AssignAuthority.AcceptAssignMove"/> and nothing else re-states
        /// it. Everything past it is TFTV's own gate re-run on host numbers: living capacity, the Just-a-Grunt
        /// restriction and the free-slot count all live inside <c>AssignWorker</c>, so a refusal there is
        /// TFTV's refusal, reported as ours.</summary>
        private static void HandleAssignMove(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                byte role = r.ReadByte();
                GeoLevelController geo;
                GeoCharacter character;
                object row;
                if (!Resolve(engine, senderPeerId, charId, role, out geo, out character, out row)) return;

                var phoenix = geo.PhoenixFaction;
                bool ok;
                if (role == RoleUnassigned)
                {
                    AccessTools.Method(_h.Personnel, "UnassignFromWork")?.Invoke(null, new object[] { row, phoenix });
                    ok = true;
                }
                else if (role == RoleResearch || role == RoleManufacturing)
                {
                    // FacilitySlotType is internal; its two members are ordered Research, Manufacturing.
                    var slotType = Enum.ToObject(_h.SlotTypeEnum, role == RoleResearch ? 0 : 1);
                    ok = Convert.ToBoolean(AccessTools.Method(_h.Personnel, "AssignWorker")
                        ?.Invoke(null, new object[] { row, phoenix, slotType }) ?? false);
                }
                else
                {
                    // Training is entered through op 14, which pays SP and opens a session. A bare role move
                    // into it would leave a worker in the Training column with no session behind it.
                    Reject(senderPeerId, charId, "training is not a role move (use op " + OpAssignTrain + ")");
                    return;
                }
                if (!ok) { Reject(senderPeerId, charId, "TFTV refused the assignment (slots, housing or restriction)"); return; }
                MpLog.Log("[MP][assign] HOST intent APPLIED op=move char=U#" + charId + " role=" + role +
                          " nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { Reject(senderPeerId, charId, "(throw) " + ex.Message); }
        }

        private static void HandleAssignTrain(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                string specName = WireString.ReadKey(r);
                int targetLevel = r.ReadInt32();
                GeoLevelController geo;
                GeoCharacter character;
                object row;
                if (!Resolve(engine, senderPeerId, charId, RoleTraining, out geo, out character, out row)) return;
                var spec = SpecByName(specName);
                if (spec == null) { Reject(senderPeerId, charId, "unknown specialization '" + specName + "'"); return; }
                // Level range, SP cost and slot count are ALL re-derived inside TFTV's own queue method
                // against host state (TrainingFacilityRework.cs:123-141) — nothing here re-implements them.
                bool ok = Convert.ToBoolean(AccessTools.Method(_h.Training, "QueueCharacterTrainingAutoFacility")
                    ?.Invoke(null, new object[] { geo, character, spec, targetLevel }) ?? false);
                if (!ok) { Reject(senderPeerId, charId, "TFTV refused the training queue (level, SP or slots)"); return; }
                MpLog.Log("[MP][assign] HOST intent APPLIED op=train char=U#" + charId + " spec=" + spec.name +
                          " lvl=" + targetLevel + " nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { Reject(senderPeerId, charId, "(throw) " + ex.Message); }
        }

        private static void HandleAssignFinalize(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int charId = -1;
            try
            {
                charId = r.ReadInt32();
                bool early = r.ReadBoolean();
                GeoLevelController geo;
                GeoCharacter character;
                object row;
                if (!Resolve(engine, senderPeerId, charId, RoleTraining, out geo, out character, out row)) return;
                // HOST-DERIVED TARGET (law 3), the same pick TFTV's own UI entry point makes
                // (TrainingFacilityRework.cs:549 Bases.FirstOrDefault): a base a client named could put the
                // finished operative anywhere on the host's map.
                var target = geo.PhoenixFaction.Bases.FirstOrDefault();
                if (target == null) { Reject(senderPeerId, charId, "the host has no Phoenix base to deploy into"); return; }
                var result = AccessTools.Method(_h.Training, "FinalizeRecruitTraining")
                    ?.Invoke(null, new object[] { geo, character, target, early });
                if (result == null) { Reject(senderPeerId, charId, "TFTV refused the finalize"); return; }
                MpLog.Log("[MP][assign] HOST intent APPLIED op=finalize char=U#" + charId + " early=" + early +
                          " nonce=" + nonce + " peer=" + senderPeerId);
                OpenUiRepaint.MarkDirty();
            }
            catch (Exception ex) { Reject(senderPeerId, charId, "(throw) " + ex.Message); }
        }

        /// <summary>The shared preamble of all three ops, and the ONE place the authority decision is asked.
        /// A row is required as well as a character: TFTV's methods take a <c>PersonnelInfo</c>, and a
        /// character with no row is one the ASSIGNMENTS screen never offered.</summary>
        private static bool Resolve(NetworkEngine engine, ulong peer, int charId, byte role,
                                    out GeoLevelController geo, out GeoCharacter character, out object row)
        {
            geo = GenericApplier.GeoLevel();
            character = geo == null ? null : IdentityResolver.Resolve(geo, "U#" + charId, null) as GeoCharacter;
            row = null;
            bool isPhoenix = geo != null && character != null &&
                             ReferenceEquals(character.Faction, geo.PhoenixFaction);
            // Bind FIRST: "is this role byte a defined enum value" is a question only TFTV's own enum can
            // answer, so an unbound host cannot even evaluate the authority decision — it must refuse it.
            bool bound = Bind();
            bool roleDefined = bound && Enum.IsDefined(_h.RoleEnum, (int)role);
            if (!AssignAuthority.AcceptAssignMove(engine != null && engine.IsHost,
                                                 engine != null && engine.IsActiveSession,
                                                 character != null, isPhoenix, roleDefined))
            {
                Reject(peer, charId, "refused: host=" + (engine != null && engine.IsHost) +
                                     " resolved=" + (character != null) + " phoenix=" + isPhoenix +
                                     " roleDefined=" + roleDefined);
                return false;
            }
            if (!bound) { Reject(peer, charId, "TFTV BaseRework is not bound on the host"); return false; }
            row = AccessTools.Method(_h.Personnel, "GetPersonnelByUnitId")?.Invoke(null, new object[] { charId });
            if (row == null) { Reject(peer, charId, "not TFTV personnel"); return false; }
            return true;
        }

        /// <summary>Never log-only (law 7): the reject re-emits the touched character subtree so the
        /// gesturing client's open ASSIGNMENTS board reconverges on host truth, and the nudge tells that one
        /// peer why its click died.</summary>
        private static void Reject(ulong peer, int charId, string why) =>
            IntentRail.Reject(SurfaceIds.GeoPersonnelIntent, peer, "assign U#" + charId + " — " + why,
                              charId >= 0 ? "U#" + charId : null);
    }

    /// <summary>
    /// The ASSIGNMENTS mirror as mod-state (root "M#assign"; contract at
    /// <see cref="IdentityResolver.RegisterModRoot"/>).
    ///
    /// THE ATTRIBUTE IS THE ROOT. Without <c>[SerializeType(SerializeAll)]</c>
    /// <c>RailMeta.HasPersistentMembers</c> is false, <c>RailType.Source</c> is "none", the walk emits ZERO
    /// fields (DiffEngine.cs:1113-1117) and the root never crosses the wire — the 2026-08-13 bug
    /// <c>DeployPrepState</c> shipped with. L442 executes the classification rather than reading the source.
    ///
    /// EVERY MEMBER IS A <c>Dictionary&lt;int, leaf&gt;</c> keyed by <c>GeoCharacter.Id</c>, for two reasons:
    /// each one classifies as <c>LeafDict</c> and ships PER-KEY deltas, and the root then holds no game type
    /// at all, so the codec stays executable headless inside a law. The session set is exactly the key set of
    /// <see cref="SessionSpec"/>; every other session dictionary is an INDEPENDENT wire entry and may lag it
    /// by a delta, which the applier reads defensively rather than indexing blind.
    ///
    /// <see cref="SessionStartLevel"/> and <see cref="SessionPersonnelId"/> are carried even though TFTV's
    /// own save DTO omits them (TrainingFacilityRework.cs:1045-1058). That omission is an upstream defect
    /// that makes level progress read differently after a reload; the mirror must not inherit it.
    /// </summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class AssignState
    {
        public Dictionary<int, byte> AssignRole = new Dictionary<int, byte>();       // PersonnelAssignment
        public Dictionary<int, string> AssignSpec = new Dictionary<int, string>();   // SpecializationDef.name, "" = none

        public Dictionary<int, int> SessionPersonnelId = new Dictionary<int, int>();
        public Dictionary<int, string> SessionSpec = new Dictionary<int, string>();
        public Dictionary<int, double> SessionStartHour = new Dictionary<int, double>();
        public Dictionary<int, double> SessionDurationHours = new Dictionary<int, double>();
        public Dictionary<int, int> SessionTargetLevel = new Dictionary<int, int>();
        public Dictionary<int, int> SessionStartLevel = new Dictionary<int, int>();
        public Dictionary<int, int> SessionVirtualLevel = new Dictionary<int, int>();
        public Dictionary<int, int> SessionSpPaid = new Dictionary<int, int>();
        public Dictionary<int, bool> SessionCompleted = new Dictionary<int, bool>();
        public Dictionary<int, bool> SessionWasDismissed = new Dictionary<int, bool>();

        public Dictionary<int, int> AppliedStatLevel = new Dictionary<int, int>();
        public Dictionary<int, int> PendingStatLevel = new Dictionary<int, int>();

        internal void Clear()
        {
            AssignRole.Clear(); AssignSpec.Clear();
            SessionPersonnelId.Clear(); SessionSpec.Clear(); SessionStartHour.Clear();
            SessionDurationHours.Clear(); SessionTargetLevel.Clear(); SessionStartLevel.Clear();
            SessionVirtualLevel.Clear(); SessionSpPaid.Clear(); SessionCompleted.Clear();
            SessionWasDismissed.Clear();
            AppliedStatLevel.Clear(); PendingStatLevel.Clear();
        }

        /// <summary>Drop every session key the host no longer has — one place, so a new session member can
        /// never be added without being pruned.</summary>
        internal void PruneSessions(HashSet<int> live)
        {
            PruneOne(SessionPersonnelId, live); PruneOne(SessionSpec, live);
            PruneOne(SessionStartHour, live); PruneOne(SessionDurationHours, live);
            PruneOne(SessionTargetLevel, live); PruneOne(SessionStartLevel, live);
            PruneOne(SessionVirtualLevel, live); PruneOne(SessionSpPaid, live);
            PruneOne(SessionCompleted, live); PruneOne(SessionWasDismissed, live);
        }

        private static void PruneOne<T>(Dictionary<int, T> d, HashSet<int> live)
        {
            if (d.Count == 0) return;
            List<int> dead = null;
            foreach (var k in d.Keys)
                if (!live.Contains(k)) (dead ?? (dead = new List<int>())).Add(k);
            if (dead != null) foreach (var k in dead) d.Remove(k);
        }

        /// <summary>Order-independent content hash — the client's "has the mirror moved" gate. Cheap by
        /// construction (a personnel pool is tens of entries), and it is a CHANGE detector, never an identity:
        /// a collision costs one skipped repaint, which the next delta corrects.</summary>
        internal int Fingerprint()
        {
            int h = 17;
            h = Fold(AssignRole, h); h = Fold(AssignSpec, h);
            h = Fold(SessionPersonnelId, h); h = Fold(SessionSpec, h);
            h = Fold(SessionStartHour, h); h = Fold(SessionDurationHours, h);
            h = Fold(SessionTargetLevel, h); h = Fold(SessionStartLevel, h);
            h = Fold(SessionVirtualLevel, h); h = Fold(SessionSpPaid, h);
            h = Fold(SessionCompleted, h); h = Fold(SessionWasDismissed, h);
            h = Fold(AppliedStatLevel, h); h = Fold(PendingStatLevel, h);
            return h;
        }

        private static int Fold<T>(Dictionary<int, T> d, int h)
        {
            unchecked
            {
                h = h * 31 + d.Count;
                foreach (var kv in d)
                    h ^= kv.Key * 397 + (kv.Value == null ? 0 : kv.Value.GetHashCode());
                return h;
            }
        }
    }
}
