// Hallow Torrents - when a download finishes: Microsoft Defender scans it (Idle priority), suspicious files are flagged, and (if its
// category says so) it's copied or moved into your media library with Jellyfin-friendly names. A flagged download or one Defender
// found something in is never handed off by itself, and nothing is ever opened.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HallowTorrents
{
    public static class Finish
    {
        public static async Task Run(Core core, Entry x, TorSettings s, Action<string, string> notify)
        {
            x.Flags = Media.Suspicious(x.Name, x.FileList).Select(f => f[0] + "|" + f[1]).ToList();
            if (s.ScanDone)
            {
                x.Scan = "scanning"; x.ScanText = "Microsoft Defender is scanning it..."; core.Save();
                var root = core.RootOf(x.Id);
                var res = await Task.Run(() => DefenderScan.Run(root)).ConfigureAwait(false);
                x.Scan = res[0]; x.ScanText = res[1]; core.Save();
                if (res[0] == "threats" && notify != null) notify("Defender found something in a download", x.Name + ": " + res[1]);
            }
            else { x.Scan = "off"; x.ScanText = "Scanning finished downloads is off in Settings."; }
            if (x.Flags.Count > 0 && notify != null) notify("Check this download before opening it", x.Name + ": " + x.Flags[0].Split('|').Last());
            await Handoff(core, x, s).ConfigureAwait(false);
            core.Save();
        }

        public static async Task Handoff(Core core, Entry x, TorSettings s)
        {
            var c = s.Cat(x.Category);
            if (c == null || !c.Handoff || string.IsNullOrWhiteSpace(c.LibraryFolder)) return;
            // v1.2: Radarr / Sonarr import it themselves from where it is - moving it away first would break their import
            if (x.Api && c.Move) { x.Handoff = "Left in place: the app that added it (Radarr / Sonarr) imports it from here."; return; }
            if (x.Flags != null && x.Flags.Count > 0) { x.Handoff = "Held: it has files that look suspicious - check it, then use Send to library."; return; }
            if (x.Scan == "threats") { x.Handoff = "Held: Microsoft Defender found something in it."; return; }
            x.Handoff = await Send(core, x, c).ConfigureAwait(false);
        }
        // copies (or moves) the files into the library; returns what it did
        public static async Task<string> Send(Core core, Entry x, Category c)
        {
            try
            {
                var files = x.FileList.Where((f, i) => x.PrioOf(i) > 0).ToList();
                var plan = Media.Plan(x.Name, files, c.Naming);
                if (plan.Count == 0) return "Nothing to hand off (no " + (c.Naming == "tv" ? "episodes" : c.Naming == "movie" ? "video" : "files") + " found).";
                var lib = c.LibraryFolder;
                var moves = plan.Select(p => new[] { p[0], Path.Combine(lib, p[1]) }).ToList();
                if (c.Move)
                {
                    if (!core.EngineUp) return "Waiting: moving needs the engine, which only runs while the VPN is connected.";
                    var err = await core.MoveFiles(x.Id, moves).ConfigureAwait(false);
                    return err == null ? "Moved " + moves.Count + " file" + (moves.Count == 1 ? "" : "s") + " to " + lib + " (still seeding from there)." : "Couldn't move it: " + err;
                }
                int n = 0;
                await Task.Run(() =>
                {
                    foreach (var mv in moves)
                    {
                        var src = core.FullPathOf(x.Id, mv[0]); if (src == null || !File.Exists(src)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(mv[1]));
                        if (File.Exists(mv[1]) && new FileInfo(mv[1]).Length == new FileInfo(src).Length) { n++; continue; }
                        var tmp = mv[1] + ".hallow-part"; File.Copy(src, tmp, true);
                        if (File.Exists(mv[1])) File.Delete(tmp); else File.Move(tmp, mv[1]);   // never overwrites a file that's already there
                        n++;
                    }
                }).ConfigureAwait(false);
                return "Copied " + n + " file" + (n == 1 ? "" : "s") + " to " + lib + ".";
            }
            catch (Exception e) { return "Couldn't copy it: " + e.Message; }
        }
    }
}
