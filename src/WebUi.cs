// Hallow Torrents 1.4 - the web interface: one Hallow-styled page at http://<address>:<port>/ui/, served by the same local server as the
// 1.2 Web API (ApiServer) with the same rules: off by default (and only while the Web API is on), 127.0.0.1 by default, a home-network
// or Tailscale address only if you pick one, never 0.0.0.0 / a public address / the VPN's address, internet addresses refused, the
// optional allow-list, the same sign-in (SID cookie, expiry, 5 wrong passwords = 15 minutes locked out).
//   * CSRF: requests whose Origin / Referer is another site are refused (like the API), and every POST also needs the X-CSRF-Token
//     header - an HMAC of the session only this server can make, handed to the page after sign-in.
//   * CSP: the page, its script and its stylesheet are served from here only (no external scripts, fonts or images; no inline script),
//     `frame-ancestors 'none'`, no-store, nosniff.
//   * Torrents added here (magnet or .torrent upload) go straight into the list without the Add window and wait for the VPN like any
//     other; search uses your own indexers (1.3, with the 1.4 local-server exception); "Connect VPN" asks Hallow VPN.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HallowTorrents
{
    public class WebUi
    {
        public const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        readonly QbitApi api; readonly Func<TorSettings> settings;
        Core Core { get { return api.Core; } }
        TorSettings S { get { return settings(); } }
        public Func<string> ConnectVpn = () => "Hallow VPN isn't reachable from here.";   // null = asked
        public Func<string> Game = () => null;
        public Func<string> VpnText = () => null;
        readonly byte[] secret = new byte[32];
        public WebUi(QbitApi api, Func<TorSettings> settings)
        {
            this.api = api; this.settings = settings;
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(secret);
        }
        public bool Enabled { get { return S.WebUiOn; } }
        public string Csrf(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return null;
            using (var h = new HMACSHA256(secret)) return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes("csrf:" + sid))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
        static bool SameToken(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
        }

        // ---- static files (embedded resources; no data in them)
        static readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
        static byte[] Res(string name)
        {
            lock (files)
            {
                byte[] b; if (files.TryGetValue(name, out b)) return b;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowTorrents.WebUi." + name))
                {
                    if (s == null) return null; var ms = new MemoryStream(); s.CopyTo(ms); b = ms.ToArray();
                }
                files[name] = b; return b;
            }
        }
        static ApiResponse Secure(ApiResponse r)
        {
            r.Headers["Content-Security-Policy"] = Csp;
            r.Headers["Cross-Origin-Opener-Policy"] = "same-origin"; r.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            r.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return r;
        }
        static ApiResponse Static(string name, string type) { var b = Res(name); return Secure(b == null ? ApiResponse.Text(404, "Not Found") : new ApiResponse { Type = type, Body = b }); }
        static ApiResponse J(object o, int code = 200) { var r = ApiResponse.Json(o); r.Code = code; return Secure(r); }
        static ApiResponse Err(int code, string message) { return J(new Dictionary<string, object> { { "ok", false }, { "error", message } }, code); }
        static ApiResponse Done(object extra = null)
        {
            var d = new Dictionary<string, object> { { "ok", true } };
            var e = extra as Dictionary<string, object>; if (e != null) foreach (var kv in e) d[kv.Key] = kv.Value;
            return J(d);
        }

        // path starts with "/ui"
        public ApiResponse Handle(ApiRequest r)
        {
            if (!Enabled) return Secure(ApiResponse.Text(404, "Not Found"));
            var path = r.Path;
            if (path == "/ui") { var red = Secure(ApiResponse.Text(302, "")); red.Headers["Location"] = "/ui/"; return red; }
            if (r.Method == "GET" || r.Method == "HEAD")
            {
                if (path == "/ui/" || path == "/ui/index.html") return Static("index.html", "text/html; charset=UTF-8");
                if (path == "/ui/app.js") return Static("app.js", "text/javascript; charset=UTF-8");
                if (path == "/ui/app.css") return Static("app.css", "text/css; charset=UTF-8");
                if (path == "/ui/icon.svg") return Static("icon.svg", "image/svg+xml");
            }
            if (!path.StartsWith("/ui/api/", StringComparison.Ordinal)) return Secure(ApiResponse.Text(404, "Not Found"));
            if (QbitApi.CrossSite(r)) return Err(401, "Refused: the request came from another site.");
            var name = path.Substring(8);
            var sid = r.Cookie("SID");
            if (name == "session")
            {
                bool inn = api.Guard.Valid(sid);
                return J(new Dictionary<string, object> { { "signedIn", inn }, { "csrf", inn ? Csrf(sid) : null }, { "version", Program.Version } });
            }
            if (name == "login")
            {
                if (r.Method != "POST") return Err(405, "Method Not Allowed");
                string newSid, why; var code = api.SignIn(r, out newSid, out why);
                if (newSid == null) return Err(code, why);
                var res = J(new Dictionary<string, object> { { "ok", true }, { "csrf", Csrf(newSid) } });
                res.SetCookie = "SID=" + newSid + "; HttpOnly; SameSite=Strict; path=/"; return res;
            }
            if (!api.Guard.Valid(sid)) return Err(403, "Sign in first.");
            bool post = r.Method == "POST";
            if (post && !SameToken(r.Header("X-CSRF-Token"), Csrf(sid))) return Err(403, "Refused: the page's security token is missing or wrong - reload the page.");
            switch (name)
            {
                case "list": return J(RemoteApi.List(Core, Game()));
                case "stats": return J(RemoteApi.Stats(Core));
                case "status": return J(Status());
                case "categories": return J(S.Categories.ToList().Select(c => c.Name).ToList());
            }
            if (!post) return Err(405, "Use POST.");
            switch (name)
            {
                case "logout": api.Guard.End(sid); { var res = Done(); res.SetCookie = "SID=; HttpOnly; SameSite=Strict; path=/; Max-Age=0"; return res; }
                case "add": return Add(r);
                case "pause": case "resume":
                    {
                        var h = (r.Arg("hash") ?? "").Trim();
                        if (h == "all") { if (name == "pause") Core.PauseAll(); else Core.ResumeAll(); return Done(); }
                        var a = RemoteApi.PauseResume(Core, h, name == "pause");
                        return a.StartsWith("ok") ? Done() : Err(404, a.Substring(6));
                    }
                case "category":
                    {
                        string err; var x = RemoteApi.Find(Core, r.Arg("hash"), out err); if (x == null) return Err(404, err);
                        var cat = (r.Arg("category") ?? "").Trim(); if (cat.Length > 0 && S.Cat(cat) == null) return Err(400, "No category called " + cat + ".");
                        Core.SetCategory(x.Id, cat.Length == 0 ? "" : S.Cat(cat).Name); return Done();
                    }
                case "search": return Search(r);
                case "search-add": return SearchAdd(r);
                case "connect-vpn": { string e; try { e = ConnectVpn(); } catch (Exception ex) { e = ex.Message; } return e == null ? Done() : Err(502, e); }
            }
            return Err(404, "Not Found");
        }

        Dictionary<string, object> Status()
        {
            return new Dictionary<string, object> {
                { "version", Program.Version }, { "vpnRequired", true }, { "vpnLocked", !Core.EngineUp }, { "vpn", VpnText() }, { "held", Core.Hold }, { "game", Game() },
                { "downBps", Core.TotalDown }, { "upBps", Core.TotalUp }, { "indexers", api.Search == null ? 0 : api.Search.FeedCount } };
        }

        ApiResponse Add(ApiRequest r)
        {
            var cat = (r.Arg("category") ?? "").Trim(); if (cat.Length > 0) { if (S.Cat(cat) == null) return Err(400, "No category called " + cat + "."); cat = S.Cat(cat).Name; } else cat = null;
            string err; Entry e = null;
            var file = r.Files.FirstOrDefault(f => f.Field == "file");
            if (file != null)
            {
                if (file.Data == null || file.Data.Length == 0 || file.Data.Length > 10 * 1024 * 1024 || file.Data[0] != (byte)'d') return Err(400, "That isn't a .torrent file.");
                e = Core.AddTorrentBytes(file.Data, null, cat, true, out err);
            }
            else
            {
                var m = (r.Arg("magnet") ?? "").Trim();
                if (!m.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return Err(400, "Only magnet links (magnet:?...) or .torrent files can be added.");
                e = Core.AddMagnet(m, null, cat, true, out err);
            }
            if (e == null) return Err(400, err ?? "It couldn't be added.");
            return Done(new Dictionary<string, object> { { "hash", e.Id }, { "name", e.Name }, { "category", e.Category }, { "vpnLocked", !Core.EngineUp } });
        }

        ApiResponse Search(ApiRequest r)
        {
            var se = api.Search; if (se == null) return Err(503, "Search isn't available.");
            var text = (r.Arg("q") ?? "").Trim(); var cat = (r.Arg("cat") ?? "").Trim().ToLowerInvariant(); int n; if (!int.TryParse(r.Arg("n"), out n)) n = 50; n = Math.Max(1, Math.Min(100, n));
            if (text.Length == 0) return Err(400, "Type what to look for.");
            if (cat.Length > 0 && Torznab.CatParam(cat) == null) return Err(400, "No search category called " + cat + ".");
            var o = se.Run(SearchQuery.From(text, cat), DateTime.UtcNow).Result;
            var d = SearchEngine.Json(o, n); d["error"] = o.Error; d["ok"] = o.Error == null;
            return J(d);
        }
        ApiResponse SearchAdd(ApiRequest r)
        {
            var se = api.Search; if (se == null) return Err(503, "Search isn't available.");
            var res = se.ByToken((r.Arg("id") ?? "").Trim()); if (res == null) return Err(404, "That result is gone - search again.");
            var cat = (r.Arg("category") ?? "").Trim(); if (cat.Length > 0 && S.Cat(cat) == null) return Err(400, "No category called " + cat + ".");
            string err; var e = se.Download(res, cat.Length > 0 ? S.Cat(cat).Name : (S.Cat(res.Category) != null ? S.Cat(res.Category).Name : null), null, true, out err);
            if (e == null) return Err(400, err ?? "It couldn't be added.");
            return Done(new Dictionary<string, object> { { "hash", e.Id }, { "name", e.Name }, { "category", e.Category }, { "vpnLocked", !Core.EngineUp } });
        }
    }
}
