// Hallow Torrents - Settings: VPN (always required), downloads, categories and the media library, speed schedule, network,
// games, safety, Hallow.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HallowTorrents
{
    public partial class MainWindow
    {
        StackPanel Section(StackPanel page, string title, string note = null)
        {
            var sp = new StackPanel();
            var cap = Ui.Caps(title, 11); cap.Margin = new Thickness(0, 0, 0, 6); sp.Children.Add(cap);
            if (note != null) sp.Children.Add(Ui.Muted(note, 12, 8));
            var card = Ui.Card(sp, 16); card.Margin = new Thickness(0, 0, 0, 12); page.Children.Add(card);
            return sp;
        }
        WrapPanel Choice(string[][] options, string current, Action<string> pick)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var o in options)
            {
                var id = o[0]; var t = new ToggleButton { Content = o[1], Style = Ui.S("Chip"), IsChecked = id == current, Margin = new Thickness(0, 0, 6, 4) };
                t.Click += (s, e) => { foreach (ToggleButton x in wp.Children) x.IsChecked = x == t; pick(id); };
                wp.Children.Add(t);
            }
            return wp;
        }
        TextBox NumField(int value, Action<int> set, int min = 0, int max = 10000000)
        {
            var f = Ui.Field(value.ToString(), 90);
            f.LostFocus += (s, e) => { int v; if (int.TryParse(f.Text.Trim(), out v) && v >= min && v <= max) set(v); else f.Text = value.ToString(); };
            return f;
        }
        FrameworkElement FolderField(string value, Action<string> set, bool file = false, string filter = null)
        {
            var dp = new DockPanel { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
            var f = Ui.Field(value ?? "");
            var b = Ui.Btn("Browse...", () => { var p = file ? Dialogs.PickFile(filter) : Dialogs.PickFolder(f.Text); if (p != null) { f.Text = p; set(p); } }); b.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(b, Dock.Right); dp.Children.Add(b); dp.Children.Add(f);
            f.LostFocus += (s, e) => set(f.Text.Trim().Length == 0 ? null : f.Text.Trim());
            return dp;
        }
        void Saved() { c.S.Fix(); c.S.Save(); }
        void SavedRestart() { Saved(); c.RestartEngine(); Say("Saved - the engine restarts with the new settings (only while the VPN is on)."); }

        FrameworkElement SettingsPage()
        {
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
            var S = c.S;

            // ---- VPN
            var v = Section(page, "VPN - always required", "Hallow Torrents never runs without a VPN, and there is no switch to change that. Every connection - peers, trackers, DHT, even the tracker name lookups - is bound to your VPN's address, so nothing can leave through your normal connection. If the VPN drops or changes, torrents stop within a second and start again once it's back.");
            var vs = c.SnapMode ? "Locked to NordLynx (10.5.0.2) - Hallow VPN, Netherlands #123" : c.Vpn.Ok ? "Locked to " + c.Vpn.Adapter + " (" + c.Vpn.Ip + ")" : "Stopped: " + c.Vpn.Message;
            v.Children.Add(Ui.T(vs, 12.5, true, (c.SnapMode || c.Vpn.Ok) ? Ui.R("Green") : Ui.R("Red"), 8));
            // 1.9: the kill switch tip - only when Hallow VPN says its kill switch is off
            var hs = c.HallowStatus; var ks = VpnAuto.Flag(hs, "killSwitch");
            if (S.VpnSource == "hallow" && !S.KillTipDismissed && (c.SnapMode ? c.SnapKillOff : ks == false))
            {
                Border tip = null;
                var hide = Ui.Btn("Hide tip", () => { S.KillTipDismissed = true; Saved(); if (tip != null) tip.Visibility = Visibility.Collapsed; }); hide.VerticalAlignment = VerticalAlignment.Center;
                tip = Dialogs.Banner("shield-halved", "Lilac", "LilacBg", "Hallow VPN's kill switch adds a second layer; turn it on in Hallow VPN. (Hallow Torrents' own lock works without it - this blocks every app's traffic if the VPN drops.)", hide);
                tip.Margin = new Thickness(0, 0, 0, 8); v.Children.Add(tip);
            }
            v.Children.Add(Ui.T("Which VPN?", 12.5, true));
            StackPanel adapterBox = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            CheckBox p2pBox = null; TextBlock p2pNote = null;
            v.Children.Add(Choice(new[] { new[] { "hallow", "Hallow VPN (recommended)" }, new[] { "adapter", "My own VPN app (pick its adapter)" } }, S.VpnSource, id =>
            {
                S.VpnSource = id; Saved(); adapterBox.Visibility = id == "adapter" ? Visibility.Visible : Visibility.Collapsed;
                if (p2pBox != null) { p2pBox.IsEnabled = id == "hallow"; p2pBox.IsChecked = VpnAuto.P2pOnly(S); p2pNote.Visibility = id == "hallow" ? Visibility.Collapsed : Visibility.Visible; }
            }));
            adapterBox.Visibility = S.VpnSource == "adapter" ? Visibility.Visible : Visibility.Collapsed;
            adapterBox.Children.Add(Ui.Muted("For the NordVPN app pick NordLynx (or its OpenVPN TAP adapter); WireGuard shows as your tunnel's name. Only adapters that are connected right now have an address.", 12, 6));
            List<AdapterInfo> ads = c.SnapMode ? new List<AdapterInfo> { new AdapterInfo { Id = "a", Name = "NordLynx", Description = "NordLynx Tunnel", Up = true }, new AdapterInfo { Id = "b", Name = "Ethernet", Description = "Realtek PCIe 2.5GbE", Up = true } } : SafeList();
            var adWrap = new WrapPanel();
            foreach (var a in ads.OrderByDescending(x => x.VpnLike).ThenBy(x => x.Name))
            {
                var aa = a; var t = new ToggleButton { Content = a.Name + (a.VpnLike ? "" : " (not a VPN?)") + (a.Up && a.IPv4.Count > 0 ? " - " + a.IPv4[0] : a.Up ? "" : " - off"), Style = Ui.S("Chip"), IsChecked = a.Id == S.VpnAdapter, Margin = new Thickness(0, 0, 6, 4), ToolTip = a.Description };
                t.Click += (s, e) => { foreach (ToggleButton x in adWrap.Children) x.IsChecked = x == t; S.VpnAdapter = aa.Id; Saved(); if (!aa.VpnLike) Say(aa.Name + " doesn't look like a VPN - torrents would go out through it. Pick your VPN's adapter."); };
                adWrap.Children.Add(t);
            }
            adapterBox.Children.Add(adWrap); v.Children.Add(adapterBox);

            // 1.9: VPN on demand - before the requirement, never instead of it
            v.Children.Add(Ui.T("VPN on demand", 12.5, true, null, 4));
            var autoSw = Ui.Switch("Connect Hallow VPN by itself when there's something to download (a P2P server, without changing the server you picked in Hallow VPN)", VpnAuto.On(S), on => { S.VpnAuto = on; Saved(); c.Auto.Reset(); });
            autoSw.Margin = new Thickness(0, 0, 0, 4); v.Children.Add(autoSw);
            var gameSw = Ui.Switch("...also while a game is running", S.VpnAutoInGames, on => { S.VpnAutoInGames = on; Saved(); c.Auto.Reset(); });
            gameSw.Margin = new Thickness(22, 0, 0, 4); v.Children.Add(gameSw);
            v.Children.Add(Ui.Muted("Only when Hallow VPN can connect without asking Windows (IKEv2). If its next connect is NordLynx, Windows asks for permission - then connect from Hallow VPN yourself. If it doesn't connect it tries again after 30 s, 2 min and 5 min, then waits for you.", 12, 8));
            p2pBox = Ui.Switch("P2P servers only - torrents run only while Hallow VPN is on one of Nord's P2P servers", VpnAuto.P2pOnly(S), on => { S.P2pOnly = on && S.VpnSource == "hallow"; Saved(); });
            p2pBox.IsEnabled = S.VpnSource == "hallow"; p2pBox.Margin = new Thickness(0, 0, 0, 4); v.Children.Add(p2pBox);
            p2pNote = Ui.Muted(VpnAuto.P2pAdapterNote, 12, 4); p2pNote.Visibility = S.VpnSource == "hallow" ? Visibility.Collapsed : Visibility.Visible; v.Children.Add(p2pNote);
            v.Children.Add(Ui.Muted("On another server torrents stop as if the VPN had dropped, and the transfers page offers \"Switch to a P2P server\".", 12, 8));
            v.Children.Add(Ui.Muted("Port forwarding (UPnP / NAT-PMP) is always off: it would open a port on your home router, outside the VPN. NordVPN doesn't offer port forwarding either, so fewer peers can connect to you - downloads still work, that's normal.", 12, 8));
            var check = Ui.Btn("Check my torrent IP", () =>
            {
                if (Dialogs.Confirm(this, "Check your torrent IP?", "This opens ipleak.net's torrent address check in your browser (a third-party site - it sees your browser's address, like any website). Click \"Activate\" there, copy its magnet link and add it here: the address ipleak.net shows for the torrent should be your VPN's, not your home one. Remove the test torrent afterwards.", "Open ipleak.net", false))
                    c.OpenUrl("https://ipleak.net/#torrentcheck");
            });
            check.HorizontalAlignment = HorizontalAlignment.Left; v.Children.Add(check);

            // ---- downloads
            var d = Section(page, "Downloads");
            d.Children.Add(Dialogs.Labeled("Save to", FolderField(S.SaveFolder, p => { S.SaveFolder = p; Saved(); })));
            d.Children.Add(Dialogs.Labeled("Downloads at once", NumField(S.MaxActive, x => { S.MaxActive = x; Saved(); if (!c.SnapMode) c.Core.Kick(); }, 1, 50)));
            d.Children.Add(Dialogs.Labeled("Download limit KB/s", NumField(S.DownKBps, x => { S.DownKBps = x; Saved(); })));
            d.Children.Add(Dialogs.Labeled("Upload limit KB/s", NumField(S.UpKBps, x => { S.UpKBps = x; Saved(); })));
            var rf = Ui.Field(S.RatioLimit.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), 90);
            rf.LostFocus += (s, e) => { double x; if (double.TryParse(rf.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) && x >= 0 && x < 1000) { S.RatioLimit = x; Saved(); } };
            d.Children.Add(Dialogs.Labeled("Stop seeding at ratio", rf));
            d.Children.Add(Dialogs.Labeled("...or after minutes", NumField(S.SeedMinutes, x => { S.SeedMinutes = x; Saved(); })));
            d.Children.Add(Ui.Muted("0 = no limit. Categories can have their own ratio goal. Resuming a torrent that reached its limit lets it seed on.", 12, 6));
            d.Children.Add(Ui.Switch("Show the Add window for links from the browser and double-clicked .torrent files", S.AskOnAdd, on => { S.AskOnAdd = on; Saved(); }));
            d.Children.Add(Dialogs.Labeled("Watch folder", FolderField(S.WatchFolder, p => { S.WatchFolder = p; Saved(); })));
            d.Children.Add(Ui.Muted(".torrent files saved in the watch folder are added by themselves (renamed to .added afterwards, never deleted).", 12, 0));

            // ---- categories
            var ct = Section(page, "Categories and the media library", "Each category can have its own folder and ratio goal. When a download finishes it can be copied - or moved, and seeded from there - into your library (a NAS share works, e.g. \\\\NAS\\media\\Movies) with names Jellyfin understands: Movies \"Title (Year)/Title (Year).mkv\", TV \"Show/Season 01/Show - S01E02.mkv\". Flagged downloads and anything Defender doesn't like are never handed off by themselves.");
            foreach (var cat in S.Categories)
            {
                var cc = cat; var box = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
                box.Children.Add(Ui.T(cc.Name, 13, true, null, 2));
                box.Children.Add(Dialogs.Labeled("Save to", FolderField(cc.SaveFolder ?? "", p => { cc.SaveFolder = p; Saved(); })));
                var rg = Ui.Field(cc.RatioGoal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), 90);
                rg.LostFocus += (s, e) => { double x; if (double.TryParse(rg.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) && x >= 0) { cc.RatioGoal = x; Saved(); } };
                box.Children.Add(Dialogs.Labeled("Ratio goal (0 = default)", rg));
                box.Children.Add(Ui.Switch("Hand off to my library when done", cc.Handoff, on => { cc.Handoff = on; Saved(); }));
                box.Children.Add(Dialogs.Labeled("Library folder", FolderField(cc.LibraryFolder ?? "", p => { cc.LibraryFolder = p; Saved(); })));
                box.Children.Add(Choice(new[] { new[] { "copy", "Copy (keep seeding from the download folder)" }, new[] { "move", "Move (seed from the library)" } }, cc.Move ? "move" : "copy", id => { cc.Move = id == "move"; Saved(); }));
                box.Children.Add(Choice(new[] { new[] { "movie", "Movie names" }, new[] { "tv", "TV episode names" }, new[] { "keep", "Keep the torrent's names" } }, cc.Naming, id => { cc.Naming = id; Saved(); }));
                ct.Children.Add(box);
            }

            // ---- schedule
            var sc = Section(page, "Speed schedule", "Limits by the time of day (KB/s, 0 = none). The lowest of these, your overall limits and the game limit applies.");
            sc.Children.Add(Ui.Switch("Use the schedule", S.ScheduleOn, on => { S.ScheduleOn = on; Saved(); }));
            var rules = new StackPanel(); sc.Children.Add(rules);
            Action drawRules = null;
            drawRules = () =>
            {
                rules.Children.Clear();
                foreach (var rule in S.Schedule.ToList())
                {
                    var rr = rule; var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
                    row.Children.Add(Mid(Ui.Muted("From", 12))); row.Children.Add(Pad(NumField(rr.From, x => { rr.From = x; Saved(); }, 0, 24)));
                    row.Children.Add(Mid(Ui.Muted("to", 12))); row.Children.Add(Pad(NumField(rr.To, x => { rr.To = x; Saved(); }, 0, 24)));
                    row.Children.Add(Ui.Muted("h  ↓", 12)); row.Children.Add(Pad(NumField(rr.DownKBps, x => { rr.DownKBps = x; Saved(); })));
                    row.Children.Add(Ui.Muted("↑", 12)); row.Children.Add(Pad(NumField(rr.UpKBps, x => { rr.UpKBps = x; Saved(); })));
                    row.Children.Add(Choice(new[] { new[] { "all", "Every day" }, new[] { "weekdays", "Weekdays" }, new[] { "weekends", "Weekends" } }, rr.Days ?? "all", id => { rr.Days = id; Saved(); }));
                    row.Children.Add(Ui.Btn("Delete", () => { S.Schedule.Remove(rr); Saved(); drawRules(); }));
                    rules.Children.Add(row);
                }
            };
            drawRules();
            var addRule = Ui.Btn("Add rule", () => { S.Schedule.Add(new ScheduleRule { From = 18, To = 23, DownKBps = 2000, UpKBps = 200, Days = "all" }); Saved(); drawRules(); }); addRule.HorizontalAlignment = HorizontalAlignment.Left; sc.Children.Add(addRule);

            // ---- games
            var g = Section(page, "While you play", "Delta Force, Discovery, games Hallow Hub says you're playing, and your own list below.");
            var limitRow = new StackPanel { Orientation = Orientation.Horizontal, Visibility = S.GameMode == "limit" ? Visibility.Visible : Visibility.Collapsed };
            g.Children.Add(Choice(new[] { new[] { "pause", "Pause all torrents" }, new[] { "limit", "Limit the speed" }, new[] { "off", "Do nothing" } }, S.GameMode, id => { S.GameMode = id; Saved(); limitRow.Visibility = id == "limit" ? Visibility.Visible : Visibility.Collapsed; }));
            limitRow.Children.Add(Dialogs.Labeled("Limit to KB/s", NumField(S.GameKBps, x => { S.GameKBps = x; Saved(); }, 1)));
            g.Children.Add(limitRow);
            var eg = Ui.Field(string.Join(", ", S.ExtraGames), 420); eg.LostFocus += (s, e) => { S.ExtraGames = eg.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList(); Saved(); };
            g.Children.Add(Dialogs.Labeled("More games (exe names)", eg));

            // ---- network
            var n = Section(page, "Network", "Changes here restart the engine (only while the VPN is on).");
            n.Children.Add(Dialogs.Labeled("Listening port", NumField(S.Port, x => { S.Port = x; SavedRestart(); }, 1024, 65535)));
            n.Children.Add(Ui.Switch("DHT (find peers without a tracker) - bound to the VPN", S.Dht, on => { S.Dht = on; SavedRestart(); }));
            n.Children.Add(Ui.Switch("Peer exchange (PEX)", S.Pex, on => { S.Pex = on; SavedRestart(); }));
            n.Children.Add(Ui.Switch("Local peer discovery (talks to your home network only, outside the VPN - off unless you need it)", S.Lpd, on => { S.Lpd = on; SavedRestart(); }));
            n.Children.Add(Ui.T("Encryption", 12.5, true));
            n.Children.Add(Choice(new[] { new[] { "prefer", "Prefer encrypted connections" }, new[] { "require", "Require encryption (fewer peers)" } }, S.Encryption, id => { S.Encryption = id; SavedRestart(); }));
            n.Children.Add(Ui.Muted("Prefer (1.5): the handshake is encrypted and full-stream encryption is used when the other side wants it - like qBittorrent's default, with less CPU than 1.4. Everything is inside your VPN's tunnel either way.", 12, 0));
            // ---- v1.5: performance profiles
            PerformanceSection(page);
            // ---- the IP blocklist (v1.1: also from a URL, downloaded over the VPN and refreshed every N days)
            var bl = Section(page, "IP blocklist", "Peers in these address ranges are refused. PeerGuardian / P2P or eMule ipfilter.dat format - plain, .gz or .zip. A list from an address is downloaded over the VPN only.");
            TextBlock blNote = null;
            Action blShow = () =>
            {
                if (blNote == null) return;
                var ranges = c.SnapMode ? c.SnapRanges : c.Core.BlocklistRanges; var blocked = c.SnapMode ? 1284 : c.StatsNow.Blocked;
                DateTime up; var when = DateTime.TryParse(S.BlocklistUpdated, out up) ? " Last downloaded " + up.ToString("d MMM, HH:mm") + "." : "";
                blNote.Text = (ranges > 0 ? "Loaded: " + ranges.ToString("N0") + " ranges \u00B7 " + blocked.ToString("N0") + " peers blocked so far." : "No blocklist loaded.") + when + (string.IsNullOrEmpty(S.BlocklistNote) ? "" : " " + S.BlocklistNote) + (c.SnapMode || c.Core.BlocklistError == null ? "" : " Problem: " + c.Core.BlocklistError);
            };
            bl.Children.Add(Dialogs.Labeled("From a file", FolderField(S.BlocklistFile, p => { S.BlocklistFile = p; Saved(); if (!c.SnapMode) c.Core.ReloadBlocklist(); blShow(); }, true, "Blocklists (*.dat;*.p2p;*.txt;*.gz;*.zip)|*.dat;*.p2p;*.txt;*.gz;*.zip|All files|*.*")));
            var blUrl = Ui.Field(S.BlocklistUrl ?? "", 520);
            blUrl.LostFocus += (s, e) =>
            {
                var t = blUrl.Text.Trim(); if (t == (S.BlocklistUrl ?? "")) return;
                Uri u; if (t.Length > 0 && (!Uri.TryCreate(t, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))) { Say("That isn't a web address (http:// or https://)."); return; }
                S.BlocklistUrl = t.Length == 0 ? null : t; S.BlocklistUpdated = null; S.BlocklistNote = null; Saved();
                c.UpdateBlocklist(m => { Say(m); blShow(); });
            };
            bl.Children.Add(Dialogs.Labeled("From an address", blUrl));
            var blDays = NumField(S.BlocklistDays, x => { S.BlocklistDays = x; Saved(); }, 1, 90);
            var blNow = Ui.Btn("Update now", () => { Say("Downloading the blocklist over the VPN..."); c.UpdateBlocklist(m => { Say(m); blShow(); }); }); blNow.Margin = new Thickness(14, 0, 0, 0);
            var daysRow = new StackPanel { Orientation = Orientation.Horizontal }; daysRow.Children.Add(blDays); var dl = Ui.Muted("days", 12); dl.VerticalAlignment = VerticalAlignment.Center; dl.Margin = new Thickness(8, 0, 0, 0); daysRow.Children.Add(dl); daysRow.Children.Add(blNow);
            bl.Children.Add(Dialogs.Labeled("Update every", daysRow));
            blNote = Ui.Muted("", 12, 0); bl.Children.Add(blNote); blShow();

            // ---- safety
            var sf = Section(page, "Safety", "Finished downloads are checked for programs, scripts and shortcuts hiding in video or music downloads, and for disguised names like movie.mp4.exe. Hallow Torrents never opens a download by itself.");
            sf.Children.Add(Ui.Switch("Scan finished downloads with Microsoft Defender (in the background, at the lowest priority)", S.ScanDone, on => { S.ScanDone = on; Saved(); }));

            // ---- Hallow
            var h = Section(page, "Hallow");
            h.Children.Add(Ui.Switch("Open .torrent files and magnet links with Hallow Torrents (for you only)", c.HandlerOn, on => { c.SetHandler(on); Say(on ? "Done. If Windows still opens another app, pick Hallow Torrents in Settings > Apps > Default apps." : "Removed."); }));
            h.Children.Add(Ui.Switch("Start with Windows (in the tray)", c.StartsWithWindows(), on => c.SetStartWithWindows(on)));
            h.Children.Add(Ui.Switch("Use the Hallow icon (Hallow Hub's tray menu)", S.UseHallowIcon, on => { S.UseHallowIcon = on; Saved(); c.CheckHost(); }));
            h.Children.Add(Ui.Switch("Use the Hallow accent colour", S.UseHallowAccent, on => { S.UseHallowAccent = on; Saved(); c.ApplyTheme(true); }));
            h.Children.Add(Ui.T("Look", 12.5, true));
            h.Children.Add(Choice(new[] { new[] { "follow", "Follow Hallow" }, new[] { "light", "Light" }, new[] { "dark", "Dark" } }, Mode.Own(S.Mode), id => { S.Mode = id; Saved(); c.ApplyTheme(true); }));
            h.Children.Add(Ui.Muted("Now " + (Mode.IsLight ? "light" : "dark") + " - " + Mode.Why(S.Mode, Accent.ReadMode()) + ".", 12, 8));
            var hb = Ui.Row(Ui.Btn("Check for updates", () => c.CheckForUpdate(true)), Ui.Btn("Open data folder", () => c.OpenFolder(TorSettings.Dir))); hb.Margin = new Thickness(0, 4, 0, 8); h.Children.Add(hb);
            h.Children.Add(Ui.Muted(Legal, 12, 4));
            h.Children.Add(Ui.Muted("Built on MonoTorrent 3.0.2 (MIT). Nothing is sent anywhere except torrent traffic over your VPN and the update check on GitHub.", 12, 0));

            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        static FrameworkElement Mid(FrameworkElement f) { f.VerticalAlignment = VerticalAlignment.Center; return f; }
        static FrameworkElement Pad(FrameworkElement f) { f.Margin = new Thickness(6, 0, 10, 0); f.Width = 64; return f; }
        static List<AdapterInfo> SafeList() { try { return TorSettings.Fake ? new FakeNetworks(Path.Combine(TorSettings.Dir, "fake-adapters.txt")).List() : new RealNetworks().List(); } catch { return new List<AdapterInfo>(); } }
    }
}
