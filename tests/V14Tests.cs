// Hallow Torrents 1.4 tests - still THIS PC only.
//   * The local-server exception for indexers: private-address detection, home-network names, "every resolved address must be
//     private" (mixed answers, DNS rebinding to a public address), only your indexer base addresses, pinned connections, redirects
//     decided again (a redirect to a public host goes back through the VPN-bound socket, or is refused while the VPN is off). A Torznab
//     stand-in on 127.0.0.1 plays a local Prowlarr; a second one on 127.0.0.2 plays a PUBLIC host (PrivateNet.Override says so).
//   * The web interface: sign-in, CSRF (token + Origin), CSP and the other headers, add a magnet / a .torrent upload, pause / resume,
//     categories, the search proxy - against the local swarm (tracker + seeder on 127.0.0.1, engine bound to 127.0.0.3) and the Torznab
//     stand-in.
//   * Headless mode: HallowTorrentsHeadless.exe and HallowTorrents.exe --headless start, answer the pipe and serve the web interface
//     with no window; the headless exe references no WPF / WinForms.
//   * The pipe contract (REMOTE-CONTRACT.md): list / stats / pause / resume / add --direct shapes from a test copy of the app.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static readonly Func<IPAddress, bool?> LoopbackPublic = a => IPAddress.IsLoopback(a) ? (bool?)false : null;
        static readonly IPAddress PublicStandIn = IPAddress.Parse("127.0.0.2");
        static readonly Func<IPAddress, bool?> StandInPublic = a => a.Equals(PublicStandIn) ? (bool?)false : null;

        // the 1.3 search suite with loopback counted as public (kept out of Main: nothing may touch the app's statics before the test
        // folder is set)
        static void Search13AsPublic() { PrivateNet.Override = LoopbackPublic; try { Search13().Wait(); } finally { PrivateNet.Override = null; } }

        // ---------------- pure parts
        static void V14Unit()
        {
            Func<string, bool> P = s => PrivateNet.IsPrivate(IPAddress.Parse(s));
            var priv = new[] { "127.0.0.1", "127.255.255.254", "::1", "10.0.0.1", "10.255.255.255", "172.16.0.1", "172.31.255.255", "192.168.0.1", "192.168.255.254", "100.64.0.1", "100.127.255.254", "fe80::1", "febf::1", "::ffff:192.168.1.5", "::ffff:127.0.0.1" };
            var pub = new[] { "8.8.8.8", "1.1.1.1", "172.15.255.255", "172.32.0.0", "100.63.255.255", "100.128.0.0", "169.254.1.1", "192.169.0.1", "11.0.0.1", "9.255.255.255", "2001:4860::8888", "fc00::1", "fec0::1", "::ffff:8.8.8.8", "0.0.0.0", "224.0.0.1", "255.255.255.255" };
            Ok(priv.All(P), "private: loopback 127/8 and ::1, 10/8, 172.16/12, 192.168/16, Tailscale 100.64/10, fe80::/10, IPv4-mapped (" + string.Join(" ", priv.Where(x => !P(x))) + ")");
            Ok(!pub.Any(P), "public: everything else - 172.15 / 172.32, 100.63 / 100.128, 169.254, unique-local fc00::, mapped 8.8.8.8, 0.0.0.0, multicast (" + string.Join(" ", pub.Where(P)) + ")");
            Ok(PrivateNet.LocalName("nas") && PrivateNet.LocalName("NAS.local") && PrivateNet.LocalName("box.lan") && PrivateNet.LocalName("x.home.arpa") && PrivateNet.LocalName("pc.tail1234.ts.net") && PrivateNet.LocalName("localhost") && !PrivateNet.LocalName("prowlarr.example.com") && !PrivateNet.LocalName("indexer.org") && !PrivateNet.LocalName(""),
                "home-network names (nas, .local, .lan, .home.arpa, Tailscale .ts.net) vs public names");
            Ok(PrivateNet.LooksLocal("http://192.168.1.20:9117/x") && PrivateNet.LooksLocal("http://nas:9696") && !PrivateNet.LooksLocal("https://indexer.example.com") && !PrivateNet.LooksLocal("http://8.8.8.8/"), "pictures: decided without a lookup");

            // decisions with a stand-in resolver (no real DNS): only your indexer base addresses, every address private, decided per request
            var calls = new List<string>(); var rebind = 0;
            var oldResolve = PrivateNet.SystemResolve;
            PrivateNet.SystemResolve = h =>
            {
                lock (calls) calls.Add(h);
                switch (h)
                {
                    case "nas": return new[] { IPAddress.Parse("192.168.1.20") };
                    case "ts.tail1.ts.net": return new[] { IPAddress.Parse("100.101.102.103") };
                    case "mixed.lan": return new[] { IPAddress.Parse("192.168.1.5"), IPAddress.Parse("93.184.216.34") };
                    case "rebind.lan": return ++rebind == 1 ? new[] { IPAddress.Parse("192.168.1.7") } : new[] { IPAddress.Parse("93.184.216.34") };
                    case "v6.lan": return new[] { IPAddress.Parse("fe80::5") };
                    default: throw new SocketException(11001);
                }
            };
            try
            {
                var bases = new[] { "http://nas:9696", "http://mixed.lan:9696", "http://rebind.lan:9117", "http://127.0.0.1:9696", "http://8.8.8.8:9696", "http://ts.tail1.ts.net:9696", "https://prowlarr.example.com", "http://localhost:9117", "http://v6.lan:9696", "http://unknown.lan:9696" };
                Func<string, IndexerRoute> D = u => IndexerRoute.Decide(u, bases, null, null);
                Ok(D("http://nas:9696/1/api?t=search").Local && D("http://NAS:9696/2/download").Local, "your Prowlarr on the NAS (nas -> 192.168.1.20): local - " + D("http://nas:9696/1/api").Why);
                Ok(!D("http://nas:9697/1/api").Local && !D("https://nas:9696/1/api").Local && !D("http://nas2:9696/").Local, "a different port, scheme or host is not one of your indexer addresses - no exception");
                Ok(D("http://127.0.0.1:9696/api").Local && D("http://localhost:9117/api").Local && D("http://ts.tail1.ts.net:9696/x").Local && D("http://v6.lan:9696/x").Local, "this PC (127.0.0.1, localhost - no lookup), Tailscale (100.x), link-local IPv6: local");
                Ok(!D("http://8.8.8.8:9696/api").Local, "an indexer base at a public address: never local (over the VPN)");
                var mixed = D("http://mixed.lan:9696/api"); Ok(!mixed.Local && mixed.Why.Contains("public"), "mixed answers (192.168.1.5 + 93.184.216.34): not local - " + mixed.Why);
                var r1 = D("http://rebind.lan:9117/api"); var r2 = D("http://rebind.lan:9117/api");
                Ok(r1.Local && !r2.Local && r2.Why.Contains("93.184.216.34"), "DNS rebinding: private on the first lookup, public on the next - that request is NOT local (decided every time): " + r2.Why);
                calls.Clear(); var pubName = D("https://prowlarr.example.com/api");
                Ok(!pubName.Local && calls.Count == 0, "a public name while the VPN is off: not looked up at all (never Windows' resolver), so no exception");
                Ok(!D("http://unknown.lan:9696/").Local, "a name that can't be looked up: not local");
                Ok(!IndexerRoute.Decide("http://nas:9696/1/api", null, null, null).Local && !IndexerRoute.Decide("magnet:?xt=urn:btih:00", bases, null, null).Local && !IndexerRoute.Decide("ftp://nas:9696/", new[] { "ftp://nas:9696" }, null, null).Local, "no indexers / a magnet / not http(s): no exception");
                // pinning: a connection only goes to the addresses that were checked
                string pinErr = null; try { LocalFetch.Fetch(new Uri("http://nas:9696/x"), new[] { IPAddress.Parse("93.184.216.34") }, 1000, TimeSpan.FromSeconds(2)); } catch (Exception e) { pinErr = e.GetBaseException().Message; }
                Ok(pinErr != null && pinErr.Contains("refused"), "pinned: a public address is refused before any connection (" + pinErr + ")");
            }
            finally { PrivateNet.SystemResolve = oldResolve; }

            // DNS answers: every A record
            var pkt = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0 }; pkt.AddRange(DnsVpn.Query("x.lan", 0x1234).Skip(12));
            foreach (var ip in new[] { new byte[] { 192, 168, 1, 9 }, new byte[] { 93, 184, 216, 34 } }) { pkt.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4 }); pkt.AddRange(ip); }
            var ans = DnsVpn.Answers(pkt.ToArray(), pkt.Count, 0x1234);
            Ok(ans.Count == 2 && ans[0].ToString() == "192.168.1.9" && ans[1].ToString() == "93.184.216.34" && DnsVpn.Answer(pkt.ToArray(), pkt.Count, 0x1234).ToString() == "192.168.1.9", "DNS over the VPN: all A records are read (the check needs every one)");

            // the pipe's small parsers
            var a = "magnet:?xt=urn:btih:ab --direct --category tv"; Ok(RemoteApi.TakeDirect(ref a) && a == "magnet:?xt=urn:btih:ab --category tv", "add ... --direct is recognised and removed: " + a);
            a = "magnet:?xt=urn:btih:ab --category tv"; Ok(!RemoteApi.TakeDirect(ref a) && a == "magnet:?xt=urn:btih:ab --category tv", "...and left alone without it");
            Ok(RemoteApi.Code(new TorrentView { State = "Waiting for VPN" }) == "waiting-vpn" && RemoteApi.Code(new TorrentView { State = "Paused for game" }) == "game" && RemoteApi.Code(new TorrentView { State = "Seeding" }) == "seeding" && RemoteApi.Code(new TorrentView { State = "Done (limit reached)" }) == "done", "state codes for the phone page");
            Ok(new WebUi(new QbitApi(null, () => new TorSettings(), null), () => new TorSettings()).Csrf("abc") != new WebUi(new QbitApi(null, () => new TorSettings(), null), () => new TorSettings()).Csrf("abc"), "CSRF tokens: an HMAC with a per-run secret (another run can't forge them)");
            Ok(WebUi.Csp.Contains("default-src 'none'") && WebUi.Csp.Contains("script-src 'self'") && !WebUi.Csp.Contains("unsafe") && WebUi.Csp.Contains("frame-ancestors 'none'"), "CSP: self only, no unsafe-inline / eval, no framing");
        }

        // ---------------- the local-server exception against stand-ins
        static async Task Local14()
        {
            var root = Path.Combine(Dir, "local14"); Directory.CreateDirectory(root);
            var local = new TzServer(); var pubSrv = new TzServer(PublicStandIn, 0); var rebindPub = new TzServer(PublicStandIn, local.Port);
            var oldOverride = PrivateNet.Override; var oldResolve = PrivateNet.SystemResolve;
            PrivateNet.Override = StandInPublic;
            Net.AuditOn = true; string a0; while (Net.Audit.TryDequeue(out a0)) { }
            try
            {
                var bytes = File.ReadAllBytes(await TinyTorrent(root));
                var u = local.Url(""); var pu = pubSrv.Url("");
                var items = Channel(
                    Item("Local.Show.S01E01.1080p.WEB-DL.x264-GRP", 2000 * MiB, 50, 60, u + "/dl/1", null, 600, 5040),
                    Item("Local.Show.S01E01.720p.HDTV.x264-PUB", 900 * MiB, 40, 45, pu + "/dl/2", null, 600, 5040),
                    Item("Local.Show.S01E01.1080p.WEB-DL.x264-RED", 2000 * MiB, 30, 35, u + "/dl/3", null, 600, 5040),
                    Item("Local.Show.S01E01.1080p.WEB-DL.x264-MAG", 2000 * MiB, 20, 25, u + "/dl/4", null, 600, 5040));
                local.Route = path => path.StartsWith("/dl/1") ? new TzReply { Type = "application/x-bittorrent", Body = bytes }
                    : path.StartsWith("/dl/3") ? new TzReply { Status = 302, Location = pu + "/dl/3" }
                    : path.StartsWith("/dl/4") ? new TzReply { Status = 302, Location = "magnet:?xt=urn:btih:4123456789abcdef0123456789abcdef01234567&dn=Mag" }
                    : Q(path, "apikey") != TzKey ? Xml("<?xml version=\"1.0\"?><error code=\"100\" description=\"Invalid API Key\"/>") : Q(path, "t") == "caps" ? Xml(CapsFull) : Xml(items);
                pubSrv.Route = path => new TzReply { Type = "application/x-bittorrent", Body = bytes };
                rebindPub.Route = path => Xml(items);

                // the VPN is OFF (no engine): only your local indexer manager can be asked
                var se = new SearchEngine(null, null, Path.Combine(root, "se")); Indexer x;
                Ok(se.AddIndexer("torznab", "Local Prowlarr", u + "/api", TzKey, out x) == null, "a Torznab feed on 127.0.0.1 (a local Prowlarr)");
                Ok(se.IsLocal(u + "/api") && !se.IsLocal(pu + "/dl/2") && !se.VpnUp, "...it is a local server; the 127.0.0.2 stand-in is a public host; the VPN is off");
                var o = await se.Run(SearchQuery.From("local show s01e01", ""), DateTime.UtcNow);
                Ok(!o.Locked && o.Error == null && o.Results.Count == 4 && o.Status.Count == 1 && o.Status[0].State == "ok" && o.Status[0].Local, "VPN off: the local indexer manager is still searched - 4 results, its status says local (" + o.Error + ")");
                var seenLocal = local.Seen();
                Ok(seenLocal.Count >= 2 && seenLocal.All(s => s.StartsWith("127.0.0.1 ")), "...the requests reached it straight from this PC (not bound to a VPN address): " + string.Join(",", seenLocal.Select(s => s.Split(' ')[0]).Distinct()));
                var js = Family.Json(SearchEngine.Json(o, 10));
                Ok(js.Contains("\"local\":true") && !js.Contains(TzKey), "pipe / web JSON: \"local\": true for it, never the key");
                byte[] t; string m;
                var r1 = o.Results.First(r => r.Title.EndsWith("-GRP")); Ok(se.Fetch(r1, out t, out m) == null && t != null && t.Length == bytes.Length, "a .torrent from the local indexer manager: fetched directly");
                var r2 = o.Results.First(r => r.Title.EndsWith("-PUB")); var e2 = se.Fetch(r2, out t, out m);
                Ok(e2 != null && e2.StartsWith("Waiting for the VPN") && pubSrv.Seen().Count == 0, "a .torrent on a public host: waits for the VPN, nothing sent (" + e2 + ")");
                var r3 = o.Results.First(r => r.Title.EndsWith("-RED")); var e3 = se.Fetch(r3, out t, out m);
                Ok(e3 != null && t == null && pubSrv.Seen().Count == 0 && local.Seen().Any(s => s.Contains("/dl/3")), "a local link that REDIRECTS to a public host: refused while the VPN is off, nothing reached the public host (" + e3 + ")");
                var r4 = o.Results.First(r => r.Title.EndsWith("-MAG")); Ok(se.Fetch(r4, out t, out m) == null && m != null && m.StartsWith("magnet:"), "a local link that redirects to a magnet link: the magnet (it waits for the VPN when added)");

                // the VPN is UP (stand-in 127.0.0.3): the redirect to the public host goes out over the VPN-bound socket
                var before = local.Seen().Count;
                var rep = IndexerNet.Get(u + "/dl/3", TimeSpan.FromSeconds(5), se.Bases(), VpnIp, new List<IPAddress> { IPAddress.Loopback });
                var ps = pubSrv.Seen();
                Ok(rep.Body != null && local.Seen().Skip(before).Any(s => s.StartsWith("127.0.0.1 ") && s.Contains("/dl/3")) && ps.Count == 1 && ps[0].StartsWith("127.0.0.3 "), "VPN up: hop 1 to the local server directly, hop 2 (public) only from the VPN address: " + string.Join(",", ps));
                var rep2 = IndexerNet.Fetch(pu + "/dl/2", TimeSpan.FromSeconds(5), se.Bases(), VpnIp, new List<IPAddress> { IPAddress.Loopback });
                Ok(rep2.Body != null && pubSrv.Seen().Last().StartsWith("127.0.0.3 "), "a public .torrent link: over the VPN-bound socket, as in 1.3");
                var audit = Net.Audit.ToArray();
                Ok(audit.Any(s => s.StartsWith("local ")) && audit.Where(s => s.StartsWith("http ")).All(s => s.Contains(" 127.0.0.3:")), "socket audit: direct sockets only to the local server; every public request bound to the VPN IP (" + audit.Length + ")");

                // DNS rebinding and mixed answers end to end: the host turns public -> no exception -> while the VPN is off nothing is sent
                var calls = 0;
                PrivateNet.SystemResolve = h => h == "rebind.lan" ? (++calls == 1 ? new[] { IPAddress.Loopback } : new[] { PublicStandIn }) : h == "mixed.lan" ? new[] { IPAddress.Loopback, PublicStandIn } : oldResolve(h);
                var se2 = new SearchEngine(null, null, null); Indexer x2;
                se2.AddIndexer("torznab", "Rebind", "http://rebind.lan:" + local.Port + "/api", TzKey, out x2);
                var lb = local.Seen().Count;
                Ok(se2.IsLocal("http://rebind.lan:" + local.Port + "/api"), "rebind.lan -> 127.0.0.1 on the first lookup: local");
                var o2 = await se2.Run(SearchQuery.From("local show", ""), DateTime.UtcNow);
                Ok(o2.Locked && rebindPub.Seen().Count == 0 && local.Seen().Count == lb, "...then it resolves to the public stand-in: the search is VPN-locked, nothing reached either server (" + o2.Error + ")");
                var se3 = new SearchEngine(null, null, null); Indexer x3; se3.AddIndexer("torznab", "Mixed", "http://mixed.lan:" + local.Port + "/api", TzKey, out x3);
                var o3 = await se3.Run(SearchQuery.From("local show", ""), DateTime.UtcNow);
                Ok(o3.Locked && rebindPub.Seen().Count == 0 && local.Seen().Count == lb, "mixed answers (127.0.0.1 + the public stand-in): VPN-locked, nothing sent");
                // not one of your indexers (e.g. an RSS feed or tracker on a private address): never the exception
                Ok(!IndexerRoute.Decide(u + "/rss", new string[0], null, null).Local, "a private address that isn't one of your indexers: no exception (RSS / trackers / blocklists keep the VPN rule)");
            }
            finally
            {
                PrivateNet.Override = oldOverride; PrivateNet.SystemResolve = oldResolve;
                local.Dispose(); pubSrv.Dispose(); rebindPub.Dispose();
            }
        }
        static async Task<string> TinyTorrent(string root)
        {
            var src = Path.Combine(root, "tiny"); Directory.CreateDirectory(src); File.WriteAllText(Path.Combine(src, "a.txt"), "made up " + Guid.NewGuid());
            var tf = Path.Combine(root, "tiny.torrent"); var cr = new MonoTorrent.TorrentCreator(MonoTorrent.TorrentType.V1Only) { PieceLength = 32 * 1024 }; cr.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" });
            await cr.CreateAsync(new MonoTorrent.TorrentFileSource(src), tf); return tf;
        }

        // ---------------- the web interface
        class Web : IDisposable
        {
            public readonly HttpClient H; public readonly CookieContainer Jar = new CookieContainer(); public readonly string Base; public string Csrf;
            public Web(string b) { Base = b; H = new HttpClient(new HttpClientHandler { CookieContainer = Jar, UseCookies = true, UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) }; }
            public HttpResponseMessage Send(HttpMethod m, string path, HttpContent body = null, string origin = null, string token = null)
            {
                var q = new HttpRequestMessage(m, Base + path) { Content = body };
                if (origin != null) q.Headers.TryAddWithoutValidation("Origin", origin);
                var tk = token ?? Csrf; if (tk != null && m == HttpMethod.Post) q.Headers.TryAddWithoutValidation("X-CSRF-Token", tk);
                return H.SendAsync(q).Result;
            }
            public KeyValuePair<int, Dictionary<string, object>> Json(HttpMethod m, string path, HttpContent body = null, string origin = null, string token = null)
            {
                var r = Send(m, path, body, origin, token); var s = r.Content.ReadAsStringAsync().Result; Dictionary<string, object> d = null;
                try { d = Js.Deserialize<Dictionary<string, object>>(s); } catch { }
                return new KeyValuePair<int, Dictionary<string, object>>((int)r.StatusCode, d);
            }
            public KeyValuePair<int, Dictionary<string, object>> Post(string path, params string[] kv)
            {
                var d = new List<KeyValuePair<string, string>>(); for (int i = 0; i + 1 < kv.Length; i += 2) d.Add(new KeyValuePair<string, string>(kv[i], kv[i + 1]));
                return Json(HttpMethod.Post, path, new FormUrlEncodedContent(d));
            }
            public KeyValuePair<int, Dictionary<string, object>> Get(string path) { return Json(HttpMethod.Get, path); }
            public void Dispose() { H.Dispose(); }
        }
        static List<Dictionary<string, object>> Items(Dictionary<string, object> list, string key = "torrents") { return list == null || !list.ContainsKey(key) ? new List<Dictionary<string, object>>() : ((System.Collections.IEnumerable)list[key]).Cast<Dictionary<string, object>>().ToList(); }
        static string Hdr(HttpResponseMessage r, string n) { IEnumerable<string> v; return r.Headers.TryGetValues(n, out v) || r.Content.Headers.TryGetValues(n, out v) ? string.Join(",", v) : null; }

        static async Task Web14()
        {
            var root = Path.Combine(Dir, "web14"); Directory.CreateDirectory(root);
            var oldOverride = PrivateNet.Override; PrivateNet.Override = LoopbackPublic;   // the Torznab stand-in plays a public indexer here (VPN only)
            using (var w = StartSwarm(root, 0))
            {
                var tracker = "http://127.0.0.1:" + w.HttpPort + "/announce";
                var tfA = await MakeTorrent(w, "Web.Made.Up.Movie.2025.1080p", new[] { 600000, 200000 }, tracker);
                var tfB = await MakeTorrent(w, "Web.Upload.Show.S01E01.720p", new[] { 300000 }, tracker);
                var tA = Torrent.Load(tfA); var hA = tA.InfoHashes.V1.ToHex().ToLowerInvariant(); var hB = Torrent.Load(tfB).InfoHashes.V1.ToHex().ToLowerInvariant();
                int port = FreePort(); var save = Path.Combine(root, "dl");
                var s = TestSettings(port, save); s.ApiOn = true; s.WebUiOn = false; s.ApiUser = "web-test"; s.ApiPassHash = ApiAuth.Hash("fake-web-pass-1");
                var core = new Core(Path.Combine(root, "core"), () => s);
                var api = new QbitApi(core, () => s, act => { act(); s.Fix(); });
                var se = new SearchEngine(core, null, Path.Combine(root, "core")); api.Search = se;
                int vpnAsks = 0;
                api.Ui = new WebUi(api, () => s) { ConnectVpn = () => { vpnAsks++; return null; }, Game = () => null, VpnText = () => core.EngineUp ? "Connected (stand-in)" : "off" };
                var srv = new ApiServer(api); int apiPort = FreePort(); Ok(srv.Start("127.0.0.1", apiPort) == null, "the server starts on 127.0.0.1 (web interface switched off)");
                var tz = new TzServer();
                Timer ticker = new Timer(_ => { try { core.Tick().Wait(5000); } catch { } }, null, 0, 500);
                try
                {
                    using (var b = new Web("http://127.0.0.1:" + apiPort))
                    {
                        Ok(b.Send(HttpMethod.Get, "/ui/").StatusCode == HttpStatusCode.NotFound && b.Get("/ui/api/session").Key == 404, "web interface off (the default): /ui/ is 404");
                        s.WebUiOn = true;
                        var page = b.Send(HttpMethod.Get, "/ui/"); var html = page.Content.ReadAsStringAsync().Result;
                        Ok(page.StatusCode == HttpStatusCode.OK && page.Content.Headers.ContentType.MediaType == "text/html" && html.Contains("<script src=\"/ui/app.js\"") && html.Contains("Hallow Torrents"), "switched on: /ui/ serves the page");
                        Ok(Hdr(page, "Content-Security-Policy") == WebUi.Csp && Hdr(page, "X-Frame-Options") == "DENY" && Hdr(page, "X-Content-Type-Options") == "nosniff" && (Hdr(page, "Cache-Control") ?? "").Contains("no-store") && Hdr(page, "Referrer-Policy") == "same-origin", "CSP (self only), no framing, nosniff, no-store, same-origin referrer");
                        var js = b.Send(HttpMethod.Get, "/ui/app.js").Content.ReadAsStringAsync().Result; var css = b.Send(HttpMethod.Get, "/ui/app.css").Content.ReadAsStringAsync().Result;
                        Ok(js.Contains("X-CSRF-Token") && css.Contains("prefers-color-scheme") && !(html + js + css).Contains("http://") && !(html + js + css).Contains("https://") && !html.Contains("<script>") && !html.Contains(" style=") && !js.Contains("innerHTML") && !js.Contains("eval("), "no external scripts / fonts / links, no inline script or style, no innerHTML / eval; light and dark");
                        Ok(b.Send(HttpMethod.Get, "/").StatusCode == HttpStatusCode.Found, "/ goes to /ui/ when it's on");
                        // sign-in
                        var ses = b.Get("/ui/api/session"); Ok(ses.Key == 200 && !(bool)ses.Value["signedIn"] && ses.Value["csrf"] == null, "session before sign-in: not signed in, no token");
                        Ok(b.Get("/ui/api/list").Key == 403 && b.Get("/ui/api/stats").Key == 403 && b.Post("/ui/api/add", "magnet", "magnet:?xt=urn:btih:" + hA).Key == 403, "list / stats / add before sign-in: 403");
                        var bad = b.Post("/ui/api/login", "username", "web-test", "password", "wrong-pass"); Ok(bad.Key == 401 && !(bool)bad.Value["ok"] && b.Jar.GetCookies(new Uri(b.Base))["SID"] == null, "a wrong password: refused, no cookie");
                        Ok(b.Send(HttpMethod.Get, "/ui/api/login").StatusCode == HttpStatusCode.MethodNotAllowed, "login by GET: 405");
                        var lx = b.Json(HttpMethod.Post, "/ui/api/login", new FormUrlEncodedContent(new Dictionary<string, string> { { "username", "web-test" }, { "password", "fake-web-pass-1" } }), "http://evil.example");
                        Ok(lx.Key == 401 && b.Jar.GetCookies(new Uri(b.Base))["SID"] == null, "a sign-in posted from another site (Origin): refused");
                        var ok = b.Post("/ui/api/login", "username", "web-test", "password", "fake-web-pass-1");
                        var sid = b.Jar.GetCookies(new Uri(b.Base))["SID"];
                        Ok(ok.Key == 200 && (bool)ok.Value["ok"] && sid != null && ok.Value["csrf"] is string && ((string)ok.Value["csrf"]).Length > 20, "sign-in: the SID cookie and a CSRF token");
                        var raw = b.Send(HttpMethod.Get, "/ui/api/session"); var setc = Hdr(raw, "Set-Cookie");
                        b.Csrf = null;
                        Ok(b.Post("/ui/api/add", "magnet", "magnet:?xt=urn:btih:" + hA).Key == 403, "CSRF: a POST without the token is refused (403)");
                        Ok(b.Json(HttpMethod.Post, "/ui/api/add", new FormUrlEncodedContent(new Dictionary<string, string> { { "magnet", "magnet:?xt=urn:btih:" + hA } }), null, "forged-token-0123456789abcdefghijk").Key == 403, "...with a made-up token: 403");
                        b.Csrf = (string)ok.Value["csrf"];
                        Ok(b.Json(HttpMethod.Post, "/ui/api/add", new FormUrlEncodedContent(new Dictionary<string, string> { { "magnet", "magnet:?xt=urn:btih:" + hA } }), "http://evil.example").Key == 401 && core.Get(hA) == null, "...with the right token but from another site (Origin): 401, nothing added");
                        Ok((bool)b.Get("/ui/api/session").Value["signedIn"] && (string)b.Get("/ui/api/session").Value["csrf"] == b.Csrf, "session after sign-in: the same token back (a reloaded page carries on)");
                        // add a magnet while the VPN is OFF
                        var magnet = "magnet:?xt=urn:btih:" + hA + "&dn=" + Uri.EscapeDataString(tA.Name) + "&tr=" + Uri.EscapeDataString(tracker);
                        var add = b.Post("/ui/api/add", "magnet", magnet, "category", "movies");
                        Ok(add.Key == 200 && (bool)add.Value["ok"] && (string)add.Value["hash"] == hA && (string)add.Value["category"] == "Movies" && (bool)add.Value["vpnLocked"], "add a magnet (category movies) while the VPN is off: added, it waits");
                        Ok(b.Post("/ui/api/add", "magnet", "https://example.com/x.torrent").Key == 400 && b.Post("/ui/api/add", "magnet", magnet, "category", "Nope").Key == 400, "a web address or an unknown category: refused");
                        var l = b.Get("/ui/api/list"); var tl = Items(l.Value);
                        Ok(l.Key == 200 && (bool)l.Value["vpnLocked"] && tl.Count == 1 && (string)tl[0]["status"] == "waiting-vpn" && (bool)tl[0]["vpnLocked"] && (string)tl[0]["name"] == tA.Name, "list: VPN-locked, the torrent waits for the VPN (" + (tl.Count > 0 ? tl[0]["state"] : "") + ")");
                        // a .torrent upload
                        var mp = new MultipartFormDataContent(); var fc = new ByteArrayContent(File.ReadAllBytes(tfB)); fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent"); mp.Add(fc, "file", "show.torrent"); mp.Add(new StringContent("TV"), "category");
                        var up = b.Json(HttpMethod.Post, "/ui/api/add", mp);
                        Ok(up.Key == 200 && (bool)up.Value["ok"] && (string)up.Value["hash"] == hB && core.Get(hB).Category == "TV", "upload a .torrent file (multipart): added in TV");
                        var mpBad = new MultipartFormDataContent(); mpBad.Add(new ByteArrayContent(Encoding.ASCII.GetBytes("MZ not a torrent")), "file", "x.torrent");
                        Ok(b.Json(HttpMethod.Post, "/ui/api/add", mpBad).Key == 400, "a file that isn't a .torrent: refused");
                        // pause / resume / category
                        Ok(b.Post("/ui/api/pause", "hash", hB).Key == 200 && core.Get(hB).UserPaused && (bool)Items(b.Get("/ui/api/list").Value).First(t => (string)t["hash"] == hB)["paused"], "pause one torrent");
                        Ok(b.Post("/ui/api/resume", "hash", hB.Substring(0, 10)).Key == 200 && !core.Get(hB).UserPaused, "resume it (by the start of its hash)");
                        Ok(b.Post("/ui/api/pause", "hash", "all").Key == 200 && core.All().All(e => e.UserPaused) && b.Post("/ui/api/resume", "hash", "all").Key == 200 && core.All().All(e => !e.UserPaused), "pause all / resume all");
                        Ok(b.Post("/ui/api/pause", "hash", "nothing").Key == 404, "an unknown hash: 404");
                        Ok(b.Post("/ui/api/category", "hash", hB, "category", "movies").Key == 200 && core.Get(hB).Category == "Movies" && b.Post("/ui/api/category", "hash", hB, "category", "nope").Key == 400, "change a category (unknown ones refused)");
                        var cats = b.Send(HttpMethod.Get, "/ui/api/categories").Content.ReadAsStringAsync().Result; Ok(cats.Contains("\"Movies\"") && cats.Contains("\"TV\""), "categories for the page");
                        Ok(b.Get("/ui/api/list").Key == 200 && b.Send(HttpMethod.Get, "/ui/api/pause?hash=all").StatusCode == HttpStatusCode.MethodNotAllowed, "actions by GET: 405");
                        Ok(b.Post("/ui/api/connect-vpn").Key == 200 && vpnAsks == 1, "Connect VPN asks Hallow VPN");
                        var st = b.Get("/ui/api/status"); Ok(st.Key == 200 && (bool)st.Value["vpnRequired"] && (bool)st.Value["vpnLocked"], "status: VPN required, locked");
                        // the VPN comes up: the magnet downloads from the local swarm
                        await core.SetVpn(Vpn(VpnIp));
                        Ok(Wait(() => core.Get(hA).CompletedAt != null, 40000), "VPN up: the magnet added from the web interface downloads from the local swarm");
                        var done = Items(b.Get("/ui/api/list").Value).First(t => (string)t["hash"] == hA);
                        Ok(Convert.ToDouble(done["progress"]) == 1.0 && (bool)done["complete"] && !(bool)done["vpnLocked"], "list: 100 %, complete, not VPN-locked");
                        var stats = b.Get("/ui/api/stats"); Ok(stats.Key == 200 && Convert.ToInt64(((Dictionary<string, object>)stats.Value["session"])["down"]) > 0, "stats: session download counted");
                        // the search proxy: the Torznab stand-in (playing a public indexer - over the VPN-bound socket)
                        var bytesA = File.ReadAllBytes(tfB);
                        tz.Route = path => path.StartsWith("/dl/b") ? new TzReply { Type = "application/x-bittorrent", Body = bytesA } : Q(path, "apikey") != TzKey ? Xml("<?xml version=\"1.0\"?><error code=\"100\" description=\"Invalid API Key\"/>") : Q(path, "t") == "caps" ? Xml(CapsFull)
                            : Xml(Channel(Item("Web.Search.Show.S01E01.1080p.WEB-DL.x264-GRP", 2000 * MiB, 80, 90, tz.Url("/dl/b.torrent"), null, 600, 5040), Item("Web.Search.Show.S01E01.HDCAM", 900 * MiB, 10, 12, tz.Url("/dl/c.torrent"), null, 600, 5040)));
                        Indexer ix; se.AddIndexer("torznab", "Stand-in", tz.Url("/api"), TzKey, out ix);
                        var sr = b.Post("/ui/api/search", "q", "web search show s01e01", "n", "10"); var res = Items(sr.Value, "results");
                        Ok(sr.Key == 200 && (bool)sr.Value["ok"] && res.Count == 2 && ((string)res[0]["title"]).EndsWith("-GRP") && !Family.Json(sr.Value).Contains(TzKey), "search through the web interface: ranked results, no API key in the answer");
                        Ok(tz.Seen().Count > 0 && tz.Seen().All(x => x.StartsWith("127.0.0.3 ")), "...asked from the VPN address only (" + string.Join(",", tz.Seen().Select(x => x.Split(' ')[0]).Distinct()) + ")");
                        Ok(b.Post("/ui/api/search", "q", "").Key == 400 && b.Post("/ui/api/search-add", "id", "r999").Key == 404, "an empty search / an unknown result: refused");
                        core.Remove(hB, false).Wait(5000);
                        var sa = b.Post("/ui/api/search-add", "id", (string)res[0]["id"], "category", "tv");
                        Ok(sa.Key == 200 && (bool)sa.Value["ok"] && (string)sa.Value["hash"] == hB && core.Get(hB) != null && core.Get(hB).Category == "TV", "search-add: added straight into the list (category TV)");
                        // sign out
                        Ok(b.Post("/ui/api/logout").Key == 200 && b.Get("/ui/api/list").Key == 403, "sign out: the session is gone");
                        // the lock-out applies to the web sign-in too
                        for (int i = 0; i < ApiGuard.MaxFails; i++) b.Post("/ui/api/login", "username", "web-test", "password", "nope-" + i);
                        var banned = b.Post("/ui/api/login", "username", "web-test", "password", "fake-web-pass-1");
                        Ok(banned.Key == 403 && ((string)banned.Value["error"]).Contains("banned"), "5 wrong passwords: that address is locked out (also for the right one)");
                        api.Guard.ResetBans();
                        // the web interface is only there while the Web API runs, and on the same address
                        srv.Stop(); Ok(RawGet(IPAddress.Loopback, apiPort, "/ui/").StartsWith("closed"), "Web API off: the web interface is gone too");
                    }
                }
                finally { ticker.Dispose(); srv.Stop(); tz.Dispose(); core.Dispose(); PrivateNet.Override = oldOverride; }
            }
        }

        // ---------------- headless mode: no window, the pipe and the web interface
        static string StartHeadless(string exe, string args, string dir, out Process p)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1";
            p = Process.Start(psi);
            var gui = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "HallowTorrents.exe");
            string r = null; for (int i = 0; i < 60 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(gui, dir, "ping"); }
            return r;
        }
        static void HeadlessDir(string dir, int apiPort)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true,\"ApiOn\":true,\"WebUiOn\":true,\"ApiPort\":" + apiPort + ",\"ApiUser\":\"nas-test\",\"ApiPassHash\":\"" + ApiAuth.Hash("fake-nas-pass-1") + "\"}");
            File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\n");   // no VPN: locked
        }
        static void Headless14()
        {
            var bin = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location));
            var hexe = Path.Combine(bin, "HallowTorrentsHeadless.exe"); var gui = Path.Combine(bin, "HallowTorrents.exe");
            if (!File.Exists(hexe)) { Ok(false, "HallowTorrentsHeadless.exe is built"); return; }
            var refs = Assembly.ReflectionOnlyLoadFrom(hexe).GetReferencedAssemblies().Select(a => a.Name).ToList();
            Ok(!refs.Any(n => n == "PresentationFramework" || n == "PresentationCore" || n == "WindowsBase" || n == "System.Windows.Forms" || n == "System.Drawing" || n == "System.Xaml"), "HallowTorrentsHeadless.exe references no WPF / WinForms / System.Drawing (" + refs.Count + " references)");
            var dir = Path.Combine(Dir, "headless"); int apiPort = FreePort(); HeadlessDir(dir, apiPort);
            Process p; var r = StartHeadless(hexe, "", dir, out p);
            try
            {
                Ok(r == "ok {\"app\":\"Torrents\",\"version\":\"" + Program.Version + "\",\"headless\":true}", "headless exe: it starts and answers the pipe: " + r);
                Thread.Sleep(300); p.Refresh();
                Ok(p.MainWindowHandle == IntPtr.Zero && !p.HasExited, "...with no window");
                var mods = new List<string>(); try { foreach (ProcessModule m in p.Modules) mods.Add(m.ModuleName); } catch { }
                Ok(mods.Count > 0 && !mods.Any(n => n.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase) || n.StartsWith("PresentationCore", StringComparison.OrdinalIgnoreCase) || n.StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase)), "...and WPF / WinForms are never loaded (" + mods.Count + " modules)");
                var st = Cmd(gui, dir, "status"); Ok(st.Contains("\"headless\":true") && st.Contains("\"vpnLocked\":true") && st.Contains("\"webApi\":true") && st.Contains("\"webUi\":true"), "status: headless, VPN-locked, Web API and web interface on");
                var magnet = "magnet:?xt=urn:btih:5123456789abcdef0123456789abcdef01234567&dn=Headless+Magnet&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce";
                r = Cmd(gui, dir, "add " + magnet + " --category tv"); Ok(r.StartsWith("ok {\"id\":\"5123456789abcdef0123456789abcdef01234567\"") && r.Contains("\"asking\":false") && r.Contains("\"category\":\"TV\""), "pipe add --category (from another process): added straight away: " + r);
                r = Cmd(gui, dir, "list"); var l = Js.Deserialize<Dictionary<string, object>>(r.Substring(3)); var tl = Items(l);
                Ok(r.StartsWith("ok ") && (bool)l["vpnLocked"] && tl.Count == 1 && (bool)tl[0]["vpnLocked"] && (string)tl[0]["status"] == "waiting-vpn", "list: VPN-locked, the torrent waits for the VPN");
                Ok(Cmd(gui, dir, "open").StartsWith("error") && Cmd(gui, dir, "pause 5123456789ab").Contains("\"paused\":true") && Cmd(gui, dir, "resume 5123456789abcdef0123456789abcdef01234567").Contains("\"paused\":false"), "open: no window to open; pause / resume by hash");
                using (var b = new Web("http://127.0.0.1:" + apiPort))
                {
                    var page = b.Send(HttpMethod.Get, "/ui/");
                    Ok(page.StatusCode == HttpStatusCode.OK && Hdr(page, "Content-Security-Policy") == WebUi.Csp, "headless: the web interface is served (with the CSP)");
                    var ok = b.Post("/ui/api/login", "username", "nas-test", "password", "fake-nas-pass-1"); b.Csrf = ok.Value != null ? ok.Value["csrf"] as string : null;
                    var wl = b.Get("/ui/api/list"); Ok(ok.Key == 200 && wl.Key == 200 && Items(wl.Value).Count == 1, "...sign in and see the list");
                    Ok(b.Post("/ui/api/pause", "hash", "all").Key == 200 && Cmd(gui, dir, "list").Contains("\"paused\":true"), "...pause from the browser, seen over the pipe");
                }
                var ls = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(x => x.Port == apiPort).ToList();
                Ok(ls.Count == 1 && ls[0].Address.Equals(IPAddress.Loopback), "...listening on 127.0.0.1 only");
                Process p2; var psi2 = new ProcessStartInfo(hexe) { UseShellExecute = false, CreateNoWindow = true }; psi2.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; p2 = Process.Start(psi2);
                Ok(p2.WaitForExit(15000) && p2.ExitCode == 3, "a second copy (headless or the app) won't start next to it (exit " + (p2.HasExited ? p2.ExitCode.ToString() : "-") + ")");
                Ok(Cmd(gui, dir, "quit") == "ok" && p.WaitForExit(15000), "quit: it exits");
                Ok(File.ReadAllText(Path.Combine(dir, "torrents.json")).Contains("Headless Magnet") && File.Exists(Path.Combine(dir, "headless.log")) && !File.ReadAllText(Path.Combine(dir, "headless.log")).Contains("fake-nas-pass"), "the list was kept; headless.log written (no password in it)");
            }
            finally { try { if (!p.HasExited) p.Kill(); } catch { } }
            // the app's own --headless switch: the same host, no window
            var dir2 = Path.Combine(Dir, "headless2"); int port2 = FreePort(); HeadlessDir(dir2, port2);
            Process g; r = StartHeadless(gui, "--headless", dir2, out g);
            try
            {
                Thread.Sleep(300); g.Refresh();
                Ok(r != null && r.Contains("\"headless\":true") && g.MainWindowHandle == IntPtr.Zero, "HallowTorrents.exe --headless: the same, no window: " + r);
                using (var b = new Web("http://127.0.0.1:" + port2)) Ok(b.Send(HttpMethod.Get, "/ui/").StatusCode == HttpStatusCode.OK, "...serving the web interface");
                Ok(Cmd(gui, dir2, "quit") == "ok" && g.WaitForExit(15000), "...and it quits");
            }
            finally { try { if (!g.HasExited) g.Kill(); } catch { } }
        }

        // ---------------- the pipe contract from the app's test copy (REMOTE-CONTRACT.md)
        static void PipeContract14(string exe, string dir)
        {
            var r = Cmd(exe, dir, "list");
            Dictionary<string, object> l = null; try { l = Js.Deserialize<Dictionary<string, object>>(r.Substring(3)); } catch { }
            var tl = Items(l);
            Ok(r.StartsWith("ok {") && l != null && new[] { "vpnLocked", "held", "game", "downBps", "upBps", "count", "torrents" }.All(l.ContainsKey) && !(bool)l["vpnLocked"] && tl.Count >= 2, "pipe list: vpnLocked, held, game, speeds, count, torrents");
            var one = tl.FirstOrDefault(t => (string)t["name"] == "Test Magnet");
            Ok(one != null && new[] { "hash", "name", "progress", "downBps", "upBps", "state", "status", "category", "size", "done", "eta", "seeds", "peers", "ratio", "paused", "complete", "flagged", "vpnLocked" }.All(one.ContainsKey), "...each torrent: hash, name, progress, speeds, state + status, category, size, eta, peers, ratio, paused, complete, flagged, vpnLocked");
            r = Cmd(exe, dir, "pause 0123456789abcdef0123456789abcdef01234567"); Ok(r == "ok {\"hash\":\"0123456789abcdef0123456789abcdef01234567\",\"paused\":true,\"vpnLocked\":false}", "pause <hash>: " + r);
            Ok(Cmd(exe, dir, "test-list").Contains("Test Magnet|Paused") && Cmd(exe, dir, "list").Contains("\"paused\":true"), "...it's paused (list shows it)");
            r = Cmd(exe, dir, "resume 0123456789ab"); Ok(r.StartsWith("ok {\"hash\":\"0123456789abcdef0123456789abcdef01234567\",\"paused\":false"), "resume <start of the hash>: " + r);
            Ok(Cmd(exe, dir, "pause nothing").StartsWith("error") && Cmd(exe, dir, "pause").StartsWith("error") && Cmd(exe, dir, "resume 0000000000000000").StartsWith("error no torrent"), "pause / resume: a bad or unknown hash is an error");
            r = Cmd(exe, dir, "stats"); Dictionary<string, object> st = null; try { st = Js.Deserialize<Dictionary<string, object>>(r.Substring(3)); } catch { }
            Ok(st != null && new[] { "session", "allTime", "since", "downBps", "upBps", "torrents", "vpnLocked", "blocklistRanges" }.All(st.ContainsKey) && ((Dictionary<string, object>)st["session"]).ContainsKey("ratio"), "stats: session and all-time (down, up, ratio, seeding, VPN-locked time, blocked), speeds, counts");
            var magnet3 = "magnet:?xt=urn:btih:6123456789abcdef0123456789abcdef01234567&dn=Direct+Magnet&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce";
            r = Cmd(exe, dir, "add " + magnet3 + " --direct --category movies"); Ok(r.StartsWith("ok {\"id\":\"6123456789abcdef0123456789abcdef01234567\",\"asking\":false,\"category\":\"Movies\"}"), "add <magnet> --direct --category movies (the phone's way, from another process): " + r);
        }
    }
}
