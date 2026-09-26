# Prompt: make SharpEmu reach KytyPS5 parity (copy-paste into a fresh AI session)

> Paste everything below the line into the agent.

---

# Task: Bring SharpEmu to KytyPS5 parity (correctness + speed)

## Repos and locations

- **SharpEmu (the project you are fixing):** `C:\ps5-emulator\sharpemu` (C#/.NET 10, direct execution of guest x64 + HLE libs). Builds with `dotnet build` (Debug) and `dotnet build -c Release`.
- **KytyPS5 reference source:** `C:\ps5-emulator\kytyps5-ref` (C++ PS4/PS5 emulator). If it's missing or stale, clone it fresh (it's a mirror of the public Kyty emulator, originally at github.com/InoriRus/kyty — the PS5 branch/fork used locally is fine; any current Kyty source tree works):
  ```powershell
  git clone https://github.com/InoriRus/kyty.git "C:\ps5-emulator\kytyps5-ref"
  ```
- Kyty Windows binary for reference behavior: `C:\ps5-emulator\KytyPS5-2026-09-25-fd2e15e-Windows-x64\launcher.exe`.
- Games live in `C:\ps5-emulator\ps5-games\` (TitleID folders).

## Read first (state of play — do NOT re-derive these)

1. `docs/GAME_TRACKING.md` — the single source of truth for every title, every known issue (M*/S*/Q*/H* rows), every ruled-out theory.
2. `docs/PERFORMANCE_PLAN.md` — the performance plan with measured baselines and phase A/B/C results.
3. `git log --oneline -15` — recent work (S5/M33 fixes, per-title pipeline cache, native fast-path stubs).

Key measured facts (2026-09-26):

- SharpEmu runs guest x64 **natively** (same model as Kyty). The gap is at HLE boundaries, not CPU translation.
- HLE profile over 180 s of Mortal Shell: 700+ CPU-seconds on 4+ cores; top costs were `scePthreadGetspecific` (65M calls), `memcpy` (29M), `scePthreadMutexLock` (10.5M), `scePthreadSelf` (11M), `pthread_cond_timedwait` (6.9M), and parked `sceKernelUsleep`/audio pushes.
- Kyty kills these costs with **zero-marshaling HLE**: import PLT slots point directly at SysV-ABI C++ handlers (see `kytyps5-ref/src/loader/runtimeLinker.cpp`), guest memory is identity-mapped (`kernel/memory.cpp`), TLS is a real `thread_local` (`kernel/pthread.cpp`). SharpEmu now has the same mechanism for the hottest 3 NIDs (`NativeFastPathRegistry` + the scraper shim in `DirectExecutionBackend.TryCreateNativeImportIntrinsic`) — extend it.
- SharpEmu writes per-title Vulkan pipeline caches now (`user/pipeline_cache/<TitleId>/`); Kyty's equivalent warm cache is why its second runs start fast.

## Definition of done (parity targets)

For the two tracked problem titles, on the user's machine (i5-12450H / RTX 3050 Laptop):

1. **Smurfs – Dreams (PPSA21607):** reaches its title/menu and plays it at a *visibly responsive* framerate comparable to Kyty (Kyty does it in seconds from launch), with no black-screen delay.
2. **Mortal Shell (PPSA02868):** finishes its asset load and reaches gameplay (Kyty-class: minutes, not the current ~hour of HLE-bound loading).
3. **No regressions:** Dreaming Sarah (PPSA02929) must stay playable after every change; Quake II (PPSA09477) and Hellboy (PPSA11264) must not gain new OPEN issues.

## Method (mandatory)

1. **Rebuild before believing any log** (`Get-Item artifacts\bin\Debug\net10.0\win-x64\SharpEmu.exe | Select LastWriteTime` vs `git log -1`). Historical session wasted hours on a 5-day-stale binary (M13).
2. **A/B one variable at a time** with `bench_run.ps1` (`bench_run.ps1 mortal 600`, `bench_run.ps1 smurfs 300`, `bench_run.ps1 sarah 60`). Baselines are in `docs/PERFORMANCE_PLAN.md`. Every optimization needs before/after numbers in that doc.
3. Profiling tools already built in: `SHARPEMU_PERF_HLE=1` (per-export HLE cost), `SHARPEMU_PROFILE_GUEST_RIP=1` (guest-IP sampler), `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` (per-thread state/sec), `run_smurfs_diag.ps1`, `run_ms_forcebisect.ps1` (streams logs; kills are safe).
4. **Compare against Kyty source for any semantic you touch** — Kyty implements these libraries already (see `kytyps5-ref/src/libs/`): NP/social (`libNet.cpp` incl. `NpWebApi2CheckTimeout`), pthreads/`syncOnAddress`, AGC/Gnm (`agc*.cpp`), AMPR (`libAmpr.cpp`), AJM audio (`ajm`). If the game waits on something Kyty answers and we don't, port the *semantics*. Smurfs currently has unresolved NP NIDs (`7hd4bRJuLMg`, `Cm2cmtCv8cA`, `xaNK0ZTH3QA`, `NDMQ5Z8QdWQ` — identified as the UE online subsystem, non-fatal so far; Kyty doesn't have them either).
5. `run_sarah_regress.ps1` after *every* code change — the reference title is the canary.
6. Never commit without explicit user confirmation. When committing: small focused commits, conventional-commit style matching the log.
7. Keep `docs/GAME_TRACKING.md` and `docs/PERFORMANCE_PLAN.md` updated as the single source of truth.

## Concrete next steps (ranked by measured value)

1. **Mutex fast path**: `scePthreadMutexLock`/`Unlock` are ~20M calls/boot. Kyty does owner-CAS directly on the guest mutex word (`kernel/pthread.cpp`); implement a native/UnmanagedCallersOnly try-lock fast path (uncontended only) with fallback to the existing managed path. Watch out: `TKO` recursion fields + the import-boundary exception delivery (`TryDeliverPendingGuestExceptionAtImportBoundary`) must not be bypassed on paths that can deliver queued guest signals.
2. **cond timedwait fast path** (~6.9M calls): monitor-less fast re-wait when the version word didn't change; keep the real blocking semantics (we measured: parking is CORRECT — disabling waits is a regression because spinners eat cores).
3. **Scan remaining HLE profile** after those land (`SHARPEMU_PERF_HLE=1`) and apply the same native-stub treatment to whatever leads (`sceAudioOut2ContextPush` pacing is real-time and is NOT a target).
4. **Verify Smurfs reaches its menu** and measure time-to-menu vs Kyty (Kyty: seconds). The 600 s run reached an animated title loop (evolving frame hashes); confirm the interactive menu and capture fps.
5. **Verify Mortal Shell finishes loading** (run up to 60 min: `run_ms_forcebisect.ps1 -TimerSeconds 3600 -ThreadSnapshots -AmprTrace`; verdict criterion = first non-zero `vk.swapchain_image` readback).

## Hard-won landmines (do not step on these again)

- `GuestImageWriteTracker` is **opt-in** (`SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`); its page guards used to CLR-FailFast the process (M12).
- The import-loop watchdog must ignore pure time queries (M33) — don't remove `IsImportLoopGuardTransparent`.
- `__cxa_guard` ownership is keyed on the *guest* thread handle, never `Environment.CurrentManagedThreadId` (S5) — guest threads migrate across host threads.
- Checkpoint: `IsHlePreferredNid` forces `memcpy` through the managed path deliberately (image-tracker `NotifyManagedWrite`); bypassing memory HLE without servicing the tracker breaks the opt-in sync.
- The mitigated child process survives parent kills — `Get-Process SharpEmu | Stop-Process -Force` before builds.
- PowerShell: bracketed paths need `-LiteralPath`; `Select-String -Recurse` doesn't exist (pipe items in).
