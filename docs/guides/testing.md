<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Testing

This page covers the test layout, how to run the tests, and conventions for
writing new tests. The project uses **xUnit** (`xunit` 2.9.3,
`xunit.runner.visualstudio` 3.1.1, `Microsoft.NET.Test.Sdk` 17.14.1) and the
`FluentAssertions`-style helpers xUnit provides natively.

## Test projects

Four test projects live under `tests/`:

| Project | What it covers |
| --- | --- |
| `SharpEmu.Libs.Tests` | The largest. Covers HLE subsystems (VideoOut, Audio, Agc, Ampr, AvPlayer, Cpu, Memory, GUI, Font, Ajm, Acm, Network, Media, PlayGo, Random, Rtc, SaveData, Fiber, SystemService, Kernel/TLS, etc.), the SysAbi registry, the aerolib catalog, and the shared logging. |
| `SharpEmu.ShaderCompiler.Tests` | The shared shader IR + the SPIR-V backend: control flow, scalar SSA, reaching definitions, VOPC f16, implicit VCC, image ops, flat memory, float16 arithmetic. |
| `SharpEmu.ShaderCompiler.Metal.Tests` | The MSL backend: translation, golden output (`MslGoldenTests`), float16, F16 compare, native Metal runtime (`MetalRuntimeTests` using `MetalNative` + `FakeGuestMemory` + compute fixtures). |
| `SharpEmu.SourceGenerators.Tests` | The `SysAbiExportGenerator` and `SysAbiExportAnalyzer` against a Roslyn test host (`RoslynTestHost`), plus `Ps5NidTests`. |

Many test class names mirror the subsystem they cover (e.g.
`VideoOut/VulkanPresentEncodeFormatTests.cs`, `Agc/AgcVertexMetadataTests.cs`,
`Ampr/PakDirectoryTrackerTests.cs`, `Cpu/ImportTrampolineAbiTests.cs`).

## Running the tests

Run everything:

```bash
dotnet test SharpEmu.slnx
```

Run a single test project:

```bash
dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj
```

Run by filter (substring of the fully qualified test name):

```bash
dotnet test SharpEmu.slnx --filter "FullyQualifiedName~AgcShaderStageRegister"
```

Run in Release (matches CI):

```bash
dotnet test SharpEmu.slnx -c Release --no-build
```

CI uses the `--no-build` form because `dotnet build` already runs the
solution as a separate step.

## Conventions

- One test class per subsystem or behavior. Class names mirror the file
  layout (`Agc/AgcXyzTests.cs`, `Cpu/CpuXyzTests.cs`, ...).
- Test method names follow `MethodOrBehavior_State_ExpectedResult` where
  practical (`TryAllocateAtExact_LargeReserve_PrimedAndCommitted`,
  `ImportTrampoline_UnresolvedSentinel_ParkAndResume`, ...).
- The `SharpEmu.Libs.Tests` project has `InternalsVisibleTo` access to
  `SharpEmu.Core`, `SharpEmu.HLE`, and `SharpEmu.GUI`. Use this to assert on
  `internal` helpers when it helps (e.g. `GuestWriteWatch`, `ModuleManager`'s
  warm-up logic).
- Test fixtures that emulate memory use `FakeCpuMemory.cs` (in
  `SharpEmu.Libs.Tests`) and `FakeGuestMemory.cs` (in
  `SharpEmu.ShaderCompiler.Metal.Tests`). Reuse these instead of inventing a
  new fake when you need a memory backend.
- Tests for analyzers/source generators use `RoslynTestHost`
  (`SharpEmu.SourceGenerators.Tests/RoslynTestHost.cs`); mirror that approach
  when adding new analyzer tests.

## Adding a new test

1. Pick the right test project. Most HLE work goes in
   `SharpEmu.Libs.Tests/<Subsystem>/`.
2. Match the file layout — drop a `MyBehaviorTests.cs` under the relevant
   subsystem folder.
3. If you need a memory backend, use the existing fakes rather than writing
   a new one.
4. Add the SPDX header to the new file.
5. Run the tests locally before opening a PR (`dotnet test`).

## Debugging a failing test

- `dotnet test --filter "..."` to scope to one test.
- `dotnet test --logger "console;verbosity=detailed"` for full output.
- The emulator's logger (`SharpEmuLog`) can be helpful even in tests — set
  `SHARPEMU_LOG_LEVEL=Debug` for the test process.
- For CI failures, the Actions logs include both the `dotnet build` and
  `dotnet test` outputs and any stderr from the test process.

## Related

- [Contributing workflow](contributing.md)
- [Debugging guide](debugging.md) — diagnosing issues, not test failures.
- [Environment variables](../reference/environment-variables.md) — knobs
  useful while writing tests.
