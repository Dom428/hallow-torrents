// Hallow Torrents - statistics (v1.1), kept on this PC only (%APPDATA%\HallowTorrents\stats.json): downloaded / uploaded all-time and
// this session, the ratio, time spent seeding, time torrents waited because the VPN was off, and peers the blocklist refused.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace HallowTorrents
{
    [DataContract]
    public class Stats
    {
        [DataMember] public long Down, Up;               // all-time bytes (payload)
        [DataMember] public double SeedSecs;             // all-time seconds with at least one torrent seeding
        [DataMember] public double LockedSecs;           // all-time seconds torrents waited for the VPN
        [DataMember] public long Blocked;                // all-time peers refused by the blocklist
        [DataMember] public string Since;                // when counting started
        public long SessionDown, SessionUp, SessionBlocked; public double SessionSeedSecs, SessionLockedSecs; public DateTime SessionStart;
        string file; Stopwatch clock; int lastBlocked = -1; bool dirty;
        object lk = new object();   // (set again in Load: deserializing skips initializers)

        public static Stats Load(string dir)
        {
            Stats s = null; var f = dir == null ? null : Path.Combine(dir, "stats.json");
            try { if (f != null && File.Exists(f)) using (var st = File.OpenRead(f)) s = (Stats)new DataContractJsonSerializer(typeof(Stats)).ReadObject(st); } catch { s = null; }
            if (s == null) s = new Stats();
            if (s.Since == null) s.Since = Family.Time(DateTime.Now);
            s.file = f; s.SessionStart = DateTime.Now; s.lastBlocked = -1; s.lk = new object();
            return s;
        }
        public double Ratio { get { return Down > 0 ? Up / (double)Down : 0; } }
        public double SessionRatio { get { return SessionDown > 0 ? SessionUp / (double)SessionDown : 0; } }

        // once a tick: bytes moved since the last one, whether anything seeds, whether torrents are waiting for the VPN, the engine's
        // running count of blocked peers. Time is measured, not assumed (ticks can be late).
        public void Tick(long down, long up, bool seeding, bool lockedWaiting, int blockedTotal)
        {
            lock (lk)
            {
                double dt = 0;
                if (clock == null) clock = Stopwatch.StartNew(); else { dt = Math.Min(5, clock.Elapsed.TotalSeconds); clock.Restart(); }
                if (down > 0) { Down += down; SessionDown += down; dirty = true; }
                if (up > 0) { Up += up; SessionUp += up; dirty = true; }
                if (seeding && dt > 0) { SeedSecs += dt; SessionSeedSecs += dt; dirty = true; }
                if (lockedWaiting && dt > 0) { LockedSecs += dt; SessionLockedSecs += dt; dirty = true; }
                if (lastBlocked >= 0 && blockedTotal > lastBlocked) { Blocked += blockedTotal - lastBlocked; SessionBlocked += blockedTotal - lastBlocked; dirty = true; }
                lastBlocked = blockedTotal;
            }
        }
        public void Save()
        {
            if (file == null || TorSettings.Frozen) return;
            lock (lk)
            {
                if (!dirty && File.Exists(file)) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)); var tmp = file + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(Stats)).WriteObject(f, this);
                    for (int i = 0; ; i++) { try { if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                    dirty = false;
                }
                catch { }
            }
        }
    }
}
