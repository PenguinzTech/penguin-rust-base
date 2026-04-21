// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("ExtensionManager", "PenguinzPlays", "1.0.0")]
    [Description("Runtime plugin add/remove/update/list via console and chat")]
    class ExtensionManager : RustPlugin
    {
        private const string AdminPerm = "extensionmanager.admin";
        private static readonly string PluginsDir = "/steamcmd/rust/oxide/plugins";

        void Init() => permission.RegisterPermission(AdminPerm, this);

        // ── Console commands ──────────────────────────────────────────────────

        [ConsoleCommand("extension.add")]
        void CcAdd(ConsoleSystem.Arg a) => RunMutating(a, "add");

        [ConsoleCommand("extension.remove")]
        void CcRemove(ConsoleSystem.Arg a) => RunMutating(a, "remove");

        [ConsoleCommand("extension.update")]
        void CcUpdate(ConsoleSystem.Arg a) => RunMutating(a, "update");

        [ConsoleCommand("extension.list")]
        void CcList(ConsoleSystem.Arg a)
        {
            if (!CheckPerm(a)) return;
            a.ReplyWith(BuildList());
        }

        // ── Chat command: /extension <sub> [name] [source] ───────────────────

        [ChatCommand("extension")]
        void ChatExtension(BasePlayer p, string _, string[] args)
        {
            if (!permission.UserHasPermission(p.UserIDString, AdminPerm))
            {
                SendReply(p, "You don't have permission to use this command.");
                return;
            }

            if (args.Length == 0)
            {
                SendReply(p, "Usage: /extension <add|remove|update|list> [name] [source]");
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
                SendReply(p, "Usage: /extension " + sub + " <name> [source]");
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
            SendReply(p, $"[ExtensionManager] {sub} job started for {name}. Watch console for result.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        void RunMutating(ConsoleSystem.Arg a, string action)
        {
            if (!CheckPerm(a)) return;
            var name   = a.GetString(0, "");
            var source = a.GetString(1, "");
            if (string.IsNullOrEmpty(name))
            {
                a.ReplyWith("Usage: extension." + action + " <name> [source]");
                return;
            }
            if (!IsValidName(name))
            {
                a.ReplyWith("Invalid plugin name.");
                return;
            }
            Fork(action, name, source);
            a.ReplyWith($"[ExtensionManager] {action} job started for {name}.");
        }

        bool CheckPerm(ConsoleSystem.Arg a)
        {
            // Allow only true server console (no connection, not from a player invoke)
            if (a.Connection == null && !a.IsClientside) return true;
            var p = a.Connection?.player as BasePlayer;
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
            // Args passed directly via ArgumentList — no shell interpolation, no injection surface.
            var psi = new ProcessStartInfo("/usr/local/bin/manage-plugin.sh")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            psi.ArgumentList.Add(action);
            psi.ArgumentList.Add(name);
            if (!string.IsNullOrEmpty(source))
                psi.ArgumentList.Add(source);
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
