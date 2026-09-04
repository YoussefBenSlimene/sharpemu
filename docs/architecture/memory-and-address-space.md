<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Memory & Address Space

SharpEmu's guest memory is **identity-mapped**: a guest virtual address is
*the same number* as the host virtual address that backs it. This is mandatory
because guest x86-64 code runs natively on the host CPU (see
[CPU execution](cpu-execution.md)) and reads `fs:`/`gs:` segment state
directly. There is no managed interpreter and no recompiler — the host CPU
loads and stores into real pages at the exact addresses the guest expects.

This page covers the address-space layout, the allocation APIs, page
protection, and the per-OS backing.

## Key address ranges

These constants live in `src/SharpEmu.Core/Loader/SelfLoader.cs` and
`src/SharpEmu.Core/Memory/PhysicalVirtualMemory.cs`.

| Region | Address | Notes |
| --- | --- | --- |
| PS5 main image base | `0x0000000800000000` | Where `eboot.bin` is mapped. |
| PS4 main image base | `0x00000000_00400000` | Used for PS4 titles. |
| PS5 module search range | `0x804000000`–`0x900000000` | Where additional `.prx` modules are placed. |
| PS4 module search range | `0x02000000`–`0x40000000` | PS4 modules. |
| Module placement step | `0x200000` (2 MiB) | Step when scanning for a free slot. |
| Guest allocator arena | `0x600000000000` (+512 MiB) | The HLE heap; grows from +4 KiB. Raised from 16 MiB for C++ runtimes that route HLE-backed allocations through it. |
| Import stub base | `0x700000000000` | One stub slot per import NID; stride `0x10000`, slot size `0x10`. |
| Process stack (Win) | `0x7FFFF0000000` | 2 MiB stack. |
| Process stack (POSIX) | `0x6FFFF0000000` | POSIX uses a lower region to avoid the host's own high addresses. |
| TLS block (Win) | `0x7FFE00000000` | |
| TLS block (POSIX) | `0x6FFE00000000` | |
| Bootstrap stub (Win) | `0x7FFDF0000000` | |
| Dynlib fallback stub (Win) | `0x7FFDE0000000` | `xor eax,eax; ret` |
| Return-to-host stub (Win) | `0x7FFDD0000000` | `hlt; int3` |
| ... (POSIX equivalents) | `0x6FFD...` | Same offsets, lower high region. |

> The Windows-vs-POSIX split exists because Windows reserves a lot of the high
> address space for itself (TEB, PEB, stack), and macOS is even more
> constrained. POSIX builds shift the guest's high regions down to `0x6FF...`.

## The two memory implementations

Both implement `IVirtualMemory` (`src/SharpEmu.Core/Memory/IVirtualMemory.cs`),
which extends `SharpEmu.HLE.ICpuMemory`.

### `PhysicalVirtualMemory` (production)
`src/SharpEmu.Core/Memory/PhysicalVirtualMemory.cs` — `sealed unsafe class`,
~2000 lines. This is the backend used by `SharpEmuRuntime.CreateDefault`. It:

- Delegates host page operations to `IHostMemory` (from `SharpEmu.HLE.Host`),
  whose default `CrossPlatformHostMemory` calls into
  `SharpEmu.HLE.Host.HostMemory` (VirtualAlloc/VirtualProtect/VirtualQuery/
  FlushInstructionCache on Windows; mmap/mprotect/munmap + a shadow region
  table on POSIX).
- Maintains a **guest allocator arena** at `0x600000000000` (512 MiB) with a
  free-list allocator (`TryAllocateGuestMemory`/`TryFreeGuestMemory`). This is
  the HLE heap.
- Bookkeeps page protection in a `ConcurrentDictionary<ulong, ProgramHeaderFlags>`
  (`_pageProtections`), merged across overlapping segments.
  `ApplySegmentProtection` coalesces runs and flushes the instruction cache
  for executable pages.
- Supports **fixed-address allocation**: `TryAllocateAtExact` (used by the
  loader for the main image base) and `TryBackFixedRange` (page-by-page
  backfill when the exact range can't be reserved atomically). On Windows it
  does "granule-aware" backing via `TryAllocateFixedThroughGranules` to handle
  the host's 64 KiB allocation granularity.
- **Lazy commit**: large non-executable reserves (≥1 GiB, above the 4 GiB
  full-commit limit) may be reserved-only and primed with a leading 64 MiB
  chunk (configurable via `SHARPEMU_LAZY_RESERVE_PRIME_MB`). On a page fault,
  `EnsureRangeCommitted` demand-commits a window. This is what lets titles
  with very large virtual reservations (e.g. "Poppy") work without committing
  everything up front.
- **Protection-changing writes**: `TryWrite`/`TryCopy` call
  `GuestImageWriteTracker.NotifyManagedWrite` *first* — the GPU backend
  write-protects cached surfaces, and a managed write into a protected page
  would AV before the signal bridge could restore access.
- **Identity pointer**: `GetPointer(virtualAddress)` returns
  `(void*)virtualAddress` directly (after ensuring the range is committed for
  reserve-only regions). This is why guest code runs natively: the host can
  just `call` guest addresses.

### `VirtualMemory` (tests)
`src/SharpEmu.Core/Memory/VirtualMemory.cs` — a pure-managed backing store
(`byte[]` per region, sorted by address, overlap-checked,
protection-enforced on read/write). Used by tests that don't need real
memory-mapped pages.

## Allocation API

`IGuestMemoryAllocator` / `IGuestAddressSpace` (interfaces in `SharpEmu.HLE`,
implemented by `PhysicalVirtualMemory`) are what HLE exports use for
`mmap`/`mprotect`-style behavior:

- `AllocateAt(size, alignment)` — allocate anywhere in the arena.
- `TryAllocateAtExact(address, size)` — the loader's path for the image base.
- `TryBackFixedRange(address, size)` — page-by-page backfill.
- `TryAllocateAtOrAbove(address, size)` — hint-based allocation.
- `TryProtect(address, size, GuestPageProtection)` — change protection.

HLE exports reach these through `ctx.Memory` (an `ICpuMemory`) cast to
`IGuestAddressSpace` rather than calling host APIs directly, because guest
addresses are identity-mapped onto host pages.

## Page protection

Two enums exist:

- `SharpEmu.HLE.GuestPageProtection` (`[Flags] None/Read/Write/Execute`) — the
  HLE-facing enum used by `IGuestAddressSpace.TryProtect`.
- `SharpEmu.HLE.Host.HostPageProtection` (`NoAccess/ReadOnly/ReadWrite/...`)
  — the host-facing enum used by `IHostMemory`.

`PhysicalVirtualMemory` translates between them. `CanReadWithoutProtectionChange` /
`CanWriteWithoutProtectionChange` decide whether the read lock suffices or an
upgrade to the write lock (with temporary `PAGE_READWRITE` /
`PAGE_EXECUTE_READWRITE`) is needed.

## Per-OS backing

The host page operations are abstracted behind `IHostMemory`
(`src/SharpEmu.HLE/Host/IHostMemory.cs:12`):

- **Windows** — `WindowsHostMemory` (`Host/Windows/WindowsHostMemory.cs:12`)
  over `VirtualAlloc`/`VirtualFree`/`VirtualProtect`/`VirtualQuery` +
  `FlushInstructionCache`. Maps `HostPageProtection` ↔ `PAGE_*`.
- **POSIX** — `PosixHostMemory` (`Host/Posix/PosixHostMemory.cs:16`) over
  `mmap`/`mprotect`/`munmap` with a shadow `SortedList<ulong, Region>` table
  that answers VirtualQuery-style questions. Notable quirks:
  - Reserve-only mappings use `MAP_NORESERVE` on Linux and are demand-paged.
  - Linux uses `MAP_FIXED_NOREPLACE` so exact-placement requests fail cleanly
    instead of clobbering host memory.
  - **macOS lacks `MAP_FIXED_NOREPLACE`**; plain `MAP_FIXED` would clobber
    untracked host memory (dyld, CLR JIT heap, Rosetta). Darwin therefore uses
    a hint-only `mmap` and falls back to `mach_vm_allocate` with
    `VM_FLAGS_FIXED` (no overwrite).
  - Instruction-cache flushing is a no-op on POSIX today (all supported POSIX
    hosts are x86-64, including Rosetta 2, with coherent I-caches).

## The memory a guest thread sees

When `CpuDispatcher` sets up a guest frame it gives the thread:

- A **stack** near the stack base.
- A **TLS block** with the FreeBSD-amd64 Variant-II TCB layout (see
  `GuestTlsTemplate` in `src/SharpEmu.HLE/GuestTlsTemplate.cs`). The thread
  pointer is at the block base; `fs:[0]` reads it. Startup static TLS modules
  live below the thread pointer; later-loaded modules get dynamic DTV entries.
- `FsBase = GsBase = tlsBase`.

Guest code reads its TLS base via `mov reg, fs:[0]` / `mov reg, gs:[0]`.
Because the guest FS/GS base must be a real host TLS slot, SharpEmu **scans
executable guest memory and rewrites those loads** into calls to a TLS handler
stub (`PatchTlsPatterns` in the CPU backend). See
[CPU execution](cpu-execution.md#tls-load-patching).

## Diagnostic tools

- `GuestWriteWatch` (`src/SharpEmu.HLE/GuestWriteWatch.cs`) — optional write
  watcher, active only with `SHARPEMU_WATCH_*` env vars. See
  [`guest-write-watch.md`](../guest-write-watch.md).
- `GuestImageWriteTracker` (`src/SharpEmu.HLE/GuestImageWriteTracker.cs`) —
  detects guest CPU writes into memory that backs a host GPU image. Enabled by
  `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`. Signal-handler-safe (no allocation, no
  locks in the fault path).

## Next

- [SysABI / HLE export system](hle-and-sysabi.md) — how the imports the
  loader patches get handled.
- [CPU execution & fault handling](cpu-execution.md) — the lazy-commit and
  write-watch fault recovery in the VEH/signal handler.
