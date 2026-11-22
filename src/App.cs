// Hallow Torrents - a game-aware torrent client on MonoTorrent that only ever runs over your VPN.
//   * VPN required: torrents run only while Hallow VPN says connected (or the VPN adapter you picked is up), and every socket is bound
//     to the VPN's IP. If the VPN drops or its IP changes, the engine is stopped within a second. There is no way to turn this off.
//   * Game-aware: while a game runs (Delta Force, Discovery, Hallow Hub's playing state, your own list) it pauses everything or
//     limits the speed, and goes back afterwards.
//   * Safety: finished downloads are scanned by Microsoft Defender (Idle priority), suspicious files are flagged, nothing is ever
//     opened by itself. Optional media library hand-off with Jellyfin-friendly names.
//   * The family link: pipe (ping, status, add, pause-all, resume-all, menu, open, invoke, quit), status.json, Hub's Hallow icon.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Torrents")]
[assembly: AssemblyProduct("Hallow Torrents")]
[assembly: AssemblyDescription("A game-aware torrent client that only runs over your VPN")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace HallowTorrents
{
    public static class Program
    {
        public const string Version = "1.1";
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        public static void Main(string[] args)
        {
            int ci = Array.IndexOf(args, "--cmd");
            if (ci >= 0) { Environment.Exit(Family.RunCmd(ci + 1 < args.Length ? args[ci + 1] : "")); return; }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; TorSettings.Frozen = true;
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SnapShots.All(sa, args[si + 1]); return;
            }
            // a .torrent path or magnet link (double-click / the browser): hand it to the running copy, or open with it
            var item = args.FirstOrDefault(a => !a.StartsWith("--") && (a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)));
            var leaf = TorSettings.TestDir != null ? "Test" + Path.GetFileName(TorSettings.TestDir.TrimEnd('\\')) : "";
            bool created;
            var mutex = new Mutex(true, @"Local\HallowTorrents" + leaf, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowTorrentsShow" + leaf);
            if (!created)
            {
                if (item != null) { for (int i = 0; i < 20; i++) { var r = Family.Send("Torrents", "add " + item, 5000); if (r != null) break; Thread.Sleep(250); } }
                else showEvent.Set();
                return;
            }
            Crash.Install();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            app.DispatcherUnhandledException += (s, e) => { Crash.Write(e.Exception); e.Handled = true; Environment.Exit(1); };
            var c = new Controller(args.Contains("--tray") || item != null && args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) app.Dispatcher.BeginInvoke(new Action(() => c.ShowMain(null))); }) { IsBackground = true }.Start();
            if (item != null) app.Dispatcher.BeginInvoke(new Action(() => c.AddFromOutside(item)), DispatcherPriority.ApplicationIdle);
            app.Run();
            GC.KeepAlive(mutex);
        }

        public static string AccentHex = Accent.Default;
        // the family console look; light = from settings + theme.json + Windows (Mode.Resolve), or forced (--snap)
        public static void LoadTheme(Application app, bool? light = null)
        {
            var st = TorSettings.Load();
            AccentHex = Accent.Current(st.UseHallowAccent);
            Mode.IsLight = light ?? Mode.Resolve(st.Mode, Accent.ReadMode(), Mode.Sys);
            var x = Res("HallowTorrents.Theme.xaml");
            if (Mode.IsLight) x = Mode.LightTheme(x, AccentHex);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Accent.ApplyTo(x, Mode.IsLight ? AccentHex : Ui.Readable(AccentHex))));
        }
        static string Res(string name) { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) using (var r = new StreamReader(s)) return r.ReadToEnd(); }
    }

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

    public class Controller
    {
        public TorSettings S = TorSettings.Load();
        public readonly bool SnapMode;
        readonly Dispatcher ui;
        public Core Core; public VpnWatch Watch;
        MainWindow main; WF.NotifyIcon tray; bool hubHosts, hubLists, quitting, balloonOwn;
        readonly List<string> log = new List<string>();
        public string Game; public VpnState Vpn = new VpnState { Message = "Checking the VPN..." };
        public event Action Ticked;
        FileSystemWatcher accentWatch; DispatcherTimer modePoll;
        public IReg Reg = TorSettings.TestDir != null ? (IReg)new FakeReg() : new RealReg();
        public List<TorrentView> SnapViews;   // --snap: made-up torrents
        public Rss Rss;                       // v1.1: RSS auto-download (over the VPN only)
        public Stats SnapStats; public int SnapRanges;   // --snap: made-up statistics
        public Stats StatsNow { get { return SnapMode ? SnapStats : Core.Stats; } }

        public Controller(bool trayOnly, bool snapMode = false)
        {
            SnapMode = snapMode; ui = Dispatcher.CurrentDispatcher;
            if (snapMode) return;
            S.Save();
            Core = new Core(TorSettings.Dir, () => S); Core.Log += Log; Core.Completed += x => Task.Run(() => Finished(x));
            Rss = new Rss(Core, TorSettings.Dir); Rss.Log += Log; Rss.Notify += (t, m) => ui.BeginInvoke(new Action(() => { Notify(t, m); var h = Ticked; if (h != null) h(); }));
            var rt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; rt.Tick += (s, e) => KickRss(); rt.Start();
            var bt = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) }; bt.Tick += (s, e) => CheckBlocklist(); bt.Start();
            INetworks nets = TorSettings.Fake ? (INetworks)new FakeNetworks(Path.Combine(TorSettings.Dir, "fake-adapters.txt")) : new RealNetworks();
            Watch = new VpnWatch(() => S, nets, !TorSettings.Fake);
            Watch.Changed += (o, n) => VpnChanged(n);
            Family.Serve("Torrents", Command);
            Watch.Start();
            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; tick.Tick += (s, e) => Tick(); tick.Start();
            var gt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; gt.Tick += (s, e) => CheckGame(); gt.Start();
            var wf = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; wf.Tick += (s, e) => CheckWatchFolder(); wf.Start();
            if (!TorSettings.Headless)
            {
                SetupTray();
                var ht = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; ht.Tick += (s, e) => CheckHost(); ht.Start(); CheckHost();
                accentWatch = Accent.Watch(() => ui.BeginInvoke(new Action(() => ApplyTheme(false))));
                modePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; modePoll.Tick += (s, e) => ApplyTheme(false); modePoll.Start();
                if (TorSettings.TestDir == null || Environment.GetEnvironmentVariable("HALLOW_TORRENTS_API") != null)
                {
                    var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(6); DateTime last; if (!DateTime.TryParse(S.LastUpdateCheck, out last) || (DateTime.Now - last).TotalHours >= 24) CheckForUpdate(false); };
                    upd.Start();
                }
                ui.BeginInvoke(new Action(ShowCrashNotice), DispatcherPriority.ApplicationIdle);
            }
            // anything that finished while we weren't looking (scan / hand-off interrupted) - pick it up again
            foreach (var x in Core.Entries.Where(e => e.CompletedAt != null && (e.Scan == "waiting" || e.Scan == "scanning"))) { var xx = x; Task.Run(() => Finished(xx)); }
            CheckGame(); WriteStatus();
            var st = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; st.Tick += (s, e) => WriteStatus(); st.Start();
            if (!trayOnly && !TorSettings.Headless) ShowMain(null);
        }
        void Log(string s) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s); if (log.Count > 120) log.RemoveAt(0); } }

        // ---------------- the VPN (on the watch thread: the engine is stopped right here, not on the UI thread)
        void VpnChanged(VpnState n)
        {
            var was = Vpn; Vpn = n;
            Log("vpn: " + (n.Ok ? "up " + n.Ip + " (" + n.Adapter + ")" : "down - " + n.Message));
            try { Core.SetVpn(n).Wait(5000); } catch (Exception e) { Log("engine: " + e.GetBaseException().Message); }
            ui.BeginInvoke(new Action(() =>
            {
                if (was.Ok && !n.Ok && Core.Entries.Any(x => !x.UserPaused)) Notify("Torrents stopped: the VPN dropped", "Everything is stopped until your VPN is back. Nothing went out over your normal connection.");
                if (n.Ok && Core.EngineUp) { KickRss(); CheckBlocklist(); }
                WriteStatus(); var h = Ticked; if (h != null) h();
            }));
        }
        public bool Locked { get { return SnapMode ? SnapLocked : !Core.EngineUp; } }
        public bool SnapLocked;
        public string ConnectVpn()
        {
            if (SnapMode) return null;
            var r = Family.Send("VPN", "connect fastest", 5000);
            if (r == null) return "Hallow VPN isn't running. Start it (or connect your own VPN app) and torrents start by themselves.";
            var e = Family.Error(r, null); return e == null ? null : "Hallow VPN: " + e;
        }

        // ---------------- every second: totals, limits (settings + schedule + game), the queue, finished downloads
        bool ticking;
        void Tick()
        {
            if (quitting || ticking) return;
            var gameLimit = Game != null && S.GameMode == "limit" ? S.GameKBps : 0;
            var rule = S.ScheduleOn ? Scheduler.Active(S.Schedule, DateTime.Now) : null;
            Core.LimitDown = Scheduler.Min(S.DownKBps, rule == null ? 0 : rule.DownKBps, gameLimit);
            Core.LimitUp = Scheduler.Min(S.UpKBps, rule == null ? 0 : rule.UpKBps, gameLimit);
            Core.GameHold = Game != null && S.GameMode == "pause";
            ticking = true;
            Task.Run(async () => { try { await Core.Tick().ConfigureAwait(false); } catch (Exception e) { Log("tick: " + e.Message); } })
                .ContinueWith(t => ui.BeginInvoke(new Action(() => { ticking = false; var h = Ticked; if (h != null) h(); })));
            if (++saveCount % 15 == 0) Task.Run(() => Core.Save());
        }
        int saveCount;
        void CheckGame()
        {
            if (quitting) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string g = null; try { g = Games.Running(S.ExtraGames); } catch { }
                ui.BeginInvoke(new Action(() =>
                {
                    if (g == Game) return;
                    var was = Game; Game = g; Log("game: " + (g ?? "none"));
                    if (S.GameMode != "off" && (g == null) != (was == null)) { Core.GameHold = g != null && S.GameMode == "pause"; Core.Kick(); WriteStatus(); var h = Ticked; if (h != null) h(); }
                }));
            });
        }
        void CheckWatchFolder()
        {
            var d = S.WatchFolder; if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) return;
            try
            {
                foreach (var f in Directory.GetFiles(d, "*.torrent"))
                {
                    if ((DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds < 2) continue;   // still being written
                    string err; var x = Core.AddTorrentFile(f, null, null, null, true, out err);
                    var done = f + (x != null ? ".added" : ".failed"); try { if (File.Exists(done)) done = f + "." + DateTime.Now.Ticks + (x != null ? ".added" : ".failed"); File.Move(f, done); } catch { }
                    Log("watch folder: " + Path.GetFileName(f) + " " + (x != null ? "added" : err));
                    if (x != null) Notify("Added from your watch folder", x.Name);
                }
            }
            catch (Exception e) { Log("watch folder: " + e.Message); }
        }
        async Task Finished(Entry x)
        {
            try { await Finish.Run(Core, x, S, (t, m) => ui.BeginInvoke(new Action(() => Notify(t, m)))).ConfigureAwait(false); }
            catch (Exception e) { Log("finish: " + e.Message); }
            ui.BeginInvoke(new Action(() => { var h = Ticked; if (h != null) h(); }));
            if (x.Scan != "threats" && (x.Flags == null || x.Flags.Count == 0)) ui.BeginInvoke(new Action(() => Notify("Download finished", x.Name)));
        }
        public Task SendToLibrary(string id)
        {
            var x = Core.Get(id); var c = x == null ? null : S.Cat(x.Category);
            if (x == null || c == null || string.IsNullOrWhiteSpace(c.LibraryFolder)) { Say("Set a library folder for its category in Settings first."); return Task.FromResult(0); }
            return Task.Run(async () => { x.Handoff = await Finish.Send(Core, x, c).ConfigureAwait(false); Core.Save(); ui.BeginInvoke(new Action(() => { Say(x.Handoff); var h = Ticked; if (h != null) h(); })); });
        }
        public void ScanAgain(string id)
        {
            var x = Core.Get(id); if (x == null) return;
            Task.Run(() => { x.Scan = "scanning"; var r = DefenderScan.Run(Core.RootOf(id)); x.Scan = r[0]; x.ScanText = r[1]; Core.Save(); ui.BeginInvoke(new Action(() => { Say(r[1]); var h = Ticked; if (h != null) h(); })); });
        }

        // ---------------- v1.1: RSS (only while the VPN is up) and the blocklist from a URL (downloaded over the VPN every N days)
        public void KickRss() { if (SnapMode || quitting || Rss == null || !Core.EngineUp || Rss.Busy) return; Task.Run(async () => { try { await Rss.Tick(DateTime.Now).ConfigureAwait(false); } catch (Exception e) { Log("rss: " + e.Message); } ui.BeginInvoke(new Action(() => { var h = Ticked; if (h != null) h(); })); }); }
        bool blocklistBusy;
        public void CheckBlocklist()
        {
            if (SnapMode || string.IsNullOrWhiteSpace(S.BlocklistUrl) || !Core.EngineUp) return;
            DateTime last; if (DateTime.TryParse(S.BlocklistUpdated, out last) && (DateTime.Now - last).TotalDays < S.BlocklistDays && File.Exists(Core.BlocklistUrlFile)) return;
            UpdateBlocklist(null);
        }
        public void UpdateBlocklist(Action<string> done)
        {
            if (SnapMode) { if (done != null) done("Updated."); return; }
            if (string.IsNullOrWhiteSpace(S.BlocklistUrl)) { Core.ReloadBlocklist(); if (done != null) done("No blocklist address set."); return; }
            if (!Core.EngineUp) { if (done != null) done("The blocklist is downloaded over the VPN only - connect it first."); return; }
            if (blocklistBusy) return; blocklistBusy = true;
            var url = S.BlocklistUrl;
            Task.Run(() =>
            {
                int n; var err = Blocklist.Update(url, Core.BoundIp, Core.BoundDns, Core.BlocklistUrlFile, out n);
                ui.BeginInvoke(new Action(() =>
                {
                    blocklistBusy = false;
                    if (err == null) { S.BlocklistUpdated = DateTime.Now.ToString("o"); S.BlocklistNote = "Downloaded " + n.ToString("N0") + " ranges over the VPN."; }
                    else S.BlocklistNote = "Couldn't update it: " + err;
                    S.Save(); Core.ReloadBlocklist(); Log("blocklist url: " + (err ?? n + " ranges"));
                    if (done != null) done(S.BlocklistNote);
                    var h = Ticked; if (h != null) h();
                }));
            });
        }

        // ---------------- adding
        // from outside (double-click, the browser, the pipe): the Add window when it can be shown, otherwise straight in.
        // category (v1.1, "add <magnet> --category <c>"): one of your categories - its folder is used, and the Add window has it picked.
        public string AddFromOutside(string item, string category = null)
        {
            item = (item ?? "").Trim().Trim('"');
            bool magnet = item.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
            if (!magnet && !(item.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(item))) return "error only magnet links and .torrent files can be added";
            if (category != null) { var cat = S.Cat(category.Trim()); if (cat == null) return "error no category called " + category.Trim() + " (" + string.Join(", ", S.Categories.Select(c => c.Name)) + ")"; category = cat.Name; }
            if (S.AskOnAdd && !TorSettings.Headless && !SnapMode) { ShowMain(null); if (main != null) main.AskAdd(item, category); return "ok " + Family.Json(new Dictionary<string, object> { { "asking", true }, { "category", category } }); }
            string err; var x = magnet ? Core.AddMagnet(item, null, category, true, out err) : Core.AddTorrentFile(item, null, category, null, true, out err);
            if (x == null) return "error " + err;
            Notify("Torrent added", x.Name + (Locked ? " - it starts when your VPN is connected." : ""));
            return "ok " + Family.Json(new Dictionary<string, object> { { "id", x.Id }, { "asking", false }, { "category", x.Category } });
        }
        // "add <magnet or path> [--category <name>]" -> (item, category or null)
        public static string[] SplitAdd(string arg)
        {
            var i = arg.LastIndexOf(" --category ", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return new[] { arg.Trim(), null };
            var cat = arg.Substring(i + 12).Trim().Trim('"');
            return new[] { arg.Substring(0, i).Trim(), cat.Length == 0 ? null : cat };
        }

        // ---------------- window
        public void ShowMain(string page)
        {
            if (TorSettings.Headless || SnapMode) return;
            if (main == null) main = new MainWindow(this);
            if (page != null) main.Show(page);
            main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal; main.Activate();
        }
        public void MainClosed()
        {
            main = null;
            if (!S.TrayHintShown && !quitting) { S.TrayHintShown = true; S.Save(); Notify("Hallow Torrents is still here", "It keeps your torrents going in the tray (only while your VPN is on). Right-click the icon to quit."); }
        }
        public void Quit()
        {
            if (quitting) return; quitting = true;
            Log("quit");
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (Watch != null) Watch.Dispose();
            try { Core.Dispose(); } catch { }
            WriteStatus(false);
            if (main != null) main.Exiting = true;
            Application.Current.Shutdown();
        }
        public void Say(string text) { if (main != null) main.Say(text); }
        public List<TorrentView> Views(bool files) { return SnapMode ? SnapViews : Core.Snapshot(files); }
        public long DownRate { get { return SnapMode ? SnapViews.Sum(v => v.DownRate) : Core.TotalDown; } }
        public long UpRate { get { return SnapMode ? SnapViews.Sum(v => v.UpRate) : Core.TotalUp; } }

        // ---------------- settings with side effects
        const string RunName = "HallowTorrents";
        string RunFile { get { return Path.Combine(TorSettings.Dir, "test-autostart.txt"); } }
        public bool StartsWithWindows()
        {
            if (TorSettings.TestDir != null || SnapMode) return File.Exists(RunFile);
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue(RunName) != null; } catch { return false; }
        }
        public void SetStartWithWindows(bool on)
        {
            if (SnapMode) return;
            if (TorSettings.TestDir != null) { if (on) File.WriteAllText(RunFile, "\"HallowTorrents.exe\" --tray"); else if (File.Exists(RunFile)) File.Delete(RunFile); return; }
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (on) k.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue(RunName, false); } } catch { }
        }
        public string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        public bool HandlerOn { get { return !SnapMode && Handler.IsRegistered(Reg, ExePath); } }
        public void SetHandler(bool on)
        {
            if (SnapMode) return;
            try { if (on) Handler.Register(Reg, ExePath); else Handler.Unregister(Reg, ExePath); if (!(Reg is FakeReg)) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch (Exception e) { Say("Couldn't change it: " + e.Message); }
            S.HandlerAsked = true; S.Save(); Log("handler " + (on ? "on" : "off"));
        }
        [DllImport("shell32.dll")] static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);
        public Task RestartEngine() { return SnapMode ? Task.FromResult(0) : Task.Run(() => Core.Restart()); }
        public void OpenFolder(string path)
        {
            if (SnapMode || TorSettings.TestDir != null || string.IsNullOrEmpty(path)) return;
            // the folder only - files are never opened by Hallow Torrents
            try { if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\""); else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\""); else Say("It isn't there (yet)."); } catch { }
        }
        public void OpenUrl(string url) { if (SnapMode || TorSettings.TestDir != null) return; try { Process.Start(url); } catch { } }

        // ---------------- tray, Hub's Hallow icon, notifications, theme, updates, crash notice
        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Torrents", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(ExePath); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Open Hallow Torrents"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowMain("transfers"); menu.Items.Add(open);
            foreach (var it in MenuItems().Cast<Dictionary<string, object>>())
            {
                var id = (string)it["id"]; if (id == "-") { menu.Items.Add(new WF.ToolStripSeparator()); continue; }
                var mi = new WF.ToolStripMenuItem((string)it["label"]); mi.Click += (s, e) => Invoke(id); menu.Items.Add(mi);
            }
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu; StyleTray();
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMain(null); };
        }
        void StyleTray()
        {
            if (tray == null) return; var p = TrayColors.For(Mode.IsLight);
            tray.ContextMenuStrip.Renderer = new WF.ToolStripProfessionalRenderer(p); tray.ContextMenuStrip.BackColor = p.Back; tray.ContextMenuStrip.ForeColor = p.Fore;
            foreach (WF.ToolStripItem i in tray.ContextMenuStrip.Items) i.ForeColor = p.Fore;
        }
        public void CheckHost()
        {
            if (tray == null) return;
            if (!S.UseHallowIcon) { SetHosted(false, false); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = false, lists = false;
                try { var r = Family.Send("Hub", "tray-host", 1500); if (r != null && r.StartsWith("ok {")) HubHosting(r.Substring(3), out on, out lists); } catch { }
                ui.BeginInvoke(new Action(() => SetHosted(on && S.UseHallowIcon, lists)));
            });
        }
        public static void HubHosting(string json, out bool on, out bool lists)
        {
            on = false; lists = false;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); object v;
                on = d != null && d.TryGetValue("on", out v) && v is bool && (bool)v;
                if (!on) return;
                if (d.TryGetValue("generic", out v) && v is bool && (bool)v) { lists = true; return; }
                if (d.TryGetValue("apps", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var a in (System.Collections.IEnumerable)v) if (a is string && string.Equals((string)a, "Torrents", StringComparison.OrdinalIgnoreCase)) lists = true;
            }
            catch { on = false; lists = false; }
        }
        void SetHosted(bool on, bool lists) { hubHosts = on; hubLists = on && lists; if (tray != null && !balloonOwn) tray.Visible = !hubLists; }
        public int Notifications;
        public void Notify(string title, string text)
        {
            Notifications++; Log("notify: " + title);
            if (tray == null || TorSettings.TestDir != null) return;
            if (!hubHosts) { tray.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Family.Error(Family.Send("Hub", "notify Torrents\t" + title + "\t" + text, 2000), "no answer") == null) return;
                ui.BeginInvoke(new Action(() => { balloonOwn = true; tray.Visible = true; tray.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.None); var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) }; t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts, hubLists); }; t.Start(); }));
            });
        }
        public void ApplyTheme(bool force)
        {
            var a = Accent.Current(S.UseHallowAccent); var light = Mode.Resolve(S.Mode, Accent.ReadMode(), Mode.Sys);
            if (!force && a == Program.AccentHex && light == Mode.IsLight) return;
            Program.LoadTheme(Application.Current);
            if (main != null) main.Rebuild();
            StyleTray(); Log("theme: " + Program.AccentHex + " " + (Mode.IsLight ? "light" : "dark"));
        }
        GhRelease pending; public GhRelease PendingUpdate { get { return pending; } }
        public void CheckForUpdate(bool manual)
        {
            if (manual) Say("Checking GitHub for a newer Hallow Torrents...");
            new Thread(() =>
            {
                var r = Updater.Latest();
                ui.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual) Say("Couldn't reach GitHub - try again later."); return; }
                    if (!Updater.IsNewer(r)) { if (manual) Say("You have the newest version (v" + Program.Version + ")."); return; }
                    pending = r; if (main != null) main.ShowUpdate("Update to v" + r.Version.ToString(2));
                    Say("Hallow Torrents v" + r.Version.ToString(2) + " is out - click Update.");
                }));
            }) { IsBackground = true }.Start();
        }
        public void InstallUpdate()
        {
            if (pending == null) return;
            if (!Dialogs.Confirm(main, "Install Hallow Torrents v" + pending.Version.ToString(2) + "?", "It downloads from GitHub, checks the file and puts the new version in place. Your torrents and settings stay; torrents pick up where they were.", "Update", false)) return;
            var r = pending; Say("Updating...");
            new Thread(() => { var err = Updater.Install(r); ui.BeginInvoke(new Action(() => Say(err ?? "Installing the update..."))); }) { IsBackground = true }.Start();
        }
        void ShowCrashNotice()
        {
            var f = Crash.Last(); if (f == null) return;
            var name = Path.GetFileName(f); if (name == S.LastCrashShown) return;
            S.LastCrashShown = name; S.Save();
            CrashNotice.Make(f).Show();
        }

        // ---------------- family link: status.json and the pipe. Counts and speeds only - never torrent names, files or addresses.
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
            // v1.1 (counts only)
            d["rssFeeds"] = Rss == null ? 0 : Rss.Data.Feeds.Count; d["rssRules"] = Rss == null ? 0 : Rss.Data.Rules.Count(r => r.Enabled);
            d["blocklistRanges"] = Core.BlocklistRanges;
            if (TorSettings.TestDir != null) { lock (log) d["log"] = log.ToArray(); d["notifications"] = Notifications; d["boundIp"] = Core.BoundIp == null ? null : Core.BoundIp.ToString(); }
            return d;
        }
        public void WriteStatus(bool running = true) { if (!SnapMode) { var d = StatusData(running); ThreadPool.QueueUserWorkItem(_ => Family.WriteStatus(Family.Json(d))); } }
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Torrents" }, { "version", Program.Version } });
                case "status": { var d = StatusData(true); Family.WriteStatus(Family.Json(d)); return "ok " + Family.Json(d); }
                case "add": { if (arg.Length == 0) return "error add <magnet link or .torrent path> [--category <name>]"; var p = SplitAdd(arg); if (arg.TrimEnd().EndsWith("--category", StringComparison.OrdinalIgnoreCase)) return "error --category needs a name"; return OnUi(() => AddFromOutside(p[0], p[1])); }
                case "pause-all": Core.PauseAll(); WriteStatus(); return "ok";
                case "resume-all": Core.ResumeAll(); WriteStatus(); return "ok";
                case "open": return OnUi(() => { var p = arg.Length > 0 ? MainWindow.PageId(arg) : null; if (arg.Length > 0 && p == null) return "error no page called " + arg; ShowMain(p); return "ok"; });
                case "menu": return OnUi(() => "ok " + Family.Json(new Dictionary<string, object> { { "app", "Torrents" }, { "items", MenuItems() } }));
                case "invoke": return OnUi(() => Invoke(arg));
                case "quit": new Thread(() => { Thread.Sleep(300); ui.BeginInvoke(new Action(Quit)); }) { IsBackground = true }.Start(); return "ok";
            }
            if (TorSettings.TestDir != null && cmd.StartsWith("test-")) return OnUi(() => TestCommand(cmd, arg));
            return "error unknown command";
        }
        string OnUi(Func<string> f)
        {
            string r = "error no answer in time";
            var op = ui.BeginInvoke(new Action(() => { try { r = f(); } catch (Exception e) { r = "error " + Family.OneLine(e.Message); } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : "error no answer in time";
        }
        public List<object> MenuItems()
        {
            return new List<object> {
                new Dictionary<string, object> { { "id", "pause-all" }, { "label", "Pause all" } },
                new Dictionary<string, object> { { "id", "resume-all" }, { "label", "Resume all" } },
                new Dictionary<string, object> { { "id", "-" } },
                new Dictionary<string, object> { { "id", "add-magnet" }, { "label", "Add magnet link..." } },
                new Dictionary<string, object> { { "id", "connect-vpn" }, { "label", "Connect Hallow VPN" }, { "enabled", Locked } },
                new Dictionary<string, object> { { "id", "settings" }, { "label", "Settings" } } };
        }
        string Invoke(string id)
        {
            switch (id)
            {
                case "pause-all": Core.PauseAll(); WriteStatus(); return "ok";
                case "resume-all": Core.ResumeAll(); WriteStatus(); return "ok";
                case "add-magnet": ShowMain("transfers"); if (main != null) main.AskMagnet(); return "ok";
                case "connect-vpn": { var e = ConnectVpn(); return e == null ? "ok" : "error " + e; }
                case "settings": ShowMain("settings"); return "ok";
                case "transfers": ShowMain("transfers"); return "ok";
            }
            return "error unknown item";
        }
        // developer tests only (HALLOW_TORRENTS_TESTDIR)
        string TestCommand(string cmd, string arg)
        {
            switch (cmd)
            {
                case "test-list": return "ok " + Family.Json(Core.Snapshot(false).Select(v => v.Name + "|" + v.State + "|" + v.Category).ToList());
                case "test-vpn": return "ok " + Family.Json(new Dictionary<string, object> { { "ok", Vpn.Ok }, { "ip", Vpn.Ip == null ? null : Vpn.Ip.ToString() }, { "engine", Core.EngineUp }, { "message", Vpn.Message } });
                case "test-handler": SetHandler(arg == "on"); return "ok " + HandlerOn;
                case "test-autostart": SetStartWithWindows(arg == "on"); return "ok " + StartsWithWindows();
                case "test-set": { var p = arg.Split(new[] { ' ' }, 2); if (p[0] == "gamemode") S.GameMode = p[1]; else if (p[0] == "vpnsource") S.VpnSource = p[1]; else if (p[0] == "vpnadapter") S.VpnAdapter = p[1]; else return "error unknown setting"; S.Fix(); S.Save(); return "ok"; }
                case "test-game": CheckGame(); return "ok";
            }
            return "error unknown test command";
        }
    }
}
