// Hallow Torrents 1.4 - what a phone page needs, in one place: the list, pause / resume one torrent, statistics. Used by the family pipe
// (Hallow Remote proxies it to your phone - REMOTE-CONTRACT.md has the exact commands and JSON) and by the web interface (/ui/).
// The pipe only answers this Windows user on this PC; the web interface needs a sign-in. Nothing here relaxes the VPN lock: a torrent
// added or resumed from a phone waits for the VPN like any other.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HallowTorrents
{
    public static class RemoteApi
    {
        // a short, stable code for a torrent's state (Hallow's own state text stays in "state")
        public static string Code(TorrentView v)
        {
            switch (v.State)
            {
                case "Waiting for VPN": return "waiting-vpn";
                case "Paused": return "paused";
                case "Done (limit reached)": return "done";
                case "Done": return "done";
                case "Paused for game": return "game";
                case "Queued": return "queued";
                case "Downloading": return "downloading";
                case "Seeding": return "seeding";
                case "Getting info": return "metadata";
                case "Checking": return "checking";
                case "Error": return "error";
                default: return "starting";
            }
        }
        static double R(double d, int n) { return Math.Round(d, n); }

        // the list: name, progress, speeds, state and the VPN-locked flag (per torrent and for the whole engine)
        public static Dictionary<string, object> List(Core core, string game, List<TorrentView> views = null)
        {
            var vs = views ?? core.Snapshot(false); bool locked = !core.EngineUp;
            var list = new List<object>();
            foreach (var v in vs.OrderBy(x => x.Complete ? 1 : 0).ThenBy(x => x.Queue))
            {
                var x = core.Get(v.Id);
                list.Add(new Dictionary<string, object> {
                    { "hash", v.Id }, { "name", v.Name ?? "" }, { "progress", v.Complete ? 1.0 : R(Math.Max(0, Math.Min(100, v.Progress)) / 100.0, 4) },
                    { "downBps", v.DownRate }, { "upBps", v.UpRate }, { "state", v.State }, { "status", Code(v) }, { "category", v.Category ?? "" },
                    { "size", v.Size }, { "done", v.Done }, { "eta", !v.Complete && v.Eta.HasValue && v.Eta.Value.TotalSeconds < 8640000 ? (object)(long)v.Eta.Value.TotalSeconds : null },
                    { "seeds", v.Seeds }, { "peers", v.Leechs }, { "ratio", R(v.Ratio, 3) }, { "paused", v.Paused || (x != null && x.UserPaused) }, { "complete", v.Complete },
                    { "flagged", v.Flagged || v.Scan == "threats" }, { "vpnLocked", locked && !(x != null && x.UserPaused) } });
            }
            return new Dictionary<string, object> {
                { "vpnLocked", locked }, { "held", core.Hold }, { "game", game }, { "downBps", core.TotalDown }, { "upBps", core.TotalUp },
                { "count", list.Count }, { "torrents", list } };
        }

        public static Dictionary<string, object> Stats(Core core)
        {
            var st = core.Stats; var v = core.Snapshot(false);
            Func<long, long, double, double, long, Dictionary<string, object>> part = (down, up, seed, lockedS, blocked) => new Dictionary<string, object> {
                { "down", down }, { "up", up }, { "ratio", down > 0 ? R(up / (double)down, 3) : 0.0 }, { "seedSecs", (long)seed }, { "lockedSecs", (long)lockedS }, { "blocked", blocked } };
            return new Dictionary<string, object> {
                { "session", st == null ? null : part(st.SessionDown, st.SessionUp, st.SessionSeedSecs, st.SessionLockedSecs, st.SessionBlocked) },
                { "allTime", st == null ? null : part(st.Down, st.Up, st.SeedSecs, st.LockedSecs, st.Blocked) },
                { "since", st == null ? null : st.Since }, { "sessionStart", st == null ? null : Family.Time(st.SessionStart) },
                { "downBps", core.TotalDown }, { "upBps", core.TotalUp }, { "torrents", v.Count }, { "downloading", v.Count(x => !x.Complete) }, { "seeding", v.Count(x => x.State == "Seeding") },
                { "vpnLocked", !core.EngineUp }, { "blocklistRanges", core.BlocklistRanges } };
        }

        // a torrent by its info hash (40 / 64 hex, any case) or a unique start of it (8+ characters)
        public static Entry Find(Core core, string hash, out string error)
        {
            error = null; var h = (hash ?? "").Trim().ToLowerInvariant();
            if (h.Length < 8 || h.Any(ch => !Uri.IsHexDigit(ch))) { error = "give the torrent's info hash (from list)"; return null; }
            var all = core.All(); var exact = all.FirstOrDefault(e => e.Id == h); if (exact != null) return exact;
            var some = all.Where(e => e.Id.StartsWith(h, StringComparison.Ordinal)).ToList();
            if (some.Count == 1) return some[0];
            error = some.Count == 0 ? "no torrent with that hash" : "more than one torrent starts with that - give more of the hash";
            return null;
        }
        public static string PauseResume(Core core, string hash, bool pause)
        {
            string err; var x = Find(core, hash, out err); if (x == null) return "error " + err;
            if (pause) core.Pause(x.Id); else core.Resume(x.Id);
            return "ok " + Family.Json(new Dictionary<string, object> { { "hash", x.Id }, { "paused", pause }, { "vpnLocked", !core.EngineUp } });
        }

        // the pipe commands both the app and the headless host answer the same way (null = not one of these)
        public static string Pipe(string cmd, string arg, Core core, string game)
        {
            switch (cmd)
            {
                case "list": return "ok " + Family.Json(List(core, game));
                case "stats": return "ok " + Family.Json(Stats(core));
                case "pause": return arg.Length == 0 ? "error pause <hash> (or pause-all)" : PauseResume(core, arg, true);
                case "resume": return arg.Length == 0 ? "error resume <hash> (or resume-all)" : PauseResume(core, arg, false);
            }
            return null;
        }
        // 1.8: "categories" - your category names only (never their folders or rules), in Settings order, for a phone's add-magnet picker
        public static string Categories(IEnumerable<Category> cats)
        {
            var names = (cats ?? Enumerable.Empty<Category>()).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name).ToList();
            return "ok " + Family.Json(new Dictionary<string, object> { { "categories", names }, { "count", names.Count } });
        }
        // 1.8: "search ... --client <id>" / "search-add <id> --client <id>": whose search it is (a phone, a Hub...). Takes it out of arg;
        // null when not given (the shared default set, as before). error is set when it's there but not 1-64 of A-Z a-z 0-9 . _ : -
        public static string TakeClient(ref string arg, out string error)
        {
            error = null;
            var m = System.Text.RegularExpressions.Regex.Match(" " + arg + " ", @"\s--client(?:\s+(\S*))?(?=\s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var c = m.Groups[1].Success ? m.Groups[1].Value.Trim('"') : "";
            arg = (" " + arg + " ").Remove(m.Index, m.Length).Trim();
            if (!SearchEngine.ValidClient(c)) { error = "--client needs a name: 1-64 letters, digits, . _ : or -"; return null; }
            return c;
        }
        // "add <magnet> [--category <c>] [--direct]": --direct adds straight away (no Add window on the PC - what a phone wants)
        public static bool TakeDirect(ref string arg)
        {
            var m = System.Text.RegularExpressions.Regex.Match(" " + arg + " ", @"\s--direct(?=\s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            arg = (" " + arg + " ").Remove(m.Index, m.Length).Trim(); return true;
        }
    }
}
