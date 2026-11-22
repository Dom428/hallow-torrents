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

WHAT'S NEW IN 1.8
- For Hallow Remote and Hallow Hub: a new pipe command "categories" lists your category names (for the
  phone's add-magnet picker), and searches can say who asked ("--client <id>") so two phones searching
  at once no longer lose each other's results. The Search page and each web-interface sign-in keep their
  own results too. Nothing else changes; the VPN lock is untouched.

WHAT'S NEW IN 1.7
- Plex and Emby too: the page is now "Media server / NAS" with a choice of Jellyfin, Emby, Plex or None
  (just copy). 1.6 settings carry on as Jellyfin. Emby: same names as Jellyfin, its address and an API
  key (Dashboard > Advanced > Security > Add). Plex: Plex's names (TV "Show (Year)\Season 01\Show (Year) -
  s01e02"), its address (port 32400) and your X-Plex-Token - in Plex Web open a movie > ... > Get Info >
  View XML; the new tab's address ends with X-Plex-Token=... - then Load libraries and pick Movies and TV.
  Plex scans just the new folder. Keys / tokens are kept encrypted; private addresses only.

WHAT'S NEW IN 1.6
- Send to Jellyfin (the new "Jellyfin / NAS" page): finished downloads that passed Defender's scan are
  copied to a folder on your NAS (\\192.168.4.197\media) with Jellyfin's names, checked (size and
  checksum) and resumed after interruptions. Per category: copy, move after seeding, or move now (the PC's
  copy goes to the Recycle Bin). Home network / Tailscale only - never through the VPN, never a public
  address. No NAS password is stored: map the share once in File Explorer. Optional Jellyfin address and
  API key (encrypted) so Jellyfin sees new downloads straight away.

WHAT'S NEW IN 1.5
- Performance profiles (Settings > Performance): Balanced (default) talks to more peers at once and keeps
  a bigger disk cache than 1.4; Max speed goes further; Light is for older or busy PCs. While a game runs
  and "While you play" limits the speed, a small gaming profile is used. Advanced numbers if you want them.
  None of this touches the VPN lock - every connection is still bound to the VPN.
- Every finished download is rechecked before it counts as finished (MonoTorrent could leave a small
  block unwritten in torrents with many small files); bad pieces are downloaded again.
- "Prefer encryption" encrypts the handshake first, like qBittorrent - same peers, less CPU.
- Socket watchdog (always on): every 3 seconds it checks this app's own connections in Windows' tables;
  one on any address other than the VPN's stops all torrents at once with a red alert.
- "Speed & leaks" page: a speed test with a legal torrent you pick (Ubuntu's current image is one click -
  its .torrent comes from releases.ubuntu.com over the VPN, only when you click), and a leak drill that
  disconnects your VPN on purpose while downloading and proves torrents locked within 2 seconds with no
  connection outside the VPN, then resumes.
- tools\speedtest.ps1 and tools\race.ps1 (in the install folder): the speed test from a script, and a race
  against your own qBittorrent on the same torrent (qBittorrent is bound to the VPN adapter for the race).
  You start them, with your VPN connected.
WHAT'S NEW IN 1.4
- Web interface for your phone or another PC (off by default): on the "Radarr / Sonarr" page turn on
  the Web API and "Serve the web interface at /ui/", then open http://localhost:8080/ui/ (or this PC's
  home-network / Tailscale address on your phone). The list, add a magnet or a .torrent file, pause /
  resume, categories, search, statistics and Connect VPN. Same sign-in and security as the Web API -
  never on the internet. Torrents added there still wait for the VPN.
- Your own Prowlarr / Jackett at a private address (this PC, your NAS, Tailscale) is now reached directly
  instead of through the VPN - your ISP never sees that traffic. Only for the indexers you added, only if
  every address the name resolves to is private, and a redirect to a public address still goes through
  the VPN (or nowhere while it's off). Trackers, peers, RSS and blocklists keep the VPN rule.
- NAS / headless mode: HallowTorrentsHeadless.exe runs the engine and the web interface with no window
  on an always-on Windows box. For a Linux NAS see NAS-MODE.md (gluetun + qBittorrent is recommended).
- For Hallow Remote: the pipe answers list, stats, pause <hash>, resume <hash> and add --direct
  (REMOTE-CONTRACT.md).
WHAT'S NEW IN 1.3
- Search: add your own Prowlarr or Jackett (or a Torznab feed) with its address and API key on the new
  Search page (Indexers...). The key is kept encrypted for your Windows user. No built-in site list, no
  scraping of torrent sites.
- Every search goes out over the VPN only - while it's off, nothing is sent. All indexers are asked at
  once, each with a timeout, and you see each one's status.
- Smart ranking: resolution, source, codec, HDR, audio and group are read from the names and scored on
  seeders, your preferences (Ranking...: e.g. 1080p WEB-DL, avoid CAM, max 8 GB), size and how reliable
  the indexer is. Likely fakes (a program in the files, "password" archives, sizes far too small,
  brand-new uploads with too many seeders, "codec packs") go to the bottom with the reason.
- Filters (quality, size, seeders, indexer, category), sorting, Download through the normal Add window
  (VPN lock, Defender scan, categories) and "Add to RSS rule".

WHAT'S NEW IN 1.2
- Works with Radarr, Sonarr and Prowlarr: turn on the Web API on the new "Radarr / Sonarr" page, set a
  username and password, then add a "qBittorrent" download client in the app (host localhost, port 8080,
  category radarr / tv-sonarr). Off by default; this PC only unless you pick a home-network or Tailscale
  address; sign-ins expire; 5 wrong passwords lock that address out; never on the internet.
- Torrents the apps send wait for the VPN like any other and show as queued (not failed) while it's off.
  Finished downloads stay where they are for the app to import, and seeding stops at the app's goal.
- Radarr on a NAS? See "Use with Radarr/Sonarr/Prowlarr" in README.md (remote path mapping).

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
