using System.Collections.Generic;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Admin Sync", "PenguinzTech", "1.0.1")]
    [Description("Grants overlay plugin permissions to server owners and moderators on boot and connect.")]
    public class AdminSync : RustPlugin
    {
        // All permissions that admin/mod players should receive on this overlay server.
        // Add or remove entries here when new overlay plugins are added.
        private static readonly string[] AdminPermissions =
        {
            "adminmenu.use",
            "adminpanel.use",
            "clansui.use",
            "ubertool.use",
            "spawncontrol.use",
            "spotsystem.use",
            "achievementsystem.admin",
            "hitmarkers.use",
            "statistics.admin",
            "lockmeup.admin",
            "raidablebases.allow",
            "raidablebases.admin",
            "raidablebases.ddraw",
            "shop.admin",
            "xdshop.admin",
            "bossmonster.admin",
            "iqcases.admin",
            "iqchat.admin",
            "iqpermissions.admin",
            "picklock.admin",
            "sputnik.admin",
            "storebobery.admin",
            "abandonedbases.allow",
            "abandonedbases.admin",
            "armoredtrain.admin",
            "cargotrainevent.admin",
            "convoy.admin",
            "iqalcoholfarm.admin",
            "jetpack.admin",
            "wipeschedule.admin",
        };

        private void OnServerInitialized()
        {
            SyncAll();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            var serverUser = ServerUsers.Get(player.userID);
            if (serverUser != null && (serverUser.group == ServerUsers.UserGroup.Owner || serverUser.group == ServerUsers.UserGroup.Moderator))
                GrantPermissions(player.UserIDString);
        }

        private void SyncAll()
        {
            var synced = 0;
            foreach (var entry in ServerUsers.GetAll(ServerUsers.UserGroup.Owner))
            {
                GrantPermissions(entry.steamid.ToString());
                synced++;
            }
            foreach (var entry in ServerUsers.GetAll(ServerUsers.UserGroup.Moderator))
            {
                GrantPermissions(entry.steamid.ToString());
                synced++;
            }
            Puts($"AdminSync: granted overlay permissions to {synced} owner/moderator account(s).");
        }

        private void GrantPermissions(string userId)
        {
            foreach (var perm in AdminPermissions)
            {
                if (!permission.PermissionExists(perm))
                    continue;
                if (!permission.UserHasPermission(userId, perm))
                    permission.GrantUserPermission(userId, perm, null);
            }
        }
    }
}
