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
        public List<TFile> FileList { get { return (Files ?? new List<string>()).Select(f => { var i = f.LastIndexOf('|'); long l; return new TFile(f.Substring(0, i), long.TryParse(f.Substring(i + 1), out l) ? l : 0); }).ToList(); } }
        public int PrioOf(int i) { return Prio != null && i < Prio.Count ? Prio[i] : 2; }
    }

    // what the window and status show for one torrent
    public class TorrentView
    {
        public string Id, Name, State, Category, Scan, ScanText, Handoff, SavePath, Error, Hash;
        public long Size, Done, DownRate, UpRate, Up, Down; public double Progress, Ratio; public TimeSpan? Eta; public int Seeds, Leechs, Queue;
        public bool Paused, Complete, Flagged, Sequential, SuperSeed; public int DownKBps, UpKBps; public string AddedAt, CompletedAt; public double SeedSecs;
        public List<string> Flags = new List<string>(); public List<FileView> Files = new List<FileView>();
    }
    public class FileView { public int Index; public string Path; public long Length; public double Progress; public int Prio; }

    // the queue and limits as a pure function (tested): which torrents should be running right now
    public static class Plan
    {
        public class Item { public string Id; public int Queue; public bool UserPaused, Complete, LimitHit, NoLimit; public double Ratio, SeedMinutes, RatioGoal; }
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
            if (i.RatioGoal > 0 && i.Ratio >= i.RatioGoal) return true;
            if (seedMinutesLimit > 0 && i.SeedMinutes >= seedMinutesLimit) return true;
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

    public class Core : IDisposable
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
        public string LastError; public int Blocked; public int BlocklistRanges;
        public event Action<Entry> Completed;
        public event Action<string> Log;
        public Func<string, bool> Exists = File.Exists;
        public Core(string dir, Func<TorSettings> settings) { Dir = dir; this.settings = settings; Load(); }
        void L(string s) { var h = Log; if (h != null) try { h(s); } catch { } }
        public bool EngineUp { get { return engine != null; } }
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

        // ---------------- adding (works while locked: it waits in the list)
        public static string IdOf(InfoHashes h) { return (h.V1 ?? h.V2).ToHex().ToLowerInvariant(); }
        public Entry AddTorrentFile(string path, string savePath, string category, List<int> prio, bool start, out string error)
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
            try { await Task.WhenAny(Task.WhenAll(ms.Select(m => SafeStop(m.Value))), Task.Delay(700)).ConfigureAwait(false); } catch { }
            try { e.Dispose(); } catch { }
        }
        static async Task SafeStop(TorrentManager m) { try { await m.StopAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false); } catch { } }

        public EngineSettings EngineSettingsFor(IPAddress ip)
        {
            var b = new EngineSettingsBuilder
            {
                AllowPortForwarding = false, AllowLocalPeerDiscovery = S.Lpd, AutoSaveLoadDhtCache = true, AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = Path.Combine(Dir, "cache"), DhtEndPoint = S.Dht ? new IPEndPoint(ip, S.Port) : null,
                MaximumDownloadRate = LimitDown * 1024, MaximumUploadRate = LimitUp * 1024
            };
            b.ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(ip, S.Port) } };
            b.AllowedEncryption = S.Encryption == "require" ? new List<EncryptionType> { EncryptionType.RC4Full, EncryptionType.RC4Header } : new List<EncryptionType> { EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText };
            return b.ToSettings();
        }
        async Task Build(IPAddress ip, List<IPAddress> dns, string key)
        {
            await op.WaitAsync().ConfigureAwait(false);
            try
            {
                if (engine != null) return;
                var e = new ClientEngine(EngineSettingsFor(ip), Net.Bound(ip, dns));
                LoadBlocklist(e);
                e.CriticalException += (s, a) => { LastError = a.Exception.Message; L("critical: " + a.Exception.Message); };
                engine = e; BoundIp = ip; boundDns = dns; boundKey = key;
                L("engine up, bound to " + ip + ":" + S.Port);
                List<Entry> es; lock (lk) es = Entries.ToList();
                foreach (var x in es) { try { await Attach(x).ConfigureAwait(false); } catch (Exception ex) { L("attach " + x.Id + ": " + ex.Message); } }
            }
            finally { op.Release(); }
            await Reconcile().ConfigureAwait(false);
        }
        void LoadBlocklist(ClientEngine e)
        {
            BlocklistRanges = 0;
            var f = S.BlocklistFile; if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) return;
            try
            {
                var ban = new BanList();
                using (var s = File.OpenRead(f)) { var ranges = new BanListParser().Parse(s).ToList(); ban.AddRange(ranges); BlocklistRanges = ranges.Count; }
                e.ConnectionManager.BanPeer += (s, a) =>
                {
                    try { IPAddress ip; if (IPAddress.TryParse(a.Peer.ConnectionUri.Host, out ip) && ban.IsBanned(ip)) { a.BanPeer = true; Interlocked.Increment(ref Blocked); } } catch { }
                };
                L("blocklist: " + BlocklistRanges + " ranges");
            }
            catch (Exception ex) { L("blocklist: " + ex.Message); }
        }
        TorrentSettings SettingsFor(Entry x)
        {
            return new TorrentSettingsBuilder
            {
                AllowDht = S.Dht, AllowPeerExchange = S.Pex, AllowInitialSeeding = x.SuperSeed, CreateContainingDirectory = true,
                MaximumDownloadRate = x.DownKBps * 1024, MaximumUploadRate = x.UpKBps * 1024
            }.ToSettings();
        }
        async Task Attach(Entry x)
        {
            var e = engine; if (e == null) return;
            TorrentManager m;
            if (x.TorrentFile != null && Exists(x.TorrentFile)) m = await e.AddAsync(Torrent.Load(x.TorrentFile), x.SavePath, SettingsFor(x)).ConfigureAwait(false);
            else if (x.Magnet != null) m = await e.AddAsync(MagnetLink.Parse(x.Magnet), x.SavePath, SettingsFor(x)).ConfigureAwait(false);
            else { x.Scan = x.Scan ?? ""; L("no source for " + x.Id); return; }
            lock (lk) { mgr[x.Id] = m; last[x.Id] = new long[] { 0, 0 }; }
            if (m.HasMetadata) await ApplyFiles(x, m).ConfigureAwait(false);
            if (x.Sequential) { try { await m.ChangePickerAsync(new MonoTorrent.PiecePicking.StreamingPieceRequester()).ConfigureAwait(false); } catch (Exception ex) { L("sequential: " + ex.Message); } }
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
            var e = engine;
            if (e != null)
            {
                List<KeyValuePair<string, TorrentManager>> ms; lock (lk) ms = mgr.ToList();
                foreach (var kv in ms)
                {
                    var x = Get(kv.Key); var m = kv.Value; if (x == null) continue;
                    long[] l; lock (lk) if (!last.TryGetValue(kv.Key, out l)) last[kv.Key] = l = new long[] { 0, 0 };
                    long sent = m.Monitor.DataBytesSent, recv = m.Monitor.DataBytesReceived;
                    if (sent >= l[0]) x.Up += sent - l[0]; if (recv >= l[1]) x.Down += recv - l[1]; l[0] = sent; l[1] = recv;
                    if (m.State == TorrentState.Seeding) x.SeedSecs += 1;
                    if (m.HasMetadata && (x.Files == null || x.TorrentFile == null)) GotMetadata(x, m);
                    if (m.HasMetadata && m.Complete && x.CompletedAt == null && m.State != TorrentState.Hashing && m.State != TorrentState.HashingPaused)
                    {
                        x.CompletedAt = Family.Time(DateTime.Now); L("complete " + x.Id); Save();
                        var h = Completed; if (h != null) try { h(x); } catch { }
                    }
                    if (m.State == TorrentState.Error && m.Error != null) LastError = m.Error.Exception != null ? m.Error.Exception.Message : m.Error.Reason.ToString();
                }
                await Reconcile().ConfigureAwait(false);
            }
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
            List<Plan.Item> items; lock (lk) items = Entries.Select(x => new Plan.Item { Id = x.Id, Queue = x.Queue, UserPaused = x.UserPaused, Complete = x.CompletedAt != null, LimitHit = x.LimitHit, NoLimit = x.NoLimit, Ratio = RatioOf(x), SeedMinutes = x.SeedSecs / 60, RatioGoal = GoalOf(x) }).ToList();
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
                var want = Wanted();
                List<KeyValuePair<string, TorrentManager>> ms; lock (lk) ms = mgr.ToList();
                foreach (var kv in ms)
                {
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
                bool multi = files.Count > 1 || files[0].Path.Contains("\\") || files[0].Path.Contains("/");
                var root = multi ? Path.Combine(x.SavePath, x.Name) : Path.Combine(x.SavePath, files[0].Path);
                if (multi && Directory.Exists(root)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(root, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else if (File.Exists(root)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(root, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
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
                if (seqChanged) await m.ChangePickerAsync(sequential ? (MonoTorrent.PiecePicking.IPieceRequester)new MonoTorrent.PiecePicking.StreamingPieceRequester() : new MonoTorrent.PiecePicking.StandardPieceRequester(MonoTorrent.PiecePicking.PieceRequesterSettings.Default)).ConfigureAwait(false);
            }
            catch (Exception ex) { L("flags " + ex.Message); }
        }
        public void SetCategory(string id, string cat) { var x = Get(id); if (x == null) return; x.Category = cat; Save(); }
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
                }
                Kick(); return null;
            }
            catch (Exception ex) { Kick(); return ex.Message; }
        }
        public string FullPathOf(string id, string relPath)
        {
            var m = M(id);
            if (m != null && m.HasMetadata) { var f = m.Files.FirstOrDefault(q => string.Equals(q.Path, relPath, StringComparison.OrdinalIgnoreCase)); if (f != null) return f.FullPath; }
            var x = Get(id); if (x == null) return null;
            var files = x.FileList; bool multi = files.Count > 1 || relPath.Contains("\\") || relPath.Contains("/");
            return multi ? Path.Combine(x.SavePath, x.Name, relPath) : Path.Combine(x.SavePath, relPath);
        }
        public string RootOf(string id)
        {
            var x = Get(id); if (x == null) return null; var files = x.FileList;
            if (files.Count == 1 && !files[0].Path.Contains("\\") && !files[0].Path.Contains("/")) return FullPathOf(id, files[0].Path);
            var m = M(id); if (m != null && m.HasMetadata && m.Files.Count > 0) { var d = m.ContainingDirectory; if (!string.IsNullOrEmpty(d)) return d; }
            return Path.Combine(x.SavePath, x.Name);
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
                        v.Files.Add(new FileView { Index = i, Path = fl[i].Path, Length = fl[i].Length, Prio = x.PrioOf(i), Progress = p });
                    }
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
        public void Dispose() { try { Teardown("quit").Wait(3000); } catch { } Save(); }
    }
}
