// Hallow Torrents 1.9 tests: VPN on demand. Auto-connect's decisions and back-off (a made-up clock), the game hold, the UAC rule,
// "P2P servers only" (it can only add a stop), a non-Hallow adapter, and a test copy of the app and of the headless exe driven over
// the pipe with a FAKE Hallow VPN: its status.json in the test folder and a stand-in pipe whose name ends in ".test-<folder>", so
// the real Hallow VPN is never sent anything. Nothing leaves this PC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace HallowTorrents.Tests
{
    // a stand-in for Hallow VPN's pipe (test pipe names only)
    public class FakeVpnPipe : IDisposable
    {
        public readonly string Name; public Func<string, string> Handle; volatile bool stop; readonly List<string> lines = new List<string>();
        public FakeVpnPipe(string testDir, Func<string, string> handle)
        {
            Name = "HallowFamily.VPN." + Environment.UserName + ".test-" + Path.GetFileName(testDir.TrimEnd('\\'));
            if (!Name.Contains(".test-")) throw new InvalidOperationException("not a test pipe");
            Handle = handle;
            new Thread(Loop) { IsBackground = true, Name = "fake vpn pipe" }.Start();
            for (int i = 0; i < 40 && !Up; i++) Thread.Sleep(25);
        }
        public bool Up;
        public List<string> Lines { get { lock (lines) return lines.ToList(); } }
        public int Connects { get { return Lines.Count(l => l.StartsWith("connect")); } }
        void Loop()
        {
            while (!stop)
            {
                NamedPipeServerStream p = null;
                try
                {
                    p = new NamedPipeServerStream(Name, PipeDirection.InOut, 8); Up = true;
                    p.WaitForConnection(); if (stop) { p.Dispose(); break; }
                    var pp = p; p = null;
                    new Thread(() => Serve(pp)) { IsBackground = true }.Start();
                }
                catch { if (p != null) try { p.Dispose(); } catch { } Thread.Sleep(30); }
            }
        }
        void Serve(NamedPipeServerStream p)
        {
            try
            {
                var buf = new List<byte>(); var one = new byte[1];
                while (buf.Count < 65536) { int n = p.Read(one, 0, 1); if (n <= 0 || one[0] == (byte)'\n') break; buf.Add(one[0]); }
                var line = Encoding.UTF8.GetString(buf.ToArray()).Trim();
                if (line.Length == 0) return;
                lock (lines) lines.Add(line);
                var r = Handle(line) ?? "error unknown command";
                var b = new UTF8Encoding(false).GetBytes(r + "\n"); p.Write(b, 0, b.Length); p.Flush();
                try { p.WaitForPipeDrain(); } catch { }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        public void Dispose()
        {
            stop = true;
            try { using (var c = new NamedPipeClientStream(".", Name, PipeDirection.InOut)) c.Connect(300); } catch { }
            Up = false;
        }
    }

    public static partial class Run
    {
        static Dictionary<string, object> Hv(bool connected, bool? p2p = null, bool? uacConnect = false, bool? killSwitch = null, bool connecting = false)
        {
            var d = new Dictionary<string, object> { { "app", "VPN" }, { "running", true }, { "connected", connected }, { "connecting", connecting }, { "server", "nl123" } };
            if (p2p != null) d["p2p"] = p2p.Value;
            if (uacConnect != null) d["needsUac"] = new Dictionary<string, object> { { "connect", uacConnect.Value }, { "disconnect", false } };
            if (killSwitch != null) d["killSwitch"] = killSwitch.Value;
            return d;
        }
        static void WriteVpn19(string dir, bool connected, bool? p2p = null, bool? uacConnect = false, bool? killSwitch = false, bool connecting = false)
        {
            var d = Path.Combine(dir, "HallowVPN"); Directory.CreateDirectory(d);
            var json = Family.Json(Hv(connected, p2p, uacConnect, killSwitch, connecting));
            File.WriteAllText(Path.Combine(d, "status.json.tmp"), json);
            File.Copy(Path.Combine(d, "status.json.tmp"), Path.Combine(d, "status.json"), true); File.Delete(Path.Combine(d, "status.json.tmp"));
        }

        // ---------------- pure: the decisions, the back-off timing, the game hold, the UAC rule, P2P-only gating
        static void V19Unit()
        {
            var asked = new List<string>(); string reply = "ok connecting";
            var a = new VpnAuto { Ask = l => { asked.Add(l); return reply; } };
            var t0 = new DateTime(2026, 11, 23, 12, 0, 0);
            Func<VpnAuto.Inputs> inp = () => new VpnAuto.Inputs { On = true, Source = "hallow", Work = true, HallowRunning = true, Hallow = Hv(false) };

            // nothing to run / off / own VPN app / already connected: never asks
            var i = inp(); i.Work = false; a.Step(t0, i); Ok(asked.Count == 0 && a.State == "idle", "nothing to run: doesn't ask (" + a.State + ")");
            i = inp(); i.On = false; a.Step(t0, i); Ok(asked.Count == 0 && a.State == "off", "the setting off: doesn't ask");
            i = inp(); i.Source = "adapter"; a.Step(t0, i); Ok(asked.Count == 0 && a.State == "off", "your own VPN app's adapter: doesn't ask Hallow VPN");
            i = inp(); i.VpnOk = true; a.Step(t0, i); Ok(asked.Count == 0 && a.State == "connected", "the VPN is already up: doesn't ask");
            i = inp(); i.Drill = true; a.Step(t0, i); Ok(asked.Count == 0, "the leak drill is running (it disconnects on purpose): doesn't ask");
            i = inp(); i.HallowRunning = false; a.Step(t0, i); Ok(asked.Count == 0 && a.State == "not-running" && a.Text.Contains("isn't running"), "Hallow VPN not running: says so, doesn't ask");
            i = inp(); i.Hallow = Hv(false, null, true); a.Step(t0, i);
            Ok(asked.Count == 0 && a.State == "uac" && a.Text == "Connect the VPN from Hallow VPN (it needs Windows' permission for NordLynx).", "needsUac.connect = true (NordLynx next): doesn't ask, says to connect from Hallow VPN: " + a.Text);
            i = inp(); i.Hallow = Hv(false, null, null); a.Step(t0, i); Ok(asked.Count == 0 && a.State == "uac" && a.Text.Contains("1.2.6"), "no needsUac (Hallow VPN older than 1.2.6): doesn't ask");
            i = inp(); i.Hallow = Hv(false, null, false, null, true); a.Step(t0, i); Ok(asked.Count == 0 && a.State == "connecting", "Hallow VPN is already connecting: doesn't ask again");
            i = inp(); i.Game = "Delta Force"; a.Step(t0, i); Ok(asked.Count == 0 && a.State == "game" && a.Text.StartsWith("Delta Force is running"), "a game is running: doesn't ask (" + a.Text + ")");
            i = inp(); i.Game = "Delta Force"; i.InGames = true; a.Step(t0, i); Ok(asked.Count == 1 && asked[0] == "connect p2p --once", "...unless \"also during games\" is ticked: connect p2p --once");
            a.Reset(); asked.Clear();

            // the back-off: now, +30 s, +2 min, +5 min, then stop until you act
            reply = "ok connecting";
            a.Step(t0, inp()); Ok(asked.Count == 1 && asked[0] == "connect p2p --once" && a.Tries == 1 && a.State == "asked", "try 1 at once: \"connect p2p --once\" (keeps your remembered server)");
            a.Step(t0.AddSeconds(1), inp()); a.Step(t0.AddSeconds(29), inp()); Ok(asked.Count == 1 && a.State == "waiting" && a.NextTry == t0.AddSeconds(30), "no second try before 30 s (" + a.State + ", next " + a.NextTry + ")");
            Ok(a.Text.Contains("12:00:30") && a.Text.Contains("try 2 of 4"), "the banner says when: " + a.Text);
            a.Step(t0.AddSeconds(30), inp()); Ok(asked.Count == 2 && a.Tries == 2, "try 2 at 30 s");
            a.Step(t0.AddSeconds(30 + 119), inp()); Ok(asked.Count == 2, "no try 3 before 2 min more");
            a.Step(t0.AddSeconds(30 + 120), inp()); Ok(asked.Count == 3 && a.Tries == 3, "try 3 at +2 min");
            var t3 = t0.AddSeconds(150);
            a.Step(t3.AddSeconds(299), inp()); Ok(asked.Count == 3, "no try 4 before 5 min more");
            a.Step(t3.AddSeconds(300), inp()); Ok(asked.Count == 4 && a.Tries == 4 && a.NextTry == null, "try 4 at +5 min (the last)");
            var t4 = t3.AddSeconds(300);
            a.Step(t4.AddSeconds(30), inp()); Ok(asked.Count == 4 && a.State != "stopped", "...a minute to connect after the last try");
            a.Step(t4.AddSeconds(61), inp()); a.Step(t4.AddHours(5), inp());
            Ok(asked.Count == 4 && a.State == "stopped" && a.Text.Contains("stopped asking"), "then it stops until you act - even hours later: " + a.Text);
            a.Reset(); a.Step(t4.AddHours(5).AddSeconds(1), inp()); Ok(asked.Count == 5 && a.Tries == 1, "you act (Connect / Retry / the setting): try 1 again at once");
            i = inp(); i.VpnOk = true; a.Step(t4.AddHours(6), i); Ok(a.Tries == 0 && a.State == "connected", "connected: the count starts again");
            a.Step(t4.AddHours(7), inp()); Ok(asked.Count == 6 && a.Tries == 1, "the VPN dropped later: asked again at once (a fresh round)");

            // an error / no answer counts as a try, and is shown
            var b = new VpnAuto { Ask = l => "error not signed in" }; b.Step(t0, inp()); b.Step(t0.AddSeconds(2), inp());
            Ok(b.Tries == 1 && b.State == "waiting" && b.Text.Contains("not signed in"), "Hallow VPN's error is shown in the banner: " + b.Text);
            var c2 = new VpnAuto { Ask = l => null }; c2.Step(t0, inp()); Ok(c2.Tries == 1 && c2.LastReply == "error no answer", "no answer: counted as a try");
            var c3 = new VpnAuto { Ask = l => { throw new IOException("pipe broke"); } }; c3.Step(t0, inp()); Ok(c3.Tries == 1 && c3.LastReply.Contains("pipe broke"), "an exception from the pipe: counted, never thrown");

            // the UAC rule also for "Switch to a P2P server"
            asked.Clear(); var sw = new VpnAuto { Ask = l => { asked.Add(l); return "ok connecting"; } };
            Ok(sw.SwitchP2p(Hv(true, false, true), true) == VpnAuto.UacText && asked.Count == 0, "Switch to a P2P server with NordLynx next: the UAC sentence, nothing sent");
            Ok(sw.SwitchP2p(Hv(true, false, false), false).Contains("isn't running") && asked.Count == 0, "...Hallow VPN not running: says so");
            Ok(sw.SwitchP2p(Hv(true, false, false), true) == null && asked.Count == 1 && asked[0] == "connect p2p --once", "...IKEv2 next: connect p2p --once");

            // P2P servers only: can only add a stop
            var ok = new VpnState { Ok = true, Ip = IPAddress.Parse("10.5.0.2"), Adapter = "Hallow VPN", Server = "us5838", HallowConnected = true };
            Ok(Net.P2pGate(ok, true, Hv(true, true)).Ok, "P2P-only + a P2P server: runs");
            var held = Net.P2pGate(ok, true, Hv(true, false));
            Ok(!held.Ok && held.NotP2p && held.Message.Contains("us5838") && held.Message.Contains("isn't a P2P server") && held.Key.StartsWith("off|"), "P2P-only + a non-P2P server: stopped (" + held.Message + ")");
            Ok(!Net.P2pGate(ok, true, Hv(true, null)).Ok && !Net.P2pGate(ok, true, null).Ok, "P2P-only + no p2p field / no status: stopped (anything but p2p:true holds)");
            Ok(Net.P2pGate(ok, false, Hv(true, false)).Ok, "P2P-only off: a non-P2P server runs, as before");
            var off = new VpnState { Ok = false, Message = "Hallow VPN isn't connected." };
            Ok(!Net.P2pGate(off, true, Hv(true, true)).Ok && !Net.P2pGate(off, false, Hv(true, true)).Ok && Net.P2pGate(off, true, Hv(true, true)).Message == off.Message, "never weakens the lock: a stopped VPN stays stopped whatever p2p says");
            Ok(Net.P2pGate(null, true, null) == null, "null in, null out");

            // a non-Hallow adapter: P2P-only can't be checked = off
            var s = new TorSettings(); s.Fix(); s.P2pOnly = true; s.VpnSource = "adapter";
            Ok(!VpnAuto.P2pOnly(s), "your own VPN app's adapter: P2P servers only counts as off");
            var nord = new List<AdapterInfo> { new AdapterInfo { Id = "nl", Name = "NordLynx", Description = "NordLynx Tunnel", Up = true, IPv4 = { IPAddress.Parse("10.5.0.2") } } };
            var dec = Net.P2pGate(Net.Decide("adapter", "nl", null, null, nord), VpnAuto.P2pOnly(s), null);
            Ok(dec.Ok && dec.Adapter == "NordLynx", "...and the NordVPN app's adapter still runs torrents (the lock is unchanged)");
            var pj = VpnAuto.P2pJson(s, dec, null); Ok((bool)pj["on"] && !(bool)pj["applies"] && pj["note"] != null, "p2pOnly status: on, applies false, a note");
            s.VpnSource = "hallow"; Ok(VpnAuto.P2pOnly(s), "with Hallow VPN it applies");

            // the settings' defaults: on when Hallow VPN is installed (test folder: its data folder), P2P-only and games off
            var fresh = new TorSettings(); fresh.Fix();
            Ok(fresh.VpnAuto == null && !fresh.P2pOnly && !fresh.VpnAutoInGames && !fresh.KillTipDismissed, "a new install: auto unset (follows Hallow VPN), P2P-only off, games off, tip not hidden");
            var vd = Path.Combine(Dir, "HallowVPN"); var had = Directory.Exists(vd); if (had) Directory.Delete(vd, true);
            Ok(!VpnAuto.On(fresh), "no Hallow VPN installed: auto-connect off by default");
            Directory.CreateDirectory(vd); Ok(VpnAuto.On(fresh), "Hallow VPN installed: on by default");
            fresh.VpnAuto = false; Ok(!VpnAuto.On(fresh), "you turned it off: off");
            Directory.Delete(vd, true);

            // the pipe commands
            var ps = new TorSettings(); ps.Fix(); var pa = new VpnAuto { Ask = l => "ok connecting" }; int switched = 0;
            Func<string, string, string> pipe = (cmd, arg) => VpnAuto.Pipe(cmd, arg, ps, x => x(), pa, () => { switched++; return null; }, ok, Hv(true, false));
            Ok(pipe("vpn-auto", "off").StartsWith("ok {\"on\":false") && ps.VpnAuto == false, "vpn-auto off");
            Ok(pipe("vpn-auto", "on").StartsWith("ok {\"on\":true") && ps.VpnAuto == true, "vpn-auto on");
            Ok(pipe("vpn-auto", "games on").Contains("\"inGames\":true") && ps.VpnAutoInGames, "vpn-auto games on");
            Ok(pipe("vpn-auto", "").Contains("\"state\":") && pipe("vpn-auto", "status").Contains("\"maxTries\":4"), "vpn-auto / vpn-auto status");
            Ok(pipe("vpn-auto", "sideways").StartsWith("error vpn-auto ["), "vpn-auto <junk>: usage");
            Ok(pipe("p2p-only", "on").Contains("\"on\":true,\"applies\":true") && ps.P2pOnly, "p2p-only on (Hallow VPN)");
            Ok(pipe("p2p-only", "off").Contains("\"on\":false") && !ps.P2pOnly, "p2p-only off");
            ps.VpnSource = "adapter"; var po = pipe("p2p-only", "on"); Ok(po.StartsWith("error ") && po.Contains("needs Hallow VPN") && !ps.P2pOnly, "p2p-only on with your own VPN app: refused, says why");
            Ok(pipe("switch-p2p", "") == "ok" && switched == 1, "switch-p2p");
            Ok(pipe("status", "") == null && pipe("ping", "") == null, "other commands aren't taken");

            // the kill switch flag the tip reads
            Ok(VpnAuto.Flag(Hv(true, true, false, false), "killSwitch") == false && VpnAuto.Flag(Hv(true, true, false, true), "killSwitch") == true && VpnAuto.Flag(Hv(true), "killSwitch") == null, "killSwitch read from Hallow VPN's status (false = the tip shows)");
            Ok(Family.PipeName("VPN").Contains(".test-"), "in tests Hallow VPN's pipe name ends in .test-... (never the real one): " + Family.PipeName("VPN"));
        }

        // ---------------- in-process: the VPN watch with P2P-only, and auto-connect against a stand-in pipe (test name)
        static void Vpn19()
        {
            var s = new TorSettings(); s.Fix();
            var adapters = Path.Combine(Dir, "fake-adapters19.txt");
            File.WriteAllText(adapters, "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nhv|Hallow VPN|Hallow VPN|up|127.0.0.5|127.0.0.1\n");
            var w = new VpnWatch(() => s, new FakeNetworks(adapters), false);
            WriteVpn19(Dir, true, false);
            var st = w.CheckNow(); Ok(st.Ok && w.Hallow != null, "the watch: connected, P2P-only off: runs on a non-P2P server (as before)");
            s.P2pOnly = true; st = w.CheckNow(); Ok(!st.Ok && st.NotP2p, "P2P-only on, p2p:false: the watch says stopped (" + st.Message + ")");
            WriteVpn19(Dir, true, true); st = w.CheckNow(); Ok(st.Ok && st.Ip.ToString() == "127.0.0.5", "p2p:true: runs, bound to the VPN adapter");
            WriteVpn19(Dir, false, true); st = w.CheckNow(); Ok(!st.Ok && !st.NotP2p, "disconnected: stopped (the requirement, not P2P)");
            s.VpnSource = "adapter"; s.VpnAdapter = "hv"; WriteVpn19(Dir, true, false); st = w.CheckNow(); Ok(st.Ok, "own VPN app's adapter with P2P-only ticked: treated as off, runs");
            s.VpnSource = "hallow"; s.P2pOnly = false;

            // the default Ask goes over Family.Send("VPN") - in tests that's the .test pipe; the stand-in answers it
            using (var fp = new FakeVpnPipe(Dir, l => l.StartsWith("connect") ? "ok connecting" : "error unknown command"))
            {
                Ok(fp.Name == Family.PipeName("VPN"), "the stand-in uses the test pipe name " + fp.Name);
                Ok(Family.Running("VPN"), "Family.Running sees the stand-in");
                var a = new VpnAuto(); WriteVpn19(Dir, false, null, false);
                var w2 = new VpnWatch(() => s, new FakeNetworks(adapters), false); var now = w2.CheckNow();
                a.Step(DateTime.Now, new VpnAuto.Inputs { On = true, Source = "hallow", Work = true, VpnOk = now.Ok, Hallow = w2.Hallow, HallowRunning = w2.HallowRunning });
                Ok(fp.Lines.SequenceEqual(new[] { "connect p2p --once" }) && a.LastReply == "ok connecting", "auto-connect over the pipe: the stand-in got exactly \"connect p2p --once\" (" + string.Join(" | ", fp.Lines) + ")");
            }
            for (int i = 0; i < 20 && Family.Running("VPN"); i++) Thread.Sleep(50);
            var b = new VpnAuto(); b.Step(DateTime.Now, new VpnAuto.Inputs { On = true, Source = "hallow", Work = true, Hallow = Hv(false), HallowRunning = Family.Running("VPN") });
            Ok(b.State == "not-running" && b.Tries == 0, "the stand-in gone: not running, nothing tried");
            try { File.Delete(adapters); Directory.Delete(Path.Combine(Dir, "HallowVPN"), true); } catch { }
        }

        static Dictionary<string, object> J(string r) { return r != null && r.StartsWith("ok {") ? new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.Substring(3)) : new Dictionary<string, object>(); }
        static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>(); }
        static string AutoState(string exe, string dir) { var a = Sub(J(Cmd(exe, dir, "status")), "vpnAuto"); object v; return a.TryGetValue("state", out v) ? v as string : null; }
        static bool Until(Func<bool> f, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(200); } return f(); }

        // ---------------- a test copy of the app (no windows) with a fake Hallow VPN, then the headless exe
        static void AppCopy19()
        {
            var bin = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location));
            var exe = Path.Combine(bin, "HallowTorrents.exe"); var hexe = Path.Combine(bin, "HallowTorrentsHeadless.exe");
            var dir = Path.Combine(Dir, "app19"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true}");
            File.WriteAllText(Path.Combine(dir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.5|127.0.0.1\n");
            WriteVpn19(dir, false, null, false, false);
            // the fake Hallow VPN: "connect p2p --once" connects it to a P2P server - unless told to ignore
            bool ignore = false;
            var fp = new FakeVpnPipe(dir, l => { if (l.StartsWith("connect")) { if (!ignore) WriteVpn19(dir, true, true, false, false); return "ok connecting"; } return "error unknown command"; });
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_TORRENTS_HEADLESS"] = "1";
            psi.EnvironmentVariables["HALLOW_TORRENTS_LOOPBACK_PUBLIC"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r != null && r.Contains("\"version\":\"" + Program.Version + "\"") && new Version(Program.Version) >= new Version("1.9"), "test copy: ping " + r);
                var st = J(Cmd(exe, dir, "status"));
                Ok(st.ContainsKey("vpnAuto") && st.ContainsKey("p2pOnly") && (bool)st["vpnLocked"] && (bool)st["vpnRequired"], "status has vpnAuto and p2pOnly; locked, VPN required");
                Ok((bool)Sub(st, "vpnAuto")["on"], "auto-connect on by default (Hallow VPN is \"installed\" in the test folder)");
                Thread.Sleep(2500);
                Ok(fp.Connects == 0 && AutoState(exe, dir) == "idle", "nothing to run: Hallow VPN isn't asked (" + AutoState(exe, dir) + ")");

                var magnet = "magnet:?xt=urn:btih:1923456789abcdef0123456789abcdef01234567&dn=Auto+Magnet&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce";
                Ok(Cmd(exe, dir, "add " + magnet).StartsWith("ok {\"id\":"), "a torrent to run is added");
                Ok(Until(() => fp.Connects >= 1, 6000), "auto-connect: Hallow VPN asked within seconds: " + string.Join(" | ", fp.Lines));
                Ok(fp.Lines.Count > 0 && fp.Lines[0] == "connect p2p --once", "...with \"connect p2p --once\" (your remembered server is kept)");
                Ok(Until(() => Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"), 6000), "Hallow VPN (fake) connected: the engine is up, bound to the VPN adapter: " + Cmd(exe, dir, "test-vpn"));
                Ok(Until(() => AutoState(exe, dir) == "connected", 4000) && fp.Connects == 1, "state connected; asked once");

                // the VPN drops and doesn't come back: one try, then the 30 s wait
                ignore = true; WriteVpn19(dir, false, null, false, false);
                Ok(Until(() => fp.Connects >= 2, 6000), "the VPN dropped: asked again at once");
                Thread.Sleep(4000);
                var va = Sub(J(Cmd(exe, dir, "status")), "vpnAuto");
                Ok(fp.Connects == 2 && (int)va["tries"] == 1 && va["nextTry"] != null && ((string)va["state"] == "waiting" || (string)va["state"] == "asked"), "no answer: no second try within seconds; tries 1, next try set: " + Family.Json(va));
                Ok(Cmd(exe, dir, "test-vpn").Contains("\"engine\":false"), "...and the lock holds (engine stopped)");
                Ok(Cmd(exe, dir, "vpn-auto retry").StartsWith("ok {") && Until(() => fp.Connects >= 3, 5000), "vpn-auto retry (you acted): asked again at once");

                // NordLynx next: needs Windows' permission - never asked
                WriteVpn19(dir, false, null, true, false); Cmd(exe, dir, "vpn-auto retry"); var n0 = fp.Connects;
                Ok(Until(() => AutoState(exe, dir) == "uac", 5000) && fp.Connects == n0, "needsUac.connect = true: not asked, state uac");
                var msg = (string)Sub(J(Cmd(exe, dir, "status")), "vpnAuto")["message"];
                Ok(msg != null && msg.Contains("Connect the VPN from Hallow VPN") && msg.Contains("NordLynx"), "...the banner line: " + msg);

                // a game: held unless "also during games"
                WriteVpn19(dir, false, null, false, false); File.WriteAllText(Path.Combine(dir, "fake-game.txt"), "Delta Force"); Cmd(exe, dir, "test-game");
                Ok(Until(() => Cmd(exe, dir, "status").Contains("\"game\":\"Delta Force\""), 5000), "a game is running (fake)");
                Cmd(exe, dir, "vpn-auto retry"); n0 = fp.Connects;
                Ok(Until(() => AutoState(exe, dir) == "game", 5000) && fp.Connects == n0, "while a game runs: not asked (state game)");
                Thread.Sleep(2000); Ok(fp.Connects == n0, "...still not asked 2 s later");
                Ok(Cmd(exe, dir, "vpn-auto games on").Contains("\"inGames\":true") && Until(() => fp.Connects > n0, 5000), "\"also during games\" ticked: asked");
                Cmd(exe, dir, "vpn-auto games off"); File.Delete(Path.Combine(dir, "fake-game.txt")); Cmd(exe, dir, "test-game");

                // off: never asked
                Ok(Cmd(exe, dir, "vpn-auto off").StartsWith("ok {\"on\":false"), "vpn-auto off"); n0 = fp.Connects; Thread.Sleep(3000);
                Ok(fp.Connects == n0 && AutoState(exe, dir) == "off", "auto-connect off: never asked");

                // P2P servers only
                WriteVpn19(dir, true, false, false, false);
                Ok(Until(() => Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"), 6000), "connected to a non-P2P server, P2P-only off: runs (as before)");
                var po = Cmd(exe, dir, "p2p-only on"); Ok(po.Contains("\"on\":true,\"applies\":true"), "p2p-only on: " + po);
                Ok(Until(() => Cmd(exe, dir, "test-vpn").Contains("\"engine\":false"), 4000), "...on a non-P2P server the engine stops");
                var p2 = Sub(J(Cmd(exe, dir, "status")), "p2pOnly"); Ok((bool)p2["held"] && p2["serverP2p"] is bool && !(bool)p2["serverP2p"], "status p2pOnly: held, serverP2p false");
                var tv = Cmd(exe, dir, "test-vpn"); Ok(tv.Contains("a P2P server") && tv.Contains("P2P servers only") && tv.Contains("nl123"), "the lock's message says why: " + tv);
                Ok(Cmd(exe, dir, "menu").Contains("{\"id\":\"switch-p2p\",\"label\":\"Switch to a P2P server\",\"enabled\":true}"), "the menu offers Switch to a P2P server");
                WriteVpn19(dir, true, false, true, false); Thread.Sleep(600); n0 = fp.Connects;
                var sw = Cmd(exe, dir, "invoke switch-p2p"); Ok(sw.StartsWith("error Connect the VPN from Hallow VPN") && fp.Connects == n0, "Switch with NordLynx next: the UAC sentence, nothing sent: " + sw);
                ignore = false; WriteVpn19(dir, true, false, false, false); Thread.Sleep(600);
                Ok(Cmd(exe, dir, "switch-p2p") == "ok" && fp.Connects == n0 + 1 && fp.Lines.Last() == "connect p2p --once", "switch-p2p: connect p2p --once");
                Ok(Until(() => Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"), 6000), "...on a P2P server: runs again");
                Cmd(exe, dir, "p2p-only off");

                // a non-Hallow adapter (the NordVPN app): P2P-only can't be checked
                Cmd(exe, dir, "test-set vpnsource adapter"); Cmd(exe, dir, "test-set vpnadapter nl"); Cmd(exe, dir, "test-set p2ponly on");
                WriteVpn19(dir, false, false, false, false);
                Ok(Until(() => Cmd(exe, dir, "test-vpn").Contains("\"engine\":true"), 6000), "own VPN app's adapter with P2P-only saved on: treated as off, runs");
                var pj = Sub(J(Cmd(exe, dir, "status")), "p2pOnly"); Ok((bool)pj["on"] && !(bool)pj["applies"] && ((string)pj["note"]).Contains("needs Hallow VPN"), "status p2pOnly: applies false, says why");
                Ok(Cmd(exe, dir, "p2p-only on").StartsWith("error \"P2P servers only\" needs Hallow VPN"), "p2p-only on: refused with your own VPN app");
                Cmd(exe, dir, "vpn-auto on"); n0 = fp.Connects; Thread.Sleep(2500);
                Ok(fp.Connects == n0 && AutoState(exe, dir) == "off", "own VPN app: Hallow VPN is never asked");
                Cmd(exe, dir, "test-set vpnsource hallow");

                Ok(fp.Lines.All(l => l == "connect p2p --once"), "the stand-in only ever got \"connect p2p --once\" (" + fp.Lines.Count + " lines)");
                Ok(!File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("connect p2p"), "settings hold no commands");
                Cmd(exe, dir, "quit"); app.WaitForExit(8000);
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } fp.Dispose(); }

            // the headless exe: the same status fields and pipe commands, and auto-connect
            if (!File.Exists(hexe)) { Ok(false, "HallowTorrentsHeadless.exe is built"); return; }
            var hdir = Path.Combine(Dir, "headless19"); Directory.CreateDirectory(hdir);
            File.WriteAllText(Path.Combine(hdir, "settings.json"), "{\"Dht\":false,\"Pex\":false,\"Lpd\":false,\"LegalSeen\":true}");
            File.WriteAllText(Path.Combine(hdir, "fake-adapters.txt"), "eth|Ethernet|Realtek PCIe GbE|up|192.168.50.20|\nnl|NordLynx|NordLynx Tunnel|up|127.0.0.5|127.0.0.1\n");
            WriteVpn19(hdir, false, null, false, false);
            var hp2 = new FakeVpnPipe(hdir, l => { if (l.StartsWith("connect")) { WriteVpn19(hdir, true, true, false, false); return "ok connecting"; } return "error unknown command"; });
            var hp = new ProcessStartInfo(hexe) { UseShellExecute = false, CreateNoWindow = true };
            hp.EnvironmentVariables["HALLOW_TORRENTS_TESTDIR"] = hdir; hp.EnvironmentVariables["HALLOW_TORRENTS_FAKE"] = "1"; hp.EnvironmentVariables["HALLOW_TORRENTS_LOOPBACK_PUBLIC"] = "1";
            var h = Process.Start(hp);
            try
            {
                string r = null; for (int i = 0; i < 60 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, hdir, "ping"); }
                Ok(r != null && r.Contains("\"headless\":true"), "headless copy: ping " + r);
                var st = J(Cmd(exe, hdir, "status")); Ok(st.ContainsKey("vpnAuto") && st.ContainsKey("p2pOnly"), "headless status has vpnAuto and p2pOnly");
                Ok(Cmd(exe, hdir, "add magnet:?xt=urn:btih:2923456789abcdef0123456789abcdef01234567&dn=H&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce").StartsWith("ok"), "headless: a torrent added");
                Ok(Until(() => hp2.Connects >= 1, 6000) && hp2.Lines[0] == "connect p2p --once", "headless: auto-connect asked \"connect p2p --once\"");
                Ok(Until(() => Cmd(exe, hdir, "test-vpn").Contains("\"engine\":true"), 6000), "headless: connected, engine up");
                Ok(Cmd(exe, hdir, "p2p-only on").Contains("\"applies\":true") && Cmd(exe, hdir, "vpn-auto off").Contains("\"on\":false"), "headless: p2p-only / vpn-auto over the pipe");
                Cmd(exe, hdir, "quit"); h.WaitForExit(10000);
            }
            finally { try { if (!h.HasExited) h.Kill(); } catch { } hp2.Dispose(); }
        }
    }
}
