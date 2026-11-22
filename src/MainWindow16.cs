// Hallow Torrents 1.6 - the "Jellyfin / NAS" page: where finished downloads go on your NAS, a test, Jellyfin's address and key, a
// rule per category, and the queue (progress, retry, open folder). 1.7: "Media server / NAS" - Jellyfin / Emby / Plex / None, the picked
// server's address and key (Plex: X-Plex-Token, how to find it) and, for Plex, its Movies / TV libraries.
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
        // v1.7: where each server's key comes from (Plex: its official support article "Finding an authentication token / X-Plex-Token")
        public const string PlexTokenHelp = "Finding your X-Plex-Token (from Plex's support article \"Finding an authentication token / X-Plex-Token\"): 1. Open Plex Web (http://192.168.4.197:32400/web or app.plex.tv) and sign in as the server's owner. 2. Open any movie or episode in your library. 3. Click the \"...\" (More) button > Get Info. 4. Click View XML (bottom left of that window). 5. A new browser tab opens - at the very end of its address is X-Plex-Token=... ; copy everything after the = sign and paste it here, then Save token. Treat it like a password: it's saved encrypted and never shown again.";
        public const string EmbyKeyHelp = "Emby API key: in Emby open the Dashboard (Manage Emby Server) > Advanced (under Expert) > Security > + Add (newer Emby: Settings > API Keys > New API Key), name it Hallow Torrents and paste it here. It needs to be an admin's key - Emby only lets admins start a library scan.";
        public const string JellyfinKeyHelp = "Jellyfin API key: Dashboard > API Keys > + (name it Hallow Torrents).";
        TextBlock plexLibNote; StackPanel plexLibBox;
        void PlexLibraries(StackPanel j, Nas nas, Action keepUrl)
        {
            var S = c.S;
            j.Children.Add(Ui.T("Plex libraries", 13, true, null, 8));
            j.Children.Add(Ui.Muted("Pick the Plex libraries your Movies and Shows folders are in. After a copy, Plex is asked to scan just the new folder there.", 12, 4));
            plexLibNote = Ui.Muted("Movies: " + (S.PlexMoviesName ?? "not picked") + " - TV: " + (S.PlexShowsName ?? "not picked"), 12, 4);
            j.Children.Add(plexLibNote);
            plexLibBox = new StackPanel();
            var load = Ui.Btn("Load libraries", () =>
            {
                Action<List<PlexSection>, string> show = (secs, err) =>
                {
                    plexLibBox.Children.Clear();
                    if (secs == null) { plexLibBox.Children.Add(Ui.T(err, 12.5, true, Ui.R("Red"), 0)); return; }
                    foreach (var kind in new[] { new[] { "movie", "Movies" }, new[] { "show", "TV" } })
                    {
                        var k = kind; var list = secs.Where(x => x.Type == k[0]).ToList();
                        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                        var name = Ui.T(k[1], 12.5, true); name.Width = 120; name.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(name, Dock.Left); row.Children.Add(name);
                        if (list.Count == 0) row.Children.Add(Ui.Muted("No " + (k[0] == "movie" ? "Movies" : "TV Shows") + " library on this Plex server.", 12, 0));
                        else row.Children.Add(Choice(list.Select(x => new[] { x.Key, x.Title }).ToArray(), k[0] == "movie" ? S.PlexMovies : S.PlexShows, id =>
                        {
                            var t = list.First(x => x.Key == id).Title;
                            if (k[0] == "movie") { S.PlexMovies = id; S.PlexMoviesName = t; } else { S.PlexShows = id; S.PlexShowsName = t; }
                            Saved(); plexLibNote.Text = "Movies: " + (S.PlexMoviesName ?? "not picked") + " - TV: " + (S.PlexShowsName ?? "not picked");
                        }));
                        plexLibBox.Children.Add(row);
                    }
                };
                if (c.SnapMode) { show(new List<PlexSection> { new PlexSection { Key = "1", Title = "Movies", Type = "movie" }, new PlexSection { Key = "4", Title = "Kids Movies", Type = "movie" }, new PlexSection { Key = "2", Title = "TV Shows", Type = "show" }, new PlexSection { Key = "3", Title = "Music", Type = "artist" } }, null); return; }
                keepUrl(); plexLibBox.Children.Clear(); plexLibBox.Children.Add(Ui.Muted("Asking Plex...", 12, 0));
                Task.Run(() => { string err; var secs = nas.Plex.Sections(out err); return Tuple.Create(secs, err); })
                    .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => show(t.Result.Item1, t.Result.Item2))));
            });
            load.HorizontalAlignment = HorizontalAlignment.Left; load.Margin = new Thickness(0, 4, 0, 4); j.Children.Add(load); j.Children.Add(plexLibBox);
            if (c.SnapMode) load.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        FrameworkElement NasPage()
        {
            var S = c.S; var nas = c.NasNow;
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
            // ---- the NAS folder
            var srv = S.MediaServer ?? "jellyfin"; var srvName = Nas.ServerName(srv);
            var d = Section(page, "Send to my NAS" + (srv == "none" ? "" : " for " + srvName), "When a download is finished and Microsoft Defender's scan has passed, it's copied to your NAS with the names " + (srv == "none" ? "media servers" : srvName) + " understand" + (srv == "none" ? "" : "s") + " - \"Movies\\Title (Year)\\Title (Year).mkv\", " + (srv == "plex" ? "\"Shows\\Show (Year)\\Season 01\\Show (Year) - s01e02.mkv\"" : "\"Shows\\Show\\Season 01\\Show - S01E02.mkv\"") + ", subtitles alongside. Copies are checked (size and checksum) and carry on where they stopped. Flagged downloads are never sent. This goes over your home network only - never through the VPN, and never to a public address.");
            d.Children.Add(Ui.Switch("Send finished downloads to my NAS", S.NasOn, on => { S.NasOn = on; Saved(); }));
            var folder = Ui.Field(S.NasFolder ?? "", 520);
            folder.LostFocus += (s, e) => { var t = folder.Text.Trim(); S.NasFolder = t.Length == 0 ? null : t; Saved(); };
            d.Children.Add(Dialogs.Labeled("NAS folder", folder));
            var mv = Ui.Field(S.NasMovies ?? "", 200); mv.LostFocus += (s, e) => { S.NasMovies = mv.Text.Trim(); Saved(); };
            var sh = Ui.Field(S.NasShows ?? "", 200); sh.LostFocus += (s, e) => { S.NasShows = sh.Text.Trim(); Saved(); };
            d.Children.Add(Dialogs.Labeled("Movies subfolder", mv)); d.Children.Add(Dialogs.Labeled("Shows subfolder", sh));
            d.Children.Add(Ui.Muted("Point " + (srv == "none" ? "your media server" : srvName) + "'s Movies library at <folder>\\" + (string.IsNullOrWhiteSpace(S.NasMovies) ? "" : S.NasMovies) + " and its Shows library at <folder>\\" + (string.IsNullOrWhiteSpace(S.NasShows) ? "" : S.NasShows) + " (empty = the folder itself). Other categories get a folder of their own name.", 12, 6));
            nasTest = Ui.T("", 12.5, true, null, 6);
            var test = Ui.Btn("Test the folder", () =>
            {
                if (c.SnapMode) { nasTest.Text = "Reachable and writable - \\\\192.168.4.197\\media is on your home network."; nasTest.Foreground = Ui.R("Green"); return; }
                S.NasFolder = folder.Text.Trim(); Saved(); nasTest.Text = "Checking..."; nasTest.Foreground = Ui.R("TextSoft");
                Task.Run(() => nas.CheckDestination()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { nasTest.Text = t.Result ?? "Reachable and writable - it's on your own network."; nasTest.Foreground = t.Result == null ? Ui.R("Green") : Ui.R("Red"); })));
            });
            test.HorizontalAlignment = HorizontalAlignment.Left; d.Children.Add(test); d.Children.Add(nasTest);
            d.Children.Add(Ui.Muted(NasLoginHelp, 12, 0));
            // ---- the media server (v1.7: Jellyfin / Emby / Plex / none)
            var j = Section(page, "Media server", "Which media server is told about new downloads straight away (instead of at its next scan). Its address must be on your own network. The key / token is kept encrypted for your Windows user (nas.json) and never shown or logged.");
            j.Children.Add(Choice(new[] { new[] { "jellyfin", "Jellyfin" }, new[] { "emby", "Emby" }, new[] { "plex", "Plex" }, new[] { "none", "None (just copy)" } }, srv, id => { if (id == S.MediaServer) return; S.MediaServer = id; Saved(); Dispatcher.BeginInvoke(new Action(() => Show("nas"))); }));
            if (srv == "none") j.Children.Add(Ui.Muted("Downloads are only copied to the NAS; your media server (if any) finds them at its next library scan.", 12, 4));
            else
            {
                bool plex = srv == "plex";
                var url = Ui.Field(Nas.UrlOf(S) ?? "", 360);
                Action keepUrl = () => { var t = url.Text.Trim(); if (t.Length == 0) t = null; if (srv == "plex") S.PlexUrl = t; else if (srv == "emby") S.EmbyUrl = t; else S.JellyfinUrl = t; Saved(); };
                url.LostFocus += (s, e) => keepUrl();
                j.Children.Add(Dialogs.Labeled(srvName + " address", url));
                j.Children.Add(Ui.Muted("For example http://192.168.4.197:" + (plex ? "32400" : "8096") + " (just the address works too - port " + (plex ? "32400" : "8096") + " is assumed).", 12, 4));
                var keyWord = plex ? "X-Plex-Token" : "API key";
                var key = new PasswordBox { Width = 360, Height = 30, Padding = new Thickness(6, 4, 6, 4), Background = Ui.R("Well"), CaretBrush = Ui.R("Text"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line") };
                var keyRow = new StackPanel { Orientation = Orientation.Horizontal }; keyRow.Children.Add(key);
                var keyNote = Ui.Muted(nas.HasKeyFor(srv) ? (plex ? "A token is saved." : "A key is saved.") : (plex ? "No token saved." : "No key saved."), 12, 0); keyNote.VerticalAlignment = VerticalAlignment.Center; keyNote.Margin = new Thickness(10, 0, 0, 0);
                var save = Ui.Btn(plex ? "Save token" : "Save key", () => { if (c.SnapMode) return; if (key.Password.Trim().Length == 0) { Say("Paste the " + keyWord + " first."); return; } nas.SetKeyFor(srv, key.Password); key.Password = ""; keyNote.Text = plex ? "A token is saved." : "A key is saved."; Say(srvName + " " + keyWord + " saved (encrypted)."); });
                save.Margin = new Thickness(8, 0, 0, 0); keyRow.Children.Add(save);
                var clear = Ui.Btn(plex ? "Remove token" : "Remove key", () => { if (c.SnapMode) return; nas.SetKeyFor(srv, null); keyNote.Text = plex ? "No token saved." : "No key saved."; Say("Removed."); }); clear.Margin = new Thickness(8, 0, 0, 0); keyRow.Children.Add(clear); keyRow.Children.Add(keyNote);
                j.Children.Add(Dialogs.Labeled(keyWord, keyRow));
                j.Children.Add(Ui.Muted(srv == "plex" ? PlexTokenHelp : srv == "emby" ? EmbyKeyHelp : JellyfinKeyHelp, 12, 4));
                nasJelly = Ui.T("", 12.5, true, null, 0);
                var check = Ui.Btn("Check the server", () =>
                {
                    if (c.SnapMode) { nasJelly.Text = (plex ? "Plex Media Server 1.41.3 (claimed)" : srv == "emby" ? "Villa - Emby 4.8.10.0" : "Villa - Jellyfin 4.9.5.0") + " (at a private address)"; nasJelly.Foreground = Ui.R("Green"); return; }
                    keepUrl(); nasJelly.Text = "Asking..."; nasJelly.Foreground = Ui.R("TextSoft");
                    Task.Run(() => { string err; var info = nas.ServerInfo(out err); return info != null ? info + " (at a private address)" : "x" + err; })
                        .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { var ok = !t.Result.StartsWith("x"); nasJelly.Text = ok ? t.Result : t.Result.Substring(1); nasJelly.Foreground = ok ? Ui.R("Green") : Ui.R("Red"); })));
                });
                check.HorizontalAlignment = HorizontalAlignment.Left; check.Margin = new Thickness(0, 4, 0, 4); j.Children.Add(check); j.Children.Add(nasJelly);
                if (plex) PlexLibraries(j, nas, keepUrl);
            }
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
