// Hallow Torrents - IP blocklists (v1.1): PeerGuardian / P2P text ("name:1.2.3.0-1.2.3.255"), eMule ipfilter.dat
// ("001.002.003.000 - 001.002.003.255 , 000 , name"; access levels of 128 and up are allowed, like eMule), single addresses and CIDR,
// plain or packed as .gz / .zip. A list from a URL is downloaded only over the VPN (VpnFetch: a socket bound to the VPN address) and
// refreshed every N days; it's checked before it replaces the old copy.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using MonoTorrent.Client;

namespace HallowTorrents
{
    public static class Blocklist
    {
        public static List<AddressRange> Load(string file) { using (var f = File.OpenRead(file)) return Parse(f); }

        // a list file in any of the supported forms (packed or not)
        public static List<AddressRange> Parse(Stream s)
        {
            var ms = new MemoryStream(); s.CopyTo(ms); var b = ms.ToArray();
            if (b.Length >= 2 && b[0] == 0x1F && b[1] == 0x8B) using (var g = new GZipStream(new MemoryStream(b), CompressionMode.Decompress)) return ParseText(new StreamReader(g));
            if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 3 && b[3] == 4)
                using (var z = new ZipArchive(new MemoryStream(b), ZipArchiveMode.Read))
                {
                    var e = z.Entries.Where(x => x.Length > 0).OrderByDescending(x => x.Length).FirstOrDefault();
                    if (e == null) throw new InvalidDataException("the .zip is empty");
                    using (var es = e.Open()) return ParseText(new StreamReader(es));
                }
            if (b.Length >= 4 && b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF) throw new InvalidDataException("7-Zip files aren't supported - use a plain, .gz or .zip list");
            return ParseText(new StreamReader(new MemoryStream(b)));
        }
        public static List<AddressRange> ParseText(TextReader r)
        {
            var list = new List<AddressRange>(); string line;
            while ((line = r.ReadLine()) != null) { uint a, z; if (ParseLine(line, out a, out z)) list.Add(new AddressRange(Ip(a), Ip(z))); }
            return list;
        }

        static readonly Regex rangeRx = new Regex(@"(?<a>\d{1,3}(?:\.\d{1,3}){3})\s*-\s*(?<b>\d{1,3}(?:\.\d{1,3}){3})");
        static readonly Regex cidrRx = new Regex(@"^\s*(?<a>\d{1,3}(?:\.\d{1,3}){3})(?:/(?<n>\d{1,2}))?\s*(?:[#;].*)?$");
        static readonly Regex levelRx = new Regex(@"^\s*,\s*(?<l>\d+)");
        // one line -> a range (start / end as numbers); false for comments, allowed eMule entries and anything else
        public static bool ParseLine(string line, out uint start, out uint end)
        {
            start = end = 0;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.Trim(); if (t.StartsWith("#") || t.StartsWith("//") || t.StartsWith(";")) return false;
            var m = rangeRx.Match(t);
            if (m.Success)
            {
                if (!Num(m.Groups["a"].Value, out start) || !Num(m.Groups["b"].Value, out end)) return false;
                var lv = levelRx.Match(t.Substring(m.Index + m.Length));
                if (lv.Success) { int level; if (int.TryParse(lv.Groups["l"].Value, out level) && level >= 128) return false; }   // eMule: 128+ = allowed
                if (start > end) { var x = start; start = end; end = x; }
                return true;
            }
            var c = cidrRx.Match(t);
            if (c.Success)
            {
                if (!Num(c.Groups["a"].Value, out start)) return false;
                int n = c.Groups["n"].Success ? int.Parse(c.Groups["n"].Value) : 32; if (n > 32) return false;
                uint mask = n == 0 ? 0 : uint.MaxValue << (32 - n); start &= mask; end = start | ~mask;
                return true;
            }
            return false;
        }
        // "001.002.003.004" -> a number (each part decimal, leading zeros allowed - never read as octal)
        static bool Num(string ip, out uint v)
        {
            v = 0; var p = ip.Split('.'); if (p.Length != 4) return false;
            foreach (var q in p) { int x; if (!int.TryParse(q, out x) || x < 0 || x > 255) return false; v = v << 8 | (uint)x; }
            return true;
        }
        public static IPAddress Ip(uint v) { return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }); }

        // downloads the list at url over the VPN into dest (only if it has at least one range); returns null or what went wrong
        public static string Update(string url, IPAddress vpnIp, List<IPAddress> dns, string dest, out int ranges)
        {
            ranges = 0;
            try
            {
                var data = VpnFetch.Get(url, vpnIp, dns, 64 * 1024 * 1024, 120);
                using (var ms = new MemoryStream(data)) ranges = Parse(ms).Count;
                if (ranges == 0) return "No address ranges found in that list (P2P / eMule .dat, plain, .gz or .zip).";
                Directory.CreateDirectory(Path.GetDirectoryName(dest)); var tmp = dest + ".tmp"; File.WriteAllBytes(tmp, data);
                if (File.Exists(dest)) File.Replace(tmp, dest, null); else File.Move(tmp, dest);
                return null;
            }
            catch (Exception e) { return VpnFetch.Why(e); }
        }
    }

    // GET over the VPN: a fresh HttpClient whose sockets are bound to the VPN address and whose names are looked up over the VPN
    // (BoundHttp, the same as the trackers). With no VPN address there is no request at all. Used by RSS and the blocklist; the
    // later Torznab search (1.3) should use it too.
    public static class VpnFetch
    {
        public class LockedException : Exception { public LockedException() : base("The VPN isn't connected - nothing is fetched without it.") { } }
        public static byte[] Get(string url, IPAddress vpnIp, List<IPAddress> dns, int maxBytes, int timeoutSeconds = 30)
        {
            if (vpnIp == null) throw new LockedException();
            Uri u; if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) throw new IOException("Only http:// and https:// addresses can be fetched.");
            using (var c = BoundHttp.Create(vpnIp, dns ?? new List<IPAddress>()))
            {
                c.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HallowTorrents/" + Program.Version);
                using (var r = c.GetAsync(u, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    if (!r.IsSuccessStatusCode) throw new IOException("The server answered " + (int)r.StatusCode + " " + r.ReasonPhrase + ".");
                    using (var s = r.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    {
                        var ms = new MemoryStream(); var buf = new byte[81920]; int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > maxBytes) throw new IOException("It's too big."); }
                        return ms.ToArray();
                    }
                }
            }
        }
        // v1.3 (search): one request over the VPN that does NOT follow redirects - the caller decides (an indexer's download link may
        // redirect to a magnet link). Same rules as Get: no VPN address = no request at all; the socket is bound to it; DNS over it.
        public class Reply { public byte[] Body; public string Location; public int Status; }
        public static Reply Fetch(string url, IPAddress vpnIp, List<IPAddress> dns, int maxBytes, TimeSpan timeout)
        {
            if (vpnIp == null) throw new LockedException();
            Uri u; if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) throw new IOException("Only http:// and https:// addresses can be fetched.");
            using (var c = BoundHttp.Create(vpnIp, dns ?? new List<IPAddress>(), false))
            {
                c.Timeout = timeout;
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HallowTorrents/" + Program.Version);
                using (var r = c.GetAsync(u, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    int code = (int)r.StatusCode;
                    if (code >= 300 && code < 400 && r.Headers.Location != null)
                    {
                        var loc = r.Headers.Location; var abs = loc.IsAbsoluteUri ? loc.OriginalString : new Uri(u, loc).ToString();
                        return new Reply { Status = code, Location = abs };
                    }
                    if (!r.IsSuccessStatusCode) throw new IOException("The server answered " + code + " " + r.ReasonPhrase + ".");
                    var read = r.Content.ReadAsStreamAsync();
                    if (!read.Wait(timeout)) throw new System.Threading.Tasks.TaskCanceledException();
                    using (var s = read.Result)
                    {
                        var ms = new MemoryStream(); var buf = new byte[81920]; int n; var until = DateTime.UtcNow + timeout;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > maxBytes) throw new IOException("It's too big."); if (DateTime.UtcNow > until) throw new System.Threading.Tasks.TaskCanceledException(); }
                        return new Reply { Status = code, Body = ms.ToArray() };
                    }
                }
            }
        }
        public static string Why(Exception e)
        {
            var b = e is AggregateException ? e.GetBaseException() : e;
            if (b is LockedException) return b.Message;
            if (b is System.Threading.Tasks.TaskCanceledException) return "It took too long.";
            var inner = b; while (inner.InnerException != null && !(inner is IOException)) inner = inner.InnerException;
            return Family.OneLine(inner.Message);
        }
    }
}
