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
| PPSA02868 | Mortal Shell: Enhanced Edition | Unreal Engine 4 | **BOOTS** — 242k draws / 103 presents, crash class removed (M12); black screen from 5 remaining 1×1 placeholder descriptors |
| PPSA09477 | Quake II (2023) | KEX Engine | **BOOTS** — renders, **NO abort** (CheckAvailability OK), alive 120s+ |
| PPSA11264 | Hellboy: Web of Wyrd | Unity (IL2CPP + FMOD) | **BOOTS** — H6 livelock GONE (TRC watchdog forces suspendPoint); fails later in guest memcpy AV |

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

**Status: BOOTS with black screen — crash class removed 2026-09-25.** Fast boot
(~1s window), full engine init, AudioOut2 streaming, double-buffered presents
to display buffers `0x8FC0000000` / `0x8FC2000000` (3840×2160, R8G8B8A8Unorm,
init=True). Current clean run (300 s, tracker off): **242,271 draws**, 464
sampled draws, 103 presents, 62 computes, `AgcSubmissionThread` Running,
`FAsyncLoadingThread` at 19.6M imports, 5× 1×1 placeholder textures remaining.
The screen stays black because the composite pass samples those placeholders.
The previous "FPS drops to 2 and the process dies" behaviour was a separate,
now-fixed crash (M12).

| # | Problem | Status | Root cause | Fix commit |
|---|---|---|---|---|
| M1 | Black screen: composite draws sample **1×1 placeholder texture descriptors** (Unity streaming upload never re-binds the real descriptor) | PARTLY FIXED | multiple placeholder sources; 4 of 5+ addresses eliminated | `2a75ad4` (DCC publish + GPU-residency gate, upstream #853) |
| M2 | DCC fast-clear draws did not publish GPU images (`RequestGuestColorClear` alone never fills `_availableGuestImages`) → later composites sample empty CPU tiles | **FIXED** | `SubmitOffscreenColorClear` creates and publishes the images | `2a75ad4` (upstream #853) |
| M3 | Texture upload skip could reuse a GPU texture that was never uploaded | **FIXED** | skip gate now requires `IsGpuGuestImageAvailable` | `2a75ad4` (upstream #853) |
| M4 | Format-14 textures with number type ≠ 4/5/7 decoded as R8G8B8A8Unorm (wrong bpp/layout) | **FIXED** | `(14,7)` case should be `(14,_)` (upstream #860) | `2a75ad4` |
| M5 | All 59 guest threads received **host-heap** pthread handles (allocator silently skipped install when `ctx.Memory` was an `ICpuMemoryWrapper`) → Unity TaskGraph/Boehm/FMOD aliased garbage | **FIXED** | allocator install now unwraps wrapper chains | `c25f851` |
| M6 | Remaining 1×1 placeholder textures (`pc=0x50 tile=1 fmt=10`, 5 addresses in the 09-25 run) — the descriptor-binding/streaming path | **OPEN** | narrowed 2026-09-25: **identical count (5) with the write tracker ON and OFF**, so a CPU-write-detection gap is not the mechanism. Real candidate is the streaming upload not being *issued* for those descriptors (see M9/M10) — the transport that would bind them is now healthy, so this is descriptor-side, not coordination-side | — |
| M8 | Draw throughput improved after the AGC fixes: 87k work items per 90 s (was ~60k), 50 presents, 198 draws sampled — double-buffer chain verified correct | **IMPROVED** | DCC fast-clear publishing + texture GPU-residency gate + format-14 wildcard | `2a75ad4` |
| M9 | ~~RenderThread 1 blocked on `pthread_cond_wait`; AgcSubmissionThread also blocked; no new frames dispatched~~ — **no longer a blocker (2026-09-25, clean 300 s run)**: `AgcSubmissionThread` **Running** (142,048 imports), `RenderThread 1` parked between frames with **892,365** imports (was 12,050), **242,271 draws** and 103 presents dispatched | **RESOLVED** | the earlier "coordination stuck" reading came from the stale 09-14 binary (M13) whose pool was serialized at `max_concurrent=2`. With the 16-worker pool + the M12 crash removed, the frame loop runs normally | (via M12/M13) |
| M10 | `FAsyncLoadingThread` never finishing (NID `EgmLo6EWgso` = `scePthreadRwlockUnlock`) keeping the render thread blocked | **RESOLVED (not a blocker)** | in the clean 300 s run it is **Running** at **19.6M imports** with `SlateLoadingThread2` at 18.5M and `SlateLoadingThread1`/`RenderThread 0` having **Exited** normally — loading progresses and completes; it simply takes a long time under emulation | (via M12/M13) |
| M11 | **GPU compute queue deadlock**: `acb.compute[32]` WAIT_REG_MEM suspends with `producer=none-observed` — no graphics-queue RELEASE_MEM ever wrote the awaited label. Correlates with FPS dropping to 2-4 before crash. Upstream #770 tried to fix and was reverted | **FIX (landed, unverified in-game)** | compute-queue WAIT_REG_MEM fences are CP-firmware completion fences: the parser now writes the completion value the firmware would have written and keeps parsing (scoped to compute queues; bypasses `RecordProduced` so recycled labels keep autocompleting). Armed but not yet observed firing — game stalls before reaching compute fences in test runs | `0872285` |
| M12 | **CLR FailFast** `Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code` — killed the process ~4 min in ("fps dropped to 2 then crash") | **FIXED** | **`GuestImageWriteTracker` page-guard design.** Arming a page turns any *managed* write into it into a CLR-fatal AccessViolation (documented at `GuestImageWriteTracker.NotifyManagedWrite`) instead of a resumable guest fault; the fault then reaches the VEH trampoline, which reverse-P/Invokes the managed `VectoredHandler` (`Exceptions.cs:58-60`, `Marshal.GetFunctionPointerForDelegate`). If the faulting thread is in cooperative GC mode that transition is illegal → CLR kills the process. Intermittent because it needs a managed writer to hit an armed page. **Every** observed FailFast (4/4 logs) was immediately preceded by `[SYNC] cpu-write-drain`. Tracker is opt-in again (`SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`) | `b9f2c1e`-series (see commit for "GuestImageWriteTracker is opt-in again") |
| M13 | **Stale-binary trap**: every "M11/M12 still broken" conclusion in this doc was drawn from logs produced by a build dated **2026-09-14 20:34**, i.e. *before* `0872285` (09-15, native-worker routing), `39dd33c` (09-15, max_concurrent 2→16), `9216ba7`/`ed220be` (09-16). Rebuilt 2026-09-25 and re-ran: pool is now `prewarmed 16/16 max_concurrent=16` (was `4/4 max_concurrent=2` — the serialization the doc blamed for the throughput drop), and the old `CallNativeEntry ← ExecuteGuestContinuationEntry` stack is **gone** (routing fix works; the remaining FailFast had no stack at all) | **FIXED (process)** | always rebuild before drawing conclusions from a log; check `Get-Item ...SharpEmu.exe \| Select LastWriteTime` against `git log -1 --date=iso`. Runs take 5 min, builds take 2.7 min — the build is the cheaper mistake | — |
| M14 | `_onGuestExecutionRunnerThread` was **dead code** — set on `GuestExecutionRunner.ThreadMain`, `GuestContinuationRunner.ThreadMain` and `RunContinuationOnTemporaryThread`, but never read (compiler `CS0414`), so the documented invariant "guest stubs must never run above a CLR runner thread's managed frames" was unenforced and `RunGuestEntryStub` would still fall back to a managed inline `calli` if any caller passed `requireNativeWorker: false` | **FIXED** | `RunGuestEntryStub` now computes `mustUseNativeWorker = requireNativeWorker \|\| (_onGuestExecutionRunnerThread && !NativeGuestWorkersDisabled)` and refuses/yields instead of inlining on runner threads; the explicit `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS=1` opt-out still permits the historical inline path | same commit as M12 |
| M12-old | (superseded) the pre-09-25 analysis attributed the FailFast to guest stubs entering via inline calli from managed frames on UE4 task-graph threads | **FIXED** | that mechanism was real (stale build stack) and was addressed by `0872285`; the *remaining* post-fix FailFast is M12 above (write tracker) | `0872285` |

**Theories (unconfirmed — do not re-derive blindly):**

- T1: the remaining placeholder is fed by the same Unity async-upload thread
  that owns the audio mixer sync — check whether the thread that owns
  descriptor `0x…56F60000` is the one blocked in `sceKernelWaitSema`.
- T2: the composite pixel shader may sample mip LOD ≠ 0; the placeholder has
  `maxMip=0`, and sampling a LOD ≠ 0 on a 1×1 returns black regardless.
- T3: ~~guest write-tracker is disabled on Windows by default~~ **RULED OUT
  (and actively harmful)** — the tracker was made default-on in `56bad5f` and
  the placeholders persist with it enabled *and* disabled (5 vs 5). Worse, its
  page-guard design is the M12 process killer. It is opt-in again as of
  2026-09-25.
- T4: the 5 remaining placeholders are **descriptor-side**: the streaming
  upload path is now healthy (M9/M10 resolved), yet the composite still binds
  `pc=0x50 tile=1 fmt=10`. Next: log the full descriptor word for each of the
  5 addresses (base/dim/tile/numType/swizzle) and compare against a *known
  good* descriptor from the same run, to see which field is degenerate.
- T5: the composite pass may be sampling mip LOD ≠ 0 while the bound image has
  only one level, or sampling with a swizzle/format the placeholder cannot
  represent — T2 extended to cover the descriptor fields rather than just LOD.

**Measured A/B (2026-09-25, identical 300 s runs, only the tracker toggled):**

| | tracker ON (default then) | tracker OFF |
|---|---|---|
| CLR FailFast | **yes**, died ~230 s | **no**, survived full run |
| draws (GAME-DBG sample) | 193 | **464** |
| presents | 49 | **103** |
| computes | 16 | **62** |
| 1×1 placeholders | 5 | **5** |
| `[SYNC] cpu-write-drain` | ~15-line storm | 0 |

**Ruled out:**

- Write-tracker disabled (T3): placeholders identical with the tracker on and
  off, and the tracker is the M12 crash cause → it neither fixes the black
  screen nor is safe to leave on.
- Render-coordination deadlock (M9) and async-loader starvation (M10): both
  were artifacts of the stale 09-14 binary; the clean run dispatches 242,271
  draws with `AgcSubmissionThread` running.
- EDEADLK from scePthreadMutexLock (M7): matches PS5/FreeBSD semantics, game
  handles it.

**Repro:**
- `run_mortal_shell_dbg.ps1` — 300 s, GAME-DBG on, tracker explicitly off.
- `run_ms_notrack.ps1` — the A/B arm that isolated M12 (tracker off).
- `run_sarah_regress.ps1` — 60 s Dreaming Sarah guard, run after every change.

---

## PPSA09477 — Quake II (KEX Engine)

**Status: BOOTS, renders, alive 120s+ — no abort.** Fast boot, full KEX init
(RHI, 40+ shader programs, audio 48 kHz/8ch, mapdb with 232 maps loaded from
pak0.pak), frames submitted to the display buffers. `kexSocialManagerPSN::
CheckAvailability: result 0x00000000` (was `0x80550006` signed-out →
`Com_Error` → `abort()` immediately after "Running game session").

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
| H6 | **PreloadManager livelock**: 1.4M+ `scePthreadSelf` calls from `libScePosix` self-cache refresh wrapper; the wrapper calls `scePthreadSelf`, compares the result with a game-managed global self-cache, and loops when they differ | **ROOT-CAUSE** | the game's TLS/global self-cache slot is never populated with the real pthread handle by SharpEmu; the wrapper refreshes it every iteration but the comparison target also changes (or is read via FS which has no guest base). Theory T1 (populate the TLS self-cache at thread creation) is the fix path. Theory T2 (cancel-type byte) applied but **insufficient alone** — the spin is in the wrapper-level global-cache comparison, not the thread-list walk | `b855a81` (T2 applied, insufficient alone) |
| H7 | All 16 JobWorkers blocked on `sceKernelWaitEventFlag` wake=`event_flag:0x4` | **OPEN** | whatever should set flag 4 never does — likely the preload work the spinner should dispatch; fixing H6 fixes this | — |
| H8 | FMOD mixer/AudioOut semaphore ping-pong runs forever (count 43k+ with waiters=0) | **SYMPTOM** | mixer alive but game never submits audio work because main thread spins; H6 fix resolves | — |

**Theories (unconfirmed — do not re-derive blindly):**

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
- T4: ~~with native-worker routing (commit `0872285`) the import throughput
  dropped (242k in 150 s vs 135M+ before) under the default
  `SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT=2` — continuations serialize~~
  **FIXED** — the default is now 16 (verified: Sarah clean, Hellboy passes
  the old H6 livelock entirely). Root cause of the drop: all guest
  continuations route through the pooled native workers, and 2 in-flight
  Runs serialized the UE4/Unity task graphs.
- T5: ~~the render loop spams `[DEBUG][PRINF] Wanted to force a call to
  sce::Agc::suspendPoint but not safe` — our `agc.flip_wait_safe` /
  `GuestGpu.Current.SubmitOrderedGuestFlipWait` gate rejects the forced
  suspendPoint~~ **RESOLVED (downstream of H6)** — the message is Unity's
  **TRC R5089 watchdog** in `GfxDevicePS5Core.cpp` (string pool at eboot
  `0x1B1ED14`): Unity must periodically force `sce::Agc::suspendPoint`
  (Sony TRC requirement) and prints "not safe...(%d seconds)" while its
  internal check fails. With the H6 livelock gone (16-worker default) the
  watchdog now prints "Forcing call to sce::Agc::suspendPoint to avoid TRC
  R5089 breach" — the check passes. No emulator-side fix needed.
- T6: **new failure point after the livelock (RACE, not deterministic —
  disassembled 2026-09-15)**: guest AVs with a bad pointer in the
  libScePosix-family pthread code inside `Il2CppUserAssemblies.prx`
  (module `0Z2sdqi9LGg`, sub-mapping base `0x805B7D710`, il2cpp base
  `0x805918000`). Site varies per run:
  - run 1: AV inside a vectorized copy loop (`vmovdqu [rdi],ymm0` — a
    realloc's old→new copy) called from a 32-byte-aligned growing
    allocator at il2cpp `0x261FB9` (bounds-check, `call 0x30b200` alloc,
    `and r14,~0x1f`, raw ptr at `[r14-8]`);
  - run 2: `movzx edx, word [rdi+0x132]` in the pthread **cancel path**
    (caller `0x274200` tests `[rdi+0x132] & 2` — cancel-type — then calls
    `0x27b890` which reads `[rdi+0x132]` and `[rdi+0xd8]`), reached from
    a pthread fn (frame#1 ret = il2cpp `0x27ECC3`) and eboot (frame#2).
  The crashed rdi is a pthread object pointer that was never allocated or
  already freed — the objects' `+0x58` self-cache IS populated (H1/H2
  fixes verified working), so the gap is object **lifetime**/chain, not
  the self-cache. Common precondition: `sceKernelWaitSema TIMED_OUT`
  storms (consumer waits on a guest-memory semaphore `0x60000056D00`,
  need=1, timeout pointer at the same stack slot `0x7FFFF01FB80C` across
  runs; producer `4czppHBiriw` interleaved) — the game's cond-wait path
  runs for the first time with the 16-worker default and handles
  spurious timeouts, after which some pthread chain dereferences a stale
  handle. Next: disassemble the frame#1 pthread fn (il2cpp `0x27ECC3`)
  to find where the bad handle comes from (likely the create-path
  self-cache — H6 theory T1: populate the TLS self-cache at thread
  creation).
- T7: **adapter progress (2026-09-16)**: the cancel-flag recovery
  (`SHARPEMU_DISABLE_CANCEL_FLAG_RECOVERY=1` to disable) substitutes a
  **self-referential 0x140 guest block** for the bad rdi at the
  `+0x132` flag word / `+0xa` kind-byte reads and re-executes — the game
  now survives both fault classes (3-4 substitutions per run; the plain
  zeroed thread object faulted downstream at `[0+0x40]+0x20 == 0x20`
  because its `+0x40` is not a self-pointer). Remaining root (disassembled
  through 3 chain levels): the game's node objects (`0x138+` bytes,
  layout `+0x40/+0x48` = self-or-cond, `+0x50` = sem, `+0x68` = attr,
  `+0x70` = pthread obj, `+0x78` = self, `+0x132` = flags) get
  `+0x40` from the per-type **singleton dispatch** `0x2779b0` — which
  returns the per-type `.bss` global, and **0 for an uninitialized table
  type** (`0x2778a0(table_entry,1)`: type = `[entry+8]>>0x10`; type 0 →
  `xor eax,eax; ret`). The uninitialized type → `[node+0x40] = 0` →
  downstream AVs at `[rbx+0x48]`/`[rax+0x2a]` that no register
  substitution can cover (r14/rbx are computed earlier from the zero).
  Root: the game's sync-object descriptor table type bytes are 0 — its
  sync-object initialization never completes. Next: instrument the
  singleton dispatch (il2cpp `0x2779b0`) to trace which table entries are
  uninitialized and find the failed init that precedes it.
- T8: **+0x40/+0x48 self-pointer fix (2026-09-16)**: kernel thread
  objects now also get `+0x40`/`+0x48` = self (the game's node-alloc
  normal-path layout, matching `+0x58`/`+0x68`/`+0x00`). When the game's
  wrapper walk reaches a zeroed kernel thread object (rdi=0 at the cancel
  checker — the walk reads `mov rbx,[x+0x40]` first), the self-pointer
  keeps every downstream chain hop in-bounds. Verified: Hellboy
  substitutions 3-4 → **1** per run, AVs 1 → **0**; the crash moved to a
  hardware-level fault (CET/CFG mitigation restart, no log entry —
  needs child-process crash diagnostics). Sarah unaffected (39 presents,
  FailFast 0).
- T9: **interpretation correction (2026-09-16)**: the trailing
  "Running in mitigated child process (CET/CFG disabled)" line is the
  **normal end-of-run marker**, not a crash marker — the parent always
  relaunches the game in a CET/CFG-disabled child (`Program.cs:509
  TryRunMitigatedChild`, unconditional unless
  `SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1`) and logs the line after the
  child exits (`Program.cs:632`). Current Hellboy state: runs ~100 s,
  1 cancel-state substitution, **0 AVs** (the pthread fault family is
  fixed by T7/T8), then the game **exits itself** — stdout ends with
  Boehm "thread not found in gc_threads" warnings during asset loading
  and no TRC-forcing this run (the render loop is timing-dependent: an
  earlier run with the same build reached "Forcing call to suspendPoint"
  + "cannot allocate system memory!"). Next root: the game's
  exit path after the `sceKernelWaitSema TIMED_OUT` retry storms —
  trace what the guest does after the last TIMED_OUT import (the exit is
  likely the Boehm GC giving up on unregistered threads, or Baselib
  exiting on the allocation failure).

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
| Guest continuations/thread-starts route through pooled native workers (CLR FailFast fix) + compute-fence autocomplete | `0872285` | Mortal Shell, Hellboy, Quake II, all titles with managed-HLE thread resume |
| **`GuestImageWriteTracker` is opt-in again** (`SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`) — its page-guard design converted managed writes into CLR-fatal AVs via the VEH reverse-P/Invoke path (M12) | this session | Mortal Shell (crash removed); any title that would have hit it |
| Enforce `_onGuestExecutionRunnerThread` in `RunGuestEntryStub` (was dead code, M14) — runner threads can no longer inline a guest stub above their own managed frames | this session | all titles with resumed guest threads |

## Process notes

| Lesson | Detail |
|---|---|
| **Rebuild before believing a log** (M13) | The entire "M11/M12 are still broken" state of this document was inferred from logs produced by a binary 5 days older than the source. Always `Get-Item artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe \| Select LastWriteTime` and compare with `git log -1 --date=iso`; the run scripts resolve that exact path. |
| **A/B one variable at a time** | M12 was proven by two 300 s runs differing only in `SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC`. Cheap, decisive, and it also produced the counter-evidence for T3 in the same pair. |
| **Check the reference title** | `run_sarah_regress.ps1` (60 s) after every change; a fix that breaks Dreaming Sarah is wrong. |

## Diagnostic tooling index

| Tool | What it captures |
|---|---|
| `run_game_with_timer.ps1` | Mortal Shell timed run, full log |
| `run_mortal_shell_dbg.ps1` | Mortal Shell **300 s**, GAME-DBG on, write-tracker explicitly **off** |
| `run_ms_notrack.ps1` | Mortal Shell A/B arm with the tracker disabled (isolated M12) |
| `run_sarah_regress.ps1` | Dreaming Sarah 60 s regression guard (reference title) |
| `run_quake2_diag.ps1` | Quake II 60 s, LOG_IO + LOG_OPEN |
| `run_hellboy_test.ps1` | Hellboy detached, snapshots + GAME-DBG |
| `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` | per-second per-thread state table |
| `SHARPEMU_LOG_SEMA=1` / `SHARPEMU_LOG_AUDIO_QUEUE=1` | semaphore + audio queue traces |
| `SHARPEMU_DISABLE_GAME_DBG=1` | silence the GAME-DBG layer |
| `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1` | **opt-in** guest-image CPU write tracker (page guards). Off by default: it kills the process via M12 and does not remove the 1×1 placeholders |
| `SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC=1` | explicit kill switch for the above (wins over the opt-in) |
| `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS=1` | debug-only: restores the historical managed inline `calli` instead of the pooled native workers |
| `SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT=<n>` | native worker concurrency (default 16) |

## Next-session priorities (expected-value order)

1. **Mortal Shell M6 / T4 — the actual black screen.** The transport is now
   healthy (242k draws, 103 presents, no crash), so the remaining work is
   descriptor-side: for each of the 5 `texture_1x1_linear_binding` addresses
   dump the full descriptor word (base, dim, tile mode, number type, swizzle,
   mip count) and diff it against a known-good descriptor from the same frame
   (the composite's other inputs). The question to answer is *which field* is
   degenerate and *which* guest code path produced it.
2. **Mortal Shell M11 — verify the compute-fence autocomplete.** With the
   crash gone the run now performs 62 compute dispatches, so re-run with
   `SHARPEMU_LOG_AGC=1` and grep for `compute_fence_autocomplete`; if it still
   never fires, `acb.compute[32]` waits are being satisfied another way and
   the trace should be moved to the satisfaction point.
3. **Hellboy T9 / T6** — the process now runs ~100 s with 0 AVs and then exits
   itself; stdout ends with Boehm "thread not found in gc_threads". Trace the
   guest exit path after the last `sceKernelWaitSema TIMED_OUT`.
4. **Quake II Q4** — disassemble the `Com_Error` caller (`frame#0
   ret=0x80053BE5D`) to find the unset message global.
