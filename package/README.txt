HALLOW TORRENTS - a game-aware torrent client that only runs over your VPN

Torrenting itself is legal. Downloading or sharing copyrighted material without permission isn't.
The VPN lock is for privacy - it doesn't change what the law allows.

HOW TO INSTALL
1. Right-click the zip > Properties > tick "Unblock" > OK
2. Right-click the zip > Extract All
3. Double-click INSTALL.bat (no admin needed)

VPN REQUIRED - ALWAYS
- Torrents only run while Hallow VPN is connected, or while the VPN adapter you picked (NordVPN app:
  NordLynx / OpenVPN TAP, WireGuard, ...) is up. There is no switch to turn this off.
- Every connection (peers, trackers, DHT, even tracker name lookups) is bound to the VPN's address, so
  nothing can leave through your normal connection. If the VPN drops or changes, torrents stop within a
  second and start again when it's back.
- Port forwarding (UPnP / NAT-PMP) is always off. NordVPN doesn't offer port forwarding either, so fewer
  peers can connect to you - downloads still work, that's normal.

WHAT IT DOES
- Add .torrent files or magnet links, pick files and folders, a queue, pause / resume, speed limits
  (overall, per torrent, by time of day), ratio and seeding-time limits, categories, DHT / PEX / LPD,
  encryption, sequential download, super-seeding, force recheck, watch folder, IP blocklist, RSS,
  torrent creation, statistics.
- While you play (Delta Force, Discovery, Hallow Hub's game, your own list): pauses or limits torrents.
- Finished downloads are scanned by Microsoft Defender; suspicious files (programs in a video download,
  "movie.mp4.exe", shortcuts) get a warning. Downloads are never opened by themselves.
- Optional media library hand-off with Jellyfin-friendly names (e.g. to a NAS share).
- Opens .torrent files and magnet links only after you say yes (for your user only).

WHAT'S NEW IN 1.1
- RSS auto-download: feeds checked over the VPN only, rules (words or regular expressions, category,
  folder, prefer 1080p, avoid CAM / TS), episode tracking so nothing is grabbed twice, optional upgrades.
- Create torrents from a file or folder (piece size, trackers, web seeds, private, comment, source) and seed.
- Edit trackers, rename files and folders, move storage, set priorities for all files at once.
- IP blocklist from an address (P2P / eMule .dat, .gz or .zip), downloaded over the VPN every N days.
- Statistics: session and all-time totals, ratio, time seeding, time waiting for the VPN, peers blocked.
- Hallow Browser can hand over magnet links with a category.

WHAT'S NEW IN 1.0
- The first version.

CREDITS
MonoTorrent 3.0.2 and its dependencies (MIT) - see the licenses folder.
Icons: Font Awesome Free 7.3.1 by Fonticons, Inc. (https://fontawesome.com), licensed CC BY 4.0 - see FontAwesome-LICENSE.txt.
