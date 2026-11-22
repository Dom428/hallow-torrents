// Hallow Torrents 1.7 tests: Plex and Emby too. Emby and Plex are local HTTP stand-ins on 127.0.0.1 (refresh calls, identity / info,
// the sections list, a refused token, the token kept out of URLs / logs / nas.json); Plex's naming; 1.6 settings migrating as Jellyfin.
// The NAS is a temp folder, as in 1.6. Nothing touches the real NAS (the optional "real17" group only does the two read-only GETs
// allowed: Plex's /identity and Jellyfin's /System/Info/Public at 192.168.4.197 - it isn't part of the suite).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static TorSettings FromJson(string json)
        {
            using (var m = new MemoryStream(Encoding.UTF8.GetBytes(json))) { var s = (TorSettings)new DataContractJsonSerializer(typeof(TorSettings)).ReadObject(m); s.Fix(); return s; }
        }

        static void V17Unit()
        {
            // migration: 1.6 settings (no MediaServer) stay Jellyfin, with their address
            var old = FromJson("{\"NasOn\":true,\"NasFolder\":\"\\\\\\\\192.168.4.197\\\\media\",\"NasMovies\":\"Movies\",\"NasShows\":\"Shows\",\"JellyfinUrl\":\"http://192.168.4.197:8096\"}");
            Ok(old.MediaServer == "jellyfin" && old.JellyfinUrl == "http://192.168.4.197:8096" && old.NasOn && old.NasFolder == @"\\192.168.4.197\media", "1.6 settings migrate as Jellyfin (address, folder kept)");
            var fresh = new TorSettings(); fresh.Fix();
            Ok(fresh.MediaServer == "jellyfin" && fresh.PlexUrl == null && fresh.EmbyUrl == null, "a new install: Jellyfin (as 1.6), no Plex / Emby address");
            Ok(FromJson("{\"MediaServer\":\"plex\"}").MediaServer == "plex" && FromJson("{\"MediaServer\":\"emby\"}").MediaServer == "emby" && FromJson("{\"MediaServer\":\"none\"}").MediaServer == "none" && FromJson("{\"MediaServer\":\"kodi\"}").MediaServer == "jellyfin", "MediaServer: jellyfin / emby / plex / none (anything else = Jellyfin)");
            var dummyNasData = FromJson("{}"); Ok(dummyNasData.MediaServer == "jellyfin", "empty settings: Jellyfin");

            // Plex naming
            Func<string, long, TFile> F = (p, l) => new TFile(p, l);
            var show = Media.Plan("Night.Sky.2019.S01.720p.WEB", new[] { F(@"Night.Sky.2019.S01.720p.WEB\Night.Sky.2019.S01E01.720p.WEB.mkv", 900), F(@"Night.Sky.2019.S01.720p.WEB\Night.Sky.2019.S01E02.720p.WEB.mkv", 900), F(@"Night.Sky.2019.S01.720p.WEB\Night.Sky.2019.S01E02.720p.WEB.en.srt", 3) }, "tv", "plex").Select(x => x[1]).ToList();
            Ok(show.SequenceEqual(new[] { @"Night Sky (2019)\Season 01\Night Sky (2019) - s01e01.mkv", @"Night Sky (2019)\Season 01\Night Sky (2019) - s01e02.mkv", @"Night Sky (2019)\Season 01\Night Sky (2019) - s01e02.en.srt" }), "Plex TV: Show (Year)\\Season 01\\Show (Year) - s01e02.ext, subtitles alongside: " + string.Join("; ", show));
            var noYear = Media.Plan("Some.Show.S02E05.1080p.WEB", new[] { F("Some.Show.S02E05.1080p.WEB.mkv", 900) }, "tv", "plex").Select(x => x[1]).ToList();
            Ok(noYear.SequenceEqual(new[] { @"Some Show\Season 02\Some Show - s02e05.mkv" }), "Plex TV without a year in the name: Show\\Season 02\\Show - s02e05.mkv: " + string.Join("; ", noYear));
            var multi = Media.Plan("Some.Show.S02E18E19.720p", new[] { F("Some.Show.S02E18E19.720p.mkv", 900) }, "tv", "plex").Select(x => x[1]).ToList();
            Ok(multi.SequenceEqual(new[] { @"Some Show\Season 02\Some Show - s02e18-e19.mkv" }), "Plex multi-episode file: s02e18-e19: " + string.Join("; ", multi));
            var jf = Media.Plan("Night.Sky.2019.S01E01.720p.WEB", new[] { F("Night.Sky.2019.S01E01.720p.WEB.mkv", 900) }, "tv").Select(x => x[1]).ToList();
            Ok(jf.SequenceEqual(new[] { @"Night Sky\Season 01\Night Sky - S01E01.mkv" }), "Jellyfin / Emby naming unchanged from 1.6: " + string.Join("; ", jf));
            var mv = Media.Plan("The.Matrix.1999.1080p.BluRay.x264-GRP", new[] { F("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", 900), F("The.Matrix.1999.1080p.BluRay.x264-GRP.en.srt", 3) }, "movie", "plex").Select(x => x[1]).ToList();
            Ok(mv.SequenceEqual(new[] { @"The Matrix (1999)\The Matrix (1999).mkv", @"The Matrix (1999)\The Matrix (1999).en.srt" }), "Plex movies: Movie (Year)\\Movie (Year).ext (+ .en.srt): " + string.Join("; ", mv));

            // addresses: default ports, private only
            Ok(ServerNet.Normalize("192.168.4.197", 32400) == "http://192.168.4.197:32400" && ServerNet.Normalize("192.168.4.197:8000", 32400) == "http://192.168.4.197:8000" && ServerNet.Normalize("https://plex.lan:443", 32400) == "https://plex.lan:443" && ServerNet.Normalize("nas/emby", 8096) == "http://nas:8096/emby", "address without a port: Plex 32400 / Jellyfin / Emby 8096 assumed");
            Uri u; string why;
            Ok(ServerNet.Pin("http://8.8.8.8:32400", 32400, "Plex", h => null, out u, out why) == null && why.Contains("isn't on your own network"), "a public Plex address: refused before connecting (" + why + ")");
            Ok(ServerNet.Pin("http://plex.example.com:32400", 32400, "Plex", h => new[] { IPAddress.Parse("203.0.113.5") }, out u, out why) == null && ServerNet.Pin("192.168.4.197", 32400, "Plex", h => null, out u, out why) != null && u.Port == 32400, "a name resolving to a public address: refused; 192.168.4.197 -> port 32400");

            // where a new folder is in Plex's eyes
            var share = @"\\192.168.4.197\media";
            Ok(PlexClient.MapPath(share + @"\Movies\The Matrix (1999)", share + @"\Movies", new[] { "/volume1/media/Movies" }) == "/volume1/media/Movies/The Matrix (1999)", "Plex path: <share>\\Movies\\X (Year) -> /volume1/media/Movies/X (Year)");
            Ok(PlexClient.MapPath(share + @"\Shows\Night Sky (2019)", share + @"\Shows", new[] { "/data/kids", "/data/Shows/" }) == "/data/Shows/Night Sky (2019)", "several library folders: the one with the same last name");
            Ok(PlexClient.MapPath(share + @"\Movies\X (2000)", share + @"\Movies", new[] { @"D:\Media\Movies" }) == @"D:\Media\Movies\X (2000)", "Plex on Windows: backslashes");
            Ok(PlexClient.MapPath(share + @"\Movies\X (2000)", share + @"\Movies", new[] { "/a", "/b" }) == null && PlexClient.MapPath(@"\\other\x\X (2000)", share + @"\Movies", new[] { "/a" }) == null, "can't tell: null (the whole library is scanned instead)");
            Ok(ServerNet.Redact("GET /x?X-Plex-Token=abc+def/ failed abc+def", "abc+def") == "GET /x?X-Plex-Token=[key]/ failed [key]" && ServerNet.Redact("no key", null) == "no key", "redaction: the token (also URL-escaped) never appears in a message");
            var snapNas = new Nas(null, () => { var t = new TorSettings(); t.Fix(); t.MediaServer = "plex"; t.PlexUrl = "http://192.168.4.197:32400"; return t; }, m => { });
            var js = Family.Json(snapNas.Json());
            Ok(js.Contains("\"server\":\"plex\"") && js.Contains("\"serverAddress\":true") && js.Contains("\"serverKey\":false") && !js.Contains("192.168"), "nas-status block: server plex, address / token as true / false only: " + js);
        }

        // Emby on 127.0.0.1: /emby/System/Info/Public, /emby/Library/Media/Updated with X-Emby-Token, a refused key
        static void Emby17()
        {
            var root = Path.Combine(Dir, "emby17"); Directory.CreateDirectory(root);
            var share = Path.Combine(root, "share"); Directory.CreateDirectory(share);
            var s = new TorSettings(); s.Fix(); s.NasOn = true; s.NasFolder = share; s.MediaServer = "emby"; s.Cat("Movies").Nas = "copy"; s.Cat("TV").Nas = "copy";
            var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>();
            const string Key = "9f8e7d6c5b4aEMBYKEY0001"; bool infoNeedsKey = false;
            var updates = new List<string>(); var heads = new List<string>(); var refreshes = 0;
            using (var em = new StandIn15(req =>
            {
                bool keyOk = req.Head.Contains("X-Emby-Token: " + Key + "\r\n");
                if (req.Path == "/emby/System/Info/Public") return infoNeedsKey && !keyOk ? StandIn15.Reply(401, "text/plain", "") : StandIn15.Reply(200, "application/json", "{\"LocalAddress\":\"http://127.0.0.1:8096\",\"ServerName\":\"Villa\",\"Version\":\"4.8.10.0\",\"Id\":\"e1\"}");
                if (req.Path == "/emby/Library/Media/Updated") { if (!keyOk) return StandIn15.Reply(401, "text/plain", ""); lock (updates) { updates.Add(req.Body); heads.Add(req.Head); } return StandIn15.Reply(204, "text/plain", ""); }
                if (req.Path == "/emby/Library/Refresh") { if (!keyOk) return StandIn15.Reply(401, "text/plain", ""); refreshes++; return StandIn15.Reply(204, "text/plain", ""); }
                return StandIn15.Reply(404, "text/plain", "");
            }))
            {
                s.EmbyUrl = "http://127.0.0.1:" + em.Port;
                var nas = new Nas(core, () => s, m => { lock (log) log.Add(m); }) { RefreshDelayMs = 0 };
                nas.RemoveLocal = id => System.Threading.Tasks.Task.FromResult<string>(null);
                string err; var info = nas.ServerInfo(out err);
                Ok(info == "Villa - Emby 4.8.10.0", "Emby GET /emby/System/Info/Public (no key): " + (info ?? err));
                lock (em.Seen) Ok(em.Seen.Last().Head.IndexOf("X-Emby-Token", StringComparison.OrdinalIgnoreCase) < 0, "...asked without the key");
                infoNeedsKey = true; info = nas.ServerInfo(out err);
                Ok(info == null && err.Contains("save the API key first"), "an Emby that wants a sign-in for its info, no key yet: says so (" + err + ")");
                nas.EmbyKey = Key; info = nas.ServerInfo(out err);
                Ok(info == "Villa - Emby 4.8.10.0", "...with the key saved: asked again with X-Emby-Token (" + (info ?? err) + ")");
                infoNeedsKey = false;
                Ok(!File.ReadAllText(Path.Combine(core.Dir, "nas.json")).Contains(Key) && nas.EmbyKey == Key && nas.HasKeyFor("emby") && !nas.HasKeyFor("plex") && !nas.HasKey, "the Emby key is kept encrypted (DPAPI) in nas.json, separate from Jellyfin's / Plex's");

                var movie = Finished(core, root, "Blade.Runner.1982.1080p.BluRay.x264-GRP", new[] { "Blade.Runner.1982.1080p.BluRay.x264-GRP.mkv", "x.txt" }, "Movies");
                var show = Finished(core, root, "Night.Sky.2019.S01E01.720p.WEB", new[] { "Night.Sky.2019.S01E01.720p.WEB.mkv", "x.txt" }, "TV");
                nas.Consider(movie); nas.Consider(show);
                var si = nas.Data.Items.First(i => i.Id == show.Id);
                Ok(si.Files[0].Dest == @"Shows\Night Sky\Season 01\Night Sky - S01E01.mkv", "Emby: the same folder names as Jellyfin (" + si.Files[0].Dest + ")");
                nas.Step(); nas.Step();
                lock (updates) Ok(updates.Count == 1 && updates[0].Contains("Blade Runner (1982)") && updates[0].Contains("Night Sky") && updates[0].Contains("\"UpdateType\":\"Created\"") && heads[0].Contains("X-Emby-Token: " + Key), "Emby told once (debounced): POST /emby/Library/Media/Updated with X-Emby-Token: " + (updates.Count > 0 ? updates[0] : ""));
                Ok(nas.LastRefreshError == null && nas.Refreshes == 1, "no refresh error");
                var j = Family.Json(nas.Json());
                Ok(j.Contains("\"server\":\"emby\"") && j.Contains("\"serverKey\":true") && j.Contains("\"serverAddress\":true") && !j.Contains(Key) && !j.Contains("127.0.0.1"), "nas-status: server emby, counts only: " + j);

                // a refused key
                nas.EmbyKey = "wrong-key-123456"; var m2 = Finished(core, root, "Alien.1979.1080p", new[] { "Alien.1979.1080p.mkv", "x.txt" }, "Movies");
                nas.Consider(m2); nas.Step(); nas.Step();
                Ok(nas.LastRefreshError == "Emby refused the API key.", "a refused Emby key: said so (" + nas.LastRefreshError + ")");
                lock (log) Ok(log.All(l => !l.Contains(Key) && !l.Contains("wrong-key-123456")), "the Emby key is never in the log");
                // none: just copy, nobody told
                s.MediaServer = "none"; int before; lock (em.Seen) before = em.Seen.Count;
                var m3 = Finished(core, root, "Heat.1995.1080p", new[] { "Heat.1995.1080p.mkv", "x.txt" }, "Movies");
                nas.Consider(m3); nas.Step(); nas.Step();
                lock (em.Seen) Ok(nas.Data.Items.First(i => i.Id == m3.Id).State == "done" && em.Seen.Count == before && File.Exists(Path.Combine(share, @"Movies\Heat (1995)\Heat (1995).mkv")), "None (just copy): copied, no server called");
                Ok(Family.Json(nas.Json()).Contains("\"server\":\"none\""), "nas-status: server none");
                s.EmbyUrl = "http://8.8.8.8:8096"; s.MediaServer = "emby"; Ok(nas.ServerInfo(out err) == null && err.Contains("isn't on your own network"), "a public Emby address: refused before connecting (" + err + ")");
                nas.Dispose();
            }
            core.Dispose();
        }

        // Plex on 127.0.0.1: /identity (no token), /library/sections, /library/sections/{key}/refresh?path=..., a refused token
        static void Plex17()
        {
            var root = Path.Combine(Dir, "plex17"); Directory.CreateDirectory(root);
            var share = Path.Combine(root, "share"); Directory.CreateDirectory(share);
            var s = new TorSettings(); s.Fix(); s.NasOn = true; s.NasFolder = share; s.MediaServer = "plex"; s.Cat("Movies").Nas = "copy"; s.Cat("TV").Nas = "copy"; s.Cat("Other").Nas = "copy";
            var core = new Core(Path.Combine(root, "core"), () => s); var log = new List<string>();
            const string Token = "pLeXtOkEn-AbC123xyz";
            var refreshed = new List<string>();
            using (var px = new StandIn15(req =>
            {
                bool tokOk = req.Head.Contains("X-Plex-Token: " + Token + "\r\n");
                if (req.Path == "/identity") return StandIn15.Reply(200, "application/xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<MediaContainer size=\"0\" apiVersion=\"1.1.1\" claimed=\"1\" machineIdentifier=\"abc123\" version=\"1.41.3.9314-a0bfb8370\"></MediaContainer>");
                if (req.Path == "/library/sections")
                {
                    if (!tokOk) return StandIn15.Reply(401, "text/html", "<html><head><title>Unauthorized</title></head><body><h1>401 Unauthorized</h1></body></html>");
                    return StandIn15.Reply(200, "application/xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<MediaContainer size=\"4\" allowSync=\"0\" title1=\"Plex Library\">" +
                        "<Directory allowSync=\"1\" key=\"1\" type=\"movie\" title=\"Movies\" agent=\"tv.plex.agents.movie\" scanner=\"Plex Movie\"><Location id=\"1\" path=\"/volume1/media/Movies\" /></Directory>" +
                        "<Directory key=\"4\" type=\"movie\" title=\"Kids Movies\"><Location id=\"4\" path=\"/volume1/media/Kids\" /></Directory>" +
                        "<Directory key=\"2\" type=\"show\" title=\"TV Shows\"><Location id=\"2\" path=\"/volume1/media/Shows\" /></Directory>" +
                        "<Directory key=\"3\" type=\"artist\" title=\"Music\"><Location id=\"3\" path=\"/volume1/media/Music\" /></Directory></MediaContainer>");
                }
                if (req.Path.StartsWith("/library/sections/") && req.Path.Contains("/refresh"))
                {
                    if (!tokOk) return StandIn15.Reply(401, "text/html", "");
                    lock (refreshed) refreshed.Add(req.Path); return StandIn15.Reply(200, "text/plain", "");
                }
                return StandIn15.Reply(404, "text/plain", "");
            }))
            {
                s.PlexUrl = "127.0.0.1:" + px.Port;   // no scheme: http:// assumed
                var nas = new Nas(core, () => s, m => { lock (log) log.Add(m); }) { RefreshDelayMs = 0 };
                nas.RemoveLocal = id => System.Threading.Tasks.Task.FromResult<string>(null);
                string err; var info = nas.ServerInfo(out err);
                Ok(info == "Plex Media Server 1.41.3.9314-a0bfb8370 (claimed)", "Plex GET /identity (no token): " + (info ?? err));
                lock (px.Seen) Ok(px.Seen.Last().Head.IndexOf("X-Plex-Token", StringComparison.OrdinalIgnoreCase) < 0, "...asked without the token");
                var secs = nas.Plex.Sections(out err);
                Ok(secs == null && err == "No Plex token saved.", "no token yet: the libraries aren't asked for (" + err + ")");
                nas.PlexToken = "not-the-token"; secs = nas.Plex.Sections(out err);
                Ok(secs == null && err == "Plex refused the token.", "a refused token: said so (" + err + ")");
                nas.PlexToken = Token;
                Ok(!File.ReadAllText(Path.Combine(core.Dir, "nas.json")).Contains(Token) && nas.PlexToken == Token && nas.HasKeyFor("plex"), "the X-Plex-Token is kept encrypted (DPAPI) in nas.json");
                secs = nas.Plex.Sections(out err);
                Ok(secs != null && secs.Count == 4 && secs.Count(x => x.Type == "movie") == 2 && secs.Single(x => x.Type == "show").Title == "TV Shows" && secs.First().Locations.SequenceEqual(new[] { "/volume1/media/Movies" }), "GET /library/sections: Movies / Kids Movies / TV Shows / Music with their folders");

                var movie = Finished(core, root, "The.Matrix.1999.1080p.BluRay.x264-GRP", new[] { "The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", "x.txt" }, "Movies");
                var show = Finished(core, root, "Night.Sky.2019.S01.720p.WEB", new[] { "Night.Sky.2019.S01E01.720p.WEB.mkv", "Night.Sky.2019.S01E02.720p.WEB.mkv" }, "TV");
                // libraries not picked yet: copied, Plex not asked, a clear note
                nas.Consider(movie); nas.Step(); nas.Step();
                Ok(nas.LastRefreshError != null && nas.LastRefreshError.StartsWith("Pick Plex's Movies / TV libraries") && refreshed.Count == 0, "libraries not picked: copied, Plex not asked (" + nas.LastRefreshError + ")");
                nas.Forget(movie.Id); s.PlexMovies = "1"; s.PlexShows = "2";
                Directory.Delete(Path.Combine(share, "Movies"), true);
                nas.Consider(movie); nas.Consider(show);
                var si = nas.Data.Items.First(i => i.Id == show.Id);
                Ok(si.Files.Select(f => f.Dest).SequenceEqual(new[] { @"Shows\Night Sky (2019)\Season 01\Night Sky (2019) - s01e01.mkv", @"Shows\Night Sky (2019)\Season 01\Night Sky (2019) - s01e02.mkv" }), "Plex picked: TV named by Plex's guide: " + string.Join("; ", si.Files.Select(f => f.Dest)));
                var other = Finished(core, root, "Some.Linux.ISO", new[] { "distro.iso", "README.txt" }, "Other"); nas.Consider(other);
                nas.Step(); nas.Step();
                lock (refreshed) Ok(refreshed.Count == 2 && refreshed.Contains("/library/sections/1/refresh?path=/volume1/media/Movies/The Matrix (1999)") && refreshed.Contains("/library/sections/2/refresh?path=/volume1/media/Shows/Night Sky (2019)"), "Plex: one partial scan per new folder, in the right library, at Plex's own path (debounced, Other not in a Plex library): " + string.Join(" | ", refreshed));
                Ok(nas.LastRefreshError == null && File.Exists(Path.Combine(share, @"Shows\Night Sky (2019)\Season 01\Night Sky (2019) - s01e02.mkv")), "copied and verified, no refresh error");
                lock (px.Seen) Ok(px.Seen.All(r => !r.Path.Contains(Token)), "the token is only ever in the X-Plex-Token header, never in a URL");
                lock (log) Ok(log.All(l => !l.Contains(Token) && !l.Contains("not-the-token")), "the token is never in the log");
                var j = Family.Json(nas.Json());
                Ok(j.Contains("\"server\":\"plex\"") && j.Contains("\"serverKey\":true") && !j.Contains(Token) && !j.Contains("127.0.0.1") && !j.Contains("Matrix"), "nas-status: server plex, counts only: " + j);

                // a library that's gone / a refused token at refresh time
                s.PlexMovies = "9"; var m2 = Finished(core, root, "Alien.1979.1080p", new[] { "Alien.1979.1080p.mkv", "x.txt" }, "Movies"); nas.Consider(m2); nas.Step(); nas.Step();
                Ok(nas.LastRefreshError != null && nas.LastRefreshError.Contains("isn't there any more"), "the picked library was removed in Plex: says so (" + nas.LastRefreshError + ")");
                s.PlexMovies = "1"; nas.PlexToken = "expired-token-xyz"; var m3 = Finished(core, root, "Heat.1995.1080p", new[] { "Heat.1995.1080p.mkv", "x.txt" }, "Movies"); nas.Consider(m3); nas.Step(); nas.Step();
                Ok(nas.LastRefreshError == "Plex refused the token.", "a refused token at refresh time: said so (" + nas.LastRefreshError + ")");
                // a direct refresh with a refused token
                Ok(nas.Plex.Refresh(new List<string[]> { new[] { "1", null } }) == "Plex refused the token.", "GET /library/sections/1/refresh with a refused token: said so");
                nas.PlexToken = Token; Ok(nas.Plex.Refresh(new List<string[]> { new[] { "1", null } }) == null && refreshed.Last() == "/library/sections/1/refresh", "a whole-library scan when the folder can't be mapped: /library/sections/1/refresh");
                s.PlexUrl = "http://8.8.8.8:32400"; Ok(nas.ServerInfo(out err) == null && err.Contains("isn't on your own network"), "a public Plex address: refused before connecting (" + err + ")");
                nas.Dispose();
            }
            core.Dispose();
        }

        static void AppCopy17()
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "HallowTorrents.exe");
            var dir = Path.Combine(Dir, "app17"); Directory.CreateDirectory(dir);
            // a 1.6 settings file: it must come up as Jellyfin
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true,\"NasOn\":true,\"NasFolder\":\"" + Path.Combine(dir, "share").Replace("\\", "\\\\") + "\",\"JellyfinUrl\":\"http://192.168.4.197:8096\"}");
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r != null && r.Contains("\"version\":\"" + Program.Version + "\"") && new Version(Program.Version) >= new Version("1.7"), "test copy: ping " + r);
                r = Cmd(exe, dir, "nas-status");
                Ok(r.StartsWith("ok {") && r.Contains("\"server\":\"jellyfin\"") && r.Contains("\"serverAddress\":true") && r.Contains("\"serverKey\":false") && !r.Contains("192.168") && !r.Contains("share"), "pipe nas-status from 1.6 settings: server jellyfin (migrated), no address: " + r);
                Ok(Cmd(exe, dir, "open plex") == "ok" && Cmd(exe, dir, "open emby") == "ok", "open plex / open emby (the Media server / NAS page)");
                Cmd(exe, dir, "quit"); app.WaitForExit(8000);
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } }
        }

        // NOT part of the suite: the two read-only GETs allowed against the real NAS (TorrentsTests.exe real17)
        static void Real17()
        {
            Func<string, IPAddress[]> none = h => null; string err;
            var plex = new PlexClient(() => "http://192.168.4.197:32400", () => null, none);
            var p = plex.Info(out err); Console.WriteLine("info Plex /identity: " + (p ?? err));
            var jf = new JellyfinClient(() => "http://192.168.4.197:8096", () => null, none);
            var j = jf.Info(out err); Console.WriteLine("info Jellyfin /System/Info/Public: " + (j ?? err));
            Ok(true, "real NAS read-only probe ran (results above are informational)");
        }
    }
}
