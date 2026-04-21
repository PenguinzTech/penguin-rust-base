# Oxide API Quick Reference

Current as of Oxide.Rust 2.x / Rust 2026.

---

## Hook Signatures (Current)

### Player hooks
```csharp
void OnPlayerConnected(BasePlayer player)
void OnPlayerDisconnected(BasePlayer player, string reason)
void OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
void OnPlayerDeath(BasePlayer player, HitInfo info)
void OnPlayerRespawned(BasePlayer player)
void OnUserPermissionGranted(string id, string perm)
void OnGroupPermissionGranted(string name, string perm)
```

### Entity hooks
```csharp
void OnEntitySpawned(BaseNetworkable entity)
void OnEntityKill(BaseNetworkable entity)
object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)  // return non-null to cancel
```

### Building hooks
```csharp
void OnEntityBuilt(Planner planner, GameObject go)
object CanBuild(Planner planner, Construction prefab, Construction.Target target)
void OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
```

### Item hooks
```csharp
void OnItemCraftFinished(ItemCraftTask task, Item item)
object OnItemPickup(Item item, BasePlayer player)
void OnItemDropped(Item item, BaseEntity entity)
```

### Server hooks
```csharp
void OnServerInitialized()
void OnServerSave()
void OnNewSave(string filename)  // wipe detection
```

---

## Player Lookup

```csharp
// By ulong userID
BasePlayer p = BasePlayer.FindAwakeOrSleeping(userID.ToString());

// By string UserIDString
BasePlayer p = BasePlayer.FindAwakeOrSleeping(userIDString);

// Iterate active players only
foreach (var p in BasePlayer.activePlayerList) { }

// Iterate sleeping players only
foreach (var p in BasePlayer.sleepingPlayerList) { }
```

---

## Permissions

```csharp
// Register in Init()
permission.RegisterPermission("myplugin.use", this);

// Check
bool ok = permission.UserHasPermission(player.UserIDString, "myplugin.use");

// Grant / Revoke
permission.GrantUserPermission(player.UserIDString, "myplugin.use", this);
permission.RevokeUserPermission(player.UserIDString, "myplugin.use");

// Groups
permission.AddUserGroup(player.UserIDString, "vip");
bool inGroup = permission.UserHasGroup(player.UserIDString, "vip");
```

---

## Timers

```csharp
Timer t1 = timer.Once(5f, () => Puts("fired once after 5s"));
Timer t2 = timer.Every(10f, () => Puts("fires every 10s"));
Timer t3 = timer.Repeat(2f, 5, () => Puts("fires 5 times, 2s apart"));

// Always destroy on Unload
t1?.Destroy();
```

---

## Config

```csharp
class Configuration
{
    [JsonProperty("Some Setting")]
    public int SomeSetting = 42;
    public static Configuration DefaultConfig() => new Configuration();
}
static Configuration _cfg;

protected override void LoadConfig()
{
    base.LoadConfig();
    try { _cfg = Config.ReadObject<Configuration>() ?? new Configuration(); }
    catch { _cfg = new Configuration(); }
    SaveConfig();
}
protected override void SaveConfig() => Config.WriteObject(_cfg);
```

---

## Data Persistence (small/per-wipe)

```csharp
// Save
Interface.Oxide.DataFileSystem.WriteObject("MyPlugin/data", myData);

// Load
var data = Interface.Oxide.DataFileSystem.ReadObject<MyDataType>("MyPlugin/data")
           ?? new MyDataType();
```

---

## Cross-plugin calls

```csharp
[PluginReference] Plugin Economics;

// Call returns object — check for null
var balance = (double?)Economics?.Call("Balance", player.UserIDString) ?? 0;
Economics?.Call("Deposit", player.UserIDString, 100.0);
```

---

## Chat / Messaging

```csharp
// To a single player
player.ChatMessage("Hello!");
SendReply(player, "Hello!");  // from within RustPlugin

// Server broadcast
Server.Broadcast("Server-wide message");

// Console reply (from ConsoleCommand handler)
a.ReplyWith("Result: done");

// Puts to server console
Puts("[MyPlugin] Something happened");
PrintWarning("Something odd");
PrintError("Something broke");
```

---

## Item Helpers

```csharp
ItemDefinition def = ItemManager.FindItemDefinition("wood");
Item item = ItemManager.CreateByName("wood", 1000);
player.GiveItem(item);

int amount = player.inventory.GetAmount(def.itemid);
player.inventory.Take(null, def.itemid, 100);
```

---

## Position / World

```csharp
Vector3 pos = player.transform.position;
float groundY = TerrainMeta.HeightMap.GetHeight(pos);

// Grid reference (e.g. "D5")
string grid = PhoneController.PositionToGridCoord(pos);
```

---

## Deprecated APIs (still compile, will be removed)

Avoid these even if they currently work — they are flagged for removal.

| Deprecated | Preferred replacement |
|---|---|
| `player.displayName` | `player.displayName` is fine; but `IPlayer.Name` preferred for Covalence code |
| `ConsoleSystem.Arg.GetString(int)` without bounds check | Always guard with `a.Args?.Length > n` |
| `ConVar`-prefixed chat enums (most moved to `Chat.*`) | Use `Chat.ChatChannel` not `ConVar.Chat.ChatChannel` |
| Direct `net.connection` field access on Arg | Use `.Connection` property |
| `BasePlayer.FindByID(ulong)` | `BasePlayer.FindAwakeOrSleeping(string)` |
| `arg.Player()` extension | `arg.Connection?.player as BasePlayer` |
| `BuildingPrivlidge` (typo spelling) | `BuildingPrivilege` |
| `ServerMgr.Instance.playerEventManager` | Direct hook subscription |
| `RustPlugin.rust` static field | Use `Oxide.Game.Rust.Libraries.Rust` library |
| Synchronous HTTP (`WebClient`) | `webrequest.Enqueue` (async) |
| `Interface.GetMod("Oxide.Core")` | `Interface.Oxide` directly |
