# Acknowledgements

This image bundles and, in some cases, patches third-party Oxide plugins. All patches are limited to fixing broken Oxide API calls caused by upstream Rust/Oxide updates. No gameplay logic, configuration defaults, or author-identifying metadata have been altered.

Patches are clearly marked at the top of each modified file:

```
// PATCHED by penguin-rust-base: <description of change>
```

---

## Patched Plugins

The following plugins are shipped in a modified form under `oxide/plugins/patched/`. The original source and all authorship information are preserved inside the file.

| Plugin | Author(s) | Version Patched | Original Source | Patch Applied |
|---|---|---|---|---|
| **Anti Offline Raid** | Calytic / Shady14u | 1.0.3 | [umod.org/plugins/anti-offline-raid](https://umod.org/plugins/anti-offline-raid) | Replaced removed Oxide APIs: `FindByID→FindAwakeOrSleeping`, `net.connection→Connection`, `BuildingPrivlidge→BuildingPrivilege` |
| **Better Chat** | LaserHydra | 5.2.15 | [umod.org/plugins/better-chat](https://umod.org/plugins/better-chat) | Replaced removed Oxide APIs: `FindByID→FindAwakeOrSleeping`, `net.connection→Connection`, `ConVar.Chat.ChatChannel→Chat.ChatChannel` |
| **Better Chat Mute** | LaserHydra | 1.2.1 | [umod.org/plugins/better-chat-mute](https://umod.org/plugins/better-chat-mute) | Replaced removed Oxide APIs: `FindByID→FindAwakeOrSleeping`, `net.connection→Connection`, `ConVar.Chat.ChatChannel→Chat.ChatChannel` |
| **Dynamic PVP** | HunterZ / CatMeat / Arainrr | 5.0.2 | [umod.org/plugins/dynamic-pvp](https://umod.org/plugins/dynamic-pvp) | Replaced removed Oxide APIs: `FindByID→FindAwakeOrSleeping`, `net.connection→Connection` |
| **NTeleportation** | nivex | 1.9.4 | [umod.org/plugins/nteleportation](https://umod.org/plugins/nteleportation) | Replaced removed Oxide APIs: `FindByID→FindAwakeOrSleeping`, `net.connection→Connection`, `BuildingPrivlidge→BuildingPrivilege` |
| **Player Administration** | ThibmoRozier / rfc1920 / Mheetu / Pho3niX90 | 1.6.9 | [umod.org/plugins/player-administration](https://umod.org/plugins/player-administration) | Replaced removed Oxide APIs: `FindByID→FindAwakeOrSleeping`, `net.connection→Connection` |
| **Quests** | Gonzi | 2.4.4 | [umod.org/plugins/quests](https://umod.org/plugins/quests) | Replaced removed Oxide API: `FindByID→FindAwakeOrSleeping` |
| **Tree Planter** | Bazz3l | 1.2.9 | [umod.org/plugins/tree-planter](https://umod.org/plugins/tree-planter) | Replaced removed Oxide APIs |
| **Vehicle Licence** | Sorrow / TheDoc / Arainrr | 1.8.9 | [umod.org/plugins/vehicle-licence](https://umod.org/plugins/vehicle-licence) | Removed Oxide API fixes (see file header) |
| **Zone Manager** | k1lly0u | 3.1.10 | [umod.org/plugins/zone-manager](https://umod.org/plugins/zone-manager) | Removed Oxide API fixes (see file header) |

---

## Baked-in Plugins (Unmodified)

These plugins are baked into the image as-is from their original published releases. No source changes have been made.

| Plugin | Author(s) | Source |
|---|---|---|
| AdminUtilities | WhiteThunder | [umod.org/plugins/admin-utilities](https://umod.org/plugins/admin-utilities) |
| BGrade | Wulf | [umod.org/plugins/bgrade](https://umod.org/plugins/bgrade) |
| CopyPaste | misticos | [umod.org/plugins/copy-paste](https://umod.org/plugins/copy-paste) |
| NightLantern | FastBurst | [umod.org/plugins/night-lantern](https://umod.org/plugins/night-lantern) |
| RemoverTool | Reneb / nivex | [umod.org/plugins/remover-tool](https://umod.org/plugins/remover-tool) |
| StackSizeController | AnExiledGod | [umod.org/plugins/stack-size-controller](https://umod.org/plugins/stack-size-controller) |
| TruePVE | nivex | [umod.org/plugins/true-pve](https://umod.org/plugins/true-pve) |
| UnburnableMeat | Wulf | [umod.org/plugins/unburnable-meat](https://umod.org/plugins/unburnable-meat) |
| Vanish | Wulf | [umod.org/plugins/vanish](https://umod.org/plugins/vanish) |
| VehicleDecayProtection | WhiteThunder | [umod.org/plugins/vehicle-decay-protection](https://umod.org/plugins/vehicle-decay-protection) |
| Whitelist | Wulf | [umod.org/plugins/whitelist](https://umod.org/plugins/whitelist) |

---

## PenguinzTech-Authored Plugins

| Plugin | Description |
|---|---|
| **AutoAdmin** | Provisions Oxide permissions and admin groups from environment variables on startup |
| **PluginManager** | Runtime add/remove/update/list for Oxide plugins via console and chat |

---

## Licenses

All third-party plugins listed above are licensed by their respective authors under the terms published on umod.org. This image redistributes them solely to provide a pre-configured server experience. Refer to each plugin's umod.org page for the applicable license terms.

The image itself (excluding bundled plugins) is licensed under the MIT License — see [LICENSE](../LICENSE).
