// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System;
using System.IO;
using System.Text;

namespace Oxide.Plugins
{
    [Info("ServerPerf", "PenguinzPlays", "1.0.0")]
    [Description("Outputs server CPU/RAM/disk performance history to the F1 console for admins")]
    class ServerPerf : RustPlugin
    {
        const string PermUse = "serverperf.use";

        void Init() => permission.RegisterPermission(PermUse, this);

        // ── server.performance [hours] ────────────────────────────────────────
        // Reads the CSV written by perf-sample.sh and renders a table.
        // Optional arg: number of hours to show (default 24, max 720).
        [ConsoleCommand("server.performance")]
        void CcPerf(ConsoleSystem.Arg a)
        {
            if (!CheckPerm(a)) return;

            int hours = 24;
            if (a.Args?.Length > 0) int.TryParse(a.Args[0], out hours);
            hours = Math.Max(1, Math.Min(hours, 720));

            string csvPath = Path.Combine(
                "/steamcmd/rust/server", ConVar.Server.identity, "perf.csv");

            if (!File.Exists(csvPath))
            {
                a.ReplyWith("[ServerPerf] No performance data recorded yet — perf-sample.sh may not have run.");
                return;
            }

            string[] lines   = File.ReadAllLines(csvPath);
            long     cutoff  = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)hours * 3600L;

            var sb = new StringBuilder();
            sb.AppendLine($"[ServerPerf] Last {hours}h  ({lines.Length} total samples on disk)");
            sb.AppendLine($"{"Time (UTC)",-17} {"CPU",5}  {"RAM Used / Total",-22} {"Disk Used / Total",-22}");
            sb.AppendLine(new string('─', 72));

            int count = 0;
            foreach (string line in lines)
            {
                string[] p = line.Split(',');
                if (p.Length < 7) continue;
                if (!long.TryParse(p[0], out long ts) || ts < cutoff) continue;

                DateTime dt      = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                string   time    = dt.ToString("MM/dd HH:mm");
                string   cpu     = p[1] + "%";
                float    muGb    = long.Parse(p[2]) / 1048576f;
                float    mtGb    = long.Parse(p[3]) / 1048576f;
                float    duGb    = long.Parse(p[4]) / 1048576f;
                float    dtGb    = long.Parse(p[5]) / 1048576f;
                string   ram     = $"{muGb:F1} / {mtGb:F1} GB";
                string   disk    = $"{duGb:F1} / {dtGb:F1} GB ({p[6]}%)";

                sb.AppendLine($"{time,-17} {cpu,5}  {ram,-22} {disk,-22}");
                count++;
            }

            if (count == 0)
            {
                a.ReplyWith($"[ServerPerf] No data in the last {hours}h.");
                return;
            }

            a.ReplyWith(sb.ToString());
        }

        bool CheckPerm(ConsoleSystem.Arg a)
        {
            if (a.Connection == null && !a.IsClientside) return true;
            var p = a.Connection?.player as BasePlayer;
            if (p != null && permission.UserHasPermission(p.UserIDString, PermUse)) return true;
            a.ReplyWith("No permission.");
            return false;
        }
    }
}
