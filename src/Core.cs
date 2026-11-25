// Hallow Torrents - the torrent engine (MonoTorrent) and the list of torrents.
//   * The engine only exists while the VPN is up: it is built with every socket bound to the VPN IP (Net.Bound) and is stopped and
//     thrown away the moment the VPN goes down or its IP changes (SetVpn) - not paused, stopped - and built again once it's back.
//   * The list (torrents.json) is kept whether or not the engine exists, so adding, pausing or removing works while locked too.
//   * Every second: speeds and totals, the queue (N downloads at once), the game hold, ratio / seeding-time limits, finished downloads.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace HallowTorrents
{
    [DataContract]
    public class Entry
    {
        [DataMember] public string Id;            // v1 info hash (hex), or v2 when there's no v1
        [DataMember] public string Name;
        [DataMember] public string Magnet;
        [DataMember] public string TorrentFile;   // our copy: <data>\torrents\<id>.torrent (null until a magnet has its metadata)
        [DataMember] public string SavePath;
        [DataMember] public string Category;
        [DataMember] public int Queue;
        [DataMember] public bool UserPaused;
        [DataMember] public string AddedAt, CompletedAt;
        [DataMember] public double SeedSecs;
        [DataMember] public long Up, Down, Size;
        [DataMember] public List<int> Prio;       // per file: 0 skip, 1 low, 2 normal, 3 high (null = all normal)
        [DataMember] public int DownKBps, UpKBps; // per-torrent limits (0 = none)
        [DataMember] public bool Sequential, SuperSeed, NoLimit, LimitHit;
        [DataMember] public string Scan, ScanText; // "", "waiting", "scanning", "clean", "threats", "error"
        [DataMember] public List<string> Flags;    // "file|why"
        [DataMember] public string Handoff;        // what the library hand-off did (or why it didn't)
        [DataMember] public List<string> Files;    // "path|length" (once the metadata is known)
        // v1.1: per file, where it is now - relative to SavePath (renamed / moved by you) or absolute (moved into the library); null or
        // an empty item = the torrent's own place. Re-applied every time the engine is built.
        [DataMember] public List<string> Paths;
        [DataMember] public List<string> Trackers; // v1.1: the tracker list you edited (null = the torrent's own)
        [DataMember] public bool Created;          // v1.1: made here with the torrent creator (seeded straight away, never "finished")
        // v1.2: added through the Web API (Radarr / Sonarr / Prowlarr): its files stay where they are for the app to import
        [DataMember] public bool Api;
        [DataMember] public string Tags;           // v1.2: qBittorrent-style tags, "a,b"
        // v1.2: share limits set through the Web API (qBittorrent's setShareLimits): null = the category / global limit, -1 = none
        [DataMember] public double? ShareRatio;
        [DataMember] public int? ShareMinutes;
        public List<TFile> FileList { get { return (Files ?? new List<string>()).Select(f => { var i = f.LastIndexOf('|'); long l; return new TFile(f.Substring(0, i), long.TryParse(f.Substring(i + 1), out l) ? l : 0); }).ToList(); } }
        public int PrioOf(int i) { return Prio != null && i < Prio.Count ? Prio[i] : 2; }
    }

    // what the window and status show for one torrent
    public class TorrentView
    {
        public string Id, Name, State, Category, Scan, ScanText, Handoff, SavePath, Error, Hash;
        public long Size, Done, DownRate, UpRate, Up, Down; public double Progress, Ratio; public TimeSpan? Eta; public int Seeds, Leechs, Queue;
        public bool Paused, Complete, Flagged, Sequential, SuperSeed; public int DownKBps, UpKBps; public string AddedAt, CompletedAt; public double SeedSecs;
        public List<string> Flags = new List<string>(); public List<FileView> Files = new List<FileView>(); public List<TrackerView> Trackers = new List<TrackerView>();
    }
    public class FileView { public int Index; public string Path, Rel; public long Length; public double Progress; public int Prio; }
    public class TrackerView { public string Url, Status, Message; }

    // the queue and limits as a pure function (tested): which torrents should be running right now
    public static class Plan
    {
        // RatioCap / SeedCap (v1.2, the Web API's per-torrent share limits): null = use RatioGoal / the global minutes, negative = no limit
        public class Item { public string Id; public int Queue; public bool UserPaused, Complete, LimitHit, NoLimit; public double Ratio, SeedMinutes, RatioGoal; public double? RatioCap, SeedCap; }
        public static HashSet<string> Running(IList<Item> items, int maxActive, string hold, double seedMinutesLimit)
        {
            var r = new HashSet<string>();
            if (hold == "vpn" || hold == "game") return r;
            foreach (var i in items.Where(x => x.Complete && !x.UserPaused && !LimitReached(x, seedMinutesLimit))) r.Add(i.Id);
            foreach (var i in items.Where(x => !x.Complete && !x.UserPaused).OrderBy(x => x.Queue).Take(Math.Max(1, maxActive))) r.Add(i.Id);
            return r;
        }
        public static bool LimitReached(Item i, double seedMinutesLimit)
        {
            if (i.NoLimit) return false;
            if (i.LimitHit) return true;
            if (i.RatioCap.HasValue) { if (i.RatioCap.Value >= 0 && i.Ratio >= i.RatioCap.Value) return true; }
            else if (i.RatioGoal > 0 && i.Ratio >= i.RatioGoal) return true;
            var minutes = i.SeedCap.HasValue ? i.SeedCap.Value : seedMinutesLimit > 0 ? seedMinutesLimit : -1;
            if (minutes >= 0 && i.SeedMinutes >= minutes) return true;
            return false;
        }
        public static string Eta(long left, long rate)
        {
            if (left <= 0) return "";
            if (rate <= 0) return "∞";
            var s = left / (double)rate; if (s > 365 * 86400) return "∞";
            var t = TimeSpan.FromSeconds(s);
            return t.TotalDays >= 1 ? (int)t.TotalDays + "d " + t.Hours + "h" : t.TotalHours >= 1 ? (int)t.TotalHours + "h " + t.Minutes + "m" : t.TotalMinutes >= 1 ? (int)t.TotalMinutes + "m " + t.Seconds + "s" : t.Seconds + "s";
        }
    }

    public partial class Core : IDisposable
    {
        public readonly string Dir; readonly Func<TorSettings> settings; TorSettings S { get { return settings(); } }
        readonly object lk = new object();
        public List<Entry> Entries = new List<Entry>();
        readonly Dictionary<string, TorrentManager> mgr = new Dictionary<string, TorrentManager>();
        readonly Dictionary<string, long[]> last = new Dictionary<string, long[]>();   // per torrent: data sent / received at the last tick
        ClientEngine engine; public IPAddress BoundIp; List<IPAddress> boundDns; string boundKey;
        readonly SemaphoreSlim op = new SemaphoreSlim(1, 1);
        public bool GameHold;        // the game rule says pause
        public string Hold { get { return engine == null ? "vpn" : GameHold ? "game" : null; } }   // null, "vpn", "game"
        public int LimitDown, LimitUp;   // engine-wide KB/s right now (settings + schedule + game limit; 0 = none)
        public string LastError; public int Blocked; public int BlocklistRanges; public string BlocklistError;
        // v1.5: the performance profile in use (Perf.cs); Gaming = a game runs and "While you play" limits the speed -> the light gaming numbers
        public bool Gaming;
        public static List<EncryptionType> BenchEncryption; public static int BenchCacheBytes = -1;
        public PerfValues PerfNow { get { return Perf.For(S, Gaming); } }
        // v1.5: the socket watchdog found a socket outside the VPN: the engine is stopped and stays stopped until you resume it
        public volatile string Tripped; public string TrippedAt;
        public readonly Stats Stats;
        public event Action<Entry> Completed;
        public event Action<string> Log;
        public Func<string, bool> Exists = File.Exists;
        public Core(string dir, Func<TorSettings> settings) { Dir = dir; this.settings = settings; Load(); Stats = Stats.Load(dir); }
        void L(string s) { var h = Log; if (h != null) try { h(s); } catch { } }
        public bool EngineUp { get { return engine != null; } }
        public List<IPAddress> BoundDns { get { return boundDns; } }
        public string TorrentsDir { get { return Path.Combine(Dir, "torrents"); } }

        // ---------------- the list on disk
        string ListFile { get { return Path.Combine(Dir, "torrents.json"); } }
        void Load()
        {
            try
            {
                if (File.Exists(ListFile)) using (var f = File.OpenRead(ListFile)) Entries = ((List<Entry>)new DataContractJsonSerializer(typeof(List<Entry>)).ReadObject(f)) ?? new List<Entry>();
            }
            catch { Entries = new List<Entry>(); }
            foreach (var e in Entries) { if (e.Flags == null) e.Flags = new List<string>(); if (e.Scan == "scanning") e.Scan = "waiting"; }
        }
        readonly object saveLock = new object();
        public void Save()
        {
            if (TorSettings.Frozen) return;
            if (Stats != null) Stats.Save();
            List<Entry> copy; lock (lk) copy = Entries.ToList();
            lock (saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir); var tmp = ListFile + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(List<Entry>)).WriteObject(f, copy);
                    for (int i = 0; ; i++) { try { if (File.Exists(ListFile)) File.Replace(tmp, ListFile, null); else File.Move(tmp, ListFile); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }
        public Entry Get(string id) { lock (lk) return Entries.FirstOrDefault(e => e.Id == id); }
        public List<Entry> All() { lock (lk) return Entries.ToList(); }   // v1.2: a copy for other threads (the Web API)

        // ---------------- adding (works while locked: it waits in the list)
        public static string IdOf(InfoHashes h) { return (h.V1 ?? h.V2).ToHex().ToLowerInvariant(); }
        public Entry AddTorrentFile(string path, string savePath, string category, List<int> prio, bool start, out string error) { return AddTorrentFile(path, savePath, category, prio, start, false, out error); }
        // a .torrent you already have the data for (the torrent creator): it is checked against the files and seeded, never "finished"
        public Entry AddCreated(string path, string savePath, string category, out string error) { return AddTorrentFile(path, savePath, category, null, true, true, out error); }
        // a .torrent's bytes (RSS downloads; later the Web API's uploads and search results): kept in the torrents folder, then added
        public Entry AddTorrentBytes(byte[] data, string savePath, string category, bool start, out string error)
        {
            error = null;
            if (data == null || data.Length == 0 || data.Length > 20 * 1024 * 1024 || data[0] != (byte)'d') { error = "That isn't a .torrent file."; return null; }
            var tmp = Path.Combine(TorrentsDir, "incoming-" + Guid.NewGuid().ToString("N") + ".torrent");
            try { Directory.CreateDirectory(TorrentsDir); File.WriteAllBytes(tmp, data); return AddTorrentFile(tmp, savePath, category, null, start, false, out error); }
            catch (Exception ex) { error = ex.Message; return null; }
            finally { try { File.Delete(tmp); } catch { } }
        }
        Entry AddTorrentFile(string path, string savePath, string category, List<int> prio, bool start, bool created, out string error)
        {
            error = null; Torrent t;
            try { if (new FileInfo(path).Length > 20 * 1024 * 1024) { error = "That file is too big to be a .torrent."; return null; } t = Torrent.Load(path); }
            catch (Exception ex) { error = "That isn't a valid .torrent file (" + ex.Message + ")."; return null; }
            var id = IdOf(t.InfoHashes);
            lock (lk) if (Entries.Any(x => x.Id == id)) { error = "That torrent is already in the list."; return null; }
            Directory.CreateDirectory(TorrentsDir); var copy = Path.Combine(TorrentsDir, id + ".torrent");
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase)) File.Copy(path, copy, true);
            var files = t.Files.Select(f => f.Path + "|" + f.Length).ToList();
            var e = New(id, t.Name, null, copy, savePath, category ?? Media.Detect(t.Name, t.Files.Select(f => new TFile(f.Path, f.Length)).ToList()), start);
            e.Files = files; e.Size = t.Size; e.Prio = prio != null && prio.Count == files.Count ? prio.ToList() : null;
            e.Flags = Media.Suspicious(t.Name, e.FileList).Select(x => x[0] + "|" + x[1]).ToList();
            if (created) { e.Created = true; e.CompletedAt = Family.Time(DateTime.Now); e.Scan = "own"; e.ScanText = "You made this torrent from your own files."; e.Flags = new List<string>(); }
            Insert(e); return e;
        }
        public Entry AddMagnet(string link, string savePath, string category, bool start, out string error)
        {
            error = null; MagnetLink m;
            if (string.IsNullOrWhiteSpace(link) || !link.Trim().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || !MagnetLink.TryParse(link.Trim(), out m)) { error = "That isn't a magnet link."; return null; }
            var id = IdOf(m.InfoHashes);
            lock (lk) if (Entries.Any(x => x.Id == id)) { error = "That torrent is already in the list."; return null; }
            var name = string.IsNullOrWhiteSpace(m.Name) ? "Magnet " + id.Substring(0, 8) : m.Name;
            var e = New(id, name, link.Trim(), null, savePath, category ?? Media.Detect(name, null), start);
            e.Size = m.Size ?? 0; Insert(e); return e;
        }
        Entry New(string id, string name, string magnet, string file, string save, string cat, bool start)
        {
            var c = S.Cat(cat);
            var folder = !string.IsNullOrWhiteSpace(save) ? save : c != null && !string.IsNullOrWhiteSpace(c.SaveFolder) ? c.SaveFolder : S.SaveFolder;
            int q; lock (lk) q = Entries.Count == 0 ? 0 : Entries.Max(x => x.Queue) + 1;
            return new Entry { Id = id, Name = name, Magnet = magnet, TorrentFile = file, SavePath = folder, Category = cat, Queue = q, UserPaused = !start, AddedAt = Family.Time(DateTime.Now), Flags = new List<string>(), Scan = "" };
        }
        void Insert(Entry e)
        {
            lock (lk) Entries.Add(e);
            Save(); L("added " + e.Id);
            if (engine != null) Task.Run(async () => { await op.WaitAsync().ConfigureAwait(false); try { if (engine != null) await Attach(e).ConfigureAwait(false); } catch (Exception ex) { L("attach " + ex.Message); } finally { op.Release(); } await Reconcile().ConfigureAwait(false); });
        }

        // ---------------- the VPN: the engine lives only while it's up, bound to its IP
        public async Task SetVpn(VpnState st)
        {
            var key = st != null && st.Ok ? st.Ip + "|" + string.Join(",", st.Dns) : null;
            if (key == null) { await Teardown("the VPN is down").ConfigureAwait(false); return; }
            if (Tripped != null) { await Teardown("stopped by the socket watchdog").ConfigureAwait(false); return; }
            if (engine != null && key == boundKey) return;
            await Teardown("the VPN changed").ConfigureAwait(false);
            await Build(st.Ip, st.Dns, key).ConfigureAwait(false);
        }
        // settings that need a new engine (port, DHT, LPD, encryption, blocklist)
        public async Task Restart()
        {
            if (engine == null) return;
            var ip = BoundIp; var dns = boundDns; var key = boundKey;
            await Teardown("settings changed").ConfigureAwait(false);
            if (ip != null) await Build(ip, dns, key).ConfigureAwait(false);
        }
        // stop every torrent (in parallel, a short wait for the trackers' "stopped") and throw the engine away - nothing stays open
        public async Task Teardown(string why)
        {
            var e = Interlocked.Exchange(ref engine, null);
            List<KeyValuePair<string, TorrentManager>> ms; lock (lk) { ms = mgr.ToList(); mgr.Clear(); last.Clear(); }
            BoundIp = null; boundKey = null;
            if (e == null) return;
            L("engine down: " + why);
            var stops = Task.WhenAll(ms.Select(m => SafeStop(m.Value)));
            try { await Task.WhenAny(stops, Task.Delay(700)).ConfigureAwait(false); } catch { }
            if (stops.IsCompleted) { try { e.Dispose(); } catch { } return; }
            // v1.5 fix: a stop that is still writing its disk cache isn't cut off any more (1.4 disposed the engine after 0.7 s, and data
            // that was verified but still in the cache could be lost). The network goes first: peer connections are already closed by
            // StopAsync; the listener and DHT are switched off now; then the stops get up to 30 s to flush before the engine is disposed.
            // A rebuild (the VPN back) and quitting wait for that.
            try { var b = new EngineSettingsBuilder(e.Settings) { DhtEndPoint = null }; b.ListenEndPoints = new Dictionary<string, IPEndPoint>(); await Task.WhenAny(e.UpdateSettingsAsync(b.ToSettings()), Task.Delay(200)).ConfigureAwait(false); } catch { }
            L("engine down: still writing cached data to disk");
            flushing = Task.Run(async () => { try { await Task.WhenAny(stops, Task.Delay(30000)).ConfigureAwait(false); } catch { } try { e.Dispose(); } catch { } L("engine disposed"); });
        }
        Task flushing = Task.FromResult(0);
        public bool Flushing { get { return !flushing.IsCompleted; } }
        // v1.5: the socket watchdog (SocketWatch) found a socket that isn't on the VPN address: stop everything now; nothing is rebuilt
        // until ClearTrip (you clicked Resume after checking).
        public async Task Trip(string reason)
        {
            if (Tripped == null) { Tripped = reason; TrippedAt = Family.Time(DateTime.Now); L("socket watchdog: " + reason); }
            await Teardown("socket watchdog: " + reason).ConfigureAwait(false);
        }
        public void ClearTrip() { if (Tripped != null) L("socket watchdog: cleared"); Tripped = null; TrippedAt = null; }
        static async Task SafeStop(TorrentManager m) { try { await m.StopAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false); } catch { } }

        public EngineSettings EngineSettingsFor(IPAddress ip)
        {
            var b = new EngineSettingsBuilder
            {
                AllowPortForwarding = false, AllowLocalPeerDiscovery = S.Lpd, AutoSaveLoadDhtCache = true, AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = Path.Combine(Dir, "cache"), DhtEndPoint = S.Dht ? new IPEndPoint(ip, S.Port) : null,
                MaximumDownloadRate = LimitDown * 1024, MaximumUploadRate = LimitUp * 1024
            };
            // v1.5: the performance profile (only numbers - every socket still comes from Net.Bound, bound to the VPN address)
            var p = PerfNow;
            b.MaximumConnections = p.Connections; b.MaximumHalfOpenConnections = p.HalfOpen; b.DiskCacheBytes = p.CacheMB * 1024 * 1024; b.MaximumOpenFiles = p.OpenFiles;
            b.ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(ip, S.Port) } };
            // v1.5: "prefer" now offers an encrypted handshake first (RC4Header - like MonoTorrent's and qBittorrent's defaults) instead of
            // full-stream RC4: the same peers connect, and the benchmark showed about a quarter less CPU per GB. Everything is inside the VPN tunnel
            // anyway. "require" is unchanged. ("v14" = 1.4's order, for the benchmark's baseline only.)
            b.AllowedEncryption = S.Encryption == "require" ? new List<EncryptionType> { EncryptionType.RC4Full, EncryptionType.RC4Header }
                : S.Perf == "v14" ? new List<EncryptionType> { EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText }
                : new List<EncryptionType> { EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText };
            if (BenchEncryption != null) b.AllowedEncryption = BenchEncryption;   // the benchmark's experiments only (null in the app)
            if (BenchCacheBytes >= 0) b.DiskCacheBytes = BenchCacheBytes;
            return b.ToSettings();
        }
        async Task Build(IPAddress ip, List<IPAddress> dns, string key)
        {
            try { await Task.WhenAny(flushing, Task.Delay(35000)).ConfigureAwait(false); } catch { }   // v1.5: the old engine's disk cache first
            await op.WaitAsync().ConfigureAwait(false);
            try
            {
                if (engine != null) return;
                var e = new ClientEngine(EngineSettingsFor(ip), Net.Bound(ip, dns));
                LoadBlocklist(e);
                e.CriticalException += (s, a) => { LastError = a.Exception.Message; L("critical: " + a.Exception.Message); };
                engine = e; BoundIp = ip; boundDns = dns; boundKey = key; appliedPerf = PerfKey(PerfNow); appliedEndgame = PerfNow.Endgame; NoteVpnIp(ip);
                L("engine up, bound to " + ip + ":" + S.Port);
                List<Entry> es; lock (lk) es = Entries.ToList();
                foreach (var x in es) { try { await Attach(x).ConfigureAwait(false); } catch (Exception ex) { L("attach " + x.Id + ": " + ex.Message); } }
            }
            finally { op.Release(); }
            await Reconcile().ConfigureAwait(false);
        }
        // the IP blocklist: your file and / or the one downloaded from a URL (over the VPN, Blocklist.Update). Swapped in live - the
        // engine's ban check always asks the current list.
        volatile BanList ban;
        public string BlocklistUrlFile { get { return Path.Combine(Dir, "blocklist-url.dat"); } }
        public string ReloadBlocklist()
        {
            var ranges = new List<AddressRange>(); string err = null;
            foreach (var f in new[] { S.BlocklistFile, string.IsNullOrWhiteSpace(S.BlocklistUrl) ? null : BlocklistUrlFile })
            {
                if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) continue;
                try { ranges.AddRange(Blocklist.Load(f)); } catch (Exception ex) { err = Path.GetFileName(f) + ": " + ex.Message; }
            }
            if (ranges.Count == 0) { ban = null; BlocklistRanges = 0; }
            else { var b = new BanList(); b.AddRange(ranges); ban = b; BlocklistRanges = ranges.Count; }
            BlocklistError = err; L("blocklist: " + BlocklistRanges + " ranges" + (err != null ? " (" + err + ")" : ""));
            return err;
        }
        public bool IsBlocked(IPAddress ip) { var b = ban; return b != null && ip != null && b.IsBanned(ip); }
        void LoadBlocklist(ClientEngine e)
        {
            ReloadBlocklist();
            e.ConnectionManager.BanPeer += (s, a) =>
            {
                try { IPAddress ip; if (IPAddress.TryParse(a.Peer.ConnectionUri.Host, out ip) && IsBlocked(ip)) { a.BanPeer = true; Interlocked.Increment(ref Blocked); } } catch { }
            };
        }
        TorrentSettings SettingsFor(Entry x)
        {
            return new TorrentSettingsBuilder
            {
                AllowDht = S.Dht, AllowPeerExchange = S.Pex, AllowInitialSeeding = x.SuperSeed, CreateContainingDirectory = true,
                MaximumDownloadRate = x.DownKBps * 1024, MaximumUploadRate = x.UpKBps * 1024,
                MaximumConnections = PerfNow.PerTorrent, UploadSlots = PerfNow.UploadSlots
            }.ToSettings();
        }
        // v1.5: the normal piece picker with the profile's end-game duplicates (MonoTorrent's default is 2 - left alone then)
        static MonoTorrent.PiecePicking.IPieceRequester Picker(int endgame)
        {
            return new MonoTorrent.PiecePicking.StandardPieceRequester(new MonoTorrent.PiecePicking.PieceRequesterSettings(true, true, true, Math.Max(1, endgame)));
        }
        string appliedPerf; int appliedEndgame = 2;
        static string PerfKey(PerfValues p) { return p.Connections + "|" + p.PerTorrent + "|" + p.HalfOpen + "|" + p.CacheMB + "|" + p.OpenFiles + "|" + p.UploadSlots + "|" + p.Endgame; }
        // VPN addresses the engine was bound to (the watchdog accepts sockets on them for a while after the VPN changed)
        public readonly Dictionary<IPAddress, DateTime> RecentVpnIps = new Dictionary<IPAddress, DateTime>();
        void NoteVpnIp(IPAddress ip) { if (ip == null) return; lock (RecentVpnIps) RecentVpnIps[ip] = DateTime.UtcNow; }
        public List<IPAddress> VpnIpsForWatch(TimeSpan keep)
        {
            var r = new List<IPAddress>(); var b = BoundIp; if (b != null) NoteVpnIp(b);
            lock (RecentVpnIps) { foreach (var kv in RecentVpnIps.ToList()) { if (DateTime.UtcNow - kv.Value <= keep) r.Add(kv.Key); else RecentVpnIps.Remove(kv.Key); } }
            return r;
        }
        async Task Attach(Entry x)
        {
            var e = engine; if (e == null) return;
            TorrentManager m;
            if (x.TorrentFile != null && Exists(x.TorrentFile)) m = await e.AddAsync(Torrent.Load(x.TorrentFile), x.SavePath, SettingsFor(x)).ConfigureAwait(false);
            else if (x.Magnet != null) m = await e.AddAsync(MagnetLink.Parse(x.Magnet), x.SavePath, SettingsFor(x)).ConfigureAwait(false);
            else { x.Scan = x.Scan ?? ""; L("no source for " + x.Id); return; }
            lock (lk) { mgr[x.Id] = m; last[x.Id] = new long[] { 0, 0 }; }
            if (m.HasMetadata) { await ApplyFiles(x, m).ConfigureAwait(false); await ApplyPaths(x, m).ConfigureAwait(false); }
            if (x.Trackers != null) await ApplyTrackers(x, m).ConfigureAwait(false);
            if (x.Sequential) { try { await m.ChangePickerAsync(new MonoTorrent.PiecePicking.StreamingPieceRequester()).ConfigureAwait(false); } catch (Exception ex) { L("sequential: " + ex.Message); } }
            else if (PerfNow.Endgame != 2) { try { await m.ChangePickerAsync(Picker(PerfNow.Endgame)).ConfigureAwait(false); } catch (Exception ex) { L("picker: " + ex.Message); } }
        }
        static Priority ToPrio(int p) { return p <= 0 ? Priority.DoNotDownload : p == 1 ? Priority.Low : p >= 3 ? Priority.High : Priority.Normal; }
        async Task ApplyFiles(Entry x, TorrentManager m)
        {
            for (int i = 0; i < m.Files.Count; i++)
            {
                var want = ToPrio(x.PrioOf(i));
                if (m.Files[i].Priority != want) await m.SetFilePriorityAsync(m.Files[i], want).ConfigureAwait(false);
            }
        }
        TorrentManager M(string id) { lock (lk) { TorrentManager m; return mgr.TryGetValue(id, out m) ? m : null; } }

        // ---------------- every second
        public async Task Tick()
        {
            var e = engine; long tickDown = 0, tickUp = 0; bool anySeeding = false;
            if (e != null)
            {
                List<KeyValuePair<string, TorrentManager>> ms; lock (lk) ms = mgr.ToList();
                foreach (var kv in ms)
                {
                    var x = Get(kv.Key); var m = kv.Value; if (x == null) continue;
                    long[] l; lock (lk) if (!last.TryGetValue(kv.Key, out l)) last[kv.Key] = l = new long[] { 0, 0 };
                    long sent = m.Monitor.DataBytesSent, recv = m.Monitor.DataBytesReceived;
                    if (sent >= l[0]) { x.Up += sent - l[0]; tickUp += sent - l[0]; } if (recv >= l[1]) { x.Down += recv - l[1]; tickDown += recv - l[1]; } l[0] = sent; l[1] = recv;
                    if (m.State == TorrentState.Seeding) { x.SeedSecs += 1; anySeeding = true; }
                    if (m.HasMetadata && (x.Files == null || x.TorrentFile == null)) GotMetadata(x, m);
                    try { RescueTick(x, m, DateTime.Now); } catch { }   // 1.10: magnets stuck on "Getting info"
                    if (m.HasMetadata && m.Complete && x.CompletedAt == null && m.State != TorrentState.Hashing && m.State != TorrentState.HashingPaused)
                    {
                        // v1.5: a full recheck of what's on disk before it counts as finished (MonoTorrent 3.0.2 can report a torrent with
                        // many small files complete while a block on disk was never written - found by the 1.5 benchmark). Bad pieces are
                        // downloaded again. "v14" (the benchmark's 1.4 baseline) skips it, like 1.4 did.
                        bool verified; lock (lk) verified = verifiedIds.Contains(kv.Key);
                        if (!verified && !x.Created && S.Perf != "v14") { StartVerify(x, m); continue; }
                        x.CompletedAt = Family.Time(DateTime.Now); L("complete " + x.Id); Save();
                        var h = Completed; if (h != null) try { h(x); } catch { }
                    }
                    if (m.State == TorrentState.Error && m.Error != null) LastError = m.Error.Exception != null ? m.Error.Exception.Message : m.Error.Reason.ToString();
                }
                await Reconcile().ConfigureAwait(false);
            }
            bool waiting; lock (lk) waiting = e == null && Entries.Any(x => !x.UserPaused);
            Stats.Tick(tickDown, tickUp, anySeeding, waiting, Blocked);
        }
        // ---- v1.5: verify on completion
        readonly HashSet<string> verifying = new HashSet<string>(), verifiedIds = new HashSet<string>();
        readonly Dictionary<string, int> repairs = new Dictionary<string, int>();
        public int Repaired;   // pieces found bad on disk and downloaded again (this session)
        public bool Verifying(string id) { lock (lk) return verifying.Contains(id); }
        void StartVerify(Entry x, TorrentManager m)
        {
            lock (lk) { if (!verifying.Add(x.Id)) return; }
            Task.Run(async () =>
            {
                try
                {
                    L("verifying " + x.Id);
                    await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await m.HashCheckAsync(false).ConfigureAwait(false);
                    int tries; lock (lk) { repairs.TryGetValue(x.Id, out tries); }
                    if (m.Complete || tries >= 3) { lock (lk) verifiedIds.Add(x.Id); if (!m.Complete) L("verify: still incomplete after 3 repairs " + x.Id); else L("verified " + x.Id); }
                    else
                    {
                        int bad = m.Bitfield.Length - m.Bitfield.TrueCount; Interlocked.Add(ref Repaired, bad);
                        lock (lk) repairs[x.Id] = tries + 1;
                        L("verify: " + bad + " piece(s) on disk were bad - downloading them again " + x.Id);
                    }
                }
                catch (Exception ex) { L("verify " + x.Id + ": " + ex.Message); }
                finally { lock (lk) verifying.Remove(x.Id); }
                Kick();
            });
        }
        void GotMetadata(Entry x, TorrentManager m)
        {
            try
            {
                var t = m.Torrent; if (t == null) return;
                x.Name = t.Name; x.Size = t.Size;
                x.Files = t.Files.Select(f => f.Path + "|" + f.Length).ToList();
                if (x.TorrentFile == null && !string.IsNullOrEmpty(m.MetadataPath) && File.Exists(m.MetadataPath))
                {
                    Directory.CreateDirectory(TorrentsDir); var copy = Path.Combine(TorrentsDir, x.Id + ".torrent"); File.Copy(m.MetadataPath, copy, true); x.TorrentFile = copy;
                }
                x.Flags = Media.Suspicious(t.Name, x.FileList).Select(f => f[0] + "|" + f[1]).ToList();
                if (x.Magnet != null && (x.Category == null || x.Category == "Other")) x.Category = Media.Detect(t.Name, x.FileList);
                Save(); L("metadata " + x.Id);
                Task.Run(() => ApplyFiles(x, m));
            }
            catch (Exception ex) { L("metadata: " + ex.Message); }
        }
        public double RatioOf(Entry x) { var size = x.Size > 0 ? x.Size : 1; return x.Up / (double)size; }
        double GoalOf(Entry x) { var c = S.Cat(x.Category); return c != null && c.RatioGoal > 0 ? c.RatioGoal : S.RatioLimit; }
        public HashSet<string> Wanted()
        {
            List<Plan.Item> items; lock (lk) items = Entries.Select(x => new Plan.Item { Id = x.Id, Queue = x.Queue, UserPaused = x.UserPaused, Complete = x.CompletedAt != null, LimitHit = x.LimitHit, NoLimit = x.NoLimit, Ratio = RatioOf(x), SeedMinutes = x.SeedSecs / 60, RatioGoal = GoalOf(x), RatioCap = x.ShareRatio, SeedCap = x.ShareMinutes }).ToList();
            foreach (var i in items) if (i.Complete && !i.UserPaused && !i.LimitHit && Plan.LimitReached(i, S.SeedMinutes)) { var x = Get(i.Id); if (x != null && !x.NoLimit) { x.LimitHit = true; L("limit reached " + x.Id); } }
            return Plan.Running(items, S.MaxActive, Hold, S.SeedMinutes);
        }
        int appliedDown = -1, appliedUp = -1;
        async Task Reconcile()
        {
            var e = engine; if (e == null) return;
            if (!await op.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (appliedDown != LimitDown || appliedUp != LimitUp)
                {
                    var b = new EngineSettingsBuilder(e.Settings) { MaximumDownloadRate = LimitDown * 1024, MaximumUploadRate = LimitUp * 1024 };
                    await e.UpdateSettingsAsync(b.ToSettings()).ConfigureAwait(false); appliedDown = LimitDown; appliedUp = LimitUp;
                }
                // v1.5: the profile changed (Settings, or a game started / stopped with "Limit the speed"): applied live, same sockets
                var pv = PerfNow; var pk = PerfKey(pv);
                if (pk != appliedPerf && BoundIp != null)
                {
                    await e.UpdateSettingsAsync(EngineSettingsFor(BoundIp)).ConfigureAwait(false);
                    List<KeyValuePair<string, TorrentManager>> all; lock (lk) all = mgr.ToList();
                    foreach (var kv in all)
                    {
                        var x = Get(kv.Key); if (x == null) continue;
                        try { await kv.Value.UpdateSettingsAsync(SettingsFor(x)).ConfigureAwait(false); if (!x.Sequential && pv.Endgame != appliedEndgame) await kv.Value.ChangePickerAsync(Picker(pv.Endgame)).ConfigureAwait(false); } catch (Exception ex) { L("perf " + ex.Message); }
                    }
                    appliedPerf = pk; appliedEndgame = pv.Endgame; L("performance: " + Perf.Label(pv.Name) + " - " + pv.Summary);
                }
                var want = Wanted();
                List<KeyValuePair<string, TorrentManager>> ms; lock (lk) ms = mgr.ToList();
                foreach (var kv in ms)
                {
                    if (Verifying(kv.Key)) continue;   // v1.5: its recheck runs (it's started again afterwards)
                    var m = kv.Value; bool run = want.Contains(kv.Key);
                    bool active = m.State != TorrentState.Stopped && m.State != TorrentState.Stopping && m.State != TorrentState.Error && m.State != TorrentState.Paused;
                    if (run && !active && m.State != TorrentState.Stopping) await m.StartAsync().ConfigureAwait(false);
                    else if (!run && active) await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { L("reconcile: " + ex.Message); }
            finally { op.Release(); }
        }

        // ---------------- your actions
        public void Pause(string id) { var x = Get(id); if (x == null) return; x.UserPaused = true; Save(); Kick(); }
        public void Resume(string id) { var x = Get(id); if (x == null) return; x.UserPaused = false; if (x.LimitHit) { x.LimitHit = false; x.NoLimit = true; } Save(); Kick(); }
        public void PauseAll() { lock (lk) foreach (var x in Entries) x.UserPaused = true; Save(); Kick(); }
        public void ResumeAll() { lock (lk) foreach (var x in Entries) { x.UserPaused = false; if (x.LimitHit) { x.LimitHit = false; x.NoLimit = true; } } Save(); Kick(); }
        public void Move(string id, int delta)
        {
            lock (lk)
            {
                var order = Entries.OrderBy(x => x.Queue).ToList(); int i = order.FindIndex(x => x.Id == id); if (i < 0) return;
                int j = Math.Max(0, Math.Min(order.Count - 1, delta == int.MinValue ? 0 : delta == int.MaxValue ? order.Count - 1 : i + delta));
                var it = order[i]; order.RemoveAt(i); order.Insert(j, it);
                for (int k = 0; k < order.Count; k++) order[k].Queue = k;
            }
            Save(); Kick();
        }
        public void Kick() { Task.Run(() => Reconcile()); }
        public async Task<string> Remove(string id, bool deleteFiles)
        {
            var x = Get(id); if (x == null) return "It's not in the list.";
            var m = M(id);
            if (m != null)
            {
                try { await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); var e = engine; if (e != null) await e.RemoveAsync(m, RemoveMode.CacheDataOnly).ConfigureAwait(false); } catch (Exception ex) { L("remove " + ex.Message); }
                lock (lk) { mgr.Remove(id); last.Remove(id); }
            }
            lock (lk) Entries.Remove(x);
            Save();
            if (deleteFiles) return Recycle(x);
            return null;
        }
        // "also delete the files": to the Recycle Bin, never a permanent delete
        public static string Recycle(Entry x)
        {
            try
            {
                var files = x.FileList; if (files.Count == 0) return null;
                // each top-level folder / file in the save folder that the torrent's files are in (renamed ones included). Files moved
                // outside it (into the library) are left alone.
                var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Count; i++) { var rel = RelOf(x, i); if (Path.IsPathRooted(rel)) continue; var first = rel.Split('\\', '/')[0]; if (first.Length > 0 && first != "..") tops.Add(first); }
                foreach (var t in tops)
                {
                    var root = Path.Combine(x.SavePath, t);
                    if (Directory.Exists(root)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(root, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else if (File.Exists(root)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(root, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                return null;
            }
            catch (Exception e) { return "The files couldn't be moved to the Recycle Bin: " + e.Message; }
        }
        public async Task SetFilePrio(string id, int index, int prio)
        {
            var x = Get(id); if (x == null) return;
            var n = x.Files == null ? 0 : x.Files.Count; if (index < 0 || index >= n) return;
            if (x.Prio == null || x.Prio.Count != n) x.Prio = Enumerable.Repeat(2, n).ToList();
            x.Prio[index] = Math.Max(0, Math.Min(3, prio)); Save();
            var m = M(id); if (m != null && m.HasMetadata) try { await ApplyFiles(x, m).ConfigureAwait(false); } catch (Exception ex) { L("prio " + ex.Message); }
        }
        public async Task SetLimits(string id, int downKBps, int upKBps)
        {
            var x = Get(id); if (x == null) return; x.DownKBps = Math.Max(0, downKBps); x.UpKBps = Math.Max(0, upKBps); Save();
            var m = M(id); if (m != null) try { await m.UpdateSettingsAsync(SettingsFor(x)).ConfigureAwait(false); } catch (Exception ex) { L("limits " + ex.Message); }
        }
        public async Task SetFlags(string id, bool sequential, bool superSeed)
        {
            var x = Get(id); if (x == null) return; bool seqChanged = x.Sequential != sequential; x.Sequential = sequential; x.SuperSeed = superSeed; Save();
            var m = M(id); if (m == null) return;
            try
            {
                await m.UpdateSettingsAsync(SettingsFor(x)).ConfigureAwait(false);
                if (seqChanged) await m.ChangePickerAsync(sequential ? (MonoTorrent.PiecePicking.IPieceRequester)new MonoTorrent.PiecePicking.StreamingPieceRequester() : Picker(PerfNow.Endgame)).ConfigureAwait(false);
            }
            catch (Exception ex) { L("flags " + ex.Message); }
        }
        public void SetCategory(string id, string cat) { var x = Get(id); if (x == null) return; x.Category = cat; Save(); }
        // v1.2 (the Web API's setShareLimits): ratio / minutes, -2 = back to the category / global limit, -1 = no limit. Checked again
        // straight away - a torrent that was stopped at its old limit seeds on if the new one isn't reached.
        public void SetShareLimits(string id, double ratio, int minutes)
        {
            var x = Get(id); if (x == null) return;
            x.ShareRatio = ratio <= -2 ? (double?)null : ratio < 0 ? -1 : ratio;
            x.ShareMinutes = minutes <= -2 ? (int?)null : minutes < 0 ? -1 : minutes;
            x.LimitHit = false; x.NoLimit = false; Save(); Kick();
        }
        public double GoalFor(Entry x) { return GoalOf(x); }
        public async Task<string> Recheck(string id)
        {
            var m = M(id); if (m == null) return "Force recheck needs the VPN connected (the engine isn't running).";
            try
            {
                var was = m.State; await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await m.HashCheckAsync(false).ConfigureAwait(false);
                var x = Get(id); if (x != null && !m.Complete) x.CompletedAt = null;
                Kick(); return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
        // the library hand-off when it moves: tell the engine the files' new places (it keeps seeding from there)
        public async Task<string> MoveFiles(string id, List<string[]> moves)
        {
            var m = M(id);
            if (m == null) return "not running";
            try
            {
                await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                foreach (var mv in moves)
                {
                    var f = m.Files.FirstOrDefault(x => string.Equals(x.Path, mv[0], StringComparison.OrdinalIgnoreCase)); if (f == null) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(mv[1])); await m.MoveFileAsync(f, mv[1]).ConfigureAwait(false);
                    var e = Get(id); if (e != null) SetPath(e, m.Files.IndexOf(f), mv[1]);   // v1.1: remembered, so it's found there after a restart
                }
                Save(); Kick(); return null;
            }
            catch (Exception ex) { Kick(); return ex.Message; }
        }
        public string FullPathOf(string id, string relPath)
        {
            var m = M(id);
            if (m != null && m.HasMetadata) { var f = m.Files.FirstOrDefault(q => string.Equals(q.Path, relPath, StringComparison.OrdinalIgnoreCase)); if (f != null) return f.FullPath; }
            var x = Get(id); if (x == null) return null;
            var files = x.FileList; var fi = files.FindIndex(q => string.Equals(q.Path, relPath, StringComparison.OrdinalIgnoreCase));
            if (fi >= 0) return FileOn(x, fi);
            bool multi = files.Count > 1 || relPath.Contains("\\") || relPath.Contains("/");
            return multi ? Path.Combine(x.SavePath, x.Name, relPath) : Path.Combine(x.SavePath, relPath);
        }
        public string RootOf(string id)
        {
            var x = Get(id); if (x == null) return null; var files = x.FileList;
            if (files.Count == 1 && !files[0].Path.Contains("\\") && !files[0].Path.Contains("/")) return FullPathOf(id, files[0].Path);
            if (files.Count > 0) { var rel = RelOf(x, 0); if (!Path.IsPathRooted(rel)) return Path.Combine(x.SavePath, rel.Split('\\', '/')[0]); }
            var m = M(id); if (m != null && m.HasMetadata && m.Files.Count > 0) { var d = m.ContainingDirectory; if (!string.IsNullOrEmpty(d)) return d; }
            return Path.Combine(x.SavePath, x.Name);
        }

        // ---------------- v1.1: where each file is (renames, move storage, the library) and the trackers you edit
        public static bool Multi(Entry x) { var f = x.FileList; return f.Count > 1 || (f.Count == 1 && (f[0].Path.Contains("\\") || f[0].Path.Contains("/"))); }
        // the torrent's own place, relative to the save folder
        public static string DefaultRel(Entry x, int i) { var f = x.FileList[i].Path.Replace('/', '\\'); return Multi(x) ? Path.Combine(x.Name, f) : f; }
        public static string RelOf(Entry x, int i) { return x.Paths != null && i < x.Paths.Count && !string.IsNullOrEmpty(x.Paths[i]) ? x.Paths[i] : DefaultRel(x, i); }
        public static string FileOn(Entry x, int i) { return Path.Combine(x.SavePath, RelOf(x, i)); }
        static void SetPath(Entry x, int i, string rel)
        {
            var n = x.Files == null ? 0 : x.Files.Count; if (i < 0 || i >= n) return;
            if (x.Paths == null || x.Paths.Count != n) { var old = x.Paths; x.Paths = Enumerable.Range(0, n).Select(k => old != null && k < old.Count ? old[k] : null).ToList(); }
            x.Paths[i] = string.Equals(rel, DefaultRel(x, i), StringComparison.OrdinalIgnoreCase) ? null : rel;
            if (x.Paths.All(string.IsNullOrEmpty)) x.Paths = null;
        }
        // a name for a file or folder inside the save folder: no ".." and nothing Windows can't use (null = not allowed)
        public static string CheckRel(string rel)
        {
            rel = (rel ?? "").Trim().Replace('/', '\\').Trim('\\');
            if (rel.Length == 0) return null;
            var parts = rel.Split('\\');
            foreach (var p in parts) { if (p.Trim().Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith(".") || p.EndsWith(" ")) return null; }
            return string.Join("\\", parts);
        }
        async Task ApplyPaths(Entry x, TorrentManager m)
        {
            if (x.Paths == null) return;
            for (int i = 0; i < m.Files.Count && i < x.Paths.Count; i++)
            {
                if (string.IsNullOrEmpty(x.Paths[i])) continue;
                var target = FileOn(x, i); if (string.Equals(m.Files[i].FullPath, target, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.CreateDirectory(Path.GetDirectoryName(target)); await m.MoveFileAsync(m.Files[i], target).ConfigureAwait(false); } catch (Exception ex) { L("path " + ex.Message); }
            }
        }
        // rename one file (newRel: its new path inside the save folder; folders allowed)
        public Task<string> RenameFile(string id, int index, string newRel)
        {
            var x = Get(id); if (x == null) return Task.FromResult("It's not in the list.");
            if (x.Files == null || index < 0 || index >= x.Files.Count) return Task.FromResult("Its file list isn't known yet.");
            var rel = CheckRel(newRel); if (rel == null) return Task.FromResult("That name can't be used.");
            return Relocate(x, new Dictionary<int, string> { { index, rel } }, null);
        }
        // rename a folder: every file under oldFolder (a folder inside the save folder, e.g. "Pack" or "Pack\Extras") moves with it
        public Task<string> RenameFolder(string id, string oldFolder, string newName)
        {
            var x = Get(id); if (x == null) return Task.FromResult("It's not in the list.");
            var from = CheckRel(oldFolder); var name = CheckRel(newName);
            if (from == null || name == null || name.Contains("\\")) return Task.FromResult("That name can't be used.");
            var parent = Path.GetDirectoryName(from); var to = string.IsNullOrEmpty(parent) ? name : Path.Combine(parent, name);
            var map = new Dictionary<int, string>();
            for (int i = 0; i < x.FileList.Count; i++) { var r = RelOf(x, i); if (r.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) map[i] = to + r.Substring(from.Length); }
            if (map.Count == 0) return Task.FromResult("No files are in that folder.");
            return Relocate(x, map, null);
        }
        public static List<string> Folders(Entry x)
        {
            var r = new List<string>();
            for (int i = 0; i < x.FileList.Count; i++) { var rel = RelOf(x, i); if (Path.IsPathRooted(rel)) continue; var d = Path.GetDirectoryName(rel); while (!string.IsNullOrEmpty(d)) { if (!r.Contains(d, StringComparer.OrdinalIgnoreCase)) r.Add(d); d = Path.GetDirectoryName(d); } }
            return r.OrderBy(q => q, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // move storage: everything in the save folder goes to another folder (renamed files keep their names; files in the library stay)
        public async Task<string> MoveStorage(string id, string newSave)
        {
            var x = Get(id); if (x == null) return "It's not in the list.";
            if (string.IsNullOrWhiteSpace(newSave)) return "Pick a folder.";
            try { newSave = Path.GetFullPath(newSave.Trim()).TrimEnd('\\'); } catch { return "That folder can't be used."; }
            if (string.Equals(newSave, (x.SavePath ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "It's already there.";
            if (x.Files == null || x.Files.Count == 0)
            {
                // a magnet without its file list yet: nothing on disk, it just downloads to the new folder
                await op.WaitAsync().ConfigureAwait(false);
                try { var m0 = M(id); x.SavePath = newSave; Save(); if (m0 != null) { await SafeStop(m0).ConfigureAwait(false); var e0 = engine; if (e0 != null) { await e0.RemoveAsync(m0, RemoveMode.CacheDataOnly).ConfigureAwait(false); lock (lk) { mgr.Remove(id); last.Remove(id); } await Attach(x).ConfigureAwait(false); } } }
                catch (Exception ex) { return ex.Message; }
                finally { op.Release(); }
                Kick(); return null;
            }
            var map = new Dictionary<int, string>();
            for (int i = 0; i < x.FileList.Count; i++) { var rel = RelOf(x, i); if (!Path.IsPathRooted(rel)) map[i] = Path.Combine(newSave, rel); }
            return await Relocate(x, map, newSave).ConfigureAwait(false);
        }
        // moves files (targets relative to the save folder, or absolute) - through the engine when it has the torrent (stopped first),
        // straight on disk while the VPN is off - then remembers where they are. Never overwrites a file that's already there.
        async Task<string> Relocate(Entry x, Dictionary<int, string> targets, string newSave)
        {
            var save = newSave ?? x.SavePath;
            var full = targets.ToDictionary(t => t.Key, t => Path.IsPathRooted(t.Value) ? t.Value : Path.Combine(save, t.Value));
            var ends = new List<string>();
            for (int i = 0; i < x.FileList.Count; i++)
            {
                var now = FileOn(x, i); var to = full.ContainsKey(i) ? full[i] : now;
                if (ends.Contains(to, StringComparer.OrdinalIgnoreCase)) return "Two files would end up with the same name.";
                ends.Add(to);
                if (full.ContainsKey(i) && !string.Equals(now, to, StringComparison.OrdinalIgnoreCase) && File.Exists(to)) return "There's already a file called " + Path.GetFileName(to) + " there.";
            }
            var oldDirs = full.Keys.Select(i => Path.GetDirectoryName(FileOn(x, i))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var oldSave = x.SavePath;
            var m = M(x.Id); string err = null;
            try
            {
                if (m != null && m.HasMetadata)
                {
                    await m.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    foreach (var kv in full) { if (string.Equals(m.Files[kv.Key].FullPath, kv.Value, StringComparison.OrdinalIgnoreCase)) continue; Directory.CreateDirectory(Path.GetDirectoryName(kv.Value)); await m.MoveFileAsync(m.Files[kv.Key], kv.Value).ConfigureAwait(false); }
                }
                else
                    await Task.Run(() => { foreach (var kv in full) { var from = FileOn(x, kv.Key); if (string.Equals(from, kv.Value, StringComparison.OrdinalIgnoreCase) || !File.Exists(from)) continue; Directory.CreateDirectory(Path.GetDirectoryName(kv.Value)); File.Move(from, kv.Value); } }).ConfigureAwait(false);
            }
            catch (Exception ex) { err = "Couldn't move it: " + ex.Message; }
            // remember where each file is now (also after a failure part-way)
            var where = new Dictionary<int, string>();
            foreach (var kv in full) where[kv.Key] = m != null && m.HasMetadata ? m.Files[kv.Key].FullPath : (File.Exists(kv.Value) || !File.Exists(FileOn(x, kv.Key)) ? kv.Value : FileOn(x, kv.Key));
            if (newSave != null && err == null) x.SavePath = newSave;
            var root = x.SavePath.TrimEnd('\\') + "\\";
            foreach (var kv in where) SetPath(x, kv.Key, kv.Value.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? kv.Value.Substring(root.Length) : kv.Value);
            Save(); L("files moved " + x.Id);
            foreach (var d in oldDirs) CleanEmpty(d, oldSave);
            Kick(); return err;
        }
        // removes folders a move left empty (only empty ones, never the save folder itself)
        static void CleanEmpty(string dir, string stopAt)
        {
            try
            {
                for (int k = 0; k < 8 && !string.IsNullOrEmpty(dir) && Directory.Exists(dir); k++)
                {
                    if (stopAt != null && string.Equals(dir.TrimEnd('\\'), stopAt.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
                    Directory.Delete(dir, false); dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
        }

        // trackers: the torrent's own list until you edit it; then yours, re-applied whenever the engine is built. Only http(s) and udp
        // trackers - both go out through the VPN-bound clients (Net.BoundTracker).
        public List<string> TrackersOf(string id)
        {
            var x = Get(id); if (x == null) return new List<string>();
            if (x.Trackers != null) return x.Trackers.ToList();
            var m = M(id);
            if (m != null) { try { return m.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.ToString()).Distinct().ToList(); } catch { } }
            try
            {
                if (x.TorrentFile != null && File.Exists(x.TorrentFile)) return Torrent.Load(x.TorrentFile).AnnounceUrls.SelectMany(t => t).Distinct().ToList();
                MagnetLink ml; if (x.Magnet != null && MagnetLink.TryParse(x.Magnet, out ml)) return ml.AnnounceUrls.ToList();
            }
            catch { }
            return new List<string>();
        }
        public static string CheckTracker(string url)
        {
            Uri u; url = (url ?? "").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "udp") || string.IsNullOrEmpty(u.Host) || (u.Scheme == "udp" && u.Port <= 0)) return null;
            return u.ToString();
        }
        public async Task<string> SetTrackers(string id, IEnumerable<string> urls)
        {
            var x = Get(id); if (x == null) return "It's not in the list.";
            var list = new List<string>();
            foreach (var raw in urls ?? new string[0]) { if (string.IsNullOrWhiteSpace(raw)) continue; var u = CheckTracker(raw); if (u == null) return "Not a tracker address (http://, https:// or udp://): " + raw.Trim(); if (!list.Contains(u)) list.Add(u); }
            x.Trackers = list; Save(); L("trackers " + x.Id + ": " + list.Count);
            var m = M(id); if (m != null) { try { await ApplyTrackers(x, m).ConfigureAwait(false); } catch (Exception ex) { return ex.Message; } }
            return null;
        }
        public Task<string> AddTracker(string id, string url) { var l = TrackersOf(id); l.Add(url); return SetTrackers(id, l); }
        public Task<string> RemoveTracker(string id, string url) { var l = TrackersOf(id); var u = CheckTracker(url) ?? url; l.RemoveAll(t => t == u || t == url); return SetTrackers(id, l); }
        public Task<string> EditTracker(string id, string oldUrl, string newUrl) { var l = TrackersOf(id); var u = CheckTracker(oldUrl) ?? oldUrl; var i = l.FindIndex(t => t == u || t == oldUrl); if (i < 0) l.Add(newUrl); else l[i] = newUrl; return SetTrackers(id, l); }
        async Task ApplyTrackers(Entry x, TorrentManager m)
        {
            var want = x.Trackers ?? new List<string>(); var tm = m.TrackerManager;
            var have = tm.Tiers.SelectMany(t => t.Trackers).ToList();
            foreach (var t in have) if (!want.Contains(t.Uri.ToString())) await tm.RemoveTrackerAsync(t);
            var ip = BoundIp; if (ip == null) return;
            foreach (var u in want) if (!have.Any(t => t.Uri.ToString() == u)) { var tr = Net.BoundTracker(new Uri(u), ip, boundDns); if (tr != null) await tm.AddTrackerAsync(tr); }
        }
        List<TrackerView> TrackerViews(Entry x, TorrentManager m)
        {
            var r = new List<TrackerView>();
            if (m != null)
            {
                try
                {
                    foreach (var t in m.TrackerManager.Tiers.SelectMany(q => q.Trackers))
                        r.Add(new TrackerView { Url = t.Uri.ToString(), Status = t.Status == MonoTorrent.Trackers.TrackerState.Ok ? "Working" : t.Status == MonoTorrent.Trackers.TrackerState.Connecting ? "Connecting" : t.Status == MonoTorrent.Trackers.TrackerState.Unknown ? "Not contacted yet" : "Not working", Message = !string.IsNullOrEmpty(t.FailureMessage) ? t.FailureMessage : t.WarningMessage });
                    return r;
                }
                catch { r.Clear(); }
            }
            foreach (var u in TrackersOf(x.Id)) r.Add(new TrackerView { Url = u, Status = engine == null ? "Waiting for VPN" : "Not contacted yet" });
            return r;
        }

        // ---------------- what the window shows
        public List<TorrentView> Snapshot(bool files)
        {
            var r = new List<TorrentView>(); var want = engine != null ? Wanted() : new HashSet<string>();
            List<Entry> es; lock (lk) es = Entries.OrderBy(x => x.Queue).ToList();
            foreach (var x in es)
            {
                var m = M(x.Id);
                var v = new TorrentView { Id = x.Id, Hash = x.Id, Name = x.Name, Category = x.Category, Scan = x.Scan, ScanText = x.ScanText, Handoff = x.Handoff, SavePath = x.SavePath, Size = x.Size, Up = x.Up, Down = x.Down, Queue = x.Queue + 1,
                    Paused = x.UserPaused, Complete = x.CompletedAt != null, Flagged = x.Flags != null && x.Flags.Count > 0, Flags = (x.Flags ?? new List<string>()).ToList(), Sequential = x.Sequential, SuperSeed = x.SuperSeed,
                    DownKBps = x.DownKBps, UpKBps = x.UpKBps, AddedAt = x.AddedAt, CompletedAt = x.CompletedAt, SeedSecs = x.SeedSecs, Ratio = RatioOf(x) };
                if (m != null)
                {
                    v.Progress = m.Progress; v.DownRate = m.Monitor.DownloadRate; v.UpRate = m.Monitor.UploadRate; v.Seeds = m.Peers.Seeds; v.Leechs = m.Peers.Leechs;
                    if (m.HasMetadata) { long wanted = 0, done = 0; for (int i = 0; i < m.Files.Count; i++) { var f = m.Files[i]; if (f.Priority == Priority.DoNotDownload) continue; wanted += f.Length; done += (long)(f.Length * f.BitField.PercentComplete / 100.0); } v.Done = done; var left = wanted - done; v.Eta = left > 0 && v.DownRate > 0 ? (TimeSpan?)TimeSpan.FromSeconds(left / (double)v.DownRate) : null; }
                    if (m.Error != null) v.Error = m.Error.Exception != null ? m.Error.Exception.Message : m.Error.Reason.ToString();
                }
                else v.Progress = x.CompletedAt != null ? 100 : 0;
                v.State = StateText(x, m, want);
                if (files)
                {
                    var fl = x.FileList;
                    for (int i = 0; i < fl.Count; i++)
                    {
                        double p = 0; if (m != null && m.HasMetadata && i < m.Files.Count) p = m.Files[i].BitField.PercentComplete; else if (x.CompletedAt != null) p = 100;
                        v.Files.Add(new FileView { Index = i, Path = fl[i].Path, Rel = RelOf(x, i), Length = fl[i].Length, Prio = x.PrioOf(i), Progress = p });
                    }
                    v.Trackers = TrackerViews(x, m);
                }
                r.Add(v);
            }
            return r;
        }
        string StateText(Entry x, TorrentManager m, HashSet<string> want)
        {
            if (Hold == "vpn") return x.UserPaused ? "Paused" : "Waiting for VPN";
            if (x.UserPaused) return "Paused";
            if (x.LimitHit) return "Done (limit reached)";
            if (Hold == "game") return "Paused for game";
            if (m == null) return "Starting";
            if (!want.Contains(x.Id)) return x.CompletedAt != null ? "Done" : "Queued";
            switch (m.State)
            {
                case TorrentState.Downloading: return "Downloading";
                case TorrentState.Seeding: return "Seeding";
                case TorrentState.Metadata: return "Getting info";
                case TorrentState.Hashing: case TorrentState.HashingPaused: return "Checking";
                case TorrentState.Starting: return "Starting";
                case TorrentState.Error: return "Error";
                case TorrentState.FetchingHashes: return "Getting info";
                default: return "Starting";
            }
        }
        public long TotalDown { get { var e = engine; return e == null ? 0 : e.TotalDownloadRate; } }
        public long TotalUp { get { var e = engine; return e == null ? 0 : e.TotalUploadRate; } }
        public TorrentManager ManagerForTests(string id) { return M(id); }
        public void Dispose() { try { Teardown("quit").Wait(3000); } catch { } try { flushing.Wait(30000); } catch { } Save(); }   // quitting waits for the disk cache
    }
}
