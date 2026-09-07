// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpTrophy2ExportsTests
{
    private const ulong Base = 0x2_0000_0000;

    private readonly FakeCpuMemory _memory = new(Base, 0x4000);
    private readonly CpuContext _ctx;

    public NpTrophy2ExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void GetTrophyInfo_FillsDummyBronzeTrophy()
    {
        const ulong details = Base + 0x100;
        const ulong data = Base + 0x800;
        _ctx[CpuRegister.Rdx] = 7;
        _ctx[CpuRegister.Rcx] = details;
        _ctx[CpuRegister.R8] = data;

        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyInfo(_ctx));

        Span<byte> id = stackalloc byte[4];
        Assert.True(_memory.TryRead(details, id));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(id));
        Assert.True(_memory.TryRead(details + 4, id));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(id));

        Span<byte> name = stackalloc byte[6];
        Assert.True(_memory.TryRead(details + 32, name));
        Assert.Equal("Trophy", Encoding.ASCII.GetString(name));

        Assert.True(_memory.TryRead(data, id));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(id));
    }
}
