# Hallow Torrents installer: copies the app (and its MonoTorrent DLLs) to your user folder (no admin needed) and adds Start menu +
# Desktop shortcuts. It does NOT make itself the handler for .torrent files / magnet links - the app asks you first.
#   Install.ps1 [-NoStart]   (-NoStart: don't open the app afterwards)
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Torrents'
New-Item -ItemType Directory -Force $dest | Out-Null
$wasRunning = [bool](Get-Process HallowTorrents -ErrorAction SilentlyContinue)
if ($wasRunning) { try { $old = Join-Path $dest "HallowTorrents.exe"; if (Test-Path $old) { & $old --cmd quit | Out-Null; Start-Sleep -Milliseconds 2500 } } catch { } }   # stops the torrents cleanly first
Get-Process HallowTorrents -ErrorAction SilentlyContinue | Stop-Process -Force
# 1.4: the headless copy (NAS mode) - stopped the same way and started again afterwards if it was running
$headlessWas = [bool](Get-Process HallowTorrentsHeadless -ErrorAction SilentlyContinue)
if ($headlessWas) { try { $old = Join-Path $dest "HallowTorrents.exe"; if (Test-Path $old) { & $old --cmd quit | Out-Null; Start-Sleep -Milliseconds 2500 } } catch { } }
Get-Process HallowTorrentsHeadless -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Copy-Item (Join-Path $PSScriptRoot 'HallowTorrents.exe') $dest -Force
if (Test-Path (Join-Path $PSScriptRoot 'HallowTorrentsHeadless.exe')) { Copy-Item (Join-Path $PSScriptRoot 'HallowTorrentsHeadless.exe') $dest -Force }
Copy-Item (Join-Path $PSScriptRoot '*.dll') $dest -Force
foreach ($f in 'README.txt', 'Uninstall.ps1', 'FontAwesome-LICENSE.txt', 'NAS-MODE.md', 'REMOTE-CONTRACT.md') { Copy-Item (Join-Path $PSScriptRoot $f) $dest -Force -ErrorAction SilentlyContinue }
# 1.5: the speed test and race scripts (you start them yourself)
if (Test-Path (Join-Path $PSScriptRoot 'tools')) { New-Item -ItemType Directory -Force (Join-Path $dest 'tools') | Out-Null; Copy-Item (Join-Path $PSScriptRoot 'tools\*.ps1') (Join-Path $dest 'tools') -Force }
if (Test-Path (Join-Path $PSScriptRoot 'licenses')) { New-Item -ItemType Directory -Force (Join-Path $dest 'licenses') | Out-Null; Copy-Item (Join-Path $PSScriptRoot 'licenses\*') (Join-Path $dest 'licenses') -Force }
$exe = Join-Path $dest 'HallowTorrents.exe'
$u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowTorrents'
New-Item $u -Force | Out-Null
$ver = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0\.0$', ''
$vals = @{ DisplayName = 'Hallow Torrents'; DisplayVersion = $ver; Publisher = 'Hallow'; DisplayIcon = $exe; InstallLocation = $dest
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dest 'Uninstall.ps1')`""
    URLInfoAbout = 'https://github.com/Dom428/hallow-torrents' }
foreach ($k in $vals.Keys) { Set-ItemProperty $u -Name $k -Value $vals[$k] }
Set-ItemProperty $u -Name NoModify -Value 1 -Type DWord; Set-ItemProperty $u -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $u -Name EstimatedSize -Value ([int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord
$w = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $s = $w.CreateShortcut((Join-Path $dir 'Hallow Torrents.lnk'))
    $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'A game-aware torrent client that only runs over your VPN'; $s.Save()
}
if ($headlessWas) { Start-Process (Join-Path $dest 'HallowTorrentsHeadless.exe') }
if ($NoStart) { Write-Host "Hallow Torrents is installed. It's in your Start menu and on your Desktop."; return }
if ($wasRunning) { Start-Process $exe -ArgumentList '--tray' } else { Start-Process $exe }
Write-Host "Hallow Torrents is installed. It's in your Start menu and on your Desktop."
