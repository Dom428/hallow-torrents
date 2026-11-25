// Hallow Torrents 1.10 tests: a magnet stuck on "Getting info" (its only tracker dead, DHT off) gets the public trackers once
// after the wait (shortened to 4 s here), "details" explains it, and the switch off leaves it alone. A test copy of the app with
// a fake Hallow VPN on loopback addresses: nothing reaches a real tracker (the engine is bound to 127.0.0.5, DNS is 127.0.0.1).
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
        static void AppCopy110()
        {
            var bin = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location));
            var exe = Path.Combine(bin, "HallowTorrents.exe");
            foreach (var sw in new[] { true, false })
            {
                var dir = Path.Combine(Dir, "app110" + (sw ? "on" : "off")); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true,\"AddPublicTrackers\":" + (sw ? "true" : "false") + "}");
                File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.5|127.0.0.1\n");
                WriteVpn19(dir, true, true, false, false);
                var fp = new FakeVpnPipe(dir, l => "ok connecting");
                var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
                psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
                psi.EnvironmentVariables["HALLOW_TORRENTS_LOOPBACK_PUBLIC"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_RESCUE_SECS"] = "4";
                var app = Process.Start(psi);
                try
                {
                    string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                    Ok(r != null && r.Contains("\"version\":\"" + Program.Version + "\""), "1.10 test copy (switch " + (sw ? "on" : "off") + "): ping " + r);
                    Ok(Until(() => Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"), 8000), "the engine is up on the (fake) VPN");
                    var magnet = "magnet:?xt=urn:btih:3923456789abcdef0123456789abcdef01234567&dn=Stuck+Magnet&tr=udp%3A%2F%2F127.0.0.1%3A9%2Fannounce";
                    var add = J(Cmd(exe, dir, "add " + magnet + " --direct")); var id = add.ContainsKey("id") ? (string)add["id"] : null;
                    Ok(id != null, "a magnet with a dead tracker is added: " + Family.Json(add));
                    Ok(Cmd(exe, dir, "details nope").StartsWith("error"), "details: an unknown hash is an error");
                    Until(() => Cmd(exe, dir, "details " + id).Contains("\"state\":\"Metadata\""), 8000); var d0 = J(Cmd(exe, dir, "details " + id));
                    Ok(d0.ContainsKey("state") && (string)d0["state"] == "Metadata" && !(bool)d0["hasMetadata"] && (bool)d0["magnet"], "details: getting info, no metadata yet: " + Family.Json(d0));
                    Ok(d0.ContainsKey("trackers") && ((System.Collections.ArrayList)d0["trackers"]).Count == 1 && (string)d0["dht"] == "off", "details: its one tracker, DHT off (as set)");
                    Thread.Sleep(7000);
                    var d1 = J(Cmd(exe, dir, "details " + id)); var tr = ((System.Collections.ArrayList)d1["trackers"]).Cast<Dictionary<string, object>>().Select(t => (string)t["url"]).ToList();
                    if (sw)
                    {
                        Ok((bool)d1["rescued"] && tr.Count == 1 + Core.PublicTrackers.Length && Core.PublicTrackers.All(tr.Contains), "switch on: after the wait the public trackers are added (" + tr.Count + "): " + Family.Json(d1));
                        Ok(tr[0] == "udp://127.0.0.1:9/announce", "...after the torrent's own tracker, which stays");
                        Ok(d1.ContainsKey("gettingInfoSecs") && Convert.ToInt32(d1["gettingInfoSecs"]) >= 4, "details: how long it has been getting info: " + d1["gettingInfoSecs"]);
                        Thread.Sleep(3000); var d2 = J(Cmd(exe, dir, "details " + id));
                        Ok(((System.Collections.ArrayList)d2["trackers"]).Count == tr.Count, "added once only");
                    }
                    else Ok(!(bool)d1["rescued"] && tr.Count == 1, "switch off: the magnet's trackers are left as they were: " + Family.Json(d1));
                }
                finally { try { if (!app.HasExited) { Cmd(exe, dir, "quit"); if (!app.WaitForExit(5000)) app.Kill(); } } catch { } fp.Dispose(); }
            }
        }
    }
}
