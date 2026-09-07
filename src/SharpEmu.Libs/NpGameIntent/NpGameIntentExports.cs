// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.NpGameIntent;

public static class NpGameIntentExports
{
    private static int _initialized;

    // KytyPS5 libNet.cpp LibNpGameIntent.
    private const int NpGameIntentErrorInvalidArgument = unchecked((int)0x80553804);
    private const int NpGameIntentErrorIntentNotFound = unchecked((int)0x80553806);
    private const int NpGameIntentErrorValueNotFound = unchecked((int)0x80553807);
    private const int NpGameIntentUserIdInvalid = -1;
    private const int NpGameIntentTypeSize = 33;
    private const int NpGameIntentInfoUserIdOffset = 8;
    private const int NpGameIntentInfoTypeOffset = 12;

    [SysAbiExport(
        Nid = "m87BHxt-H60",
        ExportName = "sceNpGameIntentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// No pending system-UI launch intent. Zero the output struct and report
    /// INTENT_NOT_FOUND so titles (Quake II) do not treat an unresolved import
    /// as a live activity and Com_Error on garbage map/activity names.
    /// </summary>
    [SysAbiExport(
        Nid = "jEIXUAr9XE8",
        ExportName = "sceNpGameIntentReceiveIntent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentReceiveIntent(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(NpGameIntentErrorInvalidArgument);
        }

        Span<byte> header = stackalloc byte[NpGameIntentInfoTypeOffset + NpGameIntentTypeSize];
        header.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(
            header.Slice(NpGameIntentInfoUserIdOffset, sizeof(int)),
            NpGameIntentUserIdInvalid);
        if (!ctx.Memory.TryWrite(infoAddress, header))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpGameIntentErrorIntentNotFound);
    }

    [SysAbiExport(
        Nid = "rPl0INNc-M8",
        ExportName = "sceNpGameIntentGetPropertyValueString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentGetPropertyValueString(CpuContext ctx)
    {
        var intentData = ctx[CpuRegister.Rdi];
        var key = ctx[CpuRegister.Rsi];
        var valueBuf = ctx[CpuRegister.Rdx];
        var bufSize = ctx[CpuRegister.Rcx];
        if (intentData == 0 || key == 0 || valueBuf == 0 || bufSize == 0)
        {
            return ctx.SetReturn(NpGameIntentErrorInvalidArgument);
        }

        Span<byte> terminator = [0];
        if (!ctx.Memory.TryWrite(valueBuf, terminator))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpGameIntentErrorValueNotFound);
    }
}
