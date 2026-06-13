using System.Collections.Generic;
using Multipleer.Network.MessageLayer;
using Multipleer.Validation;

namespace Multipleer.Network.CommandSync
{
    // Declarative, BROAD curated intercept table (prune-later). One row per CampaignActionType.
    // SignatureConfirmed=false rows are wired-but-dormant: the runtime resolver skips an unconfirmed
    // row instead of throwing, so an absent/renamed engine method never crashes the relay.
    public sealed class InterceptEntry
    {
        public CampaignActionType ActionType;
        public CampaignPermission RequiredPermission;
        public string DeclaringTypeName;    // AccessTools.TypeByName key
        public string MethodName;           // method on that type
        public string[] ParamTypeNames;     // overload disambiguation (AccessTools.TypeByName per token); null = unique
        public bool SignatureConfirmed;     // false => pending decompile confirmation (skip at resolve)
    }

    public static class InterceptRegistry
    {
        private static readonly Dictionary<CampaignActionType, InterceptEntry> _entries =
            new Dictionary<CampaignActionType, InterceptEntry>
        {
            [CampaignActionType.StartTravel] = new InterceptEntry
            {
                ActionType = CampaignActionType.StartTravel,
                RequiredPermission = CampaignPermission.ManageAircraft,
                DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoVehicle",
                MethodName = "StartTravel",
                // OVERLOADED: StartTravel(List<GeoSite>) vs StartTravel(List<Vector3>) — pin the GeoSite overload.
                ParamTypeNames = new[] { "System.Collections.Generic.List`1[PhoenixPoint.Geoscape.Entities.GeoSite]" },
                SignatureConfirmed = true
            },
            [CampaignActionType.SetTimeState] = new InterceptEntry
            {
                ActionType = CampaignActionType.SetTimeState,
                RequiredPermission = CampaignPermission.ControlTime,
                // Applied via the live UIModuleTimeControl (SelectTimePreset + OnPauseTime),
                // resolved in CommandExecutor.ApplySetTime — not by the registry resolver.
                DeclaringTypeName = "PhoenixPoint.Geoscape.View.ViewModules.UIModuleTimeControl",
                MethodName = "SetTimeState",
                ParamTypeNames = null,
                SignatureConfirmed = true
            },
            [CampaignActionType.StartResearch] = new InterceptEntry
            {
                ActionType = CampaignActionType.StartResearch,
                RequiredPermission = CampaignPermission.ManageResearch,
                // SetQueued absent in current build; real candidate Research.AddResearchToQueue(ResearchElement).
                DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Research.Research",
                MethodName = "SetQueued",
                ParamTypeNames = null,
                SignatureConfirmed = false   // pending signature confirmation
            }
        };

        public static InterceptEntry Lookup(CampaignActionType type)
            => _entries.TryGetValue(type, out var e) ? e : null;

        public static IEnumerable<InterceptEntry> All => _entries.Values;
    }
}
