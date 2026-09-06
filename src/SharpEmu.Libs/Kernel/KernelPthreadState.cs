// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Public bridge that lets the CPU backend's vectored exception handler
/// service guest allocation requests (Hellboy's libScePosix node allocator
/// returns NULL for pool slots the real kernel provides; the caller stores
/// through the NULL pointer unconditionally and kills the process).
/// Registered by <see cref="KernelPthreadCompatExports"/> once a guest
/// address space exists.
/// </summary>
public static class GuestAllocationBridge
{
    /// <summary>Allocates a zeroed guest-memory block of <paramref name="size"/> bytes, or 0.</summary>
    public static Func<int, ulong>? RequestZeroed { get; set; }
}

internal static class KernelPthreadState
{
    private const int ThreadObjectSize = 0x1000;

    private static readonly ConcurrentDictionary<ulong, ThreadIdentity> Threads = new();
    private static readonly byte[] ZeroThreadObject = new byte[ThreadObjectSize];
    private static long _nextUniqueThreadId = 1;

    // Set by the CPU backend once a guest address space exists. Guest-visible
    // pthread_t values must be guest-memory object pointers, exactly like the
    // real kernel (and KytyPS5): scePthreadSelf/scePthreadCreate hand the
    // handle to guest code, and runtimes (Unity/Boehm stop-the-world,
    // Baselib) treat ScePthread as a structure they may read fields from.
    // Host-heap pointers (Marshal.AllocHGlobal) handed out as pthread_t used
    // to alias unrelated guest flexible-heap data when dereferenced through
    // the guest address space, producing wild pointers like
    // 0x41E4E00000002D3C and an Access Violation on Unity's
    // Loading.PreloadManager thread (Hellboy boot).
    internal static Func<int, ulong>? GuestThreadObjectAllocator { get; set; }

    // Optional post-allocation hook (set by KernelPthreadCompatExports once a
    // guest address space exists). Hellboy's libScePosix pthread wrappers load
    // a secondary thread structure with `mov r15,[self+0x58]` and then read
    // fields such as [r15+0x11C] (cancel-state checks). A fully zeroed object
    // leaves that pointer NULL and crashes; pointing it at a zeroed guest
    // block gives the guest well-defined memory, mirroring the kernel where
    // ScePthread fields always reference valid allocations.
    internal static Action<ulong>? GuestThreadObjectInitializer { get; set; }

    // Handles that were allocated as guest-memory objects (not host-heap
    // fallbacks). The retrofit pass must only touch these: writing through a
    // host-heap handle interpreted as a guest address would corrupt memory.
    private static readonly HashSet<ulong> _guestAllocatedHandles = new();

    [ThreadStatic]
    private static ulong _currentThreadHandle;

    [ThreadStatic]
    private static ulong _currentThreadUniqueId;

    internal readonly record struct ThreadIdentity(ulong UniqueId, string Name);

    internal static ulong GetCurrentThreadHandle()
    {
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        // Prefer the bound guest handle even when it is not yet in Threads.
        // Falling through to a synthetic ThreadStatic handle while a guest
        // thread is bound causes mutex owner mismatches (unlock PERM → hang).
        if (guestThreadHandle != 0)
        {
            EnsureGuestThreadIdentity(guestThreadHandle);
            return guestThreadHandle;
        }

        EnsureCurrentThreadRegistered();
        return _currentThreadHandle;
    }

    internal static ulong GetCurrentThreadUniqueId()
    {
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        if (guestThreadHandle != 0)
        {
            return EnsureGuestThreadIdentity(guestThreadHandle).UniqueId;
        }

        EnsureCurrentThreadRegistered();
        return _currentThreadUniqueId;
    }

    internal static ulong[] SnapshotGuestAllocatedThreadHandles()
    {
        lock (_guestAllocatedHandles)
        {
            return _guestAllocatedHandles.ToArray();
        }
    }

    internal static string DescribeThreadHandle(ulong threadHandle)
    {
        if (threadHandle == 0)
        {
            return "none";
        }

        return TryGetThreadIdentity(threadHandle, out var identity)
            ? $"0x{threadHandle:X16}('{identity.Name}')"
            : $"0x{threadHandle:X16}";
    }

    internal static ulong CreateThreadHandle(string name)
    {
        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        return AllocateThreadHandle(uniqueId, name);
    }

    internal static bool TryGetThreadIdentity(ulong threadHandle, out ThreadIdentity identity)
    {
        return Threads.TryGetValue(threadHandle, out identity);
    }

    internal static bool TryGetCurrentThreadIdentity(
        out ulong threadHandle,
        out ThreadIdentity identity)
    {
        threadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        if (threadHandle != 0 && TryGetThreadIdentity(threadHandle, out identity))
        {
            return true;
        }

        threadHandle = _currentThreadHandle;
        if (threadHandle != 0 && TryGetThreadIdentity(threadHandle, out identity))
        {
            return true;
        }

        identity = default;
        return false;
    }

    private static ThreadIdentity EnsureGuestThreadIdentity(ulong guestThreadHandle)
    {
        if (Threads.TryGetValue(guestThreadHandle, out var existing))
        {
            return existing;
        }

        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        var identity = new ThreadIdentity(uniqueId, $"Guest-0x{guestThreadHandle:X}");
        return Threads.GetOrAdd(guestThreadHandle, identity);
    }

    private static void EnsureCurrentThreadRegistered()
    {
        if (_currentThreadHandle != 0)
        {
            return;
        }

        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        var name = $"Thread-{uniqueId:X}";
        _currentThreadHandle = AllocateThreadHandle(uniqueId, name);
        _currentThreadUniqueId = uniqueId;
    }

    private static ulong AllocateThreadHandle(ulong uniqueId, string name)
    {
        // Prefer a guest-memory object so the handle is a plain guest pointer,
        // exactly like the kernel's ScePthread objects on real hardware. Guest
        // code may dereference the handle through the guest address space; a
        // host-heap pointer there aliases unrelated guest data (Hellboy
        // PreloadManager AV). The zeroed object also makes field reads
        // well-defined instead of aliasing live guest allocations.
        ulong handle = 0;
        var allocator = GuestThreadObjectAllocator;
        if (allocator is not null)
        {
            try
            {
                handle = allocator(ThreadObjectSize);
            }
            catch
            {
                handle = 0;
            }
        }

        if (handle == 0)
        {
            // Fallback identity when guest-memory allocation is unavailable
            // (no address space yet, e.g. very early host-only threads).
            var pointer = Marshal.AllocHGlobal(ThreadObjectSize);
            Marshal.Copy(ZeroThreadObject, 0, pointer, ThreadObjectSize);
            handle = unchecked((ulong)pointer.ToInt64());
        }
        else
        {
            lock (_guestAllocatedHandles)
            {
                _guestAllocatedHandles.Add(handle);
            }
        }

        Threads[handle] = new ThreadIdentity(uniqueId, string.IsNullOrWhiteSpace(name) ? $"Thread-{uniqueId:X}" : name);

        try
        {
            GuestThreadObjectInitializer?.Invoke(handle);
        }
        catch
        {
            // Initialization is best-effort; the zeroed object remains valid.
        }

        return handle;
    }
}
