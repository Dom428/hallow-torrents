// Hallow Torrents 1.13 tests: the speed pass. The multi-threaded recheck (FastVerify) against hashes made by MonoTorrent's own
// TorrentCreator - good files pass, a changed byte / a short file / a missing file fail; a real download over a local swarm bound to
// the stand-in VPN address is rechecked that way and marked finished WITHOUT any tick (straight from the engine's switch to Seeding).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static async Task Speed113()
        {
            var root = Path.Combine(Dir, "speed113"); var src = Path.Combine(root, "src", "Pack"); Directory.CreateDirectory(src);
            var rnd = new Random(113); var sizes = new[] { 1, 70000, 0, 300000, 65536, 5, 1000000 };
            for (int i = 0; i < sizes.Length; i++) { var d = new byte[sizes[i]]; rnd.NextBytes(d); File.WriteAllBytes(Path.Combine(src, "f" + i + ".bin"), d); }
            var tf = Path.Combine(root, "pack.torrent"); var cr = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }; cr.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" });
            await cr.CreateAsync(new TorrentFileSource(src), tf);
            var t = Torrent.Load(tf); var ph = t.CreatePieceHashes();
            var expected = Enumerable.Range(0, t.PieceCount).Select(i => ph.GetHash(i).V1Hash.ToArray()).ToArray();
            Func<List<FastVerify.Part>> parts = () => t.Files.Select(f => new FastVerify.Part { Path = Path.Combine(root, "src", t.Name, f.Path), Offset = f.OffsetInTorrent, Length = f.Length }).ToList();
            Ok(FastVerify.Check(parts(), t.PieceLength, expected, 1) == 0 && FastVerify.Check(parts(), t.PieceLength, expected, 4) == 0 && FastVerify.Check(parts(), t.PieceLength, expected, 64) == 0,
                "1.13 recheck: " + t.PieceCount + " pieces over " + t.Files.Count + " files (empty and 1-byte files too) all good on 1, 4 and 64 threads");
            var f3 = Path.Combine(src, "f3.bin"); var orig = File.ReadAllBytes(f3);
            var bad = (byte[])orig.Clone(); bad[123456] ^= 1; File.WriteAllBytes(f3, bad);
            Ok(FastVerify.Check(parts(), t.PieceLength, expected, 4) == 1, "one changed byte: exactly 1 bad piece");
            var zero = (byte[])orig.Clone(); Array.Clear(zero, 40000, 16384); File.WriteAllBytes(f3, zero);
            Ok(FastVerify.Check(parts(), t.PieceLength, expected, 4) >= 1, "a 16 KB block of zeros (what MonoTorrent 3.0.2 sometimes left): found");
            File.WriteAllBytes(f3, orig.Take(orig.Length - 10).ToArray());
            Ok(FastVerify.Check(parts(), t.PieceLength, expected, 4) >= 1, "a short file: found");
            File.Delete(f3);
            Ok(FastVerify.Check(parts(), t.PieceLength, expected, 4) >= 1, "a missing file: found");
            File.WriteAllBytes(f3, orig);
            Ok(FastVerify.Check(parts(), t.PieceLength, expected, 4) == 0, "put back: all good again");

            // the peer exchange guard: MonoTorrent's own thread-pool raiser for PeerConnected now hands events over one at a time
            var c0 = new Core(Path.Combine(root, "core0"), () => TestSettings(FreePort(), Path.Combine(root, "dl0"))); c0.Dispose();
            Ok(PexGuard.On, "1.13 peer exchange guard installed (MonoTorrent 3.0.2's AsyncInvoker for PeerConnected / PeerDisconnected)");
            var inv = typeof(MonoTorrent.Client.TorrentManager).Assembly.GetType("MonoTorrent.AsyncInvoker`1").MakeGenericType(typeof(MonoTorrent.Client.PeerConnectedEventArgs)).GetMethod("InvokeAsync");
            var args = (MonoTorrent.Client.PeerConnectedEventArgs)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(MonoTorrent.Client.PeerConnectedEventArgs));
            int inside = 0, overlaps = 0, calls = 0; var plain = new Queue<int>();
            EventHandler<MonoTorrent.Client.PeerConnectedEventArgs> h = (o, e) => { if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps); plain.Enqueue(calls); Thread.SpinWait(2000); calls++; Interlocked.Decrement(ref inside); };
            for (int i = 0; i < 2000; i++) inv.Invoke(null, new object[] { h, null, args });
            Ok(Wait(() => Volatile.Read(ref calls) == 2000, 30000) && overlaps == 0 && plain.Count == 2000, "2,000 PeerConnected events raised at once from the thread pool: handled one at a time (" + calls + " calls, " + overlaps + " overlaps) - the queue that crashed MonoTorrent stays whole");

            // a real download: rechecked on several threads and finished without a single tick
            var sroot = Path.Combine(Dir, "speed113swarm"); Directory.CreateDirectory(sroot);
            using (var w = StartSwarm(sroot, 0))
            {
                var tf2 = await MakeTorrent(w, "Fast113", new[] { 3 * 1024 * 1024 + 7, 200000, 0, 900001 }, "http://127.0.0.1:" + w.HttpPort + "/announce");
                var save = Path.Combine(sroot, "dl"); var s = TestSettings(FreePort(), save);
                var core = new Core(Path.Combine(sroot, "core"), () => s); var log = new List<string>(); core.Log += q => { lock (log) log.Add(q); };
                string err; var e = core.AddTorrentFile(tf2, null, null, null, true, out err);
                await core.SetVpn(Vpn(VpnIp));   // builds the engine bound to the stand-in VPN address and starts the torrent - no ticker
                try
                {
                    Ok(Wait(() => core.Get(e.Id).CompletedAt != null, 30000), "finished without any tick (the switch to Seeding starts the recheck; a good recheck finishes it): " + string.Join(" | ", log.Where(x => x.StartsWith("verif") || x.StartsWith("complete"))));
                    Ok(core.LastVerifyKind == "fast" && core.Repaired == 0, "rechecked by the multi-threaded check (" + core.LastVerifyKind + ", " + core.LastVerifySeconds.ToString("0.00") + " s), nothing to repair");
                    Ok(Directory.GetFiles(Path.Combine(save, "Fast113")).All(f => Sha(f) == Sha(Path.Combine(sroot, "seed", "Fast113", Path.GetFileName(f)))), "the files match the seeder's");
                    var bound = core.BoundIp; Ok(bound != null && bound.Equals(VpnIp), "the engine stayed on the stand-in VPN address (" + bound + ")");
                }
                finally { core.Dispose(); }
            }
        }
    }
}
