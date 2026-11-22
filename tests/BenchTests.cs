// Hallow Torrents 1.5 - the local speed benchmark. Everything runs on THIS PC only, on loopback addresses:
//   * a tracker on 127.0.0.1 (MonoTorrent's own) for the swarm, N seeders on 127.0.0.11.., M other leechers on 127.0.0.21.. (plain
//     MonoTorrent engines, started fresh for every run so they compete for the seeders like real peers);
//   * Hallow Torrents' own Core in a CHILD process (`TorrentsTests.exe bench-client ...`) bound to the stand-in VPN address 127.0.0.3,
//     so its CPU time, memory and disk writes are its own. It gets a tracker-less copy of the torrent and is handed the peers directly;
//   * "wan" adds a delay relay in front of every peer (round-trip latency) and caps every peer's upload, like peers on the internet.
// It measures time to completion, average / peak throughput, CPU, peak memory, bytes written and the full piece-verification time
// (a recheck of the finished download) for each performance profile ("v14" = exactly 1.4's settings). Runs at BelowNormal priority and
// only while neither game runs (a run is thrown away and repeated when one starts). Generated data in %TEMP%, deleted afterwards.
//   TorrentsTests.exe bench [--big 2048] [--files 2000] [--runs 2] [--profiles v14,balanced,max] [--scen lan,wan] [--latency 40]
//                           [--cap 6144] [--seeders 4] [--leechers 2] [--out <file without extension>]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;

namespace HallowTorrents.Tests
{
    public class BenchResult
    {
        public string Profile, Torrent, Scenario; public int Run; public bool Ok; public string Error;
        public long Bytes; public double Seconds, AvgMBps, PeakMBps, CpuSeconds, CpuPctOneCore, VerifySeconds, FinishSeconds; public long PeakMB, WrittenMB;
    }

    public static class Bench
    {
        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
        public static readonly string[] Games = { "DeltaForceClient-Win64-Shipping", "Discovery-d" };
        public static bool GameRunning() { foreach (var g in Games) { var p = Process.GetProcessesByName(g); var any = p.Length > 0; foreach (var x in p) x.Dispose(); if (any) return true; } return false; }
        static void Say(string s) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + s); }
        static string Arg(string[] a, string n, string d) { var i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
        static int FreePort(IPAddress ip) { var l = new TcpListener(ip, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        // ---------------- the child: Hallow Torrents' Core, measured from the inside
        public static int Client(string[] a)
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var profile = Arg(a, "--profile", "balanced"); var torrent = Arg(a, "--torrent", null); var save = Arg(a, "--save", null);
            var peers = Arg(a, "--peers", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); var ip = IPAddress.Parse(Arg(a, "--ip", "127.0.0.3"));
            int timeout = int.Parse(Arg(a, "--timeout", "900"));
            var dir = Path.Combine(Path.GetDirectoryName(save), "core-" + Path.GetFileName(save));
            var s = new TorSettings { Dht = false, Pex = false, Lpd = false, Port = FreePort(ip), SaveFolder = save, ScanDone = false, RatioLimit = 0, MaxActive = 3 }; s.Fix();
            s.Perf = profile;   // after Fix: "v14" is the benchmark's 1.4 baseline
            int ov; if (int.TryParse(Environment.GetEnvironmentVariable("HALLOW_BENCH_OPENFILES"), out ov)) s.PerfOpenFiles = ov;   // experiments
            if (int.TryParse(Environment.GetEnvironmentVariable("HALLOW_BENCH_CACHE"), out ov)) Core.BenchCacheBytes = ov;
            var enc = Environment.GetEnvironmentVariable("HALLOW_BENCH_ENC");   // experiments: plain / header / full / mt (MonoTorrent's default order)
            if (enc == "plain") Core.BenchEncryption = new List<EncryptionType> { EncryptionType.PlainText };
            else if (enc == "header") Core.BenchEncryption = new List<EncryptionType> { EncryptionType.RC4Header, EncryptionType.PlainText };
            else if (enc == "full") Core.BenchEncryption = new List<EncryptionType> { EncryptionType.RC4Full };
            else if (enc == "mt") Core.BenchEncryption = new List<EncryptionType> { EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText };
            var core = new Core(dir, () => s);
            string err; var e = core.AddTorrentFile(torrent, null, null, null, true, out err);
            if (e == null) { Console.WriteLine("RESULT error " + err); return 2; }
            core.SetVpn(new VpnState { Ok = true, Ip = ip, Adapter = "bench", AdapterId = "bench" }).Wait();
            var me = Process.GetCurrentProcess();
            TorrentManager m = null; var sw = Stopwatch.StartNew();
            while (m == null && sw.ElapsedMilliseconds < 10000) { m = core.ManagerForTests(e.Id); if (m == null) Thread.Sleep(20); }
            if (m == null) { Console.WriteLine("RESULT error no manager"); return 2; }
            var cpu0 = me.TotalProcessorTime; IoCounters io0; GetProcessIoCounters(me.Handle, out io0);
            m.AddPeersAsync(peers.Select(p => new PeerInfo(new Uri("ipv4://" + p)))).Wait();
            var ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 1000);   // the app ticks every second
            sw.Restart(); long lastBytes = 0; double peak = 0; var window = new Queue<KeyValuePair<long, long>>(); double downloaded = -1;
            while (sw.Elapsed.TotalSeconds < timeout)
            {
                Thread.Sleep(250);
                long got = m.Monitor.DataBytesReceived; long t = sw.ElapsedMilliseconds;
                window.Enqueue(new KeyValuePair<long, long>(t, got)); while (window.Count > 2 && t - window.ElementAt(1).Key >= 1000) window.Dequeue();
                var first = window.Peek(); if (t - first.Key >= 1000) peak = Math.Max(peak, (got - first.Value) / ((t - first.Key) / 1000.0));
                if (got != lastBytes) { Console.WriteLine("T " + t + " " + got); lastBytes = got; }
                if (t % 5000 < 250 && Environment.GetEnvironmentVariable("HALLOW_BENCH_DEBUG") != null) Console.Error.WriteLine("dbg " + t + " state " + m.State + " progress " + m.Progress.ToString("0.0") + " open " + m.OpenConnections + " avail " + m.Peers.Available + " seeds " + m.Peers.Seeds + " leechs " + m.Peers.Leechs);
                if (downloaded < 0 && m.Complete) downloaded = sw.Elapsed.TotalSeconds;   // every piece in (the engine's view)
                if (core.Get(e.Id).CompletedAt != null) break;   // Core's "finished" (1.5: after its own full recheck)
            }
            var finish = sw.Elapsed.TotalSeconds; var secs = downloaded > 0 ? downloaded : finish; bool ok = core.Get(e.Id).CompletedAt != null;
            if (core.Repaired > 0) Console.WriteLine("why repaired " + core.Repaired + " bad piece(s) found by the recheck on completion");
            var cpu = (me.TotalProcessorTime - cpu0).TotalSeconds; me.Refresh(); var peakMb = me.PeakWorkingSet64 / (1024 * 1024);
            IoCounters io1; GetProcessIoCounters(me.Handle, out io1);
            ticker.Dispose(); var size = m.Torrent.Size;
            // piece verification: Hallow's engine is shut down (everything flushed and closed), then a fresh engine rechecks every piece
            // of what's on disk - the verification time, and proof the files are right
            double verify = -1; string why = null;
            if (ok)
            {
                try
                {
                    Thread.Sleep(1200); core.Dispose();
                    var vb = new EngineSettingsBuilder { AllowPortForwarding = false, AllowLocalPeerDiscovery = false, AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false, CacheDirectory = Path.Combine(dir, "verify"), DhtEndPoint = null };
                    vb.ListenEndPoints = new Dictionary<string, IPEndPoint>();
                    using (var ve = new ClientEngine(vb.ToSettings(), Factories.Default.WithPortForwarderCreator(() => new NoPortForwarder())))
                    {
                        var vm = ve.AddAsync(Torrent.Load(torrent), save).Result;
                        var vs = Stopwatch.StartNew(); vm.HashCheckAsync(false).Wait(); verify = vs.Elapsed.TotalSeconds;
                        ok = vm.Complete; if (!ok) why = "recheck: " + vm.Bitfield.TrueCount + " of " + vm.Bitfield.Length + " pieces good";
                        if (!ok && Environment.GetEnvironmentVariable("HALLOW_BENCH_DEBUG") != null)
                        {
                            Thread.Sleep(4000); vm.HashCheckAsync(false).Wait(); why += "; again 4 s later: " + vm.Bitfield.TrueCount;
                            var bad = Enumerable.Range(0, vm.Bitfield.Length).Where(i => !vm.Bitfield[i]).Take(3).ToList();
                            foreach (var b in bad) { var fs = vm.Files.Where(f => f.StartPieceIndex <= b && f.EndPieceIndex >= b).Select(f => f.Path + "(" + f.Length + ")"); why += "; piece " + b + ": " + string.Join(",", fs.Take(4)); }
                            // byte compare with the seeders' source files
                            var seedRoot = Path.Combine(Path.GetDirectoryName(save), "seed");
                            foreach (var b in bad.Take(1))
                                foreach (var f in vm.Files.Where(f => f.StartPieceIndex <= b && f.EndPieceIndex >= b))
                                {
                                    var mine = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null; var src = Path.Combine(seedRoot, vm.Torrent.Name, f.Path);
                                    var orig = File.Exists(src) ? File.ReadAllBytes(src) : null;
                                    if (mine == null || orig == null) { why += "; " + f.Path + " missing (" + (mine == null) + "/" + (orig == null) + ")"; continue; }
                                    int first = -1, count = 0; for (int i = 0; i < Math.Min(mine.Length, orig.Length); i++) if (mine[i] != orig[i]) { if (first < 0) first = i; count++; }
                                    why += "; " + f.Path + " len " + mine.Length + "/" + orig.Length + (first < 0 ? " same" : " differs at " + first + " (" + count + " bytes, zero? " + (mine[first] == 0) + ")");
                                }
                        }
                    }
                }
                catch (Exception ex) { why = "verify: " + ex.Message; ok = false; }
            }
            else why = "not complete after " + timeout + " s (" + m.Progress.ToString("0.0") + "%)";
            if (why != null) Console.WriteLine("why " + why);
            var r = new BenchResult { Profile = profile, Ok = ok, Bytes = size, FinishSeconds = finish, Seconds = secs, AvgMBps = m.Torrent.Size / secs / 1048576.0, PeakMBps = peak / 1048576.0, CpuSeconds = cpu, CpuPctOneCore = cpu / finish * 100, PeakMB = peakMb, WrittenMB = (long)((io1.WriteBytes - io0.WriteBytes) / 1048576), VerifySeconds = verify };
            Console.WriteLine("RESULT " + Line(r));
            try { core.Dispose(); } catch { }   // (already disposed after a finished download)
            return ok ? 0 : 1;
        }
        static string Line(BenchResult r)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join(";", r.Ok ? "ok" : "fail", r.Bytes.ToString(c), r.Seconds.ToString("0.000", c), r.AvgMBps.ToString("0.00", c), r.PeakMBps.ToString("0.00", c), r.CpuSeconds.ToString("0.00", c), r.CpuPctOneCore.ToString("0.0", c), r.PeakMB.ToString(c), r.WrittenMB.ToString(c), r.VerifySeconds.ToString("0.000", c), r.FinishSeconds.ToString("0.000", c));
        }
        public static BenchResult Parse(string line)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (line == null || !line.StartsWith("RESULT ")) return null;
            var p = line.Substring(7).Split(';'); if (p.Length < 10) return new BenchResult { Ok = false, Error = line.Substring(7) };
            return new BenchResult { Ok = p[0] == "ok", Bytes = long.Parse(p[1], c), Seconds = double.Parse(p[2], c), AvgMBps = double.Parse(p[3], c), PeakMBps = double.Parse(p[4], c), CpuSeconds = double.Parse(p[5], c), CpuPctOneCore = double.Parse(p[6], c), PeakMB = long.Parse(p[7], c), WrittenMB = long.Parse(p[8], c), VerifySeconds = double.Parse(p[9], c), FinishSeconds = p.Length > 10 ? double.Parse(p[10], c) : 0 };
        }

        // ---------------- a delay relay: every byte waits `oneWayMs` in each direction (round trip = 2 x oneWayMs)
        public class Relay : IDisposable
        {
            readonly TcpListener l; readonly IPEndPoint target; readonly int delay; volatile bool stop; readonly List<Socket> open = new List<Socket>();
            public IPEndPoint EndPoint { get { return (IPEndPoint)l.LocalEndpoint; } }
            public Relay(IPAddress ip, IPEndPoint target, int oneWayMs)
            {
                this.target = target; delay = oneWayMs; l = new TcpListener(ip, 0); l.Start();
                new Thread(Accept) { IsBackground = true, Name = "relay" }.Start();
            }
            void Accept()
            {
                while (!stop)
                {
                    Socket a; try { a = l.AcceptSocket(); } catch { return; }
                    var b = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try { b.Connect(target); } catch { try { a.Close(); } catch { } continue; }
                    a.NoDelay = true; b.NoDelay = true;
                    lock (open) { open.Add(a); open.Add(b); }
                    Pump(a, b); Pump(b, a);
                }
            }
            void Pump(Socket from, Socket to)
            {
                var q = new System.Collections.Concurrent.BlockingCollection<KeyValuePair<long, byte[]>>(); var clock = Stopwatch.StartNew();
                new Thread(() =>
                {
                    var buf = new byte[64 * 1024];
                    try { while (true) { int n = from.Receive(buf); if (n <= 0) break; var c = new byte[n]; Buffer.BlockCopy(buf, 0, c, 0, n); q.Add(new KeyValuePair<long, byte[]>(clock.ElapsedMilliseconds + delay, c)); } } catch { }
                    q.CompleteAdding();
                }) { IsBackground = true }.Start();
                new Thread(() =>
                {
                    try
                    {
                        foreach (var item in q.GetConsumingEnumerable())
                        {
                            var wait = item.Key - clock.ElapsedMilliseconds; if (wait > 0) Thread.Sleep((int)wait);
                            to.Send(item.Value);
                        }
                    }
                    catch { }
                    try { to.Shutdown(SocketShutdown.Send); } catch { }
                }) { IsBackground = true }.Start();
            }
            public void Dispose() { stop = true; try { l.Stop(); } catch { } lock (open) foreach (var s in open) try { s.Close(); } catch { } }
        }

        // ---------------- the orchestrator
        class Peer { public ClientEngine Engine; public IPEndPoint Ep; public Relay Relay; public string Dir; }
        static ClientEngine Engine(IPAddress ip, int port, string cache, int upKBps, bool acceptOnly)
        {
            var b = new EngineSettingsBuilder { AllowPortForwarding = false, AllowLocalPeerDiscovery = false, AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false, CacheDirectory = cache, DhtEndPoint = null, MaximumUploadRate = upKBps * 1024 };
            b.ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(ip, port) } };
            var f = Factories.Default.WithPortForwarderCreator(() => new NoPortForwarder());
            if (acceptOnly) f = f.WithPeerConnectionCreator("ipv4", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new RefusingConnector()));
            return new ClientEngine(b.ToSettings(), f);
        }
        static void GenerateBig(string file, long bytes)
        {
            var buf = new byte[4 * 1024 * 1024];
            using (var f = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                for (long done = 0, i = 0; done < bytes; i++) { new Random(1000 + (int)i).NextBytes(buf); var n = (int)Math.Min(buf.Length, bytes - done); f.Write(buf, 0, n); done += n; }
        }
        static void GenerateSmall(string dir, int count)
        {
            var rnd = new Random(77);
            for (int i = 0; i < count; i++)
            {
                var sub = Path.Combine(dir, "folder" + (i / 100).ToString("00")); Directory.CreateDirectory(sub);
                var data = new byte[rnd.Next(4 * 1024, 256 * 1024)]; rnd.NextBytes(data); File.WriteAllBytes(Path.Combine(sub, "file" + i.ToString("0000") + ".bin"), data);
            }
        }
        static async Task<string[]> MakeTorrent(string src, string outDir, string name, int pieceKB, string announce)
        {
            var c = new TorrentCreator(TorrentType.V1Only) { PieceLength = pieceKB * 1024 }; c.Announces.Add(new List<string> { announce });
            var tf = Path.Combine(outDir, name + ".torrent"); await c.CreateAsync(new TorrentFileSource(src), tf);
            // the client's copy: the same info dictionary (same info hash) without trackers - it's handed its peers directly
            var d = (BEncodedDictionary)BEncodedValue.Decode(File.ReadAllBytes(tf)); d.Remove(new BEncodedString("announce")); d.Remove(new BEncodedString("announce-list"));
            var cf = Path.Combine(outDir, name + ".client.torrent"); File.WriteAllBytes(cf, d.Encode());
            return new[] { tf, cf };
        }
        static void WaitNoGame(int freeSeconds)
        {
            bool said = false; DateTime? free = null;
            while (true)
            {
                if (GameRunning()) { if (!said) { Say("a game is running - the benchmark waits"); said = true; } free = null; }
                else if (free == null) free = DateTime.UtcNow;
                if (free != null && (!said || (DateTime.UtcNow - free.Value).TotalSeconds >= freeSeconds)) return;
                Thread.Sleep(1000);
            }
        }

        public static int Orchestrate(string[] a, List<BenchResult> into = null)
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            int bigMb = int.Parse(Arg(a, "--big", "2048")), files = int.Parse(Arg(a, "--files", "2000")), runs = int.Parse(Arg(a, "--runs", "2"));
            int latency = int.Parse(Arg(a, "--latency", "40")), cap = int.Parse(Arg(a, "--cap", "6144")), nSeed = int.Parse(Arg(a, "--seeders", "4")), nLeech = int.Parse(Arg(a, "--leechers", "2"));
            int nCrowd = int.Parse(Arg(a, "--crowd", "80")), crowdCap = int.Parse(Arg(a, "--crowdcap", "320"));
            var profiles = Arg(a, "--profiles", "v14,balanced,max").Split(','); var scens = Arg(a, "--scen", "lan,wan").Split(',');
            var only = Arg(a, "--torrents", "big,small").Split(','); var outFile = Arg(a, "--out", null);
            var root = Path.Combine(Path.GetTempPath(), "HallowBench" + Process.GetCurrentProcess().Id); Directory.CreateDirectory(root);
            var results = into ?? new List<BenchResult>(); var all = new List<IDisposable>(); var peersAll = new List<Peer>();
            TrackerServer tracker = null; ITrackerListener http = null;
            try
            {
                WaitNoGame(60);
                Say("generating " + bigMb + " MB + " + files + " small files in " + root);
                var seed = Path.Combine(root, "seed"); var bigDir = Path.Combine(seed, "Big"); var smallDir = Path.Combine(seed, "Small"); Directory.CreateDirectory(bigDir); Directory.CreateDirectory(smallDir);
                if (only.Contains("big")) { GenerateBig(Path.Combine(bigDir, "big.bin"), bigMb * 1048576L); File.WriteAllText(Path.Combine(bigDir, "readme.txt"), "Hallow Torrents benchmark data"); }   // 2+ files = a folder torrent
                if (only.Contains("small")) GenerateSmall(smallDir, files);
                tracker = new TrackerServer { AllowUnregisteredTorrents = true }; int tport = FreePort(IPAddress.Loopback);
                http = TrackerListenerFactory.CreateHttp(IPAddress.Loopback, tport); tracker.RegisterListener(http); http.Start();
                var announce = "http://127.0.0.1:" + tport + "/announce";
                var torrents = new Dictionary<string, string[]>();
                if (only.Contains("big")) torrents["big"] = MakeTorrent(bigDir, root, "Big", 1024, announce).Result;
                if (only.Contains("small")) torrents["small"] = MakeTorrent(smallDir, root, "Small", 256, announce).Result;
                Say("torrents made");
                var resume = new Dictionary<string, FastResume>();
                foreach (var scen in scens)
                {
                    // lan: a few unlimited peers next door; wan: the same peers with latency and an upload cap each; crowd: many slow
                    // peers (like a popular torrent on the internet) - the case where the number of connections matters
                    bool crowd = scen == "crowd", wan = scen == "wan" || crowd; int upCap = crowd ? crowdCap : wan ? cap : 0;
                    int seeds = crowd ? nCrowd : nSeed, leech = crowd ? 0 : nLeech;
                    var seeders = new List<Peer>();
                    for (int i = 0; i < seeds; i++)
                    {
                        var ip = IPAddress.Parse("127.0.1." + (1 + i)); var port = FreePort(ip);
                        var p = new Peer { Engine = Engine(ip, port, Path.Combine(root, "cache-s" + i), upCap, true), Ep = new IPEndPoint(ip, port) };
                        foreach (var kv in torrents)
                        {
                            var mm = p.Engine.AddAsync(Torrent.Load(kv.Value[0]), seed, new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false, UploadSlots = 8 }.ToSettings()).Result;
                            FastResume fr; if (resume.TryGetValue(kv.Key, out fr)) mm.LoadFastResumeAsync(fr).Wait(); else { mm.HashCheckAsync(false).Wait(); resume[kv.Key] = mm.SaveFastResumeAsync().Result; }   // hashed once, the others trust it
                            mm.StartAsync().Wait(); if (!mm.Complete) Say("seeder " + i + ": " + mm.Name + " isn't complete (" + mm.Progress.ToString("0.0") + "%, " + mm.State + ")");
                        }
                        if (wan) p.Relay = new Relay(IPAddress.Parse("127.0.2." + (1 + i)), p.Ep, latency / 2);
                        seeders.Add(p); peersAll.Add(p);
                    }
                    Say(scen + ": " + seeds + " seeders" + (leech > 0 ? " + " + leech + " other leechers" : "") + (wan ? " (" + latency + " ms round trip, " + upCap + " KB/s upload each)" : " (no limits)"));
                    foreach (var tk in torrents.Keys.Where(k => !crowd || k == "big"))
                        for (int run = 1; run <= runs; run++)
                            foreach (var prof in (run % 2 == 1 ? profiles : profiles.Reverse().ToArray()))
                            {
                                BenchResult r = null;
                                for (int attempt = 0; attempt < 5 && r == null; attempt++)
                                {
                                    WaitNoGame(60);
                                    r = OneRun(root, torrents[tk], seeders, leech, wan, latency, upCap, prof, tk == "big" ? 900 : 600);
                                    if (r == null) Say("thrown away (a game started) - repeating");
                                }
                                if (r == null) continue;
                                r.Torrent = tk; r.Scenario = scen; r.Run = run; r.Profile = prof; results.Add(r);
                                Say(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,-4} {1,-6} {2,-9} run {3}: {4}{13} {5:0.0} s, avg {6:0.0} MB/s, peak {7:0.0} MB/s, CPU {8:0.0} s ({9:0}% of a core), peak {10} MB, wrote {11} MB, verify {12:0.00} s",
                                    scen, tk, prof, run, r.Ok ? "ok" : "FAILED " + r.Error, r.Seconds, r.AvgMBps, r.PeakMBps, r.CpuSeconds, r.CpuPctOneCore, r.PeakMB, r.WrittenMB, r.VerifySeconds, r.Ok && !string.IsNullOrEmpty(r.Error) ? " (" + r.Error + ")" : ""));
                            }
                    foreach (var p in seeders) { try { p.Engine.StopAllAsync().Wait(3000); } catch { } try { p.Engine.Dispose(); } catch { } if (p.Relay != null) p.Relay.Dispose(); }
                }
                if (outFile != null) Write(results, outFile, a);
                return results.Count > 0 && results.All(x => x.Ok) ? 0 : 1;
            }
            catch (Exception e) { Say("bench failed: " + e); return 2; }
            finally
            {
                foreach (var p in peersAll) { try { p.Engine.Dispose(); } catch { } if (p.Relay != null) p.Relay.Dispose(); }
                try { if (http != null) http.Stop(); } catch { } try { if (tracker != null) tracker.Dispose(); } catch { }
                Thread.Sleep(500);
                for (int i = 0; i < 10 && Directory.Exists(root); i++) { try { Directory.Delete(root, true); } catch { Thread.Sleep(500); } }
                Say(Directory.Exists(root) ? "NOTE: couldn't delete " + root : "generated data deleted");
            }
        }

        // one download: fresh leechers + the measured client process. null = thrown away because a game started.
        static BenchResult OneRun(string root, string[] torrent, List<Peer> seeders, int nLeech, bool wan, int latency, int upCap, string profile, int timeout)
        {
            var leechers = new List<Peer>(); var tag = Guid.NewGuid().ToString("N").Substring(0, 6);
            var save = Path.Combine(root, "dl-" + tag); Process child = null;
            try
            {
                for (int j = 0; j < nLeech; j++)
                {
                    var ip = IPAddress.Parse("127.0.3." + (1 + j)); var port = FreePort(ip);
                    var p = new Peer { Engine = Engine(ip, port, Path.Combine(root, "cache-l" + tag + j), upCap, false), Ep = new IPEndPoint(ip, port), Dir = Path.Combine(root, "leech-" + tag + j) };
                    var mm = p.Engine.AddAsync(Torrent.Load(torrent[0]), p.Dir, new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false }.ToSettings()).Result; mm.StartAsync().Wait();
                    if (wan) p.Relay = new Relay(IPAddress.Parse("127.0.4." + (1 + j)), p.Ep, latency / 2);
                    leechers.Add(p);
                }
                var peers = seeders.Concat(leechers).Select(p => p.Relay != null ? p.Relay.EndPoint : p.Ep).Select(e => e.ToString());
                var exe = typeof(Bench).Assembly.Location;
                var psi = new ProcessStartInfo(exe, "bench-client --profile " + profile + " --torrent \"" + torrent[1] + "\" --save \"" + save + "\" --peers " + string.Join(",", peers) + " --ip 127.0.0.3 --timeout " + timeout)
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                child = Process.Start(psi); try { child.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                string result = null; var lines = new List<string>();
                child.OutputDataReceived += (s, e) => { if (e.Data == null) return; if (e.Data.StartsWith("RESULT ")) result = e.Data; else if (!e.Data.StartsWith("T ")) lock (lines) lines.Add(e.Data); };
                child.BeginOutputReadLine();
                var sw = Stopwatch.StartNew();
                while (!child.WaitForExit(1000))
                {
                    if (GameRunning()) { try { child.Kill(); } catch { } child.WaitForExit(5000); return null; }
                    if (sw.Elapsed.TotalSeconds > timeout + 120) { try { child.Kill(); } catch { } break; }
                }
                child.WaitForExit();
                var r = Parse(result); if (r != null) lock (lines) r.Error = string.Join(" | ", lines.Where(x => x.StartsWith("why ")).Take(2));
                r = r ?? new BenchResult { Ok = false, Error = "no result" + (lines.Count > 0 ? ": " + string.Join(" | ", lines.Take(3)) : "") };
                return r;
            }
            finally
            {
                foreach (var p in leechers) { try { p.Engine.StopAllAsync().Wait(2000); } catch { } try { p.Engine.Dispose(); } catch { } if (p.Relay != null) p.Relay.Dispose(); }
                Thread.Sleep(300);
                foreach (var d in new[] { save, Path.Combine(root, "core-dl-" + tag) }.Concat(leechers.Select(p => p.Dir)))
                    for (int i = 0; i < 6 && Directory.Exists(d); i++) { try { Directory.Delete(d, true); } catch { Thread.Sleep(300); } }
                if (child != null) child.Dispose();
            }
        }

        // medians per (scenario, torrent, profile) as a Markdown table + every run as JSON-ish lines
        public static List<string[]> Table(List<BenchResult> rs)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture; var rows = new List<string[]>();
            foreach (var g in rs.Where(r => r.Ok).GroupBy(r => r.Scenario + "|" + r.Torrent + "|" + r.Profile))
            {
                Func<Func<BenchResult, double>, double> med = f => { var v = g.Select(f).OrderBy(x => x).ToList(); return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; };
                var k = g.Key.Split('|');
                rows.Add(new[] { k[0], k[1], k[2], g.Count().ToString(), med(r => r.Seconds).ToString("0.0", c), med(r => r.AvgMBps).ToString("0.0", c), med(r => r.PeakMBps).ToString("0.0", c), med(r => r.CpuSeconds).ToString("0.0", c), med(r => r.CpuPctOneCore).ToString("0", c), med(r => r.PeakMB).ToString("0", c), med(r => r.WrittenMB).ToString("0", c), med(r => r.VerifySeconds).ToString("0.00", c), med(r => r.FinishSeconds).ToString("0.0", c) });
            }
            return rows;
        }
        static void Write(List<BenchResult> rs, string outFile, string[] args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Hallow Torrents local benchmark - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " - args: " + string.Join(" ", args));
            sb.AppendLine("| Scenario | Torrent | Profile | Runs | Download (s) | Avg MB/s | Peak MB/s | CPU (s) | CPU (% of a core) | Peak RAM (MB) | Written (MB) | Recheck (s) | Finished (s) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in Table(rs)) sb.AppendLine("| " + string.Join(" | ", r) + " |");
            sb.AppendLine(); sb.AppendLine("Every run:");
            foreach (var r in rs) sb.AppendLine(r.Scenario + " " + r.Torrent + " " + r.Profile + " run " + r.Run + ": " + (r.Ok ? "" : "FAILED " + r.Error + " ") + Line(r));
            File.WriteAllText(outFile + ".md", sb.ToString());
        }
    }
}
