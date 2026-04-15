using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("PluginManager", "PenguinzTech", "1.0.0")]
    [Description("Runtime plugin add/remove/update/list via console and chat")]
    class PluginManager : RustPlugin
    {
        private const string AdminPerm = "pluginmanager.admin";
        private static readonly string PluginsDir = "/steamcmd/rust/oxide/plugins";

        void Init() => permission.RegisterPermission(AdminPerm, this);

        // ── Console commands ──────────────────────────────────────────────────

        [ConsoleCommand("plugin.add")]
        void CcAdd(ConsoleSystem.Arg a) => RunMutating(a, "add");

        [ConsoleCommand("plugin.remove")]
        void CcRemove(ConsoleSystem.Arg a) => RunMutating(a, "remove");

        [ConsoleCommand("plugin.update")]
        void CcUpdate(ConsoleSystem.Arg a) => RunMutating(a, "update");

        [ConsoleCommand("plugin.list")]
        void CcList(ConsoleSystem.Arg a)
        {
            if (!CheckPerm(a)) return;
            a.ReplyWith(BuildList());
        }

        // ── Chat command: /plugin <sub> [name] [source] ───────────────────────

        [ChatCommand("plugin")]
        void ChatPlugin(BasePlayer p, string _, string[] args)
        {
            if (!permission.UserHasPermission(p.UserIDString, AdminPerm))
            {
                SendReply(p, "You don't have permission to use this command.");
                return;
            }

            if (args.Length == 0)
            {
                SendReply(p, "Usage: /plugin <add|remove|update|list> [name] [source]");
                return;
            }

            var sub = args[0].ToLower();
            if (sub == "list")
            {
                SendReply(p, BuildList());
                return;
            }

            if (args.Length < 2)
            {
                SendReply(p, "Usage: /plugin " + sub + " <name> [source]");
                return;
            }

            var name   = args[1];
            var source = args.Length > 2 ? args[2] : "";
            if (!IsValidName(name))
            {
                SendReply(p, "Invalid plugin name.");
                return;
            }

            Fork(sub, name, source);
            SendReply(p, $"[PluginManager] {sub} job started for {name}. Watch console for result.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        void RunMutating(ConsoleSystem.Arg a, string action)
        {
            if (!CheckPerm(a)) return;
            var name   = a.GetString(0, "");
            var source = a.GetString(1, "");
            if (string.IsNullOrEmpty(name))
            {
                a.ReplyWith("Usage: plugin." + action + " <name> [source]");
                return;
            }
            if (!IsValidName(name))
            {
                a.ReplyWith("Invalid plugin name.");
                return;
            }
            Fork(action, name, source);
            a.ReplyWith($"[PluginManager] {action} job started for {name}.");
        }

        bool CheckPerm(ConsoleSystem.Arg a)
        {
            // RCON and server console have no Connection — always allow
            if (a.Connection == null) return true;
            var p = a.Player();
            if (p != null && permission.UserHasPermission(p.UserIDString, AdminPerm)) return true;
            a.ReplyWith("No permission.");
            return false;
        }

        static bool IsValidName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length > 64) return false;
            foreach (var c in n)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
            return true;
        }

        void Fork(string action, string name, string source)
        {
            var src = string.IsNullOrEmpty(source) ? "" : $" '{source}'";
            var psi = new ProcessStartInfo(
                "/bin/bash",
                $"-c \"/usr/local/bin/manage-plugin.sh '{action}' '{name}'{src} >>/tmp/pluginmgr.log 2>&1 &\"")
            {
                UseShellExecute  = false,
                CreateNoWindow   = true
            };
            Process.Start(psi);
        }

        string BuildList()
        {
            var sb = new StringBuilder("=== Enabled ===\n");
            foreach (var f in Directory.GetFiles(PluginsDir, "*.cs"))
                sb.AppendLine("  + " + Path.GetFileNameWithoutExtension(f));

            var patched = Path.Combine(PluginsDir, "patched");
            if (Directory.Exists(patched))
            {
                sb.AppendLine("=== Available (patched) ===");
                foreach (var f in Directory.GetFiles(patched, "*.cs"))
                    sb.AppendLine("  ~ " + Path.GetFileNameWithoutExtension(f));
            }

            var dis = Path.Combine(PluginsDir, "disabled");
            if (Directory.Exists(dis))
            {
                sb.AppendLine("=== Available (disabled) ===");
                foreach (var f in Directory.GetFiles(dis, "*.cs.gz"))
                    sb.AppendLine("  - " + Path.GetFileNameWithoutExtension(
                                               Path.GetFileNameWithoutExtension(f)));
            }
            return sb.ToString();
        }
    }
}
