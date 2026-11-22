// Hallow Torrents settings: %APPDATA%\HallowTorrents\settings.json. Nothing secret is kept here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace HallowTorrents
{
    // a category: its own save folder, ratio goal and (optional) media library hand-off
    [DataContract]
    public class Category
    {
        [DataMember] public string Name;
        [DataMember] public string SaveFolder;        // null = the default save folder
        [DataMember] public double RatioGoal;         // 0 = the global limit
        [DataMember] public bool Handoff;             // copy / move to the library when done
        [DataMember] public string LibraryFolder;     // e.g. \\NAS\media\Movies
        [DataMember] public bool Move;                // move (keeps seeding from there) instead of copy
        [DataMember] public string Naming;            // "movie" / "tv" / "keep"
        [DataMember] public string Nas;               // v1.6 "Send to Jellyfin": "off" / "copy" (keep seeding here) / "after" (move after the seeding goal) / "now" (move now)
        public Category Clone() { return (Category)MemberwiseClone(); }
    }

    // a time-of-day speed rule: between From and To (hours, 0-24, may wrap midnight) on the given days, these limits (KB/s, 0 = no limit)
    [DataContract]
    public class ScheduleRule
    {
        [DataMember] public int From;
        [DataMember] public int To;
        [DataMember] public int DownKBps;
        [DataMember] public int UpKBps;
        [DataMember] public string Days;   // "all" / "weekdays" / "weekends"
    }

    [DataContract]
    public class TorSettings
    {
        // developer tests: HALLOW_TORRENTS_TESTDIR = data folder (and a separate pipe); HALLOW_TORRENTS_FAKE=1 = fake game / adapter
        // sources read from files in the test folder; HALLOW_TORRENTS_HEADLESS=1 = no windows or tray.
        public static readonly string TestDir = Env("HALLOW_TORRENTS_TESTDIR");
        public static readonly bool Fake = TestDir != null && Env("HALLOW_TORRENTS_FAKE") == "1";
        public static bool Headless = TestDir != null && Env("HALLOW_TORRENTS_HEADLESS") == "1";   // 1.4: also --headless / HallowTorrentsHeadless.exe
        public static bool Frozen;   // --snap: never saved
        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string Dir { get { return TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowTorrents"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        // ---- family
        [DataMember] public bool UseHallowIcon = true;
        [DataMember] public bool UseHallowAccent = true;
        [DataMember] public string Mode = "follow";
        [DataMember] public string LastUpdateCheck;
        [DataMember] public string LastCrashShown;
        [DataMember] public bool TrayHintShown;
        [DataMember] public bool LegalSeen;            // the first-run note
        [DataMember] public bool HandlerAsked;         // "open .torrent files and magnet links with Hallow Torrents?" was answered

        // ---- downloads
        [DataMember] public string SaveFolder;
        [DataMember] public int MaxActive = 3;         // downloads at once (the rest wait in the queue)
        [DataMember] public int DownKBps;              // 0 = no limit
        [DataMember] public int UpKBps;
        [DataMember] public double RatioLimit = 2.0;   // stop seeding at this ratio (0 = never)
        [DataMember] public int SeedMinutes;           // stop seeding after this long (0 = never)
        [DataMember] public bool AskOnAdd = true;      // the Add window for links from the browser / double-clicks
        [DataMember] public string WatchFolder;        // .torrent files dropped here are added
        [DataMember] public List<Category> Categories;
        [DataMember] public bool ScheduleOn;
        [DataMember] public List<ScheduleRule> Schedule;
        [DataMember] public string BlocklistFile;      // eMule .dat / PeerGuardian .p2p text
        [DataMember] public string BlocklistUrl;       // v1.1: a list downloaded over the VPN (plain, .gz or .zip)
        [DataMember] public int BlocklistDays = 7;     // ...and refreshed this often
        [DataMember] public string BlocklistUpdated, BlocklistNote;   // when it was last downloaded / what happened

        // ---- network
        [DataMember] public int Port;                  // 0 = picked on first start
        [DataMember] public bool Dht = true;
        [DataMember] public bool Pex = true;
        [DataMember] public bool Lpd;                  // home network only, can't be bound to the VPN: off by default
        [DataMember] public string Encryption = "prefer";   // "prefer" / "require"

        // ---- VPN (always required - there is no setting to turn it off)
        [DataMember] public string VpnSource = "hallow";    // "hallow" = Hallow VPN; "adapter" = the adapter below
        [DataMember] public string VpnAdapter;               // adapter id (or name) for "adapter"

        // ---- games
        [DataMember] public string GameMode = "pause";      // "off" / "pause" / "limit"
        [DataMember] public int GameKBps = 200;
        [DataMember] public List<string> ExtraGames;

        // ---- safety
        [DataMember] public bool ScanDone = true;            // Defender scan when a download completes

        // ---- v1.2: the qBittorrent-compatible Web API for Radarr / Sonarr / Prowlarr (WebApi.cs). Off by default, this PC only by default.
        [DataMember] public bool ApiOn;
        [DataMember] public int ApiPort = 8080;
        [DataMember] public string ApiBind = "127.0.0.1";    // 127.0.0.1, or a home-network / Tailscale address you picked - never 0.0.0.0
        [DataMember] public string ApiUser;
        [DataMember] public string ApiPassHash;              // a salted PBKDF2 hash (ApiAuth) - never the password itself
        [DataMember] public string ApiAllow;                 // optional allow-list: "192.168.1.20, 100.64.0.0/10" (empty = any local address)
        [DataMember] public int ApiSessionMinutes = 60;      // a sign-in expires after this long unused
        // ---- v1.4: the web interface at http://<address>:<port>/ui/ - the same server, sign-in and security as the Web API. Off by default.
        [DataMember] public bool WebUiOn;
        // ---- v1.5: performance (Perf.cs): a profile and optional Advanced numbers (0 = the profile's). Never touches the VPN lock.
        [DataMember] public string Perf = "balanced";     // "balanced" / "max" / "light"
        [DataMember] public int PerfConnections, PerfPerTorrent, PerfHalfOpen, PerfCacheMB, PerfUploadSlots, PerfOpenFiles;
        // ---- v1.6: Send to Jellyfin (Nas.cs). The folder on your NAS (\\192.168.4.197\media) - private addresses only. No password is
        // kept anywhere: Windows uses the credentials it saved when you mapped the share. The Jellyfin API key is in nas.json (DPAPI).
        [DataMember] public bool NasOn;
        [DataMember] public string NasFolder;
        [DataMember] public string NasMovies = "Movies", NasShows = "Shows";
        [DataMember] public string JellyfinUrl;

        [OnDeserializing] void Defaults(StreamingContext c) { UseHallowIcon = true; UseHallowAccent = true; Mode = "follow"; MaxActive = 3; RatioLimit = 2.0; AskOnAdd = true; Dht = true; Pex = true; Encryption = "prefer"; VpnSource = "hallow"; GameMode = "pause"; GameKBps = 200; ScanDone = true; BlocklistDays = 7; ApiPort = 8080; ApiBind = "127.0.0.1"; ApiSessionMinutes = 60; Perf = "balanced"; NasMovies = "Movies"; NasShows = "Shows"; }

        public static TorSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var f = File.OpenRead(FilePath))
                    {
                        var s = (TorSettings)new DataContractJsonSerializer(typeof(TorSettings)).ReadObject(f);
                        if (s != null) { s.Fix(); return s; }
                    }
            }
            catch { }
            var d = new TorSettings(); d.Fix(); return d;
        }
        public static string DefaultSaveFolder { get { return TestDir != null ? Path.Combine(TestDir, "Downloads") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Hallow Torrents"); } }
        public void Fix()
        {
            if (ExtraGames == null) ExtraGames = new List<string>();
            ExtraGames.RemoveAll(string.IsNullOrWhiteSpace);
            if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = DefaultSaveFolder;
            if (Categories == null || Categories.Count == 0)
                Categories = new List<Category> {
                    new Category { Name = "Movies", Naming = "movie" }, new Category { Name = "TV", Naming = "tv" },
                    new Category { Name = "Music", Naming = "keep" }, new Category { Name = "Games", Naming = "keep" }, new Category { Name = "Other", Naming = "keep" } };
            foreach (var c in Categories) { if (string.IsNullOrEmpty(c.Naming)) c.Naming = "keep"; if (c.Nas != "copy" && c.Nas != "after" && c.Nas != "now") c.Nas = "off"; }
            if (NasMovies == null) NasMovies = "Movies"; if (NasShows == null) NasShows = "Shows";
            if (Schedule == null) Schedule = new List<ScheduleRule>();
            if (MaxActive < 1) MaxActive = 1; if (MaxActive > 50) MaxActive = 50;
            if (Encryption != "require") Encryption = "prefer";
            if (VpnSource != "adapter") VpnSource = "hallow";
            if (GameMode != "off" && GameMode != "limit") GameMode = "pause";
            if (GameKBps < 1) GameKBps = 1;
            if (BlocklistDays < 1) BlocklistDays = 1; if (BlocklistDays > 90) BlocklistDays = 90;
            if (Port < 1024 || Port > 65535) Port = new Random().Next(20000, 60000);
            if (ApiPort < 1024 || ApiPort > 65535) ApiPort = 8080;
            System.Net.IPAddress bindIp; if (string.IsNullOrWhiteSpace(ApiBind) || !System.Net.IPAddress.TryParse(ApiBind.Trim(), out bindIp)) ApiBind = "127.0.0.1"; else ApiBind = bindIp.ToString();
            if (ApiSessionMinutes < 1) ApiSessionMinutes = 60; if (ApiSessionMinutes > 1440) ApiSessionMinutes = 1440;
            HallowTorrents.Perf.Fix(this);
        }
        public Category Cat(string name)
        {
            return Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        static readonly object saveLock = new object();
        public void Save()
        {
            if (Frozen) return;
            lock (saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(TorSettings)).WriteObject(f, this);
                    for (int i = 0; ; i++) { try { if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }
    }
}
