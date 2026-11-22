// Hallow Torrents 1.2 tests - the qBittorrent-compatible Web API, still on THIS PC only. A contract suite replays the HTTP request
// sequences Radarr, Sonarr and Prowlarr make (written from their qBittorrent client code, QBittorrentProxyV2 / QBittorrentProxySelector:
// webapiVersion before sign-in, login, version, preferences, categories, add by file and by magnet with a category, poll
// torrents/info, properties, files, setShareLimits, setCategory, topPrio, pause / resume and their qBittorrent 5 names, delete)
// against the real server on 127.0.0.1, with the same local swarm as 1.0 / 1.1 (tracker + seeder on 127.0.0.1, Hallow Torrents bound
// to the stand-in VPN address 127.0.0.3), so an added torrent really downloads and is then reported "uploading" / "pausedUP" with the
// right content_path. Plus sign-in tests (403 without a cookie, the lock-out after wrong passwords, expiry), CSRF, the allow-list and
// where it may listen. No real *arr install - this suite stands in for them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MonoTorrent;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static void V12Unit()
        {
            // the state mapping the apps read
            Func<string, bool, TorrentView> V = (st, done) => new TorrentView { State = st, Complete = done };
            var plain = new Entry { Flags = new List<string>(), Scan = "" };
            Ok(QbitApi.QState(V("Waiting for VPN", false), plain) == "queuedDL", "VPN-locked download -> queuedDL (the apps show it queued, not failed)");
            Ok(QbitApi.QState(V("Paused for game", false), plain) == "queuedDL" && QbitApi.QState(V("Queued", false), plain) == "queuedDL", "paused for a game / queued -> queuedDL");
            Ok(QbitApi.QState(V("Paused", false), plain) == "pausedDL" && QbitApi.QState(V("Getting info", false), plain) == "metaDL" && QbitApi.QState(V("Checking", false), plain) == "checkingDL", "paused -> pausedDL, metadata -> metaDL, checking -> checkingDL");
            var dl = V("Downloading", false); Ok(QbitApi.QState(dl, plain) == "stalledDL", "downloading with no data flowing -> stalledDL"); dl.DownRate = 5000; Ok(QbitApi.QState(dl, plain) == "downloading", "downloading -> downloading");
            var sd = V("Seeding", true); Ok(QbitApi.QState(sd, plain) == "stalledUP", "seeding, idle -> stalledUP"); sd.UpRate = 10; Ok(QbitApi.QState(sd, plain) == "uploading", "seeding -> uploading");
            Ok(QbitApi.QState(V("Waiting for VPN", true), plain) == "queuedUP", "finished while the VPN is off -> queuedUP (still importable)");
            Ok(QbitApi.QState(V("Done (limit reached)", true), plain) == "pausedUP" && QbitApi.QState(V("Paused", true), plain) == "pausedUP", "ratio / time limit reached or paused -> pausedUP");
            Ok(QbitApi.QState(V("Waiting for VPN", true), new Entry { LimitHit = true, Flags = new List<string>() }) == "pausedUP", "limit reached while the VPN is off -> pausedUP");
            Ok(QbitApi.QState(V("Seeding", true), new Entry { Scan = "scanning", Flags = new List<string>() }) == "checkingUP", "finished but Defender still scanning -> checkingUP (not imported yet)");
            Ok(QbitApi.QState(V("Seeding", true), new Entry { Scan = "threats", Flags = new List<string>() }) == "error" && QbitApi.QState(V("Seeding", true), new Entry { Scan = "clean", Flags = new List<string> { "a.exe|x" } }) == "error", "Defender found something / flagged -> error (never imported)");
            Ok(QbitApi.QState(V("Error", false), plain) == "error", "error -> error");
            // where it may listen and who may connect
            Ok(ApiGuard.IsLocalAddress(IPAddress.Parse("192.168.1.5")) && ApiGuard.IsLocalAddress(IPAddress.Parse("10.2.3.4")) && ApiGuard.IsLocalAddress(IPAddress.Parse("172.20.0.1")) && ApiGuard.IsLocalAddress(IPAddress.Parse("100.101.102.103")) && ApiGuard.IsLocalAddress(IPAddress.Loopback), "home network, Tailscale and this PC count as local");
            Ok(!ApiGuard.IsLocalAddress(IPAddress.Parse("8.8.8.8")) && !ApiGuard.IsLocalAddress(IPAddress.Parse("172.32.0.1")) && !ApiGuard.IsLocalAddress(IPAddress.Parse("100.128.0.1")) && !ApiGuard.IsLocalAddress(IPAddress.Parse("2001:4860::1")), "internet addresses never do");
            Ok(ApiGuard.BindProblem(IPAddress.Any, null) != null && ApiGuard.BindProblem(IPAddress.Parse("8.8.8.8"), null) != null && ApiGuard.BindProblem(IPAddress.Parse("10.5.0.2"), IPAddress.Parse("10.5.0.2")) != null && ApiGuard.BindProblem(IPAddress.Loopback, null) == null && ApiGuard.BindProblem(IPAddress.Parse("192.168.1.50"), IPAddress.Parse("10.5.0.2")) == null, "never 0.0.0.0, a public address or the VPN's address; 127.0.0.1 and a LAN address are fine");
            string bad; var allow = ApiGuard.ParseAllow("192.168.1.0/24, 10.0.0.5", out bad);
            Ok(bad == null && allow.Count == 2 && allow.Any(r => r.Has(IPAddress.Parse("192.168.1.77"))) && allow.Any(r => r.Has(IPAddress.Parse("10.0.0.5"))) && !allow.Any(r => r.Has(IPAddress.Parse("10.0.0.6"))) && !allow.Any(r => r.Has(IPAddress.Parse("192.168.2.1"))), "allow-list: addresses and ranges");
            ApiGuard.ParseAllow("192.168.1.0/24, nonsense", out bad); Ok(bad == "nonsense", "allow-list: a bad entry is reported");
            // the password is only a hash
            var h = ApiAuth.Hash("correct horse"); Ok(h.StartsWith("pbkdf2$") && !h.Contains("correct") && ApiAuth.Check("correct horse", h) && !ApiAuth.Check("correct hors", h) && h != ApiAuth.Hash("correct horse"), "password: salted PBKDF2 hash, checked");
            // parsing
            var args = new Dictionary<string, string>(); var files = new List<ApiFile>();
            var body = Encoding.UTF8.GetBytes("--XyZ\r\nContent-Disposition: form-data; name=\"category\"\r\n\r\nradarr\r\n--XyZ\r\nContent-Disposition: form-data; name=\"torrents\"; filename=\"a.torrent\"\r\nContent-Type: application/x-bittorrent\r\n\r\nd4:infod\r\n--XyZ--\r\n");
            ApiHttp.ParseMultipart(body, ApiHttp.Boundary("multipart/form-data; boundary=\"XyZ\""), args, files);
            Ok(args.ContainsKey("category") && args["category"] == "radarr" && files.Count == 1 && files[0].Field == "torrents" && Encoding.ASCII.GetString(files[0].Data) == "d4:infod", "multipart: fields and the .torrent file");
            var q = new Dictionary<string, string>(); ApiHttp.ParseUrlEncoded("hashes=a%7Cb&category=tv-sonarr&x=1+2", q);
            Ok(q["hashes"] == "a|b" && q["category"] == "tv-sonarr" && q["x"] == "1 2", "form / query decoding");
            Ok(QbitApi.ValidCategory("radarr") && QbitApi.ValidCategory("tv-sonarr") && !QbitApi.ValidCategory("") && !QbitApi.ValidCategory("a\\b") && !QbitApi.ValidCategory("/x"), "category names");
            // share limits in the plan
            Ok(Plan.LimitReached(new Plan.Item { Ratio = 0.0, RatioCap = 0 }, 0) && !Plan.LimitReached(new Plan.Item { Ratio = 5, RatioGoal = 2, RatioCap = -1 }, 0) && Plan.LimitReached(new Plan.Item { Ratio = 0.5, RatioGoal = 2, RatioCap = 0.4 }, 0), "per-torrent ratio limit (0 = stop now, -1 = none) beats the category / global one");
            Ok(Plan.LimitReached(new Plan.Item { SeedMinutes = 3, SeedCap = 2 }, 0) && !Plan.LimitReached(new Plan.Item { SeedMinutes = 300, SeedCap = -1 }, 60) && Plan.LimitReached(new Plan.Item { SeedMinutes = 61 }, 60) && !Plan.LimitReached(new Plan.Item { SeedMinutes = 61 }, 0), "per-torrent seeding time limit; the global one still works");
        }

        // ---------------- an HTTP client like the apps': a cookie jar, form posts, multipart uploads
        class Arr : IDisposable
        {
            public readonly HttpClient H; public readonly CookieContainer Jar = new CookieContainer(); public readonly string Base;
            public Arr(string b) { Base = b; H = new HttpClient(new HttpClientHandler { CookieContainer = Jar, UseCookies = true, UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }; }
            public KeyValuePair<int, string> Get(string path) { var r = H.GetAsync(Base + path).Result; return new KeyValuePair<int, string>((int)r.StatusCode, r.Content.ReadAsStringAsync().Result); }
            public KeyValuePair<int, string> Post(string path, params string[] kv)
            {
                var d = new List<KeyValuePair<string, string>>(); for (int i = 0; i + 1 < kv.Length; i += 2) d.Add(new KeyValuePair<string, string>(kv[i], kv[i + 1]));
                var r = H.PostAsync(Base + path, new FormUrlEncodedContent(d)).Result; return new KeyValuePair<int, string>((int)r.StatusCode, r.Content.ReadAsStringAsync().Result);
            }
            public KeyValuePair<int, string> Upload(string path, string field, string fileName, byte[] data, params string[] kv)
            {
                var m = new MultipartFormDataContent();
                for (int i = 0; i + 1 < kv.Length; i += 2) m.Add(new StringContent(kv[i + 1]), kv[i]);
                var f = new ByteArrayContent(data); f.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent"); m.Add(f, field, fileName);
                var r = H.PostAsync(Base + path, m).Result; return new KeyValuePair<int, string>((int)r.StatusCode, r.Content.ReadAsStringAsync().Result);
            }
            public string Sid { get { var c = Jar.GetCookies(new Uri(Base))["SID"]; return c == null ? null : c.Value; } }
            public void Dispose() { H.Dispose(); }
        }
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();
        static List<Dictionary<string, object>> Torrents(Arr a, string query) { var r = a.Get("/api/v2/torrents/info" + query); return r.Key == 200 ? ((object[])Js.DeserializeObject(r.Value)).Cast<Dictionary<string, object>>().ToList() : null; }
        static Dictionary<string, object> One(Arr a, string hash) { var l = Torrents(a, "?hashes=" + hash); return l != null && l.Count == 1 ? l[0] : null; }
        static string StateOf(Arr a, string hash) { var t = One(a, hash); return t == null ? null : (string)t["state"]; }
        static double D(object o) { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); }
        static string RawGet(IPAddress from, int port, string path, string extraHeaders = "")
        {
            try
            {
                using (var tc = new TcpClient(new IPEndPoint(from, 0)))
                {
                    tc.Connect(IPAddress.Loopback, port); var s = tc.GetStream(); s.ReadTimeout = 5000;
                    var req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n" + extraHeaders + "Connection: close\r\n\r\n"); s.Write(req, 0, req.Length);
                    var sr = new StreamReader(s); return sr.ReadLine() ?? "";
                }
            }
            catch (Exception e) { return "closed: " + e.GetBaseException().Message; }
        }
        // a single-file torrent the seeder has
        static async Task<string> MakeSingle(Swarm1 w, string name, int size)
        {
            var dir = Path.Combine(w.Root, "seed"); Directory.CreateDirectory(dir); var f = Path.Combine(dir, name); var data = new byte[size]; new Random(size).NextBytes(data); File.WriteAllBytes(f, data);
            var c = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }; c.Announces.Add(new List<string> { "http://127.0.0.1:" + w.HttpPort + "/announce" });
            var tf = Path.Combine(w.Root, name + ".torrent"); await c.CreateAsync(new TorrentFileSource(f), tf);
            var m = await w.Seeder.AddAsync(await Torrent.LoadAsync(tf), dir, new MonoTorrent.Client.TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false }.ToSettings()); await m.HashCheckAsync(false); await m.StartAsync();
            return tf;
        }

        static async Task Arr12()
        {
            var root = Path.Combine(Dir, "arr"); Directory.CreateDirectory(root);
            using (var w = StartSwarm(root, 0))
            {
                Net.AuditOn = true; string a0; while (Net.Audit.TryDequeue(out a0)) { }
                var tracker = "http://127.0.0.1:" + w.HttpPort + "/announce";
                var tfMovie = await MakeTorrent(w, "Made.Up.Movie.2024.1080p", new[] { 2 * 1024 * 1024 + 11, 250000 }, tracker);
                var tfEp = await MakeSingle(w, "Made.Up.Show.S01E01.720p.mkv", 900000);
                var hMovie = Torrent.Load(tfMovie).InfoHashes.V1.ToHex().ToLowerInvariant(); var tEp = Torrent.Load(tfEp); var hEp = tEp.InfoHashes.V1.ToHex().ToLowerInvariant();
                int port = FreePort(); var save = Path.Combine(root, "dl");
                var s = TestSettings(port, save); s.ApiOn = true; s.ApiUser = "arr-test"; s.ApiPassHash = ApiAuth.Hash("fake-test-pass-1");
                var core = new Core(Path.Combine(root, "core"), () => s);
                var api = new QbitApi(core, () => s, act => { act(); s.Fix(); });
                var srv = new ApiServer(api); int apiPort = FreePort();
                var serr = srv.Start("127.0.0.1", apiPort);
                Ok(serr == null && srv.Running, "the Web API starts on 127.0.0.1 (" + serr + ")");
                var ls = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(l => l.Port == apiPort).ToList();
                Ok(ls.Count == 1 && ls[0].Address.Equals(IPAddress.Loopback), "...listening on 127.0.0.1 only: " + string.Join(",", ls.Select(l => l.ToString())));
                Timer ticker = null;
                try
                {
                    using (var radarr = new Arr("http://127.0.0.1:" + apiPort))
                    {
                        // Radarr's QBittorrentProxySelector: webapiVersion before signing in - 403 means "v2 is there"
                        var r = radarr.Get("/api/v2/app/webapiVersion"); Ok(r.Key == 403, "before sign-in: app/webapiVersion -> 403 (the apps read that as \"API v2 supported\")");
                        Ok(radarr.Get("/api/v2/torrents/info").Key == 403 && radarr.Get("/api/v2/app/preferences").Key == 403, "before sign-in: torrents/info and preferences -> 403");
                        r = radarr.Post("/api/v2/auth/login", "username", "arr-test", "password", "wrong-pass"); Ok(r.Key == 200 && r.Value == "Fails." && radarr.Sid == null, "auth/login with a wrong password -> \"Fails.\", no cookie");
                        Ok(radarr.Get("/api/v2/auth/login").Key == 405, "auth/login by GET -> 405");
                        r = radarr.Post("/api/v2/auth/login", "username", "arr-test", "password", "fake-test-pass-1"); Ok(r.Key == 200 && r.Value == "Ok." && !string.IsNullOrEmpty(radarr.Sid), "auth/login -> \"Ok.\" and an SID cookie");
                        r = radarr.Get("/api/v2/app/version"); Ok(r.Key == 200 && r.Value.StartsWith("v4.") && new Version(r.Value.TrimStart('v')) >= new Version(4, 1), "app/version -> " + r.Value);
                        r = radarr.Get("/api/v2/app/webapiVersion"); Version wv; Ok(r.Key == 200 && Version.TryParse(r.Value, out wv) && wv >= new Version(2, 6, 1), "app/webapiVersion -> " + r.Value + " (content_path supported)");
                        r = radarr.Get("/api/v2/app/preferences"); var pref = Js.Deserialize<Dictionary<string, object>>(r.Value);
                        Ok(r.Key == 200 && (string)pref["save_path"] == save && (bool)pref["queueing_enabled"] && !(bool)pref["max_ratio_enabled"] && Convert.ToInt32(pref["max_ratio_act"]) == 0 && pref.ContainsKey("max_seeding_time_enabled") && pref.ContainsKey("max_inactive_seeding_time_enabled") && pref.ContainsKey("dht") && !pref.ContainsKey("web_ui_password"), "app/preferences: save_path, queueing, ratio / seeding-time settings, max_ratio_act (and no password)");
                        r = radarr.Get("/api/v2/app/buildInfo"); Ok(r.Key == 200 && r.Value.Contains("\"libtorrent\""), "app/buildInfo");
                        // categories (Radarr's Test(): GetLabels, then AddLabel if it's missing)
                        r = radarr.Get("/api/v2/torrents/categories"); var cats = Js.Deserialize<Dictionary<string, object>>(r.Value); Ok(r.Key == 200 && !cats.ContainsKey("radarr"), "torrents/categories (no radarr yet)");
                        Ok(radarr.Post("/api/v2/torrents/createCategory", "category", "radarr", "savePath", "").Key == 200, "torrents/createCategory radarr");
                        cats = Js.Deserialize<Dictionary<string, object>>(radarr.Get("/api/v2/torrents/categories").Value);
                        Ok(cats.ContainsKey("radarr") && (string)((Dictionary<string, object>)cats["radarr"])["name"] == "radarr" && ((Dictionary<string, object>)cats["radarr"]).ContainsKey("savePath") && s.Cat("radarr") != null && !s.Cat("radarr").Handoff, "...it's a Hallow category (no library hand-off) with name / savePath");
                        // add by .torrent file with a category while the VPN is OFF: accepted, waits locked, shown as queuedDL
                        r = radarr.Upload("/api/v2/torrents/add", "torrents", "movie.torrent", File.ReadAllBytes(tfMovie), "category", "radarr", "paused", "false", "sequentialDownload", "true", "firstLastPiecePrio", "true", "contentLayout", "Original");
                        Ok(r.Key == 200 && r.Value == "Ok.", "torrents/add (multipart .torrent, category radarr) while the VPN is off -> \"Ok.\"");
                        var t = One(radarr, hMovie);
                        Ok(t != null && (string)t["state"] == "queuedDL" && (string)t["category"] == "radarr" && (string)t["save_path"] == save && (bool)t["seq_dl"] && !core.EngineUp, "torrents/info: VPN-locked -> queuedDL (not failed), category radarr, save_path, sequential");
                        Ok(core.Get(hMovie).Api && !core.Get(hMovie).UserPaused, "...it's marked as added by an app, and not paused");
                        // Sonarr: magnet via urls, a category that doesn't exist yet (created like qBittorrent does), added stopped (qBittorrent 5's name)
                        var magnet = "magnet:?xt=urn:btih:" + hEp + "&dn=" + Uri.EscapeDataString(tEp.Name) + "&tr=" + Uri.EscapeDataString(tracker);
                        using (var sonarr = new Arr("http://127.0.0.1:" + apiPort))
                        {
                            Ok(sonarr.Post("/api/v2/auth/login", "username", "arr-test", "password", "fake-test-pass-1").Value == "Ok.", "a second app signs in at the same time");
                            r = sonarr.Post("/api/v2/torrents/add", "urls", magnet, "category", "tv-sonarr", "stopped", "true", "tags", "sonarr");
                            Ok(r.Value == "Ok." && s.Cat("tv-sonarr") != null, "torrents/add (magnet in urls, new category tv-sonarr, stopped) -> \"Ok.\", category created");
                            Ok(StateOf(sonarr, hEp) == "pausedDL" && (string)One(sonarr, hEp)["tags"] == "sonarr", "...shown as pausedDL with its tag");
                            Ok(Torrents(sonarr, "?category=tv-sonarr").Count == 1 && Torrents(sonarr, "?category=radarr").Count == 1, "torrents/info?category= filters per app");
                        }
                        r = radarr.Post("/api/v2/torrents/add", "urls", "not a link", "category", "radarr"); Ok(r.Value == "Fails.", "torrents/add with nothing usable -> \"Fails.\"");
                        r = radarr.Upload("/api/v2/torrents/add", "torrents", "movie.torrent", File.ReadAllBytes(tfMovie), "category", "radarr"); Ok(r.Value == "Fails.", "the same torrent again -> \"Fails.\"");
                        r = radarr.Upload("/api/v2/torrents/add", "torrents", "junk.torrent", Encoding.ASCII.GetBytes("<html>not a torrent</html>"), "category", "radarr"); Ok(r.Value == "Fails.", "an invalid .torrent -> \"Fails.\"");
                        Ok(radarr.Get("/api/v2/torrents/pause?hashes=" + hEp).Key == 405, "an action by GET -> 405");

                        // the VPN comes up: it really downloads over the stand-in VPN address
                        await core.SetVpn(Vpn(VpnIp));
                        ticker = new Timer(_ => { try { core.Tick().Wait(); } catch { } }, null, 0, 300);
                        bool done = Wait(() => { var st = StateOf(radarr, hMovie); return st == "uploading" || st == "stalledUP"; }, 40000);
                        t = One(radarr, hMovie);
                        Ok(done, "the movie downloaded and is reported " + (t == null ? "?" : t["state"]));
                        var cp = t == null ? "" : (string)t["content_path"];
                        Ok(t != null && D(t["progress"]) == 1.0 && Convert.ToInt64(t["amount_left"]) == 0 && cp == Path.Combine(save, "Made.Up.Movie.2024.1080p") && Directory.Exists(cp) && File.Exists(Path.Combine(cp, "file0.bin")), "content_path is the torrent's folder, where the files are: " + cp);
                        Ok(t != null && Sha(Path.Combine(cp, "file0.bin")) == Sha(Path.Combine(root, "seed", "Made.Up.Movie.2024.1080p", "file0.bin")), "...and the data matches the seeder's");
                        Ok(t != null && Convert.ToInt64(t["completion_on"]) > 0 && Convert.ToInt64(t["added_on"]) > 0 && Convert.ToInt64(t["eta"]) == 8640000 && t.ContainsKey("ratio") && t.ContainsKey("seeding_time") && Convert.ToInt64(t["size"]) == 2 * 1024 * 1024 + 11 + 250000 + "test Made.Up.Movie.2024.1080p".Length && D(t["ratio_limit"]) == -2, "the fields the apps parse: size, completion_on, added_on, eta, ratio, ratio_limit (-2 = global), seeding_time");
                        r = radarr.Get("/api/v2/torrents/files?hash=" + hMovie); var fl = ((object[])Js.DeserializeObject(r.Value)).Cast<Dictionary<string, object>>().ToList();
                        Ok(r.Key == 200 && fl.Count == 3 && fl.All(f => ((string)f["name"]).StartsWith("Made.Up.Movie.2024.1080p/") && D(f["progress"]) == 1.0 && Convert.ToInt32(f["priority"]) == 1), "torrents/files: names with the torrent's folder, progress 1, priority 1");
                        r = radarr.Get("/api/v2/torrents/properties?hash=" + hMovie); var pr = Js.Deserialize<Dictionary<string, object>>(r.Value);
                        Ok(r.Key == 200 && (string)pr["save_path"] == save && pr.ContainsKey("seeding_time") && (string)pr["hash"] == hMovie, "torrents/properties: save_path, seeding_time");
                        Ok(radarr.Get("/api/v2/torrents/properties?hash=0000000000000000000000000000000000000000").Key == 404, "torrents/properties of an unknown hash -> 404");
                        // the episode: started with qBittorrent 5's "start" name
                        Ok(radarr.Post("/api/v2/torrents/start", "hashes", hEp).Key == 200, "torrents/start (qBittorrent 5 name)");
                        done = Wait(() => { var st = StateOf(radarr, hEp); return st == "uploading" || st == "stalledUP"; }, 30000);
                        var te = One(radarr, hEp); var cpe = te == null ? "" : (string)te["content_path"];
                        Ok(done && cpe == Path.Combine(save, "Made.Up.Show.S01E01.720p.mkv") && File.Exists(cpe), "single-file episode downloaded; content_path is the file itself: " + cpe);
                        Ok(radarr.Post("/api/v2/torrents/stop", "hashes", hEp).Key == 200 && Wait(() => StateOf(radarr, hEp) == "pausedUP", 5000), "torrents/stop -> pausedUP");
                        Ok(radarr.Post("/api/v2/torrents/resume", "hashes", hEp).Key == 200 && Wait(() => { var st = StateOf(radarr, hEp); return st == "uploading" || st == "stalledUP"; }, 5000), "torrents/resume -> seeding again");
                        Ok(radarr.Post("/api/v2/torrents/pause", "hashes", hEp).Key == 200 && Wait(() => StateOf(radarr, hEp) == "pausedUP", 5000), "torrents/pause -> pausedUP");
                        radarr.Post("/api/v2/torrents/resume", "hashes", hEp);
                        // seed until ratio: Radarr's seed criteria -> setShareLimits; at the limit it's pausedUP and the app may remove it
                        Ok(radarr.Post("/api/v2/torrents/setShareLimits", "hashes", hMovie, "ratioLimit", "0", "seedingTimeLimit", "-2", "inactiveSeedingTimeLimit", "-2").Key == 200, "torrents/setShareLimits ratio 0");
                        Ok(Wait(() => StateOf(radarr, hMovie) == "pausedUP", 6000) && D(One(radarr, hMovie)["ratio_limit"]) == 0 && core.ManagerForTests(hMovie).State == MonoTorrent.Client.TorrentState.Stopped, "...ratio reached: seeding stopped, reported pausedUP with ratio_limit 0 (Radarr's \"seed goal reached\")");
                        Ok(radarr.Post("/api/v2/torrents/setShareLimits", "hashes", hMovie, "ratioLimit", "-1", "seedingTimeLimit", "-1").Key == 200 && Wait(() => { var st = StateOf(radarr, hMovie); return st == "uploading" || st == "stalledUP"; }, 6000), "setShareLimits -1 (no limit): seeds again");
                        Ok(Directory.Exists(cp) && File.Exists(Path.Combine(cp, "file0.bin")), "the finished files were never moved away before the app imports them");
                        // categories, queue, force start
                        Ok(radarr.Post("/api/v2/torrents/setCategory", "hashes", hEp, "category", "nope").Key == 409, "setCategory to a category that doesn't exist -> 409 (the apps then create it)");
                        Ok(radarr.Post("/api/v2/torrents/setCategory", "hashes", hEp, "category", "radarr").Key == 200 && Torrents(radarr, "?category=radarr").Count == 2, "setCategory radarr");
                        Ok(radarr.Post("/api/v2/torrents/topPrio", "hashes", hEp).Key == 200 && core.All().OrderBy(x => x.Queue).First().Id == hEp, "topPrio moves it to the top of the queue");
                        Ok(radarr.Post("/api/v2/torrents/bottomPrio", "hashes", hEp).Key == 200 && radarr.Post("/api/v2/torrents/setForceStart", "hashes", hEp, "value", "true").Key == 200, "bottomPrio / setForceStart");
                        Ok(Torrents(radarr, "?filter=completed").Count == 2 && Torrents(radarr, "?filter=downloading").Count == 0 && Torrents(radarr, "?filter=all&category=radarr&sort=name&limit=1").Count == 1, "torrents/info filter / sort / limit");
                        r = radarr.Get("/api/v2/sync/maindata?rid=0"); var md = Js.Deserialize<Dictionary<string, object>>(r.Value);
                        Ok(r.Key == 200 && (bool)md["full_update"] && ((Dictionary<string, object>)md["torrents"]).ContainsKey(hMovie) && ((Dictionary<string, object>)md["categories"]).ContainsKey("tv-sonarr"), "sync/maindata: torrents, categories");
                        r = radarr.Get("/api/v2/transfer/info"); Ok(r.Key == 200 && r.Value.Contains("\"connection_status\":\"connected\""), "transfer/info: connected (the VPN is up)");
                        Ok(srv.Start("127.0.0.3", FreePort()) != null && srv.Running == false, "it can't be put on the VPN's address");
                        serr = srv.Start("127.0.0.1", apiPort); Ok(serr == null, "back on 127.0.0.1");
                        // the restart signed everyone out: sign in again (like the apps after a 403)
                        Ok(radarr.Get("/api/v2/torrents/info").Key == 403 && radarr.Post("/api/v2/auth/login", "username", "arr-test", "password", "fake-test-pass-1").Value == "Ok.", "after a restart: 403, then the app signs in again");
                        // the VPN drops: everything locked, nothing is reported failed
                        await core.SetVpn(null);
                        Ok(!core.EngineUp && StateOf(radarr, hMovie) == "queuedUP" && radarr.Get("/api/v2/transfer/info").Value.Contains("\"disconnected\""), "VPN dropped: finished torrent -> queuedUP (still importable), transfer/info disconnected");
                        // a .torrent URL while locked: it waits, then it's fetched over the VPN once it's back
                        var fetched = new List<string>(); api.FetchUrl = u => { lock (fetched) fetched.Add(u + " engine=" + core.EngineUp); return File.ReadAllBytes(tfEp); };
                        radarr.Post("/api/v2/torrents/delete", "hashes", hEp, "deleteFiles", "false");
                        r = radarr.Post("/api/v2/torrents/add", "urls", "http://127.0.0.1:9/made-up.torrent", "category", "tv-sonarr");
                        Ok(r.Value == "Ok." && api.PendingCount == 1 && fetched.Count == 0, ".torrent URL while the VPN is off: accepted, nothing fetched");
                        await core.SetVpn(Vpn(VpnIp));
                        Ok(Wait(() => api.PendingCount == 0 && core.Get(hEp) != null, 10000) && fetched.Count == 1 && fetched[0].EndsWith("engine=True"), "...fetched once the VPN is back, then added (" + string.Join(",", fetched) + ")");
                        // delete without files
                        Ok(radarr.Post("/api/v2/torrents/delete", "hashes", hMovie, "deleteFiles", "false").Key == 200 && One(radarr, hMovie) == null && Directory.Exists(cp), "torrents/delete deleteFiles=false: gone from the list, files kept");
                        // leak checks over the whole run
                        List<string> ann; lock (w.Announces) ann = w.Announces.ToList();
                        Ok(ann.Any(x => x == "127.0.0.3:" + port) && ann.Where(x => x.EndsWith(":" + port)).All(x => x.StartsWith("127.0.0.3:")), "the tracker saw the app-added torrents only from the VPN address");
                        var audit = Net.Audit.ToArray();
                        Ok(audit.Length > 0 && audit.All(x => x.Contains(" 127.0.0.3:")), "1.2 leak check: every socket the engine opened was bound to the VPN IP (" + audit.Length + ")");

                        // ---- security
                        using (var stranger = new Arr("http://127.0.0.1:" + apiPort))
                        {
                            Ok(stranger.Get("/api/v2/torrents/info").Key == 403 && stranger.Post("/api/v2/torrents/delete", "hashes", "all", "deleteFiles", "false").Key == 403 && core.All().Count > 0, "no cookie: 403 for reading and for deleting");
                            stranger.H.DefaultRequestHeaders.Add("Cookie", "SID=made-up-session"); Ok(stranger.Get("/api/v2/torrents/info").Key == 403, "a made-up SID: 403");
                        }
                        var req = new HttpRequestMessage(HttpMethod.Get, radarr.Base + "/api/v2/torrents/info"); req.Headers.Add("Origin", "http://evil.example");
                        Ok((int)radarr.H.SendAsync(req).Result.StatusCode == 401, "a request from another site's page (Origin) -> 401");
                        // the allow-list (127.0.0.2 is another loopback address: a "different device" for this test)
                        var ck = "Cookie: SID=" + radarr.Sid + "\r\n";
                        Ok(RawGet(IPAddress.Parse("127.0.0.2"), apiPort, "/api/v2/app/webapiVersion").Contains(" 403 ") && RawGet(IPAddress.Parse("127.0.0.2"), apiPort, "/api/v2/app/version", ck).Contains(" 200 "), "no allow-list: another local address can reach it (only with a sign-in)");
                        s.ApiAllow = "127.0.0.1";
                        Ok(!RawGet(IPAddress.Parse("127.0.0.2"), apiPort, "/api/v2/app/version", ck).Contains(" 200 ") && RawGet(IPAddress.Loopback, apiPort, "/api/v2/app/version", ck).Contains(" 200 "), "allow-list 127.0.0.1: 127.0.0.2 refused even with a valid sign-in, 127.0.0.1 still fine");
                        s.ApiAllow = null;
                        // the sign-in expires
                        api.Guard.SessionTtlOverride = TimeSpan.FromMilliseconds(400); Thread.Sleep(900);
                        Ok(radarr.Get("/api/v2/torrents/info").Key == 403, "the sign-in expired: 403");
                        api.Guard.SessionTtlOverride = null;
                        Ok(radarr.Post("/api/v2/auth/login", "username", "arr-test", "password", "fake-test-pass-1").Value == "Ok." && radarr.Get("/api/v2/torrents/info").Key == 200, "...signing in again works");
                        // wrong passwords: locked out after 5, even with the right one
                        using (var guesser = new Arr("http://127.0.0.1:" + apiPort))
                        {
                            var answers = new List<string>(); for (int i = 0; i < ApiGuard.MaxFails; i++) answers.Add(guesser.Post("/api/v2/auth/login", "username", "arr-test", "password", "guess" + i).Value);
                            var after = guesser.Post("/api/v2/auth/login", "username", "arr-test", "password", "fake-test-pass-1");
                            Ok(answers.All(x => x == "Fails.") && after.Key == 403 && after.Value.Contains("banned") && guesser.Sid == null, "5 wrong passwords -> locked out (403), even the right password is refused");
                        }
                        api.Guard.ResetBans();
                        Ok(radarr.Post("/api/v2/auth/logout").Key == 200 && radarr.Get("/api/v2/torrents/info").Key == 403, "auth/logout ends the sign-in");
                    }
                    Ok(srv.Start("0.0.0.0", FreePort()) != null && srv.Start("8.8.8.8", FreePort()) != null, "it refuses 0.0.0.0 and public addresses");
                }
                finally
                {
                    if (ticker != null) ticker.Dispose(); srv.Dispose(); core.Dispose();
                }
                Ok(!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l => l.Port == apiPort), "stopped: nothing listens any more");
            }
        }
    }
}
