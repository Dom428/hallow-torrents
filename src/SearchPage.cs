// Hallow Torrents 1.3 - the Search page (your Prowlarr / Jackett / Torznab feeds, smart ranking, filters, sorting, per-indexer status,
// Download through the normal Add window, "Add to RSS rule", check a result's files) and its two small windows: Indexers and Ranking.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HallowTorrents
{
    public partial class MainWindow
    {
        DockPanel searchBox; TextBox searchField; StackPanel resultsPanel; WrapPanel statusStrip; Border searchLock, resultDetails; TextBlock profileLine, countLine;
        SearchOutcome shown; string selTok, sQual = "", sCat = "", sIdx = "", sSort = "score"; bool sDesc = true, sFlagged = true; double sMaxGb; int sMinSeed; bool searching;
        static readonly double[] SCols = { 58, -1, 82, 70, 60, 150, 62 };
        static readonly string[][] SColNames = { new[] { "score", "Score" }, new[] { "name", "Name" }, new[] { "size", "Size" }, new[] { "seeders", "Seeds" }, new[] { "peers", "Peers" }, new[] { "indexer", "Indexer" }, new[] { "age", "Age" } };
        Grid SGrid() { var g = new Grid(); foreach (var w in SCols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) }); return g; }

        FrameworkElement SearchPage()
        {
            if (c.SnapMode && shown == null) shown = c.SnapSearch;
            var dp = new DockPanel { Margin = new Thickness(20, 16, 20, 12) };
            // the search line
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var go = Ui.Btn("Search", RunSearch, true); go.Margin = new Thickness(8, 0, 0, 0);
            var idxBtn = Ui.Btn("Indexers...", () => { var w = Dialogs.IndexersWindow(c, () => { UpdateProfileLine(); }); Dialogs.Own(w, this); w.ShowDialog(); UpdateProfileLine(); }); idxBtn.Margin = new Thickness(8, 0, 0, 0);
            var prefBtn = Ui.Btn("Ranking...", () => { var w = Dialogs.ProfileWindow(c, ok => { if (ok) { UpdateProfileLine(); Rerank(); } }); Dialogs.Own(w, this); w.ShowDialog(); }); prefBtn.Margin = new Thickness(8, 0, 0, 0);
            foreach (var b in new FrameworkElement[] { prefBtn, idxBtn, go }) { DockPanel.SetDock(b, Dock.Right); top.Children.Add(b); }
            searchField = Ui.Field(shown != null && shown.Query != null ? RebuildText(shown.Query) : ""); searchField.FontSize = 14;
            searchField.KeyDown += (s, e) => { if (e.Key == Key.Enter) RunSearch(); };
            top.Children.Add(searchField);
            DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
            profileLine = Ui.Muted("", 11.5, 4); DockPanel.SetDock(profileLine, Dock.Top); dp.Children.Add(profileLine); UpdateProfileLine();
            searchLock = Dialogs.Banner("lock", "Amber", "AmberBg", "Search waits for your VPN: your indexers are only ever contacted over it, so nothing is sent while it's off.", null);
            searchLock.Margin = new Thickness(0, 2, 0, 6); DockPanel.SetDock(searchLock, Dock.Top); dp.Children.Add(searchLock);
            statusStrip = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) }; DockPanel.SetDock(statusStrip, Dock.Top); dp.Children.Add(statusStrip);
            // filters
            var filters = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            Action<string> label = t => { var l = Ui.Muted(t, 11.5); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 4); filters.Children.Add(l); };
            label("Quality");
            filters.Children.Add(MiniChips(new[] { new[] { "", "Any" }, new[] { "2160p", "2160p" }, new[] { "1080p", "1080p" }, new[] { "720p", "720p" }, new[] { "sd", "SD" } }, sQual, v => { sQual = v; ShowResults(); }));
            label("  Category");
            filters.Children.Add(MiniChips(Torznab.CatNames, sCat, v => { sCat = v; ShowResults(); }));
            DockPanel.SetDock(filters, Dock.Top); dp.Children.Add(filters);
            var filters2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            Action<string> label2 = t => { var l = Ui.Muted(t, 11.5); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 4); filters2.Children.Add(l); };
            label2("Max size GB"); var mx = Ui.Field(sMaxGb > 0 ? sMaxGb.ToString("0.#") : "", 54); mx.Margin = new Thickness(0, 0, 12, 4); mx.VerticalAlignment = VerticalAlignment.Center; mx.TextChanged += (s, e) => { double v; sMaxGb = double.TryParse(mx.Text.Trim(), out v) && v > 0 ? v : 0; ShowResults(); }; filters2.Children.Add(mx);
            label2("Min seeders"); var ms = Ui.Field(sMinSeed > 0 ? sMinSeed.ToString() : "", 48); ms.Margin = new Thickness(0, 0, 12, 4); ms.VerticalAlignment = VerticalAlignment.Center; ms.TextChanged += (s, e) => { int v; sMinSeed = int.TryParse(ms.Text.Trim(), out v) && v > 0 ? v : 0; ShowResults(); }; filters2.Children.Add(ms);
            label2("Indexer"); idxChips = new WrapPanel(); filters2.Children.Add(idxChips);
            var fl = Ui.Switch("Show flagged", sFlagged, v => { sFlagged = v; ShowResults(); }); fl.Margin = new Thickness(12, 0, 0, 4); fl.VerticalAlignment = VerticalAlignment.Center; filters2.Children.Add(fl);
            DockPanel.SetDock(filters2, Dock.Top); dp.Children.Add(filters2);
            // details of the picked result
            resultDetails = new Border { Height = 168, Background = Ui.R("Panel"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 10, 14, 10) };
            DockPanel.SetDock(resultDetails, Dock.Bottom); dp.Children.Add(resultDetails);
            countLine = Ui.Muted("", 11.5, 0); countLine.Margin = new Thickness(0, 6, 0, 0); DockPanel.SetDock(countLine, Dock.Bottom); dp.Children.Add(countLine);
            // the list
            var head = SGrid(); head.Margin = new Thickness(1, 0, 1, 0);
            for (int i = 0; i < SColNames.Length; i++)
            {
                var col = SColNames[i][0]; var right = i == 0 || (i >= 2 && i <= 4) || i == 6;
                var t = new TextBlock { Text = SColNames[i][1] + (sSort == col ? (sDesc ? " \u25BE" : " \u25B4") : ""), FontSize = 11, Foreground = sSort == col ? Ui.R("Text") : Ui.R("Muted"), Margin = new Thickness(6, 4, 6, 4), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = i == 0 ? "The smart score (flagged results always last)" : "Sort by " + SColNames[i][1].ToLowerInvariant() };
                t.MouseLeftButtonUp += (s, e) => { if (sSort == col) sDesc = !sDesc; else { sSort = col; sDesc = col != "name" && col != "indexer" && col != "age"; } Show("search"); };
                Grid.SetColumn(t, i); head.Children.Add(t);
            }
            var listBox = new DockPanel();
            var hb = new Border { Child = head, BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.R("Panel") }; DockPanel.SetDock(hb, Dock.Top); listBox.Children.Add(hb);
            resultsPanel = new StackPanel();
            listBox.Children.Add(new ScrollViewer { Content = resultsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            dp.Children.Add(new Border { Child = listBox, Background = Ui.R("Panel"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1) });
            searchBox = dp;
            if (c.SnapMode && shown == null) shown = c.SnapSearch;
            RefreshSearchLock(); ShowResults();
            return dp;
        }
        WrapPanel idxChips;
        static string RebuildText(SearchQuery q) { var t = q.Text; if (q.Mode == "tv" && q.Season >= 0) t += " S" + q.Season.ToString("00") + (q.Episode >= 0 ? "E" + q.Episode.ToString("00") : ""); if (q.Mode == "movie" && q.ImdbId != null) t += " tt" + q.ImdbId; return t.Trim(); }
        static WrapPanel MiniChips(string[][] options, string current, Action<string> pick)
        {
            var wp = new WrapPanel();
            foreach (var o in options)
            {
                var id = o[0]; var t = new ToggleButton { Content = o[1], Style = Ui.S("Chip"), IsChecked = id == current, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 1, 8, 2), FontSize = 11.5 };
                t.Click += (s, e) => { foreach (ToggleButton x in wp.Children) x.IsChecked = x == t; pick(id); };
                wp.Children.Add(t);
            }
            return wp;
        }
        void UpdateProfileLine()
        {
            if (profileLine == null) return; var se = c.Search;
            var n = se == null ? 0 : se.FeedCount; int idx; lock (se.Lock) idx = se.Data.Indexers.Count;
            profileLine.Text = (idx == 0 ? "No indexers yet - click Indexers... to add your Prowlarr or Jackett (or a Torznab feed). There's no built-in site list." : "Asking " + n + " indexer" + (n == 1 ? "" : "s") + " at once.") + "  Ranked for: " + se.Data.Profile.Text + ".  Tip: \"show s01e02\" searches that episode, \"tt0111161\" a movie by its IMDb id.";
        }
        void RefreshSearchLock() { if (searchLock != null) searchLock.Visibility = c.Locked ? Visibility.Visible : Visibility.Collapsed; }

        void RunSearch()
        {
            if (searching) return;
            var text = searchField.Text.Trim(); if (text.Length == 0) { Say("Type what to look for."); return; }
            if (c.SnapMode) return;
            if (c.Locked) { Say("Connect your VPN first - searches only go out over it (nothing was sent)."); RefreshSearchLock(); return; }
            searching = true; Say("Searching your indexers over the VPN..."); selTok = null;
            statusStrip.Children.Clear(); statusStrip.Children.Add(Ui.Muted("Searching...", 12));
            var q = SearchQuery.From(text, sCat);
            Task.Run(() => c.Search.Run(q, DateTime.UtcNow)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                searching = false;
                if (t.IsFaulted) { Say("Search failed: " + t.Exception.GetBaseException().Message); return; }
                shown = t.Result; if (shown.Error != null) Say(shown.Error); else Say(shown.Results.Count + " results in " + (shown.Ms / 1000.0).ToString("0.0") + " s.");
                if (page == "search") { RefreshSearchLock(); ShowResults(); }
            })));
        }
        void Rerank()
        {
            if (shown == null || c.Search == null) return;
            var p = c.Search.Data.Profile; var now = DateTime.UtcNow;
            var feeds = c.Search.EnabledFeeds(); shown.Results = Ranker.Rank(shown.Results.ToList(), p, id => { var f = feeds.FirstOrDefault(x => x.Id == id); return f == null ? 0.8 : f.Reliability; }, now);
            ShowResults();
        }
        bool Keep(SearchResult r)
        {
            if (!sFlagged && r.Flagged) return false;
            if (sQual == "sd" ? (r.R.Res == 0 || r.R.Res >= 720) : sQual.Length > 0 && r.R.ResText != sQual) return false;
            if (sCat.Length > 0 && Torznab.CatOf(r.Cats) != sCat) return false;
            if (sIdx.Length > 0 && r.IndexerName != sIdx) return false;
            if (sMaxGb > 0 && r.Size > sMaxGb * 1024 * 1024 * 1024) return false;
            if (sMinSeed > 0 && r.Seeders < sMinSeed) return false;
            return true;
        }
        void ShowResults()
        {
            if (resultsPanel == null) return;
            resultsPanel.Children.Clear(); statusStrip.Children.Clear();
            if (shown == null) { resultsPanel.Children.Add(Ui.Muted("Search your own indexers (Prowlarr / Jackett / Torznab). Results are ranked by health, your quality profile, size and the indexer's track record; likely fakes go to the bottom with the reason.", 12.5)); ((FrameworkElement)resultsPanel.Children[0]).Margin = new Thickness(14, 16, 14, 16); countLine.Text = ""; BuildResultDetails(); return; }
            foreach (var s in shown.Status)
            {
                var fg = s.State == "ok" ? "Green" : s.State == "timeout" || s.State == "locked" ? "Amber" : "Red";
                var p = Ui.Pill(s.Name + "  " + (s.State == "ok" ? s.Count + " \u00B7 " + s.Ms + " ms" : s.State == "timeout" ? "timed out" : s.State == "locked" ? "waiting for VPN" : "error"), fg, fg + "Bg");
                p.Margin = new Thickness(0, 0, 6, 4); if (s.Error != null) p.ToolTip = s.Error; statusStrip.Children.Add(p);
            }
            if (idxChips != null)
            {
                idxChips.Children.Clear();
                var names = new List<string[]> { new[] { "", "All" } }; names.AddRange(shown.Results.Select(r => r.IndexerName).Where(n => n != null).Distinct().OrderBy(n => n).Select(n => new[] { n, n }));
                if (!names.Any(n => n[0] == sIdx)) sIdx = "";
                idxChips.Children.Add(MiniChips(names.ToArray(), sIdx, v => { sIdx = v; ShowResults(); }));
            }
            IEnumerable<SearchResult> list = shown.Results.Where(Keep);
            Func<SearchResult, object> key = r => sSort == "name" ? (object)(r.Title ?? "") : sSort == "size" ? (object)r.Size : sSort == "seeders" ? (object)r.Seeders : sSort == "peers" ? (object)r.Peers : sSort == "indexer" ? (object)(r.IndexerName ?? "") : sSort == "age" ? (object)(r.Published.HasValue ? -r.Published.Value.Ticks : long.MaxValue) : (object)r.Score;
            if (sSort == "score") list = list.OrderBy(r => r.Flagged ? 1 : 0).ThenBy(r => sDesc ? -r.Score : r.Score);
            else list = sDesc ? list.OrderByDescending(key) : list.OrderBy(key);
            var shownList = list.ToList();
            if (shownList.Count == 0) { var t = Ui.Muted(shown.Error ?? (shown.Results.Count == 0 ? "Nothing found." : "Nothing matches the filters."), 12.5); t.Margin = new Thickness(14, 16, 14, 16); resultsPanel.Children.Add(t); }
            foreach (var r in shownList.Take(300)) resultsPanel.Children.Add(ResultRow(r));
            countLine.Text = shownList.Count + " of " + shown.Results.Count + " results" + (shown.Results.Any(r => r.Flagged) ? " \u00B7 " + shown.Results.Count(r => r.Flagged) + " flagged as likely fake" : "") + (shown.Ms > 0 ? " \u00B7 " + (shown.Ms / 1000.0).ToString("0.0") + " s" : "") + " \u00B7 everything went out over the VPN";
            BuildResultDetails();
        }
        static string Age(DateTime? d)
        {
            if (!d.HasValue) return ""; var a = DateTime.UtcNow - d.Value;
            return a.TotalHours < 1 ? Math.Max(1, (int)a.TotalMinutes) + " min" : a.TotalDays < 1 ? (int)a.TotalHours + " h" : a.TotalDays < 60 ? (int)a.TotalDays + " d" : a.TotalDays < 730 ? (int)(a.TotalDays / 30) + " mo" : (int)(a.TotalDays / 365) + " y";
        }
        Border ResultRow(SearchResult r)
        {
            var g = SGrid();
            Func<int, string, bool, Brush, TextBlock> cell = (col, text, right, fg) => { var t = new TextBlock { Text = text, FontSize = 11.5, FontFamily = Ui.Mono, Margin = new Thickness(6, 7, 6, 7), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = fg ?? Ui.R("TextSoft") }; Grid.SetColumn(t, col); g.Children.Add(t); return t; };
            cell(0, r.Score.ToString("0"), true, r.Flagged ? Ui.R("Red") : r.Score >= 100 ? Ui.R("Green") : Ui.R("Text"));
            var name = new DockPanel { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(name, 1);
            var pills = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(pills, Dock.Right);
            Action<string, string> pill = (t, k) => { if (string.IsNullOrEmpty(t)) return; var p = Ui.Pill(t, k, k + "Bg"); p.Margin = new Thickness(4, 0, 0, 0); pills.Children.Add(p); };
            if (r.Flagged) pill("Likely fake", "Red");
            else
            {
                pill(r.R.ResText, "Lilac"); pill(r.R.Low ? r.R.Source : null, "Amber"); pill(r.R.Hdr, "Lilac");
                if (r.InList) pill("In your list", "Green"); else if (r.Grabbed) pill("RSS has it", "Green");
                if (r.Notes.Any(n => n.StartsWith("Avoided") || n.StartsWith("Over"))) pill(r.Notes.First(n => n.StartsWith("Avoided") || n.StartsWith("Over")).Split('(')[0].Trim(), "Amber");
            }
            name.Children.Add(pills);
            var nm = new TextBlock { Text = r.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = r.Flagged ? Ui.R("Muted") : Ui.R("Text"), ToolTip = r.Flagged ? string.Join("\n", r.Flags) : r.Title };
            name.Children.Add(nm); g.Children.Add(name);
            cell(2, r.Size > 0 ? Fmt.Bytes(r.Size) : "?", true, null);
            cell(3, r.Seeders >= 0 ? r.Seeders.ToString("N0") : "?", true, r.Seeders == 0 ? Ui.R("Red") : r.Seeders >= 20 ? Ui.R("Green") : null);
            cell(4, r.Peers >= 0 ? Math.Max(0, r.Peers - Math.Max(0, r.Seeders)).ToString("N0") : "?", true, null);
            var ix = cell(5, r.IndexerName ?? "", false, null); ix.FontFamily = new FontFamily("Segoe UI");
            cell(6, Age(r.Published), true, null);
            var box = new Border { Child = g, BorderBrush = Ui.R("LineDim"), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand, Background = r.Token == selTok ? Ui.R("RowSel") : Brushes.Transparent };
            box.MouseLeftButtonUp += (s, e) => { selTok = r.Token; foreach (Border b in resultsPanel.Children.OfType<Border>()) b.Background = b == box ? Ui.R("RowSel") : Brushes.Transparent; BuildResultDetails(); };
            box.MouseEnter += (s, e) => { if (r.Token != selTok) box.Background = Ui.R("RowHover"); };
            box.MouseLeave += (s, e) => { if (r.Token != selTok) box.Background = Brushes.Transparent; };
            box.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) DownloadResult(r); };
            return box;
        }
        public void SnapSelect(string token) { selTok = token; ShowResults(); }
        void BuildResultDetails()
        {
            if (resultDetails == null) return;
            var r = shown == null ? null : shown.Results.FirstOrDefault(x => x.Token == selTok);
            if (r == null) { resultDetails.Child = new TextBlock { Text = "Click a result to see why it ranks where it does - double-click to download it.", Foreground = Ui.R("Muted"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; return; }
            var sp = new StackPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var acts = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(acts, Dock.Right);
            acts.Children.Add(Ui.Btn("Download", () => DownloadResult(r), true));
            acts.Children.Add(Ui.Btn("Add to RSS rule", () => RuleFromResult(r)));
            acts.Children.Add(Ui.Btn("Check files", () => CheckFiles(r), false, "Fetches its .torrent over the VPN and checks the file list for programs"));
            top.Children.Add(acts);
            var title = Ui.T(r.Title, 13.5, true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; top.Children.Add(title);
            sp.Children.Add(top);
            var parts = new[] { r.R.ResText, r.R.Source, r.R.Codec, r.R.Hdr, r.R.Audio, r.R.Group.Length > 0 ? "group " + r.R.Group : "", r.R.SxE, r.Size > 0 ? Fmt.Bytes(r.Size) : "", r.Category, r.IndexerName }.Where(x => !string.IsNullOrEmpty(x));
            sp.Children.Add(Ui.T(string.Join(" \u00B7 ", parts), 12, false, Ui.R("TextSoft"), 4));
            if (r.Flagged)
                foreach (var f in r.Flags) sp.Children.Add(Ui.T("\u26A0 " + f, 12, true, Ui.R("Red")));
            foreach (var n in r.Notes) sp.Children.Add(Ui.T("\u2022 " + n, 12, false, Ui.R("Amber")));
            sp.Children.Add(Ui.T("Score " + r.Score.ToString("0") + ": " + string.Join(", ", r.Why), 11.5, false, Ui.R("Muted"), 2));
            if (r.Files != null) sp.Children.Add(Ui.T(r.Files.Count + " file" + (r.Files.Count == 1 ? "" : "s") + ": " + string.Join(", ", r.Files.Take(5).Select(f => Path.GetFileName(f.Path))) + (r.Files.Count > 5 ? "..." : ""), 11.5, false, Ui.R("Muted")));
            resultDetails.Child = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        // the normal add flow: the Add window (files, Defender scan when done, categories), VPN-locked like everything else
        void DownloadResult(SearchResult r)
        {
            if (c.SnapMode) return;
            var cat = c.S.Cat(r.Category) != null ? r.Category : null;
            if (r.Link == null && r.Magnet != null) { AskAdd(r.Magnet, cat); return; }
            if (c.Locked) { Say("Connect your VPN first - the .torrent is only fetched over it."); return; }
            Say("Getting the .torrent over the VPN...");
            Task.Run(() =>
            {
                byte[] t; string m; var err = c.Search.Fetch(r, out t, out m); string path = null;
                if (err == null && t != null) { var dir = Path.Combine(TorSettings.Dir, "search-tmp"); Directory.CreateDirectory(dir); path = Path.Combine(dir, (r.InfoHash ?? Guid.NewGuid().ToString("N")) + ".torrent"); File.WriteAllBytes(path, t); }
                Dispatcher.BeginInvoke(new Action(() => { if (err != null) { Say(err); return; } AskAdd(path ?? m, cat); }));
            });
        }
        void RuleFromResult(SearchResult r)
        {
            var w = Dialogs.RuleWindow(c, SearchEngine.RuleFrom(r), saved => { if (saved == null) return; if (!c.SnapMode) c.Rss.SaveRule(saved); Say("RSS rule \"" + saved.Name + "\" saved - it picks new releases from your feeds."); });
            Dialogs.Own(w, this); w.ShowDialog();
        }
        void CheckFiles(SearchResult r)
        {
            if (c.SnapMode) return;
            if (c.Locked) { Say("Connect your VPN first - the .torrent is only fetched over it."); return; }
            Say("Checking its files...");
            Task.Run(() => c.Search.Inspect(r, DateTime.UtcNow)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                Say(t.Result ?? (r.Flagged ? "Flagged: " + r.Flags[0] : "No programs or disguised files in it (" + r.Files.Count + " files)."));
                if (shown != null) shown.Results = shown.Results.OrderBy(s => s.Flagged ? 1 : 0).ThenByDescending(s => s.Score).ToList();
                if (page == "search") ShowResults();
            })));
        }
    }

    public static partial class Dialogs
    {
        // ---------------- Indexers: Prowlarr / Jackett (base URL + API key) or single Torznab feeds; the key is kept encrypted
        public static Window IndexersWindow(Controller c, Action changed)
        {
            var w = Make("Indexers - Hallow Torrents", 720); var sp = Body(w); var se = c.Search;
            sp.Children.Add(Ui.T("Your indexers", 15, true, null, 2));
            sp.Children.Add(Ui.Muted("Search only asks your own Prowlarr or Jackett (usually on this PC or your NAS) or Torznab feeds you add - it never scrapes torrent sites. Every request goes out over the VPN; while it's off nothing is sent. API keys are kept encrypted for your Windows user.", 12, 10));
            var list = new StackPanel(); sp.Children.Add(new Border { Child = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Background = Ui.R("Well"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6) });
            var msg = Ui.Muted("", 12, 0); msg.Margin = new Thickness(0, 6, 0, 0);
            Action fill = null;
            fill = () =>
            {
                list.Children.Clear(); List<Indexer> all; lock (se.Lock) all = se.Data.Indexers.ToList();
                if (all.Count == 0) list.Children.Add(Ui.Muted("None yet - add one below.", 12.5));
                foreach (var x in all)
                {
                    var xx = x; var row = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
                    var head = new DockPanel();
                    var acts = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(acts, Dock.Right);
                    var on = Ui.Switch("On", xx.Enabled, v => { xx.Enabled = v; se.Save(); if (changed != null) changed(); }); on.Margin = new Thickness(0, 0, 10, 0); acts.Children.Add(on);
                    var sync = Ui.Btn(xx.Kind == "torznab" ? "Test" : "Find indexers", null); sync.Padding = new Thickness(8, 1, 8, 2); sync.FontSize = 11;
                    sync.Click += (s, e) =>
                    {
                        if (c.SnapMode) return; if (c.Locked) { msg.Text = "Connect your VPN first - indexers are only contacted over it."; return; }
                        msg.Text = "Asking " + xx.Name + " over the VPN..."; foreach (var f in xx.Feeds) f.Caps = false;
                        Task.Run(() => se.Sync(xx)).ContinueWith(t => w.Dispatcher.BeginInvoke(new Action(() => { msg.Text = t.Result ?? xx.Name + ": " + xx.Feeds.Count + " indexer" + (xx.Feeds.Count == 1 ? "" : "s") + " ready."; fill(); if (changed != null) changed(); })));
                    };
                    acts.Children.Add(sync);
                    var key = Ui.Btn("New key", () => { var pw = PromptWindow("New API key", "It replaces the saved one (kept encrypted for your Windows user).", "", "Save", k => { if (k != null && !c.SnapMode) { se.SetKey(xx, k); msg.Text = "Key saved."; } }); Own(pw, w); pw.ShowDialog(); }); key.Padding = new Thickness(8, 1, 8, 2); key.FontSize = 11; key.Margin = new Thickness(4, 0, 0, 0); acts.Children.Add(key);
                    var del = Ui.Btn("Remove", () => { if (!c.SnapMode) se.RemoveIndexer(xx.Id); fill(); if (changed != null) changed(); }); del.Padding = new Thickness(8, 1, 8, 2); del.FontSize = 11; del.Margin = new Thickness(4, 0, 0, 0); acts.Children.Add(del);
                    head.Children.Add(acts);
                    var info = new StackPanel();
                    info.Children.Add(Ui.T(xx.Name == xx.KindName ? xx.Name : xx.Name + "  \u00B7  " + xx.KindName, 12.5, true));
                    var u = Ui.Muted(Torznab.Redact(xx.BaseUrl) + (xx.LastError != null ? "  -  " + xx.LastError : xx.LastSync != null ? "  -  checked " + xx.LastSync.Replace("T", " ") : "  -  not checked yet"), 11.5); u.TextTrimming = TextTrimming.CharacterEllipsis; u.TextWrapping = TextWrapping.NoWrap; if (xx.LastError != null) u.Foreground = Ui.R("Red"); info.Children.Add(u);
                    head.Children.Add(info); row.Children.Add(head);
                    if (xx.Kind != "torznab" && xx.Feeds.Count > 0)
                    {
                        var feeds = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                        foreach (var f in xx.Feeds)
                        {
                            var ff = f; var cb = new CheckBox { Style = Ui.S("Tick"), IsChecked = ff.Enabled, Margin = new Thickness(0, 0, 14, 2), Content = new TextBlock { Text = ff.Name + (ff.Ok + ff.Fail > 0 ? "  " + (ff.Reliability * 100).ToString("0") + "%" : "") + (ff.Caps ? (ff.TvSearch ? " \u00B7 TV" : "") + (ff.MovieSearch ? " \u00B7 movies" : "") : ""), FontSize = 11.5 } };
                            cb.Click += (s, e) => { ff.Enabled = cb.IsChecked == true; se.Save(); if (changed != null) changed(); };
                            feeds.Children.Add(cb);
                        }
                        row.Children.Add(feeds);
                    }
                    list.Children.Add(row);
                }
            };
            fill();
            sp.Children.Add(msg);
            Cap(sp, "Add", 14);
            string kind = "prowlarr";
            var name = Ui.Field("", 420); var url = Ui.Field("", 420);
            var keyBox = new PasswordBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), CaretBrush = Ui.R("Text") };
            var hint = Ui.Muted("", 11.5, 4);
            Action hints = () => hint.Text = kind == "prowlarr" ? "Prowlarr's address, e.g. http://localhost:9696 or http://nas:9696. API key: Prowlarr > Settings > General. Its torrent indexers are found for you." : kind == "jackett" ? "Jackett's address, e.g. http://localhost:9117. API key: the top right of Jackett's page. Its configured indexers are found for you." : "A Torznab API address, e.g. http://nas:9696/1/api - with its API key.";
            sp.Children.Add(Chips(new[] { new[] { "prowlarr", "Prowlarr" }, new[] { "jackett", "Jackett" }, new[] { "torznab", "Torznab feed" } }, kind, k => { kind = k; hints(); }));
            hints(); sp.Children.Add(hint);
            sp.Children.Add(Labeled("Name (optional)", name, 130)); sp.Children.Add(Labeled("Address", url, 130)); sp.Children.Add(Labeled("API key", keyBox, 130));
            var row2 = Buttons(sp);
            var close = Ui.Btn("Close", () => w.Close()); row2.Children.Add(close);
            var add = Ui.Btn("Add", () =>
            {
                if (c.SnapMode) return; Indexer x;
                var err = se.AddIndexer(kind, name.Text, url.Text, keyBox.Password, out x);
                if (err != null) { msg.Text = err; msg.Foreground = Ui.R("Red"); return; }
                keyBox.Password = ""; url.Text = ""; name.Text = ""; msg.Foreground = Ui.R("Muted"); fill(); if (changed != null) changed();
                if (c.Locked) { msg.Text = "Added - it's checked once your VPN is on."; return; }
                msg.Text = "Added - asking it over the VPN...";
                Task.Run(() => se.Sync(x)).ContinueWith(t => w.Dispatcher.BeginInvoke(new Action(() => { msg.Text = t.Result ?? x.Name + ": " + x.Feeds.Count + " indexer" + (x.Feeds.Count == 1 ? "" : "s") + " ready."; fill(); if (changed != null) changed(); })));
            }, true);
            add.Margin = new Thickness(0); row2.Children.Add(add);
            return w;
        }

        // ---------------- Ranking: your preference profile
        public static Window ProfileWindow(Controller c, Action<bool> done)
        {
            var w = Make("Ranking - Hallow Torrents", 640); var sp = Body(w); var src = c.Search.Data.Profile;
            var p = new SearchProfile { Resolution = src.Resolution, Source = src.Source, Codec = src.Codec, Avoid = src.Avoid, MaxGB = src.MaxGB, MinSeeders = src.MinSeeders, TimeoutSec = src.TimeoutSec, Inspect = src.Inspect };
            sp.Children.Add(Ui.T("How results are ranked", 15, true, null, 2));
            sp.Children.Add(Ui.Muted("Health (seeders and peers) first, then how close a result is to what you like, a size that makes sense for its quality, and how reliable the indexer has been. Likely fakes always go to the bottom with the reason.", 12, 8));
            Cap(sp, "Resolution"); sp.Children.Add(Chips(new[] { new[] { "2160p", "2160p" }, new[] { "1080p", "1080p" }, new[] { "720p", "720p" }, new[] { "480p", "SD" }, new[] { "", "No preference" } }, p.Resolution, v => p.Resolution = v));
            Cap(sp, "Source"); sp.Children.Add(Chips(new[] { new[] { "Remux", "Remux" }, new[] { "BluRay", "Blu-ray" }, new[] { "WEB-DL", "WEB-DL" }, new[] { "WEBRip", "WEBRip" }, new[] { "HDTV", "HDTV" }, new[] { "", "No preference" } }, p.Source, v => p.Source = v));
            Cap(sp, "Codec"); sp.Children.Add(Chips(new[] { new[] { "x265", "x265 / HEVC" }, new[] { "x264", "x264" }, new[] { "AV1", "AV1" }, new[] { "", "No preference" } }, p.Codec, v => p.Codec = v));
            Cap(sp, "Limits");
            var avoid = Ui.Field(p.Avoid, 440); sp.Children.Add(Labeled("Avoid", avoid, 160));
            var max = Ui.Field(p.MaxGB > 0 ? p.MaxGB.ToString("0.#") : "0", 70); sp.Children.Add(Labeled("Max size GB (0 = any)", max, 160));
            var minS = Ui.Field(p.MinSeeders.ToString(), 70); sp.Children.Add(Labeled("Hide under seeders", minS, 160));
            var to = Ui.Field(p.TimeoutSec.ToString(), 70); sp.Children.Add(Labeled("Timeout per indexer (s)", to, 160));
            var insp = Ui.Switch("Check the file lists of the top results (fetches their .torrent files over the VPN - some indexers count that as a grab)", p.Inspect, v => p.Inspect = v); insp.Margin = new Thickness(0, 6, 0, 0); sp.Children.Add(insp);
            bool ok = false; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var save = Ui.Btn("Save", () =>
            {
                double d; int n;
                p.Avoid = avoid.Text.Trim(); p.MaxGB = double.TryParse(max.Text.Trim(), out d) ? d : 0; p.MinSeeders = int.TryParse(minS.Text.Trim(), out n) ? n : 0; p.TimeoutSec = int.TryParse(to.Text.Trim(), out n) ? n : 15; p.Fix();
                if (!c.SnapMode) { lock (c.Search.Lock) c.Search.Data.Profile = p; c.Search.Save(); }
                ok = true; w.Close();
            }, true);
            save.Margin = new Thickness(0); row.Children.Add(save);
            w.Closed += (s, e) => { if (done != null) done(ok); };
            return w;
        }
    }
}
