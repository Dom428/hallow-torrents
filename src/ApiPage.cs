// Hallow Torrents 1.2 - the "Radarr / Sonarr" page: the qBittorrent-compatible Web API (WebApi.cs) - on / off, where it listens, the
// sign-in, the allow-list, the apps' categories and how to connect Radarr, Sonarr and Prowlarr (both layouts: all on this PC, or the
// app on a NAS with a remote path mapping).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;

namespace HallowTorrents
{
    public partial class MainWindow
    {
        FrameworkElement ApiPage()
        {
            var page = new StackPanel { Margin = new Thickness(24, 18, 24, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
            var S = c.S;
            TextBlock status = null;
            Action showStatus = () =>
            {
                if (status == null) return;
                var on = c.ApiRunning;
                status.Text = c.SnapMode ? (S.ApiOn ? "Running on http://" + S.ApiBind + ":" + S.ApiPort + " - sign-in required." : "Off.") : c.ApiStatus;
                status.Foreground = on ? Ui.R("Green") : S.ApiOn ? Ui.R("Amber") : Ui.R("Muted");
            };
            Action restart = () => { Saved(); var e = c.ApplyApi(); showStatus(); if (S.ApiOn) Say(e == null ? "The Web API restarted with the new settings." : e); };

            // ---- on / off
            var top = Section(page, "Radarr, Sonarr and Prowlarr", "Hallow Torrents can stand in for qBittorrent: turn this on, then add a \"qBittorrent\" download client in Radarr, Sonarr or Prowlarr with the address, port, username and password below. Torrents they send wait for the VPN like any other - the VPN lock doesn't change, and while it's off they show as queued, not failed.");
            top.Children.Add(Ui.Switch("Turn on the Web API (qBittorrent Web API v2)", S.ApiOn, on => { S.ApiOn = on; restart(); }));
            status = Ui.T("", 12.5, true, null, 4); top.Children.Add(status); showStatus();

            // ---- v1.4: the web interface on the same server
            var wu = Section(page, "Web interface (phone and browser)", "A Hallow Torrents page for your phone or another PC: the list, add a magnet link or a .torrent file, pause / resume, categories, search through your indexers, statistics and the VPN lock with a Connect VPN button. It's served by the Web API above - same address, port, username and password, same lock-out and the same protection against other sites - so it's only there while the Web API is on. Use it from this PC (http://localhost:" + S.ApiPort + "/ui/) or, if you picked a home-network or Tailscale address below, from your phone on that network. Never put it on the internet.");
            wu.Children.Add(Ui.Switch("Serve the web interface at /ui/", S.WebUiOn, on => { S.WebUiOn = on; restart(); Show("apps"); }));
            var host0 = S.ApiBind == "127.0.0.1" ? "localhost" : S.ApiBind;
            wu.Children.Add(Ui.T(S.WebUiOn ? "Open http://" + host0 + ":" + S.ApiPort + "/ui/ and sign in with the username and password below." : "Off.", 12.5, true, S.WebUiOn ? Ui.R("Green") : Ui.R("Muted"), 4));
            var openUi = Ui.Btn("Open in my browser", () => { if (!c.ApiRunning || !S.WebUiOn) { Say("Turn on the Web API and the web interface first."); return; } c.OpenUrl("http://" + host0 + ":" + S.ApiPort + "/ui/"); });
            openUi.HorizontalAlignment = HorizontalAlignment.Left; wu.Children.Add(openUi);

            // ---- where it listens
            var w = Section(page, "Where it listens", "This PC only is safest. Pick a home-network or Tailscale address only if Radarr runs on another device (your NAS) - and put that device in the allowed addresses. It's never put on your VPN or a public address, never on all addresses at once, and requests from the internet are always refused.");
            var opts = new List<string[]> { new[] { "127.0.0.1", "This PC only (127.0.0.1)" } };
            foreach (var a in LocalApiAddresses()) opts.Add(new[] { a[1], a[0] + " - " + a[1] });
            if (!opts.Any(o => o[0] == S.ApiBind)) opts.Add(new[] { S.ApiBind, S.ApiBind + " (not on this PC right now)" });
            w.Children.Add(Choice(opts.ToArray(), S.ApiBind, id => { S.ApiBind = id; restart(); }));
            w.Children.Add(Dialogs.Labeled("Port", NumField(S.ApiPort, x => { S.ApiPort = x; restart(); }, 1024, 65535)));
            var allow = Ui.Field(S.ApiAllow ?? "", 420);
            allow.LostFocus += (s, e) =>
            {
                var t = allow.Text.Trim(); if (t == (S.ApiAllow ?? "")) return;
                string bad; var list = ApiGuard.ParseAllow(t, out bad);
                if (bad != null) { Say("\"" + bad + "\" isn't an address or a range like 192.168.1.0/24."); return; }
                S.ApiAllow = t.Length == 0 ? null : string.Join(", ", t.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)); Saved(); Say(list.Count == 0 ? "Any home-network address may connect (with the password)." : "Only " + S.ApiAllow + " may connect.");
            };
            w.Children.Add(Dialogs.Labeled("Allowed addresses", allow));
            w.Children.Add(Ui.Muted("Optional: addresses or ranges, e.g. 192.168.1.20 (your NAS) or 100.64.0.0/10 (Tailscale). Empty = any home-network address, with the password.", 12, 0));

            // ---- sign-in
            var si = Section(page, "Sign-in", "The apps sign in with this username and password, like qBittorrent's Web UI. The password is kept only as a salted hash. After 5 wrong passwords in 15 minutes that address is locked out for 15 minutes.");
            var user = Ui.Field(S.ApiUser ?? "", 240);
            user.LostFocus += (s, e) => { var t = user.Text.Trim(); if (t == (S.ApiUser ?? "")) return; S.ApiUser = t.Length == 0 ? null : t; restart(); };
            si.Children.Add(Dialogs.Labeled("Username", user));
            var pw = new PasswordBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), CaretBrush = Ui.R("Text") };
            var pwNote = Ui.Muted(string.IsNullOrEmpty(S.ApiPassHash) ? "No password yet - the Web API stays off until you set one." : "A password is set.", 12, 0); pwNote.VerticalAlignment = VerticalAlignment.Center; pwNote.Margin = new Thickness(12, 0, 0, 0);
            var setPw = Ui.Btn("Set password", () =>
            {
                if (pw.Password.Length < 8) { Say("Use at least 8 characters."); return; }
                S.ApiPassHash = ApiAuth.Hash(pw.Password); pw.Clear(); if (c.Api != null) c.Api.Guard.EndAll();
                pwNote.Text = "A password is set."; restart(); Say("Password saved (as a hash). Apps that were signed in sign in again with the new one.");
            });
            setPw.Margin = new Thickness(8, 0, 0, 0);
            var pwRow = new StackPanel { Orientation = Orientation.Horizontal }; pwRow.Children.Add(pw); pwRow.Children.Add(setPw); pwRow.Children.Add(pwNote);
            si.Children.Add(Dialogs.Labeled("Password", pwRow));
            si.Children.Add(Dialogs.Labeled("Sign-in lasts (minutes)", NumField(S.ApiSessionMinutes, x => { S.ApiSessionMinutes = x; Saved(); }, 1, 1440)));
            si.Children.Add(Ui.Muted("A sign-in that isn't used for this long expires; the apps sign in again by themselves.", 12, 0));

            // ---- categories
            var ca = Section(page, "Categories for the apps", "Radarr uses the category radarr and Sonarr tv-sonarr (you pick these in each app). They're added here the first time an app uses them, and you can give each its own folder in Settings > Categories. Finished downloads stay where they are until the app has imported them - never moved into your library first - and only show as finished after Defender's scan; a flagged download shows as an error so it isn't imported. Seeding stops at the ratio or time the app (or the category) asks for.");
            var names = string.Join("   ", S.Categories.Select(x => x.Name + (string.IsNullOrWhiteSpace(x.SaveFolder) ? "" : " → " + x.SaveFolder)));
            ca.Children.Add(Ui.T(names, 12.5, false, Ui.R("TextSoft"), 8));
            var addCats = Ui.Btn("Add radarr and tv-sonarr", () =>
            {
                foreach (var n in new[] { "radarr", "tv-sonarr" }) if (S.Cat(n) == null) S.Categories.Add(new Category { Name = n, Naming = "keep" });
                Saved(); Show("apps"); Say("Added. Give them their own folders in Settings > Categories if you like.");
            });
            addCats.HorizontalAlignment = HorizontalAlignment.Left; ca.Children.Add(addCats);

            // ---- how to connect
            var host = S.ApiBind == "127.0.0.1" ? "localhost" : S.ApiBind;
            var how = Section(page, "Connect Radarr (Sonarr and Prowlarr work the same way)");
            foreach (var line in new[] {
                "1.  In Radarr: Settings > Download Clients > + > qBittorrent.",
                "2.  Host " + host + ", port " + S.ApiPort + ", Use SSL off, and the username and password above.",
                "3.  Category radarr (Sonarr: tv-sonarr). Click Test, then Save. Prowlarr: Settings > Download Clients > qBittorrent, the same way.",
                "All on this PC: that's it - no path mapping needed.",
                "Radarr on your NAS: pick this PC's home-network or Tailscale address above, allow the NAS's address, and point the radarr category's folder at a NAS share (e.g. \\\\NAS\\downloads\\radarr). Then in Radarr add a remote path mapping: Host = this PC's address, Remote path = \\\\NAS\\downloads\\radarr\\, Local path = the same folder as Radarr sees it (e.g. /downloads/radarr/)." })
                how.Children.Add(Ui.T(line, 12.5, false, Ui.R("TextSoft"), 6));
            how.Children.Add(Ui.Muted("Never forward this port on your router - the Web API is for your own devices only.", 12, 0));

            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        // home-network and Tailscale addresses of this PC (never the VPN's adapter): { adapter name, address }
        List<string[]> LocalApiAddresses()
        {
            if (c.SnapMode) return new List<string[]> { new[] { "Ethernet", "192.168.1.50" }, new[] { "Tailscale", "100.101.102.103" } };
            var r = new List<string[]>();
            try
            {
                foreach (var a in SafeList().Where(x => x.Up && !x.VpnLike))
                    foreach (var ip in a.IPv4)
                        if (!IPAddress.IsLoopback(ip) && ApiGuard.IsLocalAddress(ip) && !ip.ToString().StartsWith("169.254.") && (c.Core == null || !ip.Equals(c.Core.BoundIp))) r.Add(new[] { a.Name, ip.ToString() });
            }
            catch { }
            return r;
        }
    }
}
