// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Tracks whether the emulated GPU still has work the guest is likely to be
/// polling for, and lets a polling guest thread wait for it to drain.
///
/// Why this exists (Quake II PPSA09477, Q9): engines bound their "wait for the
/// GPU" polls by an <em>iteration count</em>, not by wall time. KEX's
/// <c>kexRHIStateGnm::StartFrame</c> (eboot <c>0x53ABD0</c>) polls the previous
/// frame's context label (written by the frame's end-of-pipe
/// <c>sceAgcCbReleaseMem</c>) at most 500 times with <c>sceKernelUsleep(1)</c>
/// between polls and then raises
/// <c>Com_Error("GPU hanged while waiting for m_pContextLabel to be cleared")</c>,
/// which shows the ERROR dialog and calls <c>abort()</c>. On hardware each
/// <c>usleep(1)</c> is a real kernel sleep and the GPU retires the frame long
/// before the budget runs out. In SharpEmu the label is written later — after
/// the submission is parsed (it can sit suspended behind an earlier
/// WAIT_REG_MEM) and after the Vulkan ordered-action queue reaches it — while
/// <c>usleep(1)</c> is one host yield, so the budget elapses in microseconds.
///
/// "Busy" is the sum of two things:
/// <list type="bullet">
///   <item>label writes (release_mem / write_data / dma_data side effects)
///   queued to the Vulkan ordered-action queue but not yet performed, and</item>
///   <item>guest command buffers submitted but not yet fully parsed
///   (queued behind, or suspended on, a GPU wait) — their label writes are
///   not even queued yet, which is why counting only the first kind was not
///   enough (first fix, 66db254: the map-load frame still aborted).</item>
/// </list>
/// The sum lives in unmanaged memory so the native <c>sceKernelUsleep</c>
/// intrinsic can test it without leaving native code: zero keeps the
/// historical fast path; non-zero hands the call to the HLE handler.
/// </summary>
internal static unsafe class GuestGpuProgress
{
    /// <summary>Per-call wait cap for the first polls of a burst.</summary>
    internal const int DefaultMaxWaitMilliseconds = 8;

    /// <summary>Per-call wait cap once a call site is clearly a poll loop.</summary>
    internal const int DefaultLongWaitMilliseconds = 50;

    /// <summary>
    /// If the GPU stays busy with no progress at all for this long, stop
    /// waiting: a wedged queue must not turn every later short sleep into a
    /// long one. Generous because a first-run map load compiles many
    /// pipelines inside one frame.
    /// </summary>
    internal const int StallGuardMilliseconds = 20000;

    private static readonly object Gate = new();
    private static readonly int* BusyPointer =
        (int*)NativeMemory.AllocZeroed((nuint)sizeof(int));
    private static int _pendingLabelWrites;
    private static int _inFlightSubmissions;
    private static long _completedLabelWrites;
    private static long _progressVersion;
    private static long _lastProgressTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Address of the 32-bit busy counter, for native code. Valid for the
    /// life of the process.
    /// </summary>
    public static nint PendingCounterAddress => (nint)BusyPointer;

    /// <summary>Label writes + unparsed submissions.</summary>
    public static int Busy => Volatile.Read(ref *BusyPointer);

    /// <summary>Queued-but-not-performed label writes.</summary>
    public static int PendingLabelWrites => Volatile.Read(ref _pendingLabelWrites);

    /// <summary>Submitted-but-not-fully-parsed guest command buffers.</summary>
    public static int InFlightSubmissions => Volatile.Read(ref _inFlightSubmissions);

    public static long CompletedLabelWrites => Interlocked.Read(ref _completedLabelWrites);

    /// <summary>Records that a label write was queued to the emulated GPU.</summary>
    public static void BeginLabelWrite()
    {
        var wasIdle = Busy == 0;
        Interlocked.Increment(ref _pendingLabelWrites);
        PublishBusy(wasIdle);
    }

    /// <summary>
    /// Records that a queued label write has been performed and wakes any
    /// guest thread waiting in <see cref="WaitForIdle"/>.
    /// </summary>
    public static void EndLabelWrite()
    {
        var remaining = Interlocked.Decrement(ref _pendingLabelWrites);
        if (remaining < 0)
        {
            // An unmatched completion must never wedge the counter negative.
            Interlocked.CompareExchange(ref _pendingLabelWrites, 0, remaining);
        }

        Interlocked.Increment(ref _completedLabelWrites);
        NoteProgressAndWake();
    }

    /// <summary>
    /// Publishes the number of guest command buffers that are queued or being
    /// parsed (including ones suspended on a GPU wait). Recomputed from the
    /// submission queues under their lock, so it cannot leak.
    /// </summary>
    public static void SetInFlightSubmissions(int count)
    {
        if (count < 0)
        {
            count = 0;
        }

        var previous = Interlocked.Exchange(ref _inFlightSubmissions, count);
        if (previous == count)
        {
            return;
        }

        if (count < previous)
        {
            NoteProgressAndWake();
        }
        else
        {
            PublishBusy(wasIdle: previous == 0 && PendingLabelWrites == 0);
        }
    }

    /// <summary>
    /// Blocks until the emulated GPU makes progress (a queued label write is
    /// performed or a submission finishes parsing), becomes idle,
    /// <paramref name="maxWaitMilliseconds"/> elapses, or looks wedged.
    /// Waking on <em>any</em> progress keeps the cost low while the GPU is
    /// streaming work (many completions per frame), and turns into a real
    /// sleep only while the GPU is stuck on something slow (first-use pipeline
    /// compilation) — exactly when a bounded guest poll loop would otherwise
    /// exhaust its budget. With <paramref name="untilIdle"/> it ignores
    /// intermediate progress and waits for the GPU to drain completely (used
    /// once a call site has proven to be a long GPU poll). Returns <c>true</c>
    /// when it waited.
    /// </summary>
    public static bool WaitForIdle(int maxWaitMilliseconds, bool untilIdle = false)
    {
        if (maxWaitMilliseconds <= 0 || Busy <= 0 || IsStalled())
        {
            return false;
        }

        var startVersion = Interlocked.Read(ref _progressVersion);
        var deadline = Stopwatch.GetTimestamp() +
                       (maxWaitMilliseconds * Stopwatch.Frequency / 1000);
        lock (Gate)
        {
            while (Busy > 0 &&
                   (untilIdle || Interlocked.Read(ref _progressVersion) == startVersion))
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

    /// <summary>Kept for callers/tests written against the first fix.</summary>
    public static bool WaitForCatchUp(int maxWaitMilliseconds) =>
        WaitForIdle(maxWaitMilliseconds);

    public static string Describe() =>
        $"busy={Busy} label_writes={PendingLabelWrites} " +
        $"submissions={InFlightSubmissions} completed_writes={CompletedLabelWrites} " +
        $"ms_since_progress={(Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp)) * 1000 / Stopwatch.Frequency}";

    private static void PublishBusy(bool wasIdle)
    {
        if (wasIdle)
        {
            // Idle -> busy: the stall guard measures time since the GPU was
            // last idle or last made progress, not time since boot.
            Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
        }

        Volatile.Write(
            ref *BusyPointer,
            Math.Max(0, Volatile.Read(ref _pendingLabelWrites)) +
            Math.Max(0, Volatile.Read(ref _inFlightSubmissions)));
    }

    private static void NoteProgressAndWake()
    {
        Interlocked.Increment(ref _progressVersion);
        Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
        PublishBusy(wasIdle: false);
        lock (Gate)
        {
            Monitor.PulseAll(Gate);
        }
    }

    private static bool IsStalled()
    {
        var idleTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
        return idleTicks > StallGuardMilliseconds * Stopwatch.Frequency / 1000;
    }

    /// <summary>Test hook: forget all in-flight work.</summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref _pendingLabelWrites, 0);
        Volatile.Write(ref _inFlightSubmissions, 0);
        Volatile.Write(ref *BusyPointer, 0);
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
