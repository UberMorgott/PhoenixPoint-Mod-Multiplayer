# Foundation Build Plan — Multipleer (lobby / identity / save-transfer / permissions)

> Concrete, source-grounded BUILD SPEC for foundation work-items #1, #2, #6, #7, #8.
> Coders implement against THIS document; do not re-derive values. Match the existing
> `MessageSerializer` / `NetworkMessage` byte conventions verbatim (cited below).
>
> Design sources: `docs/specs/02-session-lifecycle-and-player-management.md` (PRIMARY),
> `docs/specs/01-design.md` §2.3/§3, `docs/engine/02-transport-layer.md` (message catalog),
> `docs/research/04-serialization.md`.
> Code sources: `src/Network/MessageLayer/{PacketType,MessageSerializer,NetworkMessage}.cs`,
> `src/Network/{NetworkEngine,SessionManager}.cs`, `src/Validation/PermissionManager.cs`,
> `src/Validation/ActionValidator.cs`, `src/Harmony/CampaignPatches.cs`.
> Game API source: `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src` (decompile).

---

## 0. Observed conventions (ground truth — DO NOT deviate)

- **Envelope** (`NetworkMessage.Serialize`, NetworkMessage.cs:33-58): fixed header
  `[type:1][senderSteamId:8][messageId:16(Guid)][timestamp:8][payloadLen:4]` then payload.
  Min length 37 bytes. **Little-endian** via `BitConverter` (host LE) and `BinaryWriter`.
- **Payload (de)serializers** live in `MessageSerializer` and use
  `MemoryStream`+`BinaryWriter`/`BinaryReader` (MessageSerializer.cs:14-22). Conventions:
  - `Guid` → `bw.Write(g.ToByteArray())` (16 bytes) / `new Guid(br.ReadBytes(16))`.
  - `string` → `bw.Write(s ?? "")` (BinaryWriter length-prefixed UTF8, 7-bit-encoded
    length) / `br.ReadString()`. **Null coalesced to ""** (see lines 21,53,109).
  - variable `byte[]` → `bw.Write(arr?.Length ?? 0)` (int32) then bytes if >0;
    read `int len = br.ReadInt32(); len>0 ? br.ReadBytes(len) : null` (lines 22-25,116-120).
  - `enum` → `bw.Write((byte)e)` / `(E)br.ReadByte()`.
  - primitives → `bw.Write(value)` (e.g. `ulong`,`int`,`long`).
- **PacketType** is `: byte`, grouped by range (PacketType.cs). Current members & a free-slot map:
  - Connection `0x01`–`0x07` (free: `0x08`–`0x0F`)
  - Session `0x10`–`0x17` (free: `0x18`–`0x1F`)
  - Tactical `0x20`–`0x27`
  - Campaign `0x30`–`0x34`
  - Management `0x40`–`0x42` (free: `0x43`–`0x4F`)
  - Chat `0x50`; Transport sentinel `0xF0` (reserved, leave at top).
- **CURRENT MAX assigned byte = `0xF0` (TransportInternal)**; it is a reserved sentinel.
  **Highest non-sentinel = `0x50` (ChatMessage).** New foundation members are placed into the
  existing free slots of their phase range (NOT appended after 0x50), per the grouping convention.
- **Reuse over churn:** the as-built `ClientReady`(0x14)/`AllClientsReady`(0x15) already implement
  the barrier ready-gate (spec 02 §2 note). Reuse them — do **not** add a new `READY`/`BEGIN`-as-ready.

---

## 1. PACKET TYPES TABLE

Legend: **EXIST** = already in enum (possibly re-purposed payload); **NEW** = add member.

| Catalog name | PacketType member | Byte | Status | Direction | Payload fields | Purpose |
|---|---|---|---|---|---|---|
| JOIN | `ConnectionRequest` | `0x01` | EXIST, reshape payload | C→H | `playerGUID:Guid(16)`, `nickname:string` | Client announces persistent identity on connect (#7). Currently sent empty (SessionManager.HandleConnectionRequest:99). |
| PEER_LIST | `PlayerListUpdate` | `0x42` | EXIST, define payload+route | H→all | `count:int`, then per peer `{ peerSteamId:ulong, playerGUID:Guid(16), nickname:string, permissions:int, ready:byte }` | Authoritative lobby roster broadcast. Member exists, currently **unrouted + no serializer**. |
| LEAVE | `ClientLeave` | `0x08` | NEW | C→H / H→all | `peerSteamId:ulong` | Graceful lobby/session leave (distinct from transport drop). |
| RENAME | `PlayerRename` | `0x09` | NEW | any→H→all | `peerSteamId:ulong`, `newNickname:string` | Live nickname edit (spec 02 §1). |
| SAVE_CHUNK | `SaveChunk` | `0x18` | NEW | H→client | `transferId:Guid(16)`, `totalBytes:long`, `offset:long`, `chunk:byte[]` (len-prefixed) | One slice of the save blob; chunked for Steam P2P limit (spec 02 §2). |
| SAVE_DONE | `SaveDone` | `0x19` | NEW | H→client | `transferId:Guid(16)`, `totalBytes:long`, `fileExtension:string`, `crc32:uint` | Signals transfer complete; client reassembles + verifies length/crc. |
| PROGRESS | `LoadProgress` | `0x1A` | NEW | C→H→all | `peerSteamId:ulong`, `phase:byte(0=download,1=load)`, `percent:byte(0-100)` | Per-peer loading progress (spec 02 §2 loading screen). May be **unreliable**. |
| LOADED | `ClientLoaded` | `0x1B` | NEW | C→H | `peerSteamId:ulong`, `transferId:Guid(16)`, `ok:byte` | Client finished loading the save; barrier ack. |
| BEGIN | `SessionBegin` | `0x1C` | NEW | H→all | `serverStartTicks:long` | Barrier release — all unblock & enter geoscape simultaneously (spec 02 §2). |
| ASSIGN_OWNER | `SoldierAssignment` | `0x41` | EXIST, define payload+route | H→all | `geoUnitId:int`, `ownerPlayerGUID:Guid(16)` (`Guid.Empty` = unassign) | soldierID→playerGUID ownership (spec 02 §3). Member exists, currently **unrouted + no serializer**. |
| PERMISSION (reshaped) | `PermissionUpdate` | `0x40` | EXIST, reshape payload | H→all | `playerGUID:Guid(16)`, `flagBit:byte`, `value:byte(0/1)` | Per-flag permission set keyed by GUID (#7). Replaces `{steamId,mask}`. |
| HOST_LEFT (notice) | `HostDisconnected` | `0x05` | EXIST, route only | H→all | (empty) | Session-end notice; currently **unrouted** (#8). |

Notes:
- JOIN reuses `ConnectionRequest` (no new code path for accept/reject; `ConnectionAccepted`/`Rejected`
  stay as-is). The reshaped payload is additive — host parses GUID+nickname instead of empty.
- PEER_LIST/ASSIGN_OWNER reuse the already-named-but-dormant `PlayerListUpdate`/`SoldierAssignment`
  members; this is why they appear under #8 "dead routes" AND here.

---

## 2. SERIALIZER SPECS (field-by-field, follows §0 conventions)

Add these to `MessageSerializer` (mirror existing method pairs). Add matching message
DTO classes in the same file's "Action Data Types" region.

### JOIN (reuse ConnectionRequest payload)
```
Write: bw.Write(playerGuid.ToByteArray());  // 16
       bw.Write(nickname ?? "");            // BinaryWriter string
Read:  guid = new Guid(br.ReadBytes(16));
       nickname = br.ReadString();
```
DTO: `JoinMessage { Guid PlayerGuid; string Nickname; }`

### PEER_LIST (PlayerListUpdate)
```
Write: bw.Write(peers.Count);               // int32
       foreach peer:
         bw.Write(peer.SteamId);            // ulong
         bw.Write(peer.PlayerGuid.ToByteArray()); // 16
         bw.Write(peer.Nickname ?? "");
         bw.Write(peer.Permissions);        // int32
         bw.Write((byte)(peer.Ready ? 1:0));
Read:  int n = br.ReadInt32(); loop n times reading the same order.
```
DTO: `PeerListEntry { ulong SteamId; Guid PlayerGuid; string Nickname; int Permissions; bool Ready; }`,
returned as `List<PeerListEntry>`.

### LEAVE (ClientLeave)
```
Write: bw.Write(peerSteamId);   // ulong
Read:  return br.ReadUInt64();
```

### RENAME (PlayerRename)
```
Write: bw.Write(peerSteamId); bw.Write(newNickname ?? "");
Read:  steamId = br.ReadUInt64(); name = br.ReadString();
```

### SAVE_CHUNK (SaveChunk)
```
Write: bw.Write(transferId.ToByteArray()); // 16
       bw.Write(totalBytes);               // long
       bw.Write(offset);                   // long
       bw.Write(chunk?.Length ?? 0);       // int32 (len prefix)
       if(len>0) bw.Write(chunk);
Read:  id=new Guid(br.ReadBytes(16)); total=br.ReadInt64(); off=br.ReadInt64();
       len=br.ReadInt32(); chunk= len>0? br.ReadBytes(len): null;
```
DTO: `SaveChunkMessage { Guid TransferId; long TotalBytes; long Offset; byte[] Chunk; }`

### SAVE_DONE (SaveDone)
```
Write: bw.Write(transferId.ToByteArray()); bw.Write(totalBytes);  // long
       bw.Write(fileExtension ?? ".zsav"); bw.Write(crc32);       // uint
Read:  id=new Guid(br.ReadBytes(16)); total=br.ReadInt64();
       ext=br.ReadString(); crc=br.ReadUInt32();
```

### PROGRESS (LoadProgress)  — keep tiny; may go unreliable
```
Write: bw.Write(peerSteamId); bw.Write((byte)phase); bw.Write((byte)percent);
Read:  steamId=br.ReadUInt64(); phase=br.ReadByte(); percent=br.ReadByte();
```

### LOADED (ClientLoaded)
```
Write: bw.Write(peerSteamId); bw.Write(transferId.ToByteArray()); bw.Write((byte)(ok?1:0));
Read:  steamId=br.ReadUInt64(); id=new Guid(br.ReadBytes(16)); ok=br.ReadByte()!=0;
```

### BEGIN (SessionBegin)
```
Write: bw.Write(serverStartTicks);  // long
Read:  return br.ReadInt64();
```

### ASSIGN_OWNER (SoldierAssignment)
```
Write: bw.Write(geoUnitId);                       // int32
       bw.Write(ownerPlayerGuid.ToByteArray());   // 16  (Guid.Empty=unassign)
Read:  id=br.ReadInt32(); owner=new Guid(br.ReadBytes(16));
```

### PERMISSION reshaped (PermissionUpdate) — replaces current SerializePermissionUpdate
```
Write: bw.Write(playerGuid.ToByteArray());  // 16
       bw.Write((byte)flagBit);             // 0..9 (bit index, NOT mask)
       bw.Write((byte)(value?1:0));
Read:  guid=new Guid(br.ReadBytes(16)); flagBit=br.ReadByte(); value=br.ReadByte()!=0;
```
New signatures:
`SerializePermissionUpdate(Guid playerGuid, byte flagBit, bool value)` and
`(Guid playerGuid, byte flagBit, bool value) DeserializePermissionUpdate(byte[])`.
`flagBit` is the **bit index** (0..9) of the `CampaignPermission` flag, so a single toggle is
sent (spec 02 §4 `{playerGUID, flag, value}`), not a whole mask.

---

## 3. PERMISSION FIX (#2 + #7)

### 3.1 Final `CampaignPermission` enum (9 flags) — file `src/Validation/PermissionManager.cs`
Drift: code currently has 8 flags with `FullCommander = 1<<7`. Spec 01-design §2.3 / spec 02 §4
mandate 9 flags inserting `ControlTime`/`ForceEndTurn`. **Final, authoritative:**
```csharp
[Flags]
public enum CampaignPermission
{
    None                = 0,
    ControlSoldiers     = 1 << 0,   // 0x001
    ManageEquipment     = 1 << 1,   // 0x002
    ManageBases         = 1 << 2,   // 0x004
    ManageResearch      = 1 << 3,   // 0x008
    ManageManufacturing = 1 << 4,   // 0x010
    ManageRecruitment   = 1 << 5,   // 0x020
    ManageAircraft      = 1 << 6,   // 0x040
    ControlTime         = 1 << 7,   // 0x080  (NEW — geoscape clock; spec08)
    ForceEndTurn        = 1 << 8,   // 0x100  (NEW — tactical turn-end; spec07)
    FullCommander       = 1 << 9    // 0x200  (MOVED from 1<<7)
}
```
> Flag count is 10 enum members incl. `None`; "9 flags" = 9 grantable bits (1<<0..1<<9).
> `flagBit` on the wire = the shift amount (0..9). `FullCommander` moves 1<<7 → 1<<9.

### 3.2 Re-key from SteamId → playerGUID
`PlayerAssignment` and the `_assignments` dictionary are keyed by `ulong SteamId`. Re-key to
`Guid PlayerGuid`:
- `PlayerAssignment.SteamId (ulong)` → `PlayerGuid (Guid)` (keep `PlayerName`, `Permissions`,
  `OwnedSoldierIds`).
- `Dictionary<ulong,PlayerAssignment>` → `Dictionary<Guid,PlayerAssignment>`.
- All public methods change their key param `ulong steamId` → `Guid playerGuid`:
  `AssignSoldier`, `UnassignSoldier`, `SetPermission`, `SetPermissionsRaw`, `CanControlSoldier`,
  `HasCampaignPermission`, `GetPermissions`, `GetAssignment`; `GetOwnerOfSoldier` returns `Guid?`.
- `SetPermission(Guid, CampaignPermission, bool)` stays the by-flag mutator (PERMISSION applies one
  flag). Drive it from the reshaped wire `{flagBit,value}`: `flag = (CampaignPermission)(1<<flagBit)`.

### 3.3 `SerializePermissionUpdate`/`HandlePermissionUpdate`
- `MessageSerializer.SerializePermissionUpdate/DeserializePermissionUpdate` — new signatures (§2).
- `SessionManager.HandlePermissionUpdate` (SessionManager.cs:195-205): parse `{guid,flagBit,value}`;
  call `PermissionManager.SetPermission((CampaignPermission)(1<<flagBit), value)` for that GUID;
  update the matching `ClientInfo` (looked up by `PlayerGuid`).
- `SessionManager.OnPermissionUpdated` event `Action<ulong,int>` → `Action<Guid,CampaignPermission,bool>`
  (or `Action<Guid,int>` if a caller wants the resulting mask — none currently consume it).

### 3.4 Breaking call-sites (exhaustive — verified via search)
1. `MessageSerializer.SerializePermissionUpdate` (MessageSerializer.cs:82) — signature change.
2. `MessageSerializer.DeserializePermissionUpdate` (MessageSerializer.cs:93) — signature change.
3. `SessionManager.HandlePermissionUpdate` (SessionManager.cs:195-205) — consumes the tuple + keys
   `_clients` by steamId; must key permission by GUID.
4. `SessionManager.OnPermissionUpdated` event decl (SessionManager.cs:20) + its `Invoke` (line 203).
5. `PermissionManager` — entire keying surface (`_assignments`, `PlayerAssignment.SteamId`, all 8
   listed methods) (PermissionManager.cs:13-130).
6. `ActionValidator.ValidateTacticalAction` / `ValidateCampaignAction` (ActionValidator.cs:9,30) —
   take `ulong clientSteamId`; must resolve steamId→playerGUID (via `SessionManager.Clients`) before
   calling `PermissionManager`. **Behavior-change across files → TEAM task.**
7. `FullCommander` value change (1<<7→1<<9): any persisted/serialized old value drifts — none on
   disk yet (no persistence implemented), so no migration needed now.

**NOT broken:** `CampaignPatches.cs` permission patches are stubs (`Check` returns host=true/
client=false, CampaignPatches.cs:10-18) — they don't key by player, so re-keying doesn't touch them.
They become *real* enforcement later (out of foundation scope).

---

## 4. DEAD-ROUTE FIX (#8)

`RouteMessage` switch is in `NetworkEngine.cs` ~lines 245-330. Members with **no `case`** today:

| Member | Byte | Decision | Action |
|---|---|---|---|
| `HostDisconnected` | 0x05 | **WIRE NOW** | add case → `Session.HandleHostDisconnected(msg)` (new: log + raise an `OnHostDisconnected` event for UI session-end). In-scope: lobby/session lifecycle. |
| `PlayerListUpdate` (PEER_LIST) | 0x42 | **WIRE NOW** | add case → `Session.HandlePeerList(msg)` (new). Foundation lobby roster. |
| `SoldierAssignment` (ASSIGN_OWNER) | 0x41 | **WIRE NOW** | add case → `Session.HandleAssignOwner(msg)` (new) → `PermissionManager.AssignSoldier/UnassignSoldier` by GUID. Foundation ownership. |
| `ClientLeave` 0x08 / `PlayerRename` 0x09 | new | **WIRE NOW** | add cases → `Session.HandleLeave` / `HandleRename`. |
| `SaveChunk`/`SaveDone`/`LoadProgress`/`ClientLoaded`/`SessionBegin` | 0x18-0x1C | **WIRE NOW** | add cases → new `SaveTransferCoordinator` handlers (§6). |
| `TacticalActionBroadcast` | 0x24 | **STUB + TODO** | sent by `ApproveTacticalAction` but never received-routed (latent bug). Tactical sync is out of foundation scope → add case with `// TODO(tactical-sync): apply broadcast on clients`. |
| `GameStateDelta` | 0x11 | STUB + TODO | delta-sync phase. |
| `TurnStateUpdate` | 0x27 | STUB + TODO | tactical-turn phase. |
| `PauseRequest`/`PauseAccepted` | 0x16/0x17 | STUB + TODO | pause feature. |
| `TacticalActionResult` | 0x23 | STUB + TODO | not currently emitted; reserved. |
| `CampaignActionResult` | 0x33 | STUB + TODO | host emits via Approved/Rejected today; reserved. |
| `CampaignStateUpdate` | 0x34 | STUB + TODO | geoscape state-sync phase. |
| `TransportInternal` | 0xF0 | **LEAVE** | consumed inside transport layer, never reaches RouteMessage. |

> "STUB + TODO" = add an empty `case X: break;` (or grouped) with a `// TODO(<phase>)` comment so the
> member is no longer a silent fall-through and the intent is recorded. Do not implement the feature.

---

## 5. PLAYERGUID FLOW (#7)

- **Generation (client, first run):** on mod init, read `playerGUID` from the mod config file; if
  absent, `Guid.NewGuid()` and persist. New helper `ClientIdentity` (small static, e.g. under
  `src/Network/` or `src/`). Generated **once**, never per-session.
- **Storage / persistence:** a mod config JSON in an update-safe per-user dir. **Spec 02 §4 + open-
  questions:** the exact PP per-user config dir that survives a mod update is an **OPEN SDK QUESTION**
  (`docs/specs/03-open-questions-sdk.md` → "Persistent Config Location"). **Interim grounded default:**
  `Application.persistentDataPath` (Unity, always available) → `<persistentDataPath>/Multipleer/identity.json`
  holding `{ playerGUID }`. Host's permission table → `<...>/Multipleer/coop-perms.json`
  (`[{ playerGUID, lastNickname, flags }]`). Flag this default for confirmation against the real PP
  mod-config dir before release.
- **Send (JOIN):** client puts `playerGUID` (+ nickname) in the reshaped `ConnectionRequest` payload
  (§2 JOIN). Sent over ANY transport — GUID rides in payload, transport-agnostic (spec 02 §4).
- **Map on host:** `ClientInfo` gains `Guid PlayerGuid`. `SessionManager.HandleConnectionRequest`
  (SessionManager.cs:97-106) parses JOIN → `AddClient(peerSteamId, endpoint)` then sets
  `client.PlayerGuid = join.PlayerGuid; client.PlayerName = join.Nickname`. The host holds the
  **peerID(steamId) ↔ playerGUID** bridge in `ClientInfo`.
- **Replaces SteamId as the key:** ownership (`soldierID→playerGUID`) and permissions
  (`playerGUID→flags`) key by GUID (§3). In-session routing/sends still use `peerSteamId` (the
  transport handle); GUID is resolved via `SessionManager.Clients` when crossing into permission/
  ownership logic (e.g. `ActionValidator` resolves sender steamId→GUID).
- **Reconciliation on session start (spec 02 §4):** host loads `coop-perms.json`; for each connected
  peer match by `playerGUID` (restore flags, refresh nickname) → else by nickname (restore + rebind
  GUID) → else default/none. Absent entries are kept (offline), never reset. **Phase B** (needs the
  Player-Management UI; foundation lands the data path + file format only).

### ClientInfo additions (`SessionManager.ClientInfo`, SessionManager.cs:208-216)
```csharp
public Guid PlayerGuid { get; set; }   // persistent identity (JOIN); permission/ownership key
public bool IsReady    { get; set; }   // mirror of _readyClients for PEER_LIST broadcast
```
(`PlayerName` already exists; `SteamId` stays = the per-session peerID.)

---

## 6. SAVE-TRANSFER + BARRIER (#1)

### 6.1 Real game save API — **VERDICT: FOUND** (decompile cited)

All in `Assembly-CSharp\src`, type `PhoenixPoint.Common.Saves.PhoenixSaveManager` and
`Base.Serialization.SerializationComponent` (`PhoenixSerializationComponent`).

**Host: savegame → byte blob.** Two grounded options:
- **(A) Read existing save file to bytes (RECOMMENDED, lowest risk):**
  `SerializationComponent.ReadSavegameBinary(SavegameMetaData metaData, ByRef<byte[]> result)`
  — `SerializationComponent.cs:281-287`, impl `ReadSavegameBinaryCrt`
  (`SerializationComponent.cs:367-376`) does `inStream.CopyTo(memoryStream)` → returns the raw
  on-disk `.zsav` (already gzip+binary — extensions `{.zsav,.zjsav,.sav,.jsav}`,
  `SerializationComponent.cs:48`). Flow: host presses Play → native save-picker → host either picks
  an existing save or first writes one (`PhoenixSaveManager.SaveWithName` /
  `SaveGame(PPSavegameMetaData, ext, …)`, PhoenixSaveManager.cs:550 / 191) then
  `ReadSavegameBinary(metaData)` → blob. **No re-compress needed** (already gzip; spec 02 §2 verified).
- **(B) Serialize straight to a `MemoryStream`:** `Level.WriteSavegame(metaData, ext, additionalContent,
  IEnumerable<Stream> additionalWriteStreams, ByRef<bool>)` (`Base.Levels/Level.cs:176` →
  `SerializationComponent.WriteSavegame`, `SerializationComponent.cs:231`) writes through a
  `MultiWriteStream` over the supplied streams. The vanilla code already captures geoscape bytes
  this exact way (`PhoenixSaveManager.SaveGame`, PhoenixSaveManager.cs:210-228:
  `new MemoryStream(...)` as an additional write stream → `byte[]`). Use if you want to avoid touching
  disk. **(A) is preferred** for v1 — fewer moving parts, reuses the picker output verbatim.

**Client: byte blob → loaded session.** Two grounded options:
- **(A) Blob → temp save file → vanilla load (RECOMMENDED):** write received bytes to a `.zsav` via
  the platform record store, run `PhoenixSaveManager.InitSaves()` (PhoenixSaveManager.cs:317) /
  `GetSaveGame(name, ByRef<SavegameMetaData>)` (PhoenixSaveManager.cs:173) to obtain its
  `PPSavegameMetaData`, then `PhoenixSaveManager.LoadGame(PPSavegameMetaData)`
  (PhoenixSaveManager.cs:609) → `_game.FinishLevelAndLoadGame(metaData)`
  (PhoenixSaveManager.cs:614→620; `PhoenixGame.FinishLevelAndLoadGame`, PhoenixGame.cs:269 →
  `FinishLevel(new LoadLevelGameResult(gameData.GetLevelSceneBindingToLoad()))`). Reuses 100% of the
  vanilla load + scene-binding path. **This is the session entry the BEGIN must trigger.**
- **(B) Pure in-memory load (no temp file):** `ReadMetaData(MemoryStream, ext, ByRef<SavegameMetaData>,
  slice)` (`SerializationComponent.cs:442`) to get `LevelScene`; build
  `new LevelSerializedParam(new BinaryDataLevelParamsSource(blob, ext),
  new BinaryDataLevelSerializedDataSource(blob, ext))` (`Base.Levels/BinaryDataLevelSerializedDataSource.cs`
  + `BinaryDataLevelParamsSource.cs`; `BinaryDataLevelSerializedDataSource.ReadSerializedDataAsync`
  calls `SerializationComponent.ReadStreamLevelData(stream, ext, …)`, `SerializationComponent.cs:330`),
  then `LevelScene.CreateSceneBinding(param)` → `PhoenixGame.FinishLevel(new LoadLevelGameResult(binding))`.
  This is exactly the in-memory pattern the game itself uses in
  `PhoenixSaveManager.LoadCurrentGeoscape` (PhoenixSaveManager.cs:380-398). Use only if the temp-file
  route proves problematic.

> Both client paths converge on `PhoenixGame.FinishLevel(LoadLevelGameResult)` — that is the single
> "start the session from a loaded save" entry. The barrier's **BEGIN** must gate the call to
> `FinishLevel`/`FinishLevelAndLoadGame` so all peers enter together.

**Loading-progress hook (phase-2 %):** still **OPEN** — `docs/specs/03-open-questions-sdk.md`
"Loading Progress Hook". Curtain API observed (`GameUtl.GetInGameLoadingCurtain().ShowCurtain/
HideCurtain`, PhoenixSaveManager.cs:199,255) but no exposed 0-1 progress float was located. Foundation
ships download-% (exact, from our own transfer) + a coarse load phase flag; wire the real load-%
when the source is confirmed. **Phase-2 % = BLOCKED-on-API (non-blocking for the barrier).**

### 6.2 Concrete host→client barrier flow (map to files/methods)

New coordinator class `src/Network/SaveTransferCoordinator.cs` (driven from `NetworkEngine`/
`SessionManager`); host side owns the barrier (host = coordinator, spec 02 §2).

| Step | Who | Mechanism | File / method to add or modify |
|---|---|---|---|
| 0. All ready | host | reuse `ClientReady`→`AllClientsReady` (SessionManager.SetClientReady:155-173) | existing |
| 1. Play pressed → save-picker | host | UI hook + `PhoenixSaveManager.SaveWithName`/`LoadGame`-adjacent | `src/UI/MultiplayerUI.cs` (Play button); pick/save via Harmony or direct call |
| 2. Host blob | host | `SerializationComponent.ReadSavegameBinary(metaData, ByRef<byte[]>)` (A) | new `SaveTransferCoordinator.GetSaveBlob()` |
| 3. Chunk + send | host | split blob; `SaveChunk` msgs then `SaveDone` (§2) | `SaveTransferCoordinator.SendBlob(blob)` → `NetworkEngine.SendToClient` |
| 4. Reassemble | client | accumulate `SaveChunk` by `transferId/offset`; on `SaveDone` verify totalBytes/crc | `SaveTransferCoordinator.OnSaveChunk/OnSaveDone` |
| 5. Client load | client | route blob → load API (§6.1 client A) — keep curtain up, **input blocked** | `SaveTransferCoordinator.LoadBlob()` → `PhoenixSaveManager.LoadGame` **but defer `FinishLevel` until BEGIN** |
| 6. Progress | client→host | `LoadProgress` (download exact, load coarse) throttled ~150ms/5% | `SaveTransferCoordinator` + `SessionManager` aggregate/rebroadcast |
| 7. LOADED ack | client→host | send `ClientLoaded{ok}` after load prepared (pre-`FinishLevel`) | `SaveTransferCoordinator.SendLoaded()` |
| 8. Wait all LOADED | host | barrier set; track per-peer; **timeout/kick** after N s (spec 02 §2 required) | `SaveTransferCoordinator` host barrier + reuse `SessionManager.RemoveClient` for kick |
| 9. BEGIN | host→all | broadcast `SessionBegin{serverStartTicks}` | `SaveTransferCoordinator.Begin()` → `NetworkEngine.BroadcastToAll` |
| 10. Enter together | all | on BEGIN → call deferred `FinishLevel(LoadLevelGameResult(binding))` → unblock input | client `SaveTransferCoordinator.OnBegin()`; host triggers its own load at the same instant |

- **Input blocking / loading overlay:** keep `InGameLoadingCurtain` up until BEGIN; ignore input.
  (Curtain shown/hidden via `GameUtl.GetInGameLoadingCurtain()`.)
- **Timeout:** host-side timer in `SaveTransferCoordinator.Update()`; on expiry show "waiting for X" +
  offer kick (`SessionManager.RemoveClient`) / abort. Required, not optional.
- **Chunk size:** ≤ ~512 KB per `SaveChunk` for Steam P2P reliable limit (spec 02 §2). DirectIP/TCP
  could stream whole, but use the same chunked path for uniformity.

---

## 7. TASK BREAKDOWN (ordered; Phase A = wire/data, Phase B = behavior/UI)

> Lead serializes file-conflicting tasks. **File-conflict hotspots:** `PacketType.cs` (T1),
> `MessageSerializer.cs` (T2,T3,T4), `NetworkEngine.cs` RouteMessage (T5,T6,T9), `SessionManager.cs`
> (T4,T6,T7,T9), `PermissionManager.cs` (T3). Do not run two tasks that edit the same file in parallel.

### Phase A — wire & data contract (no game-behavior change)
- **T1 — PacketType members** (#6/#8). File: `PacketType.cs`. Add `ClientLeave 0x08`,
  `PlayerRename 0x09`, `SaveChunk 0x18`, `SaveDone 0x19`, `LoadProgress 0x1A`, `ClientLoaded 0x1B`,
  `SessionBegin 0x1C`. SINGLE task. No deps.
- **T2 — New serializers + DTOs** (#6). File: `MessageSerializer.cs`. Implement JOIN, PEER_LIST,
  LEAVE, RENAME, SAVE_CHUNK, SAVE_DONE, PROGRESS, LOADED, BEGIN, ASSIGN_OWNER per §2 (+ DTO classes).
  Depends on T1 for any enum refs (none strictly). SINGLE task.
- **T3 — CampaignPermission 9-flag + GUID re-key** (#2/#7). Files: `PermissionManager.cs` (+ reshaped
  `SerializePermissionUpdate`/`DeserializePermissionUpdate` in `MessageSerializer.cs`). Enum fix +
  re-key dict/`PlayerAssignment` to `Guid`. **TEAM** (Coder+Reviewer): touches enum semantics
  consumed across validators. Serialize against T2 (same `MessageSerializer.cs` file). 
- **T4 — ClientInfo.PlayerGuid + JOIN handling + PERMISSION re-key handler** (#7). File:
  `SessionManager.cs`. Add `PlayerGuid`/`IsReady` to `ClientInfo`; reshape `HandleConnectionRequest`
  to parse JOIN; rewrite `HandlePermissionUpdate` for `{guid,flagBit,value}`; change
  `OnPermissionUpdated` signature. Depends on T2 (DTOs) + T3 (PermissionManager Guid API). SINGLE.

### Phase B — routing & behavior
- **T5 — Dead-route fixes** (#8). File: `NetworkEngine.cs` (RouteMessage). Add WIRE-NOW cases
  (HostDisconnected, PlayerListUpdate, SoldierAssignment, ClientLeave, PlayerRename, save-transfer
  msgs) + STUB+TODO cases (TacticalActionBroadcast, GameStateDelta, TurnStateUpdate, Pause*,
  TacticalActionResult, CampaignActionResult, CampaignStateUpdate). Depends on T1; coordinates with
  T6 handler names. SINGLE.
- **T6 — Lobby handlers** (#6/#7). File: `SessionManager.cs` (+ small `NetworkEngine` send helpers).
  `HandlePeerList`, `HandleAssignOwner`, `HandleLeave`, `HandleRename`, `HandleHostDisconnected`;
  PEER_LIST broadcast builder. Depends on T2,T4. Serialize with T4 (same file). SINGLE/ TEAM.
- **T7 — ClientIdentity (playerGUID gen/persist)** (#7). New file `src/Network/ClientIdentity.cs` +
  call from `MultipleerMain`. JSON read/write to `<persistentDataPath>/Multipleer/identity.json`.
  Independent file → parallel-safe. SINGLE. **Note: persistent-dir choice = OPEN SDK Q (interim
  default documented §5); not blocking.**
- **T8 — ActionValidator steamId→GUID resolution** (#7). File: `src/Validation/ActionValidator.cs`.
  Resolve sender peerID→`PlayerGuid` via `SessionManager.Clients` before `PermissionManager` calls.
  Depends on T3,T4. SINGLE.

### Phase B (cont.) — save transfer + barrier
- **T9 — SaveTransferCoordinator + barrier** (#1). New file `src/Network/SaveTransferCoordinator.cs`
  + RouteMessage cases (T5) + `SessionManager` barrier hooks + `MultiplayerUI` Play hook. Implements
  §6.2 steps 2-10 against the FOUND save API (§6.1). **TEAM** (Coder+Reviewer): multi-file,
  lifecycle-critical. Depends on T1,T2,T5.
  - **Sub-item BLOCKED-on-API (non-blocking):** phase-2 load-% hook — ship coarse phase flag now;
    revisit when loading-progress source confirmed (spec 03 open Q).

### Phase B — UI (later; foundation lands data path only)
- **T10 — Player-Management permission UI + coop-perms.json reconciliation** (#7, spec 02 §4).
  Files: new UI + `SessionManager` reconciliation. Depends on T3,T4,T6. Deferred past the wire
  foundation; listed for ordering. May be split out of "foundation" if scoping tight.

### Blocked / open items (carry to lead)
- **Persistent config dir** that survives a mod update — OPEN SDK Q; interim `persistentDataPath`
  default chosen (§5). Not blocking T7.
- **Phase-2 loading-% source** — OPEN SDK Q; barrier works without it (T9 ships coarse %).
- All other #1/#6/#7/#8 items are **fully grounded** — no API guesswork remains.
