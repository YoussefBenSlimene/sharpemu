// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE;

/// <summary>
/// Registry of NID → native function pointer for the zero-marshaling import
/// fast path (Kyty-style: the guest's PLT slot jumps straight to native code
/// with SysV args already in registers). SharpEmu.Libs registers handlers for
/// its hottest leaf exports; the CPU backend patches those imports to a tiny
/// 4-arg call shim instead of the full managed dispatch (register spill,
/// loop-guard bookkeeping, state tracking). Handlers must be non-blocking,
/// allocation-free on the hot path, and must never fault.
/// </summary>
public static class NativeFastPathRegistry
{
    private static readonly ConcurrentDictionary<string, nint> _handlers = new(StringComparer.Ordinal);

    public static void Register(string nid, nint handler) => _handlers[nid] = handler;

    public static bool TryGet(string nid, out nint handler) => _handlers.TryGetValue(nid, out handler);
}
