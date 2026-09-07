// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpManagerExports
{
    private const int NpTitleIdSize = 16;
    private const int NpTitleSecretSize = 128;
    private const int NpErrorInvalidArgument = unchecked((int)0x80550003);

    [SysAbiExport(
        Nid = "3Zl8BePTh9Y",
        ExportName = "sceNpCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "S7QTn72PrDw",
        ExportName = "sceNpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpDeleteRequest(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Offline NP async-request manager (KytyPS5 network.cpp semantics):
    // Quake II (KEX) creates an async request, submits reachability checks
    // against it and polls for completion. Requests complete as "signed out"
    // (0x80550006) since SharpEmu has no PSN session. Leaving any of the
    // three below unresolved makes the game's NP init fail with a leaked
    // sentinel id and Com_Error-fatal right after "Running game session".
    private const int NpErrorSignedOut = unchecked((int)0x80550006);
    private const int NpErrorRequestNotFound = unchecked((int)0x80550014);

    private sealed class NpRequest
    {
        public bool Async;
        // 0 = pending, 1 = complete.
        public int State;
        public int Result;
    }

    private static int _nextNpRequestId;
    private static readonly Dictionary<int, NpRequest> _npRequests = new();
    private static readonly object _npRequestGate = new();

    [SysAbiExport(
        Nid = "eiqMCt9UshI",
        ExportName = "sceNpCreateAsyncRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCreateAsyncRequest(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }

        var reqId = Interlocked.Increment(ref _nextNpRequestId);
        lock (_npRequestGate)
        {
            _npRequests[reqId] = new NpRequest { Async = true };
        }

        TraceNpManager($"create-async-request param=0x{paramAddress:X16} req={reqId}");
        return ctx.SetReturn(reqId);
    }

    [SysAbiExport(
        Nid = "KfGZg2y73oM",
        ExportName = "sceNpCheckNpReachability",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckNpReachability(CpuContext ctx)
    {
        var reqId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (reqId <= 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }

        lock (_npRequestGate)
        {
            if (!_npRequests.TryGetValue(reqId, out var request))
            {
                return ctx.SetReturn(NpErrorRequestNotFound);
            }

            if (request.State == 0)
            {
                // Complete the async request as reachable (result = OK). The
                // signed-out error (0x80550006) is treated as fatal by Quake
                // II's social manager, while "reachable" lets it proceed into
                // offline mode; the individual WebApi calls then fail with
                // 0x80553502, which the game logs and tolerates.
                request.State = 1;
                request.Result = 0;
            }
        }

        TraceNpManager($"check-np-reachability req={reqId} (async, signed out)");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "uqcPJLWL08M",
        ExportName = "sceNpPollAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpPollAsync(CpuContext ctx)
    {
        var reqId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }

        int result;
        lock (_npRequestGate)
        {
            if (!_npRequests.TryGetValue(reqId, out var request))
            {
                return ctx.SetReturn(NpErrorRequestNotFound);
            }

            result = request.Result;
        }

        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, result);
        if (!ctx.Memory.TryWrite(resultAddress, buffer))
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }

        TraceNpManager($"poll-async req={reqId} result=0x{result:X8}");
        return ctx.SetReturn(0);
    }

    private static void TraceNpManager(string message)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_MANAGER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] npmanager.{message}");
    }

    [SysAbiExport(
        Nid = "JELHf4xPufo",
        ExportName = "sceNpCheckCallbackForLib",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallbackForLib(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Offline profile: the online id payload is left untouched and the call
    // reports success, matching the other offline NpManager stubs here.
    [SysAbiExport(
        Nid = "XDncXQIJUSk",
        ExportName = "sceNpGetOnlineId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetOnlineId(CpuContext ctx)
    {
        // Gen5 ABI: user ID, then output structure.
        return WriteOfflineOnlineId(ctx, ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "VfRSmPmj8Q8",
        ExportName = "sceNpRegisterStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Accepts the reachability callback and never invokes it. Reachability
    /// transitions only ever fire on a real PSN connection, which an offline
    /// session does not have, so registering successfully and staying silent is
    /// the accurate emulation of a signed-out console rather than a stub.
    /// </summary>
    [SysAbiExport(
        Nid = "hw5KNqAAels",
        ExportName = "sceNpRegisterNpReachabilityStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterNpReachabilityStateCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Accepts the premium-event callback and never invokes it. Offline sessions
    /// have no PS Plus / premium transitions to deliver, and leaving this NID
    /// unresolved returns NOT_FOUND which soft-locks titles that register it
    /// during settings / store probes (GTA V Enhanced).
    /// </summary>
    [SysAbiExport(
        Nid = "+yqjab2fUJA",
        ExportName = "sceNpRegisterPremiumEventCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterPremiumEventCallback(CpuContext ctx)
    {
        TraceNp(
            $"register_premium_event_callback cb=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"userdata=0x{ctx[CpuRegister.Rsi]:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "qQJfO8HAiaY",
        ExportName = "sceNpRegisterStateCallbackA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallbackA(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0c7HbXRKUt4",
        ExportName = "sceNpRegisterStateCallbackForToolkit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManagerForToolkit")]
    public static int NpRegisterStateCallbackForToolkit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eQH7nWPcAgc",
        ExportName = "sceNpGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(stateBytes, 1);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rbknaUjpqWo",
        ExportName = "sceNpGetAccountIdA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountIdA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var accountIdAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || accountIdAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        // The offline profile exposed by sceNpGetState is signed in. Keep the
        // account query consistent with that state: Unity's PSN integration
        // treats SIGNED_OUT as an exceptional state and retries it every frame.
        // A stable local-only id is sufficient for titles which only use the
        // value as a profile key.
        Span<byte> accountId = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(accountId, 1);
        return ctx.Memory.TryWrite(accountIdAddress, accountId)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "JT+t00a3TxA",
        ExportName = "sceNpGetAccountCountryA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountCountryA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var countryAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || countryAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> country = stackalloc byte[4];
        country[0] = (byte)'U';
        country[1] = (byte)'S';
        country[2] = 0;
        country[3] = 0;
        return ctx.Memory.TryWrite(countryAddress, country)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "e-ZuhGEoeC4",
        ExportName = "sceNpGetNpReachabilityState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetNpReachabilityState(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || stateAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> state = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(state, 0); // Unavailable while offline.
        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Ec63y59l9tw",
        ExportName = "sceNpSetNpTitleId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpSetNpTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        var titleSecretAddress = ctx[CpuRegister.Rsi];
        if (titleIdAddress == 0 || titleSecretAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> titleId = stackalloc byte[NpTitleIdSize];
        Span<byte> titleSecret = stackalloc byte[NpTitleSecretSize];
        if (!ctx.Memory.TryRead(titleIdAddress, titleId) ||
            !ctx.Memory.TryRead(titleSecretAddress, titleSecret))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"set_np_title_id title='{ReadTitleId(titleId)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static string ReadTitleId(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < 12 && length < bytes.Length && bytes[length] != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private static void TraceNp(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.{message}");
    }

    private static int WriteOfflineOnlineId(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceNpOnlineId is a 16-byte handle plus four trailing bytes.
        Span<byte> onlineId = stackalloc byte[20];
        "Player"u8.CopyTo(onlineId);
        return ctx.Memory.TryWrite(address, onlineId)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
