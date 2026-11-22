# Hallow Torrents roadmap

## Done

| Version | What |
|---|---|
| 1.0 | MonoTorrent 3.0.2 (MIT) engine. VPN required with no override: Hallow VPN's status.json / pipe (`connected`) plus its adapter, or a picked adapter (NordVPN app, WireGuard...); every socket bound to the VPN IP (peer listener, outgoing peers via a bound ISocketConnector, DHT endpoint + a DHT bootstrap that resolves its routers over the VPN, HTTP(S) trackers and web seeds via StandardSocketsHttpHandler's ConnectCallback with our own DNS-over-the-VPN, UDP trackers via our own bound BEP 15 client), IPv6 peers refused, UPnP / NAT-PMP off; the VPN re-checked every 250 ms and the engine stopped and disposed within 1 s when it drops or its IP changes, rebuilt when it's back; "Connect Hallow VPN" (`connect fastest`); optional "Check my torrent IP" (ipleak.net, on click). Add .torrent / magnet (window, drop, pipe `add`, double-click after you allow the per-user handler), file picking and priorities, save folder, queue, pause / resume, global / per-torrent / scheduled speed limits, ratio and seeding-time limits, per-category ratio goals and folders, DHT / PEX / LPD switches, encryption prefer / require, sequential download, super-seeding, force recheck, watch folder, IP blocklist (eMule / P2P). Game-aware pause or limit (Delta Force, Discovery, Hub's playing, own list). Defender scan of finished downloads at Idle priority, suspicious-file flags, never opens files. Media library hand-off (copy or move-and-seed) with Jellyfin names. Family link, status.json, Hub tray host, accent, light / dark, crash reports, updater. Local-swarm tests including leak tests. |

## Later

| Idea | Why it isn't in 1.0 |
|---|---|
| RSS feeds with auto-download rules (include / exclude filters, episode tracking so duplicates aren't grabbed) | 1.1. The feed fetches must go through the VPN-bound HTTP client too; the episode parser from the library hand-off is ready for it. |
| Blocklist from a URL with automatic updates | 1.1 (over the VPN). 1.0 loads a file you saved. |
| "Play while downloading" button that opens the player | 1.0 has sequential download; it never opens files by itself, so playing is up to you. |
| Hallow Remote control | The pipe already has `status`, `add`, `pause-all`, `resume-all`; Remote can show it later. |
| Port forwarding through a VPN that offers it | NordVPN doesn't; UPnP on the home router would bypass the VPN, so it stays off. |
| Bind local peer discovery to the VPN | LPD is multicast on the home network; it's off by default instead. |
| Torrent creation, web UI | Not needed for 1.0. |
