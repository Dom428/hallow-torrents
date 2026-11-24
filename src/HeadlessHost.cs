// Hallow Torrents 1.4 - NAS / headless mode: the engine, RSS, search, the family pipe and the Web API / web interface with no window,
// no tray and no WPF. `HallowTorrents.exe --headless` runs it, and so does HallowTorrentsHeadless.exe (built from the same files minus
// the WPF ones). The rules don't change: the VPN lock is the same (VpnWatch + Core: nothing runs, every socket bound to the VPN's IP),
// the Web API / web interface listen on 127.0.0.1 unless you picked a home-network / Tailscale address in settings.json, never the
// internet. One copy at a time: it shares the app's single-instance lock, so it won't start next to the windowed app (or a second one).
// It uses the same data folder (%APPDATA%\HallowTorrents) and logs to headless.log there (never API keys or passwords).
//   --headless          run until `--cmd quit` (or the pipe's "quit")
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents
{
    public class HeadlessHost : IDisposable
    {
        public static int Run(string[] args)
        {
            TorSettings.Headless = true;
            var leaf = TorSettings.TestDir != null ? "Test" + Path.GetFileName(TorSettings.TestDir.TrimEnd('\\', '/')) : "";
            bool created; var mutex = new Mutex(true, @"Local\HallowTorrents" + leaf, out created);
            if (!created) { Console.Error.WriteLine("Hallow Torrents is already running (the app or another headless copy)."); return 3; }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            Crash.Install();
            using (var h = new HeadlessHost())
            {
                h.Start();
                h.quit.WaitOne();
            }
            GC.KeepAlive(mutex);
            return 0;
        }

        public TorSettings S = TorSettings.Load();
        readonly object sl = new object();
        public Core Core; public Rss Rss; public SearchEngine Search; public VpnWatch Watch; public QbitApi Api; public ApiServer WebApi; public Proof Proof; public Nas Nas;
        public VpnState Vpn = new VpnState { Message = "Checking the VPN..." };
        public string Game, ApiStatus = "Off.";
        readonly ManualResetEvent quit = new ManualResetEvent(false);
        readonly List<Timer> timers = new List<Timer>();
        readonly List<string> log = new List<string>();
        volatile bool quitting; int ticking, rssBusy, blBusy;

        void Log(string s)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + Torznab.Redact(s);
            lock (log) { log.Add(line); if (log.Count > 120) log.RemoveAt(0); }
            try { var f = Path.Combine(TorSettings.Dir, "headless.log"); if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f); File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine); } catch { }
        }
        void Every(int ms, Action a, int first = -1) { timers.Add(new Timer(_ => { if (quitting) return; try { a(); } catch (Exception e) { Log("timer: " + e.Message); } }, null, first < 0 ? ms : first, ms)); }
        void Change(Action a) { lock (sl) { a(); S.Fix(); S.Save(); } }

        public void Start()
        {
            S.Save();
            Directory.CreateDirectory(TorSettings.Dir);
            Core = new Core(TorSettings.Dir, () => S); Core.Log += Log; Core.Completed += x => Task.Run(() => Finished(x));
            Rss = new Rss(Core, TorSettings.Dir); Rss.Log += Log;
            Search = new SearchEngine(Core, Rss, TorSettings.Dir); Search.Log += Log;
            INetworks nets = TorSettings.Fake ? (INetworks)new FakeNetworks(Path.Combine(TorSettings.Dir, "fake-adapters.txt")) : new RealNetworks();
            Watch = new VpnWatch(() => S, nets, !TorSettings.Fake);
            Watch.Changed += (o, n) => VpnChanged(n);
            Auto.Log += Log;   // v1.9: VPN on demand
            Family.Serve("Torrents", Command);
            Api = new QbitApi(Core, () => S, Change); Api.Log += Log; Api.Search = Search;
            Api.Ui = new WebUi(Api, () => S) { ConnectVpn = ConnectVpn, Game = () => Game, VpnText = () => Vpn.Ok ? "Connected (" + (Vpn.Server ?? Vpn.Adapter) + ")" : Vpn.Message };
            WebApi = new ApiServer(Api); ApplyApi();
            // v1.5: the socket watchdog, the speed test and the leak drill (Proof.cs), the same as the app
            Proof = new Proof(Core, () => S, () => Vpn, () => WebApi != null && WebApi.Running, Log);
            Proof.Alert += reason => WriteStatus();
            Proof.Start();
            Nas = new Nas(Core, () => S, Log); Nas.Start();   // v1.6: Send to Jellyfin
            Watch.Start();
            Every(1000, Tick);
            Every(3000, CheckGame, 0);
            Every(1000, AutoTick);   // v1.9
            Every(5000, CheckWatchFolder);
            Every(30000, KickRss);
            Every(600000, CheckBlocklist);
            Every(30000, () => WriteStatus());
            foreach (var x in Core.Entries.Where(e => e.CompletedAt != null && (e.Scan == "waiting" || e.Scan == "scanning"))) { var xx = x; Task.Run(() => Finished(xx)); }
            WriteStatus();
            Log("headless: started (v" + Program.Version + ", data " + (TorSettings.TestDir != null ? "test folder" : "%APPDATA%\\HallowTorrents") + ")");
        }
        public string ApplyApi()
        {
            WebApi.Stop(); string err = null;
            if (!S.ApiOn) ApiStatus = "Off.";
            else if (string.IsNullOrWhiteSpace(S.ApiUser) || string.IsNullOrEmpty(S.ApiPassHash)) { err = "Set a username and password first (in the app's Radarr / Sonarr page) - it stays off without them."; ApiStatus = err; }
            else { err = WebApi.Start(S.ApiBind, S.ApiPort); ApiStatus = err == null ? "Running on http://" + S.ApiBind + ":" + S.ApiPort + (S.WebUiOn ? " (web interface at /ui/)" : "") : "Not running: " + err; }
            Log("webapi: " + ApiStatus); return err;
        }
        void VpnChanged(VpnState n)
        {
            Vpn = n; Log("vpn: " + (n.Ok ? "up " + n.Ip + " (" + n.Adapter + ")" : "down - " + n.Message));
            try { Core.SetVpn(n).Wait(5000); } catch (Exception e) { Log("engine: " + e.GetBaseException().Message); }
            if (n.Ok && Core.EngineUp) { KickRss(); CheckBlocklist(); }
            WriteStatus();
        }
        // v1.9: VPN on demand (VpnAuto.cs), the same as the app
        public VpnAuto Auto = new VpnAuto();
        int autoBusy;
        void AutoTick()
        {
            if (Interlocked.Exchange(ref autoBusy, 1) == 1) return;
            try
            {
                var inp = new VpnAuto.Inputs { On = VpnAuto.On(S), InGames = S.VpnAutoInGames, Source = S.VpnSource, VpnOk = Vpn.Ok, NotP2p = Vpn.NotP2p, Work = VpnAuto.Work(Core), Game = Game, Drill = Proof != null && Proof.Drill.Running, Hallow = Watch.Hallow };
                inp.HallowRunning = inp.On && inp.Source == "hallow" && !inp.VpnOk && Watch.HallowRunning;
                var before = Auto.State + Auto.Text; Auto.Step(DateTime.Now, inp); if (before != Auto.State + Auto.Text) WriteStatus();
            }
            finally { Interlocked.Exchange(ref autoBusy, 0); }
        }
        public string SwitchP2p() { Auto.Reset(); return Auto.SwitchP2p(Watch.Hallow, Watch.HallowRunning); }
        public string ConnectVpn()
        {
            Auto.Reset();   // 1.9: you acted - start again from try 1
            var r = Family.Send("VPN", VpnAuto.P2pOnly(S) ? VpnAuto.Command : "connect fastest", 5000);
            if (r == null) return "Hallow VPN isn't running on this PC. Start it (or connect your own VPN app) and torrents start by themselves.";
            var e = Family.Error(r, null); return e == null ? null : "Hallow VPN: " + e;
        }
        void Tick()
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try
            {
                var gameLimit = Game != null && S.GameMode == "limit" ? S.GameKBps : 0;
                var rule = S.ScheduleOn ? Scheduler.Active(S.Schedule, DateTime.Now) : null;
                Core.LimitDown = Scheduler.Min(S.DownKBps, rule == null ? 0 : rule.DownKBps, gameLimit);
                Core.LimitUp = Scheduler.Min(S.UpKBps, rule == null ? 0 : rule.UpKBps, gameLimit);
                Core.GameHold = Game != null && S.GameMode == "pause";
                Core.Gaming = Game != null && S.GameMode == "limit";   // v1.5: the light "while gaming" profile
                Core.Tick().Wait(30000);
                if (++saveCount % 15 == 0) Core.Save();
            }
            finally { Interlocked.Exchange(ref ticking, 0); }
        }
        int saveCount;
        void CheckGame()
        {
            string g = null; try { g = Games.Running(S.ExtraGames); } catch { }
            if (g == Game) return;
            var was = Game; Game = g; Log("game: " + (g ?? "none"));
            if (S.GameMode != "off" && (g == null) != (was == null)) { Core.GameHold = g != null && S.GameMode == "pause"; Core.Kick(); WriteStatus(); }
        }
        void CheckWatchFolder()
        {
            var d = S.WatchFolder; if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) return;
            foreach (var f in Directory.GetFiles(d, "*.torrent"))
            {
                if ((DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds < 2) continue;
                string err; var x = Core.AddTorrentFile(f, null, null, null, true, out err);
                var done = f + (x != null ? ".added" : ".failed"); try { if (File.Exists(done)) done = f + "." + DateTime.Now.Ticks + (x != null ? ".added" : ".failed"); File.Move(f, done); } catch { }
                Log("watch folder: " + Path.GetFileName(f) + " " + (x != null ? "added" : err));
            }
        }
        async Task Finished(Entry x)
        {
            try { await Finish.Run(Core, x, S, (t, m) => Log("notify: " + t)).ConfigureAwait(false); }
            catch (Exception e) { Log("finish: " + e.Message); }
            try { if (Nas != null) Nas.Consider(x); } catch (Exception e) { Log("nas: " + e.Message); }   // v1.6
        }
        void KickRss()
        {
            if (quitting || !Core.EngineUp || Rss.Busy || Interlocked.Exchange(ref rssBusy, 1) == 1) return;
            Task.Run(async () => { try { await Rss.Tick(DateTime.Now).ConfigureAwait(false); } catch (Exception e) { Log("rss: " + e.Message); } finally { Interlocked.Exchange(ref rssBusy, 0); } });
        }
        void CheckBlocklist()
        {
            if (string.IsNullOrWhiteSpace(S.BlocklistUrl) || !Core.EngineUp) return;
            DateTime last; if (DateTime.TryParse(S.BlocklistUpdated, out last) && (DateTime.Now - last).TotalDays < S.BlocklistDays && File.Exists(Core.BlocklistUrlFile)) return;
            if (Interlocked.Exchange(ref blBusy, 1) == 1) return;
            var url = S.BlocklistUrl;
            Task.Run(() =>
            {
                try
                {
                    int n; var err = Blocklist.Update(url, Core.BoundIp, Core.BoundDns, Core.BlocklistUrlFile, out n);
                    Change(() => { if (err == null) { S.BlocklistUpdated = DateTime.Now.ToString("o"); S.BlocklistNote = "Downloaded " + n.ToString("N0") + " ranges over the VPN."; } else S.BlocklistNote = "Couldn't update it: " + err; });
                    Core.ReloadBlocklist(); Log("blocklist url: " + (err ?? n + " ranges"));
                }
                finally { Interlocked.Exchange(ref blBusy, 0); }
            });
        }

        // ---- status.json and the pipe: counts only in status (never names, hashes or addresses), like the app
        public Dictionary<string, object> StatusData(bool running)
        {
            var d = new Dictionary<string, object> { { "app", "Torrents" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", running } };
            var v = Core.Snapshot(false);
            d["total"] = v.Count; d["active"] = v.Count(x => x.State == "Downloading" || x.State == "Getting info" || x.State == "Starting" || x.State == "Checking");
            d["seeding"] = v.Count(x => x.State == "Seeding"); d["paused"] = v.Count(x => x.Paused); d["queued"] = v.Count(x => x.State == "Queued");
            d["done"] = v.Count(x => x.Complete); d["flagged"] = v.Count(x => x.Flagged || x.Scan == "threats");
            d["downKBps"] = Math.Round(Core.TotalDown / 1024.0, 1); d["upKBps"] = Math.Round(Core.TotalUp / 1024.0, 1);
            d["vpnRequired"] = true; d["vpnLocked"] = !Core.EngineUp; d["vpnSource"] = S.VpnSource; d["vpnAdapter"] = Vpn.Ok ? Vpn.Adapter : null; d["vpnServer"] = Vpn.Server;
            d["game"] = Game; d["gameMode"] = S.GameMode; d["held"] = Core.Hold;
            d["limitDownKBps"] = Core.LimitDown; d["limitUpKBps"] = Core.LimitUp;
            d["lastCrash"] = Crash.LastTime() == null ? null : Family.Time(Crash.LastTime().Value);
            d["rssFeeds"] = Rss.Data.Feeds.Count; d["rssRules"] = Rss.Data.Rules.Count(r => r.Enabled);
            d["blocklistRanges"] = Core.BlocklistRanges;
            d["webApi"] = WebApi != null && WebApi.Running; d["indexers"] = Search.FeedCount;
            d["webUi"] = WebApi != null && WebApi.Running && S.WebUiOn; d["headless"] = true;
            d["perf"] = Core.PerfNow.Name; d["watchdog"] = Proof != null; d["watchdogLocked"] = Core.Tripped != null;
            d["speedTest"] = Proof != null && Proof.Speed.Running; d["leakDrill"] = Proof != null && Proof.Drill.Running; d["repairedPieces"] = Core.Repaired;
            d["nas"] = Nas == null ? null : Nas.Json();
            d["vpnAuto"] = Auto.Json(VpnAuto.On(S), S.VpnAutoInGames); d["p2pOnly"] = VpnAuto.P2pJson(S, Vpn, Watch == null ? null : Watch.Hallow);   // v1.9
            if (TorSettings.TestDir != null) { lock (log) d["log"] = log.ToArray(); d["boundIp"] = Core.BoundIp == null ? null : Core.BoundIp.ToString(); }
            return d;
        }
        public void WriteStatus(bool running = true) { try { Family.WriteStatus(Family.Json(StatusData(running))); } catch { } }

        public string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Torrents" }, { "version", Program.Version }, { "headless", true } });
                case "status": { var d = StatusData(true); Family.WriteStatus(Family.Json(d)); return "ok " + Family.Json(d); }
                case "add":
                    {
                        RemoteApi.TakeDirect(ref arg);   // always direct here: there is no Add window
                        if (arg.Length == 0) return "error add <magnet link or .torrent path> [--category <name>]";
                        if (arg.TrimEnd().EndsWith("--category", StringComparison.OrdinalIgnoreCase)) return "error --category needs a name";
                        var p = SplitAdd(arg); return Add(p[0], p[1]);
                    }
                case "pause-all": Core.PauseAll(); WriteStatus(); return "ok";
                case "resume-all": Core.ResumeAll(); WriteStatus(); return "ok";
                case "search":
                    {
                        string cerr; var client = RemoteApi.TakeClient(ref arg, out cerr); if (cerr != null) return "error " + cerr;   // 1.8
                        int n; string cat; var text = SplitSearch(" " + arg, out n, out cat);
                        if (text.Length == 0) return "error search <words> [--n 10] [--cat movies|tv|music|games|software|books|other] [--client <id>]";
                        if (cat.Length > 0 && Torznab.CatParam(cat) == null) return "error no search category called " + cat;
                        var o = Search.Run(SearchQuery.From(text, cat), DateTime.UtcNow, client).Result;
                        if (o.Locked) return "error waiting for the VPN - searches only go out over it (nothing was sent)";
                        if (o.Error != null) return "error " + o.Error;
                        var sd = SearchEngine.Json(o, n); sd["client"] = client; return "ok " + Family.Json(sd);
                    }
                case "categories": return RemoteApi.Categories(S.Categories.ToList());   // 1.8
                case "search-add":
                    {
                        string cerr; var client = RemoteApi.TakeClient(ref arg, out cerr); if (cerr != null) return "error " + cerr;   // 1.8
                        var p = SplitAdd(arg); var r = Search.ByToken(p[0], client);
                        if (r == null) return "error no search result called " + p[0] + " (search again)";
                        string cat = null; if (p[1] != null) { var c = S.Cat(p[1]); if (c == null) return "error no category called " + p[1]; cat = c.Name; } else if (S.Cat(r.Category) != null) cat = S.Cat(r.Category).Name;
                        string err; var e = Search.Download(r, cat, null, true, out err);
                        return e == null ? "error " + err : "ok " + Family.Json(new Dictionary<string, object> { { "id", e.Id }, { "asking", false }, { "category", e.Category } });
                    }
                case "open": return "error this is the headless copy - there is no window (use the web interface or the app)";
                case "menu": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Torrents" }, { "items", new List<object> {
                    new Dictionary<string, object> { { "id", "pause-all" }, { "label", "Pause all" } }, new Dictionary<string, object> { { "id", "resume-all" }, { "label", "Resume all" } },
                    new Dictionary<string, object> { { "id", "connect-vpn" }, { "label", "Connect Hallow VPN" }, { "enabled", !Core.EngineUp } },
                    new Dictionary<string, object> { { "id", "switch-p2p" }, { "label", "Switch to a P2P server" }, { "enabled", Vpn.NotP2p } } } } });
                case "invoke":
                    if (arg == "pause-all" || arg == "resume-all") return Command(arg);
                    if (arg == "connect-vpn") { var e = ConnectVpn(); return e == null ? "ok" : "error " + e; }
                    if (arg == "switch-p2p") { var e = SwitchP2p(); return e == null ? "ok" : "error " + e; }
                    return "error unknown item";
                case "quit": new Thread(() => { Thread.Sleep(300); Quit(); }) { IsBackground = true }.Start(); return "ok";
            }
            var remote = RemoteApi.Pipe(cmd, arg, Core, Game); if (remote != null) { if (cmd == "pause" || cmd == "resume") WriteStatus(); return remote; }
            var proof = Proof == null ? null : Proof.Pipe(cmd, arg, () => { Core.ClearTrip(); var v = Vpn; Task.Run(() => { try { Core.SetVpn(v).Wait(10000); } catch { } WriteStatus(); }); }); if (proof != null) { WriteStatus(); return proof; }
            if (cmd == "nas-status") return Nas == null ? "error not ready" : "ok " + Family.Json(Nas.Json());
            var auto = VpnAuto.Pipe(cmd, arg, S, Change, Auto, SwitchP2p, Vpn, Watch.Hallow); if (auto != null) { WriteStatus(); return auto; }   // v1.9
            if (TorSettings.TestDir != null && cmd == "test-vpn") return "ok " + Family.Json(new Dictionary<string, object> { { "ok", Vpn.Ok }, { "ip", Vpn.Ip == null ? null : Vpn.Ip.ToString() }, { "engine", Core.EngineUp }, { "message", Vpn.Message } });
            return "error unknown command";
        }
        string Add(string item, string category)
        {
            item = (item ?? "").Trim().Trim('"');
            bool magnet = item.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
            if (!magnet && !(item.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(item))) return "error only magnet links and .torrent files can be added";
            if (category != null) { var cat = S.Cat(category.Trim()); if (cat == null) return "error no category called " + category.Trim() + " (" + string.Join(", ", S.Categories.Select(c => c.Name)) + ")"; category = cat.Name; }
            string err; var x = magnet ? Core.AddMagnet(item, null, category, true, out err) : Core.AddTorrentFile(item, null, category, null, true, out err);
            if (x == null) return "error " + err;
            Log("added " + x.Id + (Core.EngineUp ? "" : " (waits for the VPN)"));
            return "ok " + Family.Json(new Dictionary<string, object> { { "id", x.Id }, { "asking", false }, { "category", x.Category } });
        }
        // the same parsing as the app (Controller.SplitAdd / SplitSearch - kept here so this file doesn't need the WPF app)
        public static string[] SplitAdd(string arg)
        {
            var i = arg.LastIndexOf(" --category ", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return new[] { arg.Trim(), null };
            var cat = arg.Substring(i + 12).Trim().Trim('"');
            return new[] { arg.Substring(0, i).Trim(), cat.Length == 0 ? null : cat };
        }
        public static string SplitSearch(string arg, out int n, out string cat)
        {
            n = 10; cat = "";
            var m = System.Text.RegularExpressions.Regex.Match(arg, @"\s--n\s+(\d+)"); if (m.Success) { n = Math.Max(1, Math.Min(100, int.Parse(m.Groups[1].Value))); arg = arg.Remove(m.Index, m.Length); }
            m = System.Text.RegularExpressions.Regex.Match(arg, @"\s--cat\s+(\S+)"); if (m.Success) { cat = m.Groups[1].Value.ToLowerInvariant(); arg = arg.Remove(m.Index, m.Length); }
            return arg.Trim();
        }
        public void Quit()
        {
            if (quitting) return; quitting = true; Log("headless: quit");
            foreach (var t in timers) try { t.Dispose(); } catch { }
            if (Watch != null) Watch.Dispose();
            if (Proof != null) Proof.Dispose();
            if (Nas != null) Nas.Dispose();
            if (WebApi != null) try { WebApi.Dispose(); } catch { }
            try { Core.Dispose(); } catch { }
            WriteStatus(false);
            quit.Set();
        }
        public void Dispose() { Quit(); }
    }
}
