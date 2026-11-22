// Hallow Torrents 1.8 tests: for Hallow Remote 1.18 and Hallow Hub 2.17. The "categories" pipe command (names only) and search ids
// per caller ("--client <id>"), in-process against a local Torznab stand-in on 127.0.0.1 (a local indexer manager - no VPN, no real
// indexer) and over the pipe of a windowed test copy and of the headless exe. Nothing leaves this PC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static void V18Unit()
        {
            string err; var a = "night sky --n 3 --client phone-1 --cat tv";
            var c = RemoteApi.TakeClient(ref a, out err);
            Ok(c == "phone-1" && err == null && a == "night sky --n 3 --cat tv", "--client <id> is taken out of the search words: [" + a + "] client " + c);
            a = "r12 --client remote:abc.DEF_9 --category Movies"; c = RemoteApi.TakeClient(ref a, out err);
            Ok(c == "remote:abc.DEF_9" && a == "r12 --category Movies" && SplitAddSafe(a)[1] == "Movies", "--client before --category: the category is still read: [" + a + "]");
            a = "r12 --category Movies --client hub"; c = RemoteApi.TakeClient(ref a, out err);
            Ok(c == "hub" && SplitAddSafe(a)[0] == "r12" && SplitAddSafe(a)[1] == "Movies", "--client after --category: the category doesn't swallow it");
            a = "night sky"; c = RemoteApi.TakeClient(ref a, out err);
            Ok(c == null && err == null && a == "night sky", "no --client: null (the shared default set, as before)");
            a = "night sky --client"; c = RemoteApi.TakeClient(ref a, out err);
            Ok(c == null && err != null, "--client without a name: refused (" + err + ")");
            a = "night sky --client a/b"; c = RemoteApi.TakeClient(ref a, out err); var bad1 = err != null;
            a = "night sky --client " + new string('x', 65); c = RemoteApi.TakeClient(ref a, out err);
            Ok(bad1 && err != null, "--client with a / or longer than 64 characters: refused");
            Ok(SearchEngine.ValidClient("x") && SearchEngine.ValidClient(new string('a', 64)) && !SearchEngine.ValidClient("") && !SearchEngine.ValidClient("a b") && !SearchEngine.ValidClient(null), "ValidClient: 1-64 of A-Z a-z 0-9 . _ : -");

            var cats = new List<Category> { new Category { Name = "Movies", SaveFolder = @"D:\Secret\Movies", LibraryFolder = @"\\NAS\media\Movies" }, new Category { Name = "TV" }, new Category { Name = " " }, null };
            var r = RemoteApi.Categories(cats);
            Ok(r == "ok {\"categories\":[\"Movies\",\"TV\"],\"count\":2}", "categories: names only, in order, blank ones skipped: " + r);
            Ok(!r.Contains("Secret") && !r.Contains("NAS"), "...never a folder or the library path");
            Ok(RemoteApi.Categories(null) == "ok {\"categories\":[],\"count\":0}", "no categories: an empty list");
            var s = new TorSettings(); s.Fix(); r = RemoteApi.Categories(s.Categories);
            Ok(r.StartsWith("ok {\"categories\":[") && s.Categories.All(x => r.Contains("\"" + x.Name + "\"")), "a new install's categories: " + r);
        }
        static string[] SplitAddSafe(string a) { return HeadlessHost.SplitAdd(a); }

        // in-process: two clients, the default set, the window, eviction past 32 clients
        static void Scope18() { Scope18Async().Wait(); }
        static async Task Scope18Async()
        {
            var root = Path.Combine(Dir, "scope18"); Directory.CreateDirectory(root);
            var tz = new TzServer(); var u = tz.Url("");
            var items = Channel(Item("Scope.Show.S01E01.1080p.WEB-DL.x264-GRP", 2000 * MiB, 50, 60, u + "/dl/1", null, 600, 5040), Item("Scope.Show.S01E01.720p.HDTV.x264-PUB", 900 * MiB, 40, 45, u + "/dl/2", null, 600, 5040));
            tz.Route = path => Q(path, "apikey") != TzKey ? Xml("<?xml version=\"1.0\"?><error code=\"100\" description=\"Invalid API Key\"/>") : Q(path, "t") == "caps" ? Xml(CapsFull) : Xml(items);
            var se = new SearchEngine(null, null, Path.Combine(root, "se")); Indexer x;
            Ok(se.AddIndexer("torznab", "Scope feed", u + "/api", TzKey, out x) == null && se.IsLocal(u + "/api"), "a local Torznab stand-in (no VPN needed for a local indexer manager)");
            Func<string, Task<SearchOutcome>> run = cl => se.Run(SearchQuery.From("scope show s01e01", ""), DateTime.UtcNow, cl);

            var oa = await run("phone-a"); var ob = await run("phone-b");
            var ia = oa.Results.Select(r => r.Token).ToList(); var ib = ob.Results.Select(r => r.Token).ToList();
            Ok(oa.Error == null && ia.Count == 2 && ib.Count == 2 && !ia.Intersect(ib).Any(), "two clients search: each gets its own ids (" + string.Join(",", ia) + " / " + string.Join(",", ib) + ")");
            Ok(se.ByToken(ia[0], "phone-a") != null && se.ByToken(ib[0], "phone-b") != null, "phone A's ids still work after phone B searched (the 1.7 collision is gone)");
            Ok(se.ByToken(ia[0], "phone-b") == null && se.ByToken(ib[0], "phone-a") == null, "...and one client can't use the other's ids");
            Ok(se.ByToken(ia[0]) == null && se.ByToken(ia[0], null) == null, "...nor a caller without --client");

            var o0 = await se.Run(SearchQuery.From("scope show s01e01", ""), DateTime.UtcNow);   // the 1.3-1.7 call
            var i0 = o0.Results.Select(r => r.Token).ToList();
            Ok(i0.Count == 2 && se.ByToken(i0[0]) != null && se.ByToken(i0[0], null) == se.ByToken(i0[0]), "no client: the shared default set, as before");
            Ok(se.ByToken(ia[0], "phone-a") != null && se.ByToken(i0[0], "phone-a") == null, "...a default search doesn't touch phone A's ids, and phone A can't use the default ones");
            var oa2 = await run("phone-a"); var ia2 = oa2.Results.Select(r => r.Token).ToList();
            Ok(se.ByToken(ia[0], "phone-a") == null && se.ByToken(ia2[0], "phone-a") != null && se.ByToken(ib[0], "phone-b") != null && se.ByToken(i0[0]) != null, "phone A searching again replaces only its own ids (B and the default set keep theirs)");
            Ok(!ia2.Intersect(ia.Concat(ib).Concat(i0)).Any(), "ids are never reused");
            var ow = await run("window");
            Ok(se.ByToken(ib[0], "phone-b") != null && se.ByToken(i0[0]) != null && ow.Results.Count == 2, "the window's search (its own set) leaves the phones' and the default ids alone");

            // eviction: 32 clients remembered, the least recently used one goes
            for (int i = 0; i < 40; i++) { await run("c" + i); if (i % 8 == 0) se.ByToken("r1", "phone-b"); }   // phone-b stays in use
            Ok(se.ClientCount == SearchEngine.MaxClients, "at most " + SearchEngine.MaxClients + " clients are remembered (" + se.ClientCount + ")");
            Ok(se.ByToken(ib[0], "phone-b") != null, "...a client still in use is kept");
            Ok(se.ByToken(ia2[0], "phone-a") == null, "...the least recently used one is forgotten (its search-add asks to search again)");
            Ok(se.ByToken(i0[0]) != null, "...the default set is never dropped");

            var js = Family.Json(SearchEngine.Json(oa2, 10)); Ok(js.Contains("\"id\":\"" + ia2[0] + "\"") && !js.Contains(TzKey), "the JSON shape is unchanged (ids, no key)");
            Ok(tz.Seen().All(l => !l.Contains("client") && !l.Contains("phone-")), "--client is never sent to an indexer");
            try { tz.Dispose(); } catch { }
        }

        // the pipe of a windowed test copy and of the headless exe
        static void AppCopy18()
        {
            var bin = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location));
            var exe = Path.Combine(bin, "HallowTorrents.exe"); var hexe = Path.Combine(bin, "HallowTorrentsHeadless.exe");
            var dir = Path.Combine(Dir, "app18"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true,\"Categories\":[{\"Name\":\"Films\",\"SaveFolder\":\"D:\\\\Private\\\\Films\"},{\"Name\":\"Shows\"},{\"Name\":\"Linux ISOs\"}]}");
            File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.5|127.0.0.1\n");
            WritePipeSearch(dir); WriteVpn(dir, true);
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
            psi.EnvironmentVariables["HALLOW_TORRENTS_LOOPBACK_PUBLIC"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r != null && r.Contains("\"version\":\"" + Program.Version + "\"") && new Version(Program.Version) >= new Version("1.8"), "test copy: ping " + r);
                r = Cmd(exe, dir, "categories");
                Ok(r == "ok {\"categories\":[\"Films\",\"Shows\",\"Linux ISOs\"],\"count\":3}", "pipe categories: your names only, in order: " + r);
                Ok(!r.Contains("Private"), "...never a folder");
                for (int i = 0; i < 40 && !Cmd(exe, dir, "status").Contains("\"vpnLocked\":false"); i++) Thread.Sleep(250);
                PipeClients(exe, dir, "app");
                Cmd(exe, dir, "quit"); app.WaitForExit(8000);
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } try { PipeTz.Dispose(); } catch { } }

            // the headless exe answers the same
            if (!File.Exists(hexe)) { Ok(false, "HallowTorrentsHeadless.exe is built"); return; }
            var hdir = Path.Combine(Dir, "headless18"); Directory.CreateDirectory(hdir);
            File.WriteAllText(Path.Combine(hdir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true,\"Categories\":[{\"Name\":\"Films\"},{\"Name\":\"Shows\"}]}");
            File.WriteAllText(Path.Combine(hdir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.5|127.0.0.1\n");
            WritePipeSearch(hdir); WriteVpn(hdir, true);
            var hp = new ProcessStartInfo(hexe) { UseShellExecute = false, CreateNoWindow = true };
            hp.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = hdir; hp.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; hp.EnvironmentVariables["HALLOW_TORRENTS_LOOPBACK_PUBLIC"] = "1";
            var h = Process.Start(hp);
            try
            {
                string r = null; for (int i = 0; i < 60 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, hdir, "ping"); }
                Ok(r != null && r.Contains("\"headless\":true"), "headless copy: ping " + r);
                r = Cmd(exe, hdir, "categories");
                Ok(r == "ok {\"categories\":[\"Films\",\"Shows\"],\"count\":2}", "headless: categories the same: " + r);
                for (int i = 0; i < 40 && !Cmd(exe, hdir, "status").Contains("\"vpnLocked\":false"); i++) Thread.Sleep(250);
                PipeClients(exe, hdir, "headless");
                Cmd(exe, hdir, "quit"); h.WaitForExit(10000);
            }
            finally { try { if (!h.HasExited) h.Kill(); } catch { } try { PipeTz.Dispose(); } catch { } }
        }
        static List<string> PipeIds(string r)
        {
            if (!r.StartsWith("ok ")) return new List<string>();
            var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.Substring(3));
            return ((System.Collections.ArrayList)d["results"]).Cast<Dictionary<string, object>>().Select(x => (string)x["id"]).ToList();
        }
        static void PipeClients(string exe, string dir, string who)
        {
            var ra = Cmd(exe, dir, "search pipe show s01e01 --client phone-a --n 3"); var rb = Cmd(exe, dir, "search pipe show s01e01 --n 3 --client phone-b");
            var ia = PipeIds(ra); var ib = PipeIds(rb);
            Ok(ia.Count == 3 && ib.Count == 3 && !ia.Intersect(ib).Any() && ra.Contains("\"client\":\"phone-a\"") && rb.Contains("\"client\":\"phone-b\""), who + ": two clients search over the pipe, each answer names its client: " + string.Join(",", ia) + " / " + string.Join(",", ib));
            var other = Cmd(exe, dir, "search-add " + ia[0] + " --client phone-b");
            Ok(other.StartsWith("error no search result"), who + ": phone B can't add phone A's result: " + other);
            var add = Cmd(exe, dir, "search-add " + ia[0] + " --category shows --client phone-a");
            Ok(add.StartsWith("ok {\"id\":") && add.Contains("\"category\":\"Shows\""), who + ": phone A adds its own result after phone B searched (the 1.7 collision is gone): " + add);
            var r0 = Cmd(exe, dir, "search pipe show s01e01 --n 2"); var i0 = PipeIds(r0);
            Ok(i0.Count == 2 && r0.Contains("\"client\":null"), who + ": without --client: the shared set, as before (\"client\":null)");
            var bKept = Cmd(exe, dir, "search-add " + ib[0] + " --client phone-b");   // the same torrent A added: "already in the list" - but the id was found
            Ok(Cmd(exe, dir, "search-add " + i0[1] + " --client phone-a").StartsWith("error no search result") && !bKept.StartsWith("error no search result"), who + ": ...the default search didn't touch phone B's ids, and phone A can't use the default ones: " + bKept);
            var bad = Cmd(exe, dir, "search pipe show --client bad/name");
            Ok(bad.StartsWith("error --client needs a name"), who + ": a bad client name: " + bad);
            Ok(Cmd(exe, dir, "search-add r999999 --client phone-a").StartsWith("error no search result"), who + ": an unknown id: error");
        }
    }
}
