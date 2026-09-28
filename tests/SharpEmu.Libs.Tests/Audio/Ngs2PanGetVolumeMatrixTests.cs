// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

// KytyPS5 ngs2.cpp parity: sceNgs2PanGetVolumeMatrix writes num_params rows of
// `channels` floats, unit gain on channel 0 (Quake II, 7.1 format = 8 channels).
public sealed class Ngs2PanGetVolumeMatrixTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Out = Base + 0x100;

    [Fact]
    public void Writes71MatrixWithUnitGainOnFirstChannel()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(Out, Enumerable.Repeat((byte)0xFF, 2 * 8 * 4).ToArray()));

        ctx[CpuRegister.Rdx] = 2; // num_params
        ctx[CpuRegister.Rcx] = 8; // 7.1
        ctx[CpuRegister.R8] = Out;
        Assert.Equal(0, Ngs2Exports.Ngs2PanGetVolumeMatrix(ctx));

        var bytes = new byte[2 * 8 * 4];
        Assert.True(memory.TryRead(Out, bytes));
        for (var p = 0; p < 2; p++)
        {
            for (var c = 0; c < 8; c++)
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan((p * 8 + c) * 4));
                Assert.Equal(c == 0 ? 1.0f : 0.0f, value);
            }
        }
    }

    [Fact]
    public void ZeroParams_IsOkWithoutOutput()
    {
        var ctx = new CpuContext(new FakeCpuMemory(Base, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.R8] = 0;
        Assert.Equal(0, Ngs2Exports.Ngs2PanGetVolumeMatrix(ctx));
    }
}
