// Hallow Torrents 1.12 tests: Remove all - every torrent or only the finished ones; the files stay where they are (the Recycle
// Bin option is the same code as Remove on one torrent and isn't run here, so nothing lands in your Recycle Bin).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MonoTorrent;

namespace HallowTorrents.Tests
{
    public static partial class Run
    {
        static async Task RemoveAll112()
        {
            var root = Path.Combine(Dir, "removeall112"); var src = Path.Combine(root, "src"); Directory.CreateDirectory(src);
            var s = TestSettings(FreePort(), Path.Combine(root, "dl"));
            var core = new Core(Path.Combine(root, "core"), () => s);
            var ids = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var f = Path.Combine(src, "file" + i + ".bin"); File.WriteAllBytes(f, Enumerable.Range(0, 70000 + i).Select(b => (byte)(b * 7 + i)).ToArray());
                var tf = Path.Combine(root, "t" + i + ".torrent"); var cr = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 }; cr.Announces.Add(new List<string> { "http://127.0.0.1:9/announce" });
                await cr.CreateAsync(new TorrentFileSource(f), tf);
                string err; var e = core.AddTorrentFile(tf, null, null, null, false, out err); ids.Add(e.Id);
            }
            Ok(core.RemoveAllIds(false).Count == 3 && core.RemoveAllIds(true).Count == 0, "1.12 remove all: 3 in the list, none finished");
            core.Get(ids[1]).CompletedAt = Family.Time(DateTime.Now);
            Ok(core.RemoveAllIds(true).SequenceEqual(new[] { ids[1] }), "only finished: just the finished one is picked");
            var r1 = await core.RemoveAll(true, false);
            Ok(r1.Item1 == 1 && r1.Item2 == null && core.Get(ids[1]) == null && core.Get(ids[0]) != null && core.Get(ids[2]) != null, "only finished: the finished one left the list, the other two stay");
            var r2 = await core.RemoveAll(false, false);
            Ok(r2.Item1 == 2 && core.Entries.Count == 0, "all: the rest left the list");
            Ok(Enumerable.Range(0, 3).All(i => File.Exists(Path.Combine(src, "file" + i + ".bin"))), "files stay where they are");
            var r3 = await core.RemoveAll(false, false); Ok(r3.Item1 == 0 && r3.Item2 == null, "an empty list: nothing to do, no error");
            var reloaded = new Core(Path.Combine(root, "core"), () => s); Ok(reloaded.Entries.Count == 0, "the empty list is saved"); reloaded.Dispose();
            core.Dispose();
        }
    }
}
