<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Environment Variables

Every runtime knob the emulator exposes is an environment variable prefixed
`SHARPEMU_`. This page is the exhaustive reference. Each entry lists the
default behavior, what the variable does, and where in the code it is
consumed. Most are read once at startup or in a static initializer.

> If a knob you set doesn't seem to take effect, check the spelling and
> remember that some are only consulted at specific points (e.g. lazy
> commit priming happens during initial memory setup).

## Logging

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_LOG_LEVEL` | `Info` | Minimum level: `Trace`/`Debug`/`Info`/`Warning`/`Error`/`Critical`/`None`. Also accepts `warn`/`fatal`. |
| `SHARPEMU_LOG_FILE` | unset | If set to a writable path, a file sink is added that captures **every** level; the console is still filtered by the minimum level. |
| `SHARPEMU_LOG_NO_COLOR` | unset | Disable colored console output (`1`/`true`/`yes`/`on`). |

Consumed in `src/SharpEmu.Logging/SharpEmuLog.cs`.

## CLI / startup

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_MITIGATED_CHILD` | unset | Internal — set by the parent process to mark the child launched with CET/CFG mitigations off. The `--sharpemu-mitigated-child` arg is only trusted when this env var is set. |
| `SHARPEMU_DISABLE_MITIGATION_RELAUNCH` | unset | Skip the Windows mitigated-child relaunch. |
| `SHARPEMU_APP0_DIR` | set by `SharpEmuRuntime` to the eboot's directory | The guest's `/app0` mount. |
| `SHARPEMU_GUEST_ARGS` | unset | Append up to 2 extra `argv` strings for the guest entry. |
| `SHARPEMU_IGNORE_INT41` | on | Skip PS5 guest `int 0x41` traps. |
| `SHARPEMU_IGNORE_STACK_CHK` | unset | Recover a known `__stack_chk_fail` epilogue. |
| `SHARPEMU_PRELOAD_ALL_SCE_MODULES` | unset | Load `libkernel.prx`/`libkernel_sys.prx` (and other normally-skipped system modules) during `LoadAdjacentSceModules`. |

Consumed in `src/SharpEmu.CLI/Program.cs` and `src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs`.

## CPU backend / fault handling

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_LOG_ALL_IMPORTS` | unset | Log every import the guest calls. |
| `SHARPEMU_LOG_DISASM` | unset | Print a disasm prelude at fault time. |
| `SHARPEMU_LOG_DATA_REBIND` | unset | Trace `RebindImportedDataSymbols`. |
| `SHARPEMU_PROFILE_GUEST_RIP` | unset | Start the guest-RIP sampler (`DirectExecutionBackend.GuestSampler`). |
| `SHARPEMU_PERF_HLE` | unset | Perf counters for HLE dispatch. |
| `SHARPEMU_PERF_HLE_NODICT` | unset | Same, without the dispatch cache. |
| `SHARPEMU_LOG_VMEM` | unset | Tag `VMEM` log noise (memory subsystem). |
| `SHARPEMU_TRACE_GUEST_IMAGE_ADDRS` | unset | `GuestImageWriteTracker` lifetime trace. |
| `SHARPEMU_TRACE_GUEST_MEMORY_LIFETIME` | unset | `GuestImageWriteTracker` memory lifetime trace. |
| `SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT` | `2` | Cap on pooled native guest workers (1–64). |
| `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS` | unset | Use the inline `calli` fallback instead of the worker pool. |

Consumed in `src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend*.cs`.

## LLE bridges / libc

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_DISABLE_LLE_LIBC` | unset | Don't bridge guest libc imports (memcpy/memset/memmove/memcmp) to guest-native code. |
| `SHARPEMU_LLE_LIBC_SAFE_ONLY` | unset | Bridge only the LLE-safe subset. |
| `SHARPEMU_LLE_LIBC_ALL` | unset | Bridge every candidate libc import. |

## Lazy commit

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_LAZY_RESERVE_PRIME_MB` | `64` | Initial prime size for reserve-only large allocations. |

## GPU image CPU sync

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_GUEST_IMAGE_CPU_SYNC` | unset | Enable `GuestImageWriteTracker` (re-upload GPU surfaces when guest CPU writes them). |

## Bink 2 bridge

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_BINK_MODE` | `native` | `native` (default — decode via FFmpeg in-process), `guest` (leave decoding to the game's Bink), `dummy` (placeholder frame), or `ffmpeg` (spawn an `ffmpeg` subprocess). See [`bink2-bridge.md`](../bink2-bridge.md). |
| `SHARPEMU_FFMPEG_PATH` | unset | Override the path to the `ffmpeg` executable used by the `ffmpeg` Bink mode. |

## Audio

| Variable | Default | Purpose |
| --- | --- | --- |
| `SHARPEMU_AUDIO_LATENCY_MS` | `60` | `SdlHostAudio.TargetQueuedMilliseconds` — the queue depth before the guest is paced. |
| `SHARPEMU_LOG_AUDIO_QUEUE` | unset | Per-second queue diagnostics in `SdlHostAudio`. |
| `SHARPEMU_ALSA_DEVICE` | unset | Override the ALSA device used by `PosixAlsaAudioStream` (not selected by default). |

Consumed in `src/SharpEmu.HLE/Host/Sdl/SdlHostAudio.cs` and
`src/SharpEmu.HLE/Host/Posix/PosixAlsaAudioStream.cs`.

## Guest write watch

The full set of `SHARPEMU_WATCH_*` variables is documented in
[`guest-write-watch.md`](../guest-write-watch.md):

| Variable | Purpose |
| --- | --- |
| `SHARPEMU_WATCH_WRITE=0x<address>` | Log writes overlapping the 8-byte block at the address. |
| `SHARPEMU_WATCH_POOL_HEADER=1` | Monitor the pointer at offset `0x40`. |
| `SHARPEMU_WATCH_VALUE_PATTERN=1` | Log 8-byte writes with a specific lower-32-bit pattern. |
| `SHARPEMU_WATCH_VALUE1=1` | Log short writes of value `1` in the high guest range. |
| `SHARPEMU_WATCH_BULK_TORN=1` | Scan aligned 64-bit words in bulk writes for damaged/byte-shifted pointer patterns. |
| `SHARPEMU_WATCH_BULK_DEST_HI=0x<high-dword>` | Restrict bulk scans to a destination prefix. |

## MoltenVK / macOS

These are set by `Program.ConfigureMoltenVkDefaults` when not already in the
environment, so most users don't need to touch them:

| Variable | Purpose |
| --- | --- |
| `MVK_CONFIG_*` | Standard MoltenVK configuration. |
| `SHARPEMU_PRELOAD_MAC_VULKAN` | Internal — preload `libvulkan.1.dylib`/`libMoltenVK.dylib` from the base dir. |

## Internal

| Variable | Purpose |
| --- | --- |
| `SHARPEMU_MITIGATED_CHILD` | See above. |
| `SHARPEMU_LOG_NO_COLOR` | See above. |

## Related

- [Logging guide](../projects/logging.md)
- [`guest-write-watch.md`](../guest-write-watch.md)
- [`bink2-bridge.md`](../bink2-bridge.md)
- [Debugging guide](../guides/debugging.md)
