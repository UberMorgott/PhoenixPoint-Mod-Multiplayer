namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure, Unity-free stable map from the <c>PhoenixPoint.Common.Core.ResourceType</c> integer value to the
    /// enum MEMBER NAME — the key the native reward renderer uses
    /// (<c>UIModuleSiteEncounters.cs:417: ResourcesList.GetDef&lt;ViewElementDef&gt;(item3.Type.ToString())</c>,
    /// where <c>Type.ToString()</c> is the enum name "Materials" / "Supplies" / …, NEVER the number).
    ///
    /// WHY a compile-time table instead of a runtime <c>Enum.GetName</c>: the client renderer previously resolved
    /// the name via <c>AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType")</c> + <c>Enum.GetName</c>.
    /// When that fuzzy/cached type lookup returned null (load-order / cache miss), the code fell back to
    /// <c>raw.ToString()</c> — the LITERAL "2"/"1" — and <c>ResourcesList.GetDef("2")</c> has no such item → the
    /// reward resource line was silently dropped ("unresolved type 2"/"unresolved type 1" in-game, FIX #1). The
    /// enum values are compile-time constants identical host↔client, so this table can be authored once and never
    /// returns a numeric string the NamedListDef cannot resolve.
    ///
    /// Mirrors the decompile verbatim (PhoenixPoint.Common.Core/ResourceType.cs, verified 2026-06-20):
    ///   None=0, Supplies=1, Materials=2, Tech=4, AICore1=8, AICore2=0x10, AICore3=0x20, Research=0x40,
    ///   Production=0x80, Mutagen=0x100, LivingCrystals=0x200, Orichalcum=0x400, ProteanMutane=0x800.
    /// </summary>
    public static class RewardResourceTypes
    {
        /// <summary>
        /// The enum member name for the stable <c>ResourceType</c> integer value, or <c>null</c> for an undefined
        /// value (e.g. a flag combination or a future member). Callers MUST treat null as "drop this single line"
        /// — never substitute the number (the NamedListDef is keyed by name and can never resolve a numeric key).
        /// </summary>
        public static string NameForRaw(int raw)
        {
            switch (raw)
            {
                case 0: return "None";
                case 1: return "Supplies";          // displayed "Provisions" / "Провиант"
                case 2: return "Materials";         // displayed "Materials" / "Материалы"
                case 4: return "Tech";
                case 8: return "AICore1";
                case 0x10: return "AICore2";
                case 0x20: return "AICore3";
                case 0x40: return "Research";
                case 0x80: return "Production";
                case 0x100: return "Mutagen";
                case 0x200: return "LivingCrystals";
                case 0x400: return "Orichalcum";
                case 0x800: return "ProteanMutane";
                default: return null;
            }
        }
    }
}
