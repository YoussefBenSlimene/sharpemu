# SharpEmu Game Compatibility & Issue Tracking

**Purpose.** This document is the single source of truth for the state of every
title installed under `C:\ps5-emulator\ps5-games`. For each game it tracks:

- current boot/render status,
- every problem encountered (open and fixed),
- the root cause once identified,
- the fix commit when resolved,
- suspected-but-unconfirmed problems ("theories") so they are not re-derived
  from scratch in a later session.

**Maintenance rules.**

1. Every time a new problem is observed — in a log, a crash dump, or a user
   report — add a row to that game's table with status `OPEN` and the evidence
   (log file, symptom, exact error).
2. When a root cause is identified, set status to `ROOT-CAUSE` and fill the
   Root Cause column; the problem is not fixed yet.
3. When a fix lands and the game is re-tested, set status to `FIXED`, fill in
   the commit hash, and move the row to the bottom of the table so open
   problems stay at the top.
4. A theory that is disproved gets a one-line entry in the game's
   "Ruled out" list — as valuable as a fix, because it stops the same dead end
   from being explored twice.
5. Status vocabulary: `OPEN` (observed, cause unknown) · `ROOT-CAUSE`
   (cause known, fix pending) · `FIXED` (fix landed, re-tested) ·
   `MITIGATED` (workaround) · `RULED OUT` (not a defect).

---

## Installed titles

| Title ID | Name | Engine | Last status |
|---|---|---|---|
| PPSA02929 | Dreaming Sarah | custom | **PLAYABLE** — reference title |
| PPSA02868 | Mortal Shell: Enhanced Edition | Unreal Engine 4 | **BOOTS** — full init, audio, ~68k draws, black screen |
| PPSA09477 | Quake II (2023) | KEX Engine | **BOOTS** — renders, reaches "Installation" menu, then abort |
| PPSA11264 | Hellboy: Web of Wyrd | Unity (IL2CPP + FMOD) | **BOOTS** — no crash, 135M+ imports, audio; main thread spins |

---

## PPSA02929 — Dreaming Sarah

**Status: PLAYABLE (reference title).** Boots, renders, audio, menus, gameplay.
Used as the control when validating emulator-side changes: if a SharpEmu change
breaks Dreaming Sarah, the change is wrong.

| # | Problem | Status | Root cause | Fix commit |
|---|---|---|---|---|
| — | none recorded | — | — | — |

---

## PPSA02868 — Mortal Shell: Enhanced Edition

**Status: BOOTS with black screen.** Fast boot (~1s window), full engine init,
AudioOut2 streaming (~180 submits/s, 96% fill), 60k+ GPU work items per 90 s
run, double-buffered presents to display buffers `0x8FC0000000` /
`0x8FC2000000` (3840×2160, R8G8B8A8Unorm, init=True). The screen stays black
because the composite pass samples 1×1 placeholder textures instead of the
real streamed ones.

| # | Problem | Status | Root cause | Fix commit |
|---|---|---|---|---|
| M1 | Black screen: composite draws sample **1×1 placeholder texture descriptors** (Unity streaming upload never re-binds the real descriptor) | PARTLY FIXED | multiple placeholder sources; 4 of 5+ addresses eliminated | `2a75ad4` (DCC publish + GPU-residency gate, upstream #853) |
| M2 | DCC fast-clear draws did not publish GPU images (`RequestGuestColorClear` alone never fills `_availableGuestImages`) → later composites sample empty CPU tiles | **FIXED** | `SubmitOffscreenColorClear` creates and publishes the images | `2a75ad4` (upstream #853) |
| M3 | Texture upload skip could reuse a GPU texture that was never uploaded | **FIXED** | skip gate now requires `IsGpuGuestImageAvailable` | `2a75ad4` (upstream #853) |
| M4 | Format-14 textures with number type ≠ 4/5/7 decoded as R8G8B8A8Unorm (wrong bpp/layout) | **FIXED** | `(14,7)` case should be `(14,_)` (upstream #860) | `2a75ad4` |
| M5 | All 59 guest threads received **host-heap** pthread handles (allocator silently skipped install when `ctx.Memory` was an `ICpuMemoryWrapper`) → Unity TaskGraph/Boehm/FMOD aliased garbage | **FIXED** | allocator install now unwraps wrapper chains | `c25f851` |
| M6 | One remaining 1×1 placeholder at `addr=0x…56F60000 pc=0x50 tile=1 fmt=10` — still-uncovered descriptor binding path | **OPEN** | suspected: a CPU-side upload path writes texels but never registers a GPU image (needs write-watch on that address) | — |
| M7 | Repeated `scePthreadMutexLock → EDEADLK` in steady state | **RULED OUT** | matches PS5/FreeBSD semantics (DEFAULT == ERRORCHECK); game handles it | — |

**Theories (unconfirmed — do not re-derive blindly):**

- T1: the remaining placeholder is fed by the same Unity async-upload thread
  that owns the audio mixer sync — check whether the thread that owns
  descriptor `0x…56F60000` is the one blocked in `sceKernelWaitSema`.
- T2: the composite pixel shader may sample mip LOD ≠ 0; the placeholder has
  `maxMip=0`, and sampling a LOD ≠ 0 on a 1×1 returns black regardless.
- T3: guest write-tracker is **disabled** on Windows by default; if the real
  streamed texture is uploaded via CPU memcpy into the same address, the GPU
  image is never refreshed (stale-black). Confirm with write-watch on the
  placeholder address.

**Repro:** `run_mortal_shell_dbg.ps1` (90 s, GAME-DBG draw/present on).

---

## PPSA09477 — Quake II (KEX Engine)

**Status: BOOTS, renders, reaches "Installation" menu, then aborts.** Fast
boot, full KEX init (RHI, 40+ shader programs, audio 48 kHz/8ch, mapdb with
232 maps loaded from pak0.pak), frames submitted to the display buffers. The
run ends with the game's own `Com_Error` → `abort()`.

| # | Problem | Status | Root cause | Fix commit |
|---|---|---|---|---|
| Q1 | `sceKernelClose(0x80020002)` spam → fatal: raw kernel file syscalls leaked the ORBIS error sentinel as an fd | **FIXED** | raw `sceKernel*` syscalls must return **-1 + errno** (BSD convention) | `c25f851` |
| Q2 | NP social init failed with a leaked sentinel request id (unresolved `sceNpCreateAsyncRequest` / `sceNpCheckNpReachability` / `sceNpPollAsync`) | **FIXED** | offline NP async-request manager (Kyty `network.cpp` semantics); requests complete as **reachable** — the signed-out result `0x80550006` is fatal to the game's social manager | `c25f851` |
| Q3 | `sceNpWebApi2PushEventDeletePushContext` unresolved during NP teardown | **FIXED** | offline stub (Kyty parity) returning OK | `c25f851` |
| Q4 | **Empty-message fatal**: `Com_Error` receives the border string itself as the message; underlying error text is empty (`stderr.txt`: `Error - ` + border + nothing) | **ROOT-CAUSE (partial)** | KEX `FatalError` called with an unset error-string global; call-site captured at `ret=eboot+0x4B789A`, frame chain `#0 ret=0x80053BE5D`, `#1 ret=0x80053BC68`, `#2 ret=0x8007D5D31` | — |
| Q5 | Loose asset directories absent: `/app0/baseq2/players/`, `/app0/baseq2/sound/player/steps` probed with `O_DIRECTORY` return NOT_FOUND (dump has only `pak0.pak`, `music/`, `video/`) | **THEORY** | on real hardware / Kyty these dirs exist; KEX may seed state from them and the empty Com_Error may be downstream | — |
| Q6 | `sceKernelStat` failures for `/savedata0/...` config files (0xffffffff) — game wants a mounted save | **MITIGATED** | save mount returns NOT_FOUND on first run; game tolerates it; verify `/savedata0` mount semantics against Kyty | — |
| Q7 | Unresolved `sceNpTrophy2GetTrophyInfo` (0x80020002) for user 268435456 | **OPEN** | trophy context init path; not fatal but feeds the NP error cascade | — |
| Q8 | Unresolved `sceImeKeyboardGetResourceId` (EwNylPdWUTM) at input init | **OPEN** | returns NOT_FOUND; game logs and continues — low priority | — |

**Theories:**

- T1: the empty Com_Error may be KEX printing the *last* console error from a
  global that was never set because a prerequisite failed earlier (loose dirs,
  save mount, or a sound asset). Trace backwards from `frame#0
  ret=0x80053BE5D`: disassemble that call site to see which global it reads
  for the message.
- T2: the WAV precache loop (`RIFF/WAVE` reads from pak0.pak at ~1.0 GB
  offset) completes, then the fatal — check whether the *last* sound read is
  the trigger (read trace shows `preview='RIFF…'` immediately before abort).
- T3: Kyty runs the same KEX title fine — diff SharpEmu's KEX-relevant HLE
  surface (AudioOut, Pad, IME, NP, SaveData) against Kyty's `libAudio.cpp`,
  `libPad.cpp`, `libNet.cpp` call by call.

**Repro:** `run_quake2_diag.ps1` (60 s, LOG_IO + LOG_OPEN on).

---

## PPSA11264 — Hellboy: Web of Wyrd

**Status: BOOTS without crash, main thread livelocks.** Fast boot, full Unity
init, FMOD audio alive (mixer ping-pong running), 135M+ imports, window renders
one frame then black persists. All JobWorkers block on `event_flag:0x4`;
`Loading.PreloadManager` spins on `scePthreadSelf`.

| # | Problem | Status | Root cause | Fix commit |
|---|---|---|---|---|
| H1 | `Loading.PreloadManager` crash — `mov [rsi],eax` to wild pointer `0x41E4E00000002D4C` right after `scePthreadSelf` (Boehm stop-the-world path) | **FIXED** | ScePthread chain fields (`+0x58`/`+0x68`/`+0x00`) were NULL; now always populated with fallbacks; cancel-state word written on the object itself | `c25f851` |
| H2 | Same crash persisted as a 100k-iteration VEH substitution loop | **FIXED** | cancel flag on both blocks; `[secondary+0x58]` self-pointer so reloads never yield NULL | `c25f851` |
| H3 | Guest threads on **host-heap** pthread handles (allocator skipped through memory wrappers) | **FIXED** | allocator install unwraps `ICpuMemoryWrapper` chains | `c25f851` |
| H4 | Unmapped-target guest reads killed the process mid-dump (no VEH case) | **FIXED** | last-resort garbage-read recovery (skip + zero dest, capped 1M) | `c25f851` |
| H5 | Unpatched `mov reg, fs:[0]` loads (TLS load-time patcher scans host memory; 0 loads patched in every submitted log — upstream #789) | **FIXED (backstop)** | fault-time recovery writes the calling thread's guest TLS base (upstream #791) | `51fc8f7` |
| H6 | **PreloadManager livelock**: 135M+ `scePthreadSelf` calls from `libScePosix` self-cache refresh at `0Z2sdqi9LGg+0x2256E`; loop walks `[r15+0x58]` chain, `cmp word [r15+0x11C],0` never satisfied, reloads r15=NULL via `[secondary+0x58]` | **ROOT-CAUSE** | the game's thread-list/TLS cache never validates against synthetic zeroed pthread objects; loop needs one thread with cancel-state ≠ 0 **or** cancel-type ≠ 0; the JobWorkers it waits for are all blocked on event_flag 0x4 | — |
| H7 | All 16 JobWorkers blocked on `sceKernelWaitEventFlag` wake=`event_flag:0x4` | **OPEN** | whatever should set flag 4 never does — likely the preload work the spinner should dispatch; fixing H6 fixes this | — |
| H8 | FMOD mixer/AudioOut semaphore ping-pong runs forever (count 43k+ with waiters=0) | **SYMPTOM** | mixer alive but game never submits audio work because main thread spins; H6 fix resolves | — |

**Theories:**

- T1: hook the game's pthread-create wrapper so the **self-cache slot**
  (`[tls+0x58]` in the wrapper's TLS) is pre-populated with the handle at
  thread creation — Kyty's `PthreadPrivate` works because its guest-visible
  block is written by the same create path the game reads.
- T2: the wrapper loop terminates when `[self+0x11C]` (cancel-state) is
  non-zero **or** `[self+0x12E]` (cancel-type) is non-zero; both are zero on
  the fallback path. Writing 1 to `+0x12E` as well as `+0x11C` may break the
  cycle without touching the game.
- T3: the spin may be an infinite `pthread_once`-style init: the guard at the
  loop head tests a global our HLE never sets. The spin-loop register dump
  (in place) will show the exact global address.

**Repro:** `run_hellboy_test.ps1` (detached; snapshots + GAME-DBG on).

---

## Cross-title infrastructure fixed this session

| Fix | Commit | Titles helped |
|---|---|---|
| Fast boot: HLE JIT warmup on background task + eager window (Kyty parity) | `c25f851` | all |
| Raw `sceKernel*` file syscalls return -1+errno | `c25f851` | Quake II, any title probing files |
| Offline NP async-request manager + WebApi2 push-context stub | `c25f851` | Quake II |
| TLS thread-pointer load fault-time recovery (upstream #791) | `51fc8f7` | all (safety net) |
| DCC fast-clear publishes GPU images; texture skip needs GPU residency; format-14 wildcard (upstream #853 #860) | `2a75ad4` | Mortal Shell, any UE4 title |
| GAME-DBG draw/present/compute traces, hot-spin detector, abort call-site dump | `57f9a8d` `cb9dc9b` | all |

## Diagnostic tooling index

| Tool | What it captures |
|---|---|
| `run_game_with_timer.ps1` | Mortal Shell timed run, full log |
| `run_mortal_shell_dbg.ps1` | Mortal Shell 90 s, GAME-DBG on |
| `run_quake2_diag.ps1` | Quake II 60 s, LOG_IO + LOG_OPEN |
| `run_hellboy_test.ps1` | Hellboy detached, snapshots + GAME-DBG |
| `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` | per-second per-thread state table |
| `SHARPEMU_LOG_SEMA=1` / `SHARPEMU_LOG_AUDIO_QUEUE=1` | semaphore + audio queue traces |
| `SHARPEMU_DISABLE_GAME_DBG=1` | silence the GAME-DBG layer |

## Next-session priorities (expected-value order)

1. **Quake II Q4** — disassemble the `Com_Error` caller (`frame#0
   ret=0x80053BE5D`); the message global's address falls out of that
   disassembly, then trace which subsystem failed to set it. Q5 (loose asset
   dirs) is a zero-code test if a fuller game dump is available.
2. **Mortal Shell M6** — write-watch the last placeholder address
   (`0x…56F60000`); if the guest never rewrites it, instrument the CPU upload
   path that should bind the real texture.
3. **Hellboy H6** — cheapest untested change: also write the cancel-type byte
   (`+0x12E`) on every synthesized thread object (theory T2), re-run, check
   whether the spin terminates; if not, use the spin-loop register dump to
   find the guard global (theory T3).
