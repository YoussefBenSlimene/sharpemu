// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.NpGameIntent;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpGameIntentExportsTests
{
    private const ulong Base = 0x2_0000_0000;
    private const int IntentNotFound = unchecked((int)0x80553806);
    private const int ValueNotFound = unchecked((int)0x80553807);
    private const int InvalidArgument = unchecked((int)0x80553804);

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;

    public NpGameIntentExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ReceiveIntent_NullPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidArgument, NpGameIntentExports.NpGameIntentReceiveIntent(_ctx));
    }

    [Fact]
    public void ReceiveIntent_ClearsUserIdAndReportsNotFound()
    {
        const ulong info = Base + 0x100;
        _ctx[CpuRegister.Rdi] = info;

        Assert.Equal(IntentNotFound, NpGameIntentExports.NpGameIntentReceiveIntent(_ctx));

        Span<byte> userId = stackalloc byte[4];
        Assert.True(_memory.TryRead(info + 8, userId));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(userId));
        Span<byte> typeByte = stackalloc byte[1];
        Assert.True(_memory.TryRead(info + 12, typeByte));
        Assert.Equal(0, typeByte[0]);
    }

    [Fact]
    public void GetPropertyValueString_WritesEmptyAndReportsValueNotFound()
    {
        const ulong data = Base + 0x200;
        const ulong key = Base + 0x300;
        const ulong value = Base + 0x400;
        Assert.True(_memory.TryWrite(key, "activityId\0"u8.ToArray()));
        Assert.True(_memory.TryWrite(value, [(byte)'x']));

        _ctx[CpuRegister.Rdi] = data;
        _ctx[CpuRegister.Rsi] = key;
        _ctx[CpuRegister.Rdx] = value;
        _ctx[CpuRegister.Rcx] = 32;

        Assert.Equal(ValueNotFound, NpGameIntentExports.NpGameIntentGetPropertyValueString(_ctx));
        Span<byte> written = stackalloc byte[1];
        Assert.True(_memory.TryRead(value, written));
        Assert.Equal(0, written[0]);
    }
}
