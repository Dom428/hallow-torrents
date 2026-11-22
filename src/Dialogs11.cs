// Hallow Torrents 1.1 - the new small windows: a one-line prompt (rename, edit tracker), rename a folder, the torrent creator and an
// RSS rule (with a live preview of what it would pick from the feeds' latest items).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HallowTorrents
{
    public static partial class Dialogs
    {
        static TextBox MultiLine(string text, double height)
        {
            var t = Ui.Field(text ?? ""); t.AcceptsReturn = true; t.TextWrapping = TextWrapping.NoWrap; t.Height = height; t.VerticalContentAlignment = VerticalAlignment.Top;
            t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; t.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            return t;
        }
        static void Cap(StackPanel sp, string text, double top = 10) { var c = Ui.Caps(text, 10.5); c.Margin = new Thickness(0, top, 0, 4); sp.Children.Add(c); }
        static WrapPanel Chips(string[][] options, string current, Action<string> pick)
        {
            var wp = new WrapPanel();
            foreach (var o in options)
            {
                var id = o[0]; var t = new ToggleButton { Content = o[1], Style = Ui.S("Chip"), IsChecked = id == current, Margin = new Thickness(0, 0, 6, 4) };
                t.Click += (s, e) => { foreach (ToggleButton x in wp.Children) x.IsChecked = x == t; pick(id); };
                wp.Children.Add(t);
            }
            return wp;
        }
        public static string PickSave(string name, string folder)
        {
            if (Snap.On) return null;
            var d = new Microsoft.Win32.SaveFileDialog { Filter = "Torrent files (*.torrent)|*.torrent", FileName = name ?? "", DefaultExt = ".torrent", OverwritePrompt = true };
            try { if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) d.InitialDirectory = folder; } catch { }
            return d.ShowDialog() == true ? d.FileName : null;
        }

        // ---- one line of text: done(text or null)
        public static Window PromptWindow(string title, string note, string value, string okLabel, Action<string> done)
        {
            var w = Make("Hallow Torrents", 560); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); if (note != null) sp.Children.Add(Ui.Muted(note, 12.5, 10));
            var f = Ui.Field(value ?? ""); sp.Children.Add(f);
            string r = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn(okLabel, () => { if (f.Text.Trim().Length == 0) return; r = f.Text.Trim(); w.Close(); }, true); ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Loaded += (s, e) => { f.Focus(); f.SelectAll(); };
            w.Closed += (s, e) => { if (done != null) done(r); };
            return w;
        }

        // ---- rename a folder: pick it, type its new name. done(folder, newName) or (null, null)
        public static Window RenameFolderWindow(List<string> folders, Action<string, string> done)
        {
            var w = Make("Hallow Torrents", 560); var sp = Body(w);
            sp.Children.Add(Ui.T("Rename a folder", 15, true, null, 6));
            sp.Children.Add(Ui.Muted("The folder on disk is renamed too, with everything in it. The torrent itself doesn't change.", 12.5, 10));
            string pick = folders[0];
            var name = Ui.Field(Path.GetFileName(pick));
            sp.Children.Add(Chips(folders.Select(f => new[] { f, f }).ToArray(), pick, id => { pick = id; name.Text = Path.GetFileName(id); }));
            Cap(sp, "New name", 6); sp.Children.Add(name);
            string a = null, b = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn("Rename", () => { if (name.Text.Trim().Length == 0) return; a = pick; b = name.Text.Trim(); w.Close(); }, true); ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(a, b); };
            return w;
        }

        // ---- the torrent creator. done(message or null)
        public static Window CreateWindow(Controller c, Action<string> done)
        {
            var w = Make("Create a torrent - Hallow Torrents", 660); var sp = Body(w);
            sp.Children.Add(Ui.T("Create a torrent", 15, true, null, 2));
            sp.Children.Add(Ui.Muted("From your own file or folder. Making it happens on this PC; seeding it goes over your VPN like every other torrent.", 12.5, 6));
            Cap(sp, "File or folder to share");
            var src = Ui.Field(Snap.On ? @"C:\Users\Alex\Videos\Holiday 2026" : "");
            var outF = Ui.Field(Snap.On ? @"C:\Users\Alex\Videos\Holiday 2026.torrent" : "");
            var sizeNote = Ui.Muted(Snap.On ? "2.4 GB in 38 files" : "", 11.5);
            Action srcChanged = () =>
            {
                var s = src.Text.Trim().TrimEnd('\\');
                try { if (File.Exists(s) || Directory.Exists(s)) { sizeNote.Text = Fmt.Bytes(Creator.SizeOf(s)) + (Directory.Exists(s) ? " in " + new MonoTorrent.TorrentFileSource(s).Files.Count() + " files" : ""); if (outF.Text.Trim().Length == 0) outF.Text = s + ".torrent"; } else sizeNote.Text = ""; } catch { sizeNote.Text = ""; }
            };
            src.LostFocus += (s, e) => srcChanged();
            var srcRow = new DockPanel();
            var bFolder = Ui.Btn("Folder...", () => { var p = PickFolder(src.Text); if (p != null) { src.Text = p; outF.Text = ""; srcChanged(); } }); bFolder.Margin = new Thickness(8, 0, 0, 0);
            var bFile = Ui.Btn("File...", () => { var p = PickFile("All files|*.*"); if (p != null) { src.Text = p; outF.Text = ""; srcChanged(); } }); bFile.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(bFolder, Dock.Right); DockPanel.SetDock(bFile, Dock.Right); srcRow.Children.Add(bFolder); srcRow.Children.Add(bFile); srcRow.Children.Add(src);
            sp.Children.Add(srcRow); sizeNote.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(sizeNote);
            Cap(sp, "Piece size");
            int piece = 0;
            var opts = new List<string[]> { new[] { "0", "Auto" } }; foreach (var kb in Creator.PieceSizesKB) opts.Add(new[] { kb.ToString(), kb >= 1024 ? kb / 1024 + " MB" : kb + " KB" });
            sp.Children.Add(Chips(opts.ToArray(), "0", id => piece = int.Parse(id)));
            Cap(sp, "Trackers (one per line - a blank line starts a new tier)");
            var trackers = MultiLine(Snap.On ? "udp://tracker.example.org:1337/announce\nhttps://tracker.example.net/announce" : "", 64); sp.Children.Add(trackers);
            Cap(sp, "Web seeds (one per line, optional)");
            var seeds = MultiLine("", 40); sp.Children.Add(seeds);
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var cl = new StackPanel(); var cr = new StackPanel(); Grid.SetColumn(cr, 2); grid.Children.Add(cl); grid.Children.Add(cr);
            Cap(cl, "Comment", 0); var comment = Ui.Field(""); cl.Children.Add(comment);
            Cap(cr, "Source (for private trackers)", 0); var source = Ui.Field(""); cr.Children.Add(source);
            sp.Children.Add(grid);
            var priv = Ui.Switch("Private (no DHT or peer exchange - only the trackers above)", false, null); priv.Margin = new Thickness(0, 10, 0, 0); sp.Children.Add(priv);
            var seed = Ui.Switch("Start seeding it straight away", true, null); sp.Children.Add(seed);
            Cap(sp, "Save the .torrent file to");
            var outRow = new DockPanel(); var bOut = Ui.Btn("Browse...", () => { var cur = outF.Text.Trim(); var p = PickSave(cur.Length > 0 ? Path.GetFileName(cur) : "", cur.Length > 0 ? Path.GetDirectoryName(cur) : null); if (p != null) outF.Text = p; }); bOut.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(bOut, Dock.Right); outRow.Children.Add(bOut); outRow.Children.Add(outF); sp.Children.Add(outRow);
            var msg = Ui.Muted("", 12); msg.Margin = new Thickness(0, 8, 0, 0); sp.Children.Add(msg);
            var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) }; sp.Children.Add(bar);
            CancellationTokenSource cts = null; string result = null;
            var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => { if (cts != null) cts.Cancel(); else w.Close(); }));
            Button go = null;
            go = Ui.Btn("Create", () =>
            {
                if (c.SnapMode || cts != null) return;
                string err; var tiers = Creator.Tiers(trackers.Text, out err); if (err != null) { msg.Foreground = Ui.R("Red"); msg.Text = err; return; }
                var ws = Creator.Urls(seeds.Text, out err); if (err != null) { msg.Foreground = Ui.R("Red"); msg.Text = err; return; }
                var o = new CreateOptions { Source = src.Text.Trim().TrimEnd('\\'), Output = outF.Text.Trim(), PieceKB = piece, Trackers = tiers, WebSeeds = ws, Private = priv.IsChecked == true, Comment = comment.Text, SourceTag = source.Text };
                if (o.Output.Length == 0 && o.Source.Length > 0) o.Output = o.Source + ".torrent";
                if (!o.Output.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) o.Output += ".torrent";
                bool startSeeding = seed.IsChecked == true;
                cts = new CancellationTokenSource(); bar.Visibility = Visibility.Visible; bar.Value = 0; msg.Foreground = Ui.R("TextSoft"); msg.Text = "Hashing the files..."; go.IsEnabled = false;
                var token = cts.Token;
                Task.Run(() => Creator.Create(o, p => w.Dispatcher.BeginInvoke(new Action(() => bar.Value = p)), token)).ContinueWith(t => w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    cts = null; go.IsEnabled = true; var e = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                    if (e != null) { bar.Visibility = Visibility.Collapsed; msg.Foreground = Ui.R("Red"); msg.Text = e; return; }
                    result = "Created " + Path.GetFileName(o.Output) + ".";
                    if (startSeeding)
                    {
                        string aerr; var x = c.Core.AddCreated(o.Output, Creator.SaveFolderFor(o.Source), null, out aerr);
                        result = x != null ? "Created " + Path.GetFileName(o.Output) + " - it's seeding" + (c.Locked ? " once your VPN is on." : ".") : "Created " + Path.GetFileName(o.Output) + ", but couldn't seed it: " + aerr;
                    }
                    w.Close();
                })));
            }, true);
            go.Margin = new Thickness(0); row.Children.Add(go);
            w.Closed += (s, e) => { if (cts != null) cts.Cancel(); if (done != null) done(result); };
            return w;
        }

        // ---- an RSS rule. done(the rule, changed in place on a copy) or null
        public static Window RuleWindow(Controller c, RssRule rule, Action<RssRule> done)
        {
            var r = rule == null ? new RssRule { Name = "New rule" } : new RssRule { Id = rule.Id, Name = rule.Name, MustContain = rule.MustContain, MustNotContain = rule.MustNotContain, Regex = rule.Regex, Feeds = rule.Feeds == null ? null : rule.Feeds.ToList(), Category = rule.Category, SaveFolder = rule.SaveFolder, Prefer = rule.Prefer, Avoid = rule.Avoid, Enabled = rule.Enabled, Episodes = rule.Episodes, Paused = rule.Paused, UpgradeHours = rule.UpgradeHours };
            var w = Make("RSS rule - Hallow Torrents", 700); var sp = Body(w);
            sp.Children.Add(Ui.T(rule == null ? "New download rule" : "Edit " + rule.Name, 15, true, null, 4));
            var name = Ui.Field(r.Name, 520); sp.Children.Add(Labeled("Name", name, 130));
            var must = Ui.Field(r.MustContain ?? "", 520); var mustNot = Ui.Field(r.MustNotContain ?? "", 520);
            sp.Children.Add(Labeled("Must contain", must, 130)); sp.Children.Add(Labeled("Must not contain", mustNot, 130));
            var rx = Ui.Switch("Regular expressions (instead of words)", r.Regex, null); sp.Children.Add(rx);
            sp.Children.Add(Ui.Muted("Words: all of them must be in the name; | means \"or\"; * matches anything (night sky 1080p | night sky 720p).", 11.5, 4));
            Cap(sp, "Category");
            var cats = new List<string[]> { new[] { "", "Pick for each torrent" } }; cats.AddRange(c.S.Categories.Select(x => new[] { x.Name, x.Name }));
            sp.Children.Add(Chips(cats.ToArray(), r.Category ?? "", id => r.Category = id.Length == 0 ? null : id));
            var folder = Ui.Field(r.SaveFolder ?? "");
            var fr = new DockPanel { Width = 520 }; var br = Ui.Btn("Browse...", () => { var p = PickFolder(folder.Text); if (p != null) folder.Text = p; }); br.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(br, Dock.Right); fr.Children.Add(br); fr.Children.Add(folder);
            sp.Children.Add(Labeled("Save to (optional)", fr, 130));
            var feeds = c.Rss == null ? new List<RssFeed>() : c.Rss.Data.Feeds.ToList();
            if (feeds.Count > 1)
            {
                Cap(sp, "Feeds (none ticked = every feed)");
                var fw = new WrapPanel();
                foreach (var f in feeds) { var ff = f; var cb = new CheckBox { Style = Ui.S("Tick"), Content = ff.Label, IsChecked = r.Feeds != null && r.Feeds.Contains(ff.Id), Margin = new Thickness(0, 0, 14, 4) }; cb.Click += (s, e) => { if (r.Feeds == null) r.Feeds = new List<string>(); if (cb.IsChecked == true) { if (!r.Feeds.Contains(ff.Id)) r.Feeds.Add(ff.Id); } else r.Feeds.Remove(ff.Id); }; fw.Children.Add(cb); }
                sp.Children.Add(fw);
            }
            Cap(sp, "Quality");
            var prefer = Ui.Field(r.Prefer ?? "", 160); var avoid = Ui.Field(r.Avoid ?? "", 520);
            sp.Children.Add(Labeled("Prefer", prefer, 130)); sp.Children.Add(Labeled("Never grab", avoid, 130));
            var eps = Ui.Switch("Episode tracking: never grab the same episode (or movie) twice", r.Episodes, null); sp.Children.Add(eps);
            var up = Ui.Field(r.UpgradeHours.ToString(), 70);
            var upRow = Labeled("Replace with a better quality within (hours, 0 = never)", up, 330); sp.Children.Add(upRow);
            var paused = Ui.Switch("Add them paused", r.Paused, null); sp.Children.Add(paused);
            Cap(sp, "From the feeds right now");
            var preview = new StackPanel(); sp.Children.Add(new Border { Child = new ScrollViewer { Content = preview, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Background = Ui.R("Well"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6) });
            var msg = Ui.Muted("", 12); msg.Foreground = Ui.R("Red"); sp.Children.Add(msg);
            Action read = () => { r.Name = name.Text.Trim().Length == 0 ? "Rule" : name.Text.Trim(); r.MustContain = must.Text.Trim(); r.MustNotContain = mustNot.Text.Trim(); r.Regex = rx.IsChecked == true; r.SaveFolder = folder.Text.Trim().Length == 0 ? null : folder.Text.Trim(); r.Prefer = prefer.Text.Trim(); r.Avoid = avoid.Text.Trim(); r.Episodes = eps.IsChecked == true; r.Paused = paused.IsChecked == true; int h; r.UpgradeHours = int.TryParse(up.Text.Trim(), out h) && h >= 0 ? Math.Min(h, 24 * 30) : 0; };
            Action show = () =>
            {
                read(); preview.Children.Clear();
                var items = new List<RssItem>();
                if (c.Rss != null) lock (c.Rss.Lock) foreach (var kv in c.Rss.Latest) if (r.Feeds == null || r.Feeds.Count == 0 || r.Feeds.Contains(kv.Key)) items.AddRange(kv.Value);
                if (Snap.On) items = new List<RssItem> { new RssItem { Title = "Night.Sky.S02E05.1080p.WEB-DL.x264" }, new RssItem { Title = "Night.Sky.S02E05.720p.HDTV.x264" }, new RssItem { Title = "Night.Sky.S02E05.2160p.WEB.h265" }, new RssItem { Title = "Night Sky S02E04 CAM" }, new RssItem { Title = "Other.Show.S01E01.1080p.WEB" } };
                if (items.Count == 0) { preview.Children.Add(Ui.Muted(c.Locked ? "Feeds are fetched once your VPN is on." : "No items yet - add a feed and refresh it.", 12)); return; }
                var err = r.Regex ? RssMatch.RegexError(r.MustContain) ?? RssMatch.RegexError(r.MustNotContain) : null; msg.Text = err == null ? "" : "That expression doesn't work: " + err;
                var best = items.Where(i => RssMatch.Matches(r, i.Title) && !RssMatch.Avoided(r.Avoid, i.Title)).GroupBy(i => r.Episodes ? RssMatch.Key(i.Title) ?? i.Title : i.Title).Select(g => g.OrderByDescending(i => RssMatch.Quality(i.Title, r.Prefer)).First()).ToList();
                foreach (var i in items.Take(60))
                {
                    var matches = RssMatch.Matches(r, i.Title); var avoided = matches && RssMatch.Avoided(r.Avoid, i.Title); var pick = best.Contains(i);
                    preview.Children.Add(Ui.T((pick ? "\u2713 " : matches ? (avoided ? "\u2717 " : "\u2013 ") : "   ") + i.Title + (pick ? "" : avoided ? "  (avoided)" : matches ? "  (another quality wins)" : ""), 11.5, false, pick ? Ui.R("Green") : matches ? Ui.R("Muted") : Ui.R("Faint")));
                }
            };
            foreach (var t in new[] { must, mustNot, prefer, avoid }) t.TextChanged += (s, e) => show();
            rx.Click += (s, e) => show(); eps.Click += (s, e) => show();
            show();
            RssRule result = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn("Save", () =>
            {
                read();
                if (r.Regex) { var e = RssMatch.RegexError(r.MustContain) ?? RssMatch.RegexError(r.MustNotContain); if (e != null) { msg.Text = "That expression doesn't work: " + e; return; } }
                if (string.IsNullOrWhiteSpace(r.MustContain) && string.IsNullOrWhiteSpace(r.MustNotContain)) { msg.Text = "Give it something to look for (Must contain) - otherwise it would grab everything."; return; }
                result = r; w.Close();
            }, true);
            ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(result); };
            return w;
        }
    }
}
