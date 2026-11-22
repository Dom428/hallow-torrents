# Hallow Torrents speed test from a script - the same as the "Speed & leaks" page's Start button.
#   YOU start this, with Hallow Torrents running and your VPN connected. It downloads the torrent you pick through the VPN (like any
#   other torrent - VPN-bound, game-aware) and reports average / peak speed, time, peers and CPU. Nothing else is sent anywhere.
#   .\speedtest.ps1                      Ubuntu's current desktop image (its .torrent is fetched from releases.ubuntu.com over the VPN)
#   .\speedtest.ps1 -Torrent "magnet:?xt=urn:btih:..."   or   -Torrent C:\path\file.torrent
#   -MaxMinutes 60   stop after this long      -Out result.json   save the result
param([string]$Torrent = 'ubuntu', [string]$HallowExe = "$env:LOCALAPPDATA\Programs\Hallow Torrents\HallowTorrents.exe", [int]$MaxMinutes = 60, [string]$Out = '', [int]$PollSeconds = 5)
$ErrorActionPreference = 'Stop'
function Cmd([string]$line) {
    # HallowTorrents.exe --cmd "<line>" (a window app: run it directly and read what it prints)
    $psi = New-Object Diagnostics.ProcessStartInfo $HallowExe, ('--cmd "' + $line.Replace('"', '') + '"')
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi); $o = $p.StandardOutput.ReadToEnd().Trim(); $p.WaitForExit()
    if ($o -match '^ok ?(.*)$') { return @{ ok = $true; text = $Matches[1] } }
    return @{ ok = $false; text = ($o -replace '^error ?', '') }
}
function Json([string]$t) { if ($t -and $t.StartsWith('{')) { return $t | ConvertFrom-Json } return $null }
if (-not (Test-Path $HallowExe)) { throw "Hallow Torrents isn't installed at $HallowExe (use -HallowExe)." }
$ping = Cmd 'ping'; if (-not $ping.ok) { throw 'Hallow Torrents isn''t running - start it first.' }
$v = [Version](Json $ping.text).version; if ($v -lt [Version]'1.5') { throw "Hallow Torrents $v is too old - the speed test is new in 1.5." }
$st = Json (Cmd 'status').text; if ($st.vpnLocked) { throw 'Your VPN isn''t connected (Hallow Torrents is locked) - connect it first.' }
Write-Host "Hallow Torrents $v - starting the speed test ($Torrent) over the VPN..."
$r = Cmd ("speedtest " + $Torrent); if (-not $r.ok) { throw "Couldn't start: $($r.text)" }
$deadline = (Get-Date).AddMinutes($MaxMinutes); $s = $null
while ($true) {
    Start-Sleep -Seconds $PollSeconds
    $s = Json (Cmd 'speedtest status').text
    if ($null -eq $s) { continue }
    Write-Host ("{0,6:0.0}%  avg {1,7:0.0} MB/s  peak {2,7:0.0} MB/s  peers {3,4}  {4}" -f $s.progress, $s.avgMBps, $s.peakMBps, $s.peersMax, $s.state)
    if (-not $s.running) { break }
    if ((Get-Date) -gt $deadline) { [void](Cmd 'speedtest stop'); Start-Sleep -Seconds 3; $s = Json (Cmd 'speedtest status').text; break }
}
Write-Host ''
Write-Host ("Hallow Torrents: {0} - {1:0.0} MB/s average ({2:0} Mbit/s), {3:0.0} MB/s peak, {4:0} s, {5} peers max, CPU {6:0.0}% of the PC" -f $s.state, $s.avgMBps, $s.avgMbit, $s.peakMBps, $s.activeSeconds, $s.peersMax, $s.cpuPct)
if ($Out) { $s | ConvertTo-Json -Depth 4 | Set-Content -Path $Out -Encoding UTF8; Write-Host "Saved: $Out" }
$s
