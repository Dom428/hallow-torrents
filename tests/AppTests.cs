// Hallow Torrents tests: a test copy of the app (its own test folder and pipe, no windows, fake game / adapters / Hallow VPN status
// files, a fake registry) driven over the family pipe. No real torrents: the only magnet points at a closed local port.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static string Cmd(string exe, string dir, string line)
        {
            var psi = new ProcessStartInfo(exe, "--cmd \"" + line.Replace("\"", "\\\"") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir;
            using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(10000); return o; }
        }
        static void WriteVpn(string dir, bool connected)
        {
            var d = Path.Combine(dir, "HallowVPN"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "status.json.tmp"), "{\"app\":\"VPN\",\"connected\":" + (connected ? "true" : "false") + ",\"server\":\"nl123\",\"country\":\"Netherlands\"}");
            File.Copy(Path.Combine(d, "status.json.tmp"), Path.Combine(d, "status.json"), true); File.Delete(Path.Combine(d, "status.json.tmp"));
        }
        static void AppCopy()
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "HallowTorrents.exe");
            if (!File.Exists(exe)) { Ok(false, "HallowTorrents.exe is built"); return; }
            var dir = Path.Combine(Dir, "app"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true}");
            File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.5|127.0.0.1\n");
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r == "ok {\"app\":\"Torrents\",\"version\":\"" + Program.Version + "\"}", "ping: " + r);
                r = Cmd(exe, dir, "status");
                Ok(r.Contains("\"vpnRequired\":true") && r.Contains("\"vpnLocked\":true"), "no Hallow VPN: locked");
                var magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Test+Magnet&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce";
                r = Cmd(exe, dir, "add " + magnet); Ok(r.StartsWith("ok {\"id\":\"0123456789abcdef0123456789abcdef01234567\""), "pipe add <magnet> (the browser's way): " + r);
                Ok(Cmd(exe, dir, "add " + magnet).StartsWith("error That torrent is already in the list"), "the same magnet twice: refused");
                Ok(Cmd(exe, dir, "add https://example.com/x.torrent").StartsWith("error only magnet links and .torrent files"), "a web address is refused (nothing downloaded)");
                Ok(Cmd(exe, dir, "add C:\\nope\\missing.torrent").StartsWith("error"), "a missing .torrent path: error");
                r = Cmd(exe, dir, "test-list"); Ok(r.Contains("Test Magnet|Waiting for VPN"), "it waits for the VPN: " + r);
                // v1.1: the browser hand-off with a category
                var magnet2 = "magnet:?xt=urn:btih:2123456789abcdef0123456789abcdef01234567&dn=Cat+Magnet&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce";
                r = Cmd(exe, dir, "add " + magnet2 + " --category tv"); Ok(r.StartsWith("ok {\"id\":\"2123456789abcdef0123456789abcdef01234567\"") && r.Contains("\"category\":\"TV\""), "pipe add <magnet> --category tv: " + r);
                Ok(Cmd(exe, dir, "test-list").Contains("Cat Magnet|Waiting for VPN|TV"), "...it's in the TV category");
                r = Cmd(exe, dir, "add magnet:?xt=urn:btih:3123456789abcdef0123456789abcdef01234567 --category Nope"); Ok(r.StartsWith("error no category called Nope"), "an unknown category is refused: " + r);
                Ok(Cmd(exe, dir, "add magnet:?xt=urn:btih:3123456789abcdef0123456789abcdef01234567 --category").StartsWith("error"), "--category without a name: error");
                WriteVpn(dir, true);
                bool up = false; for (int i = 0; i < 20 && !up; i++) { Thread.Sleep(150); up = Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"); }
                r = Cmd(exe, dir, "test-vpn"); Ok(up && r.Contains("\"ip\":\"127.0.0.5\""), "Hallow VPN connected: engine bound to the VPN adapter, never Ethernet: " + r);
                r = Cmd(exe, dir, "status"); var noLog = r.Contains("\"log\"") ? r.Substring(0, r.IndexOf("\"log\"")) : r; Ok(r.Contains("\"vpnLocked\":false") && r.Contains("\"boundIp\":\"127.0.0.5\"") && !noLog.Contains("Test Magnet") && !noLog.Contains("0123456789abcdef"), "status: counts only, no torrent names or hashes");
                Ok(File.Exists(Path.Combine(dir, "status.json")), "status.json written");
                Ok(Cmd(exe, dir, "pause-all") == "ok" && Cmd(exe, dir, "test-list").Contains("Test Magnet|Paused"), "pause-all");
                Ok(Cmd(exe, dir, "resume-all") == "ok" && !Cmd(exe, dir, "test-list").Contains("|Paused"), "resume-all");
                r = Cmd(exe, dir, "menu"); Ok(r.StartsWith("ok {\"app\":\"Torrents\",\"items\":[") && r.Contains("\"id\":\"pause-all\"") && r.Contains("\"id\":\"add-magnet\""), "menu (Hub's generic host)");
                Ok(Cmd(exe, dir, "invoke nothing") == "error unknown item" && Cmd(exe, dir, "frobnicate") == "error unknown command", "unknown item / command");
                File.WriteAllText(Path.Combine(dir, "fake-game.txt"), "Delta Force"); Cmd(exe, dir, "test-game"); Thread.Sleep(600);
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"game\":\"Delta Force\"") && r.Contains("\"held\":\"game\""), "game running: torrents held for the game");
                Ok(Cmd(exe, dir, "test-list").Contains("Paused for game"), "list says Paused for game");
                File.Delete(Path.Combine(dir, "fake-game.txt")); Cmd(exe, dir, "test-game"); Thread.Sleep(600);
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"game\":null") && r.Contains("\"held\":null"), "game closed: running again");
                Cmd(exe, dir, "test-set gamemode limit"); File.WriteAllText(Path.Combine(dir, "fake-game.txt"), "Delta Force"); Cmd(exe, dir, "test-game"); Thread.Sleep(1600);
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"held\":null") && r.Contains("\"limitDownKBps\":200"), "game mode 'limit': 200 KB/s while playing");
                File.Delete(Path.Combine(dir, "fake-game.txt")); Cmd(exe, dir, "test-game");
                var sw = Stopwatch.StartNew(); WriteVpn(dir, false);
                bool down = false; while (sw.ElapsedMilliseconds < 3000 && !down) { down = Cmd(exe, dir, "test-vpn").Contains("\"engine\":false"); }
                Ok(down && sw.ElapsedMilliseconds < 1500, "Hallow VPN disconnected -> engine stopped (seen over the pipe after " + sw.ElapsedMilliseconds + " ms, incl. the --cmd round trips)");
                Ok(Cmd(exe, dir, "status").Contains("\"vpnLocked\":true"), "status: locked again");
                WriteVpn(dir, true); File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\n"); Thread.Sleep(800);
                r = Cmd(exe, dir, "test-vpn"); Ok(r.Contains("\"engine\":false") && r.Contains("adapter wasn") , "Hallow VPN 'connected' without its adapter: stays locked");
                File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|127.0.0.6|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.7|\n");
                Cmd(exe, dir, "test-set vpnsource adapter"); Cmd(exe, dir, "test-set vpnadapter nl");
                up = false; for (int i = 0; i < 20 && !up; i++) { Thread.Sleep(150); up = Cmd(exe, dir, "test-vpn").Contains("\"ip\":\"127.0.0.7\""); }
                Ok(up, "any VPN: bound to the picked adapter (NordVPN app's NordLynx)");
                Ok(Cmd(exe, dir, "test-handler on") == "ok True" && Cmd(exe, dir, "test-handler off") == "ok False", ".torrent / magnet handler (a fake registry - nothing real is registered)");
                Ok(Cmd(exe, dir, "test-autostart on") == "ok True" && Cmd(exe, dir, "test-autostart off") == "ok False", "Start with Windows (test file, never the real Run key)");
                Ok(Cmd(exe, dir, "quit") == "ok", "quit");
                Ok(app.WaitForExit(10000), "the test copy exits");
                var cp = new ProcessStartInfo(exe, "--cmd ping") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }; cp.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir;
                using (var c = Process.Start(cp)) { c.StandardOutput.ReadToEnd(); c.WaitForExit(10000); Ok(c.ExitCode == 2, "--cmd when it isn't running -> exit 2"); }
                Ok(File.ReadAllText(Path.Combine(dir, "torrents.json")).Contains("Test Magnet"), "the list was kept (torrents.json)");
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } }
        }
    }
}
