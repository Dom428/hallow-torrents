// Hallow Torrents - the torrent creator (v1.1), on MonoTorrent's TorrentCreator: a .torrent from a file or a folder with the piece
// size picked for you (or your own), trackers (a blank line starts a new tier), web seeds, private, comment and source. Nothing goes on
// the network here: hashing is local. Seeding it afterwards is an ordinary torrent (so it waits for the VPN like everything else).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;

namespace HallowTorrents
{
    public class CreateOptions
    {
        public string Source;                 // a file or a folder
        public string Output;                 // the .torrent to write
        public int PieceKB;                   // 0 = picked for the size
        public List<List<string>> Trackers = new List<List<string>>();
        public List<string> WebSeeds = new List<string>();
        public bool Private; public string Comment, SourceTag;
    }

    public static class Creator
    {
        public static readonly int[] PieceSizesKB = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };

        // "one tracker per line, a blank line between tiers" -> tiers (only http(s) / udp addresses; anything else is an error)
        public static List<List<string>> Tiers(string text, out string error)
        {
            error = null; var r = new List<List<string>>(); var cur = new List<string>();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var t = raw.Trim();
                if (t.Length == 0) { if (cur.Count > 0) { r.Add(cur); cur = new List<string>(); } continue; }
                var u = Core.CheckTracker(t); if (u == null) { error = "Not a tracker address (http://, https:// or udp://): " + t; return null; }
                if (!r.Any(x => x.Contains(u)) && !cur.Contains(u)) cur.Add(u);
            }
            if (cur.Count > 0) r.Add(cur);
            return r;
        }
        public static List<string> Urls(string text, out string error)
        {
            error = null; var r = new List<string>();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var t = raw.Trim(); if (t.Length == 0) continue; Uri u;
                if (!Uri.TryCreate(t, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) { error = "Not a web seed address (http:// or https://): " + t; return null; }
                if (!r.Contains(u.ToString())) r.Add(u.ToString());
            }
            return r;
        }
        public static long SizeOf(string source)
        {
            if (File.Exists(source)) return new FileInfo(source).Length;
            if (Directory.Exists(source)) return new TorrentFileSource(source).Files.Sum(f => f.Length);
            return 0;
        }
        public static int AutoPieceKB(string source) { return Math.Max(16, TorrentCreator.RecommendedPieceSize(Math.Max(1, SizeOf(source))) / 1024); }

        // makes the .torrent; progress(0..1). Returns null or what went wrong.
        public static async Task<string> Create(CreateOptions o, Action<double> progress, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(o.Source) || (!File.Exists(o.Source) && !Directory.Exists(o.Source))) return "Pick a file or a folder to share.";
                if (string.IsNullOrWhiteSpace(o.Output)) return "Pick where to save the .torrent file.";
                var src = new TorrentFileSource(o.Source.TrimEnd('\\'));
                if (!src.Files.Any()) return "That folder has no files in it.";
                if (src.Files.Sum(f => f.Length) == 0) return "Those files are empty.";
                var c = new TorrentCreator(TorrentType.V1Only);
                c.PieceLength = (o.PieceKB > 0 ? o.PieceKB : AutoPieceKB(o.Source)) * 1024;
                foreach (var tier in o.Trackers) if (tier.Count > 0) c.Announces.Add(tier.ToList());
                foreach (var w in o.WebSeeds) c.GetrightHttpSeeds.Add(w);
                c.Private = o.Private;
                if (!string.IsNullOrWhiteSpace(o.Comment)) c.Comment = o.Comment.Trim();
                if (!string.IsNullOrWhiteSpace(o.SourceTag)) c.SetCustomSecure(new BEncodedString("source"), new BEncodedString(o.SourceTag.Trim()));   // inside the info dictionary, like other clients
                c.CreatedBy = "Hallow Torrents " + Program.Version;
                if (progress != null) c.Hashed += (s, e) => { try { progress(e.OverallCompletion); } catch { } };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Output)));
                var tmp = o.Output + ".part";
                using (var f = File.Create(tmp)) await c.CreateAsync(src, f, token).ConfigureAwait(false);
                if (File.Exists(o.Output)) File.Delete(o.Output);
                File.Move(tmp, o.Output);
                return null;
            }
            catch (OperationCanceledException) { try { File.Delete(o.Output + ".part"); } catch { } return "Stopped."; }
            catch (Exception e) { try { File.Delete(o.Output + ".part"); } catch { } return "Couldn't make it: " + Family.OneLine(e.Message); }
        }
        // where to seed it from: the folder that holds the file / folder you shared
        public static string SaveFolderFor(string source) { return Path.GetDirectoryName(Path.GetFullPath(source.TrimEnd('\\'))); }
    }
}
