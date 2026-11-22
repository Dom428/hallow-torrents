// Hallow Torrents 1.5 - proof: the speed test, the leak drill and the socket watchdog, shared by the app and the headless copy.
//   * Speed test (`speedtest`): downloads a legal, well-seeded torrent YOU pick (suggested: the current Ubuntu desktop image - its
//     .torrent is fetched from releases.ubuntu.com over the VPN, only when you ask) like any other torrent - VPN-bound, game-aware -
//     and reports average / peak speed, time, peers and CPU. The files land in "<save folder>\Speed test"; the torrent is paused
//     when it's done (it doesn't seed on by itself).
//   * Leak drill (`leak-drill`): while a torrent downloads over the real VPN, it asks Hallow VPN to disconnect (or you disconnect your
//     own VPN app), checks within 2 s that the engine locked, that no socket of this process appeared on a non-VPN address (Windows'
//     TCP / UDP tables, sampled every 100 ms), that no data moved after the lock and that the engine's sockets closed; then reconnects
//     and checks the download resumes. A pass / fail report (leak-drill.json in the data folder).
//   * Socket watchdog (SocketWatch.cs): always running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents
{
    // ---------------------------------------------------------------- speed test
    [DataContract]
    public class SpeedReport
    {
        [DataMember] public string Name, Started, Finished, State, Error;   // State: "starting", "waiting-vpn", "running", "done", "stopped", "error"
        [DataMember] public long Bytes, Size;
        [DataMember] public double Seconds, ActiveSeconds, FirstByteSeconds, AvgMBps, PeakMBps, CpuPct, CpuPctOneCore;
        [DataMember] public int PeersMax; [DataMember] public double PeersAvg; [DataMember] public double Progress;
        [DataMember] public string Profile;
        [DataMember] public string Source;   // what you started it with: your magnet link / .torrent path (or the Ubuntu .torrent fetched for you) - race.ps1 gives qBittorrent the same
        public Dictionary<string, object> Json()
        {
            return new Dictionary<string, object> { { "state", State }, { "name", Name }, { "started", Started }, { "finished", Finished }, { "error", Error }, { "bytes", Bytes }, { "size", Size }, { "progress", Math.Round(Progress, 1) },
                { "seconds", Math.Round(Seconds, 1) }, { "activeSeconds", Math.Round(ActiveSeconds, 1) }, { "firstByteSeconds", Math.Round(FirstByteSeconds, 1) }, { "avgMBps", Math.Round(AvgMBps, 2) }, { "peakMBps", Math.Round(PeakMBps, 2) },
                { "avgMbit", Math.Round(AvgMBps * 8.388608, 1) }, { "peakMbit", Math.Round(PeakMBps * 8.388608, 1) }, { "peersMax", PeersMax }, { "peersAvg", Math.Round(PeersAvg, 1) }, { "cpuPct", Math.Round(CpuPct, 1) }, { "cpuPctOneCore", Math.Round(CpuPctOneCore, 1) }, { "profile", Profile }, { "source", Source } };
        }
    }
    // the numbers from per-second samples (pure, tested)
    public static class SpeedMath
    {
        public class Sample { public double T; public long Bytes; public int Peers; public bool Locked; }
        // avg = bytes / seconds the engine could run (time locked by the VPN or a game doesn't count); peak = the best 3-second average
        public static void Fill(SpeedReport r, List<Sample> s)
        {
            if (s.Count == 0) return;
            r.Bytes = s[s.Count - 1].Bytes - s[0].Bytes; r.Seconds = s[s.Count - 1].T - s[0].T;
            double active = 0; for (int i = 1; i < s.Count; i++) if (!s[i].Locked) active += s[i].T - s[i - 1].T;
            r.ActiveSeconds = active; r.AvgMBps = active > 0 ? r.Bytes / active / 1048576.0 : 0;
            double peak = 0;
            for (int i = 0; i < s.Count; i++)
                for (int j = i + 1; j < s.Count; j++)
                    if (s[j].T - s[i].T >= 3) { peak = Math.Max(peak, (s[j].Bytes - s[i].Bytes) / (s[j].T - s[i].T)); break; }
            if (peak == 0 && r.Seconds > 0) peak = r.Bytes / r.Seconds;
            r.PeakMBps = peak / 1048576.0;
            var first = s.FirstOrDefault(x => x.Bytes > s[0].Bytes); r.FirstByteSeconds = first == null ? 0 : first.T - s[0].T;
            var live = s.Where(x => !x.Locked).ToList(); r.PeersMax = live.Count == 0 ? 0 : live.Max(x => x.Peers); r.PeersAvg = live.Count == 0 ? 0 : live.Average(x => x.Peers);
        }
        // releases.ubuntu.com: the newest "NN.NN" or "NN.NN.N" folder in its index page
        public static string NewestUbuntu(string indexHtml)
        {
            Version best = null; string bestText = null;
            foreach (Match m in Regex.Matches(indexHtml ?? "", @"href=""(\d{2}\.\d{2}(?:\.\d+)?)/"""))
            {
                Version v; if (Version.TryParse(m.Groups[1].Value, out v) && (best == null || v > best)) { best = v; bestText = m.Groups[1].Value; }
            }
            return bestText;
        }
        // that folder's page: the desktop amd64 image's .torrent link (absolute)
        public static string DesktopTorrent(string pageHtml, string pageUrl)
        {
            var m = Regex.Match(pageHtml ?? "", @"href=""([^""]*ubuntu-[\d.]+-desktop-amd64\.iso\.torrent)""", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return new Uri(new Uri(pageUrl), m.Groups[1].Value).ToString();
        }
    }

    public class SpeedTest
    {
        public const string UbuntuIndex = "https://releases.ubuntu.com/";
        readonly Core core; readonly Func<TorSettings> settings; readonly Action<string> log;
        public SpeedReport Current, Last; public volatile bool Running; string id; volatile bool stopAsked;
        public Func<string, byte[]> Fetch;   // over the VPN (VpnFetch) - tests point it at a stand-in
        public SpeedTest(Core core, Func<TorSettings> settings, Action<string> log)
        {
            this.core = core; this.settings = settings; this.log = log;
            Fetch = url => VpnFetch.Get(url, core.BoundIp, core.BoundDns, 4 * 1024 * 1024, 30);
            Last = Load();
        }
        string File_ { get { return Path.Combine(core.Dir, "speedtest.json"); } }
        SpeedReport Load() { try { if (File.Exists(File_)) using (var f = File.OpenRead(File_)) return (SpeedReport)new DataContractJsonSerializer(typeof(SpeedReport)).ReadObject(f); } catch { } return null; }
        void Save(SpeedReport r) { if (TorSettings.Frozen) return; try { using (var f = File.Create(File_)) new DataContractJsonSerializer(typeof(SpeedReport)).WriteObject(f, r); } catch { } }
        public string Folder { get { return Path.Combine(settings().SaveFolder, "Speed test"); } }

        // Ubuntu's current desktop .torrent from releases.ubuntu.com, over the VPN only (never while it's off). Returns a temp file path.
        public string GetUbuntu(out string error)
        {
            error = null;
            if (!core.EngineUp) { error = "Connect your VPN first - Ubuntu's torrent is fetched over it."; return null; }
            try
            {
                var index = System.Text.Encoding.UTF8.GetString(Fetch(UbuntuIndex));
                var v = SpeedMath.NewestUbuntu(index); if (v == null) { error = "Couldn't find Ubuntu's newest release on releases.ubuntu.com."; return null; }
                var page = UbuntuIndex + v + "/"; var link = SpeedMath.DesktopTorrent(System.Text.Encoding.UTF8.GetString(Fetch(page)), page);
                if (link == null || !link.StartsWith(UbuntuIndex, StringComparison.OrdinalIgnoreCase)) { error = "Couldn't find the desktop image's torrent for Ubuntu " + v + "."; return null; }
                var data = Fetch(link); if (data == null || data.Length == 0 || data[0] != (byte)'d') { error = "That wasn't a .torrent file."; return null; }
                var dir = Path.Combine(core.Dir, "speedtest-tmp"); Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, Path.GetFileName(new Uri(link).LocalPath)); File.WriteAllBytes(f, data);
                log("speed test: got " + Path.GetFileName(f) + " from releases.ubuntu.com over the VPN");
                return f;
            }
            catch (Exception e) { error = "Couldn't get Ubuntu's torrent: " + e.Message; return null; }
        }

        // start with a magnet link or a .torrent path ("ubuntu" = fetch Ubuntu's first)
        public string Start(string item)
        {
            if (Running) return "A speed test is already running.";
            item = (item ?? "").Trim().Trim('"'); string err;
            if (item.Equals("ubuntu", StringComparison.OrdinalIgnoreCase)) { item = GetUbuntu(out err); if (item == null) return err; }
            bool magnet = item.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
            if (!magnet && !(item.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(item))) return "Give a magnet link or a .torrent file (or \"ubuntu\").";
            Directory.CreateDirectory(Folder);
            var x = magnet ? core.AddMagnet(item, Folder, "Other", true, out err) : core.AddTorrentFile(item, Folder, "Other", null, true, out err);
            if (x == null && err != null && err.Contains("already in the list"))
            {
                // the same torrent again: start over from what's on disk (it rechecks) - simplest: resume it and measure what's left
                string hid = null; try { hid = magnet ? Core.IdOf(MonoTorrent.MagnetLink.Parse(item).InfoHashes) : Core.IdOf(MonoTorrent.Torrent.Load(item).InfoHashes); } catch { }
                x = hid == null ? null : core.Get(hid);
                if (x == null) return err;
                if (x.CompletedAt != null) return "That torrent is already downloaded in your list - remove it (and its files) first for a fair test.";
                core.Resume(x.Id);
            }
            else if (x == null) return err;
            id = x.Id; stopAsked = false; Running = true;
            Current = new SpeedReport { Name = x.Name, Started = Family.Time(DateTime.Now), State = core.EngineUp ? "starting" : "waiting-vpn", Profile = core.PerfNow.Name, Source = item };
            log("speed test: started (" + x.Name + ")");
            new Thread(Measure) { IsBackground = true, Name = "speed test", Priority = ThreadPriority.BelowNormal }.Start();
            return null;
        }
        public void Stop() { stopAsked = true; }
        void Measure()
        {
            var r = Current; var samples = new List<SpeedMath.Sample>(); var me = Process.GetCurrentProcess(); var clock = Stopwatch.StartNew();
            var cpu0 = me.TotalProcessorTime; double cpuActive = 0, lastCpu = 0; me.Refresh(); int peersSeen = 0;
            try
            {
                while (!stopAsked && clock.Elapsed.TotalHours < 6)
                {
                    var x = core.Get(id); if (x == null) { r.State = "stopped"; r.Error = "It was removed from the list."; break; }
                    var v = core.Snapshot(false).FirstOrDefault(t => t.Id == id);
                    bool locked = !core.EngineUp || core.Hold != null || v == null || v.State == "Paused" || v.State == "Waiting for VPN";
                    // Entry.Down (counted every second by Core.Tick) - it carries on across engine rebuilds, unlike the engine's monitor
                    long bytes = x.Down;
                    var mm = core.ManagerForTests(id); int peers = peersSeen; peersSeen = 0; try { peers = Math.Max(peers, mm == null ? 0 : mm.OpenConnections); } catch { }   // peers actually connected (the most seen this second)
                    samples.Add(new SpeedMath.Sample { T = clock.Elapsed.TotalSeconds, Bytes = bytes, Peers = peers, Locked = locked });
                    var cpuNow = (me.TotalProcessorTime - cpu0).TotalSeconds; if (!locked) cpuActive += cpuNow - lastCpu; lastCpu = cpuNow;
                    SpeedMath.Fill(r, samples); r.Size = x.Size; r.Progress = v == null ? 0 : v.Progress;
                    r.CpuPctOneCore = r.ActiveSeconds > 0 ? cpuActive / r.ActiveSeconds * 100 : 0; r.CpuPct = r.CpuPctOneCore / Environment.ProcessorCount;
                    r.State = locked ? (core.EngineUp ? "held" : "waiting-vpn") : "running";
                    if (x.CompletedAt != null && samples.Count > 1) { r.State = "done"; break; }
                    for (int q = 0; q < 4; q++) { Thread.Sleep(250); var m2 = core.ManagerForTests(id); try { if (m2 != null) peersSeen = Math.Max(peersSeen, m2.OpenConnections); } catch { } }
                }
                if (stopAsked && r.State != "done") r.State = "stopped";
            }
            catch (Exception e) { r.State = "error"; r.Error = e.Message; }
            r.Finished = Family.Time(DateTime.Now);
            // it doesn't seed on by itself: paused when done or stopped (the files stay in the Speed test folder)
            try { if (core.Get(id) != null) core.Pause(id); } catch { }
            Last = r; Save(r); Running = false;
            log("speed test: " + r.State + " - avg " + r.AvgMBps.ToString("0.0") + " MB/s, peak " + r.PeakMBps.ToString("0.0") + " MB/s, " + r.PeersMax + " peers max");
        }
        public Dictionary<string, object> Json()
        {
            var r = Running ? Current : Last;
            var d = r == null ? new Dictionary<string, object> { { "state", "none" } } : r.Json(); d["running"] = Running;
            return d;
        }
    }

    // ---------------------------------------------------------------- leak drill
    public class DrillCheck { public string Name; public bool Pass; public string Detail; }
    public class DrillReport
    {
        public string Started, Finished, Mode, State = "running", Error; public bool Pass; public List<DrillCheck> Checks = new List<DrillCheck>();
        public long LockedMs = -1, SocketsClosedMs = -1, ResumedMs = -1; public int Samples, RowsSeen; public List<string> Problems = new List<string>(); public HashSet<string> Kinds = new HashSet<string>();
        public void Add(string name, bool pass, string detail) { lock (Checks) Checks.Add(new DrillCheck { Name = name, Pass = pass, Detail = detail }); }
        public Dictionary<string, object> Json(bool withProblems)
        {
            List<DrillCheck> cs; lock (Checks) cs = Checks.ToList();
            return new Dictionary<string, object> { { "state", State }, { "pass", Pass }, { "mode", Mode }, { "started", Started }, { "finished", Finished }, { "error", Error },
                { "lockedMs", LockedMs }, { "socketsClosedMs", SocketsClosedMs }, { "resumedMs", ResumedMs }, { "samples", Samples }, { "socketsSeen", RowsSeen },
                { "checks", cs.Select(c => (object)new Dictionary<string, object> { { "name", c.Name }, { "pass", c.Pass }, { "detail", c.Detail } }).ToList() },
                { "problems", withProblems ? (object)Problems.ToList() : Problems.Count } };
        }
        public string Text()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Hallow Torrents leak drill - " + (State == "running" ? "RUNNING" : Pass ? "PASS" : "FAIL") + " (" + Mode + ", " + Started + ")");
            if (Error != null) sb.AppendLine("  " + Error);
            List<DrillCheck> cs; lock (Checks) cs = Checks.ToList();
            foreach (var c in cs) sb.AppendLine("  [" + (c.Pass ? "PASS" : "FAIL") + "] " + c.Name + (string.IsNullOrEmpty(c.Detail) ? "" : " - " + c.Detail));
            foreach (var p in Problems.Take(20)) sb.AppendLine("  socket: " + p);
            return sb.ToString();
        }
    }

    public class LeakDrill
    {
        readonly Core core; readonly Func<VpnState> vpn; readonly Func<TorSettings> settings; readonly ISocketTable table; readonly Func<WatchContext> context; readonly Action<string> log;
        public Func<string, string> AskVpn = cmd => Family.Send("VPN", cmd, 8000);   // Hallow VPN's pipe (tests: a stand-in)
        public int LockLimitMs = 2000, ReconnectWaitSec = 90, ResumeWaitSec = 60, ManualWaitSec = 60;
        public volatile bool Running; public DrillReport Current, Last;
        public LeakDrill(Core core, Func<VpnState> vpn, Func<TorSettings> settings, ISocketTable table, Func<WatchContext> context, Action<string> log)
        { this.core = core; this.vpn = vpn; this.settings = settings; this.table = table; this.context = context; this.log = log; }
        public string FileName { get { return Path.Combine(core.Dir, "leak-drill.json"); } }

        // can it run right now? null = yes
        public string Ready()
        {
            if (Running) return "A leak drill is already running.";
            if (!vpn().Ok || !core.EngineUp) return "Connect your VPN first - the drill disconnects it on purpose while a torrent downloads.";
            if (core.Tripped != null) return "The socket watchdog stopped torrents - check its alert and resume first.";
            if (!Downloading()) return "Start a torrent download first (the speed test's Ubuntu torrent is ideal) - the drill needs data moving.";
            return null;
        }
        bool Downloading() { return core.Snapshot(false).Any(v => v.State == "Downloading" && v.DownRate > 0); }
        long TotalDown() { return core.All().Sum(x => x.Down); }
        public string Start()
        {
            var why = Ready(); if (why != null) return why;
            Running = true; Current = new DrillReport { Started = Family.Time(DateTime.Now), Mode = settings().VpnSource == "hallow" ? "Hallow VPN (disconnected through its pipe)" : "your own VPN app (you disconnect it)" };
            new Thread(() => { try { Run(Current); } catch (Exception e) { Current.Error = e.Message; Current.Pass = false; } finally { Current.State = "done"; Current.Finished = Family.Time(DateTime.Now); Last = Current; SaveReport(Current); Running = false; log("leak drill: " + (Current.Pass ? "PASS" : "FAIL")); } }) { IsBackground = true, Name = "leak drill" }.Start();
            return null;
        }
        public DrillReport RunNow() { var why = Ready(); if (why != null) return new DrillReport { State = "done", Error = why }; Start(); while (Running) Thread.Sleep(100); return Last; }
        void SaveReport(DrillReport r)
        {
            if (TorSettings.Frozen) return;
            try { File.WriteAllText(FileName, Family.Json(r.Json(false))); File.WriteAllText(Path.ChangeExtension(FileName, ".txt"), r.Text()); } catch { }
        }

        void Run(DrillReport r)
        {
            var st0 = vpn(); var ip0 = core.BoundIp; var server0 = st0.Server; bool hallow = settings().VpnSource == "hallow";
            log("leak drill: started (" + r.Mode + ")");
            r.Add("Before: engine bound to the VPN and a torrent downloading", core.EngineUp && ip0 != null && Downloading(), core.EngineUp ? "bound to the VPN address" : "the engine isn't running");
            // only the VPN address the engine used counts as "VPN" during the drill (plus loopback / the Web API / Net.Direct, as always)
            Func<WatchContext> ctx = () => { var c = context(); c.VpnIps = new List<IPAddress> { ip0 }; return c; };
            var me = Process.GetCurrentProcess().Id; var problems = new HashSet<string>();
            Action sample = () =>
            {
                List<SockRow> rows; try { rows = table.Rows(me); } catch { return; }
                r.Samples++; r.RowsSeen += rows.Count;
                var c = ctx();
                foreach (var row in rows) { var p = SocketWatch.Judge(row, c); if (p != null && problems.Add(p)) { r.Problems.Add(p); r.Kinds.Add(SocketWatch.Kind(row)); } }
            };
            sample();
            // 1. disconnect
            var clock = new Stopwatch();
            if (hallow)
            {
                var reply = AskVpn("disconnect"); clock.Start();
                if (reply == null || !reply.StartsWith("ok")) { r.Add("Asked Hallow VPN to disconnect", false, reply == null ? "Hallow VPN didn't answer" : reply); r.Pass = false; return; }
                r.Add("Asked Hallow VPN to disconnect", true, reply);
            }
            else
            {
                r.State = "waiting-for-you"; log("leak drill: disconnect your VPN app now");
                var w = Stopwatch.StartNew(); while (vpn().Ok && core.EngineUp && w.Elapsed.TotalSeconds < ManualWaitSec) { sample(); Thread.Sleep(50); }
                clock.Start(); r.State = "running";
                if (vpn().Ok && core.EngineUp) { r.Add("You disconnected the VPN", false, "nothing changed within " + ManualWaitSec + " s"); r.Pass = false; return; }
                r.Add("You disconnected the VPN", true, "timed from the moment the drop was seen");
            }
            // 2. watch the lock: engine gone, its sockets gone, no data, no socket off the VPN - sampled every 100 ms for 4 s
            long downAtLock = -1, downLater = -1;
            while (clock.ElapsedMilliseconds < Math.Max(4000, LockLimitMs + 2000))
            {
                if (r.LockedMs < 0 && !core.EngineUp) r.LockedMs = clock.ElapsedMilliseconds;
                sample();
                if (r.LockedMs >= 0 && r.SocketsClosedMs < 0)
                {
                    List<SockRow> rows; try { rows = table.Rows(me); } catch { rows = null; }
                    if (rows != null && !rows.Any(x => x.Local != null && x.Local.Equals(ip0) && !(x.Proto == "tcp" && (x.State == "time-wait" || x.State == "closed")))) r.SocketsClosedMs = clock.ElapsedMilliseconds;
                }
                Thread.Sleep(100);
            }
            // no data while locked: the totals (counted every second) don't move over 1.5 s, a few seconds into the lock
            if (r.LockedMs >= 0 && !core.EngineUp) { downAtLock = TotalDown(); Thread.Sleep(1500); downLater = TotalDown(); }
            r.Add("Torrents locked within " + LockLimitMs / 1000.0 + " s", r.LockedMs >= 0 && r.LockedMs <= LockLimitMs, r.LockedMs >= 0 ? "engine stopped after " + r.LockedMs + " ms" : "the engine didn't stop");
            r.Add("No socket on a non-VPN address (Windows' TCP / UDP tables, " + r.Samples + " samples)", r.Problems.Count == 0, r.Problems.Count == 0 ? r.RowsSeen + " socket rows checked" : r.Problems.Count + " found: " + string.Join("; ", r.Kinds.Take(3)));
            r.Add("The engine's sockets closed", r.SocketsClosedMs >= 0 && r.SocketsClosedMs <= LockLimitMs + 1000, r.SocketsClosedMs >= 0 ? "none left on the VPN address after " + r.SocketsClosedMs + " ms" : "some were still open");
            r.Add("No data moved after the lock", downAtLock >= 0 && downLater == downAtLock, downAtLock >= 0 ? Fmt.Bytes(Math.Max(0, downLater - downAtLock)) + " after the lock" : "not measured");
            // 3. reconnect and resume
            long downBefore = TotalDown(); var back = Stopwatch.StartNew();
            if (hallow)
            {
                var reply = AskVpn("connect " + (string.IsNullOrEmpty(server0) ? "fastest" : server0));
                if (reply == null || !reply.StartsWith("ok")) reply = AskVpn("connect fastest");
                r.Add("Asked Hallow VPN to reconnect", reply != null && reply.StartsWith("ok"), reply ?? "no answer");
            }
            else { r.State = "waiting-for-you"; log("leak drill: connect your VPN app again"); }
            while (!core.EngineUp && back.Elapsed.TotalSeconds < (hallow ? ReconnectWaitSec : ManualWaitSec * 2)) { sample(); Thread.Sleep(100); }
            r.State = "running";
            r.Add("Torrents started again when the VPN was back", core.EngineUp, core.EngineUp ? "engine up after " + back.Elapsed.TotalSeconds.ToString("0.0") + " s, bound to the VPN address" : "still locked after " + back.Elapsed.TotalSeconds.ToString("0") + " s");
            if (core.EngineUp)
            {
                var res = Stopwatch.StartNew();
                while (TotalDown() <= downBefore && res.Elapsed.TotalSeconds < ResumeWaitSec) { sample(); Thread.Sleep(200); }
                bool resumed = TotalDown() > downBefore; if (resumed) r.ResumedMs = (long)back.Elapsed.TotalMilliseconds;
                r.Add("The download resumed", resumed, resumed ? "data again " + (r.ResumedMs / 1000.0).ToString("0.0") + " s after reconnecting" : "no data within " + ResumeWaitSec + " s");
                // and the rebuilt engine is on the (new) VPN address, never the normal connection
                var st = vpn(); r.Add("Bound to the VPN address again", core.BoundIp != null && st.Ok && core.BoundIp.Equals(st.Ip), st.Ok ? "bound to the VPN adapter " + st.Adapter : "VPN not up");
            }
            sample();
            r.Add("No socket on a non-VPN address during the whole drill", r.Problems.Count == 0, r.Problems.Count == 0 ? "" : r.Problems.Count + " found: " + string.Join("; ", r.Kinds.Take(3)));
            lock (r.Checks) r.Pass = r.Checks.All(c => c.Pass);
        }
        public Dictionary<string, object> Json() { var r = Running ? Current : Last; var d = r == null ? new Dictionary<string, object> { { "state", "none" } } : r.Json(false); d["running"] = Running; return d; }
    }

    // ---------------------------------------------------------------- one place for the app and the headless copy
    public class Proof : IDisposable
    {
        public readonly SocketWatchdog Watchdog; public readonly SpeedTest Speed; public readonly LeakDrill Drill;
        readonly Core core; readonly Func<TorSettings> settings; readonly Func<bool> apiRunning;
        public event Action<string> Alert;   // the watchdog tripped (full text, for the window only)
        public Proof(Core core, Func<TorSettings> settings, Func<VpnState> vpn, Func<bool> apiRunning, Action<string> log, ISocketTable table = null)
        {
            this.core = core; this.settings = settings; this.apiRunning = apiRunning;
            table = table ?? new RealSocketTable();
            Watchdog = new SocketWatchdog(table, Context, reason =>
            {
                bool first = core.Tripped == null;
                core.Trip(reason).Wait(5000);
                if (first) { log("socket watchdog: " + reason); var a = Alert; if (a != null) a(reason); }
            });
            Speed = new SpeedTest(core, settings, log);
            Drill = new LeakDrill(core, vpn, settings, table, Context, log);
        }
        public WatchContext Context()
        {
            var s = settings(); IPAddress bind; IPAddress.TryParse(s.ApiBind ?? "", out bind);
            return new WatchContext { VpnIps = core.VpnIpsForWatch(TimeSpan.FromMinutes(2)), ApiBind = bind, ApiPort = s.ApiPort, ApiRunning = apiRunning(), Lpd = s.Lpd, DirectPorts = SocketWatch.DirectPorts() };
        }
        public void Start() { Watchdog.Start(); }
        public void Dispose() { Watchdog.Dispose(); Speed.Stop(); }

        // the pipe: speedtest [ubuntu | <magnet> | <path.torrent> | status | stop], leak-drill [--wait | status | report], watchdog [status | resume]
        // (never addresses, names of other torrents or hashes - the speed test's own torrent name is the one you picked)
        public string Pipe(string cmd, string arg, Action resumed)
        {
            switch (cmd)
            {
                case "speedtest":
                    if (arg == "" || arg == "status") return "ok " + Family.Json(Speed.Json());
                    if (arg == "stop") { Speed.Stop(); return "ok"; }
                    { var e = Speed.Start(arg); return e == null ? "ok " + Family.Json(Speed.Json()) : "error " + Family.OneLine(e); }
                case "leak-drill":
                    if (arg == "status" || arg == "report") return "ok " + Family.Json(Drill.Json());
                    if (arg == "--wait") { var r = Drill.RunNow(); return r.Error != null && r.Checks.Count == 0 ? "error " + r.Error : (r.Pass ? "ok " : "error FAIL ") + Family.Json(r.Json(false)); }
                    { var e = Drill.Start(); return e == null ? "ok started - ask \"leak-drill status\" for the report" : "error " + e; }
                case "watchdog":
                    if (arg == "resume") { if (core.Tripped == null) return "ok nothing to resume"; if (resumed != null) resumed(); else core.ClearTrip(); return "ok resumed"; }
                    { var d = Watchdog.Json(); d["locked"] = core.Tripped != null; return "ok " + Family.Json(d); }
            }
            return null;
        }
    }
}
