<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# The SysABI / HLE Export System

This is the most important page for anyone adding or changing PS5 system
library behavior. It explains how a guest NID becomes a managed C# call, and
how the generated registry, the runtime, and the CPU backend cooperate.

## What is a NID?

Sony identifies every OS export with an 11-character **NID** (Native ID). For
example, `sceKernelWaitSema` is `Zxa0VhQVTsk`. NIDs are derived from the symbol
name with a fixed algorithm (SHA-1 over `name || suffix`, first 8 bytes
byte-reversed, Base64-stripped). The C# port is
`src/SharpEmu.SourceGenerators/Ps5Nid.cs:15` (`Ps5Nid.Compute`), and the
matching aerolib catalog of known NIDs is queried by
[`scripts/aerolib_catalog.py`](../aerolib-catalog.md).

A "SysABI export" is a managed method that implements one of these NIDs. The
"ABI" is the System V x86-64 calling convention the guest uses: integer
arguments in `Rdi, Rsi, Rdx, Rcx, R8, R9`, return in `Rax`.

## Authoring an export

You write a `static`, non-generic method that returns `int` in a type that is
at least `internal`, and annotate it:

```csharp
using SharpEmu.HLE;

public static class MyExports
{
    [SysAbiExport(LibraryName = "libMine", Nid = "ABC12345xyz", ExportName = "sceMyFunction")]
    public static int MyFunction(CpuContext ctx, int arg0, uint arg1)
    {
        // ...
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
```

`SysAbiExportAttribute` (`src/SharpEmu.HLE/SysAbiExportAttribute.cs:7`)
properties:

| Property | Default | Purpose |
| --- | --- | --- |
| `LibraryName` | `"libKernel"` | The logical guest module this export belongs to. |
| `Nid` | `""` | The PS5 NID. If omitted, derived from `ExportName` via `Ps5Nid.Compute`. |
| `ExportName` | `""` | Public symbol name; falls back to the method name. |
| `Target` | `Generation.None` | Which console generations (`None` ⇒ inherit the registration generation). |
| `PreferLle` | `false` | Use this handler only as a fallback when no guest (LLE) provider is available. |

### Handler shapes

The generator (`SysAbiExportShape.cs:43`) accepts three shapes:

- `int M(CpuContext ctx)` — raw-register access. Use this when you need
  unusual registers or XMM state.
- `int M()` — no guest state needed.
- `int M(CpuContext ctx, ...)` — up to 6 typed integer arguments (`int`/`uint`/
  `long`/`ulong`), mapped positionally to `Rdi, Rsi, Rdx, Rcx, R8, R9`. The
  generator emits a thunk that reads `ctx[CpuRegister.<Reg>]` and
  `unchecked`-casts.

A `string` parameter is only valid with `[GuestCString(maxLength)]`
(`src/SharpEmu.HLE/GuestCStringAttribute.cs`): the thunk calls
`ctx.TryReadNullTerminatedUtf8(...)` and returns
`ORBIS_GEN2_ERROR_MEMORY_FAULT` on failure *before* the handler runs. The
contract matches `CpuContext.TryReadNullTerminatedUtf8`, which bulk-reads in
128-byte chunks so a terminator before unmapped memory still yields the string.

Floating-point/`XMM` arguments are **not** auto-unmarshalled by the typed
thunk — handlers that need them read
`ctx.GetXmmRegister(i, out low, out high)` directly.

### Returning a value

- `return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)` — writes the code to
  `RAX` and returns it.
- `return (int)OrbisGen2Result.X` — the dispatcher writes it to `RAX` itself
  *if* you did not touch `ctx[CpuRegister.Rax]`.
- `ctx[CpuRegister.Rax] = someValue; return 0;` — for returning a 64-bit value
  or a pointer. The dispatcher sees `WasRaxWritten` (`CpuContext.cs:57`) and
  leaves `RAX` alone.

### Accessing guest memory

`ctx.Memory` is an `ICpuMemory`. Use the typed helpers
(`ctx.TryReadUInt64(addr, out var x)` / `ctx.TryWriteInt32(addr, v)`) for
safety, or `ctx.Memory.TryRead/TryWrite(VA, span)` for raw spans. For
`mmap`/`mprotect`-style ops, cast `ctx.Memory` to `IGuestAddressSpace`
(`AllocateAt`, `TryBackFixedRange`, `TryAllocateAtOrAbove`, `TryProtect`) —
guest addresses are identity-mapped onto host pages.

`ICpuMemoryWrapper.Inner` (`src/SharpEmu.HLE/ICpuMemoryWrapper.cs:13`)
unwraps decorators (e.g. access trackers) to the real implementation without
reflection.

## The compile-time pipeline

`SysAbiExportShape` (`src/SharpEmu.SourceGenerators/SysAbiExportShape.cs:13`)
is the **single source of truth** shared by the generator and the analyzer so
they cannot disagree. It defines the attribute name, the
`ArgumentRegisters = ["Rdi","Rsi","Rdx","Rcx","R8","R9"]` order, the
`HandlerShape` enum, and `Classify` (which enforces static, non-generic,
returns `int`, accessible — handler types must be at least `internal`).

### The generator

`SysAbiExportGenerator.cs:24` is an `IIncrementalGenerator`. For each assembly
that contains at least one `[SysAbiExport]`, it emits one
`SharpEmu.Generated.SysAbiExportRegistry` with a `CreateExports(Generation
registrationGeneration)` method returning `IReadOnlyList<ExportedFunction>`.

The generated `TypedThunk` (`SysAbiExportGenerator.cs:253`) is what reads guest
registers into typed parameters: for parameter `i` it emits
`ctx[global::SharpEmu.HLE.CpuRegister.{ArgumentRegisters[i]}]`,
`unchecked`-casts for non-`ulong` kinds, and for `[GuestCString]` parameters
emits the pre-call `ctx.TryReadNullTerminatedUtf8(...)` that, on failure,
returns `ctx.SetReturn(ORBIS_GEN2_ERROR_MEMORY_FAULT)`.

The generation-filtering rule: an attribute `Target` of `Generation.None`
inherits the registration generation; otherwise the attribute's target is used
and filtered against the registration generation
(`SysAbiExportGenerator.cs:233-237`). This matches the dispatcher's rule.

### The analyzer

`SysAbiExportAnalyzer.cs` reports `SysAbiDiagnostics` (`SysAbiDiagnostics.cs`)
for invalid declarations (non-static, generic, wrong return type, private,
unknown shape, etc.). Invalid declarations are skipped by the generator *and*
reported as build errors, so nothing is silently dropped.

### NID validation

`scripts/ps5_names.txt` is added as `AdditionalFiles` in
`SharpEmu.Libs.csproj`; the analyzer flags export names it doesn't recognize.

## The runtime pipeline

### Registration

`SharpEmuRuntime.CreateDefault` (`src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs:76`):

1. Create a `ModuleManager`.
2. Call `SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5)`
   — the source-generated registry.
3. `ModuleManager.RegisterExports(...)` indexes them by NID
   (`ModuleManager.cs:19`). Duplicates are logged and skipped. Each
   contributing assembly is recorded in `_warmupAssemblies`.
4. `ModuleManager.Freeze()` — flips `_isFrozen` (any later `RegisterExports`
   throws) and runs `WarmHleTypeInitializers`.

> **Do not skip `Freeze`.** `WarmHleTypeInitializers` (`ModuleManager.cs:63`)
> runs every HLE type's `.cctor` (`RuntimeHelpers.RunClassConstructor`) and
> force-JITs every method (`RuntimeHelpers.PrepareMethod`) on a *host* thread.
> A `.cctor` or first JIT firing on a guest thread's hijacked stack would
> fail-fast the CLR. It also warms framework (`System*`/`netstandard`) type
> initializers (but not their methods — the BCL is too large) and pulls in
> guest-reachable interop assemblies (Silk.NET, SharpEmu.*) via
> `WithGuestReachableDependencies`.

### Dispatch

`ModuleManager.TryDispatch` (`ModuleManager.cs:271`):

1. Look up the NID in the dispatch and export tables; missing ⇒ log, write
   `ORBIS_GEN2_ERROR_NOT_FOUND` to `RAX`, return false.
2. Check `(export.Target & context.TargetGeneration) != 0`; mismatch ⇒
   `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED`. A handler targeting `Gen4` called on a
   `Gen5` context returns `NOT_IMPLEMENTED` — so multi-generation handlers
   must set `Target = Generation.Gen4 | Generation.Gen5`.
3. `context.ClearRaxWriteFlag()` — so a handler can signal "I already wrote
   RAX" by assigning `ctx[CpuRegister.Rax]`.
4. Invoke the `SysAbiFunction`; if the handler did not write RAX, the
   dispatcher writes the `int` return itself.

### How a guest call reaches dispatch

This is the CPU backend's job (full detail in
[CPU execution](cpu-execution.md)). When the loader resolved imports, it
installed an `int3` trap stub at each import address
(`0x0000700000000000` + per-NID slot). At runtime, `DirectExecutionBackend.SetupImportStubs`
(`Cpu/Native/DirectExecutionBackend.cs:1251`) resolves each NID into one of
three targets:

1. **LLE direct bridge** — if a guest runtime symbol exists for the NID (or
   its export name, with `_` prefix variants) in executable memory, patch the
   import stub in place to `movabs r11, <target>; jmp r11`. The guest calls
   straight into the guest-native implementation. Preferred for `libc`
   `memcpy`/`memset`/`memmove`/`memcmp` (gated by `SHARPEMU_LLE_LIBC_*`) and
   any `ExportedFunction` marked `PreferLle`. **Kernel libraries are always HLE.**
2. **Native intrinsic** — `TryCreateNativeImportIntrinsic` hand-emits x86 for
   hot leaf functions (`rdtsc`, `QueryPerformanceCounter`, `usleep`,
   `strlen`, `memcmp`, ...), patching the import stub to jump to the emitted
   code.
3. **Managed HLE gateway** — a per-import trampoline saves all volatile guest
   state (RAX/AL, R10/R11, MXCSR, FPU control, XMM0–7), reads the host RSP from
   a TLS slot, and calls `ImportDispatchGatewayManaged` (a reverse-P/Invoke).
   The gateway loads the guest GPRs into `CpuContext`, dispatches (bootstrap
   bridge / `kernel_dynlib_dlsym` / il2cpp / the registered HLE export),
   stores the vector return (XMM0/1) back into the arg pack, and returns.

On POSIX the managed callbacks are SysV-compiled, so `ResolveWin64CallbackPtr`
wraps them in a `CreateWin64ToSysVThunk` (saves rdi/rsi, shuffles
rcx/rdx/r8/r9 → rdi/rsi/rdx/rcx). The emitted call sites themselves always use
the Win64 ABI.

## The aerolib catalog

`src/SharpEmu.HLE/Aerolib/Aerolib.cs` is the runtime NID → name catalog (a
lazy singleton implementing `ISymbolCatalog`). It loads an embedded
`aerolib.bin` resource: `uint32` entry count, then per entry `byte` NID length
+ UTF-8 NID, `ushort` name length + UTF-8 name. The catalog answers
`TryGetName`, `ContainsNid`, `GetAllNidNames`, and powers the diagnostics that
say "import NID `...` resolved to `scePthreadMutexLock`".

`aerolib.bin` is **derived data, never committed**. It is generated at build
time by the `GenerateAerolibBinary` MSBuild target in `SharpEmu.HLE.csproj`,
which runs `SharpEmu.SourceGenerators.GenerateAerolibBinaryTask` (an `ITask`,
not an analyzer, so RS1035's file-IO ban doesn't apply). The task reads
`scripts/ps5_names.txt`, computes each name's NID via `Ps5Nid.Compute`, and
writes the binary in the exact format `Aerolib.LoadFromEmbeddedBinary` reads.
The `EmbedAerolibBinary` target then adds it as an `EmbeddedResource` named
`SharpEmu.HLE.Aerolib.aerolib.bin`.

For querying the catalog from the command line, see
[`aerolib-catalog.md`](../aerolib-catalog.md).

## Result codes

`OrbisGen2Result` (`src/SharpEmu.HLE/OrbisGen2Result.cs`) is the synthetic
kernel result-code enum, prefixed `ORBIS_GEN2_` (to distinguish from PS4
`ORBIS_*` codes used by other emulators): `OK`, `PERMISSION_DENIED`,
`NOT_FOUND`, `INVALID_ARGUMENT`, `ALREADY_EXISTS`, `DEADLOCK`, `DELETED`,
`BUSY`, `TRY_AGAIN`, `CANCELED`, `NOT_IMPLEMENTED`, `TIMED_OUT`,
`MEMORY_FAULT`, `CPU_TRAP`.

## Adding a new export — checklist

1. Pick the right library folder in `src/SharpEmu.Libs/`. If the library is new,
   create a subfolder; otherwise add to the existing one.
2. Write a `static` method returning `int`, annotated with `[SysAbiExport]`.
   Look up the NID via `python scripts/aerolib_catalog.py lookup <name>` (or
   omit `Nid` and let the generator derive it from `ExportName`).
3. Choose the handler shape (`CpuContext` only, parameterless, or typed
   args). Use `[GuestCString]` for string parameters.
4. Return via `ctx.SetReturn(...)` or write `ctx[CpuRegister.Rax]` directly.
5. Set `Target = Generation.Gen4 | Generation.Gen5` if the handler is not
   PS5-only.
6. Build — the analyzer will tell you if the shape is invalid or the NID is
   unknown.
7. Write a test under `tests/SharpEmu.Libs.Tests/` mirroring the existing
   subsystem tests.

## Next

- [CPU execution & fault handling](cpu-execution.md) — the gateway and the
  import trampolines in detail.
- [SharpEmu.Libs guide](../projects/libs.md) — the implemented libraries.
