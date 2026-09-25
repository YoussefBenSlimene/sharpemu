// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Tracks GPU label writes (RELEASE_MEM / WRITE_DATA / DMA_DATA end-of-pipe
/// side effects) that the guest has submitted but the emulated GPU has not
/// performed yet, and lets a guest thread that is polling for them wait for
/// the GPU to catch up.
///
/// Why this exists (Quake II PPSA09477, Q4): engines bound their "wait for the
/// GPU" polls by an <em>iteration count</em>, not by wall time. KEX's
/// <c>kexRHIStateGnm::StartFrame</c> (eboot <c>0x53ABD0</c>) polls the previous
/// frame's context label at most 500 times with <c>sceKernelUsleep(1)</c>
/// between polls and then raises
/// <c>Com_Error("GPU hanged while waiting for m_pContextLabel to be cleared")</c>,
/// which shows the ERROR dialog and calls <c>abort()</c>. On hardware each
/// <c>usleep(1)</c> is a real kernel sleep and the GPU retires the frame long
/// before the budget runs out. In SharpEmu the label is written later, from
/// the Vulkan ordered-action queue, while <c>usleep(1)</c> is a single host
/// yield — so the whole 500-poll budget elapses in microseconds and the first
/// slow frame (pipeline compilation) is reported as a GPU hang.
///
/// The pending count also lives in unmanaged memory so the native
/// <c>sceKernelUsleep</c> intrinsic can test it without leaving native code:
/// zero keeps the historical fast path; non-zero hands the call to the HLE
/// handler, which decides whether the caller is a tight poll loop and, if so,
/// waits here.
/// </summary>
internal static unsafe class GuestGpuProgress
{
    /// <summary>Default cap for one catch-up wait.</summary>
    internal const int DefaultMaxWaitMilliseconds = 8;

    /// <summary>
    /// If nothing has completed for this long while writes are pending, stop
    /// waiting: a dropped ordered action (device lost, shutdown) must not turn
    /// every later <c>usleep(1)</c> into a multi-millisecond sleep.
    /// </summary>
    internal const int StallGuardMilliseconds = 3000;

    private static readonly object Gate = new();
    private static readonly int* PendingPointer =
        (int*)NativeMemory.AllocZeroed((nuint)sizeof(int));
    private static long _submitted;
    private static long _completed;
    private static long _lastProgressTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Address of the 32-bit pending-label-write counter, for native code.
    /// Valid for the life of the process.
    /// </summary>
    public static nint PendingCounterAddress => (nint)PendingPointer;

    /// <summary>Number of submitted-but-not-yet-performed label writes.</summary>
    public static int PendingLabelWrites => Volatile.Read(ref *PendingPointer);

    public static long SubmittedLabelWrites => Interlocked.Read(ref _submitted);

    public static long CompletedLabelWrites => Interlocked.Read(ref _completed);

    /// <summary>Records that a label write was submitted to the emulated GPU.</summary>
    public static void BeginLabelWrite()
    {
        Interlocked.Increment(ref _submitted);
        if (Interlocked.Increment(ref *PendingPointer) == 1)
        {
            // Idle -> busy: the stall guard measures time since the GPU was
            // last idle or last completed something, not time since boot.
            Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Records that a submitted label write has been performed and wakes any
    /// guest thread waiting in <see cref="WaitForCatchUp"/>.
    /// </summary>
    public static void EndLabelWrite()
    {
        var remaining = Interlocked.Decrement(ref *PendingPointer);
        if (remaining < 0)
        {
            // An unmatched completion must never wedge the counter negative.
            Interlocked.CompareExchange(ref *PendingPointer, 0, remaining);
        }

        Interlocked.Increment(ref _completed);
        Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
        lock (Gate)
        {
            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>
    /// Blocks until the emulated GPU has performed every label write that was
    /// submitted before this call, or <paramref name="maxWaitMilliseconds"/>
    /// elapses, or the GPU looks stalled. Returns <c>true</c> when it waited.
    /// </summary>
    public static bool WaitForCatchUp(int maxWaitMilliseconds)
    {
        if (maxWaitMilliseconds <= 0 || PendingLabelWrites <= 0 || IsStalled())
        {
            return false;
        }

        var target = Interlocked.Read(ref _submitted);
        var deadline = Stopwatch.GetTimestamp() +
                       (maxWaitMilliseconds * Stopwatch.Frequency / 1000);
        lock (Gate)
        {
            while (Interlocked.Read(ref _completed) < target &&
                   PendingLabelWrites > 0)
            {
                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    break;
                }

                var remainingMs = (int)Math.Max(
                    1,
                    remainingTicks * 1000 / Stopwatch.Frequency);
                Monitor.Wait(Gate, remainingMs);
            }
        }

        return true;
    }

    private static bool IsStalled()
    {
        var idleTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
        return idleTicks > StallGuardMilliseconds * Stopwatch.Frequency / 1000;
    }

    /// <summary>Test hook: forget all in-flight writes.</summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref *PendingPointer, 0);
        Interlocked.Exchange(ref _completed, Interlocked.Read(ref _submitted));
        Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
        lock (Gate)
        {
            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>Test hook: pretend the GPU last made progress long ago.</summary>
    internal static void AgeLastProgressForTests(int milliseconds)
    {
        Volatile.Write(
            ref _lastProgressTimestamp,
            Stopwatch.GetTimestamp() - (milliseconds * Stopwatch.Frequency / 1000));
    }
}
