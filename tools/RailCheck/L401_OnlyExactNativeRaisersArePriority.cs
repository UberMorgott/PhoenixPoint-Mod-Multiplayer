using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Reflection;
using System.Linq;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Missions;
using PhoenixPoint.Geoscape.View.ViewStates;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Common.Entities.Items;
using Base.Core;

namespace RailCheck
{
    internal static class L401_OnlyExactNativeRaisersArePriority
    {
        internal static IEnumerable<string> Check()
        {
#pragma warning disable SYSLIB0050
            var ambush = (GeoAmbushMission)FormatterServices.GetUninitializedObject(typeof(GeoAmbushMission));
            var mission = (GeoMission)FormatterServices.GetUninitializedObject(typeof(GeoAncientSiteMission));
#pragma warning restore SYSLIB0050
            var constructors = typeof(NativeRaiserToken).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
            if (constructors.Length != 1 || !constructors[0].IsPrivate)
                yield return "L401 token-mint-authority-is-not-closed";
            NativeRaiserToken Token(NativeRaiserToken.Kind kind, ModalType modal, object subject) =>
                (NativeRaiserToken)constructors[0].Invoke(new[] { (object)kind, modal, subject, "test-stable" });
            var ambushToken = Token(NativeRaiserToken.Kind.MissionBrief, ModalType.GeoAmbushBrief, ambush);
            if (DurableWindowRegistry.StableMissionSubject("S#prelaunch", "mission-def-guid") == null)
                yield return "L401 prelaunch-mission-subject-required-global-time";
            if (!DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), ModalType.GeoAmbushBrief, ambush, ambushToken))
                yield return "L401 exact-ambush-raiser-was-not-priority";
            if (DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), ModalType.GeoAmbushBrief, mission, ambushToken))
                yield return "L401 token-reused-for-another-subject";
            if (DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), ModalType.HavenInfiltrateBrief, mission,
                    Token(NativeRaiserToken.Kind.MissionBrief, ModalType.HavenInfiltrateBrief, mission)))
                yield return "L401 excluded-local-brief-became-priority";
            if (DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), ModalType.GeoHavenAttackBrief, mission, null))
                yield return "L401 caller-asserted-priority-without-raiser-token";
            if (!DurableWindowRegistry.IsPriority(typeof(UIStateRosterDeployment), ModalType.None, mission,
                    Token(NativeRaiserToken.Kind.Deployment, ModalType.None, mission)))
                yield return "L401 exact-deployment-raiser-was-not-priority";
            if (DurableWindowRegistry.IsPriority(typeof(UIStateRosterDeployment), ModalType.None, ambush,
                    Token(NativeRaiserToken.Kind.Deployment, ModalType.None, mission)))
                yield return "L401 deployment-token-crossed-mission-subject";
            var asset = new object();
            var otherAsset = new object();
            var assetToken = Token(NativeRaiserToken.Kind.AssetDestination, ModalType.None, asset);
            if (!DurableWindowRegistry.IsPriority(typeof(UIStateAssetDeployment), ModalType.None, asset, assetToken) ||
                DurableWindowRegistry.IsPriority(typeof(UIStateAssetDeployment), ModalType.None, otherAsset, assetToken))
                yield return "L401 asset-stable-subject-boundary-failed";

            var registered = new HashSet<ModalType>
            {
                ModalType.GeoHavenAttackBrief, ModalType.GeoAlienBaseBrief, ModalType.GeoScavengeBrief,
                ModalType.GeoPhoenixBaseDefenseBrief, ModalType.GeoPhoenixBaseInfestationBrief,
                ModalType.AncientSiteAttackBrief, ModalType.AncientSiteDefenceBrief,
                ModalType.BehemothAttackBrief, ModalType.InfestedHavenBrief,
            };
            foreach (ModalType modal in Enum.GetValues(typeof(ModalType)))
            {
                if (modal == ModalType.GeoAmbushBrief) continue;
                bool actual = DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), modal, mission,
                    Token(NativeRaiserToken.Kind.MissionBrief, modal, mission));
                if (actual != registered.Contains(modal))
                    yield return "L401 modal-priority-matrix-wrong-" + modal;
            }
            var captureTypes = new[] { typeof(NativeRaiserToken.MissionBriefCapture),
                                       typeof(NativeRaiserToken.DeploymentCapture),
                                       typeof(NativeRaiserToken.AssetDestinationCapture) };
            foreach (var capture in captureTypes)
            {
                string patchMethod = capture == typeof(NativeRaiserToken.MissionBriefCapture) ? "Prefix" : "Postfix";
                var method = capture.GetMethod(patchMethod, BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null)
                    yield return "L401 native-capture-adapter-is-runtime-inert-" + capture.Name;
                if (patchMethod == "Postfix" && (method == null || !Program.Callees(method, typeof(NativeRaiserToken).Assembly)
                        .Any(c => c.Name == "AfterSuccessfulQueue")))
                    yield return "L401 capture-postfix-does-not-run-success-evidence-" + capture.Name;
            }
            foreach (var capture in captureTypes.Skip(1))
            {
                var prefix = capture.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                var args = new object[] { null };
                prefix?.Invoke(null, args);
                var firstRaise = args[0] as NativeRaiserToken.RaiseState;
                args[0] = null; prefix?.Invoke(null, args);
                var secondRaise = args[0] as NativeRaiserToken.RaiseState;
                if (prefix == null || firstRaise == null || secondRaise == null ||
                    string.IsNullOrEmpty(firstRaise.TriggerId) || firstRaise.TriggerId == secondRaise.TriggerId)
                    yield return "L401 capture-prefix-did-not-record-native-queue-boundary-" + capture.Name;
            }
#pragma warning disable SYSLIB0050
            var deploymentState = (UIStateRosterDeployment)FormatterServices.GetUninitializedObject(typeof(UIStateRosterDeployment));
            typeof(UIStateRosterDeployment).GetField("_mission", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(deploymentState, mission);
            if (!NativeRaiserToken.DeploymentCapture.NativeSucceeded(0, 1, deploymentState, mission) ||
                NativeRaiserToken.DeploymentCapture.NativeSucceeded(1, 1, deploymentState, mission) ||
                NativeRaiserToken.DeploymentCapture.NativeSucceeded(0, 1, deploymentState, ambush))
                yield return "L401 deployment-postfix-success-evidence-failed";
            var faction = (GeoPhoenixFaction)FormatterServices.GetUninitializedObject(typeof(GeoPhoenixFaction));
            var character = (GeoCharacter)FormatterServices.GetUninitializedObject(typeof(GeoCharacter));
            var aircraft = (ComponentSetDef)FormatterServices.GetUninitializedObject(typeof(ComponentSetDef));
            var item = (ItemDef)FormatterServices.GetUninitializedObject(typeof(ItemDef));
            var bind = new GeoDeployAssetFactionCharacterBind { Faction = faction, Asset = character,
                Aircraft = aircraft, RelatedItemDef = item, Manufactured = true, NotEnoughSpace = false };
#pragma warning restore SYSLIB0050
            if (!NativeRaiserToken.AssetDestinationCapture.NativeSucceeded(0, 1, bind, faction, character,
                    aircraft, item, true, false) ||
                NativeRaiserToken.AssetDestinationCapture.NativeSucceeded(1, 1, bind, faction, character,
                    aircraft, item, true, false) ||
                NativeRaiserToken.AssetDestinationCapture.NativeSucceeded(0, 1, bind, faction, null,
                    aircraft, item, true, false))
                yield return "L401 asset-postfix-success-evidence-failed";
            bind.Asset = null;
            string manufacturedSubject = DurableWindowRegistry.AssetSubject(bind);
            if (string.IsNullOrEmpty(manufacturedSubject))
                yield return "L401 manufactured-aircraft-with-null-character-was-dropped";
            var manufacturedMember = new MembershipId("manufacturer", 2);
            var manufacturedStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>(), 1,
                new[] { new KeyValuePair<MembershipId, MemberPresence>(manufacturedMember, MemberPresence.Active) }));
            var raiseState = new NativeRaiserToken.RaiseState { Before = 0, TriggerId = "asset:raise-one" };
            var manufacturedOccurrence = new OccurrenceId("AssetDestination", raiseState.TriggerId,
                new[] { manufacturedSubject });
            if (!DurableWindowRegistry.EnqueuePriorityOccurrence(manufacturedStore, manufacturedOccurrence) ||
                DurableWindowRegistry.EnqueuePriorityOccurrence(manufacturedStore, manufacturedOccurrence) ||
                manufacturedStore.Ledger.EntriesFor(manufacturedMember).Count != 1)
                yield return "L401 manufactured-asset-raise-was-not-distinct-and-idempotent";
            foreach (int priority in new[] { 0, int.MaxValue })
                if (DurableWindowRegistry.IsPriorityHeuristic(true, priority, "GeoAmbushBrief", true))
                    yield return "L401 mandatory-or-numeric-or-name-tag-heuristic-became-priority";
            if (DurableWindowRegistry.IsPriority(typeof(UIStateAssetDeployment), ModalType.None, asset,
                    Token(NativeRaiserToken.Kind.Deployment, ModalType.None, asset)) ||
                DurableWindowRegistry.IsPriority(typeof(UIStateRosterDeployment), ModalType.None, mission,
                    Token(NativeRaiserToken.Kind.AssetDestination, ModalType.None, mission)) ||
                DurableWindowRegistry.IsPriority(typeof(UIStateGeoscapeEvent), ModalType.GeoAmbushBrief, ambush, ambushToken))
                yield return "L401 wrong-state-or-token-kind-was-priority";
            if (DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), (ModalType)255, mission,
                    Token(NativeRaiserToken.Kind.MissionBrief, (ModalType)255, mission)))
                yield return "L401 unknown-patched-modal-triple-became-priority";

            // POSITIVE CONTROL: semantic/numeric lookalikes without a closed token remain ordinary.
            if (DurableWindowRegistry.IsPriority(typeof(UIStateGeoModal), ModalType.GeoAmbushBrief, ambush, null))
                yield return "L401 control-not-red: modal-name-alone-granted-priority";
        }
    }
}
