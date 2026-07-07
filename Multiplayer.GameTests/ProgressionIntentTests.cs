using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using Multiplayer.Validation;
using Xunit;

// Progression client intents (feat-progression-intents) — pure wire round-trips (Write → bytes → Read via
// the registry) for LevelUpAbility / SpendStatPoints, plus category + IHostOnlyApply + registration + the
// PS4 ownership-gate matrix (the PersonnelEditActionTests / ContainmentActionTests pattern). Apply/Validate
// bind live game types and are in-game verified; the pure SP split is covered by ProgressionSpendTests in
// Multiplayer.Tests.
public class ProgressionIntentTests
{
    private static byte[] Write(ISyncedAction a)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            a.Write(w);
            w.Flush();
            return ms.ToArray();
        }
    }

    private static T RoundTrip<T>(T a) where T : ISyncedAction
    {
        SyncRegistration.RegisterAll();
        var bytes = Write(a);
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            return (T)SyncedActionRegistry.Read(a.ActionId, r);
    }

    [Fact]
    public void LevelUpAbility_RoundTrip_PreservesSlotKeyAndFingerprint()
    {
        var original = new LevelUpAbilityAction(4242, 2, 5, "quickdraw-abilitydef-guid");   // Personal track
        var rt = RoundTrip(original);
        Assert.Equal(SyncedActionIds.LevelUpAbility, rt.ActionId);
        Assert.Equal(ActionCategory.ControlSoldiers, rt.Category);   // per-soldier ownership gate (PS4 policy)
        Assert.Equal(4242, rt.UnitId);
        Assert.Equal(2, rt.TrackSource);
        Assert.Equal(5, rt.SlotIndex);
        Assert.Equal("quickdraw-abilitydef-guid", rt.AbilityGuid);
        Assert.Equal(Write(original), Write(rt));   // byte-identical re-serialize
    }

    [Fact]
    public void SpendStatPoints_RoundTrip_PreservesStatAndDelta()
    {
        var original = new SpendStatPointsAction(9001, 1, 3);   // Will +3
        var rt = RoundTrip(original);
        Assert.Equal(SyncedActionIds.SpendStatPoints, rt.ActionId);
        Assert.Equal(ActionCategory.ControlSoldiers, rt.Category);
        Assert.Equal(9001, rt.UnitId);
        Assert.Equal(1, rt.StatId);
        Assert.Equal(3, rt.Delta);
        Assert.Equal(Write(original), Write(rt));
    }

    [Fact]
    public void LevelUpAbility_NullGuid_WritesAsEmpty_NeverThrows()
    {
        // BinaryWriter.Write(string) NREs on null; an empty fingerprint then fails closed in Validate.
        var rt = RoundTrip(new LevelUpAbilityAction(1, 0, 0, null));
        Assert.Equal(string.Empty, rt.AbilityGuid);
    }

    [Fact]
    public void ProgressionActions_RegisteredAfterRegisterAll()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.LevelUpAbility));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.SpendStatPoints));
    }

    [Fact]
    public void ProgressionActions_AreHostOnlyApply()
    {
        // IHostOnlyApply → the client never replays the apply (canon: client = pure mirror); the learned
        // ability + spent SP/stat points converge ONLY via the #9 live-state blob.
        Assert.IsAssignableFrom<IHostOnlyApply>(new LevelUpAbilityAction(0, 0, 0, "g"));
        Assert.IsAssignableFrom<IHostOnlyApply>(new SpendStatPointsAction(0, 0, 1));
    }

    [Fact]
    public void ProgressionActionIds_AreInPersonnelBlock_AndMirrorSurfaceIds()
    {
        Assert.InRange(SyncedActionIds.LevelUpAbility, (ushort)60, (ushort)79);
        Assert.InRange(SyncedActionIds.SpendStatPoints, (ushort)60, (ushort)79);
        Assert.Equal(SyncedActionIds.LevelUpAbility, SurfaceIds.LevelUpAbility);
        Assert.Equal(SyncedActionIds.SpendStatPoints, SurfaceIds.SpendStatPoints);
    }

    [Fact]
    public void OwnershipGate_MirrorsPs1Policy_ScopedControlSoldiersBit()
    {
        // Same gate matrix as rename/dismiss/transfer (PS4): scoped ControlSoldiers + per-soldier
        // assignment, FullCommander (the default co-op grant) owns everything, unknown actor fails closed.
        var scoped = Guid.NewGuid();
        PermissionManager.SetPermissionsRaw(scoped, (int)CampaignPermission.ControlSoldiers);
        PermissionManager.AssignSoldier(scoped, 640);
        Assert.True(PersonnelEditReflection.OwnsSoldier(scoped, 640));
        Assert.False(PersonnelEditReflection.OwnsSoldier(scoped, 641));

        var commander = Guid.NewGuid();
        PermissionManager.SetPermission(commander, CampaignPermission.FullCommander, true);
        Assert.True(PersonnelEditReflection.OwnsSoldier(commander, 641));

        Assert.False(PersonnelEditReflection.OwnsSoldier(Guid.Empty, 640));
        Assert.Equal(CampaignPermission.ControlSoldiers, PermissionGate.PermissionFor(ActionCategory.ControlSoldiers));
    }
}

/// <summary>
/// ch#9 blob PROGRESSION schema pin (feat-progression-intents): the PersonnelChannel PS2 record is the
/// game Serializer's whole-<c>GeoCharacter</c> graph, which serializes exactly the [SerializeMember]
/// members of [SerializeType] types — so learned abilities + spent stat points + SkillPoints + level
/// round-trip iff the attribute chain below holds. The LIVE serializer round-trip itself needs the
/// running game (GameUtl.GameComponent&lt;SerializationComponent&gt;().Serializer + Timing pump — memory
/// <c>pp-serializer-context-and-pump</c>) and is in-game proven via <c>PersonnelBlob</c>; this pin loads
/// the REAL Assembly-CSharp metadata and locks the schema + every reflection binding the intents use, so
/// a game update that moves/renames any of them fails HERE instead of silently no-oping in session.
/// </summary>
public class ProgressionBlobSchemaPinTests
{
    // Same install the game-bound csprojs compile against ($(GameManaged) in Multiplayer.GameTests.csproj).
    private const string GameManaged = @"D:\Steam\steamapps\common\Phoenix Point\PhoenixPointWin64_Data\Managed";

    private static Assembly _game;

    private static Assembly Game()
    {
        if (_game != null) return _game;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var path = Path.Combine(GameManaged, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        _game = Assembly.LoadFrom(Path.Combine(GameManaged, "Assembly-CSharp.dll"));
        return _game;
    }

    private static Type T(string fullName) => Game().GetType(fullName, throwOnError: true);

    private static bool HasAttr(MemberInfo m, string attrName)
        => m.GetCustomAttributesData().Any(a => a.AttributeType.Name == attrName);

    private static FieldInfo Field(Type t, string name)
    {
        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(f != null, t.Name + "." + name + " field missing");
        return f;
    }

    [Fact]
    public void GeoCharacterBlob_CarriesProgression_SerializeMemberChain()
    {
        // GeoCharacter._progression is in the blob…
        var geoChar = T("PhoenixPoint.Geoscape.Entities.GeoCharacter");
        Assert.True(HasAttr(geoChar, "SerializeTypeAttribute"));
        Assert.True(HasAttr(Field(geoChar, "_progression"), "SerializeMemberAttribute"));

        // …and CharacterProgression serializes learned abilities, tracks, stat points and SP.
        var prog = T("PhoenixPoint.Common.Entities.Characters.CharacterProgression");
        Assert.True(HasAttr(prog, "SerializeTypeAttribute"));
        Assert.True(HasAttr(Field(prog, "SkillPoints"), "SerializeMemberAttribute"));
        Assert.True(HasAttr(Field(prog, "_abilities"), "SerializeMemberAttribute"));      // learned abilities
        Assert.True(HasAttr(Field(prog, "_abilityTracks"), "SerializeMemberAttribute"));  // track slots (incl. personal picks)
        Assert.True(HasAttr(Field(prog, "_baseStats"), "SerializeMemberAttribute"));      // spent stat points
        Assert.True(HasAttr(Field(prog, "LevelProgression"), "SerializeMemberAttribute"));

        // Level is DERIVED (Def.GetLevel(Experience)) — it rides the blob via serialized Experience + Def.
        var lvl = T("PhoenixPoint.Common.Entities.Characters.LevelProgression");
        Assert.True(HasAttr(Field(lvl, "Experience"), "SerializeMemberAttribute"));
        Assert.True(HasAttr(Field(lvl, "Def"), "SerializeMemberAttribute"));
        var levelProp = lvl.GetProperty("Level");
        Assert.NotNull(levelProp);
        Assert.False(levelProp.CanWrite);   // no setter → nothing extra to serialize for level

        // Track/slot chain (the (trackSource, slotIndex, guid) key the intent uses survives the blob).
        var track = T("PhoenixPoint.Common.Entities.Characters.AbilityTrack");
        Assert.True(HasAttr(Field(track, "AbilitiesByLevel"), "SerializeMemberAttribute"));
        Assert.True(HasAttr(Field(track, "Source"), "SerializeMemberAttribute"));
        var slot = T("PhoenixPoint.Common.Entities.Characters.AbilityTrackSlot");
        Assert.True(HasAttr(Field(slot, "Ability"), "SerializeMemberAttribute"));
    }

    [Fact]
    public void ProgressionReflectionBindings_AllResolveOnRealAssembly()
    {
        var prog = T("PhoenixPoint.Common.Entities.Characters.CharacterProgression");
        // Single-overload natives the host apply invokes name-only (PersonnelEditReflection).
        foreach (var name in new[] { "GetAbilityTrack", "CanLearnAbility", "GetAbilitySlotCost",
                                     "LearnAbility", "GetBaseStat", "CanModifyBaseStat",
                                     "GetBaseStatCost", "ModifyBaseStat" })
        {
            var overloads = prog.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                .Where(m => m.Name == name).ToArray();
            Assert.True(overloads.Length == 1, "CharacterProgression." + name + " overloads=" + overloads.Length);
        }
        Assert.NotNull(T("PhoenixPoint.Geoscape.Entities.GeoCharacter").GetProperty("Progression"));
        // SP pools are raw public int fields (SkillPoints / faction Skillpoints — the TrySpendSkillPoints targets).
        Assert.Equal(typeof(int), Field(prog, "SkillPoints").FieldType);
        var fac = T("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
        Assert.Equal(typeof(int), Field(fac, "Skillpoints").FieldType);
        // Stat ids the wire carries (u8): Strength=0, Will=1, Speed=2.
        var attr = T("PhoenixPoint.Common.Entities.Characters.CharacterBaseAttribute");
        Assert.Equal(0, (int)Enum.Parse(attr, "Strength"));
        Assert.Equal(1, (int)Enum.Parse(attr, "Will"));
        Assert.Equal(2, (int)Enum.Parse(attr, "Speed"));
    }

    [Fact]
    public void HostMutoidGuardBindings_AllResolveOnRealAssembly()
    {
        // HasPandoranProgression host re-derivation chain (mirror of UIModuleCharacterProgression.cs:467):
        // GeoLevelController.SharedData → SharedData.SharedGameTags → SharedGameTagsDataDef.MutoidClassTag,
        // matched against GeoCharacter.GameTags.
        var geoLevel = T("PhoenixPoint.Geoscape.Levels.GeoLevelController");
        var sharedProp = geoLevel.GetProperty("SharedData");
        Assert.NotNull(sharedProp);
        var sharedTagsField = Field(sharedProp.PropertyType, "SharedGameTags");
        Field(sharedTagsField.FieldType, "MutoidClassTag");
        Assert.NotNull(T("PhoenixPoint.Geoscape.Entities.GeoCharacter").GetProperty("GameTags"));
    }

    [Fact]
    public void ClientInterceptorBindings_AllResolveOnRealAssembly()
    {
        var ui = T("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
        Assert.NotNull(ui.GetMethod("BuyAbility", BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(ui.GetMethod("CommitStatChanges", BindingFlags.Instance | BindingFlags.Public));
        // Second-spec suppress patch targets (relay intent = tracked follow-up, id 70).
        Assert.NotNull(ui.GetMethod("ChoseSecondSpecialization", BindingFlags.Instance | BindingFlags.Public));
        Field(ui, "DualClassPopupWindow");
        foreach (var name in new[] { "_character", "_hasPandoranProgression", "_boughtAbilitySlot", "_boughtAbility",
                                     "_currentStrengthStat", "_startingStrengthStat",
                                     "_currentWillStat", "_startingWillStat",
                                     "_currentSpeedStat", "_startingSpeedStat",
                                     "_currentSkillPoints", "_startingSkillPoints",
                                     "_currentFactionPoints", "_startingFactionPoints",
                                     "_currentMutagens", "_startingMutagens" })
            Field(ui, name);
        // Slot → track back-reference the interceptor walks to key (trackSource, slotIndex).
        var slot = T("PhoenixPoint.Common.Entities.Characters.AbilityTrackSlot");
        Assert.NotNull(slot.GetProperty("AbilityTrack"));
    }
}
