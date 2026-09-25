// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// Quake II (PPSA09477) Q4: KEX polls the frame's context label 500 times
/// with usleep(1) between polls, then reports "GPU hanged" and aborts. The
/// emulated GPU writes that label asynchronously, so short sleeps in a poll
/// loop must give the GPU time to catch up while label writes are pending,
/// and must stay free when nothing is pending.
/// </summary>
[Collection(GpuAwareUsleepTests.CollectionName)]
public sealed class GpuAwareUsleepTests : IDisposable
{
    public const string CollectionName = "GuestGpuProgress (process-global)";

    public GpuAwareUsleepTests() => GuestGpuProgress.ResetForTests();

    public void Dispose() => GuestGpuProgress.ResetForTests();

    [Fact]
    public void PendingCounter_TracksBeginAndEnd()
    {
        Assert.Equal(0, GuestGpuProgress.PendingLabelWrites);
        GuestGpuProgress.BeginLabelWrite();
        GuestGpuProgress.BeginLabelWrite();
        Assert.Equal(2, GuestGpuProgress.PendingLabelWrites);
        Assert.Equal(2, GuestGpuProgress.Busy);
        unsafe
        {
            Assert.Equal(2, *(int*)GuestGpuProgress.PendingCounterAddress);
        }

        GuestGpuProgress.EndLabelWrite();
        GuestGpuProgress.EndLabelWrite();
        Assert.Equal(0, GuestGpuProgress.PendingLabelWrites);
    }

    [Fact]
    public void UnmatchedEnd_DoesNotGoNegative()
    {
        GuestGpuProgress.EndLabelWrite();
        Assert.Equal(0, GuestGpuProgress.PendingLabelWrites);
    }

    [Fact]
    public void WaitForCatchUp_ReturnsImmediatelyWhenIdle()
    {
        var sw = Stopwatch.StartNew();
        Assert.False(GuestGpuProgress.WaitForCatchUp(50));
        Assert.True(sw.ElapsedMilliseconds < 25);
    }

    [Fact]
    public void WaitForCatchUp_WakesWhenTheGpuCompletesTheWrite()
    {
        GuestGpuProgress.BeginLabelWrite();
        using var completer = Task.Run(async () =>
        {
            await Task.Delay(20);
            GuestGpuProgress.EndLabelWrite();
        });

        var sw = Stopwatch.StartNew();
        Assert.True(GuestGpuProgress.WaitForCatchUp(2000));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"waited {sw.ElapsedMilliseconds} ms");
        completer.Wait();
        Assert.Equal(0, GuestGpuProgress.PendingLabelWrites);
    }

    [Fact]
    public void WaitForCatchUp_IsBoundedByTheCap()
    {
        GuestGpuProgress.BeginLabelWrite();
        var sw = Stopwatch.StartNew();
        Assert.True(GuestGpuProgress.WaitForCatchUp(30));
        Assert.InRange(sw.ElapsedMilliseconds, 20, 1000);
    }

    [Fact]
    public void WaitForCatchUp_SkipsWhenTheGpuLooksStalled()
    {
        GuestGpuProgress.BeginLabelWrite();
        GuestGpuProgress.AgeLastProgressForTests(GuestGpuProgress.StallGuardMilliseconds + 500);
        var sw = Stopwatch.StartNew();
        Assert.False(GuestGpuProgress.WaitForCatchUp(200));
        Assert.True(sw.ElapsedMilliseconds < 100);
    }

    [Fact]
    public void BoundedPollLoop_SurvivesAnAsynchronousLabelWrite()
    {
        // Model of kexRHIStateGnm::StartFrame: 500 x usleep(1) polls of a
        // label that the emulated GPU clears ~60 ms later (a slow frame, e.g.
        // pipeline compilation). Without the GPU-aware wait the loop exhausts
        // its budget in microseconds and the game aborts.
        var context = new CpuContext(new FakeCpuMemory(0x1000_0000, 0x1000), Generation.Gen5);
        var label = 1;
        GuestGpuProgress.BeginLabelWrite();
        using var gpu = Task.Run(async () =>
        {
            await Task.Delay(60);
            Volatile.Write(ref label, 0);
            GuestGpuProgress.EndLabelWrite();
        });

        var budget = 500;
        while (Volatile.Read(ref label) != 0)
        {
            Assert.True(--budget > 0, "poll budget exhausted: GPU hanged");
            context[CpuRegister.Rdi] = 1;
            KernelRuntimeCompatExports.KernelUsleep(context);
        }

        gpu.Wait();
    }

    [Fact]
    public void InFlightSubmissions_CountAsBusy()
    {
        GuestGpuProgress.SetInFlightSubmissions(3);
        Assert.Equal(3, GuestGpuProgress.Busy);
        GuestGpuProgress.BeginLabelWrite();
        Assert.Equal(4, GuestGpuProgress.Busy);
        unsafe
        {
            Assert.Equal(4, *(int*)GuestGpuProgress.PendingCounterAddress);
        }

        GuestGpuProgress.EndLabelWrite();
        GuestGpuProgress.SetInFlightSubmissions(0);
        Assert.Equal(0, GuestGpuProgress.Busy);
    }

    [Fact]
    public void BoundedPollLoop_SurvivesALabelBehindAnUnparsedSubmission()
    {
        // Quake II map-load frame (second user log): the end-of-frame
        // release_mem sits in a submission that is still suspended behind a
        // GPU wait, so no label write is queued yet. The frame takes ~400 ms
        // (pipeline compilation) - far longer than 500 x usleep(1) of yields.
        var context = new CpuContext(new FakeCpuMemory(0x1000_0000, 0x1000), Generation.Gen5);
        var label = 0;
        GuestGpuProgress.SetInFlightSubmissions(1);
        using var gpu = Task.Run(async () =>
        {
            await Task.Delay(400);
            GuestGpuProgress.BeginLabelWrite();   // parse reaches release_mem
            GuestGpuProgress.SetInFlightSubmissions(0);
            await Task.Delay(20);                 // ordered action runs
            Volatile.Write(ref label, 1);
            GuestGpuProgress.EndLabelWrite();
        });

        var budget = 500;
        while (Volatile.Read(ref label) == 0)
        {
            Assert.True(--budget > 0, "poll budget exhausted: GPU hanged");
            context[CpuRegister.Rdi] = 1;
            KernelRuntimeCompatExports.KernelUsleep(context);
        }

        gpu.Wait();
    }

    [Fact]
    public void ShortSleep_StaysFastWhenNoGpuWorkIsPending()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1000_0000, 0x1000), Generation.Gen5);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            context[CpuRegister.Rdi] = 1;
            KernelRuntimeCompatExports.KernelUsleep(context);
        }

        Assert.True(sw.ElapsedMilliseconds < 500, $"500 idle usleep(1) took {sw.ElapsedMilliseconds} ms");
    }
}

[CollectionDefinition(GpuAwareUsleepTests.CollectionName, DisableParallelization = true)]
public sealed class GpuAwareUsleepCollection
{
}
