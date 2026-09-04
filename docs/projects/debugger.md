<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.Debugger (+ SharpEmu.DebugClient)

A live TCP debug server that lets an external process inspect and control a
running guest. The server lives in the emulator; `SharpEmu.DebugClient` is a
standalone CLI client. The wire protocol is JSON-lines, simple enough to
script directly. See [`debugger-server.md`](../debugger-server.md) for the
full protocol reference; this page is the project/file map.

## Layering

The dependency direction is important: Core stays debugger-agnostic and only
publishes the seam. The debugger implements that seam and is injected through
`SharpEmuRuntimeOptions.DebugHook`, so the debugger can evolve without
touching the CPU core.

| Assembly | Role |
| --- | --- |
| `SharpEmu.Core` | Defines `ICpuDebugHook` / `ICpuDebugFrame` (`namespace SharpEmu.Core.Cpu.Debugging`) and the `CpuExecutionOptions.DebugHook` slot. Core has **no** reference to the debugger. |
| `SharpEmu.Debugger` | The debugger: `DebuggerSession` (implements the hook), `BreakpointStore`, the TCP `DebuggerServer`, the pluggable `IDebugProtocol` (with a JSON-lines implementation), and the one-call `DebuggerServerHost` wiring. |
| `SharpEmu.CLI` | Parses `--debug-server`, builds a `DebuggerServerHost`, hands its `Hook` to `SharpEmuRuntimeOptions.DebugHook`, and manages its lifetime. |
| `SharpEmu.DebugClient` | A standalone client executable. Depends only on the BCL. |

## `SharpEmu.Debugger`

`src/SharpEmu.Debugger/SharpEmu.Debugger.csproj` references `SharpEmu.Core`,
`SharpEmu.HLE`, and `SharpEmu.Logging`.

### File map

| File | Purpose |
| --- | --- |
| `DebuggerServerHost.cs` | One-call wiring: owns a `DebuggerSession` + `DebuggerServer`, exposes `Hook` (an `ICpuDebugHook`), starts/stops the network front-end, and `NotifyRunCompleted()` after the runtime returns. `IAsyncDisposable`. |
| `DebugRegisterId.cs` / `DebugRegisterFile.cs` | Register ID enum + the register file view the protocol serializes. |
| `Session/DebuggerSession.cs` | `sealed class` — implements `ICpuDebugHook`. `OnFrameEnter` decides whether to stop (pause request, breakpoint on the entry address, single-step, stop-at-entry). To stop, it **parks the emulation thread** inside this call on a gate; the frame stays live so a client can read/write registers and memory while parked. `OnFrameExit` notifies. |
| `Session/DebuggerSessionOptions.cs` | `StopAtEntry` (default on) and options. |
| `Session/DebuggerRunState.cs` | Run-state enum (`Running`/`Paused`/...). |
| `Session/IDebugTarget.cs` | The target contract the session drives. |
| `Session/IDebuggerSession.cs` | Public session contract (`State`, `Continue`, `StepFrame`, `Pause`, register/memory accessors). |
| `Session/DebugStopReason.cs` | `EntryPoint`, `Breakpoint`, `Watchpoint`, `Step`, `Pause`, `Fault`, `Stall`. |
| `Session/DebugStopEvent.cs` | The structured stop event (reason, address, frameKind, frameLabel, registers, breakpoint, optional `stall` evidence). |
| `Breakpoints/BreakpointStore.cs` | Breakpoint storage (add/remove/enable). |
| `Breakpoints/Breakpoint.cs` | A breakpoint record (id, address, kind, length, enabled). |
| `Breakpoints/BreakpointKind.cs` | `execute`, `readwatch`, `writewatch`, `accesswatch`. |
| `Server/IDebuggerServer.cs` | Server contract. |
| `Server/DebuggerServer.cs` | TCP server; takes an `IDebugProtocol` factory (default `JsonLineDebugProtocol`; a GDB remote stub can be dropped in). |
| `Server/DebuggerServerOptions.cs` | Bind endpoint (default `127.0.0.1:5714`). |
| `Server/DebuggerClientConnection.cs` | One client connection. |
| `Protocol/IDebugProtocol.cs` | The pluggable protocol contract. |
| `Protocol/JsonLineDebugProtocol.cs` | The JSON-lines implementation. |
| `Protocol/DebugRequest.cs` / `DebugResponse.cs` | Request/response DTOs. |
| `Protocol/DebugCommandDispatcher.cs` | `sealed class` — translates parsed `DebugRequest` verbs into `IDebuggerSession` operations and packages the outcome as a `DebugResponse`. **The single place command semantics live**, shared by every connection and independent of the wire format. |

### Execution model

`CpuDispatcher` enters a fresh frame for the process entry point and for each
module initializer. When a `DebugHook` is attached it is notified at those
boundaries:

- `OnFrameEnter(frame)` — before the native backend runs. The `DebuggerSession`
  decides whether to stop (pause request, breakpoint on the entry address,
  single-step, or stop-at-entry). To stop, it parks the emulation thread on a
  gate; the frame stays live so a client can read/write registers and memory
  while parked. `continue`/`step` release the gate.
- `OnFrameExit(frame, result)` — after the frame completes.

Because pausing parks the one thread that owns the guest context, register and
memory accessors are only served while the session reports `Paused`; otherwise
they return "not paused" so a client never observes torn state.

### What is and isn't live

- **Live:** attach/handshake, run-state tracking, register read/write, memory
  read/write, breakpoint management, execution breakpoints at frame entry,
  pause, frame-level step, continue, stop/resume/terminate events.
- **Surface only (armed as the backend grows hooks):** per-instruction stepping
  and data watchpoints (`readwatch`/`writewatch`/`accesswatch`). The verbs and
  types exist so clients and tooling can be written now.

### Enabling the server

```bash
SharpEmu --debug-server "/path/to/eboot.bin"            # 127.0.0.1:5714
SharpEmu --debug-server=0.0.0.0:5714 "/path/to/eboot.bin"
```

The bind address defaults to loopback; a routable address must be given
explicitly. With stop-at-entry (the default), the guest parks at its first
frame until a client connects and issues `continue`.

### Embedding the server

```csharp
using SharpEmu.Debugger;
using SharpEmu.Core.Runtime;

await using var host = new DebuggerServerHost();
host.Start();

var options = new SharpEmuRuntimeOptions { DebugHook = host.Hook };
using var runtime = SharpEmuRuntime.CreateDefault(options);
var result = runtime.Run(ebootPath);

host.NotifyRunCompleted();
```

## DebugClient

A standalone console tool (`OutputType=Exe`, `AssemblyName=SharpEmu.DebugClient`).
Takes no dependency on the emulator assemblies — it speaks the server's
line-delimited JSON protocol directly over TCP, so you can also drive the
server from `nc`, a script, or your own tool. See
[`src/SharpEmu.DebugClient/DEVELOPER_READ.md`](../../src/SharpEmu.DebugClient/DEVELOPER_READ.md).

### File map

| File | Purpose |
| --- | --- |
| `Program.cs` | `ClientProgram.RunAsync(args)`. `--exec`/`-e` for non-interactive commands, `--quiet`, `--help`/`-h`. Defaults to `127.0.0.1:5714`. |
| `ClientEndpoint.cs` | The interactive REPL + connection management. |
| `CommandTranslator.cs` | Translates user commands (`status`, `regs`, `break`, `continue`, `mem`, ...) into `DebugRequest`s. |
| `DebugClientConnection.cs` | The TCP connection + JSON-line framing. |

### Output

The client prints two kinds of lines as they arrive:

- `reply>` — the response to a command you sent (`ok`, plus `data` or `error`).
- `event>` — an unsolicited notification: `hello` on connect, `stopped` on a
  breakpoint/entry/step/pause, `resumed` on continue, `terminated` when the run
  ends.

Because replies and events share one stream, the client prints everything it
receives rather than pairing replies to requests.

## Browser frontend

`tools/SharpEmu.DebuggerFrontend/` — a dependency-free Python browser UI. A
small bridge talks to the emulator over JSON-lines TCP and serves the frontend
on loopback. See [`tools/SharpEmu.DebuggerFrontend/README.md`](../../tools/SharpEmu.DebuggerFrontend/README.md)
and [`debugger-server.md`](../debugger-server.md).

```bash
./tools/SharpEmu.DebuggerFrontend/run.sh
```

It connects to `127.0.0.1:5714` and opens `http://127.0.0.1:8765/` by
default. It can launch a game's `eboot.bin`, attach automatically, and
provides execution controls, registers, memory inspection, breakpoint
management, process output, a live protocol activity stream, and
evidence-based stall diagnosis.

## Related

- [`debugger-server.md`](../debugger-server.md) — wire protocol reference.
- [SharpEmu.Core guide](core.md) — `ICpuDebugHook` seam.
- [Debugging guide](../guides/debugging.md) — how to use all of this.
