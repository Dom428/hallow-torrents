// Hallow Torrents - the small themed windows: Add (files, folder, category), Add magnet link, Remove (files to the Recycle Bin only
// if you tick it), per-torrent speed limits, confirm / message, and the crash notice. Built in code; --snap renders them hidden.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MonoTorrent;

namespace HallowTorrents
{
    public static partial class Dialogs
    {
        public static Window Make(string title, double width)
        {
            var w = new Window(); Ui.Dress(w, title, width);
            w.ResizeMode = ResizeMode.NoResize; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return w;
        }
        public static StackPanel Body(Window w) { var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) }; w.Content = sp; return sp; }
        public static WrapPanel Buttons(StackPanel sp) { var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; sp.Children.Add(row); return row; }
        public static void Own(Window w, Window owner) { if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; } }

        public static Window ConfirmWindow(string title, string text, string okLabel, bool danger, Action<bool> done)
        {
            var w = Make("Hallow Torrents", 460); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12.5));
            var row = Buttons(sp); bool answer = false;
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = danger ? Ui.Danger(okLabel, () => { answer = true; w.Close(); }) : Ui.Btn(okLabel, () => { answer = true; w.Close(); }, true); ok.Margin = new Thickness(0); ok.MinWidth = 90;
            row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(answer); };
            w.Loaded += (s, e) => ((Button)row.Children[0]).Focus();
            return w;
        }
        public static bool Confirm(Window owner, string title, string text, string okLabel, bool danger)
        {
            bool r = false; var w = ConfirmWindow(title, text, okLabel, danger, a => r = a); Own(w, owner); w.ShowDialog(); return r;
        }
        public static Window InfoWindow(string title, string text)
        {
            var w = Make("Hallow Torrents", 480); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12.5));
            var row = Buttons(sp); var ok = Ui.Btn("OK", () => w.Close(), true); ok.Margin = new Thickness(0); ok.MinWidth = 80; row.Children.Add(ok);
            return w;
        }
        public static void Info(Window owner, string title, string text) { var w = InfoWindow(title, text); Own(w, owner); w.ShowDialog(); }

        // ---- Add: what's in it (tick the files you want), where it goes, its category. done(entry or null)
        public static Window AddWindow(Controller c, string item, Action<Entry> done) { return AddWindow(c, item, null, done); }
        public static Window AddWindow(Controller c, string item, string category, Action<Entry> done)
        {
            bool magnet = item.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
            var w = Make("Add torrent - Hallow Torrents", 640); var sp = Body(w);
            string name = null; long size = 0; var files = new List<TFile>(); string loadErr = null;
            if (magnet) { MagnetLink m; if (MagnetLink.TryParse(item, out m)) { name = string.IsNullOrWhiteSpace(m.Name) ? "Magnet link" : m.Name; size = m.Size ?? 0; } else loadErr = "That isn't a magnet link."; }
            else
            {
                try { var t = Torrent.Load(item); name = t.Name; size = t.Size; files = t.Files.Select(f => new TFile(f.Path, f.Length)).ToList(); }
                catch (Exception e) { loadErr = "That isn't a valid .torrent file (" + e.Message + ")."; }
            }
            if (loadErr != null)
            {
                sp.Children.Add(Ui.T("Couldn't add it", 15, true, null, 6)); sp.Children.Add(Ui.Muted(loadErr, 12.5));
                var rr = Buttons(sp); var okb = Ui.Btn("OK", () => w.Close(), true); okb.Margin = new Thickness(0); rr.Children.Add(okb);
                w.Closed += (s, e) => { if (done != null) done(null); }; return w;
            }
            sp.Children.Add(Ui.T(name, 15, true, null, 2));
            sp.Children.Add(Ui.Muted((size > 0 ? Fmt.Bytes(size) + " · " : "") + (magnet ? "magnet link - its file list arrives once it starts (over the VPN)" : files.Count + " file" + (files.Count == 1 ? "" : "s")), 12, 10));
            var flags = Media.Suspicious(name, files);
            if (flags.Count > 0) sp.Children.Add(Banner("triangle-exclamation", "Amber", "AmberBg", "Check before opening: " + flags[0][1] + (flags.Count > 1 ? " (+" + (flags.Count - 1) + " more)" : "") + ". Hallow Torrents never opens files by itself.", null));
            // files
            var boxes = new List<CheckBox>();
            if (files.Count > 0)
            {
                var list = new StackPanel();
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i]; var flagged = flags.Any(x => x[0] == f.Path);
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                    var sz = Ui.Num(Fmt.Bytes(f.Length), 11.5, Ui.R("Muted")); DockPanel.SetDock(sz, Dock.Right); row.Children.Add(sz);
                    var cb = new CheckBox { Style = Ui.S("Tick"), IsChecked = !flagged, Margin = new Thickness(0, 2, 0, 2), Content = new TextBlock { Text = f.Path, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = flagged ? Ui.R("Amber") : Ui.R("Text") } };
                    boxes.Add(cb); row.Children.Add(cb); list.Children.Add(row);
                }
                var scroll = new ScrollViewer { Content = list, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                sp.Children.Add(new Border { Child = scroll, Background = Ui.R("Well"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6) });
                var allRow = Ui.Row(Ui.Btn("All", () => boxes.ForEach(b => b.IsChecked = true)), Ui.Btn("None", () => boxes.ForEach(b => b.IsChecked = false))); allRow.Margin = new Thickness(0, 6, 0, 0); sp.Children.Add(allRow);
            }
            // category -> folder
            string cat = category ?? Media.Detect(name, files);
            sp.Children.Add(Ui.Caps("Category", 10.5)); ((FrameworkElement)sp.Children[sp.Children.Count - 1]).Margin = new Thickness(0, 12, 0, 4);
            var folder = Ui.Field(FolderFor(c, cat));
            var chips = new WrapPanel();
            foreach (var cc in c.S.Categories)
            {
                var n = cc.Name; var t = new ToggleButton { Content = n, Style = Ui.S("Chip"), IsChecked = n == cat, Margin = new Thickness(0, 0, 6, 4) };
                t.Click += (s, e) => { cat = n; foreach (ToggleButton o in chips.Children) o.IsChecked = (string)o.Content == n; folder.Text = FolderFor(c, n); };
                chips.Children.Add(t);
            }
            sp.Children.Add(chips);
            sp.Children.Add(Ui.Caps("Save to", 10.5)); ((FrameworkElement)sp.Children[sp.Children.Count - 1]).Margin = new Thickness(0, 10, 0, 4);
            var fr = new DockPanel(); var br = Ui.Btn("Browse...", () => { var p = PickFolder(folder.Text); if (p != null) folder.Text = p; }); br.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(br, Dock.Right); fr.Children.Add(br); fr.Children.Add(folder); sp.Children.Add(fr);
            if (c.Locked) sp.Children.Add(Banner("lock", "Amber", "AmberBg", "Your VPN isn't connected - it's added now and starts by itself once the VPN is on.", null));
            var msg = Ui.Muted("", 12); msg.Foreground = Ui.R("Red"); sp.Children.Add(msg);
            Entry added = null;
            Action<bool> add = start =>
            {
                string err = null; List<int> prio = boxes.Count > 0 ? boxes.Select(b => b.IsChecked == true ? 2 : 0).ToList() : null;
                if (prio != null && prio.All(p => p == 0)) { msg.Text = "Tick at least one file."; return; }
                var save = folder.Text.Trim(); try { Directory.CreateDirectory(save); } catch { msg.Text = "That folder can't be used."; return; }
                added = c.SnapMode ? null : magnet ? c.Core.AddMagnet(item, save, cat, start, out err) : c.Core.AddTorrentFile(item, save, cat, prio, start, out err);
                if (added == null && !c.SnapMode) { msg.Text = err; return; }
                w.Close();
            };
            var row2 = Buttons(sp);
            row2.Children.Add(Ui.Btn("Cancel", () => w.Close())); row2.Children.Add(Ui.Btn("Add paused", () => add(false)));
            var go = Ui.Btn("Add and start", () => add(true), true); go.Margin = new Thickness(0); row2.Children.Add(go);
            w.Closed += (s, e) => { if (done != null) done(added); };
            return w;
        }
        static string FolderFor(Controller c, string cat) { var cc = c.S.Cat(cat); return cc != null && !string.IsNullOrWhiteSpace(cc.SaveFolder) ? cc.SaveFolder : c.S.SaveFolder; }
        public static string PickFolder(string start)
        {
            if (Snap.On) return null;
            using (var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = start ?? "", ShowNewFolderButton = true }) return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
        }
        public static string PickFile(string filter)
        {
            if (Snap.On) return null;
            var d = new Microsoft.Win32.OpenFileDialog { Filter = filter }; return d.ShowDialog() == true ? d.FileName : null;
        }

        public static Window MagnetWindow(Action<string> done)
        {
            var w = Make("Add magnet link - Hallow Torrents", 560); var sp = Body(w);
            sp.Children.Add(Ui.T("Add a magnet link", 15, true, null, 6));
            sp.Children.Add(Ui.Muted("Paste a link that starts with magnet:?", 12.5, 10));
            var f = Ui.Field(""); sp.Children.Add(f);
            try { if (!Snap.On && Clipboard.ContainsText() && Clipboard.GetText().Trim().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) f.Text = Clipboard.GetText().Trim(); } catch { }
            var msg = Ui.Muted("", 12); msg.Foreground = Ui.R("Red"); sp.Children.Add(msg);
            string result = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn("Add", () => { var t = f.Text.Trim(); MagnetLink m; if (!t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || !MagnetLink.TryParse(t, out m)) { msg.Text = "That isn't a magnet link."; return; } result = t; w.Close(); }, true);
            ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Loaded += (s, e) => f.Focus();
            w.Closed += (s, e) => { if (done != null) done(result); };
            return w;
        }

        public static Window RemoveWindow(string name, Action<bool?> done)
        {
            var w = Make("Hallow Torrents", 470); var sp = Body(w);
            sp.Children.Add(Ui.T("Remove " + name + "?", 15, true, null, 6));
            sp.Children.Add(Ui.Muted("It leaves the list. The downloaded files stay where they are unless you tick the box (then they go to the Recycle Bin, so you can still get them back).", 12.5, 10));
            var del = Ui.Switch("Also move its files to the Recycle Bin", false, null); sp.Children.Add(del);
            bool? r = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Danger("Remove", () => { r = del.IsChecked == true; w.Close(); }); ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Loaded += (s, e) => ((Button)row.Children[0]).Focus();
            w.Closed += (s, e) => { if (done != null) done(r); };
            return w;
        }

        public static Window LimitsWindow(string name, int down, int up, Action<int[]> done)
        {
            var w = Make("Hallow Torrents", 420); var sp = Body(w);
            sp.Children.Add(Ui.T("Speed limits for " + name, 15, true, null, 6));
            sp.Children.Add(Ui.Muted("KB/s, 0 = no limit of its own (the overall limits still apply).", 12, 10));
            var d = Ui.Field(down.ToString(), 100); var u = Ui.Field(up.ToString(), 100);
            sp.Children.Add(Labeled("Download", d)); sp.Children.Add(Labeled("Upload", u));
            int[] r = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn("Set", () => { int a, b; if (int.TryParse(d.Text.Trim(), out a) && int.TryParse(u.Text.Trim(), out b) && a >= 0 && b >= 0) { r = new[] { a, b }; w.Close(); } }, true); ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(r); };
            return w;
        }
        public static FrameworkElement Labeled(string label, FrameworkElement f, double labelWidth = 150)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var l = Ui.T(label, 12.5); l.Width = labelWidth; l.VerticalAlignment = VerticalAlignment.Center; dp.Children.Add(l);
            f.HorizontalAlignment = HorizontalAlignment.Left; dp.Children.Add(f); return dp;
        }
        // a coloured strip with an icon (VPN lock, game, warnings)
        public static Border Banner(string icon, string fgKey, string bgKey, string text, UIElement extra)
        {
            var dp = new DockPanel();
            var ic = Icons.Make(icon, 15, Ui.R(fgKey)); ic.Margin = new Thickness(0, 0, 10, 0); ic.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(ic, Dock.Left); dp.Children.Add(ic);
            if (extra != null) { DockPanel.SetDock(extra, Dock.Right); dp.Children.Add(extra); }
            dp.Children.Add(Ui.T(text, 12.5, false, Ui.R("Text")));
            return new Border { Background = Ui.R(bgKey), BorderBrush = Ui.R(fgKey), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 4), Child = dp };
        }
    }

    public static class CrashNotice
    {
        public static Window Make(string file)
        {
            var w = new Window(); Ui.Dress(w, "Hallow Torrents", 440);
            w.ResizeMode = ResizeMode.NoResize; w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T("Hallow Torrents closed unexpectedly last time", 15, true, null, 6));
            DateTime when = DateTime.Now; try { if (File.Exists(file)) when = File.GetLastWriteTime(file); } catch { }
            sp.Children.Add(Ui.Muted("On " + when.ToString("d MMM yyyy, HH:mm") + ". A report was saved on this PC - nothing was sent anywhere.", 12.5, 14));
            var row = new WrapPanel();
            row.Children.Add(Ui.Btn("Show details", () => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); } catch { } }));
            row.Children.Add(Ui.Btn("Open folder", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }));
            var ok = Ui.Btn("OK", () => w.Close(), true); ok.MinWidth = 70; row.Children.Add(ok);
            sp.Children.Add(row); w.Content = sp;
            return w;
        }
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b < 1024) return b + " B"; double v = b / 1024.0; if (v < 1024) return v.ToString("0") + " KB";
            v /= 1024; if (v < 1024) return v.ToString(v < 10 ? "0.0" : "0") + " MB"; v /= 1024; return v.ToString(v < 10 ? "0.00" : "0.0") + " GB";
        }
        public static string Rate(long bps) { return bps <= 0 ? "-" : Bytes(bps) + "/s"; }
        public static string Span(double secs)
        {
            var t = TimeSpan.FromSeconds(secs);
            return t.TotalDays >= 1 ? (int)t.TotalDays + "d " + t.Hours + "h" : t.TotalHours >= 1 ? (int)t.TotalHours + "h " + t.Minutes + "m" : (int)t.TotalMinutes + "m";
        }
    }
}
