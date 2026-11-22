// Hallow Torrents - "HallowTorrents.exe --snap <folder>": every window rendered off-screen to PNGs with MADE-UP torrents (never your
// list, settings or files), dark in <folder>, light in <folder>\light. Nothing is started: no engine, no pipe, no tray.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace HallowTorrents
{
    public static class SnapShots
    {
        public static void All(Application app, string dir)
        {
            foreach (var light in new[] { false, true })
            {
                Program.LoadTheme(app, light);
                Set(light ? Path.Combine(dir, "light") : dir);
            }
        }
        static TorrentView V(string id, string name, long size, double pct, string state, long down, long up, int seeds, int leechs, double ratio, string cat, int q)
        {
            return new TorrentView { Id = id, Hash = id.PadRight(40, 'a'), Name = name, Size = size, Progress = pct, State = state, DownRate = down, UpRate = up, Seeds = seeds, Leechs = leechs, Ratio = ratio, Category = cat, Queue = q,
                Complete = pct >= 100, Paused = state == "Paused", SavePath = @"C:\Users\Alex\Downloads\Hallow Torrents", AddedAt = DateTime.Now.AddHours(-q * 3).ToString("s"), Up = (long)(size * ratio), Down = (long)(size * pct / 100),
                Eta = down > 0 && pct < 100 ? (TimeSpan?)TimeSpan.FromSeconds(size * (1 - pct / 100) / down) : null, Scan = pct >= 100 ? "clean" : "", ScanText = pct >= 100 ? "Microsoft Defender found nothing." : null, CompletedAt = pct >= 100 ? DateTime.Now.AddHours(-1).ToString("s") : null };
        }
        static List<TorrentView> Made()
        {
            var l = new List<TorrentView> {
                V("1", "ubuntu-24.04.1-desktop-amd64.iso", 6114656256, 62.4, "Downloading", 9800000, 410000, 41, 12, 0.08, "Other", 1),
                V("2", "Big Buck Bunny (2008) 1080p", 928000000, 100, "Seeding", 0, 260000, 3, 9, 1.42, "Movies", 2),
                V("3", "Night.Sky.S01E02.720p.WEB", 1240000000, 31.0, "Downloading", 2300000, 120000, 18, 30, 0.02, "TV", 3),
                V("4", "Field Recordings Vol 2 [FLAC]", 412000000, 100, "Done (limit reached)", 0, 0, 0, 2, 2.01, "Music", 4),
                V("5", "debian-12.5.0-amd64-DVD-1.iso", 3994091520, 8.2, "Queued", 0, 0, 0, 0, 0, "Other", 5),
                V("6", "Free.Movie.2021.1080p.x264", 1800000, 100, "Seeding", 0, 0, 1, 0, 0.0, "Movies", 6),
                V("7", "Sintel.2010.4K", 4310000000, 0, "Paused", 0, 0, 0, 0, 0, "Movies", 7) };
            l[5].Flagged = true; l[5].Flags = new List<string> { "Free.Movie.2021.1080p.x264.mp4.exe|Disguised: it looks like a mp4 file but it's a program (.exe)" }; l[5].Scan = "threats"; l[5].ScanText = "Microsoft Defender found something: Trojan:Win32/Wacatac.B!ml (made-up). Don't open it - deal with it in Windows Security (or Hallow Guard).";
            l[5].Files = new List<FileView> { new FileView { Index = 0, Path = "Free.Movie.2021.1080p.x264.mp4.exe", Length = 1800000, Progress = 100, Prio = 2 } };
            l[2].Files = new List<FileView> { new FileView { Index = 0, Path = "Night.Sky.S01E02.720p.WEB.mkv", Length = 1239000000, Progress = 31, Prio = 3 }, new FileView { Index = 1, Path = "Night.Sky.S01E02.720p.WEB.srt", Length = 52000, Progress = 100, Prio = 2 }, new FileView { Index = 2, Path = "Sample/sample.mkv", Length = 9800000, Progress = 0, Prio = 0 } };
            l[1].Handoff = "Copied 2 files to \\\\NAS\\media\\Movies."; l[2].Sequential = true;
            foreach (var f in l[2].Files) f.Rel = "Night.Sky.S01E02.720p.WEB\\" + f.Path.Replace('/', '\\');
            l[2].Files[0].Rel = "Night Sky\\Season 01\\Night Sky - S01E02.mkv";
            l[2].Trackers = new List<TrackerView> { new TrackerView { Url = "udp://tracker.example.org:1337/announce", Status = "Working" }, new TrackerView { Url = "https://tracker.example.net/announce", Status = "Working" }, new TrackerView { Url = "http://old-tracker.example.com/announce", Status = "Not working", Message = "no answer from the tracker" } };
            return l;
        }
        static void MadeSearch(Controller c)
        {
            var se = c.Search; var now = DateTime.UtcNow;
            var p = new Indexer { Id = "i1", Kind = "prowlarr", Name = "Prowlarr", BaseUrl = "http://nas:9696", LastSync = Family.Time(DateTime.Now.AddMinutes(-12)) };
            p.Feeds.Add(new TzFeed { Id = "f1", Name = "ExampleTracker", Url = "http://nas:9696/1/api", Caps = true, TvSearch = true, MovieSearch = true, Ok = 48, Fail = 1 });
            p.Feeds.Add(new TzFeed { Id = "f2", Name = "OpenIndex", Url = "http://nas:9696/2/api", Caps = true, TvSearch = true, Ok = 30, Fail = 6 });
            p.Feeds.Add(new TzFeed { Id = "f3", Name = "SlowIndex", Url = "http://nas:9696/3/api", Caps = true, Ok = 5, Fail = 9 });
            var j = new Indexer { Id = "i2", Kind = "torznab", Name = "Home feed", BaseUrl = "http://192.168.1.20:9117/api/v2.0/indexers/demo/results/torznab/api", LastSync = Family.Time(DateTime.Now.AddHours(-3)) };
            j.Feeds.Add(new TzFeed { Id = "f4", Name = "Home feed", Url = j.BaseUrl, Caps = true, Ok = 12 });
            se.Data.Indexers.Add(p); se.Data.Indexers.Add(j);
            Func<string, string, long, int, int, int, double, SearchResult> R = (title, feed, size, seeds, peers, cat, hoursOld) =>
                new SearchResult { Title = title, FeedId = feed, IndexerName = se.Data.Indexers.SelectMany(x => x.Feeds).First(f => f.Id == feed).Name, Size = size, Seeders = seeds, Peers = peers, Cats = new List<int> { cat }, Published = now.AddHours(-hoursOld), Link = "http://nas:9696/dl/" + title.GetHashCode().ToString("x") };
            const long MB = 1024 * 1024;
            var list = new List<SearchResult> {
                R("Night.Sky.S02E06.1080p.WEB-DL.DDP5.1.H.264-NTb", "f1", 2900 * MB, 812, 950, 5040, 20),
                R("Night.Sky.S02E06.2160p.WEB-DL.DV.HDR.DDP5.1.Atmos.H.265-FLUX", "f1", 7400 * MB, 344, 420, 5045, 19),
                R("Night.Sky.S02E06.720p.HDTV.x264-SYNCOPY", "f2", 820 * MB, 156, 190, 5030, 30),
                R("Night.Sky.S02E06.1080p.WEBRip.x265-ELiTE", "f2", 690 * MB, 402, 470, 5040, 18),
                R("Night Sky S02E06 1080p WEB-DL x264 [password in txt]", "f2", 2100 * MB, 25, 30, 5040, 40),
                R("Night.Sky.S02E06.1080p.WEB-DL.x264", "f4", 96 * MB, 9500, 9600, 5040, 1),
                R("Night.Sky.S02E06.CAM.Codec.Pack.Required", "f2", 1400 * MB, 40, 52, 5040, 12),
                R("Night.Sky.S02.1080p.WEB-DL.DDP5.1.H.264-NTb", "f1", 21500 * MB, 120, 160, 5040, 200),
                R("Night.Sky.S02E06.480p.x264-mSD", "f4", 210 * MB, 61, 70, 5030, 21) };
            list[0].InList = false; list[0].InfoHash = "aa11"; list[7].Grabbed = true;
            c.SnapSearch = new SearchOutcome { Query = SearchQuery.From("night sky s02e06", ""), Ms = 2140, Results = Ranker.Rank(list, se.Data.Profile, id => se.Data.Indexers.SelectMany(x => x.Feeds).First(f => f.Id == id).Reliability, now) };
            int n = 0; foreach (var r in c.SnapSearch.Results) r.Token = "r" + (++n);
            c.SnapSearch.Status = new List<FeedStatus> { new FeedStatus { Name = "ExampleTracker", State = "ok", Count = 3, Ms = 640, Local = true }, new FeedStatus { Name = "OpenIndex", State = "ok", Count = 4, Ms = 1210, Local = true }, new FeedStatus { Name = "SlowIndex", State = "timeout", Error = "No answer within 15 s", Ms = 15000, Local = true }, new FeedStatus { Name = "Home feed", State = "ok", Count = 2, Ms = 380, Local = true } };
        }
        static void Set(string dir)
        {
            Directory.CreateDirectory(dir);
            var c = new Controller(true, true); c.S = new TorSettings(); c.S.Fix(); c.S.LegalSeen = true; c.S.HandlerAsked = true; c.S.SaveFolder = @"C:\Users\Alex\Downloads\Hallow Torrents";
            c.S.Cat("Movies").Handoff = true; c.S.Cat("Movies").LibraryFolder = @"\\NAS\media\Movies"; c.S.Cat("TV").Handoff = true; c.S.Cat("TV").LibraryFolder = @"\\NAS\media\TV"; c.S.Cat("TV").Move = true;
            c.S.ScheduleOn = true; c.S.Schedule.Add(new ScheduleRule { From = 18, To = 23, DownKBps = 4000, UpKBps = 300, Days = "weekdays" });
            c.SnapViews = Made();
            // v1.1: made-up RSS feeds / rules / grabs and statistics
            c.Rss = new Rss(null, null); var now = DateTime.Now;
            c.Rss.Data.Feeds.Add(new RssFeed { Id = "f1", Name = "Example TV releases", Url = "https://feeds.example.org/tv.xml", Minutes = 30, LastFetch = Family.Time(now.AddMinutes(-4)), LastCount = 50 });
            c.Rss.Data.Feeds.Add(new RssFeed { Id = "f2", Name = "Linux ISOs", Url = "https://feeds.example.net/linux.rss", Minutes = 120, LastFetch = Family.Time(now.AddMinutes(-35)), LastCount = 20 });
            c.Rss.Data.Rules.Add(new RssRule { Id = "r1", Name = "Night Sky", MustContain = "night sky", Category = "TV", Prefer = "1080p", UpgradeHours = 12 });
            c.Rss.Data.Rules.Add(new RssRule { Id = "r2", Name = "Debian DVDs", MustContain = "debian dvd", MustNotContain = "testing", Category = "Other", Episodes = false, Avoid = "" });
            c.Rss.Data.Grabs.Add(new RssGrab { Title = "debian-12.6.0-amd64-DVD-1.iso", Rule = "Debian DVDs", At = Family.Time(now.AddDays(-2)) });
            c.Rss.Data.Grabs.Add(new RssGrab { Title = "Night.Sky.S02E04.1080p.WEB-DL.x264", Rule = "Night Sky", At = Family.Time(now.AddDays(-1).AddHours(-3)), Key = "night sky|S02E04" });
            c.Rss.Data.Grabs.Add(new RssGrab { Title = "Night.Sky.S02E05.1080p.WEB-DL.x264", Rule = "Night Sky", At = Family.Time(now.AddHours(-2)), Upgraded = true, Key = "night sky|S02E05" });
            c.SnapStats = Stats.Load(null); c.SnapStats.Down = 412L * 1024 * 1024 * 1024; c.SnapStats.Up = 655L * 1024 * 1024 * 1024; c.SnapStats.SeedSecs = 86400 * 23 + 3600 * 5; c.SnapStats.LockedSecs = 3600 * 2 + 1260; c.SnapStats.Blocked = 1284; c.SnapStats.Since = Family.Time(now.AddDays(-40));
            c.SnapStats.SessionDown = 3200L * 1024 * 1024; c.SnapStats.SessionUp = 1900L * 1024 * 1024; c.SnapStats.SessionSeedSecs = 3600 * 3 + 1500; c.SnapStats.SessionLockedSecs = 240; c.SnapStats.SessionBlocked = 37; c.SnapRanges = 214533;
            // v1.3: made-up indexers and search results (ranked by the real ranker; nothing is contacted)
            c.Search = new SearchEngine(null, null, null); MadeSearch(c);
            var w = new MainWindow(c);
            w.Show("transfers"); Snap.Save(w, Path.Combine(dir, "transfers.png"), 1260, 780);
            w.SnapDetail("3", "files"); Snap.Save(w, Path.Combine(dir, "transfers-files.png"), 1260, 780);
            w.SnapDetail("3", "trackers"); Snap.Save(w, Path.Combine(dir, "transfers-trackers.png"), 1260, 780);
            w.Show("rss"); Snap.Save(w, Path.Combine(dir, "rss.png"), 1260, 900);
            w.Show("stats"); Snap.Save(w, Path.Combine(dir, "stats.png"), 1260, 640);
            w.Show("search"); w.SnapSelect(c.SnapSearch.Results[0].Token); Snap.Save(w, Path.Combine(dir, "search.png"), 1260, 860);
            w.SnapSelect(c.SnapSearch.Results.First(r => r.Flagged).Token); Snap.Save(w, Path.Combine(dir, "search-flagged.png"), 1260, 860);
            Snap.Save(Dialogs.IndexersWindow(c, null), Path.Combine(dir, "dialog-indexers.png"), 720, 0);
            Snap.Save(Dialogs.ProfileWindow(c, null), Path.Combine(dir, "dialog-ranking.png"), 640, 0);
            w.SnapDetail("6", "safety"); Snap.Save(w, Path.Combine(dir, "transfers-flagged.png"), 1260, 780);
            w.SnapDetail("2", "general"); Snap.Save(w, Path.Combine(dir, "transfers-details.png"), 1260, 780);
            // the VPN is off: everything stopped, the banner with Connect
            c.SnapLocked = true; foreach (var v in c.SnapViews) { v.DownRate = 0; v.UpRate = 0; v.Seeds = 0; v.Leechs = 0; if (!v.Paused) v.State = "Waiting for VPN"; }
            w.Show("transfers"); Snap.Save(w, Path.Combine(dir, "vpn-locked.png"), 1260, 780);
            // a game is running (pause) + the first-run notes
            c.SnapLocked = false; c.SnapViews = Made(); foreach (var v in c.SnapViews) { v.DownRate = 0; v.UpRate = 0; if (!v.Paused && !v.State.StartsWith("Done")) v.State = "Paused for game"; }
            c.Game = "Delta Force"; c.S.LegalSeen = false; w.Show("transfers"); Snap.Save(w, Path.Combine(dir, "game-paused.png"), 1260, 780);
            c.S.LegalSeen = true; c.S.HandlerAsked = false; c.Game = null; c.SnapViews = Made(); w.Show("transfers"); Snap.Save(w, Path.Combine(dir, "ask-handler.png"), 1260, 780);
            c.S.HandlerAsked = true;
            w.Show("settings"); Snap.Save(w, Path.Combine(dir, "settings.png"), 1260, 2950);
            // v1.2: the Radarr / Sonarr page (made-up sign-in and addresses; nothing listens)
            c.S.ApiOn = true; c.S.WebUiOn = true; c.S.ApiUser = "radarr"; c.S.ApiPassHash = "made-up"; c.S.ApiAllow = "192.168.1.20";
            c.S.Categories.Add(new Category { Name = "radarr", SaveFolder = @"\\NAS\downloads\radarr", Naming = "keep" }); c.S.Categories.Add(new Category { Name = "tv-sonarr", SaveFolder = @"\\NAS\downloads\tv-sonarr", Naming = "keep" });
            w.Show("apps"); Snap.Save(w, Path.Combine(dir, "apps.png"), 1260, 1300);
            // the dialogs (a made-up .torrent in a temp folder for the Add window)
            var tmp = Path.Combine(Path.GetTempPath(), "HallowTorrentsSnap" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                var src = Path.Combine(tmp, "Night.Sky.S01.720p.WEB"); Directory.CreateDirectory(src);
                foreach (var n in new[] { "Night.Sky.S01E01.720p.WEB.mkv", "Night.Sky.S01E02.720p.WEB.mkv", "Night.Sky.S01E03.720p.WEB.mkv", "Codec/Install Codec.exe", "Night.Sky.S01E01.720p.WEB.srt" })
                { var f = Path.Combine(src, n); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllBytes(f, new byte[n.EndsWith(".mkv") ? 300000 : 2000]); }
                var tf = Path.Combine(tmp, "snap.torrent");
                var cr = new MonoTorrent.TorrentCreator(MonoTorrent.TorrentType.V1Only); cr.Announces.Add(new List<string> { "udp://tracker.example.invalid:1337/announce" }); cr.Create(new MonoTorrent.TorrentFileSource(src), tf);
                Snap.Save(Dialogs.AddWindow(c, tf, null), Path.Combine(dir, "dialog-add.png"), 640, 0);
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "dialog-add-error.txt"), e.ToString()); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            Snap.Save(Dialogs.MagnetWindow(null), Path.Combine(dir, "dialog-magnet.png"), 560, 0);
            Snap.Save(Dialogs.CreateWindow(c, null), Path.Combine(dir, "dialog-create.png"), 660, 0);
            Snap.Save(Dialogs.RuleWindow(c, c.Rss.Data.Rules[0], null), Path.Combine(dir, "dialog-rule.png"), 700, 0);
            Snap.Save(Dialogs.RenameFolderWindow(new List<string> { "Night.Sky.S01E02.720p.WEB", "Night.Sky.S01E02.720p.WEB\\Sample" }, null), Path.Combine(dir, "dialog-rename-folder.png"), 560, 0);
            Snap.Save(Dialogs.RemoveWindow("Sintel.2010.4K", null), Path.Combine(dir, "dialog-remove.png"), 470, 0);
            Snap.Save(Dialogs.LimitsWindow("Night.Sky.S01E02.720p.WEB", 2000, 100, null), Path.Combine(dir, "dialog-limits.png"), 420, 0);
            Snap.Save(CrashNotice.Make(Path.Combine(dir, "example.txt")), Path.Combine(dir, "crash.png"), 440, 0);
        }
    }
}
