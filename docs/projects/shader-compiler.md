<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.ShaderCompiler (+ Vulkan, + Metal)

Three projects that translate PS5 GPU (RDNA / "Gen5", gfx10) shader
microcode into host shader languages: SPIR-V for Vulkan and MSL (Metal
Shading Language) for Metal. The design is two-stage: a **backend-neutral
shared IR** (`SharpEmu.ShaderCompiler`), then **per-backend translators**
(`SharpEmu.ShaderCompiler.Vulkan`, `SharpEmu.ShaderCompiler.Metal`).

The emitters deliberately have **no dependency on a host graphics API**
(`.Vulkan` has no Vulkan bindings, `.Metal` has no Metal bindings): emitters
produce bytes/text; the renderers in `SharpEmu.Libs/Gpu/Vulkan` and
`SharpEmu.Libs/Gpu/Metal` own the APIs.

> "Gen5" = the graphics generation being decoded (RDNA / gfx10, the PS5 GPU
> ISA). It is unrelated to `SharpEmu.HLE.Generation` (Gen4/Gen5 = PS4/PS5).

## `SharpEmu.ShaderCompiler` (shared IR)

Namespace `SharpEmu.ShaderCompiler`. References `SharpEmu.HLE` (for `ICpuMemory`
to read guest shader bytecode). No host graphics API dependency.

| File | Purpose |
| --- | --- |
| `Gen5ShaderIr.cs` | The shader IR: instructions, operands (`Gen5OperandKind`: `ScalarRegister`, ...), encodings (`Gen5ShaderEncoding`: `Smem`, `Smrd`, ...), shader state, evaluation. |
| `Gen5ShaderTranslator.cs` | The frontend: decodes Gen5 microcode into the IR. `ComputeConsumedScalarMask` (which scalar registers a program observes — used for byte-stable SPIR-V across draws). ~2656 lines. |
| `Gen5ShaderScalarEvaluator.cs` | The scalar evaluator (per-lane/wave semantics, EXEC/VCC). |
| `Gen5ShaderMetadataReader.cs` | Reads shader metadata embedded in the guest shader object. |
| `Gen5InlineConstants.cs` | Inline constant tables. |
| `Gfx10UnifiedFormat.cs` | Unified gfx10 format helpers. |
| `GuestDrawKind.cs` | Enum of guest draw kinds. |
| `Ir/IrControlFlow.cs` | Control-flow analysis (basic blocks, branches). Tested by `IrControlFlowTests`. |
| `Ir/Gen5ScalarSsa.cs` | Scalar SSA. Tested by `Gen5ScalarSsaTests`. |
| `Ir/Gen5IrBranchResolver.cs` | Branch resolution. |

The IR concepts (from the test suite): control flow, scalar SSA, reaching
definitions (`Gen5ReachingDefinitionTests`), VOPC f16 (`Gen5VopcF16Tests`),
implicit VCC (`Gen5ImplicitVccTests`), image ops (`Gen5ImageTests`), flat
memory (`Gen5FlatMemoryTests`), float16 arithmetic
(`Gen5Float16ArithmeticTests`).

## Vulkan (SPIR-V backend)

Namespace `SharpEmu.ShaderCompiler.Vulkan`. References
`SharpEmu.ShaderCompiler`. No Vulkan bindings dependency.

| File | Purpose |
| --- | --- |
| `Gen5SpirvTranslator.cs` | `static partial class` — lowers IR to SPIR-V. Entry points: `TryCompilePixelShader`, and (by symmetry) vertex/compute. Constants: `ScalarRegisterCount=256`, `VectorRegisterCount=512`, `LdsDwordCount=8192`, `PrivateLdsDwordCount=2048`, `RdnaWaveLaneCount=32`. ~5863 lines. |
| `Gen5SpirvTranslator.Alu.cs` | ALU lowering partial. |
| `Gen5SpirvShader.cs` | The output shader container. |
| `SpirvModuleBuilder.cs` | The SPIR-V word emitter. Emits SPIR-V 1.5 (VulkanVideoPresenter requests Vulkan 1.2 — see CI env `SPIRV_TARGET_ENV`). |
| `SpirvFixedShaders.cs` | Handwritten fixed SPIR-V shaders. |

The execution model (per the MSL doc comment, mirrored here): one GPU
invocation is one GCN lane (wave32 — the native subgroup size); the register
file is typeless 32-bit uints (float ALU bitcasts through); control flow is a
PC-dispatcher loop — a bounded `while` over a `switch` of GCN basic blocks —
rather than reconstructed structured control flow. EXEC/VCC live in their
architectural SGPRs (`s106`/`s107`, `s126`/`s127`) as raw data, with per-lane
bools as synced views. Wave64 is bridged across two 32-wide subgroups.

The output is consumed by the Vulkan renderer in `SharpEmu.Libs/Gpu/Vulkan`.

## Metal (MSL backend)

Namespace `SharpEmu.ShaderCompiler.Metal`. References
`SharpEmu.ShaderCompiler`. No Metal bindings dependency. Embeds `Templates/**/*.msl`
as resources.

| File | Purpose |
| --- | --- |
| `Gen5MslTranslator.cs` | `static partial class` — lowers IR to MSL source text. Entry points: `TryCompilePixelShader`, etc. Constants mirror the SPIR-V translator (`ScalarRegisterFileCount=128`, `VectorRegisterFileCount=256`, `VccLoRegister=106`, `ExecLoRegister=126`, ...). The Metal renderer compiles the source via `MTLLibrary` at bind time. ~2261 lines. |
| `Gen5MslTranslator.Alu.cs` | ALU lowering partial. |
| `Gen5MslTranslator.Pixel.cs` | Pixel-shader-specific partial. |
| `Gen5MslShader.cs` | The output shader container. |
| `MslTemplates.cs` | Renders static MSL blocks with placeholder substitution. |
| `MslFixedShaders.cs` | Handwritten fixed MSL shaders. |
| `Templates/` | Static MSL blocks (prelude helpers, fixed shaders) authored as real Metal source. |

The Metal backend mirrors the SPIR-V translator's execution model. Buffer
argument contract (documented in the source comment):
- `[[buffer(globalBufferBase + i)]]` — global memory binding `i`, in
  `Gen5ShaderEvaluation.GlobalMemoryBindings` order.
- `[[buffer(uniformsIndex)]]` — one `SharpEmuUniforms` constant buffer holding
  the compute dispatch limit and per-buffer byte lengths, where
  `uniformsIndex = globalBufferBase + totalGlobalBufferCount`.

> **Is the Metal backend used?** macOS normally runs the `osx-x64` build under
> Rosetta 2 and uses MoltenVK (Vulkan-on-Metal), so the **SPIR-V path is the
> default on macOS too**. The Metal backend is an experimental native
> alternative (`SharpEmu.Libs/Gpu/Metal`), exercised by
> `tests/SharpEmu.ShaderCompiler.Metal.Tests/MetalRuntimeTests.cs`
> (`MetalNative`) which compiles MSL against a real Metal device.

## Public API surface

- `SharpEmu.ShaderCompiler.Gen5ShaderTranslator` (`public static`) — frontend
  entry; produces `Gen5ShaderState` + `Gen5ShaderEvaluation`.
- `SharpEmu.ShaderCompiler.Vulkan.Gen5SpirvTranslator` (`public static
  partial`) — `TryCompilePixelShader(...)` → `Gen5SpirvShader`.
- `SharpEmu.ShaderCompiler.Metal.Gen5MslTranslator` (`public static partial`)
  — `TryCompilePixelShader(...)` → `Gen5MslShader`.

The shaders consume `Gen5ShaderEvaluation.GlobalMemoryBindings`,
`Gen5PixelOutputBinding`, `Gen5PixelOutputKind`, etc.

## Dependencies

None beyond `SharpEmu.HLE` (shared) and `SharpEmu.ShaderCompiler` (backends).
The renderers (in `SharpEmu.Libs/Gpu/...`) consume the output and own the
graphics APIs.

## Testing & tooling

- `tests/SharpEmu.ShaderCompiler.Tests/` — IR + SPIR-V: control flow, SSA,
  reaching definitions, VOPC f16, implicit VCC, image ops, flat memory,
  float16 arithmetic.
- `tests/SharpEmu.ShaderCompiler.Metal.Tests/` — MSL translation, golden
  output (`MslGoldenTests`), float16, F16 compare, native Metal runtime
  (`MetalRuntimeTests` using `MetalNative` + `FakeGuestMemory` + compute
  fixtures).
- `tools/SharpEmu.Tools.ShaderDump` — dumps synthetic shaders; CI runs it then
  validates the SPIR-V with `spirv-val` via
  `scripts/validate-synthetic-spirv.sh` (see
  [Build configuration §CI](../reference/build-configuration.md#ci)).
- `tools/SharpEmu.Tools.GpuConformance` — GPU conformance checks.

## Gotchas

- The MSL and SPIR-V translators mirror each other's scope and execution
  model — when you change one, check whether the other needs the same change
  (the MSL source comment explicitly says so).
- "Undefined" EFLAGS/flags are left untouched for determinism; the same
  principle applies to shader determinism (the consumed-scalar mask makes
  SPIR-V byte-stable across draws).
- To add a new instruction, the shared frontend (`Gen5ShaderTranslator`) is
  where decoding lands; lowering goes into each backend's translator (often
  the `.Alu` partial).

## Related

- [SharpEmu.Libs guide](libs.md) — the GPU backends that consume this output.
- [Build configuration](../reference/build-configuration.md) — the
  `SPIRV_TARGET_ENV` / SPIRV-Tools pinning in CI.
