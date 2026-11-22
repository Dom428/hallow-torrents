// Hallow Torrents: which game is running and the speed schedule - shared by the app and the headless host (moved out of App.cs in 1.4).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HallowTorrents
{
    // which game is running: the family's two, your own list, and Hallow Hub's "playing"
    public static class Games
    {
        public static readonly string[] Known = { "DeltaForceClient-Win64-Shipping", "Discovery-d" };
        public static string Running(IEnumerable<string> extra)
        {
            if (TorSettings.Fake) { try { var f = Path.Combine(TorSettings.Dir, "fake-game.txt"); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; } catch { return null; } }
            foreach (var n in Known.Concat(extra ?? new string[0]).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
                var ps = Process.GetProcessesByName(name); var any = ps.Length > 0; foreach (var p in ps) p.Dispose();
                if (any) return name == "DeltaForceClient-Win64-Shipping" ? "Delta Force" : name == "Discovery-d" ? "Discovery" : name;
            }
            var hub = HubPlaying(); return hub.Count > 0 ? hub[0] : null;
        }
        public static List<string> HubPlaying()
        {
            var r = new List<string>();
            try
            {
                var f = Path.Combine(Family.DataDir("Hub"), "status.json"); if (!File.Exists(f)) return r;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f)); object v;
                if (d == null || !d.TryGetValue("running", out v) || !(v is bool) || !(bool)v) return r;
                if (d.TryGetValue("playing", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var x in (System.Collections.IEnumerable)v)
                    {
                        if (x is string) r.Add((string)x);
                        else { var o = x as Dictionary<string, object>; object n; if (o != null && o.TryGetValue("name", out n) && n is string) r.Add((string)n); }
                    }
            }
            catch { }
            return r;
        }
    }

    // speed limits by the time of day (pure, tested)
    public static class Scheduler
    {
        public static ScheduleRule Active(IEnumerable<ScheduleRule> rules, DateTime now)
        {
            foreach (var r in rules ?? new ScheduleRule[0])
            {
                bool weekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
                if (r.Days == "weekdays" && weekend) continue; if (r.Days == "weekends" && !weekend) continue;
                int h = now.Hour; bool inside = r.From == r.To ? true : r.From < r.To ? h >= r.From && h < r.To : h >= r.From || h < r.To;
                if (inside) return r;
            }
            return null;
        }
        public static int Min(params int[] v) { var nz = v.Where(x => x > 0).ToList(); return nz.Count == 0 ? 0 : nz.Min(); }
    }

    // sizes, speeds and times as text (moved out of Dialogs.cs in 1.4: the headless host uses it too)
    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b < 1024) return b + " B"; double v = b / 1024.0; if (v < 1024) return v.ToString("0") + " KB";
            v /= 1024; if (v < 1024) return v.ToString(v < 10 ? "0.0" : "0") + " MB"; v /= 1024; return v.ToString(v < 10 ? "0.00" : "0.0") + " GB";
        }
        public static string Rate(long bps) { return bps <= 0 ? "-" : Bytes(bps) + "/s"; }
        public static string Span(double secs)
        {
            var t = TimeSpan.FromSeconds(secs);
            return t.TotalDays >= 1 ? (int)t.TotalDays + "d " + t.Hours + "h" : t.TotalHours >= 1 ? (int)t.TotalHours + "h " + t.Minutes + "m" : (int)t.TotalMinutes + "m";
        }
    }
}
