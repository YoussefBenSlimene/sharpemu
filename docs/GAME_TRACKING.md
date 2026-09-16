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
| PPSA02868 | Mortal Shell: Enhanced Edition | Unreal Engine 4 | **BOOTS** — full init, 100M+ imports, texture streaming active, FailFast largely fixed; black screen persists |
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
| M6 | One remaining 1×1 placeholder at `addr=0x2004550000 pc=0x50 tile=1 fmt=10` — still-uncovered descriptor binding path (the PREVIOUS placeholder at `0x…56F60000` was fixed by the DCC publish + GPU-residency gate) | **OPEN** | suspected: the game's streaming upload writes texels via CPU memcpy into the descriptor's backing memory, but the guest write-tracker is disabled on Windows by default → the GPU image is never refreshed (stale-black). Confirm with write-watch on `0x2004550000` or enable the tracker via env | — |
| M8 | Draw throughput improved after the AGC fixes: 87k work items per 90 s (was ~60k), 50 presents, 198 draws sampled — double-buffer chain verified correct | **IMPROVED** | DCC fast-clear publishing + texture GPU-residency gate + format-14 wildcard | `2a75ad4` |
| M9 | **RenderThread 1 blocked** on `pthread_cond_wait` (wake=`pthread_cond_waiter:243619`); AgcSubmissionThread also blocked; RenderThread 0 **exited** with 434k imports; PoolThread 13-18 all blocked; meanwhile RHIThread is Running (543k imports) and FAsyncLoadingThread is Running (1.2M imports) — the render coordination thread is stuck, so no new frames are dispatched to the GPU | **ROOT-CAUSE** | UE4's game thread should signal RenderThread 1 to start the next frame; that signal never fires because the main/entry thread (not in snapshots — runs on the host entry stack) is blocked or waiting for an async operation that never completes. Same pattern as Hellboy H7: a coordination thread blocks on a cond-var that nobody signals | — |
| M10 | `FAsyncLoadingThread` Running with 1.2M imports (NID `EgmLo6EWgso` = `scePthreadRwlockUnlock`) — the async loader is actively loading but never finishes, keeping the render thread blocked | **OPEN** | the loader may be waiting for a file I/O that SharpEmu's IFS doesn't resolve, or a dependency graph that never completes | — |
| M11 | **GPU compute queue deadlock**: `acb.compute[32]` WAIT_REG_MEM suspends with `producer=none-observed` — no graphics-queue RELEASE_MEM ever wrote the awaited label. Correlates with FPS dropping to 2-4 before crash. Upstream #770 tried to fix and was reverted | **FIX (landed, unverified in-game)** | compute-queue WAIT_REG_MEM fences are CP-firmware completion fences: the parser now writes the completion value the firmware would have written and keeps parsing (scoped to compute queues; bypasses `RecordProduced` so recycled labels keep autocompleting). Armed but not yet observed firing — game stalls before reaching compute fences in test runs | `0872285` |
| M12 | **CLR FailFast**: "UnmanagedCallersOnly method from managed code" killed the process within minutes of the first present (UE4 task-graph threads entered guest stubs via inline calli from managed frames — guest code above managed frames on that thread) | **PARTLY FIXED** | all guest continuations (`ExecuteGuestContinuationEntry`) and managed-HLE thread starts (`ExecuteGuestThreadEntry`) now route through pooled native workers + `[ThreadStatic]` runner-thread markers. 0 FailFast in most runs; 1 per ~100M imports still fires intermittently with no stack trace | `0872285` |

**Theories (unconfirmed — do not re-derive blindly):**

- T1: the remaining placeholder is fed by the same Unity async-upload thread
  that owns the audio mixer sync — check whether the thread that owns
  descriptor `0x…56F60000` is the one blocked in `sceKernelWaitSema`.
- T2: the composite pixel shader may sample mip LOD ≠ 0; the placeholder has
  `maxMip=0`, and sampling a LOD ≠ 0 on a 1×1 returns black regardless.
- T3: ~~guest write-tracker is disabled on Windows by default~~ **RULED OUT** —
  the write tracker was enabled by default in commit `56bad5f`
  (`SHARPEMU_GUEST_IMAGE_CPU_SYNC` inverted to
  `SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC`), and the 1×1 placeholders
  persist even with tracking on. The real cause is M9/M11: the render
  coordination thread is blocked, so the streaming upload never gets
  dispatched, regardless of whether the tracker can detect it.

**Ruled out:**

- Write-tracker disabled (T3): tracker is now default-on, placeholders persist → the issue is the blocked render thread, not the tracker
- EDEADLK from scePthreadMutexLock (M7): matches PS5/FreeBSD semantics, game handles it

**Repro:** `run_mortal_shell_dbg.ps1` (90 s, GAME-DBG draw/present on).

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

1. **Hellboy T6** — the new failure point after the H6 livelock: guest AV in
   a vectorized memcpy (`0x805B79FB9`) with non-canonical `+0x30` after
   `sceKernelWaitSema` TIMED_OUT storms. Same root fix family as the
   remaining kernel-managed ScePthread fields (see investigation doc).
2. **Verify M11 in-game** — the compute-fence autocomplete (commit `0872285`)
   is armed but was never observed firing (`compute_fence_autocomplete` trace
   absent — the game stalls before reaching compute fences). Re-run Mortal
   Shell with `SHARPEMU_LOG_AGC=1` and grep for the trace; if absent, the
   M9/M12 mutex coordination is still blocking compute submissions.
3. **Mortal Shell M12 remainder** — the intermittent FailFast (1 per ~100M
   imports, no stack trace) still fires with the native-worker routing in
   place. Instrument `CallNativeEntry` call sites to find which managed
   context still enters guest stubs (candidates: the main `ExecuteEntry`
   path on the emulation thread, `TryCallGuestFunction` nested case).
4. **Quake II Q4** — disassemble the `Com_Error` caller (`frame#0
   ret=0x80053BE5D`) to find the unset message global.
