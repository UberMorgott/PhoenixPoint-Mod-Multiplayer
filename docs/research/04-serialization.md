# Serialization & Save/Load Systems — Network Sync Implications

Source: `decompiled\AssemblyCSharp\Assembly-CSharp\src`

## Architecture Overview

Custom-built, reflection-driven, attribute-marked serialization engine supporting binary (default) and JSON formats with optional GZip compression.

```
Layer                   Key Files
──────────────────────────────────────────────────────
Game orchestration      PhoenixSaveManager.cs
Serialization bridge    SerializationComponent.cs
Reflection engine       Serializer.cs + TypeData.cs
Stream I/O              BinRead/WriteStream.cs, JsonRead/WriteStream.cs
```

## Save File Structure

Save files use named sections:

```
┌─────────────────────────────────┐
│ Section: "Metadata"             │ — SavegameMetaData/PPSavegameMetaData
├─────────────────────────────────┤
│ Section: "Contents"             │ — LevelSerializedData { Objects: [...] }
├─────────────────────────────────┤
│ Section: "LevelParams"          │ — TacticalGameParams or GeoscapeGameParams
├─────────────────────────────────┤
│ Section: "RuntimeDefs"          │ — runtime-generated defs
├─────────────────────────────────┤
│ Section: "Geoscape" (optional)  │ — embedded geoscape state in tactical saves
└─────────────────────────────────┘
```

File extensions: `.zsav` (binary+gzip, default), `.zjsav` (json+gzip), `.sav` (binary), `.jsav` (json).

## Serialization Attributes

```csharp
[AttributeUsage(Class | Struct | Interface)]
public class SerializeTypeAttribute : Attribute {
    public bool Embedded;
    public SerializeMembersType SerializeMembersByDefault; // SerializeMarked, SerializeOwn, SerializeAll
    public int Version = 1;
    public string[] PreviousFullNames;  // type rename support
}

[AttributeUsage(Field | Property)]
public class SerializeMemberAttribute : Attribute {
    public SerializeMode SerializeMode = ReadWrite;
    public string[] PreviousNames;
    public object DefaultValue;
}
```

## State Snapshot Pattern (Instance Data)

The game uses a **RecordInstanceData() → DTO → serialization** pattern:

```csharp
// TacticalLevelController.cs
public TacLevelInstanceData RecordInstanceData() {
    return new TacLevelInstanceData {
        CurrentFactionDef = CurrentFaction.Faction.FactionDef,
        Factions = Factions.Select(f => f.RecordInstanceData()).ToList(),
        TacMission = TacMission,
        GameplayStatistics = GameplayStatistics,
        // ...
    };
}
```

Key snapshot DTOs:

| Snapshot | Class | Contents |
|----------|-------|----------|
| Tactical level | `TacLevelInstanceData` | Faction state, mission data, achievement, extra settings |
| Tactical full | `TacLevelSavegame.Data` | Instance data + actors + destructibles + voxels + fog |
| Geoscape | `GeoLevelInstanceData` | Faction state, timing, event system, difficulty, mist, |
| Geoscape faction | `GeoFactionInstanceData` | Wallet, storage, research, diplomacy, game tags |
| Soldier (geo) | `GeoCharacter` | Full soldier state: stats, identity, progression, items, fatigue |
| Soldier (tac) | `TacticalActorBase` + `TacticalActor` | Health, position, abilities state, effects |

## State Snapshot Sizes (Estimated)

| Snapshot | Size |
|----------|------|
| Full tactical save (8 soldiers, small map) | 500KB–3MB (compressed) |
| Full geoscape save | 1MB–5MB (compressed) |
| Single soldier (GeoCharacter) | 5KB–20KB |
| Single tactical actor | 2KB–10KB (minus visual data) |
| TacLevelInstanceData (no actors) | 50KB–200KB |

## Network Sync Implications

### What Already Works
- **Serializer operates on `Stream` objects** — can write to `MemoryStream` for network packets
- **InstanceData pattern already produces snapshots** — `RecordInstanceData()` captures authoritative state
- **Section-based architecture** maps naturally to network replication channels
- **Object ID system** in `SerializationWriter` assigns integer IDs to objects

### What Needs Work
- **No dirty/delta tracking** — current system writes everything every time
- **No universal entity ID** — `GeoTacUnitId` exists for soldiers, but not for all objects
- **All-or-nothing loading** — `SetReadObjects()` processes everything at once
- **Coroutine pattern** (`IEnumerator<NextUpdate>`) embedded in all save/load

## Recommended Sync Approach

State synchronization (not lockstep):

1. **Host runs the full game** — original save/load unchanged
2. **Host snapsots state** via existing `RecordInstanceData()` after each action
3. **Host broadcasts action results** (not raw state) — lightweight `(actionType, actorId, resultData)` messages
4. **Clients apply results** — reproduce visual outcome locally
5. **Full state sync** — only on join/reconnect via existing save format

## Reuse: Snapshot Use Cases

Reuse the game's own serializers for state snapshots + client reconstruction rather than rebuilding from scratch. Snapshot use cases:

- **Client join** at session start — handled by the save-transfer + barrier path → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §2.
- **Reconnect** — the host snapshots **live** state through the game's **own save system** and re-runs the start barrier for all peers → [09-disconnect-reconnect](09-disconnect-reconnect.md). This is the practical realization that lets reconnect avoid a bespoke live-state serializer.
- **Full-state resync after divergence** — the same reload-all path doubles as the divergence recovery.

Custom tactical serialization (the snapshot DTOs above) is the **fallback only** if a mid-battle save is unavailable → see the mid-battle-save caveat in [09-disconnect-reconnect](09-disconnect-reconnect.md) and [specs/03-open-questions-sdk](../specs/03-open-questions-sdk.md).
