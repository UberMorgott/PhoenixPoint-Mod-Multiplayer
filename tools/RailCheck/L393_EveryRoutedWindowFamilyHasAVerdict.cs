using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    internal static class L393_EveryRoutedWindowFamilyHasAVerdict
    {
        internal static IEnumerable<string> Check()
        {
            var expected = new[] { nameof(EventPopup), nameof(GeoModalMirror), nameof(CutsceneMirror),
                                   nameof(MissionOutcomeMirror), nameof(MarketplaceSync) };
            foreach (var family in expected)
                if (!DurableWindowRegistry.RouterFamilies.ContainsKey(family))
                    yield return "L393 routed-family-unclassified-" + family;
            if (DurableWindowRegistry.RouterDisposition(nameof(MarketplaceSync)) != DurableWindowDisposition.LocalOnly)
                yield return "L393 marketplace-window-was-made-durable-without-a-trigger";
            bool loud = false;
            try { DurableWindowRegistry.RouterDisposition("FutureWindowRouter"); }
            catch (InvalidOperationException) { loud = true; }
            if (!loud) yield return "L393 unknown-router-fell-through-silently";
            if (DurableWindowRegistry.RoutedPresentations.Select(r => r.Family).Distinct(StringComparer.Ordinal).Count()
                != DurableWindowRegistry.RoutedPresentations.Count)
                yield return "L393 duplicate-router-registration";
            if (DurableWindowRegistry.RoutedPresentations.Any(r => r.Handle == null))
                yield return "L393 router-verdict-has-no-runtime-handler";
            var routed = typeof(SyncEngine).Assembly.GetTypes()
                .Where(t => t == typeof(SyncEngine) || t.DeclaringType == typeof(SyncEngine))
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                              BindingFlags.Public | BindingFlags.NonPublic))
                .Any(m => Program.Callees(m, typeof(SyncEngine).Assembly)
                                 .Any(c => c.Name == "HandleRoutedPresentation"));
            if (!routed)
                yield return "L393 registry-is-not-the-actual-geoscape-router-source";
            var modalValues = Enum.GetValues(typeof(PhoenixPoint.Common.Utils.ModalType))
                                  .Cast<PhoenixPoint.Common.Utils.ModalType>().ToArray();
            if (modalValues.Any(m => GeoWindowCoverage.RuleForModal(m) == null) ||
                GeoWindowCoverage.DeclaredModals.Count != modalValues.Length)
                yield return "L393 modal-taxonomy-is-not-exhaustive";
            foreach (var modal in modalValues)
            {
                var rule = GeoWindowCoverage.RuleForModal(modal);
                bool classified = true; DurableWindowDisposition disposition = default(DurableWindowDisposition);
                try { disposition = DurableWindowRegistry.ModalDisposition(modal); }
                catch (InvalidOperationException) { classified = false; }
                if (!classified)
                    yield return "L393 modal-verdict-drift-" + modal;
                var durableFormerGaps = new HashSet<PhoenixPoint.Common.Utils.ModalType>
                {
                    PhoenixPoint.Common.Utils.ModalType.GeoHavenAttackOutcome,
                    PhoenixPoint.Common.Utils.ModalType.GeoAlienBaseOutcome,
                    PhoenixPoint.Common.Utils.ModalType.GeoScavengeOutcome,
                    PhoenixPoint.Common.Utils.ModalType.GeoPhoenixBaseDefenseOutcome,
                    PhoenixPoint.Common.Utils.ModalType.GeoAmbushOutcome,
                    PhoenixPoint.Common.Utils.ModalType.HavenInfiltrateOutcome,
                    PhoenixPoint.Common.Utils.ModalType.GeoPhoenixBaseInfestationOutcome,
                    PhoenixPoint.Common.Utils.ModalType.AncientSiteAttackOutcome,
                    PhoenixPoint.Common.Utils.ModalType.AncientSiteDefenceOutcome,
                    PhoenixPoint.Common.Utils.ModalType.BehemothAttackOutcome,
                    PhoenixPoint.Common.Utils.ModalType.InfestedHavenOutcome,
                    PhoenixPoint.Common.Utils.ModalType.InterceptionBrief,
                    PhoenixPoint.Common.Utils.ModalType.InterceptionOutcome,
                    PhoenixPoint.Common.Utils.ModalType.PandoranRevealResult,
                    PhoenixPoint.Common.Utils.ModalType.AlienResearchBrief,
                };
                bool expectedDurable = rule.Sync == WindowSync.Mirrored || durableFormerGaps.Contains(modal);
                if ((disposition == DurableWindowDisposition.Durable) != expectedDurable)
                    yield return "L393 exact-modal-disposition-wrong-" + modal;
            }
            // premise-changed: a fake new router family must be rejected, not defaulted ordinary/local.
        }
    }
}
