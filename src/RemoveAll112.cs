// Hallow Torrents 1.12 - "Remove all" next to Pause all / Resume all. It asks first: every torrent, or only the finished ones,
// and whether the files go to the Recycle Bin too (never a permanent delete - the same as Remove on one torrent). By default the
// files stay where they are. Pipe "remove-all [--finished] [--files]" for Hallow Remote.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HallowTorrents
{
    public partial class Core
    {
        public List<string> RemoveAllIds(bool onlyFinished) { lock (lk) return Entries.Where(x => !onlyFinished || x.CompletedAt != null).Select(x => x.Id).ToList(); }
        // returns how many left the list and the first problem (null = none)
        public async Task<Tuple<int, string>> RemoveAll(bool onlyFinished, bool deleteFiles)
        {
            int n = 0; string first = null;
            foreach (var id in RemoveAllIds(onlyFinished))
            {
                var r = await Remove(id, deleteFiles).ConfigureAwait(false);
                if (Get(id) == null) n++;
                if (r != null && first == null) first = r;
            }
            L("remove all: " + n + (onlyFinished ? " finished" : "") + (deleteFiles ? ", files to the Recycle Bin" : ""));
            return Tuple.Create(n, first);
        }
    }

}
