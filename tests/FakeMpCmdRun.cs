// A stand-in for Microsoft Defender's MpCmdRun.exe (tests only): records its arguments and its own priority, reports a made-up
// threat when the scanned path contains "evil", nothing otherwise. Exit codes like MpCmdRun: 0 = clean, 2 = threats.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
public static class FakeMpCmdRun
{
    public static int Main(string[] args)
    {
        Thread.Sleep(400);   // the caller sets the priority right after starting us
        var line = string.Join(" ", args);
        var o = Environment.GetEnvironmentVariable("FAKE_MPCMDRUN_OUT");
        if (!string.IsNullOrEmpty(o)) File.WriteAllText(o, Process.GetCurrentProcess().PriorityClass + " " + line);
        if (line.IndexOf("evil", StringComparison.OrdinalIgnoreCase) >= 0) { Console.WriteLine("Scan starting...\nThreat                  : Virus:DOS/EICAR-Fake\nScan finished."); return 2; }
        Console.WriteLine("Scan starting...\nScan finished.\nScanning found no threats."); return 0;
    }
}
