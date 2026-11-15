# Architecture — how the overlay talks to the game

Everything below was obtained by reading the running process (Steam's DRM
encrypts `.text` on disk, so the on-disk executable is not analysable; the
in-memory image is plain x86). Addresses are **RVAs** relative to the module base
(`SR.exe` is linked at `0x00400000`, ASLR moves it at runtime).

Verified against the Steam build of Symphonic Rain (`SR.exe`, 3,371,472 bytes).

## 1. Where a judgement is made

The music-action update routine sits at **RVA `0x00F13330`**. Each frame it walks
the note table and, for every note not yet resolved, computes the distance between
"now" and the note time:

```
now      = [music] +0x1B38        ; current playback position, in ticks
prevNow  = [music] +0x1B40        ; previous frame's position
noteTime = [note]  +0x00          ; tick of the note
```

```
if noteTime <= prevNow:   delta = prevNow - noteTime + 1     ; late
elif noteTime <= now:     delta = 0                          ; inside this frame
else:                     delta = noteTime - now             ; early
```

`delta` is then graded and stored in a per-key slot (`[music] + 0x2BD8 + key*4`);
the slot only ever moves **up** (a later frame can't downgrade it), and the note
index is kept in `[music] + 0x2B60 + key*4`.

### Time unit

```
ecx = [soundManager] + 0x328      ; position in milliseconds
ebx = ecx / 5 * 88                ; == ms * 88.2
```

So one tick = **1/88200 s**. The chart files use the same unit, which is why their
`seconds` column is exactly `time / 88200`.

### The three windows

| Grade | Condition | Window |
|---|---|---|
| 3 = **Great** (displayed as *Perfect*) | `delta <= 0x498` (1176) | **±13.33 ms** |
| 2 = **Good** | `delta <= 0x1260` (4704) | **±53.33 ms** |
| 1 = **Bad** | `delta <= 0x24C0` (9408) | **±106.67 ms** |
| 0 = **Bad (empty press)** | the key had no armed note at all | — |

Interesting detail: 1176 : 4704 : 9408 = **1 : 4 : 8**, and 1176 ticks is exactly
1/75 s. These thresholds are the *only* occurrence of those immediates in the whole
`.text`; difficulty does **not** change them.

Because the grade is recomputed once per frame and the whole frame shares one
grade, the *effective* window is the union of frames that touch `[T-ε, T+ε]` — it
is always a whole multiple of the frame duration and never narrower than nominal.
At 60 fps (16.67 ms) Great becomes 33.3 ms wide when the note lands near a frame
boundary and 50.0 ms wide otherwise.

## 2. The scoring

Press handler at **RVA `0x00F13AC0`**, dispatch table at `0x00F13C98`:

| Grade | Handler | Effect |
|---|---|---|
| 0 / 1 | `0x00F13B4A` | bad: lamp counter −1, combo reset, no score |
| 2 | `0x00F13BC2` | good: **+8 points**, combo reset |
| 3 | `0x00F13C34` | great: `call 0x00F131F0`, lamp counter +1 |

`0x00F131F0` is the great-note scorer:

```
combo   = [music]+0x2D68
if combo < 10: combo++            ; capped at 10, no unbounded counter exists
points  = combo * 5 * 4 + 15      ; == 20*combo + 15
if [music]+0x2D60 == 59:          ; lamp counter full
    points += 0x60                ; +96
score  += points                  ; cap 999,999,999 (0x3B9AC9FF)
```

So a great note is worth **35, 55, 75 … 215** while the lamps are not full, and
**131, 151 … 311** once they are. A good note is a flat **+8** — even when the
lamps are full, it does not get the +96 bonus (it uses its own inline block at
`0x00F13BDF` and never calls the scorer).

The score is displayed with 9 digit sprites (`[music]+0x1F4`), each one
`score / 10^i % 10`.

### The lamp counter

`[music]+0x2D60` is a single integer in **0..59**, initialised to **20**:

* drawn as 5 "big" lamps (`tens digit`) + 10 "small" lamps (`ones digit + 1`);
  starting value 20 = 2 big + 1 small, which is what the game shows;
* "all lamps lit" is literally `== 59`;
* `+1` per resolved note (hit **or** automatically played), `−1` on a miss or an
  empty press, clamped to 0..59.

## 3. Note table

```
[music]+0x1B28  note array pointer
[music]+0x1B2C  note count (max 1024)
stride          28 bytes
```

| Offset | Meaning |
|---|---|
| `+0x00` | time, in 1/88200 s |
| `+0x08` | sample index (−1 = silent) |
| `+0x0C` | key lane 0..29 |
| `+0x10` | lyric offset |
| `+0x14` | auto-play flag |
| `+0x18` | state (0 pending, 5 resolved, 1 missed) |

**Lyric lines occupy slots in this same table** (they carry the `@JA/@EN/...`
text), which is why the array length is larger than the playable note count. A
playable note is `auto == 0 && 0 <= lane <= 29`.

## 4. Difficulty

`[music]+0x270`: `0 = Normal`, `1 = Hard`, `2 = Easy` (confirmed in the config
writer at `0x00ED1778`).

On chart load (`0x00F0E2B8`):

* **Hard** — all 30 lanes stay as charted.
* **Normal** — lane = `lane % 10`, and if the row index is > 7 subtract 5:
  `g` folds onto `f`, `h` onto `j` → the 8 keys `a s d f j k l ;`.
* **Easy** — same folding, plus any folded lane outside `2..5` becomes auto-play,
  so the player only ever presses **`d f j k`**.

The key table at RVA `0x1131918` (256 signed bytes) is:

```
a s d f j k l ; g h | q w e r u i o p t y | z x c v m < > ? b n
0 1 2 3 4 5 6 7 8 9 |10 11 ...          19 |20 ...
```

## 5. Chart format

Charts live in `Song\SRxx\Song.pak` → entry `song` (a text file, block-compressed
with the Kogado "Cocotte" codec). Lines are `time,seconds,wave,key,`; lyric lines
are `time,seconds,@LANG,text,`.

## 6. The hook

Two 6-byte sites are rewritten to `jmp rel32` (plus one `nop`):

| Site | Original bytes | Meaning |
|---|---|---|
| `0x053B34` | `8B 83 D8 2B 00 00` | `mov eax,[ebx+0x2BD8]` — grade just computed |
| `0x0536FE` | `FF 8E 60 2D 00 00` | `dec dword [esi+0x2D60]` — miss settlement |

Both jump into a `VirtualAllocEx` block (`0x3000` bytes) that is split into a code
page (`PAGE_EXECUTE_READ`) and a data page (`PAGE_READWRITE`). The stubs
`pushad`, copy `{seq, key*4, grade, note index, delta, type}` into a 128-slot ring
buffer at `data+0x40`, `popad`, replay the overwritten instruction and jump back.

Ring layout (per slot, 32 bytes):

```
+0x00 u32 seq          +0x04 u32 key*4     +0x08 i32 grade
+0x0C i32 note index   +0x10 i32 delta     +0x14 u32 type (1 press, 2 miss)
```

Control block at `data+0x00`: `u32 seq`, `u32 musicUnitPointer`. A magic dword at
`code+0xF80` identifies the cave so a leftover patch can be adopted instead of
double-hooked.
