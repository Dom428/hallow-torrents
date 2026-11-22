// Hallow Torrents - safety: Microsoft Defender's own scanner (MpCmdRun, a custom scan of the finished download, Idle priority,
// nothing removed by us), and the per-user file / link handler registration (HKCU only, only after you say yes).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace HallowTorrents
{
    public static class DefenderScan
    {
        public static string Find()
        {
            if (TorSettings.TestDir != null) { var t = Environment.GetEnvironmentVariable("HALLOW_TORRENTS_MPCMDRUN"); return string.IsNullOrEmpty(t) ? null : t; }
            try
            {
                var plat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(plat))
                {
                    var best = Directory.GetDirectories(plat).Select(d => { Version v; return new { d, v = Version.TryParse(Path.GetFileName(d).Split('-')[0], out v) ? v : null }; })
                        .Where(x => x.v != null && File.Exists(Path.Combine(x.d, "MpCmdRun.exe"))).OrderByDescending(x => x.v).FirstOrDefault();
                    if (best != null) return Path.Combine(best.d, "MpCmdRun.exe");
                }
            }
            catch { }
            var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
            return File.Exists(pf) ? pf : null;
        }
        public static string Args(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('"') >= 0) throw new ArgumentException("bad path");
            return "-Scan -ScanType 3 -File \"" + path.TrimEnd('\\') + "\" -DisableRemediation";
        }
        // "clean" / "threats" / "error" + the text the window shows
        public static string[] Verdict(int exit, string output)
        {
            var names = System.Text.RegularExpressions.Regex.Matches(output ?? "", @"Threat\s*:\s*(.+)").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            if (exit == 2 || names.Count > 0) return new[] { "threats", "Microsoft Defender found something: " + (names.Count > 0 ? string.Join(", ", names) : "a threat") + ". Don't open it - deal with it in Windows Security (or Hallow Guard)." };
            if (exit == 0) return new[] { "clean", "Microsoft Defender found nothing." };
            return new[] { "error", "Microsoft Defender couldn't scan it (code " + exit + ")." };
        }
        public static string[] Run(string path)
        {
            var exe = Find(); if (exe == null) return new[] { "error", "Microsoft Defender's scanner wasn't found on this PC." };
            try
            {
                var psi = new ProcessStartInfo(exe, Args(path)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { }
                    var outTask = p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30 * 60000)) { try { p.Kill(); } catch { } return new[] { "error", "The scan took too long and was stopped." }; }
                    return Verdict(p.ExitCode, outTask.Result);
                }
            }
            catch (Exception e) { return new[] { "error", "Couldn't start the scan: " + e.Message }; }
        }
    }

    // ---- the registry, so tests use a fake (they never register anything for real)
    public interface IReg
    {
        void Set(string key, string name, string value);   // name null = (Default)
        string Get(string key, string name);
        void DeleteTree(string key);
        void DeleteValue(string key, string name);
    }
    public class RealReg : IReg
    {
        public void Set(string key, string name, string value) { using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name ?? "", value); }
        public string Get(string key, string name) { try { using (var k = Registry.CurrentUser.OpenSubKey(key)) return k == null ? null : k.GetValue(name ?? "") as string; } catch { return null; } }
        public void DeleteTree(string key) { try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { } }
        public void DeleteValue(string key, string name) { try { using (var k = Registry.CurrentUser.OpenSubKey(key, true)) if (k != null) k.DeleteValue(name ?? "", false); } catch { } }
    }
    public class FakeReg : IReg
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public void Set(string key, string name, string value) { Values[key + "|" + (name ?? "")] = value; }
        public string Get(string key, string name) { string v; return Values.TryGetValue(key + "|" + (name ?? ""), out v) ? v : null; }
        public void DeleteValue(string key, string name) { Values.Remove(key + "|" + (name ?? "")); }
        public void DeleteTree(string key) { foreach (var k in Values.Keys.Where(x => x.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) || x.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase)).ToList()) Values.Remove(k); }
    }

    // ".torrent files and magnet: links open in Hallow Torrents" - per user (HKCU\Software\Classes), only when you click Yes
    public static class Handler
    {
        const string Prog = @"Software\Classes\HallowTorrents.torrent", Ext = @"Software\Classes\.torrent", Magnet = @"Software\Classes\magnet";
        const string Caps = @"Software\HallowTorrents\Capabilities";
        static string Cmd(string exe) { return "\"" + exe + "\" \"%1\""; }
        public static void Register(IReg r, string exe)
        {
            r.Set(Prog, null, "Torrent file"); r.Set(Prog + @"\DefaultIcon", null, "\"" + exe + "\",0"); r.Set(Prog + @"\shell\open\command", null, Cmd(exe));
            r.Set(Ext + @"\OpenWithProgids", "HallowTorrents.torrent", "");
            var cur = r.Get(Ext, null); if (string.IsNullOrEmpty(cur) || cur == "HallowTorrents.torrent") r.Set(Ext, null, "HallowTorrents.torrent");
            r.Set(Magnet, null, "URL:Magnet link"); r.Set(Magnet, "URL Protocol", ""); r.Set(Magnet + @"\DefaultIcon", null, "\"" + exe + "\",0"); r.Set(Magnet + @"\shell\open\command", null, Cmd(exe));
            // so it shows up in Windows' Default apps
            r.Set(Caps, "ApplicationName", "Hallow Torrents"); r.Set(Caps, "ApplicationDescription", "A game-aware torrent client that only runs over your VPN");
            r.Set(Caps + @"\FileAssociations", ".torrent", "HallowTorrents.torrent"); r.Set(Caps + @"\URLAssociations", "magnet", "HallowTorrents.magnet");
            r.Set(@"Software\Classes\HallowTorrents.magnet", null, "Magnet link"); r.Set(@"Software\Classes\HallowTorrents.magnet", "URL Protocol", ""); r.Set(@"Software\Classes\HallowTorrents.magnet\shell\open\command", null, Cmd(exe));
            r.Set(@"Software\RegisteredApplications", "Hallow Torrents", Caps);
        }
        public static bool IsRegistered(IReg r, string exe)
        {
            var m = r.Get(Magnet + @"\shell\open\command", null); var p = r.Get(Prog + @"\shell\open\command", null);
            return m == Cmd(exe) && p == Cmd(exe);
        }
        public static void Unregister(IReg r, string exe)
        {
            if (r.Get(Magnet + @"\shell\open\command", null) == Cmd(exe)) r.DeleteTree(Magnet);
            if (r.Get(Ext, null) == "HallowTorrents.torrent") r.DeleteValue(Ext, null);
            r.DeleteValue(Ext + @"\OpenWithProgids", "HallowTorrents.torrent");
            r.DeleteTree(Prog); r.DeleteTree(@"Software\Classes\HallowTorrents.magnet"); r.DeleteTree(@"Software\HallowTorrents\Capabilities");
            r.DeleteValue(@"Software\RegisteredApplications", "Hallow Torrents");
        }
    }
}
