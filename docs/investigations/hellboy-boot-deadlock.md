# Hellboy session addendum — 2026-09-06 (dirty-page zeroing + pthread object layout + VEH adapters)

## What changed since the 2026-09-05 addendum

The first-frame crash chain was root-caused to **uninitialized guest memory**
served with recycled (dirty) host pages, and to our zeroed ScePthread objects
missing kernel-populated sub-structures. Each fix below advanced the boot:

| Milestone | Evidence |
|---|---|
| Baseline (2026-09-05) | AV on `Loading.PreloadManager` right after `scePthreadSelf` (`mov [rsi],eax` to `0x41E4E00000002D4C`) |
| Flexible mappings zeroed | Same thread now crashes later, at the pthread self-cache (`mov r15,[r15+58h]` with r15=0) |
| Thread stacks + TLS zeroed at creation | Garbage cursor stores drop from 500+ to ~31 |
| ScePthread object fields (+0x58 → zeroed secondary with cancel flag, +0x68 → self-referential stats block, +0x00 → stats) | Wrapper at `libScePosix+0x22510..0x2255B` passes; boot reaches 1.43M+ imports |

### Fixes applied (2026-09-06)

1. **`sceKernelMapNamedFlexibleMemory` zeroes fresh mappings**
   (`KernelMemoryCompatExports.cs`). Flexible heap pages were served dirty;
   guest queue cursors and pthread self-cache slots read garbage
   (`0x41E4E…/0x8800…` patterns). Zeroing moved OUTSIDE `_memoryGate` — a
   large zeroing while holding the gate starved the main thread's concurrent
   `sceKernelMapDirectMemory` until the 20 s stall watchdog aborted boot.

2. **`MaxAutoZeroDirectBytes` lowered 256 MB → 64 MB.** Boot maps a 256 MB
   direct region (`rsi=0x10000000`); zeroing it through the tracked-memory
   path exceeded the import-progress watchdog. Critical Boehm/handshake
   structures live in small mappings, which stay zeroed.

3. **Guest thread stacks (2 MB) and TLS regions are zeroed at creation**
   (`DirectExecutionBackend.cs` `ZeroFreshGuestRegion`). Fresh host pages are
   recycled; the real kernel zero-fills new stacks/TLS. This removed most of
   the garbage-pointer stores.

4. **ScePthread object layout fields** (`KernelPthreadCompatExports.cs`
   `EnsureGuestThreadObjectAllocator`): every guest-visible pthread_t object
   now gets
   - `+0x58` → zeroed 0x200-byte secondary block whose `+0x11C` cancel-state
     word is 1 (terminates the TCB walk `mov r15,[x+58h]; cmp word [r15+11Ch],0; jne done`);
   - `+0x68` → self-referential 0x100-byte stats block (every qword points at
     itself, so chains like `[obj+0] → [x+38h] → [x+10h] → [x+rcx*8]` stay
     inside zeroed memory);
   - `+0x00` → the stats block (first pointer the scheduler walk dereferences);
   - the secondary block's `+0x68` also points at the stats block.
   Objects created before the allocator was installed are retrofitted
   (`SnapshotGuestAllocatedThreadHandles`); host-heap fallback handles are
   deliberately excluded from retrofit (writing guest addresses through them
   would corrupt memory).

5. **VEH adapters** (`DirectExecutionBackend.Exceptions.cs`), each
   instruction-signature gated and env-disableable:
   - `TryRecoverGuestBadStoreFault` — skips any guest instruction whose
     computed effective address (via Iced decode + CONTEXT registers) is
     non-canonical. Windows reports non-canonical stores as *read* AVs with
     target `0xFFFFFFFFFFFFFFFF`; both shapes are handled. Capped at 1M.
   - `TryRecoverGuestProducerCursorFault` — Hellboy's push helpers
     (`mov [rsi],eax` / `vmovss [rsi],xmm0` + `add qword [rdi],4; ret`) drop
     the item write, bump the cursor, and resume at the `ret`.
   - pthread-self substitution — `mov r15,[r15+58h]` with r15=0 and the
     cancel-state checks (`cmp word [r15+11Ch],0` / `cmp byte [r15+12Eh],0`)
     with a NULL/garbage r15 substitute the current guest thread handle and
     re-execute (capped at 100k; skipping reads instead caused an infinite
     retry loop).
   - `TryRecoverGuestNullAllocationFixup` — `mov [rax],r15; mov qword
     [rax+8],0` with rax=0 (NULL node allocation) is serviced through the new
     public `GuestAllocationBridge.RequestZeroed` and re-executed.

### Current Hellboy status (2026-09-06)

Boots further than any previous session: 1.43M+ imports, splash → first
presented frame, full worker pool, FMOD active. The remaining crash moves
around libScePosix pthread paths (`0x805B79F8B`, `0x805B81493`,
`0x805B9FC20`) and still traces back to guest-visible kernel structures our
HLE leaves zeroed where the real kernel fills them (per-thread sub-structures
dereferenced via `[x+0x30] → [x+0x38]`-style chains). The VEH adapters turn
most of these into recoverable events; the next root fix is filling the
remaining kernel-managed ScePthread fields (or lazily materializing them on
first dereference).

### Repro/test

`run_hellboy_test.ps1` (repo root) launches Hellboy **without a timer** as a
detached process with OS-level output redirection; stderr/stdout land in
`hellboy_live_<stamp>.{err,out}.log` and are complete once the game exits.
`analyze_logs.ps1` polls the PID from `hellboy_live.pid` and summarizes the
log (recoveries / substitutions / exception blocks).


## What changed this session (2026-09-05)

### Fix #1 — Guest-visible pthread_t values are now guest-memory objects

**Root cause (new):** `KernelPthreadState.AllocateThreadHandle` handed out
**host-heap pointers** (`Marshal.AllocHGlobal`) as the guest-visible
`pthread_t` (`scePthreadSelf`/`scePthreadCreate` return them verbatim).
Guest code that dereferences the handle through the *guest* address space
(real hardware / KytyPS5 semantics: `ScePthread` is a guest pointer to a
thread object) aliased unrelated guest data, producing a corrupted
destination pointer. The AV dump on Unity's `Loading.PreloadManager`
showed exactly this: `mov [rsi], eax` with `rsi=0x41E4E00000002D4C`
(pthread_t-derived pointer + 0x10) and the deterministic
`0x41E4E00000002D3C` garbage pattern on the guest stack.

**Fix** (`KernelPthreadState.cs`, `KernelPthreadCompatExports.cs`,
`KernelExports.cs`): thread handles are allocated from the guest address
space via `IGuestMemoryAllocator.TryAllocateGuestMemory` (zeroed), with a
host-heap fallback when no guest allocator exists yet. The allocator is
installed lazily on the first `scePthreadSelf`/`scePthreadCreate` call
(all guest threads share one address space, so one captured allocator is
safe).

**Result:** the game previously hung in an eternal `Baselib_SystemSemaphore`
1 ms-timeout spin after the PreloadManager AV; now the PreloadManager
survives long enough for the title to present its first frame before the
remaining crash (below).

### Fix #2 — Fresh direct mappings are zeroed (kernel semantics)

**Root cause:** `sceKernelMapDirectMemory` returned leftover host-page
bytes for newly reserved guest ranges. Real hardware (and KytyPS5) hands
out **zeroed** fresh pages; Boehm GC's stop-the-world handshake (Unity's
`SuspendSemaphore`/`ResumeSemaphore` acknowledge counters) and Baselib
worker structures assume this. Uninitialized structures then observed
stale bytes as pointers — the same deterministic garbage pattern.

**Fix** (`KernelMemoryCompatExports.cs`): `MapDirectMemoryCore` now calls
`TryZeroFreshDirectMapping` (chunked with the shared `_zeroChunk`, capped
at 256 MB per mapping via `MaxAutoZeroDirectBytes`).

## Remaining crash after the first presented frame

The `Loading.PreloadManager` thread still AVs at `eboot+0xC7A92A`
(`mov [rsi], eax`, `rsi=0x41E4E00000002D4C`) right after
`scePthreadSelf`, inside the Boehm stop-the-world callback (frame chain
runs through the Boehm suspension trampoline at `0x805C0F7D0`). The wild
pointer is deterministic across runs, so it is reproducible state, not a
race. Next steps:

1. Dump guest memory at the pthread_t object created by the new
   guest-object path and verify Boehm's thread-record lookup
   (`FindGuestExceptionThreadRecord` at `callback+0x102E8B0`) matches it.
2. Instrument `TryWriteGuestExceptionContext` to verify the
   `exceptionStackBase` region for the PreloadManager thread is the one
   the Boehm callback computes its write target from.
3. Compare `recorded_rsp` (`record+0x18`) against the live RSP at
   delivery; a mismatch means the exception context's stack slot is not
   what the guest callback expects.

## Files changed this session (2026-09-05)

| File | Change |
|---|---|
| `KernelPthreadState.cs` | `GuestThreadObjectAllocator` hook; guest-memory thread objects (zeroed); host fallback |
| `KernelPthreadCompatExports.cs` | `EnsureGuestThreadObjectAllocator` + `AllocateZeroedGuestObject`; called from `scePthreadSelf` |
| `KernelExports.cs` | `scePthreadCreate` installs the allocator before creating the first handle |
| `KernelMemoryCompatExports.cs` | `TryZeroFreshDirectMapping` zeroes fresh direct mappings (`MaxAutoZeroDirectBytes` = 256 MB) |
| `VulkanVideoPresenter.cs` | (Mortal Shell) flip-time variant promotion before blank on-demand display-buffer image — see mortal-shell doc |

---


# Hellboy: Web of Wyrd — Boot Deadlock Investigation (RESOLVED) + Mortal Shell Black Screen Status

**TID**: PPSA11264 (Hellboy) | PPSA02868 (Mortal Shell) | **Date**: 2026-09-04/05

## Executive Summary

Two long-running issues were root-caused this session by disassembling the
encrypted Il2CppUserAssemblies.prx at runtime (guest-memory dumps +
capstone) and comparing against KytyPS5's kernel implementation:

1. **Hellboy boot deadlock — RESOLVED.** A 32-bit semaphore-handle write
   into a 64-bit guest slot left the upper half as the stack canary
   (`0xC0DEC0DE`), so Unity Baselib worker threads waited forever on
   garbage handles (`0xC0DEC0DE000000XX`) that silently aliased unrelated
   live semaphores. With the handle-width fix the game boots, initializes
   FMOD audio, spawns its full worker pool, and processes 213M+ imports.

2. **Mortal Shell black screen — cause narrowed to the shader/texture
   path.** The game runs at a steady 35-40 FPS with 213M imports and
   160+ draws/frame, but every draw to the display buffer samples a 1x1
   black fallback texture (`agc.texture_binding ... decoded=addr=... 1x1
   fmt=10`), so the output stays zero. The display buffers themselves are
   now correctly allocated (valid 64-bit handles, correct layout), which
   rules out the earlier layout-transition theory.

---

## Hellboy — the deadlock chain (root cause analysis)

### Evidence timeline (from `SHARPEMU_LOG_SEMA=1` + `SHARPEMU_LOG_PTHREAD_CONDS=1`)

```
sema.create handle=0x64 name='SuspendSemaphore'          # main creates suspend/resume pair
sema.create handle=0x65 name='ResumeSemaphore'
pthread_create 'Thread-...' entry=0x805B53440             # Boehm GC coordinator
pthread_cond_wait-enter cond=0x100000BB0 mutex=0x100000BA8 # main parks waiting for GC-ready
sema.wait-block handle=0x64 'SuspendSemaphore'            # GC thread parks waiting for suspend
<nothing ever signals either primitive>                   # total deadlock
```

### Root cause #1 — 32-bit semaphore handles (fixed)

`KernelCreateSema`/`sem_init` wrote the handle with `TryWriteUInt32`
while the guest's `SceKernelSema`/`sem_t` slot is pointer-sized. The
upper 4 bytes kept whatever was on the stack — the loader's stack-canary
fragment `0xC0DEC0DE`. Unity workers then re-read the full 8 bytes and
passed `0xC0DEC0DE00000043`-style handles to `sceKernelWaitSema`, where
the old `(uint)rdi` truncation mapped them onto *real, unrelated*
Baselib worker semaphores. Workers blocked forever; the GC coordinator's
ack counters never reached their thresholds; main parked in
`pthread_cond_wait` and nothing signaled it.

**Fix** (`KernelSemaphoreCompatExports.cs`): handles are now 64-bit
guest-memory objects (KytyPS5 stores `new KernelSemaPrivate*` the same
way). All kernel-level entry points resolve the full 64-bit handle, with
one dereference fallback for runtimes that pass the slot address, and
`uint`-typed SysAbi parameters changed to `ulong` so the dispatcher
binding no longer truncates.

### Root cause #2 — dead-code exception delivery (fixed)

Boehm's PS5 stop-the-world suspends threads with
`sceKernelRaiseException(thread, 30)` (SIGUSR1). KytyPS5 queues the
signal, wakes the target out of its cond wait
(`PthreadWakeForSignal`), and the target's 10 ms polling loops dispatch
the pending signal at safe points.

SharpEmu had the queue (`TryRaiseGuestException`, external mode →
`_pendingGuestExceptions`) but **`DeliverPendingGuestExceptionAtSafePoint`
was never called** — verified against the introducing commit 864cbb0.
Queued SIGUSR1 suspensions sat undelivered forever, which is why the
cond handshake above never completed even after the handle fix.

**Fixes**:
- `DirectExecutionBackend.Imports.cs`: `DispatchImport` now consumes
  pending guest exceptions at the import boundary (the natural safe point
  where the thread is paused inside managed HLE code).
- `KernelPthreadCompatExports.cs`: `ForceSpuriousWakeForThread` completes
  the raised thread's cond waiters as spurious wakes (and force-sets host
  mutex waiter events) so a parked thread actually reaches an import
  boundary. Mirrors KytyPS5's `PthreadWakeForSignal`.
- `KernelExceptionCompatExports.cs`: `sceKernelRaiseException` calls the
  force-wake after queueing.

### Supporting fixes (KytyPS5 parity)

- `PadExports.cs`: plain `scePadOpen` now accepts special port type=2
  (KytyPS5 `PadOpenArgsAreValid`) — Hellboy opens type 2.
- `KernelMemoryCompatExports.cs`: `sceKernelDirectMemoryQuery` flags=1
  walk past the last block returns the OK terminal (KytyPS5 semantics)
  instead of DELETED; `sceKernelVirtualQuery` next-region walk can answer
  from a reserved-span tail registry so uncommitted flexible-heap probes
  succeed like KytyPS5's pre-registered ranges.
- `KernelRuntimeCompatExports.cs`: the first
  `sceKernelReserveVirtualRange` announces the flexible-heap span.

### Current Hellboy status

Boots through all 8 module initializers, initializes FMOD, runs its
worker pool, 1.36M+ imports in 3 minutes. Remaining issue: an Access
Violation on the `Loading.PreloadManager` thread after `scePthreadSelf`
(eboot+0xC7A92A, `mov [rsi], eax` with a corrupted destination), which
does not kill the process. The write's destination `0x41E4E00000002D4C`
pattern suggests a GP/XMM register interleave bug in either the
exception-delivery restore path or an unrelated long-standing emulation
gap. GPU work has not started yet (draws=0) — preload must complete
first.

---

## Mortal Shell — black screen status

With the kernel fixes the game runs at **35-40 FPS steady state**,
213M imports, 160 draws/frame, FMOD active. The screen is still black.
New evidence:

- `vk.guest_write_sample ... readback=1` on the display buffers
  (3840x2160 R8G8B8A8Unorm) shows `nonzero_bytes=0/33177600` directly
  after composite draws that executed successfully.
- Those draws sample a **1x1 fallback texture**
  (`textures=[...:1x1:f10:n0]`), i.e. the real UI/scene texture binding
  failed to decode and the draw produced (transparent) nothing.
- `SHARPEMU_TRACE_GUEST_IMAGES=present` confirms presented images are
  entirely zero.
- Earlier forced-state experiments (solid fragment, fullscreen vertex,
  default viewport, no cull) still produced zero output, which proves
  rasterization itself never lands for these MRT draws — the pipeline
  is likely never bound due to an invalid render pass/pipeline
  combination, or the draws write into an image object that is not the
  one being captured (variant/recreate split).

### Next steps for the black screen

1. Verify which `GuestImageResource` object the composite draws write
   into versus the one the flip captures (`_guestImageVariants` split
   at the same address is the prime suspect).
2. Instrument `ExecuteOffscreenDrawCore` to dump the resolved
   framebuffer's image handle and compare with `_guestImages[addr]`.
3. Investigate the 1x1 fallback texture binding — decode failure for
   the real texture descriptor is what makes the composite sample
   black even if the framebuffer were correct.

---

## Files changed this session

| File | Change |
|---|---|
| `KernelSemaphoreCompatExports.cs` | 64-bit guest-object semaphore handles; full-width resolution; slot-address dereference fallback |
| `PadExports.cs` | `scePadOpen` accepts special port type 2 |
| `KernelMemoryCompatExports.cs` | DirectMemoryQuery terminal; VirtualQuery reserved-span tail; `_reservedVirtualSpans` registry |
| `KernelRuntimeCompatExports.cs` | Flexible-heap span announcement on first reservation |
| `KernelExceptionCompatExports.cs` | RaiseException force-wakes target waiters |
| `KernelPthreadCompatExports.cs` | `ForceSpuriousWakeForThread`, `ForceWakeHostMutexWaitersForThread` |
| `DirectExecutionBackend.cs` | Import-boundary pending-exception consumer (`TryDeliverPendingGuestExceptionAtImportBoundary`), stall guest-memory dump helper |
| `DirectExecutionBackend.Imports.cs` | Safe-point consumption call at `DispatchImport` entry |
| `VulkanVideoPresenter.cs` | On-demand display-buffer image gets an explicit Undefined→ShaderReadOnly transition (correctness; not the black-screen cause) |
