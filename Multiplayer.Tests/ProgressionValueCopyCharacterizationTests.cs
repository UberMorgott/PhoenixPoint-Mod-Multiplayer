using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

// Characterization tests for the mirror-progression fix (RCA 2026-07-10). PersonnelReflection.ApplySoldierState
// used to mirror the soldier's CharacterProgression by a WHOLE-OBJECT by-ref swap
// (CopyField(t,"_progression", ...) → SetValue(existing, decoded's instance)). TFTV repaints the open
// progression panel PER FRAME recomputing base+bonus off this object graph (Stats.cs:496-628); a mid-frame
// top-level swap gave torn base-vs-effective reads that never settled — the '1+3' split, flicker, and wrong
// per-point cost labels. The fix mirrors by VALUE into the EXISTING live instance, keeping its identity (and
// its StatModifiedCallback / OnAbilityAdded / LevelUpCallback wiring) so every mid-frame read is consistent.
//
// PersonnelReflection lives in the game-bound mod assembly (needs GeoCharacter/HarmonyLib/UnityEngine) and is
// NOT reachable from this BCL-only test project, so — exactly like ByRefActivatorCharacterizationTests — these
// pin the value-copy INVARIANTS the fix relies on against local POCOs shaped like CharacterProgression /
// LevelProgression, using the SAME reflection operations the production helpers use (FieldInfo.SetValue for
// scalars; IList.Clear + refill for lists). Field NAMES here mirror the decompiled game members one-for-one, so
// this doubles as the reflection contract; a game-update rename is caught at runtime by ApplySoldierState's
// never-silent DIAG (CopyMember logs the missing member by name).
public class ProgressionValueCopyCharacterizationTests
{
    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Mirrors PhoenixPoint.Common.Entities.Characters.LevelProgression (decompile LevelProgression.cs).
    private sealed class LevelProgLike
    {
        public int Experience;                 // [SerializeMember]  → COPIED
        public object Def;                     // [SerializeMember]  → COPIED
        public bool HasNewLevel;               // [SerializeMember]  → COPIED
        public Action<int> LevelUpCallback;    // wiring (Init)      → PRESERVED, never copied
        public int ExperienceReference;        // transient prop     → never copied
    }

    // Mirrors PhoenixPoint.Common.Entities.Characters.CharacterProgression (decompile CharacterProgression.cs).
    private sealed class ProgressionLike
    {
        public List<int> _baseStats = new List<int>();               // [SerializeMember] → COPIED (list refill)
        public List<object> _abilities = new List<object>();         // [SerializeMember] → COPIED (list refill)
        public List<object> _abilityTracks = new List<object>();     // [SerializeMember] → COPIED (list refill)
        public int SkillPoints;                                      // [SerializeMember] → COPIED
        public object _secondarySpecializationDef;                   // [SerializeMember] → COPIED
        public readonly LevelProgLike LevelProgression = new LevelProgLike(); // readonly, MUTABLE inner → inner COPIED
        public readonly object MainSpecDef = new object();           // readonly identity → never copied
        public Action StatModifiedCallback;                          // wiring → PRESERVED, never copied
    }

    private sealed class GeoCharLike { public ProgressionLike _progression; }

    // The exact contract: members the fix value-copies. Kept in lockstep with MirrorProgression /
    // MirrorLevelProgression in PersonnelReflection.
    private static readonly string[] ProgressionListMembers = { "_baseStats", "_abilities", "_abilityTracks" };
    private static readonly string[] ProgressionScalarMembers = { "SkillPoints", "_secondarySpecializationDef" };
    private static readonly string[] LevelProgressionMembers = { "Experience", "Def", "HasNewLevel" };

    private static void CopyScalar(object decoded, object existing, string field)
    {
        var f = existing.GetType().GetField(field, BF);
        Assert.NotNull(f);   // reflection-contract: the member must resolve
        f.SetValue(existing, f.GetValue(decoded));
    }

    private static void CopyList(object decoded, object existing, string field)
    {
        var f = existing.GetType().GetField(field, BF);
        Assert.NotNull(f);
        var target = (IList)f.GetValue(existing);
        var source = (IList)f.GetValue(decoded);
        target.Clear();
        foreach (var o in source) target.Add(o);
    }

    // Replicates MirrorProgression + MirrorLevelProgression, value-copy into the EXISTING instance.
    private static void MirrorProgression(ProgressionLike decoded, ProgressionLike existing)
    {
        foreach (var m in ProgressionListMembers) CopyList(decoded, existing, m);
        foreach (var m in ProgressionScalarMembers) CopyScalar(decoded, existing, m);
        foreach (var m in LevelProgressionMembers) CopyScalar(decoded.LevelProgression, existing.LevelProgression, m);
    }

    private static ProgressionLike DecodedSample() => new ProgressionLike
    {
        _baseStats = { 12, 20, 14 },
        _abilities = { "assault", "rally" },
        _abilityTracks = { "primary", "personal" },
        SkillPoints = 7,
        _secondarySpecializationDef = "Sniper",
        LevelProgression = { Experience = 5400, Def = "levelDef", HasNewLevel = true },
    };

    [Fact]
    public void ValueCopy_KeepsListInstances_AndUpdatesValues()
    {
        // The panel holds references INTO the object graph; a refill must keep the instance and land the values.
        var existing = new ProgressionLike { _baseStats = { 10, 10, 10 } };
        var heldStats = existing._baseStats;

        MirrorProgression(DecodedSample(), existing);

        Assert.Same(heldStats, existing._baseStats);            // instance preserved — no torn mid-frame read
        Assert.Equal(new[] { 12, 20, 14 }, existing._baseStats); // values mirrored
        Assert.Equal(new object[] { "assault", "rally" }, existing._abilities);
        Assert.Equal(new object[] { "primary", "personal" }, existing._abilityTracks);
    }

    [Fact]
    public void ValueCopy_MirrorsScalars()
    {
        var existing = new ProgressionLike { SkillPoints = 0, _secondarySpecializationDef = null };
        MirrorProgression(DecodedSample(), existing);
        Assert.Equal(7, existing.SkillPoints);
        Assert.Equal("Sniper", existing._secondarySpecializationDef);
    }

    [Fact]
    public void ValueCopy_KeepsLevelProgressionInstance_AndPreservesLevelUpCallback()
    {
        // CAVEAT: LevelProgression is a readonly ref with mutable Level/Experience — inner value-copy keeps the
        // instance so its LevelUpCallback (wired in CharacterProgression.Init) survives.
        var existing = new ProgressionLike();
        var heldLevel = existing.LevelProgression;
        bool fired = false;
        existing.LevelProgression.LevelUpCallback = _ => fired = true;

        MirrorProgression(DecodedSample(), existing);

        Assert.Same(heldLevel, existing.LevelProgression);      // inner instance preserved
        Assert.Equal(5400, existing.LevelProgression.Experience); // XP mirrored (Level derives from it)
        Assert.True(existing.LevelProgression.HasNewLevel);
        Assert.NotNull(existing.LevelProgression.LevelUpCallback); // wiring survived
        existing.LevelProgression.LevelUpCallback(1);
        Assert.True(fired);
    }

    [Fact]
    public void ValueCopy_PreservesWiringAndReadonlyIdentity()
    {
        // StatModifiedCallback + MainSpecDef are NOT in the copy set → they must be untouched.
        var existing = new ProgressionLike();
        bool fired = false;
        existing.StatModifiedCallback = () => fired = true;
        var mainSpec = existing.MainSpecDef;
        var xpRef = existing.LevelProgression.ExperienceReference = 999; // transient, not copied

        MirrorProgression(DecodedSample(), existing);

        Assert.NotNull(existing.StatModifiedCallback);
        existing.StatModifiedCallback();
        Assert.True(fired);
        Assert.Same(mainSpec, existing.MainSpecDef);
        Assert.Equal(999, existing.LevelProgression.ExperienceReference); // untouched
    }

    [Fact]
    public void WholeObjectSwap_ChurnsInstance_TheTornReadThisFixRemoves()
    {
        // Regression guard: the OLD by-ref swap (existing._progression = decoded._progression) churns the
        // top-level instance, so a reference held by the per-frame panel goes stale mid-frame → torn reads.
        var existing = new GeoCharLike { _progression = new ProgressionLike { _baseStats = { 10, 10, 10 } } };
        var heldProg = existing._progression;
        var heldStats = existing._progression._baseStats;

        existing._progression = DecodedSample();   // the reverted-behavior swap

        Assert.NotSame(heldProg, existing._progression);
        Assert.NotSame(heldStats, existing._progression._baseStats); // held sub-ref stale — the bug
    }

    [Fact]
    public void CopiedMemberInventory_IsExactlyTheContract_AndDisjointFromWiring()
    {
        // Every named copy target resolves on the shape (rename would break here AND in the runtime DIAG).
        foreach (var m in ProgressionListMembers) Assert.NotNull(typeof(ProgressionLike).GetField(m, BF));
        foreach (var m in ProgressionScalarMembers) Assert.NotNull(typeof(ProgressionLike).GetField(m, BF));
        foreach (var m in LevelProgressionMembers) Assert.NotNull(typeof(LevelProgLike).GetField(m, BF));

        // Wiring / readonly-identity / transient members must NOT appear in any copy set.
        var copied = new HashSet<string>(ProgressionListMembers.Concat(ProgressionScalarMembers).Concat(LevelProgressionMembers));
        foreach (var forbidden in new[] { "MainSpecDef", "StatModifiedCallback", "LevelUpCallback", "ExperienceReference" })
            Assert.DoesNotContain(forbidden, copied);
    }
}
