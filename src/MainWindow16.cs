// Hallow Torrents 1.6 - the "Jellyfin / NAS" page: where finished downloads go on your NAS, a test, Jellyfin's address and key, a
// rule per category, and the queue (progress, retry, open folder).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HallowTorrents
{
    public partial class MainWindow
    {
        StackPanel nasBox, nasQueue; TextBlock nasTest, nasJelly; string nasKey;
        public const string NasLoginHelp = "If the share needs a login: open File Explorer > This PC > Map network drive, type the folder (\\\\192.168.4.197\\media), tick \"Reconnect at sign-in\" and \"Connect using different credentials\", sign in with your NAS user and tick \"Remember my credentials\". Windows keeps that login (Credential Manager) and Hallow Torrents uses it through Windows - it never asks for, sees or stores your NAS password.";
        FrameworkElement NasPage()
        {
            var S = c.S; var nas = c.NasNow;
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
            // ---- the NAS folder
            var d = Section(page, "Send to Jellyfin on my NAS", "When a download is finished and Microsoft Defender's scan has passed, it's copied to your NAS with the names Jellyfin understands - \"Movies\\Title (Year)\\Title (Year).mkv\", \"Shows\\Show\\Season 01\\Show - S01E02.mkv\", subtitles alongside. Copies are checked (size and checksum) and carry on where they stopped. Flagged downloads are never sent. This goes over your home network only - never through the VPN, and never to a public address.");
            d.Children.Add(Ui.Switch("Send finished downloads to my NAS", S.NasOn, on => { S.NasOn = on; Saved(); }));
            var folder = Ui.Field(S.NasFolder ?? "", 520);
            folder.LostFocus += (s, e) => { var t = folder.Text.Trim(); S.NasFolder = t.Length == 0 ? null : t; Saved(); };
            d.Children.Add(Dialogs.Labeled("NAS folder", folder));
            var mv = Ui.Field(S.NasMovies ?? "", 200); mv.LostFocus += (s, e) => { S.NasMovies = mv.Text.Trim(); Saved(); };
            var sh = Ui.Field(S.NasShows ?? "", 200); sh.LostFocus += (s, e) => { S.NasShows = sh.Text.Trim(); Saved(); };
            d.Children.Add(Dialogs.Labeled("Movies subfolder", mv)); d.Children.Add(Dialogs.Labeled("Shows subfolder", sh));
            d.Children.Add(Ui.Muted("Point Jellyfin's Movies library at <folder>\\" + (string.IsNullOrWhiteSpace(S.NasMovies) ? "" : S.NasMovies) + " and its Shows library at <folder>\\" + (string.IsNullOrWhiteSpace(S.NasShows) ? "" : S.NasShows) + " (empty = the folder itself). Other categories get a folder of their own name.", 12, 6));
            nasTest = Ui.T("", 12.5, true, null, 6);
            var test = Ui.Btn("Test the folder", () =>
            {
                if (c.SnapMode) { nasTest.Text = "Reachable and writable - \\\\192.168.4.197\\media is on your home network."; nasTest.Foreground = Ui.R("Green"); return; }
                S.NasFolder = folder.Text.Trim(); Saved(); nasTest.Text = "Checking..."; nasTest.Foreground = Ui.R("TextSoft");
                Task.Run(() => nas.CheckDestination()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { nasTest.Text = t.Result ?? "Reachable and writable - it's on your own network."; nasTest.Foreground = t.Result == null ? Ui.R("Green") : Ui.R("Red"); })));
            });
            test.HorizontalAlignment = HorizontalAlignment.Left; d.Children.Add(test); d.Children.Add(nasTest);
            d.Children.Add(Ui.Muted(NasLoginHelp, 12, 0));
            // ---- Jellyfin
            var j = Section(page, "Jellyfin (optional)", "With its address and an API key, Jellyfin is told about new downloads straight away instead of at its next scan. The key is kept encrypted for your Windows user (nas.json) and never shown or logged. Jellyfin: Dashboard > API Keys > + (name it Hallow Torrents).");
            var url = Ui.Field(S.JellyfinUrl ?? "", 360); url.LostFocus += (s, e) => { var t = url.Text.Trim(); S.JellyfinUrl = t.Length == 0 ? null : t; Saved(); };
            j.Children.Add(Dialogs.Labeled("Server address", url));
            var key = new PasswordBox { Width = 360, Height = 30, Padding = new Thickness(6, 4, 6, 4), Background = Ui.R("Well"), CaretBrush = Ui.R("Text"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line") };
            var keyRow = new StackPanel { Orientation = Orientation.Horizontal }; keyRow.Children.Add(key);
            var keyNote = Ui.Muted(nas.HasKey ? "A key is saved." : "No key saved.", 12, 0); keyNote.VerticalAlignment = VerticalAlignment.Center; keyNote.Margin = new Thickness(10, 0, 0, 0);
            var save = Ui.Btn("Save key", () => { if (c.SnapMode) return; if (key.Password.Trim().Length == 0) { Say("Paste the API key first."); return; } nas.JellyfinKey = key.Password; key.Password = ""; keyNote.Text = "A key is saved."; Say("Jellyfin API key saved (encrypted)."); });
            save.Margin = new Thickness(8, 0, 0, 0); keyRow.Children.Add(save);
            var clear = Ui.Btn("Remove key", () => { if (c.SnapMode) return; nas.JellyfinKey = null; keyNote.Text = "No key saved."; Say("Removed."); }); clear.Margin = new Thickness(8, 0, 0, 0); keyRow.Children.Add(clear); keyRow.Children.Add(keyNote);
            j.Children.Add(Dialogs.Labeled("API key", keyRow));
            nasJelly = Ui.T("", 12.5, true, null, 0);
            var check = Ui.Btn("Check the server", () =>
            {
                if (c.SnapMode) { nasJelly.Text = "Villa - Jellyfin 4.9.5.0 (at a private address)"; nasJelly.Foreground = Ui.R("Green"); return; }
                S.JellyfinUrl = url.Text.Trim(); Saved(); nasJelly.Text = "Asking..."; nasJelly.Foreground = Ui.R("TextSoft");
                Task.Run(() => { string err; var info = nas.Jellyfin.Info(out err); return info != null ? info + " (at a private address)" : "x" + err; })
                    .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { var ok = !t.Result.StartsWith("x"); nasJelly.Text = ok ? t.Result : t.Result.Substring(1); nasJelly.Foreground = ok ? Ui.R("Green") : Ui.R("Red"); })));
            });
            check.HorizontalAlignment = HorizontalAlignment.Left; check.Margin = new Thickness(0, 4, 0, 4); j.Children.Add(check); j.Children.Add(nasJelly);
            // ---- per category
            var cs = Section(page, "What happens to each category", "Copy: on the NAS, and it keeps seeding from this PC. Move after seeding: copied now; when its seeding goal (ratio / time) is reached it stops and the PC's copy goes to the Recycle Bin. Move now: copied, then it stops seeding and the PC's copy goes to the Recycle Bin. Torrents Radarr / Sonarr added are only ever copied (they import from here).");
            foreach (var cat in S.Categories)
            {
                var cc = cat; var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var name = Ui.T(cc.Name, 12.5, true); name.Width = 120; name.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(name, Dock.Left); row.Children.Add(name);
                row.Children.Add(Choice(new[] { new[] { "off", "Don't send" }, new[] { "copy", "Copy (keep seeding here)" }, new[] { "after", "Move after seeding" }, new[] { "now", "Move now" } }, cc.Nas ?? "off", id => { cc.Nas = id; Saved(); }));
                cs.Children.Add(row);
            }
            // ---- queue
            var q = Section(page, "Queue", null);
            nasQueue = new StackPanel(); q.Children.Add(nasQueue);
            nasBox = page; nasKey = null; RefreshNas();
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        static string NextAt(string iso) { DateTime d; return DateTime.TryParse(iso, out d) ? d.ToString("HH:mm") : "soon"; }
        void RefreshNas()
        {
            if (nasBox == null) return; var nas = c.NasNow;
            List<NasItem> items; lock (nas.Lock) items = nas.Data.Items.AsEnumerable().Reverse().Take(30).ToList();
            var key = string.Join(";", items.Select(i => i.Id + i.State + (int)i.Progress + i.Error + i.LocalRemoved));
            if (key == nasKey) return; nasKey = key;
            nasQueue.Children.Clear();
            if (items.Count == 0) { nasQueue.Children.Add(Ui.Muted("Nothing yet. Finished downloads in a category set to Copy or Move appear here.", 12.5)); return; }
            foreach (var i in items)
            {
                var ii = i; var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(acts, Dock.Right);
                if (i.State == "failed" || i.State == "waiting") acts.Children.Add(Small("Retry now", () => { if (!c.SnapMode) nas.Retry(ii.Id); nasKey = null; RefreshNas(); }));
                acts.Children.Add(Small("Open folder", () => { var f = ii.Files.FirstOrDefault(); if (f != null) c.OpenFolder(System.IO.Path.GetDirectoryName(System.IO.Path.Combine(c.S.NasFolder ?? "", f.Dest))); }));
                if (i.State == "done" || i.State == "failed") acts.Children.Add(Small("Forget", () => { if (!c.SnapMode) nas.Forget(ii.Id); nasKey = null; RefreshNas(); }));
                row.Children.Add(acts);
                var st = i.State == "done" ? "On the NAS" + (i.Rule == "copy" ? " - still seeding here" : i.LocalRemoved ? " - PC copy in the Recycle Bin" : " - moves after its seeding goal") : i.State == "copying" ? "Copying " + i.Progress.ToString("0") + "%" : i.State == "waiting" ? "Waiting for the NAS - next try at " + NextAt(i.NextTry) : i.State == "failed" ? "Failed" : "Queued";
                var pill = Ui.Pill(i.State == "done" ? "Done" : i.State == "failed" ? "Failed" : i.State == "waiting" ? "Waiting" : i.State == "copying" ? "Copying" : "Queued", i.State == "done" ? "Green" : i.State == "failed" ? "Red" : i.State == "waiting" ? "Amber" : "Lilac", i.State == "done" ? "GreenBg" : i.State == "failed" ? "RedBg" : i.State == "waiting" ? "AmberBg" : "LilacBg");
                pill.Width = 70; pill.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(pill, Dock.Left); row.Children.Add(pill);
                var info = new StackPanel();
                info.Children.Add(Ui.T(i.Name, 12.5, true));
                info.Children.Add(Ui.T(st + " · " + i.Files.Count + " file" + (i.Files.Count == 1 ? "" : "s") + ", " + Fmt.Bytes(i.Bytes) + (i.Error != null ? " · " + i.Error : ""), 11.5, false, i.State == "failed" ? Ui.R("Red") : Ui.R("TextSoft")));
                row.Children.Add(info); nasQueue.Children.Add(row);
            }
        }
    }
}
