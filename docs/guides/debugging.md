<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Debugging

This page is about debugging **the emulator** and **a running guest**. It
covers the live debug server, the browser frontend, the guest-write-watch
diagnostic, the most useful `SHARPEMU_*` env vars, and where to look when
something goes wrong.

## Debugging a running guest

The most useful tool is the **live debug server**. Start the emulator with
`--debug-server` and attach a client.

```bash
# Start the emulator with the debug server enabled (default 127.0.0.1:5714)
SharpEmu --debug-server "/path/to/eboot.bin"

# Attach the CLI client (defaults to 127.0.0.1:5714)
dotnet run --project src/SharpEmu.DebugClient/SharpEmu.DebugClient.csproj --

# Or use the browser frontend
./tools/SharpEmu.DebuggerFrontend/run.sh
```

With stop-at-entry (the default), the emulator parks at the first frame until
you `continue`. This is the easiest way to set breakpoints before any guest
code runs.

See [`debugger-server.md`](../debugger-server.md) for the full wire protocol
reference and [`src/SharpEmu.DebugClient/DEVELOPER_READ.md`](../../src/SharpEmu.DebugClient/DEVELOPER_READ.md)
for client-side commands.

### The browser frontend

`tools/SharpEmu.DebuggerFrontend/` is a dependency-free Python web UI. It
connects to `127.0.0.1:5714`, opens `http://127.0.0.1:8765/`, and provides:

- Execution controls (continue/pause/step), keyboard shortcuts.
- Register inspection and editing.
- Hex/ASCII memory reads and validated memory writes.
- Breakpoint and watchpoint creation, toggling, and deletion.
- Stop reason, frame, result, opcode, fault details.
- Evidence-based stall diagnosis with likely causes, ranked fixes, and
  targeted checks.
- Raw JSON command console for new protocol operations.
- Searchable activity stream (requests, replies, async events).

See [`tools/SharpEmu.DebuggerFrontend/README.md`](../../tools/SharpEmu.DebuggerFrontend/README.md)
for configuration options and the test commands.

### Stall diagnosis

When the debug server reports a `Stall` stop with `kind=ImportLoop`, the
event payload includes the NID, the resolved HLE export, the repeating guest
return site, the dispatch count, and the first two ABI arguments. The Python
frontend uses this evidence to explain the likely failure class and rank
concrete checks/fixes — its diagnosis is intentionally labelled heuristic; it
helps locate the responsible HLE/scheduler path but does not replace tracing.
See [`debugger-server.md`](../debugger-server.md#events-unsolicited) for the
event payload format.

## Debugging the emulator (managed side)

- **Logs.** `SHARPEMU_LOG_LEVEL=Debug` (or `Trace`) for the whole process,
  `SHARPEMU_LOG_FILE=path/to/log` to write a full log to disk (the file
  captures every level; the console still respects the level).
  See the [Logging guide](../projects/logging.md).
- **`BuildInfo`.** `BuildInfo.WriteBanner(...)` is printed at the top of the
  log with the commit SHA, branch, and workflow run URL — useful when
  bisecting a regression.
- **Hot-path logs.** Many subsystems log under specific tags (e.g. `"VMEM"`
  is `SHARPEMU_LOG_VMEM=1`, `"AUDIO_QUEUE"` is `SHARPEMU_LOG_AUDIO_QUEUE=1`).
  See [Environment variables](../reference/environment-variables.md) for the
  full list.

## Debugging the CPU backend

- `SHARPEMU_LOG_DISASM=1` — print a disasm prelude at fault time
  (`DumpGuestDisasmDiagnostics`).
- `SHARPEMU_PROFILE_GUEST_RIP=1` — start the guest-RIP sampler
  (`DirectExecutionBackend.GuestSampler.cs`) to attribute samples to guest
  addresses (`app+…`, `stub:NID`, `host`).
- `SHARPEMU_PERF_HLE=1` — perf counters for HLE dispatch.
- `SHARPEMU_PERF_HLE_NODICT=1` — perf counters without the dispatch-cache.
- `SHARPEMU_LOG_ALL_IMPORTS=1` — log every import the guest calls.

`#UD` (illegal instruction) faults are handled by
`DirectExecutionBackend.IllegalInstruction.TryRecoverIllegalInstruction` (BMI)
and `DirectExecutionBackend.Amd64Compat.TryRecoverSse4aExtractInsert` (SSE4a).
If you see "FS segment prefix - TLS access not patched!" the TLS patcher
(`PatchTlsPatterns` in `DirectExecutionBackend.cs:3149`) missed a pattern —
either it doesn't match your new code path or the executable window is
beyond the 128 MiB scan range.

## Debugging guest memory corruption

[`guest-write-watch.md`](../guest-write-watch.md) describes `GuestWriteWatch`,
an optional diagnostic tool that monitors writes through the managed
virtual-memory APIs. It is off by default; arm it with one or more
`SHARPEMU_WATCH_*` variables:

- `SHARPEMU_WATCH_WRITE=0x<address>` — log writes overlapping an 8-byte block.
- `SHARPEMU_WATCH_POOL_HEADER=1` — monitor the pointer at pool offset `0x40`.
- `SHARPEMU_WATCH_VALUE_PATTERN=1` — log an 8-byte write with a specific
  pattern.
- `SHARPEMU_WATCH_VALUE1=1` — log short writes of `1` in the high guest
  range.
- `SHARPEMU_WATCH_BULK_TORN=1` — scan aligned 64-bit words in bulk writes for
  damaged/byte-shifted pointer patterns.
- `SHARPEMU_WATCH_BULK_DEST_HI=0x<high-dword>` — restrict bulk scans to a
  destination prefix.

> The watcher monitors writes through the **managed** virtual-memory APIs.
> It does not monitor stores that native guest code makes directly. Use a
> platform debugger or a hardware watchpoint for those.

## Debugging video / GPU issues

- `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1` — enable `GuestImageWriteTracker` to
  re-upload GPU surfaces when the guest CPU writes them. See
  [`guest-write-watch.md`](../guest-write-watch.md) for related env vars.
- For the GPU backends themselves, look at `src/SharpEmu.Libs/Gpu/Vulkan/`
  and `src/SharpEmu.Libs/Gpu/Metal/`. The `SharpEmu.Libs.Tests/VideoOut/`
  and `SharpEmu.Libs.Tests/Agc/` suites cover most of the surface.
- For shader compilation issues, run the shader dump tool and validate the
  SPIR-V with `spirv-val` (see [Getting started §Validate shaders](getting-started.md#validate-shaders)).

## Common errors and what they mean

| Symptom | Likely cause | Where to look |
| --- | --- | --- |
| "still-encrypted retail eboot — use fSELF" | The eboot isn't a decrypted ELF/fSELF. | The loader's `ParseLayout`. |
| "TLS access not patched!" | A new TLS-load pattern is outside the 128 MiB scan range. | `PatchTlsPatterns` in `DirectExecutionBackend.cs:3149`. |
| `NOT_FOUND` for an import | The NID isn't registered as an HLE export. | `python scripts/aerolib_catalog.py lookup <name>`. |
| `NOT_IMPLEMENTED` for an import | The export's `Target` doesn't match the registration generation. | The export's `Target` attribute + `ModuleManager.TryDispatch`. |
| Parked guest thread, no progress | Likely a wait on a kernel object. | `GuestThreadExecution.RequestCurrentThreadBlock` — find the waiter. |
| Vulkan validation layer error | A bind/descriptor mismatch. | `SharpEmu.Libs/Gpu/Vulkan/`, enabled with the standard Vulkan `VK_LAYER_KHRONOS_validation`. |
| Apple Silicon exit with hint | The process must be `osx-x64` (Rosetta 2). | `Program.cs` `CheckHostArchitecture`. |

## Filing a useful bug report

Include:

- The game's `eboot.bin` title id (visible in `sce_sys/param.json`).
- The SharpEmu commit SHA (`BuildInfo.CommitSha` from the log banner) and
  build configuration (`Debug`/`Release`).
- Host OS, GPU, driver version.
- The exact command line and any `SHARPEMU_*` env vars you set.
- Relevant log fragments (use `SHARPEMU_LOG_FILE`).
- For stalls: the debug-server `stopped` payload.

## Related

- [`debugger-server.md`](../debugger-server.md) — protocol reference.
- [`guest-write-watch.md`](../guest-write-watch.md) — the write watcher.
- [`bink2-bridge.md`](../bink2-bridge.md) — the Bink 2 video pipeline.
- [Environment variables](../reference/environment-variables.md) — the full
  list of `SHARPEMU_*` knobs.
- [SharpEmu.Debugger guide](../projects/debugger.md) — the server/client.
