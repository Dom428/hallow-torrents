// Hallow Torrents 1.13 - a guard for a crash in MonoTorrent 3.0.2, found by the 1.13 benchmark (150 peers connecting at once):
// every peer that offers peer exchange gets a PeerExchangeManager (even with peer exchange switched off), which listens to the
// torrent's PeerConnected / PeerDisconnected events and puts each peer into a plain Queue<T>. MonoTorrent raises those events on
// thread-pool threads (AsyncInvoker<T>), so two peers connecting at the same moment run Enqueue on the same queue at once; when the
// queue grows that throws "Destination array was not long enough" on a thread-pool thread - and that ends the whole app
// (PeerExchangeManager.OnAdd -> Queue.Enqueue -> SetCapacity; 4 of 8 runs with 150 peers and 200 per torrent, 0 of 9 at 100).
// The fix: those two events are handed to their handlers one at a time (MonoTorrent's own thread-pool callback for them, wrapped
// in a lock). The handlers are tiny (put a peer in a queue), so nothing waits noticeably. If MonoTorrent's insides ever differ
// (a newer version), it does nothing. Nothing here touches a socket.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using MonoTorrent.Client;

namespace HallowTorrents
{
    public static class PexGuard
    {
        static readonly object Gate = new object(), installLock = new object();
        static int installed;   // 0 = not yet, 1 = on, -1 = not possible with this MonoTorrent
        public static long Serialized;   // events handed over through the guard (diagnostics / tests)
        public static bool On { get { return installed == 1; } }
        public static bool Install()
        {
            lock (installLock)
            {
                if (installed != 0) return installed == 1;
                try
                {
                    var open = typeof(TorrentManager).Assembly.GetType("MonoTorrent.AsyncInvoker`1");
                    var fields = new FieldInfo[2]; var olds = new WaitCallback[2]; int i = 0;
                    foreach (var arg in new[] { typeof(PeerConnectedEventArgs), typeof(PeerDisconnectedEventArgs) })
                    {
                        var t = open == null ? null : open.MakeGenericType(arg);
                        if (t != null) RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                        var f = t == null ? null : t.GetField("Invoker", BindingFlags.Static | BindingFlags.NonPublic);
                        var old = f == null || f.FieldType != typeof(WaitCallback) ? null : f.GetValue(null) as WaitCallback;
                        if (old == null) { installed = -1; return false; }
                        fields[i] = f; olds[i] = old; i++;
                    }
                    for (i = 0; i < 2; i++)
                    {
                        var old = olds[i];
                        fields[i].SetValue(null, new WaitCallback(o => { Interlocked.Increment(ref Serialized); lock (Gate) old(o); }));
                        if (!ReferenceEquals(fields[i].GetValue(null), old)) continue;
                        installed = -1; return false;
                    }
                    installed = 1; return true;
                }
                catch { installed = -1; return false; }
            }
        }
    }
}
