// Hallow Torrents - the VPN requirement and leak protection.
//   * Torrents only run while a VPN is connected: Hallow VPN (status.json / its pipe say "connected") or the network adapter you picked
//     (NordVPN's NordLynx / OpenVPN TAP, WireGuard...) is up with an IPv4 address. There is no way to turn this off.
//   * Every socket the torrent engine opens is bound to that adapter's local IP: the peer listener, outgoing peer connections, DHT,
//     HTTP(S) trackers and web seeds (their own socket + their own DNS lookup over the VPN), UDP trackers (our own BEP 15 client).
//     Windows sends a packet from a bound address only through the adapter that owns it, so nothing can go out via the normal connection;
//     if the VPN adapter disappears, binding fails and nothing is sent at all.
//   * IPv6 peers are refused (the VPN adapter's IPv4 is the only address used). UPnP / NAT-PMP port mapping is always off.
//   * v1.5: the only sockets NOT bound to the VPN are made here too (Net.DirectTcp / DirectHttp): your own indexer manager at a private
//     address (1.4, LocalNet.cs) and the GitHub update check. They register with the socket watchdog (SocketWatch.cs), which checks
//     this process's own rows in Windows' TCP / UDP tables every 3 s and stops everything if any other socket is off the VPN address.
//     The 1.5 performance profiles (Perf.cs) only change numbers - connections, cache, open files - never a socket's binding.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace HallowTorrents
{
    public class AdapterInfo
    {
        public string Id, Name, Description; public bool Up; public List<IPAddress> IPv4 = new List<IPAddress>(); public List<IPAddress> Dns = new List<IPAddress>();
        public bool VpnLike { get { return Net.LooksLikeVpn(Name, Description); } }
        public string Label { get { return Name + (string.IsNullOrEmpty(Description) || Description == Name ? "" : " (" + Description + ")"); } }
    }
    public interface INetworks { List<AdapterInfo> List(); }
    public class RealNetworks : INetworks
    {
        public List<AdapterInfo> List()
        {
            var r = new List<AdapterInfo>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var a = new AdapterInfo { Id = n.Id, Name = n.Name, Description = n.Description, Up = n.OperationalStatus == OperationalStatus.Up };
                try
                {
                    var p = n.GetIPProperties();
                    foreach (var u in p.UnicastAddresses) if (u.Address.AddressFamily == AddressFamily.InterNetwork) a.IPv4.Add(u.Address);
                    foreach (var d in p.DnsAddresses) if (d.AddressFamily == AddressFamily.InterNetwork) a.Dns.Add(d);
                }
                catch { }
                r.Add(a);
            }
            return r;
        }
    }
    // tests: <test folder>\fake-adapters.txt, one adapter per line: id|name|description|up|ip,ip|dns,dns
    public class FakeNetworks : INetworks
    {
        public string File;
        public FakeNetworks(string file) { File = file; }
        public List<AdapterInfo> List()
        {
            var r = new List<AdapterInfo>();
            try
            {
                if (!System.IO.File.Exists(File)) return r;
                foreach (var line in System.IO.File.ReadAllLines(File))
                {
                    var p = line.Split('|'); if (p.Length < 5) continue;
                    var a = new AdapterInfo { Id = p[0], Name = p[1], Description = p[2], Up = p[3] == "up" };
                    foreach (var ip in p[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) { IPAddress x; if (IPAddress.TryParse(ip.Trim(), out x)) a.IPv4.Add(x); }
                    if (p.Length > 5) foreach (var ip in p[5].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) { IPAddress x; if (IPAddress.TryParse(ip.Trim(), out x)) a.Dns.Add(x); }
                    r.Add(a);
                }
            }
            catch { }
            return r;
        }
    }

    // what the VPN check decided
    public class VpnState
    {
        public bool Ok; public IPAddress Ip; public string Adapter, AdapterId, Server, Country, Message; public List<IPAddress> Dns = new List<IPAddress>();
        public bool HallowConnected;   // Hallow VPN says connected (even if its adapter wasn't found)
        public string Key { get { return Ok ? "ok|" + Ip + "|" + AdapterId : "off|" + Message; } }
    }

    public static class Net
    {
        static readonly string[] vpnWords = { "wireguard", "wintun", "nordlynx", "tap-windows", "tap-nordvpn", "openvpn", "hallow", "nordvpn", "protonvpn", "mullvad", "private internet access", "surfshark", "expressvpn", "cyberghost", "windscribe", "ikev2", "wan miniport (ikev2)", "vpn", "tun" };
        static readonly string[] notVpn = { "hyper-v", "virtualbox", "vmware", "loopback", "bluetooth", "wi-fi direct", "teredo", "isatap" };
        public static bool LooksLikeVpn(string name, string desc)
        {
            var s = ((name ?? "") + " " + (desc ?? "")).ToLowerInvariant();
            if (notVpn.Any(s.Contains)) return false;
            return vpnWords.Any(w => w == "tun" ? System.Text.RegularExpressions.Regex.IsMatch(s, @"\btun\b") : s.Contains(w));
        }

        // ---- the decision (pure, tested). hallow = Hallow VPN's status (status.json or its pipe), null = nothing; pipeAlive null = not checked
        public static VpnState Decide(string source, string adapterChoice, Dictionary<string, object> hallow, bool? pipeAlive, List<AdapterInfo> adapters)
        {
            var st = new VpnState();
            adapters = adapters ?? new List<AdapterInfo>();
            Func<AdapterInfo, bool> usable = a => a.Up && a.IPv4.Any(ip => !IPAddress.IsLoopback(ip) || TorSettings.TestDir != null);
            if (source == "adapter")
            {
                var a = adapters.FirstOrDefault(x => x.Id == adapterChoice) ?? adapters.FirstOrDefault(x => string.Equals(x.Name, adapterChoice, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(adapterChoice)) { st.Message = "Pick your VPN's network adapter in Settings > VPN."; return st; }
                if (a == null) { st.Message = "The VPN adapter you picked isn't there - connect your VPN."; return st; }
                if (!usable(a)) { st.Message = a.Name + " isn't connected - connect your VPN."; return st; }
                Fill(st, a); st.Ok = true; return st;
            }
            // Hallow VPN
            if (pipeAlive == false) { st.Message = "Hallow VPN isn't running - start it and connect."; return st; }
            object v; bool connected = hallow != null && hallow.TryGetValue("connected", out v) && v is bool && (bool)v;
            if (hallow != null && hallow.TryGetValue("running", out v) && v is bool && !(bool)v) connected = false;
            if (hallow != null) { st.Server = Str(hallow, "server"); st.Country = Str(hallow, "country"); }
            if (!connected) { st.Message = hallow == null ? "Hallow VPN isn't running - start it and connect." : "Hallow VPN isn't connected."; return st; }
            st.HallowConnected = true;
            // its adapter: the IP it reports (if any), the adapter it names (if any), else the VPN-looking adapter that is up
            AdapterInfo pick = null;
            var ipText = Str(hallow, "ip") ?? Str(hallow, "localIp") ?? Str(hallow, "vpnIp"); IPAddress want;
            if (ipText != null && IPAddress.TryParse(ipText, out want)) pick = adapters.FirstOrDefault(x => usable(x) && x.IPv4.Contains(want));
            var named = Str(hallow, "adapter") ?? Str(hallow, "interface");
            if (pick == null && named != null) pick = adapters.FirstOrDefault(x => usable(x) && (string.Equals(x.Name, named, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Description, named, StringComparison.OrdinalIgnoreCase) || x.Id == named));
            if (pick == null)
            {
                var vpns = adapters.Where(x => usable(x) && x.VpnLike).ToList();
                pick = vpns.FirstOrDefault(x => (x.Name + x.Description).IndexOf("hallow", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? vpns.FirstOrDefault(x => (x.Name + x.Description).IndexOf("nordlynx", StringComparison.OrdinalIgnoreCase) >= 0) ?? vpns.FirstOrDefault();
            }
            if (pick == null) { st.Message = "Hallow VPN says it's connected, but its network adapter wasn't found, so traffic can't be locked to it. Pick the adapter in Settings > VPN."; return st; }
            if (ipText != null && IPAddress.TryParse(ipText, out want) && pick.IPv4.Contains(want)) { Fill(st, pick); st.Ip = want; } else Fill(st, pick);
            st.Ok = true; return st;
        }
        static void Fill(VpnState st, AdapterInfo a)
        {
            st.Adapter = a.Name; st.AdapterId = a.Id; st.Dns = a.Dns.ToList();
            st.Ip = a.IPv4.FirstOrDefault(ip => !ip.ToString().StartsWith("169.254.")) ?? a.IPv4.First();
        }
        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is string && ((string)v).Length > 0 ? (string)v : null; }

        // ---- the socket audit: every socket the engine opens through us (tests check they're all bound to the VPN IP)
        public static readonly ConcurrentQueue<string> Audit = new ConcurrentQueue<string>();
        public static bool AuditOn;
        public static void Note(string what, EndPoint local) { if (AuditOn) Audit.Enqueue(what + " " + local); }

        // a TCP socket bound to ip, connected to ep (throws if ip isn't on this PC any more - which is the point)
        public static Socket BoundTcp(IPAddress ip, IPEndPoint ep, string what, CancellationToken token)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.Bind(new IPEndPoint(ip, 0)); Note(what, s.LocalEndPoint);
                using (token.Register(() => { try { s.Dispose(); } catch { } }))
                {
                    var ar = s.BeginConnect(ep, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(15000)) throw new TimeoutException("connect timed out");
                    s.EndConnect(ar);
                }
                return s;
            }
            catch { try { s.Dispose(); } catch { } throw; }
        }

        // ---- v1.5: the only sockets that are NOT bound to the VPN, all made here: the 1.4 local-indexer exception (a private address,
        // checked by the caller - LocalFetch) and the GitHub update check. Each registers its local port with the socket watchdog
        // (SocketWatch.AllowDirect) before it connects, so the watchdog knows it; any other socket off the VPN address trips it.
        public static Socket DirectTcp(IPAddress remote, int port, string why, CancellationToken token, int timeoutMs = 10000)
        {
            var s = new Socket(remote.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.Bind(new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                SocketWatch.AllowDirect(((IPEndPoint)s.LocalEndPoint).Port, why, TimeSpan.FromMinutes(15));
                using (token.Register(() => { try { s.Dispose(); } catch { } }))
                {
                    var ar = s.BeginConnect(new IPEndPoint(remote, port), null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException("connect timed out");
                    s.EndConnect(ar);
                }
                Note(why, s.LocalEndPoint);
                return s;
            }
            catch { try { s.Dispose(); } catch { } throw; }
        }
        // the update check (Updater.cs): Windows' resolver and your normal connection, like any app's update check - never torrent traffic
        // v1.6: the same, connecting only to addresses the caller already checked (Jellyfin on your network - PrivateNet)
        public static HttpClient DirectHttp(string why, IPAddress[] pinned)
        {
            var h = new StandardSocketsHttpHandler { UseProxy = false, Proxy = null, ConnectTimeout = TimeSpan.FromSeconds(10), AllowAutoRedirect = false };
            h.ConnectCallback = (ctx, token) => Task.Run(() =>
            {
                Exception last = null;
                foreach (var a in pinned) { if (!PrivateNet.IsPrivate(a)) throw new IOException("Not a private address - refused."); try { return (Stream)new NetworkStream(DirectTcp(a, ctx.DnsEndPoint.Port, why, token), true); } catch (Exception e) { last = e; } }
                throw last ?? new IOException("No address to connect to.");
            }, token);
            return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
        }
        public static HttpClient DirectHttp(string why)
        {
            var h = new StandardSocketsHttpHandler { UseProxy = false, Proxy = null, ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = true };
            h.ConnectCallback = (ctx, token) => Task.Run(() =>
            {
                var host = ctx.DnsEndPoint.Host.Trim('[', ']'); IPAddress lit;
                var addrs = IPAddress.TryParse(host, out lit) ? new[] { lit } : Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
                Exception last = null;
                foreach (var a in addrs) { try { return (Stream)new NetworkStream(DirectTcp(a, ctx.DnsEndPoint.Port, why, token, 15000), true); } catch (Exception e) { last = e; } }
                throw last ?? new IOException("Couldn't look up " + host);
            }, token);
            return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(60) };
        }

        // ---- the engine's factories: everything bound to ip, DNS over the VPN's DNS servers (from the bound socket)
        public static Factories Bound(IPAddress ip, List<IPAddress> dns)
        {
            var servers = (dns ?? new List<IPAddress>()).Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
            Func<AddressFamily, HttpClient> http = af => BoundHttp.Create(ip, servers);
            return Factories.Default
                .WithSocketConnectorCreator(() => new BoundConnector(ip))
                .WithPeerConnectionCreator("ipv4", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new BoundConnector(ip)))
                .WithPeerConnectionCreator("ipv6", uri => new MonoTorrent.Connections.Peer.SocketPeerConnection(uri, new RefusingConnector()))
                .WithHttpClientCreator(af => http(af))
                .WithTrackerCreator("http", uri => BoundTracker(uri, ip, servers))
                .WithTrackerCreator("https", uri => BoundTracker(uri, ip, servers))
                .WithTrackerCreator("udp", uri => BoundTracker(uri, ip, servers))
                .WithPortForwarderCreator(() => new NoPortForwarder())
                .WithDhtCreator(() => new VpnDht(ip, servers));
        }
        // a tracker whose every request goes out from the VPN address (also used for trackers you add by hand); null = not http(s)/udp
        public static ITracker BoundTracker(Uri uri, IPAddress ip, List<IPAddress> dns)
        {
            var servers = (dns ?? new List<IPAddress>()).Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (uri.Scheme == "udp") return new Tracker(new BoundUdpTracker(uri, ip, servers));
            if (uri.Scheme == "http" || uri.Scheme == "https") return new Tracker(new HttpTrackerConnection(uri, af => BoundHttp.Create(ip, servers), AddressFamily.InterNetwork));
            return null;
        }
    }

    // outgoing peer connections from the VPN IP only
    public class BoundConnector : ISocketConnector
    {
        readonly IPAddress ip;
        public BoundConnector(IPAddress ip) { this.ip = ip; }
        public ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            var tcs = new ReusableTaskCompletionSource<Socket>();
            IPAddress host;
            if (!IPAddress.TryParse(uri.Host, out host) || host.AddressFamily != AddressFamily.InterNetwork) { tcs.SetException(new SocketException((int)SocketError.AddressFamilyNotSupported)); return tcs.Task; }
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.Bind(new IPEndPoint(ip, 0)); Net.Note("peer", s.LocalEndPoint);
                var reg = token.Register(() => { try { s.Dispose(); } catch { } });
                s.BeginConnect(new IPEndPoint(host, uri.Port), ar =>
                {
                    reg.Dispose();
                    try { s.EndConnect(ar); tcs.SetResult(s); }
                    catch (Exception e) { try { s.Dispose(); } catch { } tcs.SetException(e); }
                }, null);
            }
            catch (Exception e) { try { s.Dispose(); } catch { } tcs.SetException(e); }
            return tcs.Task;
        }
    }
    // IPv6 peers: refused (the VPN's IPv4 address is the only one used)
    public class RefusingConnector : ISocketConnector
    {
        public ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            var tcs = new ReusableTaskCompletionSource<Socket>(); tcs.SetException(new SocketException((int)SocketError.AddressFamilyNotSupported)); return tcs.Task;
        }
    }
    // UPnP / NAT-PMP: never (it would open a port on the home router, outside the VPN)
    public class NoPortForwarder : MonoTorrent.PortForwarding.IPortForwarder
    {
        public event EventHandler MappingsChanged { add { } remove { } }
        public bool Active { get { return false; } }
        public MonoTorrent.PortForwarding.Mappings Mappings { get { return MonoTorrent.PortForwarding.Mappings.Empty; } }
        public Task RegisterMappingAsync(MonoTorrent.PortForwarding.Mapping mapping) { return Done; }
        public Task UnregisterMappingAsync(MonoTorrent.PortForwarding.Mapping mapping, CancellationToken token) { return Done; }
        public Task StartAsync(CancellationToken token) { return Done; }
        public Task StopAsync(CancellationToken token) { return Done; }
        public Task StopAsync(bool unmapExisting, CancellationToken token) { return Done; }
        static readonly Task Done = Task.FromResult(true);
    }

    // ---- DNS over the VPN: a plain A-record lookup sent from the bound socket to the VPN adapter's DNS servers (falls back to
    // public resolvers - still through the tunnel, because the socket is bound to the VPN IP). Windows' own resolver is never used.
    public static class DnsVpn
    {
        public static readonly IPAddress[] Fallback = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("9.9.9.9") };
        static readonly ConcurrentDictionary<string, Tuple<IPAddress, DateTime>> cache = new ConcurrentDictionary<string, Tuple<IPAddress, DateTime>>(StringComparer.OrdinalIgnoreCase);
        public static int Port = 53;   // tests point it at a local stand-in
        public static IPAddress Resolve(string host, IPAddress bind, List<IPAddress> servers)
        {
            IPAddress lit; if (IPAddress.TryParse(host, out lit)) return lit;
            Tuple<IPAddress, DateTime> c; if (cache.TryGetValue(host + "@" + bind, out c) && c.Item2 > DateTime.UtcNow) return c.Item1;
            var list = (servers != null && servers.Count > 0 ? servers : new List<IPAddress>()).Concat(Fallback).Distinct().ToList();
            Exception last = null;
            foreach (var srv in list)
            {
                try
                {
                    using (var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        u.Bind(new IPEndPoint(bind, 0)); Net.Note("dns", u.LocalEndPoint); u.ReceiveTimeout = 2500;
                        ushort id = (ushort)new Random().Next(1, 65535);
                        var q = Query(host, id); u.SendTo(q, new IPEndPoint(srv, Port));
                        var buf = new byte[1500]; EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n = u.ReceiveFrom(buf, ref from);
                        var ip = Answer(buf, n, id);
                        if (ip != null) { cache[host + "@" + bind] = Tuple.Create(ip, DateTime.UtcNow.AddMinutes(10)); return ip; }
                    }
                }
                catch (Exception e) { last = e; }
            }
            throw new IOException("Couldn't look up " + host + " over the VPN" + (last != null ? ": " + last.Message : ""));
        }
        // v1.4: every A record (the local-server check for indexers needs all of them - one public address = no exception). Not cached.
        public static List<IPAddress> ResolveAll(string host, IPAddress bind, List<IPAddress> servers)
        {
            IPAddress lit; if (IPAddress.TryParse(host, out lit)) return new List<IPAddress> { lit };
            var list = (servers != null && servers.Count > 0 ? servers : new List<IPAddress>()).Concat(Fallback).Distinct().ToList();
            Exception last = null;
            foreach (var srv in list)
            {
                try
                {
                    using (var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        u.Bind(new IPEndPoint(bind, 0)); Net.Note("dns", u.LocalEndPoint); u.ReceiveTimeout = 2500;
                        ushort id = (ushort)new Random().Next(1, 65535);
                        var q = Query(host, id); u.SendTo(q, new IPEndPoint(srv, Port));
                        var buf = new byte[1500]; EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n = u.ReceiveFrom(buf, ref from);
                        var all = Answers(buf, n, id);
                        if (all.Count > 0) return all;
                    }
                }
                catch (Exception e) { last = e; }
            }
            throw new IOException("Couldn't look up " + host + " over the VPN" + (last != null ? ": " + last.Message : ""));
        }
        public static List<IPAddress> Answers(byte[] r, int n, ushort id)
        {
            var res = new List<IPAddress>();
            if (n < 12 || r[0] != (byte)(id >> 8) || r[1] != (byte)id || (r[3] & 0x0F) != 0) return res;
            int qd = r[4] << 8 | r[5], an = r[6] << 8 | r[7], p = 12;
            for (int i = 0; i < qd; i++) { p = Skip(r, p, n); p += 4; }
            for (int i = 0; i < an && p < n; i++)
            {
                p = Skip(r, p, n); if (p + 10 > n) break;
                int type = r[p] << 8 | r[p + 1], len = r[p + 8] << 8 | r[p + 9]; p += 10;
                if (type == 1 && len == 4 && p + 4 <= n) res.Add(new IPAddress(new[] { r[p], r[p + 1], r[p + 2], r[p + 3] }));
                p += len;
            }
            return res;
        }
        public static byte[] Query(string host, ushort id)
        {
            var b = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in host.TrimEnd('.').Split('.')) { var l = Encoding.ASCII.GetBytes(label); if (l.Length == 0 || l.Length > 63) throw new ArgumentException("bad host"); b.Add((byte)l.Length); b.AddRange(l); }
            b.Add(0); b.AddRange(new byte[] { 0, 1, 0, 1 });
            return b.ToArray();
        }
        // the first A record of a reply, or null
        public static IPAddress Answer(byte[] r, int n, ushort id)
        {
            if (n < 12 || r[0] != (byte)(id >> 8) || r[1] != (byte)id || (r[3] & 0x0F) != 0) return null;
            int qd = r[4] << 8 | r[5], an = r[6] << 8 | r[7], p = 12;
            for (int i = 0; i < qd; i++) { p = Skip(r, p, n); p += 4; }
            for (int i = 0; i < an && p < n; i++)
            {
                p = Skip(r, p, n); if (p + 10 > n) return null;
                int type = r[p] << 8 | r[p + 1], len = r[p + 8] << 8 | r[p + 9]; p += 10;
                if (type == 1 && len == 4 && p + 4 <= n) return new IPAddress(new[] { r[p], r[p + 1], r[p + 2], r[p + 3] });
                p += len;
            }
            return null;
        }
        static int Skip(byte[] r, int p, int n)
        {
            while (p < n) { int l = r[p]; if (l == 0) return p + 1; if ((l & 0xC0) == 0xC0) return p + 2; p += l + 1; }
            return p;
        }
    }

    // HTTP(S) trackers and web seeds: our own socket bound to the VPN IP, our own DNS over the VPN, never a system proxy
    public static class BoundHttp
    {
        public static HttpClient Create(IPAddress ip, List<IPAddress> dns) { return Create(ip, dns, true); }
        // v1.3: redirects = false lets the caller see a redirect itself (an indexer's download link that points at a magnet link)
        public static HttpClient Create(IPAddress ip, List<IPAddress> dns, bool redirects)
        {
            var h = new StandardSocketsHttpHandler { UseProxy = false, Proxy = null, ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = redirects };
            h.ConnectCallback = (ctx, token) => Task.Run(() =>
            {
                var addr = DnsVpn.Resolve(ctx.DnsEndPoint.Host, ip, dns);
                if (addr.AddressFamily != AddressFamily.InterNetwork) throw new IOException("IPv4 only over the VPN");
                var s = Net.BoundTcp(ip, new IPEndPoint(addr, ctx.DnsEndPoint.Port), "http", token);
                return (Stream)new NetworkStream(s, true);
            }, token);
            return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
        }
    }

    // UDP trackers (BEP 15) from a socket bound to the VPN IP - MonoTorrent's own UDP tracker client can't be bound
    public class BoundUdpTracker : ITrackerConnection
    {
        readonly Uri uri; readonly IPAddress ip; readonly List<IPAddress> dns;
        public BoundUdpTracker(Uri uri, IPAddress ip, List<IPAddress> dns) { this.uri = uri; this.ip = ip; this.dns = dns; }
        public bool CanScrape { get { return false; } }
        public Uri Uri { get { return uri; } }
        public ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest parameters, CancellationToken token)
        {
            return ReusableTask.FromResult(new ScrapeResponse(TrackerState.Offline, new Dictionary<InfoHash, ScrapeInfo>(), null, "scrape isn't supported"));
        }
        public ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest req, CancellationToken token)
        {
            var tcs = new ReusableTaskCompletionSource<AnnounceResponse>();
            Task.Run(() =>
            {
                try { tcs.SetResult(Announce(req, token)); }
                catch (Exception e) { tcs.SetResult(new AnnounceResponse(TrackerState.Offline, new Dictionary<InfoHash, IList<PeerInfo>>(), null, null, new Dictionary<InfoHash, ScrapeInfo>(), null, e.Message)); }
            });
            return tcs.Task;
        }
        static void Put(List<byte> b, long v, int bytes) { for (int i = bytes - 1; i >= 0; i--) b.Add((byte)(v >> (8 * i))); }
        static long Get(byte[] b, int p, int bytes) { long v = 0; for (int i = 0; i < bytes; i++) v = v << 8 | b[p + i]; return v; }
        AnnounceResponse Announce(AnnounceRequest req, CancellationToken token)
        {
            var host = DnsVpn.Resolve(uri.Host, ip, dns); var ep = new IPEndPoint(host, uri.Port);
            using (var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (token.Register(() => { try { u.Dispose(); } catch { } }))
            {
                u.Bind(new IPEndPoint(ip, 0)); Net.Note("udp-tracker", u.LocalEndPoint); u.ReceiveTimeout = 5000;
                var rnd = new Random(); var buf = new byte[4096];
                // connect
                int tid = rnd.Next(); var c = new List<byte>(); Put(c, 0x41727101980L, 8); Put(c, 0, 4); Put(c, tid, 4);
                long conn = 0; bool ok = false;
                for (int attempt = 0; attempt < 2 && !ok; attempt++)
                {
                    u.SendTo(c.ToArray(), ep); EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    try { int n = u.ReceiveFrom(buf, ref from); if (n >= 16 && Get(buf, 0, 4) == 0 && Get(buf, 4, 4) == (uint)tid) { conn = Get(buf, 8, 8); ok = true; } } catch (SocketException) { }
                }
                if (!ok) throw new IOException("no answer from the tracker");
                // announce
                tid = rnd.Next(); var a = new List<byte>(); Put(a, conn, 8); Put(a, 1, 4); Put(a, tid, 4);
                a.AddRange(req.InfoHashes.V1OrV2.Truncate().AsMemory().ToArray());
                var pid = req.PeerId.ToArray(); a.AddRange(pid.Length == 20 ? pid : pid.Concat(new byte[20]).Take(20));
                Put(a, req.BytesDownloaded, 8); Put(a, req.BytesLeft, 8); Put(a, req.BytesUploaded, 8);
                Put(a, req.ClientEvent == TorrentEvent.Completed ? 1 : req.ClientEvent == TorrentEvent.Started ? 2 : req.ClientEvent == TorrentEvent.Stopped ? 3 : 0, 4);
                Put(a, 0, 4); Put(a, req.Key, 4); Put(a, 200, 4);
                var rep = req.GetReportedAddress("ipv4"); Put(a, rep.Item2, 2);
                u.SendTo(a.ToArray(), ep); EndPoint f2 = new IPEndPoint(IPAddress.Any, 0);
                int m = u.ReceiveFrom(buf, ref f2);
                if (m < 20 || Get(buf, 4, 4) != (uint)tid) throw new IOException("bad answer from the tracker");
                if (Get(buf, 0, 4) == 3) throw new IOException("tracker: " + Encoding.UTF8.GetString(buf, 8, m - 8));
                var interval = TimeSpan.FromSeconds(Math.Max(60, Get(buf, 8, 4)));
                var peers = new List<PeerInfo>();
                for (int p = 20; p + 6 <= m; p += 6)
                {
                    var addr = new IPAddress(new[] { buf[p], buf[p + 1], buf[p + 2], buf[p + 3] }); int port = (int)Get(buf, p + 4, 2);
                    if (port > 0) peers.Add(new PeerInfo(new Uri("ipv4://" + addr + ":" + port)));
                }
                var d = new Dictionary<InfoHash, IList<PeerInfo>>(); d[req.InfoHashes.V1OrV2] = peers;
                return new AnnounceResponse(TrackerState.Ok, d, interval, interval, new Dictionary<InfoHash, ScrapeInfo>(), null, null);
            }
        }
    }

    // ---- the watch: re-checks the VPN every 250 ms (adapters every second, Hallow VPN's pipe every 3 s) and reports changes at once
    public class VpnWatch : IDisposable
    {
        readonly Func<TorSettings> settings; readonly INetworks nets; readonly bool checkPipe; volatile bool stop;
        public VpnState Current = new VpnState { Message = "Checking the VPN..." };
        public event Action<VpnState, VpnState> Changed;   // (old, new) - on the watch thread
        Dictionary<string, object> pipeStatus; DateTime pipeAt = DateTime.MinValue; bool? pipeAlive;
        public VpnWatch(Func<TorSettings> settings, INetworks nets, bool checkPipe) { this.settings = settings; this.nets = nets; this.checkPipe = checkPipe; }
        public void Start() { new Thread(Loop) { IsBackground = true, Name = "vpn watch" }.Start(); if (checkPipe) new Thread(PipeLoop) { IsBackground = true, Name = "vpn pipe" }.Start(); }
        public void Dispose() { stop = true; }
        public static Dictionary<string, object> ReadStatusFile()
        {
            try
            {
                var f = Path.Combine(Family.DataDir("VPN"), "status.json"); if (!File.Exists(f)) return null;
                string t = null; for (int i = 0; i < 3 && t == null; i++) { try { t = File.ReadAllText(f); } catch (IOException) { Thread.Sleep(30); } }
                return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(t ?? "");
            }
            catch { return null; }
        }
        public VpnState CheckNow()
        {
            var s = settings();
            Dictionary<string, object> h = null; bool? alive = null;
            if (s.VpnSource == "hallow")
            {
                h = ReadStatusFile();
                if (checkPipe) { alive = pipeAlive; if (pipeStatus != null && (DateTime.UtcNow - pipeAt).TotalSeconds < 7) h = pipeStatus; }
            }
            List<AdapterInfo> a; try { a = nets.List(); } catch { a = new List<AdapterInfo>(); }
            return Net.Decide(s.VpnSource, s.VpnAdapter, h, alive, a);
        }
        void Loop()
        {
            while (!stop)
            {
                try
                {
                    var now = CheckNow(); var old = Current;
                    if (now.Key != old.Key || now.Server != old.Server) { Current = now; var c = Changed; if (c != null) c(old, now); }
                    else Current = now;
                }
                catch { }
                Thread.Sleep(250);
            }
        }
        void PipeLoop()
        {
            int n = 0;
            while (!stop)
            {
                try
                {
                    bool alive = Family.Running("VPN"); pipeAlive = alive;
                    if (alive && n % 12 == 0) { var st = Family.Status("VPN"); if (st != null) { pipeStatus = st; pipeAt = DateTime.UtcNow; } }
                    if (!alive) pipeStatus = null;
                }
                catch { }
                n++; Thread.Sleep(250);
            }
        }
    }
}

namespace HallowTorrents
{
    // DHT whose bootstrap never uses Windows' DNS: MonoTorrent's DHT looks up router.bittorrent.com with the system resolver when it
    // has no saved nodes. Here the router is looked up over the VPN (DnsVpn, from the bound socket) and handed in as a starting node;
    // if that lookup fails the DHT simply doesn't start (it never falls back to the system resolver). Its UDP socket is bound to the
    // VPN IP by the engine (DhtEndPoint).
    public class VpnDht : MonoTorrent.Dht.IDhtEngine
    {
        readonly MonoTorrent.Dht.DhtEngine inner = new MonoTorrent.Dht.DhtEngine(); readonly IPAddress ip; readonly List<IPAddress> dns;
        public static readonly string[] Routers = { "router.bittorrent.com:6881", "dht.transmissionbt.com:6881", "router.utorrent.com:6881" };
        public VpnDht(IPAddress ip, List<IPAddress> dns) { this.ip = ip; this.dns = dns; }
        public event EventHandler<MonoTorrent.Dht.PeersFoundEventArgs> PeersFound { add { inner.PeersFound += value; } remove { inner.PeersFound -= value; } }
        public event EventHandler StateChanged { add { inner.StateChanged += value; } remove { inner.StateChanged -= value; } }
        public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) { inner.Add(nodes); }
        public void Announce(InfoHash infoHash, int port) { inner.Announce(infoHash, port); }
        public void GetPeers(InfoHash infoHash) { inner.GetPeers(infoHash); }
        public Task<ReadOnlyMemory<byte>> SaveNodesAsync() { return inner.SaveNodesAsync(); }
        public Task SetListenerAsync(MonoTorrent.Connections.Dht.IDhtListener listener) { return inner.SetListenerAsync(listener); }
        public Task StartAsync() { return StartAsync(new ReadOnlyMemory<byte>(new byte[0])); }
        public Task StartAsync(ReadOnlyMemory<byte> initialNodes)
        {
            if (initialNodes.Length >= 26) return inner.StartAsync(initialNodes);
            return Task.Run(() =>
            {
                var compact = Bootstrap(ip, dns);
                if (compact.Length == 0) return Task.FromResult(0);   // no VPN lookup = no DHT (never the system resolver)
                return inner.StartAsync(new ReadOnlyMemory<byte>(compact));
            });
        }
        public static byte[] Bootstrap(IPAddress ip, List<IPAddress> dns)
        {
            var r = new List<byte>(); var rnd = new Random();
            foreach (var h in Routers)
            {
                try
                {
                    var p = h.Split(':'); var a = DnsVpn.Resolve(p[0], ip, dns); if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    var id = new byte[20]; rnd.NextBytes(id); r.AddRange(id); r.AddRange(a.GetAddressBytes()); int port = int.Parse(p[1]); r.Add((byte)(port >> 8)); r.Add((byte)port);
                }
                catch { }
            }
            return r.ToArray();
        }
        public Task StopAsync() { return inner.StopAsync(); }
        public TimeSpan AnnounceInterval { get { return inner.AnnounceInterval; } }
        public bool Disposed { get { return inner.Disposed; } }
        public MonoTorrent.Dht.ITransferMonitor Monitor { get { return inner.Monitor; } }
        public TimeSpan MinimumAnnounceInterval { get { return inner.MinimumAnnounceInterval; } }
        public int NodeCount { get { return inner.NodeCount; } }
        public MonoTorrent.Dht.DhtState State { get { return inner.State; } }
        public void Dispose() { inner.Dispose(); }
    }
}
