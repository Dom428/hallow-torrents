// HallowTorrentsHeadless.exe - NAS / headless mode without WPF: only the engine, RSS, search, the family pipe and the Web API / web
// interface (HeadlessHost.cs). Built by build.ps1 from src\*.cs minus the window files (no PresentationFramework, WindowsBase or
// System.Windows.Forms reference), so the same exe could in principle run on Mono - that is NOT tested (see NAS-MODE.md).
using System.Reflection;

[assembly: AssemblyTitle("Hallow Torrents (headless)")]
[assembly: AssemblyProduct("Hallow Torrents")]
[assembly: AssemblyDescription("Hallow Torrents without a window: the engine, the Web API and the web interface")]
[assembly: AssemblyVersion(HallowTorrents.BuildInfo.Version + ".0.0")]
[assembly: AssemblyFileVersion(HallowTorrents.BuildInfo.Version + ".0.0")]

namespace HallowTorrents
{
    public static class Program
    {
        public const string Version = BuildInfo.Version;
        public static int Main(string[] args) { return HeadlessHost.Run(args); }
    }
}
