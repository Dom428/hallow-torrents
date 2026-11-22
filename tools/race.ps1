# Hallow Torrents vs qBittorrent - a fair race on YOUR connection, one after the other, the same torrent.
#   YOU start this (it is never run by itself), with your VPN connected and both apps running. It sends nothing anywhere except the
#   two downloads themselves (through the VPN) and Ubuntu's .torrent (from releases.ubuntu.com, through Hallow Torrents' VPN socket).
#   1. Hallow Torrents downloads the torrent (its speed test: VPN-bound like always).
#   2. qBittorrent downloads the same torrent into its own folder, with its network interface BOUND TO THE VPN ADAPTER for the race
#      (Tools > Options > Advanced > Network interface - set through its Web UI, put back to what it was afterwards).
#   3. A table: average / peak speed, time, peers, CPU - and a JSON + Markdown file in -OutDir.
# Before you start: qBittorrent's Web UI must be on (Tools > Options > Web UI: "Web User Interface (Remote control)", port 8080 on
# 127.0.0.1 is fine; tick "Bypass authentication for clients on localhost" or give -Credential). Run it once in each order
# (-QbitFirst) if you want to rule out the swarm changing between the two runs. Both downloads are left on disk (about 6 GB each
# for Ubuntu) - delete them when you're done.
#   .\race.ps1                                   Ubuntu's current desktop image
#   .\race.ps1 -Torrent "magnet:?xt=..."         any legal, well-seeded torrent (or -Torrent C:\path\file.torrent)
#   -Adapter NordLynx   the VPN adapter qBittorrent is bound to (default: the connected one that looks like a VPN)
#   -QbitUrl http://127.0.0.1:8080   -Credential (Get-Credential)   -MaxMinutes 60   -OutDir <folder>   -QbitFirst
param(
    [string]$Torrent = 'ubuntu', [string]$QbitUrl = 'http://127.0.0.1:8080', [pscredential]$Credential, [string]$Adapter = '',
    [string]$HallowExe = "$env:LOCALAPPDATA\Programs\Hallow Torrents\HallowTorrents.exe", [int]$MaxMinutes = 60,
    [string]$OutDir = "$env:USERPROFILE\Documents\Hallow Torrents race", [switch]$QbitFirst,
    [switch]$SkipHallow, [string]$HallowReport = '',      # use a finished Hallow speed test (its JSON) instead of running one
    [string]$AdapterIp = '', [switch]$Test, [int]$PollSeconds = 2   # -Test: developer tests against a local qBittorrent stand-in
)
$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host ((Get-Date -Format 'HH:mm:ss') + '  ' + $m) }

# ---------------- Hallow Torrents (its pipe, through HallowTorrents.exe --cmd)
function HCmd([string]$line) {
    $psi = New-Object Diagnostics.ProcessStartInfo $HallowExe, ('--cmd "' + $line.Replace('"', '') + '"')
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi); $o = $p.StandardOutput.ReadToEnd().Trim(); $p.WaitForExit()
    if ($o -match '^ok ?(.*)$') { return @{ ok = $true; text = $Matches[1] } }
    return @{ ok = $false; text = ($o -replace '^error ?', '') }
}
function FromJson([string]$t) { if ($t -and $t.StartsWith('{')) { return $t | ConvertFrom-Json } return $null }

# ---------------- the numbers (the same rules as Hallow's speed test: average over the time it ran, peak = best 3-second average)
function Get-RaceSummary($samples) {
    # samples: objects with T (seconds), Bytes (downloaded so far), Peers, Cpu (process CPU seconds so far)
    $s = @($samples); if ($s.Count -lt 2) { return [pscustomobject]@{ Bytes = 0; Seconds = 0; AvgMBps = 0; PeakMBps = 0; PeersMax = 0; PeersAvg = 0; CpuPct = 0; FirstByteSeconds = 0 } }
    $bytes = $s[-1].Bytes - $s[0].Bytes; $secs = $s[-1].T - $s[0].T; $peak = 0.0
    for ($i = 0; $i -lt $s.Count; $i++) { for ($j = $i + 1; $j -lt $s.Count; $j++) { if ($s[$j].T - $s[$i].T -ge 3) { $r = ($s[$j].Bytes - $s[$i].Bytes) / ($s[$j].T - $s[$i].T); if ($r -gt $peak) { $peak = $r }; break } } }
    if ($peak -eq 0 -and $secs -gt 0) { $peak = $bytes / $secs }
    $first = $s | Where-Object { $_.Bytes -gt $s[0].Bytes } | Select-Object -First 1
    $cpu = if ($secs -gt 0) { ($s[-1].Cpu - $s[0].Cpu) / $secs * 100 / [Environment]::ProcessorCount } else { 0 }
    [pscustomobject]@{
        Bytes = $bytes; Seconds = [math]::Round($secs, 1); AvgMBps = [math]::Round($(if ($secs -gt 0) { $bytes / $secs / 1MB } else { 0 }), 2); PeakMBps = [math]::Round($peak / 1MB, 2)
        PeersMax = ($s | Measure-Object -Property Peers -Maximum).Maximum; PeersAvg = [math]::Round(($s | Measure-Object -Property Peers -Average).Average, 1)
        CpuPct = [math]::Round($cpu, 1); FirstByteSeconds = $(if ($first) { [math]::Round($first.T - $s[0].T, 1) } else { 0 })
    }
}
function Compare-Race($h, $q) {
    $faster = if ($h.AvgMBps -ge $q.AvgMBps) { 'Hallow Torrents' } else { 'qBittorrent' }
    $ratio = if ([math]::Min($h.AvgMBps, $q.AvgMBps) -gt 0) { [math]::Round([math]::Max($h.AvgMBps, $q.AvgMBps) / [math]::Min($h.AvgMBps, $q.AvgMBps), 2) } else { 0 }
    [pscustomobject]@{ Faster = $faster; Ratio = $ratio; HallowPctOfQbit = $(if ($q.AvgMBps -gt 0) { [math]::Round($h.AvgMBps / $q.AvgMBps * 100) } else { 0 }) }
}
# a qBittorrent torrents/info row -> one sample
function Read-QbitSample($row, [double]$t, [double]$cpu) {
    [pscustomobject]@{ T = $t; Bytes = [double]$row.downloaded; Peers = [int]$row.num_seeds + [int]$row.num_leechs; Cpu = $cpu; Progress = [double]$row.progress; State = [string]$row.state }
}

# ---------------- qBittorrent's Web API
$script:web = $null
function Q([string]$method, [string]$path, $body = $null, [string]$contentType = $null) {
    $a = @{ Uri = $QbitUrl.TrimEnd('/') + $path; Method = $method; UseBasicParsing = $true; WebSession = $script:web; Headers = @{ Referer = $QbitUrl } }
    if ($null -ne $body) { $a.Body = $body }; if ($contentType) { $a.ContentType = $contentType }
    Invoke-WebRequest @a
}
function QJson([string]$path) { (Q 'GET' $path).Content | ConvertFrom-Json }
# a JSON array as a real list (Windows PowerShell 5.1's ConvertFrom-Json hands an array over as ONE object)
function QList([string]$path) { @(QJson $path | ForEach-Object { $_ }) }
function Qbit-SignIn {
    $script:web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    try { [void](Q 'GET' '/api/v2/app/version'); return } catch { }   # "bypass authentication for clients on localhost"
    $cred = $Credential; if (-not $cred) { $cred = Get-Credential -Message 'qBittorrent Web UI username and password' }
    $r = Q 'POST' '/api/v2/auth/login' @{ username = $cred.UserName; password = $cred.GetNetworkCredential().Password }
    if ($r.Content -notmatch 'Ok') { throw 'qBittorrent refused the sign-in (check the Web UI username and password).' }
}
function Qbit-Bind([string]$iface, [string]$ip) {
    $form = @{ json = (@{ current_network_interface = $iface; current_interface_address = $ip } | ConvertTo-Json -Compress) }
    [void](Q 'POST' '/api/v2/app/setPreferences' $form)
}
function Qbit-Add([string]$item, [string]$save, [string]$tag) {
    if ($item -match '^magnet:') { [void](Q 'POST' '/api/v2/torrents/add' @{ urls = $item; savepath = $save; tags = $tag }); return }
    # multipart: the .torrent file
    $b = [guid]::NewGuid().ToString('N'); $nl = "`r`n"; $enc = [Text.Encoding]::GetEncoding('iso-8859-1')
    $file = $enc.GetString([IO.File]::ReadAllBytes($item))
    $body = "--$b$nl" + "Content-Disposition: form-data; name=`"torrents`"; filename=`"race.torrent`"$nl" + "Content-Type: application/x-bittorrent$nl$nl" + $file + $nl
    foreach ($kv in @(@('savepath', $save), @('tags', $tag))) { $body += "--$b$nl" + "Content-Disposition: form-data; name=`"$($kv[0])`"$nl$nl" + $kv[1] + $nl }
    $body += "--$b--$nl"
    [void](Q 'POST' '/api/v2/torrents/add' ($enc.GetBytes($body)) "multipart/form-data; boundary=$b")
}

# ---------------- the VPN adapter qBittorrent gets bound to
function Find-VpnAdapter {
    if ($Test) { return [pscustomobject]@{ Name = $Adapter; Ip = $AdapterIp } }
    $words = 'wireguard|wintun|nordlynx|tap-windows|tap-nordvpn|openvpn|hallow|nordvpn|protonvpn|mullvad|surfshark|expressvpn|windscribe|vpn'
    $up = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
    $pick = if ($Adapter) { $up | Where-Object { $_.Name -eq $Adapter -or $_.InterfaceDescription -eq $Adapter } | Select-Object -First 1 } else { $up | Where-Object { ($_.Name + ' ' + $_.InterfaceDescription) -match $words -and ($_.Name + ' ' + $_.InterfaceDescription) -notmatch 'hyper-v|virtualbox|vmware|tailscale' } | Select-Object -First 1 }
    if (-not $pick) { throw 'No connected VPN adapter found - connect your VPN first (or name it with -Adapter).' }
    $ip = (Get-NetIPAddress -InterfaceIndex $pick.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress
    if (-not $ip) { throw "$($pick.Name) has no IPv4 address - is the VPN really connected?" }
    [pscustomobject]@{ Name = $pick.Name; Ip = $ip }
}

# ---------------- the two legs
function Run-Hallow([string]$item) {
    if ($HallowReport) { $j = Get-Content $HallowReport -Raw | ConvertFrom-Json; return $j }
    if (-not (Test-Path $HallowExe)) { throw "Hallow Torrents isn't installed at $HallowExe." }
    $ping = HCmd 'ping'; if (-not $ping.ok) { throw 'Hallow Torrents isn''t running - start it first.' }
    if ((FromJson (HCmd 'status').text).vpnLocked) { throw 'Hallow Torrents is locked - your VPN isn''t connected.' }
    Say "Hallow Torrents: downloading $item ..."
    $r = HCmd ('speedtest ' + $item); if (-not $r.ok) { throw "Hallow Torrents couldn't start: $($r.text)" }
    $deadline = (Get-Date).AddMinutes($MaxMinutes)
    while ($true) {
        Start-Sleep -Seconds 5; $s = FromJson (HCmd 'speedtest status').text; if (-not $s) { continue }
        Say ("  Hallow {0,6:0.0}%  avg {1,6:0.0} MB/s  peers {2}" -f $s.progress, $s.avgMBps, $s.peersMax)
        if (-not $s.running) { break }
        if ((Get-Date) -gt $deadline) { [void](HCmd 'speedtest stop'); Start-Sleep 3; $s = FromJson (HCmd 'speedtest status').text; break }
    }
    $s
}
function Run-Qbit([string]$item, $vpn) {
    if (-not $Test -and -not (Get-Process qbittorrent -ErrorAction SilentlyContinue)) { throw 'qBittorrent isn''t running - start it (with its Web UI on) first.' }
    Qbit-SignIn
    $prefs = QJson '/api/v2/app/preferences'; $oldIface = [string]$prefs.current_network_interface; $oldAddr = [string]$prefs.current_interface_address
    # qBittorrent's own id for the adapter (its interface list: name = what Windows shows, value = qBittorrent's id)
    $list = @(); try { $list = QList '/api/v2/app/networkInterfaceList' } catch { }
    $iface = ($list | Where-Object { $_.name -eq $vpn.Name } | Select-Object -First 1).value
    if (-not $iface) { throw "qBittorrent doesn't list the adapter $($vpn.Name) - pick it by hand in qBittorrent (Tools > Options > Advanced > Network interface) and run this again with -Adapter." }
    $tag = 'hallow-race-' + (Get-Date -Format 'yyyyMMddHHmmss'); $save = Join-Path $OutDir 'qBittorrent'; New-Item -ItemType Directory -Force $save | Out-Null
    $result = $null
    try {
        Say "qBittorrent: bound to $($vpn.Name) for the race (was: $(if ($oldIface) { $oldIface } else { 'any interface' }))"
        Qbit-Bind $iface $vpn.Ip
        $check = QJson '/api/v2/app/preferences'
        if ([string]$check.current_network_interface -ne $iface) { throw 'qBittorrent didn''t take the VPN adapter binding - not racing unbound.' }
        Qbit-Add $item $save $tag
        $proc = if ($Test) { $null } else { Get-Process qbittorrent -ErrorAction SilentlyContinue | Select-Object -First 1 }
        $clock = [Diagnostics.Stopwatch]::StartNew(); $samples = New-Object Collections.ArrayList; $deadline = (Get-Date).AddMinutes($MaxMinutes); $hash = $null
        while ((Get-Date) -lt $deadline) {
            $rows = QList ('/api/v2/torrents/info?tag=' + $tag); $row = $rows | Select-Object -First 1
            if ($row) {
                $hash = $row.hash; $cpu = if ($proc) { $proc.Refresh(); $proc.TotalProcessorTime.TotalSeconds } else { 0 }
                $smp = Read-QbitSample $row $clock.Elapsed.TotalSeconds $cpu; [void]$samples.Add($smp)
                if ($samples.Count % 5 -eq 1) { Say ("  qBittorrent {0,6:0.0}%  {1,6:0.0} MB/s  peers {2}  {3}" -f ($smp.Progress * 100), ($row.dlspeed / 1MB), $smp.Peers, $smp.State) }
                if ($smp.Progress -ge 1) { break }
            }
            Start-Sleep -Seconds $PollSeconds
        }
        $result = Get-RaceSummary $samples
        $result | Add-Member -NotePropertyName State -NotePropertyValue $(if ($samples.Count -gt 0 -and $samples[-1].Progress -ge 1) { 'done' } else { 'stopped' })
        if ($hash) {
            # paused and taken out of qBittorrent's list - the files stay where they are
            try { [void](Q 'POST' '/api/v2/torrents/stop' @{ hashes = $hash }) } catch { try { [void](Q 'POST' '/api/v2/torrents/pause' @{ hashes = $hash }) } catch { } }
            [void](Q 'POST' '/api/v2/torrents/delete' @{ hashes = $hash; deleteFiles = 'false' })
        }
    }
    finally {
        try { Qbit-Bind $oldIface $oldAddr; Say "qBittorrent: network interface put back ($(if ($oldIface) { $oldIface } else { 'any interface' }))" } catch { Say "NOTE: couldn't put qBittorrent's network interface back - set it in Tools > Options > Advanced. ($_)" }
    }
    $result
}

# ---------------- the race
Write-Host 'Hallow Torrents vs qBittorrent - you started this race. Your VPN must be connected; both downloads go through it.'
$vpn = Find-VpnAdapter; Say "VPN adapter: $($vpn.Name)"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$item = $Torrent; $h = $null; $q = $null
$order = if ($QbitFirst) { @('qbit', 'hallow') } else { @('hallow', 'qbit') }
foreach ($leg in $order) {
    if ($leg -eq 'hallow' -and -not $SkipHallow) {
        $h = Run-Hallow $item
        # qBittorrent gets exactly the same torrent: Ubuntu's .torrent that Hallow fetched (its "source"), or your magnet / file
        if ($item -eq 'ubuntu' -and $h.source) { $item = $h.source }
    }
    elseif ($leg -eq 'hallow' -and $HallowReport) { $h = Get-Content $HallowReport -Raw | ConvertFrom-Json; if ($item -eq 'ubuntu' -and $h.source) { $item = $h.source } }
    elseif ($leg -eq 'qbit') {
        if ($item -eq 'ubuntu') { throw 'With -QbitFirst, give -Torrent (a magnet link or .torrent file) - Ubuntu''s torrent is fetched by Hallow Torrents.' }
        $q = Run-Qbit $item $vpn
    }
}
$hs = [pscustomobject]@{ AvgMBps = [double]$h.avgMBps; PeakMBps = [double]$h.peakMBps; Seconds = [double]$h.activeSeconds; PeersMax = [int]$h.peersMax; CpuPct = [double]$h.cpuPct; State = [string]$h.state }
$cmp = Compare-Race $hs $q
$rows = @(
    [pscustomobject]@{ Client = 'Hallow Torrents'; 'Avg MB/s' = $hs.AvgMBps; 'Peak MB/s' = $hs.PeakMBps; 'Time (s)' = $hs.Seconds; 'Peers (max)' = $hs.PeersMax; 'CPU % of PC' = $hs.CpuPct; State = $hs.State }
    [pscustomobject]@{ Client = 'qBittorrent'; 'Avg MB/s' = $q.AvgMBps; 'Peak MB/s' = $q.PeakMBps; 'Time (s)' = $q.Seconds; 'Peers (max)' = $q.PeersMax; 'CPU % of PC' = $q.CpuPct; State = $q.State })
$rows | Format-Table -AutoSize | Out-String | Write-Host
Say ("Faster: {0} (x{1}); Hallow Torrents ran at {2}% of qBittorrent's average." -f $cmp.Faster, $cmp.Ratio, $cmp.HallowPctOfQbit)
$stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
[pscustomobject]@{ when = (Get-Date).ToString('s'); torrent = $(if ($Torrent -match '^magnet:') { 'magnet' } else { Split-Path $item -Leaf }); order = ($order -join ','); hallow = $hs; qbittorrent = $q; result = $cmp } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir "race-$stamp.json") -Encoding UTF8
$md = "| Client | Avg MB/s | Peak MB/s | Time (s) | Peers (max) | CPU % of PC |`n|---|---|---|---|---|---|`n" + (($rows | ForEach-Object { "| $($_.Client) | $($_.'Avg MB/s') | $($_.'Peak MB/s') | $($_.'Time (s)') | $($_.'Peers (max)') | $($_.'CPU % of PC') |" }) -join "`n")
Set-Content (Join-Path $OutDir "race-$stamp.md") $md -Encoding UTF8
Say "Saved: $(Join-Path $OutDir "race-$stamp.json")"
Say 'Both downloads are still on disk (Hallow Torrents: its Speed test folder; qBittorrent: the qBittorrent folder above) - delete them when you''re done.'
exit 0
