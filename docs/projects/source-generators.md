<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.SourceGenerators

Roslyn source generator + analyzer for the SysABI export system. Turns
`[SysAbiExport]` methods into a compile-time registry, validates NIDs against
the PS5 symbol catalog, and builds the embedded `aerolib.bin` resource.
Consumed as an analyzer reference — never ships as a runtime dependency.

`src/SharpEmu.SourceGenerators/SharpEmu.SourceGenerators.csproj` targets
**`netstandard2.0`** (required to load inside the compiler), with
`<LangVersion>latest</LangVersion>`, `<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>`,
and `<IsRoslynComponent>true</IsRoslynComponent>`. NuGet packages
(`Microsoft.Build.Framework`, `Microsoft.CodeAnalysis.Analyzers`,
`Microsoft.CodeAnalysis.CSharp`) are marked `PrivateAssets="all"` and
`ExcludeAssets="runtime"` (analyzer-only, not deployed).

The consuming projects reference it as:

```xml
<ProjectReference Include="..\SharpEmu.SourceGenerators\SharpEmu.SourceGenerators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

## File map

| File | Purpose |
| --- | --- |
| `Ps5Nid.cs` | The PS5 NID algorithm. `Ps5Nid.Compute(string symbolName) → string` (UTF-8 `name || 16-byte fixed suffix` → SHA-1 → first 8 bytes byte-reversed → Base64-stripped/`/`→`-`). `Ps5Nid.IsValidFormat(string)` validates the 11-char `[A-Za-z0-9+-]` shape. Used by the analyzer, the generator (when `Nid` is omitted), and the `GenerateAerolibBinaryTask`. |
| `SysAbiExportShape.cs` | The **single source of truth** shared by the generator and the analyzer (so they cannot disagree). Defines the attribute name (`SharpEmu.HLE.SysAbiExportAttribute`), `ArgumentRegisters = ["Rdi","Rsi","Rdx","Rcx","R8","R9"]`, the `HandlerShape` enum (`ContextOnly` / `Parameterless` / `Typed`), and `Classify`/`ReadArguments`/`TryGetGuestCStringMaxLength`. `IsAccessibleFromGeneratedCode` is why handler types must be at least `internal`. |
| `SysAbiExportGenerator.cs` | `IIncrementalGenerator`. Emits one `SharpEmu.Generated.SysAbiExportRegistry` per assembly that contains at least one `[SysAbiExport]`. The generated `CreateExports(Generation registrationGeneration)` returns `IReadOnlyList<ExportedFunction>`, applying the same generation-filtering rule as `ModuleManager.TryDispatch`. The generated `TypedThunk` reads `ctx[CpuRegister.<Reg>]` positionally and, for `[GuestCString]` parameters, emits the pre-call `ctx.TryReadNullTerminatedUtf8(...)` that returns `ORBIS_GEN2_ERROR_MEMORY_FAULT` on failure. |
| `SysAbiExportAnalyzer.cs` | The analyzer. Reports `SysAbiDiagnostics` for invalid declarations (non-static, generic, wrong return type, private, unknown shape, etc.). Invalid declarations are **skipped by the generator and reported as build errors** — nothing is silently dropped. |
| `SysAbiDiagnostics.cs` | The diagnostic IDs. |
| `GenerateAerolibBinaryTask.cs` | `ITask` (not an analyzer, so RS1035's file-IO ban doesn't apply). Reads `scripts/ps5_names.txt`, computes each name's NID via `Ps5Nid.Compute`, and writes the binary in the exact format `Aerolib.LoadFromEmbeddedBinary` reads. Wired by the `GenerateAerolibBinary` + `EmbedAerolibBinary` MSBuild targets in `SharpEmu.HLE.csproj`. |

## The NID algorithm

```text
input   = UTF8(symbolName) || 16-byte fixed suffix
hash    = SHA1(input)
bytes   = hash[0..8] (byte-reversed)
nid     = Base64UrlSafe(bytes), strip "=" padding
```

`Ps5Nid.Compute("sceKernelWaitSema")` → `"Zxa0VhQVTsk"`. Reverse the lookup
by hashing each name in `scripts/ps5_names.txt` and building a binary catalog
at build time.

## The generated registry

For each assembly with `[SysAbiExport]` methods, the generator emits
`SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation)` which
returns `IReadOnlyList<ExportedFunction>`. The runtime consumes it like:

```csharp
var generated = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5);
foreach (var export in generated)
{
    moduleManager.RegisterExports(new[] { export });
}
moduleManager.Freeze();
```

Generation filtering: an attribute `Target` of `Generation.None` inherits the
registration generation; otherwise the attribute's target is filtered against
the context's `TargetGeneration`. The dispatcher (`ModuleManager.cs:284-290`)
applies the same rule.

## The analyzer

The analyzer surfaces invalid declarations and unknown NIDs at build time.
`scripts/ps5_names.txt` is added as `AdditionalFiles` in `SharpEmu.Libs.csproj`
so the analyzer can check export names against the catalog. A `[SysAbiExport]`
with a name not in the catalog produces a warning; an unrecognized shape
produces an error.

## The aerolib MSBuild task

`SharpEmu.HLE.csproj:35-75` defines the targets:

```text
ResolveProjectReferences
  ↓
GenerateAerolibBinary  → runs GenerateAerolibBinaryTask → writes artifacts/aerolib.bin
  ↓
EmbedAerolibBinary     → adds the file as an EmbeddedResource with logical name
                          SharpEmu.HLE.Aerolib.aerolib.bin
```

`EmbedAerolibBinary` is a separate target hooked before `AssignTargetPaths`
so an up-to-date skip of `GenerateAerolibBinary` doesn't drop the item.
Design-time builds skip the embed (to keep the IDE fast). `aerolib.bin` is
derived data — it replaces the old `scripts/generate_aerolib_binary.py` and
is never committed.

The runtime-side reader is `Aerolib.LoadFromEmbeddedBinary`
(`src/SharpEmu.HLE/Aerolib/Aerolib.cs:103`): `uint32` entry count, then per
entry `byte` NID length + UTF-8 NID, `ushort` name length + UTF-8 name.

## Gotchas

- The generator and the analyzer both read `SysAbiExportShape.cs` so they
  cannot disagree; when changing the accepted handler shapes, update
  `SysAbiExportShape.Classify` and `ArgumentRegisters` together.
- Handler types must be at least `internal` (because the generated code
  references them directly).
- `Microsoft.Build.Framework` is the right package for an `ITask` (RS1035's
  file-IO ban applies only to analyzers).
- When adding a new `Generation`, update both `Generation.cs` and
  `SysAbiExportGenerator.cs:233-237` so the filter is generated correctly.

## Related

- [The SysABI / HLE export system](../architecture/hle-and-sysabi.md)
- [SharpEmu.HLE guide](hle.md) — the catalog reader.
- [`aerolib-catalog.md`](../aerolib-catalog.md) — the CLI for the catalog.
