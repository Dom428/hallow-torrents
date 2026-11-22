# Hallow Torrents

A game-aware torrent client for Windows, built on [MonoTorrent](https://github.com/alanmcgovern/monotorrent), that **only ever runs over your VPN**. Part of the Hallow family.

> **The legal bit:** torrenting itself is legal. Downloading or sharing copyrighted material without permission isn't. The VPN lock is for privacy - it doesn't change what the law allows.

![Transfers](docs/transfers.png)

## Install

Download `Hallow.Torrents.zip` from the [latest release](https://github.com/Dom428/hallow-torrents/releases/latest), right-click it > Properties > Unblock, Extract All, double-click `INSTALL.bat` (no admin needed) - or install it from Hallow Setup.

## VPN required - always

- Torrents only run while **Hallow VPN** says it's connected, or while **the VPN adapter you picked** (for the NordVPN app: NordLynx or its OpenVPN TAP adapter; WireGuard; any other VPN app) is up with an address. There is no setting to turn this off.
- **Every socket is bound to the VPN's address:** the peer listener, outgoing peer connections, DHT, HTTP(S) and UDP trackers (also the ones you add by hand), web seeds, RSS feeds and blocklist downloads. Tracker and DHT-router names are looked up with Hallow Torrents' own DNS query sent from the VPN address to the VPN's DNS servers - never Windows' resolver. Windows only sends traffic from an address through the adapter that owns it, so nothing can leave through your normal connection; if the VPN adapter disappears, the sockets can't even be opened.
- **If the VPN drops or its address changes, the engine is stopped (not just paused) within a second**, with a clear banner, and started again - bound to the new address - once the VPN is back. The banner's **Connect Hallow VPN** button asks Hallow VPN to connect to the fastest server.
- **Port forwarding (UPnP / NAT-PMP) is always off** - it would open a port on your home router, outside the VPN. NordVPN doesn't offer port forwarding either, so fewer peers can connect to you; downloads still work, that's normal. IPv6 peers aren't used.
- **Check my torrent IP** (Settings > VPN, only when you click it) opens ipleak.net's torrent address check in your browser, so you can see the address a tracker sees.

![The VPN lock](docs/vpn-locked.png)

## What it does

- **The basics:** add a .torrent file or a magnet link (or drop it on the window); pick the files and the folder; a queue (N downloads at once, move up / down); pause and resume; download and upload limits overall and per torrent; ratio and seeding-time limits (and a ratio goal per category); DHT, peer exchange and local peer discovery, each switchable; encryption preferred or required; a list with progress, speed, ETA, peers and ratio, and a details panel (general, files with Skip / Low / Normal / High, safety & library).
- **More:** sequential download to preview videos while they download, super-seeding, force recheck, categories with their own save folders, a speed schedule by the time of day, a watch folder for .torrent files.
- **RSS auto-download** (new in 1.1): feeds with their own refresh interval; rules with must contain / must not contain (words, `|` for "or", `*`) or regular expressions, a category, a save folder, a quality preference (prefer 1080p) and words to avoid (CAM, TS...). **Episode tracking** (S01E02, 1x02, dates, season packs - and movies by title and year) never grabs the same episode twice; optionally a better quality replaces it within N hours. The rule window previews what it would grab. Feeds are only fetched over the VPN - nothing is checked while it's off.
- **Create torrents** (new in 1.1): from a file or a folder, piece size automatic or your own, trackers (tiers), web seeds, private, comment and source - and start seeding it straight away.
- **Edit torrents** (new in 1.1): add, remove and edit trackers; rename files and folders (the files on disk follow, also while the VPN is off); move storage to another folder; priorities per file or for all files at once.
- **IP blocklist:** from a file, or (new in 1.1) from an address - P2P / PeerGuardian or eMule .dat, plain, .gz or .zip - downloaded over the VPN and updated every N days. Settings shows how many ranges are loaded.
- **Statistics** (new in 1.1): downloaded and uploaded this session and all-time, ratio, time seeding, time torrents waited for the VPN, and peers the blocklist refused - kept on this PC only.
- **Game-aware:** while Delta Force, Discovery, a game Hallow Hub says you're playing or one from your own list runs, it pauses everything (or limits it to N KB/s) and carries on when you stop.
- **Safety:** a finished download is scanned by Microsoft Defender (MpCmdRun custom scan at Idle priority; nothing is deleted by Hallow Torrents). Programs, scripts or shortcuts inside what looks like a video or music download, and disguised names like `movie.mp4.exe`, get a **Check before opening** badge. Downloads are never opened by themselves.
- **Media library hand-off** (per category, off until you set it up): when a download finishes it's copied - or moved and seeded from there - to your library folder (e.g. `\\NAS\media\Movies`) with names Jellyfin understands: `Title (Year)/Title (Year).mkv`, `Show/Season 01/Show - S01E02.mkv`. Flagged downloads are never handed off by themselves.
- **.torrent files and magnet links** can open in Hallow Torrents - for your Windows user only, and only after you say yes.

![RSS](docs/rss.png)

![Settings](docs/settings.png)

## Hallow family

- Pipe `HallowFamily.Torrents.<user>`, also `HallowTorrents.exe --cmd "<line>"`:
  - `ping`, `status` (counts and speeds, VPN lock state, game - never torrent names, hashes or addresses)
  - `add <magnet link or .torrent path> [--category <name>]` - for Hallow Browser and scripts: shows the Add window (or adds straight away if that's switched off); only `magnet:` links and existing `.torrent` files are accepted; `--category` picks one of your categories (and its folder)
  - `open [transfers|rss|stats|settings]`
  - `pause-all`, `resume-all`, `menu`, `invoke <id>`, `quit`
- `%APPDATA%\HallowTorrents\status.json`, Hallow Hub's one Hallow icon, the Hallow accent and light / dark mode, crash reports kept on this PC, updates from GitHub with a checksum check.

## Good to know

- Your list, settings, RSS feeds and rules (rss.json) and statistics (stats.json) are in `%APPDATA%\HallowTorrents`; downloads go to `Downloads\Hallow Torrents` unless you choose otherwise.
- Local peer discovery talks to your home network (not the internet) and can't be bound to the VPN, so it's off unless you switch it on.
- Built with the C# compiler in Windows (`build.ps1`). Third-party components and their licences: [licenses/THIRD-PARTY.md](licenses/THIRD-PARTY.md) - MonoTorrent 3.0.2 and its dependencies, all MIT.
- Tests (`build.ps1 -Tests`, `tests\TorrentsTests.exe`) only use a local swarm on this PC: a local tracker, a local seeder, a local web server for RSS and blocklists, and Hallow Torrents bound to a stand-in VPN address (127.0.0.3).

Pictures use made-up torrents.
