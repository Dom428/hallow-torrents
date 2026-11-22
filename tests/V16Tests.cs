// Hallow Torrents 1.6 tests: Send to Jellyfin. The "NAS" is a temp folder (a UNC path to this PC would need admin), the private-address
// rules are checked with stand-in lookups, Jellyfin is a local HTTP stand-in on 127.0.0.1. Nothing touches the real NAS; the move rules'
// "Recycle Bin" step is a stand-in too (nothing goes to your real Recycle Bin).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using MonoTorrent;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static void V16Unit()
        {
            Func<string, IPAddress[]> res = h => h == "nas" ? new[] { IPAddress.Parse("192.168.4.197") } : h == "mixed" ? new[] { IPAddress.Parse("192.168.1.5"), IPAddress.Parse("8.8.8.8") } : h == "nas.example.com" ? new[] { IPAddress.Parse("203.0.113.7") } : null;
            Ok(NasPath.Check(@"\\192.168.4.197\media", res) == null && NasPath.Check(@"\\192.168.4.197\media\Jellyfin", res) == null, "\\\\192.168.4.197\\media: a private address - allowed");
            Ok(NasPath.Check(@"\\100.90.88.26\media", res) == null, "a Tailscale address - allowed");
            Ok(NasPath.Check(@"\\nas\media", res) == null, "a home-network name that resolves to a private address - allowed");
            var pub = NasPath.Check(@"\\8.8.8.8\media", res);
            Ok(pub != null && pub.Contains("public"), "a public address: refused (" + pub + ")");
            Ok(NasPath.Check(@"\\nas.example.com\media", res) != null && NasPath.Check(@"\\mixed\media", res) != null, "a name resolving to a public address (or a mix) - refused");
            Ok(NasPath.Check(@"\\unknown\media", res) != null && NasPath.Check(@"\\192.168.4.197", res) != null && NasPath.Check(@"\\?\C:\x", res) != null && NasPath.Check("media", res) != null && NasPath.Check("", res) != null, "unknown hosts, no share, \\\\?\\ paths, relative paths: refused");
            Ok(NasPath.Check(Path.GetTempPath(), res) == null, "a folder on this PC - allowed");
            var keepMap = NasPath.MappedDrive;
            NasPath.MappedDrive = d => d.ToUpperInvariant() == "Z:" ? @"\\8.8.4.4\share" : d.ToUpperInvariant() == "Y:" ? @"\\192.168.4.197\media" : null;
            Ok(NasPath.Check(@"Z:\Movies", res) != null && NasPath.Check(@"Y:\Movies", res) == null, "a mapped drive is checked by its share's host (public refused, the NAS allowed)");
            NasPath.MappedDrive = keepMap;
            Ok(Nas.Backoff(1) == TimeSpan.FromSeconds(30) && Nas.Backoff(2) == TimeSpan.FromSeconds(60) && Nas.Backoff(4) == TimeSpan.FromSeconds(240) && Nas.Backoff(20) == TimeSpan.FromMinutes(30), "retry back-off: 30 s, 1 min, 2 min... up to 30 min");
            var s = new TorSettings(); s.Fix();
            Ok(s.Categories.All(c => c.Nas == "off") && s.NasMovies == "Movies" && s.NasShows == "Shows" && !s.NasOn, "off by default; Movies / Shows subfolders");
            Ok(Nas.SubFolder(s, s.Cat("Movies")) == "Movies" && Nas.SubFolder(s, s.Cat("TV")) == "Shows" && Nas.SubFolder(s, s.Cat("Music")) == "Music", "where each category goes");
        }

        // a finished download on disk: a .torrent made from the folder, added to Core with that folder's parent as the save path
        static Entry Finished(Core core, string root, string name, string[] files, string category)
        {
            var src = Path.Combine(root, "pc", name); Directory.CreateDirectory(src); var rnd = new Random(name.GetHashCode());
            foreach (var f in files) { var p = Path.Combine(src, f); Directory.CreateDirectory(Path.GetDirectoryName(p)); var b = new byte[f.EndsWith(".srt") ? 3000 : 300000 + rnd.Next(5000)]; rnd.NextBytes(b); File.WriteAllBytes(p, b); }
            var tc = new TorrentCreator(TorrentType.V1Only); tc.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" }); var tf = Path.Combine(root, name + ".torrent"); tc.Create(new TorrentFileSource(src), tf);
            string err; var x = core.AddTorrentFile(tf, Path.Combine(root, "pc"), category, null, false, out err);
            x.CompletedAt = Family.Time(DateTime.Now); x.Scan = "clean"; x.Flags = new List<string>();
            return x;
        }

        static void Nas16()
        {
            var root = Path.Combine(Dir, "nas16"); Directory.CreateDirectory(root);
            var share = Path.Combine(root, "share"); Directory.CreateDirectory(share);
            var s = new TorSettings(); s.Fix(); s.NasOn = true; s.NasFolder = share; s.Cat("Movies").Nas = "copy"; s.Cat("TV").Nas = "now"; s.Cat("Other").Nas = "after";
            var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>();
            var refreshes = new List<string>(); const string Key = "0123456789abcdefJELLYKEY";
            using (var jf = new StandIn15(req =>
            {
                if (req.Path == "/System/Info/Public") return StandIn15.Reply(200, "application/json", "{\"LocalAddresses\":[],\"ServerName\":\"Villa\",\"Version\":\"4.9.5.0\",\"Id\":\"x\"}");
                if (req.Path == "/Library/Media/Updated") { lock (refreshes) refreshes.Add(req.Body); return StandIn15.Reply(204, "text/plain", ""); }
                return StandIn15.Reply(404, "text/plain", "");
            }))
            {
                s.JellyfinUrl = "http://127.0.0.1:" + jf.Port;
                var nas = new Nas(core, () => s, m => { lock (log) log.Add(m); }) { RefreshDelayMs = 0 };
                var removed = new List<string>(); nas.RemoveLocal = id => { lock (removed) removed.Add(id); return System.Threading.Tasks.Task.FromResult<string>(null); };
                nas.JellyfinKey = Key;
                string jerr; var info = nas.Jellyfin.Info(out jerr);
                Ok(info == "Villa - Jellyfin 4.9.5.0", "Jellyfin /System/Info/Public (no key): " + (info ?? jerr));
                Ok(!File.ReadAllText(Path.Combine(core.Dir, "nas.json")).Contains(Key) && nas.JellyfinKey == Key && nas.HasKey, "the API key is kept encrypted (DPAPI) in nas.json, never in plain text");

                // naming + the copy + Jellyfin told
                var movie = Finished(core, root, "The.Matrix.1999.1080p.BluRay.x264-GRP", new[] { "The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", "The.Matrix.1999.1080p.BluRay.x264-GRP.en.srt", "Sample\\sample.mkv", "info.nfo" }, "Movies");
                var show = Finished(core, root, "Night.Sky.S01.720p.WEB", new[] { "Night.Sky.S01E01.720p.WEB.mkv", "Night.Sky.S01E02.720p.WEB.mkv", "Night.Sky.S01E02.720p.WEB.srt" }, "TV");
                Ok(nas.Consider(movie) == "Queued for the NAS." && nas.Consider(show) == "Queued for the NAS.", "finished and scanned: queued");
                var mi = nas.Data.Items.First(i => i.Id == movie.Id);
                Ok(mi.Files.Count == 2 && mi.Files[0].Dest == @"Movies\The Matrix (1999)\The Matrix (1999).mkv" && mi.Files[1].Dest == @"Movies\The Matrix (1999)\The Matrix (1999).en.srt", "movie: Movies\\Title (Year)\\Title (Year).mkv + subtitle, no sample / nfo: " + string.Join("; ", mi.Files.Select(f => f.Dest)));
                var si = nas.Data.Items.First(i => i.Id == show.Id);
                Ok(si.Files.Select(f => f.Dest).SequenceEqual(new[] { @"Shows\Night Sky\Season 01\Night Sky - S01E01.mkv", @"Shows\Night Sky\Season 01\Night Sky - S01E02.mkv", @"Shows\Night Sky\Season 01\Night Sky - S01E02.srt" }), "show: Shows\\Show\\Season 01\\Show - S01E02.mkv: " + string.Join("; ", si.Files.Select(f => f.Dest)));
                nas.Step();
                var copied = Path.Combine(share, @"Movies\The Matrix (1999)\The Matrix (1999).mkv");
                Ok(mi.State == "done" && si.State == "done" && File.Exists(copied) && Nas.Hash(copied, -1) == Nas.Hash(mi.Files[0].Src, -1) && !Directory.GetFiles(share, "*.hallow-part", SearchOption.AllDirectories).Any(), "copied and verified (checksum), no temp files left");
                Ok(File.Exists(mi.Files[0].Src), "Copy rule: the PC's copy stays (keeps seeding)");
                lock (removed) Ok(removed.SequenceEqual(new[] { show.Id }), "Move now: the torrent stopped and the PC's copy went to the (stand-in) Recycle Bin once the NAS copy was verified");
                nas.Step();
                lock (refreshes) Ok(refreshes.Count == 1 && refreshes[0].Contains("The Matrix (1999)") && refreshes[0].Contains("Night Sky"), "Jellyfin told once for both (debounced): POST /Library/Media/Updated " + (refreshes.Count > 0 ? refreshes[0] : ""));
                // the key went in the header, nowhere else
                var j = Family.Json(nas.Json());
                Ok(!j.Contains(Key) && !j.Contains("Matrix") && !j.Contains(share) && j.Contains("\"done\":2") && j.Contains("\"jellyfinKey\":true"), "status / pipe block: counts only (no names, no folder, no key): " + j);
                lock (log) Ok(log.All(l => !l.Contains(Key)), "the key is never in the log");

                // move after seeding: copied now, the PC's copy only after the seeding goal
                var iso = Finished(core, root, "Some.Linux.ISO", new[] { "distro.iso", "README.txt" }, "Other");
                nas.Consider(iso); nas.Step();
                var ii = nas.Data.Items.First(i => i.Id == iso.Id);
                lock (removed) Ok(ii.State == "done" && !removed.Contains(iso.Id) && File.Exists(Path.Combine(share, "Other", "Some.Linux.ISO", "distro.iso")), "Move after seeding: on the NAS (Other\\Some.Linux.ISO), still seeding here");
                core.Get(iso.Id).LimitHit = true; nas.Step();
                lock (removed) Ok(removed.Contains(iso.Id) && ii.LocalRemoved, "...the seeding goal reached: then the PC's copy goes");

                // never: flagged / Defender found something / still scanning / category off
                var bad = Finished(core, root, "Free.Movie.2021.1080p", new[] { "Free.Movie.2021.1080p.mp4.exe", "x.txt" }, "Movies"); bad.Flags = new List<string> { "Free.Movie.2021.1080p.mp4.exe|Disguised" };
                Ok(nas.Consider(bad).StartsWith("Not sent") && !nas.Data.Items.Any(i => i.Id == bad.Id), "a flagged download is never sent");
                var thr = Finished(core, root, "Other.Movie.2020.1080p", new[] { "Other.Movie.2020.1080p.mkv", "x.txt" }, "Movies"); thr.Scan = "threats";
                Ok(nas.Consider(thr).StartsWith("Not sent") && !nas.Data.Items.Any(i => i.Id == thr.Id), "one Defender found something in is never sent");
                thr.Scan = "scanning"; Ok(nas.Consider(thr) == null && !nas.Data.Items.Any(i => i.Id == thr.Id), "...nor while the scan still runs");
                var mus = Finished(core, root, "Band - Album", new[] { "01.flac", "02.flac" }, "Music"); Ok(nas.Consider(mus) == null, "a category set to Don't send: nothing");
                var api = Finished(core, root, "Api.Movie.2019.1080p", new[] { "Api.Movie.2019.1080p.mkv", "x.txt" }, "Movies"); api.Api = true; s.Cat("Movies").Nas = "now"; nas.Consider(api); s.Cat("Movies").Nas = "copy";
                Ok(nas.Data.Items.First(i => i.Id == api.Id).Rule == "copy", "Radarr / Sonarr torrents are only ever copied (they import from here)");
                nas.Forget(api.Id);

                // a different file already on the NAS is never overwritten
                File.Delete(copied); File.WriteAllText(copied, "something else");
                Ok(Nas.CopyOne(mi.Files[0].Src, copied, n => { }).Contains("different file") && File.ReadAllText(copied) == "something else", "a different file already there: left alone");

                // NAS offline: waits and retries with back-off, then goes through
                var clock = new DateTime(2026, 11, 22, 12, 0, 0); nas.Now = () => clock;
                var gone = Path.Combine(root, "share-offline"); s.NasFolder = gone;
                var off = Finished(core, root, "Offline.Movie.2018.720p", new[] { "Offline.Movie.2018.720p.mkv", "x.txt" }, "Movies");
                nas.Consider(off); nas.Step(); var oi = nas.Data.Items.First(i => i.Id == off.Id);
                Ok(oi.State == "waiting" && oi.Attempts == 1 && DateTime.Parse(oi.NextTry) == clock.AddSeconds(30), "NAS offline: waiting, next try in 30 s (" + oi.Error + ")");
                nas.Step(); Ok(oi.Attempts == 1, "...not tried again before its time");
                clock = clock.AddSeconds(31); nas.Step(); Ok(oi.Attempts == 2 && DateTime.Parse(oi.NextTry) == clock.AddSeconds(60), "...tried again, then 1 min");
                Directory.CreateDirectory(gone); clock = clock.AddSeconds(61); nas.Step();
                Ok(oi.State == "done" && File.Exists(Path.Combine(gone, @"Movies\Offline Movie (2018)\Offline Movie (2018).mkv")), "NAS back: copied");
                var json = Family.Json(nas.Json()); Ok(json.Contains("\"waiting\":0"), "queue counts: " + json);

                // a public NAS address: refused outright, nothing copied
                s.NasFolder = @"\\8.8.8.8\media";
                var pubx = Finished(core, root, "Public.Movie.2017.720p", new[] { "Public.Movie.2017.720p.mkv", "x.txt" }, "Movies");
                nas.Consider(pubx); nas.Step(); var pi = nas.Data.Items.First(i => i.Id == pubx.Id);
                Ok(pi.State == "failed" && pi.Error.Contains("public"), "a public NAS address: refused, not retried (" + pi.Error + ")");
                Ok(nas.CheckDestination() != null, "Test the folder: refused too");
                s.JellyfinUrl = "http://8.8.8.8:8096"; string perr; Ok(nas.Jellyfin.Info(out perr) == null && perr.Contains("isn't on your own network"), "a public Jellyfin address: refused before connecting (" + perr + ")");
                nas.Dispose();
            }
            core.Dispose();
        }

        // resumable, verified copies of one file
        static void Copy16()
        {
            var root = Path.Combine(Dir, "copy16"); Directory.CreateDirectory(root);
            var src = Path.Combine(root, "big.bin"); var data = new byte[3 * 1024 * 1024 + 123]; new Random(5).NextBytes(data); File.WriteAllBytes(src, data);
            var dest = Path.Combine(root, "nas", "Movies", "X (2000)", "X (2000).mkv");
            var keep = Nas.BufferSize; Nas.BufferSize = () => 256 * 1024;
            long seen = 0; Exception cut = null;
            try { Nas.CopyOne(src, dest, n => { seen += n; if (seen > 1024 * 1024) throw new IOException("the network name is no longer available (test)"); }); } catch (Exception e) { cut = e; }
            var part = dest + ".hallow-part";
            Ok(cut != null && File.Exists(part) && !File.Exists(dest) && new FileInfo(part).Length > 0, "an interrupted copy leaves only <name>.hallow-part (" + (File.Exists(part) ? new FileInfo(part).Length : 0) + " bytes)");
            var had = new FileInfo(part).Length; long resumedFrom = -1, wrote = 0;
            var r = Nas.CopyOne(src, dest, n => { if (resumedFrom < 0) resumedFrom = n; else wrote += n; });
            Ok(r == null && resumedFrom == had && wrote == data.Length - had && Nas.Hash(dest, -1) == Nas.Hash(src, -1) && !File.Exists(part), "resumed from byte " + had + " (only the rest copied), verified, renamed");
            // a part that doesn't match the start of the file: thrown away, copied from the beginning
            File.Delete(dest); var junk = new byte[500000]; new Random(9).NextBytes(junk); File.WriteAllBytes(part, junk);
            resumedFrom = -1; r = Nas.CopyOne(src, dest, n => { if (resumedFrom < 0) resumedFrom = n; });
            Ok(r == null && resumedFrom == 0 && Nas.Hash(dest, -1) == Nas.Hash(src, -1), "a .hallow-part with wrong bytes: not trusted, copied again from the start");
            // the same file already there: counts as done, nothing written
            var t0 = File.GetLastWriteTimeUtc(dest); Thread.Sleep(50);
            Ok(Nas.CopyOne(src, dest, n => { }) == null && File.GetLastWriteTimeUtc(dest) == t0, "the same file already on the NAS: done, untouched");
            Nas.BufferSize = keep;
        }

        static void AppCopy16()
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "HallowTorrents.exe");
            var dir = Path.Combine(Dir, "app16"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true,\"NasOn\":true,\"NasFolder\":\"" + Path.Combine(dir, "share").Replace("\\", "\\\\") + "\"}");
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r != null && r.Contains("\"version\":\"" + Program.Version + "\""), "test copy: ping " + r);
                r = Cmd(exe, dir, "nas-status");
                Ok(r.StartsWith("ok {") && r.Contains("\"on\":true") && r.Contains("\"destination\":true") && r.Contains("\"queued\":0") && !r.Contains("share"), "pipe nas-status: counts only: " + r);
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"nas\":{") && !r.Contains(Path.Combine(dir, "share").Replace("\\", "\\\\")), "status.json has the nas block (no folder)");
                Ok(Cmd(exe, dir, "open jellyfin") == "ok", "open jellyfin (the Jellyfin / NAS page)");
                Cmd(exe, dir, "quit"); app.WaitForExit(8000);
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } }
        }
    }
}
