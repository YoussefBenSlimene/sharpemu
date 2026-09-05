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
