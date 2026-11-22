// Hallow Torrents - the Hallow accent (family v3): the colour picked in Hallow Toolkit (%APPDATA%\Hallow\theme.json)
// becomes this app's accent - in every window. Missing or broken file = the family orange. Never crashes on a bad file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HallowTorrents
{
    public static class Accent
    {
        public const string Default = "#F07820";
        public static string Folder { get { return TorSettings.TestDir != null ? Path.Combine(TorSettings.TestDir, "Hallow") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hallow"); } }
        public static string FilePath { get { return Path.Combine(Folder, "theme.json"); } }

        // the shared accent ("#RRGGBB"), or null when there's none or it isn't valid
        public static string Read()
        {
            var s = Field("accent");
            return s != null && Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$") ? s.ToUpperInvariant() : null;
        }
        // v1.8: the family light/dark switch ("dark" / "light" / "system") as written (Mode.Hallow normalises it), null when missing.
        // Hallow Torrents only reads it - Hallow Toolkit owns theme.json.
        public static string ReadMode() { return Field("mode"); }
        // one string field of theme.json, or null (no file, broken file, not a string); unknown fields are ignored
        static string Field(string name)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string text = null;
                for (int i = 0; i < 3 && text == null; i++) { try { text = File.ReadAllText(FilePath); } catch (IOException) { System.Threading.Thread.Sleep(50); } }   // being replaced right now
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text ?? ""); object a;
                return d != null && d.TryGetValue(name, out a) ? a as string : null;
            }
            catch { return null; }
        }
        public static string Current(bool use) { return (use ? Read() : null) ?? Default; }

        static int[] Rgb(string hex) { return new[] { int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber), int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber), int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber) }; }
        // a lighter shade (for borders next to the accent)
        public static string Lighter(string hex) { var c = Rgb(hex); return "#" + string.Concat(Array.ConvertAll(c, v => ((int)(v + (255 - v) * 0.3)).ToString("X2"))); }
        // text that reads well on the accent: dark on light colours, white on dark ones
        public static string OnAccent(string hex)
        {
            var c = Rgb(hex); Func<int, double> lin = v => { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); };
            var l = 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
            return l > 0.22 ? "#1A0E04" : "#FFFFFF";
        }
        // the theme's own accent colours swapped for this one
        public static string ApplyTo(string xaml, string accent)
        {
            if (accent == Default) return xaml;
            // v1.7: also the see-through forms (#40F07820 - the Hallow console's glows), keeping their alpha
            var rgb = accent.Substring(1);
            return Regex.Replace(Regex.Replace(Regex.Replace(xaml, "#([0-9A-Fa-f]{2})?F07820", m => "#" + m.Groups[1].Value + rgb, RegexOptions.IgnoreCase), "#F59A50", Lighter(accent), RegexOptions.IgnoreCase), "#1A0E04", OnAccent(accent), RegexOptions.IgnoreCase);
        }

        // calls changed (on a background thread, 300 ms after the last change) when theme.json changes; null if the folder isn't there (yet)
        public static FileSystemWatcher Watch(Action changed)
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;   // (the light/dark poll in Controller re-checks theme.json, so a file made later still counts)
                var w = new FileSystemWatcher(Folder) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
                System.Threading.Timer t = null;
                FileSystemEventHandler h = (s, e) =>
                {
                    if (!string.Equals(e.Name, "theme.json", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(e.FullPath), "theme.json", StringComparison.OrdinalIgnoreCase)) return;
                    if (t != null) t.Dispose();
                    t = new System.Threading.Timer(_ => changed(), null, 300, System.Threading.Timeout.Infinite);
                };
                w.Changed += h; w.Created += h; w.Deleted += h; w.Renamed += (s, e) => h(s, e);
                w.EnableRaisingEvents = true; return w;
            }
            catch { return null; }
        }
    }
}
