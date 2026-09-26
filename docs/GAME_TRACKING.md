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
| PPSA09477 | Quake II (2023) | KEX Engine | **BOOTS** — the `GPU hanged` StartFrame abort (black screen → freeze → ERROR dialog, Q9) fixed in `66db254`; re-test on Windows |
| PPSA11264 | Hellboy: Web of Wyrd | Unity (IL2CPP + FMOD) | **BOOTS** — H6 livelock GONE (TRC watchdog forces suspendPoint); fails later in guest memcpy AV |
| PPSA21607 | The Smurfs – Dreams | Unreal Engine 4 (FMOD) | **RENDERS** — 8/9 swapchain readbacks fully non-black with evolving hashes (300 s verify, 2026-09-26); black screen root cause was S5 (__cxa_guard host-thread identity), fixed this session; speed is the remaining issue (perf plan) |

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
init=True). Current clean run (300 s, tracker off): **250,122 draws**, 477
sampled draws, **6,015 flips presented**, 64 computes, `AgcSubmissionThread`
Running, `FAsyncLoadingThread` at 19.6M imports, 5× 1× 1 placeholder textures
remaining. The black screen is now **measured**: the swapchain image the
presenter hands to the window is all-zero on 17/17 sampled flips
(`nonzero_bytes=0/8294400`, identical hash; the raw dumps decode to a single
unique pixel value), so the loss is at or before the guest image that gets
blitted. The previous "FPS drops to 2 and the process dies" behaviour was a
separate, now-fixed crash (M12).

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
| M12 | **CLR FailFast** `Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code` — killed the process ~4 min in ("fps dropped to 2 then crash") | **FIXED** | **`GuestImageWriteTracker` page-guard design.** Arming a page turns any *managed* write into it into a CLR-fatal AccessViolation (documented at `GuestImageWriteTracker.NotifyManagedWrite`) instead of a resumable guest fault; the fault then reaches the VEH trampoline, which reverse-P/Invokes the managed `VectoredHandler` (`Exceptions.cs:58-60`, `Marshal.GetFunctionPointerForDelegate`). If the faulting thread is in cooperative GC mode that transition is illegal → CLR kills the process. Intermittent because it needs a managed writer to hit an armed page. **Every** observed FailFast (4/4 logs) was immediately preceded by `[SYNC] cpu-write-drain`. Tracker is opt-in again (`SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`) | `c2d84f9` |
| M13 | **Stale-binary trap**: every "M11/M12 still broken" conclusion in this doc was drawn from logs produced by a build dated **2026-09-14 20:34**, i.e. *before* `0872285` (09-15, native-worker routing), `39dd33c` (09-15, max_concurrent 2→16), `9216ba7`/`ed220be` (09-16). Rebuilt 2026-09-25 and re-ran: pool is now `prewarmed 16/16 max_concurrent=16` (was `4/4 max_concurrent=2` — the serialization the doc blamed for the throughput drop), and the old `CallNativeEntry ← ExecuteGuestContinuationEntry` stack is **gone** (routing fix works; the remaining FailFast had no stack at all) | **FIXED (process)** | always rebuild before drawing conclusions from a log; check `Get-Item ...SharpEmu.exe \| Select LastWriteTime` against `git log -1 --date=iso`. Runs take 5 min, builds take 2.7 min — the build is the cheaper mistake | — |
| M14 | `_onGuestExecutionRunnerThread` was **dead code** — set on `GuestExecutionRunner.ThreadMain`, `GuestContinuationRunner.ThreadMain` and `RunContinuationOnTemporaryThread`, but never read (compiler `CS0414`), so the documented invariant "guest stubs must never run above a CLR runner thread's managed frames" was unenforced and `RunGuestEntryStub` would still fall back to a managed inline `calli` if any caller passed `requireNativeWorker: false` | **FIXED** | `RunGuestEntryStub` now computes `mustUseNativeWorker = requireNativeWorker \|\| (_onGuestExecutionRunnerThread && !NativeGuestWorkersDisabled)` and refuses/yields instead of inlining on runner threads; the explicit `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS=1` opt-out still permits the historical inline path | same commit as M12 |
| M15 | **The image handed to the window is provably all-zero** — `vk.swapchain_image size=1920x1080 nonzero_bytes=0/8294400 nonblack_pixels=0/2073600 hash=0x97D30483E5DD6325` on **17/17** sampled flips; decoding the 17 raw `.bgra` dumps gives `sample_unique=1`, `nonzero_bytes=0`, so the window has shown pure black the entire run | **OPEN (measured)** | the loss is at or before the guest image that gets blitted, **not** in the presenter: 6,015 flips presented at ~20 Hz, `vk.present_dropped`=0, every present logs `init=True`, and the swapchain was recreated only 3 times (all in the first seconds, `SuboptimalKhr` after the window restore) | — |
| M16 | Presenter/pipeline suspects that turned out to be **false alarms** (`pixels=False`; "only one frame presented") | **RULED OUT** | `pixels=` is `presentation.Pixels is not null`, i.e. *CPU*-backed frames; GPU images legitimately log `False` (Quake II logs the same). `presented first frame` / `presented guest frame` are one-shot guards (`_firstFramePresented`, `_firstGuestDrawPresented`), **not** per-frame counters | — |
| M17 | **Guest-image probe: the guest renders fine — only the flip images are zero.** `SHARPEMU_TRACE_GUEST_IMAGES=every:50@5000` (200 s, `run_ms_guestimg.ps1`): non-flip targets carry real content (`[RB] addr=0x2035040000 mean=71,71,71,A71 sample_unique=38`; `[RB] addr=0x2041AD0000 mean=0,0,220,A247`; `0x2024410000` R8Unorm `nonblack_pixels=2433600/5760000`; `0x2034040000` `nonblack_pixels=608400/1440000`), while `[RB] addr=0x8FC0000000` and `0x8FC2000000` read `mean=0,0,0,A0 sample_unique=1` on **~40 samples each** | **OPEN** | the loss is exactly the composite that fills the flip image — the emulator builds it (`SharpEmu offscreen mrt=1 ps=0x2005B40000 first=0x8FC2000000 3840x2160`), no pipeline/shader error is logged, and the flip images' zero state is the emulator's own initial/DCC-cleared state | — |
| M18 | Tie-breaker instrumentation: the 1×1 placeholder warning did not say **which pass** sampled it or what the descriptor bytes were, so "composite samples a placeholder" (M6) and "composite draw is dropped" were indistinguishable | **FIXED (instrumentation)** | `agc.texture_1x1_linear_binding` now logs `ps=` (pixel shader), `es=` (export shader), `op=`, `storage=` and `raw=` (the raw resource-descriptor dwords); still printed once per address, so no spam | AgcExports.cs (this session) |
| M19 | **The 5 placeholders are stable guest state, not a capture race** — `agc.texture_1x1_linear_rebound` never fired in a 300 s run (`rebounds=0`, `placeholders=5`, `failfast=0`), and all 5 descriptors share one shape: plausible base/format/type/tile with the **dimension words literally zero** (`raw=3948B500,03800000,00000000,90100FAC,…`; `raw=397FE500,04D00000,00000000,91B00FAC,…` for fmt=14) | **OPEN** | the guest itself populates `pc=0x50` — the slot the flip-buffer composite samples — with a 1×1 dummy and **never updates it for the whole run**, so the emulator is faithfully rendering placeholder material. The black screen is not a binding/race bug; it is the game still compositing placeholders | — |
| M20 | **Why is the real texture never streamed?** At 300 s the loading threads are still busy (`SlateLoadingThread2` 20.4 M imports, `FAsyncLoadingThread` 19.6 M, `DH_SaveGameThread` Running) | **OPEN (hypothesis)** | check the streaming path rather than the descriptor path: file I/O failures (`SHARPEMU_LOG_IO` / `LOG_OPEN`), async-file completions that never fire, or a GPU copy that never executes; and whether a loading UI ever reaches the flip buffers | — |
| M21 | **`IsGuestImageUploadKnown` can answer "known" with no evidence.** With the write tracker off it calls `IsUntrackedGuestImageContentUnchanged`, which returns `true` (= *skip the upload*) whenever `byteCount == 0` — exactly the case for an address with no registered extent — so the caller builds the texture with `initialPixels: []` and the composite samples a GPU image nobody filled | **REFUTED as the black-screen cause** (mechanism real, safety valve added) | Forcing the copy with `SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD=1` refused the short-cut **85 times / 83 addresses — including all 5 placeholder addresses** — and the presented image was **still `nonzero_bytes=0/8294400` on all 14 readbacks, identical hash**. So the skip is a genuine defect worth keeping the valve for (`SHARPEMU_TRACE_UPLOAD_KNOWN=1` audits it), but it does not change a single presented pixel. Prior art: shadPS4 Buffer Cache + PRs #4591/#4542 — see `docs/investigations/black-screen-research-2026-09-25.md` | instrumentation this session |
| M22 | **Compute writes to storage images are never copied to the address' other representations.** `SharpEmu compute cs=… storage=0x…200CC20000 1x1 fmt10 1x1x1` — per `BuildComputeDebugName` the `1x1` is the *descriptor image size* and `1x1x1` the guest's *dispatch group count*; the writes land at 64 KB steps in the same address family as the placeholders | **OPEN (documented fix pattern)** | on the console, unified memory lets a texture at that address read the CS output directly; here each (address, format) is its own `VkImage`, so something must copy across. shadPS4's `storage_image_sync` does exactly that (download → re-tile → notify caches). See F2 in the research doc | — |
| M23 | **The render/target/blit path works — the black frame comes from a zero-valued *input texture*.** With `SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS=*` (765 texture uploads forced to 0xFF, including `3200x1800`, `1024x1024`, `64x64`) one presented frame read back **`nonblack=2073600/2073600` — a completely white 1920×1080 screen** (`hash=0xD0EEE7805AF4C325`) | **ROOT CAUSE NARROWED (measured)** | the composite that fills the flip image *does* execute and the present blit *does* show its output; the frame is black because a real-sized sampled input is zero at present time. The five 1×1 placeholder addresses were **not** among the forced textures (`force_white=False`) and the frame did not go black because they were zero ⇒ **the 1×1 dummies are a red herring for the final image** (correction to M6/T4/T6). Next: dump `ps=0x2005B40000`'s binding set and find which real address is zero, then why | — |
| M24 | The target-scoped shader overrides (`SHARPEMU_FORCE_SOLID_FRAGMENT_TARGETS=0x8FC0000000,0x8FC2000000` + fullscreen vertex + default raster state) did **not** change the presented image (still `nonblack=0/2073600`) even though `CreateTranslatedDrawResources` is called with its targets by the offscreen path | **OPEN (debug-switch gap)** | either the address match fails for these draws or the override never reaches the pipeline. Verifiable without a rebuild: `SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT=<path>` is written **iff** `forceSolidFragment` was true for some pipeline (`VulkanVideoPresenter:7438`) — `run_ms_forcebisect.ps1 -Targets …` now sets it | — |
| M25 | **The pass that fills the flip buffer binds exactly ONE image — the 1×1 dummy.** From the new sibling dump (`agc.texture_binding_sibling`, printed once per placeholder address *in the same run*, which matters because the composite's shader address changes every boot: `0x2005B40000` in three runs, `0x2035DEC0000` in another): `ps=0x2005B40000 pc=0x50 op=ImageSample storage=False addr=0x2004550000 1x1 fmt=10 num=0 tile=1 type=9 mip=0-0/0` — and that is its **only** image binding | **ROOT CAUSE NARROWED (measured)** | the whole frame equals that single texel: flat black while it is zero, flat **white** when the 1×1 textures were forced white (M23). So the black screen reduces to *"what should that 1×1 image contain, and who should have written it"* — either the descriptor is a placeholder for a texture the guest never streamed (M19/M20), or we sample the wrong image for it (address-0 fallback / shared 1×1 path). Discriminating run: `run_ms_forcebisect.ps1 -WhiteTextureTargets 0`, which whitens **only** address-0 fallbacks | — |
| M26 | Follow-ups to M25: whitening **only** address-0 fallbacks does nothing (`texture_force_white`=0 lines, frame still all-zero) ⇒ the pass samples the descriptor's **own** 1×1 image, not the fallback; and with the upload-known short-cut active a 90 s run emitted only **one** `vk.texture_upload_contents` line ⇒ the texture-upload path barely runs at all | **OPEN — exactly one question left** | *is the guest's 1×1 texel zero, or is it non-zero and we fail to sample it?* **Zero** ⇒ the guest never produced the texture (upstream: CS→texture sync / streaming — F2/M20). **Non-zero** ⇒ our binding discards the content (a rendering fix here). The harness now forces `SHARPEMU_TRACE_UPLOAD_KNOWN=1` so a force-upload arm can prove the force applied | — |
| M27 | **The guest asks for 1×1 everywhere and has written zero content there.** With `SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS=*` every compute storage target reports `size=1x1 pitch=1 logical_bytes=4 physical_bytes=65536 read=True nonzero=False initial_bytes=0` — the guest's **own** descriptors say 1×1, the emulator **does** read guest memory for them, and it is all zero. The sampled placeholder (`pc=0x50`, `1x1`, `addr=0x2004550000` this boot) is not even a storage target. Skipped sampled binds measured the same way: `vk.upload_known_skip … guest_bytes=00000000000000000000000000000000` | **EMULATOR SIDE CLOSED — the cause is guest state** | with M23/M25 this means the emulator is faithfully rendering what the guest asked for: one 1×1 zero texel per frame (flat black; flat white only when we inject content). Nothing on the rendering side is broken — raster, offscreen submission, present blit, forced uploads and DCC publish all demonstrably work. The remaining work is upstream of rendering: why the game only ever produces 1×1 with no content. Unity semantics make this concrete: 1×1 is the *smallest mip*, i.e. "nothing streamed yet" | — |
| M28 | **The title is UE4 (`dungeonhaven`) and it reads its 8.69 GB pak successfully.** `SHARPEMU_LOG_OPEN=1`: `_open dir '…/dungeonhaven/content/paks'` (found), `stat '…/dungeonhaven-ps5.pak' result=found`, `apr_resolve … id=0xA2CB31C4 size=8694639526`. `SHARPEMU_LOG_AMPR=1` (90 s): **2150 `ampr.read_file` calls, every one `result=0`**, full byte counts (`size=0x739174 read=0x739174`, `0xB469DC`, `0x40000`), from the correct host path into guest destinations `0x2001xxxxxx`–`0x2003xxxxxx`. `ampr.get_size` returns the command-buffer size (0x4000); `apr.get_file_size` is never called | **I/O and the AMPR read path are healthy — refutes "the game cannot read its assets"** | so asset bytes do reach guest memory, and the loss happens *after* the read (decompress / copy into the final texture / upload). Next: fingerprint the read destination right after each read, then find the step that should move that data into the sampled texture address | — |
| M29 | **Three false alarms caught and corrected while chasing this (do not re-report them as bugs)** | **RULED OUT** | 1. `fstat … size=0 dir=0` on the `saved/config/ps5/*.ini` files: those are opened with flags `0x601` (create/truncate), so size 0 at that moment is correct — `engine.ini` is 1061 bytes on disk. 2. `lseek … whence=1 pos=0`: `whence=1` is `SEEK_CUR` (`SeekCur = 1` in `KernelMemoryCompatExports`), not `SEEK_END`, so `pos=0` is just the current offset. 3. `case ReadFileRecordType: break;` in `AmprExports.CompleteCommandBuffer` looks like a skipped read, but the bytes are copied at **submit** time in `AprCommandBufferReadFile` → `TryReadFileToGuestMemory`, so the record case is a no-op by design | — |
| M30 | **The bytes read from the pak are real asset data.** New `ampr.read_content` trace (first 64 bytes at each read destination, once per `(fileId,size)`): 1036 distinct fingerprints over 2294 reads. Samples: `{\r\n\t"FileVersion": 3,\r\n\t"EngineAssociation": "",…` (a UE4 `.uplugin` JSON), 262144 bytes of plugin JSON text, and archive blocks beginning `2000DA27 14000000 00020043…` | **I/O fully exonerated** | reads succeed, land in guest memory, and contain genuine UE4 content. So neither the file system, the pak discovery, the AMPR read path, nor the read destinations are the problem | `SHARPEMU_TRACE_AMPR_READ_CONTENT=1` (this session) |
| M31 | **Harness bug found while chasing this:** `run_ms_forcebisect.ps1` contained a **duplicated run block**, so every invocation ran the game **twice** (log overwritten by the second run, wall-clock doubled), and because the emulator runs the guest in a **"mitigated child process"**, killing the parent left the child alive — holding `artifacts\bin` (next `dotnet build` fails with MSB3021/MSB3027: *"file is locked by: SharpEmu"*) and running alongside the next arm. Logs from those runs are still valid (each log is one complete run), but builds failed while a child lingered | **FIXED** | single run block per script + `Get-Process SharpEmu \| Stop-Process -Force` after the timer in `run_ms_forcebisect.ps1` / `run_ms_io.ps1`; audit other harnesses the same way | this session |
| M32 | **30-minute verdict (2026-09-26, `ms_forcebisect_control_20260926_054535.txt`): SLOW LOAD, not stuck.** 115/115 presented frames byte-identical zero, BUT: pak reads never stop (46,466 reads / ~1.6 GB by minute 30, tail rate ~0.15/s after a ~16 min burst phase) and `FAsyncLoadingThread` keeps progressing (+410k imports in the final 6 min, `pthread_cond_timedwait` churn). No loop-guard kill (M33 fix holds), no crash, no wedge | **RESOLVED (root cause = emulation speed)** | the game would load on hardware in tens of seconds; at ~160 MB/min initial burst + heavy decompression on HLE-throttled threads it needs an hour+. Nothing new to fix *in the renderer* — this is now a performance problem, tracked in `docs/PERFORMANCE_PLAN.md` (Phase C: cond-wait storms + gpu-aware usleep throttle dominate the HLE profile) | measured, perf plan |
| M33 | **The "freeze at 01:19, FPS pinned 21.0" is the import-loop watchdog false-firing on a `gettimeofday` poll loop**: captured on a re-run with the fixed harness — `Import-loop guard fired at import#236041728: nid=n88vx3C5nW8 (gettimeofday) ret=0x800AF07A3`, `forced guest exit`. ≥1536 consecutive dispatches of the identical signature with no boundary call → 5 s timer → guest killed | **FIXED** | time-query imports (`gettimeofday`/`clock_gettime`/`sceKernelGetProcessTime*`/`GetTscFrequency`) are now *transparent* to the loop-guard signature window (`IsImportLoopGuardTransparent`) — a guest polling time is waiting for a deadline, not hung. Log loss also fixed the same session: the harness now streams (native redirection) instead of buffering until process exit | this session (`DirectExecutionBackend.Imports.cs`) |
| M12-old | (superseded) the pre-09-25 analysis attributed the FailFast to guest stubs entering via inline calli from managed frames on UE4 task-graph threads | **FIXED** | that mechanism was real (stale build stack) and was addressed by `0872285`; the *remaining* post-fix FailFast is M12 above (write tracker) | `0872285` |
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
- T6 (**settled by measurement**): the composite that fills the flip image
  (`ps=0x2005B40000`) samples descriptor slot `pc=0x50`, whose guest bytes
  encode a 1×1 R8G8B8A8 image with the dimension words literally zero, and the
  guest never re-binds that address with a real size (M19: `rebounds=0` in a
  300 s run). The composite therefore outputs zeros, the flip images stay
  all-zero (M15), and the window is handed an all-zero image. The remaining
  question has left the descriptor path: why does the game never stream the
  real texture (M20)?

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
| Q9 | **"GPU hanged" abort — the black screen + freeze + crash** (user log 2026-09-25: `sceMsgDialogOpen` with the `ERROR` border, `abort() called by guest`, `abort call-site ret=0x8004B789A`, `frame#0 ret=0x80053BE5D`, `frame#1 ret=0x80053BC68`) | **FIXED (pending in-game confirmation on Windows)** | Disassembled from `decrypted/eboot.bin`: frame#0 is the KEX error formatter (0x80053BDC0) whose string is `kexRHIStateGnm::StartFrame: GPU hanged while waiting for m_pContextLabel to be cleared`; frame#1 is `kexRHIStateGnm::StartFrame` (0x80053ABD0), which polls the previous frame's context label **500 times** (`mov ebx,0x1f4`) with `sceKernelUsleep(1)` between polls and then jumps to the error. On hardware usleep(1) is a real sleep and the GPU finishes well inside that budget. In SharpEmu the label is written later from the Vulkan ordered-action queue, while usleep(1) was one host yield, so all 500 polls finished in microseconds and the first slow frame (pipeline compile) counted as a hang. Fix: `GuestGpuProgress` counts label writes in flight (`SubmitOrderedGpuSideEffect`); the native usleep intrinsic takes the HLE path only while writes are pending, and `KernelUsleep` waits ≤8 ms for the GPU when one call site issues ≥4 short sleeps within 5 ms. Opt-out `SHARPEMU_DISABLE_GPU_AWARE_USLEEP=1` | `66db254` |
| Q10 | **Q9 fix engaged only once and the map-load frame still aborted** (user run 2026-09-25 23:13, `quake2_gpuhang_fix_*.txt`: `usleep.gpu_wait count=1 pending=10`, then "Starting Game" → pak0 loaded (14663 files) → `Quake2 Initialized` → map `base2` loading → 1 s later the same `GPU hanged` abort via 0x80053BC68) | **FIX (pending Windows re-test)** | The first fix counted only label writes already queued to the Vulkan ordered-action queue. On the map-load frame the end-of-frame `sceAgcCbReleaseMem` (called from the game's inline Gnm helper at 0x800143070 → PLT `sceAgcCbReleaseMem`; label set to 1 at 0x80053A4D3, cleared by `StartFrame` at 0x80053BF78, polled for non-zero at 0x80053AC10) sits inside a DCB that is still **queued or suspended behind a WAIT_REG_MEM**, so its write is not queued yet → counter 0 → native fast path → budget gone in microseconds. Also the per-call wait was only 8 ms, while first-run pipeline compiles take hundreds of ms per frame. Fix: `GuestGpuProgress.Busy` = queued label writes **+ unparsed submissions** (`PublishInFlightSubmissions`, recomputed under `gpuState.Gate` after every pump/resume/drain; ring-tail parks and queues suspended >2 s are excluded so a wedged queue can't slow every poll). `KernelUsleep` waits for GPU progress from the 4th short sleep of a burst (8 ms cap), and from the 32nd waits for the GPU to go idle (50 ms cap per poll → 500 polls ≈ 25 s of real time). Stall guard 20 s. Log line now prints `busy=/label_writes=/submissions=` | this commit |
| Q4 | **Empty-message fatal**: `Com_Error` receives the border string itself as the message; underlying error text is empty (`stderr.txt`: `Error - ` + border + nothing) | **ROOT-CAUSE → Q9** | **superseded by Q9**: the message is not unset — the formatter builds `GPU hanged while waiting for m_pContextLabel…` into a separate buffer, and only the border reached the dialog; call-site captured at `ret=eboot+0x4B789A`, frame chain `#0 ret=0x80053BE5D`, `#1 ret=0x80053BC68`, `#2 ret=0x8007D5D31` | — |
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

**Repro:** `run_quake2_diag.ps1` (60 s, LOG_IO + LOG_OPEN on). Q9 check: grep the log for `usleep.gpu_wait` (the fix engaging) and confirm no `GPU hanged` / `abort() called by guest`. A/B: `SHARPEMU_DISABLE_GPU_AWARE_USLEEP=1` should bring the abort back.

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

## PPSA21607 — The Smurfs – Dreams (v01.002.000)

**Status: BOOTS — black screen, game never reaches GPU submission
(2026-09-26).** Clean fast boot (window 1.3 s), all modules load (eboot +
libc + libSceFace + 4 more), `AgcCleanupThread` spawns, ~3.8M imports —
but the log contains **zero** draw/flip/present/`sceVideoOutOpen` lines.
The game sits in a `scePthreadCondTimedwait` timeout storm until the user
closes the window.

| # | Problem | Status | Root cause | Fix commit |
|---|---|---|---|---|
| S1 | **Black screen: no GPU work ever submitted.** 454× `ORBIS_GEN2_ERROR_TIMED_OUT` from `scePthreadCondTimedwait` (NID `BmMjYxmew1w`) — all on the **same cond var** `0x1F678F1E58`, all from one call site `ret=eboot+0x12184`, alternating 1 ms and multi-ms timeouts | **OPEN** | a worker thread polls a condition that is never signalled — its producer/task never runs or never completes. Same shape as Hellboy H6/H7 (a task graph starved of its dispatch). Need a thread-snapshot run (`SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1`) to see what the other threads are blocked on | — |
| S2 | 686× `sceKernelStat` returning -1 on stack-built paths (`rdi`=path on stack, `rcx`=path length 0x2C–0x67) | **OPEN (likely benign)** | missing/config files the game probes; same pattern as Quake II Q6. Confirm whether any probed path is one the game *requires* (save dir, config) | — |
| S3 | `sceKernelAllocateMainDirectMemory` (`B+vc2AO2Zrc`) returned TRY_AGAIN 18×, with the size request halving each time: 0x344000000 (13 GB) → 0x1A2000000 → … → 0x6880000 (103 MB) | **OPEN (likely benign)** | the game's direct-memory probe downward scan; emulation granted some size eventually (storm stops). Verify the granted size is enough for its allocator | — |
| S4 | 5 ELF "alignment mismatch" warnings on module segments (VAddr%Align≠Offset%Align) — eboot and 4 libraries all affected | **OPEN (watch)** | lots of titles log these without issue, but if a fixup/PLT region lands at the wrong delta the game will stall silently — keep in mind if S1's missing producer traces to a bad module load | — |
| S5 | **Black screen root cause: `__cxa_guard_release` returned INVALID_ARGUMENT right after RenderThread/RHIThread spawn** (Import#3605081, `9rAeANT2tyE`). A static-local guard stayed pending forever, so every later `__cxa_guard_acquire` on it spun in the HLE wait loop and the game wedged before its first draw (zero videoout/draw calls in the whole log) | **FIXED** | `CxaGuardExports` keyed guard ownership on `Environment.CurrentManagedThreadId`, but guest threads are resumed on different host threads by the native worker pool (M14) — acquire on host thread X, release on host thread Y → owner mismatch → INVALID_ARGUMENT → guard stuck. Ownership now keys on `GuestThreadExecution.CurrentGuestThreadHandle` (host id fallback). Verified: 0 INVALID_ARGUMENT in the re-run; the game reaches `presented first frame`, 93 flips / 14,733 render-work items / 17M imports in 120 s, and the presented image was **fully non-black** (`nonblack_pixels=2073600/2073600`) by the second readback | this session (`CxxAbiExports.cs`) |
| S6 | Unresolved imports (~188 calls each) from eboot `0x8032F98xx`–`0x8033020xx` (UE online subsystem): `sceNpWebApi2CheckTimeout` (`3Tt9zL3tkoc`) + four unresolvable NIDs (`7hd4bRJuLMg`, `Cm2cmtCv8cA`, `xaNK0ZTH3QA`, `NDMQ5Z8QdWQ`) | **OPEN (non-fatal)** | OnlineAsyncTaskThread polling; the game renders and loads with them unresolved. Not in the aerolib catalog; hashing likely `sceNp*`/`sceRemoteplay*`/`sceNpWebApi2*` names found no hit — extract names from the NpCppWebApi prx stubs if they ever block progress | — |

**Repro:** `run_smurfs_diag.ps1` (this session: thread snapshots + present readbacks, streaming log). Historical: `SharpEmu.log` from 2026-09-26 03:03.

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
| `run_mortal_shell_dbg.ps1` | Mortal Shell 300 s, GAME-DBG on, write-tracker explicitly **off**; takes `-TimerSeconds`, `-LogPrefix`, `-QuietGameDbg` |
| `run_ms_notrack.ps1` | Mortal Shell A/B arm with the tracker disabled (isolated M12) |
| `run_ms_framedump.ps1` | 150 s run that reads the swapchain back after the present blit and dumps every 100th frame (M15) |
| `run_ms_guestimg.ps1` | 200 s run that fingerprints every 1280×720+ guest render target (M17) |
| `bgra_to_png.ps1` | decodes `.bgra`/`.rgba` frame dumps to PNG and prints non-black / unique-pixel counts |
| `run_sarah_regress.ps1` | Dreaming Sarah 60 s regression guard (reference title) |
| `run_quake2_diag.ps1` | Quake II 60 s, LOG_IO + LOG_OPEN |
| `run_quake2_gpuhang.ps1` | Quake II Q9 check: runs 120 s and prints a verdict (`usleep.gpu_wait`, `GPU hanged`, guest `abort()`); `-DisableFix` is the A/B arm |
| `run_hellboy_test.ps1` | Hellboy detached, snapshots + GAME-DBG |
| `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS=1` | per-second per-thread state table |
| `SHARPEMU_LOG_SEMA=1` / `SHARPEMU_LOG_AUDIO_QUEUE=1` | semaphore + audio queue traces |
| `SHARPEMU_DISABLE_GAME_DBG=1` | silence the GAME-DBG layer |
| `SHARPEMU_TRACE_GUEST_IMAGES=present` | read the swapchain back after each presented frame → `vk.swapchain_image … nonblack_pixels=` |
| `SHARPEMU_TRACE_GUEST_IMAGES=every:N[@M]` | read every 1280×720+ guest render target back on every Nth draw into it → `vk.guest_image …`, `[RB] … mean=` |
| `SHARPEMU_TRACE_GUEST_IMAGES=alias` | dump aliased guest images once after the next present |
| `SHARPEMU_SWAPCHAIN_DUMP_EVERY=<n>` | with `SHARPEMU_GUEST_IMAGE_DUMP_DIR`, dump every Nth presented frame |
| `SHARPEMU_TRACE_UPLOAD_KNOWN=1` | report `vk.upload_known_skip addr=… fmt=… probe_bytes=` when a texture bind reuses an existing GPU image instead of copying guest texels (M21) |
| `SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD=1` | refuse the upload-known short-cut so guest texels are always copied (M21 experiment and fallback) |
| `SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS=0x…` | `agc.storage_initial_data … read= nonzero= initial_bytes= size= fmt= tile=` for one storage address |
| `SHARPEMU_DUMP_VIDEOOUT=1` | dump the guest display buffer on each flip |
| `SHARPEMU_DUMP_TEXTURES=1` | write draw textures to `texture-dumps/*.bmp` |
| `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1` | **opt-in** guest-image CPU write tracker (page guards). Off by default: it kills the process via M12 and does not remove the 1×1 placeholders |
| `SHARPEMU_DISABLE_GUEST_IMAGE_CPU_SYNC=1` | explicit kill switch for the above (wins over the opt-in) |
| `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS=1` | debug-only: restores the historical managed inline `calli` instead of the pooled native workers |
| `SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT=<n>` | native worker concurrency (default 16) |
| `SHARPEMU_DISABLE_GPU_AWARE_USLEEP=1` | turn off the Q9 fix (short sleeps in GPU poll loops wait for pending GPU label writes); `SHARPEMU_GPU_AWARE_USLEEP_MAX_MS` caps one wait (default 8) |

## Next-session priorities (expected-value order)

1. **M32 — decide "slow load" vs "stuck load" with a long run.** Launch
   `.\run_ms_forcebisect.ps1 -TimerSeconds 1800` (30 min) and watch
   `vk.swapchain_image … nonblack_pixels=`. Any readback that is **not**
   `0/2073600` / hash `0x97D30483E5DD6325` means the title got past its loading
   state and the "black screen" was just an extremely slow load under
   emulation (≈25 pak reads/s against an 8.69 GB pak). Still byte-identical
   after 30–60 min ⇒ the load is stuck *after* the proven-good reads.
2. **If it is stuck: instrument texture creation.** The read data is valid
   (M30) yet the guest's own descriptors are 1×1 (M27), so the target is the
   path that turns asset bytes into descriptors — log what writes the
   descriptor words (`raw=…,03800000,00000000,90100FAC,…`) and the HLE state
   the game derives dimensions from (`ampr`/`apr` get-size style queries, plus
   any stub that could answer 0).
3. **Keep the structural fixes** (same coherence bug class, needed as soon as
   content exists): F2 `storage_image_sync` (shadPS4 #4591), F3 readback
   ordering (shadPS4 #4542), F4 native page-guard write detection replacing the
   upload-known short-cut (M21).
4. **Tooling notes:** `SHARPEMU_LOG_OPEN=1` is safe; `SHARPEMU_LOG_IO=1` stalled
   once at import 256 inside `sceKernelReserveVirtualRange` and ran clean on a
   retry (treat a stall there as flaky, re-run before believing it);
   `SHARPEMU_LOG_AMPR=1` + `SHARPEMU_TRACE_AMPR_READ_CONTENT=1`
   (`run_ms_io.ps1 -AmprTrace`) is the read-path trace and is cheap for 90 s.
   **Always check for orphaned `SharpEmu` processes before building** (the
   mitigated child survives a parent kill, M31).
2. **M11 — verify the compute-fence autocomplete.** The run now performs 60+
   compute dispatches, so re-run with `SHARPEMU_LOG_AGC=1` and grep for
   `compute_fence_autocomplete`.
5. **Hellboy T9 / T6** — the process runs ~100 s with 0 AVs and then exits
   itself; stdout ends with Boehm "thread not found in gc_threads".
6. **Quake II Q4** — disassemble the `Com_Error` caller (`frame#0
   ret=0x80053BE5D`) to find the unset message global.
