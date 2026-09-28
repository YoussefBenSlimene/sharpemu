// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

// KytyPS5 libNet.cpp LibHttp parity for sceHttpUriParse / Escape / Build
// (called by Mortal Shell's online subsystem; previously unresolved).
public sealed class HttpUriExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Url = Base + 0x100;
    private const ulong Element = Base + 0x400;
    private const ulong Pool = Base + 0x500;
    private const ulong Require = Base + 0x900;
    private const ulong Out = Base + 0xA00;

    [Fact]
    public void Parse_FullUrl_SplitsEveryPart()
    {
        Assert.Equal(0, HttpUriExports.TryParse("https://user:pw@example.com:8443/a/b?x=1#frag", out var p));
        Assert.False(p.Opaque);
        Assert.Equal("https", p.Scheme);
        Assert.Equal("user", p.Username);
        Assert.Equal("pw", p.Password);
        Assert.Equal("example.com", p.Hostname);
        Assert.Equal((ushort)8443, p.Port);
        Assert.Equal("/a/b", p.Path);
        Assert.Equal("?x=1", p.Query);
        Assert.Equal("#frag", p.Fragment);
    }

    [Theory]
    [InlineData("1http://x")]
    [InlineData("http//x")]
    [InlineData("http://x:")]
    [InlineData("http://x:99999")]
    [InlineData("http://[::1")]
    public void Parse_InvalidUrl_ReturnsInvalidUrl(string url) =>
        Assert.Equal(HttpUriExports.HttpErrorInvalidUrl, HttpUriExports.TryParse(url, out _));

    [Fact]
    public void Parse_Mailto_IsOpaque()
    {
        Assert.Equal(0, HttpUriExports.TryParse("mailto:a@b", out var p));
        Assert.True(p.Opaque);
        Assert.Null(p.Hostname);
        Assert.Equal("a@b", p.Path);
    }

    [Fact]
    public void BuildOfParse_RoundTrips()
    {
        const string url = "https://user:pw@example.com:8443/a/b?x=1#frag";
        Assert.Equal(0, HttpUriExports.TryParse(url, out var p));
        Assert.Equal(url, HttpUriExports.Build(p));
    }

    [Fact]
    public void Escape_KeepsUnreservedAndPercentEncodesTheRest() =>
        Assert.Equal("a-b_c.d~e%20%2F%C3%A9", HttpUriExports.Escape(Encoding.UTF8.GetBytes("a-b_c.d~e /é")));

    [Fact]
    public void GuestParse_WritesElementPoolAndRequire()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteString(memory, Url, "http://host:81/p?q");

        ctx[CpuRegister.Rdi] = Element;
        ctx[CpuRegister.Rsi] = Url;
        ctx[CpuRegister.Rdx] = Pool;
        ctx[CpuRegister.Rcx] = Require;
        ctx[CpuRegister.R8] = 0x100;
        Assert.Equal(0, HttpUriExports.HttpUriParse(ctx));

        // "http\0host\0/p\0?q\0" = 5 + 5 + 3 + 3
        Assert.Equal(16UL, ReadU64(memory, Require));
        Assert.Equal(0u, ReadU32(memory, Element));
        Assert.Equal("http", ReadString(memory, ReadU64(memory, Element + 0x08)));
        Assert.Equal(0UL, ReadU64(memory, Element + 0x10));
        Assert.Equal("host", ReadString(memory, ReadU64(memory, Element + 0x20)));
        Assert.Equal("/p", ReadString(memory, ReadU64(memory, Element + 0x28)));
        Assert.Equal("?q", ReadString(memory, ReadU64(memory, Element + 0x30)));
        Assert.Equal(81, ReadU16(memory, Element + 0x40));

        // Build back from the guest element.
        ctx[CpuRegister.Rdi] = Out;
        ctx[CpuRegister.Rsi] = Require;
        ctx[CpuRegister.Rdx] = 0x100;
        ctx[CpuRegister.Rcx] = Element;
        ctx[CpuRegister.R8] = 0;
        Assert.Equal(0, HttpUriExports.HttpUriBuild(ctx));
        Assert.Equal("http://host:81/p?q", ReadString(memory, Out));
    }

    [Fact]
    public void GuestParse_PoolTooSmall_ReturnsOutOfMemory()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteString(memory, Url, "http://host/p");
        ctx[CpuRegister.Rdi] = Element;
        ctx[CpuRegister.Rsi] = Url;
        ctx[CpuRegister.Rdx] = Pool;
        ctx[CpuRegister.Rcx] = Require;
        ctx[CpuRegister.R8] = 2;
        Assert.Equal(HttpUriExports.HttpErrorOutOfMemory, HttpUriExports.HttpUriParse(ctx));
    }

    private static void WriteString(FakeCpuMemory memory, ulong address, string text) =>
        Assert.True(memory.TryWrite(address, Encoding.UTF8.GetBytes(text + "\0")));

    private static string ReadString(FakeCpuMemory memory, ulong address)
    {
        var buffer = new byte[256];
        Assert.True(memory.TryRead(address, buffer));
        return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
    }

    private static ulong ReadU64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        Assert.True(memory.TryRead(address, b));
        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    private static uint ReadU32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        Assert.True(memory.TryRead(address, b));
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static int ReadU16(FakeCpuMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[2];
        Assert.True(memory.TryRead(address, b));
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }
}
