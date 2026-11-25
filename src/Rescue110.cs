// Hallow Torrents 1.10 - magnet links stuck on "Getting info". A magnet has no file list until a peer sends it, and it finds
// peers only through its own trackers and DHT. Real use: an EZTV magnet sat at "Getting info" with 0 seeds and 0 peers while the
// engine and VPN were fine (the other torrents downloaded 2 GB). Now:
//   * after 75 s in "Getting info" with no peer found, the torrent gets a short list of well-known public trackers (added to
//     its own, like qBittorrent's "Automatically add these trackers") and asks the trackers and DHT again - once; then it asks
//     the DHT again every 2 minutes while it's still stuck. Settings switch "Add public trackers to magnet links that find no
//     one" (on). Every tracker still goes out from the VPN address only (Net.BoundTracker), like all the others.
//   * pipe "details <hash>": state, how long it has been getting info, peers found / connected, each tracker's status and
//     message, the DHT's state and node count, whether it was rescued - so a stuck torrent can be explained.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace HallowTorrents
{
    public partial class Core
    {
        public static readonly string[] PublicTrackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.demonii.com:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://exodus.desync.com:6969/announce",
            "udp://explodie.org:6969/announce",
            "udp://tracker.dler.org:6969/announce",
            "udp://tracker.qu.ax:6969/announce",
            "https://tracker.opentrackr.org/announce",
        };
        public const int RescueAfterSecs = 75, DhtAgainSecs = 120;
        readonly Dictionary<string, DateTime> infoSince = new Dictionary<string, DateTime>();
        readonly Dictionary<string, DateTime> lastDhtAsk = new Dictionary<string, DateTime>();
        readonly HashSet<string> rescued = new HashSet<string>();
        public int Rescues;
        // tests only: HALLOW_TORRENTS_RESCUE_SECS (with the test folder) shortens the wait
        static int RescueSecs { get { int s; return TorSettings.TestDir != null && int.TryParse(Environment.GetEnvironmentVariable("HALLOW_TORRENTS_RESCUE_SECS"), out s) && s > 0 ? s : RescueAfterSecs; } }

        // from Tick, every second, for each torrent
        void RescueTick(Entry x, TorrentManager m, DateTime now)
        {
            bool stuck = !m.HasMetadata && m.State == TorrentState.Metadata && !x.UserPaused;
            DateTime since;
            lock (lk)
            {
                if (!stuck) { infoSince.Remove(x.Id); lastDhtAsk.Remove(x.Id); return; }
                if (!infoSince.TryGetValue(x.Id, out since)) { infoSince[x.Id] = since = now; return; }
            }
            int found; try { found = m.Peers.Available + m.OpenConnections; } catch { found = 0; }
            if (found > 0 || (now - since).TotalSeconds < RescueSecs) return;
            bool first = false; if (S.AddPublicTrackers && x.Magnet != null) lock (lk) first = rescued.Add(x.Id);
            if (first)
            {
                var have = TrackersOf(x.Id); var add = PublicTrackers.Where(u => !have.Contains(u)).ToList();
                L("rescue " + x.Id + ": no peers after " + RescueSecs + " s - " + (add.Count > 0 ? "adding " + add.Count + " public trackers" : "asking again"));
                Rescues++;
                Task.Run(async () =>
                {
                    try { if (add.Count > 0) await SetTrackers(x.Id, have.Concat(add)).ConfigureAwait(false); } catch (Exception ex) { L("rescue trackers: " + ex.Message); }
                    try { await m.TrackerManager.AnnounceAsync(CancellationToken.None); } catch { }
                    try { await m.DhtAnnounceAsync(); } catch { }
                });
                lock (lk) lastDhtAsk[x.Id] = now;
                return;
            }
            DateTime lastAsk; bool due; lock (lk) due = !lastDhtAsk.TryGetValue(x.Id, out lastAsk) || (now - lastAsk).TotalSeconds >= DhtAgainSecs;
            if (due) { lock (lk) lastDhtAsk[x.Id] = now; Task.Run(async () => { try { await m.DhtAnnounceAsync(); } catch { } try { await m.TrackerManager.AnnounceAsync(CancellationToken.None); } catch { } }); }
        }

        // pipe "details <hash>" - nothing about you, only the torrent and the engine
        public Dictionary<string, object> Details(string id)
        {
            var x = Get(id); if (x == null) return null;
            var m = M(id); var e = engine; var now = DateTime.Now;
            var d = new Dictionary<string, object> { { "hash", x.Id }, { "name", x.Name }, { "magnet", x.Magnet != null }, { "paused", x.UserPaused }, { "engine", e != null } };
            if (m != null)
            {
                d["state"] = m.State.ToString(); d["hasMetadata"] = m.HasMetadata;
                try { d["peersFound"] = m.Peers.Available; d["seeds"] = m.Peers.Seeds; d["leechs"] = m.Peers.Leechs; d["connected"] = m.OpenConnections; } catch { }
                try { d["canUseDht"] = m.CanUseDht; d["lastDhtAnnounce"] = m.LastDhtAnnounce == DateTime.MinValue ? null : Family.Time(m.LastDhtAnnounce); } catch { }
            }
            DateTime since; lock (lk) { if (infoSince.TryGetValue(id, out since)) d["gettingInfoSecs"] = (int)(now - since).TotalSeconds; d["rescued"] = rescued.Contains(id); }
            d["trackers"] = TrackerViews(x, m).Select(t => (object)new Dictionary<string, object> { { "url", t.Url }, { "status", t.Status }, { "message", t.Message } }).ToList();
            try { if (!S.Dht) d["dht"] = "off"; else if (e != null && e.Dht != null) { d["dht"] = e.Dht.State.ToString(); d["dhtNodes"] = e.Dht.NodeCount; } else d["dht"] = S.Dht ? "not started" : "off"; } catch { d["dht"] = "?"; }
            d["dhtSetting"] = S.Dht; d["addPublicTrackers"] = S.AddPublicTrackers;
            return d;
        }
    }
}
