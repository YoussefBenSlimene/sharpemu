// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Console;

public sealed class EmuConsoleTests
{
    [Theory]
    [InlineData("[LOADER] Segment 0: VAddr=0x0000000800000000")]
    [InlineData("[LOADER][TRACE] ImportCtx#1033: nid=tsvEmnenz48")]
    [InlineData("[LOADER][INFO] Vulkan candidate: NVIDIA GeForce RTX 3050")]
    [InlineData("[LOADER][WARN] Import#4607 result: -1 (eV9wAD2riIA)")]
    [InlineData("[BOOT] hle-warm completed in 1.5s")]
    [InlineData("")]
    public void DeveloperChatter_IsHiddenFromTheTerminal(string line)
    {
        Assert.False(EmuConsole.ShouldShowOnTerminal(line));
    }

    [Theory]
    [InlineData("[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work.")]
    [InlineData("[ERROR][SharpEmu.CLI] Program.cs:1 EBOOT file was not found")]
    [InlineData("[LOADER][INFO] abort() called by guest - terminating")]
    [InlineData("[DEBUG][PRINF] Argument Count = 1")]
    [InlineData("[LOADER][INFO]   abort frame#0: rbp=0x00007FFFF01FD8C0 ret=0x000000080053BE5D")]
    [InlineData("[LOADER][INFO]   param+0x98 -> 0x000802AB1650 text=\"ERROR\"")]
    [InlineData("[LOADER][ERROR] Stall guest-thread: handle=0x1")]
    public void ErrorsAndGuestPrintf_ReachTheTerminal(string line)
    {
        Assert.True(EmuConsole.ShouldShowOnTerminal(line));
    }

    [Fact]
    public void ShaderTotals_CountPerStage()
    {
        var before = EmuConsole.ShaderTotals();
        EmuConsole.ShaderCompiled(EmuConsole.ShaderStage.Compute);
        EmuConsole.ShaderCompiled(EmuConsole.ShaderStage.Vertex);
        EmuConsole.ShaderCompiled(EmuConsole.ShaderStage.Pixel);
        var after = EmuConsole.ShaderTotals();
        Assert.True(after.Vs >= before.Vs + 1);
        Assert.True(after.Ps >= before.Ps + 1);
        Assert.True(after.Cs >= before.Cs + 1);
    }
}
