// Hallow Torrents - what's in a torrent: its category (TV / Movies / Music / Games / Other), suspicious files (a program inside a
// video or music download, "movie.mp4.exe", shortcuts), and Jellyfin-friendly names for the media library hand-off:
//   Movies: "Title (Year)/Title (Year).ext"      TV: "Show/Season 01/Show - S01E02.ext"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallowTorrents
{
    public class TFile { public string Path; public long Length; public TFile(string p, long l) { Path = p; Length = l; } }
    public class MediaName { public string Kind = "unknown"; public string Title; public int Year; public int Season = -1, Episode = -1, EpisodeEnd = -1; }

    public static class Media
    {
        public static readonly HashSet<string> Video = Set(".mkv .mp4 .avi .mov .wmv .m4v .mpg .mpeg .ts .m2ts .webm .flv .vob .ogv");
        public static readonly HashSet<string> Audio = Set(".mp3 .flac .m4a .aac .ogg .opus .wav .wma .alac .ape .wv");
        public static readonly HashSet<string> Subs = Set(".srt .ass .ssa .sub .idx .vtt");
        public static readonly HashSet<string> Programs = Set(".exe .scr .com .bat .cmd .ps1 .psm1 .vbs .vbe .js .jse .wsf .wsh .hta .msi .msix .appx .lnk .pif .cpl .jar .reg .dll .sys .url .iso.exe");
        public static readonly HashSet<string> AlwaysBad = Set(".scr .pif .lnk .cpl");
        static HashSet<string> Set(string s) { return new HashSet<string>(s.Split(' '), StringComparer.OrdinalIgnoreCase); }
        public static string Ext(string p) { try { return Path.GetExtension(p ?? "").ToLowerInvariant(); } catch { return ""; } }

        // ---------------- category
        public static string Detect(string name, IList<TFile> files)
        {
            files = files ?? new List<TFile>();
            long total = Math.Max(1, files.Sum(f => f.Length));
            long video = files.Where(f => Video.Contains(Ext(f.Path))).Sum(f => f.Length), audio = files.Where(f => Audio.Contains(Ext(f.Path))).Sum(f => f.Length);
            bool ep = Parse(name).Kind == "episode" || files.Any(f => Video.Contains(Ext(f.Path)) && Parse(Path.GetFileNameWithoutExtension(f.Path)).Kind == "episode");
            if (files.Count == 0) { var p = Parse(name); return p.Kind == "episode" ? "TV" : p.Kind == "movie" ? "Movies" : "Other"; }
            if (video * 2 >= total) return ep ? "TV" : "Movies";
            if (audio * 2 >= total) return "Music";
            if (files.Any(f => Ext(f.Path) == ".exe" || Ext(f.Path) == ".iso" || Ext(f.Path) == ".bin") && Regex.IsMatch(name ?? "", @"(?i)\b(repack|setup|gog|codex|fitgirl|game|edition)\b")) return "Games";
            return "Other";
        }
        public static bool LooksLikeMedia(string name, IList<TFile> files)
        {
            var c = Detect(name, files);
            if (c == "TV" || c == "Movies" || c == "Music") return true;
            return Regex.IsMatch(name ?? "", @"(?i)(\b(1080p|720p|2160p|480p|x264|x265|h\.?264|hevc|web-?dl|webrip|bluray|brrip|dvdrip|hdtv|flac|mp3|320kbps|album|discography)\b|S\d{1,2}E\d{1,3})");
        }

        // ---------------- suspicious files: (file, why) - never blocks anything by itself, it's a warning badge
        public static List<string[]> Suspicious(string name, IList<TFile> files)
        {
            var r = new List<string[]>();
            if (files == null) return r;
            bool media = LooksLikeMedia(name, files);
            foreach (var f in files)
            {
                var fn = Path.GetFileName(f.Path); var ext = Ext(fn);
                var inner = Ext(Path.GetFileNameWithoutExtension(fn));
                if (Programs.Contains(ext) && (Video.Contains(inner) || Audio.Contains(inner) || Subs.Contains(inner) || inner == ".pdf" || inner == ".jpg" || inner == ".txt"))
                    r.Add(new[] { f.Path, "Disguised: it looks like a " + inner.TrimStart('.') + " file but it's a program (" + ext + ")" });
                else if (Regex.IsMatch(fn, @"\s{6,}\S*\.\w{2,4}$") && Programs.Contains(ext))
                    r.Add(new[] { f.Path, "Hides its real ending behind spaces (" + ext + ")" });
                else if (AlwaysBad.Contains(ext))
                    r.Add(new[] { f.Path, ext == ".lnk" ? "A shortcut - it can run anything when opened" : "A program type often used by malware (" + ext + ")" });
                else if (media && Programs.Contains(ext))
                    r.Add(new[] { f.Path, "A program (" + ext + ") inside what looks like a " + (Detect(name, files) == "Music" ? "music" : "video") + " download" + (Regex.IsMatch(fn, @"(?i)codec|player|setup|install") ? " - videos never need a codec installer" : "") });
            }
            return r;
        }

        // ---------------- names
        static readonly Regex epRx = new Regex(@"^(?<t>.*?)[ ._\-\[(]*\bS(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})(?:[-_ ]?E?(?<e2>\d{1,3}))?(?!\d)", RegexOptions.IgnoreCase);
        static readonly Regex epX = new Regex(@"^(?<t>.*?)[ ._\-\[(]+(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d)", RegexOptions.IgnoreCase);
        static readonly Regex seasonRx = new Regex(@"^(?<t>.*?)[ ._\-\[(]*\b(?:S(?<s>\d{1,2})|Season[ ._-]?(?<s>\d{1,2}))(?![\dE])", RegexOptions.IgnoreCase);
        static readonly Regex yearRx = new Regex(@"(?<![0-9])(?<y>(?:19[2-9]|20[0-4])\d)(?![0-9])");
        static readonly Regex junk = new Regex(@"(?i)[ ._\-\[(](2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr|hdr10|dv|x264|x265|h\.?264|h\.?265|hevc|avc|web-?dl|webrip|web|bluray|blu-ray|brrip|bdrip|dvdrip|hdtv|remux|proper|repack|extended|unrated|imax|10bit|aac|ac3|dts|ddp?5\.1|atmos|multi|subs?)\b.*$");
        public static MediaName Parse(string name)
        {
            var m = new MediaName(); if (string.IsNullOrWhiteSpace(name)) return m;
            var n = Regex.Replace(name.Trim(), @"^\[[^\]]*\]\s*", "");   // "[group] ..."
            n = Regex.Replace(n, @"(?i)\.(mkv|mp4|avi|m4v|mov|wmv|ts|webm)$", "");
            var e = epRx.Match(n); if (!e.Success) e = epX.Match(n);
            if (e.Success)
            {
                m.Kind = "episode"; m.Season = int.Parse(e.Groups["s"].Value); m.Episode = int.Parse(e.Groups["e"].Value);
                if (e.Groups["e2"].Success) { var e2 = int.Parse(e.Groups["e2"].Value); if (e2 > m.Episode && e2 - m.Episode < 10) m.EpisodeEnd = e2; }
                var t = e.Groups["t"].Value; var y = yearRx.Match(t); if (y.Success && y.Index > 0) { m.Year = int.Parse(y.Groups["y"].Value); t = t.Substring(0, y.Index); }
                m.Title = Clean(t); return m;
            }
            var se = seasonRx.Match(n);
            if (se.Success && Clean(se.Groups["t"].Value).Length > 0) { m.Kind = "season"; m.Season = int.Parse(se.Groups["s"].Value); m.Title = Clean(Regex.Replace(se.Groups["t"].Value, @"[ ._\-(]+(?:19|20)\d\d[ ._\-)]*$", "")); return m; }
            // a movie: the last year that has some title before it
            Match best = null; foreach (Match y in yearRx.Matches(n)) if (y.Index > 0) best = y;
            if (best != null)
            {
                m.Kind = "movie"; m.Year = int.Parse(best.Groups["y"].Value); m.Title = Clean(n.Substring(0, best.Index)); if (m.Title.Length == 0) { m.Kind = "unknown"; m.Title = Clean(n); }
                return m;
            }
            m.Title = Clean(junk.Replace(n, "")); return m;
        }
        public static string Clean(string t)
        {
            t = (t ?? "").Replace('.', ' ').Replace('_', ' ');
            t = Regex.Replace(t, @"[\[(]\s*$", ""); t = Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '(', '[', '.');
            if (t.Length > 0 && t == t.ToLowerInvariant()) t = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t);
            return Safe(t);
        }
        public static string Safe(string s)
        {
            s = s.Replace(": ", " - ").Replace(":", "-");
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            s = s.Trim().TrimEnd('.'); return s.Length == 0 ? "Untitled" : s;
        }

        // where each file goes in the library: (source relative path, target relative path); files that aren't worth handing off
        // (samples, .nfo, .txt, screenshots) are left out. naming: "movie" / "tv" / "keep" (the torrent's own folder layout)
        public static List<string[]> Plan(string torrentName, IList<TFile> files, string naming)
        {
            var r = new List<string[]>(); files = files ?? new List<TFile>();
            if (naming == "keep")
            {
                foreach (var f in files) r.Add(new[] { f.Path, files.Count == 1 ? Path.GetFileName(f.Path) : Path.Combine(Safe(torrentName), StripRoot(torrentName, f.Path)) });
                return r;
            }
            var vids = files.Where(f => Video.Contains(Ext(f.Path)) && !Regex.IsMatch(f.Path, @"(?i)(\bsample\b|\btrailer\b|[\\/]extras?[\\/]|[\\/]featurettes?[\\/])")).ToList();
            var tn = Parse(torrentName);
            if (naming == "movie")
            {
                var main = vids.OrderByDescending(f => f.Length).FirstOrDefault(); if (main == null) return r;
                var p = Parse(Path.GetFileNameWithoutExtension(main.Path)); if (p.Kind != "movie" || (tn.Kind == "movie" && tn.Title.Length > 0)) p = tn.Kind == "movie" ? tn : p;
                var title = p.Title ?? Clean(torrentName); var bse = p.Year > 0 ? title + " (" + p.Year + ")" : title;
                r.Add(new[] { main.Path, Path.Combine(bse, bse + Ext(main.Path)) });
                var stem = Path.GetFileNameWithoutExtension(main.Path);
                foreach (var s in files.Where(f => Subs.Contains(Ext(f.Path))))
                {
                    var sn = Path.GetFileNameWithoutExtension(s.Path); var lang = "";
                    var lm = Regex.Match(sn, @"(?i)[._ -](en|eng|english|es|spa|fr|fre|de|ger|it|pt|nl|sv|pl|ru|ja|ko|zh)(?:[._ -](forced|sdh))?$"); if (lm.Success) lang = "." + lm.Groups[1].Value.ToLowerInvariant().Substring(0, 2) + (lm.Groups[2].Success ? "." + lm.Groups[2].Value.ToLowerInvariant() : "");
                    if (lang.Length == 0 && !sn.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && files.Count(f => Subs.Contains(Ext(f.Path))) > 1) continue;
                    var target = Path.Combine(bse, bse + lang + Ext(s.Path)); if (!r.Any(x => string.Equals(x[1], target, StringComparison.OrdinalIgnoreCase))) r.Add(new[] { s.Path, target });
                }
                return r;
            }
            // tv
            foreach (var v in vids)
            {
                var p = Parse(Path.GetFileNameWithoutExtension(v.Path));
                if (p.Kind != "episode") { var folder = Parse(Path.GetFileName(Path.GetDirectoryName(v.Path) ?? "")); if (folder.Kind == "episode") p = folder; }
                if (p.Kind != "episode") continue;
                var show = !string.IsNullOrEmpty(p.Title) && p.Title != "Untitled" ? p.Title : tn.Title;
                if (tn.Kind == "season" || tn.Kind == "episode") { if (string.IsNullOrEmpty(p.Title) || p.Title == "Untitled") show = tn.Title; }
                show = Safe(show ?? Clean(torrentName));
                var code = "S" + p.Season.ToString("00") + "E" + p.Episode.ToString("00") + (p.EpisodeEnd > 0 ? "-E" + p.EpisodeEnd.ToString("00") : "");
                var bse = show + " - " + code;
                var dir = Path.Combine(show, "Season " + p.Season.ToString("00"));
                r.Add(new[] { v.Path, Path.Combine(dir, bse + Ext(v.Path)) });
                var stem = Path.GetFileNameWithoutExtension(v.Path);
                foreach (var s in files.Where(f => Subs.Contains(Ext(f.Path)) && Path.GetFileNameWithoutExtension(f.Path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
                    r.Add(new[] { s.Path, Path.Combine(dir, bse + Path.GetFileNameWithoutExtension(s.Path).Substring(stem.Length) + Ext(s.Path)) });
            }
            return r;
        }
        static string StripRoot(string root, string p)
        {
            var parts = p.Split('\\', '/');
            return parts.Length > 1 && string.Equals(parts[0], root, StringComparison.OrdinalIgnoreCase) ? string.Join("\\", parts.Skip(1)) : p;
        }
    }
}
