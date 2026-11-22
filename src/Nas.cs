// Hallow Torrents 1.6 - "Send to Jellyfin": finished downloads copied to a folder on your NAS (an SMB share Jellyfin's library points
// at) with Jellyfin's names, and Jellyfin asked to look at them.
//   * Only after Microsoft Defender's scan (Finish.cs); flagged downloads and anything Defender found something in are never sent.
//   * The copy is resumable and verified: "<name>.hallow-part" (continued where it stopped, after its existing bytes were checked),
//     then size + SHA-256 against the download, then renamed. A file already there with the same contents counts as done; a
//     different file is never overwritten.
//   * Per category: copy and keep seeding from this PC / move after the seeding goal (copy now, the PC's copy to the Recycle Bin
//     when the goal is reached) / move now (copy, then stop seeding and the PC's copy to the Recycle Bin). Never a permanent delete.
//   * NAS offline: the item waits and is retried with back-off (30 s, 1, 2, 4... up to 30 min).
//   * Never through the VPN binding, and only to your own network: the destination must be a local folder, or a UNC path / mapped
//     drive whose host is a private (home network / Tailscale) address or a home-network name that resolves only to private
//     addresses (PrivateNet, the 1.4 rules). A public host is refused. File copies go through Windows' own SMB client with the
//     credentials Windows saved when you mapped the share - Hallow Torrents never asks for or stores the NAS password.
//   * Jellyfin (optional): its address (private only, like the share) and an API key kept with DPAPI in nas.json, never shown or
//     logged; GET /System/Info/Public (no key) shows the server; after imports POST /Library/Media/Updated (falls back to
//     /Library/Refresh), debounced.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents
{
    [DataContract]
    public class NasFile
    {
        [DataMember] public string Src;     // on this PC (absolute)
        [DataMember] public string Dest;    // relative to the NAS folder: "Movies\Title (Year)\Title (Year).mkv"
        [DataMember] public long Size;
        [DataMember] public bool Done;
    }
    [DataContract]
    public class NasItem
    {
        [DataMember] public string Id, Name, Category, Rule;   // Rule: "copy" / "after" / "now"
        [DataMember] public string State;                       // "queued" / "copying" / "waiting" (NAS offline) / "done" / "failed"
        [DataMember] public int Attempts; [DataMember] public string NextTry, Error, Added, Finished;
        [DataMember] public long Bytes, Copied;
        [DataMember] public bool LocalRemoved;                  // move rules: the PC's copy went to the Recycle Bin
        [DataMember] public List<NasFile> Files = new List<NasFile>();
        public double Progress { get { return Bytes <= 0 ? (State == "done" ? 100 : 0) : Math.Min(100, Copied * 100.0 / Bytes); } }
    }
    [DataContract]
    public class NasData
    {
        [DataMember] public List<NasItem> Items = new List<NasItem>();
        [DataMember] public string JellyfinKey;   // DPAPI (Secret.Protect), never the key itself
    }

    // where it may copy to (pure apart from the lookup, tested)
    public static class NasPath
    {
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)] static extern int WNetGetConnection(string local, StringBuilder remote, ref int length);
        public static Func<string, string> MappedDrive = d =>
        {
            try { var sb = new StringBuilder(512); int n = sb.Capacity; return WNetGetConnection(d, sb, ref n) == 0 ? sb.ToString() : null; } catch { return null; }
        };
        public static string HostOf(string unc) { var p = unc.TrimStart('\\').Split('\\'); return p.Length > 0 ? p[0] : ""; }
        // null = fine; otherwise why not. resolve(host) = every address it has (null = can't be looked up)
        public static string Check(string folder, Func<string, IPAddress[]> resolve)
        {
            folder = (folder ?? "").Trim();
            if (folder.Length == 0) return "Pick the NAS folder first (for example \\\\192.168.4.197\\media).";
            if (folder.StartsWith("\\\\?\\") || folder.StartsWith("\\\\.\\")) return "That kind of path isn't supported - use \\\\NAS\\share\\folder.";
            if (folder.StartsWith("\\\\"))
            {
                var host = HostOf(folder); if (host.Length == 0 || folder.TrimStart('\\').Split('\\').Length < 2) return "A NAS path looks like \\\\192.168.4.197\\media.";
                IPAddress lit; IPAddress[] addrs;
                if (IPAddress.TryParse(host, out lit)) addrs = new[] { lit };
                else if (host.EndsWith(".ipv6-literal.net", StringComparison.OrdinalIgnoreCase)) return "Use the NAS's IPv4 address or its name.";
                else addrs = resolve(host);
                if (addrs == null || addrs.Length == 0) return "Couldn't find " + host + " on your network.";
                if (!PrivateNet.AllPrivate(addrs)) return host + " isn't on your own network (a public address) - Hallow Torrents only sends to a NAS at home or on Tailscale.";
                return null;
            }
            if (folder.Length >= 3 && char.IsLetter(folder[0]) && folder[1] == ':' && (folder[2] == '\\' || folder[2] == '/'))
            {
                var unc = MappedDrive(folder.Substring(0, 2));
                if (unc != null) return Check(unc.TrimEnd('\\') + "\\" + folder.Substring(3), resolve);   // a mapped network drive: its share's host
                return null;   // a folder on this PC
            }
            return "Use a folder like \\\\192.168.4.197\\media (or a drive letter you mapped to it).";
        }
        // can we write there right now? (writes and deletes a small temp file)
        public static string Reach(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return "The folder isn't reachable (NAS off, the share not mapped yet, or the path is wrong).";
                var f = Path.Combine(folder, ".hallow-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(f, "Hallow Torrents write test - safe to delete"); File.Delete(f);
                return null;
            }
            catch (UnauthorizedAccessException) { return "Windows can see the folder but may not write there - check the share's permissions for your NAS user."; }
            catch (Exception e) { return "Couldn't write there: " + e.Message; }
        }
        public static bool LooksOffline(Exception e)
        {
            if (e is DirectoryNotFoundException || e is DriveNotFoundException) return true;
            var io = e as IOException; if (io == null) return false;
            int code = Marshal.GetHRForException(io) & 0xFFFF;
            return code == 53 || code == 64 || code == 67 || code == 121 || code == 1231 || code == 59 || code == 3 || code == 1219 || code == 1311;
        }
    }

    // the Jellyfin server on your network (private address only, never through the VPN binding)
    public class JellyfinClient
    {
        public Func<string> Url, Key; public Func<string, IPAddress[]> Resolve;
        public JellyfinClient(Func<string> url, Func<string> key, Func<string, IPAddress[]> resolve) { Url = url; Key = key; Resolve = resolve; }
        // the address checked like the NAS: null = refused (with why)
        public IPAddress[] Pinned(out Uri u, out string why)
        {
            why = null; u = null; var url = (Url() ?? "").Trim();
            if (url.Length == 0) { why = "No Jellyfin address set."; return null; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) { why = "That isn't a web address (http://192.168.4.197:8096)."; return null; }
            var host = u.Host.Trim('[', ']'); IPAddress lit; var addrs = IPAddress.TryParse(host, out lit) ? new[] { lit } : Resolve(host);
            if (addrs == null || addrs.Length == 0) { why = "Couldn't find " + host + " on your network."; return null; }
            if (!PrivateNet.AllPrivate(addrs)) { why = host + " isn't on your own network - Jellyfin is only called at a private address."; return null; }
            return addrs;
        }
        HttpClient Client(IPAddress[] pinned)
        {
            var c = Net.DirectHttp("jellyfin", pinned); c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HallowTorrents/" + Program.Version);
            return c;
        }
        // GET /System/Info/Public (no key): "Villa - Jellyfin 4.9.5.0"
        public string Info(out string error)
        {
            error = null; Uri u; var pin = Pinned(out u, out error); if (pin == null) return null;
            try
            {
                using (var c = Client(pin))
                {
                    var t = c.GetStringAsync(new Uri(u, "/System/Info/Public")).GetAwaiter().GetResult();
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(t);
                    object n, v; d.TryGetValue("ServerName", out n); d.TryGetValue("Version", out v);
                    return (n ?? "Jellyfin") + " - Jellyfin " + (v ?? "?");
                }
            }
            catch (Exception e) { error = "Jellyfin didn't answer: " + Redact(e.Message); return null; }
        }
        // tell Jellyfin about new folders (falls back to a full library scan)
        public string Refresh(IList<string> paths)
        {
            Uri u; string why; var pin = Pinned(out u, out why); if (pin == null) return why;
            var key = Key(); if (string.IsNullOrEmpty(key)) return "No Jellyfin API key set - Jellyfin finds them at its next scheduled scan.";
            try
            {
                using (var c = Client(pin))
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", key);
                    var body = Family.Json(new Dictionary<string, object> { { "Updates", paths.Select(p => (object)new Dictionary<string, object> { { "Path", p }, { "UpdateType", "Created" } }).ToList() } });
                    var r = c.PostAsync(new Uri(u, "/Library/Media/Updated"), new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                    if (r.IsSuccessStatusCode) return null;
                    if ((int)r.StatusCode == 401 || (int)r.StatusCode == 403) return "Jellyfin refused the API key.";
                    r = c.PostAsync(new Uri(u, "/Library/Refresh"), new StringContent("", Encoding.UTF8)).GetAwaiter().GetResult();
                    return r.IsSuccessStatusCode ? null : "Jellyfin answered " + (int)r.StatusCode + ".";
                }
            }
            catch (Exception e) { return "Jellyfin didn't answer: " + Redact(e.Message); }
        }
        string Redact(string s) { var k = Key(); return string.IsNullOrEmpty(k) || s == null ? s : s.Replace(k, "[key]"); }
    }

    public class Nas : IDisposable
    {
        readonly Core core; readonly Func<TorSettings> settings; readonly Action<string> log; readonly string file;
        public NasData Data = new NasData(); public readonly object Lock = new object();
        public Func<string, Task<string>> RemoveLocal;   // move rules: the torrent leaves the list, its files to the Recycle Bin (tests: a stand-in)
        public Func<string, IPAddress[]> Resolve;          // NAS / Jellyfin host names (PrivateNet: home-network names with Windows' resolver, others over the VPN)
        public JellyfinClient Jellyfin;
        public string LastRefresh, LastRefreshError; public int Refreshes;
        public int RefreshDelayMs = 20000;
        volatile bool stop; readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly List<string> pendingRefresh = new List<string>(); DateTime refreshAt = DateTime.MaxValue;
        public Func<DateTime> Now = () => DateTime.Now;

        public Nas(Core core, Func<TorSettings> settings, Action<string> log)
        {
            this.core = core; this.settings = settings; this.log = s => { try { log(Redact(s)); } catch { } };
            file = core == null ? null : Path.Combine(core.Dir, "nas.json");
            RemoveLocal = id => core.Remove(id, true);
            Resolve = h => PrivateNet.Resolve(h, core == null ? null : core.BoundIp, core == null ? null : core.BoundDns);
            Jellyfin = new JellyfinClient(() => settings().JellyfinUrl, () => JellyfinKey, h => Resolve(h));
            Load();
            foreach (var i in Data.Items.Where(x => x.State == "copying")) i.State = "queued";
        }
        string Redact(string s) { var k = JellyfinKey; return string.IsNullOrEmpty(k) || s == null ? s : s.Replace(k, "[key]"); }
        void Load() { try { if (file != null && File.Exists(file)) using (var f = File.OpenRead(file)) Data = (NasData)new DataContractJsonSerializer(typeof(NasData)).ReadObject(f) ?? new NasData(); } catch { Data = new NasData(); } if (Data.Items == null) Data.Items = new List<NasItem>(); }
        public void Save()
        {
            if (file == null || TorSettings.Frozen) return;
            lock (Lock) { try { Directory.CreateDirectory(Path.GetDirectoryName(file)); var tmp = file + ".tmp"; using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(NasData)).WriteObject(f, Data); if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file); } catch { } }
        }
        // the API key: DPAPI in nas.json, never in settings.json, the log, status or the pipe
        public string JellyfinKey { get { return Secret.Unprotect(Data.JellyfinKey); } set { Data.JellyfinKey = string.IsNullOrWhiteSpace(value) ? null : Secret.Protect(value.Trim()); Save(); } }
        public bool HasKey { get { return !string.IsNullOrEmpty(Data.JellyfinKey); } }

        public void Start() { new Thread(Loop) { IsBackground = true, Name = "nas", Priority = ThreadPriority.BelowNormal }.Start(); }
        public void Dispose() { stop = true; wake.Set(); }
        public void Kick() { wake.Set(); }

        // the destination folder for a category's files (Movies / Shows subfolders by naming; a folder per category otherwise)
        public static string SubFolder(TorSettings s, Category c)
        {
            if (c.Naming == "movie") return string.IsNullOrWhiteSpace(s.NasMovies) ? "" : s.NasMovies.Trim().Trim('\\');
            if (c.Naming == "tv") return string.IsNullOrWhiteSpace(s.NasShows) ? "" : s.NasShows.Trim().Trim('\\');
            return Media.Safe(c.Name);
        }
        public string CheckDestination() { var s = settings(); return NasPath.Check(s.NasFolder, h => Resolve(h)) ?? NasPath.Reach(s.NasFolder); }

        // after Finish.Run (Defender's scan done): send it if its category says so. Returns what happened (or null = nothing to do).
        public string Consider(Entry x)
        {
            var s = settings(); if (!s.NasOn || x == null) return null;
            var c = s.Cat(x.Category); if (c == null || string.IsNullOrEmpty(c.Nas) || c.Nas == "off") return null;
            if (x.Created) return null;
            if (x.Flags != null && x.Flags.Count > 0) return Note(x, "Not sent to the NAS: it has files that look suspicious.");
            if (x.Scan == "threats") return Note(x, "Not sent to the NAS: Microsoft Defender found something in it.");
            if (x.Scan == "scanning" || x.Scan == "waiting") return null;   // Finish.Run calls again when the scan is done
            lock (Lock) if (Data.Items.Any(i => i.Id == x.Id && i.State != "failed")) return null;
            var files = x.FileList.Select((f, i) => new { f, i }).Where(q => x.PrioOf(q.i) > 0).Select(q => q.f).ToList();
            var plan = Media.Plan(x.Name, files, c.Naming);
            if (plan.Count == 0) return Note(x, "Nothing to send to the NAS (no " + (c.Naming == "tv" ? "episodes" : c.Naming == "movie" ? "video" : "files") + ").");
            var sub = SubFolder(s, c);
            // Radarr / Sonarr import from where it is: never moved away
            var rule = x.Api ? "copy" : c.Nas;
            var item = new NasItem { Id = x.Id, Name = x.Name, Category = c.Name, Rule = rule, State = "queued", Added = Family.Time(Now()) };
            foreach (var p in plan)
            {
                var src = core.FullPathOf(x.Id, p[0]); long len = 0; try { len = new FileInfo(src).Length; } catch { }
                item.Files.Add(new NasFile { Src = src, Dest = sub.Length == 0 ? p[1] : Path.Combine(sub, p[1]), Size = len });
            }
            item.Bytes = item.Files.Sum(f => f.Size);
            lock (Lock) { Data.Items.RemoveAll(i => i.Id == x.Id); Data.Items.Add(item); }
            Save(); log("nas: queued " + x.Id + " (" + item.Files.Count + " files, " + rule + ")"); Kick();
            return "Queued for the NAS.";
        }
        string Note(Entry x, string t) { log("nas: " + x.Id + " " + t); return t; }
        public void Retry(string id) { lock (Lock) foreach (var i in Data.Items.Where(q => q.Id == id && q.State != "done")) { i.State = "queued"; i.NextTry = null; i.Attempts = 0; i.Error = null; } Save(); Kick(); }
        public void Forget(string id) { lock (Lock) Data.Items.RemoveAll(q => q.Id == id && q.State != "copying"); Save(); }

        void Loop()
        {
            while (!stop)
            {
                try { Step(); } catch (Exception e) { log("nas: " + e.Message); }
                wake.WaitOne(2000);
            }
        }
        // one pass (tests call it directly): due items copied, seeding goals checked for "after", Jellyfin told (debounced)
        public void Step()
        {
            var now = Now();
            List<NasItem> due; lock (Lock) due = Data.Items.Where(i => (i.State == "queued" || i.State == "waiting") && (i.NextTry == null || ParseTime(i.NextTry) <= now)).ToList();
            foreach (var i in due) { if (stop) return; Copy(i); }
            // move rules: the PC's copy goes once it's on the NAS (now) or once the seeding goal is reached (after)
            List<NasItem> moves; lock (Lock) moves = Data.Items.Where(i => i.State == "done" && !i.LocalRemoved && i.Rule != "copy").ToList();
            foreach (var i in moves)
            {
                var x = core.Get(i.Id);
                if (x == null) { i.LocalRemoved = true; continue; }
                if (i.Rule == "after" && !x.LimitHit) continue;
                var err = RemoveLocal(i.Id).GetAwaiter().GetResult();
                i.LocalRemoved = err == null; if (err != null) i.Error = "Copied, but the PC's copy couldn't go to the Recycle Bin: " + err;
                log("nas: " + i.Id + " " + (err == null ? "seeding stopped, the PC's copy is in the Recycle Bin" : err)); Save();
            }
            List<string> paths = null;
            lock (pendingRefresh) if (pendingRefresh.Count > 0 && Now() >= refreshAt) { paths = pendingRefresh.Distinct().ToList(); pendingRefresh.Clear(); refreshAt = DateTime.MaxValue; }
            if (paths != null)
            {
                var err = Jellyfin.Refresh(paths); Refreshes++; LastRefresh = Family.Time(Now()); LastRefreshError = err;
                log("nas: jellyfin " + (err ?? "asked to look at " + paths.Count + " folder(s)"));
            }
        }
        static DateTime ParseTime(string s) { DateTime d; return DateTime.TryParse(s, out d) ? d : DateTime.MinValue; }
        public static TimeSpan Backoff(int attempts) { var s = 30 * Math.Pow(2, Math.Max(0, attempts - 1)); return TimeSpan.FromSeconds(Math.Min(1800, s)); }

        void Copy(NasItem i)
        {
            var s = settings(); var root = s.NasFolder;
            var bad = NasPath.Check(root, h => Resolve(h));
            if (bad != null) { Fail(i, bad, false); return; }
            var reach = NasPath.Reach(root);
            if (reach != null) { Wait(i, reach); return; }
            i.State = "copying"; i.Error = null; Save();
            try
            {
                i.Copied = i.Files.Where(f => f.Done).Sum(f => f.Size);
                foreach (var f in i.Files.Where(f => !f.Done))
                {
                    if (stop) { i.State = "queued"; return; }
                    var dest = Path.Combine(root, f.Dest);
                    var err = CopyOne(f.Src, dest, n => i.Copied += n);
                    if (err != null) { Fail(i, err, true); return; }
                    f.Done = true; Save();
                }
                i.State = "done"; i.Finished = Family.Time(Now()); i.Attempts = 0; i.Copied = i.Bytes; Save();
                log("nas: sent " + i.Id + " (" + i.Files.Count + " files, verified)");
                var folders = i.Files.Select(f => Path.Combine(root, f.Dest.Split('\\', '/')[0] == f.Dest ? "" : TopFolder(f.Dest))).Distinct().ToList();
                lock (pendingRefresh) { pendingRefresh.AddRange(folders); refreshAt = Now().AddMilliseconds(RefreshDelayMs); }
            }
            catch (Exception e)
            {
                if (NasPath.LooksOffline(e) || NasPath.Reach(root) != null) Wait(i, "The NAS went away while copying - it carries on from where it stopped.");
                else Fail(i, e.Message, true);
            }
        }
        // "Movies\Title (Year)\Title (Year).mkv" -> "Movies\Title (Year)"; "Shows\Show\Season 01\x.mkv" -> "Shows\Show"
        static string TopFolder(string dest)
        {
            var p = dest.Split('\\', '/'); if (p.Length <= 2) return p[0];
            return p[0] + "\\" + p[1];
        }
        void Wait(NasItem i, string why) { i.Attempts++; i.State = "waiting"; i.Error = why; i.NextTry = Family.Time(Now() + Backoff(i.Attempts)); Save(); log("nas: waiting " + i.Id + " - " + why); }
        void Fail(NasItem i, string why, bool retry)
        {
            i.Attempts++; i.Error = why;
            if (retry && i.Attempts < 6) { i.State = "waiting"; i.NextTry = Family.Time(Now() + Backoff(i.Attempts)); } else i.State = "failed";
            Save(); log("nas: " + i.State + " " + i.Id + " - " + why);
        }

        // one file: resumable (.hallow-part), verified (size + SHA-256), then renamed; never overwrites a different file. null = done
        public static Func<int> BufferSize = () => 1 << 20;
        public static string CopyOne(string src, string dest, Action<long> progress)
        {
            if (!File.Exists(src)) return "The downloaded file isn't there any more: " + Path.GetFileName(src);
            var len = new FileInfo(src).Length;
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(dest))
            {
                if (new FileInfo(dest).Length == len && Hash(dest, -1) == Hash(src, -1)) { progress(len); return null; }   // already there
                return "A different file is already on the NAS at " + dest + " - left alone.";
            }
            var part = dest + ".hallow-part"; long have = 0;
            if (File.Exists(part))
            {
                have = new FileInfo(part).Length;
                // continue only if what's there is exactly the start of the file
                if (have > len || Hash(part, -1) != Hash(src, have)) { File.Delete(part); have = 0; }
            }
            progress(have);
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var o = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                i.Position = have; var buf = new byte[BufferSize()]; int n;
                while ((n = i.Read(buf, 0, buf.Length)) > 0) { o.Write(buf, 0, n); progress(n); }
            }
            if (new FileInfo(part).Length != len) return "The copy came out the wrong size - it's tried again.";
            if (Hash(part, -1) != Hash(src, -1)) { try { File.Delete(part); } catch { } return "The copy didn't match the download (checksum) - it's copied again."; }
            File.Move(part, dest);
            return null;
        }
        // SHA-256 of a file, or of its first `count` bytes (count < 0 = all)
        public static string Hash(string f, long count)
        {
            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var h = SHA256.Create())
            {
                if (count < 0) return Convert.ToBase64String(h.ComputeHash(s));
                var buf = new byte[1 << 16]; long left = count;
                while (left > 0) { int n = s.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) break; h.TransformBlock(buf, 0, n, null, 0); left -= n; }
                h.TransformFinalBlock(new byte[0], 0, 0); return Convert.ToBase64String(h.Hash);
            }
        }

        // counts only (status.json and the pipe never carry file or torrent names)
        public Dictionary<string, object> Json()
        {
            var s = settings(); List<NasItem> items; lock (Lock) items = Data.Items.ToList();
            return new Dictionary<string, object> { { "on", s.NasOn }, { "destination", !string.IsNullOrWhiteSpace(s.NasFolder) }, { "queued", items.Count(i => i.State == "queued") }, { "copying", items.Count(i => i.State == "copying") },
                { "waiting", items.Count(i => i.State == "waiting") }, { "failed", items.Count(i => i.State == "failed") }, { "done", items.Count(i => i.State == "done") },
                { "jellyfin", !string.IsNullOrWhiteSpace(s.JellyfinUrl) }, { "jellyfinKey", HasKey }, { "refreshes", Refreshes }, { "lastRefresh", LastRefresh } };
        }
    }
}
