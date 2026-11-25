// Hallow Torrents main window: Transfers (the VPN lock banner, the list, details: general / files / safety) and Settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HallowTorrents
{
    public partial class MainWindow : Window
    {
        readonly Controller c;
        string page = "transfers";
        readonly Dictionary<string, Button> nav = new Dictionary<string, Button>();
        Border content; TextBlock sayText, meter; Button updateBtn; DispatcherTimer sayTimer;
        public bool Exiting;
        public static readonly string[][] Pages = { new[] { "transfers", "Transfers", "arrows-up-down" }, new[] { "search", "Search", "magnifying-glass" }, new[] { "rss", "RSS", "rss" }, new[] { "stats", "Statistics", "chart-simple" }, new[] { "apps", "Radarr / Sonarr", "share-from-square" }, new[] { "nas", "Media server / NAS", "folder-tree" }, new[] { "proof", "Speed & leaks", "gauge-high" }, new[] { "settings", "Settings", "gear" } };
        public static string PageId(string s)
        {
            var v = (s ?? "").Trim().ToLowerInvariant(); if (v == "list" || v == "torrents") v = "transfers"; if (v == "find" || v == "indexers") v = "search"; if (v == "webapi" || v == "radarr" || v == "sonarr" || v == "prowlarr" || v == "arr") v = "apps";
            if (v == "jellyfin" || v == "emby" || v == "plex" || v == "media" || v == "library") v = "nas";
            if (v == "speed" || v == "speedtest" || v == "leak-drill" || v == "leaks" || v == "watchdog" || v == "performance") v = "proof";
            return Pages.Any(p => p[0] == v) ? v : null;
        }

        public MainWindow(Controller controller)
        {
            c = controller;
            Ui.Dress(this, "Hallow Torrents", 1260);
            SizeToContent = SizeToContent.Manual; Height = 780; MinWidth = 980; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Rebuild();
            Action ticked = () => Dispatcher.BeginInvoke(new Action(Refresh));
            c.Ticked += ticked;
            Closed += (s, e) => { c.Ticked -= ticked; c.MainClosed(); };
            AllowDrop = true;
            Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) foreach (var f in ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(x => x.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))) AskAdd(f);
                else if (e.Data.GetDataPresent(DataFormats.Text)) { var t = ((string)e.Data.GetData(DataFormats.Text) ?? "").Trim(); if (t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) AskAdd(t); }
            };
        }

        public void Rebuild()
        {
            Ui.Restyle(this); Ui.TitleBar(this);
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(226) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var side = new DockPanel { Background = Ui.R("Side"), LastChildFill = false };
            var head = new StackPanel { Margin = new Thickness(16, 18, 16, 18) };
            head.Children.Add(Ui.Header("TORRENTS", 16));
            head.Children.Add(Ui.Muted("Only over your VPN", 11.5, 0));
            DockPanel.SetDock(head, Dock.Top); side.Children.Add(head);
            nav.Clear();
            foreach (var p in Pages)
            {
                var id = p[0];
                var b = new Button { Style = Ui.S("Nav"), Content = Icons.WithText(p[2], p[1], 14, Ui.R("Text")), Margin = new Thickness(8, 0, 8, 0) };
                b.Click += (s, e) => Show(id);
                DockPanel.SetDock(b, Dock.Top); side.Children.Add(b); nav[id] = b;
            }
            var foot = new StackPanel { Margin = new Thickness(16, 0, 16, 14) };
            updateBtn = Ui.Btn("Update", () => c.InstallUpdate(), true); updateBtn.Visibility = c.PendingUpdate == null ? Visibility.Collapsed : Visibility.Visible; updateBtn.HorizontalAlignment = HorizontalAlignment.Left; updateBtn.Margin = new Thickness(0, 0, 0, 10);
            if (c.PendingUpdate != null) updateBtn.Content = "Update to v" + c.PendingUpdate.Version.ToString(2);
            foot.Children.Add(updateBtn);
            foot.Children.Add(Ui.Muted("v" + Program.Version + " · MonoTorrent 3.0.2", 11, 0));
            DockPanel.SetDock(foot, Dock.Bottom); side.Children.Add(foot);
            root.Children.Add(side);
            root.Children.Add(new Border { Width = 1, Background = Ui.R("Line"), HorizontalAlignment = HorizontalAlignment.Right });
            var right = new DockPanel(); Grid.SetColumn(right, 1);
            var bar = new DockPanel { Background = Ui.R("Side"), Height = 28 };
            meter = new TextBlock { FontFamily = Ui.Mono, FontSize = 11, Foreground = Ui.R("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            DockPanel.SetDock(meter, Dock.Right); bar.Children.Add(meter);
            sayText = new TextBlock { FontSize = 12, Foreground = Ui.R("TextSoft"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            bar.Children.Add(sayText);
            var barWrap = new Border { BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
            DockPanel.SetDock(barWrap, Dock.Bottom); right.Children.Add(barWrap);
            content = new Border(); right.Children.Add(content);
            root.Children.Add(right);
            Content = root;
            Show(page);
        }
        public void ShowUpdate(string label) { if (updateBtn == null) return; updateBtn.Content = label; updateBtn.Visibility = Visibility.Visible; }
        public void Say(string text)
        {
            if (sayText == null) return; sayText.Text = text ?? "";
            if (sayTimer != null) sayTimer.Stop();
            sayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) }; sayTimer.Tick += (s, e) => { sayTimer.Stop(); sayText.Text = ""; }; sayTimer.Start();
        }
        public void Show(string id)
        {
            page = PageId(id) ?? "transfers";
            foreach (var kv in nav) kv.Value.Tag = kv.Key == page ? "on" : null;
            listPanel = null; statsBox = null; rssBox = null; searchBox = null; proofBox = null; nasBox = null;
            content.Child = page == "settings" ? SettingsPage() : page == "apps" ? ApiPage() : page == "rss" ? RssPage() : page == "search" ? SearchPage() : page == "stats" ? StatsPage() : page == "proof" ? ProofPage() : page == "nas" ? NasPage() : TransfersPage();
            Refresh();
        }
        public void Refresh()
        {
            if (meter != null) meter.Text = (c.Locked ? "VPN LOCK · stopped" : "VPN · " + (c.SnapMode ? "10.5.0.2" : c.Vpn.Ip + "")) + "   ↓ " + Fmt.Rate(c.DownRate) + "   ↑ " + Fmt.Rate(c.UpRate);
            if (page == "transfers" && listPanel != null) RefreshTransfers();
            else if (page == "stats" && statsBox != null) RefreshStats();
            else if (page == "rss" && rssBox != null) RefreshRss();
            else if (page == "search" && searchBox != null) RefreshSearchLock();
            else if (page == "proof" && proofBox != null) RefreshProof();
            else if (page == "nas" && nasBox != null) RefreshNas();
        }

        // ---------------- adding
        public void AskAdd(string item, string category = null)
        {
            var w = Dialogs.AddWindow(c, item, category, x => { if (x != null) { selected = x.Id; Say("Added " + x.Name + (c.Locked ? " - it starts once your VPN is connected." : ".")); Refresh(); } });
            Dialogs.Own(w, this); w.Show();
        }
        public void AskMagnet() { var w = Dialogs.MagnetWindow(m => { if (m != null) AskAdd(m); }); Dialogs.Own(w, this); w.Show(); }
        void AskFile() { var f = Dialogs.PickFile("Torrent files (*.torrent)|*.torrent"); if (f != null) AskAdd(f); }

        // ---------------- Transfers
        StackPanel listPanel, bannerPanel; Border details; string selected, filter = "All", detailTab = "general"; string bannerKey;
        readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();
        class Row { public Border Box; public TextBlock Num, Name, Size, Pct, State, Down, Up, Eta, Peers, Ratio; public Border Fill, Badge; public Grid Bar; }
        static readonly double[] Cols = { 34, -1, 80, 168, 128, 84, 84, 76, 70, 56 };
        static readonly string[] ColNames = { "#", "Name", "Size", "Progress", "Status", "Down", "Up", "ETA", "Peers", "Ratio" };
        Grid ColGrid()
        {
            var g = new Grid();
            foreach (var w in Cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            return g;
        }
        FrameworkElement TransfersPage()
        {
            rows.Clear(); bannerKey = null;
            var dp = new DockPanel { Margin = new Thickness(20, 16, 20, 12) };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var acts = Ui.Row(Ui.Btn("Add torrent file", AskFile, true), Ui.Btn("Add magnet link", AskMagnet), Ui.Btn("Create torrent", AskCreate), Ui.Btn("Pause all", () => { if (!c.SnapMode) c.Core.PauseAll(); Refresh(); }), Ui.Btn("Resume all", () => { if (!c.SnapMode) c.Core.ResumeAll(); Refresh(); }), Ui.Btn("Remove all", AskRemoveAll));   // 1.12: Remove all
            top.Children.Add(acts);
            DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
            bannerPanel = new StackPanel(); DockPanel.SetDock(bannerPanel, Dock.Top); dp.Children.Add(bannerPanel);
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
            foreach (var f in new[] { "All", "Downloading", "Seeding", "Paused", "Done", "Flagged" })
            {
                var ff = f; var t = new ToggleButton { Content = f, Style = Ui.S("Chip"), IsChecked = f == filter, Margin = new Thickness(0, 0, 6, 0) };
                t.Click += (s, e) => { filter = ff; foreach (ToggleButton o in chips.Children) o.IsChecked = (string)o.Content == ff; rows.Clear(); listPanel.Children.Clear(); RefreshTransfers(); };
                chips.Children.Add(t);
            }
            DockPanel.SetDock(chips, Dock.Top); dp.Children.Add(chips);
            details = new Border { Height = 250, Background = Ui.R("Panel"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 10, 14, 10) };
            DockPanel.SetDock(details, Dock.Bottom); dp.Children.Add(details);
            var head = ColGrid(); head.Margin = new Thickness(1, 0, 1, 0);
            for (int i = 0; i < ColNames.Length; i++) { var t = new TextBlock { Text = ColNames[i], FontSize = 11, Foreground = Ui.R("Muted"), Margin = new Thickness(6, 4, 6, 4), HorizontalAlignment = i >= 5 ? HorizontalAlignment.Right : HorizontalAlignment.Left }; Grid.SetColumn(t, i); head.Children.Add(t); }
            var listBox = new DockPanel();
            var hb = new Border { Child = head, BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.R("Panel") }; DockPanel.SetDock(hb, Dock.Top); listBox.Children.Add(hb);
            listPanel = new StackPanel();
            listBox.Children.Add(new ScrollViewer { Content = listPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            dp.Children.Add(new Border { Child = listBox, Background = Ui.R("Panel"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1) });
            BuildDetails();
            return dp;
        }
        void Banners()
        {
            var vpnOk = !c.Locked; var game = c.SnapMode ? c.Game : c.Game;
            var key = vpnOk + "|" + (c.SnapMode ? "" : c.Vpn.Key + c.Vpn.Server) + "|" + game + "|" + c.S.GameMode + "|" + c.S.LegalSeen + "|" + c.S.HandlerAsked + "|" + c.S.VpnSource + "|" + TrippedText + "|" + AutoLine + "|" + NotP2p;
            if (key == bannerKey) return; bannerKey = key;
            bannerPanel.Children.Clear();
            var wd = WatchdogBanner(); if (wd != null) bannerPanel.Children.Add(wd);   // v1.5: the socket watchdog stopped everything
            if (!vpnOk && wd == null && NotP2p)
            {
                // 1.9: "P2P servers only" holds torrents on a server that isn't for P2P
                var b = Ui.Row(Ui.Btn("Switch to a P2P server", () => { var e = c.SwitchP2p(); Say(e ?? "Asked Hallow VPN to connect to a P2P server..."); }, true), Ui.Btn("VPN settings", () => Show("settings")));
                b.VerticalAlignment = VerticalAlignment.Center;
                var msg = c.SnapMode ? "Hallow VPN is connected to United States #5838, which isn't a P2P server." : c.Vpn.Message;
                bannerPanel.Children.Add(Dialogs.Banner("lock", "Amber", "AmberBg", msg + " Everything is stopped - nothing goes out until you're on a P2P server.", b));
            }
            else if (!vpnOk && wd == null)
            {
                var b = Ui.Row(Ui.Btn("Connect Hallow VPN", () => { var e = c.ConnectVpn(); Say(e ?? (VpnAuto.P2pOnly(c.S) ? "Asked Hallow VPN to connect to a P2P server..." : "Asked Hallow VPN to connect to the fastest server...")); }, true), Ui.Btn("VPN settings", () => Show("settings")));
                b.VerticalAlignment = VerticalAlignment.Center;
                var msg = c.SnapMode ? "Hallow VPN isn't connected." : c.Vpn.Message;
                var auto = AutoLine;   // 1.9: what auto-connect is doing
                bannerPanel.Children.Add(Dialogs.Banner("lock", "Red", "RedBg", "Connect your VPN to torrent. Everything is stopped - nothing goes out over your normal connection. " + msg + (auto != null ? "\n" + auto : ""), b));
            }
            else if (wd == null)
            {
                var v = c.Vpn; var where = c.SnapMode ? "NordLynx (10.5.0.2) · Hallow VPN: Netherlands #123" : v.Adapter + " (" + v.Ip + ")" + (v.Server != null ? " · Hallow VPN: " + (v.Country ?? "") + (v.Server != null ? " " + v.Server : "") : "");
                bannerPanel.Children.Add(Dialogs.Banner("lock", "Green", "GreenBg", "Locked to your VPN: " + where + ". Every connection is bound to it; if it drops, torrents stop within a second.", null));
            }
            if (game != null && c.S.GameMode != "off")
                bannerPanel.Children.Add(Dialogs.Banner("gamepad", "Lilac", "LilacBg", c.S.GameMode == "pause" ? game + " is running - torrents are paused until you stop playing." : game + " is running - torrents are limited to " + c.S.GameKBps + " KB/s until you stop playing.", null));
            if (!c.S.LegalSeen)
            {
                var got = Ui.Btn("Got it", () => { c.S.LegalSeen = true; c.S.Save(); bannerKey = null; Banners(); }); got.VerticalAlignment = VerticalAlignment.Center;
                bannerPanel.Children.Add(Dialogs.Banner("circle-info", "Lilac", "LilacBg", Legal, got));
            }
            else if (!c.S.HandlerAsked)
            {
                var b = Ui.Row(Ui.Btn("Yes, use Hallow Torrents", () => { c.SetHandler(true); bannerKey = null; Banners(); Say("Done. If Windows still opens another app, pick Hallow Torrents in Settings > Apps > Default apps."); }, true), Ui.Btn("Not now", () => { c.S.HandlerAsked = true; c.S.Save(); bannerKey = null; Banners(); }));
                b.VerticalAlignment = VerticalAlignment.Center;
                bannerPanel.Children.Add(Dialogs.Banner("magnet", "Lilac", "LilacBg", "Open .torrent files and magnet links with Hallow Torrents? (Only for you - you can change it in Settings.)", b));
            }
        }
        // 1.9: VPN on demand - the auto-connect line under the lock banner, and the P2P hold
        string AutoLine { get { return c.SnapMode ? c.SnapAuto : c.Auto.Text; } }
        bool NotP2p { get { return c.SnapMode ? c.SnapNotP2p : c.Vpn.NotP2p; } }
        public const string Legal = "Torrenting itself is legal. Downloading or sharing copyrighted material without permission isn't. The VPN lock is for privacy - it doesn't change what the law allows.";
        bool Match(TorrentView v)
        {
            switch (filter)
            {
                case "Downloading": return !v.Complete && !v.Paused;
                case "Seeding": return v.State == "Seeding";
                case "Paused": return v.Paused || v.State.StartsWith("Paused");
                case "Done": return v.Complete;
                case "Flagged": return v.Flagged || v.Scan == "threats";
            }
            return true;
        }
        void RefreshTransfers()
        {
            Banners();
            var views = c.Views(false).Where(Match).ToList();
            var ids = new HashSet<string>(views.Select(v => v.Id));
            foreach (var gone in rows.Keys.Where(k => !ids.Contains(k)).ToList()) { listPanel.Children.Remove(rows[gone].Box); rows.Remove(gone); }
            int i = 0;
            foreach (var v in views)
            {
                Row r; if (!rows.TryGetValue(v.Id, out r)) { r = MakeRow(v.Id); rows[v.Id] = r; }
                if (listPanel.Children.IndexOf(r.Box) != i) { listPanel.Children.Remove(r.Box); listPanel.Children.Insert(i, r.Box); }
                i++;
                r.Num.Text = v.Queue.ToString(); r.Name.Text = v.Name; r.Size.Text = v.Size > 0 ? Fmt.Bytes(v.Size) : "?";
                r.Pct.Text = v.Progress.ToString("0.0") + "%"; r.Fill.Width = Math.Max(0, Math.Min(1, v.Progress / 100)) * 96;
                r.Fill.Background = v.Complete ? Ui.R("Green") : v.Paused || v.State.StartsWith("Waiting") || v.State.StartsWith("Paused") ? Ui.R("Faint") : Ui.R("AccentBrush");
                r.State.Text = v.Error != null ? "Error" : v.State;
                r.State.Foreground = v.State == "Waiting for VPN" || v.Error != null ? Ui.R("Red") : v.State == "Seeding" || v.State.StartsWith("Done") ? Ui.R("Green") : v.State.StartsWith("Paused") ? Ui.R("Muted") : Ui.R("Text");
                r.Down.Text = Fmt.Rate(v.DownRate); r.Up.Text = Fmt.Rate(v.UpRate);
                r.Eta.Text = v.Complete ? "" : v.Eta.HasValue ? Plan.Eta((long)v.Eta.Value.TotalSeconds, 1) : v.DownRate > 0 ? "" : "∞";
                r.Peers.Text = v.Seeds + " / " + v.Leechs; r.Ratio.Text = v.Ratio.ToString("0.00");
                bool warn = v.Flagged || v.Scan == "threats"; r.Badge.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
                r.Box.Background = v.Id == selected ? Ui.R("RowSel") : Brushes.Transparent;
            }
            if (views.Count == 0 && listPanel.Children.Count == 0)
                listPanel.Children.Add(new TextBlock { Text = filter == "All" ? "No torrents yet. Add a .torrent file or a magnet link - or drop one here." : "Nothing here.", Foreground = Ui.R("Muted"), Margin = new Thickness(14, 16, 14, 16) });
            else if (views.Count > 0 && listPanel.Children.Count > views.Count) foreach (var t in listPanel.Children.OfType<TextBlock>().ToList()) listPanel.Children.Remove(t);
            UpdateLive();
        }
        Row MakeRow(string id)
        {
            var r = new Row(); var g = ColGrid();
            Func<int, bool, TextBlock> cell = (col, rightAlign) => { var t = new TextBlock { FontSize = 12, Margin = new Thickness(6, 7, 6, 7), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = rightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left }; Grid.SetColumn(t, col); g.Children.Add(t); return t; };
            r.Num = cell(0, false); r.Num.Foreground = Ui.R("Muted"); r.Num.FontFamily = Ui.Mono;
            var nameRow = new DockPanel { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(nameRow, 1);
            r.Badge = Ui.Pill("Check before opening", "Amber", "AmberBg"); r.Badge.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(r.Badge, Dock.Right); nameRow.Children.Add(r.Badge);
            r.Name = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; nameRow.Children.Add(r.Name); g.Children.Add(nameRow);
            r.Size = cell(2, false); r.Size.FontFamily = Ui.Mono; r.Size.Foreground = Ui.R("TextSoft");
            var pg = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(pg, 3);
            r.Bar = new Grid { Width = 96, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            r.Bar.Children.Add(new Border { Background = Ui.R("Well"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) });
            r.Fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Width = 0 }; r.Bar.Children.Add(r.Fill);
            pg.Children.Add(r.Bar); r.Pct = new TextBlock { FontSize = 11, FontFamily = Ui.Mono, Margin = new Thickness(8, 0, 0, 0), Foreground = Ui.R("TextSoft"), VerticalAlignment = VerticalAlignment.Center }; pg.Children.Add(r.Pct); g.Children.Add(pg);
            r.State = cell(4, false); r.Down = cell(5, true); r.Up = cell(6, true); r.Eta = cell(7, true); r.Peers = cell(8, true); r.Ratio = cell(9, true);
            foreach (var t in new[] { r.Down, r.Up, r.Eta, r.Peers, r.Ratio }) { t.FontFamily = Ui.Mono; t.FontSize = 11.5; t.Foreground = Ui.R("TextSoft"); }
            r.Box = new Border { Child = g, BorderBrush = Ui.R("LineDim"), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand };
            r.Box.MouseLeftButtonUp += (s, e) => Select(id);
            r.Box.MouseEnter += (s, e) => { if (id != selected) r.Box.Background = Ui.R("RowHover"); };
            r.Box.MouseLeave += (s, e) => { if (id != selected) r.Box.Background = Brushes.Transparent; };
            return r;
        }
        public void Select(string id) { selected = id; foreach (var kv in rows) kv.Value.Box.Background = kv.Key == id ? Ui.R("RowSel") : Brushes.Transparent; BuildDetails(); }
        public void SnapDetail(string id, string tab) { detailTab = tab; Select(id); }

        // ---------------- details
        TextBlock live;
        void BuildDetails()
        {
            if (details == null) return; live = null;
            var all = c.Views(true); var v = all.FirstOrDefault(x => x.Id == selected);
            if (v == null) { details.Child = new TextBlock { Text = "Click a torrent to see its details, files and safety check.", Foreground = Ui.R("Muted"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; return; }
            var dp = new DockPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var tabs = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(tabs, Dock.Right);
            foreach (var t in new[] { new[] { "general", "General" }, new[] { "files", "Files" }, new[] { "trackers", "Trackers" }, new[] { "safety", "Safety & library" } })
            {
                var id = t[0]; var tb = new ToggleButton { Content = t[1], Style = Ui.S("Chip"), IsChecked = detailTab == id, Margin = new Thickness(6, 0, 0, 0) };
                tb.Click += (s, e) => { detailTab = id; BuildDetails(); }; tabs.Children.Add(tb);
            }
            top.Children.Add(tabs);
            var title = Ui.T(v.Name, 14, true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; top.Children.Add(title);
            DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
            FrameworkElement body = detailTab == "files" ? FilesTab(v) : detailTab == "trackers" ? TrackersTab(v) : detailTab == "safety" ? SafetyTab(v) : GeneralTab(v);
            dp.Children.Add(body);
            details.Child = dp;
        }
        void UpdateLive()
        {
            if (live == null || selected == null) return;
            var v = c.Views(false).FirstOrDefault(x => x.Id == selected); if (v == null) return;
            live.Text = LiveText(v);
        }
        static string LiveText(TorrentView v)
        {
            return v.State + " · " + v.Progress.ToString("0.0") + "% · ↓ " + Fmt.Rate(v.DownRate) + " ↑ " + Fmt.Rate(v.UpRate) + " · downloaded " + Fmt.Bytes(v.Down) + ", uploaded " + Fmt.Bytes(v.Up) + " · ratio " + v.Ratio.ToString("0.00") +
                (v.SeedSecs > 0 ? " · seeded " + Fmt.Span(v.SeedSecs) : "") + (v.Error != null ? " · error: " + v.Error : "");
        }
        Task Do(Func<Task> f) { if (c.SnapMode) return Task.FromResult(0); return f().ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { RefreshTransfers(); BuildDetails(); }))); }
        FrameworkElement GeneralTab(TorrentView v)
        {
            var sp = new StackPanel();
            live = Ui.T(LiveText(v), 12.5, false, Ui.R("TextSoft"), 6); sp.Children.Add(live);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new StackPanel(); var r = new StackPanel(); Grid.SetColumn(r, 1); grid.Children.Add(l); grid.Children.Add(r);
            Action<StackPanel, string, string> kv = (p, k, val) => { var d = new DockPanel { Margin = new Thickness(0, 1, 12, 1) }; var kt = Ui.Muted(k, 12); kt.Width = 110; d.Children.Add(kt); var vt = Ui.T(val ?? "-", 12); vt.TextWrapping = TextWrapping.NoWrap; vt.TextTrimming = TextTrimming.CharacterEllipsis; d.Children.Add(vt); p.Children.Add(d); };
            kv(l, "Save to", v.SavePath); kv(l, "Size", v.Size > 0 ? Fmt.Bytes(v.Size) : "not known yet"); kv(l, "Added", Nice(v.AddedAt)); kv(l, "Finished", Nice(v.CompletedAt));
            kv(r, "Info hash", v.Hash); kv(r, "Queue", "#" + v.Queue); kv(r, "Own limits", v.DownKBps == 0 && v.UpKBps == 0 ? "none" : "↓ " + (v.DownKBps > 0 ? v.DownKBps + " KB/s" : "-") + "  ↑ " + (v.UpKBps > 0 ? v.UpKBps + " KB/s" : "-"));
            var catRow = new WrapPanel(); catRow.Children.Add(new TextBlock { Text = "Category", Width = 110, Foreground = Ui.R("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            foreach (var cc in c.S.Categories) { var n = cc.Name; var t = new ToggleButton { Content = n, Style = Ui.S("Chip"), IsChecked = n == v.Category, Margin = new Thickness(0, 0, 4, 2), Padding = new Thickness(7, 1, 7, 1), FontSize = 11 }; t.Click += (s, e) => { if (!c.SnapMode) c.Core.SetCategory(v.Id, n); BuildDetails(); }; catRow.Children.Add(t); }
            r.Children.Add(catRow);
            sp.Children.Add(grid);
            var sw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            sw.Children.Add(Ui.Switch("Sequential (preview videos while they download)", v.Sequential, on => Do(() => c.Core.SetFlags(v.Id, on, v.SuperSeed))));
            var ss = Ui.Switch("Super-seeding", v.SuperSeed, on => Do(() => c.Core.SetFlags(v.Id, v.Sequential, on))); ss.Margin = new Thickness(24, 6, 0, 6); sw.Children.Add(ss);
            sp.Children.Add(sw);
            var acts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            acts.Children.Add(v.Paused ? Ui.Btn("Resume", () => { if (!c.SnapMode) c.Core.Resume(v.Id); Refresh(); BuildDetails(); }, true) : Ui.Btn("Pause", () => { if (!c.SnapMode) c.Core.Pause(v.Id); Refresh(); BuildDetails(); }));
            acts.Children.Add(Ui.Btn("Move up", () => { if (!c.SnapMode) c.Core.Move(v.Id, -1); Refresh(); }));
            acts.Children.Add(Ui.Btn("Move down", () => { if (!c.SnapMode) c.Core.Move(v.Id, 1); Refresh(); }));
            acts.Children.Add(Ui.Btn("Speed limits...", () => { var w = Dialogs.LimitsWindow(v.Name, v.DownKBps, v.UpKBps, a => { if (a != null) Do(() => c.Core.SetLimits(v.Id, a[0], a[1])); }); Dialogs.Own(w, this); w.ShowDialog(); }));
            acts.Children.Add(Ui.Btn("Force recheck", () => { if (c.SnapMode) return; c.Core.Recheck(v.Id).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => Say(t.Result ?? "Checking its files...")))); }));
            acts.Children.Add(Ui.Btn("Move storage...", () => AskMoveStorage(v)));
            acts.Children.Add(Ui.Btn("Open folder", () => c.OpenFolder(c.SnapMode ? null : c.Core.RootOf(v.Id) ?? v.SavePath)));
            acts.Children.Add(Ui.Danger("Remove", () => { var w = Dialogs.RemoveWindow(v.Name, del => { if (del == null || c.SnapMode) return; c.Core.Remove(v.Id, del.Value).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { selected = null; Say(t.Result ?? "Removed " + v.Name + "."); RefreshTransfers(); BuildDetails(); }))); }); Dialogs.Own(w, this); w.ShowDialog(); }));
            sp.Children.Add(acts);
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        static string Nice(string iso) { DateTime d; return DateTime.TryParse(iso, out d) ? d.ToString("d MMM yyyy, HH:mm") : null; }
        FrameworkElement FilesTab(TorrentView v)
        {
            var sp = new StackPanel();
            if (v.Files.Count == 0) { sp.Children.Add(Ui.Muted(c.Locked ? "The file list arrives once it starts - it needs your VPN connected." : "Getting its file list from peers...", 12.5)); return sp; }
            var names = new[] { "Skip", "Low", "Normal", "High" };
            // v1.1: rename a folder; one priority for every file
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (v.Files.Any(f => (f.Rel ?? f.Path).Contains("\\"))) head.Children.Add(Small("Rename folder...", () => AskRenameFolder(v)));
            var allLabel = Ui.Muted("All files:", 11.5); allLabel.VerticalAlignment = VerticalAlignment.Center; allLabel.Margin = new Thickness(4, 0, 4, 0); head.Children.Add(allLabel);
            for (int p = 0; p < 4; p++) { var pp = p; head.Children.Add(Small(names[p], () => Do(async () => { foreach (var f in v.Files) await c.Core.SetFilePrio(v.Id, f.Index, pp); }))); }
            sp.Children.Add(head);
            foreach (var f in v.Files)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var pr = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(pr, Dock.Right);
                var ff = f; var ren = Small("Rename", () => AskRenameFile(v, ff)); ren.Margin = new Thickness(6, 0, 0, 0);
                for (int p = 0; p < 4; p++)
                {
                    var pp = p; var fi = f.Index; var t = new ToggleButton { Content = names[p], Style = Ui.S("Chip"), IsChecked = f.Prio == p, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(7, 1, 7, 1), FontSize = 11 };
                    t.Click += (s, e) => Do(() => c.Core.SetFilePrio(v.Id, fi, pp)); pr.Children.Add(t);
                }
                pr.Children.Add(ren);
                row.Children.Add(pr);
                var pct = Ui.Num(f.Progress.ToString("0") + "%", 11.5, Ui.R("Muted")); pct.Width = 46; DockPanel.SetDock(pct, Dock.Right); row.Children.Add(pct);
                var sz = Ui.Num(Fmt.Bytes(f.Length), 11.5, Ui.R("Muted")); sz.Width = 74; DockPanel.SetDock(sz, Dock.Right); row.Children.Add(sz);
                var flagged = v.Flags.Any(x => x.StartsWith(f.Path + "|"));
                var nm = new TextBlock { Text = f.Rel ?? f.Path, ToolTip = f.Rel != null && f.Rel != f.Path ? "In the torrent: " + f.Path : null, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = f.Prio == 0 ? Ui.R("Faint") : flagged ? Ui.R("Amber") : Ui.R("Text") };
                row.Children.Add(nm); sp.Children.Add(row);
            }
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        FrameworkElement SafetyTab(TorrentView v)
        {
            var sp = new StackPanel();
            if (v.Flags.Count > 0)
            {
                sp.Children.Add(Dialogs.Banner("triangle-exclamation", "Amber", "AmberBg", "Check before opening. Hallow Torrents never opens downloads by itself.", null));
                foreach (var f in v.Flags.Take(8)) { var p = f.Split('|'); sp.Children.Add(Ui.T("• " + p[0] + " - " + (p.Length > 1 ? p[1] : ""), 12, false, Ui.R("Text"))); }
            }
            else sp.Children.Add(Ui.T("No suspicious files (programs, scripts or shortcuts in a video / music download, or disguised names).", 12.5, false, Ui.R("TextSoft"), 4));
            var scan = string.IsNullOrEmpty(v.Scan) ? (v.Complete ? "Not scanned." : "Scanned by Microsoft Defender when it finishes.") : (v.ScanText ?? "").Replace("Microsoft Defender ", "");
            var sc = Ui.T("Microsoft Defender: " + scan, 12.5, false, v.Scan == "threats" ? Ui.R("Red") : v.Scan == "clean" ? Ui.R("Green") : Ui.R("TextSoft")); sc.Margin = new Thickness(0, 8, 0, 0); sp.Children.Add(sc);
            var cat = c.S.Cat(v.Category);
            var lib = cat != null && cat.Handoff && !string.IsNullOrWhiteSpace(cat.LibraryFolder) ? "Library: " + (v.Handoff ?? (v.Complete ? "not handed off yet." : (cat.Move ? "moved" : "copied") + " to " + cat.LibraryFolder + " when it finishes.")) : "Library: no hand-off for " + (v.Category ?? "this category") + " (set one up in Settings > Categories).";
            sp.Children.Add(Ui.T(lib, 12.5, false, Ui.R("TextSoft"), 6));
            var acts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (v.Complete) { acts.Children.Add(Ui.Btn("Scan again", () => c.ScanAgain(v.Id))); acts.Children.Add(Ui.Btn("Send to library", () => c.SendToLibrary(v.Id))); }
            sp.Children.Add(acts);
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
