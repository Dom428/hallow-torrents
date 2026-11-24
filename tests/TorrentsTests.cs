// Hallow Torrents tests. Everything runs on THIS PC only: a local tracker (MonoTorrent's own, HTTP and UDP, on 127.0.0.1), a seeder
// engine on 127.0.0.1, and Hallow Torrents' engine bound to a stand-in "VPN" address (127.0.0.3, a second loopback address) - no
// public trackers, no public torrents, DHT / PEX / LPD off. Random test files, temp folders only (never the real data folder, never
// a NAS). The registry is a fake, so nothing is registered for real.
//   tests\TorrentsTests.exe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static int pass, fail;
        static void Ok(bool c, string name) { if (c) { pass++; Console.WriteLine("ok   " + name); } else { fail++; Console.WriteLine("FAIL " + name); } }
        static string Dir;
        static readonly IPAddress VpnIp = IPAddress.Parse("127.0.0.3");

        public static int Main(string[] args)
        {
            Dir = Path.Combine(Path.GetTempPath(), "HallowTorrentsTest" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(Dir);
            Environment.SetEnvironmentVariable("HALLOW_TORRENTS_TESTDIR", Dir);
            var only = args.FirstOrDefault(a => !a.StartsWith("-"));
            // 1.5: the local speed benchmark (Bench15.cs) - not part of the suite (the suite runs a small smoke version: benchsmoke)
            if (only == "bench-client") return Bench.Client(args);
            if (only == "bench") { var code = Bench.Orchestrate(args); try { Directory.Delete(Dir, true); } catch { } return code; }
            try
            {
                if (only == null || only == "unit") { Names(); Suspicious(); Categories(); Plans(); VpnDecide(); DnsPackets(); HandlerFake(); Defender(); ModeAndContrast(); V11Unit(); V12Unit(); V13Unit(); }
                if (only == null || only == "swarm") Swarm().Wait();
                if (only == null || only == "swarm11") Swarm11().Wait();
                if (only == null || only == "arr") Arr12().Wait();
                // 1.3's Torznab stand-in on 127.0.0.1 plays a PUBLIC indexer (it must be reached over the VPN), so loopback counts as public
                // there; 1.4's local-server exception has its own suite (Local14 / Web14)
                if (only == null || only == "search") Search13AsPublic();
                if (only == null || only == "app") AppCopy();
                if (only == null || only == "unit") V14Unit();
                if (only == null || only == "local") Local14().Wait();
                if (only == null || only == "web") Web14().Wait();
                if (only == null || only == "headless") Headless14();
                // 1.5: performance profiles, the socket watchdog, the leak drill, the speed test, the benchmark harness, race.ps1
                if (only == null || only == "unit" || only == "unit15") V15Unit();
                if (only == null || only == "proof") { Proof15().Wait(); Verify15().Wait(); }
                if (only == null || only == "race") Race15();
                if (only == null || only == "benchsmoke") BenchSmoke15();
                if (only == null || only == "app15") AppCopy15();
                // 1.6: Send to Jellyfin
                if (only == null || only == "unit" || only == "unit16") V16Unit();
                if (only == null || only == "nas") { Nas16(); Copy16(); }
                if (only == null || only == "app16") AppCopy16();
                // 1.7: Plex and Emby too
                if (only == null || only == "unit" || only == "unit17") V17Unit();
                if (only == null || only == "servers") { Emby17(); Plex17(); }
                if (only == null || only == "app17") AppCopy17();
                if (only == "real17") Real17();
                // 1.8: for Hallow Remote 1.18 / Hub 2.17 - categories, search ids per client
                if (only == null || only == "unit" || only == "unit18") V18Unit();
                if (only == null || only == "scope18") Scope18();
                if (only == null || only == "app18") AppCopy18();
                // 1.9: VPN on demand - auto-connect, P2P servers only, the kill switch tip (a fake Hallow VPN on a .test pipe)
                if (only == null || only == "unit" || only == "unit19") V19Unit();
                if (only == null || only == "vpn19") Vpn19();
                if (only == null || only == "app19") AppCopy19();
            }
            catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); }
            try { Directory.Delete(Dir, true); } catch { }
            Console.WriteLine(pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }

        // ---------------- names for the library
        static void Names()
        {
            var p = Media.Parse("The.Matrix.1999.1080p.BluRay.x264-GROUP");
            Ok(p.Kind == "movie" && p.Title == "The Matrix" && p.Year == 1999, "movie: The Matrix (1999): " + p.Title + " " + p.Year);
            p = Media.Parse("Blade Runner 2049 (2017) [2160p]");
            Ok(p.Kind == "movie" && p.Title == "Blade Runner 2049" && p.Year == 2017, "movie with a number in its title: " + p.Title + " (" + p.Year + ")");
            p = Media.Parse("Some.Show.S01E02.720p.HDTV.x264");
            Ok(p.Kind == "episode" && p.Title == "Some Show" && p.Season == 1 && p.Episode == 2, "episode S01E02: " + p.Title);
            p = Media.Parse("[Grp] Another_Show - S10E11-E12 [1080p]");
            Ok(p.Kind == "episode" && p.Title == "Another Show" && p.Season == 10 && p.Episode == 11 && p.EpisodeEnd == 12, "double episode, [group] stripped: " + p.Title + " " + p.EpisodeEnd);
            p = Media.Parse("show.name.3x07.hdtv"); Ok(p.Kind == "episode" && p.Season == 3 && p.Episode == 7 && p.Title == "Show Name", "3x07 style: " + p.Title);
            p = Media.Parse("Great.Show.S02.1080p.WEB-DL"); Ok(p.Kind == "season" && p.Season == 2 && p.Title == "Great Show", "season pack: " + p.Title);
            var plan = Media.Plan("The.Matrix.1999.1080p.BluRay.x264-GROUP", new List<TFile> { new TFile("The.Matrix.1999.1080p.BluRay.x264-GROUP.mkv", 8000000000), new TFile("Sample\\sample.mkv", 50000000), new TFile("The.Matrix.1999.1080p.BluRay.x264-GROUP.en.srt", 90000), new TFile("info.nfo", 3000) }, "movie");
            Ok(plan.Count == 2 && plan[0][1] == @"The Matrix (1999)\The Matrix (1999).mkv" && plan[1][1] == @"The Matrix (1999)\The Matrix (1999).en.srt", "movie plan: Title (Year)/Title (Year).ext + subtitle, no sample / nfo: " + string.Join("; ", plan.Select(x => x[1])));
            plan = Media.Plan("Great.Show.S02.1080p.WEB-DL", new List<TFile> { new TFile("Great.Show.S02E01.1080p.mkv", 1000), new TFile("Great.Show.S02E02.1080p.mkv", 1000), new TFile("Great.Show.S02E02.1080p.srt", 10) }, "tv");
            Ok(plan.Count == 3 && plan[0][1] == @"Great Show\Season 02\Great Show - S02E01.mkv" && plan[1][1] == @"Great Show\Season 02\Great Show - S02E02.mkv" && plan[2][1] == @"Great Show\Season 02\Great Show - S02E02.srt", "TV plan: Show/Season 02/Show - S02E01.ext: " + string.Join("; ", plan.Select(x => x[1])));
            plan = Media.Plan("Album", new List<TFile> { new TFile("01 - a.flac", 10), new TFile("02 - b.flac", 10) }, "keep");
            Ok(plan.Count == 2 && plan[0][1] == @"Album\01 - a.flac", "keep: the torrent's own layout");
            Ok(Media.Safe("Who: What?") == "Who - What", "no characters Windows can't use in a name");
        }
        static void Suspicious()
        {
            var f = Media.Suspicious("Movie.2020.1080p.x264", new List<TFile> { new TFile("Movie.2020.1080p.x264.mp4.exe", 900000) });
            Ok(f.Count == 1 && f[0][1].StartsWith("Disguised"), "movie.mp4.exe is flagged as disguised");
            f = Media.Suspicious("Movie.2020.1080p.x264", new List<TFile> { new TFile("Movie.2020.mkv", 900000000), new TFile("Codec\\setup.exe", 400000) });
            Ok(f.Count == 1 && f[0][0] == "Codec\\setup.exe" && f[0][1].Contains("codec installer"), "a program inside a video download: " + (f.Count > 0 ? f[0][1] : ""));
            f = Media.Suspicious("Band - Album (2019) [FLAC]", new List<TFile> { new TFile("01.flac", 30000000), new TFile("Play.lnk", 1000) });
            Ok(f.Count == 1 && f[0][1].Contains("shortcut"), "a .lnk shortcut");
            f = Media.Suspicious("Song", new List<TFile> { new TFile("track.mp3", 5000000), new TFile("readme.vbs", 1000) });
            Ok(f.Count == 1, "a script in a music download");
            f = Media.Suspicious("Some.Linux.ISO", new List<TFile> { new TFile("distro.iso", 900000000), new TFile("README.txt", 1000) });
            Ok(f.Count == 0, "nothing flagged in an ordinary download");
            f = Media.Suspicious("Tool v2", new List<TFile> { new TFile("tool.exe", 900000) });
            Ok(f.Count == 0, "a program in a program download isn't flagged");
        }
        static void Categories()
        {
            Ok(Media.Detect("Show.S01E01", new List<TFile> { new TFile("Show.S01E01.mkv", 100) }) == "TV", "category TV");
            Ok(Media.Detect("Film 2001", new List<TFile> { new TFile("Film.2001.mkv", 100), new TFile("x.nfo", 1) }) == "Movies", "category Movies");
            Ok(Media.Detect("Album", new List<TFile> { new TFile("a.flac", 100) }) == "Music", "category Music");
            Ok(Media.Detect("stuff", new List<TFile> { new TFile("a.zip", 100) }) == "Other", "category Other");
        }
        static void Plans()
        {
            var items = new List<Plan.Item> {
                new Plan.Item { Id = "a", Queue = 0 }, new Plan.Item { Id = "b", Queue = 1 }, new Plan.Item { Id = "c", Queue = 2, UserPaused = true }, new Plan.Item { Id = "d", Queue = 3 },
                new Plan.Item { Id = "s1", Queue = 4, Complete = true, Ratio = 0.5, RatioGoal = 2 }, new Plan.Item { Id = "s2", Queue = 5, Complete = true, Ratio = 2.1, RatioGoal = 2 },
                new Plan.Item { Id = "s3", Queue = 6, Complete = true, Ratio = 3, RatioGoal = 2, NoLimit = true }, new Plan.Item { Id = "s4", Queue = 7, Complete = true, SeedMinutes = 61 } };
            var r = Plan.Running(items, 2, null, 0);
            Ok(r.SetEquals(new[] { "a", "b", "s1", "s3", "s4" }), "queue: 2 downloads at once, paused skipped, ratio limit stops s2, resumed-past-limit keeps going: " + string.Join(",", r));
            r = Plan.Running(items, 2, null, 60); Ok(!r.Contains("s4"), "seeding-time limit");
            Ok(Plan.Running(items, 2, "game", 0).Count == 0 && Plan.Running(items, 2, "vpn", 0).Count == 0, "game pause and VPN lock: nothing runs");
            items[0].Queue = 9; r = Plan.Running(items, 2, null, 0); Ok(r.Contains("b") && r.Contains("d") && !r.Contains("a"), "queue order decides");
            var sched = new List<ScheduleRule> { new ScheduleRule { From = 22, To = 6, DownKBps = 500, Days = "all" }, new ScheduleRule { From = 9, To = 17, UpKBps = 50, Days = "weekdays" } };
            Ok(Scheduler.Active(sched, new DateTime(2026, 11, 23, 23, 0, 0)).DownKBps == 500 && Scheduler.Active(sched, new DateTime(2026, 11, 24, 3, 0, 0)).DownKBps == 500, "schedule: a rule across midnight");
            Ok(Scheduler.Active(sched, new DateTime(2026, 11, 23, 10, 0, 0)).UpKBps == 50 && Scheduler.Active(sched, new DateTime(2026, 11, 22, 10, 0, 0)) == null, "schedule: weekdays only");
            Ok(Scheduler.Min(0, 300, 200) == 200 && Scheduler.Min(0, 0) == 0, "lowest limit wins, 0 = none");
            Ok(Plan.Eta(1000, 100) == "10s" && Plan.Eta(100, 0) == "\u221E" && Plan.Eta(7200 * 10, 10) == "2h 0m", "ETA text");
        }
        static AdapterInfo Ad(string id, string name, string desc, bool up, string ip) { var a = new AdapterInfo { Id = id, Name = name, Description = desc, Up = up }; if (ip != null) a.IPv4.Add(IPAddress.Parse(ip)); return a; }
        static Dictionary<string, object> H(bool connected, string extraKey = null, string extraVal = null) { var d = new Dictionary<string, object> { { "app", "VPN" }, { "connected", connected }, { "server", "nl123" }, { "country", "Netherlands" } }; if (extraKey != null) d[extraKey] = extraVal; return d; }
        static void VpnDecide()
        {
            var ads = new List<AdapterInfo> { Ad("eth", "Ethernet", "Intel(R) Ethernet", true, "192.168.1.20"), Ad("nl", "NordLynx", "NordLynx Tunnel", true, "10.5.0.2"), Ad("wg", "Wi-Fi", "Intel Wi-Fi", false, null) };
            var s = Net.Decide("hallow", null, H(true), true, ads);
            Ok(s.Ok && s.Ip.ToString() == "10.5.0.2" && s.Adapter == "NordLynx" && s.Server == "nl123", "Hallow VPN connected: bound to the VPN adapter's IP, never Ethernet: " + s.Ip);
            Ok(!Net.Decide("hallow", null, H(false), true, ads).Ok, "Hallow VPN disconnected: locked");
            Ok(!Net.Decide("hallow", null, null, null, ads).Ok, "no Hallow VPN status: locked");
            Ok(!Net.Decide("hallow", null, H(true), false, ads).Ok, "status says connected but Hallow VPN isn't running (pipe gone): locked");
            var noVpn = new List<AdapterInfo> { ads[0] };
            s = Net.Decide("hallow", null, H(true), true, noVpn); Ok(!s.Ok && s.HallowConnected && s.Message.Contains("adapter wasn't found"), "connected but no VPN adapter: locked (no binding = no torrents)");
            s = Net.Decide("hallow", null, H(true, "ip", "10.5.0.2"), true, ads); Ok(s.Ok && s.Ip.ToString() == "10.5.0.2", "Hallow VPN's own ip field");
            s = Net.Decide("hallow", null, H(true, "ip", "192.168.1.20"), true, new List<AdapterInfo> { ads[0] }); Ok(s.Ok && s.Ip.ToString() == "192.168.1.20", "an ip field naming an adapter is trusted (Hallow VPN says so)");
            s = Net.Decide("adapter", "nl", null, null, ads); Ok(s.Ok && s.Ip.ToString() == "10.5.0.2", "any VPN: the adapter you picked (NordVPN app)");
            Ok(!Net.Decide("adapter", "wg", null, null, ads).Ok, "any VPN: picked adapter down -> locked");
            Ok(!Net.Decide("adapter", null, null, null, ads).Ok && !Net.Decide("adapter", "gone", null, null, ads).Ok, "any VPN: nothing picked / adapter missing -> locked");
            Ok(Net.LooksLikeVpn("NordLynx", "NordLynx Tunnel") && Net.LooksLikeVpn("OpenVPN TAP", "TAP-NordVPN Windows Adapter V9") && Net.LooksLikeVpn("wg0", "WireGuard Tunnel") && !Net.LooksLikeVpn("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter") && !Net.LooksLikeVpn("Ethernet", "Realtek PCIe GbE"), "VPN adapter names");
        }
        static void DnsPackets()
        {
            var q = DnsVpn.Query("tracker.example.org", 0x1234);
            Ok(q.Length == 12 + 1 + 7 + 1 + 7 + 1 + 3 + 1 + 4 && q[0] == 0x12 && q[1] == 0x34, "DNS query packet");
            var resp = q.ToList(); resp[2] = 0x81; resp[3] = 0x80; resp[7] = 1; resp.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 203, 0, 113, 9 });
            var ip = DnsVpn.Answer(resp.ToArray(), resp.Count, 0x1234);
            Ok(ip != null && ip.ToString() == "203.0.113.9", "DNS answer parsed");
            Ok(DnsVpn.Answer(resp.ToArray(), resp.Count, 0x9999) == null, "DNS answer with the wrong id is ignored");
        }
        static void HandlerFake()
        {
            var r = new FakeReg(); var exe = @"C:\Users\A\AppData\Local\Programs\Hallow Torrents\HallowTorrents.exe";
            Ok(!Handler.IsRegistered(r, exe), "not registered at first");
            Handler.Register(r, exe);
            Ok(Handler.IsRegistered(r, exe) && r.Get(@"Software\Classes\magnet", "URL Protocol") == "" && r.Get(@"Software\Classes\.torrent", null) == "HallowTorrents.torrent", "registered (fake HKCU): magnet + .torrent");
            Handler.Unregister(r, exe); Ok(!Handler.IsRegistered(r, exe) && r.Get(@"Software\Classes\magnet\shell\open\command", null) == null, "unregistered");
            r = new FakeReg(); r.Set(@"Software\Classes\.torrent", null, "qBittorrent.File.Torrent"); Handler.Register(r, exe);
            Ok(r.Get(@"Software\Classes\.torrent", null) == "qBittorrent.File.Torrent" && r.Get(@"Software\Classes\.torrent\OpenWithProgids", "HallowTorrents.torrent") == "", "another app's .torrent default isn't overwritten (added to Open with)");
        }
        static void Defender()
        {
            Ok(DefenderScan.Args(@"C:\D\x") == "-Scan -ScanType 3 -File \"C:\\D\\x\" -DisableRemediation", "MpCmdRun custom scan, no remediation");
            Ok(DefenderScan.Verdict(0, "")[0] == "clean" && DefenderScan.Verdict(2, "Threat                  : Trojan:Win32/Fake")[1].Contains("Trojan:Win32/Fake"), "verdicts");
            // the stand-in MpCmdRun (tests\FakeMpCmdRun.exe): threats when the path has "evil" in it; it records its own priority
            var fake = Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "FakeMpCmdRun.exe");
            Environment.SetEnvironmentVariable("HALLOW_TORRENTS_MPCMDRUN", fake);
            var d = Path.Combine(Dir, "scan"); Directory.CreateDirectory(Path.Combine(d, "good")); Directory.CreateDirectory(Path.Combine(d, "evil"));
            var prioFile = Path.Combine(d, "prio.txt"); Environment.SetEnvironmentVariable("FAKE_MPCMDRUN_OUT", prioFile);
            var v = DefenderScan.Run(Path.Combine(d, "good")); Ok(v[0] == "clean", "stand-in scan: clean");
            Ok(File.Exists(prioFile) && File.ReadAllText(prioFile).Contains("Idle") && File.ReadAllText(prioFile).Contains("-ScanType 3"), "the scan runs at Idle priority: " + (File.Exists(prioFile) ? File.ReadAllText(prioFile) : "no file"));
            v = DefenderScan.Run(Path.Combine(d, "evil")); Ok(v[0] == "threats" && v[1].Contains("EICAR-Fake"), "stand-in scan: threat found");
        }
        static void ModeAndContrast()
        {
            var sys = new FakeSystemTheme { Light = true };
            Ok(!Mode.Resolve("follow", null, sys) && Mode.Resolve("follow", "light", sys) && Mode.Resolve("follow", "system", sys) && !Mode.Resolve("dark", "light", sys) && Mode.Resolve("light", "dark", sys), "mode: missing = dark, system = Windows, own choice wins");
            sys.Light = false; Ok(!Mode.Resolve("follow", "system", sys), "system dark");
            var pairs = new[] { new[] { Mode.LightText, Mode.LightBg }, new[] { Mode.LightText, Mode.LightPanel2 }, new[] { Mode.LightMuted, Mode.LightPanel }, new[] { Mode.LightMuted, Mode.LightBg }, new[] { Mode.TextAccent(Accent.Default), Mode.LightPanel2 },
                new[] { Mode.LightGreenText, Mode.LightPanel }, new[] { Mode.LightAmberText, Mode.LightPanel }, new[] { Mode.LightRed, Mode.LightPanel }, new[] { Mode.LilacText, Mode.LightPanel2 }, new[] { Mode.TextAccent("#8B5CF6"), Mode.LightPanel2 } };
            Ok(pairs.All(p => Mode.Ratio(p[0], p[1]) >= 4.5), "light palette text contrast >= 4.5:1");
            Ok(Mode.Ratio(Mode.FillText(Mode.LightFill(Accent.Default)), Mode.LightFill(Accent.Default)) >= 4.5, "filled accent button text >= 4.5:1");
        }

        // ---------------- the local swarm
        static string Sha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)); }
        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        class Swarm1 : IDisposable
        {
            public TrackerServer Tracker; public ITrackerListener Http, Udp; public int HttpPort, UdpPort, SeedPort; public ClientEngine Seeder; public string Root;
            public readonly List<string> Announces = new List<string>();
            public void Dispose() { try { Seeder.StopAllAsync().Wait(3000); } catch { } try { Seeder.Dispose(); } catch { } try { Http.Stop(); } catch { } try { Udp.Stop(); } catch { } try { Tracker.Dispose(); } catch { } }
        }
        static Swarm1 StartSwarm(string root, int seedUpKBps)
        {
            var w = new Swarm1 { Root = root, HttpPort = FreePort(), UdpPort = FreePort(), SeedPort = FreePort() };
            w.Tracker = new TrackerServer { AllowUnregisteredTorrents = true };
            w.Http = TrackerListenerFactory.CreateHttp(IPAddress.Loopback, w.HttpPort); w.Udp = TrackerListenerFactory.CreateUdp(IPAddress.Loopback, w.UdpPort);
            EventHandler<MonoTorrent.TrackerServer.AnnounceRequest> h = (s, e) => { lock (w.Announces) w.Announces.Add(e.RemoteAddress + ":" + e.Port); };
            w.Http.AnnounceReceived += h; w.Udp.AnnounceReceived += h;
            w.Tracker.RegisterListener(w.Http); w.Tracker.RegisterListener(w.Udp); w.Http.Start(); w.Udp.Start();
            var b = new EngineSettingsBuilder { AllowPortForwarding = false, AllowLocalPeerDiscovery = false, AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false, CacheDirectory = Path.Combine(root, "seedcache"), DhtEndPoint = null, MaximumUploadRate = seedUpKBps * 1024 };
            b.ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Loopback, w.SeedPort) } };
            w.Seeder = new ClientEngine(b.ToSettings());
            return w;
        }
        // random files + a .torrent for them; the seeder has the data
        static async Task<string> MakeTorrent(Swarm1 w, string name, int[] sizes, string announce)
        {
            var src = Path.Combine(w.Root, "seed", name); Directory.CreateDirectory(src); var rnd = new Random(name.GetHashCode());
            for (int i = 0; i < sizes.Length; i++) { var data = new byte[sizes[i]]; rnd.NextBytes(data); File.WriteAllBytes(Path.Combine(src, "file" + i + ".bin"), data); }
            File.WriteAllText(Path.Combine(src, "readme.txt"), "test " + name);   // always 2+ files: a folder torrent
            var c = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 }; c.Announces.Add(new List<string> { announce });
            var tf = Path.Combine(w.Root, name + ".torrent"); await c.CreateAsync(new TorrentFileSource(src), tf);
            var seedTf = tf;
            if (announce.Contains(".invalid")) { var c2 = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 }; c2.Announces.Add(new List<string> { "http://127.0.0.1:" + w.HttpPort + "/announce" }); seedTf = tf + ".seed"; await c2.CreateAsync(new TorrentFileSource(src), seedTf); }   // the seeder can't look the test name up: same torrent, tracker by IP
            var t = await Torrent.LoadAsync(seedTf);
            var settings = new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false }.ToSettings();
            var m = await w.Seeder.AddAsync(t, Path.Combine(w.Root, "seed"), settings); await m.HashCheckAsync(false); await m.StartAsync();
            return tf;
        }
        static TorSettings TestSettings(int port, string save) { var s = new TorSettings { Dht = false, Pex = false, Lpd = false, Port = port, SaveFolder = save, ScanDone = false, RatioLimit = 0, MaxActive = 3 }; s.Fix(); return s; }
        static VpnState Vpn(IPAddress ip) { var v = new VpnState { Ok = true, Ip = ip, Adapter = "Fake VPN", AdapterId = "fake" }; v.Dns.Add(IPAddress.Loopback); return v; }
        // the fake adapter file is read by the watch every 250 ms: write it with a few retries (a sharing clash is a test race, not a bug)
        static void WriteRetry(string f, string text) { for (int i = 0; ; i++) { try { File.WriteAllText(f, text); return; } catch (IOException) { if (i >= 40) throw; Thread.Sleep(25); } } }
        static bool Wait(Func<bool> c, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (c()) return true; Thread.Sleep(50); } return c(); }
        static List<TcpConnectionInformation> Conns(int port) { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Where(c => c.State == TcpState.Established && (c.RemoteEndPoint.Port == port || c.LocalEndPoint.Port == port)).ToList(); }

        static async Task Swarm()
        {
            var root = Path.Combine(Dir, "swarm"); Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("HALLOW_TORRENTS_MPCMDRUN", Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "FakeMpCmdRun.exe"));
            using (var w = StartSwarm(root, 0))
            {
                Net.AuditOn = true; string a; while (Net.Audit.TryDequeue(out a)) { }
                var tf = await MakeTorrent(w, "Pack", new[] { 3 * 1024 * 1024 + 77, 300000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                int port = FreePort(); var save = Path.Combine(root, "dl");
                var s = TestSettings(port, save);
                var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>(); core.Log += m => { lock (log) log.Add(m); };
                string err; var e = core.AddTorrentFile(tf, null, null, null, true, out err);
                Ok(e != null && err == null && core.Get(e.Id).Files.Count == 3, "add a .torrent file (" + err + ")");
                Ok(core.Snapshot(false)[0].State == "Waiting for VPN" && !core.EngineUp, "added while the VPN is off: it waits, no engine");
                await core.SetVpn(Vpn(VpnIp));
                Ok(core.EngineUp && core.BoundIp.Equals(VpnIp), "VPN up: engine bound to " + core.BoundIp);
                var ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 300);
                bool done = Wait(() => core.Get(e.Id).CompletedAt != null, 30000);
                Ok(done, "downloaded from the local seeder");
                Ok(done && Sha(Path.Combine(save, "Pack", "file0.bin")) == Sha(Path.Combine(root, "seed", "Pack", "file0.bin")) && Sha(Path.Combine(save, "Pack", "file1.bin")) == Sha(Path.Combine(root, "seed", "Pack", "file1.bin")), "the files' hashes match the seeder's");
                // leak checks: the tracker only ever saw the VPN address from us; every socket we opened was bound to it
                List<string> ann; lock (w.Announces) ann = w.Announces.ToList();
                Ok(ann.Any(x => x == "127.0.0.3:" + port) && !ann.Any(x => x.EndsWith(":" + port) && !x.StartsWith("127.0.0.3:")), "tracker saw our announces only from the VPN address: " + string.Join(", ", ann.Distinct()));
                var audit = Net.Audit.ToArray();
                Ok(audit.Length > 0 && audit.All(x => x.Contains(" 127.0.0.3:")), "every socket the engine opened was bound to the VPN IP (" + audit.Length + ": " + string.Join(", ", audit.Select(x => x.Split(' ')[0]).Distinct()) + ")");
                var conns = Conns(w.SeedPort).Where(c => c.RemoteEndPoint.Port == w.SeedPort || c.LocalEndPoint.Port == port).ToList();
                var ours = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Where(c => c.LocalEndPoint.Port == port || (c.RemoteEndPoint.Port == w.SeedPort && !c.LocalEndPoint.Address.Equals(IPAddress.Loopback))).ToList();
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(l => l.Port == port).ToList();
                Ok(listeners.Count == 1 && listeners[0].Address.Equals(VpnIp), "the peer listener is bound to the VPN IP only: " + string.Join(",", listeners.Select(l => l.ToString())));

                // pause / resume and the speed limit: a slower second torrent (the seeder uploads at most 300 KB/s)
                var tf2 = await MakeTorrent(w, "Big", new[] { 12 * 1024 * 1024 }, "udp://127.0.0.1:" + w.UdpPort + "/announce");
                await w.Seeder.UpdateSettingsAsync(new EngineSettingsBuilder(w.Seeder.Settings) { MaximumUploadRate = 200 * 1024 }.ToSettings());
                lock (w.Announces) w.Announces.Clear();
                var e2 = core.AddTorrentFile(tf2, null, "Other", null, true, out err);
                Ok(Wait(() => core.Snapshot(false).First(v => v.Id == e2.Id).Progress > 3, 15000), "UDP tracker torrent starts (our own bound UDP tracker client)");
                lock (w.Announces) ann = w.Announces.ToList();
                Ok(ann.Any(x => x == "127.0.0.3:" + port) && ann.All(x => !x.EndsWith(":" + port) || x.StartsWith("127.0.0.3:")), "UDP tracker saw only the VPN address: " + string.Join(", ", ann.Distinct()));
                core.Pause(e2.Id);
                Ok(Wait(() => core.ManagerForTests(e2.Id).State == TorrentState.Stopped, 5000), "pause: stopped");
                var p1 = core.Snapshot(false).First(v => v.Id == e2.Id).Progress; Thread.Sleep(1500); var p2 = core.Snapshot(false).First(v => v.Id == e2.Id).Progress;
                Ok(Math.Abs(p2 - p1) < 0.01 && core.Snapshot(false).First(v => v.Id == e2.Id).State == "Paused", "paused: no progress (" + p1.ToString("0.0") + "% -> " + p2.ToString("0.0") + "%)");
                core.LimitDown = 100; core.Resume(e2.Id);
                Ok(Wait(() => core.ManagerForTests(e2.Id).State == TorrentState.Downloading, 5000), "resume: downloading again");
                Thread.Sleep(1500); var d0 = core.Get(e2.Id).Down; var sw = Stopwatch.StartNew(); Thread.Sleep(4000); var rate = (core.Get(e2.Id).Down - d0) / sw.Elapsed.TotalSeconds / 1024;
                Ok(rate > 20 && rate < 160, "download limit 100 KB/s: measured " + rate.ToString("0") + " KB/s");
                // game mode: pause everything while a (fake) game runs, back afterwards
                core.LimitDown = 0; core.GameHold = true; core.Kick();
                Ok(Wait(() => core.ManagerForTests(e2.Id).State == TorrentState.Stopped, 4000) && core.Snapshot(false).First(v => v.Id == e2.Id).State == "Paused for game", "game running: paused for the game");
                core.GameHold = false; core.Kick();
                Ok(Wait(() => core.ManagerForTests(e2.Id).State == TorrentState.Downloading || core.ManagerForTests(e2.Id).State == TorrentState.Seeding, 4000), "game closed: running again");

                // the VPN drops: everything stops within 1 s, no connection from the VPN address stays open
                var watchFile = Path.Combine(root, "adapters.txt");
                WriteRetry(watchFile, "fake|Fake VPN|WireGuard Tunnel|up|127.0.0.3|127.0.0.1\n");
                var ts = new TorSettings { VpnSource = "adapter", VpnAdapter = "fake" };
                var watch = new VpnWatch(() => ts, new FakeNetworks(watchFile), false); Stopwatch dropClock = null; long stoppedMs = -1;
                watch.Changed += (o, n) => { if (!n.Ok && dropClock != null) { core.SetVpn(n).Wait(); stoppedMs = dropClock.ElapsedMilliseconds; } };
                watch.Start(); Thread.Sleep(600);
                Ok(watch.Current.Ok && watch.Current.Ip.Equals(VpnIp), "watch sees the fake VPN adapter up");
                Ok(Conns(w.SeedPort).Any(c => c.LocalEndPoint.Address.Equals(VpnIp) || c.RemoteEndPoint.Address.Equals(VpnIp)), "connections over the VPN address before the drop");
                dropClock = Stopwatch.StartNew(); WriteRetry(watchFile, "fake|Fake VPN|WireGuard Tunnel|down||\n");
                Ok(Wait(() => stoppedMs >= 0, 3000) && stoppedMs < 1000, "VPN drop -> engine stopped in " + stoppedMs + " ms (< 1 s)");
                Ok(!core.EngineUp && core.Snapshot(false).All(v => v.State == "Waiting for VPN" || v.State == "Paused"), "everything waits for the VPN");
                Ok(Wait(() => !Conns(w.SeedPort).Any(c => c.LocalEndPoint.Address.Equals(VpnIp) || c.RemoteEndPoint.Address.Equals(VpnIp)), 1500) && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().All(l => l.Port != port), "no connection and no listener left");
                var downAtDrop = core.Get(e2.Id).Down; Thread.Sleep(1500);
                Ok(core.Get(e2.Id).Down == downAtDrop, "no data moves while the VPN is down");
                lock (w.Announces) w.Announces.Clear();
                // back up -> rebuilt and bound again
                watch.Changed += (o, n) => { if (n.Ok) core.SetVpn(n).Wait(); };
                WriteRetry(watchFile, "fake|Fake VPN|WireGuard Tunnel|up|127.0.0.3|127.0.0.1\n");
                Ok(Wait(() => core.EngineUp, 3000) && core.BoundIp.Equals(VpnIp), "VPN back: engine rebuilt and bound again");
                await w.Seeder.UpdateSettingsAsync(new EngineSettingsBuilder(w.Seeder.Settings) { MaximumUploadRate = 0 }.ToSettings());
                Ok(Wait(() => core.Get(e2.Id).CompletedAt != null, 40000), "the paused-by-VPN download finishes after the VPN is back");
                // the VPN's IP changes -> rebuilt on the new address
                WriteRetry(watchFile, "fake|Fake VPN|WireGuard Tunnel|up|127.0.0.4|127.0.0.1\n");
                Ok(Wait(() => core.EngineUp && core.BoundIp != null && core.BoundIp.ToString() == "127.0.0.4", 3000), "VPN IP changed: engine stopped and rebuilt on the new address");
                watch.Dispose(); ticker.Dispose();

                // an address that isn't on this PC: nothing gets out (bind fails, no announce, no download)
                var tf3 = await MakeTorrent(w, "Nope", new[] { 500000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                int port3 = FreePort(); var s3 = TestSettings(port3, Path.Combine(root, "dl3"));
                var core3 = new Core(Path.Combine(root, "core3"), () => s3); core3.AddTorrentFile(tf3, null, null, null, true, out err);
                lock (w.Announces) w.Announces.Clear();
                try { await core3.SetVpn(Vpn(IPAddress.Parse("10.254.254.77"))); } catch { }
                var t3 = new Timer(_ => { try { core3.Tick().Wait(); } catch { } }, null, 0, 300);
                Thread.Sleep(4000);
                lock (w.Announces) ann = w.Announces.ToList();
                Ok(!ann.Any(x => x.EndsWith(":" + port3)) && core3.Snapshot(false)[0].Progress == 0, "bound to an address that isn't there: no announce, nothing downloaded");
                t3.Dispose(); core3.Dispose();

                // a tracker by NAME: looked up over the VPN (our own DNS from the bound socket), never Windows' resolver
                var dnsSeen = new List<string>(); var dnsPort = FreePort();
                var dnsSrv = new UdpClient(new IPEndPoint(IPAddress.Loopback, dnsPort)); bool dnsStop = false;
                new Thread(() => { while (!dnsStop) { try { IPEndPoint from = null; var q = dnsSrv.Receive(ref from); lock (dnsSeen) dnsSeen.Add(from.Address.ToString()); var r = q.ToList(); r[2] = 0x81; r[3] = 0x80; r[7] = 1; r.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 127, 0, 0, 1 }); dnsSrv.Send(r.ToArray(), r.Count, from); } catch { } } }) { IsBackground = true }.Start();
                DnsVpn.Port = dnsPort;
                var tf4 = await MakeTorrent(w, "Named", new[] { 400000 }, "http://tracker.hallow-test.invalid:" + w.HttpPort + "/announce");
                var e4 = core.AddTorrentFile(tf4, null, null, null, true, out err);
                var t4 = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 300);
                Ok(Wait(() => core.Get(e4.Id).CompletedAt != null, 20000), "tracker by host name works through the VPN DNS lookup");
                lock (dnsSeen) Ok(dnsSeen.Count > 0 && dnsSeen.All(x => x == "127.0.0.4"), "the DNS query came from the VPN address only: " + string.Join(",", dnsSeen.Distinct()));
                var boot = VpnDht.Bootstrap(IPAddress.Parse("127.0.0.4"), new List<IPAddress> { IPAddress.Loopback });
                Ok(boot.Length == 26 * VpnDht.Routers.Length, "DHT bootstrap routers looked up over the VPN (no system resolver): " + boot.Length + " bytes");
                lock (dnsSeen) Ok(dnsSeen.All(x => x == "127.0.0.4"), "...from the VPN address only");
                dnsStop = true; dnsSrv.Close(); DnsVpn.Port = 53;

                // a magnet link (metadata from the seeder through the tracker)
                var tf5 = await MakeTorrent(w, "Mag", new[] { 700000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                var t5 = Torrent.Load(tf5);
                var magnet = "magnet:?xt=urn:btih:" + t5.InfoHashes.V1.ToHex() + "&dn=Mag&tr=" + Uri.EscapeDataString("http://127.0.0.1:" + w.HttpPort + "/announce");
                var e5 = core.AddMagnet(magnet, null, null, true, out err);
                Ok(e5 != null && e5.Name == "Mag", "add a magnet link");
                Ok(Wait(() => core.Get(e5.Id).CompletedAt != null, 30000) && core.Get(e5.Id).TorrentFile != null && core.Get(e5.Id).Files.Count == 2, "magnet: metadata fetched, downloaded, .torrent kept");
                Ok(Sha(Path.Combine(save, "Mag", "file0.bin")) == Sha(Path.Combine(root, "seed", "Mag", "file0.bin")), "magnet download hash matches");

                // finished-download steps on a real finished torrent: flags, the stand-in Defender, the library hand-off (temp folders)
                var lib = Path.Combine(root, "library", "TV");
                s.ScanDone = true; s.Cat("TV").Handoff = true; s.Cat("TV").LibraryFolder = lib;
                var tf6 = await MakeTorrent(w, "Good.Show.S01E02.720p", new[] { 300000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                var src6 = Path.Combine(root, "seed", "Good.Show.S01E02.720p"); File.Move(Path.Combine(src6, "file0.bin"), Path.Combine(src6, "Good.Show.S01E02.720p.mkv"));
                var c6 = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 }; c6.Announces.Add(new List<string> { "http://127.0.0.1:" + w.HttpPort + "/announce" });
                await c6.CreateAsync(new TorrentFileSource(src6), tf6);
                var m6 = await w.Seeder.AddAsync(await Torrent.LoadAsync(tf6), Path.Combine(root, "seed"), new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false }.ToSettings()); await m6.HashCheckAsync(false); await m6.StartAsync();
                Entry done6 = null; core.Completed += x => { if (x.Name.StartsWith("Good.Show")) done6 = x; };
                var e6 = core.AddTorrentFile(tf6, null, null, null, true, out err);
                Ok(e6 != null && e6.Category == "TV", "category detected: TV");
                Ok(Wait(() => done6 != null, 20000), "episode downloaded");
                if (done6 != null)
                {
                    await Finish.Run(core, done6, s, null);
                    Ok(done6.Scan == "clean", "finished download scanned by the stand-in Defender: " + done6.ScanText);
                    var target = Path.Combine(lib, "Good Show", "Season 01", "Good Show - S01E02.mkv");
                    Ok(File.Exists(target) && Sha(target) == Sha(Path.Combine(src6, "Good.Show.S01E02.720p.mkv")), "copied to the library as Good Show/Season 01/Good Show - S01E02.mkv: " + done6.Handoff);
                    Ok(File.Exists(Path.Combine(save, "Good.Show.S01E02.720p", "Good.Show.S01E02.720p.mkv")), "copy mode: the original stays (keeps seeding)");
                    // move mode: renamed in place for the library, the engine keeps seeding from there
                    s.Cat("TV").Move = true; File.Delete(target);
                    var r6 = await Finish.Send(core, done6, s.Cat("TV"));
                    Ok(File.Exists(target) && !File.Exists(Path.Combine(save, "Good.Show.S01E02.720p", "Good.Show.S01E02.720p.mkv")), "move mode: moved into the library: " + r6);
                    Ok(Wait(() => core.ManagerForTests(done6.Id).State == TorrentState.Seeding, 8000) && core.ManagerForTests(done6.Id).Files[0].FullPath == target, "move mode: still seeding from the library");
                }
                // a flagged download is held, not handed off
                var held = new Entry { Id = "x", Name = "Movie.2020.1080p", Category = "TV", Flags = new List<string> { "a.mp4.exe|Disguised" }, Files = new List<string> { "a.mp4.exe|10" } };
                await Finish.Handoff(core, held, s); Ok(held.Handoff != null && held.Handoff.StartsWith("Held"), "suspicious download: hand-off held");

                // remove (files stay unless asked)
                var rm = await core.Remove(e5.Id, false);
                Ok(rm == null && core.Get(e5.Id) == null && File.Exists(Path.Combine(save, "Mag", "file0.bin")), "remove: gone from the list, files kept");
                t4.Dispose(); core.Dispose();
                Ok(!core.EngineUp, "quit: engine stopped");
                Net.AuditOn = false;
            }
        }
    }
}
