# SharpEmu performance plan — closing the gap to Kyty

**Goal.** Make titles run at Kyty-class speeds (Smurfs: 3–4 fps → tens of fps;
Mortal Shell: finish its ~9 GB asset load in minutes, not an hour).

**Can C# reach that speed?** Mostly yes, with a native island where needed.
SharpEmu already uses *direct execution* — guest x64 runs natively on the host
CPU, only imports/HLE calls enter managed code (same model Kyty uses in C++).
So raw guest code speed is not the bottleneck; **boundary crossings are**.
The plan therefore targets the HLE/sync/GPU boundaries, not the core executor.
A C++ rewrite is **not** required; a small native helper DLL for the very
hottest shims is the only C#→C++ step contemplated (phase E, optional).

**Grounded observations (from this session's logs):**

| Signal | Evidence | Cost source |
|---|---|---|
| Mortal Shell: 46k pak reads / 600 s (~162 MB/min) | `ms_forcebisect_control_20260926_035026.txt` | AMPR HLE read path per chunk: host `File.Read` + guest copy, synchronously |
| 236M imports in one run; guard fired at import #236,041,728 | `ms_forcebisect_control_20260926_041603.txt` | per-import dispatch overhead: `NextImportDispatchIndex`, `RecordImportLoopSignature`, histogram/guard bookkeeping, register spill/refill, `Console.Error` probes — all on EVERY call |
| `scePthreadCondTimedwait` storms (1 ms polls, ~thousands/s/thread) | both UE titles; Smurfs S1, MS M32 loaders | each poll = full import round-trip; guest spends most wall time *entering/leaving HLE*, not waiting |
| M33 false kill: watchdog fired on a gettimeofday poll loop | fixed this session (`IsImportLoopGuardTransparent`) | side effects of instrumentation |
| Presents ~0.5–4 fps while loading | user report (Smurfs sky) + MS 20 Hz identical frames | guest CPU-bound init + frame pacing on a host-clock time base |

## Phase A — measure first (no optimization without a number)

1. Add `bench_run.ps1 <game> <seconds>`: fixed env (all TRACE/diag OFF), prints
   wall time, imports, imports/s, presents, MB read. Needed to A/B every step.
2. `dotnet-counters` + `dotnet-trace` (or PerfView) on a 120 s boot:
   - imports/s and HLE total CPU % (top NIDs by self time),
   - allocation rate on the import path (goal: 0 B/import in steady state),
   - time in native worker stub vs HLE body.
   **Done via the built-in profiler instead** (`SHARPEMU_PERF_HLE=1`, 180 s
   Mortal Shell, `bench_perfhle_mortal_*.txt`): HLE = 736 CPU-s over 174 s wall
   (**4.2 cores busy**). Breakdown: `sceKernelUsleep` 2.03 cores (352 s over
   59k calls — the Q9 GPU-aware wait, ~6 ms/call while the GPU is busy),
   `memcpy` 0.50 cores (29M calls), `sceAudioOut2ContextPush` 0.41 cores
   (blocked), `pthread_cond_timedwait` 0.21 cores (6.9M calls),
   `scePthreadMutexLock` 0.20, `scePthreadGetspecific` 0.19 (**65M calls**,
   0.5 µs/call ≈ the dispatch floor). Conclusions:
   (a) ~0.5 µs fixed dispatch cost × 84M+ tiny-op calls is a large, real cost;
   (b) the usleep GPU-wait throttles *all* sub-ms sleepers while the GPU is
   busy (which is always, during black-frame streaming) — the biggest lever
   but touches Q9/Q10 (pending user verification), so handle with an A/B;
   (c) Phase C cond-wait storms confirm: 6.9M timed-waits / 174 s.

### Phase B results (2026-09-26)

| Change | Baseline → After | Verdict |
|---|---|---|
| Stack-arg capture (`LastImportStack0-5` guest reads/import) gated behind `SHARPEMU_IMPORT_STACK_TRACKING=1` | 4.993 → 4.933 µs/dispatch avg | **−1%, kept** (strictly less work; zero functional loss) |
| `TryCopy` thread-static last-region cache (+ version-guarded invalidation) | memcpy 3.04 → 2.83 µs; total HLE 767 → 700 CPU-s; dispatch 5.17 → 4.86 µs (Mortal Shell 180 s arms) | **~7% on memcpy, kept** |
| **Phase B step 3 — native zero-marshaling leaf stubs** (Kyty model: PLT → tiny 4-arg SysV→Win64 call shim → `UnmanagedCallersOnly` handler). NIDs: `scePthreadGetspecific`/`pthread_getspecific`, `scePthreadSelf`/`pthread_self`, `gettimeofday`; opt-out `SHARPEMU_DISABLE_NATIVE_FASTPATH=1` | `scePthreadGetspecific`, `scePthreadSelf`, `gettimeofday` **vanish from the HLE profile** (were 65M+11M+7M calls/180 s). Mortal Shell 180 s: reads ~24k steady, guard kills 0. Smurfs reaches non-black frames faster than before (2/2 readbacks non-zero at 90 s vs 0/2 at 120 s earlier) | **shipped** (`NativeFastPathRegistry` + `NativeFastPathRegistration` + shim emit in `TryCreateNativeImportIntrinsic`) |
| **Phase B step 4 — mutex fast path with fallback** (`9UK1vLZQft4`/`7H0iTOciTLo`/`upoVrzMHFeE`/`K-jXhbt2gn4`/`tn3VlD0hG60`/`2Z+PpY6CaJg`): shim saves guest rdi/rsi/rdx/rcx, calls the fast handler, and on the `FallbackSentinel` restores them and tail-jumps into the full trampoline. Contention, blocking, DEADLOCK checks and waiter handoff stay in the managed core | Mortal Shell 180 s A/B: `scePthreadMutexLock`+`Unlock` **gone from the profile** (were 35.5s+13.5s ≈ 7% of HLE CPU); HLE total 700→628 CPU-s. Sarah green; Smurfs 10/11 readbacks non-zero at 300 s | **shipped** (`TryCreateNativeFastShim` + `FastMutexLock/Trylock/Unlock` in `KernelPthreadCompatExports`); **lost-wakeup bug fixed afterwards** (GAME_TRACKING M34): the native unlock now performs the waiter hand-off when a waiter queued during the release |

### Phase C findings (2026-09-26, 180 s Mortal Shell A/B)

- **`SHARPEMU_DISABLE_GPU_AWARE_USLEEP=1` A/B:** ON = 25,042 pak reads / 767 HLE CPU-s; OFF = 15,977 reads / 381 CPU-s. The GPU-aware usleep is **not** a loading throttle — disabled, sleeper threads *spin* (more HLE churn, *fewer* reads); enabled, they park and free cores for the loaders. **Keep it.** Do not disable for Mortal Shell.
- **Pak read latency is NOT the floor either:** 46k reads × ~0.3-0.4 ms ≈ 18 s of 180 s wall. The load is bounded by guest-side work (decompression/copy) executing between HLE calls — i.e. total CPU throughput and core contention, not any single syscall.
- **Guest-RIP sampler run** (`SHARPEMU_PROFILE_GUEST_RIP=1`, 180 s, `phasec_ripsample_20260926_100524.txt`): **98.7% of guest thread-time is waiting**, top waits = `<idle-or-scheduler>` 78%, `sceAudioOut2ContextPush` 10%, mutex/submit ~6% — the sampler lands on *host* RIPs, i.e. threads parked in HLE waits. No single guest busy hotspot: the load is a long **producer→consumer wake-chain**, and each link costs HLE round-trips + host scheduling latency. Also captured: a periodic "Stall main-thread" watchdog dump at a `scePthreadGetthreadid` stub (benign reporting, no kill).
- **Revised conclusion:** the biggest lever is *reducing per-transition latency on sync primitives/boundaries* (they gate every task handoff), not removing the waits:
  1. lock-free region reads (immutable snapshot array instead of the read-lock on ~84M tiny ops/s),
  2. inline TLS read for `scePthreadGetspecific` (65M calls) in the stub itself,
  3. audio push pacing is real-time and necessary — exclude.
- Status: Phase B shipped (~7% memcpy, 4.86 µs dispatch down from 5.17). Expect further gains to be incremental-per-primitive; measure each with `bench_run.ps1 mortal 600`.
3. Record the baseline table in this doc before changing code.

### Baseline (2026-09-26, bench_run.ps1, snapshots+AMPR+present-readback on)

| Game | Duration | Imports | Imports/s | Presents | Reads | Read rate | Notes |
|---|---|---|---|---|---|---|---|
| sarah | 60 s | 2.32M | 38.6k/s | 24 | 0 | — | playable guard |
| smurfs | 300 s | 20.95M | 69.8k/s | 10 | 1,255 (381 MB) | 76 MB/min | frames non-black, hash evolves (rendering alive) |
| mortal | 600 s | 273.7M | 456k/s | 72 | 46,250 (1.62 GB) | 162 MB/min | still loading; loader cond-poll storms dominate imports |

## Phase B — boundary-cost cuts (C# only, biggest expected win)

1. Slim `DispatchImport`: all diagnostics (`_perfHleHistogram`,
   `RecordImportLoopSignature`, sentinel-recovery prints,
   return-address probes) behind a single cached `DiagEnabled` bool so the
   fast path is ~load args → call → store result.
2. Pre-spill only the volatile registers the callee clobbers; today every
   dispatch spills/loads 14 registers regardless of the HLE function.
3. Give the top ~20 hot NIDs (cond waits, memcpy/memset family, time queries,
   TLS) dedicated `UnmanagedCallersOnly` thunks that skip the shared tail.
4. Verify zero per-dispatch allocations on the hot path (string formatting
   only under trace flags — audit with the Phase A counter).

## Phase C — threading/time model (fixes "loading takes an hour")

1. Real blocking `pthread_cond_timedwait` (condition-variable wait with the
   guest's timeout) instead of host-time 1 ms slice polls, so loader threads
   park instead of burning HLE round-trips — cuts imports/s by orders of
   magnitude and frees host CPU.
2. Decide time policy explicitly: host-real clock (current) vs scaled
   virtual time. Prototype a `SHARPEMU_TIME_SCALE` (e.g. 0.25× guest time)
   behind an env flag, A/B against Sarah + the four tracked titles before
   defaulting anything. (Kyty's speed partly comes from simply being fast
   enough that real-time pacing works; do not paper over CPU slowness with
   time scaling if A/B breaks games.)
3. AMPR file reads: async overlapping reads (or memory-map paks on the host
   and copy) so the guest's 25–77 reads/s stop being latency-bound.

## Phase D — GPU path

1. Batch command-buffer parsing and Vulkan submission (today: near per-draw
   `vk.render_work_enter` submissions with 0.05–0.15 ms queue times — fine
   individually, but thousands/frame-second add up).
2. Pipeline caches are already persisted; verify parallel shader compile is
   on during the first heavy frames (Quake II Q9 showed first-frame compile
   stalls are the dominant early-frame cost).
3. Keep readback/trace tooling strictly opt-in in user runs (it forces GPU
   sync per present — it is already opt-in, keep it that way).

## Phase E — optional native island (only if B–D leave a gap)

Move the top-5 hottest shims (memory leaf ops, TLS read, cond wait wakeup,
time queries) into a tiny C++ DLL with naked exports called directly from the
import stub table — no P/Invoke marshaling, no managed frames at all. This is 90%
of a C++ rewrite's boundary benefit for ~1% of the code.

## Gates (must hold after every phase)

- `bench_run.ps1` numbers improve or stay flat for each title.
- `run_sarah_regress.ps1` stays PLAYABLE (60 s, no FailFast).
- Mortal Shell, Smurfs, Hellboy, Quake II: no new table rows of class OPEN.

## Evidence so far (2026-09-26 session)

- Smurfs black screen **fixed** (`__cxa_guard_*` ownership by guest thread
  handle, not host managed thread id — S5): game presents non-black frames
  (sky) at 3–4 fps in 120 s.
- M33 freeze **fixed** (loop-guard transparency for time queries): the 30 min
  run no longer gets killed mid-poll.
- Mortal Shell: load measured at ~162 MB/min against an 8.69 GB pak — the
  black screen is dominated by load progress, which Phase C targets.

### Load-time overhead trims (Mortal Shell loader report)
- Unresolved-import warnings are rate-limited per NID (they used to print on every call, with a stderr flush).
- GAME-DBG per-draw/compute/present strings are formatted only when the rate limiter admits them (`GameDebug.ShouldEmitRateLimited`/`Emit`); `GameDebug.Enabled` is cached.
- Kyty-parity offline NpWebApi2 stubs, so the UE online task thread gets a clean offline result instead of unresolved-import traps.
- Next: make the `DescribeGuestWork` label lazy (it allocates per work item), a cond_timedwait fast path (Kyty `wait_for` shape), an allocation-source audit.
