// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Zero-marshaling leaf import handlers (PERFORMANCE_PLAN, Kyty-style direct
/// ABI calls). Registered as raw function pointers in
/// <see cref="NativeFastPathRegistry"/>; DirectExecutionBackend patches the
/// guest PLT stub to call the pointer directly instead of the full managed
/// dispatch (register arg-pack + bookkeeping), turning a ~0.5-3 µs dispatch
/// into a ~tens-of-ns native call. Only non-blocking, guest-memory-safe leaf
/// semantics belong here: scePthreadGetspecific (65M calls per Mortal Shell
/// boot), scePthreadSelf (11M), gettimeofday (7M).
/// </summary>
public static class NativeFastPathRegistration
{
    private static readonly bool _disabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_NATIVE_FASTPATH"), "1", StringComparison.Ordinal);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static ulong FastGetspecific(ulong key)
    {
        if (key > int.MaxValue)
        {
            return 0;
        }

        var handle = KernelPthreadState.GetCurrentThreadHandle();
        return handle == 0
            ? 0
            : KernelPthreadExtendedCompatExports.GetSpecificFast(handle, unchecked((int)key));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static ulong FastPthreadSelf() => KernelPthreadState.GetCurrentThreadHandle();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe ulong FastGettimeofday(ulong timeAddress, ulong timezoneAddress)
    {
        var now = DateTimeOffset.UtcNow;
        if (timeAddress != 0)
        {
            *(long*)timeAddress = now.ToUnixTimeSeconds();
            ((long*)timeAddress)[1] = (now.Ticks % TimeSpan.TicksPerSecond) / 10;
        }

        if (timezoneAddress != 0)
        {
            *(int*)timezoneAddress = 0;
            ((int*)timezoneAddress)[1] = 0;
        }

        return 0;
    }

    public static unsafe void Register()
    {
        if (_disabled)
        {
            return;
        }

        NativeFastPathRegistry.Register("eoht7mQOCmo", (nint)(delegate* unmanaged[Cdecl]<ulong, ulong>)&FastGetspecific); // scePthreadGetspecific
        NativeFastPathRegistry.Register("0-KXaS70xy4", (nint)(delegate* unmanaged[Cdecl]<ulong, ulong>)&FastGetspecific); // pthread_getspecific
        NativeFastPathRegistry.Register("aI+OeCz8xrQ", (nint)(delegate* unmanaged[Cdecl]<ulong>)&FastPthreadSelf);        // scePthreadSelf
        NativeFastPathRegistry.Register("EotR8a3ASf4", (nint)(delegate* unmanaged[Cdecl]<ulong>)&FastPthreadSelf);        // pthread_self
        NativeFastPathRegistry.Register("n88vx3C5nW8", (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong>)&FastGettimeofday); // gettimeofday
    }
}
