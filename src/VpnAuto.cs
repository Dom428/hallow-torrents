// Hallow Torrents 1.9 - VPN on demand. Everything here happens BEFORE the VPN requirement (Net.cs), never instead of it:
//   * Auto-connect: when there are torrents to run and Hallow VPN is off, Hallow Torrents asks Hallow VPN "connect p2p --once" over
//     its pipe (a P2P server, without changing the server you picked in Hallow VPN). Only when Hallow VPN's status says the next
//     connect doesn't need Windows' permission (needsUac.connect == false) - otherwise it says so and you connect from Hallow VPN.
//     Tries back off: now, then after 30 s, 2 min and 5 min; then it stops until you act (Connect, Retry, or the setting).
//     Never while a game runs, unless you ticked "also during games". On by default when Hallow VPN is installed.
//   * P2P servers only (off by default): torrents run only while Hallow VPN reports "p2p": true. On any other server the VPN check
//     answers "not OK" (Net.P2pGate), so the engine is stopped exactly as if the VPN had dropped. It can only ever ADD a stop.
//     It needs Hallow VPN's status: with your own VPN app's adapter it can't be checked and counts as off.
//   * The lock itself (binding, the watchdog, the leak drill) is unchanged: torrents still never run without a VPN.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HallowTorrents
{
    public class VpnAuto
    {
        // the waits after try 1, 2 and 3; after try 4 it stops until you act
        public static readonly TimeSpan[] Backoff = { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) };
        public static int MaxTries { get { return Backoff.Length + 1; } }
        public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);   // after the last try: this long to connect, then "stopped"
        public const string Command = "connect p2p --once";
        public const string UacText = "Connect the VPN from Hallow VPN (it needs Windows' permission for NordLynx).";
        public const string OldVpnText = "Connect the VPN from Hallow VPN (update Hallow VPN to 1.2.6 or later so Hallow Torrents can ask it to connect).";

        public Func<string, string> Ask = line => Family.Send("VPN", line, 5000);   // Hallow VPN's pipe (tests: a .test pipe / a stand-in)
        public event Action<string> Log;

        // what the banner / status show
        public string State = "off";   // off | idle | connected | game | not-running | uac | connecting | asked | waiting | stopped
        public string Text;            // a sentence for the VPN-lock banner (null = nothing to say)
        public int Tries;              // tries since the VPN was last up (or since you acted)
        public DateTime? LastTry, NextTry;
        public string LastReply;
        readonly object gate = new object();

        public class Inputs
        {
            public bool On, InGames;          // the settings (On = the effective value)
            public string Source;             // "hallow" / "adapter"
            public bool VpnOk;                // the lock is open (VpnState.Ok)
            public bool NotP2p;               // connected, but held by "P2P servers only"
            public bool Work;                 // torrents to run
            public string Game;               // a game is running (null = none)
            public bool HallowRunning;        // Hallow VPN's pipe answers
            public Dictionary<string, object> Hallow;   // Hallow VPN's status (pipe or status.json)
            public bool Drill;                // the leak drill is running (it disconnects the VPN on purpose - never interfere)
        }

        // you acted (Connect / Retry / changed the setting): start again from try 1
        public void Reset() { lock (gate) { Tries = 0; LastTry = null; NextTry = null; if (State == "stopped" || State == "waiting") State = "idle"; } }

        // is it allowed to ask without a UAC prompt? null = yes; else the sentence to show
        public static string UacBlock(Dictionary<string, object> hallow)
        {
            object v; if (hallow == null || !hallow.TryGetValue("needsUac", out v)) return OldVpnText;
            var d = v as Dictionary<string, object>; object c;
            if (d == null || !d.TryGetValue("connect", out c) || !(c is bool)) return OldVpnText;
            return (bool)c ? UacText : null;
        }
        public static bool? Flag(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v is bool ? (bool?)(bool)v : null; }

        // one step (every second). Returns the line sent to Hallow VPN this step, or null.
        public string Step(DateTime now, Inputs i)
        {
            lock (gate)
            {
                if (!i.On || i.Source != "hallow") { Set("off", null); Tries = 0; NextTry = null; return null; }
                if (i.VpnOk || i.NotP2p) { if (Tries > 0) Say("vpn auto: connected"); Tries = 0; NextTry = null; LastTry = null; Set(i.VpnOk ? "connected" : "idle", null); return null; }
                if (!i.Work || i.Drill) { Set("idle", null); NextTry = null; return null; }
                if (Tries >= MaxTries)
                {
                    if (LastTry != null && now - LastTry.Value < GiveUpAfter && Flag(i.Hallow, "connecting") == true) { Set("connecting", "Hallow VPN is connecting to a P2P server..."); return null; }
                    if (LastTry == null || now - LastTry.Value >= GiveUpAfter) { NextTry = null; Set("stopped", "Hallow Torrents asked Hallow VPN " + MaxTries + " times and it didn't connect, so it stopped asking. Click Connect Hallow VPN, or connect it in Hallow VPN." + (LastReply != null && LastReply.StartsWith("error ") ? " (Hallow VPN: " + LastReply.Substring(6) + ")" : "")); }
                    return null;
                }
                if (i.Game != null && !i.InGames) { Set("game", i.Game + " is running, so Hallow VPN isn't connected automatically (tick \"also during games\" to allow it)."); return null; }
                if (!i.HallowRunning || i.Hallow == null) { Set("not-running", "Hallow VPN isn't running, so it can't be asked to connect. Start it and it's connected for you."); return null; }
                if (Flag(i.Hallow, "connecting") == true) { Set("connecting", "Hallow VPN is connecting..."); return null; }
                var uac = UacBlock(i.Hallow); if (uac != null) { Set("uac", uac); return null; }
                if (NextTry != null && now < NextTry.Value)
                {
                    if (LastTry != null && now - LastTry.Value < TimeSpan.FromSeconds(15) && LastReply != null && LastReply.StartsWith("ok"))
                    { Set("asked", "Asked Hallow VPN to connect to a P2P server (try " + Tries + " of " + MaxTries + ")..."); return null; }   // give it a moment
                    Set("waiting", "Hallow VPN didn't connect yet - trying again at " + NextTry.Value.ToString("HH:mm:ss") + " (try " + (Tries + 1) + " of " + MaxTries + ")." + (LastReply != null && LastReply.StartsWith("error ") ? " Hallow VPN said: " + LastReply.Substring(6) : ""));
                    return null;
                }
                // ask
                Tries++; LastTry = now; NextTry = Tries <= Backoff.Length ? now + Backoff[Tries - 1] : (DateTime?)null;
                string r; try { r = Ask(Command); } catch (Exception e) { r = "error " + e.Message; }
                LastReply = r ?? "error no answer";
                Say("vpn auto: try " + Tries + " - " + Command + " -> " + LastReply);
                Set("asked", "Asked Hallow VPN to connect to a P2P server (try " + Tries + " of " + MaxTries + ")...");
                return Command;
            }
        }
        void Set(string state, string text) { State = state; Text = text; }
        void Say(string s) { var l = Log; if (l != null) try { l(s); } catch { } }

        public Dictionary<string, object> Json(bool on, bool inGames)
        {
            lock (gate)
                return new Dictionary<string, object> { { "on", on }, { "inGames", inGames }, { "state", State }, { "tries", Tries }, { "maxTries", MaxTries },
                    { "nextTry", NextTry == null ? null : Family.Time(NextTry.Value) }, { "message", Text } };
        }

        // ---- the settings' effective values
        // on by default when Hallow VPN is installed (null = never changed)
        public static bool On(TorSettings s) { return s.VpnAuto ?? HallowVpnInstalled(); }
        public static bool HallowVpnInstalled()
        {
            try
            {
                if (TorSettings.TestDir != null) return Directory.Exists(Family.DataDir("VPN"));
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hallow VPN", "HallowVPN.exe");
                return File.Exists(exe) || Directory.Exists(Family.DataDir("VPN"));
            }
            catch { return false; }
        }
        // "P2P servers only" applies only with Hallow VPN (its status says p2p); with your own VPN app it can't be checked = off
        public static bool P2pOnly(TorSettings s) { return s.P2pOnly && s.VpnSource == "hallow"; }
        public const string P2pAdapterNote = "\"P2P servers only\" needs Hallow VPN: with your own VPN app's adapter Hallow Torrents can't tell which server you're on, so it counts as off.";

        // torrents that would run if the VPN were up
        public static bool Work(Core core)
        {
            try { return core != null && core.Entries.ToArray().Any(x => !x.UserPaused && !(x.CompletedAt != null && x.LimitHit)); } catch { return false; }
        }

        public static Dictionary<string, object> P2pJson(TorSettings s, VpnState vpn, Dictionary<string, object> hallow)
        {
            return new Dictionary<string, object> { { "on", s.P2pOnly }, { "applies", P2pOnly(s) }, { "held", vpn != null && vpn.NotP2p }, { "serverP2p", s.VpnSource == "hallow" ? Flag(hallow, "p2p") : null },
                { "note", s.VpnSource == "hallow" ? null : P2pAdapterNote } };
        }

        // ---- the pipe (the app and the headless copy): vpn-auto, p2p-only, switch-p2p. null = not ours. change = apply + save a setting.
        public static string Pipe(string cmd, string arg, TorSettings s, Action<Action> change, VpnAuto auto, Func<string> switchP2p, VpnState vpn, Dictionary<string, object> hallow)
        {
            arg = (arg ?? "").Trim().ToLowerInvariant();
            switch (cmd)
            {
                case "vpn-auto":
                    if (arg == "on" || arg == "off") { change(() => s.VpnAuto = arg == "on"); auto.Reset(); }
                    else if (arg == "games on" || arg == "games off") { change(() => s.VpnAutoInGames = arg == "games on"); auto.Reset(); }
                    else if (arg == "retry") auto.Reset();
                    else if (arg != "" && arg != "status") return "error vpn-auto [on|off|games on|games off|retry]";
                    return "ok " + Family.Json(auto.Json(On(s), s.VpnAutoInGames));
                case "p2p-only":
                    if (arg == "on") { if (s.VpnSource != "hallow") return "error " + P2pAdapterNote; change(() => s.P2pOnly = true); }
                    else if (arg == "off") change(() => s.P2pOnly = false);
                    else if (arg != "" && arg != "status") return "error p2p-only [on|off]";
                    return "ok " + Family.Json(P2pJson(s, vpn, hallow));
                case "switch-p2p": { auto.Reset(); var e = switchP2p(); return e == null ? "ok" : "error " + e; }
            }
            return null;
        }

        // "Switch to a P2P server" (and Connect while P2P-only is on): the same UAC rule as auto-connect. null = asked.
        public string SwitchP2p(Dictionary<string, object> hallow, bool running)
        {
            if (!running) return "Hallow VPN isn't running. Start it and connect to a P2P server.";
            var uac = UacBlock(hallow); if (uac != null) return uac;
            string r; try { r = Ask(Command); } catch (Exception e) { r = "error " + e.Message; }
            Say("vpn: switch to a P2P server -> " + (r ?? "no answer"));
            if (r == null) return "Hallow VPN didn't answer.";
            var e2 = Family.Error(r, null); return e2 == null ? null : "Hallow VPN: " + e2;
        }
    }
}
