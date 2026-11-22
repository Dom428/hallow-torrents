// Hallow Torrents - RSS auto-download (v1.1).
//   * Feeds you add are fetched every N minutes - ONLY over the VPN: the request goes out from a socket bound to the VPN address with
//     its name looked up over the VPN (VpnFetch / BoundHttp, the same as the trackers). While the VPN is off nothing is fetched.
//   * Rules: must contain / must not contain (words, "|" for "or", "*" wildcards) or regular expressions, which feeds, category,
//     save folder, a quality preference (e.g. prefer 1080p) and words to avoid (CAM, TS...).
//   * Episode tracking: SxxEyy (and 1x02), dates (2026.11.21) and season packs - the same episode is never grabbed twice, across all
//     rules. Optionally a better quality replaces it within N hours (the old one leaves the list; its files go to the Recycle Bin).
//   * Kept in %APPDATA%\HallowTorrents\rss.json.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace HallowTorrents
{
    [DataContract]
    public class RssFeed
    {
        [DataMember] public string Id, Url, Name;
        [DataMember] public int Minutes;                 // how often (5 - 1440)
        [DataMember] public bool Enabled;
        [DataMember] public string LastFetch, LastError; [DataMember] public int LastCount;
        public RssFeed() { Minutes = 30; Enabled = true; }
        [OnDeserializing] void D(StreamingContext c) { Minutes = 30; Enabled = true; }
        public string Label { get { return string.IsNullOrWhiteSpace(Name) ? Url : Name; } }
    }

    [DataContract]
    public class RssRule
    {
        [DataMember] public string Id, Name;
        [DataMember] public string MustContain, MustNotContain;   // words ("|" = or, "*" = anything) - or regular expressions when Regex
        [DataMember] public bool Regex;
        [DataMember] public List<string> Feeds;                    // feed ids (null / empty = every feed)
        [DataMember] public string Category, SaveFolder;          // null = the category's / the default folder
        [DataMember] public string Prefer;                         // e.g. "1080p" - preferred over every other quality
        [DataMember] public string Avoid;                          // e.g. "CAM TS TELESYNC" - never grabbed
        [DataMember] public bool Enabled, Episodes, Paused;        // Episodes = episode tracking; Paused = add them paused
        [DataMember] public int UpgradeHours;                      // 0 = never replace; N = a better quality within N hours replaces it
        public RssRule() { Enabled = true; Episodes = true; Avoid = DefaultAvoid; }
        [OnDeserializing] void D(StreamingContext c) { Enabled = true; Episodes = true; }
        public const string DefaultAvoid = "CAM HDCAM TS HDTS TELESYNC TC TELECINE SCR SCREENER";
    }

    [DataContract]
    public class RssGrab
    {
        [DataMember] public string Key, Title, Rule, EntryId, At;   // At = first grabbed (the upgrade window starts here)
        [DataMember] public int Score; [DataMember] public bool Upgraded;
    }

    [DataContract]
    public class RssStore
    {
        [DataMember] public List<RssFeed> Feeds;
        [DataMember] public List<RssRule> Rules;
        [DataMember] public List<string> Seen;     // "<rule id>|<item id>" handled already
        [DataMember] public List<RssGrab> Grabs;   // what was grabbed (newest last)
        public void Fix()
        {
            if (Feeds == null) Feeds = new List<RssFeed>(); if (Rules == null) Rules = new List<RssRule>();
            if (Seen == null) Seen = new List<string>(); if (Grabs == null) Grabs = new List<RssGrab>();
            Feeds.RemoveAll(f => f == null || string.IsNullOrWhiteSpace(f.Url)); Rules.RemoveAll(r => r == null);
            foreach (var f in Feeds) { if (f.Id == null) f.Id = Guid.NewGuid().ToString("N").Substring(0, 8); f.Minutes = Math.Max(5, Math.Min(1440, f.Minutes)); }
            foreach (var r in Rules) { if (r.Id == null) r.Id = Guid.NewGuid().ToString("N").Substring(0, 8); if (string.IsNullOrWhiteSpace(r.Name)) r.Name = "Rule"; if (r.UpgradeHours < 0) r.UpgradeHours = 0; }
            if (Seen.Count > 5000) Seen.RemoveRange(0, Seen.Count - 5000);
            if (Grabs.Count > 2000) Grabs.RemoveRange(0, Grabs.Count - 2000);
        }
    }

    public class RssItem { public string Title, Link, Id, Torrent; public DateTime? Date; }
    public class RssDecision { public RssItem Item; public RssRule Rule; public string Key, SeenKey; public int Score; public RssGrab Replaces; }

    // ---------------- reading a feed (RSS 2.0 and Atom; torrent links as enclosures, links or <torrent:magnetURI>)
    public static class RssParse
    {
        public static List<RssItem> Items(byte[] data, out string title)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 40000000 };
            XDocument doc; using (var r = XmlReader.Create(new MemoryStream(data), settings)) doc = XDocument.Load(r);
            title = null;
            var top = doc.Root == null ? null : doc.Root.Name.LocalName == "rss" ? doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel") : doc.Root;
            if (top != null) { var t = top.Elements().FirstOrDefault(e => e.Name.LocalName == "title"); if (t != null) title = t.Value.Trim(); }
            var list = new List<RssItem>();
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "item" || e.Name.LocalName == "entry"))
            {
                var it = new RssItem(); string link = null, enclosure = null, magnet = null, hash = null;
                foreach (var c in el.Elements())
                {
                    var n = c.Name.LocalName;
                    if (n == "title") it.Title = c.Value.Trim();
                    else if (n == "link")
                    {
                        var href = (string)c.Attribute("href"); var type = (string)c.Attribute("type"); var rel = (string)c.Attribute("rel");
                        if (href == null) { if (link == null) link = c.Value.Trim(); }
                        else if (type == "application/x-bittorrent" || rel == "enclosure") { if (enclosure == null) enclosure = href.Trim(); }
                        else if (link == null) link = href.Trim();
                    }
                    else if (n == "enclosure") { var u = (string)c.Attribute("url"); if (u != null && enclosure == null) enclosure = u.Trim(); }
                    else if (string.Equals(n, "magnetURI", StringComparison.OrdinalIgnoreCase)) magnet = c.Value.Trim();
                    else if (string.Equals(n, "infoHash", StringComparison.OrdinalIgnoreCase)) hash = c.Value.Trim();
                    else if (n == "guid" || n == "id") it.Id = c.Value.Trim();
                    else if (n == "pubDate" || n == "published" || n == "updated") { DateTime d; if (DateTime.TryParse(c.Value.Trim().Replace(" GMT", " +0000"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out d)) it.Date = d; }
                }
                if (magnet == null && link != null && link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) magnet = link;
                if (magnet == null && enclosure != null && enclosure.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) magnet = enclosure;
                if (magnet == null && enclosure == null && hash != null && Regex.IsMatch(hash, "^[0-9a-fA-F]{40}$")) magnet = "magnet:?xt=urn:btih:" + hash.ToLowerInvariant() + "&dn=" + Uri.EscapeDataString(it.Title ?? "");
                it.Link = link; it.Torrent = magnet ?? enclosure ?? link;
                if (!string.IsNullOrWhiteSpace(it.Title)) list.Add(it);
            }
            return list;
        }
    }

    // ---------------- the rules (pure, tested)
    public static class RssMatch
    {
        static readonly TimeSpan rxTime = TimeSpan.FromMilliseconds(250);
        public static bool Matches(RssRule r, string title)
        {
            title = title ?? "";
            if (r.Regex)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(r.MustContain) && !System.Text.RegularExpressions.Regex.IsMatch(title, r.MustContain, RegexOptions.IgnoreCase, rxTime)) return false;
                    if (!string.IsNullOrWhiteSpace(r.MustNotContain) && System.Text.RegularExpressions.Regex.IsMatch(title, r.MustNotContain, RegexOptions.IgnoreCase, rxTime)) return false;
                    return true;
                }
                catch { return false; }   // a broken expression matches nothing
            }
            var t = Norm(title);
            if (!string.IsNullOrWhiteSpace(r.MustContain) && !Alts(r.MustContain).Any(a => a.All(w => Has(t, w)))) return false;
            if (!string.IsNullOrWhiteSpace(r.MustNotContain) && Alts(r.MustNotContain).Any(a => a.All(w => Has(t, w)))) return false;
            return true;
        }
        public static string RegexError(string rx)
        {
            if (string.IsNullOrWhiteSpace(rx)) return null;
            try { new Regex(rx, RegexOptions.IgnoreCase, rxTime); return null; } catch (Exception e) { return e.Message; }
        }
        static string Norm(string s) { return " " + System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), @"[\s._\-\[\]()]+", " ").Trim() + " "; }
        static List<List<string>> Alts(string expr)
        {
            return expr.Split('|').Select(a => Norm(a).Trim().Split(' ').Where(w => w.Length > 0).ToList()).Where(a => a.Count > 0).ToList();
        }
        static bool Has(string t, string w)
        {
            if (w.IndexOf('*') < 0 && w.IndexOf('?') < 0) return t.Contains(w);
            var rx = System.Text.RegularExpressions.Regex.Escape(w).Replace(@"\*", ".*").Replace(@"\?", ".");
            return System.Text.RegularExpressions.Regex.IsMatch(t, rx, RegexOptions.None, rxTime);
        }
        // a word to avoid (whole words: "TS" never matches "TSUNAMI"; "HD-CAM" counts as "hdcam")
        public static bool Avoided(string avoid, string title)
        {
            if (string.IsNullOrWhiteSpace(avoid)) return false;
            var words = System.Text.RegularExpressions.Regex.Split((title ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0).ToList();
            var joined = new HashSet<string>(words); for (int i = 0; i + 1 < words.Count; i++) joined.Add(words[i] + words[i + 1]);
            return avoid.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(a => System.Text.RegularExpressions.Regex.Replace(a.ToLowerInvariant(), "[^a-z0-9]", "")).Where(a => a.Length > 0).Any(joined.Contains);
        }
        static readonly Regex dateRx = new Regex(@"(?<![0-9])(?<y>(?:19|20)\d\d)[ ._-](?<m>0[1-9]|1[0-2])[ ._-](?<d>0[1-9]|[12]\d|3[01])(?![0-9])");
        // what makes two releases "the same": show + SxxEyy, show + date, show + season pack, movie + year (null = nothing to track)
        public static string Key(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var m = Media.Parse(title);
            if (m.Kind == "episode" && Show(m.Title).Length > 0) return Show(m.Title) + "|S" + m.Season.ToString("00") + "E" + m.Episode.ToString("00") + (m.EpisodeEnd > 0 ? "-E" + m.EpisodeEnd.ToString("00") : "");
            var d = dateRx.Match(title);
            if (d.Success && d.Index > 0) { var show = Show(Media.Clean(title.Substring(0, d.Index))); if (show.Length > 0) return show + "|" + d.Groups["y"].Value + "-" + d.Groups["m"].Value + "-" + d.Groups["d"].Value; }
            if (m.Kind == "season" && Show(m.Title).Length > 0) return Show(m.Title) + "|S" + m.Season.ToString("00");
            if (m.Kind == "movie" && m.Year > 0 && Show(m.Title).Length > 0) return "movie|" + Show(m.Title) + "|" + m.Year;
            return null;
        }
        static string Show(string t) { return System.Text.RegularExpressions.Regex.Replace((t ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Trim(); }
        // higher is better: what you prefer first, then the resolution, then the source (remux > Blu-ray > WEB-DL > WEBRip > HDTV)
        public static int Quality(string title, string prefer)
        {
            var t = (title ?? "").ToLowerInvariant();
            int res = Rx(t, @"2160p|\b4k\b|\buhd\b") ? 4 : Rx(t, @"1080[pi]") ? 3 : Rx(t, @"720p") ? 2 : Rx(t, @"480p|576p|\bsd\b|dvdrip") ? 1 : 0;
            int src = Rx(t, @"\bremux\b") ? 5 : Rx(t, @"blu-?ray|\bbdrip\b|\bbrrip\b") ? 4 : Rx(t, @"\bweb[ ._-]?dl\b") ? 3 : Rx(t, @"\bwebrip\b|\bweb\b") ? 2 : Rx(t, @"\bhdtv\b") ? 1 : 0;
            int score = res * 10 + src + (Rx(t, @"\b(proper|repack)\b") ? 1 : 0);
            if (!string.IsNullOrWhiteSpace(prefer))
            {
                var words = System.Text.RegularExpressions.Regex.Split(t, "[^a-z0-9]+");
                if (prefer.Split(new[] { ',', ' ', ';', '|' }, StringSplitOptions.RemoveEmptyEntries).Any(p => words.Contains(System.Text.RegularExpressions.Regex.Replace(p.ToLowerInvariant(), "[^a-z0-9]", "")))) score += 1000;
            }
            return score;
        }
        static bool Rx(string t, string rx) { return System.Text.RegularExpressions.Regex.IsMatch(t, rx); }
        public static string QualityText(string title)
        {
            var t = (title ?? "").ToLowerInvariant();
            return Rx(t, @"2160p|\b4k\b|\buhd\b") ? "2160p" : Rx(t, @"1080[pi]") ? "1080p" : Rx(t, @"720p") ? "720p" : Rx(t, @"480p|576p") ? "SD" : "";
        }
    }

    // ---------------- the engine: feeds, rules, what was grabbed
    public class Rss
    {
        public RssStore Data; readonly string file; readonly Core core;
        public readonly object Lock = new object();
        // url -> bytes. Over the VPN only (VpnFetch with the engine's VPN address); throws while the VPN is off.
        public Func<string, byte[]> Fetch;
        public event Action<string, string> Notify;
        public event Action<string> Log;
        public readonly Dictionary<string, List<RssItem>> Latest = new Dictionary<string, List<RssItem>>();   // feed id -> items (last fetch)
        int busy;

        public Rss(Core core, string dir)
        {
            this.core = core; file = dir == null ? null : Path.Combine(dir, "rss.json");
            try { if (file != null && File.Exists(file)) using (var f = File.OpenRead(file)) Data = (RssStore)new DataContractJsonSerializer(typeof(RssStore)).ReadObject(f); } catch { Data = null; }
            if (Data == null) Data = new RssStore(); Data.Fix();
            Fetch = url =>
            {
                if (core == null || !core.EngineUp) throw new VpnFetch.LockedException();
                return VpnFetch.Get(url, core.BoundIp, core.BoundDns, 16 * 1024 * 1024);
            };
        }
        void L(string s) { var h = Log; if (h != null) try { h(s); } catch { } }
        public void Save()
        {
            if (file == null || TorSettings.Frozen) return;
            lock (Lock)
            {
                try
                {
                    Data.Fix(); Directory.CreateDirectory(Path.GetDirectoryName(file)); var tmp = file + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(RssStore)).WriteObject(f, Data);
                    for (int i = 0; ; i++) { try { if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }

        // ---- your changes
        public string AddFeed(string url, int minutes)
        {
            Uri u; url = (url ?? "").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return "That isn't a feed address (http:// or https://).";
            lock (Lock)
            {
                if (Data.Feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase))) return "That feed is already in the list.";
                Data.Feeds.Add(new RssFeed { Id = Guid.NewGuid().ToString("N").Substring(0, 8), Url = url, Minutes = Math.Max(5, Math.Min(1440, minutes <= 0 ? 30 : minutes)) });
            }
            Save(); return null;
        }
        public void RemoveFeed(string id) { lock (Lock) { Data.Feeds.RemoveAll(f => f.Id == id); Latest.Remove(id); foreach (var r in Data.Rules) if (r.Feeds != null) r.Feeds.Remove(id); } Save(); }
        // a new or changed rule looks at the feeds' items again (episodes it already grabbed still count)
        public void SaveRule(RssRule r)
        {
            lock (Lock)
            {
                if (r.Id == null) r.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                var i = Data.Rules.FindIndex(x => x.Id == r.Id); if (i < 0) Data.Rules.Add(r); else Data.Rules[i] = r;
                Data.Seen.RemoveAll(s => s.StartsWith(r.Id + "|"));
            }
            Save();
        }
        public void RemoveRule(string id) { lock (Lock) { Data.Rules.RemoveAll(r => r.Id == id); Data.Seen.RemoveAll(s => s.StartsWith(id + "|")); } Save(); }
        public void Forget(RssGrab g) { lock (Lock) Data.Grabs.Remove(g); Save(); }

        // ---- deciding (no network): which items to add now, per rule, with episode tracking and upgrades
        public List<RssDecision> Decide(RssFeed feed, List<RssItem> items, DateTime now)
        {
            var result = new List<RssDecision>(); var chosen = new HashSet<string>();
            lock (Lock)
            {
                foreach (var r in Data.Rules.Where(x => x.Enabled && (x.Feeds == null || x.Feeds.Count == 0 || x.Feeds.Contains(feed.Id))).ToList())
                {
                    var cands = new List<RssDecision>();
                    foreach (var it in items)
                    {
                        if (string.IsNullOrWhiteSpace(it.Torrent)) continue;
                        var seen = r.Id + "|" + (it.Id ?? it.Torrent);
                        if (Data.Seen.Contains(seen)) continue;
                        if (!RssMatch.Matches(r, it.Title) || RssMatch.Avoided(r.Avoid, it.Title)) continue;
                        cands.Add(new RssDecision { Item = it, Rule = r, Key = r.Episodes ? RssMatch.Key(it.Title) : null, Score = RssMatch.Quality(it.Title, r.Prefer), SeenKey = seen });
                    }
                    foreach (var g in cands.GroupBy(c => c.Key ?? ("\u0001" + c.SeenKey)))
                    {
                        var best = g.OrderByDescending(c => c.Score).ThenByDescending(c => c.Item.Date ?? DateTime.MinValue).First();
                        foreach (var c in g) if (c != best) Seen(c.SeenKey);
                        if (best.Key == null) { result.Add(best); continue; }
                        if (chosen.Contains(best.Key)) { Seen(best.SeenKey); continue; }
                        var had = Data.Grabs.LastOrDefault(x => x.Key == best.Key);
                        if (had == null) { chosen.Add(best.Key); result.Add(best); continue; }
                        DateTime at; bool inWindow = r.UpgradeHours > 0 && DateTime.TryParse(had.At, out at) && (now - at).TotalHours <= r.UpgradeHours;
                        if (inWindow && best.Score > had.Score) { best.Replaces = had; chosen.Add(best.Key); result.Add(best); }
                        else Seen(best.SeenKey);   // the same episode again: never twice
                    }
                }
            }
            return result;
        }
        void Seen(string key) { if (!Data.Seen.Contains(key)) Data.Seen.Add(key); }

        // ---- fetching (over the VPN only) and adding
        public bool Busy { get { return busy != 0; } }
        public async Task<string> Refresh(RssFeed f)
        {
            string err = null;
            try
            {
                if (core == null || !core.EngineUp) { err = "Waiting for the VPN - feeds are only fetched over it."; return err; }
                byte[] b; try { b = await Task.Run(() => Fetch(f.Url)).ConfigureAwait(false); } catch (Exception e) { err = VpnFetch.Why(e); return err; }
                List<RssItem> items; string title;
                try { items = RssParse.Items(b, out title); } catch (Exception e) { err = "That isn't an RSS or Atom feed (" + Family.OneLine(e.Message) + ")."; return err; }
                lock (Lock) { Latest[f.Id] = items; f.LastCount = items.Count; if (string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(title)) f.Name = title.Length > 80 ? title.Substring(0, 80) : title; }
                var now = DateTime.Now;
                foreach (var d in Decide(f, items, now)) await Apply(d, now).ConfigureAwait(false);
                L("rss " + f.Id + ": " + items.Count + " items");
                return null;
            }
            finally
            {
                lock (Lock) { f.LastFetch = Family.Time(DateTime.Now); f.LastError = err; }
                Save();
            }
        }
        async Task<bool> Apply(RssDecision d, DateTime now)
        {
            string err = null; Entry e = null; var link = d.Item.Torrent;
            if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) e = core.AddMagnet(link, d.Rule.SaveFolder, d.Rule.Category, !d.Rule.Paused, out err);
            else
            {
                byte[] t; try { t = await Task.Run(() => Fetch(link)).ConfigureAwait(false); } catch (Exception ex) { L("rss get: " + VpnFetch.Why(ex)); return false; }   // tried again next time
                e = core.AddTorrentBytes(t, d.Rule.SaveFolder, d.Rule.Category, !d.Rule.Paused, out err);
            }
            bool already = e == null && err != null && err.Contains("already in the list");
            if (e == null && !already) { L("rss add: " + err); lock (Lock) Seen(d.SeenKey); return false; }
            lock (Lock)
            {
                Seen(d.SeenKey);
                if (d.Key != null)
                {
                    if (d.Replaces != null) { d.Replaces.Score = d.Score; d.Replaces.Title = d.Item.Title; d.Replaces.Upgraded = true; }
                    var old = d.Replaces != null ? d.Replaces.EntryId : null;
                    if (d.Replaces != null) d.Replaces.EntryId = e != null ? e.Id : null;
                    else Data.Grabs.Add(new RssGrab { Key = d.Key, Title = d.Item.Title, Rule = d.Rule.Name, EntryId = e != null ? e.Id : null, At = Family.Time(now), Score = d.Score });
                    if (old != null && e != null && old != e.Id) { var oid = old; Task.Run(() => core.Remove(oid, true)); }   // the old one: its files to the Recycle Bin
                }
                else Data.Grabs.Add(new RssGrab { Title = d.Item.Title, Rule = d.Rule.Name, EntryId = e != null ? e.Id : null, At = Family.Time(now), Score = d.Score });
            }
            var h = Notify; if (h != null && e != null) try { h(d.Replaces != null ? "RSS: a better version replaced it" : "RSS: added", d.Item.Title); } catch { }
            return true;
        }
        // every so often: the feeds that are due (only while the VPN is up)
        public async Task Tick(DateTime now)
        {
            if (core == null || !core.EngineUp) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                List<RssFeed> due; lock (Lock) due = Data.Feeds.Where(f => f.Enabled && Due(f, now)).ToList();
                foreach (var f in due) { if (!core.EngineUp) break; await Refresh(f).ConfigureAwait(false); }
            }
            finally { busy = 0; }
        }
        static bool Due(RssFeed f, DateTime now) { DateTime last; return !DateTime.TryParse(f.LastFetch, out last) || (now - last).TotalMinutes >= f.Minutes || (f.LastError != null && f.LastError.StartsWith("Waiting for the VPN")); }
        public async Task RefreshAll()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try { List<RssFeed> all; lock (Lock) all = Data.Feeds.Where(f => f.Enabled).ToList(); foreach (var f in all) await Refresh(f).ConfigureAwait(false); }
            finally { busy = 0; }
        }
    }
}
