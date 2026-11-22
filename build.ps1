# Builds HallowTorrents.exe with the C# compiler that ships with Windows (.NET Framework 4.8) against MonoTorrent 3.0.2 and its
# dependencies in lib\ (MIT; versions, hashes and licences in licenses\). The DLLs are copied next to the exe.
#   .\build.ps1          the app
#   .\build.ps1 -Tests   also tests\TorrentsTests.exe (names, flags, the queue, the VPN decision, DNS, a fake registry, a local swarm
#                        bound to a stand-in VPN address, the pipe of a test copy) and tests\FakeMpCmdRun.exe (a Defender stand-in)
param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$src = Join-Path $PSScriptRoot 'src'
$lib = Join-Path $PSScriptRoot 'lib'
$out = Join-Path $PSScriptRoot 'HallowTorrents.exe'
$refs = "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Runtime.Serialization.dll", "$fw\System.Core.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Xml.dll",
    "$fw\Microsoft.VisualBasic.dll", "$fw\netstandard.dll", "$fw\System.Runtime.dll", "$fw\System.Net.Http.dll"
$refs += Get-ChildItem $lib -Filter *.dll | ForEach-Object FullName
$common = @('/nologo', '/optimize+', '/nowarn:1685', "/win32icon:$src\hallow.ico", "/resource:$src\Theme.xaml,HallowTorrents.Theme.xaml") + ($refs | ForEach-Object { "/reference:$_" })
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object FullName
& "$fw\csc.exe" @($common + @('/target:winexe', "/out:$out") + $files)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$lib\*.dll" $PSScriptRoot -Force
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
if ($Tests) {
    $t = Join-Path $PSScriptRoot 'tests\TorrentsTests.exe'
    & "$fw\csc.exe" @($common + @('/target:exe', "/out:$t", '/main:HallowTorrents.Tests.Run') + $files + (Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter *Tests.cs | ForEach-Object FullName))
    if ($LASTEXITCODE -ne 0) { throw "Test build failed" }
    Copy-Item "$lib\*.dll" (Join-Path $PSScriptRoot 'tests') -Force
    & "$fw\csc.exe" /nologo /optimize+ /target:exe "/out:$(Join-Path $PSScriptRoot 'tests\FakeMpCmdRun.exe')" (Join-Path $PSScriptRoot 'tests\FakeMpCmdRun.cs')
    if ($LASTEXITCODE -ne 0) { throw "FakeMpCmdRun build failed" }
    "Built $t"
}
