# Handoff prompt: SharpEmu → KytyPS5-class performance and no black screens

> Copy everything below the line into a fresh AI agent session (written for
> Kimi K3, but it works for any coding agent that has shell access to the
> user's Windows machine). It is self-contained: the agent needs no earlier
> chat history.

---

# ROLE AND MISSION

You are a senior emulator engineer. You are working on **SharpEmu**, a PS5
emulator written in C#/.NET 10, on the user's Windows PC. The goal is for
SharpEmu to run games like **KytyPS5** does (a C++ PS5 emulator the user also
has installed). That means:

1. **No black screens.** Games that show a picture in Kyty must show one in
   SharpEmu.
2. **Kyty-class loading and frame rate.** Mortal Shell loads in minutes in
   Kyty. In SharpEmu it takes roughly an hour and never shows a picture.
3. **No regressions.** Dreaming Sarah (the reference title) must stay
   playable after every single change.

You are expected to **debug, measure, read the reference source, look for
solutions online, and invent new ones** when nothing published fits. Do not
guess and do not trust any log you have not reproduced on a fresh build.

---

# 1. ENVIRONMENT (the user's machine)

| Item | Location / value |
|---|---|
| SharpEmu repo (the project you change) | `C:\ps5-emulator\sharpemu` |
| GitHub remote | `https://github.com/YoussefBenSlimene/sharpemu` (branch `main`) |
| KytyPS5 source (reference) | `C:\ps5-emulator\kytyps5-ref`. If it is missing: `git clone https://github.com/KytyPS5/KytyPS5.git C:\ps5-emulator\kytyps5-ref` (commit `fd2e15e` or newer). The original upstream is `https://github.com/InoriRus/Kyty` |
| KytyPS5 Windows binary | `C:\ps5-emulator\KytyPS5-2026-09-25-fd2e15e-Windows-x64\launcher.exe` |
| Games | `C:\ps5-emulator\ps5-games\` (see table below) |
| Hardware | Intel i5-12450H (8C/12T), RTX 3050 6 GB Laptop, 16 GB RAM, Windows |
| Toolchain | .NET SDK 10 (`global.json`), PowerShell |

Games (the paths come from `bench_run.ps1`):

| Key | Title | Path | Engine | Status in SharpEmu |
|---|---|---|---|---|
| `sarah` | Dreaming Sarah (PPSA02929) | `PPSA02929-app0\eboot.bin` | custom | **PLAYABLE**, the regression canary |
| `mortal` | Mortal Shell EE (PPSA02868) | `PPSA02868-app0\eboot.bin` | UE4 (`dungeonhaven`, 8.69 GB pak) | Loads for ~1 h, black screen, sound plays |
| `quake2` | Quake II 2023 (PPSA09477) | `PPSA09477-app0\eboot.bin` | KEX | Boots; "GPU hanged" abort fixed (Q9/Q10), **user re-test pending** |
| `hellboy` | Hellboy: Web of Wyrd (PPSA11264) | `PPSA11264-app0\eboot.bin` | Unity IL2CPP | Boots, livelocks in PreloadManager (H6) |
| `smurfs` | The Smurfs – Dreams (PPSA21607) | `[DLPSGAME.COM]-PPSA21607\PPSA21607\eboot.bin` | UE4 | Renders an animated title loop; too slow to reach the menu |

Build commands:

```powershell
cd C:\ps5-emulator\sharpemu
Get-Process SharpEmu -ErrorAction SilentlyContinue | Stop-Process -Force   # the mitigated child survives parent kills and locks artifacts\bin
dotnet build -c Release          # -> artifacts\bin\Release\net10.0\win-x64\SharpEmu.exe
dotnet build                     # Debug -> artifacts\bin\Debug\...
dotnet test tests\SharpEmu.Libs.Tests\SharpEmu.Libs.Tests.csproj   # 867 tests, must stay green
```

`bench_run.ps1` now runs the **newest** of the Debug and Release exes and
prints its build time. Always check that line. **Benchmark the Release
build**: Debug is much slower and makes every measurement misleading.

---

# 2. HARD RULES (non-negotiable)

1. **Rebuild before believing any log.** Compare the exe `LastWriteTime` with
   `git log -1`. Earlier sessions lost hours drawing conclusions from a
   5-day-old binary (GAME_TRACKING M13).
2. **Change one variable at a time.** Every performance change needs
   before/after numbers from `bench_run.ps1`. Every risky change gets an
   environment-variable kill switch (`SHARPEMU_DISABLE_<FEATURE>=1`) so it can
   be A/B-tested without rebuilding.
3. **Run `.\run_sarah_regress.ps1` after every change.** A change that breaks
   Dreaming Sarah is wrong, whatever else it fixes.
4. **Keep the docs current with every finding**, positive or negative:
   - `docs/GAME_TRACKING.md`: one row per problem (`M*` Mortal Shell, `Q*`
     Quake II, `H*` Hellboy, `S*` Smurfs), using the status words OPEN /
     ROOT-CAUSE / FIXED / MITIGATED / RULED OUT. A theory you disprove goes
     in "Ruled out"; that is as valuable as a fix.
   - `docs/PERFORMANCE_PLAN.md`: baselines, and the A/B result of every
     optimization.
5. **Commit and push to GitHub after every fix or change** (the user
   requires this):
   `git add -A; git commit -m "type(scope): description"; git push origin main`.
   Use small, focused conventional commits. Pull first if the remote moved.
6. **Add unit tests** for every semantic fix (xUnit in `tests/`). Where
   possible, prove the test **fails without the fix** before you commit.
7. **End each task with a verification contract for the user**: the exact
   commands, the metric that proves the fix, and a rollback command
   (`git revert <sha>`).
8. **Never widen a fast path without checking the slow path's invariants.**
   Two recent regressions came from this (see §4.3): a native mutex unlock
   that skipped the waiter hand-off, and a cond-wait shortcut that skipped
   unlock/relock.
9. Do not make diagnostics or tracing default-on in user runs.
   Per-draw logging and GPU readbacks force synchronisation and look like
   performance bugs.

---

# 3. HOW SHARPEMU WORKS (what you need to know first)

Read `docs/architecture/*.md` (overview, cpu-execution, hle-and-sysabi,
memory-and-address-space, runtime-flow), `docs/reference/environment-variables.md`
and `docs/guides/debugging.md`.

- **CPU:** guest x64 code runs **natively** on the host (no JIT or
  interpreter), exactly like Kyty. The guest's `syscall`/`fs:` quirks are
  patched. The speed gap to Kyty is **not** CPU emulation. It is at the
  HLE boundary (guest → emulator library calls) and in the GPU path.
- **HLE imports:** every guest import (PLT slot, identified by a NID) goes to
  a trampoline that saves registers, switches into managed code, dispatches
  through `[SysAbiExport(Nid=...)]` methods in `src/SharpEmu.Libs/**`, and
  returns. This costs about **0.5–5 µs per call**. Kyty's equivalent is a
  direct SysV-ABI C++ call of about 10–50 ns.
  - **Native fast paths** (the Kyty model) already exist for a few NIDs:
    `NativeFastPathRegistry` (`src/SharpEmu.HLE/NativeFastPathRegistry.cs`)
    and `NativeFastPathRegistration` (`src/SharpEmu.Libs/Kernel/NativeFastPathRegistration.cs`),
    with the shim emitted in
    `DirectExecutionBackend.TryCreateNativeImportIntrinsic` / `TryCreateNativeFastShim`
    (`src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.cs`).
  - Two kinds exist. Leaf stubs (`scePthreadGetspecific`, `scePthreadSelf`,
    `gettimeofday`) always finish natively. Fallback stubs (the mutex
    lock/trylock/unlock family) return
    `FallbackSentinel = 0xDEADBEEFDEADBEEF` to tail-jump into the full managed
    trampoline when they cannot finish.
  - `IsHlePreferredNid` deliberately keeps `memcpy`/`memset` on the managed
    path; the `MemcpyHleRoutingTests` test enforces this.
- **Guest threads are cooperative on the HLE side.** A blocking HLE call
  calls `GuestThreadExecution.RequestCurrentThreadBlock(ctx, reason, wakeKey,
  resume, tryWake, deadline)`. The scheduler parks the guest continuation
  and later resumes it through `WakeBlockedThreads(wakeKey)` or
  `WakeExpiredBlockedGuestThreads` (polled from `Pump` and a dispatcher that
  sleeps 1 ms). Guest threads execute on pooled native workers
  (`DirectExecutionBackend.NativeWorker.cs`, 16 workers).
- **Pthreads:** `src/SharpEmu.Libs/Kernel/KernelPthreadCompatExports.cs`
  (about 2,900 lines).
  - `PthreadMutexState` has an owner CAS, a recursion count,
    `_queuedWaiterCount`, a `SyncRoot` lock and a FIFO `Waiters` list; the
    hand-off goes through `TryGrantMutexWaiterLocked`.
  - `PthreadCondWaitCore` allocates a waiter and a **string wake key** on
    every call, and a **`System.Threading.Timer`** on every timed
    cooperative wait.
- **GPU:** `src/SharpEmu.Libs/Agc/AgcExports.cs` (about 16,800 lines) parses
  PM4 command buffers (DCB/ACB) and turns draws and dispatches into Vulkan
  work.
  - That work goes to `src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs`
    (about 20,000 lines), where one render thread executes it.
  - Shaders are translated by `src/SharpEmu.ShaderCompiler.Vulkan/Gen5SpirvTranslator*.cs`.
  - Per-title pipeline caches live in `user/pipeline_cache/<TitleId>/`.
  - **Every (guest address, format) pair gets its own VkImage.** There is no
    unified texture/buffer cache with page tracking like Kyty's or shadPS4's,
    and this is the root of a whole class of coherence bugs (§5.A).
- **GPU progress for sleepers:** `src/SharpEmu.Libs/Gpu/GuestGpuProgress.cs`.
  `sceKernelUsleep` in a GPU poll loop waits for pending label writes (the
  Quake II Q9/Q10 fix). A/B switch: `SHARPEMU_DISABLE_GPU_AWARE_USLEEP=1`.
- **File I/O:** AMPR/APR (`src/SharpEmu.Libs/Ampr/AmprExports.cs`). Reads
  are copied synchronously at submit time (`AprCommandBufferReadFile` →
  `TryReadFileToGuestMemory`).

## Kyty source map (compare against these for any semantics you touch)

| Area | Kyty file(s) |
|---|---|
| Import linking / zero-marshal HLE | `src/loader/runtimeLinker.cpp`, `symbolDatabase.cpp` |
| Memory / address space | `src/kernel/memory.cpp`, `memoryAddressSpace.inc`, `src/common/virtualMemory.h` |
| Pthreads / cond / mutex | `src/kernel/pthread.cpp`: `NativeMutexLock` about line 1256, `PthreadCondTimedwait` about line 2846 (waits with `cond_cv.wait_for` in 10 ms `SIGNAL_APC_POLL_MICROS` slices, then **unlocks and relocks** the mutex even on an immediate timeout) |
| Event flags / queues / sema / syncOnAddress | `src/kernel/eventFlag.cpp`, `eventQueue.cpp`, `semaphore.cpp`, `syncOnAddress.cpp` |
| File system | `src/kernel/fileSystem.cpp` |
| AMPR / APR (file reads, possibly hardware decompression) | `src/libs/libAmpr.cpp` (2,600 lines) |
| AGC / GPU front-end | `src/libs/agc.cpp`, `libAgcDriver.cpp`, `src/graphics/guest_gpu/command_processor/pm4Dispatch.cpp`, `pm4Handlers.cpp`, `graphicsRun.cpp`, `tile.cpp` |
| **Texture/buffer cache and page tracking (black-screen class)** | `src/graphics/host_gpu/renderer/cache/textureCache.cpp`, `bufferCache.cpp`, `faultManager.cpp`, `multiLevelPageTable.h`, `src/graphics/host_gpu/memoryTracker.cpp`, `pageManager.cpp`, `regionManager.h`, `rangeSet.h`, `shaders/fault_buffer_process.comp` |
| Render targets / blits / tiling | `renderer/colorRenderTarget.cpp`, `depthRenderTarget.cpp`, `image/blitHelper.cpp`, `image/tiler.cpp`, `image/image.cpp` |
| Scheduling / sync | `renderer/commandScheduler.cpp`, `masterSemaphore.cpp`, `renderer/sync.cpp` |
| Pipelines | `renderer/pipeline/pipelineCache.cpp`, `descriptorHeap.cpp` |
| Shader recompiler | `src/graphics/shader/recompiler/**` (IR passes, SPIR-V backend) |
| Presentation | `src/graphics/presentation/presenter.*`, `videoOut.*`, `renderDoc.h` |
| NP / online (offline answers) | `src/libs/libNet.cpp` (LibNpWebApi2 about line 3400), `network.cpp` |
| Audio | `src/libs/libAudio*.cpp`, `ajm/`, `ngs2.cpp` |

---

# 4. CURRENT STATE — MEASURED FACTS (do not re-derive; verify only if you doubt them)

## 4.1 Mortal Shell performance numbers

| Run | Imports | Imports/s | Pak reads | MB read | Notes |
|---|---|---|---|---|---|
| Baseline 2026-09-26 (600 s) | 273.7M | 456k/s | 46,250 | 1,620 | still loading at the end |
| `289b10c` (600 s) | 1.44M | 2.4k/s | 2,150 | 188 | **livelock** (M35), fixed in `dac5ef7` |
| Latest, user run 2026-09-26 22:44 (600 s) | 124.0M | 207k/s | 46,460 | 1,616 | healthy again. The "presents=1" it printed was a **bench bug** (it counted the one-shot readback), fixed in `b83c4f6`. It is **not certain which exe produced this run**: the user built Release, but the bench used the Debug exe until `3c989cc`. Re-run first |

Other measured facts:

- **30-minute run (M32):** 115/115 presented frames byte-identical and all
  zero. 46,466 reads (about 1.6 GB) by minute 30: a ~16-minute burst, then
  about 0.15 reads/s. `FAsyncLoadingThread` keeps making progress. So the
  load is **slow, not stuck**.
- **HLE profile** (`SHARPEMU_PERF_HLE=1`, 180 s): **4.2 host cores** spent in
  HLE. Top costs:
  - `sceKernelUsleep` (the parked GPU-aware wait)
  - `memcpy` (29M calls, 0.5 cores)
  - `sceAudioOut2ContextPush` (real-time; not a target)
  - `pthread_cond_timedwait` (6.9M calls)
  - `scePthreadMutexLock` (now native)
  - `scePthreadGetspecific` (65M calls, now native)
- **Guest-RIP sampler:** 98.7% of guest thread time is spent **waiting** in
  HLE. The load is a long producer→consumer wake chain, where every link
  costs an HLE round-trip plus host scheduling latency. There is no single
  hot loop to optimise.
- The GPU-aware usleep is **not** a loading throttle. With it disabled,
  sleepers spin, HLE work doubles and reads drop (25k → 16k). Keep it.
- Pak read latency is not the floor: about 18 s of a 180 s run.

## 4.2 Black-screen evidence for Mortal Shell (M15–M31)

- **Presenter is fine.** The image handed to the window is provably all-zero
  (`vk.swapchain_image nonblack_pixels=0/2073600 hash=0x97D30483E5DD6325`)
  while presents happen at about 20 Hz with no drops.
- **The guest renders.** Non-flip render targets hold real content (M17).
  Only the flip images `0x8FC0000000`/`0x8FC2000000` stay zero.
- **The composite shows exactly one texel.** The pass that fills the flip
  image binds exactly **one** texture: a 1×1 `fmt=10` descriptor at slot
  `pc=0x50` whose dimension words are literally zero (M19, M25). Forcing
  textures white turns the whole screen white (M23), which proves raster,
  blit and present all work.
- **The zero is guest state.** The guest's own descriptors ask for 1×1, and
  guest memory there is zero (M27).
- **I/O is healthy.** AMPR reads succeed, land in guest memory, and contain
  real UE4 data (M28–M30).
- **Conclusion so far:** the frame is black because the guest is still
  compositing its **loading placeholder**, i.e. the slow load (M32). This
  may be combined with a coherence bug that stops data written by the GPU
  (compute/storage, M22) from reaching the texture the composite samples.
  Both hypotheses are still open.
- **Watch for new evidence.** Every `vk.present_taken` shows
  `drawKind=None hasPixels=False hasTranslatedDraw=False`, and presents keep
  flowing after load while sound plays. The user's observation ("55 fps
  while loading, then 5 fps, sound starts, still black") suggests the game
  **does reach gameplay** but nothing reaches the flip buffer. Check this
  first (§5.A step 1).

## 4.3 Recent commits (read `git log --oneline -20`)

| SHA | What | State |
|---|---|---|
| `3c989cc` / `b83c4f6` | `bench_run.ps1`: runs the newest exe, counts presents correctly (`vk.present_taken`), adds `nonblack_readbacks` | tooling |
| `dac5ef7` | M35 fix: the zero-timeout cond_timedwait fast path applies only when no locker is queued (`SHARPEMU_DISABLE_COND_ZERO_TIMEOUT_FASTPATH=1` turns it off) | needs A/B |
| `289b10c` | zero-timeout cond_timedwait fast path; **livelocked Mortal Shell** until `dac5ef7` | superseded |
| `958754a` | lazy guest-work / submit diagnostic labels (no per-draw strings) | perf |
| `fb77087` | Kyty-parity NpWebApi2 offline stubs; unresolved-import log rate-limited; GAME-DBG strings built only when admitted | perf/compat |
| `a4cff62` | M34: the native mutex unlock hands off to a waiter that queued during the release (lost wake-up) | needs A/B |
| `5c70e54`, `66db254` | Quake II "GPU hanged" fix (GPU-aware usleep, Q9/Q10) | user re-test pending |
| `1035a7d` | native mutex fast path (lock/trylock/unlock with fallback) | shipped |

**Lessons from these two regressions:**

- A fast path may only skip work that is **provably a no-op in that state**.
- Unlocking a mutex has **side effects**: it hands the mutex off and gives
  queued threads a turn.
- A polling guest thread (`cond_timedwait(0)` in a loop, `usleep(0)`,
  trylock spins) relies on those side effects to let others run.

## 4.4 Known landmines

- `GuestImageWriteTracker` page guards are **opt-in**
  (`SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`). Enabling them CLR-FailFasts the
  process (M12): a managed write into an armed page leads to a reverse
  P/Invoke in cooperative GC mode. Any new write tracking must be native or
  must never guard pages that managed code writes.
- The import-loop watchdog is **opt-in** (`SHARPEMU_IMPORT_LOOP_GUARD=1`).
  It false-fired on legitimate UE busy-waits (M33).
- `__cxa_guard` ownership is keyed on the **guest** thread handle, never on
  the managed thread id (S5). Guest threads migrate across host workers.
- The emulator runs the guest in a **mitigated child process**. Kill
  `SharpEmu` processes before building (M31).
- PowerShell: bracketed paths (the Smurfs folder) need `-LiteralPath`.
- `SHARPEMU_LOG_IO=1` once stalled at startup (flaky). `SHARPEMU_LOG_OPEN=1`
  and `SHARPEMU_LOG_AMPR=1` are safe.
- Stack-built paths returning -1 from `sceKernelStat` (NID `eV9wAD2riIA`,
  thousands of lines in the Mortal Shell log) are mostly config probes, but
  **audit them once** (§5.C).

---

# 5. WORKSTREAMS AND NEXT STEPS (in priority order)

## 5.A Black screen (Mortal Shell first; the same class hits other UE4/Unity titles)

**Step 1: establish the truth about "after loading" (1–2 runs).**

- Run Mortal Shell for 15–20 minutes with
  `SHARPEMU_TRACE_GUEST_IMAGES=present` plus the readback cadence mode
  `SHARPEMU_TRACE_GUEST_IMAGES=1`. That mode samples frames 1, 30, 120 and
  then every 600th, so you get readbacks later in the run.
- Also enable `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1`.
- Record when sound starts (AudioOut pushes with non-zero samples) and when
  the fps drops. From the thread snapshots, find which threads start running
  (`GameThread`, `RenderThread`, `RHIThread`).
- Question to answer: **after the drop to 5 fps, does the guest still flip
  the same two display buffers (`0x8FC0000000`/`0x8FC2000000`), or other
  addresses?**
  - Turn on `SHARPEMU_LOG_VIDEOOUT=1` (and `SHARPEMU_LOG_VIDEOOUT_FPS=1`)
    to log `sceVideoOutRegisterBuffers` / `sceVideoOutSubmitFlip`
    (`src/SharpEmu.Libs/VideoOut/VideoOutExports.cs`), then compare the
    registered buffers with the flipped ones.
  - If the game flips a buffer SharpEmu never registered, or a buffer whose
    format/tiling differs from the registration, that is a presenter-mapping
    bug and a quick win.
- Also check `vk.present_taken … drawKind=None hasTranslatedDraw=False`.
  Find out exactly what this means in `VulkanVideoPresenter.cs` (search
  `present_taken`): does the presenter pick a GPU image for the flip
  address, or fall back to empty CPU memory?
  - If the flip's guest image was never produced by a GPU draw
    (`_availableGuestImages` has no entry for that address/format/version),
    the presenter may be showing a zeroed CPU mirror while the real image
    lives under a different key (different format, a DCC/tiled variant,
    another mip or slice). **Log the lookup key and every available key at
    that address.**

**Step 2: find who should write what the composite samples.**

- Dump the full binding set of the composite pixel shader. Its address
  changes every boot, so find it via the draw that writes the flip image; the
  helpers are `agc.texture_1x1_linear_binding` and
  `agc.texture_binding_sibling`.
- In the UE4 tonemapper/composite, the sampled input is normally the scene
  colour, or a UI render target produced earlier in the frame, often by a
  **compute** pass.
- For the sampled address, list **every** write to that address range in the
  frame: color render target, compute storage write, CP DMA / `DMA_DATA`,
  `WRITE_DATA`, a CPU memcpy, an AMPR read.
- On console there is one unified memory. In SharpEmu, each (address,
  format) is a separate VkImage, so a write through one view is invisible to
  another view unless something copies it across.

**Step 3: implement proper GPU-memory coherence (the structural fix, Kyty/shadPS4 model).**

- Study Kyty `renderer/cache/textureCache.cpp`, `bufferCache.cpp`,
  `faultManager.cpp`, `memoryTracker.cpp`, `pageManager.cpp`,
  `regionManager.h`, and `shaders/fault_buffer_process.comp`, plus the
  shadPS4 buffer cache (sources in §7). Then design a SharpEmu equivalent:
  - A **page-granular tracker** of guest memory with three states: CPU-dirty
    (needs upload), GPU-dirty (needs download before CPU or other-view
    reads), and clean.
  - **Native write detection.** Use a native VEH/page-protection handler
    that never re-enters managed code for the fast path, or hardware dirty
    tracking (`GetWriteWatch` on Windows `MEM_WRITE_WATCH` allocations; on
    Linux, userfaultfd write-protect or soft-dirty bits). Do **not** reuse
    the managed page guards (M12).
  - **Image aliasing / overlap resolution.** When a new view (another format,
    another size, a storage vs sampled view) covers memory that another
    image wrote on the GPU, copy or reinterpret on the GPU (`vkCmdCopyImage`
    for compatible classes, a compute shader for re-tiling or format
    reinterpretation). shadPS4 PR #4542 and #4591 are exactly this bug class
    (§7).
  - **Storage-image sync (M22).** After compute writes a storage image,
    mark the range GPU-dirty so the next sampled or other-format view pulls
    from it.
- Implement it **incrementally**, each step behind an env switch:
  1. Detect and log overlaps (no behaviour change).
  2. GPU→GPU copies for same-size compatible formats.
  3. Re-tiling / reinterpretation.
  4. CPU-write detection.
- After each step, A/B on Mortal Shell (`nonblack_readbacks`), Smurfs,
  Dreaming Sarah and Quake II.

**Step 4: keep the smaller known fixes** listed in
`docs/investigations/black-screen-research-2026-09-25.md` (F1–F5). F1 is the
upload-known short-cut safety valve (M21); F3 is readback ordering against
alias recreation.

## 5.B Performance: close the HLE-boundary gap (the Kyty model)

Measure with `SHARPEMU_PERF_HLE=1` (per-export CPU time) and
`bench_run.ps1 mortal 600` in **Release**. Record every result in
`docs/PERFORMANCE_PLAN.md`.

1. **Timed cond waits (largest remaining sync cost).** `PthreadCondWaitCore`
   currently allocates per call (a waiter object, a string `WakeKey`, a
   `Timer` for timed cooperative waits).
   - Replace the Timer with the scheduler's existing deadline support
     (`RequestCurrentThreadBlock(..., blockDeadlineTimestamp)`, handled by
     `WakeExpiredBlockedGuestThreads`).
   - Use a numeric or pooled wake key.
   - Keep the exact semantics:
     - **always unlock, and relock on return** (Kyty does);
     - re-acquire the mutex through the FIFO on both timeout and signal;
     - no lost signals.
   - The expiry must still grant the mutex hand-off. Study
     `CompleteCondWaiterLocked` and `TryGrantCondWaiterMutex`.
   - Add tests for: signal vs timeout races, broadcast, a timeout while the
     mutex is contended, and a poller plus a queued locker (see
     `CondTimedwait_ZeroTimeoutPollLetsAQueuedLockerIn`).
2. **Scheduler wake latency.** `WakeExpiredBlockedGuestThreads` is polled at
   about 1 ms (`Thread.Sleep(1)`), and `WakeBlockedThreads` does a **linear
   scan over all guest threads with string comparison** under a global lock.
   - Replace this with a dictionary from wake key to blocked threads and a
     min-heap of deadlines.
   - Use a waitable timer (`CreateWaitableTimerExW` with
     `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` on Windows) instead of a sleep
     loop.
   - Every producer→consumer link in the load chain pays this latency, so
     this is probably the biggest lever for load time.
3. **Extend native fast paths** (the Kyty zero-marshal model) to the next
   hottest NIDs from the profile. Candidates:
   - `pthread_cond_signal`/`broadcast` when there are no waiters (a pure
     no-op);
   - `scePthreadGetthreadid`;
   - `clock_gettime` / `sceKernelGetProcessTime` /
     `sceKernelReadTsc`-style time queries;
   - `sceKernelGetTscFrequency`;
   - `pthread_once` after completion;
   - `__cxa_guard_acquire` after completion;
   - `sceKernelUsleep(0)`;
   - `pthread_rwlock` read-lock/unlock when uncontended;
   - `sceAtomic*` style exports if present.

   Leaf stubs that never block are safe. Blocking ones need the fallback
   sentinel.
4. **memcpy (29M calls).** It is forced through managed HLE for the opt-in
   image tracker (`IsHlePreferredNid`).
   - When the tracker is **off** (the default), route `memcpy`/`memset`/
     `memmove` to a native stub (`rep movsb` or an AVX2 copy), as Kyty does.
   - Keep the managed path only when `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`.
   - Update `MemcpyHleRoutingTests` to encode the new condition.
5. **Per-dispatch overhead.** Audit the generic import trampoline and
   `DispatchImport`. Put all diagnostics (histograms, loop-guard signatures,
   probes, `Console.Error`) behind one cached bool, and make the register
   spill set minimal. Target: less than 1 µs per HLE call on the hot path,
   with zero allocations. Check with `dotnet-counters` (allocation rate) and
   the built-in profiler.
6. **Allocation audit.** Earlier data showed about 1.5 GB/s of managed
   allocation. Use `dotnet-trace collect --providers
   Microsoft-Windows-DotNETRuntime:0x1:5` (GC allocation ticks) or PerfView
   on a 120 s Mortal Shell run, find the top allocation stacks, and remove
   them (per-call strings, closures and lambdas in
   `RequestCurrentThreadBlock` delegate overloads, LINQ, boxing, `params`
   arrays). Also set `<ServerGarbageCollector>false</ServerGarbageCollector>`
   / `<ConcurrentGarbageCollection>` / `TieredPGO` and measure (GC settings
   are cheap A/B experiments).
7. **GPU pump.**
   - Batch Vulkan submissions: today they are near per-draw, with
     `vk.render_work_enter` for every item.
   - Record several draws per command buffer and submit per queue flush, as
     Kyty's `commandScheduler.cpp` does.
   - Confirm pipelines compile in parallel, and that the per-title pipeline
     cache is loaded before the first frame.
   - Measure with `RenderPhaseProfile` (it already exists in the presenter)
     and in RenderDoc or Nsight Graphics.
8. **AMPR reads.** Make reads asynchronous and overlapped, or memory-map the
   pak on the host and copy from the mapping. Check in Kyty `libAmpr.cpp`
   whether APR decompression (Kraken/Oodle-style hardware decode commands)
   is emulated. If the game submits compressed-read commands that SharpEmu
   treats as plain reads, or decompresses on the guest CPU, that is a large
   loading cost.
9. **Time policy (only after the above).** Consider scaled virtual time
   behind a new `SHARPEMU_TIME_SCALE` switch (does not exist yet). Do not use it to hide CPU slowness if the
   A/B breaks games.

## 5.C Correctness and compatibility items (each is a GAME_TRACKING row)

- **Mortal Shell `sceKernelStat` = -1 storm** (NID `eV9wAD2riIA`, about 8.5k
  warnings / 600 s from `ret=0x800A70DC3`/`0x800A70E8B`).
  - Log the path strings (`rdi` points at a guest string).
  - If any is a file that exists in the dump (case sensitivity, `/app0/`
    vs `/hostapp/` prefix, `dungeonhaven/` vs `DungeonHaven/`), fix the path
    mapping.
  - Missing config/save files are fine.
- **Unresolved NIDs** still seen: `Cm2cmtCv8cA`, `7hd4bRJuLMg`,
  `xaNK0ZTH3QA`, `NDMQ5Z8QdWQ`.
  - Search the aerolib catalogue and online NID databases (§7).
  - The NID hash is `base64(reverse(SHA1(name + 518d64a635ded8c1e6b039b1c3e55230)[0:8]))`
    with `/` replaced by `-`. The name list is `scripts/ps5_names.txt`.
  - Try the candidate names from the game's own import string table
    (`strings eboot.bin | findstr sce`).
- **Quake II:** confirm Q9/Q10 with `run_quake2_gpuhang.ps1`. Q7
  (`sceNpTrophy2GetTrophyInfo`) and Q8 (`sceImeKeyboardGetResourceId`) need
  Kyty-parity stubs.
- **Hellboy H6/H7:** the PreloadManager self-cache livelock
  (`scePthreadSelf` loop). The theories are in GAME_TRACKING. Compare
  Kyty's `PthreadPrivate` / guest-visible pthread block layout in
  `pthread.cpp`.
- **Smurfs:** measure time to menu against Kyty. Profile it the same way as
  Mortal Shell.
- Fix the 3 build warnings (SHEM006: `sceVoiceQoSSetMode`,
  `sceVoiceQoSTerminate`, `sceAgcAddPrimStateRegisters` are not in
  `ps5_names.txt`). Verify the names against the NID hash and add them to the
  catalogue if they are correct.

## 5.D Use Kyty as a live oracle (differential debugging)

Kyty runs these games, so use it as ground truth:

1. Run the same game in Kyty with its logging on (check `launcher.exe`
   options and Kyty's `emulatorConfig`) and capture:
   - the import-call sequence for the first N seconds;
   - which HLE functions it calls that SharpEmu never sees, and the reverse;
   - the return values of the first calls that differ.
2. Write a small script that aligns the two traces by NID and finds the
   **first divergence**. Often a single different return value (a stub
   returning an error or 0) sends a game down a slow or placeholder path.
3. For the GPU, capture a RenderDoc frame in each emulator (Kyty ships a
   `renderDoc.h` integration). Compare which render targets and textures the
   final composite samples.

---

# 6. TOOLS AND HARNESSES (already in the repo)

| Tool | Use |
|---|---|
| `.\bench_run.ps1 <game> <sec>` | A/B metric line: `imports`, `imports_per_s`, `presents` (via `vk.present_taken`), `reads`, `read_mb`, `nonblack_readbacks`; prints which exe it ran |
| `.\run_sarah_regress.ps1` | regression canary (60 s) |
| `.\run_ms_forcebisect.ps1 -TimerSeconds N -ThreadSnapshots -AmprTrace` | long Mortal Shell runs with streaming logs |
| `.\run_ms_guestimg.ps1`, `run_ms_framedump.ps1`, `bgra_to_png.ps1` | guest render-target fingerprints / frame dumps / PNG decode |
| `.\run_quake2_gpuhang.ps1 [-DisableFix]` | Quake II Q9 verdict |
| `.\run_smurfs_diag.ps1`, `run_hellboy_test.ps1` | per-title diagnostics |
| `SHARPEMU_PERF_HLE=1` | per-export HLE CPU-time profile |
| `SHARPEMU_PROFILE_GUEST_RIP=1` | guest instruction-pointer sampler |
| `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` | per-second thread table (state, block reason, wake key, import count). This is how the M35 livelock was found: threads whose import count stops changing |
| `SHARPEMU_TRACE_GUEST_IMAGES=present` / `=1` / `=every:N[@M]` / `=alias` | swapchain / guest-image readbacks |
| `SHARPEMU_LOG_AMPR=1`, `SHARPEMU_TRACE_AMPR_READ_CONTENT=1`, `SHARPEMU_LOG_OPEN=1` | I/O tracing |
| `SHARPEMU_LOG_AGC=1`, `SHARPEMU_LOG_AGC_SHADER=1`, `SHARPEMU_LOG_VK_RESOURCES=1` | GPU front-end tracing |
| `SHARPEMU_DISABLE_GAME_DBG=1` | silence GAME-DBG |
| Kill switches | `SHARPEMU_DISABLE_NATIVE_FASTPATH=1`, `SHARPEMU_DISABLE_COND_ZERO_TIMEOUT_FASTPATH=1`, `SHARPEMU_DISABLE_GPU_AWARE_USLEEP=1`, `SHARPEMU_IMPORT_STACK_TRACKING=1` |

The full list is in `docs/reference/environment-variables.md` and
`docs/GAME_TRACKING.md` ("Diagnostic tooling index").

External tools to install and use:

- **RenderDoc**: frame capture; see what the composite samples.
- **Nsight Graphics**: GPU timing.
- **PerfView** or **dotnet-trace / dotnet-counters**: CPU and allocation
  profiling of the managed side.
- **Windows Performance Recorder/Analyzer**: host scheduling latency and
  context switches. This is the key tool for the wake-chain problem.
- **x64dbg**: attach to a guest-visible hang.
- **Ghidra** (with a PS5 ELF loader), `objdump`, or a Python Capstone
  script: disassemble eboot call sites from `ret=` addresses.

---

# 7. ONLINE SOURCES TO READ (search for newer ones too)

- **KytyPS5**: https://github.com/KytyPS5/KytyPS5 (README, issues, PRs;
  compatibility list https://kytyps5.github.io/). Issue #58 is linked from
  the black-screen research. Original Kyty: https://github.com/InoriRus/Kyty
- **shadPS4** (the PS4 emulator most similar in the GPU area):
  - https://github.com/shadps4-emu/shadPS4
  - Buffer cache architecture: https://deepwiki.com/shadps4-emu/shadPS4/4.7-buffer-cache
  - PR #4591, storage images ↔ guest memory sync:
    https://github.com/shadps4-emu/shadPS4/pull/4591
  - PR #4542, stale memory when reusing GPU-written linear images through
    overlapping aliases: https://github.com/shadps4-emu/shadPS4/pull/4542
  - Also read their texture cache (`video_core/texture_cache/`), page manager
    (`video_core/page_manager.cpp`), and `video_core/buffer_cache/` in the
    repo.
  - Driveclub investigation method (runtime texture dumps):
    https://github.com/akitaonrails/shadPS4/blob/gamma-debug/docs/driveclub-investigation/phase-15-runtime-texture-dump.md
- **Other emulators** with the same problems (memory coherence, fast HLE):
  - RPCS3 (texture cache / "strict rendering mode"; issue #9968 is in the
    research doc)
  - xemu (issue #2516)
  - Ryujinx / yuzu forks (buffer and texture cache page tracking, native
    HLE thunks)
  - Vita3K
- **Hardware references:**
  - AMD RDNA2 ISA and PM4 packet documentation (GPUOpen): https://gpuopen.com
  - Mesa `radv`/`radeonsi` sources for register semantics
  - AMD tiling / addrlib (`addrlib` in Mesa)
- **PS5 system knowledge:**
  - psdevwiki PS5 (https://www.psdevwiki.com/ps5/)
  - NID databases (search "PS5 NID list aerolib"; the repo also has
    `docs/aerolib-catalog.md`)
  - ps4 OpenOrbis toolchain headers for libkernel semantics
- **.NET performance:**
  - `UnmanagedCallersOnly` / function-pointer interop docs
  - the "Writing high-performance .NET" guidance on allocation-free hot
    paths and `ArrayPool`
  - Windows high-resolution waitable timers
    (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`)
  - `GetWriteWatch` / `MEM_WRITE_WATCH` for dirty-page tracking
- **UE4 specifics:**
  - `FAsyncLoadingThread`, `FRunnableThread`, `FEvent` (UE4 builds `FEvent`
    on pthread cond/mutex; that is why `cond_timedwait` dominates)
  - the async loading and texture streaming docs, to understand what the
    loading screen composite samples

---

# 8. YOUR FIRST SESSION: CONCRETE PLAN

1. `git pull`, then `dotnet build -c Release`, then `dotnet test`. Read
   `docs/GAME_TRACKING.md`, `docs/PERFORMANCE_PLAN.md`,
   `docs/investigations/black-screen-research-2026-09-25.md`,
   `docs/architecture/*`, and this prompt.
2. **Re-baseline in Release with the fixed bench:** `.\bench_run.ps1 sarah 60`,
   `.\bench_run.ps1 mortal 600`, `.\bench_run.ps1 smurfs 300`. Replace the
   baseline table in `PERFORMANCE_PLAN.md`, noting Release vs the old Debug
   numbers.
3. A/B the two pending fixes in Mortal Shell (600 s each):
   - `SHARPEMU_DISABLE_COND_ZERO_TIMEOUT_FASTPATH=1` against default;
   - `SHARPEMU_DISABLE_NATIVE_FASTPATH=1` against default.

   Update M34/M35 to FIXED or ROOT-CAUSE.
4. §5.A step 1 (what happens after loading, and flip-buffer identity). This
   is the most likely route to a quick black-screen win.
5. §5.B items 1 and 2 (timed cond waits, scheduler wake latency). Measure
   and commit each separately.
6. §5.D: capture a Kyty import trace for Mortal Shell's first 60 s and diff
   it against SharpEmu's.
7. Continue down §5.B and §5.A step 3. Update the docs and push after every
   step.

# 9. REPORTING FORMAT (for each change you make)

```
Change: <one line>            Commit: <sha> (pushed)
Why: <evidence: log line / profile number / Kyty file:line>
A/B: bench_run <game> <sec> — before: <metrics>  after: <metrics>
Sarah regression: PASS/FAIL
Docs updated: GAME_TRACKING <row>, PERFORMANCE_PLAN <section>
User verification: <exact commands> — success metric: <what to look for>
Rollback: git revert <sha>   (or env kill switch: SHARPEMU_DISABLE_...=1)
```

When you are stuck, say so. List the hypotheses you tested (with evidence)
and the next experiment. Do not ship speculative changes without an A/B
switch.
