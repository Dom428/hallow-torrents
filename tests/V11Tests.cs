// Hallow Torrents 1.1 tests - still on THIS PC only: the same local swarm (tracker + seeder on 127.0.0.1, Hallow Torrents bound to the
// stand-in VPN address 127.0.0.3), plus a tiny local HTTP stand-in on 127.0.0.1 that serves an RSS feed, .torrent files and a
// blocklist and writes down which address every request came from. Temp folders only; RSS test torrents are added paused (nothing
// downloads, nothing ends up in the Recycle Bin).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        // ---------------- pure parts
        static void V11Unit()
        {
            // RSS reading
            var xml = "<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:torrent=\"http://xmlns.ezrss.it/0.1/\"><channel><title>Test feed</title>" +
                "<item><title>A.Show.S01E01.720p.HDTV</title><guid>g1</guid><enclosure url=\"http://x.invalid/a.torrent\" type=\"application/x-bittorrent\"/></item>" +
                "<item><title>A.Show.S01E01.1080p.WEB-DL</title><link>magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&amp;dn=x</link></item>" +
                "<item><title>B.Show.S02E03</title><torrent:magnetURI>magnet:?xt=urn:btih:1123456789abcdef0123456789abcdef01234567</torrent:magnetURI></item>" +
                "</channel></rss>";
            string title; var items = RssParse.Items(Encoding.UTF8.GetBytes(xml), out title);
            Ok(title == "Test feed" && items.Count == 3 && items[0].Torrent == "http://x.invalid/a.torrent" && items[0].Id == "g1" && items[1].Torrent.StartsWith("magnet:") && items[2].Torrent.StartsWith("magnet:?xt=urn:btih:1123"), "RSS 2.0: enclosure, magnet link, torrent:magnetURI");
            var atom = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Atom</title><entry><title>C.Show.S01E05</title><id>e1</id><link rel=\"enclosure\" type=\"application/x-bittorrent\" href=\"http://x.invalid/c.torrent\"/></entry></feed>";
            items = RssParse.Items(Encoding.UTF8.GetBytes(atom), out title);
            Ok(title == "Atom" && items.Count == 1 && items[0].Torrent == "http://x.invalid/c.torrent", "Atom feed");
            var dtd = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><rss><channel><item><title>&x;</title></item></channel></rss>";
            bool safe; try { items = RssParse.Items(Encoding.UTF8.GetBytes(dtd), out title); safe = items.All(i => !i.Title.Contains("[")); } catch { safe = true; }
            Ok(safe, "a feed can't read local files through XML entities");
            // rules
            var r = new RssRule { MustContain = "a show 1080p | a show 720p", MustNotContain = "x265", Avoid = RssRule.DefaultAvoid };
            Ok(RssMatch.Matches(r, "A.Show.S01E01.1080p.WEB-DL") && RssMatch.Matches(r, "A Show S01E01 720p") && !RssMatch.Matches(r, "A.Show.S01E01.2160p") && !RssMatch.Matches(r, "A.Show.S01E01.1080p.x265"), "must contain (with |) / must not contain");
            Ok(RssMatch.Matches(new RssRule { MustContain = "a*show" }, "A.Great.Show.S01") && !RssMatch.Matches(new RssRule { MustContain = "a*show" }, "Other.S01"), "* wildcard");
            var rr = new RssRule { Regex = true, MustContain = @"^A\.Show\.S\d+E\d+", MustNotContain = "(?i)720p" };
            Ok(RssMatch.Matches(rr, "A.Show.S01E01.1080p") && !RssMatch.Matches(rr, "A.Show.S01E01.720p") && !RssMatch.Matches(new RssRule { Regex = true, MustContain = "([" }, "x") && RssMatch.RegexError("([") != null, "regular expressions (a broken one matches nothing)");
            Ok(RssMatch.Avoided(RssRule.DefaultAvoid, "New.Movie.2026.CAM.x264") && RssMatch.Avoided(RssRule.DefaultAvoid, "New Movie 2026 HD-TS") && RssMatch.Avoided(RssRule.DefaultAvoid, "New.Movie.2026.TS.XviD") && !RssMatch.Avoided(RssRule.DefaultAvoid, "Tsunami.2026.1080p") && !RssMatch.Avoided(RssRule.DefaultAvoid, "Show.S01E01.1080p.WEB"), "avoid CAM / TS as whole words (not Tsunami)");
            Ok(RssMatch.Key("Some.Show.S01E02.720p") == "some show|S01E02" && RssMatch.Key("Some Show - 1x02 [1080p]") == "some show|S01E02" && RssMatch.Key("[grp] Some_Show - S01E02-E03") == "some show|S01E02-E03", "episode keys: SxxEyy, 1x02, double episodes");
            Ok(RssMatch.Key("Late.Night.Talk.2026.11.20.720p.WEB") == "late night talk|2026-11-20" && RssMatch.Key("Some.Show.S03.1080p") == "some show|S03" && RssMatch.Key("The.Film.2024.1080p") == "movie|the film|2024" && RssMatch.Key("random words") == null, "date episodes, season packs, movies, nothing");
            Ok(RssMatch.Quality("X.S01E01.1080p.WEB-DL", "1080p") > RssMatch.Quality("X.S01E01.2160p.WEB-DL", "1080p") && RssMatch.Quality("X.S01E01.2160p", null) > RssMatch.Quality("X.S01E01.1080p", null) && RssMatch.Quality("X.1080p.BluRay", null) > RssMatch.Quality("X.1080p.WEBRip", null), "quality: preference first, then resolution, then source");
            // deciding: duplicates and upgrades
            var rss = new Rss(null, null); var feed = new RssFeed { Id = "f", Url = "http://x.invalid/" };
            var rule = new RssRule { Id = "r", Name = "A", MustContain = "a show", Prefer = "1080p" }; rss.Data.Rules.Add(rule);
            Func<string, RssItem> I = t => new RssItem { Title = t, Id = t, Torrent = "magnet:?xt=urn:btih:" + t.GetHashCode().ToString("x8").PadRight(40, '0') };
            var now = new DateTime(2026, 11, 21, 12, 0, 0);
            var d = rss.Decide(feed, new List<RssItem> { I("A.Show.S01E01.720p"), I("A.Show.S01E01.1080p"), I("A.Show.S01E01.CAM"), I("A.Show.S01E02.720p"), I("Other.S01E01") }, now);
            Ok(d.Count == 2 && d.Any(x => x.Item.Title == "A.Show.S01E01.1080p") && d.Any(x => x.Item.Title == "A.Show.S01E02.720p"), "one per episode, the preferred quality, CAM avoided: " + string.Join(", ", d.Select(x => x.Item.Title)));
            foreach (var x in d) rss.Data.Grabs.Add(new RssGrab { Key = x.Key, Score = x.Score, At = Family.Time(now), Title = x.Item.Title });
            Ok(rss.Decide(feed, new List<RssItem> { I("A.Show.S01E01.1080p.REPACK"), I("A.Show.S01E02.1080p") }, now.AddHours(1)).Count == 0, "the same episode is never grabbed twice (no upgrade window)");
            var rule2 = new RssRule { Id = "u", Name = "B", MustContain = "b show", UpgradeHours = 24 }; rss.Data.Rules.Add(rule2);
            d = rss.Decide(feed, new List<RssItem> { I("B.Show.S01E01.720p") }, now); foreach (var x in d) rss.Data.Grabs.Add(new RssGrab { Key = x.Key, Score = x.Score, At = Family.Time(now) });
            var up = rss.Decide(feed, new List<RssItem> { I("B.Show.S01E01.1080p") }, now.AddHours(3));
            Ok(d.Count == 1 && up.Count == 1 && up[0].Replaces != null, "a better quality within the upgrade window replaces it");
            Ok(rss.Decide(feed, new List<RssItem> { I("B.Show.S01E01.2160p") }, now.AddHours(30)).Count == 0 && rss.Decide(feed, new List<RssItem> { I("B.Show.S01E01.480p") }, now.AddHours(4)).Count == 0, "...but not after the window, and never a worse one");
            // blocklists
            uint a, z;
            Ok(Blocklist.ParseLine("Some ISP:1.2.3.0-1.2.3.255", out a, out z) && Blocklist.Ip(a).ToString() == "1.2.3.0" && Blocklist.Ip(z).ToString() == "1.2.3.255", "P2P line");
            Ok(Blocklist.ParseLine("001.009.096.000 - 001.009.096.255 , 000 , Bad range", out a, out z) && Blocklist.Ip(a).ToString() == "1.9.96.0", "eMule .dat line (leading zeros are decimal)");
            Ok(!Blocklist.ParseLine("002.000.000.000 - 002.000.000.255 , 200 , Allowed", out a, out z) && !Blocklist.ParseLine("# comment", out a, out z) && !Blocklist.ParseLine("garbage", out a, out z), "eMule level 128+ is allowed; comments and junk skipped");
            Ok(Blocklist.ParseLine("10.0.0.0/8", out a, out z) && Blocklist.Ip(z).ToString() == "10.255.255.255", "CIDR line");
            var gz = new MemoryStream(); using (var g = new GZipStream(gz, CompressionMode.Compress, true)) { var t = Encoding.ASCII.GetBytes("a:1.1.1.0-1.1.1.255\nb:2.2.2.2-2.2.2.2\n# x\n"); g.Write(t, 0, t.Length); }
            gz.Position = 0; Ok(Blocklist.Parse(gz).Count == 2, "a .gz list");
            // the creator's tracker list, names, the pipe's --category
            string err; var tiers = Creator.Tiers("udp://a.example:1/announce\nhttp://b.example/announce\n\nhttps://c.example/announce", out err);
            Ok(err == null && tiers.Count == 2 && tiers[0].Count == 2 && tiers[1].Count == 1 && Creator.Tiers("ftp://x", out err) == null, "creator: tracker tiers (blank line = new tier), only http(s) / udp");
            Ok(Core.CheckRel("Folder\\new name.mkv") == "Folder\\new name.mkv" && Core.CheckRel("..\\x") == null && Core.CheckRel("a:b") == null && Core.CheckRel("") == null, "rename: no .. and no characters Windows can't use");
            var sa = Controller.SplitAdd("magnet:?xt=urn:btih:abc --category TV"); var sb = Controller.SplitAdd("C:\\x y\\a.torrent");
            Ok(sa[0] == "magnet:?xt=urn:btih:abc" && sa[1] == "TV" && sb[0] == "C:\\x y\\a.torrent" && sb[1] == null, "pipe: add <item> --category <c>");
            // statistics are kept
            var sd = Path.Combine(Dir, "stats1"); Directory.CreateDirectory(sd);
            var st = Stats.Load(sd); st.Tick(0, 0, false, false, 0); Thread.Sleep(120); st.Tick(5000, 2000, true, true, 3); st.Save();
            var st2 = Stats.Load(sd);
            Ok(st2.Down == 5000 && st2.Up == 2000 && st2.SeedSecs > 0.05 && st2.LockedSecs > 0.05 && st2.Blocked == 3 && st2.SessionDown == 0, "statistics persist (stats.json); a new session starts at 0");
        }

        // ---------------- a tiny HTTP stand-in on 127.0.0.1: serves byte arrays by path, records who asked
        class MiniHttp : IDisposable
        {
            readonly TcpListener l; public readonly int Port; public readonly List<string> From = new List<string>(); public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(); volatile bool stop;
            public MiniHttp() { l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; new Thread(Loop) { IsBackground = true }.Start(); }
            public string Url(string path) { return "http://127.0.0.1:" + Port + path; }
            public List<string> Seen() { lock (From) return From.ToList(); }
            void Loop() { while (!stop) { try { var c = l.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(_ => Serve(c)); } catch { } } }
            void Serve(TcpClient c)
            {
                try
                {
                    using (c)
                    {
                        var s = c.GetStream(); s.ReadTimeout = 5000; var head = new List<byte>(); var one = new byte[1];
                        while (head.Count < 16384) { if (s.Read(one, 0, 1) <= 0) break; head.Add(one[0]); var n = head.Count; if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break; }
                        var first = Encoding.ASCII.GetString(head.ToArray()).Split('\n')[0].Split(' '); var path = first.Length > 1 ? first[1] : "/";
                        lock (From) From.Add(((IPEndPoint)c.Client.RemoteEndPoint).Address + " " + path);
                        byte[] body; lock (Files) Files.TryGetValue(path, out body);
                        var h = Encoding.ASCII.GetBytes((body != null ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found") + "\r\nContent-Length: " + (body == null ? 0 : body.Length) + "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                        s.Write(h, 0, h.Length); if (body != null) s.Write(body, 0, body.Length); s.Flush();
                    }
                }
                catch { }
            }
            public void Dispose() { stop = true; try { l.Stop(); } catch { } }
        }
        static string Feed(params string[][] items)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><rss version=\"2.0\"><channel><title>Local test feed</title>");
            foreach (var i in items) sb.Append("<item><title>" + i[0] + "</title><guid>" + i[0] + "</guid>" + (i[1].StartsWith("magnet:") ? "<link>" + System.Security.SecurityElement.Escape(i[1]) + "</link>" : "<enclosure type=\"application/x-bittorrent\" url=\"" + i[1] + "\"/>") + "</item>");
            return sb.Append("</channel></rss>").ToString();
        }
        // a .torrent for random files (not seeded) - for RSS, where nothing needs to download
        static async Task<string> PlainTorrent(string root, string name)
        {
            var src = Path.Combine(root, "rsssrc", name); Directory.CreateDirectory(src); var rnd = new Random(name.GetHashCode());
            var data = new byte[70000]; rnd.NextBytes(data); File.WriteAllBytes(Path.Combine(src, name + ".mkv"), data);
            var c = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }; c.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" });
            var tf = Path.Combine(root, "rsssrc", name + ".torrent"); await c.CreateAsync(new TorrentFileSource(src), tf); return tf;
        }
        // a torrent whose own tracker is dead (core side) while the seeder announces to the real local tracker - same info hash
        static async Task<string> DeadTrackerTorrent(Swarm1 w, string name, int[] sizes)
        {
            var src = Path.Combine(w.Root, "seed", name); Directory.CreateDirectory(src); var rnd = new Random(name.GetHashCode());
            for (int i = 0; i < sizes.Length; i++) { var data = new byte[sizes[i]]; rnd.NextBytes(data); Directory.CreateDirectory(Path.Combine(src, "sub")); File.WriteAllBytes(Path.Combine(src, i == 0 ? "first.bin" : "sub\\more" + i + ".bin"), data); }
            var c = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 }; c.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" });
            var tf = Path.Combine(w.Root, name + ".torrent"); await c.CreateAsync(new TorrentFileSource(src), tf);
            var c2 = new TorrentCreator(TorrentType.V1Only) { PieceLength = 64 * 1024 }; c2.Announces.Add(new List<string> { "http://127.0.0.1:" + w.HttpPort + "/announce" });
            var seedTf = tf + ".seed"; await c2.CreateAsync(new TorrentFileSource(src), seedTf);
            var m = await w.Seeder.AddAsync(await Torrent.LoadAsync(seedTf), Path.Combine(w.Root, "seed"), new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false }.ToSettings()); await m.HashCheckAsync(false); await m.StartAsync();
            return tf;
        }
        static string ShaAll(string dir) { return string.Join(",", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f.Substring(dir.Length), StringComparer.OrdinalIgnoreCase).Select(f => f.Substring(dir.Length) + "=" + Sha(f))); }

        // ---------------- the local swarm, 1.1 parts
        static async Task Swarm11()
        {
            var root = Path.Combine(Dir, "swarm11"); Directory.CreateDirectory(root);
            using (var w = StartSwarm(root, 0))
            using (var web = new MiniHttp())
            {
                Net.AuditOn = true; string a; while (Net.Audit.TryDequeue(out a)) { }
                int port = FreePort(); var save = Path.Combine(root, "dl"); var s = TestSettings(port, save);
                var core = new Core(Path.Combine(root, "core"), () => s); string err;
                var ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 300);

                // ---- RSS: locked while the VPN is off
                var rss = new Rss(core, Path.Combine(root, "core"));
                var tB = await PlainTorrent(root, "Good.Show.S01E01.1080p.WEB-DL"); var tA = await PlainTorrent(root, "Good.Show.S01E01.720p.HDTV");
                var tD = await PlainTorrent(root, "Good.Show.S01E02.720p.HDTV"); var tF1 = await PlainTorrent(root, "Fine.Show.S01E01.720p.HDTV"); var tF2 = await PlainTorrent(root, "Fine.Show.S01E01.1080p.WEB-DL");
                lock (web.Files) { foreach (var t in new[] { tA, tB, tD, tF1, tF2 }) web.Files["/t/" + Path.GetFileName(t)] = File.ReadAllBytes(t); }
                var magD = "magnet:?xt=urn:btih:" + Torrent.Load(tD).InfoHashes.V1.ToHex().ToLowerInvariant() + "&dn=Good.Show.S01E02";
                lock (web.Files) web.Files["/feed.xml"] = Encoding.UTF8.GetBytes(Feed(
                    new[] { "Good.Show.S01E01.720p.HDTV", web.Url("/t/" + Path.GetFileName(tA)) }, new[] { "Good.Show.S01E01.1080p.WEB-DL", web.Url("/t/" + Path.GetFileName(tB)) },
                    new[] { "Good.Show.S01E01.CAM", web.Url("/t/none.torrent") }, new[] { "Other.Show.S01E01.1080p", web.Url("/t/other.torrent") }, new[] { "Good.Show.S01E02.720p.HDTV", magD },
                    new[] { "Fine.Show.S01E01.720p.HDTV", web.Url("/t/" + Path.GetFileName(tF1)) }));
                Ok(rss.AddFeed(web.Url("/feed.xml"), 15) == null && rss.AddFeed("ftp://x/feed", 15) != null, "add a feed (http only)");
                var rssSave = Path.Combine(root, "rss-tv");
                rss.SaveRule(new RssRule { Name = "Good Show", MustContain = "good show", Prefer = "1080p", Category = "TV", SaveFolder = rssSave, Paused = true });
                rss.SaveRule(new RssRule { Name = "Fine Show", MustContain = "fine show", UpgradeHours = 24, Paused = true });
                var feed = rss.Data.Feeds[0];
                var r0 = await rss.Refresh(feed);
                Ok(r0 != null && r0.StartsWith("Waiting for the VPN") && web.Seen().Count == 0 && core.Entries.Count == 0, "VPN off: RSS isn't fetched at all (" + r0 + ")");
                await core.SetVpn(Vpn(VpnIp));
                var r1 = await rss.Refresh(feed);
                var ids = core.Entries.Select(x => x.Name).ToList();
                Ok(r1 == null && feed.LastCount == 6 && feed.Name == "Local test feed", "VPN on: feed fetched (" + r1 + ")");
                Ok(ids.Contains("Good.Show.S01E01.1080p.WEB-DL") && !ids.Contains("Good.Show.S01E01.720p.HDTV") && ids.Any(n => n.StartsWith("Good.Show.S01E02")) && !ids.Any(n => n.Contains("CAM") || n.StartsWith("Other")) && ids.Contains("Fine.Show.S01E01.720p.HDTV") && ids.Count == 3,
                    "rules: S01E01 once in the preferred 1080p, S01E02 (magnet), CAM avoided, other show skipped: " + string.Join(", ", ids));
                var gB = core.Entries.First(x => x.Name.StartsWith("Good.Show.S01E01"));
                Ok(gB.Category == "TV" && gB.SavePath == rssSave && gB.UserPaused, "rule's category, save folder, added paused");
                var seen = web.Seen();
                Ok(seen.Count >= 3 && seen.All(x => x.StartsWith("127.0.0.3 ")), "every RSS request came from the VPN stand-in address only: " + string.Join(", ", seen.Select(x => x.Split(' ')[0]).Distinct()) + " (" + seen.Count + " requests)");
                var audit = Net.Audit.ToArray();
                Ok(audit.Any(x => x.StartsWith("http ")) && audit.Where(x => x.StartsWith("http ")).All(x => x.Contains(" 127.0.0.3:")), "...and every RSS socket was bound to it");
                // the feed again, with the same episodes in other qualities: nothing new (no upgrade window for Good Show)
                lock (web.Files) web.Files["/feed.xml"] = Encoding.UTF8.GetBytes(Feed(
                    new[] { "Good.Show.S01E01.720p.HDTV", web.Url("/t/" + Path.GetFileName(tA)) }, new[] { "Good.Show.S01E01.1080p.WEB-DL.REPACK", web.Url("/t/" + Path.GetFileName(tA)) },
                    new[] { "Good.Show.S01E02.1080p.WEB", web.Url("/t/" + Path.GetFileName(tA)) },
                    new[] { "Fine.Show.S01E01.1080p.WEB-DL", web.Url("/t/" + Path.GetFileName(tF2)) }));
                var before = core.Entries.Count; await rss.Refresh(feed);
                ids = core.Entries.Select(x => x.Name).ToList();
                Ok(ids.Count(n => n.StartsWith("Good.Show")) == 2 && !ids.Contains("Good.Show.S01E01.720p.HDTV"), "the same episodes again: never grabbed twice: " + string.Join(", ", ids));
                Ok(ids.Contains("Fine.Show.S01E01.1080p.WEB-DL") && Wait(() => !core.Entries.Any(x => x.Name == "Fine.Show.S01E01.720p.HDTV"), 3000) && rss.Data.Grabs.Any(g => g.Upgraded && g.Title.StartsWith("Fine.Show.S01E01.1080p")), "upgrade within 24 h: 1080p replaced the 720p");
                Ok(File.Exists(Path.Combine(root, "core", "rss.json")) && new Rss(null, Path.Combine(root, "core")).Data.Grabs.Count == rss.Data.Grabs.Count, "rss.json keeps feeds, rules and what was grabbed");
                foreach (var x in core.Entries.ToList()) await core.Remove(x.Id, false);

                // ---- the blocklist from a URL (over the VPN) and a blocked peer refused
                var p2p = "Local seeder:127.0.0.1-127.0.0.1\nSome range:10.20.0.0-10.20.255.255\n# comment\nAnother:192.0.2.0-192.0.2.255\n";
                var gz = new MemoryStream(); using (var g = new GZipStream(gz, CompressionMode.Compress, true)) { var b = Encoding.ASCII.GetBytes(p2p); g.Write(b, 0, b.Length); }
                lock (web.Files) web.Files["/block.p2p.gz"] = gz.ToArray();
                int nr; var berr = Blocklist.Update(web.Url("/block.p2p.gz"), null, null, core.BlocklistUrlFile, out nr);
                Ok(berr != null && !File.Exists(core.BlocklistUrlFile), "blocklist download while the VPN is off: refused (" + berr + ")");
                var countBefore = web.Seen().Count;
                berr = Blocklist.Update(web.Url("/block.p2p.gz"), core.BoundIp, core.BoundDns, core.BlocklistUrlFile, out nr);
                var bseen = web.Seen().Skip(countBefore).ToList();
                Ok(berr == null && nr == 3 && bseen.Count == 1 && bseen[0].StartsWith("127.0.0.3 "), "blocklist (.gz) downloaded over the VPN stand-in: " + nr + " ranges, from " + string.Join(",", bseen));
                s.BlocklistUrl = web.Url("/block.p2p.gz"); core.ReloadBlocklist();
                Ok(core.BlocklistRanges == 3 && core.IsBlocked(IPAddress.Loopback) && !core.IsBlocked(VpnIp), "ranges loaded: " + core.BlocklistRanges);
                var tfBl = await MakeTorrent(w, "Blocked", new[] { 600000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                var eBl = core.AddTorrentFile(tfBl, null, null, null, true, out err);
                Thread.Sleep(5000);
                Ok(core.Snapshot(false).First(v => v.Id == eBl.Id).Progress == 0 && core.Blocked > 0, "a peer on the blocklist (the seeder) is refused: nothing downloaded, " + core.Blocked + " refusals");
                s.BlocklistUrl = null; core.ReloadBlocklist();
                core.Pause(eBl.Id); Wait(() => core.ManagerForTests(eBl.Id).State == TorrentState.Stopped, 5000); core.Resume(eBl.Id);
                Ok(Wait(() => core.Get(eBl.Id).CompletedAt != null, 20000), "list cleared (no engine restart): after a pause / resume it downloads");
                Ok(Wait(() => core.Stats.Blocked > 0, 2000), "statistics count the blocked peers: " + core.Stats.Blocked);
                // an eMule .dat fixture file, too
                var dat = Path.Combine(root, "ipfilter.dat"); File.WriteAllText(dat, "001.002.003.000 - 001.002.003.255 , 000 , Bad\n127.000.000.001 - 127.000.000.001 , 100 , Local\n127.000.000.002 - 127.000.000.002 , 200 , Allowed\n");
                s.BlocklistFile = dat; core.ReloadBlocklist();
                Ok(core.BlocklistRanges == 2 && core.IsBlocked(IPAddress.Loopback) && !core.IsBlocked(IPAddress.Parse("127.0.0.2")), "eMule .dat file: 2 blocked ranges (level 200 allowed)");
                s.BlocklistFile = null; core.ReloadBlocklist();

                // ---- the torrent creator round trip: create, seed from Hallow Torrents (VPN-bound), download with another client, same files
                var mine = Path.Combine(root, "mine", "Holiday"); Directory.CreateDirectory(Path.Combine(mine, "day2")); var rnd = new Random(7);
                foreach (var f in new[] { "a.bin", "day2\\b.bin", "day2\\c.txt" }) { var data = new byte[f.EndsWith(".txt") ? 1234 : 400000]; rnd.NextBytes(data); File.WriteAllBytes(Path.Combine(mine, f), data); }
                var created = Path.Combine(root, "Holiday.torrent"); double prog = 0;
                var cerr = await Creator.Create(new CreateOptions { Source = mine, Output = created, PieceKB = 32, Trackers = new List<List<string>> { new List<string> { "http://127.0.0.1:" + w.HttpPort + "/announce" } }, WebSeeds = new List<string> { "http://127.0.0.1:9/seed/" }, Comment = "made in a test", SourceTag = "HALLOW", Private = false }, p => prog = p, CancellationToken.None);
                var ct = cerr == null ? Torrent.Load(created) : null;
                Ok(cerr == null && ct.Files.Count == 3 && ct.PieceLength == 32 * 1024 && ct.Comment == "made in a test" && ct.Source == "HALLOW" && ct.HttpSeeds.Count == 1 && !ct.IsPrivate && prog > 0.99, "creator: 3 files, 32 KB pieces, comment, source, web seed (" + cerr + ")");
                var cr2 = Path.Combine(root, "Holiday-private.torrent");
                Ok(await Creator.Create(new CreateOptions { Source = Path.Combine(mine, "a.bin"), Output = cr2, Private = true }, null, CancellationToken.None) == null && Torrent.Load(cr2).IsPrivate && Torrent.Load(cr2).Files.Count == 1 && Torrent.Load(cr2).PieceLength >= 16384, "creator: a single file, auto piece size, private");
                bool finishedEvent = false; core.Completed += x => { if (x.Name == "Holiday") finishedEvent = true; };
                lock (w.Announces) w.Announces.Clear();
                var eC = core.AddCreated(created, Creator.SaveFolderFor(mine), null, out err);
                Ok(eC != null && eC.Created && eC.CompletedAt != null && eC.SavePath == Path.Combine(root, "mine"), "start seeding it straight away (" + err + ")");
                Ok(Wait(() => core.ManagerForTests(eC.Id) != null && core.ManagerForTests(eC.Id).State == TorrentState.Seeding, 15000), "Hallow Torrents seeds it (files checked against the new torrent)");
                var other = Path.Combine(root, "otherclient");
                var mo = await w.Seeder.AddAsync(Torrent.Load(created), other, new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false }.ToSettings()); await mo.StartAsync();
                Ok(Wait(() => mo.Complete, 30000), "another client downloaded it from Hallow Torrents");
                Ok(mo.Complete && ShaAll(Path.Combine(other, "Holiday")) == ShaAll(mine), "the downloaded files' hashes match the originals");
                List<string> ann; lock (w.Announces) ann = w.Announces.ToList();
                Ok(ann.Any(x => x == "127.0.0.3:" + port) && ann.Where(x => x.EndsWith(":" + port)).All(x => x.StartsWith("127.0.0.3:")), "the new torrent was announced from the VPN address only");
                Ok(!finishedEvent && eC.Scan == "own", "your own torrent isn't treated as a finished download (no scan, no hand-off)");
                await mo.StopAsync(); await w.Seeder.RemoveAsync(mo);

                // ---- tracker editing: a dead tracker, then the working one added by hand (still from the VPN address)
                var tfE = await DeadTrackerTorrent(w, "Edit", new[] { 500000, 300000 });
                var eE = core.AddTorrentFile(tfE, null, null, null, true, out err);
                Thread.Sleep(3000);
                Ok(core.Snapshot(false).First(v => v.Id == eE.Id).Progress == 0 && core.TrackersOf(eE.Id).SequenceEqual(new[] { "http://127.0.0.1:9/announce" }), "only a dead tracker: nothing yet");
                lock (w.Announces) w.Announces.Clear();
                var real = "http://127.0.0.1:" + w.HttpPort + "/announce";
                Ok(await core.AddTracker(eE.Id, real) == null && core.TrackersOf(eE.Id).Count == 2, "add a tracker");
                Ok(await core.AddTracker(eE.Id, "wss://nope.example/announce") != null, "a tracker type that can't be bound to the VPN is refused");
                Ok(Wait(() => core.Get(eE.Id).CompletedAt != null, 20000), "...and it downloads through the added tracker");
                lock (w.Announces) ann = w.Announces.ToList();
                Ok(ann.Any(x => x == "127.0.0.3:" + port) && ann.Where(x => x.EndsWith(":" + port)).All(x => x.StartsWith("127.0.0.3:")), "the added tracker saw only the VPN address: " + string.Join(", ", ann.Distinct()));
                Ok(await core.RemoveTracker(eE.Id, "http://127.0.0.1:9/announce") == null && core.TrackersOf(eE.Id).SequenceEqual(new[] { real }) && core.ManagerForTests(eE.Id).TrackerManager.Tiers.SelectMany(t => t.Trackers).Count() == 1, "remove a tracker");
                var udp = "udp://127.0.0.1:" + w.UdpPort + "/announce";
                Ok(await core.EditTracker(eE.Id, real, udp) == null && core.Get(eE.Id).Trackers.SequenceEqual(new[] { udp }), "edit a tracker (kept in the list)");
                await core.Restart();
                Ok(Wait(() => core.ManagerForTests(eE.Id) != null, 5000) && core.ManagerForTests(eE.Id).TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.ToString()).SequenceEqual(new[] { udp }), "edited trackers come back after the engine is rebuilt");

                // ---- renaming files and folders, move storage (the files on disk follow; the data still checks out)
                Ok(Wait(() => core.ManagerForTests(eE.Id).State == TorrentState.Seeding, 15000), "seeding before the renames");
                var oldFirst = Path.Combine(save, "Edit", "first.bin");
                Ok(await core.RenameFile(eE.Id, 0, "Edit\\renamed\\the first.bin") == null && File.Exists(Path.Combine(save, "Edit", "renamed", "the first.bin")) && !File.Exists(oldFirst) && core.ManagerForTests(eE.Id).Files[0].FullPath == Path.Combine(save, "Edit", "renamed", "the first.bin"), "rename a file (into a new folder): moved on disk and in the engine");
                Ok(await core.RenameFile(eE.Id, 1, "Edit\\renamed\\the first.bin") != null, "renaming onto another file is refused");
                Ok(await core.RenameFolder(eE.Id, "Edit", "Edit 2") == null && File.Exists(Path.Combine(save, "Edit 2", "renamed", "the first.bin")) && File.Exists(Path.Combine(save, "Edit 2", "sub", "more1.bin")) && !Directory.Exists(Path.Combine(save, "Edit")), "rename the top folder: everything moved, the old folder is gone");
                var moved = Path.Combine(root, "moved");
                Ok(await core.MoveStorage(eE.Id, moved) == null && core.Get(eE.Id).SavePath == moved && File.Exists(Path.Combine(moved, "Edit 2", "renamed", "the first.bin")) && File.Exists(Path.Combine(moved, "Edit 2", "sub", "more1.bin")) && !Directory.Exists(Path.Combine(save, "Edit 2")), "move storage: the files (with their new names) moved to the new folder");
                Ok(await core.Recheck(eE.Id) == null && Wait(() => core.ManagerForTests(eE.Id).Complete && core.ManagerForTests(eE.Id).State != TorrentState.Hashing, 10000), "force recheck after all that: 100%");
                Ok(Wait(() => core.ManagerForTests(eE.Id).State == TorrentState.Seeding, 10000), "...and it seeds from the new place");
                await core.Restart();
                Ok(Wait(() => core.ManagerForTests(eE.Id) != null, 5000) && core.ManagerForTests(eE.Id).Files[0].FullPath == Path.Combine(moved, "Edit 2", "renamed", "the first.bin"), "after the engine is rebuilt the renamed paths are used again");
                await Task.Delay(500); await core.Recheck(eE.Id);
                Ok(Wait(() => core.ManagerForTests(eE.Id).Complete, 10000), "...and the data still checks out");
                // while the VPN is off (no engine): straight on disk, picked up again when it's back
                await core.SetVpn(new VpnState { Message = "off" });
                Ok(!core.EngineUp && await core.RenameFile(eE.Id, 1, "Edit 2\\sub\\renamed while locked.bin") == null && File.Exists(Path.Combine(moved, "Edit 2", "sub", "renamed while locked.bin")), "rename while the VPN is off: done on disk");
                await core.SetVpn(Vpn(VpnIp));
                Ok(Wait(() => core.ManagerForTests(eE.Id) != null, 5000) && core.ManagerForTests(eE.Id).Files[1].FullPath == Path.Combine(moved, "Edit 2", "sub", "renamed while locked.bin"), "VPN back: the engine uses the new name");
                await Task.Delay(500); await core.Recheck(eE.Id);
                Ok(Wait(() => core.ManagerForTests(eE.Id).Complete, 10000), "...and the data checks out");

                // ---- statistics
                core.Save();
                var sNow = Stats.Load(Path.Combine(root, "core"));
                Ok(sNow.Down > 500000 && sNow.Up > 500000 && sNow.SeedSecs > 1 && sNow.Blocked > 0, "statistics kept on disk: down " + Fmt.Bytes(sNow.Down) + ", up " + Fmt.Bytes(sNow.Up) + ", seeding " + sNow.SeedSecs.ToString("0") + " s, blocked " + sNow.Blocked);
                await core.SetVpn(new VpnState { Message = "off" }); Thread.Sleep(1500); core.Save();
                Ok(Stats.Load(Path.Combine(root, "core")).LockedSecs > 0.5, "time waiting for the VPN is counted");
                audit = Net.Audit.ToArray();
                Ok(audit.Length > 0 && audit.All(x => x.Contains(" 127.0.0.3:")), "1.1 leak check: every socket opened (RSS, blocklist, trackers, peers) was bound to the VPN IP (" + audit.Length + ")");
                ticker.Dispose(); core.Dispose(); Net.AuditOn = false;
            }
        }
    }
}
