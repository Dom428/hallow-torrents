// Hallow Torrents 1.11 tests: "Pause torrents when they finish downloading" - a local swarm bound to the stand-in VPN address:
// on, a finished download is paused (after the full recheck) instead of seeding; off (the default), it seeds as before.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static async Task PauseDone111()
        {
            Ok(!new TorSettings().PauseWhenDone, "1.11: \"Pause torrents when they finish downloading\" is off by default");
            foreach (var on in new[] { true, false })
            {
                var root = Path.Combine(Dir, "pause111" + (on ? "on" : "off")); Directory.CreateDirectory(root);
                using (var w = StartSwarm(root, 0))
                {
                    var tf = await MakeTorrent(w, "Done" + (on ? "On" : "Off"), new[] { 1024 * 1024 + 11 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                    int port = FreePort(); var s = TestSettings(port, Path.Combine(root, "dl")); s.PauseWhenDone = on;
                    var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>(); core.Log += m => { lock (log) log.Add(m); };
                    string err; var e = core.AddTorrentFile(tf, null, null, null, true, out err);
                    await core.SetVpn(Vpn(VpnIp));
                    var ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 300);
                    try
                    {
                        Ok(Wait(() => core.Get(e.Id).CompletedAt != null, 30000), "switch " + (on ? "on" : "off") + ": downloaded from the local seeder");
                        Thread.Sleep(1500);
                        var st = core.Snapshot(false).First(v => v.Id == e.Id).State;
                        if (on) Ok(core.Get(e.Id).UserPaused && st == "Paused" && log.Any(l => l.StartsWith("paused when done")), "switch on: paused as soon as it finished (" + st + ")");
                        else Ok(!core.Get(e.Id).UserPaused && st == "Seeding", "switch off: it seeds as before (" + st + ")");
                        if (on) { core.Resume(e.Id); Thread.Sleep(1500); Ok(core.Snapshot(false).First(v => v.Id == e.Id).State == "Seeding", "resumed by you: it seeds (not paused again)"); }
                    }
                    finally { ticker.Dispose(); core.Dispose(); }
                }
            }
        }
    }
}
