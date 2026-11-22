// Hallow Torrents 1.2 - a qBittorrent-compatible Web API (v2), so Radarr, Sonarr and Prowlarr can add Hallow Torrents as "qBittorrent".
//   * Off by default. Listens on 127.0.0.1 unless you pick a home-network or Tailscale address - never 0.0.0.0, never a public
//     address and never the VPN's address. Requests from public (internet) addresses are always refused; an optional allow-list
//     narrows it further.
//   * A sign-in is required (qBittorrent's SID cookie). It expires after N minutes unused. 5 wrong passwords in 15 minutes lock that
//     address out for 15 minutes. A request whose Origin / Referer is another site gets 401 (like qBittorrent's CSRF check).
//   * It's only a front end to Core: torrents added here wait for the VPN like any other (shown as "queuedDL" while it's off, so the
//     apps don't mark them failed); a .torrent URL is fetched over the VPN only (VpnFetch), later if the VPN is off right now.
//   * Finished downloads are reported where they are (content_path) and are never moved before the app imports them; a download
//     shows as finished only after Defender's scan, and a flagged one is reported as "error" so it isn't imported.
//   * Hand-written HTTP/1.1 on a TcpListener: HttpListener would need admin rights for anything but localhost.
// Extension points for 1.3 / 1.4: QbitApi.Routes (add endpoints), ApiServer (any front end - a phone / web UI can reuse it).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents
{
    // ---------------- the password: a salted PBKDF2 hash only
    public static class ApiAuth
    {
        const int Iterations = 20000;
        public static string Hash(string password)
        {
            var salt = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(salt);
            using (var k = new Rfc2898DeriveBytes(password ?? "", salt, Iterations)) return "pbkdf2$" + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(k.GetBytes(32));
        }
        public static bool Check(string password, string stored)
        {
            try
            {
                var p = (stored ?? "").Split('$'); if (p.Length != 4 || p[0] != "pbkdf2") return false;
                int it = int.Parse(p[1], CultureInfo.InvariantCulture); var salt = Convert.FromBase64String(p[2]); var want = Convert.FromBase64String(p[3]);
                byte[] got; using (var k = new Rfc2898DeriveBytes(password ?? "", salt, it)) got = k.GetBytes(want.Length);
                int diff = 0; for (int i = 0; i < want.Length; i++) diff |= want[i] ^ got[i];
                return diff == 0;
            }
            catch { return false; }
        }
        public static string NewSid() { var b = new byte[24]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return Convert.ToBase64String(b).Replace('+', 'A').Replace('/', 'B').TrimEnd('='); }
    }

    // ---------------- who may connect, sign-ins and the lock-out (pure parts tested)
    public class ApiGuard
    {
        // home network, Tailscale (100.64.0.0/10), link-local and this PC - never a public address
        public static bool IsLocalAddress(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6) { var b = a.GetAddressBytes(); return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC; }
            return false;
        }
        // why an address can't be listened on (null = fine)
        public static string BindProblem(IPAddress ip, IPAddress vpnIp)
        {
            if (ip == null) return "That isn't an address.";
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return "Pick one address - it never listens on all of them (0.0.0.0).";
            if (!IsLocalAddress(ip)) return "Only this PC (127.0.0.1), a home-network or a Tailscale address can be used - never a public one.";
            if (vpnIp != null && ip.Equals(vpnIp)) return "That's the VPN's address - the Web API is never put on the VPN.";
            return null;
        }
        // "192.168.1.20, 10.0.0.0/8, fd7a::1" -> ranges; error = the first entry that isn't an address or range
        public class Range { public byte[] Net; public int Bits; public bool Has(IPAddress a) { if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4(); var b = a.GetAddressBytes(); if (b.Length != Net.Length) return false; for (int i = 0; i < Bits; i++) { int m = 0x80 >> (i % 8); if ((b[i / 8] & m) != (Net[i / 8] & m)) return false; } return true; } }
        public static List<Range> ParseAllow(string text, out string error)
        {
            error = null; var r = new List<Range>();
            foreach (var raw in (text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim(); var slash = part.IndexOf('/'); IPAddress ip; int bits = -1;
                if (!IPAddress.TryParse(slash < 0 ? part : part.Substring(0, slash), out ip) || (slash >= 0 && !int.TryParse(part.Substring(slash + 1), out bits))) { error = part; continue; }
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128; if (bits < 0) bits = max; if (bits > max) { error = part; continue; }
                r.Add(new Range { Net = ip.GetAddressBytes(), Bits = bits });
            }
            return r;
        }
        readonly Func<TorSettings> settings;
        public ApiGuard(Func<TorSettings> settings) { this.settings = settings; }
        public bool RemoteAllowed(IPAddress remote)
        {
            if (!IsLocalAddress(remote)) return false;
            string err; var list = ParseAllow(settings().ApiAllow, out err);
            return list.Count == 0 && string.IsNullOrWhiteSpace(settings().ApiAllow) ? true : list.Any(x => x.Has(remote));
        }

        // sign-ins: SID -> last used
        readonly ConcurrentDictionary<string, DateTime> sessions = new ConcurrentDictionary<string, DateTime>();
        public TimeSpan? SessionTtlOverride;   // tests
        TimeSpan Ttl { get { return SessionTtlOverride ?? TimeSpan.FromMinutes(Math.Max(1, settings().ApiSessionMinutes)); } }
        public string NewSession()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in sessions) if (now - kv.Value > Ttl) { DateTime d; sessions.TryRemove(kv.Key, out d); }
            while (sessions.Count >= 50) { var old = sessions.OrderBy(kv => kv.Value).First(); DateTime d; sessions.TryRemove(old.Key, out d); }
            var sid = ApiAuth.NewSid(); sessions[sid] = now; return sid;
        }
        public bool Valid(string sid)
        {
            DateTime last; if (string.IsNullOrEmpty(sid) || !sessions.TryGetValue(sid, out last)) return false;
            if (DateTime.UtcNow - last > Ttl) { sessions.TryRemove(sid, out last); return false; }
            sessions[sid] = DateTime.UtcNow; return true;
        }
        public void End(string sid) { DateTime d; if (sid != null) sessions.TryRemove(sid, out d); }
        public void EndAll() { sessions.Clear(); }
        public int Sessions { get { return sessions.Count(kv => DateTime.UtcNow - kv.Value <= Ttl); } }

        // the lock-out: 5 wrong passwords within 15 minutes -> 15 minutes banned
        public const int MaxFails = 5; public static readonly TimeSpan Window = TimeSpan.FromMinutes(15), BanTime = TimeSpan.FromMinutes(15);
        readonly Dictionary<string, List<DateTime>> fails = new Dictionary<string, List<DateTime>>(); readonly Dictionary<string, DateTime> banned = new Dictionary<string, DateTime>();
        public bool Banned(IPAddress ip) { lock (fails) { DateTime until; if (banned.TryGetValue(ip.ToString(), out until)) { if (DateTime.UtcNow < until) return true; banned.Remove(ip.ToString()); } return false; } }
        public void Failed(IPAddress ip)
        {
            lock (fails)
            {
                var k = ip.ToString(); List<DateTime> l; if (!fails.TryGetValue(k, out l)) fails[k] = l = new List<DateTime>();
                var now = DateTime.UtcNow; l.RemoveAll(t => now - t > Window); l.Add(now);
                if (l.Count >= MaxFails) { banned[k] = now + BanTime; l.Clear(); }
            }
        }
        public void Succeeded(IPAddress ip) { lock (fails) fails.Remove(ip.ToString()); }
        public void ResetBans() { lock (fails) { fails.Clear(); banned.Clear(); } }
    }

    public class ApiFile { public string Field, FileName; public byte[] Data; }
    public class ApiRequest
    {
        public string Method = "GET", Path = "/", Version = "HTTP/1.1";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Args = new Dictionary<string, string>(StringComparer.Ordinal);   // query + form (form wins)
        public List<ApiFile> Files = new List<ApiFile>();
        public IPAddress Remote = IPAddress.Loopback;
        public byte[] Body = new byte[0];
        public string Header(string n) { string v; return Headers.TryGetValue(n, out v) ? v : null; }
        public string Arg(string n) { string v; return Args.TryGetValue(n, out v) ? v : null; }
        public string Cookie(string name)
        {
            foreach (var part in (Header("Cookie") ?? "").Split(';')) { var i = part.IndexOf('='); if (i > 0 && part.Substring(0, i).Trim() == name) return part.Substring(i + 1).Trim(); }
            return null;
        }
    }
    public class ApiResponse
    {
        public int Code = 200; public string Type = "text/plain; charset=UTF-8"; public byte[] Body = new byte[0]; public string SetCookie;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 1.4: CSP, Location...
        public static ApiResponse Text(int code, string text) { return new ApiResponse { Code = code, Body = Encoding.UTF8.GetBytes(text ?? "") }; }
        public static ApiResponse Json(object o) { return new ApiResponse { Type = "application/json", Body = Encoding.UTF8.GetBytes(Family.Json(o)) }; }
    }

    // ---------------- request parsing (pure, tested)
    public static class ApiHttp
    {
        public static void ParseUrlEncoded(string s, Dictionary<string, string> into)
        {
            foreach (var pair in (s ?? "").Split('&'))
            {
                if (pair.Length == 0) continue; var i = pair.IndexOf('=');
                var k = WebUtility.UrlDecode(i < 0 ? pair : pair.Substring(0, i)); var v = i < 0 ? "" : WebUtility.UrlDecode(pair.Substring(i + 1));
                into[k] = v;
            }
        }
        static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = Math.Max(0, from); i <= hay.Length - needle.Length; i++)
            {
                int j = 0; while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
        public static string Boundary(string contentType)
        {
            foreach (var p in (contentType ?? "").Split(';')) { var t = p.Trim(); if (t.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) return t.Substring(9).Trim('"'); }
            return null;
        }
        // multipart/form-data: text fields into args, file parts into files
        public static void ParseMultipart(byte[] body, string boundary, Dictionary<string, string> args, List<ApiFile> files)
        {
            var delim = Encoding.ASCII.GetBytes("--" + boundary); var crlf2 = Encoding.ASCII.GetBytes("\r\n\r\n");
            int pos = IndexOf(body, delim, 0);
            while (pos >= 0)
            {
                int start = pos + delim.Length;
                if (start + 2 <= body.Length && body[start] == '-' && body[start + 1] == '-') break;   // the closing boundary
                start += 2;   // CRLF after the boundary
                int headEnd = IndexOf(body, crlf2, start); if (headEnd < 0) break;
                var head = Encoding.UTF8.GetString(body, start, headEnd - start);
                int dataStart = headEnd + 4;
                int next = IndexOf(body, Encoding.ASCII.GetBytes("\r\n--" + boundary), dataStart); if (next < 0) break;
                var data = new byte[next - dataStart]; Buffer.BlockCopy(body, dataStart, data, 0, data.Length);
                string name = null, filename = null;
                foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase)) continue;
                    name = DispValue(line, "name"); filename = DispValue(line, "filename");
                }
                if (name != null)
                {
                    if (filename != null) files.Add(new ApiFile { Field = name, FileName = filename, Data = data });
                    else args[name] = Encoding.UTF8.GetString(data);
                }
                pos = next + 2;
            }
        }
        static string DispValue(string line, string key)
        {
            foreach (var p in line.Split(';'))
            {
                var t = p.Trim(); if (!t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
                return t.Substring(key.Length + 1).Trim().Trim('"');
            }
            return null;
        }
        public static long Unix(string localTime)
        {
            DateTime t; if (string.IsNullOrEmpty(localTime) || !DateTime.TryParse(localTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t)) return -1;
            return (long)(t.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
        public static string Reason(int code)
        {
            switch (code) { case 200: return "OK"; case 302: return "Found"; case 400: return "Bad Request"; case 401: return "Unauthorized"; case 403: return "Forbidden"; case 404: return "Not Found"; case 405: return "Method Not Allowed"; case 409: return "Conflict"; case 413: return "Payload Too Large"; case 415: return "Unsupported Media Type"; default: return code >= 500 ? "Internal Server Error" : "OK"; }
        }
    }

    // ---------------- the qBittorrent v2 endpoints Radarr / Sonarr / Prowlarr use (and a few more), on top of Core
    public class QbitApi
    {
        public const string QbitVersion = "v4.6.7", WebApiVersion = "2.9.3";
        public readonly Core Core; readonly Func<TorSettings> settings; readonly Action<Action> change; public readonly ApiGuard Guard;
        TorSettings S { get { return settings(); } }
        public event Action<string> Log;
        void L(string s) { var h = Log; if (h != null) try { h("webapi: " + s); } catch { } }
        // change: runs a settings change (new category) where the settings live (the UI thread) and saves them
        public QbitApi(Core core, Func<TorSettings> settings, Action<Action> change)
        {
            Core = core; this.settings = settings; this.change = change ?? (a => { a(); settings().Fix(); settings().Save(); }); Guard = new ApiGuard(settings);
        }

        // .torrent URLs that arrived while the VPN was off: fetched over the VPN once it's back (FetchPending, every few seconds)
        class Pending { public string Url, Category, Save, Tags; public bool Start, Sequential; public double? Ratio; public int? Minutes; public int Tries; public DateTime Since; }
        readonly List<Pending> pending = new List<Pending>();
        public int PendingCount { get { lock (pending) return pending.Count; } }
        public Func<string, byte[]> FetchUrl;   // tests can swap it; by default over the VPN only
        public SearchEngine Search;             // 1.4: your indexers (the local-server exception for their download links) and the web UI's search
        public WebUi Ui;                        // 1.4: the web interface at /ui/ (null = not served)

        public ApiResponse Handle(ApiRequest r)
        {
            try
            {
                // 1.4: the web interface (same listener, same sign-in, CSRF and CSP - WebUi.cs)
                if (Ui != null && (r.Path == "/ui" || r.Path.StartsWith("/ui/", StringComparison.Ordinal))) return Ui.Handle(r);
                if (!r.Path.StartsWith("/api/v2/", StringComparison.Ordinal))
                {
                    if (r.Path == "/" && Ui != null && Ui.Enabled) { var red = ApiResponse.Text(302, ""); red.Headers["Location"] = "/ui/"; return red; }
                    return r.Path == "/" ? ApiResponse.Text(200, "Hallow Torrents - qBittorrent-compatible Web API v2 for Radarr, Sonarr and Prowlarr. The web interface (/ui/) is switched off.") : ApiResponse.Text(404, "Not Found");
                }
                // CSRF: a browser page from another site can't drive it (qBittorrent does the same)
                if (CrossSite(r)) return ApiResponse.Text(401, "Unauthorized");
                var path = r.Path.Substring(8);
                if (path == "auth/login") return Login(r);
                var sid = r.Cookie("SID");
                if (!Guard.Valid(sid)) return ApiResponse.Text(403, "Forbidden");
                return Route(path, r, sid);
            }
            catch (Exception e) { L("error " + r.Path + ": " + e.Message); return ApiResponse.Text(500, "Internal Server Error"); }
        }

        // a request whose Origin / Referer is another site (qBittorrent's CSRF check; the web interface uses it too)
        public static bool CrossSite(ApiRequest r)
        {
            var origin = r.Header("Origin"); var referer = r.Header("Referer"); var host = r.Header("Host") ?? "";
            var from = !string.IsNullOrEmpty(origin) && origin != "null" ? origin : referer;
            if (string.IsNullOrEmpty(from)) return false;
            Uri u; return !Uri.TryCreate(from, UriKind.Absolute, out u) || !string.Equals(u.Authority, host, StringComparison.OrdinalIgnoreCase);
        }
        ApiResponse Login(ApiRequest r)
        {
            if (r.Method != "POST") return ApiResponse.Text(405, "Method Not Allowed");
            string sid, why; var code = SignIn(r, out sid, out why);
            if (sid == null) return code == 403 ? ApiResponse.Text(403, why) : ApiResponse.Text(200, "Fails.");
            var res = ApiResponse.Text(200, "Ok."); res.SetCookie = "SID=" + sid + "; HttpOnly; SameSite=Strict; path=/";
            return res;
        }
        // the one sign-in check (API and web interface): the lock-out, the hash, a new session. sid = null when it failed.
        public int SignIn(ApiRequest r, out string sid, out string why)
        {
            sid = null; why = null;
            if (Guard.Banned(r.Remote)) { why = "Your IP address has been banned after too many failed authentication attempts."; return 403; }
            var s = S; var user = r.Arg("username") ?? ""; var pass = r.Arg("password") ?? "";
            bool ok = !string.IsNullOrWhiteSpace(s.ApiUser) && !string.IsNullOrEmpty(s.ApiPassHash) && string.Equals(user, s.ApiUser, StringComparison.Ordinal) & ApiAuth.Check(pass, s.ApiPassHash);
            if (!ok) { Guard.Failed(r.Remote); L("wrong sign-in from " + r.Remote); Thread.Sleep(300); why = "Wrong username or password."; return 401; }
            Guard.Succeeded(r.Remote);
            sid = Guard.NewSession(); L("signed in from " + r.Remote); return 200;
        }

        static readonly HashSet<string> postOnly = new HashSet<string> { "auth/logout", "app/setPreferences", "app/shutdown", "torrents/add", "torrents/delete", "torrents/pause", "torrents/resume", "torrents/stop", "torrents/start",
            "torrents/setCategory", "torrents/createCategory", "torrents/editCategory", "torrents/removeCategories", "torrents/setShareLimits", "torrents/topPrio", "torrents/bottomPrio", "torrents/increasePrio",
            "torrents/decreasePrio", "torrents/setForceStart", "torrents/recheck", "torrents/setLocation", "torrents/addTags", "torrents/removeTags", "torrents/createTags", "torrents/deleteTags", "torrents/toggleSequentialDownload",
            "torrents/setSuperSeeding", "torrents/reannounce" };
        static ApiResponse Ok() { return ApiResponse.Text(200, ""); }

        ApiResponse Route(string path, ApiRequest r, string sid)
        {
            if (postOnly.Contains(path) && r.Method != "POST") return ApiResponse.Text(405, "Method Not Allowed");
            switch (path)
            {
                case "auth/logout": Guard.End(sid); return Ok();
                case "app/version": return ApiResponse.Text(200, QbitVersion);
                case "app/webapiVersion": return ApiResponse.Text(200, WebApiVersion);
                case "app/buildInfo": return ApiResponse.Json(new Dictionary<string, object> { { "qt", "6.5.3" }, { "libtorrent", "2.0.10.0" }, { "boost", "1.83.0" }, { "openssl", "3.1.4" }, { "zlib", "1.3" }, { "bitness", 64 }, { "app", "Hallow Torrents " + Program.Version } });
                case "app/preferences": return ApiResponse.Json(Preferences());
                case "app/setPreferences": return Ok();   // the apps don't need it; Hallow's own settings stay in Hallow
                case "app/defaultSavePath": return ApiResponse.Text(200, S.SaveFolder);
                case "app/shutdown": return ApiResponse.Text(403, "Forbidden");
                case "transfer/info": return ApiResponse.Json(TransferInfo());
                case "sync/maindata": return ApiResponse.Json(MainData(r.Arg("rid")));
                case "torrents/info": return ApiResponse.Json(Info(r));
                case "torrents/properties": return Properties(r.Arg("hash"));
                case "torrents/files": return Files(r.Arg("hash"));
                case "torrents/trackers": return Trackers(r.Arg("hash"));
                case "torrents/categories": return ApiResponse.Json(Categories());
                case "torrents/tags": { var tags = new SortedSet<string>(StringComparer.Ordinal); foreach (var x in Core.All()) foreach (var t in TagList(x.Tags)) tags.Add(t); return ApiResponse.Json(tags.ToList()); }
                case "torrents/add": return Add(r);
                case "torrents/delete": { var del = Bool(r.Arg("deleteFiles")); foreach (var x in Pick(r)) { var err = Core.Remove(x.Id, del).Result; L("removed " + x.Id + (del ? " (files to the Recycle Bin)" : "") + (err != null ? ": " + err : "")); } return Ok(); }
                case "torrents/pause": case "torrents/stop": foreach (var x in Pick(r)) Core.Pause(x.Id); return Ok();
                case "torrents/resume": case "torrents/start": foreach (var x in Pick(r)) Core.Resume(x.Id); return Ok();
                case "torrents/setForceStart": if (Bool(r.Arg("value"))) foreach (var x in Pick(r)) Core.Resume(x.Id); return Ok();
                case "torrents/recheck": foreach (var x in Pick(r)) Core.Recheck(x.Id); return Ok();
                case "torrents/topPrio": foreach (var x in Pick(r).OrderByDescending(e => e.Queue)) Core.Move(x.Id, int.MinValue); return Ok();
                case "torrents/bottomPrio": foreach (var x in Pick(r).OrderBy(e => e.Queue)) Core.Move(x.Id, int.MaxValue); return Ok();
                case "torrents/increasePrio": foreach (var x in Pick(r)) Core.Move(x.Id, -1); return Ok();
                case "torrents/decreasePrio": foreach (var x in Pick(r)) Core.Move(x.Id, 1); return Ok();
                case "torrents/setShareLimits":
                    {
                        var ratio = Num(r.Arg("ratioLimit"), -2); var minutes = (int)Num(r.Arg("seedingTimeLimit"), -2);
                        foreach (var x in Pick(r)) Core.SetShareLimits(x.Id, ratio, minutes); return Ok();
                    }
                case "torrents/setCategory":
                    {
                        var cat = r.Arg("category") ?? "";
                        if (cat.Length > 0 && S.Cat(cat) == null) return ApiResponse.Text(409, "Incorrect category name");
                        foreach (var x in Pick(r)) Core.SetCategory(x.Id, cat.Length == 0 ? "" : S.Cat(cat).Name); return Ok();
                    }
                case "torrents/createCategory": case "torrents/editCategory":
                    {
                        var cat = (r.Arg("category") ?? "").Trim(); if (!ValidCategory(cat)) return ApiResponse.Text(cat.Length == 0 ? 400 : 409, "Invalid category name");
                        var save = r.Arg("savePath"); if (save != null && save.Trim().Length > 0 && !Path.IsPathRooted(save.Trim())) save = null;
                        if (path == "torrents/editCategory" && S.Cat(cat) == null) return ApiResponse.Text(409, "Category doesn't exist");
                        EnsureCategory(cat, save, path == "torrents/editCategory"); return Ok();
                    }
                case "torrents/removeCategories":
                    {
                        var names = (r.Arg("categories") ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                        change(() => { foreach (var n in names) { var c = S.Cat(n); if (c != null && S.Categories.Count > 1) S.Categories.Remove(c); } });
                        foreach (var x in Core.All()) if (names.Any(n => string.Equals(n, x.Category, StringComparison.OrdinalIgnoreCase))) Core.SetCategory(x.Id, "");
                        return Ok();
                    }
                case "torrents/addTags": case "torrents/removeTags":
                    {
                        var tags = TagList(r.Arg("tags"));
                        foreach (var x in Pick(r)) { var have = TagList(x.Tags); if (path == "torrents/addTags") have = have.Union(tags).ToList(); else have.RemoveAll(tags.Contains); x.Tags = string.Join(",", have); }
                        Core.Save(); return Ok();
                    }
                case "torrents/createTags": case "torrents/deleteTags": return Ok();
                case "torrents/toggleSequentialDownload": foreach (var x in Pick(r)) Core.SetFlags(x.Id, !x.Sequential, x.SuperSeed).Wait(5000); return Ok();
                case "torrents/setSuperSeeding": foreach (var x in Pick(r)) Core.SetFlags(x.Id, x.Sequential, Bool(r.Arg("value"))).Wait(5000); return Ok();
                case "torrents/reannounce": return Ok();
                case "torrents/setLocation":
                    {
                        var loc = r.Arg("location"); if (string.IsNullOrWhiteSpace(loc) || !Path.IsPathRooted(loc.Trim())) return ApiResponse.Text(400, "Save path is empty");
                        foreach (var x in Pick(r)) { var err = Core.MoveStorage(x.Id, loc.Trim()).Result; if (err != null && err != "It's already there.") return ApiResponse.Text(409, err); }
                        return Ok();
                    }
            }
            return ApiResponse.Text(404, "Not Found");
        }

        // ---- helpers
        static bool Bool(string v) { return v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"); }
        static double Num(string v, double dflt) { double d; return v != null && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : dflt; }
        static List<string> TagList(string s) { return (s ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList(); }
        List<Entry> Pick(ApiRequest r)
        {
            var h = r.Arg("hashes") ?? r.Arg("hash") ?? "";
            var all = Core.All(); if (h.Trim() == "all") return all;
            var want = new HashSet<string>(h.Split('|').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
            return all.Where(x => want.Contains(x.Id)).ToList();
        }
        public static bool ValidCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name != name.Trim()) return false;
            if (name.StartsWith("/") || name.EndsWith("/") || name.Contains("//")) return false;
            return name.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|', '\r', '\n', '\t' }) < 0;
        }
        // a new category for the apps (qBittorrent creates one when a torrent is added with it): no library hand-off, its own folder if given
        void EnsureCategory(string name, string save, bool edit = false)
        {
            change(() =>
            {
                var c = S.Cat(name);
                if (c == null) { S.Categories.Add(new Category { Name = name, SaveFolder = string.IsNullOrWhiteSpace(save) ? null : save.Trim(), Naming = "keep" }); L("new category " + name); }
                else if (edit || (!string.IsNullOrWhiteSpace(save) && string.IsNullOrWhiteSpace(c.SaveFolder))) c.SaveFolder = string.IsNullOrWhiteSpace(save) ? null : save.Trim();
            });
        }
        Dictionary<string, object> Categories()
        {
            var d = new Dictionary<string, object>();
            foreach (var c in S.Categories.ToList()) d[c.Name] = new Dictionary<string, object> { { "name", c.Name }, { "savePath", c.SaveFolder ?? "" } };
            return d;
        }
        Dictionary<string, object> Preferences()
        {
            var s = S;
            return new Dictionary<string, object> {
                { "locale", "en" }, { "save_path", s.SaveFolder }, { "temp_path_enabled", false }, { "temp_path", "" }, { "auto_tmm_enabled", false }, { "torrent_changed_tmm_enabled", false },
                { "torrent_content_layout", "Original" }, { "create_subfolder_enabled", true }, { "start_paused_enabled", false }, { "preallocate_all", false }, { "incomplete_files_ext", false },
                { "queueing_enabled", true }, { "max_active_downloads", s.MaxActive }, { "max_active_torrents", -1 }, { "max_active_uploads", -1 }, { "dont_count_slow_torrents", false },
                { "max_ratio_enabled", s.RatioLimit > 0 }, { "max_ratio", s.RatioLimit > 0 ? s.RatioLimit : -1 }, { "max_ratio_act", 0 },
                { "max_seeding_time_enabled", s.SeedMinutes > 0 }, { "max_seeding_time", s.SeedMinutes > 0 ? s.SeedMinutes : -1 },
                { "max_inactive_seeding_time_enabled", false }, { "max_inactive_seeding_time", -1 },
                { "dht", s.Dht }, { "pex", s.Pex }, { "lsd", s.Lpd }, { "encryption", s.Encryption == "require" ? 1 : 0 }, { "anonymous_mode", false },
                { "listen_port", s.Port }, { "upnp", false }, { "random_port", false }, { "dl_limit", s.DownKBps * 1024 }, { "up_limit", s.UpKBps * 1024 },
                { "scheduler_enabled", s.ScheduleOn }, { "web_ui_address", s.ApiBind }, { "web_ui_port", s.ApiPort }, { "web_ui_username", s.ApiUser ?? "" },
                { "bypass_local_auth", false }, { "bypass_auth_subnet_whitelist_enabled", false }, { "web_ui_session_timeout", s.ApiSessionMinutes * 60 }, { "web_ui_max_auth_fail_count", ApiGuard.MaxFails },
                { "web_ui_ban_duration", (int)ApiGuard.BanTime.TotalSeconds }, { "web_ui_csrf_protection_enabled", true }, { "use_https", false }, { "export_dir", "" }, { "export_dir_fin", "" },
                { "proxy_type", 0 }, { "hallow_vpn_required", true } };
        }
        Dictionary<string, object> TransferInfo()
        {
            var st = Core.Stats;
            return new Dictionary<string, object> { { "dl_info_speed", Core.TotalDown }, { "dl_info_data", st == null ? 0 : st.SessionDown }, { "up_info_speed", Core.TotalUp }, { "up_info_data", st == null ? 0 : st.SessionUp },
                { "dl_rate_limit", Core.LimitDown * 1024 }, { "up_rate_limit", Core.LimitUp * 1024 }, { "dht_nodes", 0 }, { "connection_status", Core.EngineUp ? "connected" : "disconnected" }, { "hallow_vpn_locked", !Core.EngineUp } };
        }

        // Hallow's state -> qBittorrent's state string (pure, tested). While the VPN is off / a game runs / it waits in the queue:
        // "queuedDL" (the apps show it as queued, never failed). Finished: only after Defender's scan; a flagged or infected one is "error".
        public static string QState(TorrentView v, Entry x)
        {
            if (v.State == "Error") return "error";
            if (v.Complete)
            {
                if (x != null && (x.Scan == "threats" || (x.Flags != null && x.Flags.Count > 0))) return "error";
                if (x != null && (x.Scan == "waiting" || x.Scan == "scanning")) return "checkingUP";
                if (x != null && x.LimitHit && v.State != "Seeding") return "pausedUP";
                switch (v.State)
                {
                    case "Paused": case "Done (limit reached)": case "Done": return "pausedUP";
                    case "Checking": return "checkingUP";
                    case "Seeding": return v.UpRate > 0 ? "uploading" : "stalledUP";
                    default: return "queuedUP";
                }
            }
            switch (v.State)
            {
                case "Paused": return "pausedDL";
                case "Getting info": return "metaDL";
                case "Checking": return "checkingDL";
                case "Downloading": return v.DownRate > 0 ? "downloading" : "stalledDL";
                default: return "queuedDL";   // waiting for the VPN, paused for a game, queued, starting
            }
        }
        static bool InFilter(string filter, string st)
        {
            switch ((filter ?? "all").ToLowerInvariant())
            {
                case "downloading": return st.EndsWith("DL") || st == "downloading";
                case "seeding": return st == "uploading" || st == "stalledUP" || st == "queuedUP" || st == "forcedUP";
                case "completed": return st.EndsWith("UP") || st == "uploading";
                case "paused": case "stopped": return st.StartsWith("paused") || st.StartsWith("stopped");
                case "resumed": case "running": return !st.StartsWith("paused") && !st.StartsWith("stopped");
                case "active": return st == "downloading" || st == "uploading" || st == "metaDL";
                case "inactive": return st != "downloading" && st != "uploading" && st != "metaDL";
                case "stalled": return st.StartsWith("stalled");
                case "stalled_uploading": return st == "stalledUP";
                case "stalled_downloading": return st == "stalledDL";
                case "checking": return st.StartsWith("checking");
                case "errored": return st == "error" || st == "missingFiles";
                default: return true;
            }
        }
        double RatioLimitOf(Entry x) { if (x.ShareRatio.HasValue) return x.ShareRatio.Value; var c = S.Cat(x.Category); return c != null && c.RatioGoal > 0 ? c.RatioGoal : -2; }
        public Dictionary<string, object> Torrent(TorrentView v, Entry x)
        {
            var s = S; var st = QState(v, x);
            long total = x.Size; long wanted = 0; var files = x.FileList;
            if (files.Count > 0) { total = files.Sum(f => f.Length); for (int i = 0; i < files.Count; i++) if (x.PrioOf(i) > 0) wanted += files[i].Length; } else wanted = total;
            double prog = v.Complete ? 1.0 : Math.Max(0, Math.Min(1, v.Progress / 100.0));
            long left = v.Complete ? 0 : (long)(wanted * (1 - prog));
            long eta = !v.Complete && v.Eta.HasValue && v.Eta.Value.TotalSeconds < 8640000 ? (long)v.Eta.Value.TotalSeconds : 8640000;
            var added = ApiHttp.Unix(x.AddedAt); var completed = ApiHttp.Unix(x.CompletedAt);
            var goal = x.ShareRatio ?? Core.GoalFor(x);
            var content = Core.RootOf(x.Id) ?? Path.Combine(x.SavePath ?? "", x.Name ?? "");
            bool active = v.DownRate > 0 || v.UpRate > 0;
            return new Dictionary<string, object> {
                { "hash", x.Id }, { "infohash_v1", x.Id.Length == 40 ? x.Id : "" }, { "infohash_v2", x.Id.Length == 64 ? x.Id : "" }, { "name", x.Name ?? "" },
                { "size", wanted }, { "total_size", total }, { "progress", prog }, { "dlspeed", v.DownRate }, { "upspeed", v.UpRate }, { "eta", eta }, { "state", st },
                { "category", x.Category ?? "" }, { "tags", string.Join(", ", TagList(x.Tags)) }, { "save_path", x.SavePath ?? "" }, { "download_path", "" }, { "content_path", content },
                { "added_on", added }, { "completion_on", completed }, { "last_activity", active ? (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds : Math.Max(added, completed) },
                { "seen_complete", completed }, { "amount_left", left }, { "completed", Math.Max(0, wanted - left) }, { "downloaded", x.Down }, { "uploaded", x.Up },
                { "downloaded_session", x.Down }, { "uploaded_session", x.Up }, { "ratio", Math.Round(v.Ratio, 4) },
                { "ratio_limit", RatioLimitOf(x) }, { "seeding_time_limit", x.ShareMinutes.HasValue ? (long)x.ShareMinutes.Value : -2L }, { "inactive_seeding_time_limit", -2 },
                { "max_ratio", goal > 0 || (x.ShareRatio.HasValue && x.ShareRatio.Value >= 0) ? goal : -1 }, { "max_seeding_time", x.ShareMinutes ?? (s.SeedMinutes > 0 ? s.SeedMinutes : -1) }, { "max_inactive_seeding_time", -1 },
                { "seeding_time", (long)x.SeedSecs }, { "time_active", (long)x.SeedSecs }, { "num_seeds", v.Seeds }, { "num_leechs", v.Leechs }, { "num_complete", v.Seeds }, { "num_incomplete", v.Leechs },
                { "priority", v.Complete ? 0 : v.Queue }, { "force_start", false }, { "seq_dl", x.Sequential }, { "f_l_piece_prio", false }, { "super_seeding", x.SuperSeed }, { "auto_tmm", false },
                { "dl_limit", x.DownKBps > 0 ? x.DownKBps * 1024 : -1 }, { "up_limit", x.UpKBps > 0 ? x.UpKBps * 1024 : -1 }, { "availability", v.Complete ? -1.0 : (v.Seeds > 0 ? 1.0 : 0.0) },
                { "magnet_uri", x.Magnet ?? ("magnet:?xt=urn:btih:" + x.Id + "&dn=" + Uri.EscapeDataString(x.Name ?? "")) }, { "tracker", "" }, { "trackers_count", 0 }, { "private", false } };
        }
        List<KeyValuePair<TorrentView, Entry>> Views()
        {
            var r = new List<KeyValuePair<TorrentView, Entry>>();
            foreach (var v in Core.Snapshot(false)) { var x = Core.Get(v.Id); if (x != null) r.Add(new KeyValuePair<TorrentView, Entry>(v, x)); }
            return r;
        }
        List<object> Info(ApiRequest r)
        {
            var cat = r.Arg("category"); var tag = r.Arg("tag"); var filter = r.Arg("filter");
            var hashes = r.Arg("hashes"); var want = string.IsNullOrEmpty(hashes) ? null : new HashSet<string>(hashes.Split('|').Select(h => h.Trim().ToLowerInvariant()));
            var list = new List<Dictionary<string, object>>();
            foreach (var kv in Views())
            {
                var x = kv.Value;
                if (cat != null && !string.Equals(x.Category ?? "", cat, StringComparison.OrdinalIgnoreCase)) continue;
                if (tag != null && !(tag.Length == 0 ? TagList(x.Tags).Count == 0 : TagList(x.Tags).Contains(tag))) continue;
                if (want != null && !want.Contains(x.Id)) continue;
                var t = Torrent(kv.Key, x); if (!InFilter(filter, (string)t["state"])) continue;
                list.Add(t);
            }
            var sort = r.Arg("sort");
            if (!string.IsNullOrEmpty(sort) && list.Count > 0 && list[0].ContainsKey(sort)) { list = list.OrderBy(t => t[sort] is string ? (object)((string)t[sort]).ToLowerInvariant() : t[sort]).ToList(); if (Bool(r.Arg("reverse"))) list.Reverse(); }
            int off = (int)Num(r.Arg("offset"), 0), lim = (int)Num(r.Arg("limit"), 0);
            if (off < 0) off = Math.Max(0, list.Count + off);
            IEnumerable<Dictionary<string, object>> q = list.Skip(off); if (lim > 0) q = q.Take(lim);
            return q.Cast<object>().ToList();
        }
        int rid;
        Dictionary<string, object> MainData(string ridArg)
        {
            var torrents = new Dictionary<string, object>();
            foreach (var kv in Views()) torrents[kv.Value.Id] = Torrent(kv.Key, kv.Value);
            var tags = new SortedSet<string>(StringComparer.Ordinal); foreach (var x in Core.All()) foreach (var t in TagList(x.Tags)) tags.Add(t);
            var info = TransferInfo(); info["free_space_on_disk"] = FreeSpace(S.SaveFolder); info["queueing"] = true;
            return new Dictionary<string, object> { { "rid", Interlocked.Increment(ref rid) }, { "full_update", true }, { "torrents", torrents }, { "categories", Categories() }, { "tags", tags.ToList() }, { "server_state", info } };
        }
        static long FreeSpace(string folder) { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace; } catch { return 0; } }
        ApiResponse Properties(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return ApiResponse.Text(400, "Bad Request");
            var x = Core.Get(hash.Trim().ToLowerInvariant()); if (x == null) return ApiResponse.Text(404, "Not Found");
            var v = Core.Snapshot(false).FirstOrDefault(q => q.Id == x.Id); if (v == null) return ApiResponse.Text(404, "Not Found");
            var t = Torrent(v, x);
            return ApiResponse.Json(new Dictionary<string, object> {
                { "hash", x.Id }, { "infohash_v1", t["infohash_v1"] }, { "infohash_v2", t["infohash_v2"] }, { "name", x.Name ?? "" }, { "save_path", x.SavePath ?? "" }, { "download_path", "" },
                { "creation_date", -1 }, { "piece_size", -1 }, { "comment", "" }, { "created_by", "" }, { "is_private", false }, { "isPrivate", false },
                { "total_wasted", 0 }, { "total_uploaded", x.Up }, { "total_uploaded_session", x.Up }, { "total_downloaded", x.Down }, { "total_downloaded_session", x.Down },
                { "up_limit", t["up_limit"] }, { "dl_limit", t["dl_limit"] }, { "time_elapsed", (long)x.SeedSecs }, { "seeding_time", (long)x.SeedSecs }, { "nb_connections", v.Seeds + v.Leechs }, { "nb_connections_limit", 100 },
                { "share_ratio", t["ratio"] }, { "addition_date", t["added_on"] }, { "completion_date", t["completion_on"] }, { "last_seen", t["seen_complete"] }, { "dl_speed", v.DownRate }, { "up_speed", v.UpRate },
                { "dl_speed_avg", 0 }, { "up_speed_avg", 0 }, { "eta", t["eta"] }, { "peers", v.Leechs }, { "peers_total", v.Leechs }, { "seeds", v.Seeds }, { "seeds_total", v.Seeds },
                { "pieces_have", -1 }, { "pieces_num", -1 }, { "reannounce", 0 }, { "total_size", t["total_size"] }, { "popularity", 0 } });
        }
        ApiResponse Files(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return ApiResponse.Text(400, "Bad Request");
            var x = Core.Get(hash.Trim().ToLowerInvariant()); if (x == null) return ApiResponse.Text(404, "Not Found");
            var v = Core.Snapshot(true).FirstOrDefault(q => q.Id == x.Id); var list = new List<object>();
            if (v != null)
                foreach (var f in v.Files)
                {
                    var rel = (Path.IsPathRooted(f.Rel ?? "") ? Core.DefaultRel(x, f.Index) : f.Rel ?? f.Path).Replace('\\', '/');
                    var p = f.Prio <= 0 ? 0 : f.Prio >= 3 ? 6 : 1;
                    list.Add(new Dictionary<string, object> { { "index", f.Index }, { "name", rel }, { "size", f.Length }, { "progress", Math.Round(f.Progress / 100.0, 4) }, { "priority", p }, { "is_seed", f.Progress >= 100 }, { "piece_range", new[] { 0, 0 } }, { "availability", f.Progress >= 100 ? 1.0 : 0.0 } });
                }
            return ApiResponse.Json(list);
        }
        ApiResponse Trackers(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return ApiResponse.Text(400, "Bad Request");
            var x = Core.Get(hash.Trim().ToLowerInvariant()); if (x == null) return ApiResponse.Text(404, "Not Found");
            var v = Core.Snapshot(true).FirstOrDefault(q => q.Id == x.Id); var list = new List<object>();
            if (v != null) foreach (var t in v.Trackers) list.Add(new Dictionary<string, object> { { "url", t.Url }, { "status", t.Status == "Working" ? 2 : t.Status == "Connecting" ? 3 : t.Status == "Not working" ? 4 : 1 }, { "tier", 0 }, { "num_peers", -1 }, { "num_seeds", -1 }, { "num_leeches", -1 }, { "num_downloaded", -1 }, { "msg", t.Message ?? "" } });
            return ApiResponse.Json(list);
        }

        // ---- torrents/add: urls (magnet links, info hashes, .torrent URLs) and / or uploaded .torrent files ("torrents")
        ApiResponse Add(ApiRequest r)
        {
            var cat = (r.Arg("category") ?? "").Trim();
            if (cat.Length > 0) { if (!ValidCategory(cat)) return ApiResponse.Text(200, "Fails."); EnsureCategory(cat, null); cat = S.Cat(cat).Name; }
            var save = r.Arg("savepath") ?? r.Arg("savePath"); if (save != null) { save = save.Trim(); if (save.Length == 0 || !Path.IsPathRooted(save)) save = null; }
            bool start = !(Bool(r.Arg("paused")) || Bool(r.Arg("stopped")));
            var p = new Pending { Category = cat, Save = save, Start = start, Tags = string.Join(",", TagList(r.Arg("tags"))), Sequential = Bool(r.Arg("sequentialDownload")), Since = DateTime.UtcNow };
            var rl = r.Arg("ratioLimit"); if (rl != null) p.Ratio = Num(rl, -2); var sl = r.Arg("seedingTimeLimit"); if (sl != null) p.Minutes = (int)Num(sl, -2);
            int added = 0, failed = 0;
            foreach (var raw in (r.Arg("urls") ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var u = raw.Trim(); if (u.Length == 0) continue; string err; Entry e = null;
                if (System.Text.RegularExpressions.Regex.IsMatch(u, "^[0-9a-fA-F]{40}$")) u = "magnet:?xt=urn:btih:" + u.ToLowerInvariant();
                if (u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) e = Core.AddMagnet(u, save, cat, start, out err);
                else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var q = Copy(p, u);
                    if (!Core.EngineUp) { lock (pending) pending.Add(q); L("a .torrent URL waits for the VPN"); added++; continue; }
                    e = FromUrl(q, out err);
                }
                else err = "not a magnet link or URL";
                if (e != null) { Configure(e, p); added++; } else { failed++; L("add failed: " + err); }
            }
            foreach (var f in r.Files.Where(f => f.Field == "torrents" || f.Field.StartsWith("torrents")))
            {
                string err; var e = Core.AddTorrentBytes(f.Data, save, cat, start, out err);
                if (e != null) { Configure(e, p); added++; } else { failed++; L("add failed: " + err); }
            }
            return ApiResponse.Text(200, added > 0 ? "Ok." : "Fails.");
        }
        static Pending Copy(Pending p, string url) { return new Pending { Url = url, Category = p.Category, Save = p.Save, Start = p.Start, Tags = p.Tags, Sequential = p.Sequential, Ratio = p.Ratio, Minutes = p.Minutes, Since = p.Since }; }
        void Configure(Entry e, Pending p)
        {
            e.Api = true; e.Tags = string.IsNullOrEmpty(p.Tags) ? null : p.Tags;
            if (p.Ratio.HasValue || p.Minutes.HasValue) Core.SetShareLimits(e.Id, p.Ratio ?? -2, p.Minutes ?? -2); else Core.Save();
            if (p.Sequential) Core.SetFlags(e.Id, true, false).Wait(5000);
            L("added " + e.Id + (string.IsNullOrEmpty(p.Category) ? "" : " in " + p.Category) + (Core.EngineUp ? "" : " (waits for the VPN)"));
        }
        // a .torrent from a URL: fetched over the VPN only
        Entry FromUrl(Pending q, out string err)
        {
            err = null; byte[] data;
            try
            {
                // 1.4: a link to one of YOUR indexer managers at a private address (Radarr hands over Prowlarr's download links) is fetched
                // straight from it - every redirect decided again, a public one only over the VPN; anything else over the VPN only
                if (FetchUrl == null && Search != null && Search.IsLocal(q.Url))
                {
                    var rep = IndexerNet.Get(q.Url, TimeSpan.FromSeconds(20), Search.Bases(), Core.EngineUp ? Core.BoundIp : null, Core.EngineUp ? Core.BoundDns : null);
                    if (rep.Location != null && rep.Location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return Core.AddMagnet(rep.Location, q.Save, q.Category, q.Start, out err);
                    if (rep.Location != null) { err = "too many redirects"; return null; }
                    data = rep.Body;
                }
                else data = FetchUrl != null ? FetchUrl(q.Url) : Core.EngineUp ? VpnFetch.Get(q.Url, Core.BoundIp, Core.BoundDns, 20 * 1024 * 1024) : null;
            }
            catch (Exception e) { err = VpnFetch.Why(e); return null; }
            if (data == null) { err = "the VPN is off"; return null; }
            return Core.AddTorrentBytes(data, q.Save, q.Category, q.Start, out err);
        }
        public void FetchPending()
        {
            if (!Core.EngineUp) return;
            List<Pending> now; lock (pending) { now = pending.ToList(); }
            foreach (var q in now)
            {
                string err; var e = FromUrl(q, out err);
                lock (pending)
                {
                    if (e != null) { pending.Remove(q); Configure(e, q); continue; }
                    if (++q.Tries >= 5 || DateTime.UtcNow - q.Since > TimeSpan.FromHours(6)) { pending.Remove(q); L("gave up on a .torrent URL: " + err); }
                }
            }
        }
    }

    // ---------------- the listener: plain HTTP/1.1 with keep-alive, on one local address
    public class ApiServer : IDisposable
    {
        public readonly QbitApi Api; TcpListener listener; volatile bool stopping; int conns; Timer pendingTimer;
        readonly ConcurrentDictionary<TcpClient, byte> open = new ConcurrentDictionary<TcpClient, byte>();
        public IPEndPoint EndPoint; public string Error;
        public bool Running { get { return listener != null; } }
        public ApiServer(QbitApi api) { Api = api; }
        public const int MaxBody = 30 * 1024 * 1024, MaxHead = 16 * 1024, MaxConns = 32;

        public string Start(string bind, int port)
        {
            Stop();
            IPAddress ip; if (!IPAddress.TryParse((bind ?? "").Trim(), out ip)) return Error = "That isn't an address.";
            var why = ApiGuard.BindProblem(ip, Api.Core.BoundIp); if (why != null) return Error = why;
            try { var l = new TcpListener(ip, port); l.Start(16); listener = l; }
            catch (SocketException e) { listener = null; return Error = e.SocketErrorCode == SocketError.AddressAlreadyInUse ? "Port " + port + " is already used by another program - pick another one." : e.SocketErrorCode == SocketError.AddressNotAvailable ? ip + " isn't an address of this PC right now." : e.Message; }
            EndPoint = (IPEndPoint)listener.LocalEndpoint; stopping = false; Error = null;
            var lst = listener;
            new Thread(() => Accept(lst)) { IsBackground = true, Name = "webapi-accept" }.Start();
            pendingTimer = new Timer(_ => { try { Api.FetchPending(); } catch { } }, null, 3000, 3000);
            return null;
        }
        public void Stop()
        {
            stopping = true; var l = listener; listener = null;
            if (l != null) try { l.Stop(); } catch { }
            var t = pendingTimer; pendingTimer = null; if (t != null) t.Dispose();
            foreach (var c in open.Keys.ToList()) try { c.Close(); } catch { }
            Api.Guard.EndAll();
        }
        public void Dispose() { Stop(); }

        void Accept(TcpListener l)
        {
            while (!stopping && listener == l)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { if (stopping || listener != l) return; Thread.Sleep(50); continue; }
                if (Interlocked.Increment(ref conns) > MaxConns) { Interlocked.Decrement(ref conns); try { c.Close(); } catch { } continue; }
                open[c] = 0; new Thread(() => { try { Serve(c, l); } catch { } finally { Interlocked.Decrement(ref conns); byte z; open.TryRemove(c, out z); try { c.Close(); } catch { } } }) { IsBackground = true, Name = "webapi-conn" }.Start();
            }
        }

        // one connection: requests until "Connection: close", an error, 30 s idle or the server stops
        void Serve(TcpClient c, TcpListener l)
        {
            c.NoDelay = true; c.ReceiveTimeout = 30000; c.SendTimeout = 30000;
            var remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address; if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var s = c.GetStream();
            if (!Api.Guard.RemoteAllowed(remote)) { Write(s, ApiResponse.Text(403, "Forbidden"), false); return; }
            var rd = new Reader(s);
            for (int n = 0; n < 10000 && !stopping && listener == l; n++)
            {
                var head = rd.Head(); if (head == null) return;
                var req = new ApiRequest { Remote = remote };
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var first = lines[0].Split(' '); if (first.Length < 3) { Write(s, ApiResponse.Text(400, "Bad Request"), false); return; }
                req.Method = first[0].ToUpperInvariant(); req.Version = first[2];
                var target = first[1]; var qi = target.IndexOf('?');
                req.Path = Uri.UnescapeDataString(qi < 0 ? target : target.Substring(0, qi));
                if (qi >= 0) ApiHttp.ParseUrlEncoded(target.Substring(qi + 1), req.Args);
                for (int i = 1; i < lines.Length; i++) { var k = lines[i].IndexOf(':'); if (k > 0) req.Headers[lines[i].Substring(0, k).Trim()] = lines[i].Substring(k + 1).Trim(); }
                bool keep = req.Version == "HTTP/1.1" ? !string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase) : string.Equals(req.Header("Connection"), "keep-alive", StringComparison.OrdinalIgnoreCase);
                // the body
                long len = 0; var te = req.Header("Transfer-Encoding"); bool chunked = te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!chunked && req.Header("Content-Length") != null && (!long.TryParse(req.Header("Content-Length"), out len) || len < 0)) { Write(s, ApiResponse.Text(400, "Bad Request"), false); return; }
                if (len > MaxBody) { Write(s, ApiResponse.Text(413, "Payload Too Large"), false); return; }
                if ((len > 0 || chunked) && string.Equals(req.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase)) { var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); s.Write(cont, 0, cont.Length); }
                req.Body = chunked ? rd.Chunked(MaxBody) : rd.Bytes((int)len); if (req.Body == null) return;
                var ct = req.Header("Content-Type") ?? "";
                if (ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) ApiHttp.ParseUrlEncoded(Encoding.UTF8.GetString(req.Body), req.Args);
                else if (ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)) { var b = ApiHttp.Boundary(ct); if (b != null) ApiHttp.ParseMultipart(req.Body, b, req.Args, req.Files); }
                var res = Api.Handle(req);
                Write(s, res, keep, req.Method == "HEAD");
                if (!keep) return;
            }
        }
        static void Write(Stream s, ApiResponse r, bool keep, bool head = false)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Code).Append(' ').Append(ApiHttp.Reason(r.Code)).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.Type).Append("\r\nContent-Length: ").Append(r.Body.Length).Append("\r\n");
            if (r.SetCookie != null) sb.Append("Set-Cookie: ").Append(r.SetCookie).Append("\r\n");
            foreach (var kv in r.Headers) if (kv.Key.IndexOfAny(new[] { '\r', '\n', ':' }) < 0 && (kv.Value ?? "").IndexOfAny(new[] { '\r', '\n' }) < 0) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (!r.Headers.ContainsKey("Content-Security-Policy")) sb.Append("Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n");
            sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: same-origin\r\n");
            sb.Append("Connection: ").Append(keep ? "keep-alive" : "close").Append("\r\n\r\n");
            var h = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(h, 0, h.Length); if (!head && r.Body.Length > 0) s.Write(r.Body, 0, r.Body.Length); s.Flush();
        }

        // a small buffered reader for the head, fixed-length and chunked bodies
        class Reader
        {
            readonly Stream s; byte[] buf = new byte[8192]; int start, end;
            public Reader(Stream s) { this.s = s; }
            bool Fill() { if (start > 0 && start == end) { start = end = 0; } if (end == buf.Length) { if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, end - start); end -= start; start = 0; } else Array.Resize(ref buf, buf.Length * 2); } int n; try { n = s.Read(buf, end, buf.Length - end); } catch { return false; } if (n <= 0) return false; end += n; return true; }
            public string Head()
            {
                while (true)
                {
                    for (int i = start; i + 3 < end; i++) if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') { var h = Encoding.ASCII.GetString(buf, start, i - start); start = i + 4; return h.TrimStart('\r', '\n'); }
                    if (end - start > MaxHead) return null;
                    if (!Fill()) return null;
                }
            }
            public byte[] Bytes(int n)
            {
                var r = new byte[n]; int got = 0;
                while (got < n) { if (start == end && !Fill()) return null; var k = Math.Min(n - got, end - start); Buffer.BlockCopy(buf, start, r, got, k); start += k; got += k; }
                return r;
            }
            string Line() { var sb = new StringBuilder(); while (true) { if (start == end && !Fill()) return null; var ch = (char)buf[start++]; if (ch == '\n') return sb.ToString().TrimEnd('\r'); sb.Append(ch); if (sb.Length > 1024) return null; } }
            public byte[] Chunked(int max)
            {
                var ms = new MemoryStream();
                while (true)
                {
                    var l = Line(); if (l == null) return null; var semi = l.IndexOf(';'); int n;
                    if (!int.TryParse(semi < 0 ? l.Trim() : l.Substring(0, semi).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) || n < 0) return null;
                    if (n == 0) { while (true) { var t = Line(); if (t == null) return null; if (t.Length == 0) break; } return ms.ToArray(); }
                    if (ms.Length + n > max) return null;
                    var d = Bytes(n); if (d == null) return null; ms.Write(d, 0, d.Length); if (Line() == null) return null;
                }
            }
        }
    }
}
