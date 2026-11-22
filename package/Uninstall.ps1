# Hallow Torrents uninstaller (run from Windows Settings > Apps, or directly). Your downloads are never touched.
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Torrents'
Add-Type -AssemblyName System.Windows.Forms
if ([System.Windows.Forms.MessageBox]::Show("Uninstall Hallow Torrents?`n`nYour downloaded files stay where they are.", 'Uninstall Hallow Torrents', 'YesNo', 'Question') -ne 'Yes') { return }
if (Get-Process HallowTorrents -ErrorAction SilentlyContinue) { try { & (Join-Path $dest "HallowTorrents.exe") --cmd quit | Out-Null; Start-Sleep -Milliseconds 2500 } catch { } }
Get-Process HallowTorrents -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HallowTorrents' -ErrorAction SilentlyContinue
# the .torrent / magnet handler - only where it points at Hallow Torrents
$cmd = (Get-ItemProperty 'HKCU:\Software\Classes\magnet\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
if ($cmd -and $cmd -like '*HallowTorrents.exe*') { Remove-Item 'HKCU:\Software\Classes\magnet' -Recurse -ErrorAction SilentlyContinue }
if ((Get-ItemProperty 'HKCU:\Software\Classes\.torrent' -ErrorAction SilentlyContinue).'(default)' -eq 'HallowTorrents.torrent') { Remove-ItemProperty 'HKCU:\Software\Classes\.torrent' -Name '(default)' -ErrorAction SilentlyContinue }
Remove-ItemProperty 'HKCU:\Software\Classes\.torrent\OpenWithProgids' -Name 'HallowTorrents.torrent' -ErrorAction SilentlyContinue
foreach ($k in 'HKCU:\Software\Classes\HallowTorrents.torrent', 'HKCU:\Software\Classes\HallowTorrents.magnet', 'HKCU:\Software\HallowTorrents') { Remove-Item $k -Recurse -ErrorAction SilentlyContinue }
Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'Hallow Torrents' -ErrorAction SilentlyContinue
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) { Remove-Item (Join-Path $dir 'Hallow Torrents.lnk') -ErrorAction SilentlyContinue }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowTorrents' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'HallowTorrents') -Recurse -Force -ErrorAction SilentlyContinue
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show("Hallow Torrents is uninstalled. Your downloads are still in their folders.", 'Hallow Torrents') | Out-Null
