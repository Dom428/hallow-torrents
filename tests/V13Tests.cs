// Hallow Torrents 1.3 tests - search. Still THIS PC only: a local Torznab stand-in on 127.0.0.1 plays Prowlarr (an indexer list with a
// good, a slow, a broken and a Usenet indexer) and Jackett (t=indexers + a magnet result behind a redirect), serves fixture results
// (good releases, CAMs, fakes with .exe, wrong sizes, a password "archive", a codec pack, a too-good-to-be-true upload) and .torrent
// files, checks the API key and writes down which address every request came from. Real indexers are never called.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        // ---------------- a Torznab stand-in: routes by path + query, can be slow, records who asked
        class TzReply { public int Status = 200; public string Type = "application/xml"; public byte[] Body = new byte[0]; public string Location; public int DelayMs; }
        class TzServer : IDisposable
        {
            readonly TcpListener l; public readonly int Port; readonly List<string> from = new List<string>(); volatile bool stop;
            public Func<string, TzReply> Route;
            public readonly IPAddress Ip;
            public TzServer() : this(IPAddress.Loopback, 0) { }
            public TzServer(IPAddress ip, int port) { Ip = ip; l = new TcpListener(ip, port); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; new Thread(Loop) { IsBackground = true }.Start(); }   // 1.4: any loopback address / port
            public string Url(string path) { return "http://" + Ip + ":" + Port + path; }
            public List<string> Seen() { lock (from) return from.ToList(); }
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
                        lock (from) from.Add(((IPEndPoint)c.Client.RemoteEndPoint).Address + " " + path);
                        var r = (Route == null ? null : Route(path)) ?? new TzReply { Status = 404 };
                        if (r.DelayMs > 0) Thread.Sleep(r.DelayMs);
                        var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + r.Status + (r.Status == 200 ? " OK" : r.Status == 302 ? " Found" : " Error") + "\r\nContent-Length: " + r.Body.Length + "\r\nContent-Type: " + r.Type + (r.Location != null ? "\r\nLocation: " + r.Location : "") + "\r\nConnection: close\r\n\r\n");
                        s.Write(h, 0, h.Length); s.Write(r.Body, 0, r.Body.Length); s.Flush();
                    }
                }
                catch { }
            }
            public void Dispose() { stop = true; try { l.Stop(); } catch { } }
        }
        static TzReply Xml(string x) { return new TzReply { Body = Encoding.UTF8.GetBytes(x) }; }
        static string Q(string path, string name) { var i = path.IndexOf('?'); if (i < 0) return null; foreach (var kv in path.Substring(i + 1).Split('&')) { var p = kv.Split(new[] { '=' }, 2); if (p[0] == name) return Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""); } return null; }
        const string TzKey = "test-key-0123456789abcdef";   // made up
        const long MiB = 1024 * 1024;
        // one item: title, size, seeders, peers, link (http or magnet), info hash (or null), minutes old, category
        static string Item(string title, long size, int seeders, int peers, string link, string hash, double minutesOld, int cat)
        {
            var sb = new StringBuilder("<item><title>" + System.Security.SecurityElement.Escape(title) + "</title><guid>" + System.Security.SecurityElement.Escape(title) + "</guid>");
            sb.Append("<size>" + size + "</size><pubDate>" + DateTime.UtcNow.AddMinutes(-minutesOld).ToString("r") + "</pubDate><category>" + cat + "</category>");
            if (link.StartsWith("magnet:")) sb.Append("<link>" + System.Security.SecurityElement.Escape(link) + "</link>");
            else sb.Append("<link>" + System.Security.SecurityElement.Escape(link) + "</link><enclosure url=\"" + System.Security.SecurityElement.Escape(link) + "\" length=\"" + size + "\" type=\"application/x-bittorrent\"/>");
            sb.Append("<torznab:attr name=\"seeders\" value=\"" + seeders + "\"/><torznab:attr name=\"peers\" value=\"" + peers + "\"/><torznab:attr name=\"category\" value=\"" + cat + "\"/>");
            if (hash != null) sb.Append("<torznab:attr name=\"infohash\" value=\"" + hash + "\"/>");
            return sb.Append("</item>").ToString();
        }
        static string Channel(params string[] items) { return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel><title>stand-in</title>" + string.Join("", items) + "</channel></rss>"; }
        const string CapsFull = "<?xml version=\"1.0\"?><caps><server title=\"stand-in\"/><searching><search available=\"yes\" supportedParams=\"q\"/><tv-search available=\"yes\" supportedParams=\"q,season,ep\"/><movie-search available=\"yes\" supportedParams=\"q,imdbid\"/></searching><categories><category id=\"2000\" name=\"Movies\"/><category id=\"5000\" name=\"TV\"><subcat id=\"5040\" name=\"TV/HD\"/></category></categories></caps>";
        const string CapsBasic = "<?xml version=\"1.0\"?><caps><searching><search available=\"yes\" supportedParams=\"q\"/><tv-search available=\"no\"/><movie-search available=\"no\"/></searching><categories><category id=\"5000\" name=\"TV\"/></categories></caps>";

        // ---------------- pure parts: names, ranking order, fake flags, Torznab, the key
        static void V13Unit()
        {
            var r = Ranker.Parse("Night.Sky.S02E06.2160p.WEB-DL.DV.HDR.DDP5.1.Atmos.H.265-FLUX");
            Ok(r.Res == 2160 && r.Source == "WEB-DL" && r.Codec == "x265" && r.Hdr == "DV HDR" && r.Audio == "DD+ Atmos" && r.Group == "FLUX" && r.Season == 2 && r.Episode == 6 && !r.Low, "release name: 2160p WEB-DL x265 DV HDR DD+ Atmos, group, S02E06: " + r.ResText + " " + r.Source + " " + r.Codec + " " + r.Hdr + " " + r.Audio + " " + r.Group);
            r = Ranker.Parse("The.Film.2024.1080p.BluRay.REMUX.AVC.TrueHD.7.1-GRP"); Ok(r.Res == 1080 && r.Source == "Remux" && r.Codec == "x264" && r.Audio == "TrueHD" && r.Kind == "movie" && r.Year == 2024, "a movie Remux: 1080p, TrueHD, year 2024");
            Ok(Ranker.Parse("New.Movie.2026.HDCAM.x264").Low && Ranker.Parse("New Movie 2026 HD-TS").Source == "TS" && Ranker.Parse("New.Movie.2026.TC.XviD").Source == "TC" && Ranker.Parse("New.Movie.2026.DVDSCR").Low && !Ranker.Parse("Tsunami.2026.1080p.WEB").Low, "CAM / TS / TC / SCR are low quality (not Tsunami)");
            Ok(Ranker.Parse("Show.S01E01.720p.HDTV.x264-LOL").Source == "HDTV" && Ranker.Parse("Show S01E01 480p").Res == 480 && Ranker.Parse("Show.S01E01.1080p.WEBRip.AV1.Opus").Codec == "AV1" && Ranker.Parse("Show.S03.1080p.WEB-DL").Pack, "HDTV, 480p, AV1, a season pack");
            // ranking order (same indexer reliability for all)
            var p = new SearchProfile { Resolution = "1080p", Source = "WEB-DL", MaxGB = 8 }; p.Fix(); var now = DateTime.UtcNow;
            Func<string, long, int, int, double, SearchResult> R = (t, size, s, peers, minOld) => new SearchResult { Title = t, Size = size, Seeders = s, Peers = peers, Cats = new List<int> { 5040 }, Published = now.AddMinutes(-minOld), Link = "http://x.invalid/" + t.GetHashCode() };
            var fx = new List<SearchResult> {
                R("Good.Show.S01E02.720p.HDTV.x264-GRP", 900 * MiB, 500, 560, 600),
                R("Good.Show.S01E02.HDCAM.x264", 1200 * MiB, 800, 900, 600),
                R("Good.Show.S01E02.1080p.WEB-DL.x264", 80 * MiB, 40, 45, 600),
                R("Good.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP", 2500 * MiB, 300, 350, 600),
                R("Good.Show.S01E02.1080p.WEB-DL.x264-GRP [password protected]", 2000 * MiB, 60, 70, 600),
                R("Good.Show.S01E02.2160p.WEB-DL.DV.HDR.H.265-GRP", 6000 * MiB, 120, 130, 600),
                R("Good.Show.S01E02.1080p.WEB.Codec.Pack.Required", 1500 * MiB, 70, 80, 600),
                R("Good.Show.S01E02.1080p.WEB-DL.H.264-NEW", 2400 * MiB, 9000, 9100, 10),
                R("Good.Show.S01E02.1080p.WEB-DL.H.264-BIG", 12000 * MiB, 50, 60, 600),
                R("Good.Show.S01E02.1080p.WEB-DL.H.264-DEAD", 2400 * MiB, 0, 0, 600) };
            var ranked = Ranker.Rank(fx, p, id => 0.8, now).Select(x => x.Title).ToList();
            var want = new[] { "Good.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP", "Good.Show.S01E02.2160p.WEB-DL.DV.HDR.H.265-GRP", "Good.Show.S01E02.720p.HDTV.x264-GRP", "Good.Show.S01E02.1080p.WEB-DL.H.264-DEAD", "Good.Show.S01E02.1080p.WEB-DL.H.264-BIG", "Good.Show.S01E02.HDCAM.x264" };
            Ok(ranked.Take(6).SequenceEqual(want), "ranking order: preferred 1080p WEB-DL first, then 2160p, 720p HDTV, a dead one, over the 8 GB max, the CAM: " + string.Join(" > ", ranked));
            var flagged = Ranker.Rank(fx, p, id => 0.8, now).Skip(6).ToList();
            Ok(flagged.Count == 4 && flagged.All(x => x.Flagged), "the 4 likely fakes are the last 4, all flagged");
            Func<string, string> why = t => string.Join("; ", Ranker.Rank(fx, p, id => 0.8, now).First(x => x.Title == t).Flags);
            Ok(why("Good.Show.S01E02.1080p.WEB-DL.x264").StartsWith("Far too small for 1080p"), "flag: far too small for 1080p (80 MB episode): " + why("Good.Show.S01E02.1080p.WEB-DL.x264"));
            Ok(why("Good.Show.S01E02.1080p.WEB-DL.x264-GRP [password protected]").Contains("password-protected"), "flag: a password-protected archive hint");
            Ok(why("Good.Show.S01E02.1080p.WEB.Codec.Pack.Required").Contains("codec pack"), "flag: a codec pack style name");
            Ok(why("Good.Show.S01E02.1080p.WEB-DL.H.264-NEW").Contains("too good to be true"), "flag: brand-new upload with 9,000 seeders");
            var ex = R("Good.Show.S01E02.1080p.WEB-DL.x264-GRP", 2000 * MiB, 50, 60, 600); ex.Files = new List<TFile> { new TFile("Good.Show.S01E02.mkv", 2000 * MiB), new TFile("Codec/Setup.exe", 900000) };
            var lnk = R("Linux.Distro.2026.x64", 2000 * MiB, 50, 60, 600); lnk.Cats = new List<int> { 4000 }; lnk.Files = new List<TFile> { new TFile("distro.iso", 2000 * MiB), new TFile("readme.lnk", 900) };
            var sw = R("Some.App.2026.x64", 90 * MiB, 50, 60, 600); sw.Cats = new List<int> { 4000 }; sw.Files = new List<TFile> { new TFile("setup.exe", 90 * MiB) };
            var exeName = R("Good.Show.S01E02.1080p.mkv.exe", 2000 * MiB, 50, 60, 600);
            Ok(Ranker.Flags(ex, now).Any(f => f.Contains("Setup.exe")) && Ranker.Flags(lnk, now).Count > 0 && Ranker.Flags(sw, now).Count == 0 && Ranker.Flags(exeName, now).Count > 0, "flag: an .exe in a video's file list, a .lnk anywhere, a name ending .mkv.exe - but not setup.exe in software");
            Ok(Ranker.Flags(fx[3], now).Count == 0 && Ranker.Flags(fx[0], now).Count == 0, "...and nothing on the good releases");
            // Torznab: results, caps, errors, request parameters
            var xml = Channel(Item("A.Show.S01E02.1080p.WEB-DL", 2 * 1024 * MiB, 12, 20, "http://x.invalid/dl/1?apikey=SECRET", "0123456789abcdef0123456789abcdef01234567", 30, 5040), Item("B.Movie.2020.720p", 900 * MiB, 3, 4, "magnet:?xt=urn:btih:1123456789abcdef0123456789abcdef01234567&dn=B", null, 60, 2000));
            var res = Torznab.Results(Encoding.UTF8.GetBytes(xml), new TzFeed { Id = "f", Name = "Feed" });
            Ok(res.Count == 2 && res[0].Seeders == 12 && res[0].Peers == 20 && res[0].Size == 2048 * MiB && res[0].InfoHash == "0123456789abcdef0123456789abcdef01234567" && res[0].Link.Contains("/dl/1") && res[0].Cats.Contains(5040) && res[1].Magnet != null && res[1].InfoHash == "1123456789abcdef0123456789abcdef01234567" && res[0].Published.HasValue, "Torznab results: seeders, peers, size, info hash, category, link / magnet, date");
            var f1 = new TzFeed(); Torznab.ReadCaps(Encoding.UTF8.GetBytes(CapsFull), f1); var f2 = new TzFeed(); Torznab.ReadCaps(Encoding.UTF8.GetBytes(CapsBasic), f2);
            Ok(f1.Caps && f1.TvSearch && f1.TvSeasonEp && f1.MovieImdb && f1.Cats.Count == 2 && f2.Caps && !f2.TvSearch && !f2.MovieSearch, "t=caps: tv-search (season, ep), movie-search (imdbid), categories");
            string terr = null; try { Torznab.Results(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><error code=\"100\" description=\"Incorrect user credentials\"/>"), f1); } catch (TorznabException e) { terr = e.Message; }
            Ok(terr != null && terr.Contains("API key was refused"), "a Torznab <error code=100>: the key was refused (" + terr + ")");
            var qtv = SearchQuery.From("good show s01e02", ""); var qm = SearchQuery.From("tt0111161", ""); var qs = SearchQuery.From("ubuntu 24.04", "software");
            var ptv = Torznab.Url("http://h/api", "K", Torznab.Params(qtv, f1)); var pfall = Torznab.Url("http://h/api", "K", Torznab.Params(qtv, f2)); var pm = Torznab.Url("http://h/api", "K", Torznab.Params(qm, f1)); var ps = Torznab.Url("http://h/api", "K", Torznab.Params(qs, f1));
            Ok(ptv.Contains("t=tvsearch") && ptv.Contains("q=good%20show") && ptv.Contains("season=1") && ptv.Contains("ep=2") && ptv.Contains("cat=5000") && ptv.EndsWith("apikey=K"), "tvsearch with season / ep / category: " + Torznab.Redact(ptv));
            Ok(pfall.Contains("t=search") && pfall.Contains("q=good%20show%20S01E02"), "a feed without tvsearch gets \"show S01E02\" as words");
            Ok(pm.Contains("t=movie") && pm.Contains("imdbid=0111161") && ps.Contains("t=search") && ps.Contains("cat=4000"), "movie search by IMDb id; categories (software = 4000)");
            Ok(Torznab.Redact("http://h/api?t=search&apikey=SECRET&q=x") == "http://h/api?t=search&apikey=***&q=x", "API keys are redacted from anything shown or logged");
            // the key: DPAPI for this user
            var blob = Secret.Protect(TzKey);
            Ok(blob != null && !blob.Contains(TzKey) && !Encoding.UTF8.GetString(Convert.FromBase64String(blob)).Contains(TzKey) && Secret.Unprotect(blob) == TzKey, "API key encrypted with DPAPI (CurrentUser) and back");
            var bad = Convert.FromBase64String(blob); bad[bad.Length / 2] ^= 0x55;
            Ok(Secret.Unprotect(Convert.ToBase64String(bad)) == null && Secret.Unprotect("not base64!") == null, "a tampered key blob doesn't decrypt");
            // Add to RSS rule
            var rule = SearchEngine.RuleFrom(new SearchResult { Title = "Night.Sky.S02E06.1080p.WEB-DL.DDP5.1.H.264-NTb", Cats = new List<int> { 5040 } });
            Ok(rule.MustContain == "night sky" && rule.Prefer == "1080p" && rule.Category == "TV" && rule.Episodes && rule.Id == null && RssMatch.Matches(rule, "Night.Sky.S02E07.720p.HDTV"), "Add to RSS rule: show words, prefer its quality, TV, episode tracking: " + rule.MustContain + " / " + rule.Prefer);
            var mrule = SearchEngine.RuleFrom(new SearchResult { Title = "The.Film.2024.2160p.WEB-DL", Cats = new List<int> { 2000 } });
            Ok(mrule.MustContain == "the film 2024" && mrule.Category == "Movies" && mrule.Prefer == "2160p", "...for a movie: title and year");
            int n; string cat; var sp = Controller.SplitSearch(" good show --n 3 --cat tv", out n, out cat);
            Ok(sp[0] == "good show" && n == 3 && cat == "tv", "pipe: search <words> --n 3 --cat tv");
        }

        // ---------------- the stand-in Prowlarr / Jackett and the local swarm
        static async Task Search13()
        {
            var root = Path.Combine(Dir, "search13"); Directory.CreateDirectory(root);
            using (var w = StartSwarm(root, 0))
            using (var tz = new TzServer())
            {
                Net.AuditOn = true; string a; while (Net.Audit.TryDequeue(out a)) { }
                int port = FreePort(); var save = Path.Combine(root, "dl"); var s = TestSettings(port, save);
                var core = new Core(Path.Combine(root, "core"), () => s); string err;
                var ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 300);
                // fixtures: a real torrent the local swarm seeds, a "codec" torrent with Setup.exe, a magnet behind a redirect
                var goodName = "Good.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP";
                var tfGood = await MakeTorrent(w, goodName, new[] { 700000, 300000 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                var goodHash = Torrent.Load(tfGood).InfoHashes.V1.ToHex().ToLowerInvariant();
                var csrc = Path.Combine(root, "codecsrc", "Good.Show.S01E02.1080p.WEBRip.x265-ELiTE"); Directory.CreateDirectory(Path.Combine(csrc, "Codec"));
                File.WriteAllBytes(Path.Combine(csrc, "Good.Show.S01E02.mkv"), new byte[90000]); File.WriteAllBytes(Path.Combine(csrc, "Codec", "Setup.exe"), new byte[5000]);
                var tfCodec = Path.Combine(root, "codec.torrent"); var cr = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }; cr.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" }); await cr.CreateAsync(new TorrentFileSource(csrc), tfCodec);
                var magHash = "7123456789abcdef0123456789abcdef01234567"; var magnet = "magnet:?xt=urn:btih:" + magHash + "&dn=Good.Show.S01E02.1080p.BluRay.x264-JGRP";
                var u = tz.Url("");
                var goodItems = Channel(
                    Item(goodName, 2500 * MiB, 300, 350, u + "/dl/good.torrent?apikey=" + TzKey, goodHash, 600, 5040),
                    Item("Good.Show.S01E02.2160p.WEB-DL.DV.HDR.H.265-GRP", 6000 * MiB, 30, 40, u + "/dl/2160", null, 600, 5040),
                    Item("Good.Show.S01E02.720p.HDTV.x264-GRP", 900 * MiB, 500, 560, u + "/dl/720", null, 600, 5040),
                    Item("Good.Show.S01E02.HDCAM.x264", 1200 * MiB, 800, 900, u + "/dl/cam", null, 600, 5040),
                    Item("Good.Show.S01E02.1080p.WEB-DL.x264", 80 * MiB, 40, 45, u + "/dl/tiny", null, 600, 5040),
                    Item("Good.Show.S01E02.1080p.WEB-DL.x264-GRP [password protected]", 2000 * MiB, 60, 70, u + "/dl/pw", null, 600, 5040),
                    Item("Good.Show.S01E02.1080p.WEB.Codec.Pack.Required", 1500 * MiB, 70, 80, u + "/dl/codecname", null, 600, 5040),
                    Item("Good.Show.S01E02.1080p.WEB-DL.H.264-NEW", 2400 * MiB, 9000, 9100, u + "/dl/new", null, 10, 5040),
                    Item("Good.Show.S01E02.1080p.WEBRip.x265-ELiTE", 700 * MiB, 200, 230, u + "/dl/codec.torrent", null, 600, 5040),
                    Item("Good.Show.S01E02.1080p.WEB-DL.H.264-BIG", 12000 * MiB, 50, 60, u + "/dl/big", null, 600, 5040),
                    Item("Good.Show.S01E02.720p.WEB-ZERO", 800 * MiB, 0, 0, u + "/dl/zero", null, 600, 5040));
                var jackettItems = Channel(
                    Item("Good.Show.S01E02.1080p.BluRay.x264-JGRP", 3000 * MiB, 90, 100, u + "/dl/mag", null, 900, 5040),
                    Item(goodName, 2500 * MiB, 10, 12, u + "/dl/good.torrent?apikey=" + TzKey, goodHash, 600, 5040));   // the same torrent again (fewer seeders)
                tz.Route = path =>
                {
                    var key = Q(path, "apikey"); var t = Q(path, "t");
                    if (path.StartsWith("/dl/"))
                    {
                        if (path.StartsWith("/dl/good.torrent")) return new TzReply { Type = "application/x-bittorrent", Body = File.ReadAllBytes(tfGood) };
                        if (path.StartsWith("/dl/codec.torrent")) return new TzReply { Type = "application/x-bittorrent", Body = File.ReadAllBytes(tfCodec) };
                        if (path.StartsWith("/dl/mag")) return new TzReply { Status = 302, Location = magnet };
                        return new TzReply { Status = 404 };
                    }
                    if (key != TzKey) return path.StartsWith("/prowlarr/api/v1/") ? new TzReply { Status = 401 } : Xml("<?xml version=\"1.0\"?><error code=\"100\" description=\"Invalid API Key\"/>");
                    if (path.StartsWith("/prowlarr/api/v1/indexer")) return new TzReply { Type = "application/json", Body = Encoding.UTF8.GetBytes("[{\"id\":1,\"name\":\"GoodIdx\",\"enable\":true,\"protocol\":\"torrent\"},{\"id\":2,\"name\":\"SlowIdx\",\"enable\":true,\"protocol\":\"torrent\"},{\"id\":3,\"name\":\"BrokenIdx\",\"enable\":true,\"protocol\":\"torrent\"},{\"id\":4,\"name\":\"UsenetIdx\",\"enable\":true,\"protocol\":\"usenet\"}]") };
                    if (path.StartsWith("/prowlarr/1/api")) return t == "caps" ? Xml(CapsFull) : Xml(goodItems);
                    if (path.StartsWith("/prowlarr/2/api")) return t == "caps" ? Xml(CapsBasic) : new TzReply { Body = Encoding.UTF8.GetBytes(Channel()), DelayMs = 5000 };
                    if (path.StartsWith("/prowlarr/3/api")) return t == "caps" ? Xml(CapsBasic) : new TzReply { Status = 500 };
                    if (path.StartsWith("/jackett/api/v2.0/indexers/all/results/torznab/api") && t == "indexers") return Xml("<?xml version=\"1.0\"?><indexers><indexer id=\"jfeed\" configured=\"true\"><title>JackettFeed</title>" + CapsFull.Substring(CapsFull.IndexOf("<caps>")) + "</indexer><indexer id=\"off\" configured=\"false\"><title>Off</title></indexer></indexers>");
                    if (path.StartsWith("/jackett/api/v2.0/indexers/jfeed/results/torznab/api")) return t == "caps" ? Xml(CapsFull) : Xml(jackettItems);
                    return new TzReply { Status = 404 };
                };

                var se = new SearchEngine(core, null, Path.Combine(root, "core")); var logs = new List<string>(); se.Log += x => { lock (logs) logs.Add(x); };
                se.Data.Profile.TimeoutSec = 2;
                Indexer pr = null, jk = null, wrong;
                Ok(se.AddIndexer("prowlarr", "Prowlarr", u + "/prowlarr", TzKey, out pr) == null && se.AddIndexer("jackett", null, u + "/jackett/", TzKey, out jk) == null, "add Prowlarr and Jackett (base URL + API key)");
                Ok(se.AddIndexer("prowlarr", null, u + "/other?apikey=x", "k", out wrong) != null && se.AddIndexer("torznab", null, "ftp://x/api", "k", out wrong) != null && se.AddIndexer("jackett", null, u + "/jackett", TzKey, out wrong) != null, "refused: a key in the address, not http(s), the same one twice");
                var json = File.ReadAllText(Path.Combine(root, "core", "search.json"));
                Ok(!json.Contains(TzKey) && pr.Key != TzKey && se.KeyOf(pr) == TzKey && new SearchEngine(null, null, Path.Combine(root, "core")).KeyOf(new SearchEngine(null, null, Path.Combine(root, "core")).Data.Indexers[0]) == TzKey, "search.json keeps the key DPAPI-encrypted only (never in clear), and it reads back");
                Ok(!File.Exists(Path.Combine(root, "core", "settings.json")) || !File.ReadAllText(Path.Combine(root, "core", "settings.json")).Contains(TzKey), "...and never in settings.json");

                // ---- VPN off: nothing at all is sent
                var o0 = await se.Run(SearchQuery.From("good show s01e02", ""), DateTime.UtcNow);
                var s0 = await se.Sync(pr);
                Ok(o0.Locked && o0.Results.Count == 0 && s0.StartsWith("Waiting for the VPN") && tz.Seen().Count == 0, "VPN locked: a search and a sync send nothing (" + o0.Error + ")");
                byte[] tb; string mg; Ok(se.Fetch(new SearchResult { Title = "x", Link = u + "/dl/good.torrent" }, out tb, out mg) != null && tz.Seen().Count == 0, "...and no .torrent is fetched either");

                await core.SetVpn(Vpn(VpnIp));
                Ok(await se.Sync(pr) == null && pr.Feeds.Count == 3 && pr.Feeds.All(f => f.Caps) && pr.Feeds.First(f => f.Name == "GoodIdx").TvSeasonEp && !pr.Feeds.Any(f => f.Name == "UsenetIdx"), "Prowlarr: its 3 torrent indexers found (Usenet skipped), caps read: " + string.Join(", ", pr.Feeds.Select(f => f.Name)));
                Ok(await se.Sync(jk) == null && jk.Feeds.Count == 1 && jk.Feeds[0].Name == "JackettFeed" && jk.Feeds[0].MovieImdb, "Jackett: its configured indexer found with caps (t=indexers)");

                // ---- one search: everything at once, one slow and one failing
                tz.Seen(); var before = tz.Seen().Count;
                var sw = Stopwatch.StartNew();
                var o = await se.Run(SearchQuery.From("good show s01e02", ""), DateTime.UtcNow); sw.Stop();
                var st = o.Status.ToDictionary(x => x.Name, x => x);
                Ok(st["GoodIdx"].State == "ok" && st["GoodIdx"].Count == 11 && st["JackettFeed"].State == "ok" && st["SlowIdx"].State == "timeout" && st["BrokenIdx"].State == "error", "per-indexer status: " + string.Join(", ", o.Status.Select(x => x.Name + " " + x.State + (x.Error != null ? " (" + x.Error + ")" : ""))));
                Ok(sw.ElapsedMilliseconds < 4000, "asked in parallel: the slow one (5 s) timed out at 2 s without holding up the rest - " + sw.ElapsedMilliseconds + " ms in all");
                var titles = o.Results.Select(x => x.Title).ToList();
                Ok(titles[0] == goodName && o.Results.Count(x => x.InfoHash == goodHash) == 1 && o.Results.First(x => x.InfoHash == goodHash).Seeders == 300, "ranked: the 1080p WEB-DL first; the same torrent from two indexers once (the one with more seeders)");
                Ok(!titles.Contains("Good.Show.S01E02.720p.WEB-ZERO"), "no seeders: hidden (min seeders 1)");
                var lastFour = o.Results.Skip(o.Results.Count - 4).ToList();
                Ok(lastFour.All(x => x.Flagged) && o.Results.Count(x => x.Flagged) == 4 && lastFour.Any(x => x.Title.Contains("password")) && lastFour.Any(x => x.Title.EndsWith("-NEW")) && lastFour.Any(x => x.Title.Contains("Codec.Pack")) && lastFour.Any(x => x.Title == "Good.Show.S01E02.1080p.WEB-DL.x264"), "fakes at the bottom with reasons: " + string.Join(" | ", lastFour.Select(x => x.Flags[0])));
                var normal = o.Results.Where(x => !x.Flagged).ToList();
                Ok(normal[normal.Count - 1].Title == "Good.Show.S01E02.HDCAM.x264" && normal[normal.Count - 2].Title.EndsWith("-BIG"), "the CAM is last of the rest, over-the-max-size just above it");
                var seen = tz.Seen().Skip(before).ToList();
                var tvReq = seen.FirstOrDefault(x => x.Contains("/prowlarr/1/api"));
                Ok(tvReq != null && tvReq.Contains("t=tvsearch") && tvReq.Contains("season=1") && tvReq.Contains("ep=2") && seen.Any(x => x.Contains("/prowlarr/2/api") && x.Contains("t=search") && x.Contains("S01E02")), "tvsearch with season=1&ep=2 where supported, words elsewhere");
                Ok(logs.Count > 0 && logs.All(x => !x.Contains(TzKey)) && o.Status.All(x => x.Error == null || !x.Error.Contains(TzKey)), "the key never shows up in the log or errors");

                // ---- movie search by IMDb id
                before = tz.Seen().Count; await se.Run(SearchQuery.From("tt0111161", ""), DateTime.UtcNow);
                Ok(tz.Seen().Skip(before).Any(x => x.Contains("/prowlarr/1/api") && x.Contains("t=movie") && x.Contains("imdbid=0111161")), "movie search: t=movie&imdbid=0111161");
                o = await se.Run(SearchQuery.From("good show s01e02", ""), DateTime.UtcNow);

                // ---- check files: the x265 one carries Setup.exe -> flagged, to the bottom
                var elite = o.Results.First(x => x.Title.EndsWith("ELiTE"));
                Ok(!elite.Flagged && se.Inspect(elite, DateTime.UtcNow) == null && elite.Files.Count == 2 && elite.Flagged && elite.Flags[0].Contains("Setup.exe"), "check files: its .torrent (over the VPN) has Setup.exe -> flagged: " + (elite.Flags.Count > 0 ? elite.Flags[0] : "-"));
                se.Data.Profile.Inspect = true; var oi = await se.Run(SearchQuery.From("good show s01e02", ""), DateTime.UtcNow); se.Data.Profile.Inspect = false;
                Ok(oi.Results.Last(x => x.Flagged).Title.EndsWith("ELiTE") || oi.Results.Skip(oi.Results.Count - 5).Any(x => x.Title.EndsWith("ELiTE") && x.Flagged), "automatic file check of the top results moves it among the flagged");

                // ---- download from a result into the local swarm (the normal add: category, VPN-bound engine)
                var good = o.Results.First(x => x.InfoHash == goodHash);
                var e = se.Download(good, null, null, true, out err);
                Ok(e != null && e.Category == "TV" && e.Id == goodHash, "Download: added from the result (.torrent fetched over the VPN), category TV (" + err + ")");
                Ok(Wait(() => core.Get(goodHash).CompletedAt != null, 30000), "...and it downloads from the local swarm");
                Ok(File.Exists(Path.Combine(save, goodName, "file0.bin")) && Sha(Path.Combine(save, goodName, "file0.bin")) == Sha(Path.Combine(w.Root, "seed", goodName, "file0.bin")), "...the files match the seeder's");
                var o2 = await se.Run(SearchQuery.From("good show s01e02", ""), DateTime.UtcNow);
                Ok(o2.Results.First(x => x.InfoHash == goodHash).InList, "a new search marks it \"in your list\"");
                var jr = o2.Results.First(x => x.Title.EndsWith("JGRP"));
                var em = se.Download(jr, null, null, false, out err);
                Ok(em != null && em.Id == magHash && em.Magnet != null && em.UserPaused, "a download link that redirects to a magnet link: added as that magnet (" + err + ")");

                // ---- everything came from the VPN stand-in address; then the VPN drops and nothing more is sent
                var all = tz.Seen();
                Ok(all.Count > 15 && all.All(x => x.StartsWith("127.0.0.3 ")), "every search request came from the VPN stand-in address only (" + all.Count + " requests from " + string.Join(",", all.Select(x => x.Split(' ')[0]).Distinct()) + ")");
                await core.SetVpn(new VpnState { Message = "off" });
                before = tz.Seen().Count;
                var o3 = await se.Run(SearchQuery.From("good show", ""), DateTime.UtcNow);
                Ok(o3.Locked && tz.Seen().Count == before && se.Inspect(elite, DateTime.UtcNow) != null && tz.Seen().Count == before, "VPN dropped: searches and file checks send nothing");
                var audit = Net.Audit.ToArray();
                Ok(audit.Any(x => x.StartsWith("http ")) && audit.All(x => x.Contains(" 127.0.0.3:")), "1.3 leak check: every socket (search, .torrent, trackers, peers) was bound to the VPN IP (" + audit.Length + ")");
                ticker.Dispose(); core.Dispose(); Net.AuditOn = false;
            }
        }

        // the app's pipe: search <words> (a test copy with a Torznab stand-in), used from AppCopy
        static TzServer PipeTz;
        static void WritePipeSearch(string dir)
        {
            PipeTz = new TzServer(); var u = PipeTz.Url("");
            var name = "Pipe.Show.S01E01.1080p.WEB-DL.x264-GRP";
            var src = Path.Combine(dir, "..", "pipesrc", name); Directory.CreateDirectory(src); File.WriteAllBytes(Path.Combine(src, "Pipe.Show.S01E01.mkv"), new byte[50000]);
            var tf = Path.Combine(dir, "..", "pipe.torrent"); var cr = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }; cr.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" }); cr.Create(new TorrentFileSource(src), tf);
            var bytes = File.ReadAllBytes(tf);
            var items = Channel(Item(name, 1800 * MiB, 120, 150, u + "/dl/p.torrent", null, 600, 5040), Item("Pipe.Show.S01E01.CAM", 1400 * MiB, 900, 950, u + "/dl/cam", null, 600, 5040), Item("Pipe.Show.S01E01.720p.HDTV.x264", 700 * MiB, 60, 70, u + "/dl/720", null, 600, 5040));
            PipeTz.Route = path => path.StartsWith("/dl/p.torrent") ? new TzReply { Type = "application/x-bittorrent", Body = bytes } : Q(path, "apikey") != TzKey ? Xml("<?xml version=\"1.0\"?><error code=\"100\" description=\"Invalid API Key\"/>") : Q(path, "t") == "caps" ? Xml(CapsFull) : Xml(items);
            var se = new SearchEngine(null, null, dir); Indexer x; se.AddIndexer("torznab", "Pipe feed", u + "/api", TzKey, out x);
        }
        static void PipeSearchChecks(string exe, string dir, bool vpnUp)
        {
            if (!vpnUp)
            {
                var r0 = Cmd(exe, dir, "search pipe show");
                Ok(r0.StartsWith("error waiting for the VPN") && PipeTz.Seen().Count == 0, "pipe search while VPN-locked: refused, nothing sent: " + r0);
                return;
            }
            var r = Cmd(exe, dir, "search pipe show s01e01 --n 2");
            var d = r.StartsWith("ok ") ? new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.Substring(3)) : null;
            var list = d == null ? null : ((System.Collections.ArrayList)d["results"]).Cast<Dictionary<string, object>>().ToList();
            Ok(list != null && list.Count == 2 && (string)list[0]["title"] == "Pipe.Show.S01E01.1080p.WEB-DL.x264-GRP" && (string)list[0]["resolution"] == "1080p" && (int)d["total"] == 3 && !r.Contains(TzKey), "pipe: search <query> --n 2 -> the top 2 as JSON, best first, no API key: " + (r.Length > 160 ? r.Substring(0, 160) + "..." : r));
            Ok(PipeTz.Seen().Count > 0 && PipeTz.Seen().All(x => x.StartsWith("127.0.0.5 ")), "...asked from the app's VPN address only (" + string.Join(",", PipeTz.Seen().Select(x => x.Split(' ')[0]).Distinct()) + ")");
            r = Cmd(exe, dir, "search-add " + (string)list[0]["id"] + " --category tv");
            Ok(r.StartsWith("ok {\"id\":") && r.Contains("\"category\":\"TV\"") && Cmd(exe, dir, "test-list").Contains("Pipe.Show.S01E01.1080p.WEB-DL.x264-GRP"), "pipe: search-add <id> --category tv -> added through the normal add: " + r);
            Ok(Cmd(exe, dir, "search-add r999").StartsWith("error no search result"), "search-add with an unknown id: error");
            r = Cmd(exe, dir, "status"); Ok(r.Contains("\"indexers\":1") && !r.Contains("Pipe feed") && !r.Contains("127.0.0.1:" + PipeTz.Port), "status: the indexer count only (no names or addresses)");
        }
    }
}
