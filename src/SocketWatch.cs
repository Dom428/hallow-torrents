// Hallow Torrents 1.5 - the socket watchdog (defence in depth on top of the VPN binding in Net.cs).
// Every few seconds it reads THIS process's own rows of Windows' TCP and UDP tables (GetExtendedTcpTable / GetExtendedUdpTable,
// IPv4 and IPv6, owner = our process id) and checks every socket's local address. Allowed:
//   * the VPN address the engine is bound to (and VPN addresses it was bound to in the last 2 minutes - those sockets can't send
//     anything once that adapter is gone);
//   * loopback (127.x, ::1) - never leaves this PC;
//   * the Web API / web interface listener and its connections, on the one address you picked (127.0.0.1 or a home-network /
//     Tailscale address - WebApi.cs never allows a public one), only while it runs;
//   * sockets opened through Net.Direct - the 1.4 local-indexer exception (private addresses only, LocalNet.cs) and the GitHub
//     update check. Each registers its own local port before it connects;
//   * local peer discovery's UDP port 6771, only if you switched LPD on (it's home-network only by design, off by default).
// Anything else - a socket bound to your normal connection, to 0.0.0.0 / ::, or any IPv6 address - locks the engine at once
// (Core.Trip: stopped like a VPN drop, and it stays stopped until you click Resume) and shows a red alert.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace HallowTorrents
{
    public class SockRow
    {
        public string Proto;   // "tcp" / "udp"
        public IPAddress Local, Remote; public int LocalPort, RemotePort, Pid; public string State;   // State: TCP only ("listen", "established"...)
        public override string ToString()
        {
            return Proto.ToUpperInvariant() + " " + Ep(Local, LocalPort) + (Proto == "tcp" && State != "listen" && Remote != null ? " -> " + Ep(Remote, RemotePort) : "") + (State != null ? " (" + State + ")" : "");
        }
        static string Ep(IPAddress a, int p) { return (a == null ? "?" : a.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + a + "]" : a.ToString()) + ":" + p; }
    }
    public interface ISocketTable { List<SockRow> Rows(int pid); }

    // Windows' own tables (iphlpapi), filtered to one process
    public class RealSocketTable : ISocketTable
    {
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int cls, uint reserved);
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int cls, uint reserved);
        const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
        static readonly string[] States = { "?", "closed", "listen", "syn-sent", "syn-received", "established", "fin-wait-1", "fin-wait-2", "close-wait", "closing", "last-ack", "time-wait", "delete-tcb" };
        public List<SockRow> Rows(int pid)
        {
            var r = new List<SockRow>();
            Read(true, AF_INET, pid, r); Read(true, AF_INET6, pid, r); Read(false, AF_INET, pid, r); Read(false, AF_INET6, pid, r);
            return r;
        }
        static int Port(uint p) { return (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF)); }
        static void Read(bool tcp, int af, int pid, List<SockRow> into)
        {
            int size = 0; uint err = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buf = IntPtr.Zero;
                try
                {
                    if (size > 0) buf = Marshal.AllocHGlobal(size);
                    err = tcp ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                    if (err == 122) { size += 4096; continue; }   // ERROR_INSUFFICIENT_BUFFER: the table grew
                    if (err != 0 || buf == IntPtr.Zero) { if (buf == IntPtr.Zero && size > 0) continue; return; }
                    int n = Marshal.ReadInt32(buf); var p = buf + 4;
                    for (int i = 0; i < n; i++)
                    {
                        if (tcp && af == AF_INET)
                        {
                            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                            int owner = Marshal.ReadInt32(p, 20);
                            if (owner == pid) into.Add(new SockRow { Proto = "tcp", State = St(Marshal.ReadInt32(p, 0)), Local = new IPAddress((uint)Marshal.ReadInt32(p, 4)), LocalPort = Port((uint)Marshal.ReadInt32(p, 8)), Remote = new IPAddress((uint)Marshal.ReadInt32(p, 12)), RemotePort = Port((uint)Marshal.ReadInt32(p, 16)), Pid = owner });
                            p += 24;
                        }
                        else if (tcp)
                        {
                            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                            int owner = Marshal.ReadInt32(p, 52);
                            if (owner == pid) into.Add(new SockRow { Proto = "tcp", Local = V6(p, 0), LocalPort = Port((uint)Marshal.ReadInt32(p, 20)), Remote = V6(p, 24), RemotePort = Port((uint)Marshal.ReadInt32(p, 44)), State = St(Marshal.ReadInt32(p, 48)), Pid = owner });
                            p += 56;
                        }
                        else if (af == AF_INET)
                        {
                            // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
                            int owner = Marshal.ReadInt32(p, 8);
                            if (owner == pid) into.Add(new SockRow { Proto = "udp", Local = new IPAddress((uint)Marshal.ReadInt32(p, 0)), LocalPort = Port((uint)Marshal.ReadInt32(p, 4)), Pid = owner });
                            p += 12;
                        }
                        else
                        {
                            // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScope, localPort, pid
                            int owner = Marshal.ReadInt32(p, 24);
                            if (owner == pid) into.Add(new SockRow { Proto = "udp", Local = V6(p, 0), LocalPort = Port((uint)Marshal.ReadInt32(p, 20)), Pid = owner });
                            p += 28;
                        }
                    }
                    return;
                }
                finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
            }
        }
        static string St(int s) { return s >= 0 && s < States.Length ? States[s] : "?"; }
        static IPAddress V6(IntPtr p, int off) { var b = new byte[16]; Marshal.Copy(p + off, b, 0, 16); return new IPAddress(b); }
    }

    // what is allowed right now (built fresh for every check)
    public class WatchContext
    {
        public List<IPAddress> VpnIps = new List<IPAddress>();
        public IPAddress ApiBind; public int ApiPort; public bool ApiRunning;
        public bool Lpd;
        public HashSet<int> DirectPorts = new HashSet<int>();
    }

    public static class SocketWatch
    {
        // sockets opened through Net.Direct (local indexer at a private address, the update check): local TCP port -> why, until when
        static readonly ConcurrentDictionary<int, Tuple<string, DateTime>> direct = new ConcurrentDictionary<int, Tuple<string, DateTime>>();
        public static void AllowDirect(int localPort, string why, TimeSpan keep) { direct[localPort] = Tuple.Create(why, DateTime.UtcNow + keep); }
        public static HashSet<int> DirectPorts()
        {
            var now = DateTime.UtcNow; var r = new HashSet<int>();
            foreach (var kv in direct.ToArray()) { if (kv.Value.Item2 > now) r.Add(kv.Key); else { Tuple<string, DateTime> x; direct.TryRemove(kv.Key, out x); } }
            return r;
        }
        public const int LpdPort = 6771;

        // null = fine; otherwise why it isn't
        public static string Judge(SockRow r, WatchContext c)
        {
            if (r == null || r.Local == null) return null;
            if (r.Proto == "tcp" && (r.State == "time-wait" || r.State == "closed" || r.State == "delete-tcb")) return null;   // already closed
            var local = r.Local.IsIPv4MappedToIPv6 ? r.Local.MapToIPv4() : r.Local;
            if (IPAddress.IsLoopback(local)) return null;
            if (local.AddressFamily == AddressFamily.InterNetwork && c.VpnIps.Any(v => v.Equals(local))) return null;
            if (r.Proto == "tcp" && c.ApiRunning && c.ApiBind != null && local.Equals(c.ApiBind) && r.LocalPort == c.ApiPort) return null;
            if (r.Proto == "tcp" && r.State != "listen" && c.DirectPorts.Contains(r.LocalPort)) return null;
            if (r.Proto == "udp" && c.Lpd && r.LocalPort == LpdPort) return null;
            if (local.AddressFamily == AddressFamily.InterNetworkV6) return r + ": an IPv6 socket (Hallow Torrents only uses the VPN's IPv4 address)";
            if (local.Equals(IPAddress.Any)) return r + ": bound to every network (0.0.0.0), not to the VPN";
            return r + ": on " + local + ", which isn't the VPN's address";
        }
        public static List<string> Check(IEnumerable<SockRow> rows, WatchContext c) { return rows.Select(x => Judge(x, c)).Where(x => x != null).ToList(); }
        // the same without any address (for status.json and the pipe, which never carry peer or VPN addresses)
        public static string Kind(SockRow r)
        {
            var local = r.Local != null && r.Local.IsIPv4MappedToIPv6 ? r.Local.MapToIPv4() : r.Local;
            var what = r.Proto.ToUpperInvariant() + (r.State == "listen" ? " listener" : " socket");
            if (local != null && local.AddressFamily == AddressFamily.InterNetworkV6) return what + " on an IPv6 address";
            if (local != null && local.Equals(IPAddress.Any)) return what + " bound to every network (0.0.0.0)";
            return what + " on an address that isn't the VPN's";
        }
    }

    // the loop: every few seconds, from the moment the app starts until it quits (locked or not)
    public class SocketWatchdog : IDisposable
    {
        readonly ISocketTable table; readonly Func<WatchContext> context; readonly Action<string> trip; volatile bool stop;
        public int PeriodMs = 3000;
        public int Checks, LastRows; public string LastAlert, LastAlertSafe, LastAlertAt; public DateTime LastCheck;
        public readonly int Pid = Process.GetCurrentProcess().Id;
        public SocketWatchdog(ISocketTable table, Func<WatchContext> context, Action<string> trip) { this.table = table; this.context = context; this.trip = trip; }
        public void Start() { new Thread(Loop) { IsBackground = true, Name = "socket watchdog", Priority = ThreadPriority.BelowNormal }.Start(); }
        public void Dispose() { stop = true; }
        // one check now: the problems found (and the engine is locked if there are any)
        public List<string> CheckNow()
        {
            var rows = table.Rows(Pid); var ctx = context();
            var badRows = rows.Where(x => SocketWatch.Judge(x, ctx) != null).ToList(); var bad = badRows.Select(x => SocketWatch.Judge(x, ctx)).ToList();
            Checks++; LastRows = rows.Count; LastCheck = DateTime.Now;
            if (bad.Count > 0)
            {
                LastAlert = bad[0] + (bad.Count > 1 ? " (and " + (bad.Count - 1) + " more)" : ""); LastAlertSafe = SocketWatch.Kind(badRows[0]); LastAlertAt = Family.Time(DateTime.Now);
                try { trip(LastAlert); } catch { }
            }
            return bad;
        }
        void Loop()
        {
            while (!stop)
            {
                try { CheckNow(); } catch { }
                for (int i = 0; i < PeriodMs / 100 && !stop; i++) Thread.Sleep(100);
            }
        }
        public Dictionary<string, object> Json()
        {
            return new Dictionary<string, object> { { "on", !stop }, { "everySeconds", PeriodMs / 1000.0 }, { "checks", Checks }, { "lastSockets", LastRows }, { "lastCheck", Checks == 0 ? null : Family.Time(LastCheck) }, { "alert", LastAlertSafe }, { "alertAt", LastAlertAt } };
        }
    }
}
