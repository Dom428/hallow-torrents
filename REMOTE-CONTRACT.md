# Hallow Torrents - the pipe contract for Hallow Remote (and Hallow Browser)

Hallow Torrents 1.4 answers everything a phone page needs over the family pipe. A later Hallow Remote update can add a **Torrents** tab by proxying these commands - Remote itself doesn't change in 1.4.

## Transport

- Pipe `\\.\pipe\HallowFamily.Torrents.<Windows user name>` - one UTF-8 line in, one line out (`\n`), then the pipe closes. Only the same Windows user on the same PC can open it (no network access).
- Or `HallowTorrents.exe --cmd "<line>"` from a script: prints the answer, exit code `0` = `ok`, `1` = `error`, `2` = Hallow Torrents isn't running.
- Answers start with `ok` (maybe followed by a space and JSON) or `error <message for people>`.
- The same commands work against the windowed app and the headless copy (`HallowTorrentsHeadless.exe` / `HallowTorrents.exe --headless`).
- Field names are stable from 1.4 on; new fields may be added later, so ignore ones you don't know. Speeds are bytes per second, sizes bytes, times seconds, `progress` is 0..1.

## The VPN lock (never relaxed by anything here)

Torrents only ever run over the VPN. A torrent added or resumed from a phone **waits for the VPN** like any other: `vpnLocked: true` and `status: "waiting-vpn"` until it's connected. Remote should show that plainly and offer Hallow VPN's own `connect fastest` (Remote already talks to Hallow VPN) - Hallow Torrents' pipe doesn't connect the VPN for you; the web interface's "Connect VPN" button does the same `connect fastest` call.

## Commands

| Command | Answer |
|---|---|
| `ping` | `ok {"app":"Torrents","version":"1.4"}` (the headless copy adds `"headless":true`) |
| `status` | `ok {...}` counts and speeds only - no names, hashes or addresses (also in `%APPDATA%\HallowTorrents\status.json`). 1.4 adds `webUi` and `headless`. |
| `list` | `ok {list}` - see below |
| `stats` | `ok {stats}` - see below |
| `add <magnet> [--category <name>] [--direct]` | `ok {"id":"<hash>","asking":false,"category":"Movies"}`. **Use `--direct` from a phone**: it adds straight away. Without it the app may open its Add window on the PC (`"asking":true`, `"id"` missing) - that's for Hallow Browser on the same PC. Only `magnet:` links and existing `.torrent` paths are accepted; `--category` must be one of your categories (case doesn't matter; the answer has its real name). Errors: `error only magnet links and .torrent files can be added`, `error no category called X (Movies, TV, ...)`, `error That torrent is already in the list.` |
| `pause <hash>` / `resume <hash>` | `ok {"hash":"<full hash>","paused":true,"vpnLocked":false}`. `<hash>` = the info hash from `list`, or a unique start of it (8+ hex characters). Errors: `error no torrent with that hash`, `error give the torrent's info hash (from list)`, `error more than one torrent starts with that - give more of the hash` |
| `pause-all` / `resume-all` | `ok` |
| `search <words> [--n 10] [--cat movies\|tv\|music\|games\|software\|books\|other]` | `ok {search}` - see below. `show s01e02` searches that episode, `tt0111161` a movie by IMDb id. Can take up to ~20 s (each indexer has a 15 s timeout): use a 45 s pipe timeout. `error waiting for the VPN - searches only go out over it (nothing was sent)` while locked and no local indexer manager is set up. |
| `search-add <id> [--category <name>]` | the same answer as `add`. `<id>` comes from the last `search` (ids live until the next search). |
| `menu` / `invoke <id>` | Hallow Hub's generic tray menu (`pause-all`, `resume-all`, `add-magnet`, `connect-vpn`, `settings`) |
| `open [transfers\|search\|rss\|stats\|apps\|settings]` | opens the window on the PC (`error` on the headless copy) - not for phones |

### `list`

```json
{
  "vpnLocked": false,
  "held": null,
  "game": null,
  "downBps": 524288,
  "upBps": 81920,
  "count": 1,
  "torrents": [
    {
      "hash": "0123456789abcdef0123456789abcdef01234567",
      "name": "Made.Up.Movie.2024.1080p.WEB-DL",
      "progress": 0.4213,
      "downBps": 524288,
      "upBps": 81920,
      "state": "Downloading",
      "status": "downloading",
      "category": "Movies",
      "size": 2147483648,
      "done": 904726528,
      "eta": 2369,
      "seeds": 12,
      "peers": 3,
      "ratio": 0.09,
      "paused": false,
      "complete": false,
      "flagged": false,
      "vpnLocked": false
    }
  ]
}
```

- `held`: `null`, `"vpn"` (the VPN is off - nothing runs) or `"game"` (paused while a game runs; `game` has its name).
- `state` is Hallow's own wording (show it as is): `Downloading`, `Seeding`, `Waiting for VPN`, `Paused`, `Paused for game`, `Queued`, `Getting info`, `Checking`, `Starting`, `Done`, `Done (limit reached)`, `Error`.
- `status` is a stable code for colours / icons: `downloading`, `seeding`, `waiting-vpn`, `paused`, `game`, `queued`, `metadata`, `checking`, `starting`, `done`, `error`.
- `eta` is `null` when unknown or finished. `flagged`: Microsoft Defender found something or a file looks suspicious - show "Check before opening on the PC", never offer to open it.
- A torrent's `vpnLocked` is true while the engine is locked and it isn't paused by you (it will start when the VPN is back).
- Sorted: unfinished first in queue order, then finished ones.

### `stats`

```json
{
  "session": { "down": 904726528, "up": 81920000, "ratio": 0.091, "seedSecs": 3600, "lockedSecs": 120, "blocked": 0 },
  "allTime": { "down": 52613349376, "up": 61203283968, "ratio": 1.163, "seedSecs": 864000, "lockedSecs": 7200, "blocked": 14 },
  "since": "2026-11-21T22:13:05",
  "sessionStart": "2026-11-22T09:00:00",
  "downBps": 524288,
  "upBps": 81920,
  "torrents": 7,
  "downloading": 2,
  "seeding": 3,
  "vpnLocked": false,
  "blocklistRanges": 0
}
```

`lockedSecs` = time torrents waited because the VPN was off; `blocked` = peers the IP blocklist refused. Times are local time, `yyyy-MM-ddTHH:mm:ss`.

### `search`

```json
{
  "query": "night sky s01e01", "mode": "tv", "locked": false, "ms": 2140, "total": 9, "count": 2,
  "indexers": [
    { "name": "Home Prowlarr", "state": "ok", "count": 9, "ms": 640, "error": null, "local": true }
  ],
  "results": [
    {
      "id": "r1", "title": "Night.Sky.S01E01.1080p.WEB-DL.DDP5.1.H.264-NTb", "indexer": "Home Prowlarr",
      "size": 3040870400, "seeders": 812, "peers": 950, "score": 179.2,
      "resolution": "1080p", "source": "WEB-DL", "codec": "x264", "hdr": "", "audio": "DD+", "group": "NTb",
      "season": 1, "episode": 1, "category": "TV", "published": "2026-11-21T14:00:00",
      "flags": [], "notes": [], "magnet": null, "infoHash": "aa11...", "inList": false
    }
  ]
}
```

- Results are ranked best first; likely fakes are last with `flags` saying why - show those greyed out with the reason.
- `indexers[].state`: `ok`, `timeout`, `error` (with `error`), `locked` (waiting for the VPN). `local: true` = an indexer manager at a private address reached directly (1.4) - show "Local server - not through VPN (private address)".
- API keys never appear in any answer.

## The web interface (for comparison)

The 1.4 web interface (`http://<address>:<port>/ui/`, off by default) uses the same shapes: `GET /ui/api/list` = `list`, `GET /ui/api/stats` = `stats`, `POST /ui/api/search` (`q`, `cat`, `n`) = `search` plus `ok` / `error`. It needs the Web API sign-in and an `X-CSRF-Token` header on every POST - Remote should use the pipe, not the web interface.
