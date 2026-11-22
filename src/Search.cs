// Hallow Torrents 1.3 - search, done properly: through YOUR OWN indexer manager (Prowlarr or Jackett) or Torznab feeds you add, never
// by scraping torrent sites (there is no built-in site list).
//   * Every request (caps, searches, the indexer list, .torrent downloads) goes out from a socket bound to the VPN address with its name
//     looked up over the VPN (VpnFetch.Fetch, like RSS and the trackers). While the VPN is off NOTHING is sent - a search says so.
//   * The API keys are kept encrypted for your Windows user (DPAPI, CurrentUser) in search.json - never in settings.json, never in clear.
//   * All enabled indexers are asked at once, each with its own timeout; each one's status (results, time, error) is shown.
//   * Smart ranking: release names are read (resolution, source, codec, HDR / DV, audio, group, season / episode), then scored on health
//     (seeders, peers), your preference profile, size sanity and how reliable the indexer has been. Likely fakes are flagged and go to
//     the bottom with the reason.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace HallowTorrents
{
    // ---------------- what is kept (search.json)
    [DataContract]
    public class TzFeed   // one Torznab endpoint: an indexer inside Prowlarr / Jackett, or a feed you added yourself
    {
        [DataMember] public string Id, Name, Url;       // Url = the Torznab API address, WITHOUT the key
        [DataMember] public bool Enabled;
        [DataMember] public bool Caps, Search, TvSearch, TvSeasonEp, MovieSearch, MovieImdb, MovieTmdb;   // from t=caps
        [DataMember] public List<string> Cats;          // "2000|Movies"
        [DataMember] public int Ok, Fail; [DataMember] public double AvgMs;
        public TzFeed() { Enabled = true; Search = true; }
        [OnDeserializing] void D(StreamingContext c) { Enabled = true; Search = true; }
        // 0..1: how often it answered (a new one starts at 0.8)
        public double Reliability { get { var n = Ok + Fail; return n == 0 ? 0.8 : (Ok + 0.8 * 2) / (n + 2.0); } }
    }

    [DataContract]
    public class Indexer   // what you added: "prowlarr", "jackett" or "torznab"
    {
        [DataMember] public string Id, Kind, Name, BaseUrl;
        [DataMember] public string Key;                 // the API key, DPAPI-protected for this Windows user (base64) - never the key itself
        [DataMember] public bool Enabled;
        [DataMember] public List<TzFeed> Feeds;         // prowlarr / jackett: the indexers found inside; torznab: just itself
        [DataMember] public string LastSync, LastError;
        public Indexer() { Enabled = true; Feeds = new List<TzFeed>(); }
        [OnDeserializing] void D(StreamingContext c) { Enabled = true; }
        public string KindName { get { return Kind == "prowlarr" ? "Prowlarr" : Kind == "jackett" ? "Jackett" : "Torznab feed"; } }
    }

    [DataContract]
    public class SearchProfile   // "1080p WEB-DL preferred, avoid CAM, max 8 GB"
    {
        [DataMember] public string Resolution;          // "2160p" / "1080p" / "720p" / "480p" / "" (no preference)
        [DataMember] public string Source;              // "Remux" / "BluRay" / "WEB-DL" / "WEBRip" / "HDTV" / ""
        [DataMember] public string Codec;               // "x265" / "x264" / "AV1" / ""
        [DataMember] public string Avoid;               // words never wanted (CAM TS...)
        [DataMember] public double MaxGB;               // 0 = no maximum
        [DataMember] public int MinSeeders;             // fewer are hidden
        [DataMember] public int TimeoutSec;             // per indexer
        [DataMember] public bool Inspect;               // fetch the top results' .torrent files (over the VPN) to check their file lists
        public SearchProfile() { Defaults(); }
        [OnDeserializing] void D(StreamingContext c) { Defaults(); }
        void Defaults() { Resolution = "1080p"; Source = "WEB-DL"; Codec = ""; Avoid = RssRule.DefaultAvoid; MaxGB = 8; MinSeeders = 1; TimeoutSec = 15; }
        public void Fix()
        {
            if (new[] { "2160p", "1080p", "720p", "480p", "" }.All(x => x != Resolution)) Resolution = "1080p";
            if (new[] { "Remux", "BluRay", "WEB-DL", "WEBRip", "HDTV", "" }.All(x => x != Source)) Source = "";
            if (new[] { "x265", "x264", "AV1", "" }.All(x => x != Codec)) Codec = "";
            if (Avoid == null) Avoid = ""; if (MaxGB < 0) MaxGB = 0; if (MaxGB > 1000) MaxGB = 1000;
            if (MinSeeders < 0) MinSeeders = 0; if (TimeoutSec < 2) TimeoutSec = 2; if (TimeoutSec > 60) TimeoutSec = 60;
        }
        public string Text
        {
            get
            {
                var p = new List<string>();
                var want = ((Resolution ?? "") + " " + (Source ?? "") + " " + (Codec ?? "")).Trim(); while (want.Contains("  ")) want = want.Replace("  ", " ");
                p.Add(want.Length > 0 ? want + " preferred" : "no quality preference");
                var av = (Avoid ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries); if (av.Length > 0) p.Add("avoid " + string.Join(" / ", av.Take(3)) + (av.Length > 3 ? "..." : ""));
                if (MaxGB > 0) p.Add("max " + MaxGB.ToString("0.#") + " GB");
                return string.Join(", ", p);
            }
        }
    }

    [DataContract]
    public class SearchStore
    {
        [DataMember] public List<Indexer> Indexers;
        [DataMember] public SearchProfile Profile;
        public void Fix()
        {
            if (Indexers == null) Indexers = new List<Indexer>(); Indexers.RemoveAll(i => i == null || string.IsNullOrWhiteSpace(i.BaseUrl));
            foreach (var i in Indexers) { if (i.Id == null) i.Id = NewId(); if (i.Feeds == null) i.Feeds = new List<TzFeed>(); i.Feeds.RemoveAll(f => f == null || string.IsNullOrWhiteSpace(f.Url)); foreach (var f in i.Feeds) if (f.Id == null) f.Id = NewId(); }
            if (Profile == null) Profile = new SearchProfile(); Profile.Fix();
        }
        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }
    }

    // ---------------- the API key: DPAPI for this Windows user (with our own entropy), never stored or logged in clear
    public static class Secret
    {
        static readonly byte[] entropy = Encoding.UTF8.GetBytes("HallowTorrents.Indexer.v1");
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return null;
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), entropy, DataProtectionScope.CurrentUser));
        }
        public static string Unprotect(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), entropy, DataProtectionScope.CurrentUser)); } catch { return null; }
        }
    }

    // ---------------- a release name, read
    public class Release
    {
        public int Res;                       // 480 / 576 / 720 / 1080 / 2160 (0 = not said)
        public string Source = "", Codec = "", Hdr = "", Audio = "", Group = "";
        public bool Low;                      // CAM / TS / TC / SCR: recorded in a cinema or a screener
        public string Kind = "unknown", Title; public int Year, Season = -1, Episode = -1, EpisodeEnd = -1;
        public bool Pack { get { return Kind == "season"; } }
        public string ResText { get { return Res >= 2160 ? "2160p" : Res >= 1080 ? "1080p" : Res >= 720 ? "720p" : Res > 0 ? Res + "p" : ""; } }
        public string SxE { get { return Season < 0 ? "" : Episode < 0 ? "S" + Season.ToString("00") : "S" + Season.ToString("00") + "E" + Episode.ToString("00") + (EpisodeEnd > 0 ? "-E" + EpisodeEnd.ToString("00") : ""); } }
    }

    public class SearchResult
    {
        public string Token, Title, IndexerName, FeedId, SourceId, Link, Magnet, InfoHash, Details;
        public long Size; public int Seeders = -1, Peers = -1, Grabs = -1, FileCount = -1;
        public DateTime? Published; public List<int> Cats = new List<int>();
        public string ImdbId;
        public Release R;
        public double Score; public List<string> Why = new List<string>();     // what the score is made of
        public List<string> Flags = new List<string>();                         // why it looks fake / suspicious
        public List<string> Notes = new List<string>();                         // avoided, over the max size...
        public List<TFile> Files;                                               // when known (a fetched .torrent)
        public bool InList, Grabbed;
        public bool Flagged { get { return Flags.Count > 0; } }
        public string Category { get { return Torznab.HallowCat(Cats, R); } }
    }
    public class FeedStatus { public string FeedId, Name, State, Error; public int Count; public long Ms; }   // State: ok / error / timeout / locked
    public class SearchQuery
    {
        public string Text = "", Mode = "search", Cat = "", ImdbId, TmdbId; public int Season = -1, Episode = -1; public int Limit = 100;
        // "night sky s01e02" -> TV search for S01E02; "tt0111161" -> a movie by its IMDb id; anything else -> a normal search
        public static SearchQuery From(string text, string cat)
        {
            var q = new SearchQuery { Cat = cat ?? "" }; text = (text ?? "").Trim();
            var ep = Regex.Match(text, @"(?i)\bS(\d{1,2})(?:[ ._-]?E(\d{1,3}))?\b");
            var imdb = Regex.Match(text, @"(?i)\btt(\d{6,9})\b");
            if (imdb.Success) { q.Mode = "movie"; q.ImdbId = imdb.Groups[1].Value; text = (text.Remove(imdb.Index, imdb.Length)).Trim(); }
            else if (ep.Success && cat != "movies") { q.Mode = "tv"; q.Season = int.Parse(ep.Groups[1].Value); if (ep.Groups[2].Success) q.Episode = int.Parse(ep.Groups[2].Value); text = text.Remove(ep.Index, ep.Length).Trim(); }
            q.Text = Regex.Replace(text, @"\s+", " ");
            return q;
        }
    }
    public class SearchOutcome
    {
        public List<SearchResult> Results = new List<SearchResult>(); public List<FeedStatus> Status = new List<FeedStatus>();
        public bool Locked; public string Error; public long Ms; public SearchQuery Query;
    }
    public class TorznabException : Exception { public int Code; public TorznabException(int code, string m) : base(m) { Code = code; } }

    // ---------------- the Torznab API (pure)
    public static class Torznab
    {
        static readonly XNamespace tz = "http://torznab.com/schemas/2015/feed";
        public static string Url(string api, string key, IEnumerable<KeyValuePair<string, string>> p)
        {
            var sb = new StringBuilder(api.TrimEnd('?', '&')); bool first = api.IndexOf('?') < 0;
            foreach (var kv in p) { if (kv.Value == null) continue; sb.Append(first ? '?' : '&').Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value)); first = false; }
            if (!string.IsNullOrEmpty(key)) sb.Append(first ? '?' : '&').Append("apikey=").Append(Uri.EscapeDataString(key));
            return sb.ToString();
        }
        // never show or log a key: apikey=*** (also in error messages that quote the address)
        public static string Redact(string s) { return s == null ? null : Regex.Replace(s, @"(?i)(api_?key=|apikey%3d)[^&\s""'<]+", "$1***"); }
        static readonly Dictionary<string, string> catParam = new Dictionary<string, string> { { "movies", "2000" }, { "tv", "5000" }, { "music", "3000" }, { "games", "1000,4050" }, { "software", "4000" }, { "books", "7000" }, { "other", "8000" } };
        public static string CatParam(string cat) { string v; return cat != null && catParam.TryGetValue(cat.ToLowerInvariant(), out v) ? v : null; }
        public static readonly string[][] CatNames = { new[] { "", "All" }, new[] { "movies", "Movies" }, new[] { "tv", "TV" }, new[] { "music", "Music" }, new[] { "games", "Games" }, new[] { "software", "Software" }, new[] { "books", "Books" }, new[] { "other", "Other" } };
        public static string CatOf(IEnumerable<int> cats)
        {
            foreach (var c in cats ?? new int[0])
            {
                var top = c >= 100000 ? 0 : c / 1000;   // 100000+ = an indexer's own categories
                if (c == 4050) return "games";
                switch (top) { case 2: return "movies"; case 5: return "tv"; case 3: return "music"; case 1: return "games"; case 4: return "software"; case 7: return "books"; case 8: return "other"; }
            }
            return "";
        }
        // the Hallow Torrents category a result goes to (Movies / TV / Music / Games / Other)
        public static string HallowCat(IEnumerable<int> cats, Release r)
        {
            var c = CatOf(cats);
            if (c == "movies") return "Movies"; if (c == "tv") return "TV"; if (c == "music") return "Music"; if (c == "games") return "Games";
            if (c == "" && r != null) { if (r.Kind == "episode" || r.Kind == "season") return "TV"; if (r.Kind == "movie" && r.Res > 0) return "Movies"; }
            return "Other";
        }
        static XDocument Load(byte[] data)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 40000000 };
            using (var r = XmlReader.Create(new MemoryStream(data), settings)) return XDocument.Load(r);
        }
        static void ThrowIfError(XDocument doc)
        {
            if (doc.Root != null && doc.Root.Name.LocalName == "error")
            {
                int code; int.TryParse((string)doc.Root.Attribute("code"), out code);
                var d = (string)doc.Root.Attribute("description") ?? "error";
                throw new TorznabException(code, code == 100 || code == 101 || code == 102 ? "The API key was refused (" + Family.OneLine(d) + ")." : Family.OneLine(d) + (code > 0 ? " (code " + code + ")" : ""));
            }
        }
        // t=caps: what the feed can search by, and its categories
        public static void ReadCaps(byte[] data, TzFeed f)
        {
            var doc = Load(data); ThrowIfError(doc);
            var caps = doc.Root != null && doc.Root.Name.LocalName == "caps" ? doc.Root : doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "caps");
            if (caps == null) throw new IOException("That isn't a Torznab API (no caps).");
            ReadCaps(caps, f);
        }
        static void ReadCaps(XElement caps, TzFeed f)
        {
            Func<string, XElement> s = n => caps.Descendants().FirstOrDefault(e => e.Name.LocalName == n);
            Func<XElement, bool> on = e => e != null && string.Equals((string)e.Attribute("available"), "yes", StringComparison.OrdinalIgnoreCase);
            Func<XElement, string> pars = e => (e == null ? "" : (string)e.Attribute("supportedParams") ?? "").ToLowerInvariant();
            var se = s("search"); var tv = s("tv-search"); var mv = s("movie-search");
            f.Search = se == null || on(se); f.TvSearch = on(tv); f.MovieSearch = on(mv);
            f.TvSeasonEp = f.TvSearch && (pars(tv).Length == 0 || pars(tv).Contains("season"));
            f.MovieImdb = f.MovieSearch && pars(mv).Contains("imdbid"); f.MovieTmdb = f.MovieSearch && pars(mv).Contains("tmdbid");
            f.Cats = caps.Descendants().Where(e => e.Name.LocalName == "category").Select(e => (string)e.Attribute("id") + "|" + (string)e.Attribute("name")).Take(60).ToList();
            f.Caps = true;
        }
        // Jackett: t=indexers&configured=true on the "all" endpoint lists the indexers you set up (with their caps)
        public static List<TzFeed> JackettIndexers(byte[] data, string baseUrl)
        {
            var doc = Load(data); ThrowIfError(doc); var list = new List<TzFeed>();
            foreach (var x in doc.Descendants().Where(e => e.Name.LocalName == "indexer"))
            {
                var id = (string)x.Attribute("id"); if (string.IsNullOrWhiteSpace(id) || id == "all") continue;
                var conf = (string)x.Attribute("configured"); if (conf != null && conf != "true") continue;
                var t = x.Elements().FirstOrDefault(e => e.Name.LocalName == "title");
                var f = new TzFeed { Id = "j-" + Regex.Replace(id, "[^A-Za-z0-9_-]", ""), Name = t != null ? t.Value.Trim() : id, Url = baseUrl.TrimEnd('/') + "/api/v2.0/indexers/" + Uri.EscapeDataString(id) + "/results/torznab/api" };
                var caps = x.Elements().FirstOrDefault(e => e.Name.LocalName == "caps"); if (caps != null) ReadCaps(caps, f);
                list.Add(f);
            }
            return list;
        }
        // Prowlarr: /api/v1/indexer lists them (JSON); each torrent indexer has its own Torznab API at <base>/<id>/api
        public static List<TzFeed> ProwlarrIndexers(byte[] json, string baseUrl)
        {
            var o = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(Encoding.UTF8.GetString(json)) as object[];
            if (o == null) throw new IOException("Prowlarr's answer wasn't a list of indexers.");
            var list = new List<TzFeed>();
            foreach (var d in o.OfType<Dictionary<string, object>>())
            {
                object id, name, en, proto;
                if (!d.TryGetValue("id", out id) || id == null) continue;
                if (d.TryGetValue("protocol", out proto) && proto is string && !string.Equals((string)proto, "torrent", StringComparison.OrdinalIgnoreCase)) continue;   // Usenet indexers aren't for us
                d.TryGetValue("name", out name); d.TryGetValue("enable", out en);
                list.Add(new TzFeed { Id = "p-" + id, Name = name as string ?? ("Indexer " + id), Url = baseUrl.TrimEnd('/') + "/" + id + "/api", Enabled = !(en is bool) || (bool)en });
            }
            return list;
        }
        static string Attr(XElement item, string name)
        {
            var a = item.Elements().FirstOrDefault(e => (e.Name.LocalName == "attr") && string.Equals((string)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            return a == null ? null : (string)a.Attribute("value");
        }
        static int Int(string s) { long v; return long.TryParse(s, out v) ? (int)Math.Max(-1, Math.Min(int.MaxValue, v)) : -1; }
        // t=search / tvsearch / movie: the results
        public static List<SearchResult> Results(byte[] data, TzFeed f)
        {
            var doc = Load(data); ThrowIfError(doc);
            var list = new List<SearchResult>();
            foreach (var it in doc.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                var r = new SearchResult { FeedId = f == null ? null : f.Id, IndexerName = f == null ? null : f.Name };
                string enclosure = null, link = null;
                foreach (var c in it.Elements())
                {
                    var n = c.Name.LocalName;
                    if (n == "title") r.Title = c.Value.Trim();
                    else if (n == "link") link = c.Value.Trim();
                    else if (n == "comments") r.Details = c.Value.Trim();
                    else if (n == "size") { long l; if (long.TryParse(c.Value.Trim(), out l)) r.Size = l; }
                    else if (n == "enclosure") { enclosure = (string)c.Attribute("url"); long l; if (r.Size <= 0 && long.TryParse((string)c.Attribute("length"), out l)) r.Size = l; }
                    else if (n == "category") { int cat; if (int.TryParse(c.Value.Trim(), out cat) && !r.Cats.Contains(cat)) r.Cats.Add(cat); }
                    else if (n == "pubDate") { DateTime d; if (DateTime.TryParse(c.Value.Trim().Replace(" GMT", " +0000"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out d)) r.Published = d; }
                    else if (n == "jackettindexer" || n == "prowlarrindexer") { if (!string.IsNullOrWhiteSpace(c.Value) && (f == null || f.Name == null)) r.IndexerName = c.Value.Trim(); }
                }
                long size; if (long.TryParse(Attr(it, "size"), out size) && size > 0) r.Size = size;
                r.Seeders = Int(Attr(it, "seeders")); var peers = Int(Attr(it, "peers")); var leech = Int(Attr(it, "leechers"));
                r.Peers = peers >= 0 ? peers : leech >= 0 && r.Seeders >= 0 ? leech + r.Seeders : -1;
                r.Grabs = Int(Attr(it, "grabs")); r.FileCount = Int(Attr(it, "files"));
                foreach (var a in it.Elements().Where(e => e.Name.LocalName == "attr" && (string)e.Attribute("name") == "category")) { int cat; if (int.TryParse((string)a.Attribute("value"), out cat) && !r.Cats.Contains(cat)) r.Cats.Add(cat); }
                var hash = Attr(it, "infohash"); if (hash != null && Regex.IsMatch(hash.Trim(), "^[0-9a-fA-F]{40}$")) r.InfoHash = hash.Trim().ToLowerInvariant();
                var mag = Attr(it, "magneturl");
                if (mag == null && link != null && link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) mag = link;
                if (mag == null && enclosure != null && enclosure.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) mag = enclosure;
                if (mag != null && mag.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) r.Magnet = mag.Trim();
                if (r.Magnet == null && r.InfoHash != null && enclosure == null && link == null) r.Magnet = "magnet:?xt=urn:btih:" + r.InfoHash + "&dn=" + Uri.EscapeDataString(r.Title ?? "");
                var http = new[] { enclosure, link }.FirstOrDefault(u => u != null && (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
                r.Link = http;
                if (r.InfoHash == null && r.Magnet != null) { var m = Regex.Match(r.Magnet, "(?i)btih:([0-9a-f]{40})"); if (m.Success) r.InfoHash = m.Groups[1].Value.ToLowerInvariant(); }
                var imdb = Attr(it, "imdbid") ?? Attr(it, "imdb"); if (!string.IsNullOrWhiteSpace(imdb)) r.ImdbId = imdb.Trim().TrimStart('t');
                DateTime ud; var usd = Attr(it, "usenetdate"); if (r.Published == null && usd != null && DateTime.TryParse(usd, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out ud)) r.Published = ud;
                if (!string.IsNullOrWhiteSpace(r.Title) && (r.Link != null || r.Magnet != null)) list.Add(r);
            }
            return list;
        }
        // the request for a query, using what the feed says it can do (a feed without tvsearch gets "show S01E02" as words)
        public static List<KeyValuePair<string, string>> Params(SearchQuery q, TzFeed f)
        {
            var p = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (k, v) => p.Add(new KeyValuePair<string, string>(k, v));
            var cat = CatParam(q.Cat);
            if (q.Mode == "tv" && (f.TvSearch || !f.Caps))
            {
                add("t", "tvsearch"); if (q.Text.Length > 0) add("q", q.Text);
                if (q.Season >= 0) add("season", q.Season.ToString()); if (q.Episode >= 0) add("ep", q.Episode.ToString());
                add("cat", cat ?? "5000");
            }
            else if (q.Mode == "movie" && (f.MovieSearch || !f.Caps))
            {
                add("t", "movie"); if (q.Text.Length > 0) add("q", q.Text);
                if (q.ImdbId != null) add("imdbid", q.ImdbId); if (q.TmdbId != null) add("tmdbid", q.TmdbId);
                add("cat", cat ?? "2000");
            }
            else
            {
                var text = q.Text;
                if (q.Mode == "tv" && q.Season >= 0) text = (text + " S" + q.Season.ToString("00") + (q.Episode >= 0 ? "E" + q.Episode.ToString("00") : "")).Trim();
                if (q.Mode == "movie" && q.ImdbId != null && text.Length == 0) text = "tt" + q.ImdbId;
                add("t", "search"); add("q", text); if (cat != null) add("cat", cat);
            }
            add("extended", "1"); add("limit", Math.Max(10, Math.Min(500, q.Limit)).ToString());
            return p;
        }
    }

    // ---------------- reading release names and ranking (pure, tested)
    public static class Ranker
    {
        static bool Rx(string t, string rx) { return Regex.IsMatch(t, rx, RegexOptions.IgnoreCase); }
        public static Release Parse(string title)
        {
            var r = new Release(); var t = " " + Regex.Replace((title ?? "").Replace('_', ' '), @"\.(mkv|mp4|avi|torrent)$", "", RegexOptions.IgnoreCase) + " ";
            r.Res = Rx(t, @"2160p|\b4k\b|\buhd\b") ? 2160 : Rx(t, @"1080[pi]") ? 1080 : Rx(t, @"720p") ? 720 : Rx(t, @"576p") ? 576 : Rx(t, @"480p|\bsd\b|\bdvdrip\b") ? 480 : 0;
            if (Rx(t, @"\b(hd-?)?cam(-?rip)?\b|\bhdcam\b")) { r.Source = "CAM"; r.Low = true; }
            else if (Rx(t, @"\b(hd-?)?ts\b|\btelesync\b|\bhdts\b|\bpdvd\b")) { r.Source = "TS"; r.Low = true; }
            else if (Rx(t, @"\b(hd-?)?tc\b|\btelecine\b")) { r.Source = "TC"; r.Low = true; }
            else if (Rx(t, @"\b(dvd)?scr\b|\bscreener\b")) { r.Source = "SCR"; r.Low = true; }
            else if (Rx(t, @"\bremux\b")) r.Source = "Remux";
            else if (Rx(t, @"blu-?ray|\bbdrip\b|\bbrrip\b|\bbd(25|50)?\b")) r.Source = "BluRay";
            else if (Rx(t, @"\bweb[ .-]?dl\b|\bamzn\b|\bnf\b|\bdsnp\b|\bhmax\b|\batvp\b")) r.Source = "WEB-DL";
            else if (Rx(t, @"\bweb-?rip\b|\bweb\b")) r.Source = "WEBRip";
            else if (Rx(t, @"\b(hd|pd)tv\b")) r.Source = "HDTV";
            else if (Rx(t, @"\bdvd(rip|5|9)?\b")) r.Source = "DVD";
            r.Codec = Rx(t, @"\bav1\b") ? "AV1" : Rx(t, @"\bx265\b|\bh\.?265\b|\bhevc\b") ? "x265" : Rx(t, @"\bx264\b|\bh\.?264\b|\bavc\b") ? "x264" : Rx(t, @"\bxvid\b|\bdivx\b") ? "XviD" : "";
            var dv = Rx(t, @"\bdv\b|\bdovi\b|dolby[ .-]?vision"); var hdr = Rx(t, @"\bhdr10\+|\bhdr10plus\b") ? "HDR10+" : Rx(t, @"\bhdr(10)?\b") ? "HDR" : "";
            r.Hdr = dv && hdr.Length > 0 ? "DV " + hdr : dv ? "DV" : hdr;
            var audio = Rx(t, @"\btrue-?hd\b") ? "TrueHD" : Rx(t, @"\bdts[ .-]?(hd|x|ma)\b") ? "DTS-HD" : Rx(t, @"\bdts\b") ? "DTS" : Rx(t, @"\b(ddp|dd\+|e-?ac-?3)") ? "DD+" : Rx(t, @"\b(dd|ac-?3)[ .]?[257]\.?[01]\b|\bac-?3\b") ? "DD" : Rx(t, @"\bflac\b") ? "FLAC" : Rx(t, @"\baac") ? "AAC" : Rx(t, @"\bopus\b") ? "Opus" : Rx(t, @"\bmp3\b") ? "MP3" : "";
            if (Rx(t, @"\batmos\b")) audio = audio.Length > 0 ? audio + " Atmos" : "Atmos";
            r.Audio = audio;
            var g = Regex.Match(t.Trim(), @"-([A-Za-z0-9]{2,20})(\s*\[[^\]]*\])?$");
            if (g.Success && !Rx(g.Groups[1].Value, @"^(dl|rip|hd|web|ts|cam|x264|x265|hevc|1080p|720p|2160p)$")) r.Group = g.Groups[1].Value;
            var m = Media.Parse(title ?? "");
            r.Kind = m.Kind; r.Title = m.Title; r.Year = m.Year; r.Season = m.Season; r.Episode = m.Episode; r.EpisodeEnd = m.EpisodeEnd;
            return r;
        }
        static readonly int[] steps = { 480, 720, 1080, 2160 };
        static int Step(int res) { if (res <= 0) return -1; for (int i = steps.Length - 1; i >= 0; i--) if (res >= steps[i]) return i; return 0; }
        static int SourceRank(string s) { switch (s) { case "Remux": return 18; case "BluRay": return 16; case "WEB-DL": return 15; case "WEBRip": return 12; case "HDTV": return 8; case "DVD": return 5; case "": return 4; default: return 0; } }
        // the smallest size a real release of this quality usually has (null = can't tell: season packs, music, software...)
        public static long? ExpectedMin(Release r, string cat)
        {
            if (r.Res <= 0 || r.Pack) return null;
            double minutes = r.Kind == "episode" ? 45 * (r.EpisodeEnd > r.Episode && r.Episode >= 0 ? r.EpisodeEnd - r.Episode + 1 : 1) : r.Kind == "movie" ? 100 : cat == "movies" ? 100 : cat == "tv" ? 45 : 0;
            if (minutes <= 0) return null;
            double mbPerMin = r.Res >= 2160 ? 40 : r.Res >= 1080 ? 15 : r.Res >= 720 ? 8 : 4;
            if (r.Codec == "x265" || r.Codec == "AV1") mbPerMin /= 2;
            if (r.Source == "Remux" || r.Source == "BluRay" && r.Res >= 2160) mbPerMin *= 2;
            return (long)(minutes * mbPerMin * 1024 * 1024);
        }
        static string Gb(long b) { return Fmt.Bytes(b); }

        // why a result looks fake or suspicious (each one is a reason it goes to the bottom)
        public static readonly HashSet<string> BadInList = new HashSet<string>(new[] { ".exe", ".scr", ".lnk", ".pif", ".com", ".bat", ".cmd", ".vbs", ".js", ".msi", ".hta", ".cpl", ".jar", ".ps1" }, StringComparer.OrdinalIgnoreCase);
        public static List<string> Flags(SearchResult s, DateTime nowUtc)
        {
            var f = new List<string>(); var r = s.R ?? Parse(s.Title); var t = s.Title ?? "";
            var cat = Torznab.CatOf(s.Cats);
            bool mediaLike = cat == "movies" || cat == "tv" || cat == "music" || (cat == "" && (r.Res > 0 || r.Kind == "episode" || r.Kind == "season" || r.Kind == "movie"));   // software / games: a setup.exe is normal
            // 1. a program in the file list (when the indexer gave the files, or the .torrent was fetched)
            if (s.Files != null && s.Files.Count > 0)
            {
                var bad = s.Files.Where(x => { var e = Media.Ext(x.Path); return e == ".scr" || e == ".lnk" || e == ".pif" || (mediaLike && BadInList.Contains(e)); }).ToList();
                if (bad.Count > 0) f.Add("Has a program in it: " + Path.GetFileName(bad[0].Path) + (bad.Count > 1 ? " (+" + (bad.Count - 1) + " more)" : "") + " - a video or music download never needs one");
                else foreach (var x in Media.Suspicious(t, s.Files).Take(1)) f.Add(x[1] + ": " + Path.GetFileName(x[0]));
            }
            if (Regex.IsMatch(t, @"(?i)\.(exe|scr|lnk|pif|msi|bat|cmd|vbs)\s*$") || Regex.IsMatch(t, @"(?i)\.(mkv|mp4|avi)\.(exe|scr|lnk)\b")) f.Add("The name ends like a program, not a video");
            // 2. a password-protected archive (the classic "pay for the password" scam)
            if (Regex.IsMatch(t, @"(?i)\b(password(ed)?|passw(or)?d[ ._-]?protected|pw[ ._-]?protected|need(s)?[ ._-]?(a[ ._-]?)?password|pass[ ._-]?in[ ._-]?(txt|rar)|unlock[ ._-]?code)\b")) f.Add("The name hints at a password-protected archive");
            // 3. far too small for what it claims
            var min = ExpectedMin(r, cat);
            if (min.HasValue && s.Size > 0 && s.Size < min.Value / 4) f.Add("Far too small for " + r.ResText + (r.Kind == "episode" ? " (an episode)" : r.Kind == "movie" || cat == "movies" ? " (a movie)" : "") + ": " + Gb(s.Size));
            // 4. brand-new upload with too-good-to-be-true seeders
            if (s.Published.HasValue && s.Seeders >= 1000 && (nowUtc - s.Published.Value).TotalHours < 3 && (nowUtc - s.Published.Value).TotalHours > -1) f.Add("Brand-new upload (" + Math.Max(1, (int)(nowUtc - s.Published.Value).TotalMinutes) + " min old) with " + s.Seeders.ToString("N0") + " seeders - too good to be true");
            // 5. "codec pack" style names
            if (Regex.IsMatch(t, @"(?i)\b(codecs?[ ._-]?(pack|installer|setup|update)|k[ ._-]?lite|install(er)?[ ._-]?(the[ ._-]?)?codecs?|(media|video)?[ ._-]?player[ ._-]?(setup|installer)|codec[ ._-]?required|needs?[ ._-]?codec)\b")) f.Add("A \"codec pack\" style name - videos never need one");
            return f;
        }

        // the smart score: health + your profile + size sanity + indexer reliability (flags are separate and win over the score)
        public static void Score(SearchResult s, SearchProfile p, double reliability, DateTime nowUtc)
        {
            var r = s.R = s.R ?? Parse(s.Title); var why = s.Why; why.Clear(); s.Notes.Clear(); double score = 0;
            // health
            if (s.Seeders < 0) { score += 8; why.Add("seeders not known +8"); }
            else if (s.Seeders == 0) { score -= 40; why.Add("no seeders -40"); s.Notes.Add("No seeders"); }
            else { var h = Math.Min(70, 20 * Math.Log10(1 + s.Seeders)); score += h; why.Add(s.Seeders + " seeders +" + h.ToString("0")); }
            if (s.Peers > s.Seeders && s.Seeders >= 0) { var lp = Math.Min(8, 3 * Math.Log10(1 + s.Peers - s.Seeders)); score += lp; }
            // quality vs. the profile
            var want = p.Resolution == "2160p" ? 2160 : p.Resolution == "1080p" ? 1080 : p.Resolution == "720p" ? 720 : p.Resolution == "480p" ? 480 : 0;
            if (r.Res == 0) { score += 5; why.Add("resolution not said +5"); }
            else if (want == 0) { var q = Step(r.Res) * 12; score += q; why.Add(r.ResText + " +" + q); }
            else { int d = Math.Abs(Step(r.Res) - Step(want)); var q = d == 0 ? 50 : Math.Max(-10, 50 - 22 * d); score += q; why.Add(r.ResText + (d == 0 ? " (preferred)" : "") + (q >= 0 ? " +" : " ") + q); }
            var sr = SourceRank(r.Source); score += sr;
            if (!string.IsNullOrEmpty(p.Source) && r.Source == p.Source) { score += 25; why.Add(r.Source + " (preferred) +" + (25 + sr)); } else if (r.Source.Length > 0 && !r.Low) why.Add(r.Source + " +" + sr);
            if (!string.IsNullOrEmpty(p.Codec) && r.Codec == p.Codec) { score += 10; why.Add(r.Codec + " (preferred) +10"); }
            bool avoided = RssMatch.Avoided(p.Avoid, s.Title);
            if (avoided) { score -= 300; why.Add("avoided -300"); s.Notes.Add("Avoided (" + AvoidedWord(p.Avoid, s.Title) + ")"); }
            else if (r.Low) { score -= 150; why.Add(r.Source + " (low quality) -150"); s.Notes.Add(r.Source + " - recorded in a cinema / a screener"); }
            // size
            var min = ExpectedMin(r, Torznab.CatOf(s.Cats));
            if (s.Size > 0 && p.MaxGB > 0 && s.Size > p.MaxGB * 1024 * 1024 * 1024) { score -= 120; why.Add("over your " + p.MaxGB.ToString("0.#") + " GB max -120"); s.Notes.Add("Over your " + p.MaxGB.ToString("0.#") + " GB max"); }
            if (min.HasValue && s.Size > 0)
            {
                if (s.Size < min.Value / 2) { score -= 40; why.Add("small for " + r.ResText + " -40"); }
                else if (s.Size > min.Value * 12) { score -= 10; why.Add("unusually big -10"); }
                else { score += 6; why.Add("size fits +6"); }
            }
            // the indexer
            var rel = 20 * Math.Max(0, Math.Min(1, reliability)); score += rel; why.Add("indexer " + (reliability * 100).ToString("0") + "% reliable +" + rel.ToString("0"));
            s.Score = Math.Round(score, 1);
        }
        static string AvoidedWord(string avoid, string title)
        {
            foreach (var a in (avoid ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)) if (RssMatch.Avoided(a, title)) return a;
            return "a word you avoid";
        }
        // everything, ranked: flagged results last (with their reasons), then by score; duplicates (same info hash) once
        public static List<SearchResult> Rank(IEnumerable<SearchResult> all, SearchProfile p, Func<string, double> reliability, DateTime nowUtc)
        {
            var list = new List<SearchResult>(); var hashes = new Dictionary<string, SearchResult>();
            foreach (var s in all)
            {
                Score(s, p, reliability == null ? 0.8 : reliability(s.FeedId), nowUtc);
                s.Flags = Flags(s, nowUtc);
                SearchResult had;
                if (s.InfoHash != null && hashes.TryGetValue(s.InfoHash, out had))
                {
                    // the same torrent from two indexers: keep the one with more seeders (and its link)
                    if (s.Seeders > had.Seeders) { list[list.IndexOf(had)] = s; hashes[s.InfoHash] = s; }
                    continue;
                }
                if (s.InfoHash != null) hashes[s.InfoHash] = s;
                list.Add(s);
            }
            return list.OrderBy(s => s.Flagged ? 1 : 0).ThenByDescending(s => s.Score).ThenByDescending(s => s.Seeders).ToList();
        }
    }

    // ---------------- the engine: indexers, searching (over the VPN only), downloading a result
    public class SearchEngine
    {
        public SearchStore Data; readonly string file; readonly Core core; readonly Rss rss;
        public readonly object Lock = new object();
        public event Action<string> Log;
        // one request over the VPN (no redirects followed). Default: VpnFetch.Fetch with the engine's VPN address; throws while it's off.
        public Func<string, TimeSpan, VpnFetch.Reply> Http;
        public SearchOutcome Last;
        readonly Dictionary<string, SearchResult> tokens = new Dictionary<string, SearchResult>();
        int counter;

        public SearchEngine(Core core, Rss rss, string dir)
        {
            this.core = core; this.rss = rss; file = dir == null ? null : Path.Combine(dir, "search.json");
            try { if (file != null && File.Exists(file)) using (var f = File.OpenRead(file)) Data = (SearchStore)new DataContractJsonSerializer(typeof(SearchStore)).ReadObject(f); } catch { Data = null; }
            if (Data == null) Data = new SearchStore(); Data.Fix();
            Http = (url, timeout) =>
            {
                if (core == null || !core.EngineUp) throw new VpnFetch.LockedException();
                return VpnFetch.Fetch(url, core.BoundIp, core.BoundDns, 20 * 1024 * 1024, timeout);
            };
        }
        public bool VpnUp { get { return core != null && core.EngineUp; } }
        void L(string s) { var h = Log; if (h != null) try { h(Torznab.Redact(s)); } catch { } }
        public void Save()
        {
            if (file == null || TorSettings.Frozen) return;
            lock (Lock)
            {
                try
                {
                    Data.Fix(); Directory.CreateDirectory(Path.GetDirectoryName(file)); var tmp = file + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(SearchStore)).WriteObject(f, Data);
                    for (int i = 0; ; i++) { try { if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }
        public List<TzFeed> EnabledFeeds() { lock (Lock) return Data.Indexers.Where(i => i.Enabled).SelectMany(i => i.Feeds.Where(f => f.Enabled)).ToList(); }
        public int FeedCount { get { return EnabledFeeds().Count; } }
        Indexer OwnerOf(TzFeed f) { lock (Lock) return Data.Indexers.FirstOrDefault(i => i.Feeds.Contains(f)); }
        public string KeyOf(Indexer x) { return Secret.Unprotect(x.Key); }

        // ---- your indexers
        public string AddIndexer(string kind, string name, string baseUrl, string key, out Indexer added)
        {
            added = null; Uri u; baseUrl = (baseUrl ?? "").Trim();
            if (kind != "prowlarr" && kind != "jackett" && kind != "torznab") return "Pick Prowlarr, Jackett or a Torznab feed.";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return "That isn't an address (http://... or https://...).";
            if (!string.IsNullOrEmpty(u.Query) && Regex.IsMatch(u.Query, "(?i)apikey=")) return "Leave the API key out of the address - put it in the API key box (it's kept encrypted).";
            if (string.IsNullOrWhiteSpace(key)) return "The API key is needed (Prowlarr: Settings > General; Jackett: the top of its page).";
            if (kind != "torznab") baseUrl = baseUrl.TrimEnd('/');
            lock (Lock) if (Data.Indexers.Any(i => string.Equals(i.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))) return "That one is already in the list.";
            var x = new Indexer { Id = SearchStore.NewId(), Kind = kind, BaseUrl = baseUrl, Key = Secret.Protect(key.Trim()), Name = string.IsNullOrWhiteSpace(name) ? (kind == "prowlarr" ? "Prowlarr" : kind == "jackett" ? "Jackett" : u.Host) : name.Trim() };
            if (kind == "torznab") x.Feeds.Add(new TzFeed { Id = SearchStore.NewId(), Name = x.Name, Url = baseUrl });
            lock (Lock) Data.Indexers.Add(x);
            Save(); added = x; return null;
        }
        public void RemoveIndexer(string id) { lock (Lock) Data.Indexers.RemoveAll(i => i.Id == id); Save(); }
        public void SetKey(Indexer x, string key) { if (string.IsNullOrWhiteSpace(key)) return; lock (Lock) x.Key = Secret.Protect(key.Trim()); Save(); }

        VpnFetch.Reply Get(string url, TimeSpan timeout, int redirects = 3)
        {
            for (int i = 0; ; i++)
            {
                var r = Http(url, timeout);
                if (r.Location == null) return r;
                if (r.Location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || i >= redirects) return r;
                url = r.Location;
            }
        }
        static string Why(Exception e)
        {
            var b = e is AggregateException ? e.GetBaseException() : e;
            if (b is TorznabException) return b.Message;
            if (b is XmlException) return "That isn't a Torznab answer (" + Family.OneLine(b.Message) + ").";
            return Torznab.Redact(VpnFetch.Why(b));
        }

        // finds the indexers inside Prowlarr / Jackett and asks each feed what it can do (t=caps) - over the VPN only
        public async Task<string> Sync(Indexer x)
        {
            if (!VpnUp) return "Waiting for the VPN - indexers are only contacted over it.";
            var key = KeyOf(x); if (key == null) return "The API key can't be read (was it saved by another Windows user?) - enter it again.";
            var timeout = TimeSpan.FromSeconds(Data.Profile.TimeoutSec);
            string err = null;
            try
            {
                List<TzFeed> found = null;
                if (x.Kind == "prowlarr")
                    found = Torznab.ProwlarrIndexers(await Task.Run(() => Get(Torznab.Url(x.BaseUrl + "/api/v1/indexer", key, new KeyValuePair<string, string>[0]), timeout).Body).ConfigureAwait(false), x.BaseUrl);
                else if (x.Kind == "jackett")
                {
                    try { found = Torznab.JackettIndexers(await Task.Run(() => Get(Torznab.Url(x.BaseUrl + "/api/v2.0/indexers/all/results/torznab/api", key, new[] { new KeyValuePair<string, string>("t", "indexers"), new KeyValuePair<string, string>("configured", "true") }), timeout).Body).ConfigureAwait(false), x.BaseUrl); }
                    catch (TorznabException) { throw; }
                    catch { found = null; }
                    if (found == null || found.Count == 0) found = new List<TzFeed> { new TzFeed { Id = "j-all", Name = x.Name + " (all indexers)", Url = x.BaseUrl + "/api/v2.0/indexers/all/results/torznab/api" } };   // an older Jackett: its "all" endpoint
                }
                if (found != null)
                    lock (Lock)
                    {
                        // keep what you switched off and each one's track record
                        foreach (var f in found) { var old = x.Feeds.FirstOrDefault(o => o.Id == f.Id); if (old != null) { f.Enabled = f.Enabled && old.Enabled; f.Ok = old.Ok; f.Fail = old.Fail; f.AvgMs = old.AvgMs; } }
                        x.Feeds = found;
                    }
                List<TzFeed> feeds; lock (Lock) feeds = x.Feeds.Where(f => !f.Caps).ToList();
                var capsTasks = feeds.Select(f => Task.Run(() => { try { Torznab.ReadCaps(Get(Torznab.Url(f.Url, key, new[] { new KeyValuePair<string, string>("t", "caps") }), timeout).Body, f); return (string)null; } catch (Exception e) { return f.Name + ": " + Why(e); } })).ToList();
                var errs = (await Task.WhenAll(capsTasks).ConfigureAwait(false)).Where(e => e != null).ToList();
                if (errs.Count > 0 && errs.Count == feeds.Count && feeds.Count > 0) err = errs[0];
            }
            catch (Exception e) { err = Why(e); }
            lock (Lock) { x.LastSync = Family.Time(DateTime.Now); x.LastError = err; }
            Save(); L("search sync " + x.Id + ": " + (err ?? x.Feeds.Count + " indexers"));
            return err;
        }

        // ---- searching: every enabled indexer at once, each with its own timeout; nothing at all while the VPN is off
        public async Task<SearchOutcome> Run(SearchQuery q, DateTime nowUtc)
        {
            var o = new SearchOutcome { Query = q }; var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(q.Text) && q.ImdbId == null && q.TmdbId == null) { o.Error = "Type what to look for."; return o; }
            List<Indexer> idx; lock (Lock) idx = Data.Indexers.Where(i => i.Enabled).ToList();
            if (idx.Count == 0) { o.Error = "Add your Prowlarr or Jackett (or a Torznab feed) first - Search only asks your own indexers."; return o; }
            if (!VpnUp)
            {
                o.Locked = true; o.Error = "Waiting for the VPN - searches only go out over it, so nothing was sent.";
                foreach (var f in EnabledFeeds()) o.Status.Add(new FeedStatus { FeedId = f.Id, Name = f.Name, State = "locked", Error = "VPN off" });
                Last = o; return o;
            }
            // indexers whose feeds (or caps) aren't known yet: find them first
            foreach (var i in idx.Where(i => i.Feeds.Count == 0 || i.Feeds.Any(f => !f.Caps)).ToList()) await Sync(i).ConfigureAwait(false);
            var feeds = EnabledFeeds();
            if (feeds.Count == 0) { o.Error = "None of your indexers are switched on (or found) - check them in Indexers."; Last = o; return o; }
            var timeout = TimeSpan.FromSeconds(Data.Profile.TimeoutSec);
            var keys = new Dictionary<string, string>(); lock (Lock) foreach (var i in idx) foreach (var f in i.Feeds) keys[f.Id] = KeyOf(i);
            var jobs = feeds.Select(f => new { F = f, T = Task.Run(() => Ask(f, keys.ContainsKey(f.Id) ? keys[f.Id] : null, q, timeout)) }).ToList();
            await Task.WhenAll(jobs.Select(j => Task.WhenAny(j.T, Task.Delay(timeout + TimeSpan.FromMilliseconds(400))))).ConfigureAwait(false);
            var results = new List<SearchResult>();
            foreach (var j in jobs)
            {
                FeedStatus st;
                if (!j.T.IsCompleted) { st = new FeedStatus { FeedId = j.F.Id, Name = j.F.Name, State = "timeout", Error = "No answer within " + Data.Profile.TimeoutSec + " s", Ms = (long)timeout.TotalMilliseconds }; Track(j.F, false, timeout.TotalMilliseconds); }
                else { st = j.T.Result.Item1; if (j.T.Result.Item2 != null) results.AddRange(j.T.Result.Item2); }
                o.Status.Add(st);
            }
            // what you already have / RSS already grabbed
            HashSet<string> have; lock (Lock) have = core == null ? new HashSet<string>() : new HashSet<string>(core.All().Select(e => e.Id));
            HashSet<string> grabbed = new HashSet<string>(); if (rss != null) lock (rss.Lock) foreach (var g in rss.Data.Grabs) if (g.Key != null) grabbed.Add(g.Key);
            foreach (var r in results) { r.InList = r.InfoHash != null && have.Contains(r.InfoHash); var k = RssMatch.Key(r.Title); r.Grabbed = k != null && grabbed.Contains(k); }
            var p = Data.Profile;
            var ranked = Ranker.Rank(results, p, id => { var f = feeds.FirstOrDefault(x => x.Id == id); return f == null ? 0.8 : f.Reliability; }, nowUtc);
            o.Results = ranked.Where(r => p.MinSeeders <= 0 || r.Seeders < 0 || r.Seeders >= p.MinSeeders || r.Flagged).ToList();
            lock (tokens) { tokens.Clear(); foreach (var r in o.Results) { r.Token = "r" + (++counter); tokens[r.Token] = r; } }
            if (p.Inspect) await InspectTop(o, 8, nowUtc).ConfigureAwait(false);
            Save();
            o.Ms = sw.ElapsedMilliseconds; Last = o;
            L("search: " + o.Results.Count + " results from " + o.Status.Count(s => s.State == "ok") + "/" + o.Status.Count + " indexers in " + o.Ms + " ms");
            return o;
        }
        Tuple<FeedStatus, List<SearchResult>> Ask(TzFeed f, string key, SearchQuery q, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew(); var st = new FeedStatus { FeedId = f.Id, Name = f.Name };
            try
            {
                if (key == null) throw new IOException("The API key can't be read - enter it again.");
                var r = Get(Torznab.Url(f.Url, key, Torznab.Params(q, f)), timeout);
                var list = Torznab.Results(r.Body ?? new byte[0], f);
                foreach (var x in list) x.SourceId = f.Id;
                st.State = "ok"; st.Count = list.Count; st.Ms = sw.ElapsedMilliseconds; Track(f, true, st.Ms);
                return Tuple.Create(st, list);
            }
            catch (Exception e)
            {
                var b = e is AggregateException ? e.GetBaseException() : e;
                st.State = b is System.Threading.Tasks.TaskCanceledException || b is OperationCanceledException ? "timeout" : "error"; st.Error = st.State == "timeout" ? "No answer within " + (int)timeout.TotalSeconds + " s" : Why(e); st.Ms = sw.ElapsedMilliseconds;
                Track(f, false, st.Ms);
                return Tuple.Create(st, (List<SearchResult>)null);
            }
        }
        void Track(TzFeed f, bool ok, double ms) { lock (Lock) { if (ok) f.Ok++; else f.Fail++; if (f.Ok + f.Fail > 200) { f.Ok = f.Ok * 100 / 200; f.Fail = f.Fail * 100 / 200; } f.AvgMs = f.AvgMs <= 0 ? ms : f.AvgMs * 0.7 + ms * 0.3; } }
        public SearchResult ByToken(string token) { lock (tokens) { SearchResult r; return token != null && tokens.TryGetValue(token, out r) ? r : null; } }

        // ---- a result's .torrent (over the VPN): the magnet link it redirects to, or the file
        public string Fetch(SearchResult r, out byte[] torrent, out string magnet)
        {
            torrent = null; magnet = r.Magnet;
            if (r.Link == null) return magnet != null ? null : "This result has no link.";
            if (!VpnUp) return "Waiting for the VPN - the .torrent is only fetched over it.";
            try
            {
                var rep = Get(r.Link, TimeSpan.FromSeconds(Math.Max(15, Data.Profile.TimeoutSec)));
                if (rep.Location != null && rep.Location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) { magnet = rep.Location; return null; }
                if (rep.Body == null || rep.Body.Length == 0 || rep.Body[0] != (byte)'d') return "The indexer didn't send a .torrent file.";
                torrent = rep.Body; return null;
            }
            catch (Exception e) { return Why(e); }
        }
        // check a result's file list (fetches the .torrent over the VPN); its flags are worked out again
        public string Inspect(SearchResult r, DateTime nowUtc)
        {
            byte[] t; string m; var err = Fetch(r, out t, out m);
            if (err != null) return err;
            if (t == null) return "It's a magnet link - its file list arrives once it starts (the Add window still checks it).";
            try { var tor = MonoTorrent.Torrent.Load(t); r.Files = tor.Files.Select(x => new TFile(x.Path, x.Length)).ToList(); if (r.Size <= 0) r.Size = tor.Size; r.InfoHash = r.InfoHash ?? Core.IdOf(tor.InfoHashes); }
            catch (Exception e) { return "That isn't a valid .torrent (" + Family.OneLine(e.Message) + ")."; }
            r.Flags = Ranker.Flags(r, nowUtc);
            return null;
        }
        async Task InspectTop(SearchOutcome o, int n, DateTime nowUtc)
        {
            var top = o.Results.Where(r => !r.Flagged && r.Link != null).Take(n).ToList();
            await Task.WhenAll(top.Select(r => Task.Run(() => Inspect(r, nowUtc)))).ConfigureAwait(false);
            o.Results = o.Results.OrderBy(s => s.Flagged ? 1 : 0).ThenByDescending(s => s.Score).ThenByDescending(s => s.Seeders).ToList();
        }
        // ---- straight into the list (the pipe / tests; the window uses the Add window): VPN lock, Defender scan and categories as always
        public Entry Download(SearchResult r, string category, string save, bool start, out string err)
        {
            byte[] t; string m; err = Fetch(r, out t, out m);
            if (err != null) return null;
            var cat = category ?? r.Category;
            return t != null ? core.AddTorrentBytes(t, save, cat, start, out err) : core.AddMagnet(m, save, cat, start, out err);
        }

        // ---- "Add to RSS rule": a rule for the show / movie of a result, preferring its quality
        public static RssRule RuleFrom(SearchResult s)
        {
            var r = s.R ?? Ranker.Parse(s.Title);
            var name = string.IsNullOrWhiteSpace(r.Title) ? Regex.Replace(s.Title ?? "Rule", @"[._]", " ") : r.Title;
            var words = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
            if (r.Kind == "movie" && r.Year > 0) words += " " + r.Year;
            return new RssRule { Name = name + (r.Kind == "movie" && r.Year > 0 ? " (" + r.Year + ")" : ""), MustContain = words, Prefer = r.ResText, Category = Torznab.HallowCat(s.Cats, r), Episodes = true, Avoid = RssRule.DefaultAvoid };
        }

        // ---- the pipe's answer: the top N as JSON (never an API key; links that carry one are left out)
        public static Dictionary<string, object> Json(SearchOutcome o, int n)
        {
            var d = new Dictionary<string, object> { { "query", o.Query == null ? null : o.Query.Text }, { "mode", o.Query == null ? null : o.Query.Mode }, { "locked", o.Locked }, { "ms", o.Ms }, { "total", o.Results.Count }, { "count", Math.Min(n, o.Results.Count) } };
            d["indexers"] = o.Status.Select(s => new Dictionary<string, object> { { "name", s.Name }, { "state", s.State }, { "count", s.Count }, { "ms", s.Ms }, { "error", s.Error } }).ToList();
            d["results"] = o.Results.Take(n).Select(r => new Dictionary<string, object> {
                { "id", r.Token }, { "title", r.Title }, { "indexer", r.IndexerName }, { "size", r.Size }, { "seeders", r.Seeders }, { "peers", r.Peers },
                { "score", r.Score }, { "resolution", r.R.ResText }, { "source", r.R.Source }, { "codec", r.R.Codec }, { "hdr", r.R.Hdr }, { "audio", r.R.Audio }, { "group", r.R.Group },
                { "season", r.R.Season >= 0 ? (object)r.R.Season : null }, { "episode", r.R.Episode >= 0 ? (object)r.R.Episode : null }, { "category", r.Category },
                { "published", r.Published.HasValue ? Family.Time(r.Published.Value) : null }, { "flags", r.Flags }, { "notes", r.Notes },
                { "magnet", r.Magnet != null && !Regex.IsMatch(r.Magnet, "(?i)apikey") ? r.Magnet : null }, { "infoHash", r.InfoHash }, { "inList", r.InList } }).ToList();
            return d;
        }
    }
}
