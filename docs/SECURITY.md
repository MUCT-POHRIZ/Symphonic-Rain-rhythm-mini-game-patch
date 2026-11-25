# Security design

This tool writes into another process's memory. That is inherently powerful, so
the implementation is deliberately conservative. Everything listed here is
enforced in code, not just documented.

## What it does *not* do

* No file in the game directory is created, modified or deleted.
* No `CreateRemoteThread`, no `SetThreadContext`, no `SetWindowsHookEx`,
  no DLL injection.
* No `PROCESS_ALL_ACCESS` — the handle requests
  `VM_READ | VM_WRITE | VM_OPERATION | QUERY_INFORMATION` only.
* No network access of any kind (the binary contains no socket/WinINet code).
* No driver, no service, no startup entry unless you tick the checkbox
  (which writes `HKCU\...\Run`, and can be unticked to remove it).
* No telemetry, no analytics, no auto-update.

## Write path, step by step

1. **Find the process** — by executable name `SR.exe`, then confirm it is 32-bit
   (`IsWow64Process`) and that its main module is really `SR.exe`.
2. **Verify before touching anything** — read the 6 bytes at both hook sites and
   compare with the expected patterns. Any mismatch aborts with
   »bytes mismatch (expected …), nothing was written«. This is what protects other
   game versions.
3. **Allocate** a `0x3000`-byte region with `PAGE_READWRITE`, fill in both stubs
   and the data page, then `VirtualProtect` the first page to `PAGE_EXECUTE_READ`
   (W^X — the cave is never simultaneously writable and executable).
4. **Suspend every thread** of the game (`Thread32First/Next` + `SuspendThread`)
   so no thread can be inside the two instructions being rewritten.
5. **Patch** the 6 bytes, flush the instruction cache, read them back and verify.
   On any failure: restore the original bytes, free the cave, resume threads.
6. **Restore page protection** and resume threads.

## Uninstall path

* Writes the original 6 bytes back (threads suspended, same procedure).
* Restores each site's original page protection; if a page somehow still carries
  a writable bit, it is forced back to `PAGE_EXECUTE_READ`.
* If a previous run left a cave behind, it is only freed when the magic tag proves
  it is ours *and* the version is one we know (SRP1..SRP4). Anything else is
  reported and left alone.
* Waits 150 ms before freeing so a thread that is still inside the cave can
  finish; the failure path (cave never executed) skips the wait.

## Failure containment

* The tool is a separate process. If it crashes or is force-killed, the game keeps
  running; the patch simply stays in memory and is **adopted** (not re-installed)
  on the next start.
* `jmp rel32` reach is checked (`±2 GB`) before patching; a distant allocation is
  discarded instead of producing a wild jump.
* Hook sites are keyed to exact byte patterns, so a game update makes the tool
  refuse to work rather than corrupt the process.

## Known limitations

* While attached, the tool has read/write access to the game's memory; that is the
  nature of the technique. Only run builds you compiled yourself or that come from
  the official release page.
* Everything is verified against one specific executable (Steam 2023 re-release,
  3,371,472 bytes). Other releases will be rejected by the byte check.
* The overlay uses a layered window that is re-raised to topmost after each paint;
  it never takes focus and never blocks game input (`WS_EX_TRANSPARENT`).
