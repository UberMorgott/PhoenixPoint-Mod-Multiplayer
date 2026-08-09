using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Missions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Common.Entities.Items;
using Base.Core;
using Base.Utils;

namespace Multiplayer.Network.Sync
{
    internal enum DurableWindowDisposition { Durable, LocalOnly }
    internal enum DurableWindowPriority { Ordinary, Priority, Deployment }

    /// <summary>A closed proof of the native method that raised a candidate window.</summary>
    internal sealed class NativeRaiserToken
    {
        internal sealed class RaiseState
        {
            internal int Before;
            internal string TriggerId;
        }
        internal enum Kind { MissionBrief, Deployment, AssetDestination }
        internal Kind Raiser { get; }
        internal ModalType Modal { get; }
        internal object Subject { get; }
        internal string StableRaiserId { get; }

        private NativeRaiserToken(Kind raiser, ModalType modal, object subject, string stableRaiserId)
        { Raiser = raiser; Modal = modal; Subject = subject; StableRaiserId = stableRaiserId; }

        [ThreadStatic] private static NativeRaiserToken _missionBrief;
        internal static NativeRaiserToken CurrentMissionBrief => _missionBrief;
        private static RaiseState NewRaiseState(string category) => new RaiseState
        { Before = DurableWindowRegistry.QueueCount(), TriggerId = category + ":" + Guid.NewGuid().ToString("N") };

        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ShowMissionBriefing),
            new[] { typeof(GeoMission), typeof(GeoVehicle) })]
        internal static class MissionBriefCapture
        {
            private static readonly MethodInfo GetBrief = AccessTools.Method(typeof(GeoscapeView),
                "GetMissionBriefModal", new[] { typeof(GeoMission) });
            private static void Prefix(GeoscapeView __instance, GeoMission mission, GeoVehicle vehicle)
            {
                _missionBrief = null;
                if (__instance == null || mission == null || GetBrief == null) return;
                var modal = (ModalType)GetBrief.Invoke(__instance, new object[] { mission });
                string missionId = DurableWindowRegistry.StableMissionSubject(mission);
                if (modal != ModalType.None && !string.IsNullOrEmpty(missionId))
                    _missionBrief = new NativeRaiserToken(Kind.MissionBrief, modal, mission,
                        "brief:" + Guid.NewGuid().ToString("N"));
            }
            private static Exception Finalizer(Exception __exception) { _missionBrief = null; return __exception; }
        }

        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ToDeploymentState),
            new[] { typeof(GeoMission), typeof(IGeoCharacterContainer), typeof(bool) })]
        internal static class DeploymentCapture
        {
            private static void Prefix(ref RaiseState __state) { __state = NewRaiseState("deployment"); }
            private static void Postfix(GeoMission mission, IGeoCharacterContainer container, RaiseState __state)
            {
                var state = DurableWindowRegistry.LastQueued<UIStateRosterDeployment>();
                AfterSuccessfulQueue(__state, DurableWindowRegistry.QueueCount(), state, mission, container);
            }
            internal static bool AfterSuccessfulQueue(RaiseState raise, int after, UIStateRosterDeployment state,
                                                      GeoMission mission, IGeoCharacterContainer container)
            {
                string missionId = DurableWindowRegistry.StableMissionSubject(mission);
                if (raise == null || !NativeSucceeded(raise.Before, after, state, mission) || string.IsNullOrEmpty(missionId)) return false;
                DurableWindowRegistry.CaptureDeployment(mission, new NativeRaiserToken(Kind.Deployment,
                    ModalType.None, mission, raise.TriggerId));
                return true;
            }
            internal static bool NativeSucceeded(int before, int after, UIStateRosterDeployment state, GeoMission mission) =>
                after > before && mission != null && state != null && ReferenceEquals(state.Mission, mission);
        }

        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.PrepareDeployAsset),
            new[] { typeof(GeoFaction), typeof(GeoCharacter), typeof(ComponentSetDef), typeof(ItemDef), typeof(bool), typeof(bool) })]
        internal static class AssetDestinationCapture
        {
            private static void Prefix(ref RaiseState __state) { __state = NewRaiseState("asset"); }
            private static void Postfix(GeoscapeView __instance, GeoFaction faction, GeoCharacter character,
                                        ComponentSetDef aircraft, ItemDef relatedItemDef, bool manufactured,
                                        bool spaceFull, RaiseState __state)
            {
                var state = DurableWindowRegistry.LastQueued<UIStateAssetDeployment>();
                var bind = state?.DeployBind;
                AfterSuccessfulQueue(__state, DurableWindowRegistry.QueueCount(), bind, faction, character,
                    aircraft, relatedItemDef, manufactured, spaceFull);
            }
            internal static bool AfterSuccessfulQueue(RaiseState raise, int after,
                PhoenixPoint.Geoscape.View.ViewControllers.GeoDeployAssetFactionCharacterBind bind,
                GeoFaction faction, GeoCharacter character, ComponentSetDef aircraft, ItemDef relatedItemDef,
                bool manufactured, bool spaceFull)
            {
                if (raise == null || !NativeSucceeded(raise.Before, after, bind, faction, character, aircraft, relatedItemDef,
                    manufactured, spaceFull)) return false;
                string stable = DurableWindowRegistry.AssetSubject(bind);
                if (string.IsNullOrEmpty(stable)) return false;
                DurableWindowRegistry.CaptureAssetDestination(bind,
                    new NativeRaiserToken(Kind.AssetDestination, ModalType.None, bind, raise.TriggerId));
                return true;
            }
            internal static bool NativeSucceeded(int before, int after,
                PhoenixPoint.Geoscape.View.ViewControllers.GeoDeployAssetFactionCharacterBind bind,
                GeoFaction faction, GeoCharacter character, ComponentSetDef aircraft, ItemDef relatedItemDef,
                bool manufactured, bool spaceFull) =>
                after > before && faction is GeoPhoenixFaction && bind != null &&
                ReferenceEquals(bind.Faction, faction) && ReferenceEquals(bind.Asset, character) &&
                ReferenceEquals(bind.Aircraft, aircraft) && ReferenceEquals(bind.RelatedItemDef, relatedItemDef) &&
                bind.Manufactured == manufactured && bind.NotEnoughSpace == spaceFull;

        }
    }

    /// <summary>Approved durable taxonomy and the only DWI display gate.</summary>
    internal static class DurableWindowRegistry
    {
        internal sealed class RouterRegistration
        {
            internal string Family;
            internal DurableWindowDisposition Disposition;
            internal Func<NetworkEngine, ulong, byte, byte[], bool> Handle;
        }

        internal static readonly IReadOnlyList<RouterRegistration> RoutedPresentations =
            new[]
            {
                Route(nameof(EventPopup), DurableWindowDisposition.Durable, EventPopup.HandleInbound),
                Route(nameof(GeoModalMirror), DurableWindowDisposition.Durable, GeoModalMirror.HandleInbound),
                Route(nameof(CutsceneMirror), DurableWindowDisposition.Durable, CutsceneMirror.HandleInbound),
                Route(nameof(MissionOutcomeMirror), DurableWindowDisposition.Durable, MissionOutcomeMirror.HandleInbound),
                Route(nameof(MarketplaceSync), DurableWindowDisposition.LocalOnly, MarketplaceSync.HandleInbound),
            };

        private static RouterRegistration Route(string family, DurableWindowDisposition disposition,
            Func<NetworkEngine, ulong, byte, byte[], bool> handle) =>
            new RouterRegistration { Family = family, Disposition = disposition, Handle = handle };

        internal static bool HandleRoutedPresentation(NetworkEngine engine, ulong peer, byte surface, byte[] payload)
        {
            foreach (var route in RoutedPresentations)
                if (route.Handle(engine, peer, surface, payload)) return true;
            return false;
        }

        internal static bool EnqueuePriorityOccurrence(DurableInboxStore store, OccurrenceId occurrence)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            for (int attempt = 0; attempt != 32; attempt++)
            {
                var expected = store.Ledger;
                if (expected.AllEntries.Any(e => e.Occurrence.Equals(occurrence))) return false;
                var sequencer = new HostInboxSequencer(expected);
                if (!sequencer.CreateOccurrence(occurrence)) return false;
                if (store.Commit(expected, sequencer.Ledger)) return true;
            }
            throw new InvalidOperationException("durable priority occurrence commit contention exceeded bound");
        }

        private static void EnqueueCapturedPriority(string family, string stableTrigger, params string[] subjects)
        {
            var engine = NetworkEngine.Instance;
            var store = DurableInboxSaveBridge.ActiveStore;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost || store == null) return;
            for (int attempt = 0; attempt != 32; attempt++)
            {
                var expected = store.Ledger;
                var occurrence = new OccurrenceId(family, stableTrigger, subjects);
                if (expected.AllEntries.Any(e => e.Occurrence.Equals(occurrence))) return;
                var sequencer = new HostInboxSequencer(expected);
                if (!sequencer.CreateOccurrence(occurrence)) return;
                if (store.Commit(expected, sequencer.Ledger)) return;
            }
            throw new InvalidOperationException("native priority occurrence commit contention exceeded bound");
        }

        internal static IReadOnlyDictionary<string, DurableWindowDisposition> RouterFamilies =>
            RoutedPresentations.ToDictionary(x => x.Family, x => x.Disposition, StringComparer.Ordinal);

        private static readonly HashSet<ModalType> MissionBriefs = new HashSet<ModalType>
        {
            ModalType.GeoHavenAttackBrief, ModalType.GeoAlienBaseBrief, ModalType.GeoScavengeBrief,
            ModalType.GeoPhoenixBaseDefenseBrief, ModalType.GeoPhoenixBaseInfestationBrief,
            ModalType.AncientSiteAttackBrief, ModalType.AncientSiteDefenceBrief,
            ModalType.BehemothAttackBrief, ModalType.InfestedHavenBrief,
        };

        private static readonly HashSet<Type> MapStates = new HashSet<Type>
        { typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected), typeof(UIStateInitial) };

        internal static DurableWindowDisposition RouterDisposition(string family)
        {
            if (family == null || !RouterFamilies.TryGetValue(family, out var result))
                throw new InvalidOperationException("unclassified geoscape router family: " + (family ?? "<null>"));
            return result;
        }

        internal static DurableWindowDisposition ModalDisposition(ModalType modal)
        {
            var rule = GeoWindowCoverage.RuleForModal(modal);
            if (rule == null)
                throw new InvalidOperationException("loud-unclassified modal family: " + modal);
            if (rule.Sync == WindowSync.Gap)
                return DurableGapModals.Contains(modal)
                    ? DurableWindowDisposition.Durable
                    : DurableWindowDisposition.LocalOnly;
            return rule.Sync == WindowSync.Mirrored
                ? DurableWindowDisposition.Durable
                : DurableWindowDisposition.LocalOnly;
        }

        private static readonly HashSet<ModalType> DurableGapModals = new HashSet<ModalType>
        {
            ModalType.GeoHavenAttackOutcome, ModalType.GeoAlienBaseOutcome, ModalType.GeoScavengeOutcome,
            ModalType.GeoPhoenixBaseDefenseOutcome, ModalType.GeoAmbushOutcome, ModalType.HavenInfiltrateOutcome,
            ModalType.GeoPhoenixBaseInfestationOutcome, ModalType.AncientSiteAttackOutcome,
            ModalType.AncientSiteDefenceOutcome, ModalType.BehemothAttackOutcome, ModalType.InfestedHavenOutcome,
            ModalType.InterceptionBrief, ModalType.InterceptionOutcome,
            ModalType.PandoranRevealResult, ModalType.AlienResearchBrief,
        };

        internal static bool MayPresent(bool geoscapeStarted, Type currentViewState) =>
            geoscapeStarted && currentViewState != null && MapStates.Contains(currentViewState);

        internal static bool MayPresent()
        {
            GeoLevelController geo = GenericApplier.StartedGeoLevel();
            return geo != null && MayPresent(true, geo.View?.CurrentViewState?.GetType());
        }

        internal static T LastQueued<T>() where T : class
        {
            var geo = GenericApplier.StartedGeoLevel();
            var query = geo == null ? null : AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery")?.GetValue(geo.View);
            if (query == null) return null;
            var requests = WindowOrder.RequestsField?.GetValue(query)
                as System.Collections.IList;
            if (requests == null) return null;
            for (int i = requests.Count - 1; i >= 0; i--)
                if ((requests[i] as GeoscapeViewStateSwitchRequest)?.State is T state) return state;
            return null;
        }

        internal static int QueueCount()
        {
            try
            {
                var geo = GenericApplier.StartedGeoLevel();
                var query = geo == null ? null : AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery")?.GetValue(geo.View);
                if (query == null) return 0;
                return (WindowOrder.RequestsField?.GetValue(query) as System.Collections.ICollection)?.Count ?? 0;
            }
            catch { return 0; }
        }

        internal static void CaptureModal(ModalType modal, object subject)
        {
            var token = NativeRaiserToken.CurrentMissionBrief;
            if (token != null && IsPriority(typeof(UIStateGeoModal), modal, subject, token))
                EnqueueCapturedPriority("Modal:" + modal, token.StableRaiserId,
                    StableMissionSubject(subject as GeoMission));
        }

        internal static void CaptureDeployment(GeoMission mission, NativeRaiserToken token)
        {
            if (IsPriority(typeof(UIStateRosterDeployment), ModalType.None, mission, token))
                EnqueueCapturedPriority("DeploymentPreparing", token.StableRaiserId,
                    StableMissionSubject(mission));
        }

        internal static void CaptureAssetDestination(object subject, NativeRaiserToken token)
        {
            if (IsPriority(typeof(UIStateAssetDeployment), ModalType.None, subject, token))
                EnqueueCapturedPriority("AssetDestination", token.StableRaiserId, token.StableRaiserId);
        }

        internal static string AssetSubject(PhoenixPoint.Geoscape.View.ViewControllers.GeoDeployAssetFactionCharacterBind bind) =>
            bind == null || (ReferenceEquals(bind.Asset, null) && ReferenceEquals(bind.Aircraft, null) &&
                             ReferenceEquals(bind.RelatedItemDef, null)) ? null :
            (IdentityResolver.RootRef(bind.Asset) ?? "asset:none") + "|" +
            (ReferenceEquals(bind.Aircraft, null) ? "aircraft:none" : (bind.Aircraft.Guid ?? "aircraft-guid:none")) + "|" +
            (ReferenceEquals(bind.RelatedItemDef, null) ? "item:none" : (bind.RelatedItemDef.Guid ?? "item-guid:none")) + "|" +
            (bind.Manufactured ? "manufactured" : "recruited") + "|" +
            (bind.NotEnoughSpace ? "space-full" : "space-available");

        internal static string StableMissionSubject(GeoMission mission)
        {
            if (mission == null || mission.Site == null || mission.MissionDef == null)
                return null;
            string site = IdentityResolver.RootRef(mission.Site);
            if (string.IsNullOrEmpty(site)) return null;
            return StableMissionSubject(site, mission.MissionDef.Guid);
        }

        internal static string StableMissionSubject(string siteIdentity, string missionDefGuid) =>
            string.IsNullOrEmpty(siteIdentity) || string.IsNullOrEmpty(missionDefGuid)
                ? null
                : siteIdentity + "|" + missionDefGuid;

        internal static string[] MissionOccurrenceSubjects(string siteIdentity, string vehicleIdentity,
                                                           string stableMissionSubject)
        {
            if (string.IsNullOrEmpty(siteIdentity) || string.IsNullOrEmpty(stableMissionSubject))
                throw new InvalidOperationException("stable mission occurrence subjects are unavailable");
            return new[] { siteIdentity,
                string.IsNullOrEmpty(vehicleIdentity) ? "vehicle:none" : vehicleIdentity,
                stableMissionSubject };
        }

        internal static bool IsPriority(Type stateType, ModalType modal, object subject, NativeRaiserToken token)
        {
            if (token == null || stateType == null) return false;
            if (stateType == typeof(UIStateRosterDeployment))
                return token.Raiser == NativeRaiserToken.Kind.Deployment && subject is GeoMission &&
                       ReferenceEquals(subject, token.Subject);
            if (stateType == typeof(UIStateAssetDeployment))
                return token.Raiser == NativeRaiserToken.Kind.AssetDestination && subject != null &&
                       ReferenceEquals(subject, token.Subject);
            if (stateType != typeof(UIStateGeoModal) || token.Raiser != NativeRaiserToken.Kind.MissionBrief ||
                !ReferenceEquals(subject, token.Subject) || modal != token.Modal) return false;
            if (subject is GeoAmbushMission) return modal == ModalType.GeoAmbushBrief;
            return subject is GeoMission && MissionBriefs.Contains(modal);
        }

        internal static bool IsPriorityHeuristic(bool mandatoryMission, int nativePriority,
                                                 string semanticName, bool hasAmbushTag) => false;
    }

}
