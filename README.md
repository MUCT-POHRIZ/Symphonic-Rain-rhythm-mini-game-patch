# SR Perfect Patch

**English** | [简体中文](README.zh-CN.md)

A read-only memory overlay for the rhythm ("music action") mini-game in
**Symphonic Rain** (シンフォニック＝レイン, Kogado Studio).

The original never tells you *how* you were judged — only a score number and a
vague lamp. This tool reads the judgement the game itself just computed and draws
it on top of the game, so you can see `Perfect` / `Good (Early 37 ms)` / `Miss`,
the current combo, the live frame rate, and a full result card when the song ends.

> **It does not modify a single game file.** No patching on disk, no repacking,
> no added DLL in the game folder. Everything happens in RAM, and is reverted
> byte-for-byte when you disconnect or when the game exits.

![UI](preview/ui.png)

## Supported versions

| Version | Executable | Notes |
|---|---|---|
| Steam release (2023 re-release) | `SR.exe` — 3,371,472 bytes, MFC build | found automatically via the Steam library folders |
| 2007 pop/retail release | `SR.EXE` — 2,324,480 bytes, Delphi build (common crack names: `[交响乐之雨].SR_Cracked.exe`, `[交响乐之雨].SR.exe`) | start the game yourself, then press **Attach patch** |

Both builds are recognised by process name and then verified byte-by-byte at
every hook site. Anything that does not match is refused with
"bytes mismatch, nothing was written".

## Features

| Feature | Detail |
|---|---|
| Live judgement text | `Perfect` (gold) / `Good` (blue) / `Miss` (grey), with `Early`/`Late` **and milliseconds** |
| Combo | Current combo counter, same size and baseline as the sub-line |
| AP badge | A small `AP` next to `Perfect` until the first non-Perfect judgement |
| Result card | After a complete performance: Perfect / Good (split into Early & Late) / Miss / Score, faded in |
| `★ ALL PERFECT ★` | Added to the card when there is no Good / Miss / empty press (or the score hits the song's theoretical maximum) |
| Live FPS | Frame rate of the game's own per-frame judgement update, shown while playing |
| Free-play only by default | Story-mode performances do not pop the card unless you tick the box |
| Optional ms on Perfect | Off by default; tick the box to see `Early 6ms` on Perfects too |
| Resolution aware | Auto-scales to any client size; or pin to a fullscreen / windowed preset (2007 build keeps its own anchor) |
| Three languages | 简体中文 / English / 日本語, switchable at runtime |
| Tray + hotkeys | F7 preview card, F8 self-test, F9 reload config, F10 show/hide, `Ctrl+Alt+Arrows` nudge |
| Remembers window size | Saved back to `config.ini` on close |
| Auto attach | Starts with the game, detaches when it exits |

## Requirements

* Windows 10 / 11, **x64**
* One of the two builds listed above. The tool verifies the exact instruction
  bytes at all hook sites and refuses to touch anything that does not match.

Nothing else. The release build is a self-contained single `.exe`; no .NET runtime
installation needed.

## Quick start

1. Run `SRPerfectPatch.exe`.
2. Steam version: it finds the game via the Steam library folders and attaches
   automatically. 2007 version: start the game, then press **Attach patch**.
3. Start a song in **Free Play** in the game. Judgement text appears next to the
   pink lamp, above the staff.
4. Play the song to the end → the result card fades in.

Uninstalling = press **Detach patch**, or just close the game (the patch is
released automatically). There is nothing installed on disk except this tool's
own `config.ini`.

## Hotkeys

| Key | Action |
|---|---|
| `F7` | Preview the result card (normal / ALL PERFECT sample, alternating) |
| `F8` | Self-test: cycles Perfect(+AP) / Good / Miss overlay samples |
| `F9` | Reload `config.ini` without restarting |
| `F10` | Show / hide the overlay |
| `Ctrl+Alt+←/→/↑/↓` | Move the judgement text by 2 px |
| `Ctrl+Alt+[` / `]` | Font size −1 / +1 |
| `Ctrl+Alt+S` | Save the current nudge |

## Judgement windows

| Version | Perfect | Good | Bad | Time base |
|---|---|---|---|---|
| Steam release | ±13.33 ms | ±53.33 ms | ±106.67 ms | 1/88200 s, 1176 = 1/75 s |
| 2007 release | ±20 ms | ±80 ms | ±160 ms | 1/88200 s, 1764 = 1/50 s |

The windows are compile-time constants (resolution never enters the formula); the
effective tolerance is additionally quantised by the frame rate — a lower frame
rate makes it slightly wider, never narrower.

## Configuration (`config.ini`)

```ini
[behavior]
autoattach=1              ; attach when the game appears
storysummary=0            ; 1 = also show result card in story performances
showperfectms=0           ; 1 = show Early/Late ms on Perfect too
fitmode=auto              ; auto | full | window
x=440 / y=456             ; anchor inside the game client area
xwin=440 / ywin=444
x2007=268 / y2007=303     ; anchor for the 2007 build
fontsize=27               ; fontwin=26, font2007=18
fadems=900
colorperfect=#FFD24A
colorgood=#4DA6FF
colormiss=#A0A0A0
colorcombo=#FFECBE
showcombo=1
clickthrough=1
language=zh               ; zh | en | ja
windowwidth=0 / windowheight=0   ; 0 = auto
```

`fitmode=auto` scales the anchor and font by `client_height / 720`, so the text
stays in the same musical position whether you play fullscreen (1280×720) or
windowed (1280×701). The 2007 build does not scale — its layout is not
proportional, so it uses `x2007` / `y2007` / `font2007` as absolutes.

## How it works (short version)

The game's judgement routine lives in an encrypted `.text` section, so the tool
attaches to the running process and hooks a few instructions with a short jump
into a code cave it allocates in the process. All offsets come from a small
per-version profile:

* **Steam**: press `0x053B34`, missed note `0x0536FE`, per-frame update
  `call 0x05056A → 0x053330`
* **2007**: press `0x044804`, missed note `0x044387`, per-frame update
  `call 0x04146C → 0x044130`

The press hook copies grade (0..3), key index, note index and the signed time
delta into a 128-slot ring buffer. The overlay's UI thread polls that ring every
16 ms and renders an `UpdateLayeredWindow` overlay that follows the game's
foreground window.

Safety measures are described in [docs/SECURITY.md](docs/SECURITY.md), and the
internals in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Summary:

* `PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | QUERY_INFORMATION`
  — **not** `ALL_ACCESS`; no thread injection, no remote `CreateRemoteThread`,
  no `SetWindowsHookEx`, no network code at all.
* Every hook site is byte-verified before writing; mismatch → abort with zero
  writes.
* All game threads are suspended while the bytes are swapped, then resumed.
* The code cave's code page is `PAGE_EXECUTE_READ`, its data page
  `PAGE_READWRITE` (W^X), never RWX.
* A magic tag identifies our own cave; anything unrecognised is never freed.
* Uninstall restores the original bytes and restores the original page
  protection (execute+read).
* If this tool crashes, the game keeps running; the patch is simply left in place
  and is adopted on the next start (no double hooking).

## Building from source

```powershell
cd src
dotnet build -c Release                    # public (no-art) build
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o ..\dist   # single-file release
```

This repository contains **no game assets**: the header decoration is drawn in
code (paper, wine-red rule, staff lines, a treble-clef glyph from Segoe UI
Symbol) and the icon is generated programmatically. If you happen to place
`frame_top.png` / `header_icon.png` next to the `.csproj`, the build switches to
the "art build" that embeds them — that variant is for personal use only.

## Changelog

See [docs/CHANGELOG.md](docs/CHANGELOG.md).

## Legal / disclaimer

* Symphonic Rain is © Kogado Studio / 工画堂スタジオ. This project is unofficial,
  not affiliated with or endorsed by them, and ships **no** game data, art,
  music or text.
* The tool only reads the game process it attaches to. It is intended for
  practice and for understanding the judgement system. Use at your own risk —
  no warranty of any kind, see [LICENSE](LICENSE).
* Please buy the game if you want to play it.

## Credits

* Made by **MUCT_POHRIZ** (bilibili), with assistance from **DeepSeek Harness**.
* Judgement windows, scoring formula and internal layout were obtained by
  reverse-engineering the game's own code at runtime (see
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the numbers).

## License

[MIT](LICENSE)
