// Hallow Torrents 1.5 - performance profiles: how hard the engine (MonoTorrent 3.0.2) works. None of this touches the VPN lock: every
// number here only changes how many peers / how much memory / how many open files the engine uses; every socket still comes from
// Net.Bound (bound to the VPN's address) and the engine still stops within a second when the VPN drops.
//   Balanced (default) - faster than 1.4 on the local benchmark without being heavy on a gaming PC
//   Max speed          - more peers, a bigger disk cache, more duplicate requests at the end
//   Light              - fewer peers, a small cache (old or busy PCs)
//   while gaming       - used by itself while a game runs and "While you play" is set to "Limit the speed" (with "Pause" nothing runs)
// Advanced fields (settings.json Perf*; 0 = the profile's number) override single numbers.
// uTP: MonoTorrent 3.0.2 has no uTP / uTorrent Transport Protocol at all (TCP peers only), so there is nothing to switch on - and it
// could never be allowed unless its UDP socket were bound to the VPN address like everything else.
using System;
using System.Collections.Generic;

namespace HallowTorrents
{
    public class PerfValues
    {
        public string Name;
        public int Connections;      // engine-wide open peer connections (EngineSettings.MaximumConnections)
        public int PerTorrent;       // per torrent (TorrentSettings.MaximumConnections)
        public int HalfOpen;         // connection attempts at once (EngineSettings.MaximumHalfOpenConnections)
        public int CacheMB;          // disk write cache (EngineSettings.DiskCacheBytes)
        public int OpenFiles;        // file handles kept open (EngineSettings.MaximumOpenFiles)
        public int UploadSlots;      // peers unchoked per torrent (TorrentSettings.UploadSlots)
        public int Endgame;          // duplicate requests per block in end-game mode (PieceRequesterSettings.MaximumDuplicateRequests)
        public PerfValues Clone() { return (PerfValues)MemberwiseClone(); }
        public string Summary { get { return Connections + " peers (" + PerTorrent + " per torrent), " + HalfOpen + " connecting at once, " + CacheMB + " MB disk cache, " + OpenFiles + " open files, " + UploadSlots + " upload slots, end game x" + Endgame; } }
    }

    public static class Perf
    {
        public static readonly string[] Profiles = { "balanced", "max", "light" };
        // "v14" = exactly what 1.4 used (MonoTorrent 3.0.2's defaults) - only for the benchmark, never offered in Settings
        public static PerfValues Preset(string name)
        {
            switch (name)
            {
                case "v14": return new PerfValues { Name = "v14", Connections = 150, PerTorrent = 60, HalfOpen = 8, CacheMB = 5, OpenFiles = 196, UploadSlots = 8, Endgame = 2 };
                case "max": return new PerfValues { Name = "max", Connections = 500, PerTorrent = 200, HalfOpen = 24, CacheMB = 64, OpenFiles = 512, UploadSlots = 10, Endgame = 4 };
                case "light": return new PerfValues { Name = "light", Connections = 80, PerTorrent = 40, HalfOpen = 6, CacheMB = 8, OpenFiles = 96, UploadSlots = 4, Endgame = 2 };
                case "gaming": return new PerfValues { Name = "gaming", Connections = 50, PerTorrent = 30, HalfOpen = 4, CacheMB = 4, OpenFiles = 64, UploadSlots = 3, Endgame = 1 };
                default: return new PerfValues { Name = "balanced", Connections = 300, PerTorrent = 100, HalfOpen = 16, CacheMB = 32, OpenFiles = 256, UploadSlots = 8, Endgame = 3 };
            }
        }
        public static string Label(string name)
        {
            switch (name) { case "max": return "Max speed"; case "light": return "Light"; case "gaming": return "While gaming"; case "v14": return "1.4 settings"; default: return "Balanced"; }
        }
        // what the engine uses right now: the profile, your Advanced numbers on top, or the gaming profile while a game is limited
        public static PerfValues For(TorSettings s, bool gaming)
        {
            if (gaming) return Preset("gaming");
            var v = Preset(s.Perf);
            if (s.Perf == "v14") return v;   // the benchmark's 1.4 baseline: no overrides
            if (s.PerfConnections > 0) v.Connections = s.PerfConnections;
            if (s.PerfPerTorrent > 0) v.PerTorrent = s.PerfPerTorrent;
            if (s.PerfHalfOpen > 0) v.HalfOpen = s.PerfHalfOpen;
            if (s.PerfCacheMB > 0) v.CacheMB = s.PerfCacheMB;
            if (s.PerfUploadSlots > 0) v.UploadSlots = s.PerfUploadSlots;
            if (s.PerfOpenFiles > 0) v.OpenFiles = s.PerfOpenFiles;
            if (v.PerTorrent > v.Connections) v.PerTorrent = v.Connections;
            return v;
        }
        // clamps for the Advanced fields (also applied when settings.json is loaded)
        public static void Fix(TorSettings s)
        {
            if (s.Perf != "max" && s.Perf != "light" && s.Perf != "v14") s.Perf = "balanced";
            s.PerfConnections = Clamp(s.PerfConnections, 10, 2000);
            s.PerfPerTorrent = Clamp(s.PerfPerTorrent, 5, 1000);
            s.PerfHalfOpen = Clamp(s.PerfHalfOpen, 1, 100);
            s.PerfCacheMB = Clamp(s.PerfCacheMB, 1, 512);
            s.PerfUploadSlots = Clamp(s.PerfUploadSlots, 1, 50);
            s.PerfOpenFiles = Clamp(s.PerfOpenFiles, 16, 2048);
        }
        static int Clamp(int v, int min, int max) { return v <= 0 ? 0 : v < min ? min : v > max ? max : v; }
        public static Dictionary<string, object> Json(PerfValues v)
        {
            return new Dictionary<string, object> { { "profile", v.Name }, { "connections", v.Connections }, { "perTorrent", v.PerTorrent }, { "halfOpen", v.HalfOpen }, { "cacheMB", v.CacheMB }, { "openFiles", v.OpenFiles }, { "uploadSlots", v.UploadSlots }, { "endgame", v.Endgame } };
        }
    }
}
