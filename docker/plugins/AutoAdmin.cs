using System;
using System.Collections.Generic;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("AutoAdmin", "PenguinzTech", "2.0.0")]
    [Description("Provisions admin group with per-plugin permissions from RUST_ADMIN_STEAMIDS env var on every boot")]
    class AutoAdmin : RustPlugin
    {
        private const string AdminGroup = "admin";

        // Plugins with a fixed, well-known permission set.
        // Grant exactly these — no more, no less.
        private static readonly Dictionary<string, string[]> _explicit =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["AdminUtilities"] = new[]
            {
                "adminutilities.disconnectteleport", "adminutilities.saveinventory",
                "adminutilities.maxhht",             "adminutilities.nocliptoggle",
                "adminutilities.godmodetoggle",      "adminutilities.kick",
                "adminutilities.ban",                "adminutilities.banip",
                "adminutilities.unban",              "adminutilities.banlist",
                "adminutilities.preventkick",        "adminutilities.preventban",
                "adminutilities.preventbanip",       "adminutilities.give",
                "adminutilities.giveto",             "adminutilities.giveall",
                "adminutilities.spawn",              "adminutilities.spawnto",
                "adminutilities.spawnall",           "adminutilities.despawn",
                "adminutilities.bypassblacklist",    "adminutilities.bypassplayerconsolecommands",
                "adminutilities.bypasschatcommands",
            },
            ["BGrade"] = new[]
            {
                "bgrade.1", "bgrade.2", "bgrade.3", "bgrade.4", "bgrade.nores", "bgrade.all",
            },
            ["CopyPaste"] = new[]
            {
                "copypaste.copy", "copypaste.list", "copypaste.paste",
                "copypaste.pasteback", "copypaste.undo",
            },
            // vanish.permanent is intentionally excluded — it auto-applies invisibility/freeze
            // on every connect, which is disruptive even for admins.
            ["Vanish"] = new[]
            {
                "vanish.allow", "vanish.unlock", "vanish.damage",
                "vanish.invviewer", "vanish.teleport",
            },
            ["TruePVE"] = new[] { "truepve.canmap" },
            ["CustomEntities"] = new[] { "custententities.admin" },
            ["SignArtist"] = new[]
            {
                "signartist.file",        "signartist.ignorecd",  "signartist.ignoreowner",
                "signartist.raw",         "signartist.restore",   "signartist.restoreall",
                "signartist.text",        "signartist.url",
            },
            ["GridPower"] = new[]
            {
                "gridpower.admin",
                "gridpower.vip1", "gridpower.vip2", "gridpower.vip3",
                "gridpower.vip4", "gridpower.vip5",
            },
            ["WaterBases"] = new[]
            {
                "waterbases.admin",
                "waterbases.vip1", "waterbases.vip2", "waterbases.vip3",
                "waterbases.vip4", "waterbases.vip5",
            },
            ["Whitelist"] = new[] { "whitelist.allow" },
        };

        // Plugins whose permissions are generated at runtime from config files
        // (e.g. biplane presets, XDQuest quests, vehicle types, kit names).
        // Grant all permissions whose name starts with this prefix.
        private static readonly Dictionary<string, string> _prefix =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RemoverTool"]            = "removertool.",
            ["VehicleDecayProtection"] = "vehicledecayprotection.",
            ["NightLantern"]           = "nightlantern.",
            ["StackSizeController"]    = "stacksizecontroller.",
            ["Biplane"]                = "biplane.",
            ["Kits"]                   = "kits.",
            ["PersonalAnimal"]         = "personalanimal.",
            ["XDQuest"]                = "xdquest.",
            ["DeployableZipline"]      = "deployablezipline.",
        };

        void OnServerInitialized()
        {
            var steamIds = Environment.GetEnvironmentVariable("RUST_ADMIN_STEAMIDS");
            if (string.IsNullOrEmpty(steamIds))
            {
                Puts("RUST_ADMIN_STEAMIDS not set — skipping auto-provisioning");
                return;
            }

            EnsureAdminGroup();
            GrantAllPluginPermissions();
            ProvisionAdmins(steamIds);
        }

        // Fires for each plugin as it loads — handles hot-reload and
        // ensures dynamic permissions (registered during plugin Init) are captured.
        void OnPluginLoaded(Plugin plugin)
        {
            var steamIds = Environment.GetEnvironmentVariable("RUST_ADMIN_STEAMIDS");
            if (string.IsNullOrEmpty(steamIds)) return;

            EnsureAdminGroup();
            GrantPluginPermissions(plugin.Name);
        }

        private void EnsureAdminGroup()
        {
            if (!permission.GroupExists(AdminGroup))
                permission.CreateGroup(AdminGroup, "Admin", 0);
        }

        private void GrantAllPluginPermissions()
        {
            foreach (var pluginName in _explicit.Keys)
                GrantPluginPermissions(pluginName);
            foreach (var pluginName in _prefix.Keys)
                GrantPluginPermissions(pluginName);
        }

        private void GrantPluginPermissions(string pluginName)
        {
            string[] explicitPerms;
            if (_explicit.TryGetValue(pluginName, out explicitPerms))
            {
                var granted = new System.Text.StringBuilder();
                foreach (var perm in explicitPerms)
                {
                    if (!permission.PermissionExists(perm)) continue;
                    permission.GrantGroupPermission(AdminGroup, perm, null);
                    granted.Append(perm).Append(' ');
                }
                if (granted.Length > 0)
                    Puts($"[AutoAdmin] {pluginName}: {granted.ToString().TrimEnd()}");
                return;
            }

            string prefix;
            if (_prefix.TryGetValue(pluginName, out prefix))
            {
                var all = permission.GetPermissions();
                var granted = new System.Text.StringBuilder();
                foreach (var perm in all)
                {
                    if (!perm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    permission.GrantGroupPermission(AdminGroup, perm, null);
                    granted.Append(perm).Append(' ');
                }
                if (granted.Length > 0)
                    Puts($"[AutoAdmin] {pluginName}: {granted.ToString().TrimEnd()}");
            }
        }

        private void ProvisionAdmins(string steamIds)
        {
            foreach (var rawId in steamIds.Split(','))
            {
                var steamId = rawId.Trim();
                if (string.IsNullOrEmpty(steamId)) continue;

                permission.AddUserGroup(steamId, AdminGroup);
                Puts($"[AutoAdmin] {steamId} → group:{AdminGroup}");
            }
        }
    }
}
