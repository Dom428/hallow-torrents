// Hallow Torrents 1.5 - the new parts of the main window: the "Speed & leaks" page (speed test, leak drill, socket watchdog), the
// watchdog's red alert on Transfers, and Settings > Performance.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HallowTorrents
{
    public partial class MainWindow
    {
        // ---------------- the watchdog's alert (Transfers and the Speed & leaks page)
        string TrippedText { get { return c.SnapMode ? c.SnapTripped : c.Core.Tripped; } }
        Border WatchdogBanner()
        {
            var t = TrippedText; if (t == null) return null;
            var b = Ui.Btn("I've checked - resume", () => { c.ResumeAfterWatchdog(); bannerKey = null; proofKey = null; Say("Resumed - torrents start again over the VPN."); Refresh(); }, true);
            b.VerticalAlignment = VerticalAlignment.Center;
            return Dialogs.Banner("triangle-exclamation", "Red", "RedBg", "Socket watchdog: torrents were stopped at once because this app had a socket outside the VPN - " + t + ". Nothing more can go out until you resume. If you don't know why, leave it stopped and check Settings > VPN.", b);
        }

        // ---------------- Settings > Performance
        void PerformanceSection(StackPanel page)
        {
            var S = c.S;
            var p = Section(page, "Performance", "How hard the engine works. Only numbers change here - every connection is still bound to your VPN and stops with it.");
            var note = Ui.Muted("", 12, 6);
            Action show = () => { var v = Perf.For(S, false); note.Text = Perf.Label(S.Perf) + ": " + v.Summary + "."; };
            p.Children.Add(Choice(new[] { new[] { "balanced", "Balanced (recommended)" }, new[] { "max", "Max speed" }, new[] { "light", "Light" } }, S.Perf, id => { S.Perf = id; Saved(); show(); if (!c.SnapMode) c.Core.Kick(); }));
            show(); p.Children.Add(note);
            p.Children.Add(Ui.Muted("Balanced talks to more peers at once than 1.4 and keeps a bigger disk cache - faster on busy torrents, still light on a gaming PC. Max speed goes further (more memory and CPU); Light is for older or busy PCs. While a game runs and \"While you play\" is set to limit the speed, a small gaming profile is used by itself.", 12, 6));
            var adv = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            p.Children.Add(Ui.Switch("Advanced numbers", false, on => adv.Visibility = on ? Visibility.Visible : Visibility.Collapsed));
            adv.Children.Add(Ui.Muted("0 = the profile's number. Applied straight away (also to running torrents).", 12, 6));
            Action<string, int, Action<int>, int, int> num = (label, value, set, min, max) => adv.Children.Add(Dialogs.Labeled(label, NumField(value, x => { set(x); Saved(); show(); if (!c.SnapMode) c.Core.Kick(); }, 0, max)));
            num("Peers at once (all torrents)", S.PerfConnections, x => S.PerfConnections = x, 10, 2000);
            num("Peers per torrent", S.PerfPerTorrent, x => S.PerfPerTorrent = x, 5, 1000);
            num("Connecting at once", S.PerfHalfOpen, x => S.PerfHalfOpen = x, 1, 100);
            num("Disk cache (MB)", S.PerfCacheMB, x => S.PerfCacheMB = x, 1, 512);
            num("Upload slots per torrent", S.PerfUploadSlots, x => S.PerfUploadSlots = x, 1, 50);
            num("Open files", S.PerfOpenFiles, x => S.PerfOpenFiles = x, 16, 2048);
            p.Children.Add(adv);
            p.Children.Add(Ui.Muted("uTP (the UDP transport some clients use) isn't in MonoTorrent 3.0.2, so Hallow Torrents uses TCP peers only. Speed test and leak drill: the Speed & leaks page.", 12, 0));
        }

        // ---------------- the Speed & leaks page
        StackPanel proofBox, speedTiles, drillList; TextBlock speedState, drillState, watchState; Border proofAlert; TextBox speedItem; string proofKey;
        FrameworkElement ProofPage()
        {
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
            proofAlert = new Border(); page.Children.Add(proofAlert);
            // speed test
            var s = Section(page, "Speed test", "Downloads a legal, well-seeded torrent you pick - through your VPN, like any other torrent - and measures it: average and peak speed, time, peers and CPU. The files go to \"" + (c.SnapMode ? @"C:\Users\Alex\Downloads\Hallow Torrents" : c.S.SaveFolder) + "\\Speed test\"; it's paused when it's done.");
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            speedItem = Ui.Field(""); speedItem.ToolTip = "A magnet link or a .torrent file";
            var start = Ui.Btn("Start", () => StartSpeed(speedItem.Text), true); start.Margin = new Thickness(8, 0, 0, 0);
            var ubuntu = Ui.Btn("Use Ubuntu's torrent", () =>
            {
                if (!Dialogs.Confirm(this, "Speed test with Ubuntu?", "Hallow Torrents fetches the current Ubuntu desktop .torrent from releases.ubuntu.com (over your VPN, only now) and downloads the image - about 6 GB - into the Speed test folder. Ubuntu is free and legal to share.", "Start", false)) return;
                StartSpeed("ubuntu");
            });
            ubuntu.Margin = new Thickness(8, 0, 0, 0);
            var stop = Ui.Btn("Stop", () => { if (!c.SnapMode) c.Proof.Speed.Stop(); Say("Stopping the speed test..."); }); stop.Margin = new Thickness(8, 0, 0, 0);
            foreach (var x in new FrameworkElement[] { stop, ubuntu, start }) { DockPanel.SetDock(x, Dock.Right); row.Children.Add(x); }
            row.Children.Add(speedItem); s.Children.Add(row);
            speedState = Ui.T("", 12.5, true, null, 8); s.Children.Add(speedState);
            speedTiles = new StackPanel(); s.Children.Add(speedTiles);
            s.Children.Add(Ui.Muted("Against qBittorrent: tools\\race.ps1 in the install folder runs the same torrent in Hallow Torrents and then in your qBittorrent (bound to the VPN adapter for the race) and compares them. You start it, with your VPN connected.", 12, 0));
            // leak drill
            var d = Section(page, "Leak drill", "Proves the VPN lock for real: while a torrent downloads it disconnects your VPN on purpose (Hallow VPN: through its pipe; your own VPN app: you disconnect it when asked), checks that torrents locked within 2 seconds, that this app had no socket on your normal connection (Windows' own connection tables, every 100 ms), that no data moved after the lock - then reconnects and checks the download carries on.");
            var run = Ui.Btn("Run leak drill", () =>
            {
                if (c.SnapMode) return;
                var why = c.Proof.Drill.Ready(); if (why != null) { Say(why); return; }
                var hallow = c.S.VpnSource == "hallow";
                if (!Dialogs.Confirm(this, "Run the leak drill?", hallow ? "Hallow VPN will be disconnected for a few seconds and connected again to the same server. Torrents stop while it's off and carry on afterwards." : "When the page says so, disconnect your VPN app (for example NordVPN) and connect it again a few seconds later. Torrents stop while it's off and carry on afterwards.", "Run it", false)) return;
                var e = c.Proof.Drill.Start(); Say(e ?? (hallow ? "Leak drill running - Hallow VPN disconnects now..." : "Leak drill running - disconnect your VPN app now.")); proofKey = null;
            }, true);
            run.HorizontalAlignment = HorizontalAlignment.Left; run.Margin = new Thickness(0, 0, 0, 8); d.Children.Add(run);
            drillState = Ui.T("", 12.5, true, null, 6); d.Children.Add(drillState);
            drillList = new StackPanel(); d.Children.Add(drillList);
            // watchdog
            var w = Section(page, "Socket watchdog", "Always on. Every 3 seconds it reads this app's own entries in Windows' TCP and UDP tables. A socket on any address other than the VPN's (loopback, the Radarr / Sonarr Web API on the address you picked, your own Prowlarr / Jackett at a private address and the update check excepted) stops all torrents at once and shows a red alert.");
            watchState = Ui.T("", 12.5, false, Ui.R("TextSoft"), 0); w.Children.Add(watchState);
            proofBox = page; proofKey = null; RefreshProof();
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        void StartSpeed(string item)
        {
            if (c.SnapMode) return;
            item = (item ?? "").Trim(); if (item.Length == 0) { Say("Paste a magnet link or a .torrent path - or use Ubuntu's torrent."); return; }
            Say(item == "ubuntu" ? "Getting Ubuntu's torrent from releases.ubuntu.com over the VPN..." : "Starting the speed test...");
            Task.Run(() => c.Proof.Speed.Start(item)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { Say(t.Result ?? "Speed test running - it shows its numbers here."); proofKey = null; RefreshProof(); RefreshTransfersIfShown(); })));
        }
        static FrameworkElement Tile(string label, string big, string small)
        {
            var sp = new StackPanel(); sp.Children.Add(Ui.Caps(label, 10.5));
            sp.Children.Add(new TextBlock { Text = big, FontFamily = Ui.Head, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ui.R("Text"), Margin = new Thickness(0, 4, 0, 0) });
            if (small != null) sp.Children.Add(Ui.Muted(small, 11.5, 0));
            var card = Ui.Card(sp, 12); card.Width = 176; card.Margin = new Thickness(0, 0, 10, 10); return card;
        }
        void RefreshProof()
        {
            if (proofBox == null) return;
            var speed = c.SnapMode ? c.SnapSpeed : (c.Proof.Speed.Running ? c.Proof.Speed.Current : c.Proof.Speed.Last);
            var speedRunning = c.SnapMode ? c.SnapSpeed != null && c.SnapSpeed.State == "running" : c.Proof.Speed.Running;
            var drill = c.SnapMode ? c.SnapDrill : (c.Proof.Drill.Running ? c.Proof.Drill.Current : c.Proof.Drill.Last);
            var drillRunning = !c.SnapMode && c.Proof.Drill.Running;
            var wd = c.SnapMode ? null : c.Proof.Watchdog;
            var key = (speed == null ? "" : speed.State + speed.Bytes + speed.PeersMax) + "|" + (drill == null ? "" : drill.State + drill.Checks.Count + drill.Pass) + "|" + TrippedText + "|" + (wd == null ? 0 : wd.Checks / 5) + "|" + c.Locked;
            if (key == proofKey) return; proofKey = key;
            var alert = WatchdogBanner(); proofAlert.Child = alert; proofAlert.Visibility = alert == null ? Visibility.Collapsed : Visibility.Visible;
            // speed test
            speedTiles.Children.Clear();
            if (speed == null) { speedState.Text = "No speed test yet."; speedState.Foreground = Ui.R("Muted"); }
            else
            {
                var st = speed.State == "done" ? "Finished" : speed.State == "running" ? "Running" : speed.State == "waiting-vpn" ? "Waiting for the VPN (time without it doesn't count)" : speed.State == "held" ? "Held (a game or the queue) - that time doesn't count" : speed.State == "stopped" ? "Stopped" : speed.State == "error" ? "Couldn't finish: " + speed.Error : "Starting...";
                speedState.Text = speed.Name + " - " + st + (speed.Size > 0 ? " - " + speed.Progress.ToString("0.0") + "% of " + Fmt.Bytes(speed.Size) : "");
                speedState.Foreground = speed.State == "error" ? Ui.R("Red") : speed.State == "done" ? Ui.R("Green") : Ui.R("Text");
                var tiles = new WrapPanel();
                tiles.Children.Add(Tile("Average", Fmt.Rate((long)(speed.AvgMBps * 1048576)), (speed.AvgMBps * 8.388608).ToString("0") + " Mbit/s"));
                tiles.Children.Add(Tile("Peak (3 s)", Fmt.Rate((long)(speed.PeakMBps * 1048576)), (speed.PeakMBps * 8.388608).ToString("0") + " Mbit/s"));
                tiles.Children.Add(Tile("Time", Fmt.Span(speed.ActiveSeconds), "first data after " + speed.FirstByteSeconds.ToString("0") + " s"));
                tiles.Children.Add(Tile("Peers", speed.PeersMax.ToString(), "max \u00B7 " + speed.PeersAvg.ToString("0") + " on average"));
                tiles.Children.Add(Tile("CPU", speed.CpuPct.ToString("0.0") + "%", "of the whole CPU (" + speed.CpuPctOneCore.ToString("0") + "% of one core)"));
                speedTiles.Children.Add(tiles);
                speedTiles.Children.Add(Ui.Muted("Profile: " + Perf.Label(speed.Profile) + " \u00B7 started " + When(speed.Started) + (speed.Finished != null ? " \u00B7 finished " + When(speed.Finished) : "") + (speedRunning ? "" : ""), 11.5, 8));
            }
            // leak drill
            drillList.Children.Clear();
            if (drill == null) { drillState.Text = "Not run yet. It needs your VPN connected and a torrent downloading (the speed test's Ubuntu torrent is ideal)."; drillState.Foreground = Ui.R("Muted"); }
            else
            {
                drillState.Text = drillRunning ? (drill.State == "waiting-for-you" ? "Waiting for you: " + (c.Vpn.Ok ? "disconnect your VPN app now." : "connect your VPN app again.") : "Running...") : (drill.Pass ? "PASS" : "FAIL") + " - " + drill.Mode + ", " + When(drill.Started) + (drill.Error != null ? " - " + drill.Error : "");
                drillState.Foreground = drillRunning ? Ui.R("Text") : drill.Pass ? Ui.R("Green") : Ui.R("Red");
                List<DrillCheck> checks; lock (drill.Checks) checks = drill.Checks.ToList();
                foreach (var ch in checks)
                {
                    var r = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var pill = Ui.Pill(ch.Pass ? "PASS" : "FAIL", ch.Pass ? "Green" : "Red", ch.Pass ? "GreenBg" : "RedBg"); pill.Width = 52; pill.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(pill, Dock.Left); r.Children.Add(pill);
                    r.Children.Add(new TextBlock { Text = ch.Name + (string.IsNullOrEmpty(ch.Detail) ? "" : "  \u00B7  " + ch.Detail), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                    drillList.Children.Add(r);
                }
                foreach (var p in drill.Problems.Take(8)) drillList.Children.Add(Ui.T("Socket: " + p, 11.5, false, Ui.R("Red"), 0));
            }
            // watchdog
            if (c.SnapMode) watchState.Text = "On \u00B7 1,204 checks this session \u00B7 last check 2 s ago: 31 sockets, all on the VPN address or this PC.";
            else
            {
                var bad = TrippedText;
                watchState.Text = "On \u00B7 " + wd.Checks.ToString("N0") + " checks this session" + (wd.Checks > 0 ? " \u00B7 last check " + Math.Max(0, (int)(DateTime.Now - wd.LastCheck).TotalSeconds) + " s ago: " + wd.LastRows + " sockets" + (bad == null ? ", all allowed." : "") : "") + (bad != null ? " \u00B7 STOPPED: " + bad : "");
                watchState.Foreground = bad != null ? Ui.R("Red") : Ui.R("TextSoft");
            }
        }
    }
}
