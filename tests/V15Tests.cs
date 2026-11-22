// Hallow Torrents 1.5 tests: performance profiles, the socket watchdog (real tables + an injected non-VPN row), the leak drill against
// the stand-in VPN (a fake adapter file playing Hallow VPN's disconnect / connect), the speed test on the local swarm (and Ubuntu's
// .torrent from a stand-in, never ubuntu.com), the benchmark harness at small sizes, race.ps1 against a local qBittorrent Web API
// stand-in, the update check through Net.DirectHttp and the pipe of a test copy. Everything on this PC; nothing goes to the internet.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace HallowTorrents.Tests
{
    // the real table plus rows a test injects (a fake socket on a non-VPN address)
    public class InjectTable : ISocketTable
    {
        public ISocketTable Inner = new RealSocketTable(); public volatile SockRow Extra;
        public List<SockRow> Rows(int pid) { var r = Inner.Rows(pid); var x = Extra; if (x != null) r.Add(x); return r; }
    }

    public static partial class Run
    {
        static readonly Regex AnyIp = new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
        static SockRow Row(string proto, string local, int lport, string remote, int rport, string state) { return new SockRow { Proto = proto, Local = IPAddress.Parse(local), LocalPort = lport, Remote = remote == null ? null : IPAddress.Parse(remote), RemotePort = rport, State = state }; }

        // ---------------- unit
        static void V15Unit()
        {
            // the "v14" baseline really is what 1.4 ran: MonoTorrent 3.0.2's own defaults
            var eb = new EngineSettingsBuilder(); var tb = new TorrentSettingsBuilder(); var v14 = Perf.Preset("v14");
            Ok(v14.Connections == eb.MaximumConnections && v14.HalfOpen == eb.MaximumHalfOpenConnections && v14.CacheMB * 1024 * 1024 == eb.DiskCacheBytes && v14.OpenFiles == eb.MaximumOpenFiles && v14.PerTorrent == tb.MaximumConnections && v14.UploadSlots == tb.UploadSlots && v14.Endgame == MonoTorrent.PiecePicking.PieceRequesterSettings.Default.MaximumDuplicateRequests,
                "benchmark baseline 'v14' = MonoTorrent 3.0.2's defaults (what 1.4 used): " + v14.Summary);
            var s = new TorSettings(); s.Fix();
            Ok(s.Perf == "balanced" && Perf.For(s, false).Name == "balanced", "default profile: Balanced");
            var bal = Perf.For(s, false); var max = Perf.Preset("max"); var light = Perf.Preset("light"); var game = Perf.For(s, true);
            Ok(bal.Connections > v14.Connections && bal.PerTorrent > v14.PerTorrent && bal.CacheMB > v14.CacheMB, "Balanced: more peers and a bigger cache than 1.4: " + bal.Summary);
            Ok(max.Connections >= bal.Connections && max.PerTorrent >= bal.PerTorrent && max.CacheMB >= bal.CacheMB && light.Connections < bal.Connections && light.CacheMB < bal.CacheMB, "Max speed >= Balanced > Light");
            Ok(game.Name == "gaming" && game.Connections <= light.Connections && game.CacheMB <= light.CacheMB && game.UploadSlots <= light.UploadSlots, "while gaming: the smallest numbers (" + game.Summary + ")");
            s.Perf = "max"; s.PerfConnections = 5; s.PerfPerTorrent = 99999; s.PerfCacheMB = 48; s.Fix();
            var mx = Perf.For(s, false);
            Ok(s.PerfConnections == 10 && s.PerfPerTorrent == 1000 && mx.Connections == 10 && mx.PerTorrent == 10 && mx.CacheMB == 48 && mx.HalfOpen == max.HalfOpen, "Advanced numbers: clamped, applied on top of the profile, per torrent never above the total");
            Ok(Perf.For(s, true).Name == "gaming", "a game with \"Limit the speed\" wins over Advanced numbers");
            s.Perf = "turbo"; s.Fix(); Ok(s.Perf == "balanced", "an unknown profile in settings.json -> Balanced");
            // settings.json round trip (the test data folder)
            var keep = new TorSettings(); keep.Fix(); keep.Perf = "light"; keep.PerfHalfOpen = 12; keep.Save();
            var back = TorSettings.Load(); Ok(back.Perf == "light" && back.PerfHalfOpen == 12 && Perf.For(back, false).HalfOpen == 12, "profile and Advanced numbers kept in settings.json");
            try { File.Delete(Path.Combine(Dir, "settings.json")); } catch { }
            // the engine's settings: the profile's numbers, and the VPN binding exactly as before
            var cs = new TorSettings { Port = 45678, Dht = true }; cs.Fix(); cs.Perf = "max";
            var core = new Core(Path.Combine(Dir, "perfcore"), () => cs); var es = core.EngineSettingsFor(VpnIp);
            Ok(es.MaximumConnections == max.Connections && es.MaximumHalfOpenConnections == max.HalfOpen && es.DiskCacheBytes == max.CacheMB * 1048576 && es.MaximumOpenFiles == max.OpenFiles, "engine settings follow the profile");
            Ok(es.ListenEndPoints.Count == 1 && es.ListenEndPoints["ipv4"].Address.Equals(VpnIp) && es.DhtEndPoint != null && ((IPEndPoint)es.DhtEndPoint).Address.Equals(VpnIp) && !es.AllowPortForwarding, "...and the listener / DHT stay bound to the VPN address, port forwarding off");
            Ok(es.AllowedEncryption[0] == MonoTorrent.Connections.EncryptionType.RC4Header && es.AllowedEncryption.Contains(MonoTorrent.Connections.EncryptionType.RC4Full) && es.AllowedEncryption.Contains(MonoTorrent.Connections.EncryptionType.PlainText), "\"prefer encryption\": encrypted handshake first (less CPU than full RC4), full RC4 and plain still accepted");
            cs.Encryption = "require"; es = core.EngineSettingsFor(VpnIp); Ok(!es.AllowedEncryption.Contains(MonoTorrent.Connections.EncryptionType.PlainText), "\"require encryption\" unchanged: never plain text"); cs.Encryption = "prefer";
            cs.Perf = "v14"; es = core.EngineSettingsFor(VpnIp); Ok(es.AllowedEncryption[0] == MonoTorrent.Connections.EncryptionType.RC4Full, "the benchmark's 1.4 baseline keeps 1.4's order (full RC4 first)"); cs.Perf = "max";
            core.Gaming = true; es = core.EngineSettingsFor(VpnIp); Ok(es.MaximumConnections == Perf.Preset("gaming").Connections && es.ListenEndPoints["ipv4"].Address.Equals(VpnIp), "gaming profile while a game is limited, still VPN-bound");
            core.Dispose();

            // the watchdog's rules
            var c = new WatchContext { VpnIps = new List<IPAddress> { IPAddress.Parse("10.5.0.2") }, ApiBind = IPAddress.Parse("192.168.1.30"), ApiPort = 8080, ApiRunning = true, DirectPorts = new HashSet<int> { 51000 } };
            Ok(SocketWatch.Judge(Row("tcp", "10.5.0.2", 40000, "203.0.113.9", 6881, "established"), c) == null && SocketWatch.Judge(Row("udp", "10.5.0.2", 40000, null, 0, null), c) == null, "VPN address: fine (TCP and UDP)");
            Ok(SocketWatch.Judge(Row("tcp", "127.0.0.1", 8080, null, 0, "listen"), c) == null && SocketWatch.Judge(Row("tcp", "::1", 9000, "::1", 1, "established"), c) == null, "loopback: fine");
            Ok(SocketWatch.Judge(Row("tcp", "192.168.1.30", 8080, null, 0, "listen"), c) == null && SocketWatch.Judge(Row("tcp", "192.168.1.30", 8080, "192.168.1.20", 50000, "established"), c) == null, "the Web API on the address you picked: fine while it runs");
            c.ApiRunning = false; Ok(SocketWatch.Judge(Row("tcp", "192.168.1.30", 8080, null, 0, "listen"), c) != null, "...not when it's off"); c.ApiRunning = true;
            Ok(SocketWatch.Judge(Row("tcp", "192.168.1.30", 51000, "192.168.1.20", 9696, "established"), c) == null && SocketWatch.Judge(Row("tcp", "192.168.1.30", 51001, "192.168.1.20", 9696, "established"), c) != null, "Net.Direct sockets (local indexer / update check): only the registered port");
            Ok(SocketWatch.Judge(Row("tcp", "192.168.1.30", 51000, null, 0, "listen"), c) != null, "...never a listener on that port");
            var leak = SocketWatch.Judge(Row("tcp", "192.168.1.20", 50123, "203.0.113.9", 6881, "established"), c);
            Ok(leak != null && leak.Contains("192.168.1.20") && leak.Contains("isn't the VPN"), "a peer connection from the normal connection: caught - " + leak);
            Ok(SocketWatch.Judge(Row("udp", "0.0.0.0", 6881, null, 0, null), c) != null && SocketWatch.Judge(Row("tcp", "0.0.0.0", 6881, null, 0, "listen"), c).Contains("0.0.0.0"), "bound to every network (0.0.0.0): caught");
            Ok(SocketWatch.Judge(Row("udp", "::", 6881, null, 0, null), c).Contains("IPv6") && SocketWatch.Judge(Row("tcp", "fe80::1", 6881, "fe80::2", 1, "established"), c) != null, "IPv6: caught");
            Ok(SocketWatch.Judge(Row("udp", "0.0.0.0", 6771, null, 0, null), c) != null, "LPD's port while LPD is off: caught");
            c.Lpd = true; Ok(SocketWatch.Judge(Row("udp", "0.0.0.0", 6771, null, 0, null), c) == null && SocketWatch.Judge(Row("udp", "0.0.0.0", 6772, null, 0, null), c) != null, "LPD on (your choice): only its own port 6771");
            Ok(SocketWatch.Judge(Row("tcp", "192.168.1.20", 50123, "203.0.113.9", 6881, "time-wait"), c) == null, "closed (TIME_WAIT) rows are ignored");
            var kind = SocketWatch.Kind(Row("tcp", "192.168.1.20", 50123, "203.0.113.9", 6881, "established"));
            Ok(!AnyIp.IsMatch(kind) && kind.Contains("isn't the VPN"), "the alert for status / the pipe carries no address: " + kind);

            // the speed test's numbers and Ubuntu's pages
            var smp = new List<SpeedMath.Sample>();
            for (int i = 0; i <= 10; i++) smp.Add(new SpeedMath.Sample { T = i, Bytes = i <= 5 ? i * 10L * 1048576 : 50L * 1048576 + (i - 5) * 20L * 1048576, Peers = 10 + i });
            smp.Add(new SpeedMath.Sample { T = 20, Bytes = smp.Last().Bytes, Peers = 0, Locked = true });   // 10 s waiting for the VPN
            var rep = new SpeedReport(); SpeedMath.Fill(rep, smp);
            Ok(rep.Bytes == 150L * 1048576 && Math.Abs(rep.ActiveSeconds - 10) < 0.01 && Math.Abs(rep.AvgMBps - 15) < 0.01, "speed test average leaves out the time locked by the VPN: " + rep.AvgMBps.ToString("0.0") + " MB/s");
            Ok(Math.Abs(rep.PeakMBps - 20) < 0.01 && rep.PeersMax == 20 && rep.FirstByteSeconds == 1, "peak = best 3-second average (" + rep.PeakMBps.ToString("0.0") + "), peers max, first byte");
            var index = "<a href=\"18.04/\">18.04/</a> <a href=\"24.04/\">24.04/</a> <a href=\"24.04.3/\">24.04.3/</a> <a href=\"25.10/\">25.10/</a> <a href=\"26.04/\">26.04/</a> <a href=\"noble/\">noble/</a> <a href=\"releases/\">releases</a>";
            Ok(SpeedMath.NewestUbuntu(index) == "26.04" && SpeedMath.NewestUbuntu("<a href=\"24.04.3/\">x</a><a href=\"24.04/\">") == "24.04.3" && SpeedMath.NewestUbuntu("nothing") == null, "releases.ubuntu.com: the newest version folder");
            var page = "<a href=\"ubuntu-26.04-live-server-amd64.iso.torrent\">server</a> <a href=\"ubuntu-26.04-desktop-amd64.iso.torrent\">desktop</a> <a href=\"ubuntu-26.04-desktop-amd64.iso\">iso</a>";
            Ok(SpeedMath.DesktopTorrent(page, "https://releases.ubuntu.com/26.04/") == "https://releases.ubuntu.com/26.04/ubuntu-26.04-desktop-amd64.iso.torrent" && SpeedMath.DesktopTorrent("<a href=\"x.iso\">", "https://releases.ubuntu.com/26.04/") == null, "the desktop image's .torrent link");
            // the benchmark's result lines and table
            var br = Bench.Parse("RESULT ok;2147483648;20.500;99.90;150.00;54.40;265.4;73;2048;7.020");
            Ok(br != null && br.Ok && br.Bytes == 2147483648 && Math.Abs(br.AvgMBps - 99.9) < 0.001 && br.WrittenMB == 2048 && Math.Abs(br.VerifySeconds - 7.02) < 0.001, "benchmark result line parsed");
            Ok(Bench.Parse("RESULT error no manager").Ok == false && Bench.Parse("T 1 2") == null, "benchmark: an error line / a progress line");
            var tbl = Bench.Table(new List<BenchResult> { new BenchResult { Ok = true, Scenario = "lan", Torrent = "big", Profile = "v14", Seconds = 10 }, new BenchResult { Ok = true, Scenario = "lan", Torrent = "big", Profile = "v14", Seconds = 20 }, new BenchResult { Ok = true, Scenario = "lan", Torrent = "big", Profile = "v14", Seconds = 12 } });
            Ok(tbl.Count == 1 && tbl[0][3] == "3" && tbl[0][4] == "12.0", "benchmark table: the median of the runs");
        }

        // ---------------- the watchdog, the leak drill and the speed test on the local swarm
        static async Task Proof15()
        {
            var root = Path.Combine(Dir, "proof15"); Directory.CreateDirectory(root);
            using (var w = StartSwarm(root, 400))
            {
                var tf = await MakeTorrent(w, "DrillData", new[] { 48 * 1024 * 1024 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                var tfSpeed = await MakeTorrent(w, "SpeedData", new[] { 6 * 1024 * 1024, 900000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                int port = FreePort(); var save = Path.Combine(root, "dl"); var s = TestSettings(port, save);
                var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>(); core.Log += m => { lock (log) log.Add(m); };
                var watchFile = Path.Combine(root, "adapters.txt"); const string up = "fake|Fake VPN|WireGuard Tunnel|up|127.0.0.3|127.0.0.1\n", down = "fake|Fake VPN|WireGuard Tunnel|down||\n";
                WriteRetry(watchFile, up);
                var ws = new TorSettings { VpnSource = "adapter", VpnAdapter = "fake" };
                var watch = new VpnWatch(() => ws, new FakeNetworks(watchFile), false);
                watch.Changed += (o, n) => { try { core.SetVpn(n).Wait(5000); } catch { } };
                watch.Start();
                Ok(Wait(() => core.EngineUp, 5000), "stand-in VPN up: engine bound to " + core.BoundIp);
                var ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 1000);
                string err; var e = core.AddTorrentFile(tf, null, null, null, true, out err);
                Ok(Wait(() => core.Snapshot(false).Any(v => v.Id == e.Id && v.State == "Downloading" && v.DownRate > 0), 15000), "a download is running (the seeder is limited, so it keeps going)");

                // ---- a profile change applies live (Settings / a game with "Limit the speed"), the sockets stay VPN-bound
                s.Perf = "max"; core.Kick();
                Ok(Wait(() => { var m0 = core.ManagerForTests(e.Id); return m0 != null && m0.Engine.Settings.MaximumConnections == Perf.Preset("max").Connections && m0.Settings.MaximumConnections == Perf.Preset("max").PerTorrent; }, 5000), "profile switched to Max speed while downloading: engine and torrent settings updated live");
                core.Gaming = true; core.Kick();
                Ok(Wait(() => core.ManagerForTests(e.Id).Engine.Settings.MaximumConnections == Perf.Preset("gaming").Connections && core.ManagerForTests(e.Id).Engine.Settings.DiskCacheBytes == Perf.Preset("gaming").CacheMB * 1048576, 5000), "a game with \"Limit the speed\": the gaming profile, live");
                core.Gaming = false; s.Perf = "balanced"; core.Kick(); Wait(() => core.ManagerForTests(e.Id).Engine.Settings.MaximumConnections == Perf.Preset("balanced").Connections, 5000);
                var lst = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(x => x.Port == port).ToList();
                Ok(lst.Count == 1 && lst[0].Address.Equals(VpnIp) && core.ManagerForTests(e.Id).Engine.Settings.ListenEndPoints["ipv4"].Address.Equals(VpnIp), "...the listener is still only on the VPN address: " + string.Join(",", lst.Select(x => x.ToString())));
                Ok(Wait(() => core.Snapshot(false).Any(v => v.Id == e.Id && v.State == "Downloading" && v.DownRate > 0), 15000), "...and the download keeps going");

                // ---- the watchdog: Windows' real table for this process, then an injected non-VPN row
                var ds = new TorSettings { VpnSource = "hallow" }; ds.Fix(); ds.SaveFolder = save;   // the drill plays Hallow VPN mode (its pipe = the stand-in below)
                var inj = new InjectTable(); string alerted = null;
                var proof = new Proof(core, () => ds, () => watch.Current, () => false, m => { lock (log) log.Add(m); }, inj);
                proof.Alert += r => alerted = r;
                var real = new RealSocketTable().Rows(Process.GetCurrentProcess().Id);
                Ok(real.Any(r => r.Proto == "tcp" && r.State == "listen" && r.Local.Equals(VpnIp) && r.LocalPort == port), "Windows' TCP table shows our peer listener on the VPN address (" + real.Count + " rows for this process)");
                Ok(real.Any(r => r.Proto == "tcp" && r.State == "established" && r.Local.Equals(VpnIp)), "...and our peer connections from it");
                var first = proof.Watchdog.CheckNow();
                Ok(first.Count == 0 && core.EngineUp && core.Tripped == null, "the watchdog on the real table during a download: no false alarm (" + proof.Watchdog.LastRows + " sockets)" + (first.Count > 0 ? " - " + first[0] : ""));
                inj.Extra = Row("tcp", "192.168.77.5", 50123, "203.0.113.9", 6881, "established");
                var sw = Stopwatch.StartNew(); var bad = proof.Watchdog.CheckNow(); var lockMs = sw.ElapsedMilliseconds;
                Ok(bad.Count == 1 && !core.EngineUp && core.Tripped != null && lockMs < 1000 && alerted != null, "an injected socket on a non-VPN address: engine locked in " + lockMs + " ms, red alert raised: " + alerted);
                inj.Extra = null; await core.SetVpn(watch.Current);
                Ok(!core.EngineUp && core.Snapshot(false).First(v => v.Id == e.Id).State == "Waiting for VPN", "it stays locked although the VPN is up (until you resume)");
                var wj = proof.Pipe("watchdog", "", null);
                Ok(wj.StartsWith("ok {") && wj.Contains("\"locked\":true") && wj.Contains("\"alert\":\"TCP socket on an address that isn") && !AnyIp.IsMatch(wj.Replace(Program.Version, "")), "pipe watchdog: locked, the alert without addresses: " + wj);
                var rr = proof.Pipe("watchdog", "resume", () => { core.ClearTrip(); core.SetVpn(watch.Current).Wait(5000); });
                Ok(rr == "ok resumed" && core.EngineUp && core.Tripped == null, "pipe watchdog resume: engine back on the VPN address");
                Ok(Wait(() => core.Snapshot(false).Any(v => v.Id == e.Id && v.State == "Downloading" && v.DownRate > 0), 15000), "...and the download carries on");
                // a REAL socket off the VPN in this process is seen too: UDP on 0.0.0.0, IPv6, and (if this PC has one) a TCP connection on its LAN address
                using (var u = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    var p0 = ((IPEndPoint)u.Client.LocalEndPoint).Port; var rows = new RealSocketTable().Rows(Process.GetCurrentProcess().Id);
                    var mine = rows.FirstOrDefault(r => r.Proto == "udp" && r.LocalPort == p0);
                    Ok(mine != null && SocketWatch.Judge(mine, proof.Context()) != null, "a real UDP socket on 0.0.0.0 is caught: " + (mine == null ? "not in the table" : SocketWatch.Judge(mine, proof.Context())));
                }
                using (var s6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
                {
                    s6.Bind(new IPEndPoint(IPAddress.IPv6Any, 0)); var p6 = ((IPEndPoint)s6.LocalEndPoint).Port;
                    var mine = new RealSocketTable().Rows(Process.GetCurrentProcess().Id).FirstOrDefault(r => r.Proto == "udp" && r.LocalPort == p6);
                    Ok(mine != null && SocketWatch.Judge(mine, proof.Context()).Contains("IPv6"), "a real IPv6 socket is caught");
                }
                var lan = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
                if (lan != null)
                {
                    var l = new TcpListener(lan, 0); l.Start();
                    try
                    {
                        using (var cl = new TcpClient(new IPEndPoint(lan, 0)))
                        {
                            cl.Connect(lan, ((IPEndPoint)l.LocalEndpoint).Port); var acc = l.AcceptTcpClient();
                            var cp = ((IPEndPoint)cl.Client.LocalEndPoint).Port;
                            var mine = new RealSocketTable().Rows(Process.GetCurrentProcess().Id).FirstOrDefault(r => r.Proto == "tcp" && r.LocalPort == cp);
                            Ok(mine != null && mine.State == "established" && SocketWatch.Judge(mine, proof.Context()) != null, "a real TCP connection on this PC's own network address (never leaves the PC) is caught");
                            acc.Close();
                        }
                    }
                    finally { l.Stop(); }
                }
                var direct0 = SocketWatch.DirectPorts().Count;

                // ---- the leak drill (Hallow VPN mode; its disconnect / connect play out on the fake adapter file)
                var asked = new List<string>();
                proof.Drill.AskVpn = cmd => { lock (asked) asked.Add(cmd); if (cmd == "disconnect") { WriteRetry(watchFile, down); return "ok"; } if (cmd.StartsWith("connect")) { WriteRetry(watchFile, up); return "ok connecting"; } return null; };
                proof.Drill.ReconnectWaitSec = 20; proof.Drill.ResumeWaitSec = 30;
                core.Pause(e.Id); Wait(() => core.Snapshot(false).All(v => v.DownRate == 0), 5000);
                Ok(proof.Drill.Ready() != null && proof.Drill.Ready().Contains("Start a torrent download"), "leak drill refused while nothing downloads: " + proof.Drill.Ready());
                core.Resume(e.Id); Ok(Wait(() => proof.Drill.Ready() == null, 15000), "...ready once the download runs");
                var rep = proof.Drill.RunNow();
                Ok(rep.Pass, "leak drill PASS against the stand-in VPN:\n" + rep.Text());
                Ok(rep.LockedMs >= 0 && rep.LockedMs <= 2000 && rep.SocketsClosedMs >= 0, "locked " + rep.LockedMs + " ms after Hallow VPN's disconnect, engine sockets gone after " + rep.SocketsClosedMs + " ms");
                lock (asked) Ok(asked.Count == 2 && asked[0] == "disconnect" && asked[1].StartsWith("connect "), "asked Hallow VPN: " + string.Join(", ", asked));
                Ok(rep.ResumedMs > 0 && core.EngineUp && core.BoundIp.Equals(VpnIp), "the download resumed after the reconnect, bound to the VPN address again");
                Ok(File.Exists(proof.Drill.FileName) && File.ReadAllText(proof.Drill.FileName).Contains("\"pass\":true"), "report saved (leak-drill.json + .txt)");
                var dj = proof.Pipe("leak-drill", "status", null);
                Ok(dj.StartsWith("ok {") && dj.Contains("\"pass\":true") && !AnyIp.IsMatch(dj), "pipe leak-drill status: the report without addresses");
                // the drill must FAIL when a socket appears off the VPN during the drop
                proof.Drill.AskVpn = cmd => { if (cmd == "disconnect") { WriteRetry(watchFile, down); inj.Extra = Row("udp", "192.168.77.5", 6881, null, 0, null); return "ok"; } if (cmd.StartsWith("connect")) { inj.Extra = null; WriteRetry(watchFile, up); return "ok connecting"; } return null; };
                Ok(Wait(() => proof.Drill.Ready() == null, 20000), "download running again for the second drill");
                var rep2 = proof.Drill.RunNow();
                Ok(!rep2.Pass && rep2.Checks.Any(x => x.Name.StartsWith("No socket on a non-VPN address") && !x.Pass) && rep2.Problems.Any(p => p.Contains("192.168.77.5")), "a socket off the VPN during the drop: the drill FAILS and names it");
                Ok(rep2.LockedMs >= 0 && rep2.LockedMs <= 2000, "(it still locked in " + rep2.LockedMs + " ms)");
                inj.Extra = null; if (core.Tripped != null) core.ClearTrip();
                Ok(Wait(() => core.EngineUp, 10000), "VPN back after the second drill");

                // ---- the speed test on the local swarm (a .torrent file), then Ubuntu's .torrent from a stand-in (never ubuntu.com)
                core.Pause(e.Id);
                await w.Seeder.UpdateSettingsAsync(new EngineSettingsBuilder(w.Seeder.Settings) { MaximumUploadRate = 0 }.ToSettings());
                Ok(proof.Speed.Start("https://example.com/a.torrent") != null, "speed test: only a magnet link, a .torrent file or \"ubuntu\"");
                var st = proof.Speed.Start(tfSpeed);
                Ok(st == null && proof.Speed.Running, "speed test started: " + st);
                Ok(Wait(() => !proof.Speed.Running, 60000), "speed test finished");
                var sr = proof.Speed.Last;
                Ok(sr != null && sr.State == "done" && sr.Bytes >= 6 * 1024 * 1024 && sr.AvgMBps > 0 && sr.PeakMBps > 0 && sr.PeersMax >= 1 && sr.Profile == "balanced", "speed test report: " + (sr == null ? "none" : Family.Json(sr.Json())));
                var sid = Core.IdOf(Torrent.Load(tfSpeed).InfoHashes);
                Ok(core.Get(sid).UserPaused && File.Exists(Path.Combine(save, "Speed test", "SpeedData", "file0.bin")), "the files are in \"Speed test\" and it's paused (doesn't seed on by itself)");
                var sj = proof.Pipe("speedtest", "status", null);
                Ok(sj.StartsWith("ok {") && sj.Contains("\"state\":\"done\"") && sj.Contains("\"running\":false") && !AnyIp.IsMatch(sj.Replace(save, "")), "pipe speedtest status: " + sj.Substring(0, Math.Min(160, sj.Length)));
                var fetched = new List<string>(); var ubuntuTf = await MakeTorrent(w, "UbuntuStandIn", new[] { 2 * 1024 * 1024 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                proof.Speed.Fetch = url =>
                {
                    lock (fetched) fetched.Add(url);
                    if (url == SpeedTest.UbuntuIndex) return Encoding.UTF8.GetBytes("<a href=\"24.04/\">24.04/</a><a href=\"26.04/\">26.04/</a>");
                    if (url == SpeedTest.UbuntuIndex + "26.04/") return Encoding.UTF8.GetBytes("<a href=\"ubuntu-26.04-desktop-amd64.iso.torrent\">t</a>");
                    if (url.EndsWith("ubuntu-26.04-desktop-amd64.iso.torrent")) return File.ReadAllBytes(ubuntuTf);
                    throw new IOException("unexpected " + url);
                };
                st = proof.Speed.Start("ubuntu");
                Ok(st == null && proof.Speed.Current.Source.EndsWith("ubuntu-26.04-desktop-amd64.iso.torrent"), "\"ubuntu\": the newest desktop .torrent fetched (through the VPN fetcher) and started: " + st);
                lock (fetched) Ok(fetched.Count == 3 && fetched.All(u => u.StartsWith("https://releases.ubuntu.com/")), "only releases.ubuntu.com was asked: " + string.Join(", ", fetched));
                Ok(Wait(() => !proof.Speed.Running, 60000) && proof.Speed.Last.State == "done", "...and measured");
                await core.Teardown("test: locked"); fetched.Clear(); string uerr;
                Ok(proof.Speed.GetUbuntu(out uerr) == null && uerr.Contains("Connect your VPN") && fetched.Count == 0, "while locked: nothing is fetched (" + uerr + ")");

                // ---- the update check goes through Net.DirectHttp (registered with the watchdog), here against a local stand-in for api.github.com
                using (var gh = new StandIn15(req => req.Path.StartsWith("/repos/" + Updater.Repo + "/releases/latest") ? StandIn15.Reply(200, "application/json", "{\"tag_name\":\"v9.9\",\"assets\":[{\"name\":\"Hallow.Torrents.zip\",\"browser_download_url\":\"http://127.0.0.1/x.zip\",\"digest\":\"sha256:00\"}]}") : StandIn15.Reply(404, "text/plain", "no")))
                {
                    Environment.SetEnvironmentVariable("HALLOW_TORRENTS_API", "http://127.0.0.1:" + gh.Port);
                    var rel = Updater.Latest();
                    Environment.SetEnvironmentVariable("HALLOW_TORRENTS_API", null);
                    Ok(rel != null && rel.Tag == "v9.9" && SocketWatch.DirectPorts().Count > direct0, "update check through Net.DirectHttp: works and its socket is registered with the watchdog");
                }
                ticker.Dispose(); watch.Dispose(); proof.Dispose(); core.Dispose();
            }
        }

        // ---------------- verify on completion: a block zeroed on disk after the engine said "complete" is found and downloaded again
        static async Task Verify15()
        {
            var root = Path.Combine(Dir, "verify15"); Directory.CreateDirectory(root);
            using (var w = StartSwarm(root, 0))
            {
                var tf = await MakeTorrent(w, "VerifyData", new[] { 3 * 1024 * 1024, 200000, 700000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                int port = FreePort(); var save = Path.Combine(root, "dl"); var s = TestSettings(port, save);
                var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>(); core.Log += q => { lock (log) log.Add(q); };
                string err; var e = core.AddTorrentFile(tf, null, null, null, true, out err);
                await core.SetVpn(Vpn(VpnIp)); await core.Tick();   // no ticker: the test decides when Core looks
                var m = core.ManagerForTests(e.Id);
                Ok(Wait(() => m.Complete, 30000), "the engine says complete");
                await m.StopAsync(TimeSpan.FromSeconds(5));
                var f = Path.Combine(save, "VerifyData", "file0.bin");
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { fs.Position = 1000000; fs.Write(new byte[16384], 0, 16384); }   // what MonoTorrent 3.0.2 sometimes leaves behind
                await m.StartAsync();
                await core.Tick();
                Ok(core.Get(e.Id).CompletedAt == null && Wait(() => { lock (log) return log.Any(x => x.StartsWith("verify: 1 piece(s) on disk were bad")); }, 15000), "Core's recheck before \"finished\" finds the bad piece: " + string.Join(" | ", log.Where(x => x.StartsWith("verif"))));
                var t = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 500);
                Ok(Wait(() => core.Get(e.Id).CompletedAt != null, 30000), "...downloads it again, verifies again, then it's finished");
                t.Dispose();
                Ok(Sha(f) == Sha(Path.Combine(root, "seed", "VerifyData", "file0.bin")) && core.Repaired == 1, "the file on disk is right again (" + core.Repaired + " piece repaired)");
                core.Dispose();
            }
        }

        // ---------------- a tiny HTTP server for stand-ins (one request per connection)
        public class StandIn15 : IDisposable
        {
            public class Req { public string Method, Path, Body; public byte[] Raw; }
            public static byte[] Reply(int code, string type, string body) { var b = Encoding.UTF8.GetBytes(body ?? ""); return Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " X\r\nContent-Type: " + type + "\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n").Concat(b).ToArray(); }
            readonly TcpListener l; volatile bool stop; readonly Func<Req, byte[]> handler; public readonly List<Req> Seen = new List<Req>();
            public int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }
            public StandIn15(Func<Req, byte[]> handler)
            {
                this.handler = handler; l = new TcpListener(IPAddress.Loopback, 0); l.Start();
                new Thread(() => { while (!stop) { TcpClient c; try { c = l.AcceptTcpClient(); } catch { return; } new Thread(() => Serve(c)) { IsBackground = true }.Start(); } }) { IsBackground = true }.Start();
            }
            void Serve(TcpClient c)
            {
                try
                {
                    using (c)
                    {
                        var s = c.GetStream(); s.ReadTimeout = 10000; var buf = new List<byte>(); var one = new byte[1];
                        while (true) { int n = s.Read(one, 0, 1); if (n <= 0) return; buf.Add(one[0]); if (buf.Count >= 4 && buf[buf.Count - 4] == 13 && buf[buf.Count - 3] == 10 && buf[buf.Count - 2] == 13 && buf[buf.Count - 1] == 10) break; }
                        var head = Encoding.ASCII.GetString(buf.ToArray()); var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None); var first = lines[0].Split(' ');
                        int len = 0; foreach (var h in lines) if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(h.Substring(15).Trim());
                        bool expect = lines.Any(h => h.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase));
                        if (expect) { var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); s.Write(cont, 0, cont.Length); }
                        var body = new byte[len]; int got = 0; while (got < len) { int n = s.Read(body, got, len - got); if (n <= 0) break; got += n; }
                        var req = new Req { Method = first[0], Path = Uri.UnescapeDataString(first[1]), Raw = body, Body = Encoding.GetEncoding("iso-8859-1").GetString(body) };
                        lock (Seen) Seen.Add(req);
                        var r = handler(req); s.Write(r, 0, r.Length); s.Flush();
                    }
                }
                catch { }
            }
            public void Dispose() { stop = true; try { l.Stop(); } catch { } }
        }

        // ---------------- race.ps1 against a qBittorrent Web API stand-in (no qBittorrent, no internet)
        static void Race15()
        {
            var script = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "tools", "race.ps1");
            if (!File.Exists(script)) { Ok(false, "tools\\race.ps1 exists"); return; }
            var dir = Path.Combine(Dir, "race"); Directory.CreateDirectory(dir);
            var events = new List<string>(); string iface = "", addr = ""; int infoCalls = 0; bool added = false; const long MB = 1048576;
            using (var q = new StandIn15(req =>
            {
                var p = req.Path;
                if (p.StartsWith("/api/v2/app/version")) return StandIn15.Reply(200, "text/plain", "v5.2.4");
                if (p.StartsWith("/api/v2/app/preferences")) return StandIn15.Reply(200, "application/json", "{\"current_network_interface\":\"" + iface + "\",\"current_interface_address\":\"" + addr + "\",\"save_path\":\"C:\\\\x\"}");
                if (p.StartsWith("/api/v2/app/networkInterfaceList")) return StandIn15.Reply(200, "application/json", "[{\"name\":\"Ethernet\",\"value\":\"eth_1\"},{\"name\":\"TestVPN\",\"value\":\"test_iface_1\"}]");
                if (p.StartsWith("/api/v2/app/setPreferences"))
                {
                    var j = Uri.UnescapeDataString(req.Body.Replace('+', ' ')); var m = Regex.Match(j, "\"current_network_interface\":\"([^\"]*)\""); var a = Regex.Match(j, "\"current_interface_address\":\"([^\"]*)\"");
                    iface = m.Groups[1].Value; addr = a.Groups[1].Value; lock (events) events.Add("bind:" + iface + "@" + addr); return StandIn15.Reply(200, "text/plain", "");
                }
                if (p.StartsWith("/api/v2/torrents/add")) { lock (events) events.Add("add:" + (req.Body.Contains("4:infod") && req.Body.Contains("filename=\"race.torrent\"") ? "file" : "other " + req.Raw.Length + " " + req.Body.Substring(0, Math.Min(120, req.Body.Length)).Replace("\r\n", "~")) + ":" + (iface == "test_iface_1" ? "bound" : "UNBOUND")); added = true; return StandIn15.Reply(200, "text/plain", "Ok."); }
                if (p.StartsWith("/api/v2/torrents/info"))
                {
                    if (!added) return StandIn15.Reply(200, "application/json", "[]");
                    var n = Interlocked.Increment(ref infoCalls); var done = Math.Min(60 * MB, n * 12 * MB);
                    return StandIn15.Reply(200, "application/json", "[{\"hash\":\"abcdef\",\"downloaded\":" + done + ",\"progress\":" + (done / (double)(60 * MB)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"num_seeds\":30,\"num_leechs\":" + (n + 2) + ",\"dlspeed\":12582912,\"state\":\"" + (done >= 60 * MB ? "uploading" : "downloading") + "\"}]");
                }
                if (p.StartsWith("/api/v2/torrents/stop")) { lock (events) events.Add("stop"); return StandIn15.Reply(200, "text/plain", ""); }
                if (p.StartsWith("/api/v2/torrents/delete")) { lock (events) events.Add("delete:" + req.Body); return StandIn15.Reply(200, "text/plain", ""); }
                return StandIn15.Reply(404, "text/plain", "Not Found");
            }))
            {
                iface = "eth_1"; addr = "";   // the user's qBittorrent was bound to something else before the race
                var tdir = Path.Combine(dir, "t"); Directory.CreateDirectory(tdir); File.WriteAllBytes(Path.Combine(tdir, "a.bin"), new byte[300000]); File.WriteAllText(Path.Combine(tdir, "b.txt"), "x");
                var tc = new TorrentCreator(TorrentType.V1Only); tc.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" }); var tf = Path.Combine(dir, "race.torrent"); tc.Create(new TorrentFileSource(tdir), tf);
                var fixture = Path.Combine(dir, "hallow.json"); File.WriteAllText(fixture, "{\"state\":\"done\",\"avgMBps\":12.5,\"peakMBps\":20.1,\"activeSeconds\":100,\"peersMax\":40,\"cpuPct\":1.5,\"source\":null}");
                var outDir = Path.Combine(dir, "out");
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Test -Adapter TestVPN -AdapterIp 127.0.0.3 -SkipHallow -HallowReport \"" + fixture + "\" -QbitUrl http://127.0.0.1:" + q.Port + " -Torrent \"" + tf + "\" -OutDir \"" + outDir + "\" -PollSeconds 1 -MaxMinutes 2")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                string so, se; int code;
                using (var p = Process.Start(psi)) { var t1 = p.StandardOutput.ReadToEndAsync(); var t2 = p.StandardError.ReadToEndAsync(); if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } } so = t1.Result; se = t2.Result; code = p.HasExited ? p.ExitCode : -1; }
                Ok(code == 0, "race.ps1 against the qBittorrent stand-in: exit " + code + (code != 0 ? "\n" + so + "\n" + se : ""));
                List<string> ev; lock (events) ev = events.ToList();
                int bind = ev.FindIndex(x => x == "bind:test_iface_1@127.0.0.3"), add = ev.FindIndex(x => x.StartsWith("add:"));
                Ok(bind >= 0 && add > bind && ev[add] == "add:file:bound", "qBittorrent bound to the VPN adapter BEFORE the torrent was added (the .torrent uploaded): " + string.Join(" | ", ev.Select(x => x.Length > 200 ? x.Substring(0, 200) : x)));
                Ok(ev.Last().StartsWith("bind:eth_1") && iface == "eth_1", "its own network interface put back afterwards");
                Ok(ev.Any(x => x.StartsWith("delete:") && x.Contains("deleteFiles=false")), "the race torrent leaves qBittorrent's list, files kept (never deleted)");
                var json = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "race-*.json").FirstOrDefault() : null;
                var text = json == null ? "" : File.ReadAllText(json);
                var qb = Regex.Match(text, "\"qbittorrent\":\\s*\\{[^}]*\"AvgMBps\":\\s*([0-9.]+)"); double qavg = qb.Success ? double.Parse(qb.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
                Ok(json != null && (text.Contains("\"AvgMBps\":  12.5") || text.Contains("\"AvgMBps\": 12.5")) && qavg > 5 && qavg < 40 && text.Contains("\"Faster\""), "race result saved (Hallow 12.5 MB/s from its report, qBittorrent " + qavg + " MB/s from the stand-in's samples): " + (json ?? "no file"));
                Ok(Directory.GetFiles(outDir, "race-*.md").Length == 1, "...and a Markdown table");
            }
        }

        // ---------------- the benchmark harness at small sizes (the real run is TorrentsTests.exe bench)
        static void BenchSmoke15()
        {
            var rs = new List<BenchResult>();
            var code = Bench.Orchestrate(new[] { "bench", "--big", "24", "--files", "60", "--runs", "1", "--profiles", "v14,balanced", "--scen", "lan,wan,crowd", "--latency", "20", "--cap", "2048", "--crowd", "12", "--crowdcap", "512" }, rs);
            Ok(code == 0 && rs.Count == 10 && rs.All(r => r.Ok), "benchmark smoke run: 10 downloads (lan / wan x big / small, crowd x big; 1.4 and Balanced) all complete: " + string.Join(", ", rs.Select(r => r.Scenario + "/" + r.Torrent + "/" + r.Profile + (r.Ok ? "" : " FAILED " + r.Error))));
            Ok(rs.All(r => r.Seconds > 0 && r.AvgMBps > 0 && r.CpuSeconds > 0 && r.PeakMB > 0 && r.WrittenMB >= (r.Torrent == "big" ? 20 : 1) && r.VerifySeconds >= 0), "every run measured time, speed, CPU, memory, bytes written and the recheck");
            Ok(Bench.Table(rs).Count == 10, "a table row per scenario / torrent / profile");
            Ok(!Directory.GetDirectories(Path.GetTempPath(), "HallowBench" + Process.GetCurrentProcess().Id).Any(), "its generated data is deleted afterwards");
        }

        // ---------------- the pipe of a test copy
        static void AppCopy15()
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "HallowTorrents.exe");
            var dir = Path.Combine(Dir, "app15"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true}");
            File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.6|127.0.0.1\n");
            WriteVpn(dir, true);
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r != null && r.Contains("\"version\":\"" + Program.Version + "\""), "test copy: ping " + r);
                bool up = false; for (int i = 0; i < 30 && !up; i++) { Thread.Sleep(200); up = Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"); }
                Ok(up, "engine up on the stand-in VPN");
                Thread.Sleep(3500);   // a watchdog check or two on a real running app
                r = Cmd(exe, dir, "watchdog");
                Ok(r.StartsWith("ok {") && r.Contains("\"on\":true") && r.Contains("\"locked\":false") && !r.Contains("\"checks\":0"), "pipe watchdog: on, checking, no false alarm in the running app: " + r);
                r = Cmd(exe, dir, "status");
                Ok(r.Contains("\"perf\":\"balanced\"") && r.Contains("\"watchdog\":true") && r.Contains("\"watchdogLocked\":false") && r.Contains("\"speedTest\":false") && r.Contains("\"leakDrill\":false"), "status: perf / watchdog / speed test / leak drill fields");
                Ok(Cmd(exe, dir, "speedtest").Contains("\"state\":\"none\""), "pipe speedtest: none yet");
                Ok(Cmd(exe, dir, "speedtest https://example.com/x.torrent").StartsWith("error Give a magnet link"), "pipe speedtest: a web address is refused (nothing fetched)");
                r = Cmd(exe, dir, "leak-drill"); Ok(r.StartsWith("error Start a torrent download first"), "pipe leak-drill: refused while nothing downloads: " + r);
                Ok(Cmd(exe, dir, "leak-drill status").Contains("\"state\":\"none\""), "pipe leak-drill status: none yet");
                Ok(Cmd(exe, dir, "watchdog resume") == "ok nothing to resume", "pipe watchdog resume: nothing to resume");
                Ok(Cmd(exe, dir, "open proof") == "ok" && Cmd(exe, dir, "open speedtest") == "ok", "open proof / speedtest (the Speed & leaks page)");
                Cmd(exe, dir, "quit"); app.WaitForExit(8000);
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } }
        }
    }
}
