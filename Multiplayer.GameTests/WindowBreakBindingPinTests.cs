using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

/// <summary>
/// Reflection-binding pin for the TS6 direct geometry-break capture (gap-window-break-geometry): loads the REAL
/// installed Assembly-CSharp and locks every game member <c>WindowBreakCapturePatch</c> +
/// <c>TacticalStructDamageSync.HostCaptureDirectBreak</c> bind, so a game update that moves/renames any of them
/// fails HERE instead of silently no-oping in session (patch Prepare() returns false → class skipped quietly).
/// Also pins the two facts the design rests on: (a) the actor-passage break path
/// (<c>TacticalNavigationComponent.TriggerWindowBreak</c> → <c>Breakable.ApplyDamage(Vector3,Vector3,float)</c>)
/// exists, and (b) <c>Destructable</c>'s override of that overload is the only OTHER concrete implementation
/// (a NOT-IMPLEMENTED stub in the decompile) — i.e. patching Breakable covers the funnel.
/// </summary>
public class WindowBreakBindingPinTests
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

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Vector3 lives in UnityEngine.CoreModule (not Assembly-CSharp) — match overloads by param-type FULL NAMES so
    // the test never eagerly binds Unity types (they resolve lazily through the Game() resolver when inspected).
    private static MethodInfo MethodByParamNames(Type t, string name, BindingFlags flags, params string[] paramFullNames)
    {
        foreach (var m in t.GetMethods(flags))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length != paramFullNames.Length) continue;
            bool match = true;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType.FullName != paramFullNames[i]) { match = false; break; }
            if (match) return m;
        }
        return null;
    }

    private const string Vec3 = "UnityEngine.Vector3";
    private const string F32 = "System.Single";

    [Fact]
    public void Breakable_DirectBreakOverload_AndBrokenGuard_Resolve()
    {
        var breakable = T("PhoenixPoint.Tactical.Levels.Destruction.Breakable");

        // The patch target: EXACT (Vector3, Vector3, float) — must stay distinct from the radius overload.
        var m = MethodByParamNames(breakable, "ApplyDamage", All, Vec3, Vec3, F32);
        Assert.NotNull(m);
        Assert.True(m.IsPublic && m.IsVirtual);

        // The dedup pre-state field the prefix snapshots.
        var broken = breakable.GetField("_broken", All);
        Assert.NotNull(broken);
        Assert.Equal(typeof(bool), broken.FieldType);
    }

    [Fact]
    public void PassageBreakPath_TriggerWindowBreak_Exists()
    {
        // The native caller that makes this the actor-passage chokepoint (vault / move-through).
        var nav = T("PhoenixPoint.Tactical.Entities.TacticalNavigationComponent");
        Assert.NotNull(nav.GetMethod("TryWindowBreak", All));
        Assert.NotNull(nav.GetMethod("TriggerWindowBreak", All));
    }

    [Fact]
    public void DirectionOverload_Implementations_ArePinned_NothingUnpatched()
    {
        // DestructableBase.ApplyDamage(Vector3,Vector3,float) is abstract with exactly THREE concrete
        // implementations (verified on the real assembly): Breakable (patched), Swappables (patched — glass pane
        // swap-to-broken) and Destructable (a NOT-IMPLEMENTED log stub — nothing to mirror). If a game update
        // adds/renames a subclass, this pin fails → review WindowBreakCapturePatch coverage.
        var baseType = T("PhoenixPoint.Tactical.Levels.Destruction.DestructableBase");
        var baseMethod = MethodByParamNames(baseType, "ApplyDamage", All, Vec3, Vec3, F32);
        Assert.NotNull(baseMethod);
        Assert.True(baseMethod.IsAbstract);

        Type[] allTypes;
        try { allTypes = Game().GetTypes(); }
        catch (ReflectionTypeLoadException ex) { allTypes = ex.Types; }   // keep the types that DID load

        var implementations = new List<string>();
        foreach (var t in allTypes)
        {
            if (t == null || t.IsAbstract || !baseType.IsAssignableFrom(t)) continue;
            var m = MethodByParamNames(t, "ApplyDamage", All | BindingFlags.DeclaredOnly, Vec3, Vec3, F32);
            if (m != null) implementations.Add(t.FullName);
        }
        implementations.Sort();
        Assert.Equal(new List<string>
        {
            "PhoenixPoint.Tactical.Levels.Destruction.Breakable",
            "PhoenixPoint.Tactical.Levels.Destruction.Destructable",
            "PhoenixPoint.Tactical.Levels.Destruction.Swappables",
        }, implementations);
    }

    [Fact]
    public void Swappables_PatchTarget_Resolves()
    {
        // The second patched implementation (glass panes that swap to a broken state; its native re-break guard
        // is its own collider being disabled — read via UnityEngine.Collider.enabled, resolved reflectively).
        var swappables = T("PhoenixPoint.Tactical.Levels.Destruction.Swappables");
        var m = MethodByParamNames(swappables, "ApplyDamage", All, Vec3, Vec3, F32);
        Assert.NotNull(m);
        Assert.True(m.IsPublic && m.IsVirtual);
    }

    [Fact]
    public void ClientReplayTarget_ReceiverApplyDamage_Resolves()
    {
        // The client-side replay seam the direct break funnels into (unchanged, shared with combat hits).
        var receiver = T("PhoenixPoint.Tactical.Levels.Destruction.DestructableDamageReceiver");
        var dr = T("PhoenixPoint.Tactical.Entities.DamageResult");
        Assert.NotNull(receiver.GetMethod("ApplyDamage", All, null, new[] { dr }, null));
        var toughness = T("PhoenixPoint.Tactical.Levels.Destruction.DestructableBase").GetMethod("GetToughness", All);
        Assert.NotNull(toughness);   // receiver max health source — DirectBreakHealthDamage must exceed it
    }
}
