// Hallow Torrents 1.4 - the local-server exception for indexers. Prowlarr / Jackett often run on this PC or on your own NAS. A request
// to one of YOUR indexer managers may skip the VPN binding only when:
//   * its address (scheme + host + port) is one of the indexer base addresses you added (Search > Indexers) - never a tracker, a peer,
//     an RSS feed, a blocklist or a .torrent on a public host;
//   * the host is resolved first and EVERY address it resolves to is private: loopback (127.0.0.0/8, ::1), RFC 1918 (10/8, 172.16/12,
//     192.168/16), Tailscale's CGNAT range (100.64.0.0/10) or IPv6 link-local (fe80::/10). One public address = no exception;
//   * the connection goes to exactly the addresses that were checked (pinned - a second lookup can't swap in a public address, so DNS
//     rebinding doesn't work), and every redirect is decided again: a redirect to a public address goes back through the VPN-bound
//     socket, or is refused while the VPN is off.
// Your ISP never sees that traffic (it stays on this PC / your home network / Tailscale); the indexer manager does the public fetching.
// Everything else - and any indexer on a public host - still goes out over the VPN only, exactly as in 1.3.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace HallowTorrents
{
    public static class PrivateNet
    {
        // tests: decide an address differently (true = private, false = public, null = the normal rule). The 1.3 suites set loopback to
        // "public" so their Torznab stand-in on 127.0.0.1 keeps playing a public indexer that must be reached over the VPN.
        // A test copy of the app gets the same with HALLOW_TORRENTS_LOOPBACK_PUBLIC=1 (only together with HALLOW_TORRENTS_TESTDIR). Read on
        // first use - never in a static initializer, so it can't run before a test has set its environment.
        public static Func<IPAddress, bool?> Override;
        static int envRead; static bool envLoopbackPublic;
        static bool EnvLoopbackPublic
        {
            get
            {
                if (envRead == 0) { envLoopbackPublic = TorSettings.TestDir != null && Environment.GetEnvironmentVariable("HALLOW_TORRENTS_LOOPBACK_PUBLIC") == "1"; envRead = 1; }
                return envLoopbackPublic;
            }
        }

        public static bool IsPrivate(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            var o = Override; if (o != null) { var v = o(a); if (v.HasValue) return v.Value; }
            else if (EnvLoopbackPublic && IPAddress.IsLoopback(a)) return false;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return b[0] == 127 || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.Equals(IPAddress.IPv6Loopback)) return true;
                var b = a.GetAddressBytes(); return b[0] == 0xFE && (b[1] & 0xC0) == 0x80;   // fe80::/10
            }
            return false;
        }
        public static bool AllPrivate(IList<IPAddress> list) { return list != null && list.Count > 0 && list.All(IsPrivate); }

        // names that only exist on your own network: one label ("nas"), .local, .lan, .home, .home.arpa, .internal, .localdomain and
        // Tailscale's MagicDNS (.ts.net). These are looked up with Windows' resolver (they never reach a public DNS server by design);
        // any other name is looked up over the VPN, like the trackers - and while the VPN is off it isn't looked up at all.
        static readonly string[] localSuffixes = { ".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".ts.net" };
        public static bool LocalName(string host)
        {
            host = (host ?? "").TrimEnd('.').ToLowerInvariant();
            if (host.Length == 0) return false;
            if (host == "localhost" || host.EndsWith(".localhost")) return true;
            return !host.Contains('.') || localSuffixes.Any(s => host.EndsWith(s));
        }
        // without any lookup (pictures only): a private address literal or a home-network name
        public static bool LooksLocal(string url)
        {
            Uri u; if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u)) return false;
            IPAddress ip; return IPAddress.TryParse(u.Host.Trim('[', ']'), out ip) ? IsPrivate(ip) : LocalName(u.Host);
        }
        public static Func<string, IPAddress[]> SystemResolve = h => Dns.GetHostAddresses(h);   // tests swap it (rebinding, mixed answers)

        // every address a host resolves to (null = it can't be looked up without the VPN, or the lookup failed)
        public static IPAddress[] Resolve(string host, IPAddress vpnIp, List<IPAddress> vpnDns)
        {
            host = (host ?? "").Trim('[', ']');
            IPAddress lit; if (IPAddress.TryParse(host, out lit)) return new[] { lit };
            var h = host.TrimEnd('.').ToLowerInvariant();
            if (h == "localhost" || h.EndsWith(".localhost")) return new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };
            try
            {
                if (LocalName(h)) return SystemResolve(h);
                if (vpnIp == null) return null;
                return DnsVpn.ResolveAll(h, vpnIp, vpnDns).ToArray();
            }
            catch { return null; }
        }
    }

    // the decision for one URL (pure apart from the lookup, tested)
    public class IndexerRoute
    {
        public bool Local;                 // true = it may skip the VPN binding, connecting only to Addresses
        public IPAddress[] Addresses;
        public string Why;                 // for tests / logs
        public const string Label = "Local server — not through VPN (private address)";

        public static bool SameOrigin(Uri a, Uri b)
        {
            return a != null && b != null && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Host.Trim('[', ']'), b.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
        }
        // bases: the indexer base addresses you added (enabled ones)
        public static IndexerRoute Decide(string url, IEnumerable<string> bases, IPAddress vpnIp, List<IPAddress> vpnDns)
        {
            Uri u; if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return new IndexerRoute { Why = "not http(s)" };
            if (!(bases ?? new string[0]).Any(b => { Uri bu; return Uri.TryCreate((b ?? "").Trim(), UriKind.Absolute, out bu) && SameOrigin(u, bu); })) return new IndexerRoute { Why = "not one of your indexer addresses" };
            var addrs = PrivateNet.Resolve(u.Host, vpnIp, vpnDns);
            if (addrs == null || addrs.Length == 0) return new IndexerRoute { Why = "couldn't be looked up" };
            if (!PrivateNet.AllPrivate(addrs)) return new IndexerRoute { Addresses = addrs, Why = "resolves to a public address (" + string.Join(", ", addrs.Select(a => a.ToString())) + ")" };
            return new IndexerRoute { Local = true, Addresses = addrs, Why = "private: " + string.Join(", ", addrs.Select(a => a.ToString())) };
        }
    }

    public static class LocalFetch
    {
        // one request straight to a private address that was just checked (no VPN binding, no redirects followed, no proxy).
        // The connection only ever goes to `pinned` - each address is checked again right before connecting.
        public static VpnFetch.Reply Fetch(Uri u, IPAddress[] pinned, int maxBytes, TimeSpan timeout)
        {
            if (!PrivateNet.AllPrivate(pinned)) throw new IOException("Not a private address - refused.");
            var h = new StandardSocketsHttpHandler { UseProxy = false, Proxy = null, ConnectTimeout = TimeSpan.FromSeconds(10), AllowAutoRedirect = false };
            h.ConnectCallback = (ctx, token) => Task.Run(() =>
            {
                if (!string.Equals(ctx.DnsEndPoint.Host.Trim('[', ']'), u.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected host - refused.");
                Exception last = null;
                foreach (var a in pinned)
                {
                    if (!PrivateNet.IsPrivate(a)) throw new IOException("Not a private address - refused.");
                    var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        using (token.Register(() => { try { s.Dispose(); } catch { } }))
                        {
                            var ar = s.BeginConnect(new IPEndPoint(a, ctx.DnsEndPoint.Port), null, null);
                            if (!ar.AsyncWaitHandle.WaitOne(10000)) throw new TimeoutException("connect timed out");
                            s.EndConnect(ar);
                        }
                        Net.Note("local", s.LocalEndPoint);
                        return (Stream)new NetworkStream(s, true);
                    }
                    catch (Exception e) { last = e; try { s.Dispose(); } catch { } }
                }
                throw last ?? new IOException("No address to connect to.");
            }, token);
            using (var c = new HttpClient(h) { Timeout = timeout })
            {
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HallowTorrents/" + Program.Version);
                using (var r = c.GetAsync(u, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    int code = (int)r.StatusCode;
                    if (code >= 300 && code < 400 && r.Headers.Location != null)
                    {
                        var loc = r.Headers.Location; return new VpnFetch.Reply { Status = code, Location = loc.IsAbsoluteUri ? loc.OriginalString : new Uri(u, loc).ToString() };
                    }
                    if (!r.IsSuccessStatusCode) throw new IOException("The server answered " + code + " " + r.ReasonPhrase + ".");
                    var read = r.Content.ReadAsStreamAsync(); if (!read.Wait(timeout)) throw new TaskCanceledException();
                    using (var s = read.Result)
                    {
                        var ms = new MemoryStream(); var buf = new byte[81920]; int n; var until = DateTime.UtcNow + timeout;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > maxBytes) throw new IOException("It's too big."); if (DateTime.UtcNow > until) throw new TaskCanceledException(); }
                        return new VpnFetch.Reply { Status = code, Body = ms.ToArray() };
                    }
                }
            }
        }
    }

    // one hop to an indexer: local (pinned, unbound) when the exception applies, otherwise over the VPN only (1.3's rule). Redirects
    // aren't followed here - the caller calls again with the new address, so every hop is decided again.
    public static class IndexerNet
    {
        public static VpnFetch.Reply Fetch(string url, TimeSpan timeout, IEnumerable<string> bases, IPAddress vpnIp, List<IPAddress> vpnDns, int maxBytes = 20 * 1024 * 1024)
        {
            var route = IndexerRoute.Decide(url, bases, vpnIp, vpnDns);
            if (route.Local) return LocalFetch.Fetch(new Uri(url.Trim()), route.Addresses, maxBytes, timeout);
            if (vpnIp == null) throw new VpnFetch.LockedException();
            return VpnFetch.Fetch(url, vpnIp, vpnDns, maxBytes, timeout);
        }
        // follows up to `redirects` redirects (each hop decided again); stops at a magnet link
        public static VpnFetch.Reply Get(string url, TimeSpan timeout, IEnumerable<string> bases, IPAddress vpnIp, List<IPAddress> vpnDns, int redirects = 3)
        {
            for (int i = 0; ; i++)
            {
                var r = Fetch(url, timeout, bases, vpnIp, vpnDns);
                if (r.Location == null || r.Location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || i >= redirects) return r;
                url = r.Location;
            }
        }
    }
}
