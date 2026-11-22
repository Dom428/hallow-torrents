# Hallow Torrents - NAS / headless mode

Hallow Torrents 1.4 can run **without a window**: the engine, RSS, search, the family pipe and the Web API / web interface, nothing else. This page says what works today, what doesn't, and what to use on a Linux NAS. Short version:

- **On any Windows machine** (this PC, a mini PC, a Windows "NAS"): use `HallowTorrentsHeadless.exe`. It ships in 1.4 and is tested.
- **On a Linux NAS** (Synology, Unraid, TrueNAS, a Raspberry Pi...): Hallow Torrents does **not** run there today. Use a proven headless client in Docker behind a VPN container - **gluetun + qBittorrent** (Option B below) - and point Radarr / Sonarr at it. That's the recommendation.

## What ships in 1.4 (Windows)

- `HallowTorrentsHeadless.exe` - built from the same source files as the app minus every window file. It references no WPF, WinForms or System.Drawing (the tests check the references and that no WPF / WinForms module is ever loaded).
- `HallowTorrents.exe --headless` - the same thing from the normal exe.
- Both use the same data folder (`%APPDATA%\HallowTorrents`), the same settings and the same single-instance lock: they won't start next to the windowed app or each other (exit code 3). They write `headless.log` in the data folder (no passwords or API keys).
- **The VPN lock is exactly the same**: Hallow VPN (or the adapter you picked) must be up on that machine, every socket is bound to its address, the engine stops within a second when it drops. There's no setting to turn it off, headless or not.
- Control it with the **web interface** (`http://<address>:<port>/ui/`), the **Web API** (Radarr / Sonarr / Prowlarr, unchanged) and the **pipe** (`HallowTorrents.exe --cmd "list"`, `quit`, ... - see REMOTE-CONTRACT.md). `open` answers an error: there's no window.

### Set it up on a Windows box

1. Install Hallow Torrents there as usual and open it once. On the **Radarr / Sonarr** page: set a username and password, turn on the Web API and **Serve the web interface**, and pick that box's **home-network or Tailscale address** under *Where it listens* (never 0.0.0.0, never public - the app refuses). Add your phone's / NAS's address to *Allowed addresses* if you like.
2. Set up the VPN on that box (Hallow VPN, or the NordVPN app / WireGuard + pick its adapter in Settings > VPN).
3. If you use Search: add your indexers there, **as the Windows user that will run the headless copy** - API keys are encrypted for that Windows user (DPAPI) and can't be read by another user or machine.
4. Quit the app (tray > Quit) and start `HallowTorrentsHeadless.exe` (in `%LOCALAPPDATA%\Programs\Hallow Torrents`). To start it with Windows, make a Task Scheduler task "At log on" for your user running that exe - Hallow Torrents doesn't create one for you.
5. Stop it with `HallowTorrents.exe --cmd quit`. Updating with the installer stops it and starts it again.

What you lose without a window: the Add window (adds from the pipe / web are direct), notifications (they go to `headless.log`), the tray menu and the update prompt (update by running the new installer). Defender scans still run (Windows), the game-aware pause still works if games run on that box.

## Option A - Hallow Torrents itself on Linux (Mono or .NET 8): not today

The question was whether the headless exe could run on a Linux NAS. **It is untested** - Mono and the .NET 8 SDK aren't installed here and weren't downloaded for this - so everything below is from reading the code and the libraries, not from running it.

**What looks possible.** MonoTorrent 3.0.2 targets .NET Standard and runs on Mono and .NET 8. The headless exe is a plain .NET Framework 4.8 assembly without WPF / WinForms, and Mono can often load csc-built assemblies like that as they are. The Web API / web interface are a hand-written HTTP server on a `TcpListener` - nothing Windows-specific.

**What would break or need work** (found in the code):

| Part | On Linux | Work needed |
|---|---|---|
| **The VPN lock** - "every socket bound to the VPN's address" | **Not enough on Linux.** Windows only sends traffic from an address out of the adapter that owns it (strong host model) - that's what makes the lock absolute here. Linux uses the weak host model: a socket bound to the VPN's address can still leave through the normal interface unless policy routing says otherwise. | Bind to the *interface* (`SO_BINDTODEVICE`, needs native calls and `CAP_NET_RAW`) for every socket (peers, trackers, DHT, DNS, RSS, blocklists, search), or rely on the VPN's own routing / kill switch. This is the main reason not to do it casually. |
| VPN detection | Reads Hallow VPN's `status.json` / pipe and Windows adapter names (NordLynx, TAP...). Hallow VPN doesn't exist on Linux. | A Linux VPN source (wg / tun interface state). |
| Family pipe | Uses Windows named-pipe ACLs (`PipeSecurity`) - not supported on Mono / .NET 8 on Linux. It would fail quietly (no pipe). | Unix socket with file permissions. |
| API keys (Search) | DPAPI (`ProtectedData`): Windows-only on .NET 8 (throws); Mono has its own key store - untested. | Another secret store, keys entered on the NAS. |
| JSON | `JavaScriptSerializer` (System.Web.Extensions) exists in Mono, **not** in .NET 8. | Port to System.Text.Json for .NET 8. |
| "Remove with files" | Uses the Windows Recycle Bin (Microsoft.VisualBasic). | A trash folder on the NAS. |
| Defender scan | No MpCmdRun - every scan ends as "couldn't scan" (downloads still finish). | ClamAV, optional. |
| Runtime | Mono is barely maintained (6.12 from 2021; now under the Wine project). .NET 8 needs a real port (SDK project, the API differences above). | - |

**Verdict:** possible as a real porting project (most of it in the VPN lock), not as "just run the exe on Mono". A Hallow Torrents that runs on Linux with a lock that is weaker than on Windows would break this app's one promise, so it isn't offered.

## Option B - gluetun + qBittorrent in Docker on the NAS (recommended for Linux)

A proven, maintained setup: **[gluetun](https://github.com/qdm12/gluetun)** is a VPN client container with a built-in kill switch (a firewall: nothing leaves the container's network except through the tunnel), and **qBittorrent** runs *inside gluetun's network*, so it can't reach the internet at all without the VPN. That's a stronger lock than socket binding, at the network level, and it's what most *arr setups use.

> Not deployed or tested from here - this is a starting point. Check gluetun's wiki for your VPN provider's exact variables.

```yaml
# docker-compose.yml on the NAS (example)
services:
  gluetun:
    image: qmcgaw/gluetun
    cap_add: [NET_ADMIN]
    devices: [/dev/net/tun:/dev/net/tun]
    environment:
      - VPN_SERVICE_PROVIDER=nordvpn
      - VPN_TYPE=wireguard                 # or openvpn (service credentials from your NordVPN account)
      - WIREGUARD_PRIVATE_KEY=...          # keep this file private
      - SERVER_COUNTRIES=Netherlands
      - FIREWALL_OUTBOUND_SUBNETS=192.168.1.0/24   # your home network, so the web UI is reachable from it
    ports:
      - 8080:8080                          # qBittorrent's web UI / API - home network only, never forwarded on the router
    restart: unless-stopped

  qbittorrent:
    image: lscr.io/linuxserver/qbittorrent
    network_mode: "service:gluetun"        # all its traffic goes through gluetun - no VPN, no internet
    environment:
      - PUID=1000
      - PGID=1000
      - WEBUI_PORT=8080
    volumes:
      - ./qbittorrent:/config
      - /volume1/downloads:/downloads
    depends_on: [gluetun]
    restart: unless-stopped
```

Then:

- **Radarr / Sonarr / Prowlarr**: add a qBittorrent download client with the NAS's address and port 8080 - exactly like Hallow Torrents' Web API, but no remote path mapping is needed when they share `/downloads`.
- **Check the lock** once: stop gluetun and confirm qBittorrent has no connection; from qBittorrent's container, an IP check should show the VPN's address.
- **From your phone**: qBittorrent's own web UI (`http://<nas>:8080`), on your home network or Tailscale.

**Where Hallow Torrents fits with Option B.** Hallow Torrents 1.4 can't yet show or control a qBittorrent on the NAS from its own window or web page - it speaks qBittorrent's API as a *server*, not as a client. Keep using Hallow Torrents on your PC for what it adds (game-aware pausing, Defender scans, the VPN lock on Windows, search with the local-server exception for a Prowlarr on the NAS), and let the NAS's qBittorrent do the always-on *arr downloads. A "remote qBittorrent" view in Hallow Torrents is a possible later item.

## Recommendation

1. **Linux NAS: Option B** (gluetun + qBittorrent). Maintained, a network-level kill switch, and Radarr / Sonarr already know it.
2. **Windows box that's always on: `HallowTorrentsHeadless.exe`** - works today with the full Hallow VPN lock, the web interface and the Web API.
3. Option A (Hallow Torrents on Mono / .NET 8) only as a deliberate port that keeps the lock absolute on Linux (interface binding, a Linux VPN source) - not before.
