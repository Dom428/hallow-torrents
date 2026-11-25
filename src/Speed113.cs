// Hallow Torrents 1.13 - speed pass. Measured on the local benchmark (tests\BenchTests.cs, loopback swarms only):
//   * The 1.5 full recheck before "finished" stays - every piece on disk is compared with the torrent's hashes - but it now hashes on
//     several threads straight from the files. MonoTorrent 3.0.2's HashCheckAsync (one thread) took the app 7-11 s for 2,000 files /
//     251 MB; this takes ~1.3 s. One big file gains less: the first read of just-written data ran at ~450 MB/s here with 2, 6 or 12
//     threads alike (the disk / on-access scan; a second pass takes 0.2 s per GB). Only when that finds a
//     bad or missing piece does MonoTorrent's own recheck run (it marks the bad pieces so they're downloaded again), so the 1.5
//     integrity fix is unchanged. Torrents it can't check this way (v2-only, padding files, skipped files) use MonoTorrent's recheck.
//   * "Finished" no longer waits for the 1-second tick: the engine's switch to Seeding starts the recheck at once, and a good recheck
//     marks the download finished straight away (before: up to a second to notice, then up to a second after the recheck).
// No sockets here: file reads and hashing only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace HallowTorrents
{
    public static class FastVerify
    {
        public class Part { public string Path; public long Offset, Length; }
        public static double LastSetupSeconds;   // reading the torrent's piece hashes (diagnostics)
        public static bool LowPriority = true; public static double LastHashSeconds; public static int Threads;   // 0 = half the cores, at most 6

        // bad pieces (0 = all good), or -1 = can't check this torrent this way (use MonoTorrent's recheck)
        public static int Check(TorrentManager m, int threads = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var t = m.Torrent; if (t == null) return -1;
                var hashes = t.CreatePieceHashes(); if (hashes == null || !hashes.HasV1Hashes) return -1;
                var parts = new List<Part>();
                foreach (var f in m.Files)
                {
                    if (f.Padding != 0 || f.Priority == Priority.DoNotDownload) return -1;
                    parts.Add(new Part { Path = f.FullPath, Offset = f.OffsetInTorrent, Length = f.Length });
                }
                int n = t.PieceCount; var expected = new byte[n][];
                for (int i = 0; i < n; i++) { var h = hashes.GetHash(i).V1Hash; if (h.Length != 20) return -1; expected[i] = h.ToArray(); }
                LastSetupSeconds = sw.Elapsed.TotalSeconds;
                return Check(parts, t.PieceLength, expected, threads);
            }
            catch { return -1; }
        }

        // the pieces are split into one contiguous range per thread (long sequential reads - kind to hard disks too)
        public static int Check(List<Part> parts, int pieceLength, byte[][] expected, int threads = 0)
        {
            parts = parts.OrderBy(p => p.Offset).ToList();
            long total = parts.Count == 0 ? 0 : parts.Max(p => p.Offset + p.Length);
            int n = expected.Length; if (n == 0) return 0;
            if (threads <= 0) threads = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 6));
            threads = Math.Min(threads, n);
            int bad = 0; var per = (n + threads - 1) / threads; var clock = System.Diagnostics.Stopwatch.StartNew();
            var tasks = new List<Task>();
            for (int w = 0; w < threads; w++)
            {
                int from = w * per, to = Math.Min(n, from + per); if (from >= to) break;
                tasks.Add(Task.Factory.StartNew(() =>
                {
                    if (LowPriority) try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }   // a game in front keeps priority
                    var open = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase); var buf = new byte[pieceLength];
                    try
                    {
                        using (var sha = SHA1.Create())
                            for (int p = from; p < to; p++)
                            {
                                long start = (long)p * pieceLength; int len = (int)Math.Min(pieceLength, total - start);
                                if (len <= 0 || !Read(parts, open, start, buf, len) || !Same(sha.ComputeHash(buf, 0, len), expected[p])) Interlocked.Increment(ref bad);
                            }
                    }
                    finally { foreach (var s in open.Values) try { s.Dispose(); } catch { } }
                }, TaskCreationOptions.LongRunning));
            }
            Task.WaitAll(tasks.ToArray()); LastHashSeconds = clock.Elapsed.TotalSeconds;
            return bad;
        }
        static bool Read(List<Part> parts, Dictionary<string, FileStream> open, long start, byte[] buf, int len)
        {
            int done = 0;
            // the first file that ends after `start` (binary search - torrents can have tens of thousands of files)
            int lo = 0, hi = parts.Count - 1, first = parts.Count;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (parts[mid].Offset + parts[mid].Length > start) { first = mid; hi = mid - 1; } else lo = mid + 1; }
            for (int i = first; i < parts.Count; i++)
            {
                var f = parts[i];
                if (done >= len) break;
                long pos = start + done; if (f.Length == 0 || pos >= f.Offset + f.Length) continue;
                if (pos < f.Offset) return false;   // a gap between files (shouldn't happen)
                int want = (int)Math.Min(len - done, f.Offset + f.Length - pos);
                FileStream s;
                if (!open.TryGetValue(f.Path, out s))
                {
                    if (!File.Exists(f.Path)) return false;
                    if (open.Count >= 64) { foreach (var o in open.Values) o.Dispose(); open.Clear(); }
                    s = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan); open[f.Path] = s;   // buffer 1 = none: whole pieces are read at once
                }
                if (s.Length < f.Length) return false;   // short file (never fully written)
                s.Position = pos - f.Offset;
                int got = 0; while (got < want) { int r = s.Read(buf, done + got, want - got); if (r <= 0) return false; got += r; }
                done += want;
            }
            return done == len;
        }
        static bool Same(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
}
