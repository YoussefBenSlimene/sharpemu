// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpTrophy2Exports
{
    private static int _nextContext = 1;
    private static int _nextHandle = 1;

    [SysAbiExport(
        Nid = "Bagshr7OQ6Q",
        ExportName = "sceNpTrophy2CreateContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateContext(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextContext);
    }

    [SysAbiExport(
        Nid = "Gz1rmUZpROM",
        ExportName = "sceNpTrophy2CreateHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateHandle(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextHandle);
    }

    [SysAbiExport(
        Nid = "sysY2FHYff4",
        ExportName = "sceNpTrophy2DestroyContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "d8P11CI40KE",
        ExportName = "sceNpTrophy2DestroyHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "fYapWA9xVmA",
        ExportName = "sceNpTrophy2AbortHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2AbortHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "bIDov3wBu5Q",
        ExportName = "sceNpTrophy2RegisterContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "sUXGfNMalIo",
        ExportName = "sceNpTrophy2RegisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterUnlockCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "wVqxM58sIKs",
        ExportName = "sceNpTrophy2UnregisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2UnregisterUnlockCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "EHQEDVXZ0TI",
        ExportName = "sceNpTrophy2ShowTrophyList",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2ShowTrophyList(CpuContext ctx) => ReturnOk(ctx);

    // KytyPS5 libNet.cpp LibNpTrophy2 layout (static_asserted there).
    private const int TrophyDetailsSize = 1312;
    private const int TrophyDataSize = 32;
    private const int TrophyGradeBronze = 4;
    private const int TrophyNameOffset = 32;
    private const int TrophyNameSize = 128;
    private const int TrophyDescriptionOffset = 160;
    private const int TrophyDescriptionSize = 1024;

    /// <summary>
    /// Gen5 ABI: context, handle, trophy id, then SceNpTrophy2Details and
    /// SceNpTrophy2Data output pointers. Fills dummy bronze trophies the way
    /// KytyPS5 does — Quake II treats NOT_FOUND here as a fatal Installation
    /// error after "Quake2 Initialized".
    /// </summary>
    [SysAbiExport(
        Nid = "EwNylPdWUTM",
        ExportName = "sceNpTrophy2GetTrophyInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfo(CpuContext ctx)
    {
        var trophyId = unchecked((int)ctx[CpuRegister.Rdx]);
        var detailsAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        if (!TryWriteTrophyDetails(ctx, detailsAddress, trophyId) ||
            !TryWriteTrophyData(ctx, dataAddress, trophyId))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    [SysAbiExport(
        Nid = "y3zHpdZO6ME",
        ExportName = "sceNpTrophy2GetTrophyInfoArray",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfoArray(CpuContext ctx)
    {
        var offset = unchecked((uint)ctx[CpuRegister.Rdx]);
        var limit = unchecked((uint)ctx[CpuRegister.Rcx]);
        var detailsAddress = ctx[CpuRegister.R8];
        var dataAddress = ctx[CpuRegister.R9];
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var countAddress);

        var outCount = offset == 0 && limit != 0 ? 1u : 0u;
        if (countAddress != 0)
        {
            Span<byte> countBytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(countBytes, outCount);
            if (!ctx.Memory.TryWrite(countAddress, countBytes))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (outCount != 0 &&
            (!TryWriteTrophyDetails(ctx, detailsAddress, trophyId: 0) ||
             !TryWriteTrophyData(ctx, dataAddress, trophyId: 0)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    private static bool TryWriteTrophyDetails(CpuContext ctx, ulong address, int trophyId)
    {
        if (address == 0)
        {
            return true;
        }

        Span<byte> details = stackalloc byte[TrophyDetailsSize];
        details.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(details, trophyId);
        BinaryPrimitives.WriteInt32LittleEndian(details.Slice(4, sizeof(int)), TrophyGradeBronze);
        WriteAscii(details.Slice(TrophyNameOffset, TrophyNameSize), "Trophy");
        WriteAscii(details.Slice(TrophyDescriptionOffset, TrophyDescriptionSize), "Trophy");
        return ctx.Memory.TryWrite(address, details);
    }

    private static bool TryWriteTrophyData(CpuContext ctx, ulong address, int trophyId)
    {
        if (address == 0)
        {
            return true;
        }

        Span<byte> data = stackalloc byte[TrophyDataSize];
        data.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(data, trophyId);
        return ctx.Memory.TryWrite(address, data);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        var length = Math.Min(value.Length, destination.Length - 1);
        for (var i = 0; i < length; i++)
        {
            destination[i] = (byte)value[i];
        }
    }


    private static int WriteIdAndReturn(CpuContext ctx, ulong outAddress, ref int nextId)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> idBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(idBytes, nextId);
        if (!ctx.Memory.TryWrite(outAddress, idBytes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        nextId++;
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int ReturnOk(CpuContext ctx) => SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
