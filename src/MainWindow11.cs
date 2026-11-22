// Hallow Torrents 1.1 - the new parts of the main window: RSS (feeds, rules, what was grabbed), Statistics, the Trackers tab, renaming
// files and folders, move storage and the torrent creator.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HallowTorrents
{
    public partial class MainWindow
    {
        static Button Small(string label, Action click)
        {
            var b = Ui.Btn(label, click); b.Padding = new Thickness(8, 1, 8, 2); b.FontSize = 11; b.Margin = new Thickness(0, 0, 4, 0); b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }
        void SayLater(Task<string> t, string ok) { t.ContinueWith(r => Dispatcher.BeginInvoke(new Action(() => { Say(r.IsFaulted ? r.Exception.GetBaseException().Message : r.Result ?? ok); RefreshTransfers(); BuildDetails(); }))); }

        // ---------------- creating a torrent
        void AskCreate()
        {
            var w = Dialogs.CreateWindow(c, msg => { if (msg != null) { Say(msg); Refresh(); } });
            Dialogs.Own(w, this); w.Show();
        }

        // ---------------- editing
        void AskMoveStorage(TorrentView v)
        {
            if (c.SnapMode) return;
            var p = Dialogs.PickFolder(v.SavePath); if (p == null) return;
            Say("Moving " + v.Name + "..."); SayLater(c.Core.MoveStorage(v.Id, p), "Moved to " + p + ".");
        }
        void AskRenameFile(TorrentView v, FileView f)
        {
            var w = Dialogs.PromptWindow("Rename a file", "Its new name inside " + v.SavePath + ". Folders are fine (Folder\\name.mkv). The file on disk is renamed too; the torrent itself doesn't change.", f.Rel ?? f.Path, "Rename",
                t => { if (t == null || c.SnapMode) return; SayLater(c.Core.RenameFile(v.Id, f.Index, t), "Renamed."); });
            Dialogs.Own(w, this); w.ShowDialog();
        }
        void AskRenameFolder(TorrentView v)
        {
            var folders = FoldersOf(v.Files.Select(f => f.Rel ?? f.Path));
            if (folders.Count == 0) { Say("Its files aren't in a folder."); return; }
            var w = Dialogs.RenameFolderWindow(folders, (from, to) => { if (from == null || c.SnapMode) return; SayLater(c.Core.RenameFolder(v.Id, from, to), "Renamed the folder."); });
            Dialogs.Own(w, this); w.ShowDialog();
        }
        static List<string> FoldersOf(IEnumerable<string> rels)
        {
            var r = new List<string>();
            foreach (var rel in rels) { if (Path.IsPathRooted(rel)) continue; var d = Path.GetDirectoryName(rel); while (!string.IsNullOrEmpty(d)) { if (!r.Contains(d, StringComparer.OrdinalIgnoreCase)) r.Add(d); d = Path.GetDirectoryName(d); } }
            return r.OrderBy(q => q, StringComparer.OrdinalIgnoreCase).ToList();
        }
        FrameworkElement TrackersTab(TorrentView v)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Muted("Every tracker - the ones you add too - is contacted from your VPN's address only. Changes apply right away and are kept.", 12, 6));
            if (v.Trackers.Count == 0) sp.Children.Add(Ui.Muted("No trackers: it finds peers through DHT and peer exchange.", 12.5, 4));
            foreach (var t in v.Trackers)
            {
                var tt = t; var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var acts = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(acts, Dock.Right);
                acts.Children.Add(Small("Edit", () =>
                {
                    var w = Dialogs.PromptWindow("Edit tracker", "http://, https:// or udp:// address.", tt.Url, "Save", n => { if (n == null || c.SnapMode) return; SayLater(c.Core.EditTracker(v.Id, tt.Url, n), "Tracker changed."); });
                    Dialogs.Own(w, this); w.ShowDialog();
                }));
                acts.Children.Add(Small("Remove", () => { if (!c.SnapMode) SayLater(c.Core.RemoveTracker(v.Id, tt.Url), "Tracker removed."); }));
                row.Children.Add(acts);
                var st = Ui.Num(tt.Status ?? "", 11.5, tt.Status == "Working" ? Ui.R("Green") : tt.Status == "Not working" ? Ui.R("Red") : Ui.R("Muted")); st.Width = 130; st.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(st, Dock.Left); row.Children.Add(st);
                var u = new TextBlock { Text = tt.Url + (string.IsNullOrEmpty(tt.Message) ? "" : "  -  " + tt.Message), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(u); sp.Children.Add(row);
            }
            var add = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var f = Ui.Field(""); var b = Ui.Btn("Add tracker", () => { var url = f.Text.Trim(); if (url.Length == 0 || c.SnapMode) return; SayLater(c.Core.AddTracker(v.Id, url), "Tracker added."); });
            b.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(b, Dock.Right); add.Children.Add(b); add.Children.Add(f);
            sp.Children.Add(add);
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        // ---------------- RSS
        StackPanel rssBox, feedsList, rulesList, grabsList; Border rssLock; string rssKey;
        FrameworkElement RssPage()
        {
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
            rssLock = Dialogs.Banner("lock", "Amber", "AmberBg", "RSS waits for your VPN: feeds are only ever fetched over it, so nothing is checked while it's off.", null);
            rssLock.Margin = new Thickness(0, 0, 0, 10); page.Children.Add(rssLock);
            var fs = Section(page, "Feeds", "Checked every few minutes, over the VPN only. The rules below pick from the torrents (.torrent links or magnet links) in them.");
            var addRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var url = Ui.Field(""); var mins = Ui.Field("30", 56);
            var addBtn = Ui.Btn("Add feed", () =>
            {
                int m; int.TryParse(mins.Text.Trim(), out m);
                var err = c.SnapMode ? null : c.Rss.AddFeed(url.Text, m);
                if (err != null) { Say(err); return; }
                url.Text = ""; rssKey = null; RefreshRss(); Say("Feed added - it's checked " + (c.Locked ? "once your VPN is on." : "now.")); c.KickRss();
            }, true);
            addBtn.Margin = new Thickness(8, 0, 0, 0);
            var refreshAll = Ui.Btn("Refresh", () =>
            {
                if (c.SnapMode) return; if (c.Locked) { Say("Connect your VPN first - feeds are only fetched over it."); return; }
                Say("Checking the feeds over the VPN..."); Task.Run(() => c.Rss.RefreshAll()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { rssKey = null; RefreshRss(); Say("Feeds checked."); RefreshTransfersIfShown(); })));
            });
            refreshAll.Margin = new Thickness(8, 0, 0, 0);
            var every = Ui.Muted("every", 12); every.VerticalAlignment = VerticalAlignment.Center; every.Margin = new Thickness(10, 0, 6, 0);
            var minLabel = Ui.Muted("min", 12); minLabel.VerticalAlignment = VerticalAlignment.Center; minLabel.Margin = new Thickness(6, 0, 0, 0);
            foreach (var x in new FrameworkElement[] { refreshAll, addBtn, minLabel, mins, every }) { DockPanel.SetDock(x, Dock.Right); addRow.Children.Add(x); }
            addRow.Children.Add(url); fs.Children.Add(addRow);
            feedsList = new StackPanel(); fs.Children.Add(feedsList);

            var rs = Section(page, "Download rules", "A rule adds the torrents whose names match. Episode tracking (SxxEyy, 1x02, dates like 2026.11.21, season packs) never grabs the same episode twice; a better quality can replace it within the hours you choose.");
            var addRule = Ui.Btn("Add rule", () => EditRule(null), true); addRule.HorizontalAlignment = HorizontalAlignment.Left; addRule.Margin = new Thickness(0, 0, 0, 8); rs.Children.Add(addRule);
            rulesList = new StackPanel(); rs.Children.Add(rulesList);

            var gs = Section(page, "Grabbed by RSS", null);
            grabsList = new StackPanel(); gs.Children.Add(grabsList);
            rssBox = page; rssKey = null; RefreshRss();
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        void RefreshTransfersIfShown() { if (page == "transfers" && listPanel != null) RefreshTransfers(); }
        void RefreshRss()
        {
            if (rssBox == null) return;
            rssLock.Visibility = c.Locked ? Visibility.Visible : Visibility.Collapsed;
            var d = c.Rss.Data; string key;
            lock (c.Rss.Lock) key = string.Join(";", d.Feeds.Select(f => f.Id + f.LastFetch + f.LastError + f.LastCount + f.Enabled + f.Name)) + "|" + string.Join(";", d.Rules.Select(r => r.Id + r.Name + r.Enabled + r.MustContain + r.Category)) + "|" + d.Grabs.Count + (d.Grabs.Count > 0 ? d.Grabs[d.Grabs.Count - 1].Title + d.Grabs[d.Grabs.Count - 1].Upgraded : "") + c.Locked;
            if (key == rssKey) return; rssKey = key;
            List<RssFeed> feeds; List<RssRule> rules; List<RssGrab> grabs;
            lock (c.Rss.Lock) { feeds = d.Feeds.ToList(); rules = d.Rules.ToList(); grabs = d.Grabs.AsEnumerable().Reverse().Take(15).ToList(); }
            feedsList.Children.Clear();
            if (feeds.Count == 0) feedsList.Children.Add(Ui.Muted("No feeds yet. Paste a feed address above.", 12.5));
            foreach (var f in feeds)
            {
                var ff = f; var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(acts, Dock.Right);
                acts.Children.Add(Small("Refresh", () => { if (c.SnapMode) return; Task.Run(() => c.Rss.Refresh(ff)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { rssKey = null; RefreshRss(); Say(t.Result ?? "Checked " + ff.Label + "."); }))); }));
                acts.Children.Add(Small("Delete", () => { if (!c.SnapMode) c.Rss.RemoveFeed(ff.Id); rssKey = null; RefreshRss(); }));
                row.Children.Add(acts);
                var info = new StackPanel();
                info.Children.Add(Ui.T(ff.Label, 12.5, true));
                if (ff.Name != null) { var u = Ui.Muted(ff.Url, 11.5); u.TextTrimming = TextTrimming.CharacterEllipsis; u.TextWrapping = TextWrapping.NoWrap; info.Children.Add(u); }
                var status = ff.LastError != null ? ff.LastError : ff.LastFetch == null ? "Not checked yet." : ff.LastCount + " items \u00B7 checked " + When(ff.LastFetch);
                info.Children.Add(Ui.T(status + " \u00B7 every " + ff.Minutes + " min", 11.5, false, ff.LastError != null && !ff.LastError.StartsWith("Waiting") ? Ui.R("Red") : Ui.R("TextSoft")));
                row.Children.Add(info); feedsList.Children.Add(row);
            }
            rulesList.Children.Clear();
            if (rules.Count == 0) rulesList.Children.Add(Ui.Muted("No rules yet - without one, nothing is downloaded.", 12.5));
            foreach (var r in rules)
            {
                var rr = r; var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(acts, Dock.Right);
                var on = Ui.Switch("On", rr.Enabled, v => { rr.Enabled = v; if (!c.SnapMode) c.Rss.SaveRule(rr); rssKey = null; }); on.Margin = new Thickness(0, 0, 10, 0); acts.Children.Add(on);
                acts.Children.Add(Small("Edit", () => EditRule(rr)));
                acts.Children.Add(Small("Delete", () => { if (!c.SnapMode) c.Rss.RemoveRule(rr.Id); rssKey = null; RefreshRss(); }));
                row.Children.Add(acts);
                var info = new StackPanel();
                info.Children.Add(Ui.T(rr.Name, 12.5, true, rr.Enabled ? null : Ui.R("Muted")));
                info.Children.Add(Ui.T(RuleSummary(rr), 11.5, false, Ui.R("TextSoft")));
                row.Children.Add(info); rulesList.Children.Add(row);
            }
            grabsList.Children.Clear();
            if (grabs.Count == 0) grabsList.Children.Add(Ui.Muted("Nothing yet.", 12.5));
            foreach (var g in grabs)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var when = Ui.Num(When(g.At), 11.5, Ui.R("Muted")); when.Width = 120; DockPanel.SetDock(when, Dock.Right); row.Children.Add(when);
                if (g.Upgraded) { var p = Ui.Pill("Upgraded", "Green", "GreenBg"); p.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(p, Dock.Right); row.Children.Add(p); }
                var q = RssMatch.QualityText(g.Title); if (q.Length > 0) { var qp = Ui.Pill(q, "Lilac", "LilacBg"); qp.Margin = new Thickness(8, 0, 8, 0); DockPanel.SetDock(qp, Dock.Right); row.Children.Add(qp); }
                var t = new TextBlock { Text = g.Title + "   (" + g.Rule + ")", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(t); grabsList.Children.Add(row);
            }
        }
        static string RuleSummary(RssRule r)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.MustContain)) parts.Add((r.Regex ? "matches /" + r.MustContain + "/" : "has \"" + r.MustContain + "\""));
            if (!string.IsNullOrWhiteSpace(r.MustNotContain)) parts.Add((r.Regex ? "not /" + r.MustNotContain + "/" : "not \"" + r.MustNotContain + "\""));
            parts.Add(r.Category ?? "category picked for it"); if (!string.IsNullOrWhiteSpace(r.SaveFolder)) parts.Add(r.SaveFolder);
            if (!string.IsNullOrWhiteSpace(r.Prefer)) parts.Add("prefers " + r.Prefer);
            if (!string.IsNullOrWhiteSpace(r.Avoid)) parts.Add("avoids " + (r.Avoid.Length > 30 ? r.Avoid.Substring(0, 30) + "..." : r.Avoid));
            parts.Add(r.Episodes ? "each episode once" + (r.UpgradeHours > 0 ? ", upgrades within " + r.UpgradeHours + " h" : "") : "no episode tracking");
            if (r.Paused) parts.Add("added paused");
            return string.Join(" \u00B7 ", parts);
        }
        static string When(string iso)
        {
            DateTime d; if (!DateTime.TryParse(iso, out d)) return "";
            var ago = DateTime.Now - d;
            return ago.TotalMinutes < 1 ? "just now" : ago.TotalHours < 1 ? (int)ago.TotalMinutes + " min ago" : d.Date == DateTime.Today ? d.ToString("HH:mm") : d.ToString("d MMM, HH:mm");
        }
        void EditRule(RssRule r)
        {
            var w = Dialogs.RuleWindow(c, r, saved => { if (saved == null) return; if (!c.SnapMode) c.Rss.SaveRule(saved); rssKey = null; RefreshRss(); Say("Rule saved" + (c.Locked ? " - it runs once your VPN is on." : " - checking the feeds...")); if (!c.SnapMode && !c.Locked) Task.Run(() => c.Rss.RefreshAll()); });
            Dialogs.Own(w, this); w.ShowDialog();
        }

        // ---------------- Statistics
        WrapPanel statsBox; readonly Dictionary<string, TextBlock[]> statTiles = new Dictionary<string, TextBlock[]>(); TextBlock statsNote;
        FrameworkElement StatsPage()
        {
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
            page.Children.Add(Ui.Header("STATISTICS", 16)); ((FrameworkElement)page.Children[0]).Margin = new Thickness(0, 0, 0, 12);
            statsBox = new WrapPanel(); statTiles.Clear();
            foreach (var t in new[] { new[] { "down", "Downloaded", "arrow-down" }, new[] { "up", "Uploaded", "arrow-up" }, new[] { "ratio", "Ratio", "scale-balanced" }, new[] { "seed", "Time seeding", "clock" }, new[] { "vpn", "Waited for the VPN", "lock" }, new[] { "blocked", "Peers blocked", "ban" } })
            {
                var sp = new StackPanel();
                var cap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var ic = Icons.Make(t[2], 13, Ui.R("Lilac")); ic.Margin = new Thickness(0, 0, 8, 0); cap.Children.Add(ic); cap.Children.Add(Ui.Caps(t[1], 10.5));
                sp.Children.Add(cap);
                var big = new TextBlock { FontFamily = Ui.Head, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Ui.R("Text") };
                var small = Ui.Muted("", 12); small.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(big); sp.Children.Add(small);
                var card = Ui.Card(sp, 16); card.Width = 300; card.Margin = new Thickness(0, 0, 12, 12); statsBox.Children.Add(card);
                statTiles[t[0]] = new[] { big, small };
            }
            page.Children.Add(statsBox);
            statsNote = Ui.Muted("", 12, 0); page.Children.Add(statsNote);
            RefreshStats();
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        void RefreshStats()
        {
            if (statsBox == null) return; var s = c.StatsNow; if (s == null) return;
            Action<string, string, string> set = (k, big, small) => { statTiles[k][0].Text = big; statTiles[k][1].Text = small; };
            set("down", Fmt.Bytes(s.SessionDown), "This session \u00B7 " + Fmt.Bytes(s.Down) + " all-time");
            set("up", Fmt.Bytes(s.SessionUp), "This session \u00B7 " + Fmt.Bytes(s.Up) + " all-time");
            set("ratio", s.Ratio.ToString("0.00"), "All-time \u00B7 this session " + s.SessionRatio.ToString("0.00"));
            set("seed", Fmt.Span(s.SeedSecs), "All-time (with anything seeding) \u00B7 this session " + Fmt.Span(s.SessionSeedSecs));
            set("vpn", Fmt.Span(s.LockedSecs), "All-time, torrents locked until it was back \u00B7 this session " + Fmt.Span(s.SessionLockedSecs));
            var ranges = c.SnapMode ? c.SnapRanges : c.Core.BlocklistRanges;
            set("blocked", s.Blocked.ToString("N0"), "This session " + s.SessionBlocked.ToString("N0") + " \u00B7 " + (ranges > 0 ? ranges.ToString("N0") + " ranges loaded" : "no blocklist loaded"));
            DateTime since; statsNote.Text = "Counted since " + (DateTime.TryParse(s.Since, out since) ? since.ToString("d MMM yyyy") : "now") + ". Kept on this PC only (stats.json in the data folder) - never sent anywhere.";
        }
    }
}
